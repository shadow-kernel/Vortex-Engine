# Vortex Engine - Features

## Overview
Vortex Engine is a modern game engine with DirectX 12 rendering, asset management, and a powerful editor.

## Core Features

### 1. Asset Management System

#### GUID-Based References
- Every asset has a unique GUID that never changes
- References use GUIDs instead of paths - assets can be moved/renamed safely
- `.vmeta` files store metadata alongside each asset
- Automatic metadata generation for existing assets

#### Supported Asset Types
- **Meshes**: .fbx, .obj, .gltf, .glb, .dae, .blend, .3ds, .vmesh (native)
- **Textures**: .png, .jpg, .jpeg, .tga, .bmp, .psd, .hdr, .dds
- **Materials**: .vmat (native binary format)
- **Scenes**: .vscene (native binary format)
- **Prefabs**: .ventity (entity prefabs)
- **Shaders**: .hlsl, .glsl, .shader
- **Audio**: .wav, .mp3, .ogg, .flac
- **Fonts**: .ttf, .otf
- **Scripts**: .cs, .cpp, .h

#### Dependency Tracking
- Automatic dependency tracking (materials reference textures, etc.)
- Dependency resolver finds what depends on what
- Orphan detection - finds unused dependencies
- Circular dependency detection

#### Cascading Delete
- Safe asset deletion with dependency analysis
- Shows what will be affected before deleting
- Automatically deletes orphaned dependencies
- Prevents deletion if asset is referenced elsewhere

### 2. Model Import System

#### Assimp Integration (Optional)
- Import from industry-standard formats
- Automatic vertex and index extraction
- Material reference parsing
- Bounding box calculation

#### Native Binary Formats
- `.vmesh` - 10x faster loading than source formats
- `.vmat` - Compact material storage
- Version checking and validation
- Submesh support

#### Texture Import
- PNG, JPG, TGA, BMP, PSD, HDR support
- Multi-channel support (R, RG, RGB, RGBA)
- Automatic format detection
- DirectX coordinate flipping

### 3. Runtime Asset Loading

#### Engine/Editor Separation
- Asset loading works in both engine and editor
- No editor dependency in exported games
- C++ AssetDatabase for runtime
- Asset manifest system for packaged games

#### GUID-Based Loading
```cpp
// C++ (Engine)
auto handle = resource_manager::load_mesh_by_guid("guid-string");
auto handle = resource_manager::load_texture_by_guid("guid-string");
```

```csharp
// C# (Editor)
long meshId = VortexAPI.LoadMeshByGuid("guid-string");
long textureId = VortexAPI.LoadTextureByGuid("guid-string");
```

#### Reference Counting
- Automatic resource lifetime management
- Resources unloaded when reference count reaches 0
- Thread-safe reference counting
- Get reference count for diagnostics

### 4. Drag-and-Drop Support

#### Windows Explorer Integration
- Drag models directly from file explorer
- Supported formats: .fbx, .obj, .gltf, .vmesh
- Auto-import into project
- Creates entity with MeshRenderer automatically

#### Asset Browser Integration
- Drag assets from Asset Browser to viewport
- GUID-based dropping
- Visual feedback during drag
- Type-safe dropping (only meshes create entities)

### 5. Project Structure

#### File-Based Architecture
- Individual files for each asset
- Scenes as separate .vscene files
- Prefabs as .ventity files
- No single monolithic project file

#### Project Manifest
- `project.vortex` - Project metadata
- Scene references
- Last opened scene
- Thumbnail path

#### Folder Structure
```
MyProject/
├── project.vortex          # Project manifest
├── Assets/
│   ├── Materials/          # Material files
│   ├── Models/             # 3D models
│   ├── Scenes/             # Scene files
│   ├── Textures/           # Texture files
│   ├── Prefabs/            # Entity prefabs
│   ├── Scripts/            # Code files
│   ├── Audio/              # Sound files
│   └── Shaders/            # Shader files
├── Packages/               # Third-party packages
└── ProjectSettings/        # Project settings
```

### 6. Entity Component System (ECS)

#### Components
- Transform - Position, rotation, scale
- MeshRenderer - 3D mesh rendering
- Camera - View and projection
- Light - Lighting (planned)
- RigidBody - Physics (planned)
- AudioSource - Sound playback (planned)

#### Entity Features
- Hierarchical parent/child relationships
- Component-based architecture
- Serialization support
- Prefab system

### 7. Rendering System

#### DirectX 12
- Modern graphics API
- Multi-viewport rendering
- PBR material system
- Deferred rendering (planned)

#### Camera System
- Multiple cameras
- Camera preview
- Viewport navigation
- Gizmo rendering

#### Visual Features
- Grid rendering
- Transform gizmos
- Wireframe mode (planned)
- Skybox (planned)

### 8. Editor Features

#### Asset Browser
- View all project assets
- Filter by type
- Import models and textures
- Assimp support detection

#### File Explorer
- Navigate project files
- Synchronize with file system
- Drag-and-drop support

#### Scene Hierarchy
- View all entities
- Parent/child relationships
- Selection management
- Multi-selection (planned)

#### Inspector
- Edit component properties
- Add/remove components
- Asset pickers
- Real-time updates

#### Undo/Redo
- Full undo/redo stack
- Collection operations
- Property changes
- Command pattern

### 9. Serialization

#### Formats
- JSON - Human-readable (metadata, manifests)
- Binary - Performance (scenes, prefabs, meshes, materials)

#### DataContract Serialization
- .NET DataContract for C# objects
- Custom serializers for engine types
- Version compatibility

### 10. Build System

#### Dependencies
- .NET Framework 4.8
- DirectX 12
- Assimp (optional, via NuGet)
- stb_image (included)

#### Project Types
- C++/CLI Engine (C++20)
- C# Editor (WPF)
- C# Tests (planned)

## Performance

### Asset Loading
- FBX import: ~100-500ms
- .vmesh load: ~10-50ms (10x faster!)
- Binary serialization for speed
- Lazy loading support

### Memory Management
- Reference counting for resources
- Automatic cleanup
- Resource pooling (planned)

## Scalability

### Large Projects
- GUID-based references scale infinitely
- Asset database indexing
- Incremental asset scanning
- Async loading (planned)

### Multi-Threading
- Thread-safe resource manager
- Async resource loading (planned)
- Background asset processing (planned)

## Security

### Code Quality
- CodeQL security scanning
- Zero vulnerabilities
- Buffer overrun prevention
- Input validation

### Best Practices
- RAII resource management
- Const correctness
- Modern C++17/C++20
- Exception safety

## Future Enhancements

### Short-term
- Animation system
- Skeleton/bone support
- LOD generation
- Thumbnail generation

### Medium-term
- Physics integration
- Audio system
- Particle system
- Post-processing effects

### Long-term
- Cross-platform (Vulkan, Metal)
- Scripting system
- Networking
- VR support

## Technical Specifications

### Code Metrics
- C++ Lines: ~15,000
- C# Lines: ~20,000
- Average File Size: <500 lines
- Architecture: Clean, modular

### Supported Platforms
- Windows 10/11 (DirectX 12)
- More platforms planned

### System Requirements
- Windows 10/11 64-bit
- DirectX 12 capable GPU
- 8GB RAM minimum
- Visual Studio 2019+ for building

---

**Last Updated:** January 2025  
**Version:** Development
