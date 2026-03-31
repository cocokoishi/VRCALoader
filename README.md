This is a tool that allows you to load VRCA/VRCW files directly into the Unity Editor!

> **Warning:** This tool is absolutely not intended for ripping models; it is strictly meant to help you recover your own avatars!

### 1. How does it work?
The Unity Editor can use a script to call the `StartLoadBundle` function, which directly loads a VRCA assetbundle file into the current scene without needing the actual project files at all.

### 2. What can this be used for?
It can be used for a lot of things!

* **2.1 Recovering from Local build:** If you've lost your avatar's project files but haven't cleared your cache, you can find your avatar's local assetbundle file (the VRCA) at `C:\Users\**YourUsername**\AppData\LocalLow\VRChat\VRChat\Avatars`. Using this tool, you can load it directly into the current scene to help you recover your facial blendshape values, shader parameters, and more.
* **2.2 Recovering from the Cloud:** If your local avatar files are completely gone, I recommend using [https://github.com/200Tigersbloxed/dVRC](https://github.com/200Tigersbloxed/dVRC). It uses VRChat-permitted APIs to re-download your cloud-uploaded VRCA package and can also load it directly into the scene.

### 3. Is this a model-ripping script?
This script can exist completely independently of VRChat, but I believe it is incredibly useful for VRChat players—especially those who accidentally delete their project files. As for using this script to inspect or study other people's avatars without authorization, I strongly condemn such behavior.

---

**Reference Project:**
[https://github.com/200Tigersbloxed/dVRC](https://github.com/200Tigersbloxed/dVRC)
A huge thanks to this project for providing the `StartLoadBundle` method! This script is a streamlined adaptation of that project, designed specifically for the lightweight loading of locally downloaded assetbundles.