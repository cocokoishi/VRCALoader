## What Can It Do?

VRCALoader can download **VRCA/VRCW builds from your own VRChat account** and load them directly into Unity through `AssetBundle` memory loading, letting you inspect the contents preserved in the uploaded bundle without first reconstructing them into project files.

With **Asset Extraction & Controllers** and **Reference Remapper**, it can also extract the bundle and repair missing controller, shader and script references, making recovery of your own uploaded avatars/worlds close to a one-click workflow. Some manual cleanup may still be required before upload.

> **Tip:** Most recovered avatars need their **Gesture Layer removed** before they can be uploaded normally.

> **Warning:** This tool is intended for recovering your own avatars and worlds only. Do not use it on content you do not own or have explicit permission to access.

### 1. Installation & Usage
Install the unitypackage from the latest release, then open **Tools > VRCALoader**. Select or drag a `.vrca`, `.vrcw`, or compatible AssetBundle file into a slot and click **Load**. Double-click a loaded asset to inspect it, use **Spawn** for GameObjects, or use **Open** for an extracted scene. Multiple slots can remain available in the window, and their count can be changed from the bottom toolbar.

The first main action row contains **Extract All Assets&Controller** and, when VRCSDK3 is available, **Download VRCA&VRCW**. **Reference Remapper** is on the second action row.

### 2. How It Works
The tool calls `AssetBundle.LoadFromFileAsync`, `LoadAllAssetsAsync`, and `Object.Instantiate` to load a bundle straight into memory and place its contents in the current scene — no original project files required.

### 3. Use Cases

* **3.1 Recovering a local build** — If you lost your project files but still have the cached VRCA, find it under `C:\Users\<YourUsername>\AppData\LocalLow\VRChat\VRChat\Avatars` and load it with this tool. You can inspect blendshape values, shader parameters, and more. Objects loaded directly from the AssetBundle are for inspection and cannot themselves be re-uploaded as a reconstructed Unity project; for project recovery, use **Asset Extraction & Controllers** together with **Reference Remapper**. [unity-blendshape-to-json](https://github.com/cocokoishi/unity-blendshape-to-json) can help migrate blendshape data.
* **3.2 Recovering from the cloud** — Log in through the VRChat SDK Control Panel, open VRCALoader, and click **Download VRCA&VRCW**. Choose **Cloud Avatars** or **Cloud Worlds**, select a target platform, and click **Download**. A build picker opens with the newest build for that platform selected by default; select another build when needed. Avatars are saved as `.vrca` and worlds as `.vrcw` under `Assets/VRCALoader/VRCA/`. New file names include the current VRChat display name with Windows-incompatible characters replaced safely. The **Downloaded** tab shows that account name, can reveal or delete local files, and can add one directly to a loader slot. An empty slot is reused automatically, or a new slot is created when needed.

### 4. Asset Extraction & Controllers
Uploaded bundles do not retain the normal Unity project representation needed to work with AnimatorControllers directly. Click **Extract All Assets&Controller** on the first main action row to open the AssetRipper extraction window. It uses the bundle selected in a VRCALoader slot and exports an AssetRipper Unity-project result into `Assets/VRCALoader/Exports/<bundle>_<timestamp>/`.

[AssetRipper](https://github.com/AssetRipper/AssetRipper) is offered as a one-time download when it is missing. **Start AssetRipper** creates or refreshes `start_assetripper.bat` and reveals it in Explorer; double-click that file to start AssetRipper on port `55510`, then return to Unity and click **Extract Bundle**. The result list can open or reveal individual `.controller` files and can reveal or delete an entire extraction.

By default, the complete exported asset set is kept so it can also be used by Reference Remapper. The optional **After export, delete all folders except Animators** setting instead keeps only the AnimatorController, AnimationClip, and AnimatorState folders. **Open Exports Folder** reveals all results, while **Clear All Exports** permanently deletes every extraction after confirmation.

### 5. Reference Remapper
After keeping a complete extraction, click **Reference Remapper** on the second action row. Select a folder under `Assets/VRCALoader/Exports/` and run **Analyze References** to match AssetRipper Shader and MonoScript placeholder GUIDs with real assets installed in the current project. Unresolved entries can be assigned manually. **Apply Shaders** changes only Shader references, **Apply Scripts** changes only MonoScript references, and **Apply All** performs both operations. Matching MonoBehaviour `m_Script` references, AnimationClip script bindings, and supported `.playable` YAML are handled while preserving the source files' encoding and line endings. Shader and Script mappings remain visible when an already-repaired export is analyzed again, while the replacement count reflects only references that still need repair.

> **Tip:** Reference Remapper can only restore references when the corresponding shaders, scripts, and required SDKs/packages are already installed in the current Unity project. **Poiyomi shaders must be mapped manually.**

> **Reference Remapper warning:** Use this feature only to restore avatars, worlds, or related assets that you legally own or have explicit permission to recover. Do not use it for unauthorized extraction, copying, redistribution, or any illegal purpose.

Reference Remapper was inspired by **FACS Utilities**. This implementation follows clean-room principles and contains no source code copied from FACS Utilities.

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
