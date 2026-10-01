using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Editor.Editors.WorldEditor.Components.FileExplorer.Models;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>
    /// A folder in the project file tree. Children are loaded lazily (a placeholder child makes the expander show
    /// until the folder is opened) and re-synced in place on file-system changes, so expansion and selection
    /// survive refreshes (the WPF tree rebuilt itself and collapsed).
    /// </summary>
    public sealed class FolderNode : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public FolderNode Parent { get; }
        public ObservableCollection<FolderNode> Children { get; } = new ObservableCollection<FolderNode>();
        public bool IsPlaceholder { get; private set; }
        public bool IsRoot => Parent == null;
        public bool IsLoaded { get; private set; }

        private string _fullPath;
        public string FullPath { get => _fullPath; private set { _fullPath = value; Raise(nameof(FullPath)); } }

        private string _name;
        public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(nameof(Name)); } }

        private bool _isExpanded;
        /// <summary>Two-way bound to the TreeViewItem; expanding loads the children.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                if (value) EnsureLoaded();
                Raise(nameof(IsExpanded));
            }
        }

        private bool _isRenaming;
        public bool IsRenaming { get => _isRenaming; set { if (_isRenaming == value) return; _isRenaming = value; Raise(nameof(IsRenaming)); } }

        private string _editName;
        public string EditName { get => _editName; set { if (_editName == value) return; _editName = value; Raise(nameof(EditName)); } }

        private bool _isDropTarget;
        public bool IsDropTarget { get => _isDropTarget; set { if (_isDropTarget == value) return; _isDropTarget = value; Raise(nameof(IsDropTarget)); Raise(nameof(DropBrush)); } }
        public IBrush DropBrush => _isDropTarget && Application.Current != null && Application.Current.TryFindResource("VxAccentSoftBrush", Application.Current.ActualThemeVariant, out var b) ? (IBrush)b : Brushes.Transparent;

        public string IconName => IsRoot ? "Home" : "FolderFill";

        public FolderNode(string fullPath, FolderNode parent, string displayName = null)
        {
            Parent = parent;
            _fullPath = AssetTile.Normalize(fullPath);
            _name = displayName ?? Path.GetFileName(_fullPath);
            if (HasSubfolders(_fullPath)) Children.Add(Placeholder(this));
        }

        private FolderNode(FolderNode parent) { Parent = parent; IsPlaceholder = true; _name = ""; _fullPath = ""; }
        private static FolderNode Placeholder(FolderNode parent) => new FolderNode(parent);

        public static bool HasSubfolders(string path)
        {
            try { return new DirectoryInfo(path).EnumerateDirectories().Any(d => !FileSystemItem.IsIgnoredDir(d)); }
            catch { return false; }
        }

        /// <summary>Replace the placeholder with the real sub-folders (once).</summary>
        public void EnsureLoaded()
        {
            if (IsLoaded || IsPlaceholder) return;
            IsLoaded = true;
            Sync(recursive: false);
        }

        /// <summary>Re-read the sub-folders in place: add new ones, drop vanished ones, keep existing nodes (their
        /// expansion + loaded children). Recurses into loaded children when <paramref name="recursive"/>.</summary>
        public void Sync(bool recursive)
        {
            if (IsPlaceholder) return;
            if (!IsLoaded)
            {
                bool has = HasSubfolders(FullPath);
                bool hasPlaceholder = Children.Count == 1 && Children[0].IsPlaceholder;
                if (has && Children.Count == 0) Children.Add(Placeholder(this));
                else if (!has && hasPlaceholder) Children.Clear();
                return;
            }
            string[] dirs;
            try
            {
                dirs = new DirectoryInfo(FullPath).GetDirectories().Where(d => !FileSystemItem.IsIgnoredDir(d))
                    .Select(d => d.FullName).OrderBy(d => Path.GetFileName(d), System.Collections.Generic.Comparer<string>.Create(AssetBrowserPanel.NaturalCompare)).ToArray();
            }
            catch { dirs = Array.Empty<string>(); }
            for (int i = Children.Count - 1; i >= 0; i--)
                if (Children[i].IsPlaceholder || !dirs.Any(d => string.Equals(AssetTile.Normalize(d), Children[i].FullPath, StringComparison.Ordinal)))
                    Children.RemoveAt(i);
            for (int i = 0; i < dirs.Length; i++)
            {
                string d = AssetTile.Normalize(dirs[i]);
                int existing = -1;
                for (int k = 0; k < Children.Count; k++) if (string.Equals(Children[k].FullPath, d, StringComparison.Ordinal)) { existing = k; break; }
                if (existing < 0) Children.Insert(Math.Min(i, Children.Count), new FolderNode(d, this));
                else if (existing != i && i < Children.Count) Children.Move(existing, i);
            }
            if (recursive) foreach (var c in Children) c.Sync(true);
        }

        /// <summary>The loaded node for <paramref name="path"/> under this one (loading folders along the way), or null.</summary>
        public FolderNode Find(string path, bool expandAlongTheWay)
        {
            path = AssetTile.Normalize(path);
            if (string.Equals(path, FullPath, StringComparison.OrdinalIgnoreCase)) return this;
            if (!path.StartsWith(FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            EnsureLoaded();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                foreach (var c in Children)
                {
                    if (c.IsPlaceholder) continue;
                    if (string.Equals(path, c.FullPath, StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith(c.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        if (expandAlongTheWay) IsExpanded = true;
                        return c.Find(path, expandAlongTheWay);
                    }
                }
                // a folder created a moment ago (the watcher resync is still pending): re-read this level once
                if (attempt == 0 && Directory.Exists(path)) Sync(recursive: false); else break;
            }
            return null;
        }

        public void CollapseAll()
        {
            foreach (var c in Children) if (!c.IsPlaceholder) c.CollapseAll();
            if (!IsRoot) IsExpanded = false;
        }

        public override string ToString() => Name;
    }
}
