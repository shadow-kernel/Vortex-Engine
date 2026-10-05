using System;
using System.Collections.Generic;

namespace Editor.Core.Assets.Library
{
    /// <summary>Where a library entry came from (catalog column <c>source_kind</c>).</summary>
    public static class LibrarySource
    {
        public const string Project = "project";     // imported into / indexed from a project
        public const string Manual = "manual";       // "Add to Library" of a loose file
        public const string Store = "store";         // an Asset Store provider (v2.10)
        public const string Generated = "generated"; // Claude Sound Studio and other generators (v2.10)
        public const string Bundle = "bundle";       // imported from a .vlib.zip bundle
    }

    /// <summary>One catalog entry: a named, tagged alias of a stored blob (several entries may share one blob —
    /// "Import as new" gives the same bytes a second name, never a second copy).</summary>
    public sealed class LibraryEntry
    {
        public long Id;
        public string Hash;
        public string Name;
        public string FileName;
        public AssetType Type;
        public long Size;
        public bool Stored;
        public DateTime Added;
        public DateTime Updated;
        public string SourceKind;
        public string SourceName;
        public string SourceUrl;
        public string Author;
        public string License;
        public bool Redistributable = true;
        public double? Duration;
        public int? Channels;
        public int? SampleRate;
        public int? Width;
        public int? Height;
        public string Notes;
        public List<string> Tags = new List<string>();
        public List<LibraryCompanion> Companions = new List<LibraryCompanion>();

        public string Extension => System.IO.Path.GetExtension(FileName ?? "").ToLowerInvariant();
        /// <summary>Badge text for a tile: the project/provider it came from.</summary>
        public string SourceLabel
        {
            get
            {
                if (!string.IsNullOrEmpty(SourceName)) return SourceName;
                switch (SourceKind)
                {
                    case LibrarySource.Manual: return "Added";
                    case LibrarySource.Generated: return "Generated";
                    case LibrarySource.Bundle: return "Bundle";
                    case LibrarySource.Store: return "Store";
                    default: return null;
                }
            }
        }

        /// <summary>"from project Range", "added by hand", "from Poly Haven" …</summary>
        public string SourceDescription
        {
            get
            {
                switch (SourceKind)
                {
                    case LibrarySource.Project: return string.IsNullOrEmpty(SourceName) ? "from a project" : "from project " + SourceName;
                    case LibrarySource.Manual: return "added by hand";
                    case LibrarySource.Generated: return "generated" + (string.IsNullOrEmpty(SourceName) ? "" : " by " + SourceName);
                    case LibrarySource.Bundle: return "from a library bundle";
                    default: return "from " + (SourceName ?? SourceKind);
                }
            }
        }
        public override string ToString() => Type + ": " + Name + " (" + (Hash ?? "").Substring(0, Math.Min(8, (Hash ?? "").Length)) + ")";
    }

    /// <summary>A file that belongs to an entry (glTF buffers/images, a model folder's textures/materials/clips),
    /// stored as its own blob and restored at <see cref="RelPath"/> next to the main file.</summary>
    public sealed class LibraryCompanion
    {
        public string RelPath;
        public string Hash;
        public long Size;
    }

    public enum LibrarySort { Name, Type, Added, Size }

    /// <summary>Library search: every set criterion must match.</summary>
    public sealed class LibraryQuery
    {
        /// <summary>Case-insensitive substring of the name or file name (also matches tags).</summary>
        public string Search;
        /// <summary>Only these types (null/empty = all).</summary>
        public List<AssetType> Types;
        /// <summary>Entries carrying ALL of these tags.</summary>
        public List<string> Tags;
        /// <summary>Only entries from this source name (project / provider).</summary>
        public string SourceName;
        public LibrarySort Sort = LibrarySort.Name;
        public bool Descending;
        public int Limit;   // 0 = no limit
        public int Offset;

        public LibraryQuery Clone()
        {
            var q = (LibraryQuery)MemberwiseClone();
            q.Types = Types == null ? null : new List<AssetType>(Types);
            q.Tags = Tags == null ? null : new List<string>(Tags);
            return q;
        }
    }

    /// <summary>A named search (search text + types + tags) the user can recall from the Library tab.</summary>
    public sealed class SavedFilter
    {
        public string Name;
        public string Search;
        public List<AssetType> Types = new List<AssetType>();
        public List<string> Tags = new List<string>();
        public DateTime Created;

        public LibraryQuery ToQuery() => new LibraryQuery { Search = Search, Types = new List<AssetType>(Types), Tags = new List<string>(Tags) };
    }

    /// <summary>A project file that uses a library blob.</summary>
    public sealed class LibraryUsage
    {
        public string Hash;
        public string ProjectPath;
        public string ProjectName;
        public string RelativePath;
        public string AssetGuid;
        public DateTime Seen;
    }

