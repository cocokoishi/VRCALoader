#if UNITY_EDITOR && VRC_SDK_VRCSDK3
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BestHTTP.JSON;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDKBase.Editor;

namespace Cocokoishi.VRCALoader
{
    public sealed class VRCADownloadWindow : EditorWindow
    {
        private const int PageSize = 20;
        private static readonly string[] WorldPlatforms = { "standalonewindows", "android", "ios" };
        private static readonly string DownloadRoot = Path.GetFullPath(
            Path.Combine(Application.dataPath, "VRCALoader/VRCA"));

        private enum ViewPage
        {
            CloudAvatars,
            CloudWorlds,
            Downloaded
        }

        private sealed class DownloadJob
        {
            public float progress;
            public long doneBytes;
            public long totalBytes;
            public string message;
            public bool running;
            public bool failed;
        }

        private sealed class DownloadedFile
        {
            public string path;
            public string fileName;
            public string contentName;
            public string contentId;
            public string platform;
            public string vrchatUserName;
            public long size;
            public DateTime modified;
        }

        private readonly List<ApiAvatar> _avatars = new List<ApiAvatar>();
        private readonly List<ApiWorld> _worlds = new List<ApiWorld>();
        private readonly List<DownloadedFile> _downloaded = new List<DownloadedFile>();
        private readonly Dictionary<string, Texture2D> _thumbnails = new Dictionary<string, Texture2D>();
        private readonly HashSet<string> _requestedThumbnails = new HashSet<string>();
        private readonly Dictionary<string, string> _selectedPlatforms = new Dictionary<string, string>();
        private readonly Dictionary<string, DownloadJob> _downloads = new Dictionary<string, DownloadJob>();

        private Vector2 _cloudScroll;
        private Vector2 _worldScroll;
        private Vector2 _downloadedScroll;
        private ViewPage _page;
        private string _search = "";
        private string _status = "";
        private bool _fetching;
        private bool _fetchingWorlds;
        private bool _wasLoggedIn;
        private int _fetchGeneration;
        private string _fetchError = "";

        private bool IsFetching => _fetching || _fetchingWorlds;

        public static void Open()
        {
            var window = GetWindow<VRCADownloadWindow>("Download VRCA / VRCW");
            window.minSize = new Vector2(680, 480);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshDownloaded();
            _wasLoggedIn = APIUser.IsLoggedIn;
            if (_wasLoggedIn) RefreshCloud();
        }

        private void OnGUI()
        {
            var loggedIn = APIUser.IsLoggedIn;
            if (loggedIn && !_wasLoggedIn) RefreshCloud();
            _wasLoggedIn = loggedIn;

            DrawToolbar(loggedIn);
            EditorGUILayout.Space(4);
            _search = EditorGUILayout.TextField("Search", _search);
            _page = (ViewPage)GUILayout.Toolbar((int)_page,
                new[]
                {
                    $"Cloud Avatars ({_avatars.Count})",
                    $"Cloud Worlds ({_worlds.Count})",
                    $"Downloaded ({_downloaded.Count})"
                });
            EditorGUILayout.Space(4);

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);

            if (_page == ViewPage.CloudAvatars) DrawCloud(loggedIn);
            else if (_page == ViewPage.CloudWorlds) DrawWorlds(loggedIn);
            else DrawDownloaded();
        }

