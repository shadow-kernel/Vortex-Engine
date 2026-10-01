using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Editor.Core.Services;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>
    /// Drag payloads of the Asset Browser and the file tree. <see cref="AssetFormat"/> is the shared contract every
    /// drop target reads (viewport, hierarchy, inspector fields): the project-relative path of the dragged asset (or
    /// "Primitive:Cube"). Multi-selection drags add every dragged asset (<see cref="AssetListFormat"/>) and the
    /// absolute paths of the real files/folders (<see cref="MovePathsFormat"/>) used by folder drop targets to move.
    /// </summary>
    public static class AssetDragData
    {
        public const string AssetFormat = "vortex/asset";
        public const string AssetListFormat = "vortex/assets";
        public const string MovePathsFormat = "vortex/asset-paths";

#pragma warning disable CS0618 // DataObject is the drag API of Avalonia 11
        public static DataObject Create(IEnumerable<AssetTile> tiles)
        {
            var list = tiles.Where(t => t != null && !t.IsParentLink).ToList();
            var data = new DataObject();
            if (list.Count == 0) return data;
            data.Set(AssetFormat, list[0].RelPath);
            data.Set(AssetListFormat, list.Select(t => t.RelPath).ToArray());
            var fs = list.Where(t => t.IsFileSystemItem).Select(t => t.FullPath).ToArray();
            if (fs.Length > 0) data.Set(MovePathsFormat, fs);
            return data;
        }

        /// <summary>Payload for dragging one real folder/file (the file tree).</summary>
        public static DataObject ForPath(string fullPath)
        {
            var data = new DataObject();
            data.Set(AssetFormat, AssetFileOps.ToRelative(fullPath));
            data.Set(AssetListFormat, new[] { AssetFileOps.ToRelative(fullPath) });
            data.Set(MovePathsFormat, new[] { fullPath });
            return data;
        }
#pragma warning restore CS0618

        /// <summary>Absolute paths of editor-internal items being dragged (moves), or null.</summary>
        public static string[] MovePaths(IDataObject data) => data?.Get(MovePathsFormat) as string[];

        /// <summary>Local files dragged in from Finder (not editor-internal drags), or null.</summary>
        public static string[] OsFiles(IDataObject data)
        {
            if (data == null || data.Contains(MovePathsFormat) || data.Contains(AssetFormat) || !data.Contains(DataFormats.Files)) return null;
            var files = (data.GetFiles() ?? Enumerable.Empty<IStorageItem>()).Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            return files.Length > 0 ? files : null;
        }
    }

    /// <summary>View options of the Asset Browser, remembered per user (asset-browser.json in the app-data folder).</summary>
    public sealed class BrowserSettings
    {
        public string ViewMode { get; set; } = "Grid";
        public double TileSize { get; set; } = 96;
        public string SortBy { get; set; } = "Name";
        public bool SortDescending { get; set; }

        private static string FilePath => Path.Combine(EditorPaths.VortexAppData, "asset-browser.json");

        public static BrowserSettings Load()
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<BrowserSettings>(File.ReadAllText(FilePath)) ?? new BrowserSettings(); }
            catch { }
            return new BrowserSettings();
        }

        public void Save()
        {
            try { Directory.CreateDirectory(EditorPaths.VortexAppData); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); } catch { }
        }
    }
}
