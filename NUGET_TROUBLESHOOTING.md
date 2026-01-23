# NuGet Package Troubleshooting

## Problem: "Assimp 5.3.1 not found" or Package Version Mismatch

If you're seeing version 5.3.1 in the NuGet Package Manager but it's not available, this is because:
1. The packages.config was recently updated from 5.3.1 to 3.0.0
2. Your local NuGet cache or Visual Studio cache still shows the old version

## Solution Steps

### Step 1: Pull Latest Changes
```bash
git pull origin copilot/integrate-models-import-feature
```

### Step 2: Clear NuGet Cache
```bash
# Clear all NuGet caches
dotnet nuget locals all --clear

# Or use NuGet CLI
nuget locals all -clear
```

### Step 3: Close and Reopen Visual Studio
1. Close Visual Studio completely
2. Delete the `.vs` folder in your solution directory (hidden folder):
   ```bash
   rmdir /s /q .vs
   ```
3. Reopen Visual Studio

### Step 4: Restore Packages
In Visual Studio:
1. Right-click on the **Solution** in Solution Explorer
2. Select **Restore NuGet Packages**
3. Wait for restoration to complete

Or from command line:
```bash
nuget restore Vortex.slnx
```

### Step 5: Verify Package Installation
1. Right-click **Engine** project → **Manage NuGet Packages**
2. Click **Installed** tab
3. You should see:
   - **Assimp** version **3.0.0** ✅
   - **Assimp.redist** version **3.0.0** ✅

If you still see 5.3.1, the cache hasn't been cleared properly.

## Alternative: Manual Package Installation

If automatic restore fails:

### Option 1: Remove and Reinstall
1. In Visual Studio, go to **Tools** → **NuGet Package Manager** → **Package Manager Console**
2. Run:
   ```powershell
   Uninstall-Package Assimp -ProjectName Engine -Force
   Uninstall-Package Assimp.redist -ProjectName Engine -Force
   Install-Package Assimp -Version 3.0.0 -ProjectName Engine
   Install-Package Assimp.redist -Version 3.0.0 -ProjectName Engine
   ```

### Option 2: Manual Download
If NuGet installation still fails, download manually:
1. Download Assimp from: https://github.com/assimp/assimp/releases
2. Follow manual installation instructions in `BUILD_SETUP.md` (Option B)

## Current Status

✅ **packages.config is correct** (version 3.0.0)
✅ **BUILD_SETUP.md updated** with correct instructions
✅ **Documentation updated** with troubleshooting

The code is ready - you just need to clear your local cache and pull the latest changes.

## Why Version 3.0.0?

- NuGet only provides **Assimp 3.0.0** for native C++ projects
- Version 5.3.1 doesn't exist on NuGet for native C++
- Version 3.0.0 works fine for FBX, OBJ, GLTF import
- For version 5.x features, use manual installation (see BUILD_SETUP.md)

## Still Having Issues?

If problems persist after following all steps:
1. Delete the `packages` folder in your solution directory
2. Delete the `bin` and `obj` folders in Engine project
3. Rebuild solution from scratch

## Contact
If none of these solutions work, please provide:
- Visual Studio version
- Output from: `dotnet --version`
- Screenshot of NuGet Package Manager after clearing cache
