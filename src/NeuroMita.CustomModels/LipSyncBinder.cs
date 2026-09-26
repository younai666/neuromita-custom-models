using System;
using System.Reflection;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    internal static class LipSyncBinder
    {
        private const string VoiceTypeName = "Audio_BlendShapeVoice";
        private const string VoiceNamespace = "";
        public static void TryBind(SkinnedMeshRenderer installedRenderer, Transform characterRoot)
        {
            if (installedRenderer == null || installedRenderer.sharedMesh == null || characterRoot == null) return;
            var mesh = installedRenderer.sharedMesh;
            var mapping = LipSyncBlendShapeResolver.Resolve(mesh);
            if (!mapping.HasA && !mapping.HasO)
            {
                Logging.Verbose($"[LipSync] {installedRenderer.name}: no mouth-compatible BlendShapes, binding skipped");
                return;
            }

            try
            {
                var voiceType = FindVoiceType();
                if (voiceType == null)
                {
                    Logging.Verbose("[LipSync] Audio_BlendShapeVoice type is unavailable");
                    return;
                }
                var components = characterRoot.GetComponentsInChildren(voiceType, true);
                if (components == null || components.Length == 0)
                {
                    Logging.Verbose("[LipSync] no Audio_BlendShapeVoice found on character");
                    return;
                }
                var voice = components[0];
                if (voice == null) return;

                var type = voice.GetType();
                var targetField = type.GetField("targetMesh", BindingFlags.Instance | BindingFlags.Public);
                var namesField = type.GetField("blendShapeNames", BindingFlags.Instance | BindingFlags.Public);
                var suppressField = type.GetField("suppressMouthOnly", BindingFlags.Instance | BindingFlags.Public);
                var suppressIndicesField = type.GetField("mouthSuppressIndices", BindingFlags.Instance | BindingFlags.Public);
                var rebind = type.GetMethod("RebindBlendshapes", BindingFlags.Instance | BindingFlags.Public);
                if (targetField == null || namesField == null || suppressField == null || suppressIndicesField == null || rebind == null)
                {
                    Logging.Warn("[LipSync] Audio_BlendShapeVoice fields do not match the expected API");
                    return;
                }

                targetField.SetValue(voice, installedRenderer);
                namesField.SetValue(voice, new[] { mapping.OName, mapping.AName });
                suppressField.SetValue(voice, false);
                suppressIndicesField.SetValue(voice, Array.Empty<int>());
                rebind.Invoke(voice, null);
                Logging.Verbose($"[LipSync] Mapping: O -> {Format(mapping.OName, mapping.OIndex)}, " +
                                $"A -> {Format(mapping.AName, mapping.AIndex)}");
                Logging.Info($"[LipSync] Bound Audio_BlendShapeVoice to custom renderer '{installedRenderer.name}'");
            }
            catch (Exception e)
            {
                Logging.Warn($"[LipSync] binding failed for '{installedRenderer.name}': {e.GetType().Name}: {e.Message}");
            }
        }

        private static Type FindVoiceType()
        {
            var direct = Type.GetType(VoiceTypeName + ", Assembly-CSharp", false);
            if (direct != null) return direct;
            var gameAssembly = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in gameAssembly)
            {
                try
                {
                    var type = assembly.GetType(VoiceNamespace.Length == 0 ? VoiceTypeName : VoiceNamespace + "." + VoiceTypeName, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static string Format(string name, int index) => index >= 0 ? $"{name} [{index}]" : "missing";
    }
}
