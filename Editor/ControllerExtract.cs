#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Cocokoishi.VRCALoader
{
    public class ControllerExtract : EditorWindow
    {
        private static readonly string AssetRipperDir = Path.GetFullPath(
            Path.Combine(Application.dataPath, "../VRCALoader_Data/Assetripper"));
        private static readonly string AssetRipperExe = Path.Combine(AssetRipperDir, "AssetRipper.GUI.Free.exe");
        private static readonly string ExportsRoot = Path.GetFullPath(
            Path.Combine(Application.dataPath, "VRCALoader/Exports"));

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
        private bool _stripNonControllers;
        private IEnumerator _routine;
        private bool _serverAlive;
        private double _lastServerCheckTime;

        private readonly List<Extraction> _extractions = new List<Extraction>();

        private sealed class Extraction
        {
            public string folderName;
            public string fullPath;
            public bool expanded;
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
            _stripNonControllers = EditorPrefs.GetBool("ControllerExtract_Strip", false);
            CheckServerAlive();
        }

        private void OnDisable()
        {
            EditorPrefs.SetString("ControllerExtract_LastBundle", _bundlePath ?? "");
            EditorPrefs.SetBool("ControllerExtract_Strip", _stripNonControllers);
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
            var prevStrip = _stripNonControllers;
            _stripNonControllers = EditorGUILayout.ToggleLeft(
                "After export, delete all folders except Animators",
                _stripNonControllers);
            if (_stripNonControllers != prevStrip)
                EditorPrefs.SetBool("ControllerExtract_Strip", _stripNonControllers);

            EditorGUILayout.Space(4);

            // ── Source ──
            var slotNames = BundlePaths.Length > 0
                ? BundlePaths.Select(p => Path.GetFileName(p)).ToArray()
                : new[] { "(open VRCALoader first)" };

            if (_selectedIndex < 0)
            {
                // Try matching EditorPrefs-restored path
                if (!string.IsNullOrEmpty(_bundlePath) && BundlePaths.Length > 0)
                    _selectedIndex = Array.IndexOf(BundlePaths, _bundlePath);
                if (_selectedIndex < 0 && BundlePaths.Length > 0)
                    _selectedIndex = 0;
            }
            if (_selectedIndex >= 0 && _selectedIndex < BundlePaths.Length)
                _bundlePath = BundlePaths[_selectedIndex];

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
            CheckServerAlive();
            EditorGUILayout.BeginHorizontal();

            bool canExtract = !_busy && !string.IsNullOrEmpty(_bundlePath) && File.Exists(_bundlePath);
            var extractLabel = _busy ? "Working..." :
                string.IsNullOrEmpty(_bundlePath) ? "No bundle selected" :
                !File.Exists(_bundlePath) ? "File not found" : "Extract Bundle";

            GUI.enabled = canExtract;
            if (GUILayout.Button(extractLabel, GUILayout.Height(28))) StartExtract();
            GUI.enabled = true;
            if (GUILayout.Button("Refresh List", GUILayout.Height(28))) RefreshExtractions();

            var batPath = Path.Combine(AssetRipperDir, "startsh/start_assetripper.bat");
            if (!_serverAlive)
            {
                GUI.contentColor = Color.gray;
                GUILayout.Label("●", GUILayout.Width(14), GUILayout.Height(28));
                GUI.contentColor = Color.white;

                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0f, 0.48f, 1f);
                if (GUILayout.Button("Start AssetRipper", GUILayout.Height(28)))
                    EditorUtility.RevealInFinder(batPath);
                GUI.backgroundColor = oldBg;
            }
            else
            {
                GUI.contentColor = new Color(0.2f, 0.78f, 0.35f);
                GUILayout.Label("●", GUILayout.Width(14), GUILayout.Height(28));
                GUI.contentColor = Color.white;

                GUI.enabled = false;
                GUILayout.Button("AssetRipper Running", GUILayout.Height(28));
                GUI.enabled = true;
            }
            EditorGUILayout.EndHorizontal();

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
                Extraction toDelete = null;
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var ex in _extractions)
                {
                    EditorGUILayout.BeginVertical(GUI.skin.box);
                    EditorGUILayout.BeginHorizontal();
                    ex.expanded = EditorGUILayout.Foldout(ex.expanded, ex.folderName, true, EditorStyles.foldoutHeader);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"{ex.controllers.Count} controller(s)", EditorStyles.miniLabel);
                    if (GUILayout.Button("Reveal", EditorStyles.miniButton, GUILayout.Width(56)))
                        EditorUtility.RevealInFinder(ex.fullPath);
                    if (GUILayout.Button("Delete", EditorStyles.miniButton, GUILayout.Width(56)))
                    {
                        if (EditorUtility.DisplayDialog("Delete Extraction",
                                $"Delete {ex.folderName}?", "Delete", "Cancel"))
                            toDelete = ex;
                    }
                    EditorGUILayout.EndHorizontal();

                    if (ex.expanded)
                    {
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
                            if (GUILayout.Button("Reveal", EditorStyles.miniButton, GUILayout.Width(56)))
                                EditorUtility.RevealInFinder(c.filePath);
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndScrollView();

                if (toDelete != null)
                {
                    if (Directory.Exists(toDelete.fullPath)) Directory.Delete(toDelete.fullPath, true);
                    var meta = toDelete.fullPath + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                    _extractions.Remove(toDelete);
                    AssetDatabase.Refresh();
                }
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

                // Auto-generate the start script
                var startshDir = Path.Combine(AssetRipperDir, "startsh");
                if (!Directory.Exists(startshDir)) Directory.CreateDirectory(startshDir);
                File.WriteAllText(Path.Combine(startshDir, "start_assetripper.bat"),
                    "@echo off\r\ncd /d \"%~dp0..\"\r\n" +
                    "echo AssetRipper on http://localhost:6969\r\n" +
                    "echo Close this window to stop.\r\n" +
                    "AssetRipper.GUI.Free.exe --port 6969\r\n");

                _status = "AssetRipper installed. Run start_assetripper.bat, then click Extract again.";
                yield break;
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
                _serverAlive = false;
                _status = "AssetRipper is not running. Click \"Start AssetRipper\", double-click the .bat file, then come back and click Extract.";
                yield break;
            }

            _serverAlive = true;

            // ── Load bundle ──
            PrepareExportDir();
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
                    if (name.Equals("AnimatorController", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("AnimationClip", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("AnimatorState", StringComparison.OrdinalIgnoreCase)) continue;
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

        private void CheckServerAlive()
        {
            if (EditorApplication.timeSinceStartup - _lastServerCheckTime < 3.0) return;
            _lastServerCheckTime = EditorApplication.timeSinceStartup;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        var result = client.BeginConnect("127.0.0.1", RipPort, null, null);
                        if (result.AsyncWaitHandle.WaitOne(500))
                        {
                            try { client.EndConnect(result); _serverAlive = true; }
                            catch { _serverAlive = false; }
                        }
                        else _serverAlive = false;
                    }
                }
                catch { _serverAlive = false; }
            });
        }

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

                var ex = new Extraction { folderName = name, fullPath = dir, expanded = dir == _currentExportDir };
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
