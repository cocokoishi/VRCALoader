#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;
using System.IO;

namespace SimpleUtils
{
    public class VRCALoader : EditorWindow
    {
        private enum LoadPhase { Idle, LoadingBundle, LoadingAssets }

        private sealed class BundleSlot
        {
            public string path = "";
            public AssetBundle bundle;
            public UnityEngine.Object[] assets;
            public string[] scenePaths;
            public bool isSceneBundle;
            public readonly List<GameObject> spawned = new List<GameObject>();

            public LoadPhase phase;
            public AssetBundleCreateRequest bundleRequest;
            public AssetBundleRequest assetRequest;
            public float progress;
        }

        private readonly List<BundleSlot> slots = new List<BundleSlot>();
        private Vector2 scroll;
        private int slotCount = 1;
        private bool anySlotLoading;

        private static string SettingsPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "VRCALoader/vrcaloader-settings.json"));
        // Modern Ai icon: dark charcoal bg, vibrant orange text
        private static readonly Color BadgeBg = new Color(0.17f, 0.024f, 0.024f); // deep red-brown #2C0606
        private static readonly Color BadgeFg = new Color(1f, 0.604f, 0f);         // bright orange #FF9A00

        [MenuItem("Tools/VRCALoader")]
        public static void ShowWindow()
        {
            var w = GetWindow<VRCALoader>("VRCALoader");
            w.minSize = new Vector2(420, 400);
            w.Show();
        }

        // ── Lifecycle ──────────────────────────────────────

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
            UnloadAll();
            SaveState();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
                UnloadAll();
        }

        // ── Persistence ────────────────────────────────────

        private void LoadState()
        {
            slots.Clear();

            if (!File.Exists(SettingsPath))
            {
                slotCount = 1;
                slots.Add(new BundleSlot());
                return;
            }

            try
            {
                var json = File.ReadAllText(SettingsPath);
                var data = JsonUtility.FromJson<SettingsData>(json);
                if (data == null)
                {
                    slotCount = 1;
                    slots.Add(new BundleSlot());
                    return;
                }

                slotCount = Mathf.Clamp(data.slotCount, 0, 32);
                if (data.paths != null)
                {
                    foreach (var p in data.paths)
                        slots.Add(new BundleSlot { path = p ?? "" });
                }
                while (slots.Count < slotCount)
                    slots.Add(new BundleSlot());
            }
            catch
            {
                slotCount = 1;
                slots.Add(new BundleSlot());
            }
        }

        private void SaveState()
        {
            try
            {
                var data = new SettingsData
                {
                    slotCount = slotCount,
                    paths = new List<string>(slots.Count),
                };
                foreach (var s in slots)
                    data.paths.Add(s.path ?? "");

                var dir = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(SettingsPath, JsonUtility.ToJson(data, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VRCALoader] Failed to save settings: {e.Message}");
            }
        }

        [Serializable]
        private sealed class SettingsData
        {
            public int slotCount = 1;
            public List<string> paths = new List<string>();
        }

        // ── GUI ────────────────────────────────────────────

        private void OnGUI()
        {
            DrawHeader();
            DrawToolbar();
            EditorGUILayout.Space(4);

            scroll = EditorGUILayout.BeginScrollView(scroll);

            if (slots.Count == 0)
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.HelpBox("No slots. Click \"+ Slot\" to add one.", MessageType.Info);
                GUILayout.FlexibleSpace();
            }

            for (int i = 0; i < slots.Count; i++)
            {
                DrawSlot(slots[i], i);
                if (i < slots.Count - 1) EditorGUILayout.Space(4);
            }

            EditorGUILayout.EndScrollView();
            DrawFooter();
        }

        // ── Header (Illustrator-style rounded icon badge, left-aligned) ──

        private static Texture2D _badgeTex;

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();

            // Illustrator-style rounded-rect icon badge
            var badgeSize = 40;
            var badgeRect = GUILayoutUtility.GetRect(badgeSize, badgeSize, GUILayout.Width(badgeSize), GUILayout.Height(badgeSize));

            if (_badgeTex == null)
                _badgeTex = MakeRoundedRectTex(badgeSize, BadgeBg, 8);

            GUI.DrawTexture(badgeRect, _badgeTex, ScaleMode.StretchToFill);

            GUI.Label(badgeRect, "AL", new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = BadgeFg },
            });

            GUILayout.Space(8);

            EditorGUILayout.BeginVertical();
            GUILayout.Space(4);
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };
            EditorGUILayout.LabelField("VRCALoader", titleStyle, GUILayout.Height(20));
            EditorGUILayout.LabelField("AssetBundle Loader", EditorStyles.miniLabel, GUILayout.Height(14));
            GUILayout.Space(2);
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        // ── Toolbar ────────────────────────────────────────

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("+ Slot", EditorStyles.toolbarButton, GUILayout.Width(56)))
            {
                slots.Add(new BundleSlot());
                slotCount = slots.Count;
                SaveState();
            }

            if (GUILayout.Button("Unload All", EditorStyles.toolbarButton, GUILayout.Width(74)))
            {
                UnloadAll();
                slots.Clear();
                slots.Add(new BundleSlot());
                slotCount = 1;
                SaveState();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ── Slot Card ──────────────────────────────────────

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
                if (!string.IsNullOrEmpty(p))
                {
                    slot.path = p;
                    SaveState();
                    GUI.FocusControl(null);
                }
            }

            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(24), GUILayout.Height(18)))
            {
                UnloadSlot(slot);
                slots.RemoveAt(index);
                slotCount = slots.Count;
                if (slotCount == 0) slots.Add(new BundleSlot());
                SaveState();
                GUI.backgroundColor = oldBg;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            GUI.backgroundColor = oldBg;

            EditorGUILayout.EndHorizontal();

            // Load / Unload / Progress row
            EditorGUILayout.BeginHorizontal();

            if (slot.phase != LoadPhase.Idle)
            {
                var label = slot.phase == LoadPhase.LoadingBundle ? "Loading bundle..." : "Loading assets...";
                EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(GUILayout.Height(20)), slot.progress, label);
            }
            else if (slot.bundle == null)
            {
                var hasPath = !string.IsNullOrEmpty(slot.path);
                GUI.enabled = hasPath;
                if (GUILayout.Button("Load", GUILayout.Width(56), GUILayout.Height(20)))
                    BeginLoad(slot);
                GUI.enabled = true;

                if (!hasPath)
                    EditorGUILayout.LabelField("Select a file...", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                if (GUILayout.Button("Unload", GUILayout.Width(56), GUILayout.Height(20)))
                {
                    UnloadSlot(slot);
                    Repaint();
                }

                var parts = new List<string>(3);
                if (slot.isSceneBundle)
                {
                    parts.Add($"Scene Bundle ({slot.scenePaths.Length} scene(s))");
                }
                else
                {
                    if (slot.assets != null && slot.assets.Length > 0)
                        parts.Add($"{slot.assets.Length} assets");
                    if (slot.scenePaths != null && slot.scenePaths.Length > 0)
                        parts.Add($"{slot.scenePaths.Length} scenes");
                }
                if (slot.spawned.Count > 0)
                    parts.Add($"{slot.spawned.Count} spawned");
                if (parts.Count > 0)
                    EditorGUILayout.LabelField(string.Join(", ", parts), EditorStyles.miniBoldLabel);
            }

            EditorGUILayout.EndHorizontal();

            // Asset list
            if (slot.bundle != null && slot.assets != null)
            {
                var len = slot.assets.Length;
                if (len > 0)
                {
                    EditorGUILayout.Space(2);
                    for (int i = 0; i < len; i++)
                    {
                        if (slot.assets[i] == null) continue;
                        DrawAssetRow(slot, slot.assets[i]);
                    }
                }
            }

            // Scene list
            if (slot.bundle != null && slot.scenePaths != null)
            {
                var slen = slot.scenePaths.Length;
                if (slen > 0)
                {
                    EditorGUILayout.Space(2);
                    for (int i = 0; i < slen; i++)
                        DrawSceneRow(slot, slot.scenePaths[i]);
                }
            }

            EditorGUILayout.EndVertical();
        }

        // ── Asset Row ──────────────────────────────────────

        private static void DrawAssetRow(BundleSlot slot, UnityEngine.Object asset)
        {
            var rowRect = EditorGUILayout.BeginHorizontal();
            var hover = rowRect.Contains(Event.current.mousePosition);

            if (hover)
            {
                EditorGUI.DrawRect(rowRect, new Color(0.3f, 0.5f, 0.9f, 0.12f));
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                    if (Event.current.clickCount == 2 && asset is GameObject go)
                        SpawnAsset(slot, go);
                    Event.current.Use();
                }
            }

            GUILayout.Space(4);
            var icon = AssetPreview.GetMiniThumbnail(asset)
                    ?? AssetPreview.GetMiniTypeThumbnail(asset.GetType());
            GUILayout.Label(icon, GUILayout.Width(18), GUILayout.Height(18));
            EditorGUILayout.LabelField(asset.name, GUILayout.MinWidth(80));
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(asset.GetType().Name, EditorStyles.miniLabel, GUILayout.Width(80));

            if (asset is GameObject)
            {
                if (GUILayout.Button("Spawn", EditorStyles.miniButton, GUILayout.Width(54)))
                    SpawnAsset(slot, (GameObject)asset);
            }

            GUILayout.Space(2);
            EditorGUILayout.EndHorizontal();
        }

        // ── Scene Row ──────────────────────────────────────

        private static void DrawSceneRow(BundleSlot slot, string scenePath)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(4);
            GUILayout.Label(EditorGUIUtility.IconContent("SceneAsset Icon"), GUILayout.Width(18), GUILayout.Height(18));
            var sceneName = Path.GetFileNameWithoutExtension(scenePath);
            EditorGUILayout.LabelField(sceneName, GUILayout.MinWidth(80));
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Scene", EditorStyles.miniLabel, GUILayout.Width(80));

            if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(54)))
                LoadSceneFromBundle(slot, scenePath);

            GUILayout.Space(2);
            EditorGUILayout.EndHorizontal();
        }

        // ── Footer ─────────────────────────────────────────

        private void DrawFooter()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField("Slots", GUILayout.Width(32));
            var n = EditorGUILayout.IntField(slotCount, GUILayout.Width(36));
            n = Mathf.Clamp(n, 0, 32);

            if (EditorGUI.EndChangeCheck())
            {
                var delta = n - slotCount;
                slotCount = n;
                if (delta > 0)
                {
                    for (int i = 0; i < delta; i++)
                        slots.Add(new BundleSlot());
                }
                else if (delta < 0)
                {
                    for (int i = 0; i < -delta && slots.Count > 0; i++)
                    {
                        var last = slots.Count - 1;
                        UnloadSlot(slots[last]);
                        slots.RemoveAt(last);
                    }
                }
                SaveState();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ── Drag & Drop ────────────────────────────────────

        private void HandleDragDrop(Rect rect, BundleSlot slot)
        {
            var evt = Event.current;
            if (!rect.Contains(evt.mousePosition)) return;

            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                foreach (var p in DragAndDrop.paths)
                {
                    var ext = Path.GetExtension(p).ToLower();
                    if (ext != ".vrca" && ext != ".vrcw" && ext != ".bundle" && ext != "") continue;

                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        slot.path = p;
                        SaveState();
                        GUI.FocusControl(null);
                    }
                    evt.Use();
                    break;
                }
            }
        }

        // ── Async Load (phase-driven, no coroutines) ──────

        private void BeginLoad(BundleSlot slot)
        {
            if (string.IsNullOrEmpty(slot.path)) return;
            if (!File.Exists(slot.path))
            {
                Debug.LogError($"[VRCALoader] File not found: {slot.path}");
                return;
            }

            UnloadSlot(slot);

            slot.phase = LoadPhase.LoadingBundle;
            slot.progress = 0f;
            slot.bundleRequest = AssetBundle.LoadFromFileAsync(slot.path);

            if (!anySlotLoading)
            {
                anySlotLoading = true;
                EditorApplication.update += TickLoading;
            }

            Repaint();
        }

        private void TickLoading()
        {
            anySlotLoading = false;
            var needsRepaint = false;

            foreach (var slot in slots)
            {
                switch (slot.phase)
                {
                    case LoadPhase.LoadingBundle:
                        anySlotLoading = true;
                        if (slot.bundleRequest == null)
                        {
                            slot.phase = LoadPhase.Idle;
                            needsRepaint = true;
                            break;
                        }

                        slot.progress = Mathf.Lerp(0f, 0.5f, slot.bundleRequest.progress);

                        if (!slot.bundleRequest.isDone) break;

                        slot.bundle = slot.bundleRequest.assetBundle;
                        slot.bundleRequest = null;

                        if (slot.bundle == null)
                        {
                            Debug.LogError($"[VRCALoader] Bundle load failed: {slot.path}");
                            slot.phase = LoadPhase.Idle;
                            needsRepaint = true;
                            break;
                        }

                        // Check for scene bundle first — streamed scene bundles reject LoadAllAssetsAsync
                        slot.scenePaths = slot.bundle.GetAllScenePaths();
                        slot.isSceneBundle = slot.scenePaths != null && slot.scenePaths.Length > 0;

                        if (slot.isSceneBundle)
                        {
                            // Scene bundle: cannot enumerate assets, skip to done
                            slot.assets = new UnityEngine.Object[0];
                            slot.phase = LoadPhase.Idle;
                            slot.progress = 0f;
                            needsRepaint = true;
                            Debug.Log($"[VRCALoader] Scene bundle loaded: {slot.scenePaths.Length} scene(s)");
                            break;
                        }

                        slot.phase = LoadPhase.LoadingAssets;
                        slot.assetRequest = slot.bundle.LoadAllAssetsAsync();
                        needsRepaint = true;
                        goto case LoadPhase.LoadingAssets;

                    case LoadPhase.LoadingAssets:
                        anySlotLoading = true;
                        if (slot.assetRequest == null)
                        {
                            slot.phase = LoadPhase.Idle;
                            needsRepaint = true;
                            break;
                        }

                        slot.progress = Mathf.Lerp(0.5f, 1f, slot.assetRequest.progress);

                        if (!slot.assetRequest.isDone) break;

                        slot.assets = slot.assetRequest.allAssets;
                        slot.assetRequest = null;
                        slot.phase = LoadPhase.Idle;
                        slot.progress = 0f;
                        needsRepaint = true;

#if VRC_SDK_VRCSDK3
                        foreach (var asset in slot.assets)
                        {
                            if (asset is GameObject go)
                                FixEmptyAnimatorControllers(go);
                        }
#endif

                        Debug.Log($"[VRCALoader] Loaded: {slot.assets.Length} assets");
                        break;
                }
            }

            if (needsRepaint) Repaint();
            if (!anySlotLoading)
            {
                EditorApplication.update -= TickLoading;
            }
        }

        // ── Unload ─────────────────────────────────────────

        private static void UnloadSlot(BundleSlot slot)
        {
            slot.phase = LoadPhase.Idle;
            slot.bundleRequest = null;
            slot.assetRequest = null;

            // Destroy all spawned instances
            for (int i = slot.spawned.Count - 1; i >= 0; i--)
            {
                var go = slot.spawned[i];
                if (go != null) DestroyImmediate(go);
            }
            slot.spawned.Clear();

            if (slot.bundle != null)
            {
                slot.bundle.Unload(true);
                slot.bundle = null;
            }

            slot.assets = null;
            slot.scenePaths = null;
            slot.isSceneBundle = false;
            slot.progress = 0f;
        }

        private void UnloadAll()
        {
            foreach (var slot in slots)
                UnloadSlot(slot);
            anySlotLoading = false;
            EditorApplication.update -= TickLoading;
            Repaint();
        }

        // ── Scene Loading ──────────────────────────────────

        private static void LoadSceneFromBundle(BundleSlot slot, string scenePath)
        {
            var sceneName = Path.GetFileNameWithoutExtension(scenePath);

            if (Application.isPlaying)
            {
                SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                Debug.Log($"[VRCALoader] Scene opened additively: {sceneName}");
                return;
            }

            // Edit mode
            if (!slot.isSceneBundle && slot.assets != null)
            {
                // Non-scene bundle with assets: spawn root GameObjects
                var count = 0;
                foreach (var asset in slot.assets)
                {
                    if (asset is GameObject go)
                    {
                        SpawnAsset(slot, go);
                        count++;
                    }
                }
                Debug.Log($"[VRCALoader] Spawned {count} object(s).");
                return;
            }

            // Streamed scene bundle in edit mode: cannot extract assets.
            // Only SceneManager.LoadSceneAsync works (play mode).
            if (EditorUtility.DisplayDialog(
                "Scene Bundle",
                $"\"{sceneName}\" is a streamed scene bundle.\n\nAssets cannot be extracted in Edit mode.\nEnter Play Mode to load the scene additively via SceneManager.",
                "Enter Play Mode", "Cancel"))
            {
                EditorApplication.EnterPlaymode();
            }
        }

        // ── Spawn ──────────────────────────────────────────

        private static void SpawnAsset(BundleSlot slot, GameObject prefab)
        {
            if (prefab == null) return;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab)
                        ?? (GameObject)Instantiate(prefab);
            if (instance == null) return;

            instance.name = prefab.name;
            instance.transform.position = Vector3.zero;

