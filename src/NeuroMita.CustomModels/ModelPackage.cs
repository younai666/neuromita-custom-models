using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    /// <summary>模型包里的一块可替换网格，以及它自带的骨架和材质。</summary>
    public sealed class ModelPart
    {
        public string Name;                 // 网格名，如 Body / Hair / Sweater
        public Mesh Mesh;                   // 已建好的 Unity Mesh
        public string[] BoneNames;          // 与 Bindposes 一一对应
        public Matrix4x4[] Bindposes;       // 绑定姿势（网格空间 -> 骨骼空间）
        public long SourceMaterialPathId;    // AssetBundle 中原始 SkinnedMeshRenderer 的材质引用
        public string SourceFile;           // 来源文件（FBX 目录包里用于定位同目录贴图）

        /// <summary>采样"骨骼名 -> 绑定姿势下该骨骼在网格空间的位置"，供自动对齐使用。</summary>
        public AutoAlign.Sample SampleBindposes()
        {
            var s = new AutoAlign.Sample();
            if (BoneNames == null || Bindposes == null) return s;
            int n = Math.Min(BoneNames.Length, Bindposes.Length);
            for (int i = 0; i < n; i++)
            {
                if (string.IsNullOrEmpty(BoneNames[i])) continue;
                var inv = Bindposes[i].inverse;
                s.Add(BoneNames[i], inv.GetColumn(3));
            }
            return s;
        }
    }

    /// <summary>
    /// 一个模型包。两种来源：Unity AssetBundle（.vrmmod 等）和 FBX 目录（带 addons_config.txt）。
    /// </summary>
    public abstract class ModelPackage : IDisposable
    {
        public string RootPath;
        public abstract bool Open();
        public abstract List<ModelPart> Parts { get; }
        public virtual void Dispose() { }

        /// <summary>读文件头判断格式。</summary>
        public static string DetectFormat(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    if (File.Exists(Path.Combine(path, "addons_config.txt"))) return "fbx-dir";
                    // 目录里找 .fbx
                    if (Directory.GetFiles(path, "*.fbx", SearchOption.AllDirectories).Length > 0) return "fbx-dir";
                    // 目录里装着 AssetBundle（.vrmmod / 无扩展名等）
                    foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        if (LooksLikeBundle(f)) return "assetbundle";
                    return "unknown-dir";
                }
                if (!File.Exists(path)) return "missing";
                if (LooksLikeBundle(path)) return "assetbundle";

                using (var fs = File.OpenRead(path))
                {
                    var buf = new byte[8];
                    int n = fs.Read(buf, 0, 8);
                    if (n >= 6 && buf[0] == 'K' && buf[1] == 'a' && buf[2] == 'y' && buf[3] == 'd' &&
                        buf[4] == 'a' && buf[5] == 'r')
                        return "fbx";
                }
            }
            catch { }
            return "unknown";
        }

        /// <summary>文件头是不是 UnityFS（AssetBundle）。</summary>
        public static bool LooksLikeBundle(string file)
        {
            try
            {
                if (!File.Exists(file)) return false;
                using (var fs = File.OpenRead(file))
                {
                    var buf = new byte[8];
                    int n = fs.Read(buf, 0, 8);
                    return n >= 7 && buf[0] == 'U' && buf[1] == 'n' && buf[2] == 'i' && buf[3] == 't' &&
                           buf[4] == 'y' && buf[5] == 'F' && buf[6] == 'S';
                }
            }
            catch { return false; }
        }

        /// <summary>按格式打开对应的包实现。</summary>
        public static ModelPackage Open(string path)
        {
            switch (DetectFormat(path))
            {
                case "fbx-dir":
                    return new FbxDirPackage { RootPath = path };

                case "fbx":
                    return new FbxFilePackage { RootPath = path };

                case "assetbundle":
                    // 游戏自身从不加载 AssetBundle（该子系统从未初始化、类型从未注册），
                    // 所以运行时的 AssetBundle.LoadFrom* 全部不可用。
                    // 这里改为在托管侧自己解析 UnityFS 容器与序列化数据。
                    return new BundlePackage { RootPath = path };

                default:
                    Logging.Error($"[Pkg] unrecognised package format: {path}");
                    return null;
            }
        }
    }
}
