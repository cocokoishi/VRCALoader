Load VRCA/VRCW AssetBundle files directly into the Unity Editor for avatar inspection and recovery.

> **Warning:** This tool is meant for recovering your own avatars only.

### 1. Installation & Usage
Place the package under `Assets/VRCALoader/` in your project, then open it via **Tools > VRCALoader**. Select a `.vrca` or `.vrcw` file in a slot, click **Load**, then double-click any asset or click **Spawn** to instantiate it into the scene.

### 2. How It Works
The tool calls `AssetBundle.LoadFromFileAsync`, `LoadAllAssetsAsync`, and `Object.Instantiate` to load a bundle straight into memory and place its contents in the current scene — no original project files required.

### 3. Use Cases

* **3.1 Recovering a local build** — If you lost your project files but still have the cached VRCA, find it under `C:\Users\<YourUsername>\AppData\LocalLow\VRChat\VRChat\Avatars` and load it with this tool. You can inspect blendshape values, shader parameters, and more. The loaded data lives in memory only and cannot be re-uploaded; use it as a reference to manually recreate your work. [unity-blendshape-to-json](https://github.com/cocokoishi/unity-blendshape-to-json) can help migrate blendshape data.
* **3.2 Recovering from the cloud** — If local files are gone, use [dVRC](https://github.com/200Tigersbloxed/dVRC) to re-download your cloud-uploaded VRCA via VRChat's permitted APIs, then load it here.

### 4. Controller Extract
AnimatorControllers inside a VRCA bundle are stripped of editor-layer data and state-graph layout information, so Unity's Animator window cannot open them. The **Controller Extract** window (opened from the VRCALoader footer) works around this by driving [AssetRipper](https://github.com/AssetRipper/AssetRipper) to unpack the bundle and produce readable `.controller` files. AssetRipper is auto-downloaded on first use.

---

Credit to [dVRC](https://github.com/200Tigersbloxed/dVRC) for the original AssetBundle loading approach which inspired this project.

---

### Acknowledgements

While reimplementing dVRC's bundle-loading functionality, I discovered **[FACS Utilities](https://github.com/FACS01-01/FACS_Utilities)** — a more comprehensive editor toolset that handles many VRChat SDK edge cases, including fixes the vanilla SDK inspector is missing. Our controller-patching approach references their `LoadBundle` implementation to ensure the avatar descriptor behaves correctly in both Edit and Play modes.

This project, however, is intentionally lightweight: it is designed solely for recovering and inspecting assets from locally-built VRCA cache files. If you need more advanced AssetRipper-based recovery features, FACS Utilities is worth checking out.
