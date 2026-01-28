using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Xml;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Service for managing asset tags across the project.
    /// Provides tag suggestions, filtering, and persistence.
    /// </summary>
    public class AssetTagService
    {
        private static AssetTagService _instance;
        public static AssetTagService Instance => _instance ?? (_instance = new AssetTagService());

        private readonly HashSet<string> _allTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<Guid>> _tagToAssets = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, HashSet<string>> _assetToTags = new Dictionary<Guid, HashSet<string>>();
        
        private string _projectPath;
        private string _tagsFilePath;

        /// <summary>
        /// All unique tags in the project.
        /// </summary>
        public IReadOnlyCollection<string> AllTags => _allTags.ToList().AsReadOnly();

        /// <summary>
        /// Common/predefined tags for quick access.
        /// </summary>
        public static readonly string[] PredefinedTags = new[]
        {
            "Environment",
            "Character",
            "Prop",
            "Skybox",
            "UI",
            "Audio",
            "VFX",
            "Prototype",
            "Final",
            "WIP",
            "Imported",
            "Custom"
        };

        private AssetTagService() { }

        /// <summary>
        /// Initialize the tag service for a project.
        /// </summary>
        public void Initialize(string projectPath)
        {
            _projectPath = projectPath;
            _tagsFilePath = Path.Combine(projectPath, "Library", "AssetTags.xml");
            
            _allTags.Clear();
            _tagToAssets.Clear();
            _assetToTags.Clear();

            // Add predefined tags
            foreach (var tag in PredefinedTags)
            {
                _allTags.Add(tag);
            }

            // Load saved tags
            LoadTags();
        }

        /// <summary>
        /// Add a tag to an asset.
        /// </summary>
        public void AddTag(Guid assetGuid, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return;
            tag = tag.Trim();

            _allTags.Add(tag);

            if (!_tagToAssets.ContainsKey(tag))
                _tagToAssets[tag] = new HashSet<Guid>();
            _tagToAssets[tag].Add(assetGuid);

            if (!_assetToTags.ContainsKey(assetGuid))
                _assetToTags[assetGuid] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _assetToTags[assetGuid].Add(tag);

            SaveTags();
        }

        /// <summary>
        /// Remove a tag from an asset.
        /// </summary>
        public void RemoveTag(Guid assetGuid, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return;

            if (_tagToAssets.ContainsKey(tag))
                _tagToAssets[tag].Remove(assetGuid);

            if (_assetToTags.ContainsKey(assetGuid))
                _assetToTags[assetGuid].Remove(tag);

            SaveTags();
        }

        /// <summary>
        /// Get all tags for an asset.
        /// </summary>
        public IReadOnlyCollection<string> GetTags(Guid assetGuid)
        {
            if (_assetToTags.TryGetValue(assetGuid, out var tags))
                return tags.ToList().AsReadOnly();
            return Array.Empty<string>();
        }

        /// <summary>
        /// Set all tags for an asset (replaces existing).
        /// </summary>
        public void SetTags(Guid assetGuid, IEnumerable<string> tags)
        {
            // Remove old tags
            if (_assetToTags.TryGetValue(assetGuid, out var oldTags))
            {
                foreach (var oldTag in oldTags.ToList())
                {
                    if (_tagToAssets.ContainsKey(oldTag))
                        _tagToAssets[oldTag].Remove(assetGuid);
                }
            }

            // Set new tags
            _assetToTags[assetGuid] = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
            
            foreach (var tag in tags)
            {
                _allTags.Add(tag);
                if (!_tagToAssets.ContainsKey(tag))
                    _tagToAssets[tag] = new HashSet<Guid>();
                _tagToAssets[tag].Add(assetGuid);
            }

            SaveTags();
        }

        /// <summary>
        /// Find all assets with a specific tag.
        /// </summary>
        public IReadOnlyCollection<Guid> FindAssetsByTag(string tag)
        {
            if (_tagToAssets.TryGetValue(tag, out var assets))
                return assets.ToList().AsReadOnly();
            return Array.Empty<Guid>();
        }

        /// <summary>
        /// Find all assets matching ALL specified tags.
        /// </summary>
        public IReadOnlyCollection<Guid> FindAssetsByTags(IEnumerable<string> tags)
        {
            var tagList = tags.ToList();
            if (tagList.Count == 0) return Array.Empty<Guid>();

            HashSet<Guid> result = null;
            foreach (var tag in tagList)
            {
                if (_tagToAssets.TryGetValue(tag, out var assets))
                {
                    if (result == null)
                        result = new HashSet<Guid>(assets);
                    else
                        result.IntersectWith(assets);
                }
                else
                {
                    return Array.Empty<Guid>(); // Tag doesn't exist, no matches
                }
            }

            return result?.ToList().AsReadOnly() ?? (IReadOnlyCollection<Guid>)Array.Empty<Guid>();
        }

        /// <summary>
        /// Find all assets matching ANY of the specified tags.
        /// </summary>
        public IReadOnlyCollection<Guid> FindAssetsByAnyTag(IEnumerable<string> tags)
        {
            var result = new HashSet<Guid>();
            foreach (var tag in tags)
            {
                if (_tagToAssets.TryGetValue(tag, out var assets))
                    result.UnionWith(assets);
            }
            return result.ToList().AsReadOnly();
        }

        /// <summary>
        /// Search tags by prefix for autocomplete.
        /// </summary>
        public IEnumerable<string> SearchTags(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return _allTags.OrderBy(t => t);

            return _allTags
                .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t);
        }

        /// <summary>
        /// Create a new custom tag.
        /// </summary>
        public void CreateTag(string tag)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                _allTags.Add(tag.Trim());
                SaveTags();
            }
        }

        /// <summary>
        /// Delete a tag from all assets.
        /// </summary>
        public void DeleteTag(string tag)
        {
            if (_tagToAssets.TryGetValue(tag, out var assets))
            {
                foreach (var assetGuid in assets.ToList())
                {
                    if (_assetToTags.ContainsKey(assetGuid))
                        _assetToTags[assetGuid].Remove(tag);
                }
                _tagToAssets.Remove(tag);
            }

            // Don't remove predefined tags from the list
            if (!PredefinedTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                _allTags.Remove(tag);
            }

            SaveTags();
        }

        #region Persistence

        private void LoadTags()
        {
            if (!File.Exists(_tagsFilePath)) return;

            try
            {
                var serializer = new DataContractSerializer(typeof(AssetTagData));
                using (var reader = XmlReader.Create(_tagsFilePath))
                {
                    var data = (AssetTagData)serializer.ReadObject(reader);
                    
                    foreach (var tag in data.AllTags)
                        _allTags.Add(tag);

                    foreach (var entry in data.AssetTags)
                    {
                        var guid = Guid.Parse(entry.AssetGuid);
                        var tags = new HashSet<string>(entry.Tags, StringComparer.OrdinalIgnoreCase);
                        _assetToTags[guid] = tags;

                        foreach (var tag in tags)
                        {
                            if (!_tagToAssets.ContainsKey(tag))
                                _tagToAssets[tag] = new HashSet<Guid>();
                            _tagToAssets[tag].Add(guid);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AssetTagService] Error loading tags: {ex.Message}");
            }
        }

        private void SaveTags()
        {
            if (string.IsNullOrEmpty(_tagsFilePath)) return;

            try
            {
                var dir = Path.GetDirectoryName(_tagsFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var data = new AssetTagData
                {
                    AllTags = _allTags.ToList(),
                    AssetTags = _assetToTags.Select(kvp => new AssetTagEntry
                    {
                        AssetGuid = kvp.Key.ToString(),
                        Tags = kvp.Value.ToList()
                    }).ToList()
                };

                var serializer = new DataContractSerializer(typeof(AssetTagData));
                var settings = new XmlWriterSettings { Indent = true };
                using (var writer = XmlWriter.Create(_tagsFilePath, settings))
                {
                    serializer.WriteObject(writer, data);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AssetTagService] Error saving tags: {ex.Message}");
            }
        }

        [DataContract]
        private class AssetTagData
        {
            [DataMember(Order = 0)]
            public List<string> AllTags { get; set; } = new List<string>();

            [DataMember(Order = 1)]
            public List<AssetTagEntry> AssetTags { get; set; } = new List<AssetTagEntry>();
        }

        [DataContract]
        private class AssetTagEntry
        {
            [DataMember(Order = 0)]
            public string AssetGuid { get; set; }

            [DataMember(Order = 1)]
            public List<string> Tags { get; set; } = new List<string>();
        }

        #endregion
    }
}
