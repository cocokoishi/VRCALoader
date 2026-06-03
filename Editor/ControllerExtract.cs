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
        private static readonly string ExportsDir = Path.Combine(AssetRipperDir, "Exports");

        private const string DownloadUrl =
            "https://github.com/AssetRipper/AssetRipper/releases/download/1.3.14/AssetRipper_win_x64.zip";

        // Populated by VRCALoader before Open()
        public static string[] BundlePaths = Array.Empty<string>();

        private int _selectedIndex = -1;
        private string _bundlePath = "";
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
                EditorUtility.RevealInFinder(ExportsDir);
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

        // ── Extraction pipeline ───────────────────────────

        private void Extract()
        {
            if (_busy) return;
            if (!File.Exists(_bundlePath))
            {
                _status = "Bundle file not found.";
                return;
            }
            // Check / auto-download AssetRipper on first use
            if (!File.Exists(AssetRipperExe))
            {
                if (!EditorUtility.DisplayDialog(
                        "AssetRipper Required",
                        "AssetRipper is not installed. Download ~120 MB from GitHub?\n\n" +
                        "(You only need to do this once.)",
                        "Download", "Cancel"))
                {
                    _status = "AssetRipper is required for extraction. Download cancelled.";
                    return;
                }
                _status = "Downloading AssetRipper...";
                Repaint();
                EditorApplication.delayCall += () => DownloadAndExtract();
                return;
            }

            DoExtract();
        }

        private void DoExtract()
        {
            // Clean or create exports dir
            PrepareExportDir();

            _busy = true;
            _status = "Starting AssetRipper...";

            EditorApplication.delayCall += () =>
            {
                try { RunAssetRipper(); }
                catch (Exception ex)
                {
                    _status = $"AssetRipper failed: {ex.Message}";
                    _busy = false;
                    Repaint();
                }
            };
        }

        private void DownloadAndExtract()
        {
            try
            {
                var zipPath = Path.Combine(AssetRipperDir, "AssetRipper_win_x64.zip");

                // Download
                _status = "Downloading AssetRipper...";
                Repaint();

                using (var client = new System.Net.WebClient())
                {
                    client.DownloadFile(DownloadUrl, zipPath);
                }

                if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
                {
                    _status = "Download failed — file is empty or missing.";
                    Repaint();
                    return;
                }

                // Extract
                _status = "Extracting AssetRipper...";
                Repaint();

                ZipFile.ExtractToDirectory(zipPath, AssetRipperDir, true);

                // Clean up zip
                File.Delete(zipPath);

                if (!File.Exists(AssetRipperExe))
                {
                    _status = "Extraction complete but exe not found. Check the zip structure.";
                    Repaint();
                    return;
                }

                _status = "AssetRipper installed. Starting extraction...";
                Repaint();
                DoExtract();
            }
            catch (Exception e)
            {
                _status = $"Download/extract failed: {e.Message}";
                Repaint();
            }
        }

        private static void PrepareExportDir()
        {
            if (Directory.Exists(ExportsDir))
            {
                // Delete everything inside
                var di = new DirectoryInfo(ExportsDir);
                foreach (var f in di.GetFiles()) f.Delete();
                foreach (var d in di.GetDirectories()) d.Delete(true);
            }
            else
            {
                Directory.CreateDirectory(ExportsDir);
            }
        }

        private void RunAssetRipper()
        {
            // Find a free port
            int port = FindFreePort();

            var psi = new ProcessStartInfo
            {
                FileName = AssetRipperExe,
                Arguments = $"--headless true --port {port}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _status = "Failed to start AssetRipper process.";
                _busy = false;
                Repaint();
                return;
            }

            // Wait for the server to come up
            var baseUrl = $"http://localhost:{port}";
            if (!WaitForServer(baseUrl, 30))
            {
                proc.Kill();
                _status = "AssetRipper server didn't start within 30 seconds.";
                _busy = false;
                Repaint();
                return;
            }

            _status = "AssetRipper server running. Loading bundle...";
            Repaint();

            // Load the bundle file
            var loadOk = PostJson(baseUrl + "/api/load-file",
                $"{{\"path\":\"{EscapeJson(_bundlePath)}\"}}");

            if (!loadOk)
            {
                proc.Kill();
                _status = "Failed to send load-file command.";
                _busy = false;
                Repaint();
                return;
            }

            // Wait for file to load
            System.Threading.Thread.Sleep(3000);

            // Export
            _status = "Exporting...";
            Repaint();

            var exportOk = PostJson(baseUrl + "/api/command/export",
                $"{{\"exportPath\":\"{EscapeJson(ExportsDir)}\",\"exportType\":0}}");

            if (!exportOk)
            {
                // Try legacy endpoint
                exportOk = PostJson(baseUrl + "/api/export",
                    $"{{\"path\":\"{EscapeJson(ExportsDir)}\"}}");
            }

            // Wait for export to complete
            System.Threading.Thread.Sleep(5000);

            // Kill the process
            try { proc.Kill(); } catch { /* already exited */ }

            // Post-process
            _status = "Post-processing exported files...";
            Repaint();
            PostProcessExports();

            // Scan
            ScanExports();

            _busy = false;
            _status = "Extraction complete.";
            Repaint();
        }

        private static bool WaitForServer(string url, int timeoutSec)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSec)
            {
                try
                {
                    using var req = UnityWebRequest.Get(url);
                    req.timeout = 2;
                    var op = req.SendWebRequest();
                    // wait synchronously (we're on delayCall, not main thread)
                    var start = Time.realtimeSinceStartup;
                    while (!op.isDone && Time.realtimeSinceStartup - start < 3f)
                        System.Threading.Thread.Sleep(100);

                    if (req.result == UnityWebRequest.Result.Success)
                        return true;
                }
                catch
                {
                    // not up yet
                }
                System.Threading.Thread.Sleep(500);
            }
            return false;
        }

        private static bool PostJson(string url, string json)
        {
            try
            {
                using var req = new UnityWebRequest(url, "POST");
                var body = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 10;

                var op = req.SendWebRequest();
                var start = Time.realtimeSinceStartup;
                while (!op.isDone && Time.realtimeSinceStartup - start < 12f)
                    System.Threading.Thread.Sleep(100);

                bool ok = req.result == UnityWebRequest.Result.Success;
                if (!ok)
                    UnityEngine.Debug.Log($"[ControllerExtract] POST {url} → {req.responseCode} {req.error}");
                else
                    UnityEngine.Debug.Log($"[ControllerExtract] POST {url} → OK {req.downloadHandler.text}");
                return ok;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[ControllerExtract] POST {url} failed: {e.Message}");
                return false;
            }
        }

        private void ScanExports()
        {
            _entries.Clear();

            if (!Directory.Exists(ExportsDir))
            {
                _status = "Exports directory not found.";
                return;
            }

            // Recursively find .controller files
            var files = Directory.GetFiles(ExportsDir, "*.controller*", SearchOption.AllDirectories);
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

        private static void PostProcessExports()
        {
            if (!Directory.Exists(ExportsDir)) return;

            // Rename .shader → .shader.txt so Unity doesn't try to compile them
            var shaderFiles = Directory.GetFiles(ExportsDir, "*.shader", SearchOption.AllDirectories);
            foreach (var f in shaderFiles)
            {
                if (f.EndsWith(".txt")) continue;
                var newPath = f + ".txt";
                File.Move(f, newPath);
                UnityEngine.Debug.Log($"[ControllerExtract] Renamed: {Path.GetFileName(f)} → {Path.GetFileName(newPath)}");
            }

            // Rename .cs → .cs.txt so Unity doesn't try to compile them
            var scriptFiles = Directory.GetFiles(ExportsDir, "*.cs", SearchOption.AllDirectories);
            foreach (var f in scriptFiles)
            {
                if (f.EndsWith(".txt")) continue;
                var newPath = f + ".txt";
                File.Move(f, newPath);
                UnityEngine.Debug.Log($"[ControllerExtract] Renamed: {Path.GetFileName(f)} → {Path.GetFileName(newPath)}");
            }

            // Rename .cginc → .cginc.txt
            var cgincFiles = Directory.GetFiles(ExportsDir, "*.cginc", SearchOption.AllDirectories);
            foreach (var f in cgincFiles)
            {
                if (f.EndsWith(".txt")) continue;
                File.Move(f, f + ".txt");
            }

            // Rename .hlsl → .hlsl.txt
            var hlslFiles = Directory.GetFiles(ExportsDir, "*.hlsl", SearchOption.AllDirectories);
            foreach (var f in hlslFiles)
            {
                if (f.EndsWith(".txt")) continue;
                File.Move(f, f + ".txt");
            }

            AssetDatabase.Refresh();
        }

        private void ClearExports()
        {
            if (!Directory.Exists(ExportsDir)) return;
            if (!EditorUtility.DisplayDialog("Clear Exports",
                    "Delete everything in the Exports directory?", "Delete", "Cancel"))
                return;

            PrepareExportDir();
            _entries.Clear();
            _status = "Exports cleared.";
            AssetDatabase.Refresh();
        }

        // ── Helpers ───────────────────────────────────────

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static int FindFreePort()
        {
            // Just use a high port — AssetRipper will pick one if this is taken
            return 51337;
        }
    }
}
#endif
