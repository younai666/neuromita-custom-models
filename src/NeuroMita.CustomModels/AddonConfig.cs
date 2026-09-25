using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NeuroMita.CustomModels
{
    public enum AddonVerb
    {
        Unknown,
        CreateStaticAppendix,
        CreateSkinnedAppendix,
        ReplaceTex,
        ReplaceMesh,
        Remove,
        Recover,
        SetScale,
        MovePosition,
        SetRotation,
        ResizeMesh,
    }

    /// <summary>
    /// addons_config.txt 里的一条命令。
    ///
    /// 语法： &lt;verb&gt; &lt;Prefab&gt; &lt;Name&gt; [固定个数的参数...] [KeyWord...]
    /// 每个 verb 的参数个数是固定的，所以剩下的 token 就是 KeyWord。
    /// </summary>
    public sealed class AddonCommand
    {
        public AddonVerb Verb;
        public string RawVerb;
        public string Prefab;          // 通常是 "Mita"
        public string Target;          // 部件名，或新部件的名字
        public string[] Args = Array.Empty<string>();
        public string[] Keywords = Array.Empty<string>();
        public string Raw;

        /// <summary>KeyWord 是否命中给定的角色名。全部条件都要满足（! 表示取反）。</summary>
        public bool Matches(string mitaName)
        {
            if (Keywords.Length == 0) return true;
            var n = mitaName ?? "";
            foreach (var kw in Keywords)
            {
                if (string.IsNullOrEmpty(kw)) continue;
                if (kw[0] == '!')
                {
                    var w = kw.Substring(1);
                    if (w.Length > 0 && n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return false;
                }
                else
                {
                    if (kw.Equals("all", StringComparison.OrdinalIgnoreCase)) continue;
                    if (n.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }
            }
            return true;
        }

        public override string ToString() =>
            $"{RawVerb} {Prefab} {Target} [{string.Join(" ", Args)}] kw={string.Join(",", Keywords)}";
    }

    /// <summary>配置文件里的一个按钮（*Name / 激活命令 / -反激活命令）。</summary>
    public sealed class AddonButton
    {
        public string Name;
        public readonly List<AddonCommand> Activate = new List<AddonCommand>();
        public readonly List<AddonCommand> Deactivate = new List<AddonCommand>();
    }

    /// <summary>addons_config.txt 的解析结果。</summary>
    public sealed class AddonConfig
    {
        public readonly List<AddonButton> Buttons = new List<AddonButton>();
        public readonly List<AddonCommand> Loose = new List<AddonCommand>();   // 不在任何按钮下的命令

        /// <summary>每个 verb 后面跟的固定参数个数。</summary>
        /// <remarks>
        /// 注意这里是"Target 之后还有几个参数" —— Target 本身是 tok[2] 已经存进 cmd.Target 了。
        /// 例如 `remove Mita Pantyhose` 里 Pantyhose 就是 Target，后面没有参数。
        /// </remarks>
        private static readonly Dictionary<string, int> ArgCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "create_static_appendix",  1 },   // ParentRenderer
                { "create_skinned_appendix", 1 },   // ParentRenderer
                { "replace_tex",             1 },   // TextureFilename
                { "replace_mesh",            2 },   // MeshFilename MeshName
                { "remove",                  0 },
                { "recover",                 0 },
                { "set_scale",               3 },   // x y z
                { "move_position",           3 },   // x y z
                { "set_rotation",            4 },   // x y z w
                { "resize_mesh",             1 },   // size（统一缩放，见于 chibi 变体）
            };

        private static AddonVerb MapVerb(string v)
        {
            switch ((v ?? "").ToLowerInvariant())
            {
                case "create_static_appendix":  return AddonVerb.CreateStaticAppendix;
                case "create_skinned_appendix": return AddonVerb.CreateSkinnedAppendix;
                case "replace_tex":             return AddonVerb.ReplaceTex;
                case "replace_mesh":            return AddonVerb.ReplaceMesh;
                case "remove":                  return AddonVerb.Remove;
                case "recover":                 return AddonVerb.Recover;
                case "set_scale":               return AddonVerb.SetScale;
                case "move_position":           return AddonVerb.MovePosition;
                case "set_rotation":            return AddonVerb.SetRotation;
                case "resize_mesh":             return AddonVerb.ResizeMesh;
                default:                        return AddonVerb.Unknown;
            }
        }

        public static AddonConfig Parse(string text)
        {
            var cfg = new AddonConfig();
            if (string.IsNullOrEmpty(text)) return cfg;

            // 去掉 BOM。这类配置的 BOM 不一定在文件开头 ——
            // 实测有包把它放在注释块之后、按钮行之前，所以整篇清一遍。
            text = text.Replace("\uFEFF", "");

            AddonButton current = null;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith("//")) continue;

                // 按钮：*Name
                if (line[0] == '*' && line.Length > 1)
                {
                    current = new AddonButton { Name = line.Substring(1).Trim() };
                    cfg.Buttons.Add(current);
                    continue;
                }

                // 反激活命令：-cmd
                bool deactivate = false;
                if (line[0] == '-')
                {
                    deactivate = true;
                    line = line.Substring(1).Trim();
                }
                if (line.Length == 0) continue;

                var cmd = ParseCommand(line);
                if (cmd == null) continue;

                if (current == null)
                {
                    cfg.Loose.Add(cmd);
                }
                else if (deactivate)
                {
                    current.Deactivate.Add(cmd);
                }
                else
                {
                    current.Activate.Add(cmd);
                }
            }

            return cfg;
        }

        public static AddonConfig FromFile(string path)
        {
            if (!File.Exists(path)) return new AddonConfig();
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        private static AddonCommand ParseCommand(string line)
        {
            var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 3) return null;

            var verb = tok[0];
            var cmd = new AddonCommand
            {
                Raw = line,
                RawVerb = verb,
                Verb = MapVerb(verb),
                Prefab = tok[1],
                Target = tok[2],
            };

            if (cmd.Verb == AddonVerb.Unknown)
            {
                Logging.Warn($"[Dsl] unknown command, skipped: {line}");
                return null;
            }

            int want = ArgCounts.TryGetValue(verb, out var n) ? n : 0;

            var rest = new List<string>();
            for (int i = 3; i < tok.Length; i++) rest.Add(tok[i]);

            if (rest.Count < want)
            {
                Logging.Warn($"[Dsl] '{verb}' needs {want} args, got {rest.Count}: {line}");
                return null;
            }

            cmd.Args = rest.GetRange(0, want).ToArray();
            cmd.Keywords = rest.GetRange(want, rest.Count - want).ToArray();
            return cmd;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"{Buttons.Count} buttons, {Loose.Count} loose commands");
            foreach (var b in Buttons)
                sb.Append($"\n  [{b.Name}] +{b.Activate.Count} -{b.Deactivate.Count}");
            return sb.ToString();
        }
    }
}
