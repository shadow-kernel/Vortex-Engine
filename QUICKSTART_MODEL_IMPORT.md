# Quick Start: Model Import Feature

This guide will help you enable and use the 3D model import feature in Vortex Engine.

## Step 1: Enable Assimp (Optional but Recommended)

The model import feature uses Assimp for FBX/OBJ/GLTF support. You can skip this if you only plan to use the native .vmesh format.

### Using NuGet (Easiest)

1. Open `Vortex.slnx` in Visual Studio 2022
2. In Solution Explorer, right-click the **Engine** project
3. Select **Manage NuGet Packages**
4. Click **Browse** tab
5. Search for "**Assimp**"
6. Install both packages:
   - ✅ **Assimp** (main library)
   - ✅ **Assimp.redist** (runtime DLLs)
7. Right-click Engine project → **Properties**
8. Navigate to: **C/C++** → **Preprocessor** → **Preprocessor Definitions**
9. Add: **VORTEX_USE_ASSIMP** (important!)
10. Click **OK** and rebuild the project

### Verify Installation

After installing Assimp, rebuild the Engine project. If successful, you should see no compile errors.

## Step 2: Build the Solution

```bash
# In Visual Studio
Build → Rebuild Solution (Ctrl+Shift+B)

# Or from command line
msbuild Vortex.slnx /p:Configuration=Debug /p:Platform=x64
```

## Step 3: Run the Editor

1. Set **Editor** as the startup project (right-click → Set as Startup Project)
2. Press **F5** to run
3. The editor should launch with the 3D viewport

## Step 4: Import Your First Model

### Using the Asset Browser

1. In the editor, find the **Asset Browser** panel (usually bottom-left)
2. Click the **Models** tab
3. Click the **Import** button (📥 icon in top-right)
4. Browse to your 3D model file (.fbx, .obj, .gltf, etc.)
5. Click **Open**
6. The model should appear in the Models list

### From Code (C#)

```csharp
using Editor.DllWrapper;

// Import a model
long modelId = VortexAPI.ImportModelFromFile("path/to/model.fbx");

// Import a texture
long textureId = VortexAPI.ImportTextureFromFile("path/to/texture.png");

// Load native format
long meshId = VortexAPI.LoadVMeshFromFile("path/to/mesh.vmesh");
```

### From Engine (C++)

```cpp
#include "Graphics/Resources/ResourceRegistry.h"

using namespace vortex::graphics;

auto& registry = ResourceRegistry::instance();

// Import model
id::id_type model_id = registry.import_model("assets/character.fbx");

// Import texture
id::id_type tex_id = registry.import_texture("assets/diffuse.png");
```

## Step 5: Use Imported Models

Once imported, models can be used like any other mesh:

1. Create a game entity
2. Add a MeshRenderer component
3. Assign the imported model ID to the mesh property
4. The model will render in the viewport

## Supported Formats

### 3D Models (with Assimp)
- ✅ FBX (.fbx) - Autodesk Filmbox
- ✅ OBJ (.obj) - Wavefront Object
- ✅ glTF (.gltf, .glb) - GL Transmission Format
- ✅ Collada (.dae)
- ✅ Blender (.blend)
- ✅ 3DS Max (.3ds)

### Textures (always available)
- ✅ PNG (.png)
- ✅ JPEG (.jpg, .jpeg)
- ✅ TGA (.tga)
- ✅ BMP (.bmp)
- ✅ PSD (.psd)
- ✅ HDR (.hdr)

### Native Format
- ✅ Vortex Mesh (.vmesh) - No Assimp required!

## Troubleshooting

### "Model import failed"
- **Check**: Is VORTEX_USE_ASSIMP defined in preprocessor?
- **Check**: Is Assimp NuGet package installed?
- **Check**: Does the file path exist?
- **Check**: Is the model format supported?

### "Cannot find assimp DLL"
- **Solution**: The Assimp.redist package should copy DLLs automatically
- **Manual Fix**: Copy `assimp-vc143-mt.dll` from NuGet packages to output folder

### "Model imports but looks wrong"
- Models use right-handed coordinate system
- Y-up is default
- Check your modeling software export settings

### "Import button does nothing"
If Assimp is not installed:
- Only .vmesh files will work
- Install Assimp NuGet package to enable full import support

## Performance Tips

1. **Use .vmesh for distribution**
   - Import source files once
   - Export to .vmesh format
   - .vmesh loads 10x faster

2. **Optimize models before import**
   - Keep vertex count < 100K per mesh
   - Combine materials where possible
   - Remove unnecessary geometry

3. **Texture sizes**
   - Use power-of-2 dimensions (512, 1024, 2048)
   - Maximum recommended: 4096x4096
   - Use DDS for large textures (future feature)

## Next Steps

- ✅ Import models
- ⏭️ Create materials (.vmat)
- ⏭️ Set up textures on materials
- ⏭️ Export optimized .vmesh files
- ⏭️ Build your game!

## Support

For issues or questions:
- Check: `Engine/Graphics/Importers/README.md` (detailed documentation)
- Check: `BUILD_SETUP.md` (build configuration)
- GitHub Issues: Report bugs and feature requests

## Example Workflow

```
1. Model in Blender/Maya → Export as FBX
2. Import via Editor → Models tab → Import button
3. [Optional] Export as .vmesh for faster loading
4. Create material
5. Assign textures
6. Add to scene
7. Done! 🎉
```

---

**Note:** This is a new feature. Report any issues you encounter!