        private void DrawToolbar(bool loggedIn)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("VRC Downloader", EditorStyles.boldLabel, GUILayout.Width(130));
            EditorGUILayout.LabelField(loggedIn && APIUser.CurrentUser != null
                    ? APIUser.CurrentUser.displayName
                    : "Not logged in to VRChat SDK",
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            EditorGUI.BeginDisabledGroup(!loggedIn || IsFetching);
            if (GUILayout.Button(IsFetching ? "Fetching..." : "Refresh", EditorStyles.toolbarButton,
                    GUILayout.Width(72)))
                RefreshCloud();
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Open Folder", EditorStyles.toolbarButton, GUILayout.Width(86)))
            {
                Directory.CreateDirectory(DownloadRoot);
                EditorUtility.RevealInFinder(DownloadRoot);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawCloud(bool loggedIn)
        {
            if (!loggedIn)
            {
                EditorGUILayout.HelpBox("Log in from the VRChat SDK Control Panel first.", MessageType.Warning);
                return;
            }

            if (_fetching && _avatars.Count == 0)
                EditorGUILayout.LabelField("Fetching avatars from this account...", EditorStyles.centeredGreyMiniLabel);
            else if (!_fetching && _avatars.Count == 0)
                EditorGUILayout.LabelField("No downloadable avatars were found on this account.", EditorStyles.centeredGreyMiniLabel);

            _cloudScroll = EditorGUILayout.BeginScrollView(_cloudScroll);
            foreach (var avatar in _avatars)
            {
                if (!MatchesSearch(avatar.name, avatar.id)) continue;
                DrawAvatar(avatar);
                EditorGUILayout.Space(3);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawAvatar(ApiAvatar avatar)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox, GUILayout.MinHeight(104));
            DrawThumbnail(avatar.id);
            EditorGUILayout.BeginVertical();

            EditorGUILayout.LabelField(string.IsNullOrEmpty(avatar.name) ? "(unnamed)" : avatar.name,
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"{avatar.releaseStatus}  ·  Updated {avatar.updated_at:yyyy-MM-dd HH:mm}",
                EditorStyles.miniLabel);
            EditorGUILayout.SelectableLabel(avatar.id, EditorStyles.miniLabel, GUILayout.Height(17));

            var platforms = GetPlatforms(avatar);
            if (platforms.Length == 0)
            {
                EditorGUILayout.LabelField("No downloadable builds.", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                return;
            }

            var platform = GetSelectedPlatform(avatar.id, platforms);
            var selectedIndex = Math.Max(0, Array.IndexOf(platforms, platform));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Platform", GUILayout.Width(52));
            var nextIndex = EditorGUILayout.Popup(selectedIndex,
                platforms.Select(PlatformLabel).ToArray(), GUILayout.Width(130));
            platform = platforms[Mathf.Clamp(nextIndex, 0, platforms.Length - 1)];
            _selectedPlatforms[avatar.id] = platform;

            var package = avatar.unityPackages
                .Where(p => p != null && string.Equals(p.platform, platform,
                    StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(p.assetUrl))
                .FirstOrDefault();
            if (package != null)
                EditorGUILayout.LabelField($"Unity {package.unityVersion}",
                    EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            DrawDownloadControls(avatar.id, platform, () => ShowAvatarBuilds(avatar, platform));

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawWorlds(bool loggedIn)
        {
            if (!loggedIn)
            {
                EditorGUILayout.HelpBox("Log in from the VRChat SDK Control Panel first.", MessageType.Warning);
                return;
            }

            if (_fetchingWorlds && _worlds.Count == 0)
                EditorGUILayout.LabelField("Fetching worlds from this account...", EditorStyles.centeredGreyMiniLabel);
            else if (!_fetchingWorlds && _worlds.Count == 0)
                EditorGUILayout.LabelField("No downloadable worlds were found on this account.", EditorStyles.centeredGreyMiniLabel);

            _worldScroll = EditorGUILayout.BeginScrollView(_worldScroll);
            foreach (var world in _worlds)
            {
                if (!MatchesSearch(world.name, world.id)) continue;
                DrawWorld(world);
                EditorGUILayout.Space(3);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawWorld(ApiWorld world)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox, GUILayout.MinHeight(104));
            DrawThumbnail(world.id);
            EditorGUILayout.BeginVertical();

            EditorGUILayout.LabelField(string.IsNullOrEmpty(world.name) ? "(unnamed)" : world.name,
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"{world.releaseStatus}  ·  Updated {world.updated_at:yyyy-MM-dd HH:mm}  ·  Capacity {world.capacity}",
                EditorStyles.miniLabel);
            EditorGUILayout.SelectableLabel(world.id, EditorStyles.miniLabel, GUILayout.Height(17));

            var platform = GetSelectedPlatform(world.id, WorldPlatforms);
            var selectedIndex = Math.Max(0, Array.IndexOf(WorldPlatforms, platform));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Platform", GUILayout.Width(52));
            var nextIndex = EditorGUILayout.Popup(selectedIndex,
                WorldPlatforms.Select(PlatformLabel).ToArray(), GUILayout.Width(130));
            platform = WorldPlatforms[Mathf.Clamp(nextIndex, 0, WorldPlatforms.Length - 1)];
            _selectedPlatforms[world.id] = platform;
            if (!string.IsNullOrEmpty(world.unityVersion))
                EditorGUILayout.LabelField($"Unity {world.unityVersion}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            DrawDownloadControls(world.id, platform, () => ShowWorldBuilds(world, platform));
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDownloadControls(string contentId, string platform, Action startDownload)
        {
            var local = FindDownloaded(contentId, platform);
            _downloads.TryGetValue(JobKey(contentId, platform), out var job);
            if (job != null && (job.running || job.failed))
            {
                var rect = EditorGUILayout.GetControlRect(GUILayout.Height(18));
                EditorGUI.ProgressBar(rect, job.progress, DownloadStatus(job));
            }
            else
            {
                EditorGUILayout.LabelField(local == null
                        ? "Size: not downloaded"
                        : $"Size: {FormatBytes(local.size)}  ·  {local.fileName}",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUI.BeginDisabledGroup(job != null && job.running);
            if (GUILayout.Button(local == null ? "Download" : "Download Again", GUILayout.Width(118)))
                startDownload();
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(local == null);
            if (GUILayout.Button("Add to Slot", GUILayout.Width(82)))
            {
                VRCALoader.AddDownloadedToSlot(local.path);
                _status = $"Added {local.fileName} to a main-window slot.";
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawThumbnail(string contentId)
        {
            var rect = GUILayoutUtility.GetRect(112, 84, GUILayout.Width(112), GUILayout.Height(84));
            if (_thumbnails.TryGetValue(contentId, out var texture) && texture != null)
                GUI.DrawTexture(rect, texture, ScaleMode.ScaleAndCrop);
            else
            {
                EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f));
                GUI.Label(rect, "No Image", EditorStyles.centeredGreyMiniLabel);
            }
        }

        private void DrawDownloaded()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(DownloadRoot, EditorStyles.miniLabel);
            if (GUILayout.Button("Refresh Files", GUILayout.Width(88))) RefreshDownloaded();
            EditorGUILayout.EndHorizontal();

            _downloadedScroll = EditorGUILayout.BeginScrollView(_downloadedScroll);
            foreach (var file in _downloaded.ToArray())
            {
                if (!MatchesSearch(file.contentName, file.fileName, file.contentId, file.vrchatUserName)) continue;

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField(file.contentName, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    $"{PlatformLabel(file.platform)}  ·  {FormatBytes(file.size)}  ·  {file.modified:yyyy-MM-dd HH:mm}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(file.vrchatUserName)
                        ? "Unknown (legacy download)"
                        : file.vrchatUserName,
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(file.fileName, EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                if (GUILayout.Button("Add to Slot", GUILayout.Width(82), GUILayout.Height(36)))
                {
                    VRCALoader.AddDownloadedToSlot(file.path);
                    _status = $"Added {file.fileName} to a main-window slot.";
                }
                if (GUILayout.Button("Reveal", GUILayout.Width(54), GUILayout.Height(36)))
                    EditorUtility.RevealInFinder(file.path);
                if (GUILayout.Button("Delete", GUILayout.Width(54), GUILayout.Height(36)))
                    DeleteDownloaded(file);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DeleteDownloaded(DownloadedFile file)
        {
            if (!EditorUtility.DisplayDialog("Delete Downloaded File",
                    $"Delete {file.fileName}?", "Delete", "Cancel")) return;

            try
            {
                VRCALoader.RemoveDownloadedFromSlots(file.path);
                if (File.Exists(file.path)) File.Delete(file.path);
                var metaPath = file.path + ".meta";
                if (File.Exists(metaPath)) File.Delete(metaPath);
                RefreshDownloaded();
                AssetDatabase.Refresh();
                _status = "Deleted " + file.fileName + ".";
            }
            catch (Exception e)
            {
                _status = "Could not delete " + file.fileName + ": " + e.Message;
            }
        }

        private void RefreshCloud()
        {
            if (!APIUser.IsLoggedIn)
            {
                _status = "Log in to the VRChat SDK first.";
                return;
            }

            _fetchGeneration++;
            _avatars.Clear();
            _worlds.Clear();
            _thumbnails.Clear();
            _requestedThumbnails.Clear();
            _selectedPlatforms.Clear();
            _fetching = true;
            _fetchingWorlds = true;
            _fetchError = "";
            _status = "Fetching account avatars and worlds...";
            FetchPage(0, _fetchGeneration);
            FetchWorldPage(0, _fetchGeneration);
        }

        private void FetchPage(int offset, int generation)
        {
            ApiAvatar.FetchList(
                (items, _) =>
                {
                    if (!this || generation != _fetchGeneration) return;
                    var page = items == null
                        ? new List<ApiAvatar>()
                        : items.Where(a => a != null && !string.IsNullOrEmpty(a.id)).ToList();

                    foreach (var avatar in page)
                    {
                        if (_avatars.All(a => a.id != avatar.id)) _avatars.Add(avatar);
                        RequestThumbnail(avatar.id, avatar.thumbnailImageUrl, generation);
                    }

                    _avatars.Sort((a, b) => b.updated_at.CompareTo(a.updated_at));
                    if (page.Count > 0)
                    {
                        UpdateFetchStatus();
                        Repaint();
                        FetchPage(offset + page.Count, generation);
                    }
                    else
                    {
                        _fetching = false;
                        UpdateFetchStatus();
                    }
                },
                error =>
                {
                    if (!this || generation != _fetchGeneration) return;
                    _fetching = false;
                    _fetchError = "Could not fetch avatars: " + error;
                    UpdateFetchStatus();
                },
                ApiAvatar.Owner.Mine,
                ApiAvatar.ReleaseStatus.All,
                null,
                PageSize,
                offset,
                ApiAvatar.SortHeading.None,
                ApiAvatar.SortOrder.Descending,
                null,
                null,
                true,
                false,
                null,
                false);
        }

        private void FetchWorldPage(int offset, int generation)
        {
            ApiWorld.FetchList(
                items =>
                {
                    if (!this || generation != _fetchGeneration) return;
                    var page = items == null
                        ? new List<ApiWorld>()
                        : items.Where(w => w != null && !string.IsNullOrEmpty(w.id)).ToList();

                    foreach (var world in page)
                    {
                        if (_worlds.All(w => w.id != world.id)) _worlds.Add(world);
                        RequestThumbnail(world.id, world.thumbnailImageUrl, generation);
                    }

                    _worlds.Sort((a, b) => b.updated_at.CompareTo(a.updated_at));
                    if (page.Count > 0)
                    {
                        UpdateFetchStatus();
                        Repaint();
                        FetchWorldPage(offset + page.Count, generation);
                    }
                    else
                    {
                        _fetchingWorlds = false;
                        UpdateFetchStatus();
                    }
                },
                error =>
                {
                    if (!this || generation != _fetchGeneration) return;
                    _fetchingWorlds = false;
                    _fetchError = "Could not fetch worlds: " + error;
                    UpdateFetchStatus();
                },
                "updated",
                ApiWorld.SortOwnership.Mine,
                ApiWorld.SortOrder.Descending,
                offset,
                PageSize,
                "",
                null,
                null,
                null,
                null,
                "",
                ApiWorld.ReleaseStatus.All,
                null,
                null,
                true,
                false);
        }

        private void UpdateFetchStatus()
        {
            if (!string.IsNullOrEmpty(_fetchError)) _status = _fetchError;
            else if (IsFetching) _status = $"Fetched {_avatars.Count} avatars and {_worlds.Count} worlds...";
            else _status = $"Finished fetching {_avatars.Count} avatars and {_worlds.Count} worlds.";
            Repaint();
        }

        private void RequestThumbnail(string contentId, string url, int generation)
        {
            if (string.IsNullOrEmpty(url) || !_requestedThumbnails.Add(contentId)) return;

            EditorCoroutine.Start(VRCCachedWebRequest.Get(url, texture =>
            {
                if (!this || generation != _fetchGeneration || texture == null) return;
                _thumbnails[contentId] = texture;
                Repaint();
            }));
        }

        private void ShowAvatarBuilds(ApiAvatar avatar, string preferredPlatform)
        {
            _status = $"Fetching builds for {avatar.name}.";
            API.Fetch<ApiAvatar>(avatar.id,
                container => OpenBuildSelector(avatar.name, avatar.id, preferredPlatform, ".vrca", container),
                _ => { _status = "Could not fetch Avatar builds."; Repaint(); },
                true);
        }

        private void ShowWorldBuilds(ApiWorld world, string preferredPlatform)
        {
            _status = $"Fetching builds for {world.name}.";
            API.Fetch<ApiWorld>(world.id,
                container => OpenBuildSelector(world.name, world.id, preferredPlatform, ".vrcw", container),
                _ => { _status = "Could not fetch World builds."; Repaint(); },
                true);
        }

        private void OpenBuildSelector(string contentName, string contentId, string preferredPlatform,
            string extension, ApiContainer container)
        {
            try
            {
                var builds = ParseBuilds((Json.JObject)container.Data);
                if (builds.Count == 0)
                {
                    _status = "No downloadable builds were found.";
                    Repaint();
                    return;
                }

                VRCBuildSelectionWindow.Open(contentName, builds, preferredPlatform, build =>
                {
                    if (!this) return;
                    _selectedPlatforms[contentId] = build.platform;
                    Directory.CreateDirectory(DownloadRoot);
                    var vrchatUserName = SafeFilePart(CurrentVrchatUserName());
                    var outputPath = Path.Combine(DownloadRoot,
                        $"{SafeFilePart(contentName)}_{contentId}_{SafeFilePart(build.platform)}__{vrchatUserName}{extension}");
                    var job = CreateJob(contentId, build.platform, "Connecting...");
                    _status = $"Downloading {contentName} ({PlatformLabel(build.platform)}).";
                    DownloadAsset(build.assetUrl, outputPath, job);
                    Repaint();
                });
                _status = $"Select a build for {contentName}.";
                Repaint();
            }
            catch (Exception e)
            {
                _status = "Could not read builds: " + e.Message;
                Repaint();
            }
        }

        private static List<VRCBuildOption> ParseBuilds(Json.JObject root)
        {
            var result = new List<VRCBuildOption>();
            Json.Token packagesToken;
            if (!root.TryGetValue("unityPackages", out packagesToken) || packagesToken.IsNull)
                return result;

            foreach (var token in packagesToken.Array)
            {
                var item = token.Object;
                var assetUrl = JsonString(item, "assetUrl");
                var platform = JsonString(item, "platform");
                if (string.IsNullOrEmpty(assetUrl) || string.IsNullOrEmpty(platform)) continue;

                DateTime.TryParse(JsonString(item, "created_at"), out var createdAt);
                result.Add(new VRCBuildOption
                {
                    assetUrl = assetUrl,
                    id = JsonString(item, "id"),
                    platform = platform,
                    unityVersion = JsonString(item, "unityVersion"),
                    variant = JsonString(item, "variant", "standard"),
                    createdAt = createdAt,
                    unitySortNumber = JsonLong(item, "unitySortNumber"),
                    assetVersion = (int)JsonLong(item, "assetVersion")
                });
            }
            return result;
        }

        private static string JsonString(Json.JObject item, string key, string fallback = "")
        {
            Json.Token token;
            return item.TryGetValue(key, out token) && !token.IsNull
                ? token.StringInstance
                : fallback;
        }

        private static long JsonLong(Json.JObject item, string key)
        {
            Json.Token token;
            return item.TryGetValue(key, out token) && !token.IsNull
                ? Convert.ToInt64(token.Number)
                : 0L;
        }

        private DownloadJob CreateJob(string contentId, string platform, string message)
        {
            var job = new DownloadJob { message = message, running = true };
            _downloads[JobKey(contentId, platform)] = job;
            return job;
        }

        private void DownloadAsset(string assetUrl, string outputPath, DownloadJob job)
        {
            try
            {
                ApiFile.DownloadFile(assetUrl,
                    bytes =>
                    {
                        try
                        {
                            if (bytes == null || bytes.Length == 0)
                                throw new IOException("The server returned an empty file.");
                            File.WriteAllBytes(outputPath, bytes);
                            job.doneBytes = bytes.LongLength;
                            job.totalBytes = bytes.LongLength;
                            job.progress = 1f;
                            job.running = false;
                            job.message = "Complete";
                            RefreshDownloaded();
                            _status = $"Downloaded {Path.GetFileName(outputPath)} ({FormatBytes(bytes.LongLength)}).";
                        }
                        catch (Exception e)
                        {
                            FailDownload(job, e.Message);
                        }
                        if (this) Repaint();
                    },
                    error =>
                    {
                        FailDownload(job, error == null ? "Unknown error" : error.ToString());
                        if (this) Repaint();
                    },
                    (done, total) =>
                    {
                        job.doneBytes = Math.Max(0L, Convert.ToInt64(done));
                        job.totalBytes = Math.Max(0L, Convert.ToInt64(total));
                        job.progress = job.totalBytes > 0
                            ? Mathf.Clamp01((float)job.doneBytes / job.totalBytes)
                            : 0f;
                        job.message = "Downloading";
                        if (this) Repaint();
                    });
            }
            catch (Exception e)
            {
                FailDownload(job, e.Message);
            }
        }

        private void FailDownload(DownloadJob job, string error)
        {
            job.running = false;
            job.failed = true;
            job.message = error;
            _status = "Download failed: " + error;
        }

        private void RefreshDownloaded()
        {
            _downloaded.Clear();
            if (!Directory.Exists(DownloadRoot)) return;

            foreach (var path in Directory.GetFiles(DownloadRoot, "*", SearchOption.TopDirectoryOnly)
                         .Where(p => string.Equals(Path.GetExtension(p), ".vrca", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(Path.GetExtension(p), ".vrcw", StringComparison.OrdinalIgnoreCase)))
            {
                var info = new FileInfo(path);
                var file = ParseDownloadedFile(info);
                _downloaded.Add(file);
            }
            _downloaded.Sort((a, b) => b.modified.CompareTo(a.modified));
        }

        private static DownloadedFile ParseDownloadedFile(FileInfo info)
        {
            var stem = Path.GetFileNameWithoutExtension(info.Name);
            var marker = stem.IndexOf("_avtr_", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) marker = stem.IndexOf("_wrld_", StringComparison.OrdinalIgnoreCase);
            if (marker < 0 && (stem.StartsWith("avtr_", StringComparison.OrdinalIgnoreCase) ||
                               stem.StartsWith("wrld_", StringComparison.OrdinalIgnoreCase))) marker = 0;

            var contentId = "";
            var platform = "";
            var vrchatUserName = "";
            var contentName = stem;
            if (marker >= 0)
            {
                var idStart = marker == 0 ? 0 : marker + 1;
                var platformStart = stem.IndexOf('_', idStart + 5);
                if (platformStart > idStart)
                {
                    contentId = stem.Substring(idStart, platformStart - idStart);
                    var platformAndUser = stem.Substring(platformStart + 1);
                    var userMarker = platformAndUser.IndexOf("__", StringComparison.Ordinal);
                    if (userMarker >= 0)
                    {
                        platform = platformAndUser.Substring(0, userMarker);
                        vrchatUserName = platformAndUser.Substring(userMarker + 2);
                    }
                    else
                    {
                        platform = platformAndUser;
                    }
                    contentName = marker == 0 ? contentId : stem.Substring(0, marker).TrimEnd('_');
                }
            }

            return new DownloadedFile
            {
                path = info.FullName,
                fileName = info.Name,
                contentName = contentName,
                contentId = contentId,
                platform = platform,
                vrchatUserName = vrchatUserName,
                size = info.Length,
                modified = info.LastWriteTime
            };
        }

        private DownloadedFile FindDownloaded(string contentId, string platform)
        {
            var matches = _downloaded.Where(file =>
                string.Equals(file.contentId, contentId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(file.platform, platform, StringComparison.OrdinalIgnoreCase)).ToList();
            var currentUserName = SafeFilePart(CurrentVrchatUserName());
            return matches.FirstOrDefault(file =>
                       string.Equals(file.vrchatUserName, currentUserName, StringComparison.OrdinalIgnoreCase))
                   ?? matches.FirstOrDefault(file => string.IsNullOrEmpty(file.vrchatUserName));
        }

        private static string CurrentVrchatUserName()
        {
            return APIUser.CurrentUser == null || string.IsNullOrWhiteSpace(APIUser.CurrentUser.displayName)
                ? "UnknownUser"
                : APIUser.CurrentUser.displayName;
        }

        private string GetSelectedPlatform(string avatarId, string[] platforms)
        {
            if (_selectedPlatforms.TryGetValue(avatarId, out var selected) &&
                platforms.Contains(selected, StringComparer.OrdinalIgnoreCase))
                return selected;

            selected = platforms.FirstOrDefault(p =>
                string.Equals(p, VRC.Tools.Platform, StringComparison.OrdinalIgnoreCase)) ?? platforms[0];
            _selectedPlatforms[avatarId] = selected;
            return selected;
        }

        private static string[] GetPlatforms(ApiAvatar avatar)
        {
            if (avatar.unityPackages == null) return Array.Empty<string>();
            return avatar.unityPackages
                .Where(p => p != null && !string.IsNullOrEmpty(p.platform) && !string.IsNullOrEmpty(p.assetUrl))
                .Select(p => p.platform)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => PlatformOrder(p))
                .ToArray();
        }

        private bool MatchesSearch(params string[] values)
        {
            if (string.IsNullOrWhiteSpace(_search)) return true;
            return values.Any(value => !string.IsNullOrEmpty(value) &&
                value.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string DownloadStatus(DownloadJob job)
        {
            if (job.failed) return "Failed: " + job.message;
            if (job.totalBytes > 0)
                return $"{job.message}  {FormatBytes(job.doneBytes)} / {FormatBytes(job.totalBytes)}";
            return job.message;
        }

        private static string JobKey(string avatarId, string platform)
        {
            return avatarId + "|" + platform.ToLowerInvariant();
        }

        private static int PlatformOrder(string platform)
        {
            if (string.Equals(platform, "standalonewindows", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        private static string PlatformLabel(string platform)
        {
            if (string.Equals(platform, "standalonewindows", StringComparison.OrdinalIgnoreCase)) return "Windows";
            if (string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase)) return "Android";
            if (string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)) return "iOS";
            return string.IsNullOrEmpty(platform) ? "Unknown" : platform;
        }

        private static string SafeFilePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unnamed";
            foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            value = value.Trim().TrimEnd('.');
            if (value.Length > 60) value = value.Substring(0, 60).TrimEnd();
            return string.IsNullOrEmpty(value) ? "Unnamed" : value;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576d).ToString("0.0") + " MB";
            return (bytes / 1073741824d).ToString("0.00") + " GB";
        }
    }

    internal sealed class VRCBuildOption
    {
        public string assetUrl;
        public string id;
        public string platform;
        public string unityVersion;
        public string variant;
        public DateTime createdAt;
        public long unitySortNumber;
        public int assetVersion;
    }

    internal sealed class VRCBuildSelectionWindow : EditorWindow
    {
        private List<VRCBuildOption> _builds;
        private Action<VRCBuildOption> _onSelected;
        private Vector2 _scroll;
        private int _selectedIndex;
        private string _contentName;

        public static void Open(string contentName, List<VRCBuildOption> builds, string preferredPlatform,
            Action<VRCBuildOption> onSelected)
        {
            var window = CreateInstance<VRCBuildSelectionWindow>();
            window.titleContent = new GUIContent("Select Build");
            window._contentName = contentName;
            window._onSelected = onSelected;
            window._builds = builds
                .OrderByDescending(build => string.Equals(build.platform, preferredPlatform,
                    StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(build => build.createdAt)
                .ThenByDescending(build => build.assetVersion)
                .ThenByDescending(build => build.unitySortNumber)
                .ThenByDescending(build => string.Equals(build.variant, "standard",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            window.minSize = new Vector2(540, 300);
            window.maxSize = new Vector2(760, 640);
            window.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(_contentName, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "The newest build for the selected platform is selected by default.",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (var i = 0; i < _builds.Count; i++)
            {
                var build = _builds[i];
                var created = build.createdAt == default
                    ? "Unknown date"
                    : build.createdAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
                var label =
                    $"{(i == 0 ? "[Latest]  " : "")}{PlatformName(build.platform)}  ·  " +
                    $"Unity {build.unityVersion}  ·  {build.variant}\n" +
                    $"{created}  ·  Asset Version {build.assetVersion}  ·  {build.id}";
                if (GUILayout.Toggle(_selectedIndex == i, label, GUI.skin.button, GUILayout.Height(48)))
                    _selectedIndex = i;
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel", GUILayout.Width(90), GUILayout.Height(26))) Close();
            if (GUILayout.Button("Download Selected", GUILayout.Width(140), GUILayout.Height(26)))
            {
                var selected = _builds[_selectedIndex];
                var callback = _onSelected;
                Close();
                callback?.Invoke(selected);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);
        }

        private static string PlatformName(string platform)
        {
            if (string.Equals(platform, "standalonewindows", StringComparison.OrdinalIgnoreCase)) return "Windows";
            if (string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase)) return "Android";
            if (string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)) return "iOS";
            return platform;
        }
    }
}
#endif
