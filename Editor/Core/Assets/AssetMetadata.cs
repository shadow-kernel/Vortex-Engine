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

        /// <summary>
        /// SHA-256 of the asset file's bytes (lowercase hex) — the asset's identity in the global asset library and the
        /// join key to its catalog entry (#54). Null until the file has been hashed (on import, "Add to Project" or the
        /// "Backfill Content Hashes" command); old .vmeta files without it still load.
        /// </summary>
        [DataMember(Order = 10, EmitDefaultValue = false)]
        public string ContentHash { get; set; }

        /// <summary>File size the <see cref="ContentHash"/> was computed from (a different size = stale hash).</summary>
        [DataMember(Order = 11, EmitDefaultValue = false)]
        public long ContentHashFileSize { get; set; }

        /// <summary>Last write time (UTC ticks) the <see cref="ContentHash"/> was computed from. A rename or move keeps it,
        /// so moving an asset never invalidates its hash; editing the file does.</summary>
        [DataMember(Order = 12, EmitDefaultValue = false)]
        public long ContentHashFileTime { get; set; }

        /// <summary>License id (SPDX style: "CC0-1.0", "CC-BY-4.0", or "Mixamo", "Sonniss-GDC" …) of an asset that came from
        /// the asset store / library; null for own work. Game exports credit CC-BY assets in CREDITS.md (#77).</summary>
        [DataMember(Order = 13, EmitDefaultValue = false)]
        public string License { get; set; }

        /// <summary>Author / creator to credit.</summary>
        [DataMember(Order = 14, EmitDefaultValue = false)]
        public string Author { get; set; }

        /// <summary>Where the asset came from (store page URL).</summary>
        [DataMember(Order = 15, EmitDefaultValue = false)]
        public string SourceUrl { get; set; }

        /// <summary>The provider or project it came from ("Poly Haven", "Freesound" …).</summary>
        [DataMember(Order = 16, EmitDefaultValue = false)]
        public string Source { get; set; }

        /// <summary>How a generated sound was made (Sound Studio recipe JSON: prompt, backend, length, loop …) — "Open in
        /// Sound Studio" regenerates a sibling from it (#83). Null for normal assets.</summary>
        [DataMember(Order = 17, EmitDefaultValue = false)]
        public string Recipe { get; set; }

        /// <summary>True when <see cref="ContentHash"/> was computed from the file as it is now.</summary>
        public bool HasFreshContentHash(string fullPath)
        {
            if (string.IsNullOrEmpty(ContentHash)) return false;
            return Library.ContentHash.TryStamp(fullPath, out long size, out long ticks) && size == ContentHashFileSize && ticks == ContentHashFileTime;
        }

        /// <summary>Hash the file now (unless the stored hash is still fresh). Returns true when the metadata changed.</summary>
        public bool UpdateContentHash(string fullPath)
        {
            if (HasFreshContentHash(fullPath)) return false;
            if (!Library.ContentHash.TryStamp(fullPath, out long size, out long ticks)) return false;
            string hash = Library.ContentHash.OfFile(fullPath);
            if (hash == null) return false;
            ContentHash = hash; ContentHashFileSize = size; ContentHashFileTime = ticks;
            return true;
        }

        /// <summary>Record a hash computed elsewhere (e.g. while copying the file) for the file as it is now.</summary>
        public void SetContentHash(string hash, string fullPath)
        {
            ContentHash = hash;
            if (Library.ContentHash.TryStamp(fullPath, out long size, out long ticks)) { ContentHashFileSize = size; ContentHashFileTime = ticks; }
        }

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
