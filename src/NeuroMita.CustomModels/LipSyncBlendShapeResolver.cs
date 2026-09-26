using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    public sealed class LipSyncBlendShapeMapping
    {
        public string AName;
        public int AIndex = -1;
        public string OName;
        public int OIndex = -1;
        public bool HasA => AIndex >= 0;
        public bool HasO => OIndex >= 0;
    }

    public static class LipSyncBlendShapeResolver
    {
        private static readonly string[] MouthAAliases =
        {
            "A", "AA", "Ah", "MouthA", "Mouth_A", "mouth_a", "vrc.v_aa", "Fcl_MTH_A"
        };

        private static readonly string[] MouthOAliases =
        {
            "O", "OH", "Oh", "MouthO", "Mouth_O", "mouth_o", "vrc.v_oh", "Fcl_MTH_O"
        };

        public static LipSyncBlendShapeMapping Resolve(Mesh mesh)
        {
            if (mesh == null) return new LipSyncBlendShapeMapping();
            var names = new List<string>(mesh.blendShapeCount);
            for (int i = 0; i < mesh.blendShapeCount; i++) names.Add(mesh.GetBlendShapeName(i));
            return Resolve(names);
        }

        public static LipSyncBlendShapeMapping Resolve(IList<string> names)
        {
            var result = new LipSyncBlendShapeMapping();
            if (names == null) return result;
            result.AIndex = Find(names, MouthAAliases, true);
            result.OIndex = Find(names, MouthOAliases, false);
            if (result.AIndex >= 0) result.AName = names[result.AIndex];
            if (result.OIndex >= 0) result.OName = names[result.OIndex];
            return result;
        }

        private static int Find(IList<string> names, string[] aliases, bool mouthA)
        {
            foreach (var alias in aliases)
                for (int i = 0; i < names.Count; i++)
                    if (string.Equals(names[i], alias, StringComparison.OrdinalIgnoreCase)) return i;

            foreach (var alias in aliases)
            {
                string normalizedAlias = Normalize(alias);
                for (int i = 0; i < names.Count; i++)
                    if (string.Equals(Normalize(names[i]), normalizedAlias, StringComparison.Ordinal)) return i;
            }

            for (int i = 0; i < names.Count; i++)
            {
                string normalized = Normalize(names[i]);
                if (normalized.StartsWith("mouth", StringComparison.Ordinal) || normalized.StartsWith("fclmth", StringComparison.Ordinal))
                {
                    string token = normalized.StartsWith("mouth", StringComparison.Ordinal) ? normalized.Substring(5) : normalized.Substring(6);
                    if (MatchesPhoneme(token, mouthA)) return i;
                }
                if (normalized.StartsWith("vrcv_", StringComparison.Ordinal) && MatchesPhoneme(normalized.Substring(5), mouthA)) return i;
            }
            return -1;
        }

        private static bool MatchesPhoneme(string token, bool mouthA)
        {
            return mouthA
                ? token == "a" || token == "aa" || token == "ah"
                : token == "o" || token == "oh";
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            return builder.ToString();
        }
    }
}
