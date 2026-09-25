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
                    return "unknown-dir";
                }
                if (!File.Exists(path)) return "missing";

                using (var fs = File.OpenRead(path))
                {
                    var buf = new byte[8];
                    int n = fs.Read(buf, 0, 8);
                    if (n >= 8 && buf[0] == 'U' && buf[1] == 'n' && buf[2] == 'i' && buf[3] == 't' &&
                        buf[4] == 'y' && buf[5] == 'F' && buf[6] == 'S')
                        return "assetbundle";
                    if (n >= 6 && buf[0] == 'K' && buf[1] == 'a' && buf[2] == 'y' && buf[3] == 'd' &&
                        buf[4] == 'a' && buf[5] == 'r')
                        return "fbx";
                }
            }
            catch { }
            return "unknown";
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
                    // 这条路在这个游戏上不可行：游戏自身从不加载 AssetBundle，
                    // 该子系统从未初始化，类型也从未注册。
                    // 详见 README 的 "AssetBundle packages" 一节。
                    Logging.Error(
                        "[Pkg] AssetBundle packages are not supported at runtime on this game. " +
                        "Use an FBX version of the pack if one exists, or convert it offline.");
                    return null;

                default:
                    Logging.Error($"[Pkg] unrecognised package format: {path}");
                    return null;
            }
        }
    }
}
