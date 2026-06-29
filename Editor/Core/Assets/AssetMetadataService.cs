using System;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Service for loading and saving asset metadata (.vmeta files).
    /// </summary>
    public class AssetMetadataService
    {
        private static AssetMetadataService _instance;
        public static AssetMetadataService Instance => _instance ?? (_instance = new AssetMetadataService());

        private readonly DataContractSerializer _serializer;

        private AssetMetadataService()
        {
            _serializer = new DataContractSerializer(typeof(AssetMetadata));
        }

        /// <summary>
        /// Load metadata from a .vmeta file.
        /// </summary>
        public AssetMetadata LoadMetadata(string metaFilePath)
        {
            if (!File.Exists(metaFilePath)) return null;

            try
            {
                using (var reader = XmlReader.Create(metaFilePath))
                {
                    return (AssetMetadata)_serializer.ReadObject(reader);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AssetMetadataService] Error loading {metaFilePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Save metadata to a .vmeta file.
        /// </summary>
        public bool SaveMetadata(string metaFilePath, AssetMetadata metadata)
        {
            try
            {
                var settings = new XmlWriterSettings { Indent = true };
                using (var writer = XmlWriter.Create(metaFilePath, settings))
                {
                    _serializer.WriteObject(writer, metadata);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AssetMetadataService] Error saving {metaFilePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get or create metadata for an asset file.
        /// </summary>
        public AssetMetadata GetOrCreateMetadata(string assetFilePath, string projectPath)
        {
            var metaPath = assetFilePath + ".vmeta";
            var existing = LoadMetadata(metaPath);
            
            if (existing != null)
            {
                // Update last modified time
                existing.LastModified = File.GetLastWriteTime(assetFilePath);
                return existing;
            }

            // Create new metadata
            var relativePath = assetFilePath;
            if (assetFilePath.StartsWith(projectPath))
            {
                relativePath = assetFilePath.Substring(projectPath.Length).TrimStart('\\', '/');
            }

            var fileName = Path.GetFileName(assetFilePath);
            var extension = Path.GetExtension(assetFilePath).ToLowerInvariant();
            var assetType = GetAssetTypeFromExtension(extension);

            var metadata = new AssetMetadata(assetType, relativePath, fileName)
            {
                FileSize = new FileInfo(assetFilePath).Length,
                LastModified = File.GetLastWriteTime(assetFilePath)
            };

            // Save it
            SaveMetadata(metaPath, metadata);

            return metadata;
        }

        /// <summary>
        /// Determine asset type from file extension.
        /// </summary>
        public AssetType GetAssetTypeFromExtension(string extension)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".fbx":
                case ".obj":
                case ".gltf":
                case ".glb":
                case ".vmesh":
                    return AssetType.Mesh;

                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tga":
                case ".dds":
                case ".hdr":
                case ".exr":
                    return AssetType.Texture;

                case ".vmat":
                    return AssetType.Material;

                case ".wav":
                case ".mp3":
                case ".ogg":
                    return AssetType.Audio;

                case ".vshader":
                case ".hlsl":
                    return AssetType.Shader;

                case ".vscene":
                    return AssetType.Scene;

                case ".vprefab":
                    return AssetType.Prefab;

                case ".vui":
                    return AssetType.UI;

                default:
                    return AssetType.Unknown;
            }
        }
    }
}
