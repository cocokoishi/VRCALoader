This is a tool that allows you to load VRCA/VRCW files directly into the Unity Editor!

> **Warning:** This tool is to help you recover your own avatars!

### 1. How to install and use?
Put it into your project's Assets/Editor/VRCALoader.cs Directory. Then use it from Tool/VRCALoader. After selecting your vrca, load and double click spawn to spawn it into the hierarchy and scene.

<img width="1254" height="895" alt="image" src="https://github.com/user-attachments/assets/aa8ee5fe-3acd-40a5-a346-52876dd0c02d" />

### 2. How does it work?
The Unity Editor can use a script to call the `StartLoadBundle` function, which directly loads a VRCA assetbundle file into the current scene without needing the actual project files at all.

### 3. What can this be used for?
It can be used for a lot of things!

* **3.1 Recovering from Local build:** If you've lost your avatar's project files but haven't cleared your cache, you can find your avatar's local assetbundle file (the VRCA) at `C:\Users\**YourUsername**\AppData\LocalLow\VRChat\VRChat\Avatars`. Using this tool, you can load it directly into the current scene to help you recover your facial blendshape values, shader parameters, and more. Since this data is saved on memory, it is transient and cannot be stored or re-uploaded directly. Your best bet is to use the loaded data as a guide and manually recreate your work. You can use [unity-blendshape-to-json](https://github.com/cocokoishi/unity-blendshape-to-json) to migrate your blendshape data in a simple way.
* **3.2 Recovering from the Cloud:** If your local avatar files are completely gone, I recommend using [https://github.com/200Tigersbloxed/dVRC](https://github.com/200Tigersbloxed/dVRC). It uses VRChat-permitted APIs to re-download your cloud-uploaded VRCA package and can also load it directly into the scene.

### 4. Don't do anything stupid.
While this script functions independently, it is designed as a recovery utility for VRChat creators—specifically for those who need to restore their own projects after data loss or file corruption. I do not support or condone the use of this tool for accessing protected assets or any activities that violate the platform's Terms of Service.

---

**Reference Project:**
[https://github.com/200Tigersbloxed/dVRC](https://github.com/200Tigersbloxed/dVRC)
A huge thanks to this project for providing the `StartLoadBundle` method! This script is a streamlined adaptation of that project, designed specifically for the lightweight loading of locally downloaded assetbundles. 
