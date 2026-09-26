using System;
using System.Reflection;
using HarmonyLib;
using MitaAI;
using MitaAI.Controllers;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    internal static class MorphRuntimePatches
    {
        private static readonly System.Collections.Generic.Dictionary<IntPtr, Transform> FaceControllerActors =
            new System.Collections.Generic.Dictionary<IntPtr, Transform>();
        private static readonly System.Collections.Generic.Dictionary<IntPtr, Transform> FaceBlendDomainActors =
            new System.Collections.Generic.Dictionary<IntPtr, Transform>();
        private static readonly PropertyInfo VoiceWeightsProperty = typeof(Audio_BlendShapeVoice).GetProperty(
            "currentWeights", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo VoiceWeightsField = typeof(Audio_BlendShapeVoice).GetField(
            "currentWeights", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static bool _weightsWarningLogged;

        public static void Install(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(Audio_BlendShapeVoice), "LateUpdate"),
                nameof(VoiceLateUpdatePrefix), "Audio_BlendShapeVoice.LateUpdate");
            Patch(harmony, AccessTools.Method(typeof(SkinnedMeshRenderer), "SetBlendShapeWeight", new[] { typeof(int), typeof(float) }),
                nameof(SetWeightPrefix), "SkinnedMeshRenderer.SetBlendShapeWeight");
            Patch(harmony, AccessTools.Method(typeof(SkinnedMeshRenderer), "GetBlendShapeWeight", new[] { typeof(int) }),
                nameof(GetWeightPrefix), "SkinnedMeshRenderer.GetBlendShapeWeight");

            Patch(harmony, AccessTools.Constructor(typeof(MitaFaceController), new[] { typeof(MitaActor) }),
                nameof(FaceControllerConstructorPostfix), "MitaFaceController..ctor");
            var emotionMethod = typeof(MitaFaceController).GetMethod("SetEmotion",
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(MitaFaceExpression) }, null);
            Patch(harmony, emotionMethod, nameof(SetEmotionPostfix), "MitaFaceController.SetEmotion");
            var blendDomain = typeof(MitaFaceController).GetNestedType("BlendShapesDomain", BindingFlags.Public);
            if (blendDomain != null)
            {
                var weightMethod = blendDomain.GetMethod("SetWeight", BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(float), typeof(MitaFaceBlendShape[]) }, null);
                Patch(harmony, weightMethod, nameof(SetFaceWeightsPrefix), "MitaFaceController.BlendShapesDomain.SetWeight");
                var clearMethod = blendDomain.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                Patch(harmony, clearMethod, nameof(ClearDomainPostfix), "MitaFaceController.BlendShapesDomain.Clear");
            }
            var clearEmotion = typeof(MitaFaceController).GetMethod("SetEmotionOff",
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            Patch(harmony, clearEmotion, nameof(ClearEmotionPostfix), "MitaFaceController.SetEmotionOff");
        }

        private static void Patch(Harmony harmony, MethodBase target, string patchName, string label)
        {
            if (target == null)
            {
                Logging.Warn($"[Morph] patch target unavailable: {label}");
                return;
            }
            try
            {
                var patch = AccessTools.Method(typeof(MorphRuntimePatches), patchName);
                var harmonyMethod = new HarmonyMethod(patch);
                if (patchName.EndsWith("Postfix", StringComparison.Ordinal)) harmony.Patch(target, postfix: harmonyMethod);
                else harmony.Patch(target, prefix: harmonyMethod);
                Logging.Verbose($"[Morph] installed patch: {label}");
            }
            catch (Exception e) { Logging.Warn($"[Morph] patch failed for {label}: {e.Message}"); }
        }

        private static bool VoiceLateUpdatePrefix(Audio_BlendShapeVoice __instance)
        {
            var renderer = __instance != null ? __instance.targetMesh : null;
            if (!CpuMorphRuntime.Has(renderer)) return true;
            if (VoiceWeightsProperty == null && VoiceWeightsField == null)
            {
                if (!_weightsWarningLogged)
                {
                    _weightsWarningLogged = true;
                    Logging.Warn("[Morph] Audio_BlendShapeVoice.currentWeights binding was not found");
                }
                return false;
            }

            try
            {
                object rawWeights = VoiceWeightsProperty != null
                    ? VoiceWeightsProperty.GetValue(__instance)
                    : VoiceWeightsField.GetValue(__instance);
                if (!TryReadPair(rawWeights, out float o, out float a)) return false;
                CpuMorphRuntime.SetSpeechWeights(renderer, o, a, __instance.maxBlendShapeWeight);
            }
            catch (Exception e) { Logging.Warn($"[Morph] speech weight bridge failed: {e.Message}"); }
            return false;
        }

        private static bool SetWeightPrefix(SkinnedMeshRenderer __instance, int index, float value) =>
            !CpuMorphRuntime.SetWeight(__instance, index, value);

        private static bool GetWeightPrefix(SkinnedMeshRenderer __instance, int index, ref float __result)
        {
            if (!CpuMorphRuntime.TryGetWeight(__instance, index, out __result)) return true;
            return false;
        }

        private static bool SetFaceWeightsPrefix(object __instance, object[] __args)
        {
            if (__args == null || __args.Length < 2 || __instance == null ||
                !FaceBlendDomainActors.TryGetValue(GetNativePointer(__instance), out var actorTransform)) return true;
            var voice = actorTransform != null ? actorTransform.GetComponentInChildren<Audio_BlendShapeVoice>(true) : null;
            if (voice == null || !CpuMorphRuntime.Has(voice.targetMesh)) return true;
            if (!(__args[0] is float weight)) return false;
            if (!TryReadShapes(__args[1], out var shapes)) return false;
            foreach (string shape in shapes)
                CpuMorphRuntime.SetWeight(voice.targetMesh, CpuMorphRuntime.ResolveIndex(voice.targetMesh.sharedMesh, shape), weight);
            return false;
        }

        private static void SetEmotionPostfix(object __instance, object[] __args)
        {
            if (__args == null || __args.Length == 0 || __instance == null ||
                !FaceControllerActors.TryGetValue(GetNativePointer(__instance), out var actorTransform)) return;
            try
            {
                var voice = actorTransform != null ? actorTransform.GetComponentInChildren<Audio_BlendShapeVoice>(true) : null;
                if (voice == null) return;
                CpuMorphRuntime.SetExpression(voice.targetMesh, __args[0]?.ToString());
            }
            catch (Exception e) { Logging.Warn($"[Morph] face-expression bridge failed: {e.Message}"); }
        }

        private static void FaceControllerConstructorPostfix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length == 0 || !(__args[0] is MitaActor actor)) return;
            RegisterFaceController(__instance as MitaFaceController, actor.transform);
        }

        public static void RegisterFaceController(MitaFaceController controller, Transform actorTransform)
        {
            if (controller == null || actorTransform == null) return;
            IntPtr pointer = GetNativePointer(controller);
            if (pointer == IntPtr.Zero) return;
            FaceControllerActors[pointer] = actorTransform;
            try
            {
                var domain = typeof(MitaFaceController).GetProperty("BlendShapes", BindingFlags.Instance | BindingFlags.Public)?.GetValue(controller);
                if (domain != null) FaceBlendDomainActors[GetNativePointer(domain)] = actorTransform;
            }
            catch (Exception e) { Logging.Warn($"[Morph] face domain registration failed: {e.Message}"); }
            Logging.Verbose($"[Morph] registered face controller for '{actorTransform.name}'");
        }

        private static void ClearDomainPostfix(object __instance)
        {
            if (__instance == null || !FaceBlendDomainActors.TryGetValue(GetNativePointer(__instance), out var actorTransform)) return;
            var voice = actorTransform != null ? actorTransform.GetComponentInChildren<Audio_BlendShapeVoice>(true) : null;
            if (voice == null) return;
            var names = CpuMorphRuntime.GetNames(voice.targetMesh);
            if (names == null) return;
            for (int i = 0; i < names.Count; i++) CpuMorphRuntime.SetWeight(voice.targetMesh, i, 0f);
        }

        private static void ClearEmotionPostfix(object __instance)
        {
            if (__instance == null || !FaceControllerActors.TryGetValue(GetNativePointer(__instance), out var actorTransform)) return;
            var voice = actorTransform != null ? actorTransform.GetComponentInChildren<Audio_BlendShapeVoice>(true) : null;
            if (voice != null) CpuMorphRuntime.SetExpression(voice.targetMesh, null);
        }

        private static bool TryReadPair(object source, out float first, out float second)
        {
            first = second = 0f;
            if (source == null) return false;
            if (source is float[] managed)
            {
                if (managed.Length < 2) return false;
                first = managed[0]; second = managed[1]; return true;
            }

            var type = source.GetType();
            var lengthProperty = AccessTools.Property(type, "Length");
            var itemProperty = AccessTools.Property(type, "Item");
            if (lengthProperty == null || itemProperty == null || Convert.ToInt32(lengthProperty.GetValue(source)) < 2)
                return false;
            first = Convert.ToSingle(itemProperty.GetValue(source, new object[] { 0 }));
            second = Convert.ToSingle(itemProperty.GetValue(source, new object[] { 1 }));
            return true;
        }

        private static bool TryReadShapes(object source, out System.Collections.Generic.List<string> names)
        {
            names = new System.Collections.Generic.List<string>();
            if (source == null) return false;
            if (source is Array array)
            {
                foreach (var item in array) names.Add(item?.ToString());
                return true;
            }

            var type = source.GetType();
            var lengthProperty = AccessTools.Property(type, "Length");
            var itemProperty = AccessTools.Property(type, "Item");
            if (lengthProperty == null || itemProperty == null) return false;
            int length = Convert.ToInt32(lengthProperty.GetValue(source));
            for (int i = 0; i < length; i++)
                names.Add(itemProperty.GetValue(source, new object[] { i })?.ToString());
            return true;
        }

        private static IntPtr GetNativePointer(object instance)
        {
            if (instance == null) return IntPtr.Zero;
            try
            {
                var property = AccessTools.Property(instance.GetType(), "Pointer");
                if (property != null) return (IntPtr)property.GetValue(instance);
                var field = AccessTools.Field(instance.GetType(), "Pointer");
                if (field != null) return (IntPtr)field.GetValue(instance);
            }
            catch { }
            return IntPtr.Zero;
        }
    }
}
