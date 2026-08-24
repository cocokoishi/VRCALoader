#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Cocokoishi.VRCALoader
{
    public class TutorialWindow : EditorWindow
    {
        private Vector2 _scroll;
        private const float LinkColor = 0.3f;

        public static void Open()
        {
            var w = GetWindow<TutorialWindow>(true, "VRCALoader Tutorial");
            w.minSize = new Vector2(480, 460);
            w.Show();
        }

        private void OnGUI()
        {
            var richLabel = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true };
            var boldLabel = new GUIStyle(richLabel) { fontStyle = FontStyle.Bold };
            var warningLabel = new GUIStyle(richLabel) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.6f, 0f) } };
            var sectionLabel = new GUIStyle(richLabel) { fontStyle = FontStyle.Bold, fontSize = 13 };

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(4);

            // ── Warning ──
            EditorGUILayout.LabelField("WARNING", warningLabel);
            EditorGUILayout.LabelField(
                "This tool is intended for recovering your own avatars and worlds only. " +
                "Do not use it on content you do not own or have explicit permission to access.",
                richLabel);
            EditorGUILayout.Space(8);

            // ── 1. Installation & Usage ──
            EditorGUILayout.LabelField("1. Installation & Usage", sectionLabel);
            EditorGUILayout.LabelField(
                "Place the plugin folder under Assets/VRCALoader/ in your Unity project. " +
                "Open the tool window via the menu bar: Tools > VRCALoader. " +
                "Select a .vrca (avatar) or .vrcw (world) file and click Load. " +
                "After loading, double-click any asset in the list or click Spawn to instantiate it into the current scene.\n\n" +
                "Tip: you can drag & drop .vrca / .vrcw files directly onto a slot's path field.",
                richLabel);
            EditorGUILayout.Space(6);

            // ── 2. How It Works ──
            EditorGUILayout.LabelField("2. How It Works", sectionLabel);
            EditorGUILayout.LabelField(
                "VRCALoader calls AssetBundle.LoadFromFileAsync, LoadAllAssetsAsync, and Object.Instantiate " +
                "to load AssetBundle contents directly into memory and place them into the current scene. " +
                "No original project files are required — only the bundled .vrca or .vrcw file.",
                richLabel);
            EditorGUILayout.Space(6);

            // ── 3. Use Cases ──
            EditorGUILayout.LabelField("3. Use Cases", sectionLabel);
            EditorGUILayout.LabelField("3.1 Local Recovery", boldLabel);
            EditorGUILayout.LabelField(
                "Find cached VRCA files at:\n" +
                "C:\\Users\\<username>\\AppData\\LocalLow\\VRChat\\VRChat\\Avatars\n" +
                "Load them to inspect BlendShapes, shader parameters, animation clips, and other asset data. " +
                "Pair with the unity-blendshape-to-json tool to migrate BlendShape data between avatars.",
                richLabel);
            DrawLink("unity-blendshape-to-json on GitHub",
                "https://github.com/cocokoishi/unity-blendshape-to-json");
            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("3.2 Cloud Recovery", boldLabel);
            EditorGUILayout.LabelField(
                "Log in through the VRChat SDK Control Panel, open VRCALoader, and click Download VRCA&VRCW. " +
                "Choose Cloud Avatars or Cloud Worlds, select a target platform, and click Download. A build " +
                "picker opens with the newest build for that platform selected by default; select another build " +
                "when needed. Avatars are saved as .vrca and worlds as .vrcw under Assets/VRCALoader/VRCA/. " +
                "The Downloaded tab shows the VRChat user stored in each new file name, can reveal or delete " +
                "local files, and can add one directly to a loader slot. " +
                "An empty slot is reused automatically, or a new slot is created when needed.",
                richLabel);
            EditorGUILayout.Space(6);

            // ── 4. Controller Extraction ──
            EditorGUILayout.LabelField("4. Controller Extraction", sectionLabel);
            EditorGUILayout.LabelField(
                "AnimatorControllers inside VRCA bundles have their editor-layer data and state-machine layout " +
                "stripped during upload — Unity's Animator window cannot open them directly. " +
                "Click the Controller Extract button at the bottom of the VRCALoader window to open the extraction tool. " +
                "It drives AssetRipper (auto-downloaded on first use) to unpack the bundle via its HTTP API " +
                "and produce readable .controller files that Unity can open normally.",
                richLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField(
                "Note: AssetRipper must be running before extraction. " +
                "Use the \"Reveal start_assetripper.bat\" button in the Controller Extract window to locate and run it.",
                new GUIStyle(richLabel) { normal = { textColor = Color.grey } });
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("5. Reference Remapper", sectionLabel);
            EditorGUILayout.LabelField(
                "After extracting a bundle, click Reference Remapper on the second action row. Select the export and " +
                "click Analyze References. Shader and MonoScript placeholders are matched to assets installed in the " +
                "project, and unresolved entries can be assigned manually. Apply Remapping repairs matching Material, " +
                "MonoBehaviour, AnimationClip, and supported .playable YAML references. Mappings remain visible when an " +
                "already-repaired export is analyzed again.",
                richLabel);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "WARNING: Reference Remapper is only for restoring avatars, worlds, and related assets that you legally " +
                "own or are explicitly authorized to recover. Do not use it for unauthorized extraction, copying, " +
                "redistribution, or any illegal purpose.",
                warningLabel);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Reference Remapper was inspired by FACS Utilities. It was implemented under clean-room principles, " +
                "and no source code from FACS Utilities was copied.",
                richLabel);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space(8);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(80), GUILayout.Height(24)))
                Close();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private static void DrawLink(string label, string url)
        {
            var linkStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(LinkColor, 0.5f, 1f) },
                fontStyle = FontStyle.Bold,
            };
            var content = new GUIContent(label);
            var rect = EditorGUILayout.GetControlRect(GUILayout.Width(linkStyle.CalcSize(content).x));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            GUI.Label(rect, content, linkStyle);
            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                Application.OpenURL(url);
                Event.current.Use();
            }
        }
    }
}
#endif
