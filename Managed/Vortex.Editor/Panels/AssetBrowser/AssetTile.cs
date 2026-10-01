using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>
    /// One tile / row of the Asset Browser: a folder, a file, the ".." parent link, a built-in primitive or a
    /// built-in material/texture. Thumbnail, tooltip, inline-rename and drop-highlight state notify so the tile
    /// updates in place (thumbnails arrive asynchronously after the tile is shown, like the WPF browser).
    /// </summary>
    public sealed class AssetTile : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Label under the tile (file name in the Explorer tab, stem in the type tabs — like WPF).</summary>
        public string Name { get; set; }
        /// <summary>Absolute path, or a pseudo path ("Primitive:Cube", "Material:Default", "Texture:White").</summary>
        public string FullPath { get; set; }
        /// <summary>Project-relative path with '/' separators (the "vortex/asset" drag payload); pseudo paths unchanged.</summary>
        public string RelPath { get; set; }
        public AssetKind Kind { get; set; }
        public string Extension { get; set; } = "";
        public string TypeName { get; set; }
        public string Icon { get; set; }
        public IBrush IconBrush { get; set; }
        public string Badge { get; set; }
        public bool HasBadge => !string.IsNullOrEmpty(Badge);
        public long Size { get; set; } = -1;
        public DateTime Modified { get; set; }
        /// <summary>Declared order of built-in tiles (primitives, built-in materials/textures).</summary>
        internal int Order;
        /// <summary>Folder shown in search results that span sub-folders (empty when the tile is in the listed folder).</summary>
        public string Location { get; set; } = "";

        public bool IsFolder => Kind == AssetKind.Folder || Kind == AssetKind.ParentFolder;
        public bool IsParentLink => Kind == AssetKind.ParentFolder;
        /// <summary>No file behind it (primitive / built-in) — can't be renamed, deleted, moved, revealed.</summary>
        public bool IsVirtual => Kind == AssetKind.Primitive || Kind == AssetKind.BuiltInMaterial || Kind == AssetKind.BuiltInTexture;
        /// <summary>A real file or folder the file operations may touch (not "..", not virtual).</summary>
        public bool IsFileSystemItem => !IsParentLink && !IsVirtual;
        public double TileOpacity => IsParentLink ? 0.5 : 1.0;
        public string SizeText => IsFolder || IsVirtual || Size < 0 ? "" : AssetKinds.FormatSize(Size);
        public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        private Bitmap _thumbnail;
        /// <summary>Real preview (image / 3D render / waveform / swatch); null shows the type icon instead.</summary>
        public Bitmap Thumbnail
        {
            get => _thumbnail;
            set { if (ReferenceEquals(_thumbnail, value)) return; _thumbnail = value; Raise(nameof(Thumbnail)); Raise(nameof(HasThumbnail)); }
        }
        public bool HasThumbnail => _thumbnail != null;
        /// <summary>Pixel size the current thumbnail was requested at (0 = none requested yet).</summary>
        internal int RequestedSize;

        private string _toolTip;
        public string ToolTip { get => _toolTip; set { if (_toolTip == value) return; _toolTip = value; Raise(nameof(ToolTip)); } }

        private bool _isRenaming;
        public bool IsRenaming { get => _isRenaming; set { if (_isRenaming == value) return; _isRenaming = value; Raise(nameof(IsRenaming)); } }

        private string _editName;
        public string EditName { get => _editName; set { if (_editName == value) return; _editName = value; Raise(nameof(EditName)); } }

        private bool _isDropTarget;
        /// <summary>A drag is hovering this folder tile (highlights the tile so the move target is obvious).</summary>
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set { if (_isDropTarget == value) return; _isDropTarget = value; Raise(nameof(IsDropTarget)); Raise(nameof(DropBrush)); }
        }
        public IBrush DropBrush => _isDropTarget
            ? (Application.Current != null && Application.Current.TryFindResource("VxAccentBrush", Application.Current.ActualThemeVariant, out var b) ? (IBrush)b : Brushes.DodgerBlue)
            : Brushes.Transparent;

        /// <summary>Identity for in-place refreshes: same path + same file stamp = same tile (keeps thumbnail + selection).</summary>
        internal string Key => (FullPath ?? "") + "|" + Kind;
        internal bool SameContent(AssetTile other) => other != null && other.Key == Key && other.Size == Size && other.Modified == Modified && other.Name == Name && other.Location == Location;

        public override string ToString() => Name;

        /// <summary>Full-path helpers shared by the browser and the tree.</summary>
        internal static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            try { return Path.GetFullPath(p).TrimEnd('/', '\\'); } catch { return p.TrimEnd('/', '\\'); }
        }
    }
}
