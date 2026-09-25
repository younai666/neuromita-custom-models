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
        public const string PluginVersion = "0.2.0";

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
        private bool _waitLogged;
        private int _lastSceneHandle = int.MinValue;
        private bool _routesSettled;
        private readonly HashSet<string> _installedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _routesDoneInScene = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _routeWaitLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                var dir = Plugin.CfgPackDir.Value;

                // 场景切换时清空路由缓存：新场景里的角色是全新实例，
                // 而且旧实例上装的蒙皮会随骨架销毁而失效，必须重装。
                int sceneHandle = 0;
                try { sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { }
                if (sceneHandle != _lastSceneHandle)
                {
                    _lastSceneHandle = sceneHandle;
                    _installedRoots.Clear();
                    _routeWaitLogged.Clear();
                    _routesDoneInScene.Clear();
                    _routesSettled = false;
                    Logging.Verbose($"[CM] scene changed (handle={sceneHandle}); route cache cleared");
                }

                // 1) 先看有没有"角色文件夹"（CustomModels\Player\、CustomModels\Crazy\ ...）
                //    这种结构下一份安装可以同时管多个角色。
                var routes = DiscoverRoutes(dir);
                if (routes.Count > 0)
                {
                    // 本场景里每个路由都至少装到一个实例后，就没必要继续扫描了
                    // （扫描要遍历全场 renderer 并拼路径，进新场景时会自动重新开始）。
                    if (_routesSettled) return false;

                    foreach (var route in routes)
                    {
                        var roots = FindCharacterRoots(route.Key);
                        if (roots.Count == 0)
                        {
                            if (_routeWaitLogged.Add(route.Key))
                                Logging.Info($"[CM] '{route.Key}': character not in this scene yet " +
                                             $"({route.Packs.Count} pack(s) waiting)");
                            continue;
                        }

                        // 同一个角色可能有多个实例（主菜单的静态展示件 + 游戏内真身），
                        // 逐个装配。用"实例 ID"去重：场景重载会产生新对象，需要重新装配。
                        foreach (var rroot in roots)
                        {
                            var instKey = route.Key + "#" + rroot.GetInstanceID();
                            if (!_installedRoots.Add(instKey)) continue;

                            Logging.Info($"[CM] ===== character '{route.Key}' -> '{rroot.name}' " +
                                         $"({route.Packs.Count} pack(s)) =====");
                            foreach (var pack in route.Packs)
                            {
                                Logging.Info($"[CM] ----- installing '{Path.GetFileName(pack)}' -----");
                                InstallOne(pack, rroot, route.Key);
                            }
                        }
                        _routesDoneInScene.Add(route.Key);
                    }

                    bool all = true;
                    foreach (var r in routes) if (!_routesDoneInScene.Contains(r.Key)) { all = false; break; }
                    _routesSettled = all;
                    return false;
                }

                // 2) 旧行为：单个 TargetAvatar + PackDirectory
                var keyword = Plugin.CfgAvatar.Value;
                var anim = FindTarget(keyword);
                if (anim == null)
                {
                    if (!_waitLogged)
                    {
                        Logging.Info($"[CM] waiting for a character whose Avatar name contains '{keyword}' " +
                                     "(enter a scene where a Mita is present)");
                        _waitLogged = true;
                    }
                    return false;
                }

                var root = anim.transform;
                var avatarName = "";
                try { if (anim.avatar != null) avatarName = anim.avatar.name ?? ""; } catch { }
                Logging.Info($"[CM] target: '{root.name}'  avatar='{avatarName}'");

                var packs = DiscoverPacks(dir, Plugin.CfgActivePack.Value);
                Logging.Info($"[CM] pack dir: {dir}");
                Logging.Info($"[CM] packs found: {packs.Count} [{string.Join(", ", packs)}]");
                if (packs.Count == 0)
                {
                    Logging.Warn("[CM] no model packs found; nothing to do");
                    return true;
                }

                var mitaName = ResolveMitaName(avatarName, root);

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

        /// <summary>一个角色文件夹：文件夹名（角色 id）+ 里面的包。</summary>
        private sealed class Route
        {
            public string Key;
            public List<string> Packs = new List<string>();
        }

        /// <summary>
        /// 已知角色文件夹名 -> 匹配游戏对象用的关键词组。
        /// 数组里任一关键词命中即可（用于容忍游戏内的拼写差异）。
        /// </summary>
        private static readonly Dictionary<string, string[]> RouteKeys =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Player",    new[] { "ViewRoot/Person" } },
                { "Crazy",     new[] { "Mita Crazy" } },
                { "Kind",      new[] { "Mita Kind" } },
                { "Cappie",    new[] { "Mita Cappie", "Mita Cappy" } },
                { "Cappy",     new[] { "Mita Cappie", "Mita Cappy" } },
                { "ShortHair", new[] { "Mita ShortHair" } },
                { "Mila",      new[] { "Mita Mila" } },
                { "Sleepy",    new[] { "Mita Dream" } },
                { "Dream",     new[] { "Mita Dream" } },
                { "Ghost",     new[] { "Mita Ghost" } },
            };

        /// <summary>
        /// 扫描包目录的顶层子目录：名字是已知角色 id 的，视为"角色文件夹"，
        /// 它里面的每个子目录/文件是一个包。返回空表示没有用这种结构。
        /// </summary>
        private static List<Route> DiscoverRoutes(string dir)
        {
            var routes = new List<Route>();
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return routes;

                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (!RouteKeys.ContainsKey(name)) continue;

                    var r = new Route { Key = name };
                    foreach (var inner in Directory.GetDirectories(sub)) r.Packs.Add(inner);
                    foreach (var f in Directory.GetFiles(sub))
                    {
                        if (f.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                            ModelPackage.LooksLikeBundle(f))
                            r.Packs.Add(f);
                    }
                    r.Packs.Sort(StringComparer.OrdinalIgnoreCase);
                    routes.Add(r);
                }
            }
            catch (Exception e) { Logging.Error("[CM] DiscoverRoutes failed: " + e.Message); }
            return routes;
        }

        /// <summary>
        /// 按角色 id 找出该角色的**所有**骨架根实例。
        /// 同一个角色常常有多个实例：主菜单里的静态展示件（Legacy）、
        /// 游戏内的真身（MitaCore (Start)/Mitas/）、以及旧版残留。
        /// 它们都要装配，玩家无论在哪看到都是新模型。
        /// </summary>
        private static List<Transform> FindCharacterRoots(string routeKey)
        {
            var result = new List<Transform>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string[] keys;
                if (!RouteKeys.TryGetValue(routeKey, out keys)) return result;

                var smrs = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true);
                if (smrs == null) return result;

                // 两轮：先收集当前激活的实例（正在显示的那个优先），再收集其余的
                for (int pass = 0; pass < 2; pass++)
                {
                    bool wantActive = pass == 0;
                    foreach (var s in smrs)
                    {
                        if (s == null) continue;
                        if (wantActive && !s.gameObject.activeInHierarchy) continue;

                        var path = TransformPath(s.transform);
                        foreach (var k in keys)
                        {
                            if (path.IndexOf(k, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var root = ResolveRoot(s.transform, routeKey);
                            if (root == null) break;
                            var rp = TransformPath(root);
                            if (seen.Add(rp)) result.Add(root);
                            break;
                        }
                    }
                }
            }
            catch (Exception e) { Logging.Warn("[CM] FindCharacterRoots failed: " + e.Message); }

            // 一个都没找到时，Verbose 级别提示场景里有哪些含 "Mita" 的 renderer
            if (result.Count == 0 && Logging.VerboseEnabled)
            {
                try
                {
                    var all = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true);
                    int shown = 0;
                    if (all != null)
                    {
                        foreach (var s in all)
                        {
                            if (s == null || shown >= 4) break;
                            var p = TransformPath(s.transform);
                            if (p.IndexOf("Mita", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            Logging.Verbose($"[CM]   waiting; scene has: {p} (active={s.gameObject.activeInHierarchy})");
                            shown++;
                        }
                    }
                    if (shown == 0) Logging.Verbose("[CM]   waiting; no renderer path contains 'Mita'");
                }
                catch { }
            }
            return result;
        }

        /// <summary>从命中的 renderer 往上找出该角色的骨架根。</summary>
        private static Transform ResolveRoot(Transform from, string routeKey)
        {
            if (string.Equals(routeKey, "Player", StringComparison.OrdinalIgnoreCase))
            {
                // 玩家：骨架根是 ViewRoot 下的 Person
                var t = from;
                int g = 0;
                while (t != null && g++ < 64)
                {
                    if (t.gameObject.name == "Person") return t;
                    t = t.parent;
                }
                return from.root;
            }

            // 米塔：往上找名字里含 "Mita XXX" 的那一层（取最靠上的命中）
            string[] keys;
            if (!RouteKeys.TryGetValue(routeKey, out keys)) return from.root;

            Transform best = null;
            var cur = from;
            int guard = 0;
            while (cur != null && guard++ < 64)
            {
                var n = cur.gameObject.name;
                foreach (var kk in keys)
                    if (n.IndexOf(kk, StringComparison.OrdinalIgnoreCase) >= 0) best = cur;
                cur = cur.parent;
            }
            return best ?? from.root;
        }

        private static string TransformPath(Transform t)
        {
            var parts = new List<string>();
            var cur = t;
            int guard = 0;
            while (cur != null && guard++ < 64) { parts.Add(cur.gameObject.name); cur = cur.parent; }
            parts.Reverse();
            return string.Join("/", parts);
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

                // 没有 addons_config.txt（AssetBundle 包，或裸 FBX）：
                // 按部件名自动匹配槽位；匹配不上再退化为"整体替换"。
                var bp = pkg as BundlePackage;
                var multi = pkg.Parts.Count > 1;

                if (multi)
                {
                    int applied = 0;
                    foreach (var p in pkg.Parts)
                    {
                        var slot = FindBestRenderer(root, p.Name);
                        if (slot == null)
                        {
                            Logging.Warn($"[CM] no slot matched part '{p.Name}'");
                            continue;
                        }
                        var rr = ModelApplier.Apply(slot, p, root);
                        Logging.Info($"[CM] RESULT {rr}");
                        ApplyTexture(bp, slot, p.Name);
                        applied++;
                    }

                    if (applied > 0)
                    {
                        Logging.Info($"[CM] auto-slotted {applied}/{pkg.Parts.Count} parts");
                        return;
                    }
                    Logging.Warn("[CM] no part matched any slot; falling back to whole-body replace");
                }

                // 整体替换（一体化网格，例如"整个人一个 mesh"的包）：
                // 装到 Body，并把同角色的其余部件隐藏，避免原模型穿帮。
                {
                    var body = FindRenderer(root, Plugin.CfgFallbackRenderer.Value);
                    if (body == null)
                    {
                        Logging.Error($"[CM] no SkinnedMeshRenderer matching fallback '{Plugin.CfgFallbackRenderer.Value}'");
                        return;
                    }

                    var part = pkg.Parts[0];
                    Logging.Info($"[CM] applying '{part.Name}' -> '{body.gameObject.name}' (whole-body)");
                    var r = ModelApplier.Apply(body, part, root);
                    Logging.Info($"[CM] RESULT {r}");
                    ApplyTexture(bp, body, part.Name);

                    if (r.Ok)
                    {
                        int hidden = HideOtherRenderers(root, body, pkg.Parts.Count);
                        Logging.Info($"[CM] whole-body replace: hid {hidden} other renderer(s)");
                    }
                }
            }
        }

        /// <summary>把包里的贴图贴到该槽位的材质上（AssetBundle 包专用）。</summary>
        private static void ApplyTexture(BundlePackage bp, SkinnedMeshRenderer slot, string partName)
        {
            if (bp == null || slot == null || bp.Textures.Count == 0) return;
            try
            {
                var tex = PickTexture(bp, partName);
                if (tex == null) return;
                var mats = slot.sharedMaterials;
                if (mats == null) return;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null) mats[i].mainTexture = tex;
                Logging.Info($"[CM]   texture '{tex.name}' -> {slot.gameObject.name}");
            }
            catch (Exception e) { Logging.Warn("[CM] ApplyTexture failed: " + e.Message); }
        }

        /// <summary>挑一张贴图：先按部件名模糊匹配，否则取第一张。</summary>
        private static UnityEngine.Texture2D PickTexture(BundlePackage bp, string partName)
        {
            try
            {
                var low = (partName ?? "").ToLowerInvariant();
                foreach (var kv in bp.Textures)
                {
                    var n = kv.Key.ToLowerInvariant();
                    if (low.Contains("hair") && n.Contains("hair")) return kv.Value;
                    if ((low.Contains("cloth") || low.Contains("body") || low.Contains("arm")) &&
                        (n.Contains("cloth") || n.Contains("body") || n.Contains("arm"))) return kv.Value;
                }
                foreach (var kv in bp.Textures) return kv.Value;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 隐藏同角色下的其他 renderer（整体替换后用）。
        /// 只处理同一个骨架子树，不会碰到别的角色。
        /// </summary>
        private static int HideOtherRenderers(Transform root, SkinnedMeshRenderer keep, int keepCount)
        {
            int n = 0;
            try
            {
                var all = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (all == null) return 0;
                foreach (var s in all)
                {
                    if (s == null || s == keep) continue;
                    if (s.enabled) { s.enabled = false; n++; }
                }
            }
            catch { }
            return n;
        }

        /// <summary>
        /// 按部件名给 renderer 打分选最合适的一个。
        /// 包的部件名千奇百怪（Body / Clothes / Arm / Head / Hair / Sweater...），
        /// 这里只做"关键词包含"的宽松匹配。
        /// </summary>
        private static SkinnedMeshRenderer FindBestRenderer(Transform root, string partName)
        {
            try
            {
                var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (smrs == null || smrs.Length == 0) return null;

                string key = null;
                var low = (partName ?? "").ToLowerInvariant();
                if (low.Contains("hair")) key = "hair";
                else if (low.Contains("head") || low.Contains("face")) key = "head";
                else if (low.Contains("arm") || low.Contains("hand") || low.Contains("glove")) key = "arm";
                else if (low.Contains("cloth") || low.Contains("sweater") || low.Contains("body")
                         || low.Contains("skirt") || low.Contains("pant") || low.Contains("shoe")) key = "body";

                if (key == null) return null;

                foreach (var s in smrs)
                {
                    if (s == null) continue;
                    var n = (s.gameObject != null ? s.gameObject.name : "").ToLowerInvariant();
                    bool hit = key == "hair" ? n.Contains("hair")
                             : key == "head" ? (n.Contains("head") || n.Contains("face"))
                             : key == "arm" ? n.Contains("arm")
                             : (n.Contains("body") || n.Contains("cloth"));
                    if (hit) return s;
                }
            }
            catch { }
            return null;
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
