#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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
            _bundlePath = EditorPrefs.GetString("ControllerExtract_LastBundle", "");
            if (!string.IsNullOrEmpty(_bundlePath) && !File.Exists(_bundlePath))
                _bundlePath = "";
        }

        private void OnDisable()
        {
            EditorPrefs.SetString("ControllerExtract_LastBundle", _bundlePath ?? "");
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
            var slotNames = BundlePaths.Length > 0
                ? BundlePaths.Select(p => Path.GetFileName(p)).ToArray()
                : new[] { "(open VRCALoader first)" };

            if (_selectedIndex < 0 || _selectedIndex >= BundlePaths.Length)
                _selectedIndex = BundlePaths.Length > 0 ? 0 : -1;

            EditorGUILayout.BeginHorizontal();
            var newIdx = EditorGUILayout.Popup("Slot", _selectedIndex, slotNames);
            if (newIdx != _selectedIndex && newIdx >= 0 && newIdx < BundlePaths.Length)
            { _selectedIndex = newIdx; _bundlePath = BundlePaths[_selectedIndex]; }

            if (GUILayout.Button("Browse", GUILayout.Width(64)))
            {
                var p = EditorUtility.OpenFilePanel("Select VRCA / VRCW", "", "");
                if (!string.IsNullOrEmpty(p)) { _bundlePath = p; _selectedIndex = -1; }
            }
            EditorGUILayout.EndHorizontal();

            // Show current path (persists across domain reloads)
            if (!string.IsNullOrEmpty(_bundlePath))
                EditorGUILayout.LabelField(Path.GetFileName(_bundlePath), EditorStyles.miniLabel);
            else if (BundlePaths.Length == 0)
                EditorGUILayout.LabelField("Open VRCALoader first, or use Browse", EditorStyles.centeredGreyMiniLabel);

            // ── Actions ──
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            bool canExtract = !_busy && !string.IsNullOrEmpty(_bundlePath) && File.Exists(_bundlePath);
            var extractLabel = _busy ? "Working..." :
                string.IsNullOrEmpty(_bundlePath) ? "No bundle selected" :
                !File.Exists(_bundlePath) ? "File not found" : "Extract Bundle";

            GUI.enabled = canExtract;
            if (GUILayout.Button(extractLabel, GUILayout.Height(28))) StartExtract();
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
                            if (Directory.Exists(ex.fullPath)) Directory.Delete(ex.fullPath, true);
                            var meta = ex.fullPath + ".meta";
                            if (File.Exists(meta)) File.Delete(meta);
                            _extractions.Remove(ex);
                            AssetDatabase.Refresh();
                            break;
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
                            var relPath = "Assets" + c.filePath
                                .Substring(Application.dataPath.Length)
                                .Replace('\\', '/');
                            EditorApplication.delayCall += () =>
                            {
                                try { AssetDatabase.ImportAsset(relPath, ImportAssetOptions.ForceUpdate); } catch { }
                                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relPath);
                                if (asset != null) AssetDatabase.OpenAsset(asset);
                                else EditorUtility.RevealInFinder(c.filePath);
                            };
                        }
                        if (GUILayout.Button("Reveal", EditorStyles.miniButton, GUILayout.Width(48)))
                            EditorUtility.RevealInFinder(c.filePath);
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
                var body = "Path=" + Uri.EscapeDataString(_bundlePath);
                using var req = new UnityWebRequest(baseUrl + "/LoadFile", "POST");
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
                req.timeout = 60;
                var op = req.SendWebRequest();
                while (!op.isDone) yield return null;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _status = $"LoadFile failed: {req.responseCode}";
                    UnityEngine.Debug.LogError($"[ControllerExtract] LoadFile {req.responseCode}: {req.downloadHandler?.text}");
                    yield break;
                }
            }

            _status = "Bundle loaded. Exporting...";
            yield return new WaitForSecondsRealtime(1f);

            // ── Export ──
            {
                var body = "Path=" + Uri.EscapeDataString(_currentExportDir);
                using var req = new UnityWebRequest(baseUrl + "/Export/UnityProject", "POST");
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
                req.timeout = 600;
                var op = req.SendWebRequest();
                while (!op.isDone) yield return null;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _status = $"Export failed: {req.responseCode}";
                    UnityEngine.Debug.LogError($"[ControllerExtract] Export {req.responseCode}: {req.downloadHandler?.text}");
                    yield break;
                }
            }

            // ── Flatten: move ExportedProject/Assets/* up to export root ──
            _status = "Flattening export structure...";
            yield return null;

            var exportedProject = Path.Combine(_currentExportDir, "ExportedProject");
            var nestedAssets = Path.Combine(exportedProject, "Assets");
            if (Directory.Exists(nestedAssets))
            {
                // Delete everything in export root EXCEPT ExportedProject
                foreach (var f in Directory.GetFiles(_currentExportDir)) File.Delete(f);
                foreach (var d in Directory.GetDirectories(_currentExportDir))
                    if (d != exportedProject) Directory.Delete(d, true);

                // Delete everything in ExportedProject EXCEPT Assets
                foreach (var f in Directory.GetFiles(exportedProject)) File.Delete(f);
                foreach (var d in Directory.GetDirectories(exportedProject))
                    if (d != nestedAssets) Directory.Delete(d, true);

                // Move Assets/* up to export root
                foreach (var d in Directory.GetDirectories(nestedAssets))
                    Directory.Move(d, Path.Combine(_currentExportDir, Path.GetFileName(d)));
                foreach (var f in Directory.GetFiles(nestedAssets))
                    File.Move(f, Path.Combine(_currentExportDir, Path.GetFileName(f)));

                // Delete empty ExportedProject
                Directory.Delete(exportedProject, true);
            }

            // ── Strip non-controller folders if requested ──
            if (_stripNonControllers)
            {
                _status = "Stripping non-controller assets...";
                yield return null;
                foreach (var d in Directory.GetDirectories(_currentExportDir))
                {
                    var name = Path.GetFileName(d);
                    if (name.Equals("AnimatorController", StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(d, true); }
                    catch (Exception e) { UnityEngine.Debug.LogWarning($"[ControllerExtract] Could not delete {name}: {e.Message}"); }
                }
            }

            // ── Rename Shader/Scripts folders ──
            _status = "Post-processing...";
            yield return null;
            RenameDir(_currentExportDir, "Shader", ".Shader");
            RenameDir(_currentExportDir, "shader", ".shader");
            RenameDir(_currentExportDir, "Scripts", ".Scripts");
            RenameDir(_currentExportDir, "scripts", ".scripts");

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
