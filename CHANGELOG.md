# Changelog

All notable changes to this package will be documented in this file.

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
