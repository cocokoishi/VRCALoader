#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Cocokoishi.VRCALoader
{
    public class ControllerExtract : EditorWindow
    {
        private static readonly string AssetRipperDir = Path.GetFullPath(
            Path.Combine(Application.dataPath, "VRCALoader/Assetripper"));
        private static readonly string AssetRipperExe = Path.Combine(AssetRipperDir, "AssetRipper.GUI.Free.exe");
        private static readonly string ExportsRoot = Path.Combine(AssetRipperDir, "Exports");

        private const string DownloadUrl =
            "https://github.com/AssetRipper/AssetRipper/releases/download/1.3.14/AssetRipper_win_x64.zip";
        private const int RipPort = 6969;

        public static string[] BundlePaths = Array.Empty<string>();

        private int _selectedIndex = -1;
        private string _bundlePath = "";
        private string _currentExportDir = "";
        private Vector2 _scroll;
        private string _status = "";
        private bool _busy;
        private bool _stripNonControllers = true;
        private IEnumerator _routine;

        private readonly List<Extraction> _extractions = new List<Extraction>();

        private sealed class Extraction
        {
            public string folderName;
            public string fullPath;
            public readonly List<ControllerEntry> controllers = new List<ControllerEntry>();
        }

        private sealed class ControllerEntry
        {
            public string filePath;
            public string fileName;
        }

        public static void Open()
        {
            var w = GetWindow<ControllerExtract>("Controller Extract");
            w.minSize = new Vector2(520, 400);
            w.Show();
        }

        private void OnEnable()
        {
            RefreshExtractions();
        }

        private void OnDisable()
        {
            EditorApplication.update -= Pump;
            _routine = null;
            _busy = false;
        }

        // ── GUI ────────────────────────────────────────────

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("AssetRipper Controller Extraction", EditorStyles.boldLabel);

            // ── Options ──
            _stripNonControllers = EditorGUILayout.ToggleLeft(
                "After export, delete all folders except AnimatorController",
                _stripNonControllers);

            EditorGUILayout.Space(4);

            // ── Source ──
            EditorGUILayout.LabelField("Bundle from VRCALoader Slots", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            var names = BundlePaths.Length > 0
                ? BundlePaths.Select(p => Path.GetFileName(p)).ToArray()
                : new[] { "(no slots — use Browse)" };

            if (_selectedIndex < 0 || _selectedIndex >= BundlePaths.Length)
            {
                _selectedIndex = BundlePaths.Length > 0 ? 0 : -1;
                if (_selectedIndex >= 0) _bundlePath = BundlePaths[_selectedIndex];
            }

            var newIdx = EditorGUILayout.Popup("Source", _selectedIndex, names);
            if (newIdx != _selectedIndex && newIdx < BundlePaths.Length)
            { _selectedIndex = newIdx; _bundlePath = BundlePaths[_selectedIndex]; }

            if (GUILayout.Button("Browse", GUILayout.Width(64)))
            {
                var p = EditorUtility.OpenFilePanel("Select VRCA / VRCW", "", "");
                if (!string.IsNullOrEmpty(p)) { _bundlePath = p; _selectedIndex = -1; }
            }
            EditorGUILayout.EndHorizontal();

            // ── Actions ──
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !_busy && File.Exists(_bundlePath);
            if (GUILayout.Button("Extract Bundle", GUILayout.Height(28))) StartExtract();
            GUI.enabled = true;
            if (GUILayout.Button("Refresh List", GUILayout.Height(28))) RefreshExtractions();
            EditorGUILayout.EndHorizontal();

            var batPath = Path.Combine(AssetRipperDir, "startsh/start_assetripper.bat");
            if (GUILayout.Button("Reveal start_assetripper.bat", EditorStyles.miniButton))
                EditorUtility.RevealInFinder(batPath);

            if (_busy)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(_status, EditorStyles.centeredGreyMiniLabel);
            }
            if (!string.IsNullOrEmpty(_status) && !_busy)
                EditorGUILayout.HelpBox(_status, MessageType.None);

            EditorGUILayout.Space(6);

            // ── Past Extractions ──
            EditorGUILayout.LabelField("Extracted Controllers", EditorStyles.boldLabel);

            if (_extractions.Count == 0)
            {
                EditorGUILayout.HelpBox("No extractions yet. Load a bundle and click Extract.", MessageType.Info);
            }
            else
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var ex in _extractions)
                {
                    EditorGUILayout.BeginVertical(GUI.skin.box);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(ex.folderName, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"{ex.controllers.Count} controller(s)", EditorStyles.miniLabel);
                    if (GUILayout.Button("Reveal", EditorStyles.miniButton, GUILayout.Width(52)))
                        EditorUtility.RevealInFinder(ex.fullPath);
                    if (GUILayout.Button("Delete", EditorStyles.miniButton, GUILayout.Width(52)))
                    {
                        if (EditorUtility.DisplayDialog("Delete Extraction",
                                $"Delete {ex.folderName}?", "Delete", "Cancel"))
                        {
                            Directory.Delete(ex.fullPath, true);
                            _extractions.Remove(ex);
                            AssetDatabase.Refresh();
                            break; // collection modified
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    foreach (var c in ex.controllers)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(20);
                        EditorGUILayout.LabelField(c.fileName);
                        if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(44)))
                        {
                            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                                "Assets" + c.filePath.Substring(Application.dataPath.Length));
                            if (asset) { Selection.activeObject = asset; AssetDatabase.OpenAsset(asset); }
                            else EditorUtility.RevealInFinder(c.filePath);
                        }
                        if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(44)))
                            GUIUtility.systemCopyBuffer = c.filePath;
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndScrollView();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Open Exports Folder", EditorStyles.toolbarButton))
                EditorUtility.RevealInFinder(ExportsRoot);
            if (GUILayout.Button("Clear All Exports", EditorStyles.toolbarButton)) ClearExports();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ── Extraction pipeline ────────────────────────────

        private void StartExtract()
        {
            if (_busy) return;
            if (!File.Exists(_bundlePath)) { _status = "Bundle file not found."; return; }

            PrepareExportDir();
            _busy = true;
            AssetDatabase.DisallowAutoRefresh();
            _routine = ExtractRoutine();
            EditorApplication.update += Pump;
            Repaint();
        }

        private void Pump()
        {
            if (_routine == null || !this)
            {
                CleanupRoutine();
                return;
            }

            try
            {
                if (!_routine.MoveNext())
                    CleanupRoutine();
            }
            catch (Exception e)
            {
                _status = $"Extraction failed: {e.Message}";
                UnityEngine.Debug.LogError($"[ControllerExtract] {e}");
                CleanupRoutine();
            }
            Repaint();
        }

        private void CleanupRoutine()
        {
            EditorApplication.update -= Pump;
            _routine = null;
            _busy = false;
            AssetDatabase.AllowAutoRefresh();
            AssetDatabase.Refresh();
            RefreshExtractions();
            Repaint();
        }

        private IEnumerator ExtractRoutine()
        {
            // ── Ensure AssetRipper exists ──
            if (!File.Exists(AssetRipperExe))
            {
                if (!EditorUtility.DisplayDialog("AssetRipper Required",
                        "AssetRipper is not installed. Download ~120 MB from GitHub?\n\n(You only need to do this once.)",
                        "Download", "Cancel"))
                {
                    _status = "AssetRipper is required. Download cancelled.";
                    yield break;
                }

                _status = "Downloading AssetRipper...";
                yield return null;

                var zipPath = Path.Combine(AssetRipperDir, "AssetRipper_win_x64.zip");
                if (!Directory.Exists(AssetRipperDir)) Directory.CreateDirectory(AssetRipperDir);

                using (var req = UnityWebRequest.Get(DownloadUrl))
                {
                    var dh = new DownloadHandlerFile(zipPath) { removeFileOnAbort = true };
                    req.downloadHandler = dh;
                    var op = req.SendWebRequest();
                    while (!op.isDone) yield return null;
                    if (req.result != UnityWebRequest.Result.Success)
                    { _status = $"Download failed: {req.error}"; yield break; }
                }

                _status = "Extracting...";
                yield return null;
                ZipFile.ExtractToDirectory(zipPath, AssetRipperDir, true);
                File.Delete(zipPath);

                if (!File.Exists(AssetRipperExe))
                { _status = "Extraction complete but exe not found."; yield break; }
            }

            // ── Check AssetRipper is running (user must start it manually) ──
            var baseUrl = $"http://localhost:{RipPort}";
            _status = "Checking AssetRipper server...";
            yield return null;

            bool alive = false;
            using (var req = UnityWebRequest.Get(baseUrl))
            {
                req.timeout = 5;
                var op = req.SendWebRequest();
                while (!op.isDone) yield return null;
                // Any response (even error) means the server is running
                alive = req.result != UnityWebRequest.Result.ConnectionError;
                if (!alive)
                {
                    // try /api/health
                    using var req2 = UnityWebRequest.Get(baseUrl + "/api/health");
                    req2.timeout = 3;
                    var op2 = req2.SendWebRequest();
                    while (!op2.isDone) yield return null;
                    alive = req2.result != UnityWebRequest.Result.ConnectionError;
                }
            }

            if (!alive)
            {
                var batPath = Path.Combine(AssetRipperDir, "startsh/start_assetripper.bat");
                _status = $"AssetRipper is not running.\n\nPlease run:\n{batPath}\n\nthen click Extract again.";
                yield break;
            }

            // ── Load bundle ──
            _status = "Loading bundle into AssetRipper...";
            yield return null;

            {
                var form = new WWWForm();
                form.AddField("Path", _bundlePath);
                using var req = UnityWebRequest.Post(baseUrl + "/LoadFile", form);
                req.timeout = 60;
                var op = req.SendWebRequest();
                while (!op.isDone) yield return null;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _status = $"LoadFile failed: {req.responseCode} {req.error}";
                    UnityEngine.Debug.LogError($"[ControllerExtract] LoadFile {req.responseCode}: {req.downloadHandler?.text}");
                    yield break;
                }
                UnityEngine.Debug.Log($"[ControllerExtract] LoadFile OK");
            }

            _status = "Bundle loaded. Exporting...";
            yield return new WaitForSecondsRealtime(1f);

            // ── Export ──
            {
                var form = new WWWForm();
                form.AddField("Path", _currentExportDir);
                using var req = UnityWebRequest.Post(baseUrl + "/Export/UnityProject", form);
                req.timeout = 600;
                var op = req.SendWebRequest();
                while (!op.isDone) yield return null;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _status = $"Export failed: {req.responseCode} {req.error}";
                    UnityEngine.Debug.LogError($"[ControllerExtract] Export {req.responseCode}: {req.downloadHandler?.text}");
                    yield break;
                }
                UnityEngine.Debug.Log($"[ControllerExtract] Export OK");
            }

            // ── Strip non-controller folders if requested ──
            if (_stripNonControllers)
            {
                _status = "Stripping non-controller assets...";
                yield return null;
                StripNonControllers(_currentExportDir);
            }

            // ── Rename Shader/Scripts folders ──
            _status = "Post-processing...";
            yield return null;
            PostProcessExports();

            // ── Scan & refresh list ──
            _status = "Scanning...";
            yield return null;
            RefreshExtractions();

            _status = "Extraction complete.";
        }

        // ── Helpers ────────────────────────────────────────

        private void PrepareExportDir()
        {
            if (!Directory.Exists(ExportsRoot))
                Directory.CreateDirectory(ExportsRoot);
            var baseName = Path.GetFileNameWithoutExtension(_bundlePath);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _currentExportDir = Path.Combine(ExportsRoot, $"{baseName}_{stamp}");
            Directory.CreateDirectory(_currentExportDir);
        }

        private void RefreshExtractions()
        {
            _extractions.Clear();
            if (!Directory.Exists(ExportsRoot)) return;

            foreach (var dir in Directory.GetDirectories(ExportsRoot))
            {
                var name = Path.GetFileName(dir);
                var files = Directory.GetFiles(dir, "*.controller", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".meta")).ToArray();

                var ex = new Extraction { folderName = name, fullPath = dir };
                foreach (var f in files)
                    ex.controllers.Add(new ControllerEntry { filePath = f, fileName = Path.GetFileName(f) });
                _extractions.Add(ex);
            }
        }

        private static void StripNonControllers(string exportDir)
        {
            // Find the Assets folder AssetRipper exported
            var assetsDir = FindDir(exportDir, "Assets");
            if (assetsDir == null) return;

            foreach (var sub in Directory.GetDirectories(assetsDir))
            {
                var name = Path.GetFileName(sub);
                if (name.Equals("AnimatorController", StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(sub, true); }
                catch (Exception e) { UnityEngine.Debug.LogWarning($"[ControllerExtract] Could not delete {name}: {e.Message}"); }
            }
        }

        private static string FindDir(string root, string name)
        {
            foreach (var d in Directory.GetDirectories(root, name, SearchOption.AllDirectories))
                return d;
            return null;
        }

        private void PostProcessExports()
        {
            if (string.IsNullOrEmpty(_currentExportDir) || !Directory.Exists(_currentExportDir)) return;
            RenameDir(_currentExportDir, "Shader", ".Shader");
            RenameDir(_currentExportDir, "shader", ".shader");
            RenameDir(_currentExportDir, "Scripts", ".Scripts");
            RenameDir(_currentExportDir, "scripts", ".scripts");
        }

        private static void RenameDir(string root, string name, string newName)
        {
            foreach (var d in Directory.GetDirectories(root, name, SearchOption.AllDirectories))
            {
                var parent = Path.GetDirectoryName(d);
                var target = Path.Combine(parent, newName);
                if (!Directory.Exists(target)) Directory.Move(d, target);
            }
        }

        private void ClearExports()
        {
            if (!Directory.Exists(ExportsRoot)) return;
            if (!EditorUtility.DisplayDialog("Clear All Exports",
                    "Delete every extraction under Exports/?", "Delete", "Cancel")) return;
            Directory.Delete(ExportsRoot, true);
            _currentExportDir = "";
            _extractions.Clear();
            _status = "All exports cleared.";
            AssetDatabase.Refresh();
        }
    }
}
#endif
