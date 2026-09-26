using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using MitaAI;
using MitaAI.Dialogue.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NeuroMita.AshleyAutoTest
{
    [BepInPlugin("com.neuromita.ashleyautotest", "Ashley Crazy House Face Test", "0.2.0")]
    public sealed class AshleyAutoTestPlugin : BasePlugin
    {
        internal static BepInEx.Logging.ManualLogSource TestLog;

        public override void Load()
        {
            TestLog = Log;
            ClassInjector.RegisterTypeInIl2Cpp<TestRuntime>();
            var host = new GameObject("Ashley Crazy House Face Test");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<TestRuntime>();
            Log.LogInfo("[AshleyFaceTest] loaded; opening CrazyHouse and testing face emotion and layer commands");
        }
    }

    public sealed class TestRuntime : MonoBehaviour
    {
        private int _ticks;
        private float _nextAction;
        private int _step = -1;
        private bool _sceneRequested;
        private int _lastActorScan;
        private MitaActor _actor;
        private string _pendingLayerReport;
        private float _layerReportAt;

        public TestRuntime(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            _ticks++;
            if (_ticks % 300 == 0)
                Log($"tick={_ticks} scene='{SceneManager.GetActiveScene().name}' stage={_step}");
            if (_pendingLayerReport != null && Time.unscaledTime >= _layerReportAt)
            {
                ReportLayer(_pendingLayerReport);
                _pendingLayerReport = null;
            }
            if (!_sceneRequested && _ticks >= 120)
            {
                _sceneRequested = true;
                if (SceneManager.GetActiveScene().name != "CrazyHouse")
                {
                    Log($"requesting CrazyHouse from '{SceneManager.GetActiveScene().name}' through SceneService");
                    SceneService.LoadSingle(GameSceneId.CrazyHouse);
                }
                else Log("CrazyHouse already active");
                return;
            }

            if (_actor == null)
            {
                if (_ticks % 30 != 0 || SceneManager.GetActiveScene().name != "CrazyHouse") return;
                var actors = UnityEngine.Object.FindObjectsOfType<MitaActor>(true);
                if (actors == null || actors.Length == 0)
                {
                    if (_ticks - _lastActorScan >= 300)
                    {
                        _lastActorScan = _ticks;
                        Log($"waiting for MitaActor in '{SceneManager.GetActiveScene().name}'");
                    }
                    return;
                }
                _actor = actors[0];
                for (int i = 0; i < actors.Length; i++)
                {
                    if (actors[i] == null) continue;
                    Log($"actor[{i}]='{actors[i].name}' resolved={actors[i].IsResolved}");
                    if (actors[i].name.IndexOf("Crazy", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _actor = actors[i];
                        break;
                    }
                }
                if (!_actor.IsResolved) _actor.ResolveComponents();
                _step = 0;
                _nextAction = Time.unscaledTime + 3f;
                Log($"target='{_actor.name}', scene='{SceneManager.GetActiveScene().name}'");
                return;
            }

            if (Time.unscaledTime < _nextAction) return;
            RunStep(_step++);
            _nextAction = Time.unscaledTime + 4f;
            if (_step > 7)
            {
                Log("face sequence complete; tester is unloading itself");
                UnityEngine.Object.Destroy(gameObject);
            }
        }

        private void RunStep(int step)
        {
            switch (step)
            {
                case 0: SendEmotion("smile"); break;
                case 1: SendEmotion("sad"); break;
                case 2: SendEmotion("angry"); break;
                case 3: SendEmotion("surprise"); break;
                case 4: SendLayer(2, "SadMask"); break;
                case 5: SendLayer(1, "Blush"); break;
                case 6: SendLayer(0, "ResetFace"); break;
                default: SendEmotion("off"); break;
            }
        }

        private void SendEmotion(string expression)
        {
            string cleaned = FaceCommandParser.ProcessFaceCommands(
                $"<face:emotion>{expression}</face:emotion>", _actor);
            Log($"emotion='{expression}' command result='{cleaned}'");
        }

        private void SendLayer(int layer, string label)
        {
            string cleaned = FaceCommandParser.ProcessFaceCommands($"<face:layer>{layer}</face:layer>", _actor);
            Log($"layer='{label}' value={layer} command result='{cleaned}'");
            _pendingLayerReport = label;
            _layerReportAt = Time.unscaledTime + 1f;
        }

        private void ReportLayer(string label)
        {
            try
            {
                var faceLayer = _actor.Links != null ? _actor.Links.faceLayer : null;
                if (faceLayer == null)
                {
                    Log($"layer='{label}' renderer missing");
                    return;
                }
                var material = faceLayer.material;
                var texture = material != null ? material.GetTexture("_MainTex") : null;
                float alpha = material != null && material.HasProperty("_AlphaMod") ? material.GetFloat("_AlphaMod") : float.NaN;
                Log($"layer='{label}' rendererEnabled={faceLayer.enabled} active={faceLayer.gameObject.activeInHierarchy} " +
                    $"mesh='{(faceLayer.sharedMesh != null ? faceLayer.sharedMesh.name : "<null>")}' " +
                    $"texture='{(texture != null ? texture.name : "<null>")}' alpha={alpha}");
            }
            catch (Exception e) { Log($"layer='{label}' state read failed: {e.Message}"); }
        }

        private static void Log(string message)
        {
            if (AshleyAutoTestPlugin.TestLog != null)
                AshleyAutoTestPlugin.TestLog.LogInfo("[AshleyFaceTest] " + message);
            else Debug.Log("[AshleyFaceTest] " + message);
        }
    }
}
