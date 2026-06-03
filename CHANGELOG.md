# Changelog

All notable changes to this package will be documented in this file.

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
