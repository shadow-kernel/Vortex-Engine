# Vortex Engine - Usage Guide

## Table of Contents
- [Getting Started](#getting-started)
- [Working with Assets](#working-with-assets)
- [Using the Asset Database](#using-the-asset-database)
- [Importing Models](#importing-models)
- [Drag-and-Drop](#drag-and-drop)
- [Deleting Assets Safely](#deleting-assets-safely)
- [Working with Scenes](#working-with-scenes)
- [Using References](#using-references)
- [Best Practices](#best-practices)
- [Troubleshooting](#troubleshooting)

## Getting Started

### Creating a New Project

1. Launch the Vortex Editor
2. Click "New Project"
3. Choose a name and location
4. The editor will create the default project structure

### Opening an Existing Project

1. Launch the Vortex Editor
2. Click "Open Project"
3. Navigate to the project folder
4. Select the `project.vortex` file

### Project Structure

Your project folder contains:
```
MyProject/
├── project.vortex          # Project manifest (DO NOT EDIT MANUALLY)
├── Assets/                 # All your game assets
│   ├── Scenes/            # Scene files (.vscene)
│   ├── Models/            # 3D models
│   ├── Textures/          # Images
│   ├── Materials/         # Material files (.vmat)
│   └── ...                # Other asset folders
└── .ve/                    # Hidden editor data
```

## Working with Assets

### Understanding Asset Metadata

Every asset has a `.vmeta` file:
```
MyModel.fbx           # The actual asset
MyModel.fbx.vmeta     # Metadata (GUID, dependencies, settings)
```

**Important:** Never delete `.vmeta` files manually! They contain critical information.

### Asset Browser

The Asset Browser shows all project assets:

1. **Tabs**: Switch between Scenes, Meshes, Materials, Textures
2. **Import Button**: Import new assets
3. **Search**: Filter assets by name
4. **Grid/List View**: Change how assets are displayed

### File Explorer

The File Explorer shows your project's file system:

1. Navigate folders like Windows Explorer
2. Right-click for context menu (planned)
3. Double-click to open/import
4. Sync with external changes

## Using the Asset Database

### What is the Asset Database?

The Asset Database:
- Tracks all assets in your project
- Assigns unique GUIDs to each asset
- Manages dependencies between assets
- Enables safe moving/renaming

### How It Works

When you load a project:
1. Asset Database scans the `Assets` folder
2. Loads `.vmeta` files for existing assets
3. Generates `.vmeta` for assets without one
4. Builds an index of all assets

### Asset GUIDs

Every asset has a GUID (Globally Unique Identifier):
```
Model.fbx.vmeta:
{
  "Guid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "Type": "Mesh",
  "RelativePath": "Assets/Models/Model.fbx",
  ...
}
```

**Benefits:**
- Move/rename assets without breaking references
- Reliable cross-references between assets
- Duplicate detection

## Importing Models

### Supported Formats

**With Assimp (optional):**
- .fbx - Autodesk FBX
- .obj - Wavefront OBJ
- .gltf/.glb - GL Transmission Format
- .dae - Collada
- .blend - Blender
- .3ds - 3D Studio

**Always available:**
- .vmesh - Vortex native format

### Installing Assimp Support

1. Open Visual Studio
2. Right-click the Engine project
3. Manage NuGet Packages
4. Install "Assimp" and "Assimp.redist"
5. Rebuild the solution

### Importing a Model (Manual)

1. Open Asset Browser
2. Click "Models" tab
3. Click "Import Model"
4. Select your model file
5. Wait for import to complete

### Importing a Model (Drag-and-Drop)

1. Open Windows Explorer
2. Drag .fbx/.obj/.gltf file to viewport
3. Model is automatically imported and placed in scene

### Native Format (.vmesh)

For best performance, export to .vmesh:

```csharp
// In editor
VortexAPI.ExportMeshToVMesh(meshId, "path/to/output.vmesh");
```

**Advantages:**
- 10x faster loading
- Optimized for engine
- Smaller file size
- No import processing

## Drag-and-Drop

### From Windows Explorer

**Supported Files:**
- .fbx, .obj, .gltf, .glb, .dae, .blend, .3ds
- .vmesh

**Steps:**
1. Open Windows Explorer
2. Navigate to your model
3. Drag file to viewport
4. Model imports and entity is created automatically

### From Asset Browser

**Steps:**
1. Select a mesh asset in Asset Browser
2. Drag to viewport
3. Entity with MeshRenderer is created

### Tips

- Imported models are copied to `Assets/Models/`
- A `.vmeta` file is created automatically
- The entity is named after the file
- Position is currently at origin (raycasting planned)

## Deleting Assets Safely

### Why Safe Deletion Matters

Deleting an asset that other assets depend on breaks your project. The engine prevents this.

### Deletion Analysis

Before deleting, the system checks:
1. **Dependents** - What references this asset?
2. **Dependencies** - What does this asset reference?
3. **Orphans** - What becomes unused if deleted?

### Deleting an Asset

**Manual (API):**
```csharp
var deletionService = new AssetDeletionService(AssetDatabase.Instance);
var analysis = deletionService.AnalyzeDeletion(assetGuid);

if (analysis.CanDelete)
{
    var summary = deletionService.GetDeletionSummary(assetGuid);
    if (MessageBox.Show(summary, "Confirm Delete", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
    {
        deletionService.DeleteAsset(assetGuid, deleteOrphans: true);
    }
}
else
{
    MessageBox.Show(analysis.WarningMessage, "Cannot Delete");
}
```

### Cascading Delete

When deleting an asset with orphaned dependencies:
```
Delete "Character.fbx"?

This will also delete 3 unused dependencies:
  • CharacterTexture.png (Texture)
  • CharacterMaterial.vmat (Material)
  • CharacterNormals.png (Texture)

Are you sure?
```

### Cannot Delete Example

```
Cannot delete 'Concrete.png'

Referenced by:
  • WallMaterial.vmat (Material)
  • FloorMaterial.vmat (Material)
  • PillarMaterial.vmat (Material)
```

## Working with Scenes

### Creating a Scene

1. File → New Scene
2. Enter scene name
3. Scene saved to `Assets/Scenes/`

### Saving a Scene

- **Manual:** File → Save Scene
- **Auto:** Triggered on project save
- Format: Binary .vscene

### Loading a Scene

1. Double-click scene in Asset Browser
2. Or: File → Open Scene

### Scene Files

Scenes are stored as `.vscene` files:
- Binary format for performance
- Contains all entities and components
- References assets by GUID

## Using References

### Why References?

Instead of storing paths:
```csharp
// OLD (breaks if file moves)
MeshPath = "Assets/Models/Character.fbx"

// NEW (never breaks)
MeshGuid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

### In C# (Editor)

```csharp
// Create a reference
var assetRef = new AssetReference(assetGuid, AssetType.Mesh);

// Resolve to path
var asset = AssetDatabase.Instance.GetAsset(assetRef.Guid);
var path = AssetDatabase.Instance.GetAssetPath(assetRef.Guid);
```

### In C++ (Engine)

```cpp
// Load by GUID
auto mesh_handle = resource_manager::load_mesh_by_guid("guid-string");
auto texture_handle = resource_manager::load_texture_by_guid("guid-string");

// Asset database resolves GUID to path
runtime::AssetDatabase::instance().get_asset_path_by_guid("guid");
```

### Dependencies

When asset A references asset B:
```csharp
// Material references texture
var material = AssetDatabase.Instance.GetAsset(materialGuid);
material.Dependencies.Add(textureGuid);
AssetDatabase.Instance.UpdateMetadata(material);
```

The system now knows:
- Material depends on Texture
- Texture is depended upon by Material

## Best Practices

### Asset Organization

**DO:**
- ✓ Use clear folder structure
- ✓ Name files descriptively
- ✓ Keep related assets together
- ✓ Use subfolders for large projects

**DON'T:**
- ✗ Put everything in root Assets folder
- ✗ Use special characters in names
- ✗ Manually edit .vmeta files
- ✗ Copy/paste assets outside editor

### Working with Models

**DO:**
- ✓ Export to .vmesh for production
- ✓ Use realistic scale (1 unit = 1 meter)
- ✓ Name meshes clearly
- ✓ Import textures separately first

**DON'T:**
- ✗ Import huge models (>1M triangles) without optimization
- ✗ Leave unused materials/textures
- ✗ Use non-power-of-2 textures (in some cases)

### Asset References

**DO:**
- ✓ Always use GUIDs
- ✓ Let Asset Database manage paths
- ✓ Check dependencies before deleting

**DON'T:**
- ✗ Store absolute paths
- ✗ Hardcode asset paths in code
- ✗ Bypass Asset Database

### Performance

**DO:**
- ✓ Use .vmesh for frequently-loaded models
- ✓ Compress textures appropriately
- ✓ Unload unused assets
- ✓ Use asset pooling (when available)

**DON'T:**
- ✗ Keep all assets loaded at once
- ✗ Import models without optimization
- ✗ Use uncompressed textures for large images

## Troubleshooting

### "Asset not found"

**Cause:** GUID doesn't match any asset

**Solution:**
1. Check if asset file exists
2. Check if .vmeta file exists
3. Refresh Asset Database
4. Re-import if necessary

### "Cannot import model: Assimp not available"

**Cause:** Assimp NuGet package not installed

**Solution:**
1. Install Assimp via NuGet (see [Importing Models](#installing-assimp-support))
2. Or convert to .vmesh format

### "Cannot delete asset: referenced by..."

**Cause:** Other assets depend on this one

**Solution:**
1. Remove references in dependent assets
2. Or delete dependent assets first
3. Or accept that it cannot be deleted

### Missing textures after import

**Cause:** Texture paths in model are absolute or incorrect

**Solution:**
1. Import textures manually first
2. Update material to reference textures by GUID
3. Or use relative paths in modeling software

### Slow asset loading

**Cause:** Using source formats (.fbx, .obj) instead of .vmesh

**Solution:**
1. Export frequently-used models to .vmesh
2. Load .vmesh instead of source format
3. 10x loading speedup

### Asset Database not updating

**Cause:** External file changes not detected

**Solution:**
1. Manually refresh: AssetDatabase.Instance.Refresh()
2. Restart editor
3. Check file system permissions

## Advanced Topics

### Custom Asset Types

You can extend the asset system:

```csharp
// Define new asset type
public enum AssetType
{
    // ... existing types
    CustomData = 100
}

// Determine type from extension
private AssetType DetermineAssetType(string extension)
{
    return extension switch
    {
        ".customdata" => AssetType.CustomData,
        _ => AssetType.Unknown
    };
}
```

### Asset Manifest (Runtime)

For packaged games, create a manifest:

```csharp
// Export manifest
// (Implementation planned)
```

```cpp
// Load in game
runtime::AssetDatabase::instance().initialize_with_manifest("game.manifest");
```

### Dependency Resolver

Advanced dependency queries:

```csharp
var resolver = new DependencyResolver(AssetDatabase.Instance);

// What depends on this?
var dependents = resolver.GetDependents(assetGuid);

// What does this depend on?
var dependencies = resolver.GetDependencies(assetGuid);

// Recursive dependencies
var allDeps = resolver.GetDependenciesRecursive(assetGuid);

// Orphans
var orphans = resolver.GetOrphanedDependencies(assetGuid);

// Reference count
var refCount = resolver.GetReferenceCount(assetGuid);
```

---

**Need Help?**  
- Check [FEATURES.md](FEATURES.md) for feature documentation
- Check [QUICKSTART_MODEL_IMPORT.md](QUICKSTART_MODEL_IMPORT.md) for quick start
- Check [BUILD_SETUP.md](BUILD_SETUP.md) for build instructions

**Last Updated:** January 2025
