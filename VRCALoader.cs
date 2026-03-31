#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

namespace SimpleUtils
{
    public class VRCALoader : EditorWindow
    {
        // 用于管理每个 VRCA 包的数据结构
        private class VRCAEntry
        {
            public string path = "";
            public AssetBundle loadedBundle = null;
            public Object[] loadedAssets = null;

            public AssetBundleCreateRequest bundleLoadRequest;
            public AssetBundleRequest assetLoadRequest;
            public bool isLoading = false;
        }

        private List<VRCAEntry> vrcaList = new List<VRCAEntry>();
        private Vector2 scrollPosition;
        private static readonly Color hoverColor = new Color(0.5f, 0.5f, 0.5f, 0.3f);

        [MenuItem("Tools/VRCALoader")]
        public static void ShowWindow()
        {
            var window = GetWindow<VRCALoader>("VRCALoader");
            window.minSize = new Vector2(400, 500);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += UnloadAllBundles;
            
            // 如果列表为空，默认给一个空槽位
            if (vrcaList.Count == 0)
            {
                vrcaList.Add(new VRCAEntry());
            }
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= UnloadAllBundles;
            UnloadAllBundles();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
            {
                UnloadAllBundles();
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("VRCALoader", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            // 遍历并绘制每个 VRCA 条目
            for (int i = 0; i < vrcaList.Count; i++)
            {
                DrawVRCAEntry(vrcaList[i], i);
            }

            EditorGUILayout.Space();

            // --- 底部工具栏：添加新路径 & 卸载所有 ---
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add New Path", GUILayout.Height(30)))
            {
                vrcaList.Add(new VRCAEntry());
            }

            GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
            if (GUILayout.Button("Unload & Clear All", GUILayout.Height(30)))
            {
                UnloadAllBundles();
                vrcaList.Clear();
                vrcaList.Add(new VRCAEntry()); // 留一个空的
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        private void DrawVRCAEntry(VRCAEntry entry, int index)
        {
            EditorGUILayout.BeginVertical("box");

            // --- 路径选择与删除按钮 ---
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"#{index + 1}", GUILayout.Width(25));
            entry.path = EditorGUILayout.TextField(entry.path);

            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                // 将扩展名参数设为 "" 以支持所有文件
                string path = EditorUtility.OpenFilePanel("Select VRCA / Bundle", "", "");
                if (!string.IsNullOrEmpty(path))
                {
                    entry.path = path;
                    GUI.FocusControl(null);
                }
            }

            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("X", GUILayout.Width(25)))
            {
                UnloadEntry(entry);
                vrcaList.RemoveAt(index);
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return; // 删除了就直接返回，不绘制后面的内容
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // --- 加载/卸载状态 ---
            if (entry.isLoading)
            {
                EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(), 0.5f, "Loading...");
            }
            else if (entry.loadedBundle == null)
            {
                if (GUILayout.Button("Load VRCA", GUILayout.Height(25)))
                {
                    StartLoadBundle(entry);
                }
            }
            else
            {
                GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
                if (GUILayout.Button("Unload VRCA", GUILayout.Height(25)))
                {
                    UnloadEntry(entry);
                }
                GUI.backgroundColor = Color.white;

                // --- 资源列表显示逻辑 ---
                if (entry.loadedAssets != null && entry.loadedAssets.Length > 0)
                {
                    EditorGUILayout.LabelField($"Found Assets: {entry.loadedAssets.Length}", EditorStyles.miniBoldLabel);
                    
                    for (int i = 0; i < entry.loadedAssets.Length; i++)
                    {
                        var asset = entry.loadedAssets[i];
                        if (asset == null) continue;

                        EditorGUILayout.BeginHorizontal();
                        Rect rect = EditorGUILayout.GetControlRect(GUILayout.Height(22));

                        // 鼠标悬停高亮与点击选中
                        if (rect.Contains(Event.current.mousePosition))
                        {
                            EditorGUI.DrawRect(rect, hoverColor);
                            if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                            {
                                Selection.activeObject = asset;
                                EditorGUIUtility.PingObject(asset);

                                // 双击生成
                                if (Event.current.clickCount == 2 && asset is GameObject)
                                    SpawnAssetInScene(asset as GameObject);
                                
                                Event.current.Use();
                            }
                        }

                        Texture2D icon = AssetPreview.GetMiniThumbnail(asset);
                        GUIContent content = new GUIContent(" " + asset.name, icon);
                        float btnWidth = 60f;
                        Rect labelRect = new Rect(rect.x, rect.y, rect.width - btnWidth, rect.height);
                        EditorGUI.LabelField(labelRect, content);

                        if (asset is GameObject)
                        {
                            Rect btnRect = new Rect(rect.x + rect.width - btnWidth, rect.y + 1, btnWidth, rect.height - 2);
                            if (GUI.Button(btnRect, "Spawn")) SpawnAssetInScene(asset as GameObject);
                        }
                        else
                        {
                            Rect typeRect = new Rect(rect.x + rect.width - btnWidth * 1.5f, rect.y, btnWidth * 1.5f, rect.height);
                            EditorGUI.LabelField(typeRect, asset.GetType().Name, EditorStyles.miniLabel);
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        // --- 核心逻辑：生成物体 ---
        private void SpawnAssetInScene(GameObject prefab)
        {
            if (prefab == null) return;
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab) ?? (GameObject)Instantiate(prefab);
            instance.name = prefab.name;
            instance.transform.position = Vector3.zero;
            Undo.RegisterCreatedObjectUndo(instance, "Spawn VRCA Asset");
            Selection.activeGameObject = instance;
        }

        // --- 异步加载逻辑 ---
        private void StartLoadBundle(VRCAEntry entry)
        {
            if (string.IsNullOrEmpty(entry.path)) return;
            UnloadEntry(entry);
            
            entry.isLoading = true;
            entry.bundleLoadRequest = AssetBundle.LoadFromFileAsync(entry.path);
            
            // 确保更新检测挂载
            EditorApplication.update -= CheckLoadProgress; 
            EditorApplication.update += CheckLoadProgress;
        }

        private void CheckLoadProgress()
        {
            bool needsRepaint = false;
            bool anyLoading = false;

            foreach (var entry in vrcaList)
            {
                if (!entry.isLoading) continue;
                anyLoading = true;

                // 阶段1：加载 Bundle
                if (entry.bundleLoadRequest != null)
                {
                    if (!entry.bundleLoadRequest.isDone) continue;

                    entry.loadedBundle = entry.bundleLoadRequest.assetBundle;
                    entry.bundleLoadRequest = null;

                    if (entry.loadedBundle == null)
                    {
                        Debug.LogError($"[VRCALoader] Load Failed! Check path: {entry.path}");
                        entry.isLoading = false;
                        needsRepaint = true;
                        continue;
                    }

                    // 开始加载内部资源
                    entry.assetLoadRequest = entry.loadedBundle.LoadAllAssetsAsync();
                    needsRepaint = true;
                }
                
                // 阶段2：加载 Assets
                if (entry.assetLoadRequest != null)
                {
                    if (!entry.assetLoadRequest.isDone) continue;

                    entry.loadedAssets = entry.assetLoadRequest.allAssets;
                    entry.assetLoadRequest = null;
                    entry.isLoading = false;
                    needsRepaint = true;
                }
            }

            if (needsRepaint)
            {
                Repaint();
            }

            // 如果全部加载完毕，移除 Update 监听
            if (!anyLoading)
            {
                EditorApplication.update -= CheckLoadProgress;
            }
        }

        // --- 卸载逻辑 ---
        private void UnloadEntry(VRCAEntry entry)
        {
            if (entry.loadedBundle != null)
            {
                entry.loadedBundle.Unload(true);
                entry.loadedBundle = null;
            }
            entry.loadedAssets = null;
            entry.isLoading = false;
            entry.bundleLoadRequest = null;
            entry.assetLoadRequest = null;
        }

        private void UnloadAllBundles()
        {
            foreach (var entry in vrcaList)
            {
                UnloadEntry(entry);
            }
            Repaint();
        }
    }
}
#endif