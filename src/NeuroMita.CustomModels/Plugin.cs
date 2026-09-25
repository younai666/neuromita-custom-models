using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BepInEx.Unity.IL2CPP.BasePlugin
    {
        public const string PluginGuid = "com.neuromita.custommodels";
        public const string PluginName = "NeuroMita.CustomModels";
        public const string PluginVersion = "0.1.0";

        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<string> CfgPackDir;
        internal static ConfigEntry<string> CfgAvatar;
        internal static ConfigEntry<string> CfgActivePack;
        internal static ConfigEntry<string> CfgFallbackRenderer;
        internal static ConfigEntry<bool> CfgVerbose;

        public override void Load()
        {
            Logging.Init(Log);
            Logging.Info($"[CM] ===== {PluginName} {PluginVersion} =====");

            var defaultDir = Path.Combine(Paths.GameRootPath, "CustomModels");

            CfgEnabled = Config.Bind("General", "Enabled", true,
                "Master switch. When false the plugin loads nothing.");

            CfgPackDir = Config.Bind("General", "PackDirectory", defaultDir,
                "Folder scanned for model packs. Each subfolder (or single file) is treated as one pack. " +
                "Supported: an FBX pack directory with addons_config.txt, or a bare .fbx file.");

            CfgAvatar = Config.Bind("General", "TargetAvatar", "Crazy",
                "Which character to replace, matched against the Animator's Avatar name. " +
                "Examples: Crazy (CrazyMitaAvatar), Good, Mila, ShortHair, Player.");

            CfgActivePack = Config.Bind("General", "ActivePack", "",
                "Install only this pack (folder or file name). Empty = install every pack found.");

            CfgFallbackRenderer = Config.Bind("General", "FallbackRenderer", "Body",
                "Only used for bare .fbx packs that carry no addons_config.txt: " +
                "which renderer slot to replace. Packs with a config file ignore this.");

            CfgVerbose = Config.Bind("Diagnostics", "Verbose", true,
                "Log per-part alignment details. Summary lines (INSTALL RESULT) are always logged.");

            // 让这个配置真正生效：Verbose 关闭后只保留给用户看的摘要
            Logging.VerboseEnabled = CfgVerbose.Value;

            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<ModelRuntime>();
                var go = new GameObject("NeuroMita.CustomModels");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<ModelRuntime>();
                Logging.Info("[CM] runtime injected");
            }
            catch (Exception e)
            {
                Logging.Error("[CM] inject failed: " + e);
            }
        }
    }

    public class ModelRuntime : MonoBehaviour
    {
        private int _ticks;
        private bool _done;
        private bool _waitoogged;

        public ModelRuntime(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            _ticks++;
            if (_done || _ticks % 120 != 0) return;
            _done = TryRun();
        }

        private bool TryRun()
        {
            try
            {
                if (!Plugin.CfgEnabled.Value) return true;

                // 1) 目标角色
                var keyword = Plugin.CfgAvatar.Value;
                var anim = FindTarget(keyword);
                if (anim == null)
                {
                    if (!_waitoogged)
                    {
                        Logging.Info($"[CM] waiting for a character whose Avatar name contains '{keyword}' " +
                                     "(enter a scene where a Mita is present)");
                        _waitoogged = true;
                    }
                    return false;
                }

                var root = anim.transform;
                var avatarName = "";
                try { if (anim.avatar != null) avatarName = anim.avatar.name ?? ""; } catch { }
                Logging.Info($"[CM] target: '{root.name}'  avatar='{avatarName}'");

                // 2) 找模型包
                var dir = Plugin.CfgPackDir.Value;
                var packs = DiscoverPacks(dir, Plugin.CfgActivePack.Value);
                Logging.Info($"[CM] pack dir: {dir}");
                Logging.Info($"[CM] packs found: {packs.Count} [{string.Join(", ", packs)}]");
                if (packs.Count == 0)
                {
                    Logging.Warn("[CM] no model packs found; nothing to do");
                    return true;
                }

                var mitaName = ResolveMitaName(avatarName, root);

                // 3) 逐个安装
                foreach (var path in packs)
                {
                    Logging.Info($"[CM] ===== installing '{Path.GetFileName(path)}' =====");
                    InstallOne(path, root, mitaName);
                }

                Logging.Info("[CM] all packs processed");
                return true;
            }
            catch (Exception e)
            {
                Logging.Error("[CM] TryRun failed: " + e);
                return true;
            }
        }

        /// <summary>列出模型包：目录下的每个子目录、以及散落的 .fbx / UnityFS 文件。</summary>
        private static List<string> DiscoverPacks(string dir, string only)
        {
            var list = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    Logging.Warn($"[CM] pack directory does not exist: {dir}");
                    return list;
                }

                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (!string.IsNullOrEmpty(only) && !string.Equals(name, only, StringComparison.OrdinalIgnoreCase))
                        continue;
                    list.Add(sub);
                }

                foreach (var f in Directory.GetFiles(dir, "*.fbx"))
                {
                    var name = Path.GetFileName(f);
                    if (!string.IsNullOrEmpty(only) && !string.Equals(name, only, StringComparison.OrdinalIgnoreCase))
                        continue;
                    list.Add(f);
                }
            }
            catch (Exception e) { Logging.Error("[CM] DiscoverPacks failed: " + e.Message); }
            return list;
        }

        private static void InstallOne(string path, Transform root, string mitaName)
        {
            var fmt = ModelPackage.DetectFormat(path);
            Logging.Info($"[CM] format: {fmt}");

            var pkg = ModelPackage.Open(path);
            if (pkg == null)
            {
                Logging.Error($"[CM] unsupported package format: {path}");
                return;
            }

            using (pkg)
            {
                if (!pkg.Open())
                {
                    Logging.Error($"[CM] failed to open: {path}");
                    return;
                }
                Logging.Info($"[CM] parts: {pkg.Parts.Count}");
                if (pkg.Parts.Count == 0) return;

                var dirPkg = pkg as FbxDirPackage;
                if (dirPkg != null && !string.IsNullOrEmpty(dirPkg.ConfigText))
                {
                    var cfg = AddonConfig.Parse(dirPkg.ConfigText);
                    Logging.Info("[CM] config: " + cfg.Describe());

                    if (cfg.Buttons.Count > 0)
                    {
                        // 贴图相对路径以配置所在目录为基准（配置可能在子目录里）
                        var baseDir = !string.IsNullOrEmpty(dirPkg.ConfigDir) ? dirPkg.ConfigDir : dirPkg.RootPath;
                        var installer = new PackageInstaller(root, baseDir, dirPkg);
                        var rep = installer.Run(cfg.Buttons[0], mitaName);
                        Logging.Info($"[CM] INSTALL RESULT {rep}");
                        foreach (var err in rep.Errors) Logging.Warn("[CM]   err: " + err);
                        return;
                    }
                    Logging.Warn("[CM] config has no button, falling back to single-part replace");
                }

                // 裸 FBX（没有 addons_config.txt）：替换回退槽位，默认 Body
                var fallback = Plugin.CfgFallbackRenderer.Value;
                var target = FindRenderer(root, fallback);
                if (target == null)
                {
                    Logging.Error($"[CM] no SkinnedMeshRenderer matching fallback '{fallback}'");
                    return;
                }
                var part = pkg.Parts[0];
                Logging.Info($"[CM] applying '{part.Name}' -> '{target.gameObject.name}'");
                var r = ModelApplier.Apply(target, part, root);
                Logging.Info($"[CM] RESULT {r}");
            }
        }

        /// <summary>
        /// 用于 KeyWord 匹配的名字。用 Avatar 名而不是场景路径 ——
        /// 路径里含 "MitaCore (Start)" 这类容器名，会让配置里的 "!Core" 误伤。
        /// </summary>
        private static string ResolveMitaName(string avatarName, Transform root)
        {
            var name = ((avatarName ?? "") + " " + (root != null ? root.name : "")).Trim();
            Logging.Info($"[CM] keyword-match name = '{name}'");
            return name;
        }

        private static Animator FindTarget(string avatarKeyword)
        {
            try
            {
                var animators = UnityEngine.Object.FindObjectsOfType<Animator>();
                if (animators == null) return null;
                foreach (var a in animators)
                {
                    if (a == null) continue;
                    string av = "";
                    try { if (a.avatar != null) av = a.avatar.name ?? ""; } catch { }
                    if (av.Length > 0 && av.IndexOf(avatarKeyword, StringComparison.OrdinalIgnoreCase) >= 0) return a;
                }
            }
            catch { }
            return null;
        }

        private static SkinnedMeshRenderer FindRenderer(Transform root, string keyword)
        {
            try
            {
                var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (smrs == null) return null;

                SkinnedMeshRenderer first = null;
                foreach (var s in smrs)
                {
                    if (s == null) continue;
                    if (first == null) first = s;
                    var n = s.gameObject != null ? s.gameObject.name : "";
                    if (n.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return s;
                }
                return first;
            }
            catch { }
            return null;
        }
    }
}
