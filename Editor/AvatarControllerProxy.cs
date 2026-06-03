#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cocokoishi.VRCALoader
{
    public class AvatarControllerProxy : EditorWindow
    {
        private const string ProxyFolder = "Assets/VRCALoader/_ProxyControllers";

        private readonly List<Source> _sources = new List<Source>();
        private UnityEngine.Object _target;
        private Vector2 _scroll;
        private string _status;

        private sealed class Source
        {
            public AnimatorController controller;
            public string owner;
            public string slot;

            public string Label => controller != null ? controller.name : "(null)";

            public int TotalLayers => controller != null ? controller.layers.Length : 0;

            // A layer is "real" only if it has a stateMachine.
            // Bare stub layers (from the facs-style patch) have stateMachine == null.
            public int ContentLayerCount
            {
                get
                {
                    if (controller == null || controller.layers.Length == 0) return 0;
                    int n = 0;
                    foreach (var l in controller.layers)
                        if (l.stateMachine != null) n++;
                    return n;
                }
            }
        }

        public static void Open()
        {
            var w = GetWindow<AvatarControllerProxy>("Controller Inspector");
            w.minSize = new Vector2(500, 400);
            w.Scan();
            w.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Readable Controller Proxy", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Generates a stand-alone copy of the selected controller with states laid out on a " +
                "readable grid. The original bundle objects are never modified.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _target = EditorGUILayout.ObjectField("Avatar / Controller", _target, typeof(UnityEngine.Object), true);
            if (EditorGUI.EndChangeCheck()) AddTarget(_target);
            if (GUILayout.Button("Scan Scene", GUILayout.Width(96))) Scan();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            if (_sources.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No controllers found. Spawn an avatar, then click Scan Scene — " +
                    "or drop a GameObject / AnimatorController into the field above.",
                    MessageType.Info);
            }
            else
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var src in _sources) DrawSource(src);
                EditorGUILayout.EndScrollView();

                EditorGUILayout.Space(2);
                var eligible = _sources.Count(s => s.ContentLayerCount > 0);
                GUI.enabled = eligible > 0;
                if (GUILayout.Button($"Generate All ({eligible} eligible)", GUILayout.Height(24)))
                    GenerateAll();
                GUI.enabled = true;
            }

            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.None);

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Open Proxy Folder", EditorStyles.toolbarButton)) RevealProxyFolder();
            if (GUILayout.Button("Delete Generated", EditorStyles.toolbarButton)) DeleteProxies();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSource(Source src)
        {
            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.BeginHorizontal();

            var icon = AssetPreview.GetMiniThumbnail(src.controller)
                       ?? AssetPreview.GetMiniTypeThumbnail(typeof(AnimatorController));
            if (icon != null) GUILayout.Label(icon, GUILayout.Width(18), GUILayout.Height(18));

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(src.Label, EditorStyles.boldLabel);

            // Description line
            var ownerStr = string.IsNullOrEmpty(src.owner) ? "" : src.owner + " · ";
            var layerInfo = src.TotalLayers == 0
                ? "no layers"
                : src.TotalLayers == 1 && src.ContentLayerCount == 0
                    ? "1 stub layer (patched, no content)"
                    : $"{src.ContentLayerCount} of {src.TotalLayers} layer(s) have content";
            EditorGUILayout.LabelField($"{ownerStr}{src.slot}  —  {layerInfo}", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();

            if (src.ContentLayerCount == 0)
            {
                var oldColor = GUI.color;
                GUI.color = new Color(0.5f, 0.5f, 0.5f);
                GUILayout.Label("no\ndata", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(36), GUILayout.Height(28));
                GUI.color = oldColor;
            }
            else
            {
                if (GUILayout.Button("Generate", GUILayout.Width(74), GUILayout.Height(28)))
                    Generate(src);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // ── Discovery ──────────────────────────────────────

        private void Scan()
        {
            _sources.Clear();
            var seen = new HashSet<AnimatorController>();

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    Collect(root, seen);
            }

            var withContent = _sources.Count(x => x.ContentLayerCount > 0);
            _status = _sources.Count == 0
                ? "Scan found no controllers. Spawn an avatar first."
                : $"Found {_sources.Count} controller(s) — {withContent} with readable content.";
        }

        private void AddTarget(UnityEngine.Object target)
        {
            if (target == null) return;
            var seen = new HashSet<AnimatorController>(_sources.Select(x => x.controller));

            if (target is AnimatorController ac)
            {
                if (seen.Add(ac))
                    _sources.Add(new Source { controller = ac, owner = ac.name, slot = "Controller" });
            }
            else if (target is GameObject go)
            {
                Collect(go, seen);
            }
            _target = null;
        }

        private void Collect(GameObject root, HashSet<AnimatorController> seen)
        {
#if VRC_SDK_VRCSDK3
            foreach (var descriptor in root.GetComponentsInChildren<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true))
            {
                CollectLayers(descriptor.gameObject.name, descriptor.baseAnimationLayers, seen);
                CollectLayers(descriptor.gameObject.name, descriptor.specialAnimationLayers, seen);
            }
#endif
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController is AnimatorController ac && seen.Add(ac))
                    _sources.Add(new Source { controller = ac, owner = animator.gameObject.name, slot = "Animator" });
            }
        }

