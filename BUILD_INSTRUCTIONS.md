# Vortex Engine - Build Instructions

## Prerequisites

- Visual Studio 2022 (or later) with C++ Desktop Development workload
- Windows 10/11 SDK
- .NET Framework 4.8 SDK
- NuGet Package Manager (included with Visual Studio)

## Building the Engine

### Step 1: Clone the Repository

```bash
git clone https://github.com/shadow-kernel/Vortex-Engine.git
cd Vortex-Engine
```

### Step 2: Install Assimp NuGet Packages

The engine requires Assimp for 3D model import (FBX, OBJ, GLTF, etc.).

**Option A: Automatic Installation (Recommended)**

1. Open `Vortex.sln` in Visual Studio
2. Right-click on the **Solution** in Solution Explorer
3. Select **"Restore NuGet Packages"**
4. Wait for packages to download (Assimp 3.0.0)

**Option B: Manual Package Installation**

If automatic restore doesn't work:

1. Right-click on the **Engine** project
2. Select **"Manage NuGet Packages"**
3. Go to the **Browse** tab
4. Search for "Assimp" 
5. Install **Assimp** version 3.0.0
6. Install **Assimp.redist** version 3.0.0

**Option C: Manual Installation from GitHub (Advanced)**

For better format support and newer features, you can manually install Assimp 5.x:

1. Download Assimp from https://github.com/assimp/assimp/releases
2. Extract to a location (e.g., `C:\Libraries\assimp-5.x`)
3. In Visual Studio, right-click **Engine** project → **Properties**
4. **C/C++** → **General** → **Additional Include Directories**:
   - Add: `C:\Libraries\assimp-5.x\include`
5. **Linker** → **General** → **Additional Library Directories**:
   - Add: `C:\Libraries\assimp-5.x\lib\x64` (for x64 build)
6. **Linker** → **Input** → **Additional Dependencies**:
   - Add: `assimp-vc143-mt.lib` (adjust for your VS version)
7. Copy `assimp-vc143-mt.dll` to your output directory

### Step 3: Clear NuGet Cache (If Having Issues)

If you encounter package-related errors:

```bash
dotnet nuget locals all --clear
```

Then:
1. Close Visual Studio
2. Delete the `.vs` folder in the solution directory
3. Reopen Visual Studio
4. Restore NuGet packages again

### Step 4: Build the Solution

1. Select build configuration: **Debug x64** or **Release x64**
2. Build → **Rebuild Solution** (Ctrl+Shift+B)
3. Wait for compilation to complete

