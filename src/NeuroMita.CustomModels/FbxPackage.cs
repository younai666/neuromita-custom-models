using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    /// <summary>
    /// FBX 模型包（单个 .fbx 文件）。
    ///
    /// 和 AssetBundle 路线的区别：Mesh 要自己建，顶点权重/绑定姿势要自己转。
    /// 顶点保持 FBX 原始空间，坐标差异由 AutoAlign 在装配时统一解算，这里不做任何硬编码旋转。
    /// </summary>
    public class FbxFilePackage : ModelPackage
    {

        private static bool _nativeChecked;
        private static bool _nativeAvailable;

        /// <summary>
        /// 探测 native assimp 是否可用，只做一次。
        /// 这是最常见的安装问题，所以单独给一条可执行的指引，而不是让每个文件各抛一次异常。
        /// </summary>
        protected static bool EnsureNativeLibrary()
        {
            if (_nativeChecked) return _nativeAvailable;
            _nativeChecked = true;

            try
            {
                using (var probe = new Assimp.AssimpContext()) { }
                _nativeAvailable = true;
            }
            catch (DllNotFoundException e)
            {
                _nativeAvailable = false;
                Logging.Error(
                    "[Pkg] native assimp library not found - FBX packs cannot be read. " +
                    "Copy assimp.dll from the AssimpNetter NuGet package " +
                    "(runtimes/win-x64/native/assimp.dll) into BepInEx\\plugins\\ " +
                    "next to NeuroMita.CustomModels.dll. Detail: " + e.Message);
            }
            catch (Exception e)
            {
                // 其他异常说明库已经加载起来了
                _nativeAvailable = true;
                Logging.Warn("[Pkg] assimp probe raised (library seems present): " + e.Message);
            }
            return _nativeAvailable;
        }

        protected readonly List<ModelPart> _parts = new List<ModelPart>();
        public override List<ModelPart> Parts => _parts;

        public override bool Open()
        {
            if (!EnsureNativeLibrary()) return false;
            try
            {
                Assimp.Scene scene;
                using (var ctx = new Assimp.AssimpContext())
                {
                    var steps = Assimp.PostProcessSteps.Triangulate
                              | Assimp.PostProcessSteps.MakeLeftHanded
                              | Assimp.PostProcessSteps.FlipWindingOrder;
                    scene = ctx.ImportFile(RootPath, steps);
                }

                if (scene == null || scene.MeshCount == 0)
                {
                    Logging.Warn($"[Pkg] FBX has no mesh: {RootPath}");
                    return false;
                }

                Logging.Info($"[Pkg] FBX loaded: meshes={scene.MeshCount} materials={scene.MaterialCount}");
                foreach (var am in scene.Meshes)
                {
                    var part = BuildPart(am, scene);
                    if (part != null) _parts.Add(part);
                }
                return _parts.Count > 0;
            }
            catch (DllNotFoundException e)
            {
                // 最常见的一类安装问题，单独给出可执行的指引
                Logging.Error("[Pkg] the native assimp library is missing, so FBX packs cannot be read. " +
                              "Put assimp.dll (from the AssimpNetter NuGet package, runtimes/win-x64/native/) " +
                              "next to NeuroMita.CustomModels.dll inside BepInEx\\plugins\\. " +
                              "Detail: " + e.Message);
                return false;
            }
            catch (Exception e)
            {
                Logging.Error("[Pkg] FBX open failed: " + e);
                return false;
            }
        }

        /// <summary>Assimp mesh -> ModelPart（顶点 / 法线 / UV / 三角形 / 权重 / 绑定姿势）。</summary>
        protected static ModelPart BuildPart(Assimp.Mesh am, Assimp.Scene scene)
        {
            int vc = am.VertexCount;
            if (vc == 0) return null;

            var mesh = new Mesh { name = am.Name ?? "mesh" };

            var verts = new Vector3[vc];
            for (int i = 0; i < vc; i++)
            {
                var v = am.Vertices[i];
                verts[i] = new Vector3(v.X, v.Y, v.Z);
            }
            mesh.vertices = verts;

            Vector3[] baseNormals = null;
            if (am.HasNormals)
            {
                baseNormals = new Vector3[vc];
                for (int i = 0; i < vc; i++)
                {
                    var n = am.Normals[i];
                    baseNormals[i] = new Vector3(n.X, n.Y, n.Z);
                }
                mesh.normals = baseNormals;
            }

            Vector3[] baseTangents = null;
            if (am.HasTangentBasis)
            {
                baseTangents = new Vector3[vc];
                for (int i = 0; i < vc; i++)
                {
                    var t = am.Tangents[i];
                    baseTangents[i] = new Vector3(t.X, t.Y, t.Z);
                }
            }
            var blendShapes = ImportBlendShapes(am, verts, baseNormals, baseTangents);

            if (am.HasTextureCoords(0))
            {
                var uvs = new Vector2[vc];
                var ch = am.TextureCoordinateChannels[0];
                for (int i = 0; i < vc && i < ch.Count; i++)
                    uvs[i] = new Vector2(ch[i].X, ch[i].Y);
                mesh.uv = uvs;
            }

            mesh.triangles = am.GetIndices();

            // 骨骼名 + 绑定姿势（保持 FBX 空间）
            var boneNames = new List<string>();
            var bindList = new List<Matrix4x4>();
            var perVertex = new List<KeyValuePair<int, float>>[vc];
            for (int i = 0; i < vc; i++) perVertex[i] = new List<KeyValuePair<int, float>>(6);

            foreach (var b in am.Bones)
            {
                int bi = boneNames.Count;
                boneNames.Add(b.Name);
                bindList.Add(ToUnity(b.OffsetMatrix));

                foreach (var vw in b.VertexWeights)
                {
                    int vi = vw.VertexID;
                    if (vi < 0 || vi >= vc) continue;
                    perVertex[vi].Add(new KeyValuePair<int, float>(bi, vw.Weight));
                }
            }

            // 每顶点取权重最大的 4 根并归一化
            var weights = new BoneWeight[vc];
            for (int i = 0; i < vc; i++)
            {
                var list = perVertex[i];
                if (list.Count == 0) continue;
                list.Sort((x, y) => y.Value.CompareTo(x.Value));

                int take = list.Count < 4 ? list.Count : 4;
                float sum = 0f;
                for (int k = 0; k < take; k++) sum += list[k].Value;

                var bw = new BoneWeight();
                if (sum <= 1e-6f)
                {
                    bw.boneIndex0 = list[0].Key;
                    bw.weight0 = 1f;
                }
                else
                {
                    float inv = 1f / sum;
                    for (int k = 0; k < take; k++)
                    {
                        int idx = list[k].Key;
                        float w = list[k].Value * inv;
                        if (k == 0) { bw.boneIndex0 = idx; bw.weight0 = w; }
                        else if (k == 1) { bw.boneIndex1 = idx; bw.weight1 = w; }
                        else if (k == 2) { bw.boneIndex2 = idx; bw.weight2 = w; }
                        else { bw.boneIndex3 = idx; bw.weight3 = w; }
                    }
                }
                weights[i] = bw;
            }

            var bindposes = bindList.ToArray();

            // 通用权重清理：补上"没有任何骨骼影响"的孤儿顶点并归一化。
            // 不做这一步时，权重和为零的顶点会塌缩，表现为细长尖刺或整块消失。
            int repaired = WeightRepair.Fix(verts, weights, bindposes);
            if (repaired > 0)
                Logging.Verbose($"[Pkg]   {am.Name}: repaired {repaired} weightless vertex/vertices");

            mesh.boneWeights = weights;
            mesh.bindposes = bindposes;
            mesh.RecalculateBounds();

            int sourceMorphs = 0;
            try { sourceMorphs = am.MeshAnimationAttachmentCount; } catch { }
            var importedNames = new List<string>();
            foreach (var shape in blendShapes) importedNames.Add(shape.Name);
            try
            {
                var mi = am.MaterialIndex;
                if (mi >= 0 && mi < scene.MaterialCount)
                {
                    var am2 = scene.Materials[mi];
                    Logging.Verbose($"[Pkg]   part '{am.Name}' verts={vc} bones={boneNames.Count} " +
                                 $"sourceMorphs={sourceMorphs} importedBlendShapes={blendShapes.Count} " +
                                 $"material='{am2.Name}'");
                }
                else
                {
                    Logging.Verbose($"[Pkg]   part '{am.Name}' verts={vc} bones={boneNames.Count} " +
                                 $"sourceMorphs={sourceMorphs} importedBlendShapes={blendShapes.Count}");
                }
            }
            catch { }
            if (importedNames.Count > 0)
                Logging.Verbose($"[Pkg]   {am.Name} blendshapes: {string.Join(", ", importedNames)}");

            return new ModelPart
            {
                Name = am.Name ?? "mesh",
                Mesh = mesh,
                BlendShapes = blendShapes,
                BoneNames = boneNames.ToArray(),
                Bindposes = bindposes,
            };
        }

        private static List<ModelBlendShape> ImportBlendShapes(Assimp.Mesh source, Vector3[] baseVertices,
            Vector3[] baseNormals, Vector3[] baseTangents)
        {
            var result = new List<ModelBlendShape>();
            if (source == null || baseVertices == null || baseVertices.Length == 0) return result;

            var attachments = source.MeshAnimationAttachments;
            if (attachments == null) return result;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < attachments.Count; index++)
            {
                var attachment = attachments[index];
                string name = ReadAttachmentName(attachment);
                bool nameMissing = string.IsNullOrWhiteSpace(name);
                    if (nameMissing) name = $"{source.Name ?? "mesh"}_BlendShape_{index + 1}";
                string uniqueName = name;
                int suffix = 2;
                while (!usedNames.Add(uniqueName)) uniqueName = $"{name}_{suffix++}";

                try
                {
                    if (attachment == null || !attachment.HasVertices || attachment.Vertices == null ||
                        attachment.Vertices.Count != baseVertices.Length)
                        throw new InvalidDataException("morph vertex count does not match base mesh");

                    var dv = new Vector3[baseVertices.Length];
                    var dn = new Vector3[baseVertices.Length];
                    var dt = new Vector3[baseVertices.Length];
                    for (int vertex = 0; vertex < baseVertices.Length; vertex++)
                    {
                        var p = attachment.Vertices[vertex];
                        dv[vertex] = new Vector3(p.X - baseVertices[vertex].x,
                            p.Y - baseVertices[vertex].y, p.Z - baseVertices[vertex].z);

                        if (attachment.HasNormals && attachment.Normals != null && attachment.Normals.Count == baseVertices.Length)
                        {
                            var n = attachment.Normals[vertex];
                            var baseNormal = baseNormals != null ? baseNormals[vertex] : Vector3.zero;
                            dn[vertex] = new Vector3(n.X - baseNormal.x, n.Y - baseNormal.y, n.Z - baseNormal.z);
                        }
                        if (attachment.HasTangentBasis && attachment.Tangents != null && attachment.Tangents.Count == baseVertices.Length)
                        {
                            var t = attachment.Tangents[vertex];
                            var baseTangent = baseTangents != null ? baseTangents[vertex] : Vector3.zero;
                            dt[vertex] = new Vector3(t.X - baseTangent.x, t.Y - baseTangent.y, t.Z - baseTangent.z);
                        }

                        if (!IsFinite(dv[vertex]) || !IsFinite(dn[vertex]) || !IsFinite(dt[vertex]))
                            throw new InvalidDataException($"non-finite data at vertex {vertex}");
                    }

                    var shape = new ModelBlendShape { Name = uniqueName };
                    shape.Frames.Add(new ModelBlendShapeFrame
                    {
                        Weight = 100f,
                        DeltaVertices = dv,
                        DeltaNormals = dn,
                        DeltaTangents = dt
                    });
                    result.Add(shape);
                    if (nameMissing)
                        Logging.Warn($"[Pkg] BlendShape #{index + 1} on '{source.Name}' has no name in the AssimpNetter data; " +
                                     $"using '{uniqueName}'. Check the installed AssimpNetter package version.");
                }
                catch (Exception e)
                {
                    Logging.Warn($"[Pkg] BlendShape '{uniqueName}' skipped: {e.Message}");
                }
            }
            return result;
        }

        private static string ReadAttachmentName(Assimp.MeshAnimationAttachment attachment)
        {
            return attachment != null ? attachment.Name : null;
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        /// <summary>Assimp 行主序矩阵 -> Unity 列主序矩阵。</summary>
        protected static Matrix4x4 ToUnity(System.Numerics.Matrix4x4 m)
        {
            var r = new Matrix4x4();
            r.m00 = m.M11; r.m01 = m.M12; r.m02 = m.M13; r.m03 = m.M14;
            r.m10 = m.M21; r.m11 = m.M22; r.m12 = m.M23; r.m13 = m.M24;
            r.m20 = m.M31; r.m21 = m.M32; r.m22 = m.M33; r.m23 = m.M34;
            r.m30 = m.M41; r.m31 = m.M42; r.m32 = m.M43; r.m33 = m.M44;
            return r;
        }
    }

    /// <summary>
    /// FBX 目录包：目录里若干 .fbx + 一张贴图 + addons_config.txt。
    /// 这是 Miside Custom Models Loader 的原生格式。
    /// </summary>
    public sealed class FbxDirPackage : FbxFilePackage
    {

        /// <summary>原始 config 文本，交给 DSL 解析器。</summary>
        public string ConfigText { get; private set; }
        public string ConfigPath { get; private set; }

        /// <summary>配置所在目录。贴图等相对路径以它为基准。</summary>
        public string ConfigDir { get; private set; }

        public override bool Open()
        {
            if (!EnsureNativeLibrary()) return false;
            try
            {
                // 配置不一定在包目录根部 —— 实测有些包把它放在子目录里
                // （例如 <pack>\CJ\addons_config.txt，而 <pack> 下还有别的目录）。
                ConfigPath = Path.Combine(RootPath, "addons_config.txt");
                if (!File.Exists(ConfigPath))
                {
                    try
                    {
                        var hits = Directory.GetFiles(RootPath, "addons_config.txt", SearchOption.AllDirectories);
                        if (hits.Length > 0)
                        {
                            ConfigPath = hits[0];
                            Logging.Info($"[Pkg] addons_config.txt found in subdirectory: {ConfigPath}");
                        }
                    }
                    catch { }
                }

                if (File.Exists(ConfigPath))
                {
                    ConfigText = File.ReadAllText(ConfigPath);
                    ConfigDir = Path.GetDirectoryName(ConfigPath);
                    Logging.Info($"[Pkg] addons_config.txt loaded ({ConfigText.Length} chars) from {ConfigDir}");
                }
                else
                {
                    ConfigDir = RootPath;
                    Logging.Warn($"[Pkg] no addons_config.txt under {RootPath}");
                }

                var files = Directory.GetFiles(RootPath, "*.fbx", SearchOption.AllDirectories);
                Logging.Info($"[Pkg] fbx files in dir: {files.Length}");

                foreach (var f in files)
                {
                    Logging.Info($"[Pkg] --- loading {Path.GetFileName(f)}");
                    try
                    {
                        Assimp.Scene scene;
                        using (var ctx = new Assimp.AssimpContext())
                        {
                            var steps = Assimp.PostProcessSteps.Triangulate
                                      | Assimp.PostProcessSteps.MakeLeftHanded
                                      | Assimp.PostProcessSteps.FlipWindingOrder;
                            scene = ctx.ImportFile(f, steps);
                        }
                        if (scene == null || scene.MeshCount == 0) continue;

                        foreach (var am in scene.Meshes)
                        {
                            var part = BuildPart(am, scene);
                            if (part == null) continue;
                            // 记下来源文件，装配时用于定位贴图
                            part.SourceFile = f;
                            _parts.Add(part);
                        }
                    }
                    catch (Exception e)
                    {
                        Logging.Warn($"[Pkg] failed to load {Path.GetFileName(f)}: {e.Message}");
                    }
                }

                return _parts.Count > 0;
            }
            catch (Exception e)
            {
                Logging.Error("[Pkg] FBX dir open failed: " + e);
                return false;
            }
        }
    }
}
