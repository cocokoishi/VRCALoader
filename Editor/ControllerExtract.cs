#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Cocokoishi.VRCALoader
{
    /// <summary>
    /// Launches AssetRipper to unpack a VRCA bundle, then scans the export for
    /// AnimatorController assets and lists them in a GUI. Also renames .shader
    /// files so Unity doesn't try to compile them on import.
    /// </summary>
    public class ControllerExtract : EditorWindow
    {
        private static readonly string AssetRipperDir = Path.GetFullPath(
            Path.Combine(Application.dataPath, "VRCALoader/Assetripper"));
        private static readonly string AssetRipperExe = Path.Combine(AssetRipperDir, "AssetRipper.GUI.Free.exe");
        private static readonly string ExportsRoot = Path.Combine(AssetRipperDir, "Exports");

        private const string DownloadUrl =
            "https://github.com/AssetRipper/AssetRipper/releases/download/1.3.14/AssetRipper_win_x64.zip";

        // Populated by VRCALoader before Open()
        public static string[] BundlePaths = Array.Empty<string>();

        private int _selectedIndex = -1;
        private string _bundlePath = "";
        private string _currentExportDir = "";
        private Vector2 _scroll;
        private string _status = "";
        private bool _busy;

        private readonly List<ControllerEntry> _entries = new List<ControllerEntry>();

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

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("AssetRipper Controller Extraction", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Unpack a VRCA bundle through AssetRipper and list every " +
                "AnimatorController found in the export.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(6);

            // Bundle selection — populated from VRCALoader slots
            EditorGUILayout.LabelField("Bundle from VRCALoader Slots", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            var names = BundlePaths.Length > 0
                ? BundlePaths.Select(p => Path.GetFileName(p)).ToArray()
                : new[] { "(no slots — use Browse)" };

            if (_selectedIndex < 0 || _selectedIndex >= BundlePaths.Length)
            {
                _selectedIndex = BundlePaths.Length > 0 ? 0 : -1;
                if (_selectedIndex >= 0)
                    _bundlePath = BundlePaths[_selectedIndex];
            }

            var newIdx = EditorGUILayout.Popup("Source", _selectedIndex, names);
            if (newIdx != _selectedIndex && newIdx < BundlePaths.Length)
            {
                _selectedIndex = newIdx;
                _bundlePath = BundlePaths[_selectedIndex];
            }

            if (GUILayout.Button("Browse", GUILayout.Width(64)))
            {
                var p = EditorUtility.OpenFilePanel("Select VRCA / VRCW", "", "");
                if (!string.IsNullOrEmpty(p))
                {
                    _bundlePath = p;
                    _selectedIndex = -1;
                }
            }
            EditorGUILayout.EndHorizontal();

            // Action buttons
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !_busy && File.Exists(_bundlePath);
            if (GUILayout.Button("Extract Bundle", GUILayout.Height(28)))
                Extract();
            GUI.enabled = true;

            if (GUILayout.Button("Scan Exports Folder", GUILayout.Height(28)))
                ScanExports();
            EditorGUILayout.EndHorizontal();

            if (_busy)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Running AssetRipper... please wait.", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.Space(4);

            // Controller list
            if (_entries.Count > 0)
            {
                EditorGUILayout.LabelField($"Controllers found: {_entries.Count}", EditorStyles.miniBoldLabel);
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUI.skin.box);
                foreach (var e in _entries)
                    DrawEntry(e);
                EditorGUILayout.EndScrollView();
            }

            // Status
            EditorGUILayout.Space(4);
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.None);

            // Footer
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Open Exports Folder", EditorStyles.toolbarButton))
                EditorUtility.RevealInFinder(
                    !string.IsNullOrEmpty(_currentExportDir) && Directory.Exists(_currentExportDir)
                        ? _currentExportDir : ExportsRoot);
            if (GUILayout.Button("Clear Exports", EditorStyles.toolbarButton))
                ClearExports();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEntry(ControllerEntry e)
        {
            EditorGUILayout.BeginHorizontal();

            var icon = AssetPreview.GetMiniTypeThumbnail(typeof(UnityEditor.Animations.AnimatorController));
            if (icon != null) GUILayout.Label(icon, GUILayout.Width(18), GUILayout.Height(18));

            EditorGUILayout.LabelField(e.fileName, EditorStyles.boldLabel);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Reveal", EditorStyles.miniButton, GUILayout.Width(52)))
                EditorUtility.RevealInFinder(e.filePath);

            if (GUILayout.Button("Copy Path", EditorStyles.miniButton, GUILayout.Width(72)))
            {
                GUIUtility.systemCopyBuffer = e.filePath;
                _status = $"Copied: {e.filePath}";
            }

            EditorGUILayout.EndHorizontal();
        }

        // ── Extraction pipeline (coroutine-driven) ──────────

        private IEnumerator _extractRoutine;

        private void Extract()
        {
            if (_busy) return;
            if (!File.Exists(_bundlePath))
            {
                _status = "Bundle file not found.";
                return;
            }

            PrepareExportDir();
            _busy = true;
            _entries.Clear();
            _extractRoutine = ExtractRoutine();
            EditorApplication.update += PumpExtract;
            Repaint();
        }

        private void PumpExtract()
        {
            // Guard against domain reload / editor shutdown
            if (_extractRoutine == null || !this)
            {
                EditorApplication.update -= PumpExtract;
                _extractRoutine = null;
                _busy = false;
                return;
            }

            try
            {
                if (!_extractRoutine.MoveNext())
                {
                    EditorApplication.update -= PumpExtract;
                    _extractRoutine = null;
                    _busy = false;
                }
            }
            catch (Exception e)
            {
                EditorApplication.update -= PumpExtract;
                _extractRoutine = null;
                _busy = false;
                _status = $"Extraction failed: {e.Message}";
                UnityEngine.Debug.LogError($"[ControllerExtract] {e}");
            }
            Repaint();
        }

        private IEnumerator ExtractRoutine()
        {
            // ── Ensure AssetRipper exists ──
            if (!File.Exists(AssetRipperExe))
            {
                if (!EditorUtility.DisplayDialog(
                        "AssetRipper Required",
                        "AssetRipper is not installed. Download ~120 MB from GitHub?\n\n(You only need to do this once.)",
                        "Download", "Cancel"))
                {
                    _status = "AssetRipper is required. Download cancelled.";
                    yield break;
                }

                _status = "Downloading AssetRipper...";
                yield return null;

                var zipPath = Path.Combine(AssetRipperDir, "AssetRipper_win_x64.zip");
                if (!Directory.Exists(AssetRipperDir))
                    Directory.CreateDirectory(AssetRipperDir);

                using (var req = UnityWebRequest.Get(DownloadUrl))
                {
                    var dh = new DownloadHandlerFile(zipPath);
                    dh.removeFileOnAbort = true;
                    req.downloadHandler = dh;
                    var op = req.SendWebRequest();
                    while (!op.isDone) yield return null;

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        _status = $"Download failed: {req.error}";
                        yield break;
                    }
                }

                _status = "Extracting AssetRipper...";
                yield return null;

                ZipFile.ExtractToDirectory(zipPath, AssetRipperDir, true);
                File.Delete(zipPath);

                if (!File.Exists(AssetRipperExe))
                {
                    _status = "Extraction complete but exe not found. Check zip structure.";
                    yield break;
                }
            }

            // ── Launch AssetRipper (web-based, user must export manually) ──
            int port = 51337;
            _status = "Launching AssetRipper...";
            yield return null;

            var psi = new ProcessStartInfo
            {
                FileName = AssetRipperExe,
                Arguments = $"--headless true --port {port}",
                UseShellExecute = true,
            };

            Process.Start(psi);

            // Give it a moment then open browser
            yield return new WaitForSecondsRealtime(2f);
            Application.OpenURL($"http://localhost:{port}");

            _status = "AssetRipper opened in browser. Drag the bundle file into it,\n" +
                       $"set export path to:\n{_currentExportDir}\n" +
                       "then click Export. Come back here and click 'Scan' when done.";
            yield break;
        }

        private void PrepareExportDir()
        {
            if (!Directory.Exists(ExportsRoot))
                Directory.CreateDirectory(ExportsRoot);

            var baseName = Path.GetFileNameWithoutExtension(_bundlePath);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _currentExportDir = Path.Combine(ExportsRoot, $"{baseName}_{timestamp}");
            Directory.CreateDirectory(_currentExportDir);
        }

        private void ScanExports()
        {
            _entries.Clear();

            if (string.IsNullOrEmpty(_currentExportDir) || !Directory.Exists(_currentExportDir))
            {
                _status = "No export directory. Run an extraction first.";
                return;
            }

            // Recursively find .controller files
            var files = Directory.GetFiles(_currentExportDir, "*.controller*", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                _entries.Add(new ControllerEntry
                {
                    filePath = f,
                    fileName = Path.GetFileName(f),
                });
            }

            _status = _entries.Count == 0
                ? "No .controller files found in export."
                : $"Found {_entries.Count} controller(s).";
        }

        private void PostProcessExports()
        {
            if (string.IsNullOrEmpty(_currentExportDir) || !Directory.Exists(_currentExportDir)) return;

            // Rename Shader/ folder → .Shader so Unity ignores it
            RenameDir(_currentExportDir, "Shader", ".Shader");
            RenameDir(_currentExportDir, "shader", ".shader");

            // Rename Scripts/ folder → .Scripts so Unity ignores it
            RenameDir(_currentExportDir, "Scripts", ".Scripts");
            RenameDir(_currentExportDir, "scripts", ".scripts");

            AssetDatabase.Refresh();
        }

        private static void RenameDir(string root, string name, string newName)
        {
            foreach (var d in Directory.GetDirectories(root, name, SearchOption.AllDirectories))
            {
                var parent = Path.GetDirectoryName(d);
                var target = Path.Combine(parent, newName);
                if (!Directory.Exists(target))
                    Directory.Move(d, target);
            }
        }

        private void ClearExports()
        {
            if (!Directory.Exists(ExportsRoot)) return;
            if (!EditorUtility.DisplayDialog("Clear Exports",
                    "Delete every extraction under Exports/?", "Delete", "Cancel"))
                return;

            Directory.Delete(ExportsRoot, true);
            _currentExportDir = "";
            _entries.Clear();
            _status = "All exports cleared.";
            AssetDatabase.Refresh();
        }

        // ── Helpers ───────────────────────────────────────

    }
}
#endif