#if VRC_SDK_VRCSDK3
        private void CollectLayers(string owner,
            VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.CustomAnimLayer[] layers,
            HashSet<AnimatorController> seen)
        {
            if (layers == null) return;
            foreach (var layer in layers)
            {
                if (layer.isDefault) continue;
                if (layer.animatorController is AnimatorController ac && seen.Add(ac))
                    _sources.Add(new Source { controller = ac, owner = owner, slot = layer.type.ToString() });
            }
        }
#endif

        // ── Generation ─────────────────────────────────────

        private void GenerateAll()
        {
            int made = 0, skipped = 0;
            foreach (var src in _sources)
            {
                if (src.ContentLayerCount == 0) { skipped++; continue; }
                var r = Build(src);
                if (r != null) made++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            _status = $"Done: {made} generated, {skipped} skipped (no content).";
        }

        private void Generate(Source src)
        {
            if (src.ContentLayerCount == 0)
            {
                _status = $"\"{src.Label}\" has no meaningful layer content.";
                return;
            }

            var proxy = Build(src);
            if (proxy == null) return;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            _status = $"Generated proxy — {src.owner} / {src.slot} / {src.Label}.";
            Selection.activeObject = proxy;
            EditorGUIUtility.PingObject(proxy);
            AssetDatabase.OpenAsset(proxy);
        }

        private AnimatorController Build(Source src)
        {
            try
            {
                EnsureFolder();

                var ctrlName = string.IsNullOrEmpty(src.Label) ? "proxy" : Sanitize(src.Label);
                var ownerName = string.IsNullOrEmpty(src.owner) ? "" : Sanitize(src.owner);
                var slotName = string.IsNullOrEmpty(src.slot) ? "" : Sanitize(src.slot);
                var fn = ownerName.Length > 0
                    ? $"{ownerName}__{ctrlName}__{slotName}"
                    : $"{ctrlName}__{slotName}";

                var path = AssetDatabase.GenerateUniqueAssetPath($"{ProxyFolder}/{fn}_proxy.controller");
                return new ProxyBuilder().Build(src.controller, path);
            }
            catch (Exception e)
            {
                _status = $"Generation failed: {e.Message}";
                Debug.LogError($"[VRCALoader] Proxy failed for \"{src.Label}\": {e}");
                return null;
            }
        }

        // ── Folder helpers ─────────────────────────────────

        private static void EnsureFolder()
        {
            if (AssetDatabase.IsValidFolder(ProxyFolder)) return;
            if (!AssetDatabase.IsValidFolder("Assets/VRCALoader"))
                AssetDatabase.CreateFolder("Assets", "VRCALoader");
            AssetDatabase.CreateFolder("Assets/VRCALoader", "_ProxyControllers");
        }

        private void RevealProxyFolder()
        {
            EnsureFolder();
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ProxyFolder);
            if (obj != null) EditorGUIUtility.PingObject(obj);
        }

        private void DeleteProxies()
        {
            if (!AssetDatabase.IsValidFolder(ProxyFolder)) { _status = "Nothing to delete."; return; }
            if (!EditorUtility.DisplayDialog("Delete Generated Proxies",
                    $"Delete everything under {ProxyFolder}?", "Delete", "Cancel")) return;
            AssetDatabase.DeleteAsset(ProxyFolder);
            AssetDatabase.Refresh();
            _status = "Deleted generated proxies.";
        }

        private static string Sanitize(string raw)
        {
            var clean = new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
            return string.IsNullOrWhiteSpace(clean) ? "controller" : clean;
        }

        // ── Proxy builder ──────────────────────────────────

        private sealed class ProxyBuilder
        {
            private const float ColStep = 300f;
            private const float RowStep = 80f;
            private static readonly Vector3 EntryPos = new(0, 0);
            private static readonly Vector3 AnyPos = new(0, 70);
            private static readonly Vector3 ExitPos = new(0, 140);
            private static readonly Vector3 ParentPos = new(0, 210);
            private static readonly Vector3 StateOrigin = new(260, 0);

            private AnimatorController _proxy;
            private readonly Dictionary<AnimatorState, AnimatorState> _sm = new();
            private readonly Dictionary<AnimatorStateMachine, AnimatorStateMachine> _mm = new();
            private readonly Dictionary<Motion, Motion> _mo = new();

            public AnimatorController Build(AnimatorController source, string path)
            {
                // Try public API first; if empty, try SerializedObject fallback.
                var srcLayers = source.layers;
                if (srcLayers.Length == 0)
                {
                    var so = new SerializedObject(source);
                    var lp = so.FindProperty("m_AnimatorLayers");
                    if (lp == null || !lp.isArray || lp.arraySize == 0)
                        throw new InvalidOperationException("No layer data (editor layers are stripped).");
                    // We can read array-size but can't fully reconstruct via SerializedObject
                    throw new InvalidOperationException(
                        $"Controller has {lp.arraySize} serialized layer(s) but public API returns none. " +
                        "Open an issue with a sample bundle.");
                }

                _proxy = AnimatorController.CreateAnimatorControllerAtPath(path);
                while (_proxy.layers.Length > 0)
                    _proxy.RemoveLayer(0);

                // Pass 1: layers + state machines
                for (int i = 0; i < srcLayers.Length; i++)
                {
                    var src = srcLayers[i];
                    _proxy.AddLayer(new AnimatorControllerLayer
                    {
                        name = string.IsNullOrEmpty(src.name) ? $"Layer {i}" : src.name,
                        defaultWeight = i == 0 ? 1f : src.defaultWeight,
                        avatarMask = src.avatarMask,
                        blendingMode = src.blendingMode,
                        iKPass = src.iKPass,
                        syncedLayerIndex = src.syncedLayerIndex,
                        syncedLayerAffectsTiming = src.syncedLayerAffectsTiming,
                    });

                    if (src.stateMachine != null)
                        CopyStructure(src.stateMachine, _proxy.layers[i].stateMachine);
                }

                // Parameters
                if (source.parameters is { Length: > 0 })
                    _proxy.parameters = source.parameters.Select(p => new AnimatorControllerParameter
                    {
                        name = p.name, type = p.type,
                        defaultBool = p.defaultBool, defaultFloat = p.defaultFloat, defaultInt = p.defaultInt,
                    }).ToArray();

                // Pass 2: transitions
                for (int i = 0; i < srcLayers.Length; i++)
                    if (srcLayers[i].stateMachine != null)
                        CopyTransitions(srcLayers[i].stateMachine);

                return _proxy;
            }

            private void CopyStructure(AnimatorStateMachine src, AnimatorStateMachine dst)
            {
                _mm[src] = dst;
                dst.entryPosition = EntryPos;
                dst.anyStatePosition = AnyPos;
                dst.exitPosition = ExitPos;
                dst.parentStateMachinePosition = ParentPos;

                var kids = src.states;
                int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(kids.Length)));
                for (int i = 0; i < kids.Length; i++)
                {
                    var ss = kids[i].state;
                    if (!ss) continue;
                    var p = StateOrigin + new Vector3((i % cols) * ColStep, (i / cols) * RowStep);
                    var ds = dst.AddState(ss.name, p);
                    CopyState(ss, ds);
                    _sm[ss] = ds;
                }

                var subs = src.stateMachines;
                int sr = Mathf.CeilToInt((float)kids.Length / cols) + 1;
                for (int i = 0; i < subs.Length; i++)
                {
                    var sm = subs[i].stateMachine;
                    if (!sm) continue;
                    var p = StateOrigin + new Vector3((i % cols) * ColStep, (sr + i / cols) * RowStep);
                    var dm = dst.AddStateMachine(sm.name, p);
                    CopyStructure(sm, dm);
                }

                CopyBehaviours(src.behaviours, dst);
            }

            private void CopyState(AnimatorState src, AnimatorState dst)
            {
                dst.speed = src.speed;
                dst.cycleOffset = src.cycleOffset;
                dst.mirror = src.mirror;
                dst.iKOnFeet = src.iKOnFeet;
                dst.writeDefaultValues = src.writeDefaultValues;
                dst.tag = src.tag;
                dst.speedParameter = src.speedParameter;
                dst.speedParameterActive = src.speedParameterActive;
                dst.mirrorParameter = src.mirrorParameter;
                dst.mirrorParameterActive = src.mirrorParameterActive;
                dst.cycleOffsetParameter = src.cycleOffsetParameter;
                dst.cycleOffsetParameterActive = src.cycleOffsetParameterActive;
                dst.timeParameter = src.timeParameter;
                dst.timeParameterActive = src.timeParameterActive;
                dst.motion = CloneMotion(src.motion);
                CopyBehaviours(src.behaviours, dst);
            }

            private void CopyTransitions(AnimatorStateMachine src)
            {
                if (!_mm.TryGetValue(src, out var dst)) return;

                foreach (var ch in src.states)
                {
                    var ss = ch.state;
                    if (!ss || !_sm.TryGetValue(ss, out var ds)) continue;
                    foreach (var t in ss.transitions)
                    {
                        AnimatorStateTransition ct = null;
                        if (t.isExit) ct = ds.AddExitTransition();
                        else if (t.destinationState != null && _sm.TryGetValue(t.destinationState, out var ts)) ct = ds.AddTransition(ts);
                        else if (t.destinationStateMachine != null && _mm.TryGetValue(t.destinationStateMachine, out var tm)) ct = ds.AddTransition(tm);
                        if (ct != null) ApplyBody(t, ct);
                    }
                }

                foreach (var t in src.anyStateTransitions)
                {
                    AnimatorStateTransition ct = null;
                    if (t.destinationState != null && _sm.TryGetValue(t.destinationState, out var ts)) ct = dst.AddAnyStateTransition(ts);
                    else if (t.destinationStateMachine != null && _mm.TryGetValue(t.destinationStateMachine, out var tm)) ct = dst.AddAnyStateTransition(tm);
                    if (ct != null) ApplyBody(t, ct);
                }

                foreach (var t in src.entryTransitions)
                {
                    AnimatorTransition ct = null;
                    if (t.destinationState != null && _sm.TryGetValue(t.destinationState, out var ts)) ct = dst.AddEntryTransition(ts);
                    else if (t.destinationStateMachine != null && _mm.TryGetValue(t.destinationStateMachine, out var tm)) ct = dst.AddEntryTransition(tm);
                    if (ct != null) ApplyCond(t.conditions, ct);
                }

                if (src.defaultState != null && _sm.TryGetValue(src.defaultState, out var df))
                    dst.defaultState = df;

                foreach (var sub in src.stateMachines)
                    if (sub.stateMachine) CopyTransitions(sub.stateMachine);
            }

            private static void ApplyBody(AnimatorStateTransition src, AnimatorStateTransition dst)
            {
                dst.hasExitTime = src.hasExitTime; dst.exitTime = src.exitTime;
                dst.hasFixedDuration = src.hasFixedDuration; dst.duration = src.duration;
                dst.offset = src.offset;
                dst.interruptionSource = src.interruptionSource;
                dst.orderedInterruption = src.orderedInterruption;
                dst.canTransitionToSelf = src.canTransitionToSelf;
                dst.mute = src.mute; dst.solo = src.solo;
                ApplyCond(src.conditions, dst);
            }

            private static void ApplyCond(AnimatorCondition[] conds, AnimatorStateTransition dst)
            { foreach (var c in conds) dst.AddCondition(c.mode, c.threshold, c.parameter); }

            private static void ApplyCond(AnimatorCondition[] conds, AnimatorTransition dst)
            { foreach (var c in conds) dst.AddCondition(c.mode, c.threshold, c.parameter); }

            private void CopyBehaviours(StateMachineBehaviour[] src, AnimatorState dst)
            {
                if (src == null) return;
                foreach (var b in src)
                {
                    if (!b) continue;
                    var nb = dst.AddStateMachineBehaviour(b.GetType());
                    if (nb) EditorUtility.CopySerialized(b, nb);
                }
            }

            private void CopyBehaviours(StateMachineBehaviour[] src, AnimatorStateMachine dst)
            {
                if (src == null) return;
                foreach (var b in src)
                {
                    if (!b) continue;
                    var nb = dst.AddStateMachineBehaviour(b.GetType());
                    if (nb) EditorUtility.CopySerialized(b, nb);
                }
            }

            private Motion CloneMotion(Motion src)
            {
                if (!src) return null;
                if (_mo.TryGetValue(src, out var c)) return c;

                if (src is BlendTree bt)
                {
                    var tree = new BlendTree
                    {
                        name = bt.name, hideFlags = HideFlags.HideInHierarchy,
                        blendType = bt.blendType, blendParameter = bt.blendParameter,
                        blendParameterY = bt.blendParameterY,
                        minThreshold = bt.minThreshold, maxThreshold = bt.maxThreshold,
                        useAutomaticThresholds = bt.useAutomaticThresholds,
                    };
                    _mo[src] = tree;
                    AssetDatabase.AddObjectToAsset(tree, _proxy);
                    tree.children = bt.children.Select(ch => new ChildMotion
                    {
                        motion = CloneMotion(ch.motion), threshold = ch.threshold,
                        position = ch.position, timeScale = ch.timeScale,
                        cycleOffset = ch.cycleOffset, directBlendParameter = ch.directBlendParameter,
                        mirror = ch.mirror,
                    }).ToArray();
                    return tree;
                }

                if (src is AnimationClip clip)
                {
                    var copy = Instantiate(clip);
                    copy.name = clip.name;
                    _mo[src] = copy;
                    AssetDatabase.AddObjectToAsset(copy, _proxy);
                    return copy;
                }

                _mo[src] = src;
                return src;
            }
        }
    }
}
#endif
