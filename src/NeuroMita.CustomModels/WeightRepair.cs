using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    /// <summary>
    /// 顶点权重的通用清理。
    ///
    /// 全部基于数据条件判定，不含任何模型特判：
    ///   1) "没有骨骼影响"的顶点（四权重和为 0）：优先借用空间上最近的有效顶点，
    ///      借不到时改绑到"绑定姿势下距离最近的那根骨骼"。
    ///      这类顶点在蒙皮时权重和为零，会塌缩掉 —— 少数几个会拉出细长尖刺，
    ///      整块网格如此则整块消失（例如某些包的头盔网格没有任何权重）。
    ///   2) 权重和不为 1 的顶点：归一化，避免整体缩放/漂移。
    ///
    /// 在"构建网格"阶段调用，此时权重与绑定姿势都是纯托管数组，不涉及 IL2CPP 互操作。
    /// </summary>
    public static class WeightRepair
    {
        private const float Eps = 1e-4f;

        /// <summary>修复孤儿顶点并归一化权重，返回被修复的孤儿顶点数。</summary>
        public static int Fix(Vector3[] verts, BoneWeight[] ws, Matrix4x4[] bindposes)
        {
            if (verts == null || ws == null) return 0;
            int n = Math.Min(verts.Length, ws.Length);
            if (n == 0) return 0;

            // ---- 1) 找出孤儿顶点 ----
            List<int> orphans = null;
            for (int i = 0; i < n; i++)
            {
                var bw = ws[i];
                if (bw.weight0 + bw.weight1 + bw.weight2 + bw.weight3 < Eps)
                {
                    if (orphans == null) orphans = new List<int>();
                    orphans.Add(i);
                }
            }
            if (orphans == null) return 0;

            // 先判断有没有"有效顶点"可借。整块网格都无权重时直接走"最近骨骼"，
            // 免得对一个根本找不到目标的 O(n²) 邻居搜索空跑（例如 3751 顶点的头盔）。
            bool anyValid = orphans.Count < n;

            // ---- 2) 骨骼在"绑定姿势下的位置"（bindpose 取逆后的平移列）----
            Vector3[] bonePos = null;
            if (bindposes != null && bindposes.Length > 0)
            {
                bonePos = new Vector3[bindposes.Length];
                for (int i = 0; i < bindposes.Length; i++)
                {
                    var inv = bindposes[i].inverse;
                    bonePos[i] = new Vector3(inv.m03, inv.m13, inv.m23);
                }
            }

            int repaired = 0;
            for (int k = 0; k < orphans.Count; k++)
            {
                int zi = orphans[k];
                var p = verts[zi];

                // 2a) 优先借"空间上最近的有效顶点"的权重：能保留原有的局部形变
                int best = -1;
                if (anyValid)
                {
                    float bestD = float.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        if (i == zi) continue;
                        var bw = ws[i];
                        if (bw.weight0 + bw.weight1 + bw.weight2 + bw.weight3 < Eps) continue;

                        float dx = verts[i].x - p.x, dy = verts[i].y - p.y, dz = verts[i].z - p.z;
                        float d = dx * dx + dy * dy + dz * dz;
                        if (d < bestD) { bestD = d; best = i; }
                    }
                }

                if (best >= 0)
                {
                    ws[zi] = ws[best];
                    repaired++;
                    continue;
                }

                // 2b) 一个有效顶点都没有（整块网格无权重）：绑到绑定姿势下最近的骨骼
                if (bonePos != null && bonePos.Length > 0)
                {
                    int bb = 0;
                    float bbD = float.MaxValue;
                    for (int b = 0; b < bonePos.Length; b++)
                    {
                        float dx = bonePos[b].x - p.x, dy = bonePos[b].y - p.y, dz = bonePos[b].z - p.z;
                        float d = dx * dx + dy * dy + dz * dz;
                        if (d < bbD) { bbD = d; bb = b; }
                    }
                    var nb = new BoneWeight
                    {
                        weight0 = 1f,
                        boneIndex0 = bb
                    };
                    ws[zi] = nb;
                    repaired++;
                }
            }

            // ---- 3) 归一化 ----
            for (int i = 0; i < n; i++)
            {
                var bw = ws[i];
                float s = bw.weight0 + bw.weight1 + bw.weight2 + bw.weight3;
                if (s < Eps) continue;
                if (s > 0.9999f && s < 1.0001f) continue;
                bw.weight0 /= s; bw.weight1 /= s; bw.weight2 /= s; bw.weight3 /= s;
                ws[i] = bw;
            }

            return repaired;
        }
    }
}
