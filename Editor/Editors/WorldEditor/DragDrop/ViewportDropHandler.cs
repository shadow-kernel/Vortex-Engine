using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Dialogs;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace Editor.Editors.WorldEditor.DragDrop
{
    /// <summary>
    /// Handles drag-and-drop operations for the viewport.
    /// Supports dropping models from Windows Explorer and Asset Browser.
    /// </summary>
    public class ViewportDropHandler
    {
        private readonly Scene _scene;
        private readonly AssetDatabase _assetDatabase;

        public ViewportDropHandler(Scene scene)
        {
            _scene = scene ?? throw new ArgumentNullException(nameof(scene));
            _assetDatabase = AssetDatabase.Instance;
        }

        /// <summary>
        /// Determines if the drag data can be accepted.
        /// </summary>
        public bool CanAcceptDrop(IDataObject data)
        {
            if (data == null)
                return false;

            // Check for file drop from Windows Explorer
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    return IsAcceptedFileType(files[0]);
                }
            }

            // Check for asset path from Asset Browser
            if (data.GetDataPresent("AssetPath"))
            {
                var path = data.GetData("AssetPath") as string;
                if (!string.IsNullOrEmpty(path))
                {
                    // Accept primitives and mesh files
                    if (path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                        return true;
                    return IsAcceptedFileType(path);
                }
            }

            // Check for asset GUID from Asset Browser
            if (data.GetDataPresent("AssetGuid"))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if a file type is accepted for import.
        /// </summary>
        private bool IsAcceptedFileType(string filePath)
        {
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            
            return extension switch
            {
                ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".blend" or ".3ds" => true,
                ".vmesh" => true,
                _ => false
            };
        }

        /// <summary>
        /// Handles the drop operation.
        /// </summary>
        public void HandleDrop(IDataObject data, Point dropPosition)
        {
            if (data == null || _scene == null)
                return;

            // Handle file drop from Windows Explorer
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    HandleFileDrop(files[0], dropPosition);
                }
                return;
            }

            // Handle asset drop from Asset Browser (via AssetPath)
            if (data.GetDataPresent("AssetPath"))
            {
                var assetPath = data.GetData("AssetPath") as string;
                if (!string.IsNullOrEmpty(assetPath))
                {
                    HandleAssetPathDrop(assetPath, dropPosition);
                    return;
                }
            }

            // Handle asset drop from Asset Browser (via GUID)
            if (data.GetDataPresent("AssetGuid"))
            {
                var guidString = data.GetData("AssetGuid") as string;
                if (Guid.TryParse(guidString, out var assetGuid))
                {
                    HandleAssetDrop(assetGuid, dropPosition);
                }
            }
        }

        /// <summary>
        /// Handles dropping an asset by its path (from Asset Browser).
        /// </summary>
        private void HandleAssetPathDrop(string assetPath, Point dropPosition)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            // Handle primitive meshes
            if (assetPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                var primitiveName = assetPath.Substring("Primitive:".Length);
                CreateEntityWithMesh(assetPath, primitiveName, dropPosition);
                return;
            }

            // Check if this is a model file with multiple submeshes
            var extension = Path.GetExtension(assetPath)?.ToLowerInvariant();
            bool isModelFile = extension == ".fbx" || extension == ".obj" || extension == ".gltf" || 
                               extension == ".glb" || extension == ".dae" || extension == ".3ds" || 
                               extension == ".blend";

            if (isModelFile)
            {
                // Get full path to the model file
                var projectPath = Core.Data.ProjectData.Current?.Path ?? "";
                string fullPath = assetPath;
                if (!Path.IsPathRooted(assetPath))
                {
                    fullPath = Path.Combine(projectPath, assetPath);
                }

                if (File.Exists(fullPath))
                {
                    // Check how many submeshes the model has
                    int submeshCount = VortexAPI.GetSubmeshCount(fullPath);
                    
                    if (submeshCount > 1)
                    {
                        // Multi-material model - create parent with child entities
                        var result = VortexAPI.ImportModelWithMaterialsFromFile(fullPath);
                        if (result != null && result.Length > 0)
                        {
                            var name = Path.GetFileNameWithoutExtension(assetPath);
                            CreateMultiMaterialEntity(name, assetPath, result, dropPosition);
                            return;
                        }
                    }
                }
            }

            // Single mesh - create simple entity
            var meshName = Path.GetFileNameWithoutExtension(assetPath);
            CreateEntityWithMesh(assetPath, meshName, dropPosition);
        }

        /// <summary>
        /// Handles dropping a file from Windows Explorer.
        /// </summary>
        private void HandleFileDrop(string filePath, Point dropPosition)
        {
            if (!File.Exists(filePath))
                return;

            try
            {
                var extension = Path.GetExtension(filePath)?.ToLowerInvariant();

                // Check if it's a supported model format
                if (!ModelImportService.IsSupportedModelFormat(extension))
                {
                    MessageBox.Show(
                        $"Unsupported file format: {extension}\n\nSupported formats:\n• FBX, OBJ, GLTF, GLB, DAE, 3DS, Blend, VMesh",
                        "Unsupported Format",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // For non-vmesh files, check Assimp availability
                if (extension != ".vmesh" && !VortexAPI.IsAssimpAvailable())
                {
                    MessageBox.Show(
                        "Cannot import mesh - Assimp library is not available.\n\n" +
                        "Please install Assimp and rebuild the engine:\n" +
                        "1. Install Assimp NuGet package (version 3.0.0)\n" +
                        "2. Add VORTEX_USE_ASSIMP preprocessor definition\n" +
                        "3. Rebuild Engine project\n\n" +
                        "See NUGET_TROUBLESHOOTING.md for help.",
                        "Assimp Required",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // Use the ModelImportService
                var result = ModelImportService.Instance.ImportModel(filePath);
                
                System.Diagnostics.Debug.WriteLine($"[ViewportDropHandler] BEFORE DIALOG - result.Success={result.Success}, Submeshes.Count={result.Submeshes.Count}, IsMultiMaterial={result.IsMultiMaterial}");

                // Show the import result dialog
                var owner = Application.Current.MainWindow;
                var dialog = ImportResultDialog.Show(owner, result);
                
                System.Diagnostics.Debug.WriteLine($"[ViewportDropHandler] AFTER DIALOG - AddToSceneRequested={dialog.AddToSceneRequested}, DialogResult={dialog.DialogResult}");

                if (result.Success)
                {
                    // Entities are now created directly in ImportResultDialog.AddToScene_Click
                    // No need to do anything here
                    System.Diagnostics.Debug.WriteLine($"[ViewportDropHandler] Import complete. AddToSceneRequested={dialog.AddToSceneRequested}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to import model:\n{ex.Message}",
                    "Import Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles dropping an asset by GUID from Asset Browser.
        /// </summary>
        private void HandleAssetDrop(Guid assetGuid, Point dropPosition)
        {
            var asset = _assetDatabase.GetAsset(assetGuid);
            if (asset == null)
                return;

            // Only handle mesh assets
            if (asset.Type != AssetType.Mesh)
                return;

            try
            {
                var assetPath = asset.RelativePath;
                if (string.IsNullOrEmpty(assetPath))
                    return;

                // Check if this is a model file with multiple submeshes
                var extension = Path.GetExtension(assetPath)?.ToLowerInvariant();
                bool isModelFile = extension == ".fbx" || extension == ".obj" || extension == ".gltf" || 
                                   extension == ".glb" || extension == ".dae" || extension == ".3ds" || 
                                   extension == ".blend";

                if (isModelFile)
                {
                    var projectPath = Core.Data.ProjectData.Current?.Path ?? "";
                    string fullPath = assetPath;
                    if (!Path.IsPathRooted(assetPath))
                    {
                        fullPath = Path.Combine(projectPath, assetPath);
                    }

                    if (File.Exists(fullPath))
                    {
                        int submeshCount = VortexAPI.GetSubmeshCount(fullPath);
                        
                        if (submeshCount > 1)
                        {
                            var result = VortexAPI.ImportModelWithMaterialsFromFile(fullPath);
                            if (result != null && result.Length > 0)
                            {
                                CreateMultiMaterialEntity(asset.FileName, assetPath, result, dropPosition);
                                return;
                            }
                        }
                    }
                }

                CreateEntityWithMesh(assetPath, asset.FileName, dropPosition);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load asset:\n{ex.Message}",
                    "Load Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Creates a multi-material entity with child entities for each submesh.
        /// </summary>
        private void CreateMultiMaterialEntity(string modelName, string relativePath, VortexAPI.SubmeshImportData[] submeshes, Point dropPosition)
        {
            var projectPath = Core.Data.ProjectData.Current?.Path ?? "";
            
            // Find texture paths in the model directory
            string fullModelPath = relativePath;
            if (!Path.IsPathRooted(relativePath))
            {
                fullModelPath = Path.Combine(projectPath, relativePath);
            }
            var texturePaths = FindTexturesForModel(fullModelPath);
            
            // Get submesh names from the model
            string[] submeshNames = VortexAPI.GetSubmeshNames(fullModelPath, submeshes.Length);

            // Create parent container entity
            var parentEntity = new GameEntity(_scene, modelName);
            parentEntity.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);
            _scene.AddEntity(parentEntity);

            // Create child entity for each submesh
            for (int i = 0; i < submeshes.Length; i++)
            {
                var submesh = submeshes[i];
                string childName = i < submeshNames.Length && !string.IsNullOrEmpty(submeshNames[i]) 
                    ? submeshNames[i] 
                    : $"Submesh_{i}";
                
                var childEntity = new GameEntity(_scene, childName);
                
                // Mark child as locked to parent (can't be moved individually)
                childEntity.IsLockedToParent = true;
                
                // Use submesh-specific mesh path
                string submeshPath = $"{relativePath}#submesh{i}";
                var meshRenderer = new MeshRenderer(childEntity)
                {
                    MeshPath = submeshPath,
                    MaterialHandle = submesh.MaterialId
                };

                // Find the best matching texture for this submesh
                if (texturePaths.Count > 0)
                {
                    string texPath = FindTextureForSubmesh(childName, texturePaths);
                    if (!string.IsNullOrEmpty(texPath))
                    {
                        // Convert to relative path
                        if (!string.IsNullOrEmpty(projectPath) && texPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            texPath = texPath.Substring(projectPath.Length)
                                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        }
                        meshRenderer.TexturePath = texPath;
                    }
                }

                // Register material in SceneRenderService for consistent rendering
                if (submesh.MaterialId >= 0)
                {
                    Core.Services.SceneRenderService.RegisterMaterialForMeshPath(submeshPath, submesh.MaterialId);
                }

                childEntity.AddComponent(meshRenderer);
                childEntity.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);
                parentEntity.AddChild(childEntity);
            }

            // Select the parent entity
            Core.Services.SelectionService.Instance.Select(parentEntity);
            System.Diagnostics.Debug.WriteLine($"[ViewportDropHandler] Created multi-material entity with {submeshes.Length} submeshes");
        }

        /// <summary>
        /// Finds texture files associated with a model file.
        /// </summary>
        private List<string> FindTexturesForModel(string modelPath)
        {
            var result = new List<string>();
            
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
                return result;

            var dir = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return result;

            // Look for texture files in the same directory
            var textureExtensions = new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.dds" };
            
            foreach (var ext in textureExtensions)
            {
                try
                {
                    var files = Directory.GetFiles(dir, ext);
                    foreach (var file in files)
                    {
                        // Prefer color/diffuse/albedo textures
                        var fileName = Path.GetFileName(file).ToLowerInvariant();
                        if (fileName.Contains("col") || fileName.Contains("diffuse") || 
                            fileName.Contains("albedo") || fileName.Contains("base"))
                        {
                            result.Insert(0, file); // Add to beginning
                        }
                        else if (!fileName.Contains("normal") && !fileName.Contains("rough") && 
                                 !fileName.Contains("metal") && !fileName.Contains("ao") &&
                                 !fileName.Contains("height") && !fileName.Contains("spec"))
                        {
                            result.Add(file);
                        }
                    }
                }
                catch { /* Ignore errors */ }
            }

            return result;
        }

        /// <summary>
        /// Finds the best matching texture for a given submesh name.
        /// </summary>
        private string FindTextureForSubmesh(string submeshName, List<string> availableTextures)
        {
            if (string.IsNullOrEmpty(submeshName) || availableTextures == null || availableTextures.Count == 0)
                return availableTextures?.FirstOrDefault() ?? "";

            // Normalize the submesh name (lowercase, replace spaces with underscores)
            string normalizedName = submeshName.ToLowerInvariant().Replace(" ", "_");

            // Find texture that matches the submesh name
            foreach (var texPath in availableTextures)
            {
                string texFileName = Path.GetFileName(texPath).ToLowerInvariant();
                if (texFileName.Contains(normalizedName))
                {
                    return texPath;
                }
            }

            // If no exact match, return the first available texture
            return availableTextures.FirstOrDefault() ?? "";
        }

        /// <summary>
        /// Creates a new entity with a mesh renderer at the drop position.
        /// </summary>
        private void CreateEntityWithMesh(string meshPath, string name, Point dropPosition, long materialId = -1, string texturePath = null)
        {
            System.Diagnostics.Debug.WriteLine($"[ViewportDropHandler] CreateEntityWithMesh - materialId: {materialId}, texturePath: {texturePath}");
            
            // Create a new entity
            var entity = new GameEntity(_scene, name);

            // Add mesh renderer component
            var meshRenderer = new MeshRenderer(entity)
            {
                MeshPath = meshPath
            };

            // If we have an imported material with textures, use it
            if (materialId >= 0)
            {
                meshRenderer.MaterialHandle = materialId;
                System.Diagnostics.Debug.WriteLine($"[ViewportDropHandler] Set MaterialHandle to {materialId}");
            }

            // Save the texture path for persistence (so it survives restart)
            if (!string.IsNullOrEmpty(texturePath))
            {
                // Convert to relative path if possible
                var projectPath = Core.Data.ProjectData.Current?.Path;
                if (!string.IsNullOrEmpty(projectPath) && texturePath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    texturePath = texturePath.Substring(projectPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                meshRenderer.TexturePath = texturePath;
                System.Diagnostics.Debug.WriteLine($"[ViewportDropHandler] Set TexturePath to {texturePath}");
            }

            entity.AddComponent(meshRenderer);

            // TODO: Convert dropPosition to 3D world position via raycasting
            // For now, place at origin
            entity.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);

            // Add to scene
            _scene.AddEntity(entity);
            System.Diagnostics.Debug.WriteLine($"[ViewportDropHandler] Entity added to scene. HasImportedMaterial: {meshRenderer.HasImportedMaterial}");
        }

        /// <summary>
        /// Gets visual feedback for drag over.
        /// </summary>
        public DragDropEffects GetDragEffect(IDataObject data)
        {
            return CanAcceptDrop(data) ? DragDropEffects.Copy : DragDropEffects.None;
        }
    }
}
