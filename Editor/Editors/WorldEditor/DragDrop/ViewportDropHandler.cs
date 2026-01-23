using System;
using System.IO;
using System.Linq;
using System.Windows;
using Editor.Core.Assets;
using Editor.Core.Data;
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

            // Handle asset drop from Asset Browser
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
        /// Handles dropping a file from Windows Explorer.
        /// </summary>
        private void HandleFileDrop(string filePath, Point dropPosition)
        {
            if (!File.Exists(filePath))
                return;

            try
            {
                string meshPath = filePath;
                var extension = Path.GetExtension(filePath)?.ToLowerInvariant();

                // For non-vmesh files, check Assimp availability
                if (extension != ".vmesh" && !VortexResources.IsAssimpAvailable())
                {
                    MessageBox.Show(
                        "Cannot import model: Assimp library not available.\nPlease install the Assimp NuGet package.",
                        "Import Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Import to project as asset
                var fileName = Path.GetFileName(filePath);
                var targetPath = $"Assets/Models/{fileName}";
                var asset = _assetDatabase.ImportAsset(filePath, targetPath, AssetType.Mesh);

                // Get the project-relative path
                meshPath = asset.RelativePath;

                CreateEntityWithMesh(meshPath, Path.GetFileNameWithoutExtension(filePath), dropPosition);
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
        /// Creates a new entity with a mesh renderer at the drop position.
        /// </summary>
        private void CreateEntityWithMesh(string meshPath, string name, Point dropPosition)
        {
            // Create a new entity
            var entity = new GameEntity(_scene)
            {
                Name = name
            };

            // Add mesh renderer component
            var meshRenderer = new MeshRenderer(entity)
            {
                MeshPath = meshPath
            };
            entity.AddComponent(meshRenderer);

            // TODO: Convert dropPosition to 3D world position via raycasting
            // For now, place at origin
            entity.Position = new System.Numerics.Vector3(0, 0, 0);

            // Add to scene
            _scene.AddGameEntity(entity);
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