    public sealed class LibraryStats
    {
        public int Entries;
        public int Blobs;
        public long StoredBytes;
        public long ThumbnailBytes;
        public int Tags;
        public int Projects;
        public long SizeCapBytes;
        public bool OverCap => SizeCapBytes > 0 && StoredBytes > SizeCapBytes;
        public readonly List<(AssetType type, int count, long bytes)> ByType = new List<(AssetType, int, long)>();
        public readonly List<(string source, int count, long bytes)> BySource = new List<(string, int, long)>();
        public readonly List<LibraryEntry> Largest = new List<LibraryEntry>();
    }

    /// <summary>What registering a file did.</summary>
    public sealed class RegisterResult
    {
        public bool Success;
        public string Error;
        public string Hash;
        public LibraryEntry Entry;
        /// <summary>The bytes were already in the library (no new blob was written).</summary>
        public bool BlobExisted;
        /// <summary>A catalog entry with this hash existed already (the registration added a usage / merged tags).</summary>
        public bool EntryExisted;
        /// <summary>Skipped by the settings (type excluded / auto-registration off).</summary>
        public bool Skipped;
        public long BytesWritten;
    }

    /// <summary>How a file is registered.</summary>
    public sealed class RegisterOptions
    {
        public string Name;                 // null = file name without extension
        public List<string> Tags = new List<string>();
        public string SourceKind = LibrarySource.Project;
        public string SourceName;           // project name / provider id
        public string SourceUrl;            // project path / store page
        public string Author, License, Notes;
        public bool Redistributable = true;
        /// <summary>The file's project (usage row); null = not in a project.</summary>
        public string ProjectPath;
        public string ProjectName;
        public string AssetGuid;
        /// <summary>Hash computed earlier (skips re-hashing the main file).</summary>
        public string KnownHash;
        /// <summary>Always create a new catalog entry even when one with this hash exists ("Import as new").</summary>
        public bool ForceNewEntry;
        /// <summary>Ignore the type rules and the auto-registration switch (explicit "Add to Library").</summary>
        public bool Explicit;
        /// <summary>An automatic registration (import hook): skipped when auto-registration is switched off.</summary>
        public bool IsAutomatic;
        /// <summary>Extra files to keep with the entry (absolute path → path relative to the main file's folder).
        /// Null = detect them (<see cref="LibraryCompanions"/>).</summary>
        public Dictionary<string, string> Companions;
        /// <summary>Hashes already computed for companions (absolute path → hash).</summary>
        public Dictionary<string, string> CompanionHashes;
    }

    public enum AddToProjectStatus { Added, AlreadyInProject, Failed }

    public sealed class AddToProjectResult
    {
        public AddToProjectStatus Status;
        public string Error;
        /// <summary>The asset in the project (new copy, or the existing file for AlreadyInProject).</summary>
        public string Path;
        public Guid AssetGuid;
        public readonly List<string> Files = new List<string>();
    }

    public sealed class VerifyReport
    {
        public int Checked;
        public readonly List<string> Missing = new List<string>();
        public readonly List<string> Corrupt = new List<string>();
        public TimeSpan Duration;
        public bool Ok => Missing.Count == 0 && Corrupt.Count == 0;
    }

    public sealed class OrphanReport
    {
        /// <summary>Blob files on disk without a catalog row.</summary>
        public readonly List<string> UntrackedFiles = new List<string>();
        /// <summary>Blob rows no entry or companion references.</summary>
        public readonly List<string> UnreferencedBlobs = new List<string>();
        /// <summary>Blob rows whose file is missing.</summary>
        public readonly List<string> MissingFiles = new List<string>();
        /// <summary>Entries whose blob is missing (they can't be added to a project any more).</summary>
        public readonly List<LibraryEntry> BrokenEntries = new List<LibraryEntry>();
        /// <summary>Thumbnails of hashes the library no longer knows.</summary>
        public readonly List<string> StaleThumbnails = new List<string>();
        public long ReclaimableBytes;
        public bool Fixed;
        public bool IsClean => UntrackedFiles.Count == 0 && UnreferencedBlobs.Count == 0 && MissingFiles.Count == 0 && BrokenEntries.Count == 0 && StaleThumbnails.Count == 0;
    }

    public sealed class BundleReport
    {
        public int Entries;
        public int Blobs;
        public int BlobsAlreadyPresent;
        public long Bytes;
        public readonly List<string> SkippedNotRedistributable = new List<string>();
        public readonly List<string> Errors = new List<string>();
    }

    public sealed class IndexReport
    {
        public int Projects;
        public int Files;
        public int Registered;
        public int AlreadyKnown;
        public int Skipped;
        public int DuplicatesCollapsed;
        public long BytesStored;
        public int VmetaUpdated;
        public readonly List<string> Errors = new List<string>();
        public TimeSpan Duration;
        public bool Cancelled;
    }

    /// <summary>Progress of a long library job (hashing, indexing, verifying, moving).</summary>
    public struct LibraryProgress
    {
        public string Phase;
        public int Done;
        public int Total;
        public string Current;
        public LibraryProgress(string phase, int done, int total, string current) { Phase = phase; Done = done; Total = total; Current = current; }
        public double Fraction => Total <= 0 ? 0 : Math.Min(1.0, (double)Done / Total);
    }
}
