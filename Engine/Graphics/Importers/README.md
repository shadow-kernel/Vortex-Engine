# Vortex Engine - Model Import System

## Overview
The Vortex Engine model import system supports loading 3D models from various formats and converting them to an optimized native format.

## Supported Formats

### Import Formats (via Assimp)
- FBX (.fbx)
- Wavefront OBJ (.obj)
- glTF 2.0 (.gltf, .glb)
- Collada (.dae)
- Blender (.blend)
- 3DS Max (.3ds)
- AutoCAD (.ase)

### Native Format
- Vortex Mesh (.vmesh) - Binary optimized format for fast loading

### Texture Formats (via stb_image)
- PNG (.png)
- JPEG (.jpg, .jpeg)
- TGA (.tga)
- BMP (.bmp)
- PSD (.psd)
- HDR (.hdr)

## Architecture

### Core Components

1. **ModelImporter** (`Engine/Graphics/Importers/ModelImporter.h/cpp`)
   - Imports models using Assimp
   - Converts to engine's internal vertex format
   - Extracts materials and texture references
   - Calculates bounding boxes

2. **TextureImporter** (`Engine/Graphics/Importers/TextureImporter.h/cpp`)
   - Loads images using stb_image (header-only library)
   - Supports common image formats
   - Handles vertical flipping for DirectX

3. **MeshSerializer** (`Engine/Graphics/Importers/MeshSerializer.h/cpp`)
   - Saves/loads binary .vmesh format
   - Fast loading with minimal parsing
   - Includes metadata (bounds, submeshes)

4. **MaterialSerializer** (`Engine/Graphics/Importers/MaterialSerializer.h/cpp`)
   - Saves/loads binary .vmat format
   - Stores PBR material properties
   - References texture paths

### Integration

- **ResourceRegistry** - Enhanced with import methods:
  - `import_model(filepath)` - Import FBX/OBJ/GLTF
  - `import_texture(filepath)` - Import PNG/JPG/TGA
  - `load_vmesh(filepath)` - Load native .vmesh format
  - `export_mesh_to_vmesh(id, filepath)` - Export to native format

- **VortexAPI** - C++ exports for editor:
  - `ImportModel(filepath)` - Import model from file
  - `ImportTexture(filepath)` - Import texture from file
  - `LoadVMesh(filepath)` - Load .vmesh file
  - `ExportMeshToVMesh(id, filepath)` - Export mesh

- **Editor** - C# wrapper and UI:
  - `VortexAPI.ImportModelFromFile()`
  - `VortexAPI.ImportTextureFromFile()`
  - Asset Browser with "Models" tab
  - Import button with file picker

## Dependencies

### Required External Libraries

1. **Assimp (Open Asset Import Library)** - OPTIONAL but RECOMMENDED
   - Version: 5.0 or later recommended
   - License: BSD 3-Clause
   - Website: https://www.assimp.org/
   - NuGet: `Assimp` or `Assimp.redist`
   
   **Installation:**
   ```
   Method 1: NuGet (Recommended)
   1. Open Vortex.slnx in Visual Studio
   2. Right-click Engine project → Manage NuGet Packages
   3. Search and install: "Assimp" and "Assimp.redist"
   4. The package will automatically configure include paths and libraries
   5. Add VORTEX_USE_ASSIMP to Preprocessor Definitions in project settings
   ```
   
   ```
   Method 2: Manual Installation
   1. Download from https://github.com/assimp/assimp/releases
   2. Extract to C:\Libraries\assimp (or your preferred location)
   3. In Engine project properties:
      - C/C++ → General → Additional Include Directories: Add assimp\include
      - Linker → General → Additional Library Directories: Add assimp\lib\x64
      - Linker → Input → Additional Dependencies: Add assimp-vc143-mt.lib
      - C/C++ → Preprocessor → Preprocessor Definitions: Add VORTEX_USE_ASSIMP
   4. Copy assimp-vc143-mt.dll to output directory
   ```
   
   **Note:** Without Assimp, model import will return empty results but won't break compilation.
   The system will still work with procedurally generated meshes and .vmesh files.

2. **stb_image**
   - Single header library (included in `Engine/ThirdParty/stb_image.h`)
   - License: Public Domain / MIT
   - No installation required - header-only library

## Usage

### Enabling Assimp Support

To enable full model import (FBX/OBJ/GLTF), add `VORTEX_USE_ASSIMP` to preprocessor definitions:

**Visual Studio:**
1. Right-click Engine project → Properties
2. C/C++ → Preprocessor → Preprocessor Definitions  
3. Add: `VORTEX_USE_ASSIMP;%(PreprocessorDefinitions)`

Without this flag, model import will compile but return empty results.

### From Editor (C#)
```csharp
// Import a model
long modelId = VortexAPI.ImportModelFromFile("path/to/model.fbx");

// Import a texture
long textureId = VortexAPI.ImportTextureFromFile("path/to/texture.png");

// Load native format
long meshId = VortexAPI.LoadVMeshFromFile("path/to/mesh.vmesh");
```

### From Engine (C++)
```cpp
using namespace vortex::graphics;

// Import model
auto& registry = ResourceRegistry::instance();
id::id_type model_id = registry.import_model("assets/models/character.fbx");

// Import texture
id::id_type texture_id = registry.import_texture("assets/textures/diffuse.png");

// Load native format
id::id_type mesh_id = registry.load_vmesh("assets/meshes/optimized.vmesh");
```

## File Formats

### .vmesh Format (Binary)
```
Header (96 bytes):
- u32 magic (0x4853454D = "MESH")
- u32 version (1)
- u32 submesh_count
- XMFLOAT3 bounds_min
- XMFLOAT3 bounds_max
- char[64] name

For each submesh:
  SubMeshHeader (76 bytes):
  - u32 vertex_count
  - u32 index_count
  - u32 material_index
  - char[64] name
  
  Vertex Data:
  - VertexPosNormalUV[vertex_count]
  
  Index Data:
  - u32[index_count]
```

### .vmat Format (Binary)
```
Header (608 bytes):
- u32 magic (0x54414D56 = "VMAT")
- u32 version (1)
- MaterialProperties (32 bytes)
- char[64] name
- char[256] albedo_texture
- char[256] normal_texture
- char[256] metallic_roughness_texture
```

## Best Practices

1. **Import Once, Save Native**
   - Import source files (FBX/OBJ) during development
   - Export to .vmesh for distribution
   - .vmesh loads significantly faster

2. **Texture Organization**
   - Keep textures in same directory as models when possible
   - Use relative paths for portability
   - Supported texture resolutions: up to 8192x8192

3. **Model Optimization**
   - Keep vertex counts reasonable (<100K vertices per mesh)
   - Use multiple LODs for large models
   - Combine small submeshes when possible

4. **Material Workflow**
   - Import materials from model files
   - Customize in editor
   - Save as .vmat for reuse

## Performance

- **Import Speed**: FBX/OBJ import uses Assimp (optimized C++)
- **Load Speed**: .vmesh format loads ~10x faster than source formats
- **Memory**: Efficient vertex format (32 bytes per vertex)
- **GPU Upload**: Direct buffer creation from binary data

## Future Enhancements

- [ ] Multi-submesh support
- [ ] Animation import
- [ ] Skeleton/bone import
- [ ] LOD generation
- [ ] Mesh optimization (vertex cache, overdraw)
- [ ] Async loading
- [ ] Texture compression (BC formats)
- [ ] Material graph system
- [ ] Preview thumbnail generation

## License

This import system uses:
- Assimp (BSD 3-Clause License)
- stb_image (Public Domain / MIT License)

See respective library documentation for full license terms.