#if VRC_SDK_VRCSDK3
            FixEmptyAnimatorControllers(instance);
#endif
            Undo.RegisterCreatedObjectUndo(instance, "Spawn from Bundle");
            Selection.activeGameObject = instance;
            EditorGUIUtility.PingObject(instance);

            slot.spawned.Add(instance);
        }

#if VRC_SDK_VRCSDK3
        /// <summary>
        /// AnimatorControllers from AssetBundles may fail to deserialize their internal
        /// layers, arriving with zero layers. The VRCSDK inspector blindly accesses
        /// <c>controller.layers[0]</c> and crashes. Give each broken controller a single
        /// empty layer so the inspector survives while the controller reference stays
        /// intact in the avatar descriptor.
        /// </summary>
        private static void FixEmptyAnimatorControllers(GameObject root)
        {
            var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null) return;

            var animator = root.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return;

            foreach (var layer in descriptor.baseAnimationLayers)
            {
                if (layer.isDefault) continue;
                var ac = layer.animatorController as UnityEditor.Animations.AnimatorController;
                if (ac != null && ac.layers.Length == 0)
                    ac.layers = new UnityEditor.Animations.AnimatorControllerLayer[1]
                        { new UnityEditor.Animations.AnimatorControllerLayer() };
            }
        }
#endif

        // ── Rounded-rect texture (Illustrator icon style) ──

        private static Texture2D MakeRoundedRectTex(int size, Color color, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;

            var pixels = new Color[size * size];
            float rSq = radius * radius;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Compute distance from nearest corner
                    float dx = 0f, dy = 0f;
                    if (x < radius) dx = radius - x - 0.5f;
                    else if (x >= size - radius) dx = x - (size - radius) + 0.5f;
                    if (y < radius) dy = radius - y - 0.5f;
                    else if (y >= size - radius) dy = y - (size - radius) + 0.5f;

                    float alpha = 1f;
                    if (dx > 0f && dy > 0f)
                    {
                        float distSq = dx * dx + dy * dy;
                        if (distSq > rSq)
                            alpha = 0f;
                        else if (distSq > rSq - 1.5f)
                            alpha = rSq - distSq + 0.5f; // simple AA
                    }

                    pixels[y * size + x] = alpha > 0f
                        ? new Color(color.r, color.g, color.b, Mathf.Clamp01(alpha))
                        : Color.clear;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
#endif
