## What Can It Do?

VRCALoader can download **VRCA/VRCW builds from your own VRChat account** and load them directly into Unity through `AssetBundle` memory loading, letting you inspect the contents preserved in the uploaded bundle without first reconstructing them into project files.

With **Asset Extraction & Controllers** and **Reference Remapper**, it can also extract the bundle and repair missing controller, shader and script references, making recovery of your own uploaded avatars/worlds close to a one-click workflow. Some manual cleanup may still be required before upload.

> **Warning:** This tool is intended for recovering your own avatars and worlds only. Do not use it on content you do not own or have explicit permission to access.

## Usage

Install the latest unitypackage and open **Tools > VRCALoader**.

### Download VRCA / VRCW

When logged into the official VRChat SDK, VRCALoader uses the SDK's existing authenticated API methods to list and download VRCA/VRCW builds belonging to the current account. VRChat's [Creator Guidelines](https://hello.vrchat.com/creator-guidelines) permit applications to interact with its API when following their rules.

VRCALoader only lists and downloads builds from **your own account**. In fact, you do not have permission to download any other users' or public VRCA files.

### Direct AssetBundle Loading

Load a `.vrca` / `.vrcw` directly into Unity through `AssetBundle` loading.

This is a **temporary in-memory preview**. The loaded contents are not reconstructed into project files or written back to disk, so they **cannot be uploaded through the VRChat SDK**.

### Project Recovery

Use **Extract All Assets&Controller** to reconstruct the bundle into Unity project files through AssetRipper, then use **Reference Remapper** to repair missing shader and script references.

Unlike direct loading, this workflow produces recoverable project files that can be cleaned up and uploaded again through the VRChat SDK.

The required shaders, scripts, SDKs, and packages must already exist in the Unity project. **Poiyomi shaders must be mapped manually.**

All of the downloaded content and extracted assets are under Assets/VRCALoader.

> **Tip:** Most recovered avatars need their **Gesture Layer removed** before they can be uploaded normally.

---

### Acknowledgements

The core concept of loading VRCA bundles directly into the Unity Editor was inspired by **[dVRC](https://github.com/200Tigersbloxed/dVRC)**.

During development I discovered **[FACS Utilities](https://github.com/FACS01-01/FACS_Utilities)**, a more comprehensive toolset that handles many VRChat SDK edge cases. Its `LoadBundle` implementation informed the controller-patching logic that keeps the avatar descriptor from crashing on bundle-loaded avatars.

This project is designed to help recover as much of your original avatar logic as possible when the Unity project files are lost but the locally-built VRCA cache or an uploaded build from your account still exists.

Direct AssetBundle loading is intended for inspection. For project recovery, use **Asset Extraction & Controllers** together with **Reference Remapper**; the result may still require normal Unity-side cleanup before it is ready to upload.

---

### License

Copyright (c) 2026 cocokoishi.

VRCALoader is licensed under the **GNU Affero General Public License v3.0 only (AGPL-3.0-only)**. See [LICENSE](LICENSE) for the complete license text.

---

### Legal Notice

VRChat's Terms restrict reverse engineering of the VRChat Platform and SDK, while also stating that users retain copyright and other proprietary rights in the content they upload.

VRCALoader only accesses builds from your own account. Its recovery process operates on the locally obtained, Unity-generated AssetBundle rather than the VRChat Platform or SDK; no VRChat SDK code is decompiled or reconstructed, and missing SDK references are only re-linked to a legitimately installed SDK.

On this basis, VRCALoader considers recovery of content from your own account that you own or are authorized to restore to be compliant with the VRChat Terms. This does not apply to content belonging to other users.

Do not use VRCALoader to recover, copy, or redistribute content that is not yours.
