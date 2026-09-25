using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    public sealed class InstallReport
    {
        public int Created, Replaced, Textured, Removed, Recovered, Transformed, Skipped, Failed;
        public readonly List<string> Errors = new List<string>();

        public override string ToString() =>
            $"created={Created} replaced={Replaced} textured={Textured} removed={Removed} " +
            $"recovered={Recovered} transformed={Transformed} skipped={Skipped} failed={Failed}";
    }

    /// <summary>
    /// 把 addons_config.txt 里的命令真正执行到游戏角色上。
    ///
    /// 支持的 verb：create_static_appendix / create_skinned_appendix / replace_tex /
    /// replace_mesh / remove / recover / set_scale / move_position / set_rotation
    /// </summary>
    public sealed class PackageInstaller
    {
        private readonly Transform _root;
        private readonly string _packDir;
        private readonly FbxDirPackage _pkg;

        /// <summary>部件名 -> renderer。同时用 GameObject 名和 mesh 名做键，因为配置里两种写法都有。</summary>
        private readonly Dictionary<string, SkinnedMeshRenderer> _slots =
            new Dictionary<string, SkinnedMeshRenderer>(StringComparer.OrdinalIgnoreCase);

        private readonly List<GameObject> _created = new List<GameObject>();

        public PackageInstaller(Transform root, string packDir, FbxDirPackage pkg)
        {
            _root = root;
            _packDir = packDir;
            _pkg = pkg;
        }

        public InstallReport Run(AddonButton button, string mitaName)
        {
            var rep = new InstallReport();
            IndexSlots();

            Logging.Info($"[Inst] button '{button.Name}' target='{mitaName}' " +
                         $"activate={button.Activate.Count} deactivate={button.Deactivate.Count}");

            foreach (var cmd in button.Activate)
            {
                if (!cmd.Matches(mitaName))
                {
                    rep.Skipped++;
                    continue;
                }
                Execute(cmd, rep);
            }
            return rep;
        }

        private void IndexSlots()
        {
            _slots.Clear();
            try
            {
                var smrs = _root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (smrs == null) return;
                foreach (var s in smrs)
                {
                    if (s == null) continue;
                    var goName = s.gameObject != null ? s.gameObject.name : null;
                    if (!string.IsNullOrEmpty(goName) && !_slots.ContainsKey(goName)) _slots[goName] = s;

                    var meshName = s.sharedMesh != null ? s.sharedMesh.name : null;
                    if (!string.IsNullOrEmpty(meshName) && !_slots.ContainsKey(meshName)) _slots[meshName] = s;
                }
                Logging.Verbose($"[Inst] indexed {_slots.Count} slots: {string.Join(", ", _slots.Keys)}");
            }
            catch (Exception e) { Logging.Error("[Inst] IndexSlots failed: " + e.Message); }
        }

        private SkinnedMeshRenderer Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // 1) 精确匹配（GameObject 名或 mesh 名）
            SkinnedMeshRenderer s;
            if (_slots.TryGetValue(name, out s) && s != null) return s;

            // 2) 模糊匹配：配置里常写简称，例如 Sweater，
            //    而游戏侧叫 SweaterSlot（对象名）/ Sweater_1（网格名）。
            //    只允许单向：游戏名以配置名开头。
            //    反向（配置名更长）不算 —— 否则 BodyTie1 会被误判成 Body。
            //    精确匹配优先，所以 Hair 与 Hairs 这种同名前缀不会互相抢。
            foreach (var kv in _slots)
            {
                if (kv.Value == null) continue;
                var key = kv.Key;
                if (key.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    Logging.Verbose($"[Inst] slot '{name}' -> fuzzy matched '{key}'");
                    return kv.Value;
                }
            }
            return null;
        }

        private void Execute(AddonCommand cmd, InstallReport rep)
        {
            try
            {
                switch (cmd.Verb)
                {
                    case AddonVerb.CreateSkinnedAppendix:
                    case AddonVerb.CreateStaticAppendix:
                        DoCreate(cmd, rep);
                        break;

                    case AddonVerb.ReplaceMesh:
                        DoReplaceMesh(cmd, rep);
                        break;

                    case AddonVerb.ReplaceTex:
                        DoReplaceTex(cmd, rep);
                        break;

                    case AddonVerb.Remove:
                        DoRemove(cmd, rep);
                        break;

                    case AddonVerb.Recover:
                        DoRecover(cmd, rep);
                        break;

                    case AddonVerb.SetScale:
                        DoSetScale(cmd, rep);
                        break;

                    case AddonVerb.MovePosition:
                        DoMove(cmd, rep);
                        break;

                    case AddonVerb.SetRotation:
                        DoSetRotation(cmd, rep);
                        break;

                    case AddonVerb.ResizeMesh:
                        DoResize(cmd, rep);
                        break;

                    default:
                        rep.Skipped++;
                        break;
                }
            }
            catch (Exception e)
            {
                rep.Failed++;
                rep.Errors.Add($"{cmd.RawVerb} {cmd.Target}: {e.Message}");
                Logging.Error($"[Inst] {cmd.RawVerb} '{cmd.Target}' failed: {e}");
            }
        }

        // ---- create_skinned_appendix Mita <Name> <ParentRenderer> ----
        private void DoCreate(AddonCommand cmd, InstallReport rep)
        {
            var name = cmd.Target;                       // 新部件名
            var parentName = cmd.Args.Length > 0 ? cmd.Args[0] : null;
            var parent = Find(parentName);

            if (parent == null)
            {
                Logging.Warn($"[Inst] create: parent renderer '{parentName}' not found, skipped");
                rep.Skipped++;
                return;
            }
            if (_slots.ContainsKey(name))
            {
                Logging.Info($"[Inst] create: '{name}' already exists, reused");
                rep.Skipped++;
                return;
            }

            var go = new GameObject(name);

            // 挂到父 renderer 的上一层（同级），不要挂在它底下：
            // 配置里通常是 create ... Body 紧跟着 remove Body，
            // 若挂在 Body 下，remove 一旦停用父对象，新部件会跟着消失。
            // 同级还顺带避开了继承父对象的 Transform 偏移。
            var anchor = parent.transform.parent != null ? parent.transform.parent : _root;
            go.transform.SetParent(anchor, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var smr = go.AddComponent<SkinnedMeshRenderer>();
            // 先借用父 renderer 的网格与骨骼：这样 bindpose / 骨架采样都可用，
            // 稍后的 replace_mesh 才有对齐依据。
            smr.sharedMesh = parent.sharedMesh;
            smr.bones = parent.bones;
            smr.rootBone = parent.rootBone;
            smr.sharedMaterials = parent.sharedMaterials;
            smr.updateWhenOffscreen = true;

            _slots[name] = smr;
            _created.Add(go);
            rep.Created++;
            Logging.Info($"[Inst] created '{name}' under '{parentName}'");
        }

        // ---- replace_mesh Mita <Name> <MeshFilename> <MeshName> ----
        private void DoReplaceMesh(AddonCommand cmd, InstallReport rep)
        {
            var slot = Find(cmd.Target);
            if (slot == null)
            {
                Logging.Warn($"[Inst] replace_mesh: slot '{cmd.Target}' not found");
                rep.Skipped++;
                return;
            }

            var meshFile = cmd.Args.Length > 0 ? cmd.Args[0] : null;
            var meshName = cmd.Args.Length > 1 ? cmd.Args[1] : null;

            var part = FindPart(meshFile, meshName);
            if (part == null)
            {
                Logging.Warn($"[Inst] replace_mesh: no part for file='{meshFile}' mesh='{meshName}'");
                rep.Skipped++;
                return;
            }

            var r = ModelApplier.Apply(slot, part, _root);
            if (r.Ok) { rep.Replaced++; Logging.Info($"[Inst] replaced '{cmd.Target}' <- {r}"); }
            else { rep.Failed++; rep.Errors.Add($"replace_mesh {cmd.Target}: {r.Message}"); Logging.Warn($"[Inst] replace_mesh '{cmd.Target}' failed: {r.Message}"); }
        }

        // ---- replace_tex Mita <Name> <TextureFilename> ----
        private void DoReplaceTex(AddonCommand cmd, InstallReport rep)
        {
            var slot = Find(cmd.Target);
            if (slot == null)
            {
                Logging.Warn($"[Inst] replace_tex: slot '{cmd.Target}' not found");
                rep.Skipped++;
                return;
            }

            var texArg = cmd.Args.Length > 0 ? cmd.Args[0] : null;
            var tex = LoadTexture(texArg);
            if (tex == null)
            {
                Logging.Warn($"[Inst] replace_tex: texture '{texArg}' not found");
                rep.Skipped++;
                return;
            }

            ApplyTexture(slot, tex);
            rep.Textured++;
            Logging.Info($"[Inst] textured '{cmd.Target}' with '{tex.name}' ({tex.width}x{tex.height})");
        }

        // ---- remove / recover Mita <Name> ----
        private void DoRemove(AddonCommand cmd, InstallReport rep)
        {
            var slot = Find(cmd.Target);
            if (slot == null) { rep.Skipped++; return; }

            // 只关闭 renderer 组件，不停用 GameObject。
            // SetActive(false) 会连同子对象一起隐藏，而 create_skinned_appendix
            // 建的部件有可能就挂在它下面（虽然现在改成同级了，这里再保一道）。
            slot.enabled = false;
            rep.Removed++;
            Logging.Info($"[Inst] removed '{cmd.Target}' (renderer disabled on '{slot.gameObject.name}')");
        }

        private void DoRecover(AddonCommand cmd, InstallReport rep)
        {
            var slot = Find(cmd.Target);
            if (slot == null) { rep.Skipped++; return; }
            slot.enabled = true;
            rep.Recovered++;
            Logging.Info($"[Inst] recovered '{cmd.Target}'");
        }

        // ---- transform 调整 ----
        private void DoSetScale(AddonCommand cmd, InstallReport rep)
        {
            var slot = Find(cmd.Target);
            if (slot == null) { rep.Skipped++; return; }
            var v = ParseVec(cmd.Args, 3, Vector3.one);
            slot.transform.localScale = v;
            rep.Transformed++;
            Logging.Info($"[Inst] set_scale '{cmd.Target}' = {v}");
        }

        private void DoMove(AddonCommand cmd, InstallReport rep)
        {
            var slot = Find(cmd.Target);
            if (slot == null) { rep.Skipped++; return; }
            var v = ParseVec(cmd.Args, 3, Vector3.zero);
            slot.transform.localPosition += v;
            rep.Transformed++;
            Logging.Info($"[Inst] move_position '{cmd.Target}' += {v}");
        }

        private void DoSetRotation(AddonCommand cmd, InstallReport rep)
        {
            var slot = Find(cmd.Target);
            if (slot == null) { rep.Skipped++; return; }
            float x = ParseF(cmd.Args, 0), y = ParseF(cmd.Args, 1), z = ParseF(cmd.Args, 2), w = ParseF(cmd.Args, 3, 1f);
            var q = new Quaternion(x, y, z, w);
            slot.transform.localRotation = q;
            rep.Transformed++;
            Logging.Info($"[Inst] set_rotation '{cmd.Target}' = ({x},{y},{z},{w})");
        }

        // ---- resize_mesh Mita <Name> <size> ----
        private void DoResize(AddonCommand cmd, InstallReport rep)
        {
            var slot = Find(cmd.Target);
            if (slot == null) { rep.Skipped++; return; }
            float size = ParseF(cmd.Args, 0, 1f);
            slot.transform.localScale = new Vector3(size, size, size);
            rep.Transformed++;
            Logging.Info($"[Inst] resize_mesh '{cmd.Target}' -> {size}");
        }

        // ---- 辅助 ----

        private static float ParseF(string[] a, int i, float def = 0f)
        {
            if (a == null || i >= a.Length) return def;
            float v;
            return float.TryParse(a[i], System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static Vector3 ParseVec(string[] a, int n, Vector3 def)
        {
            if (a == null || a.Length < n) return def;
            return new Vector3(ParseF(a, 0), ParseF(a, 1), ParseF(a, 2));
        }

        /// <summary>按配置里的相对路径 + 网格名在已加载的包部件里找。</summary>
        private ModelPart FindPart(string meshFile, string meshName)
        {
            if (_pkg == null) return null;

            string want = null;
            if (!string.IsNullOrEmpty(meshFile))
            {
                want = meshFile.Replace('/', '\\');
                var slash = want.LastIndexOf('\\');
                if (slash >= 0) want = want.Substring(slash + 1);   // 只留文件名部分
            }

            ModelPart loose = null;
            foreach (var p in _pkg.Parts)
            {
                if (!string.IsNullOrEmpty(meshName) &&
                    string.Equals(p.Name, meshName, StringComparison.OrdinalIgnoreCase))
                    return p;

                if (want != null && !string.IsNullOrEmpty(p.SourceFile))
                {
                    var fn = Path.GetFileNameWithoutExtension(p.SourceFile);
                    if (string.Equals(fn, want, StringComparison.OrdinalIgnoreCase)) loose = p;
                }
            }
            return loose;
        }

        /// <summary>
        /// 贴图路径在配置里是不带扩展名的相对路径，而且这个相对路径的基准
        /// 在不同包里并不一致：有的相对包根，有的会多带一层子目录名
        /// （例如包目录就是 CJ\，配置却写 CJ\cloth_merge）。
        /// 所以按 完整路径 -> 只取文件名 -> 递归同名 的顺序找。
        /// </summary>
        private Texture2D LoadTexture(string texArg)
        {
            try
            {
                if (string.IsNullOrEmpty(texArg)) return null;
                var rel = texArg.Replace('/', '\\');
                var fileOnly = rel;
                var slash = fileOnly.LastIndexOf('\\');
                if (slash >= 0) fileOnly = fileOnly.Substring(slash + 1);

                var exts = new[] { ".png", ".jpg", ".jpeg", ".tga" };
                var candidates = new List<string>();

                // 1) 按配置给的完整相对路径
                foreach (var ext in exts) candidates.Add(Path.Combine(_packDir, rel + ext));
                candidates.Add(Path.Combine(_packDir, rel));

                // 2) 只用文件名（应对多带一层目录前缀的情况）
                if (!string.Equals(fileOnly, rel, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var ext in exts) candidates.Add(Path.Combine(_packDir, fileOnly + ext));
                    candidates.Add(Path.Combine(_packDir, fileOnly));
                }

                // 3) 递归找同名（带扩展名）
                foreach (var ext in exts)
                {
                    try
                    {
                        var hits = Directory.GetFiles(_packDir, fileOnly + ext, SearchOption.AllDirectories);
                        foreach (var h in hits) candidates.Add(h);
                    }
                    catch { }
                }

                foreach (var c in candidates)
                {
                    if (!File.Exists(c)) continue;
                    var bytes = File.ReadAllBytes(c);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, bytes))
                    {
                        Logging.Warn($"[Inst] failed to decode texture: {c}");
                        continue;
                    }
                    tex.name = Path.GetFileName(c);
                    tex.wrapMode = TextureWrapMode.Clamp;
                    Logging.Info($"[Inst] texture resolved: {c}");
                    return tex;
                }

                // 没找到就打印目录内容帮助定位
                Logging.Warn($"[Inst] texture not found: '{texArg}'. pack contents:");
                foreach (var f in Directory.GetFiles(_packDir, "*", SearchOption.AllDirectories))
                    Logging.Warn("[Inst]    " + f.Substring(_packDir.Length + 1));
                return null;
            }
            catch (Exception e)
            {
                Logging.Warn("[Inst] LoadTexture failed: " + e.Message);
                return null;
            }
        }

        /// <summary>用目标 renderer 原有材质的 shader，只替换主贴图。</summary>
        private static void ApplyTexture(SkinnedMeshRenderer target, Texture2D tex)
        {
            try
            {
                var mats = target.sharedMaterials;
                if (mats == null || mats.Length == 0)
                {
                    var m = new Material(Shader.Find("Standard"));
                    m.mainTexture = tex;
                    target.sharedMaterial = m;
                    return;
                }

                var newMats = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    Material nm;
                    if (src != null && src.shader != null)
                    {
                        nm = new Material(src.shader);
                        nm.CopyPropertiesFromMaterial(src);
                    }
                    else
                    {
                        nm = new Material(Shader.Find("Standard"));
                    }
                    nm.mainTexture = tex;
                    try { nm.SetTexture("_BaseMap", tex); } catch { }
                    try { nm.SetTexture("_MainTex", tex); } catch { }
                    newMats[i] = nm;
                }
                target.sharedMaterials = newMats;
            }
            catch (Exception e) { Logging.Warn("[Inst] ApplyTexture failed: " + e.Message); }
        }
    }
}
