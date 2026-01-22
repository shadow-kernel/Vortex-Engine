# Vortex Engine - Build Setup Guide

## Prerequisites

### Required Tools
- Visual Studio 2022 or later (with C++ Desktop Development workload)
- .NET 8.0 SDK or later
- Windows 10/11 SDK

### External Dependencies

The Vortex Engine requires the following external libraries:

#### 1. Assimp (Open Asset Import Library)

Assimp is required for importing 3D models (FBX, OBJ, GLTF, etc.).

**Option A: NuGet Package (Recommended)**

1. Open the solution in Visual Studio
2. Right-click on the `Engine` project → Manage NuGet Packages
3. Search for "Assimp" and install both:
   - `Assimp` (version 5.3.1 or later)
   - `Assimp.redist` (redistributable binaries)
4. **Important:** Add `VORTEX_USE_ASSIMP` to preprocessor definitions:
   - Right-click Engine project → Properties
   - C/C++ → Preprocessor → Preprocessor Definitions
   - Add: `VORTEX_USE_ASSIMP;%(PreprocessorDefinitions)`
5. Rebuild the Engine project

**Option B: Manual Installation**

1. Download Assimp from: https://github.com/assimp/assimp/releases
2. Extract to a convenient location (e.g., `C:\Libraries\assimp`)
3. Update the Engine project properties:
   - Configuration Properties → C/C++ → General → Additional Include Directories
     - Add: `C:\Libraries\assimp\include`
   - Configuration Properties → Linker → General → Additional Library Directories
     - Add: `C:\Libraries\assimp\lib\x64` (for x64 builds)
   - Configuration Properties → Linker → Input → Additional Dependencies
     - Add: `assimp-vc143-mt.lib` (Debug: `assimp-vc143-mtd.lib`)
   - Configuration Properties → C/C++ → Preprocessor → Preprocessor Definitions
     - Add: `VORTEX_USE_ASSIMP`

4. Copy the Assimp DLL to the output directory:
   - `assimp-vc143-mt.dll` → `$(OutputPath)`

#### 2. stb_image (Header-Only)

**Already Included!** The stb_image library is included in `Engine/ThirdParty/stb_image.h`. No additional setup required.

**Note:** Model import (FBX/OBJ/GLTF) requires Assimp. Without it, the engine will still compile and run, but only native .vmesh format and procedural meshes will be available. Texture import works without Assimp.

#### 3. DirectX 12 and DirectXMath

These are included with the Windows SDK. Ensure you have Windows 10 SDK version 10.0.19041.0 or later installed.

## Building the Engine

### Command Line Build

```bash
# Restore NuGet packages
dotnet restore Vortex.slnx

# Build Engine (C++)
msbuild Engine/Engine.vcxproj /p:Configuration=Debug /p:Platform=x64

# Build VortexAPI (C++ DLL)
msbuild VortexAPI/VortexAPI.vcxproj /p:Configuration=Debug /p:Platform=x64

# Build Editor (C#)
dotnet build Editor.csproj
```

### Visual Studio Build

1. Open `Vortex.slnx` in Visual Studio
2. Set the build configuration to `Debug | x64`
3. Build → Build Solution (Ctrl+Shift+B)

### Build Configuration Notes

- **Platform**: Use `x64` (Win32/x86 not fully supported)
- **Configuration**: 
  - `Debug`: Full debugging symbols, no optimization
  - `Release`: Optimized, minimal debug info

## Project Structure

```
Vortex-Engine/
├── Engine/               # Core engine (C++ static library)
│   ├── Graphics/
│   │   ├── Importers/   # Model/Texture import system
│   │   ├── Resources/   # Mesh, Material, Texture classes
│   │   └── DX12/        # DirectX 12 renderer
│   ├── ThirdParty/      # Third-party headers (stb_image)
│   └── packages.config  # NuGet packages
├── VortexAPI/           # C++ DLL for editor interop
├── Editor/              # C# WPF editor application
└── EngineTest/          # C++ unit tests

```

## Troubleshooting

### Issue: "Cannot find assimp/Importer.hpp"
**Solution**: Install the Assimp NuGet package or verify manual installation paths.

### Issue: "Unresolved external symbol in assimp"
**Solution**: 
- Ensure you're linking the correct Assimp library variant
- Debug builds need `assimp-vc143-mtd.lib`
- Release builds need `assimp-vc143-mt.lib`

### Issue: "stb_image.h not found"
**Solution**: The file should be in `Engine/ThirdParty/stb_image.h`. If missing, download from:
https://raw.githubusercontent.com/nothings/stb/master/stb_image.h

### Issue: "Multiple definition of stbi_load"
**Solution**: `#define STB_IMAGE_IMPLEMENTATION` should only appear in ONE .cpp file (TextureImporter.cpp).

### Issue: Runtime error - "assimp DLL not found"
**Solution**: Copy the Assimp DLL to the same directory as VortexAPI.dll:
- From: `packages\Assimp.redist.5.3.1\build\native\bin\x64\`
- To: Output directory (usually `x64\Debug\` or `x64\Release\`)

## Platform Support

Currently supported:
- ✅ Windows 10/11 (x64)
- ❌ macOS (future)
- ❌ Linux (future)

## Additional Information

- Engine uses C++20 features
- Editor uses C# 12 with .NET 8
- Minimum Visual Studio version: 2022 (v143 toolset)
- DirectX 12 Feature Level: 12.0 minimum

For more information, see:
- Model Import System: `Engine/Graphics/Importers/README.md`
- Engine Documentation: (coming soon)
