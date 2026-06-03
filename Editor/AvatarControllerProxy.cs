#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
            public RuntimeAnimatorController controller;
            public string owner;
            public string slot;
            public string Label => controller != null ? controller.name : "(null)";

            public int TotalLayers
            {
                get
                {
                    if (controller == null) return 0;
                    if (controller is AnimatorController ac && ac.layers.Length > 0)
                        return ac.layers.Length;
                    using var so = new SerializedObject(controller);
                    // editor layers (may exist in serialized data even when public getter fails)
                    var arr = so.FindProperty("m_AnimatorLayers");
                    if (arr != null && arr.isArray && arr.arraySize > 0) return arr.arraySize;
                    // runtime blob paths
                    arr = so.FindProperty("m_Controller.m_LayerArray");
                    if (arr != null && arr.isArray) return arr.arraySize;
                    arr = so.FindProperty("m_LayerArray");
                    return (arr != null && arr.isArray) ? arr.arraySize : 0;
                }
            }

            public bool HasReadableData
            {
                get
                {
                    if (controller == null) return false;
                    if (controller is AnimatorController ac && ac.layers.Length > 0)
                    {
                        foreach (var l in ac.layers)
                            if (l.stateMachine != null && l.stateMachine.states.Length > 0)
                                return true;
                    }
                    using var so = new SerializedObject(controller);
                    // editor m_AnimatorLayers — often present even when public .layers is empty
                    var layers = so.FindProperty("m_AnimatorLayers");
                    if (layers != null && layers.isArray && layers.arraySize > 0)
                    {
                        for (int i = 0; i < layers.arraySize; i++)
                        {
                            var smRef = layers.GetArrayElementAtIndex(i)
                                .FindPropertyRelative("m_StateMachine")?.objectReferenceValue;
                            if (smRef is AnimatorStateMachine machine && machine.states.Length > 0)
                                return true;
                        }
                    }
                    // runtime blob paths
                    return so.FindProperty("m_TOS")?.isArray == true
                        || so.FindProperty("m_Controller.m_StateConstantArray")?.isArray == true
                        || so.FindProperty("m_StateConstantArray")?.isArray == true;
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
                "Rebuilds a fully readable AnimatorController from the underlying runtime data " +
                "in a bundle-loaded controller. Original objects are never touched.",
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
                var eligible = _sources.Count(s => s.HasReadableData);
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

            if (src.controller != null)
            {
                var icon = AssetPreview.GetMiniThumbnail(src.controller)
                           ?? AssetPreview.GetMiniTypeThumbnail(typeof(RuntimeAnimatorController));
                if (icon != null) GUILayout.Label(icon, GUILayout.Width(18), GUILayout.Height(18));
            }

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(src.Label, EditorStyles.boldLabel);
            var ownerStr = string.IsNullOrEmpty(src.owner) ? "" : src.owner + " · ";
            var desc = src.TotalLayers == 0 ? "no layers found" : $"{src.TotalLayers} layer(s)";
            if (!src.HasReadableData) desc += " — no reconstructable data";
            EditorGUILayout.LabelField(ownerStr + src.slot + "  —  " + desc, EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();

            if (!src.HasReadableData)
            {
                var old = GUI.color;
                GUI.color = new Color(0.5f, 0.5f, 0.5f);
                GUILayout.Label("no\ndata", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(36), GUILayout.Height(28));
                GUI.color = old;
            }
            else if (GUILayout.Button("Generate", GUILayout.Width(74), GUILayout.Height(28)))
                Generate(src);

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // ── Discovery ──────────────────────────────────────

        private void Scan()
        {
            _sources.Clear();
            var seen = new HashSet<RuntimeAnimatorController>();

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    Collect(root, seen);
            }

            var ok = _sources.Count(x => x.HasReadableData);
            _status = _sources.Count == 0
                ? "Scan found no controllers."
                : $"Found {_sources.Count} controller(s) — {ok} have readable data.";
        }

        private void AddTarget(UnityEngine.Object target)
        {
            if (target == null) return;
            var seen = new HashSet<RuntimeAnimatorController>(
                _sources.Select(x => x.controller));

            if (target is RuntimeAnimatorController rac)
            {
                if (seen.Add(rac))
                    _sources.Add(new Source { controller = rac, owner = rac.name, slot = "Controller" });
            }
            else if (target is GameObject go)
            {
                Collect(go, seen);
            }
            _target = null;
        }

        private void Collect(GameObject root, HashSet<RuntimeAnimatorController> seen)
        {
#if VRC_SDK_VRCSDK3
            foreach (var d in root.GetComponentsInChildren<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true))
            {
                CollectLayers(d.gameObject.name, d.baseAnimationLayers, seen);
                CollectLayers(d.gameObject.name, d.specialAnimationLayers, seen);
            }
#endif
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
                if (a.runtimeAnimatorController != null && seen.Add(a.runtimeAnimatorController))
                    _sources.Add(new Source { controller = a.runtimeAnimatorController, owner = a.gameObject.name, slot = "Animator" });
        }

#if VRC_SDK_VRCSDK3
        private void CollectLayers(string owner,
            VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.CustomAnimLayer[] layers,
            HashSet<RuntimeAnimatorController> seen)
        {
            if (layers == null) return;
            foreach (var layer in layers)
            {
                if (layer.isDefault || layer.animatorController == null) continue;
                if (seen.Add(layer.animatorController))
                    _sources.Add(new Source { controller = layer.animatorController, owner = owner, slot = layer.type.ToString() });
            }
        }
#endif

        // ── Generation ─────────────────────────────────────

        private void GenerateAll()
        {
            int made = 0, skipped = 0;
            foreach (var src in _sources)
            {
                if (!src.HasReadableData) { skipped++; continue; }
                if (Build(src) != null) made++;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            _status = $"Done: {made} generated, {skipped} skipped.";
        }

        private void Generate(Source src)
        {
            if (!src.HasReadableData) { _status = $"\"{src.Label}\" has no reconstructable data."; return; }
            var proxy = Build(src);
            if (proxy == null) return;
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            _status = $"Generated — {src.owner} / {src.slot} / {src.Label}.";
            Selection.activeObject = proxy;
            EditorGUIUtility.PingObject(proxy);
            AssetDatabase.OpenAsset(proxy);
        }

        private AnimatorController Build(Source src)
        {
            try
            {
                EnsureFolder();
                var ctrl = Sanitize(src.Label);
                var own = string.IsNullOrEmpty(src.owner) ? "" : Sanitize(src.owner);
                var sl = string.IsNullOrEmpty(src.slot) ? "" : Sanitize(src.slot);
                var fn = own.Length > 0 ? $"{own}__{ctrl}__{sl}" : $"{ctrl}__{sl}";
                var path = AssetDatabase.GenerateUniqueAssetPath($"{ProxyFolder}/{fn}_proxy.controller");
                return new ProxyBuilder().Build(src.controller, path);
            }
            catch (Exception e)
            {
                _status = $"Generation failed: {e.Message}";
                Debug.LogError($"[VRCALoader] Proxy failed for \"{src.Label}\":\n{e}");
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
            var o = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ProxyFolder);
            if (o) EditorGUIUtility.PingObject(o);
        }

        private void DeleteProxies()
        {
            if (!AssetDatabase.IsValidFolder(ProxyFolder)) { _status = "Nothing to delete."; return; }
            if (!EditorUtility.DisplayDialog("Delete Generated Proxies",
                    $"Delete everything under {ProxyFolder}?", "Delete", "Cancel")) return;
            AssetDatabase.DeleteAsset(ProxyFolder);
            AssetDatabase.Refresh();
            _status = "Deleted.";
        }

        private static string Sanitize(string raw)
        {
            var c = new string(raw.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray());
            return string.IsNullOrWhiteSpace(c) ? "controller" : c;
        }

        // ═══════════════════════════════════════════════════════
        // ProxyBuilder – runtime controller bytecode reconstructor
        // ═══════════════════════════════════════════════════════

        private sealed class ProxyBuilder
        {
            // ── layout constants ──────────────────────────
            private const float ColStep = 280f;
            private const float RowStep = 90f;
            private const int MaxPerRow = 6;
            private static readonly Vector3 EntryPos = new(0, 0);
            private static readonly Vector3 AnyPos = new(0, 70);
            private static readonly Vector3 ExitPos = new(0, 140);
            private static readonly Vector3 ParentPos = new(0, 210);

            // ── state ─────────────────────────────────────
            private AnimatorController _dst;
            private RuntimeAnimatorController _src;

            // hash → name
            private Dictionary<uint, string> _tos;
            // flat index → rebuilt BlendTree
            private Dictionary<int, BlendTree> _blendMap;
            // flat index → rebuilt AnimatorState
            private Dictionary<int, AnimatorState> _stateMap;
            // flat index → state machine (layer root)
            private Dictionary<int, AnimatorStateMachine> _machineMap;
            // AnimationClips from the source
            private AnimationClip[] _clips;
            // index → child motion info for blend trees
            private Dictionary<int, ChildMotionData> _childMotionData;

            private struct ChildMotionData
            {
                public int motionIndex; // positive = state/clip index, negative bit pattern = subtree
                public float threshold;
                public Vector2 position;
                public float timeScale;
            }

            // ── entry point ───────────────────────────────

            public AnimatorController Build(RuntimeAnimatorController source, string path)
            {
                _src = source;
                _dst = AnimatorController.CreateAnimatorControllerAtPath(path);
                _tos = new Dictionary<uint, string>();
                _blendMap = new Dictionary<int, BlendTree>();
                _stateMap = new Dictionary<int, AnimatorState>();
                _machineMap = new Dictionary<int, AnimatorStateMachine>();
                _childMotionData = new Dictionary<int, ChildMotionData>();

                using var so = new SerializedObject(source);

                // Step 1: TOS (hash → name dictionary)
                ExtractTOS(so);

                // Step 2: animation clips
                ExtractClips(so);

                // Step 3: parameters (needs TOS)
                ExtractParameters(so);

                // Step 4: layers (needs TOS)
                int layerCount = ExtractLayers(so);

                // Step 5: states within each layer (needs TOS, layers)
                ExtractStates(so, layerCount);

                // Step 6: blend trees (needs TOS, states, clips)
                ExtractBlendTrees(so);

                // Step 7: assign motions (needs blend trees + clips)
                AssignMotions(so);

                // Step 8: transitions (needs states, machines)
                ExtractTransitions(so, layerCount);

                // Remove the default layer that CreateAnimatorControllerAtPath made
                while (_dst.layers.Length > layerCount)
                    _dst.RemoveLayer(0);

                return _dst;
            }

            // ── helpers ───────────────────────────────────

            private static string ResolveName(Dictionary<uint, string> tos, uint hash)
            {
                if (hash == 0) return "";
                return tos.TryGetValue(hash, out var n) ? n : $"#{hash:X8}";
            }

            private static Vector3 GridPos(int index)
            {
                return new Vector3(
                    (index % MaxPerRow) * ColStep,
                    (index / MaxPerRow) * RowStep,
                    0f);
            }

            private static SerializedProperty TryFind(SerializedObject so, string path)
            {
                var p = so.FindProperty(path);
                if (p == null) Debug.Log($"[ProxyBuilder] Property not found: {path}");
                return p;
            }

            // ── Step 1: TOS ───────────────────────────────

            private void ExtractTOS(SerializedObject so)
            {
                // Try multiple possible paths for the string table
                var tosProp = TryFind(so, "m_TOS");
                if (tosProp == null || !tosProp.isArray) return;

                for (int i = 0; i < tosProp.arraySize; i++)
                {
                    var el = tosProp.GetArrayElementAtIndex(i);
                    var hashProp = el.FindPropertyRelative("first");
                    var nameProp = el.FindPropertyRelative("second");
                    if (hashProp != null && nameProp != null)
                        _tos[(uint)hashProp.longValue] = nameProp.stringValue;
                }
                Debug.Log($"[ProxyBuilder] TOS: {_tos.Count} entries");
            }

            // ── Step 2: animation clips ───────────────────

            private void ExtractClips(SerializedObject so)
            {
                var clipProp = TryFind(so, "m_AnimationClips");
                if (clipProp == null || !clipProp.isArray) return;

                _clips = new AnimationClip[clipProp.arraySize];
                for (int i = 0; i < clipProp.arraySize; i++)
                    _clips[i] = clipProp.GetArrayElementAtIndex(i).objectReferenceValue as AnimationClip;

                Debug.Log($"[ProxyBuilder] Clips: {_clips.Length}");
            }

            // ── Step 3: parameters ────────────────────────

            private void ExtractParameters(SerializedObject so)
            {
                // public API may work even for bundle controllers
                if (_src is AnimatorController editorAC && editorAC.parameters.Length > 0)
                {
                    _dst.parameters = editorAC.parameters.Select(p => new AnimatorControllerParameter
                    {
                        name = p.name, type = p.type,
                        defaultBool = p.defaultBool, defaultFloat = p.defaultFloat, defaultInt = p.defaultInt,
                    }).ToArray();
                    Debug.Log($"[ProxyBuilder] Params (public): {_dst.parameters.Length}");
                    return;
                }

                // Try runtime parameter array
                var valArr = TryFind(so, "m_Controller.m_ValueArray");
                if (valArr == null || !valArr.isArray)
                    valArr = TryFind(so, "m_ValueArray");
                if (valArr == null || !valArr.isArray) return;

                var paramList = new List<AnimatorControllerParameter>();
                for (int i = 0; i < valArr.arraySize; i++)
                {
                    var el = valArr.GetArrayElementAtIndex(i);
                    var idProp = el.FindPropertyRelative("m_ID") ?? el.FindPropertyRelative("m_NameID");
                    var typeProp = el.FindPropertyRelative("m_Type");
                    var defF = el.FindPropertyRelative("m_DefaultFloat");
                    var defI = el.FindPropertyRelative("m_DefaultInt");
                    var defB = el.FindPropertyRelative("m_DefaultBool");

                    if (idProp == null) continue;
                    var name = ResolveName(_tos, (uint)idProp.longValue);
                    if (string.IsNullOrEmpty(name)) name = $"Param_{i}";

                    var pType = (AnimatorControllerParameterType)(typeProp?.intValue ?? 1);
                    paramList.Add(new AnimatorControllerParameter
                    {
                        name = name, type = pType,
                        defaultFloat = defF?.floatValue ?? 0f,
                        defaultInt = defI?.intValue ?? 0,
                        defaultBool = defB?.boolValue ?? false,
                    });
                }
                if (paramList.Count > 0) _dst.parameters = paramList.ToArray();
                Debug.Log($"[ProxyBuilder] Params (runtime): {paramList.Count}");
            }

            // ── Step 4: layers ─────────────────────────────

            private int ExtractLayers(SerializedObject so)
            {
                // Try public API first
                if (_src is AnimatorController editorAC && editorAC.layers.Length > 0)
                {
                    int pubCount = 0;
                    for (int i = 0; i < editorAC.layers.Length; i++)
                    {
                        var sl = editorAC.layers[i];
                        if (sl.stateMachine == null) continue;

                        _dst.AddLayer(new AnimatorControllerLayer
                        {
                            name = string.IsNullOrEmpty(sl.name) ? $"Layer {i}" : sl.name,
                            defaultWeight = sl.defaultWeight,
                            avatarMask = sl.avatarMask,
                            blendingMode = sl.blendingMode,
                            iKPass = sl.iKPass,
                            syncedLayerIndex = sl.syncedLayerIndex,
                            syncedLayerAffectsTiming = sl.syncedLayerAffectsTiming,
                        });
                        pubCount++;
                    }
                    if (pubCount > 0) { Debug.Log($"[ProxyBuilder] Layers (public): {pubCount}"); return pubCount; }
                }

                // Runtime layer array
                var arr = TryFind(so, "m_Controller.m_LayerArray");
                if (arr == null || !arr.isArray)
                    arr = TryFind(so, "m_LayerArray");
                if (arr == null || !arr.isArray) return 0;

                for (int i = 0; i < arr.arraySize; i++)
                {
                    var el = arr.GetArrayElementAtIndex(i);
                    var nameHash = el.FindPropertyRelative("m_Binding")?.longValue
                                ?? el.FindPropertyRelative("m_Name")?.longValue ?? 0;
                    var name = ResolveName(_tos, (uint)nameHash);
                    if (string.IsNullOrEmpty(name)) name = $"Layer_{i}";

                    var blend = (AnimatorLayerBlendingMode)(el.FindPropertyRelative("m_LayerBlendingMode")?.intValue ?? 0);
                    var weight = el.FindPropertyRelative("m_DefaultWeight")?.floatValue ?? 1f;

                    _dst.AddLayer(new AnimatorControllerLayer
                    {
                        name = name,
                        defaultWeight = weight,
                        blendingMode = blend,
                    });
                }
                Debug.Log($"[ProxyBuilder] Layers (runtime): {arr.arraySize}");
                return arr.arraySize;
            }

            // ── Step 5: states ─────────────────────────────

            private void ExtractStates(SerializedObject so, int layerCount)
            {
                // State machine array
                var smArr = TryFind(so, "m_Controller.m_StateMachineArray");
                if (smArr == null || !smArr.isArray)
                    smArr = TryFind(so, "m_StateMachineArray");

                // State constant array
                var scArr = TryFind(so, "m_Controller.m_StateConstantArray");
                if (scArr == null || !scArr.isArray)
                    scArr = TryFind(so, "m_StateConstantArray");

                if (scArr == null || !scArr.isArray) return;

                // Read all state descriptors
                var stateDescs = new List<StateDesc>();
                for (int i = 0; i < scArr.arraySize; i++)
                {
                    var el = scArr.GetArrayElementAtIndex(i);
                    var nameHash = (uint)(el.FindPropertyRelative("m_NameID")?.longValue
                                       ?? el.FindPropertyRelative("m_Name")?.longValue ?? 0);
                    var name = ResolveName(_tos, nameHash);
                    if (string.IsNullOrEmpty(name)) name = $"State_{i}";

                    stateDescs.Add(new StateDesc
                    {
                        index = i,
                        name = name,
                        speed = el.FindPropertyRelative("m_Speed")?.floatValue ?? 1f,
                        cycleOffset = el.FindPropertyRelative("m_CycleOffset")?.floatValue ?? 0f,
                        mirror = el.FindPropertyRelative("m_Mirror")?.boolValue ?? false,
                        ikOnFeet = el.FindPropertyRelative("m_IKOnFeet")?.boolValue ?? false,
                        writeDefaults = el.FindPropertyRelative("m_WriteDefaultValues")?.boolValue ?? false,
                        speedParamIdx = el.FindPropertyRelative("m_SpeedParam")?.intValue ?? -1,
                        mirrorParamIdx = el.FindPropertyRelative("m_MirrorParam")?.intValue ?? -1,
                        cycleOffsetParamIdx = el.FindPropertyRelative("m_CycleOffsetParam")?.intValue ?? -1,
                        motionIndex = el.FindPropertyRelative("m_MotionIndex")?.intValue ?? -1,
                        tagHash = (uint)(el.FindPropertyRelative("m_Tag")?.longValue ?? 0),
                    });
                }

                // Assign states to layers via state machine array
                if (smArr != null && smArr.isArray && layerCount > 0)
                {
                    for (int li = 0; li < Mathf.Min(layerCount, smArr.arraySize); li++)
                    {
                        var smEl = smArr.GetArrayElementAtIndex(li);
                        var firstState = smEl.FindPropertyRelative("m_FirstStateIndex")?.intValue ?? -1;
                        var stateCount = smEl.FindPropertyRelative("m_StateCount")?.intValue ?? 0;
                        var defaultIdx = smEl.FindPropertyRelative("m_DefaultState")?.intValue ?? -1;

                        var dstSM = _dst.layers[li].stateMachine;
                        dstSM.entryPosition = EntryPos;
                        dstSM.anyStatePosition = AnyPos;
                        dstSM.exitPosition = ExitPos;
                        dstSM.parentStateMachinePosition = ParentPos;
                        _machineMap[li] = dstSM;

                        // Place states on grid
                        for (int si = 0; si < stateCount && (firstState + si) < stateDescs.Count; si++)
                        {
                            var sd = stateDescs[firstState + si];
                            var pos = GridPos(si);
                            var state = dstSM.AddState(sd.name, pos);
                            state.speed = sd.speed;
                            state.cycleOffset = sd.cycleOffset;
                            state.mirror = sd.mirror;
                            state.iKOnFeet = sd.ikOnFeet;
                            state.writeDefaultValues = sd.writeDefaults;
                            if (sd.tagHash != 0)
                            {
                                var tagName = ResolveName(_tos, sd.tagHash);
                                if (!string.IsNullOrEmpty(tagName)) state.tag = tagName;
                            }

                            _stateMap[sd.index] = state;
                        }

                        // Default state
                        if (defaultIdx >= 0 && _stateMap.TryGetValue(defaultIdx, out var def))
                            dstSM.defaultState = def;
                    }
                }
                else
                {
                    // No state machine array — assign all states to first layer(s)
                    var perLayer = Mathf.Max(1, stateDescs.Count / Mathf.Max(1, layerCount));
                    for (int li = 0; li < layerCount; li++)
                    {
                        var dstSM = _dst.layers[li].stateMachine;
                        dstSM.entryPosition = EntryPos;
                        dstSM.anyStatePosition = AnyPos;
                        dstSM.exitPosition = ExitPos;
                        dstSM.parentStateMachinePosition = ParentPos;
                        _machineMap[li] = dstSM;

                        int start = li * perLayer;
                        int end = Mathf.Min(start + perLayer, stateDescs.Count);
                        for (int si = start; si < end; si++)
                        {
                            var sd = stateDescs[si];
                            var pos = GridPos(si - start);
                            var state = dstSM.AddState(sd.name, pos);
                            state.speed = sd.speed;
                            state.cycleOffset = sd.cycleOffset;
                            state.mirror = sd.mirror;
                            state.iKOnFeet = sd.ikOnFeet;
                            state.writeDefaultValues = sd.writeDefaults;
                            if (sd.tagHash != 0)
                            {
                                var tagName = ResolveName(_tos, sd.tagHash);
                                if (!string.IsNullOrEmpty(tagName)) state.tag = tagName;
                            }
                            _stateMap[sd.index] = state;
                        }
                    }
                }

                Debug.Log($"[ProxyBuilder] States: {stateDescs.Count} across {layerCount} layer(s)");
            }

            private struct StateDesc
            {
                public int index;
                public string name;
                public float speed, cycleOffset;
                public bool mirror, ikOnFeet, writeDefaults;
                public int speedParamIdx, mirrorParamIdx, cycleOffsetParamIdx;
                public int motionIndex;
                public uint tagHash;
            }

            // ── Step 6: blend trees ────────────────────────

            private void ExtractBlendTrees(SerializedObject so)
            {
                var btArr = TryFind(so, "m_Controller.m_BlendTreeConstantArray");
                if (btArr == null || !btArr.isArray)
                    btArr = TryFind(so, "m_BlendTreeConstantArray");
                if (btArr == null || !btArr.isArray) return;

                var nodeArr = TryFind(so, "m_Controller.m_BlendTreeNodeConstantArray");
                if (nodeArr == null || !nodeArr.isArray)
                    nodeArr = TryFind(so, "m_BlendTreeNodeConstantArray");

                // First pass: read node array (child motion data)
                if (nodeArr != null && nodeArr.isArray)
                {
                    for (int i = 0; i < nodeArr.arraySize; i++)
                    {
                        var el = nodeArr.GetArrayElementAtIndex(i);
                        _childMotionData[i] = new ChildMotionData
                        {
                            motionIndex = el.FindPropertyRelative("m_ClipID")?.intValue
                                       ?? el.FindPropertyRelative("m_MotionIndex")?.intValue ?? -1,
                            threshold = el.FindPropertyRelative("m_Threshold")?.floatValue ?? 0f,
                            position = new Vector2(
                                el.FindPropertyRelative("m_PositionX")?.floatValue ?? 0f,
                                el.FindPropertyRelative("m_PositionY")?.floatValue ?? 0f),
                            timeScale = el.FindPropertyRelative("m_TimeScale")?.floatValue ?? 1f,
                        };
                    }
                }

                // Second pass: read blend tree descriptors, build recursive trees
                for (int i = 0; i < btArr.arraySize; i++)
                {
                    var el = btArr.GetArrayElementAtIndex(i);
                    var nameHash = (uint)(el.FindPropertyRelative("m_NameID")?.longValue
                                       ?? el.FindPropertyRelative("m_Name")?.longValue ?? 0);
                    var name = ResolveName(_tos, nameHash);
                    if (string.IsNullOrEmpty(name)) name = $"BlendTree_{i}";

                    var typeVal = el.FindPropertyRelative("m_BlendType")?.intValue ?? 0;
                    var childStart = el.FindPropertyRelative("m_ChildIndicesArrayIndex")?.intValue
                                  ?? el.FindPropertyRelative("m_Childs")?.intValue ?? 0;
                    var childCount = el.FindPropertyRelative("m_ChildCount")?.intValue
                                  ?? el.FindPropertyRelative("m_NumChildren")?.intValue ?? 0;
                    // Workaround: the actual first child index might be different.
                    // Many Unity versions encode it as m_NodeStartIndex
                    var nodeStart = el.FindPropertyRelative("m_NodeStartIndex")?.intValue
                                 ?? el.FindPropertyRelative("m_FirstChildIndex")?.intValue
                                 ?? childStart;

                    var tree = BuildBlendTreeRecursive(name, typeVal, nodeStart, childCount, i);
                    _blendMap[i] = tree;
                    AssetDatabase.AddObjectToAsset(tree, _dst);
                }

                Debug.Log($"[ProxyBuilder] BlendTrees: {btArr.arraySize}");
            }

            private BlendTree BuildBlendTreeRecursive(
                string name, int blendType, int nodeStart, int childCount, int btIndex)
            {
                var tree = new BlendTree
                {
                    name = name,
                    hideFlags = HideFlags.HideInHierarchy,
                    blendType = MapBlendType(blendType),
                    useAutomaticThresholds = blendType == 0, // 1D
                };

                // Needs parameter name from TOS or parameter list
                // We'll set this in a second pass, or leave as-is

                var children = new List<ChildMotion>();
                for (int ci = 0; ci < childCount; ci++)
                {
                    int nodeIdx = nodeStart + ci;
                    if (!_childMotionData.TryGetValue(nodeIdx, out var cmd))
                    {
                        children.Add(new ChildMotion { threshold = ci });
                        continue;
                    }

                    var child = new ChildMotion
                    {
                        threshold = cmd.threshold,
                        position = cmd.position,
                        timeScale = cmd.timeScale,
                    };

                    // Resolve motion
                    if (cmd.motionIndex >= 0)
                    {
                        // Could be an animation clip or a blend tree
                        if (_clips != null && cmd.motionIndex < _clips.Length && _clips[cmd.motionIndex] != null)
                        {
                            var copy = Instantiate(_clips[cmd.motionIndex]);
                            copy.name = _clips[cmd.motionIndex].name;
                            AssetDatabase.AddObjectToAsset(copy, _dst);
                            child.motion = copy;
                        }
                        else if (_blendMap.TryGetValue(cmd.motionIndex, out var subTree))
                        {
                            child.motion = subTree;
                        }
                    }

                    children.Add(child);
                }
                tree.children = children.ToArray();
                return tree;
            }

            private static BlendTreeType MapBlendType(int raw)
            {
                return raw switch
                {
                    0 => BlendTreeType.Simple1D,
                    1 => BlendTreeType.SimpleDirectional2D,
                    2 => BlendTreeType.FreeformDirectional2D,
                    3 => BlendTreeType.FreeformCartesian2D,
                    4 => BlendTreeType.Direct,
                    _ => BlendTreeType.Simple1D,
                };
            }

            // ── Step 7: assign motions ─────────────────────

            private void AssignMotions(SerializedObject so)
            {
                var scArr = TryFind(so, "m_Controller.m_StateConstantArray");
                if (scArr == null || !scArr.isArray)
                    scArr = TryFind(so, "m_StateConstantArray");
                if (scArr == null || !scArr.isArray) return;

                for (int i = 0; i < scArr.arraySize; i++)
                {
                    if (!_stateMap.TryGetValue(i, out var state)) continue;

                    var el = scArr.GetArrayElementAtIndex(i);
                    var motIdx = el.FindPropertyRelative("m_MotionIndex")?.intValue
                              ?? el.FindPropertyRelative("m_BlendTreeIndex")?.intValue ?? -1;

                    if (motIdx < 0) continue;

                    if (_blendMap.TryGetValue(motIdx, out var bt))
                    {
                        state.motion = bt;
                    }
                    else if (_clips != null && motIdx < _clips.Length && _clips[motIdx] != null)
                    {
                        var copy = Instantiate(_clips[motIdx]);
                        copy.name = _clips[motIdx].name;
                        AssetDatabase.AddObjectToAsset(copy, _dst);
                        state.motion = copy;
                    }
                }

                // Also assign blend parameter names to blend trees
                var btArr = TryFind(so, "m_Controller.m_BlendTreeConstantArray");
                if (btArr == null || !btArr.isArray)
                    btArr = TryFind(so, "m_BlendTreeConstantArray");
                if (btArr != null && btArr.isArray)
                {
                    for (int i = 0; i < btArr.arraySize; i++)
                    {
                        if (!_blendMap.TryGetValue(i, out var tree)) continue;
                        var el = btArr.GetArrayElementAtIndex(i);
                        var blendParamHash = (uint)(el.FindPropertyRelative("m_BlendParameterID")?.longValue
                                                  ?? el.FindPropertyRelative("m_BlendParameter")?.longValue ?? 0);
                        var blendParamYHash = (uint)(el.FindPropertyRelative("m_BlendParameterYID")?.longValue
                                                   ?? el.FindPropertyRelative("m_BlendParameterY")?.longValue ?? 0);

                        tree.blendParameter = ResolveName(_tos, blendParamHash);
                        tree.blendParameterY = ResolveName(_tos, blendParamYHash);

                        var minThresh = el.FindPropertyRelative("m_MinThreshold")?.floatValue ?? 0f;
                        var maxThresh = el.FindPropertyRelative("m_MaxThreshold")?.floatValue ?? 1f;
                        tree.minThreshold = minThresh;
                        tree.maxThreshold = maxThresh;
                    }
                }

                Debug.Log($"[ProxyBuilder] Motions assigned.");
            }

            // ── Step 8: transitions ────────────────────────

            private void ExtractTransitions(SerializedObject so, int layerCount)
            {
                var transArr = TryFind(so, "m_Controller.m_TransitionConstantArray");
                if (transArr == null || !transArr.isArray)
                    transArr = TryFind(so, "m_TransitionConstantArray");
                var condArr = TryFind(so, "m_Controller.m_ConditionConstantArray");
                if (condArr == null || !condArr.isArray)
                    condArr = TryFind(so, "m_ConditionConstantArray");
                var anyArr = TryFind(so, "m_Controller.m_AnyStateTransitionConstantArray");
                if (anyArr == null || !anyArr.isArray)
                    anyArr = TryFind(so, "m_AnyStateTransitionConstantArray");

                if (transArr == null || !transArr.isArray)
                {
                    Debug.Log("[ProxyBuilder] Transitions: none found");
                    return;
                }

                // Build condition lookup: condition index → list of conditions
                var condMap = new Dictionary<int, List<AnimatorCondition>>();
                if (condArr != null && condArr.isArray)
                {
                    int currentBundle = -1;
                    List<AnimatorCondition> currentList = null;
                    for (int i = 0; i < condArr.arraySize; i++)
                    {
                        var el = condArr.GetArrayElementAtIndex(i);
                        var bundleIdx = el.FindPropertyRelative("m_ConditionBundleIndex")?.intValue
                                     ?? el.FindPropertyRelative("m_BundleIndex")?.intValue
                                     ?? el.FindPropertyRelative("m_TransitionIndex")?.intValue ?? -1;

                        if (bundleIdx != currentBundle)
                        {
                            currentBundle = bundleIdx;
                            currentList = new List<AnimatorCondition>();
                            if (currentBundle >= 0)
                                condMap[currentBundle] = currentList;
                        }

                        if (currentList == null) continue;

                        var mode = (AnimatorConditionMode)(el.FindPropertyRelative("m_ConditionMode")?.intValue ?? 3); // 3 = If
                        var paramHash = (uint)(el.FindPropertyRelative("m_ParameterID")?.longValue
                                            ?? el.FindPropertyRelative("m_EventID")?.longValue ?? 0);
                        var threshold = el.FindPropertyRelative("m_Threshold")?.floatValue ?? 0f;
                        var paramName = ResolveName(_tos, paramHash);

                        currentList.Add(new AnimatorCondition
                        {
                            mode = mode,
                            threshold = threshold,
                            parameter = string.IsNullOrEmpty(paramName) ? $"Param_{paramHash:X}" : paramName,
                        });
                    }
                }

                // Process transitions
                for (int ti = 0; ti < transArr.arraySize; ti++)
                {
                    var el = transArr.GetArrayElementAtIndex(ti);
                    var srcIdx = el.FindPropertyRelative("m_SrcStateIndex")?.intValue
                              ?? el.FindPropertyRelative("m_SourceState")?.intValue ?? -1;
                    var dstIdx = el.FindPropertyRelative("m_DstStateIndex")?.intValue
                              ?? el.FindPropertyRelative("m_DestinationState")?.intValue ?? -1;
                    var isExit = el.FindPropertyRelative("m_IsExit")?.boolValue ?? false;
                    var layerIdx = el.FindPropertyRelative("m_LayerIndex")?.intValue
                                ?? el.FindPropertyRelative("m_Layer")?.intValue ?? 0;

                    if (!_stateMap.TryGetValue(srcIdx, out var srcState)) continue;
                    if (layerIdx < 0 || layerIdx >= _dst.layers.Length) continue;

                    AnimatorStateTransition trans;
                    if (isExit)
                    {
                        trans = srcState.AddExitTransition();
                    }
                    else if (_stateMap.TryGetValue(dstIdx, out var dstState))
                    {
                        trans = srcState.AddTransition(dstState);
                    }
                    else
                    {
                        continue;
                    }

                    // Fill transition properties
                    trans.hasExitTime = el.FindPropertyRelative("m_HasExitTime")?.boolValue ?? false;
                    trans.exitTime = el.FindPropertyRelative("m_ExitTime")?.floatValue ?? 1f;
                    trans.hasFixedDuration = el.FindPropertyRelative("m_HasFixedDuration")?.boolValue ?? false;
                    trans.duration = el.FindPropertyRelative("m_TransitionDuration")?.floatValue ?? 0.25f;
                    trans.offset = el.FindPropertyRelative("m_TransitionOffset")?.floatValue ?? 0f;
                    int interruptVal = el.FindPropertyRelative("m_InterruptionSource")?.intValue ?? 0;
                    trans.interruptionSource = (TransitionInterruptionSource)interruptVal;
                    trans.orderedInterruption = el.FindPropertyRelative("m_OrderedInterruption")?.boolValue ?? true;
                    trans.canTransitionToSelf = el.FindPropertyRelative("m_CanTransitionToSelf")?.boolValue ?? false;

                    // Conditions
                    var condBundleIdx = el.FindPropertyRelative("m_ConditionBundleIndex")?.intValue
                                     ?? el.FindPropertyRelative("m_ConditionIndex")?.intValue ?? -1;
                    if (condBundleIdx >= 0 && condMap.TryGetValue(condBundleIdx, out var conds))
                    {
                        foreach (var c in conds)
                            trans.AddCondition(c.mode, c.threshold, c.parameter);
                    }
                }

                // AnyState transitions
                if (anyArr != null && anyArr.isArray)
                {
                    for (int ai = 0; ai < anyArr.arraySize; ai++)
                    {
                        var el = anyArr.GetArrayElementAtIndex(ai);
                        var dstIdx = el.FindPropertyRelative("m_DstStateIndex")?.intValue
                                  ?? el.FindPropertyRelative("m_DestinationState")?.intValue ?? -1;
                        var layerIdx = el.FindPropertyRelative("m_LayerIndex")?.intValue
                                    ?? el.FindPropertyRelative("m_Layer")?.intValue ?? 0;

                        if (!_stateMap.TryGetValue(dstIdx, out var dstState)) continue;
                        if (layerIdx < 0 || layerIdx >= _dst.layers.Length) continue;

                        var machine = _dst.layers[layerIdx].stateMachine;
                        var trans = machine.AddAnyStateTransition(dstState);

                        trans.hasExitTime = el.FindPropertyRelative("m_HasExitTime")?.boolValue ?? false;
                        trans.exitTime = el.FindPropertyRelative("m_ExitTime")?.floatValue ?? 1f;
                        trans.hasFixedDuration = el.FindPropertyRelative("m_HasFixedDuration")?.boolValue ?? false;
                        trans.duration = el.FindPropertyRelative("m_TransitionDuration")?.floatValue ?? 0.25f;
                        trans.offset = el.FindPropertyRelative("m_TransitionOffset")?.floatValue ?? 0f;
                        trans.canTransitionToSelf = el.FindPropertyRelative("m_CanTransitionToSelf")?.boolValue ?? false;

                        var cbIdx = el.FindPropertyRelative("m_ConditionBundleIndex")?.intValue
                                 ?? el.FindPropertyRelative("m_ConditionIndex")?.intValue ?? -1;
                        if (cbIdx >= 0 && condMap.TryGetValue(cbIdx, out var conds))
                            foreach (var c in conds)
                                trans.AddCondition(c.mode, c.threshold, c.parameter);
                    }
                }

                Debug.Log($"[ProxyBuilder] Transitions: {transArr.arraySize} regular, {anyArr?.arraySize ?? 0} anyState");
            }
        }
    }
}
#endif
