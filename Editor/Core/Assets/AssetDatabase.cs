using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Serialization;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Central database for all assets in the project.
    /// Manages asset metadata, GUID mapping, and dependency tracking.
    /// </summary>
    public class AssetDatabase
    {
        private static AssetDatabase _instance;
        public static AssetDatabase Instance => _instance ?? (_instance = new AssetDatabase());

        private string _projectPath;
        private Dictionary<Guid, AssetMetadata> _assetsByGuid;
        private Dictionary<string, Guid> _assetsByPath;

        public const string MetaFileExtension = ".vmeta";

        /// <summary>
        /// Gets the root path of the current project.
        /// </summary>
        public string ProjectPath => _projectPath;

        /// <summary>
        /// Event fired when assets are added, removed, or changed.
        /// </summary>
        public event EventHandler AssetsChanged;

        private AssetDatabase()
        {
            _assetsByGuid = new Dictionary<Guid, AssetMetadata>();
            _assetsByPath = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Initializes the asset database for a project.
        /// Scans the project directory and builds the asset registry.
        /// </summary>
        public void Initialize(string projectPath)
        {
            _projectPath = projectPath;
            _assetsByGuid.Clear();
            _assetsByPath.Clear();

            if (!Directory.Exists(projectPath))
                return;

            ScanProjectAssets();
            AssetsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Rescans the project assets and updates the database.
        /// </summary>
        public void Refresh()
        {
            if (string.IsNullOrEmpty(_projectPath) || !Directory.Exists(_projectPath))
                return;

            _assetsByGuid.Clear();
            _assetsByPath.Clear();
            ScanProjectAssets();
            AssetsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Scans the project directory for all assets and loads their metadata.
        /// Generates metadata for assets that don't have .vmeta files.
        /// </summary>
        private void ScanProjectAssets()
        {
            var assetsPath = Path.Combine(_projectPath, "Assets");
            if (!Directory.Exists(assetsPath))
                return;

            // Scan all files in Assets folder recursively
            var files = Directory.GetFiles(assetsPath, "*.*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                // Skip meta files themselves
                if (file.EndsWith(MetaFileExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip hidden files and temp files
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith(".") || fileName.EndsWith("~"))
                    continue;

                ProcessAssetFile(file);
            }
        }

        /// <summary>
        /// Processes a single asset file, loading or creating its metadata.
        /// </summary>
        private void ProcessAssetFile(string filePath)
        {
            var metaPath = filePath + MetaFileExtension;
            AssetMetadata metadata;

            // Load existing metadata or create new
            if (File.Exists(metaPath))
            {
                try
                {
                    metadata = DataSerializer.LoadFromJson<AssetMetadata>(metaPath);
                }
                catch
                {
                    // If metadata is corrupted, regenerate it
                    metadata = CreateMetadataForFile(filePath);
                    SaveMetadata(metadata, metaPath);
                }
            }
            else
            {
                // Generate new metadata
                metadata = CreateMetadataForFile(filePath);
                SaveMetadata(metadata, metaPath);
            }

            // Update metadata file info
            var fileInfo = new FileInfo(filePath);
            metadata.LastModified = fileInfo.LastWriteTime;
            metadata.FileSize = fileInfo.Length;

            // Register in database
            RegisterAsset(metadata);
        }

        /// <summary>
        /// Creates metadata for a file based on its extension.
        /// </summary>
        private AssetMetadata CreateMetadataForFile(string filePath)
        {
            var relativePath = GetRelativePath(filePath);
            var fileName = Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            var type = DetermineAssetType(extension);

            return new AssetMetadata(type, relativePath, fileName);
        }

        /// <summary>
        /// Determines asset type from file extension.
        /// </summary>
        private AssetType DetermineAssetType(string extension)
        {
            return extension switch
            {
                ".vscene" => AssetType.Scene,
                ".vmesh" or ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".blend" or ".3ds" => AssetType.Mesh,
                ".vmat" => AssetType.Material,
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".psd" or ".hdr" or ".dds" => AssetType.Texture,
                ".ventity" => AssetType.Prefab,
                ".hlsl" or ".glsl" or ".shader" => AssetType.Shader,
                ".wav" or ".mp3" or ".ogg" or ".flac" => AssetType.Audio,
                ".cs" or ".cpp" or ".h" => AssetType.Script,
                ".ttf" or ".otf" => AssetType.Font,
                _ => AssetType.Unknown
            };
        }

        /// <summary>
        /// Registers an asset in the database.
        /// </summary>
        private void RegisterAsset(AssetMetadata metadata)
        {
            _assetsByGuid[metadata.Guid] = metadata;
            
            var normalizedPath = NormalizePath(metadata.RelativePath);
            _assetsByPath[normalizedPath] = metadata.Guid;
        }

        /// <summary>
        /// Gets an asset by its GUID.
        /// </summary>
        public AssetMetadata GetAsset(Guid guid)
        {
            return _assetsByGuid.TryGetValue(guid, out var metadata) ? metadata : null;
        }

        /// <summary>
        /// Gets an asset by its relative path.
        /// </summary>
        public AssetMetadata GetAssetByPath(string relativePath)
        {
            var normalized = NormalizePath(relativePath);
            if (_assetsByPath.TryGetValue(normalized, out var guid))
            {
                return GetAsset(guid);
            }
            return null;
        }

        /// <summary>
        /// Gets the full file path for an asset.
        /// </summary>
        public string GetAssetPath(Guid guid)
        {
            var metadata = GetAsset(guid);
            if (metadata == null)
                return null;

            return Path.Combine(_projectPath, metadata.RelativePath);
        }

        /// <summary>
        /// Gets all assets of a specific type.
        /// </summary>
        public IEnumerable<AssetMetadata> GetAssetsByType(AssetType type)
        {
            return _assetsByGuid.Values.Where(a => a.Type == type);
        }

        /// <summary>
        /// Gets all registered assets.
        /// </summary>
        public IEnumerable<AssetMetadata> GetAllAssets()
        {
            return _assetsByGuid.Values;
        }

        /// <summary>
        /// Imports a new asset into the project.
        /// </summary>
        public AssetMetadata ImportAsset(string sourcePath, string targetRelativePath, AssetType type = AssetType.Unknown)
        {
            var targetPath = Path.Combine(_projectPath, targetRelativePath);
            var targetDir = Path.GetDirectoryName(targetPath);

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            // Copy file if it's not already in the project
            if (!File.Exists(targetPath))
            {
                File.Copy(sourcePath, targetPath, false);
            }

            // Determine type if not specified
            if (type == AssetType.Unknown)
            {
                var extension = Path.GetExtension(sourcePath);
                type = DetermineAssetType(extension);
            }

            // Create metadata
            var fileName = Path.GetFileName(targetPath);
            var metadata = new AssetMetadata(type, targetRelativePath, fileName);

            var fileInfo = new FileInfo(targetPath);
            metadata.LastModified = fileInfo.LastWriteTime;
            metadata.FileSize = fileInfo.Length;

            // Save metadata
            var metaPath = targetPath + MetaFileExtension;
            SaveMetadata(metadata, metaPath);

            // Register
            RegisterAsset(metadata);

            // Notify listeners
            AssetsChanged?.Invoke(this, EventArgs.Empty);

            return metadata;
        }

        /// <summary>
        /// Saves asset metadata to a .vmeta file.
        /// </summary>
        public void SaveMetadata(AssetMetadata metadata, string metaPath = null)
        {
            if (metaPath == null)
            {
                var assetPath = Path.Combine(_projectPath, metadata.RelativePath);
                metaPath = assetPath + MetaFileExtension;
            }

            DataSerializer.SaveAsJson(metadata, metaPath);
        }

        /// <summary>
        /// Updates asset metadata and saves it.
        /// </summary>
        public void UpdateMetadata(AssetMetadata metadata)
        {
            if (_assetsByGuid.ContainsKey(metadata.Guid))
            {
                _assetsByGuid[metadata.Guid] = metadata;
                SaveMetadata(metadata);
            }
        }

        /// <summary>
        /// Adds a dependency between two assets.
        /// </summary>
        public void AddDependency(Guid assetGuid, Guid dependencyGuid)
        {
            var metadata = GetAsset(assetGuid);
            if (metadata == null || metadata.Dependencies == null)
                return;

            if (!metadata.Dependencies.Contains(dependencyGuid))
            {
                metadata.Dependencies.Add(dependencyGuid);
                UpdateMetadata(metadata);
            }
        }

        /// <summary>
        /// Removes a dependency between two assets.
        /// </summary>
        public void RemoveDependency(Guid assetGuid, Guid dependencyGuid)
        {
            var metadata = GetAsset(assetGuid);
            if (metadata == null || metadata.Dependencies == null)
                return;

            if (metadata.Dependencies.Remove(dependencyGuid))
            {
                UpdateMetadata(metadata);
            }
        }

        /// <summary>
        /// Deletes an asset from the database.
        /// Note: This only removes from the in-memory database, not the physical files.
        /// </summary>
        public void RemoveAsset(Guid guid)
        {
            var metadata = GetAsset(guid);
            if (metadata == null)
                return;

            _assetsByGuid.Remove(guid);
            
            var normalizedPath = NormalizePath(metadata.RelativePath);
            _assetsByPath.Remove(normalizedPath);
        }

        /// <summary>
        /// Gets relative path from project root.
        /// </summary>
        private string GetRelativePath(string fullPath)
        {
            if (string.IsNullOrEmpty(_projectPath) || string.IsNullOrEmpty(fullPath))
                return fullPath;

            // Ensure both paths end with directory separator for consistent comparison
            var projectPath = _projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            
            if (fullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(projectPath.Length);
            }

            // Fallback to Uri-based approach if simple prefix doesn't work
            try
            {
                var uri1 = new Uri(projectPath);
                var uri2 = new Uri(fullPath);
                var relativeUri = uri1.MakeRelativeUri(uri2);
                return Uri.UnescapeDataString(relativeUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return fullPath;
            }
        }

        /// <summary>
        /// Normalizes a path for consistent comparison.
        /// </summary>
        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return path.Replace('/', Path.DirectorySeparatorChar)
                      .Replace('\\', Path.DirectorySeparatorChar)
                      .TrimEnd(Path.DirectorySeparatorChar);
        }
    }
}
