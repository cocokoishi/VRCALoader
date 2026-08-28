#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Cocokoishi.VRCALoader
{
    /// <summary>
    /// Inspired by FACS Utilities and independently implemented under clean-room principles.
    /// No source code from FACS Utilities is copied here. Use only for lawful recovery of content
    /// that the user owns or is explicitly authorized to restore; never for unauthorized or illegal use.
    /// </summary>
    public sealed class ReferenceRemapperWindow : EditorWindow
    {
        private static readonly string ExportsRoot = Path.GetFullPath(
            Path.Combine(Application.dataPath, "VRCALoader/Exports"));

        private static readonly HashSet<string> YamlExtensions = new HashSet<string>(
            new[] { ".anim", ".asset", ".blendtree", ".controller", ".mask", ".mat", ".overridecontroller",
                ".playable", ".prefab", ".rendertexture", ".state", ".unity" },
            StringComparer.OrdinalIgnoreCase);

        private static readonly Regex GuidPattern = new Regex(
            @"\bguid\s*:\s*([0-9a-f]{32})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ShaderDeclaration = new Regex(
            @"^\s*Shader\s+""([^""]+)""", RegexOptions.CultureInvariant);
        private static readonly Regex NamespaceDeclaration = new Regex(
            @"\bnamespace\s+([A-Za-z_][\w.]*)\s*[;{]", RegexOptions.CultureInvariant);
        private static readonly Regex ClassDeclaration = new Regex(
            @"\bclass\s+([A-Za-z_]\w*)", RegexOptions.CultureInvariant);

        private enum ReferenceKind
        {
            Shader,
            Script,
            PostProcessResources
        }

        private sealed class SerializedReference
        {
            public long fileId;
            public string guid;
            public int type;

            public override string ToString()
            {
                return $"{{fileID: {fileId}, guid: {guid}, type: {type}}}";
            }
        }

        private sealed class MappingRow
        {
            public ReferenceKind kind;
            public string placeholderGuid;
            public string sourcePath;
            public string label;
            public Shader shader;
            public MonoScript script;
            public UnityEngine.Object resource;
            public SerializedReference target;
            public readonly List<MaterialUsage> materialUsages = new List<MaterialUsage>();
            public bool materialUsagesFoldout;
        }

        private sealed class MaterialUsage
        {
            public string path;
            public string label;
            public bool ignored;
            public bool applied;
        }

        private struct RewriteStats
        {
            public int shaderReferences;
            public int scriptReferences;
            public int postProcessResourceReferences;

            public int Total => shaderReferences + scriptReferences + postProcessResourceReferences;
        }

        private readonly List<MappingRow> _mappings = new List<MappingRow>();
        private readonly List<string> _yamlFiles = new List<string>();
        private string[] _exportFolders = Array.Empty<string>();
        private string _selectedRoot = "";
        private int _folderIndex;
        private Vector2 _scroll;
        private bool _shaderFoldout = true;
        private bool _scriptFoldout = true;
        private bool _postProcessResourcesFoldout = true;
        private string _materialSearch = "";
        private bool _keepBackups = false;
        private bool _analyzed;
        private int _plannedReferences;
        private string _message = "";
        private MessageType _messageType = MessageType.Info;

        public static void Open()
        {
            var window = GetWindow<ReferenceRemapperWindow>("Reference Remapper");
            window.minSize = new Vector2(620, 460);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshExportFolders();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Reference Remapper", new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 });
            EditorGUILayout.LabelField(
                "Reconnect AssetRipper placeholder Shader, MonoScript, and post-processing resource GUIDs to matching assets installed in this project.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                "Inspired by FACS Utilities; independently implemented under clean-room principles without copied source code.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6);

            DrawFolderPicker();
            EditorGUILayout.Space(4);
            DrawActions();

            if (!string.IsNullOrEmpty(_message))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_message, _messageType);
            }

            if (!_analyzed) return;

            EditorGUILayout.Space(6);
            var resolved = _mappings.Count(m => m.target != null);
            EditorGUILayout.LabelField(
                $"{resolved}/{_mappings.Count} mappings resolved  ·  {_yamlFiles.Count} YAML files  ·  {_plannedReferences} references found",
                EditorStyles.boldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawMappingGroup(ReferenceKind.Shader, ref _shaderFoldout);
            EditorGUILayout.Space(4);
            DrawMappingGroup(ReferenceKind.Script, ref _scriptFoldout);
            EditorGUILayout.Space(4);
            DrawMappingGroup(ReferenceKind.PostProcessResources, ref _postProcessResourcesFoldout);
            EditorGUILayout.EndScrollView();
        }

        private void DrawFolderPicker()
        {
            EditorGUILayout.BeginHorizontal();
            if (_exportFolders.Length == 0)
            {
                EditorGUILayout.LabelField("Export Folder", GUILayout.Width(84));
                EditorGUILayout.LabelField("No exports found", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                var names = _exportFolders.Select(Path.GetFileName).ToArray();
                var next = EditorGUILayout.Popup("Export Folder", Mathf.Clamp(_folderIndex, 0, names.Length - 1), names);
                if (next != _folderIndex || string.IsNullOrEmpty(_selectedRoot)) SelectFolder(next);
            }

            if (GUILayout.Button("Browse", GUILayout.Width(62)))
            {
                var chosen = EditorUtility.OpenFolderPanel("Select AssetRipper export", ExportsRoot, "");
                if (!string.IsNullOrEmpty(chosen))
                {
                    _selectedRoot = Path.GetFullPath(chosen);
                    RefreshExportFolders();
                    ResetAnalysis();
                }
            }
            if (GUILayout.Button("Refresh", GUILayout.Width(62))) RefreshExportFolders();
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_selectedRoot))
                EditorGUILayout.SelectableLabel(_selectedRoot, EditorStyles.miniLabel, GUILayout.Height(17));
        }

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(!Directory.Exists(_selectedRoot));
            if (GUILayout.Button("Analyze References", GUILayout.Height(28), GUILayout.Width(138))) AnalyzeSelectedExport();
            EditorGUI.EndDisabledGroup();

            // _keepBackups = EditorGUILayout.ToggleLeft("Keep .vrcaloader.bak files", _keepBackups, GUILayout.Width(184));
            GUILayout.FlexibleSpace();

            var canApplyShaders = _analyzed && _yamlFiles.Count > 0 &&
                                  _mappings.Any(m => m.kind == ReferenceKind.Shader && m.target != null);
            var canApplyScripts = _analyzed && _yamlFiles.Count > 0 &&
                                  _mappings.Any(m => m.kind == ReferenceKind.Script && m.target != null);
            var canApplyPostProcessResources = _analyzed && _yamlFiles.Count > 0 &&
                                               _mappings.Any(m => m.kind == ReferenceKind.PostProcessResources &&
                                                                  m.target != null);

            EditorGUI.BeginDisabledGroup(!canApplyShaders);
            if (GUILayout.Button("Apply Shaders", GUILayout.Height(28), GUILayout.Width(104)))
                ApplyRemapping(true, false, false);
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!canApplyScripts);
            if (GUILayout.Button("Apply Scripts", GUILayout.Height(28), GUILayout.Width(100)))
                ApplyRemapping(false, true, false);
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!canApplyPostProcessResources);
            if (GUILayout.Button("Apply PostProcessing", GUILayout.Height(28), GUILayout.Width(140)))
                ApplyRemapping(false, false, true);
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!canApplyShaders && !canApplyScripts && !canApplyPostProcessResources);
            if (GUILayout.Button("Apply All", GUILayout.Height(28), GUILayout.Width(82)))
                ApplyRemapping(true, true, true);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawMappingGroup(ReferenceKind kind, ref bool foldout)
        {
            var rows = _mappings.Where(m => m.kind == kind).ToList();
            var resolved = rows.Count(m => m.target != null);
            var groupLabel = kind == ReferenceKind.PostProcessResources ? "Post-processing Resources" : kind + "s";
            foldout = EditorGUILayout.Foldout(foldout,
                $"{groupLabel} ({resolved} resolved, {rows.Count - resolved} unresolved)", true);
            if (!foldout) return;

            if (kind == ReferenceKind.Shader)
            {
                DrawMaterialSearch();
                foreach (var row in rows)
                {
                    if (!ShaderRowMatchesMaterialSearch(row)) continue;
                    DrawShaderMappingRow(row);
                }
                return;
            }

            foreach (var row in rows)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                var oldColor = GUI.contentColor;
                GUI.contentColor = row.target == null ? new Color(1f, 0.68f, 0.18f) : new Color(0.35f, 0.9f, 0.45f);
                GUILayout.Label(row.target == null ? "●" : "✓", GUILayout.Width(18));
                GUI.contentColor = oldColor;

                var labelWidth = Mathf.Max(180f, position.width * 0.43f);
                EditorGUILayout.LabelField(new GUIContent(row.label, row.sourcePath), GUILayout.Width(labelWidth));

                EditorGUI.BeginChangeCheck();
                if (kind == ReferenceKind.Script)
                {
                    var picked = (MonoScript)EditorGUILayout.ObjectField(row.script, typeof(MonoScript), false);
                    if (EditorGUI.EndChangeCheck()) SetScriptTarget(row, picked);
                }
                else
                {
                    var picked = EditorGUILayout.ObjectField(row.resource, typeof(UnityEngine.Object), false);
                    if (EditorGUI.EndChangeCheck()) SetPostProcessResourcesTarget(row, picked);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawMaterialSearch()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Material Search", GUILayout.Width(96));
            _materialSearch = EditorGUILayout.TextField(_materialSearch,
                GUI.skin.FindStyle("ToolbarSearchTextField") ?? EditorStyles.textField);
            if (!string.IsNullOrEmpty(_materialSearch) && GUILayout.Button("Clear", EditorStyles.toolbarButton,
                    GUILayout.Width(46)))
            {
                _materialSearch = "";
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();
        }

        private bool ShaderRowMatchesMaterialSearch(MappingRow row)
        {
            if (string.IsNullOrWhiteSpace(_materialSearch)) return true;
            var search = _materialSearch.Trim();
            return row.materialUsages.Any(usage => !usage.applied &&
                (usage.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 usage.path.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private void DrawShaderMappingRow(MappingRow row)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            var oldColor = GUI.contentColor;
            GUI.contentColor = row.target == null ? new Color(1f, 0.68f, 0.18f) : new Color(0.35f, 0.9f, 0.45f);
            GUILayout.Label(row.target == null ? "●" : "✓", GUILayout.Width(18));
            GUI.contentColor = oldColor;

            var visibleUsages = GetVisibleMaterialUsages(row);
            var pendingCount = row.materialUsages.Count(usage => !usage.applied);
            var ignoredCount = row.materialUsages.Count(usage => usage.ignored && !usage.applied);
            var usageSummary = ignoredCount > 0
                ? $"{pendingCount} materials, {ignoredCount} ignored"
                : $"{pendingCount} materials";
            var forceExpanded = !string.IsNullOrWhiteSpace(_materialSearch);
            row.materialUsagesFoldout = EditorGUILayout.Foldout(
                row.materialUsagesFoldout || forceExpanded,
                row.label, true);
            EditorGUILayout.LabelField(usageSummary, EditorStyles.miniLabel, GUILayout.Width(145));

            EditorGUI.BeginChangeCheck();
            var picked = (Shader)EditorGUILayout.ObjectField(row.shader, typeof(Shader), false);
            if (EditorGUI.EndChangeCheck()) SetShaderTarget(row, picked);
            EditorGUILayout.EndHorizontal();

            if (row.materialUsagesFoldout || forceExpanded)
            {
                if (visibleUsages.Count == 0)
                {
                    EditorGUILayout.LabelField(
                        string.IsNullOrWhiteSpace(_materialSearch)
                            ? "No pending material references use this placeholder Shader."
                            : "No materials match the current search.",
                        EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    foreach (var usage in visibleUsages) DrawMaterialUsageRow(row, usage);
                }
            }
            EditorGUILayout.EndVertical();
        }

        private List<MaterialUsage> GetVisibleMaterialUsages(MappingRow row)
        {
            var usages = row.materialUsages.Where(usage => !usage.applied);
            if (!string.IsNullOrWhiteSpace(_materialSearch))
            {
                var search = _materialSearch.Trim();
                usages = usages.Where(usage =>
                    usage.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    usage.path.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return usages.OrderBy(usage => usage.label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(usage => usage.path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void DrawMaterialUsageRow(MappingRow shaderRow, MaterialUsage usage)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(28);
            EditorGUILayout.LabelField(new GUIContent(usage.label, usage.path));

            EditorGUI.BeginChangeCheck();
            usage.ignored = GUILayout.Toggle(usage.ignored, "Ignore", "Button", GUILayout.Width(64));
            if (EditorGUI.EndChangeCheck()) MaterialIgnoreChanged();

            EditorGUI.BeginDisabledGroup(usage.ignored || shaderRow.target == null || !File.Exists(usage.path));
            if (GUILayout.Button("Apply", GUILayout.Width(58))) ApplyShaderToMaterial(shaderRow, usage);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        private void AnalyzeSelectedExport()
        {
            ResetAnalysis();
            if (!Directory.Exists(_selectedRoot)) return;

            try
            {
                EditorUtility.DisplayProgressBar("Reference Remapper", "Reading exported YAML references...", 0.05f);
                _yamlFiles.AddRange(CollectYamlFiles(_selectedRoot));
                var scriptMetaPaths = Directory.EnumerateFiles(_selectedRoot, "*.cs.meta", SearchOption.AllDirectories)
                    .ToArray();

                EditorUtility.DisplayProgressBar("Reference Remapper", "Indexing project scripts...", 0.22f);
                var scriptIndex = scriptMetaPaths.Length > 0
                    ? BuildMonoScriptIndex()
                    : new Dictionary<Type, MonoScript>();
                var typeIndex = scriptMetaPaths.Length > 0
                    ? BuildTypeIndex(scriptIndex.Keys)
                    : new Dictionary<string, List<Type>>(StringComparer.Ordinal);

                EditorUtility.DisplayProgressBar("Reference Remapper", "Resolving Shader placeholders...", 0.48f);
                ScanShaderPlaceholders();
                ScanShaderMaterialUsages();

                EditorUtility.DisplayProgressBar("Reference Remapper", "Resolving Script placeholders...", 0.68f);
                ScanScriptPlaceholders(scriptMetaPaths, typeIndex, scriptIndex);

                EditorUtility.DisplayProgressBar("Reference Remapper", "Resolving post-processing resources...", 0.76f);
                ScanPostProcessResourcesPlaceholders();

                EditorUtility.DisplayProgressBar("Reference Remapper", "Inspecting YAML references...", 0.82f);
                BuildLookups(out var shaderLookup, out var scriptLookup, out var postProcessResourcesLookup);
                var planned = CountReferences(_yamlFiles, shaderLookup, scriptLookup, postProcessResourcesLookup);
                _plannedReferences = planned.Total;
                _analyzed = true;

                var unresolved = _mappings.Count(m => m.target == null);
                _message = unresolved == 0
                    ? "Analysis complete. All discovered placeholders have project targets."
                    : $"Analysis complete. {unresolved} placeholders are unresolved; assign targets manually or apply the resolved mappings only.";
                _messageType = unresolved == 0 ? MessageType.Info : MessageType.Warning;
            }
            catch (Exception e)
            {
                _message = "Analysis failed: " + e.Message;
                _messageType = MessageType.Error;
                Debug.LogError("[ReferenceRemapper] " + e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        private void ScanShaderPlaceholders()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var metaPath in Directory.EnumerateFiles(_selectedRoot, "*.shader.meta", SearchOption.AllDirectories))
            {
                var shaderPath = metaPath.Substring(0, metaPath.Length - ".meta".Length);
                var placeholder = ReadMetaGuid(metaPath);
                var shaderName = ReadShaderName(shaderPath);
                if (string.IsNullOrEmpty(placeholder) || string.IsNullOrEmpty(shaderName) || !seen.Add(placeholder)) continue;

                var row = new MappingRow
                {
                    kind = ReferenceKind.Shader,
                    placeholderGuid = placeholder,
                    sourcePath = shaderPath,
                    label = shaderName
                };
                var found = Shader.Find(shaderName);
                if (found != null && !string.Equals(found.name, "Hidden/InternalErrorShader", StringComparison.Ordinal))
                    SetShaderTarget(row, found, false);
                _mappings.Add(row);
            }
        }

        private void ScanScriptPlaceholders(IEnumerable<string> metaPaths, Dictionary<string, List<Type>> typeIndex,
            Dictionary<Type, MonoScript> scriptIndex)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var metaPath in metaPaths)
            {
                var placeholder = ReadMetaGuid(metaPath);
                if (string.IsNullOrEmpty(placeholder) || !seen.Add(placeholder)) continue;

                var container = FindScriptContainer(metaPath);
                var matchedType = MatchExportedType(metaPath, container, typeIndex, out var expectedName);
                var row = new MappingRow
                {
                    kind = ReferenceKind.Script,
                    placeholderGuid = placeholder,
                    sourcePath = metaPath,
                    label = expectedName
                };

                if (matchedType != null)
                {
                    if (!scriptIndex.TryGetValue(matchedType, out var monoScript))
                        monoScript = CreateMonoScriptHandle(matchedType);
                    SetScriptTarget(row, monoScript, false);
                }
                _mappings.Add(row);
            }
        }

        private void ScanShaderMaterialUsages()
        {
            var shaderRows = _mappings
                .Where(row => row.kind == ReferenceKind.Shader)
                .ToDictionary(row => row.placeholderGuid, row => row, StringComparer.OrdinalIgnoreCase);
            if (shaderRows.Count == 0) return;

            foreach (var materialPath in _yamlFiles.Where(path =>
                         path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    foreach (var line in File.ReadLines(materialPath))
                    {
                        var trimmed = line.TrimStart(' ', '\t');
                        if (!trimmed.StartsWith("m_Shader: {", StringComparison.Ordinal) ||
                            !TryExtractGuid(trimmed, out var shaderGuid) ||
                            !shaderRows.TryGetValue(shaderGuid, out var shaderRow)) continue;

                        shaderRow.materialUsages.Add(new MaterialUsage
                        {
                            path = materialPath,
                            label = Path.GetFileNameWithoutExtension(materialPath)
                        });
                        break;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ReferenceRemapper] Could not inspect material {materialPath}: {e.Message}");
                }
            }
        }

        private void ScanPostProcessResourcesPlaceholders()
        {
            var installedResource = FindInstalledPostProcessResources();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assetPath in _yamlFiles.Where(path =>
                         path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)))
            {
                if (!IsExportedPostProcessResources(assetPath)) continue;

                var placeholder = ReadMetaGuid(assetPath + ".meta");
                if (string.IsNullOrEmpty(placeholder) || !seen.Add(placeholder)) continue;

                var row = new MappingRow
                {
                    kind = ReferenceKind.PostProcessResources,
                    placeholderGuid = placeholder,
                    sourcePath = assetPath,
                    label = "PostProcessResources"
                };
                SetPostProcessResourcesTarget(row, installedResource, false);
                _mappings.Add(row);
            }
        }

        private UnityEngine.Object FindInstalledPostProcessResources()
        {
            const string preferredPath =
                "Packages/com.unity.postprocessing/PostProcessing/PostProcessResources.asset";
            var candidates = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.EndsWith("/PostProcessResources.asset", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => string.Equals(path, preferredPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (var candidatePath in candidates)
            {
                var candidate = AssetDatabase.LoadMainAssetAtPath(candidatePath);
                if (!IsPostProcessResourcesObject(candidate) || AssetIsInsideExport(candidate)) continue;
                return candidate;
            }
            return null;
        }

        private static bool IsExportedPostProcessResources(string assetPath)
        {
            var hasExpectedName = false;
            var hasShaders = false;
            var hasComputeShaders = false;
            try
            {
                foreach (var line in File.ReadLines(assetPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("m_Name:", StringComparison.Ordinal))
                    {
                        var value = trimmed.Substring("m_Name:".Length).Trim().Trim('"', '\'');
                        hasExpectedName = string.Equals(value, "PostProcessResources", StringComparison.Ordinal);
                    }
                    else if (string.Equals(trimmed, "shaders:", StringComparison.Ordinal))
                    {
                        hasShaders = true;
                    }
                    else if (string.Equals(trimmed, "computeShaders:", StringComparison.Ordinal))
                    {
                        hasComputeShaders = true;
                    }

                    if (hasExpectedName && hasShaders && hasComputeShaders) return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ReferenceRemapper] Could not inspect post-processing resource {assetPath}: {e.Message}");
            }
            return false;
        }

        private static bool IsPostProcessResourcesObject(UnityEngine.Object asset)
        {
            return asset != null && string.Equals(asset.GetType().FullName,
                "UnityEngine.Rendering.PostProcessing.PostProcessResources", StringComparison.Ordinal);
        }

        private static Dictionary<string, List<Type>> BuildTypeIndex(IEnumerable<Type> importedScriptTypes)
        {
            var all = new HashSet<Type>();
            foreach (var type in importedScriptTypes) all.Add(type);
            foreach (var type in TypeCache.GetTypesDerivedFrom<MonoBehaviour>()) all.Add(type);
            foreach (var type in TypeCache.GetTypesDerivedFrom<ScriptableObject>()) all.Add(type);

            var result = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
            foreach (var type in all)
            {
                if (type == null || type.IsAbstract || type.ContainsGenericParameters || string.IsNullOrEmpty(type.FullName)) continue;
                if (!result.TryGetValue(type.FullName, out var matches))
                {
                    matches = new List<Type>();
                    result[type.FullName] = matches;
                }
                matches.Add(type);
            }
            return result;
        }

        private static Dictionary<Type, MonoScript> BuildMonoScriptIndex()
        {
            var result = new Dictionary<Type, MonoScript>();

            try
            {
                AddMonoScriptsToIndex(MonoImporter.GetAllRuntimeMonoScripts(), result);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ReferenceRemapper] Could not read Unity's runtime script index: {e.Message}");
            }

            foreach (var assetPath in AssetDatabase.GetAllAssetPaths())
            {
                if (!assetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                AddMonoScriptsToIndex(AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<MonoScript>(), result);
            }

            foreach (var guid in AssetDatabase.FindAssets("t:MonoScript"))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                AddMonoScriptsToIndex(AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<MonoScript>(), result);
            }
            return result;
        }

        private static void AddMonoScriptsToIndex(IEnumerable<MonoScript> scripts,
            Dictionary<Type, MonoScript> index)
        {
            if (scripts == null) return;
            foreach (var script in scripts)
            {
                if (script == null) continue;
                try
                {
                    var type = script.GetClass();
                    if (type != null && !index.ContainsKey(type)) index[type] = script;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ReferenceRemapper] Could not inspect MonoScript {script.name}: {e.Message}");
                }
            }
        }

        private static Type MatchExportedType(string metaPath, string container,
            Dictionary<string, List<Type>> typeIndex, out string expectedName)
        {
            var sourcePath = metaPath.Substring(0, metaPath.Length - ".meta".Length);
            var candidates = new List<string>();
            var declared = ReadDeclaredType(sourcePath);
            if (!string.IsNullOrEmpty(declared)) candidates.Add(declared);

            var relative = RelativePath(container, metaPath).Replace('\\', '/');
            if (relative.EndsWith(".cs.meta", StringComparison.OrdinalIgnoreCase))
                relative = relative.Substring(0, relative.Length - ".cs.meta".Length);
            var parts = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var assemblyName = parts.Length > 1 ? parts[0] : null;
            for (var start = 0; start < parts.Length; start++)
            {
                var candidate = string.Join(".", parts.Skip(start));
                if (!candidates.Contains(candidate)) candidates.Add(candidate);
            }

            foreach (var candidate in candidates)
            {
                if (!typeIndex.TryGetValue(candidate, out var matches) || matches.Count == 0) continue;
                expectedName = candidate;
                return matches.FirstOrDefault(type => string.Equals(type.Assembly.GetName().Name, assemblyName,
                           StringComparison.Ordinal)) ?? matches[0];
            }

            expectedName = parts.Length > 1
                ? string.Join(".", parts.Skip(1))
                : candidates.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(sourcePath);
            return null;
        }

        private static MonoScript CreateMonoScriptHandle(Type type)
        {
            GameObject temporaryObject = null;
            ScriptableObject temporaryAsset = null;
            try
            {
                if (typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    temporaryObject = new GameObject("ReferenceRemapper") { hideFlags = HideFlags.HideAndDontSave };
                    temporaryObject.SetActive(false);
                    var component = temporaryObject.AddComponent(type) as MonoBehaviour;
                    return component == null ? null : ReadAttachedMonoScript(component) ?? MonoScript.FromMonoBehaviour(component);
                }

                if (typeof(ScriptableObject).IsAssignableFrom(type))
                {
                    temporaryAsset = ScriptableObject.CreateInstance(type);
                    temporaryAsset.hideFlags = HideFlags.HideAndDontSave;
                    return ReadAttachedMonoScript(temporaryAsset) ?? MonoScript.FromScriptableObject(temporaryAsset);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ReferenceRemapper] Could not resolve MonoScript for {type.FullName}: {e.Message}");
            }
            finally
            {
                if (temporaryAsset != null) UnityEngine.Object.DestroyImmediate(temporaryAsset);
                if (temporaryObject != null) UnityEngine.Object.DestroyImmediate(temporaryObject);
            }
            return null;
        }

        private static MonoScript ReadAttachedMonoScript(UnityEngine.Object instance)
        {
            if (instance == null) return null;
            var serialized = new SerializedObject(instance);
            return serialized.FindProperty("m_Script")?.objectReferenceValue as MonoScript;
        }

        private void SetShaderTarget(MappingRow row, Shader shader, bool updateMessage = true)
        {
            row.shader = shader;
            row.target = shader == null ? null : CreateReference(shader, ShaderReferenceType(shader));
            if (row.target != null && (string.Equals(row.target.guid, row.placeholderGuid, StringComparison.OrdinalIgnoreCase) ||
                                       AssetIsInsideExport(shader)))
            {
                row.shader = null;
                row.target = null;
            }
            if (updateMessage) MappingChanged();
        }

        private void SetScriptTarget(MappingRow row, MonoScript script, bool updateMessage = true)
        {
            row.script = script;
            row.target = script == null ? null : CreateReference(script, 3);
            if (row.target != null && (string.Equals(row.target.guid, row.placeholderGuid, StringComparison.OrdinalIgnoreCase) ||
                                       AssetIsInsideExport(script)))
            {
                row.script = null;
                row.target = null;
            }
            if (updateMessage) MappingChanged();
        }

        private void SetPostProcessResourcesTarget(MappingRow row, UnityEngine.Object resource,
            bool updateMessage = true)
        {
            row.resource = IsPostProcessResourcesObject(resource) ? resource : null;
            row.target = row.resource == null ? null : CreateReference(row.resource, 2);
            if (row.target != null &&
                (string.Equals(row.target.guid, row.placeholderGuid, StringComparison.OrdinalIgnoreCase) ||
                 AssetIsInsideExport(row.resource)))
            {
                row.resource = null;
                row.target = null;
            }
            if (updateMessage) MappingChanged();
        }

        private void MappingChanged()
        {
            _message = "Mapping updated. The selected Apply action will rescan the YAML files before writing.";
            _messageType = MessageType.Info;
            Repaint();
        }

        private void MaterialIgnoreChanged()
        {
            _message = "Material Ignore settings updated. Ignored materials will be skipped by Apply Shaders and Apply All.";
            _messageType = MessageType.Info;
            Repaint();
        }

        private HashSet<string> GetIgnoredShaderMaterialPaths()
        {
            return new HashSet<string>(_mappings
                .Where(row => row.kind == ReferenceKind.Shader)
                .SelectMany(row => row.materialUsages)
                .Where(usage => usage.ignored && !usage.applied)
                .Select(usage => usage.path), StringComparer.OrdinalIgnoreCase);
        }

        private void ApplyShaderToMaterial(MappingRow shaderRow, MaterialUsage usage)
        {
            if (shaderRow == null || shaderRow.target == null || usage == null || usage.ignored ||
                !File.Exists(usage.path)) return;

            var shaders = new Dictionary<string, SerializedReference>(StringComparer.OrdinalIgnoreCase)
            {
                [shaderRow.placeholderGuid] = shaderRow.target
            };
            var scripts = new Dictionary<string, SerializedReference>(StringComparer.OrdinalIgnoreCase);
            var postProcessResources = new Dictionary<string, SerializedReference>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var stats = RewriteYamlFile(usage.path, shaders, scripts, postProcessResources, _keepBackups,
                    out var changed);
                if (changed) AssetDatabase.Refresh();

                if (stats.shaderReferences > 0)
                {
                    usage.applied = true;
                    usage.ignored = false;
                    _plannedReferences = Math.Max(0, _plannedReferences - stats.shaderReferences);
                    _message = $"Applied Shader remapping to {usage.label}; replaced {stats.shaderReferences} reference" +
                               (stats.shaderReferences == 1 ? "." : "s.");
                    _messageType = MessageType.Info;
                }
                else
                {
                    usage.applied = !MaterialUsesPlaceholderShader(usage.path, shaderRow.placeholderGuid);
                    _message = $"No matching Shader placeholder reference remains in {usage.label}.";
                    _messageType = MessageType.Info;
                }
            }
            catch (Exception e)
            {
                _message = $"Could not remap {usage.label}: {e.Message}";
                _messageType = MessageType.Error;
                Debug.LogError($"[ReferenceRemapper] Could not rewrite material {usage.path}: {e}");
            }
            Repaint();
        }

        private static bool MaterialUsesPlaceholderShader(string materialPath, string placeholderGuid)
        {
            if (!File.Exists(materialPath) || string.IsNullOrEmpty(placeholderGuid)) return false;
            try
            {
                foreach (var line in File.ReadLines(materialPath))
                {
                    var trimmed = line.TrimStart(' ', '\t');
                    if (trimmed.StartsWith("m_Shader: {", StringComparison.Ordinal) &&
                        TryExtractGuid(trimmed, out var shaderGuid) &&
                        string.Equals(shaderGuid, placeholderGuid, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ReferenceRemapper] Could not inspect material {materialPath}: {e.Message}");
            }
            return false;
        }

        private void UpdateShaderMaterialUsageStates()
        {
            foreach (var shaderRow in _mappings.Where(row => row.kind == ReferenceKind.Shader))
            foreach (var usage in shaderRow.materialUsages)
                usage.applied = !MaterialUsesPlaceholderShader(usage.path, shaderRow.placeholderGuid);
        }

        private static SerializedReference CreateReference(UnityEngine.Object asset, int type)
        {
            if (asset == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long fileId) ||
                string.IsNullOrEmpty(guid)) return null;
            return new SerializedReference { fileId = fileId, guid = guid, type = type };
        }

        private static int ShaderReferenceType(Shader shader)
        {
            if (shader == null) return 3;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(shader, out string guid, out long _)) return 3;
            return guid.StartsWith("0000000000000000", StringComparison.Ordinal) ? 0 : 3;
        }

        private void BuildLookups(out Dictionary<string, SerializedReference> shaders,
            out Dictionary<string, SerializedReference> scripts,
            out Dictionary<string, SerializedReference> postProcessResources)
        {
            shaders = new Dictionary<string, SerializedReference>(StringComparer.OrdinalIgnoreCase);
            scripts = new Dictionary<string, SerializedReference>(StringComparer.OrdinalIgnoreCase);
            postProcessResources = new Dictionary<string, SerializedReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _mappings)
            {
                if (row.target == null) continue;
                if (row.kind == ReferenceKind.Shader) shaders[row.placeholderGuid] = row.target;
                else if (row.kind == ReferenceKind.Script) scripts[row.placeholderGuid] = row.target;
                else postProcessResources[row.placeholderGuid] = row.target;
            }
        }

        private void ApplyRemapping(bool applyShaders, bool applyScripts, bool applyPostProcessResources)
        {
            BuildLookups(out var shaders, out var scripts, out var postProcessResources);
            if (!applyShaders) shaders.Clear();
            if (!applyScripts) scripts.Clear();
            if (!applyPostProcessResources) postProcessResources.Clear();

            var enabledScopes = new List<string>();
            if (applyShaders) enabledScopes.Add("Shader");
            if (applyScripts) enabledScopes.Add("Script");
            if (applyPostProcessResources) enabledScopes.Add("Post-processing Resource");
            var scope = string.Join(", ", enabledScopes);
            var files = CollectYamlFiles(_selectedRoot);
            var ignoredShaderMaterials = GetIgnoredShaderMaterialPaths();
            var preview = CountReferences(files, shaders, scripts, postProcessResources, ignoredShaderMaterials);
            if (preview.Total == 0)
            {
                _message = $"No matching {scope} placeholder references remain in the selected export.";
                _messageType = MessageType.Info;
                return;
            }

            if (!EditorUtility.DisplayDialog($"Apply {scope} Remapping",
                    $"Rewrite {preview.Total} references across the selected AssetRipper export?\n\n" +
                    $"Shaders: {preview.shaderReferences}\nScripts: {preview.scriptReferences}\n" +
                    $"Post-processing resources: {preview.postProcessResourceReferences}",
                    "Apply", "Cancel")) return;

            var modifiedFiles = 0;
            var applied = new RewriteStats();
            var failures = 0;
            var cancelled = false;
            var noShaders = new Dictionary<string, SerializedReference>(StringComparer.OrdinalIgnoreCase);
            AssetDatabase.StartAssetEditing();
            try
            {
                for (var i = 0; i < files.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Reference Remapper",
                            Path.GetFileName(files[i]), files.Count == 0 ? 1f : (float)i / files.Count))
                    {
                        cancelled = true;
                        break;
                    }

                    try
                    {
                        var fileShaders = ignoredShaderMaterials.Contains(files[i]) ? noShaders : shaders;
                        var stats = RewriteYamlFile(files[i], fileShaders, scripts, postProcessResources,
                            _keepBackups, out var changed);
                        if (changed) modifiedFiles++;
                        applied.shaderReferences += stats.shaderReferences;
                        applied.scriptReferences += stats.scriptReferences;
                        applied.postProcessResourceReferences += stats.postProcessResourceReferences;
                    }
                    catch (Exception e)
                    {
                        failures++;
                        Debug.LogWarning($"[ReferenceRemapper] Could not rewrite {files[i]}: {e.Message}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }

            if (modifiedFiles > 0) AssetDatabase.Refresh();
            UpdateShaderMaterialUsageStates();
            _plannedReferences = Math.Max(0, _plannedReferences - applied.Total);
            _message = $"{(cancelled ? "Cancelled after partial completion." : "Remapping complete.")} " +
                       $"Modified {modifiedFiles} files; replaced {applied.shaderReferences} Shader and " +
                       $"{applied.scriptReferences} Script and {applied.postProcessResourceReferences} " +
                       "post-processing resource references." +
                       (failures > 0 ? $" {failures} files failed; see Console." : "");
            _messageType = failures > 0 ? MessageType.Warning : MessageType.Info;
            Repaint();
        }

        private static RewriteStats RewriteYamlFile(string path,
            Dictionary<string, SerializedReference> shaders,
            Dictionary<string, SerializedReference> scripts,
            Dictionary<string, SerializedReference> postProcessResources,
            bool keepBackup, out bool changed)
        {
            var encoding = DetectEncoding(path);
            var newline = DetectNewline(path, encoding);
            var tempPath = CreateSiblingTempPath(path);
            var stats = new RewriteStats();
            changed = false;
            var unityClass = -1;

            try
            {
                using (var reader = new StreamReader(path, encoding, true))
                using (var writer = new StreamWriter(tempPath, false, encoding) { NewLine = newline })
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var rewritten = RewriteLine(line, ref unityClass, shaders, scripts, postProcessResources,
                            ref stats);
                        if (!string.Equals(line, rewritten, StringComparison.Ordinal)) changed = true;
                        writer.WriteLine(rewritten);
                    }
                }

                if (!changed)
                {
                    File.Delete(tempPath);
                    return stats;
                }

                if (keepBackup)
                {
                    var backupPath = path + ".vrcaloader.bak";
                    if (!File.Exists(backupPath)) File.Copy(path, backupPath);
                }

                try
                {
                    File.Replace(tempPath, path, null);
                }
                catch (Exception e) when (e is IOException || e is PlatformNotSupportedException)
                {
                    File.Copy(tempPath, path, true);
                    File.Delete(tempPath);
                }
                return stats;
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static string CreateSiblingTempPath(string sourcePath)
        {
            var directory = Path.GetDirectoryName(sourcePath) ?? "";
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var candidate = Path.Combine(directory,
                    "~rr" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                if (!File.Exists(candidate)) return candidate;
            }
            throw new IOException("Could not allocate a temporary file beside the YAML asset.");
        }

        private static RewriteStats CountReferences(IEnumerable<string> files,
            Dictionary<string, SerializedReference> shaders,
            Dictionary<string, SerializedReference> scripts,
            Dictionary<string, SerializedReference> postProcessResources,
            ISet<string> ignoredShaderMaterialPaths = null)
        {
            var total = new RewriteStats();
            var noShaders = new Dictionary<string, SerializedReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in files)
            {
                var unityClass = -1;
                var fileShaders = ignoredShaderMaterialPaths != null && ignoredShaderMaterialPaths.Contains(path)
                    ? noShaders
                    : shaders;
                try
                {
                    using (var reader = new StreamReader(path, DetectEncoding(path), true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                            RewriteLine(line, ref unityClass, fileShaders, scripts, postProcessResources, ref total);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ReferenceRemapper] Could not inspect {path}: {e.Message}");
                }
            }
            return total;
        }

        private static string RewriteLine(string line, ref int unityClass,
            Dictionary<string, SerializedReference> shaders,
            Dictionary<string, SerializedReference> scripts,
            Dictionary<string, SerializedReference> postProcessResources,
            ref RewriteStats stats)
        {
            if (TryReadUnityClass(line, out var nextClass)) unityClass = nextClass;
            var trimmed = line.TrimStart(' ', '\t');
            var indentation = line.Substring(0, line.Length - trimmed.Length);

            if (trimmed.StartsWith("m_Shader: {", StringComparison.Ordinal) &&
                TryExtractGuid(trimmed, out var shaderGuid) && shaders.TryGetValue(shaderGuid, out var shaderTarget))
            {
                stats.shaderReferences++;
                return indentation + "m_Shader: " + shaderTarget;
            }

            var monoBehaviourScript = unityClass == 114 && trimmed.StartsWith("m_Script: {", StringComparison.Ordinal);
            var animationScript = unityClass == 74 && trimmed.StartsWith("script: {", StringComparison.Ordinal);
            if ((monoBehaviourScript || animationScript) && TryExtractGuid(trimmed, out var scriptGuid) &&
                scripts.TryGetValue(scriptGuid, out var scriptTarget))
            {
                stats.scriptReferences++;
                return indentation + (monoBehaviourScript ? "m_Script: " : "script: ") + scriptTarget;
            }

            if (trimmed.StartsWith("m_Resources: {", StringComparison.Ordinal) &&
                TryExtractGuid(trimmed, out var resourceGuid) &&
                postProcessResources.TryGetValue(resourceGuid, out var resourceTarget))
            {
                stats.postProcessResourceReferences++;
                return indentation + "m_Resources: " + resourceTarget;
            }
            return line;
        }

        private static bool TryReadUnityClass(string line, out int unityClass)
        {
            unityClass = -1;
            const string prefix = "--- !u!";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var end = prefix.Length;
            while (end < line.Length && char.IsDigit(line[end])) end++;
            return end > prefix.Length && int.TryParse(line.Substring(prefix.Length, end - prefix.Length), out unityClass);
        }

        private static bool TryExtractGuid(string line, out string guid)
        {
            var match = GuidPattern.Match(line);
            guid = match.Success ? match.Groups[1].Value : null;
            return !string.IsNullOrEmpty(guid);
        }

        private static List<string> CollectYamlFiles(string root)
        {
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => YamlExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ReadMetaGuid(string metaPath)
        {
            try
            {
                foreach (var line in File.ReadLines(metaPath))
                {
                    if (!line.StartsWith("guid:", StringComparison.Ordinal)) continue;
                    var guid = line.Substring(5).Trim();
                    return guid.Length == 32 ? guid : null;
                }
            }
            catch { }
            return null;
        }

        private static string ReadShaderName(string shaderPath)
        {
            try
            {
                foreach (var line in File.ReadLines(shaderPath).Take(128))
                {
                    var match = ShaderDeclaration.Match(line);
                    if (match.Success) return match.Groups[1].Value;
                }
            }
            catch { }
            return null;
        }

        private static string ReadDeclaredType(string sourcePath)
        {
            if (!File.Exists(sourcePath)) return null;
            try
            {
                var text = File.ReadAllText(sourcePath);
                var classMatch = ClassDeclaration.Match(text);
                if (!classMatch.Success) return null;
                var namespaceMatch = NamespaceDeclaration.Match(text);
                return namespaceMatch.Success
                    ? namespaceMatch.Groups[1].Value + "." + classMatch.Groups[1].Value
                    : classMatch.Groups[1].Value;
            }
            catch { return null; }
        }

        private string FindScriptContainer(string metaPath)
        {
            var current = Directory.GetParent(metaPath);
            while (current != null && IsPathInside(current.FullName, _selectedRoot))
            {
                if (string.Equals(current.Name, ".Scripts", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current.Name, "Scripts", StringComparison.OrdinalIgnoreCase)) return current.FullName;
                current = current.Parent;
            }
            return _selectedRoot;
        }

        private bool AssetIsInsideExport(UnityEngine.Object asset)
        {
            var assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return false;
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? "";
            return IsPathInside(Path.Combine(projectRoot, assetPath), _selectedRoot);
        }

        private static bool IsPathInside(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string RelativePath(string root, string path)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                           Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(fullRoot.Length)
                : Path.GetFileName(path);
        }

        private static Encoding DetectEncoding(string path)
        {
            var header = new byte[4];
            int count;
            using (var stream = File.OpenRead(path)) count = stream.Read(header, 0, header.Length);
            if (count >= 4 && header[0] == 0x00 && header[1] == 0x00 && header[2] == 0xFE && header[3] == 0xFF)
                return new UTF32Encoding(true, true);
            if (count >= 4 && header[0] == 0xFF && header[1] == 0xFE && header[2] == 0x00 && header[3] == 0x00)
                return new UTF32Encoding(false, true);
            if (count >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
                return new UTF8Encoding(true);
            if (count >= 2 && header[0] == 0xFE && header[1] == 0xFF) return new UnicodeEncoding(true, true);
            if (count >= 2 && header[0] == 0xFF && header[1] == 0xFE) return new UnicodeEncoding(false, true);
            return new UTF8Encoding(false);
        }

        private static string DetectNewline(string path, Encoding encoding)
        {
            using (var reader = new StreamReader(path, encoding, true))
            {
                var buffer = new char[4096];
                var count = reader.Read(buffer, 0, buffer.Length);
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\r') return i + 1 < count && buffer[i + 1] == '\n' ? "\r\n" : "\r";
                    if (buffer[i] == '\n') return "\n";
                }
            }
            return Environment.NewLine;
        }

        private void RefreshExportFolders()
        {
            var folders = Directory.Exists(ExportsRoot)
                ? Directory.GetDirectories(ExportsRoot).OrderByDescending(Directory.GetLastWriteTime).ToList()
                : new List<string>();
            if (Directory.Exists(_selectedRoot) && !folders.Any(f => string.Equals(f, _selectedRoot, StringComparison.OrdinalIgnoreCase)))
                folders.Add(_selectedRoot);
            _exportFolders = folders.ToArray();

            _folderIndex = Array.FindIndex(_exportFolders,
                f => string.Equals(f, _selectedRoot, StringComparison.OrdinalIgnoreCase));
            if (_folderIndex < 0 && _exportFolders.Length > 0) SelectFolder(0);
            Repaint();
        }

        private void SelectFolder(int index)
        {
            if (_exportFolders.Length == 0) return;
            _folderIndex = Mathf.Clamp(index, 0, _exportFolders.Length - 1);
            _selectedRoot = _exportFolders[_folderIndex];
            ResetAnalysis();
        }

        private void ResetAnalysis()
        {
            _mappings.Clear();
            _yamlFiles.Clear();
            _materialSearch = "";
            _plannedReferences = 0;
            _analyzed = false;
            _message = "";
        }
    }
}
#endif
