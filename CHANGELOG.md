# Changelog

All notable changes to this package will be documented in this file.

## [0.0.1] - 2026-06-02

### Added
- Initial release.
- Editor window at Tools > VRCALoader for loading VRCA/VRCW assetbundle files.
- **Modern dark-themed UI** with card-style slot layout, color-coded buttons, hover highlights, and version badge.
- **Save as Scene** — instantiate all loaded GameObjects into a new .unity scene file for permanent recovery.
- **Persistent storage** via EditorPrefs — paths and slot count survive editor restarts and assembly reloads.
- Drag-and-drop support for VRCA/VRCW/bundle files onto path fields.
- Multi-slot management with adjustable slot count in Settings.
- "Spawn All" batch instantiation for all GameObjects in a loaded bundle.
- Per-asset icon, type label, click-to-select and double-click-to-spawn.
- Auto-unload on assembly reload and play mode transitions.
