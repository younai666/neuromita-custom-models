using System;
using System.Collections.Generic;

namespace NeuroMita.CustomModels
{
    internal static class RendererSlotMatcher
    {
        public static int Score(string partName, string rendererName, string meshName, IList<string> materialNames)
        {
            var partTokens = Tokens(partName);
            var partRole = Classify(partTokens);
            var rendererTokens = Tokens(rendererName);
            var rendererRole = Classify(rendererTokens);
            var meshTokens = Tokens(meshName);
            var meshRole = Classify(meshTokens);
            var materialTokens = new List<string>();
            if (materialNames != null)
                foreach (var materialName in materialNames) materialTokens.AddRange(Tokens(materialName));
            var materialRole = Classify(materialTokens);

            var candidateRole = rendererRole != Role.Unknown ? rendererRole
                : meshRole != Role.Unknown ? meshRole : materialRole;
            if (partRole != Role.Unknown && candidateRole != Role.Unknown && partRole != candidateRole)
                return -1;

            int score = partRole != Role.Unknown && partRole == candidateRole ? 100 : 0;
            string normalizedPart = string.Concat(partTokens);
            if (normalizedPart.Length == 0) return 0;
            bool exactRenderer = normalizedPart == string.Concat(rendererTokens);
            bool exactMesh = normalizedPart == string.Concat(meshTokens);
            if (partRole == Role.Unknown)
                return exactRenderer ? 1000 : exactMesh ? 800 : 0;

            if (exactRenderer) score += 1000;
            else if (ContainsSequence(rendererTokens, partTokens)) score += 300;
            if (exactMesh) score += 800;
            else if (ContainsSequence(meshTokens, partTokens)) score += 200;
            if (ContainsSequence(materialTokens, partTokens)) score += 100;

            if (partRole != Role.Unknown && candidateRole == Role.Unknown && score == 0) return 0;
            return score;
        }

        private enum Role
        {
            Unknown,
            Head,
            Hair,
            Arm,
            UpperBody,
            LowerBody,
            Legwear,
            Footwear,
            Body,
            Accessory,
            Outline
        }

        private static Role Classify(List<string> tokens)
        {
            if (HasAny(tokens, "pantyhose", "tights", "stocking", "hosiery", "legging", "sock")) return Role.Legwear;
            if (HasAny(tokens, "shoe", "shoes", "boot", "boots", "sandal", "footwear")) return Role.Footwear;
            if (HasAny(tokens, "skirt", "pants", "pant", "trouser", "shorts", "lower", "bottom")) return Role.LowerBody;
            if (HasAny(tokens, "sweater", "shirt", "blouse", "jacket", "coat", "top", "upper")) return Role.UpperBody;
            if (HasAny(tokens, "accessory", "accessories", "attribute", "pin", "ornament")) return Role.Accessory;
            if (HasAny(tokens, "outline", "outlines")) return Role.Outline;
            if (HasAny(tokens, "hair", "hairs", "bang", "bangs", "fringe", "wig")) return Role.Hair;
            if (HasAny(tokens, "head", "face", "skull")) return Role.Head;
            if (HasAny(tokens, "arm", "arms", "hand", "hands", "glove", "gloves")) return Role.Arm;
            if (HasAny(tokens, "body", "torso", "skin")) return Role.Body;
            return Role.Unknown;
        }

        private static List<string> Tokens(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(value)) return result;
            var token = new System.Text.StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (!char.IsLetterOrDigit(current))
                {
                    AddToken(result, token);
                    continue;
                }
                if (char.IsUpper(current) && token.Length > 0 && char.IsLower(value[i - 1])) AddToken(result, token);
                token.Append(char.ToLowerInvariant(current));
            }
            AddToken(result, token);
            return result;
        }

        private static void AddToken(List<string> result, System.Text.StringBuilder token)
        {
            if (token.Length == 0) return;
            string value = token.ToString();
            token.Clear();
            if (value == "slot" || value == "renderer" || value == "mesh" || value == "material" ||
                value == "aligned" || value == "mita" || value == "part") return;
            if (value.Length > 3 && value.EndsWith("s", StringComparison.Ordinal) &&
                value != "tights" && value != "shorts" && value != "accessories")
                value = value.Substring(0, value.Length - 1);
            result.Add(value);
        }

        private static bool ContainsSequence(List<string> haystack, List<string> needle)
        {
            if (needle.Count == 0 || haystack.Count < needle.Count) return false;
            for (int start = 0; start <= haystack.Count - needle.Count; start++)
            {
                bool match = true;
                for (int i = 0; i < needle.Count; i++)
                    if (!string.Equals(haystack[start + i], needle[i], StringComparison.Ordinal)) { match = false; break; }
                if (match) return true;
            }
            return false;
        }

        private static bool HasAny(List<string> tokens, params string[] values)
        {
            foreach (var token in tokens)
                foreach (var value in values)
                    if (string.Equals(token, value, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
