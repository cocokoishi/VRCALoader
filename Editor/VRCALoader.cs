#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cocokoishi.VRCALoader
{
    public class VRCALoader : EditorWindow
    {
        private sealed class BundleSlot
        {
            public string path = "";
            public AssetBundle bundle;
            public UnityEngine.Object[] assets;
            public string[] scenePaths;
            public bool isSceneBundle;
            public readonly List<GameObject> spawned = new List<GameObject>();

            public IEnumerator routine;
            public float progress;
            public string stage = "";

            public bool IsLoaded => bundle != null;
            public bool IsLoading => routine != null;
        }

        private readonly List<BundleSlot> _slots = new List<BundleSlot>();
        private readonly List<BundleSlot> _running = new List<BundleSlot>();
        private Vector2 _scroll;
        private int _slotCount = 1;

        private static string SettingsPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../VRCALoader_Data/vrcaloader-settings.json"));

        private static readonly Color BadgeBg = new Color(0.17f, 0.024f, 0.024f);
        private static readonly Color BadgeFg = new Color(1f, 0.604f, 0f);
        private static Texture2D _badgeTex;

        [MenuItem("Tools/VRCALoader")]
        public static void ShowWindow()
        {
            var w = GetWindow<VRCALoader>("VRCALoader");
            w.minSize = new Vector2(420, 400);
            w.Show();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += UnloadAll;
            LoadState();
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= UnloadAll;
            EditorApplication.update -= Drive;
            UnloadAll();
            SaveState();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                UnloadAll();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                // Unity tears down all Play Mode objects automatically.
                // Calling DestroyImmediate or AssetBundle.Unload here would
                // race with Unity's own cleanup and crash (Access Violation).
                foreach (var slot in _slots)
                {
                    slot.routine = null;
                    slot.spawned.Clear();
                    if (slot.bundle != null) { slot.bundle.Unload(false); slot.bundle = null; }
                    slot.assets = null;
                    slot.scenePaths = null;
                    slot.isSceneBundle = false;
                    slot.progress = 0f;
                    slot.stage = "";
                }
                _running.Clear();
                EditorApplication.update -= Drive;
            }
        }

        [Serializable]
        private sealed class SettingsData
        {
            public int slotCount = 1;
            public List<string> paths = new List<string>();
        }

        private void LoadState()
        {
            _slots.Clear();
            if (!File.Exists(SettingsPath))
            {
                _slotCount = 1;
                _slots.Add(new BundleSlot());
                return;
            }

            try
            {
                var data = JsonUtility.FromJson<SettingsData>(File.ReadAllText(SettingsPath));
                if (data == null) { _slotCount = 1; _slots.Add(new BundleSlot()); return; }

                _slotCount = Mathf.Clamp(data.slotCount, 0, 32);
                if (data.paths != null)
                    foreach (var p in data.paths) _slots.Add(new BundleSlot { path = p ?? "" });
                while (_slots.Count < _slotCount) _slots.Add(new BundleSlot());
            }
            catch
            {
                _slotCount = 1;
                _slots.Clear();
                _slots.Add(new BundleSlot());
            }
        }

        private void SaveState()
        {
            try
            {
                var data = new SettingsData { slotCount = _slotCount, paths = new List<string>(_slots.Count) };
                foreach (var s in _slots) data.paths.Add(s.path ?? "");
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, JsonUtility.ToJson(data, true));
            }
            catch (Exception e) { Debug.LogWarning($"[VRCALoader] Could not save settings: {e.Message}"); }
        }

        // ── Loading ────────────────────────────────────────

        private void StartLoad(BundleSlot slot)
        {
            if (string.IsNullOrEmpty(slot.path)) return;
            UnloadSlot(slot);
            slot.routine = LoadRoutine(slot);
            if (!_running.Contains(slot)) _running.Add(slot);
            EditorApplication.update -= Drive;
            EditorApplication.update += Drive;
            Repaint();
        }

        private void Drive()
        {
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                var slot = _running[i];
                bool keepGoing;
                try { keepGoing = slot.routine != null && slot.routine.MoveNext(); }
                catch (Exception e) { Debug.LogError($"[VRCALoader] Load failed: {e}"); keepGoing = false; }
                if (!keepGoing) { slot.routine = null; _running.RemoveAt(i); }
            }
            Repaint();
            if (_running.Count == 0) EditorApplication.update -= Drive;
        }

        private IEnumerator LoadRoutine(BundleSlot slot)
        {
            if (!File.Exists(slot.path))
            {
                Debug.LogError($"[VRCALoader] File not found: {slot.path}");
                yield break;
            }

            slot.stage = "Reading bundle...";
            slot.progress = 0f;

            AssetBundleCreateRequest bundleReq;
            try { bundleReq = AssetBundle.LoadFromFileAsync(slot.path); }
            catch (Exception e) { Debug.LogError($"[VRCALoader] Could not open bundle: {e.Message}"); yield break; }

            while (!bundleReq.isDone) { slot.progress = bundleReq.progress * 0.5f; yield return null; }

            slot.bundle = bundleReq.assetBundle;
            if (slot.bundle == null) { Debug.LogError($"[VRCALoader] Not a valid AssetBundle: {slot.path}"); yield break; }

            slot.scenePaths = slot.bundle.GetAllScenePaths();
            slot.isSceneBundle = slot.bundle.isStreamedSceneAssetBundle;

            if (slot.isSceneBundle)
            {
                slot.assets = Array.Empty<UnityEngine.Object>();
                slot.progress = 0f; slot.stage = "";
                Debug.Log($"[VRCALoader] Scene bundle ready: {slot.scenePaths.Length} scene(s).");
                yield break;
            }

            slot.stage = "Loading assets...";
            var assetReq = slot.bundle.LoadAllAssetsAsync();
            while (!assetReq.isDone) { slot.progress = 0.5f + assetReq.progress * 0.5f; yield return null; }

            slot.assets = assetReq.allAssets;
            foreach (var asset in slot.assets)
            {
                if (asset == null) continue;
                asset.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
#if VRC_SDK_VRCSDK3 && !UDON
                if (asset is GameObject go)
                {
                    StripPipelineManager(go);
                    if (!Application.isPlaying) PatchEmptyControllers(go);
                }
#endif
            }

            slot.progress = 0f; slot.stage = "";
            Debug.Log($"[VRCALoader] Loaded {slot.assets.Length} asset(s) from {Path.GetFileName(slot.path)}.");
        }

        private void UnloadSlot(BundleSlot slot)
        {
            slot.routine = null;
            _running.Remove(slot);
            for (int i = slot.spawned.Count - 1; i >= 0; i--)
                if (slot.spawned[i] != null) DestroyImmediate(slot.spawned[i]);
            slot.spawned.Clear();
            if (slot.bundle != null) { slot.bundle.Unload(true); slot.bundle = null; }
            slot.assets = null; slot.scenePaths = null; slot.isSceneBundle = false;
            slot.progress = 0f; slot.stage = "";
        }

        private void UnloadAll()
        {
            foreach (var slot in _slots) UnloadSlot(slot);
            _running.Clear();
            EditorApplication.update -= Drive;
            Repaint();
        }

        // ── Spawn ──────────────────────────────────────────

        private static void SpawnAsset(BundleSlot slot, GameObject source)
        {
            if (source == null) return;
            var instance = Instantiate(source);
            if (instance == null) return;
            instance.name = source.name;
            if (!Application.isPlaying) instance.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
            instance.transform.position = Vector3.zero;
#if VRC_SDK_VRCSDK3 && !UDON
            StripPipelineManager(instance);
            if (!Application.isPlaying) PatchEmptyControllers(instance);
#endif
            Undo.RegisterCreatedObjectUndo(instance, "Spawn from Bundle");
            Selection.activeGameObject = instance;
            EditorGUIUtility.PingObject(instance);
            slot.spawned.Add(instance);
        }

        // ── Controller patching ────────────────────────────

        // VRC_SDK_VRCSDK3 is defined by both Worlds and Avatars SDKs, but
        // VRC.SDK3.Avatars types only exist when the Avatars SDK is installed.
        // UDON is only defined by the Worlds SDK, so !UDON prevents compiling
        // avatar-specific code in Worlds-only projects. Pattern from FACS01.
