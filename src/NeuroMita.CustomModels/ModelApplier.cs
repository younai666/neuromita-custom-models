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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (target == null || part == null || part.Mesh == null)
                {
                    rep.Message = "null input";
                    return rep;
                }

                var oldMesh = target.sharedMesh;
                var oldBones = target.bones;
                long msClone = 0, msTransform = 0;

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
                var swClone = System.Diagnostics.Stopwatch.StartNew();
                try { mesh = UnityEngine.Object.Instantiate(part.Mesh).TryCast<Mesh>(); } catch { }
                msClone = swClone.ElapsedMilliseconds;
                if (mesh == null)
                {
                    rep.Message = "mesh clone failed";
                    return rep;
                }
                mesh.name = (part.Mesh.name ?? "mesh") + "_aligned";
                var swTr = System.Diagnostics.Stopwatch.StartNew();
                TransformMesh(mesh, align.Fix);
                msTransform = swTr.ElapsedMilliseconds;

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
                int missing = 0, remapped = 0, bpFromTarget = 0, bpFromModel = 0;

                // 骨骼名索引只建一次。
                // 之前是每根骨骼都递归走一遍整棵树（O(n²)）—— Lumine 那种 285 根骨骼的包
                // 就是 8 万次节点访问，纯浪费。
                var skeletonByName = BuildNameIndex(skeletonRoot);

                for (int i = 0; i < count; i++)
                {
                    var name = part.BoneNames[i];
                    Transform t = null;
                    if (!string.IsNullOrEmpty(name)) skeletonByName.TryGetValue(name, out t);
                    if (t == null)
                    {
                        missing++;
                        var nearest = FindNearestMappedBone(name, modelSample, targetBp, skeletonByName, align.Fix);
                        if (nearest != null && skeletonByName.TryGetValue(nearest, out t))
                        {
                            name = nearest;
                            remapped++;
                        }
                        else t = skeletonRoot;
                    }
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
                Logging.Verbose($"[Apply] bones={count} missing={missing} remapped={remapped} bpFromTarget={bpFromTarget} bpFromModel={bpFromModel}");

                if (missing > 0)
                    Logging.Warn($"[Apply] {missing} bone(s) not found in the game skeleton; " +
                                 $"{remapped} mapped to nearby existing bones, {missing - remapped} use the skeleton root");

                // ---- 5) 装配 ----
                mesh.bindposes = bindposes;
                target.sharedMesh = mesh;
                target.bones = bones;
                if (bones.Length > 0 && bones[0] != null) target.rootBone = skeletonRoot;
                target.updateWhenOffscreen = true;

                rep.Ok = true;
                rep.Bones = count;
                rep.Missing = missing;
                Logging.Verbose($"[PERF] apply '{part.Name}' clone={msClone}ms transform={msTransform}ms " +
                             $"total={sw.ElapsedMilliseconds}ms verts={part.Mesh.vertexCount}");
                return rep;
            }
            catch (Exception e)
            {
                rep.Message = e.GetType().Name + ": " + e.Message;
                Logging.Error("[Apply] failed: " + e);
                return rep;
            }
        }

        private static string FindNearestMappedBone(string name, AutoAlign.Sample modelSample,
            Dictionary<string, Matrix4x4> targetBp, Dictionary<string, Transform> skeletonByName, Matrix4x4 modelToTarget)
        {
            if (string.IsNullOrEmpty(name) || !modelSample.Pos.TryGetValue(name, out var pos)) return null;
            pos = modelToTarget.MultiplyPoint3x4(pos);
            string best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (var candidate in targetBp.Keys)
            {
                if (!skeletonByName.ContainsKey(candidate) || !modelSample.Pos.TryGetValue(candidate, out var other)) continue;
                if (!IsValidRemap(name, candidate)) continue;
                other = modelToTarget.MultiplyPoint3x4(other);
                float distance = (pos - other).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = candidate; }
            }
            return best;
        }

        private static bool IsValidRemap(string source, string candidate)
        {
            if (source.StartsWith("Ahoge", StringComparison.Ordinal) ||
                source.StartsWith("BangHair", StringComparison.Ordinal) ||
                source.StartsWith("HairSide", StringComparison.Ordinal))
                return candidate.IndexOf("hair", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       string.Equals(candidate, "Head", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(candidate, "Neck", StringComparison.OrdinalIgnoreCase);

            if (source.StartsWith("Butt", StringComparison.Ordinal))
                return candidate.IndexOf("hips", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       candidate.IndexOf("pelvis", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       candidate.IndexOf("skirt", StringComparison.OrdinalIgnoreCase) >= 0;

            return true;
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

        /// <summary>
        /// 一次性把骨架子树里所有 transform 按名字建索引（同名只取最靠上的那个）。
        /// 装配时要按骨骼名查几百次，逐次递归遍历整棵树是 O(n²)。
        /// </summary>
        public static Dictionary<string, Transform> BuildNameIndex(Transform root)
        {
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            try { Collect(root, map); }
            catch { }
            return map;
        }

        private static void Collect(Transform t, Dictionary<string, Transform> map)
        {
            if (t == null) return;
            var n = t.name;
            if (!string.IsNullOrEmpty(n) && !map.ContainsKey(n)) map[n] = t;
            int c = t.childCount;
            for (int i = 0; i < c; i++) Collect(t.GetChild(i), map);
        }
    }
}