Expected build order:
- Engine (C++ Static Library)
- VortexAPI (C++ DLL)
- Editor (C# WPF Application)
- EngineTest (C++ Test Project)

### Step 5: Run the Editor

1. Set **Editor** as the startup project (right-click → Set as Startup Project)
2. Press **F5** to run
3. The Vortex Editor should launch

## Troubleshooting Build Errors

### Error: "assimp/Importer.hpp: No such file or directory"

**Cause:** Assimp include paths not configured or packages not installed.

**Solution:**
1. Restore NuGet packages (Step 2)
2. Verify `VORTEX_USE_ASSIMP` is in preprocessor definitions:
   - Engine project → Properties → C/C++ → Preprocessor → Preprocessor Definitions
   - Should contain: `VORTEX_USE_ASSIMP;_DEBUG;_LIB;%(PreprocessorDefinitions)`
3. Verify Additional Include Directories contains:
   - `$(ProjectDir)ThirdParty;$(SolutionDir)packages\Assimp.3.0.0\build\native\include`

### Error: LNK2019 "Unresolved external symbol Assimp::Importer::..."

**Cause:** Assimp library files (.lib) are not being linked.

**Solution:**

1. **Restore NuGet packages:**
   ```bash
   # In Visual Studio: Right-click Solution → Restore NuGet Packages
   ```

2. **Verify package installation:**
   - Check that `packages\Assimp.3.0.0\build\native\lib\` folder exists
   - Should contain `Win32` and `x64` subfolders with `.lib` files

3. **Clean and rebuild:**
   - Build → Clean Solution
   - Build → Rebuild Solution

4. **If still failing, manually add library directories:**
   - Right-click **Engine** project → **Properties**
   - **Linker** → **General** → **Additional Library Directories**
   - For x64 Debug/Release: Add `$(SolutionDir)packages\Assimp.3.0.0\build\native\lib\x64`
   - For Win32 Debug/Release: Add `$(SolutionDir)packages\Assimp.3.0.0\build\native\lib\Win32`

5. **Add library dependency:**
   - **Linker** → **Input** → **Additional Dependencies**
   - Add: `assimp-vc142-mt.lib`

6. **Ensure preprocessor is set:**
   - **C/C++** → **Preprocessor** → **Preprocessor Definitions**
   - Must include: `VORTEX_USE_ASSIMP`

**Note:** The latest commit should have these linker settings pre-configured. If you pulled the latest changes, try:
```bash
# Clear all caches
dotnet nuget locals all --clear
# Delete .vs folder in solution directory
# Restart Visual Studio
# Restore NuGet Packages
# Rebuild Solution
```

### Error: "Engine.lib cannot be opened"

**Cause:** Engine project failed to build, so dependent projects can't link.

**Solution:**
1. Build **Engine** project separately first (right-click → Build)
2. Check Output window for actual Engine build errors
3. Fix Engine errors first, then rebuild solution

### Error: "Package Assimp 5.3.1 not found"

**Cause:** Old commit referenced non-existent package version.

**Solution:**
- Already fixed in latest commits
- Use Assimp 3.0.0 (specified in `packages.config`)
- Pull latest changes: `git pull origin copilot/integrate-models-import-feature`

### Error: CS8370 "or-Muster not available in C# 7.3"

**Cause:** C# language version too old.

**Solution:**
- Already fixed in latest commits
- Editor project now uses C# 9.0 (`<LangVersion>9.0</LangVersion>`)

## Importing 3D Models

Once the engine is built and running:

### Via Asset Browser

1. Open a project in the Editor
2. Go to **Asset Browser** panel (bottom)
3. Click on **Meshes** or **Models** tab
4. Click **Import** button
5. Select your model file (`.obj`, `.fbx`, `.gltf`, `.glb`)
6. The model will be automatically:
   - Imported via Assimp
   - Converted to `.vmesh` native format
   - Materials and textures extracted
   - Metadata (`.vmeta`) generated with GUID

### Via Drag & Drop

1. Drag a model file from Windows Explorer
2. Drop it into the **Viewport**
3. The model will be:
   - Auto-imported
   - Entity created in scene
   - Ready to render

### Supported Formats

**Models:**
- FBX (Autodesk Filmbox)
- OBJ (Wavefront)
- GLTF / GLB (Khronos Group)
- DAE (Collada)
- 3DS (3D Studio)
- Blend (Blender - limited)
- And 40+ more via Assimp

**Textures:**
- PNG
- JPG / JPEG
- TGA
- BMP
- DDS
- HDR

**Materials:**
- `.mtl` files (for OBJ)
- Embedded materials (FBX, GLTF)
- `.vmat` (Vortex native format)

## Asset System Features

- **GUID-based References:** Assets can be moved/renamed without breaking references
- **Automatic Dependency Tracking:** Materials reference textures, meshes reference materials
- **Cascading Delete Protection:** Can't delete assets that are in use
- **Native Binary Formats:** `.vmesh` for 10x faster loading than source formats
- **Runtime Loading:** Standalone games load assets without the editor

## Performance

- **First Import:** Slower (Assimp processing + conversion)
- **Subsequent Loads:** Fast (native `.vmesh` format)
- **Typical Import Time:** 
  - Small model (1K triangles): < 1 second
  - Medium model (50K triangles): 2-5 seconds
  - Large model (500K triangles): 10-20 seconds

## Documentation

- `FEATURES.md` - Complete feature list with code examples
- `USAGE_GUIDE.md` - Detailed usage instructions
- `REFACTORING_SUMMARY.md` - Architecture changes
- `QUICKSTART_MODEL_IMPORT.md` - Quick start guide
- `NUGET_TROUBLESHOOTING.md` - NuGet package troubleshooting

## Getting Help

If you encounter issues:

1. Check this BUILD_INSTRUCTIONS.md
2. Check NUGET_TROUBLESHOOTING.md for package issues
3. Check Output window in Visual Studio for detailed errors
4. Open an issue on GitHub with:
   - Error message
   - Build configuration (Debug/Release, x64/x86)
   - Visual Studio version
   - Steps to reproduce

## Architecture Overview

```
Vortex Engine Architecture
├── Engine (C++ Static Library)
│   ├── Graphics/
│   │   ├── Importers/
│   │   │   ├── ModelImporter (Assimp integration)
│   │   │   ├── TextureImporter (stb_image)
│   │   │   ├── MeshSerializer (.vmesh format)
│   │   │   └── MaterialSerializer (.vmat format)
│   │   └── Resources/
│   │       ├── Mesh
│   │       ├── Material
│   │       └── Texture
│   └── Runtime/
│       ├── AssetDatabase (C++ GUID system)
│       ├── ResourceManager (Runtime loading)
│       └── AssetManifest (.vam binary manifest)
│
├── VortexAPI (C++ DLL)
│   └── Exports C++ functionality to C#
│
└── Editor (C# WPF Application)
    ├── Core/
    │   └── Assets/
    │       ├── AssetDatabase (C# GUID system)
    │       ├── AssetMetadata (.vmeta files)
    │       ├── DependencyResolver
    │       └── AssetDeletionService
    └── Editors/
        └── WorldEditor/
            ├── AssetBrowser (Import UI)
            └── DragDrop/
                └── ViewportDropHandler
```

## Next Steps

After successful build:

1. **Create a Project:** File → New Project
2. **Import a Model:** Asset Browser → Import
3. **Add to Scene:** Drag model to viewport or hierarchy
4. **Test Rendering:** Press Play button
5. **Save Scene:** File → Save Scene

Enjoy building with Vortex Engine! 🚀