#if VRC_SDK_VRCSDK3 && !UDON
        private static void StripPipelineManager(GameObject root)
        {
            foreach (var pm in root.GetComponentsInChildren<VRC.Core.PipelineManager>(true))
                DestroyImmediate(pm, true);
        }

        // AnimatorControllers extracted from a VRChat AssetBundle have their editor
        // serialized layers stripped, which makes controller.layers return an empty
        // array. The AvatarDescriptor inspector then crashes with IndexOutOfRange
        // when it tries to read layers[0]. Give every zero-layer controller a single
        // bare layer — the runtime blob stays untouched, so Play-mode consumers like
        // GestureManager continue to work.
        private static void PatchEmptyControllers(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            if (!animator || !animator.isHuman) return;

            var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (!descriptor) return;

            foreach (var layer in descriptor.baseAnimationLayers)
            {
                if (layer.isDefault) continue;
                if (layer.animatorController
                    is UnityEditor.Animations.AnimatorController ac
                    && ac.layers.Length == 0)
                {
                    ac.layers = new UnityEditor.Animations.AnimatorControllerLayer[1]
                        { new UnityEditor.Animations.AnimatorControllerLayer() };
                }
            }
        }
#endif

        // ── Scenes ─────────────────────────────────────────

        private static void OpenScene(BundleSlot slot, string scenePath)
        {
            var sceneName = Path.GetFileNameWithoutExtension(scenePath);
            if (Application.isPlaying) { SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive); return; }
            if (EditorUtility.DisplayDialog("Scene Bundle",
                    $"\"{sceneName}\" is a streamed scene bundle.\n\nStreamed scenes can only be opened through SceneManager in Play Mode.",
                    "Enter Play Mode", "Cancel"))
                EditorApplication.EnterPlaymode();
        }

        // ── GUI ────────────────────────────────────────────

        private void OnGUI()
        {
            // Keep ControllerExtract's slot list up to date
            ControllerExtract.BundlePaths = _slots
                .Where(s => !string.IsNullOrEmpty(s.path))
                .Select(s => s.path).ToArray();

            DrawHeader();
            DrawToolbar();
            EditorGUILayout.Space(4);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_slots.Count == 0)
                GUILayout.Label("No slots. Click \"+ Slot\" to add one.", EditorStyles.centeredGreyMiniLabel);

            for (int i = 0; i < _slots.Count; i++)
            {
                DrawSlot(_slots[i], i);
                if (i < _slots.Count - 1) EditorGUILayout.Space(4);
            }

            EditorGUILayout.EndScrollView();
            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            const int badgeSize = 40;
            var badgeRect = GUILayoutUtility.GetRect(badgeSize, badgeSize, GUILayout.Width(badgeSize), GUILayout.Height(badgeSize));
            if (_badgeTex == null) _badgeTex = MakeRoundedRectTex(badgeSize, BadgeBg, 8);
            GUI.DrawTexture(badgeRect, _badgeTex, ScaleMode.StretchToFill);
            GUI.Label(badgeRect, "AL", new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold,
                normal = { textColor = BadgeFg },
            });
            GUILayout.Space(8);
            EditorGUILayout.BeginVertical();
            GUILayout.Space(4);
            EditorGUILayout.LabelField("VRCALoader", new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } }, GUILayout.Height(20));
            var headerLinkRect = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(headerLinkRect, MouseCursor.Link);
            if (Event.current.type == EventType.MouseDown && headerLinkRect.Contains(Event.current.mousePosition))
            {
                Application.OpenURL("https://github.com/cocokoishi/VRCALoader");
                Event.current.Use();
            }
            EditorGUILayout.LabelField("AssetBundle Loader", EditorStyles.miniLabel, GUILayout.Height(14));
            GUILayout.Space(2);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("+ Slot", EditorStyles.toolbarButton, GUILayout.Width(56)))
            { _slots.Add(new BundleSlot()); _slotCount = _slots.Count; SaveState(); }
            if (GUILayout.Button("Unload All", EditorStyles.toolbarButton, GUILayout.Width(74)))
            { UnloadAll(); _slots.Clear(); _slots.Add(new BundleSlot()); _slotCount = 1; SaveState(); }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSlot(BundleSlot slot, int index)
        {
            EditorGUILayout.BeginVertical(GUI.skin.box);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"#{index + 1}", EditorStyles.miniBoldLabel, GUILayout.Width(24));
            var dropRect = EditorGUILayout.GetControlRect(GUILayout.Height(18));
            HandleDragDrop(dropRect, slot);
            EditorGUI.BeginChangeCheck();
            slot.path = EditorGUI.TextField(dropRect, slot.path);
            if (EditorGUI.EndChangeCheck()) SaveState();
            if (GUILayout.Button("...", EditorStyles.miniButton, GUILayout.Width(24), GUILayout.Height(18)))
            {
                var p = EditorUtility.OpenFilePanel("Select VRCA / VRCW", "", "");
                if (!string.IsNullOrEmpty(p)) { slot.path = p; SaveState(); GUI.FocusControl(null); }
            }
            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(24), GUILayout.Height(18)))
            {
                UnloadSlot(slot); _slots.RemoveAt(index); _slotCount = _slots.Count;
                if (_slotCount == 0) _slots.Add(new BundleSlot());
                SaveState();
                GUI.backgroundColor = oldBg;
                EditorGUILayout.EndHorizontal(); EditorGUILayout.EndVertical();
                return;
            }
            GUI.backgroundColor = oldBg;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (slot.IsLoading)
                EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(GUILayout.Height(20)), slot.progress, slot.stage);
            else if (!slot.IsLoaded)
            {
                GUI.enabled = !string.IsNullOrEmpty(slot.path);
                if (GUILayout.Button("Load", GUILayout.Width(56), GUILayout.Height(20))) StartLoad(slot);
                GUI.enabled = true;
                if (string.IsNullOrEmpty(slot.path)) EditorGUILayout.LabelField("Select a file...", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                if (GUILayout.Button("Unload", GUILayout.Width(56), GUILayout.Height(20))) { UnloadSlot(slot); Repaint(); }
                EditorGUILayout.LabelField(DescribeSlot(slot), EditorStyles.miniBoldLabel);
            }
            EditorGUILayout.EndHorizontal();

            if (slot.IsLoaded && slot.assets != null)
                for (int i = 0; i < slot.assets.Length; i++)
                    if (slot.assets[i] != null) DrawAssetRow(slot, slot.assets[i]);

            if (slot.IsLoaded && slot.scenePaths != null)
                for (int i = 0; i < slot.scenePaths.Length; i++) DrawSceneRow(slot, slot.scenePaths[i]);

            EditorGUILayout.EndVertical();
        }

        private static string DescribeSlot(BundleSlot slot)
        {
            var parts = new List<string>(3);
            if (slot.isSceneBundle) parts.Add($"Scene bundle ({slot.scenePaths.Length} scene(s))");
            else if (slot.assets != null && slot.assets.Length > 0) parts.Add($"{slot.assets.Length} assets");
            if (slot.spawned.Count > 0) parts.Add($"{slot.spawned.Count} spawned");
            return parts.Count > 0 ? string.Join(", ", parts) : "Loaded";
        }

        private static void DrawAssetRow(BundleSlot slot, UnityEngine.Object asset)
        {
            if (!asset) return; // destroyed object guard

            var rowRect = EditorGUILayout.BeginHorizontal();
            if (rowRect.Contains(Event.current.mousePosition))
            {
                EditorGUI.DrawRect(rowRect, new Color(0.3f, 0.5f, 0.9f, 0.12f));
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                    if (Event.current.clickCount == 2 && asset is GameObject go) SpawnAsset(slot, go);
                    Event.current.Use();
                }
            }
            GUILayout.Space(4);
            var icon = AssetPreview.GetMiniThumbnail(asset) ?? AssetPreview.GetMiniTypeThumbnail(asset.GetType());
            GUILayout.Label(icon, GUILayout.Width(18), GUILayout.Height(18));
            EditorGUILayout.LabelField(asset.name, GUILayout.MinWidth(80));
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(asset.GetType().Name, EditorStyles.miniLabel, GUILayout.Width(80));
            if (asset is GameObject)
                if (GUILayout.Button("Spawn", EditorStyles.miniButton, GUILayout.Width(54))) SpawnAsset(slot, (GameObject)asset);
            GUILayout.Space(2);
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawSceneRow(BundleSlot slot, string scenePath)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(4);
            GUILayout.Label(EditorGUIUtility.IconContent("SceneAsset Icon"), GUILayout.Width(18), GUILayout.Height(18));
            EditorGUILayout.LabelField(Path.GetFileNameWithoutExtension(scenePath), GUILayout.MinWidth(80));
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Scene", EditorStyles.miniLabel, GUILayout.Width(80));
            if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(54))) OpenScene(slot, scenePath);
            GUILayout.Space(2);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Controller Extract  ▾", GUILayout.Height(22)))
                ControllerExtract.Open();
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField("Slots", GUILayout.Width(32));
            var n = Mathf.Clamp(EditorGUILayout.IntField(_slotCount, GUILayout.Width(36)), 0, 32);
            if (EditorGUI.EndChangeCheck())
            {
                var delta = n - _slotCount; _slotCount = n;
                if (delta > 0) for (int i = 0; i < delta; i++) _slots.Add(new BundleSlot());
                else for (int i = 0; i < -delta && _slots.Count > 0; i++) { var last = _slots.Count - 1; UnloadSlot(_slots[last]); _slots.RemoveAt(last); }
                SaveState();
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Tutorial", EditorStyles.toolbarButton, GUILayout.Width(56)))
                TutorialWindow.Open();
            EditorGUILayout.EndHorizontal();
        }

        private void HandleDragDrop(Rect rect, BundleSlot slot)
        {
            var evt = Event.current;
            if (!rect.Contains(evt.mousePosition)) return;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            foreach (var p in DragAndDrop.paths)
            {
                var ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext != ".vrca" && ext != ".vrcw" && ext != ".bundle" && ext != "") continue;
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); slot.path = p; SaveState(); GUI.FocusControl(null); }
                evt.Use(); break;
            }
        }

        private static Texture2D MakeRoundedRectTex(int size, Color color, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color[size * size];
            float rSq = radius * radius;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = 0f, dy = 0f;
                if (x < radius) dx = radius - x - 0.5f;
                else if (x >= size - radius) dx = x - (size - radius) + 0.5f;
                if (y < radius) dy = radius - y - 0.5f;
                else if (y >= size - radius) dy = y - (size - radius) + 0.5f;
                float alpha = 1f;
                if (dx > 0f && dy > 0f) { float distSq = dx * dx + dy * dy; if (distSq > rSq) alpha = 0f; else if (distSq > rSq - 1.5f) alpha = rSq - distSq + 0.5f; }
                pixels[y * size + x] = alpha > 0f ? new Color(color.r, color.g, color.b, Mathf.Clamp01(alpha)) : Color.clear;
            }
            tex.SetPixels(pixels); tex.Apply();
            return tex;
        }
    }
}
#endif
