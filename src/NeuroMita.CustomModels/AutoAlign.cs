using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    /// <summary>
    /// 自动对齐：把模型自带的绑定空间解算到目标骨架的绑定空间。
    ///
    /// 不硬编码任何角度。做法是在两套骨架里各取三个同名骨骼的位置，
    /// 用它们构造两组正交基，再解出"模型空间 -> 目标空间"的刚体变换。
    ///
    /// 之所以可以只解刚体变换：模型包和游戏骨架同源（骨骼名一致、层级一致），
    /// 差异只是整体朝向不同（不同作者、不同导出设置）。
    /// </summary>
    public static class AutoAlign
    {
        /// <summary>骨骼位置取样：名字 -> 该骨骼在绑定姿势下的位置。</summary>
        public sealed class Sample
        {
            public readonly Dictionary<string, Vector3> Pos =
                new Dictionary<string, Vector3>(StringComparer.Ordinal);

            public void Add(string bone, Vector3 p)
            {
                if (string.IsNullOrEmpty(bone)) return;
                if (!Pos.ContainsKey(bone)) Pos[bone] = p;
            }
        }

        public sealed class Result
        {
            public bool Ok;
            public string UsedTriad;
            public Matrix4x4 Fix = Matrix4x4.identity;
            public float Residual;      // 拟合后其余同名骨骼的平均位置误差
            public int ComparedBones;
            public string Message;
        }

        /// <summary>解算把 model 空间变换到 target 空间的矩阵。</summary>
        /// <remarks>
        /// 三点不是写死的，而是从共享骨骼里自动挑：
        ///   1) 先取距离最远的两根作为主轴端点 —— 主轴越长数值越稳
        ///   2) 再取离这条直线最远的第三根 —— 保证不共线，否则正交基退化
        /// 这样即使某块网格只绑了局部骨骼（例如头发只有 13 根、全是头部骨骼，
        /// 根本没有 Hips/Spine），也能在它自己的骨骼集合里找到可用的一组。
        /// </remarks>
        public static Result Solve(Sample target, Sample model)
        {
            var res = new Result();

            if (target == null || model == null || target.Pos.Count == 0 || model.Pos.Count == 0)
            {
                res.Message = "empty sample";
                return res;
            }

            var shared = new List<string>();
            foreach (var kv in model.Pos)
                if (target.Pos.ContainsKey(kv.Key)) shared.Add(kv.Key);

            if (shared.Count < 3)
            {
                res.Message = $"incompatible rig: only {shared.Count} of {model.Pos.Count} bones in " +
                              $"this mesh share a name with the game skeleton (need at least 3). " +
                              $"The pack was most likely built for a different rig.";
                return res;
            }

            // Compare a small set of widely separated pairs and every viable third point.
            // This avoids letting one unusual limb or accessory bone decide the whole rig alignment.
            var pairs = new List<(int A, int B, float Span)>();
            for (int i = 0; i < shared.Count; i++)
            {
                var pa = model.Pos[shared[i]];
                for (int j = i + 1; j < shared.Count; j++)
                    pairs.Add((i, j, (model.Pos[shared[j]] - pa).sqrMagnitude));
            }
            pairs.Sort((a, b) => b.Span.CompareTo(a.Span));
            if (pairs.Count == 0 || pairs[0].Span < 1e-8f)
            {
                res.Message = "cannot align: all shared bones sit at the same position " +
                              "(the mesh has no usable skeleton)";
                return res;
            }

            if (pairs.Count > 12) pairs.RemoveRange(12, pairs.Count - 12);

            string na = null, nb = null, nc = null;
            Matrix4x4 bestFix = Matrix4x4.identity;
            float bestError = float.PositiveInfinity;
            foreach (var pair in pairs)
            {
                var a = shared[pair.A];
                var b = shared[pair.B];
                foreach (var c in shared)
                {
                    if (c == a || c == b) continue;
                    if (!BuildBasis(target.Pos[a], target.Pos[b], target.Pos[c], out var basisT) ||
                        !BuildBasis(model.Pos[a], model.Pos[b], model.Pos[c], out var basisM)) continue;

                    var rot = basisT * Transpose3(basisM);
                    var fix = Matrix4x4.Translate(target.Pos[a]) * rot * Matrix4x4.Translate(-model.Pos[a]);
                    float error = 0f;
                    foreach (var bone in shared)
                        error += (fix.MultiplyPoint3x4(model.Pos[bone]) - target.Pos[bone]).magnitude;

                    if (error < bestError)
                    {
                        bestError = error;
                        bestFix = fix;
                        na = a; nb = b; nc = c;
                    }
                }
            }

            if (na == null)
            {
                res.Message = "cannot align: the shared bones are collinear, " +
                              "so no stable orientation can be derived from them";
                return res;
            }

            res.Ok = true;
            res.UsedTriad = $"{na}/{nb}/{nc}";
            res.Fix = bestFix;
            res.ComparedBones = shared.Count;
            res.Residual = bestError / shared.Count;
            res.Message = $"triad={res.UsedTriad} bones={res.ComparedBones} avgErr={res.Residual:F4}";
            return res;
        }

        /// <summary>由三个点构造正交基（列向量：right / up / forward）。</summary>
        private static bool BuildBasis(Vector3 origin, Vector3 upPt, Vector3 sidePt, out Matrix4x4 basis)
        {
            basis = Matrix4x4.identity;

            var up = upPt - origin;
            var side = sidePt - origin;
            if (up.sqrMagnitude < 1e-10f || side.sqrMagnitude < 1e-10f) return false;

            up.Normalize();
            side.Normalize();

            // 侧向正交化
            var right = side - up * Vector3.Dot(side, up);
            if (right.sqrMagnitude < 1e-8f) return false;   // 共线
            right.Normalize();

            var fwd = Vector3.Cross(right, up);

            basis.SetColumn(0, new Vector4(right.x, right.y, right.z, 0f));
            basis.SetColumn(1, new Vector4(up.x, up.y, up.z, 0f));
            basis.SetColumn(2, new Vector4(fwd.x, fwd.y, fwd.z, 0f));
            basis.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
            return true;
        }

        /// <summary>3x3 部分的转置（等价于逆，因为基是正交的）。</summary>
        private static Matrix4x4 Transpose3(Matrix4x4 m)
        {
            var r = Matrix4x4.identity;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i, j] = m[j, i];
            return r;
        }

        /// <summary>把一个 Unity Mesh 的 bindpose 采样成"骨骼名 -> 位置"。</summary>
        public static Sample SampleFromMesh(Transform[] bones, Matrix4x4[] bindposes)
        {
            var s = new Sample();
            if (bones == null || bindposes == null) return s;
            int n = Math.Min(bones.Length, bindposes.Length);
            for (int i = 0; i < n; i++)
            {
                var t = bones[i];
                if (t == null) continue;
                // bindpose 的平移列 = 该骨骼绑定位置在网格空间中的坐标（取逆后的位置）
                var inv = bindposes[i].inverse;
                s.Add(t.name, inv.GetColumn(3));
            }
            return s;
        }
    }
}
