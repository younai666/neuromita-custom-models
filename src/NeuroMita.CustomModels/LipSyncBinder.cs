using System;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    /// <summary>
    /// Перепривязка липсинка после установки всего пакета.
    ///
    /// Вызывается один раз на персонажа: проходит по всем SkinnedMeshRenderer,
    /// оценивает кандидатов и выбирает одну голову. Раньше биндинг жил внутри
    /// ModelApplier.Apply и срабатывал на каждом part — в многопартовом пакете
    /// побеждал просто последний mesh с A/O, что зависело от порядка частей.
    ///
    /// Обращение к Audio_BlendShapeVoice идёт через типизированную ссылку на
    /// generated interop (Assembly-CSharp): интероп выставляет эти поля как
    /// свойства (targetMesh, blendShapeNames, suppressMouthOnly), а не как CLR
    /// поля, поэтому reflection по имени поля раньше молча не находил ничего.
    /// </summary>
    internal static class LipSyncBinder
    {
        // Audio_BlendShapeVoice ожидает порядок 0 = O, 1 = A.
        private const int ScoreBothMouths = 100;
        private const int ScoreSingleMouth = 10;
        private const int ScoreNameHint = 50;
        private const int PenaltyDisabled = 20;

        public static void BindBest(Transform characterRoot)
        {
            if (characterRoot == null) return;
            try
            {
                var voice = characterRoot.GetComponentInChildren<Audio_BlendShapeVoice>(true);
                if (voice == null)
                {
                    Logging.Verbose("[LipSync] Audio_BlendShapeVoice not found on character; binding skipped");
                    return;
                }

                SkinnedMeshRenderer bestRenderer = null;
                LipSyncBlendShapeMapping bestMapping = null;
                string bestHint = null;
                int bestScore = int.MinValue;

                var renderers = characterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (renderers != null)
                {
                    foreach (var renderer in renderers)
                    {
                        if (renderer == null || renderer.sharedMesh == null) continue;

                        var mapping = LipSyncBlendShapeResolver.Resolve(renderer.sharedMesh);
                        if (!mapping.HasA && !mapping.HasO) continue;

                        int score = (mapping.HasA && mapping.HasO) ? ScoreBothMouths : ScoreSingleMouth;
                        string hint = NameHint(renderer);
                        if (hint != null) score += ScoreNameHint;
                        if (!renderer.enabled) score -= PenaltyDisabled;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestRenderer = renderer;
                            bestMapping = mapping;
                            bestHint = hint;
                        }
                    }
                }

                if (bestRenderer == null)
                {
                    Logging.Verbose($"[LipSync] '{characterRoot.name}': no mouth-compatible BlendShapes, binding skipped");
                    return;
                }

                voice.targetMesh = bestRenderer;
                voice.blendShapeNames = new[] { bestMapping.OName, bestMapping.AName };

                // Индексы mouthSuppressIndices заданы под оригинальную голову и на чужом
                // mesh означают случайные морфы (Blink/Smile/...). Для кастомной модели
                // подавление по индексам отключаем — речь сама двигает только A/O.
                voice.suppressMouthOnly = false;

                voice.RebindBlendshapes();

                Logging.Info($"[LipSync] Bound Audio_BlendShapeVoice to '{bestRenderer.name}' " +
                             $"(score={bestScore}{(bestHint != null ? ", " + bestHint : "")}) " +
                             $"O -> {Format(bestMapping.OName, bestMapping.OIndex)}, " +
                             $"A -> {Format(bestMapping.AName, bestMapping.AIndex)}");
            }
            catch (Exception e)
            {
                Logging.Warn($"[LipSync] binding failed for '{characterRoot.name}': {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>Head/Face в имени даёт только приоритет, но не является обязательным условием.</summary>
        private static string NameHint(SkinnedMeshRenderer renderer)
        {
            string name = renderer != null ? renderer.name : null;
            if (Contains(name, "head")) return "name~Head";
            if (Contains(name, "face")) return "name~Face";

            string meshName = renderer != null && renderer.sharedMesh != null ? renderer.sharedMesh.name : null;
            if (Contains(meshName, "head")) return "mesh~Head";
            if (Contains(meshName, "face")) return "mesh~Face";
            return null;
        }

        private static bool Contains(string value, string token) =>
            !string.IsNullOrEmpty(value) && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Format(string name, int index) => index >= 0 ? $"{name} [{index}]" : "missing";
    }
}
