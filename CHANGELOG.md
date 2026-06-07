# Changelog

All notable changes to this package will be documented in this file.

## [0.1.4] - 2026-06-07

### Changed
- **AssetRipper install logic refactored.** The download-and-install flow is now a shared `EnsureInstalledRoutine` used by both "Start AssetRipper" and "Extract Bundle". Previously "Start AssetRipper" blindly called `EditorUtility.RevealInFinder` to a non-existent `.bat` path when AssetRipper was not installed, opening Explorer to an irrelevant location. Now both buttons trigger the install dialog when AssetRipper is missing, and the `startsh` folder plus `.bat` file are recreated on every "Start AssetRipper" click.

### Added
- **First-time file browser defaults to VRChat Avatars folder.** On the very first use of any slot's browse ("...") button, the file open dialog starts at `AppData/LocalLow/VRChat/VRChat/Avatars`. All subsequent opens use the OS default (last-used folder). The flag persists in `vrcaloader-settings.json`.

## [0.1.3] - 2026-06-05

### Changed
- **Server-first extraction check.** Controller Extract now probes port 6969 before checking for a local AssetRipper installation. If AssetRipper is already running (regardless of where it was installed), extraction proceeds immediately — no download prompt, no local-exe check. The installation fallback only triggers when the server is unreachable.

### Removed
- **Browse button** from the Controller Extract source selector. Bundle selection is handled by VRCALoader's slot system; the manual file browser was unused and misleading.

## [0.1.2] - 2026-06-05

### Changed
- The AssetRipper download now reports live progress (`Downloading AssetRipper... NN%`) instead of a static "Downloading..." label, so the ~120 MB first-run download no longer looks frozen.

### Fixed
- Loading a bundle no longer logs "Destroying assets is not permitted to avoid data loss" for every avatar. `StripPipelineManager` also runs on the asset bodies returned by `LoadAllAssetsAsync`, which Unity treats as assets, so the `DestroyImmediate` call now passes `allowDestroyingAssets: true`. These are transient bundle objects (hidden from save/build) with no backing file, so nothing on disk is affected.
- Deleting an entry in Controller Extract no longer throws "Invalid GUILayout state". The delete handler used to remove the item and `break` out of the draw loop between `BeginHorizontal`/`BeginVertical` and their matching `End` calls, leaving layout groups unclosed. Deletion is now deferred until after the scroll view closes.
- Controller extraction could request the export before AssetRipper finished loading the bundle. The one-second settle between `/LoadFile` and `/Export/UnityProject` used `WaitForSecondsRealtime`, which has no effect here: the extraction `IEnumerator` is driven by manual `MoveNext()` calls from `EditorApplication.update`, not Unity's coroutine scheduler, so it resumed on the very next editor tick instead of pausing. The delay now polls `EditorApplication.timeSinceStartup`, so the export waits the intended interval.
- The AssetRipper server-status dot could show a stale state. `CheckServerAlive` probes port 6969 on a `ThreadPool` worker and writes `_serverAlive`, which `OnGUI` reads on the main thread without a memory barrier; the field is now `volatile` so the GUI reliably observes the latest probe result.

## [0.1.1] - 2026-06-04

### Changed
- **HIG-compliant AssetRipper launcher.** The bat-file button is now on the action row, renamed to "Start AssetRipper", and driven by server state. When port 6969 is unreachable: a gray dot with a blue primary button signals action is needed. When the server is running: a green dot with a disabled "AssetRipper Running" label replaces it — no clickable affordance, no ambiguity. Removed "from bat" implementation detail from user-facing text.

### Changed
- Extraction history list now defaults to collapsed; only the most recently exported item expands automatically. Foldout toggles let the user expand or collapse any entry.

### Fixed
- "Reveal" and "Delete" buttons in extraction history widened slightly to prevent label clipping.

## [0.1.0] - 2026-06-03

### Added
- Header title "VRCALoader" now links to the GitHub repository (https://github.com/cocokoishi/VRCALoader).
- Tutorial button in the footer toolbar opens a dedicated Tutorial window covering installation, usage, use cases (local/cloud recovery with links to unity-blendshape-to-json and dVRC), and controller extraction.

### Changed
- `#if VRC_SDK_VRCSDK3` guards tightened to `#if VRC_SDK_VRCSDK3 && !UDON`, matching the FACS01 compatibility pattern so the editor compiles in Worlds-only projects where `VRC.SDK3.Avatars` types are absent.

### Fixed
- Exiting Play Mode while a bundle is loaded no longer crashes Unity. `DestroyImmediate` and `AssetBundle.Unload(true)` were racing with Unity's own scene teardown during `ExitingPlayMode` — now `Unload(false)` is used instead, releasing the bundle handle without touching instantiated objects that Unity is already cleaning up.

## [0.0.5] - 2026-06-03

### Changed
- AssetRipper program and config file relocated to `VRCALoader_Data/` at project root (alongside `Assets/`), keeping editor tooling separate from Unity asset imports.
- Exports directory moved to `Assets/VRCALoader/Exports/` so extracted controllers remain within the Assets tree for direct Unity indexing.

## [0.0.4] - 2026-06-03

### Changed
- Strip-non-controller checkbox now defaults to off and persists via EditorPrefs.
- Bundle path selection persists across domain reloads and auto-matches VRCALoader slots.
- AssetRipper is no longer launched automatically; a `.bat` file is generated and the user starts it manually.

### Fixed
- Extract button shows contextual help ("No bundle selected" / "File not found") when disabled instead of a silent greyed-out state.
- VRCALoader slot list is refreshed every frame so Controller Extract always has current bundle paths.
- Post-export flattening removes the `ExportedProject` wrapper and places assets directly under the export folder.
- Delete button in extraction history now also removes the `.meta` folder.
- Null-ref guard in asset row draw prevents rare GUI exceptions.

## [0.0.3] - 2026-06-03

### Changed
- **Coroutine-driven async loading** replaces the previous phase-machine approach. Bundle reads and asset extraction are pumped by `EditorApplication.update`, keeping the editor responsive during load.
- **Refactored architecture** — clearer separation of concerns, smaller methods, and guard-clause style throughout.

### Fixed
- Empty AnimatorControllers are now patched consistently at both bundle-load and spawn time, with a minimal stub layer applied only in Edit Mode so the VRCSDK descriptor inspector does not throw `IndexOutOfRangeException`.
- `PipelineManager` is stripped on spawn as well as on load.
- Destroyed-object guard in the asset row draw prevents rare null-refs during GUI repaint.

### Added
- **Controller Extract** — a companion editor window opened from the VRCALoader footer. Drives AssetRipper (web-based, launched externally via a provided `.bat` file) through its HTTP API to unpack a VRCA bundle, then flattens the export, strips non-controller assets when requested, and presents a persistent history of extracted `.controller` files. Supports one-click download of AssetRipper on first use.

## [0.0.2] - 2026-06-02

### Fixed
- Spawning a VRChat avatar whose AnimatorController has zero layers no longer triggers `IndexOutOfRangeException` in the VRCSDK avatar inspector. Empty controllers are patched with a stub layer on spawn.

## [0.0.1] - 2026-06-02

### Added
- Initial release.
- Editor window at Tools > VRCALoader for loading VRCA/VRCW assetbundle files.
- Card-style slot layout with drag-and-drop, multi-slot management, and JSON persistence.
- Per-asset icon, type label, click-to-select and double-click-to-spawn.
- Auto-unload on assembly reload and play mode transitions.
