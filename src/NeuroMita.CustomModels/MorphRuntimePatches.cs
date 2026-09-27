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

        // 反射结果按类型缓存。
        // 原来每次调用都跑一遍 AccessTools.Property/Field —— 那会遍历整个类型层级，
        // 而这个函数在脸部权重写入的热路径上（SetFaceWeightsPrefix 每次都调）。
        private static readonly System.Collections.Generic.Dictionary<Type, PropertyInfo> PointerPropertyCache =
            new System.Collections.Generic.Dictionary<Type, PropertyInfo>();
        private static readonly System.Collections.Generic.Dictionary<Type, FieldInfo> PointerFieldCache =
            new System.Collections.Generic.Dictionary<Type, FieldInfo>();
        private static readonly PropertyInfo VoiceWeightsProperty = typeof(Audio_BlendShapeVoice).GetProperty(
            "currentWeights", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo VoiceWeightsField = typeof(Audio_BlendShapeVoice).GetField(
            "currentWeights", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static bool _weightsWarningLogged;
        private static bool _badWeightArgumentLogged;
        private static bool _badShapeListLogged;

        public static void Install(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(Audio_BlendShapeVoice), "LateUpdate"),
                nameof(VoiceLateUpdatePrefix), PatchKind.Prefix, "Audio_BlendShapeVoice.LateUpdate");
            Patch(harmony, AccessTools.Method(typeof(SkinnedMeshRenderer), "SetBlendShapeWeight", new[] { typeof(int), typeof(float) }),
                nameof(SetWeightPrefix), PatchKind.Prefix, "SkinnedMeshRenderer.SetBlendShapeWeight");
            Patch(harmony, AccessTools.Method(typeof(SkinnedMeshRenderer), "GetBlendShapeWeight", new[] { typeof(int) }),
                nameof(GetWeightPrefix), PatchKind.Prefix, "SkinnedMeshRenderer.GetBlendShapeWeight");

            Patch(harmony, AccessTools.Constructor(typeof(MitaFaceController), new[] { typeof(MitaActor) }),
                nameof(FaceControllerConstructorPostfix), PatchKind.Postfix, "MitaFaceController..ctor");
            var emotionMethod = typeof(MitaFaceController).GetMethod("SetEmotion",
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(MitaFaceExpression) }, null);
            Patch(harmony, emotionMethod, nameof(SetEmotionPostfix), PatchKind.Postfix, "MitaFaceController.SetEmotion");
            var blendDomain = typeof(MitaFaceController).GetNestedType("BlendShapesDomain", BindingFlags.Public);
            if (blendDomain != null)
            {
                var weightMethod = blendDomain.GetMethod("SetWeight", BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(float), typeof(MitaFaceBlendShape[]) }, null);
                Patch(harmony, weightMethod, nameof(SetFaceWeightsPrefix), PatchKind.Prefix, "MitaFaceController.BlendShapesDomain.SetWeight");
                var clearMethod = blendDomain.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                Patch(harmony, clearMethod, nameof(ClearDomainPostfix), PatchKind.Postfix, "MitaFaceController.BlendShapesDomain.Clear");
            }
            var clearEmotion = typeof(MitaFaceController).GetMethod("SetEmotionOff",
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            Patch(harmony, clearEmotion, nameof(ClearEmotionPostfix), PatchKind.Postfix, "MitaFaceController.SetEmotionOff");
        }

        private enum PatchKind { Prefix, Postfix }

        private static void Patch(Harmony harmony, MethodBase target, string patchName, PatchKind kind, string label)
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
                // 显式指定 prefix/postfix。原来靠方法名后缀推断 —— 谁一改名就会静默变成另一种语义。
                if (kind == PatchKind.Postfix) harmony.Patch(target, postfix: harmonyMethod);
                else harmony.Patch(target, prefix: harmonyMethod);

                // Harmony.Patch 不会因为 IL2CPP patch backend 起不来而抛异常 —— 它只在自己
                // 内部记一条日志（"Failed to init IL2CPP patch backend ..."）。所以这里必须
                // 回查一次真实状态，否则"installed"是假的、排查时会被带偏。
                bool registered = false;
                try
                {
                    var info = Harmony.GetPatchInfo(target);
                    if (info != null)
                    {
                        if (info.Prefixes != null)
                            foreach (var p in info.Prefixes) if (p.PatchMethod == patch) { registered = true; break; }
                        if (!registered && info.Postfixes != null)
                            foreach (var p in info.Postfixes) if (p.PatchMethod == patch) { registered = true; break; }
                    }
                }
                catch { }

                if (registered) Logging.Verbose($"[Morph] installed patch: {label}");
                else Logging.Warn($"[Morph] patch NOT registered (IL2CPP backend refused it?): {label}");
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
                !TryGetActor(FaceBlendDomainActors, GetNativePointer(__instance), out var actorTransform)) return true;
            var voice = actorTransform.GetComponentInChildren<Audio_BlendShapeVoice>(true);
            if (voice == null || !CpuMorphRuntime.Has(voice.targetMesh)) return true;

            // 参数不是预期形状时放行原方法，而不是跳过它。
            // 跳过会让游戏自己的脸部写入消失 —— 出问题时表现为"脸不动了"，比什么都不做更糟。
            // 这两条只记一次：SetFaceWeightsPrefix 每帧都会被调到，不能刷屏。
            if (!(__args[0] is float weight))
            {
                if (!_badWeightArgumentLogged)
                {
                    _badWeightArgumentLogged = true;
                    Logging.Warn("[Morph] BlendShapesDomain.SetWeight: unexpected weight argument, passing through");
                }
                return true;
            }
            if (!TryReadShapes(__args[1], out var shapes))
            {
                if (!_badShapeListLogged)
                {
                    _badShapeListLogged = true;
                    Logging.Warn("[Morph] BlendShapesDomain.SetWeight: could not read shape list, passing through");
                }
                return true;
            }

            foreach (string shape in shapes)
                CpuMorphRuntime.SetWeight(voice.targetMesh, CpuMorphRuntime.ResolveIndex(voice.targetMesh.sharedMesh, shape), weight);
            return false;
        }

        private static void SetEmotionPostfix(object __instance, object[] __args)
        {
            if (__args == null || __args.Length == 0 || __instance == null ||
                !TryGetActor(FaceControllerActors, GetNativePointer(__instance), out var actorTransform)) return;
            try
            {
                var voice = actorTransform.GetComponentInChildren<Audio_BlendShapeVoice>(true);
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
            if (__instance == null || !TryGetActor(FaceBlendDomainActors, GetNativePointer(__instance), out var actorTransform)) return;
            var voice = actorTransform.GetComponentInChildren<Audio_BlendShapeVoice>(true);
            if (voice == null) return;
            var names = CpuMorphRuntime.GetNames(voice.targetMesh);
            if (names == null) return;
            for (int i = 0; i < names.Count; i++) CpuMorphRuntime.SetWeight(voice.targetMesh, i, 0f);
        }

        private static void ClearEmotionPostfix(object __instance)
        {
            if (__instance == null || !TryGetActor(FaceControllerActors, GetNativePointer(__instance), out var actorTransform)) return;
            var voice = actorTransform.GetComponentInChildren<Audio_BlendShapeVoice>(true);
            if (voice != null) CpuMorphRuntime.SetExpression(voice.targetMesh, null);
        }

        /// <summary>
        /// 读一个"带 Length/Item 的类数组" —— 反射结果同样按类型缓存。
        /// 这个也可能在热路径上（SetWeight 每次都读一次 shape 列表）。
        /// </summary>
        private static void GetArrayAccessors(Type type, out PropertyInfo lengthProperty, out PropertyInfo itemProperty)
        {
            if (!ArrayAccessorCache.TryGetValue(type, out var pair))
            {
                pair = (AccessTools.Property(type, "Length"), AccessTools.Property(type, "Item"));
                ArrayAccessorCache[type] = pair;
            }
            lengthProperty = pair.Length;
            itemProperty = pair.Item;
        }

        private static readonly System.Collections.Generic.Dictionary<Type, (PropertyInfo Length, PropertyInfo Item)>
            ArrayAccessorCache = new System.Collections.Generic.Dictionary<Type, (PropertyInfo Length, PropertyInfo Item)>();

        private static bool TryReadPair(object source, out float first, out float second)
        {
            first = second = 0f;
            if (source == null) return false;
            if (source is float[] managed)
            {
                if (managed.Length < 2) return false;
                first = managed[0]; second = managed[1]; return true;
            }

            GetArrayAccessors(source.GetType(), out var lengthProperty, out var itemProperty);
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
                var type = instance.GetType();

                if (!PointerPropertyCache.TryGetValue(type, out var property))
                {
                    property = AccessTools.Property(type, "Pointer");
                    PointerPropertyCache[type] = property;   // null 也缓存，避免每次都重查一遍
                }
                if (property != null) return (IntPtr)property.GetValue(instance);

                if (!PointerFieldCache.TryGetValue(type, out var field))
                {
                    field = AccessTools.Field(type, "Pointer");
                    PointerFieldCache[type] = field;
                }
                if (field != null) return (IntPtr)field.GetValue(instance);
            }
            catch { }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 从"原生指针 → 角色"的映射里取值。
        ///
        /// 这些字典是按原生指针索引的，而指针会被复用；而且它们只加不删，场景切多了会一直涨。
        /// 所以取的时候顺手校验：对象已经被销毁就把这条删掉。
        /// （新构造的 controller 会在 FaceControllerConstructorPostfix 里覆盖同指针的旧条目，
        ///   所以这里只需要处理"死掉但没被覆盖"的那种。）
        /// </summary>
        private static bool TryGetActor(System.Collections.Generic.Dictionary<IntPtr, Transform> map,
                                        IntPtr key, out Transform actor)
        {
            actor = null;
            if (key == IntPtr.Zero) return false;
            if (!map.TryGetValue(key, out actor)) return false;
            if (actor == null) { map.Remove(key); return false; }
            return true;
        }
    }
}
