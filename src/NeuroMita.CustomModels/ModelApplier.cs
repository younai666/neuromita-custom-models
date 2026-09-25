using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    /// <summary>
    /// 把模型包里的网格装配到游戏角色身上。
    ///
    /// 这里没有任何硬编码的朝向修正 —— 坐标差异由 AutoAlign 从两套骨架的
    /// 绑定姿势里解算出来，所以任意朝向的模型包走的是同一条代码路径。
    /// </summary>
    public static class ModelApplier
    {
        public sealed class Report
        {
            public bool Ok;
            public string Part;
            public int Bones;
            public int Missing;
            public float AlignResidual = -1f;
            public string AlignTriad;
            public string Message = "";

            public override string ToString() =>
                $"part='{Part}' ok={Ok} bones={Bones} missing={Missing} " +
                $"align=[{AlignTriad}] residual={AlignResidual:F4} {Message}";
        }

        public static Report Apply(SkinnedMeshRenderer target, ModelPart part, Transform skeletonRoot)
        {
            var rep = new Report { Part = part != null ? part.Name : "null" };
            try
            {
                if (target == null || part == null || part.Mesh == null)
                {
                    rep.Message = "null input";
                    return rep;
                }

                var oldMesh = target.sharedMesh;
                var oldBones = target.bones;

                // ---- 1) 自动对齐：目标骨架的绑定空间 vs 模型自带的绑定空间 ----
                var targetSample = AutoAlign.SampleFromMesh(oldBones, oldMesh != null ? oldMesh.bindposes : null);
                var modelSample = part.SampleBindposes();
                var align = AutoAlign.Solve(targetSample, modelSample);

                rep.AlignResidual = align.Residual;
                rep.AlignTriad = align.UsedTriad;

                if (!align.Ok)
                {
                    rep.Message = "align failed: " + align.Message;
                    return rep;
                }
                Logging.Verbose($"[Apply] align: {align.Message}");

                // ---- 2) 复制网格并把顶点搬到目标空间 ----
                Mesh mesh = null;
                try { mesh = UnityEngine.Object.Instantiate(part.Mesh).TryCast<Mesh>(); } catch { }
                if (mesh == null)
                {
                    rep.Message = "mesh clone failed";
                    return rep;
                }
                mesh.name = (part.Mesh.name ?? "mesh") + "_aligned";
                TransformMesh(mesh, align.Fix);

                // ---- 3) 目标骨架的 bindpose 表（按骨骼名）----
                var targetBp = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
                if (oldMesh != null && oldBones != null)
                {
                    var bp = oldMesh.bindposes;
                    int n = Math.Min(oldBones.Length, bp != null ? bp.Length : 0);
                    for (int i = 0; i < n; i++)
                    {
                        var t = oldBones[i];
                        if (t == null) continue;
                        var nm = t.name;
                        if (!string.IsNullOrEmpty(nm) && !targetBp.ContainsKey(nm)) targetBp[nm] = bp[i];
                    }
                }
                Logging.Verbose($"[Apply] target bindpose table: {targetBp.Count} entries");

                // ---- 4) 骨骼映射：保持 1:1 索引，缺失的记录出来稍后清权重 ----
                int count = part.BoneNames != null ? part.BoneNames.Length : 0;
                var bones = new Transform[count];
                var bindposes = new Matrix4x4[count];
                var missingIdx = new HashSet<int>();
                int missing = 0, bpFromTarget = 0, bpFromModel = 0;

                for (int i = 0; i < count; i++)
                {
                    var name = part.BoneNames[i];
                    var t = ModelApplier.FindByName(skeletonRoot, name);
                    if (t == null) { t = skeletonRoot; missing++; missingIdx.Add(i); }
                    bones[i] = t;

                    if (!string.IsNullOrEmpty(name) && targetBp.TryGetValue(name, out var mbp))
                    {
                        // bindpose 取游戏角色自己的那一份：骨骼是游戏的，绑定空间就该用它
                        bindposes[i] = mbp;
                        bpFromTarget++;
                    }
                    else if (part.Bindposes != null && i < part.Bindposes.Length)
                    {
                        bindposes[i] = align.Fix * part.Bindposes[i];
                        bpFromModel++;
                    }
                    else
                    {
                        bindposes[i] = Matrix4x4.identity;
                    }
                }
                Logging.Verbose($"[Apply] bones={count} missing={missing} bpFromTarget={bpFromTarget} bpFromModel={bpFromModel}");

                // 缺失骨骼的权重必须清掉并重新归一化：
                // 否则这些顶点会被拉向骨架根，在模型上拖出一条细长尖刺。
                if (missingIdx.Count > 0) DropMissingWeights(mesh, missingIdx);

                // ---- 5) 装配 ----
                mesh.bindposes = bindposes;
                target.sharedMesh = mesh;
                target.bones = bones;
                if (bones.Length > 0 && bones[0] != null) target.rootBone = skeletonRoot;

                rep.Ok = true;
                rep.Bones = count;
                rep.Missing = missing;
                return rep;
            }
            catch (Exception e)
            {
                rep.Message = e.GetType().Name + ": " + e.Message;
                Logging.Error("[Apply] failed: " + e);
                return rep;
            }
        }

        /// <summary>
        /// 把绑定到"游戏骨架里不存在"的骨骼上的权重清零，并在剩余权重间重新归一化。
        /// 这些顶点的骨骼会被占位到骨架根，若不清权重，它们会被从原位拉到骨架根，
        /// 在模型上表现为一条细长的尖刺（拉扯条）。
        /// </summary>
        private static void DropMissingWeights(Mesh mesh, HashSet<int> missingIdx)
        {
            try
            {
                var ws = mesh.boneWeights;
                if (ws == null || ws.Length == 0) return;

                for (int v = 0; v < ws.Length; v++)
                {
                    var bw = ws[v];
                    float w0 = bw.weight0, w1 = bw.weight1, w2 = bw.weight2, w3 = bw.weight3;
                    if (missingIdx.Contains(bw.boneIndex0)) w0 = 0f;
                    if (missingIdx.Contains(bw.boneIndex1)) w1 = 0f;
                    if (missingIdx.Contains(bw.boneIndex2)) w2 = 0f;
                    if (missingIdx.Contains(bw.boneIndex3)) w3 = 0f;

                    float sum = w0 + w1 + w2 + w3;
                    if (sum > 1e-6f && sum < 0.9999f)
                    {
                        w0 /= sum; w1 /= sum; w2 /= sum; w3 /= sum;
                    }
                    else if (sum <= 1e-6f)
                    {
                        // 整条顶点都绑在缺失骨骼上：退化为跟随骨架根，保持原位不变形
                        w0 = 1f; bw.boneIndex0 = bw.boneIndex0; w1 = w2 = w3 = 0f;
                    }

                    bw.weight0 = w0; bw.weight1 = w1; bw.weight2 = w2; bw.weight3 = w3;
                    ws[v] = bw;
                }
                mesh.boneWeights = ws;
            }
            catch (Exception e) { Logging.Warn("[Apply] DropMissingWeights failed: " + e.Message); }
        }

        private static void TransformMesh(Mesh mesh, Matrix4x4 fix)
        {
            var v = mesh.vertices;
            if (v != null && v.Length > 0)
            {
                for (int i = 0; i < v.Length; i++) v[i] = fix.MultiplyPoint3x4(v[i]);
                mesh.vertices = v;
            }

            var n = mesh.normals;
            if (n != null && n.Length > 0)
            {
                for (int i = 0; i < n.Length; i++) n[i] = fix.MultiplyVector(n[i]);
                mesh.normals = n;
            }

            mesh.RecalculateBounds();
        }

        /// <summary>按名字在骨架子树里找骨骼。</summary>
        public static Transform FindByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            if (root.name == name) return root;
            int c = root.childCount;
            for (int i = 0; i < c; i++)
            {
                var r = FindByName(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
