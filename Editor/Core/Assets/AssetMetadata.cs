using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Metadata for a single asset in the project.
    /// Stored as .vmeta files alongside each asset.
    /// </summary>
    [DataContract]
    public class AssetMetadata
    {
        /// <summary>
        /// Unique identifier for this asset. Never changes even if file is moved/renamed.
        /// </summary>
        [DataMember(Order = 0)]
        public Guid Guid { get; set; }

        /// <summary>
        /// Type of asset (Mesh, Texture, Material, etc.)
        /// </summary>
        [DataMember(Order = 1)]
        public AssetType Type { get; set; }

        /// <summary>
        /// Relative path to the asset file from project root.
        /// </summary>
        [DataMember(Order = 2)]
        public string RelativePath { get; set; }

        /// <summary>
        /// Original filename of the asset.
        /// </summary>
        [DataMember(Order = 3)]
        public string FileName { get; set; }

        /// <summary>
        /// When the asset was first imported/created.
        /// </summary>
        [DataMember(Order = 4)]
        public DateTime ImportDate { get; set; }

        /// <summary>
        /// Last modification time of the source file.
        /// </summary>
        [DataMember(Order = 5)]
        public DateTime LastModified { get; set; }

        /// <summary>
        /// File size in bytes.
        /// </summary>
        [DataMember(Order = 6)]
        public long FileSize { get; set; }

        /// <summary>
        /// List of asset GUIDs this asset depends on.
        /// For example, a Material depends on its Textures.
        /// </summary>
        [DataMember(Order = 7)]
        public List<Guid> Dependencies { get; set; }

        /// <summary>
        /// Import settings specific to this asset type (JSON serialized).
        /// </summary>
        [DataMember(Order = 8)]
        public Dictionary<string, string> ImportSettings { get; set; }

        /// <summary>
        /// Custom metadata tags for searching/filtering.
        /// </summary>
        [DataMember(Order = 9)]
        public List<string> Tags { get; set; }

        public AssetMetadata()
        {
            Guid = Guid.NewGuid();
            Type = AssetType.Unknown;
            ImportDate = DateTime.Now;
            LastModified = DateTime.Now;
            Dependencies = new List<Guid>();
            ImportSettings = new Dictionary<string, string>();
            Tags = new List<string>();
        }

        public AssetMetadata(AssetType type, string relativePath, string fileName)
        {
            Guid = Guid.NewGuid();
            Type = type;
            RelativePath = relativePath;
            FileName = fileName;
            ImportDate = DateTime.Now;
            LastModified = DateTime.Now;
            Dependencies = new List<Guid>();
            ImportSettings = new Dictionary<string, string>();
            Tags = new List<string>();
        }

        /// <summary>
        /// Creates an AssetReference from this metadata.
        /// </summary>
        public AssetReference ToReference() => new AssetReference(Guid, Type);

        public override string ToString() => $"{Type}: {FileName} ({Guid})";
    }
}
