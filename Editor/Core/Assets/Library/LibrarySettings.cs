using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Editor.Core.Services;

namespace Editor.Core.Assets.Library
{
    /// <summary>
    /// Global asset library settings (#65). They live OUTSIDE the library (bootstrap problem: the catalog can move) in
    /// <c>&lt;VortexAppData&gt;/asset-library.json</c>. Every library service resolves its folders through
    /// <see cref="EffectiveRoot"/> — no other code builds library paths.
    /// </summary>
    [DataContract(Name = "AssetLibrarySettings")]
    public sealed class LibrarySettings
    {
        /// <summary>Custom library folder; null/empty = the default location (<see cref="DefaultRoot"/>).</summary>
        [DataMember(Order = 0)] public string Root { get; set; }
        /// <summary>Soft size cap of the blob store in GB (0 = none). Over the cap new registrations still work but warn;
        /// nothing is ever evicted silently.</summary>
        [DataMember(Order = 1)] public double SizeCapGB { get; set; }
        /// <summary>Register every import automatically (off = only the explicit "Add to Library" actions).</summary>
        [DataMember(Order = 2)] public bool AutoRegisterImports { get; set; }
        /// <summary>Asset types that are never auto-registered (AssetType values).</summary>
        [DataMember(Order = 3)] public List<int> ExcludedTypes { get; set; }
        /// <summary>The "index your existing projects" prompt was shown once.</summary>
        [DataMember(Order = 4)] public bool IndexPromptShown { get; set; }
        /// <summary>A library move that did not finish (old root) — the next start completes or rolls it back.</summary>
        [DataMember(Order = 5)] public string PendingMoveFrom { get; set; }

        public LibrarySettings() { ApplyDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext c) => ApplyDefaults();

        private void ApplyDefaults()
        {
            AutoRegisterImports = true;
            ExcludedTypes = new List<int> { (int)AssetType.Unknown, (int)AssetType.Folder, (int)AssetType.Script, (int)AssetType.Scene };
        }

        public static string FilePath => Path.Combine(EditorPaths.VortexAppData, "asset-library.json");

        /// <summary>The machine-wide default: %LOCALAPPDATA%\VortexEngine\AssetDB on Windows,
        /// ~/Library/Application Support/VortexEngine/AssetDB on macOS, ~/.local/share/VortexEngine/AssetDB on Linux.
        /// With VORTEX_APPDATA_DIR set (tests, CI) the library sits inside that folder instead.</summary>
        public static string DefaultRoot()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VORTEX_APPDATA_DIR")))
                return Path.Combine(EditorPaths.VortexAppData, "AssetDB");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = EditorPaths.AppDataRoot();
            return Path.Combine(local, "VortexEngine", "AssetDB");
        }

        /// <summary>The folder the library lives in right now (VORTEX_ASSETDB_DIR &gt; custom root &gt; default).</summary>
        public string EffectiveRoot
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("VORTEX_ASSETDB_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return string.IsNullOrWhiteSpace(Root) ? DefaultRoot() : Root;
            }
        }

        public bool Includes(AssetType type) => ExcludedTypes == null || !ExcludedTypes.Contains((int)type);

        public void SetIncluded(AssetType type, bool included)
        {
            if (ExcludedTypes == null) ExcludedTypes = new List<int>();
            ExcludedTypes.Remove((int)type);
            if (!included) ExcludedTypes.Add((int)type);
        }

        public long SizeCapBytes => SizeCapGB <= 0 ? 0 : (long)(SizeCapGB * 1024 * 1024 * 1024);

        public static LibrarySettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var ser = new DataContractJsonSerializer(typeof(LibrarySettings));
                    using (var fs = File.OpenRead(FilePath))
                        if (ser.ReadObject(fs) is LibrarySettings s) return s;
                }
            }
            catch { }
            return new LibrarySettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var ser = new DataContractJsonSerializer(typeof(LibrarySettings));
            string tmp = FilePath + ".tmp";
            using (var ms = new MemoryStream())
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    ser.WriteObject(w, this);
                    w.Flush();
                }
                File.WriteAllBytes(tmp, ms.ToArray());
            }
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }
    }
}
