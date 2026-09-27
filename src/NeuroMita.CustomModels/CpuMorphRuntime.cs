using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    internal static class CpuMorphRuntime
    {
        private sealed class Frame
        {
            public float Weight;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector3[] Tangents;
        }

        private sealed class Channel
        {
            public string Name;
            public readonly List<Frame> Frames = new List<Frame>();
        }

        private sealed class State
        {
            public SkinnedMeshRenderer Renderer;
            public Mesh Mesh;
            public Vector3[] BaseVertices;
            public Vector3[] BaseNormals;
            public Vector4[] BaseTangents;
            public Il2CppStructArray<Vector3> VertexBuffer;
            public Il2CppStructArray<Vector3> NormalBuffer;
            public Il2CppStructArray<Vector4> TangentBuffer;
            public readonly List<Channel> Channels = new List<Channel>();
            public readonly Dictionary<string, int> Indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly float[] SpeechWeights = new float[2];
            public readonly int[] SpeechChannelIndices = { -1, -1 };
            public readonly Dictionary<int, float> ManualWeights = new Dictionary<int, float>();
            public readonly Dictionary<int, float> ExpressionWeights = new Dictionary<int, float>();
            public readonly HashSet<int> ExpressionChannels = new HashSet<int>();
            public bool Dirty;
            public bool LoggedMotion;
        }

        private static readonly Dictionary<int, State> ByRenderer = new Dictionary<int, State>();
        private static readonly Dictionary<int, State> ByMesh = new Dictionary<int, State>();

        public static bool Register(SkinnedMeshRenderer renderer, Mesh mesh,
            IList<ModelBlendShape> blendShapes, Matrix4x4 fix)
        {
            if (renderer == null || mesh == null || blendShapes == null || blendShapes.Count == 0)
                return false;

            try
            {
                var state = new State
                {
                    Renderer = renderer,
                    Mesh = mesh,
                    BaseVertices = Copy(mesh.vertices),
                    BaseNormals = Copy(mesh.normals),
                    BaseTangents = Copy(mesh.tangents)
                };
                if (state.BaseVertices.Length != mesh.vertexCount)
                    throw new InvalidOperationException("mesh vertex buffer length is inconsistent");

                foreach (var source in blendShapes)
                {
                    if (source == null || string.IsNullOrWhiteSpace(source.Name) || source.Frames == null)
                        continue;

                    var channel = new Channel { Name = source.Name.Trim() };
                    foreach (var sourceFrame in source.Frames)
                    {
                        if (sourceFrame == null || !IsFinite(sourceFrame.Weight) ||
                            sourceFrame.DeltaVertices == null || sourceFrame.DeltaVertices.Length != mesh.vertexCount)
                            continue;

                        channel.Frames.Add(new Frame
                        {
                            Weight = sourceFrame.Weight,
                            Vertices = Transform(sourceFrame.DeltaVertices, fix),
                            Normals = TransformOptional(sourceFrame.DeltaNormals, mesh.vertexCount, fix),
                            Tangents = TransformOptional(sourceFrame.DeltaTangents, mesh.vertexCount, fix)
                        });
                    }

                    channel.Frames.Sort((a, b) => a.Weight.CompareTo(b.Weight));
                    if (channel.Frames.Count == 0 || state.Indices.ContainsKey(channel.Name)) continue;
                    state.Indices.Add(channel.Name, state.Channels.Count);
                    state.Channels.Add(channel);
                }

                if (state.Channels.Count == 0) return false;
                var speechMapping = LipSyncBlendShapeResolver.Resolve(
                    state.Channels.Select(channel => channel.Name).ToList());
                state.SpeechChannelIndices[0] = speechMapping.OIndex;
                state.SpeechChannelIndices[1] = speechMapping.AIndex;
                state.VertexBuffer = new Il2CppStructArray<Vector3>(state.BaseVertices);
                if (state.BaseNormals.Length == mesh.vertexCount)
                    state.NormalBuffer = new Il2CppStructArray<Vector3>(state.BaseNormals);
                if (state.BaseTangents.Length == mesh.vertexCount)
                    state.TangentBuffer = new Il2CppStructArray<Vector4>(state.BaseTangents);
                mesh.MarkDynamic();

                int rendererId = renderer.GetInstanceID();
                int meshId = mesh.GetInstanceID();
                if (ByRenderer.TryGetValue(rendererId, out var replaced) && replaced.Mesh != null)
                    ByMesh.Remove(replaced.Mesh.GetInstanceID());
                ByRenderer[rendererId] = state;
                ByMesh[meshId] = state;
                Logging.Info($"[Morph] CPU fallback registered {state.Channels.Count} channel(s) on '{renderer.name}' " +
                             $"({mesh.vertexCount} vertices)");
                return true;
            }
            catch (Exception e)
            {
                Logging.Warn($"[Morph] CPU fallback registration failed on '{renderer.name}': {e.Message}");
                return false;
            }
        }

        public static bool Has(SkinnedMeshRenderer renderer) =>
            renderer != null && ByRenderer.TryGetValue(renderer.GetInstanceID(), out var state) && state.Mesh == renderer.sharedMesh;

        public static bool Has(Mesh mesh) =>
            mesh != null && ByMesh.TryGetValue(mesh.GetInstanceID(), out var state) && state.Mesh == mesh;

        public static List<string> GetNames(SkinnedMeshRenderer renderer)
        {
            return TryGet(renderer, out var state)
                ? state.Channels.Select(channel => channel.Name).ToList()
                : null;
        }

        public static int ResolveIndex(Mesh mesh, string name)
        {
            name = CanonicalFaceShapeName(name);
            return TryGet(mesh, out var state) && TryResolveName(state, name, out int index) ? index : -1;
        }

        public static bool SetWeight(SkinnedMeshRenderer renderer, int index, float weight)
        {
            if (!TryGet(renderer, out var state) || index < 0 || index >= state.Channels.Count) return false;
            float clamped = Mathf.Clamp(weight, 0f, 100f);
            if (state.ManualWeights.TryGetValue(index, out float current) && Mathf.Abs(current - clamped) < 0.01f)
                return true;
            state.ManualWeights[index] = clamped;
            state.Dirty = true;
            return true;
        }

        public static bool TryGetWeight(SkinnedMeshRenderer renderer, int index, out float weight)
        {
            weight = 0f;
            if (!TryGet(renderer, out var state) || index < 0 || index >= state.Channels.Count) return false;
            weight = EffectiveWeight(state, index);
            return true;
        }

        public static bool SetSpeechWeights(SkinnedMeshRenderer renderer, float o, float a, float maxWeight)
        {
            if (!TryGet(renderer, out var state)) return false;
            float nextO = state.SpeechChannelIndices[0] >= 0 ? Mathf.Clamp(o * maxWeight, 0f, 100f) : 0f;
            float nextA = state.SpeechChannelIndices[1] >= 0 ? Mathf.Clamp(a * maxWeight, 0f, 100f) : 0f;
            if (Mathf.Abs(state.SpeechWeights[0] - nextO) < 0.01f && Mathf.Abs(state.SpeechWeights[1] - nextA) < 0.01f)
                return true;
            state.SpeechWeights[0] = nextO;
            state.SpeechWeights[1] = nextA;
            state.Dirty = true;
            return true;
        }

        public static bool SetExpression(SkinnedMeshRenderer renderer, string expression)
        {
            if (!TryGet(renderer, out var state)) return false;
            foreach (int oldIndex in state.ExpressionChannels) state.ExpressionWeights[oldIndex] = 0f;
            state.ExpressionChannels.Clear();
            if (!string.IsNullOrWhiteSpace(expression))
            {
                foreach (string alias in FaceExpressionResolver.Aliases(expression))
                {
                    if (!TryResolveName(state, alias, out int index) || !state.ExpressionChannels.Add(index)) continue;
                    state.ExpressionWeights[index] = 100f;
                }
                if (state.ExpressionChannels.Count > 0)
                    Logging.Verbose($"[Morph] face expression '{expression}' -> " +
                                    string.Join(", ", state.ExpressionChannels.Select(index => state.Channels[index].Name)));
                else Logging.Verbose($"[Morph] no face channel matched expression '{expression}'");
            }
            state.Dirty = true;
            return true;
        }

        public static void ApplyPending()
        {
            var dead = new List<int>();
            foreach (var pair in ByRenderer)
            {
                var state = pair.Value;
                if (state.Renderer == null || state.Mesh == null || state.Renderer.sharedMesh != state.Mesh)
                {
                    dead.Add(pair.Key);
                    continue;
                }
                if (!state.Dirty) continue;
                Apply(state);
                state.Dirty = false;
            }
            foreach (int id in dead)
            {
                if (ByRenderer.TryGetValue(id, out var state) && state.Mesh != null)
                    ByMesh.Remove(state.Mesh.GetInstanceID());
                ByRenderer.Remove(id);
            }
        }

        private static void Apply(State state)
        {
            for (int i = 0; i < state.BaseVertices.Length; i++) state.VertexBuffer[i] = state.BaseVertices[i];
            if (state.NormalBuffer != null)
                for (int i = 0; i < state.BaseNormals.Length; i++) state.NormalBuffer[i] = state.BaseNormals[i];
            if (state.TangentBuffer != null)
                for (int i = 0; i < state.BaseTangents.Length; i++) state.TangentBuffer[i] = state.BaseTangents[i];

            for (int i = 0; i < state.Channels.Count; i++)
            {
                float weight = EffectiveWeight(state, i);
                if (weight <= 0.001f) continue;
                Blend(state.Channels[i], weight, state.VertexBuffer, state.NormalBuffer, state.TangentBuffer);
            }

            state.Mesh.vertices = state.VertexBuffer;
            if (state.NormalBuffer != null)
            {
                for (int i = 0; i < state.NormalBuffer.Length; i++) state.NormalBuffer[i] = state.NormalBuffer[i].normalized;
                state.Mesh.normals = state.NormalBuffer;
            }
            if (state.TangentBuffer != null) state.Mesh.tangents = state.TangentBuffer;
            state.Mesh.RecalculateBounds();
            if (!state.LoggedMotion)
            {
                for (int i = 0; i < state.BaseVertices.Length; i++)
                {
                    if ((state.VertexBuffer[i] - state.BaseVertices[i]).sqrMagnitude <= 0.00000001f) continue;
                    state.LoggedMotion = true;
                    Logging.Info($"[Morph] applied animated vertices on '{state.Renderer.name}' (first affected vertex {i})");
                    break;
                }
            }
        }

        private static void Blend(Channel channel, float weight, Il2CppStructArray<Vector3> vertices,
            Il2CppStructArray<Vector3> normals, Il2CppStructArray<Vector4> tangents)
        {
            float frameWeight = Mathf.Clamp(weight, 0f, channel.Frames[channel.Frames.Count - 1].Weight);
            Frame lower = null, upper = null;
            foreach (var frame in channel.Frames)
            {
                if (frame.Weight <= frameWeight) lower = frame;
                if (frame.Weight >= frameWeight) { upper = frame; break; }
            }

            float lowerFactor, upperFactor;
            if (lower == null)
            {
                upperFactor = upper.Weight > 0f ? frameWeight / upper.Weight : 0f;
                lowerFactor = 0f;
            }
            else if (upper == null || ReferenceEquals(lower, upper))
            {
                lowerFactor = 1f;
                upperFactor = 0f;
            }
            else
            {
                upperFactor = (frameWeight - lower.Weight) / Mathf.Max(0.0001f, upper.Weight - lower.Weight);
                lowerFactor = 1f - upperFactor;
            }

            AddFrame(lower, lowerFactor, vertices, normals, tangents);
            AddFrame(upper, upperFactor, vertices, normals, tangents);
        }

        private static void AddFrame(Frame frame, float factor, Il2CppStructArray<Vector3> vertices,
            Il2CppStructArray<Vector3> normals, Il2CppStructArray<Vector4> tangents)
        {
            if (frame == null || factor <= 0f) return;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] += frame.Vertices[i] * factor;
                if (normals != null && frame.Normals != null) normals[i] += frame.Normals[i] * factor;
                if (tangents != null && frame.Tangents != null)
                {
                    var tangent = tangents[i];
                    tangent.x += frame.Tangents[i].x * factor;
                    tangent.y += frame.Tangents[i].y * factor;
                    tangent.z += frame.Tangents[i].z * factor;
                    tangents[i] = tangent;
                }
            }
        }

        private static float EffectiveWeight(State state, int index)
        {
            float weight = 0f;
            if (state.ManualWeights.TryGetValue(index, out float manual)) weight += manual;
            if (state.ExpressionWeights.TryGetValue(index, out float expression)) weight += expression;
            if (state.SpeechChannelIndices[0] == index) weight += state.SpeechWeights[0];
            if (state.SpeechChannelIndices[1] == index) weight += state.SpeechWeights[1];
            return Mathf.Clamp(weight, 0f, 100f);
        }

        private static bool TryResolveName(State state, string name, out int index)
        {
            string normalized = Normalize(name);
            if (normalized.Length == 0)
            {
                index = -1;
                return false;
            }
            if (state.Indices.TryGetValue(name ?? string.Empty, out index)) return true;
            foreach (var pair in state.Indices)
            {
                string candidate = Normalize(pair.Key);
                if (candidate == normalized || candidate.EndsWith(normalized, StringComparison.Ordinal) ||
                    normalized.EndsWith(candidate, StringComparison.Ordinal) ||
                    (normalized.Length >= 4 && candidate.StartsWith(normalized, StringComparison.Ordinal)))
                {
                    index = pair.Value;
                    return true;
                }
            }
            index = -1;
            return false;
        }

        private static bool TryGet(SkinnedMeshRenderer renderer, out State state)
        {
            state = null;
            return renderer != null && ByRenderer.TryGetValue(renderer.GetInstanceID(), out state) && state.Mesh == renderer.sharedMesh;
        }

        private static bool TryGet(Mesh mesh, out State state)
        {
            state = null;
            return mesh != null && ByMesh.TryGetValue(mesh.GetInstanceID(), out state) && state.Mesh == mesh;
        }

        private static Vector3[] Copy(Il2CppStructArray<Vector3> source)
        {
            if (source == null) return Array.Empty<Vector3>();
            var copy = new Vector3[source.Length];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return copy;
        }

        private static Vector4[] Copy(Il2CppStructArray<Vector4> source)
        {
            if (source == null) return Array.Empty<Vector4>();
            var copy = new Vector4[source.Length];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return copy;
        }

        private static Vector3[] Transform(Vector3[] source, Matrix4x4 fix)
        {
            var result = new Vector3[source.Length];
            for (int i = 0; i < source.Length; i++) result[i] = fix.MultiplyVector(source[i]);
            return result;
        }

        private static Vector3[] TransformOptional(Vector3[] source, int vertexCount, Matrix4x4 fix)
        {
            return source != null && source.Length == vertexCount ? Transform(source, fix) : null;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
            return new string(chars);
        }

        private static string CanonicalFaceShapeName(string name)
        {
            switch (Normalize(name))
            {
                case "smilecolon3": return "Smile :3";
                case "smilecolongreater": return "Smile :>";
                case "asmile": return "A Smile";
                case "akwardsmile": return "AkwardSmile";
                default: return name;
            }
        }

        private static class FaceExpressionResolver
        {
            public static bool TryResolve(State state, string expression, out int index)
            {
                string[] aliases = Aliases(expression);
                foreach (string alias in aliases)
                    if (TryResolveName(state, alias, out index)) return true;

                string[] terms = aliases.Select(Normalize).Where(term => term.Length > 2).ToArray();
                for (int i = 0; i < state.Channels.Count; i++)
                {
                    string candidate = Normalize(state.Channels[i].Name);
                    if (terms.Any(term => candidate.Contains(term, StringComparison.Ordinal)))
                    {
                        index = i;
                        return true;
                    }
                }
                index = -1;
                return false;
            }

            public static string[] Aliases(string expression)
            {
                string key = Normalize(expression);
                switch (key)
                {
                    case "none":
                    case "off":
                    case "emptiness": return Array.Empty<string>();
                    case "smile": return new[] { "Smile", "SmileMiddle", "SmileSmall", "Happy", "MouthSmile", "FclMTHSmile" };
                    case "quest": return new[] { "Quest", "EyeBrowsUp", "BrowInnerUp", "EyesWonder" };
                    case "smileteeth": return new[] { "SmileTeeth", "SmileBig", "TeethSmile", "Grin", "MouthSmileTeeth" };
                    case "sad": return new[] { "Sad", "EyeBrowsDown", "BrowDown", "Frown", "MouthSad", "FclMTHSad" };
                    case "angry": return new[] { "Angry", "EyesAngry", "EyeBrowsDown", "BrowDown", "MouthAngry" };
                    case "smilestrange": return new[] { "SmileStrange", "MouthFrown", "Frown" };
                    case "shy": return new[] { "Shy", "Blush", "EyeBrowsUp", "SmileSmall" };
                    case "smileobvi": return new[] { "SmileObvi", "SmileMiddle", "SmileSmall" };
                    case "smiletongue": return new[] { "SmileTongue", "TongueSmile", "TongueA", "MouthTongue" };
                    case "smilecringe": return new[] { "SmileCringe", "SmileStrange", "MouthFrown" };
                    case "broken": return new[] { "Broken", "EyesSquints", "MouthFrown" };
                    case "smug": return new[] { "Smug", "SmileColonGreater", "Smile :>", "SmileSmug", "EyesSmug" };
                    case "tsundere": return new[] { "Tsundere", "EyesAngry", "EyeBrowsDown", "Blush", "SmileSmall" };
                    case "yandere": return new[] { "Yandere", "YandereSquints", "EyesAngry", "SmileManiac" };
                    case "sleep": return new[] { "Sleep", "EyesClosed", "CozyBlink", "Blink" };
                    case "halfsleep": return new[] { "HalfSleep", "HalfClosedEyes", "EyesHalfClosed", "EyesSquints" };
                    case "suspicion": return new[] { "Suspicion", "EyesSquints", "EyeBrowLeftUp" };
                    case "trytoque": return new[] { "Trytoque", "EyeBrowLeftUp", "EyeBrowsUp" };
                    case "discontent": return new[] { "Discontent", "AnnoyMiddle", "AnnoySmall", "EyesAngry" };
                    case "ajar": return new[] { "Ajar", "O", "MouthO" };
                    case "catchquest": return new[] { "CatchQuest", "EyesWonder", "SmileSmall" };
                    case "arrogance": return new[] { "Arrogance", "SmileColonGreater", "Smile :>", "EyeBrowLeftUp" };
                    case "surprise": return new[] { "Surprise", "EyesWonder", "MouthOpen", "MouthSurprise", "O" };
                    case "surpriseo": return new[] { "SurpriseO", "Surprise", "O", "MouthO", "EyesWonder" };
                    default: return new[] { expression };
                }
            }
        }
    }
}
