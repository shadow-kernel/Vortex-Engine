using System;
using System.Collections.ObjectModel;
using System.IO;
using Editor.Core;

namespace Editor.Editors.WorldEditor.Components.FileExplorer.Models
{
    /// <summary>
    /// Repr�sentiert ein Element im Dateisystem (Datei oder Ordner).
    /// </summary>
    public class FileSystemItem : ViewModelBase
    {
        private string _name;
        private string _fullPath;
        private bool _isDirectory;
        private bool _isExpanded;
        private bool _isSelected;
        private bool _isRenaming;
        private bool _isCut;
        private ObservableCollection<FileSystemItem> _children;
        private FileSystemItem _parent;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value, nameof(Name));
        }

        public string FullPath
        {
            get => _fullPath;
            set
            {
                if (SetProperty(ref _fullPath, value, nameof(FullPath)))
                {
                    OnPropertyChanged(nameof(Extension));
                    OnPropertyChanged(nameof(Icon));
                    OnPropertyChanged(nameof(IconColor));
                }
            }
        }

        public bool IsDirectory
        {
            get => _isDirectory;
            set
            {
                if (SetProperty(ref _isDirectory, value, nameof(IsDirectory)))
                {
                    OnPropertyChanged(nameof(Icon));
                    OnPropertyChanged(nameof(IconColor));
                }
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value, nameof(IsExpanded)))
                {
                    // Lazy Loading: Lade Unterordner wenn aufgeklappt wird
                    if (value && IsDirectory && Children.Count == 0)
                    {
                        LoadDirectoriesOnly();
                    }
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value, nameof(IsSelected));
        }

        public bool IsRenaming
        {
            get => _isRenaming;
            set => SetProperty(ref _isRenaming, value, nameof(IsRenaming));
        }

        /// <summary>
        /// Gibt an, ob dieses Element ausgeschnitten wurde (Ctrl+X).
        /// </summary>
        public bool IsCut
        {
            get => _isCut;
            set
            {
                if (SetProperty(ref _isCut, value, nameof(IsCut)))
                {
                    OnPropertyChanged(nameof(ItemOpacity));
                }
            }
        }

        /// <summary>
        /// Opacity f�r die Anzeige (reduziert wenn ausgeschnitten).
        /// </summary>
        public double ItemOpacity => IsCut ? 0.5 : 1.0;

        public FileSystemItem Parent
        {
            get => _parent;
            set => SetProperty(ref _parent, value, nameof(Parent));
        }

        public ObservableCollection<FileSystemItem> Children
        {
            get => _children ?? (_children = new ObservableCollection<FileSystemItem>());
            set => SetProperty(ref _children, value, nameof(Children));
        }

        /// <summary>
        /// Pr�ft ob dieser Ordner Unterordner hat (f�r den Expander-Button)
        /// </summary>
        public bool HasSubDirectories
        {
            get
            {
                if (!IsDirectory || string.IsNullOrEmpty(FullPath))
                    return false;

                // Wenn bereits Kinder geladen sind
                if (_children != null && _children.Count > 0)
                    return true;

                // Pr�fe das Dateisystem
                try
                {
                    var dirInfo = new DirectoryInfo(FullPath);
                    foreach (var dir in dirInfo.EnumerateDirectories())
                    {
                        if (!IsIgnoredDir(dir))
                            return true;
                    }
                }
                catch
                {
                    // Zugriff verweigert oder anderer Fehler
                }
                return false;
            }
        }

        public string Extension => IsDirectory ? "" : Path.GetExtension(FullPath)?.ToLowerInvariant() ?? "";

        /// <summary>
        /// Build/dev folders that must never appear in the editor's file tree or asset browser.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> IgnoredDirs =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", "ProjectSettings", "Library", "Temp", "Logs", ".vs", ".git", ".idea", "node_modules" };

        /// <summary>True for hidden, dot- or build/dev directories that should be filtered out.</summary>
        public static bool IsIgnoredDir(DirectoryInfo dir) =>
            (dir.Attributes & FileAttributes.Hidden) != 0
            || dir.Name.StartsWith(".")
            || IgnoredDirs.Contains(dir.Name);

        /// <summary>
        /// Icon basierend auf dem Dateityp (Segoe MDL2 Assets)
        /// </summary>
        public string Icon
        {
            get
            {
                if (IsDirectory)
                    return "\uE8B7"; // Folder

                switch (Extension)
                {
                    case ".cs":
                        return "\uE943"; // Code
                    case ".xml":
                    case ".xaml":
                    case ".json":
                        return "\uE9D5"; // Document
                    case ".png":
                    case ".jpg":
                    case ".jpeg":
                    case ".bmp":
                    case ".tga":
                    case ".dds":
                        return "\uEB9F"; // Picture
                    case ".fbx":
                    case ".obj":
                    case ".dae":
                    case ".gltf":
                    case ".glb":
                        return "\uF158"; // 3D Model
                    case ".mat":
                        return "\uEB9F"; // Material
                    case ".wav":
                    case ".mp3":
                    case ".ogg":
                        return "\uE8D6"; // Audio
                    case ".mp4":
                    case ".avi":
                    case ".mov":
                        return "\uE8B2"; // Video
                    case ".scene":
                        return "\uE81E"; // Scene
                    case ".prefab":
                        return "\uE74C"; // Prefab
                    case ".shader":
                    case ".hlsl":
                    case ".glsl":
                        return "\uE950"; // Shader
                    default:
                        return "\uE8A5"; // Generic file
                }
            }
        }

        /// <summary>
        /// Icon-Farbe basierend auf dem Dateityp
        /// </summary>
        public string IconColor
        {
            get
            {
                if (IsDirectory)
                    return "#E6B422"; // Gold for folders

                switch (Extension)
                {
                    case ".cs":
                        return "#9B59B6"; // Purple
                    case ".xml":
                    case ".xaml":
                        return "#3498DB"; // Blue
                    case ".json":
                        return "#F39C12"; // Orange
                    case ".png":
                    case ".jpg":
                    case ".jpeg":
                    case ".bmp":
                    case ".tga":
                    case ".dds":
                        return "#E74C3C"; // Red
                    case ".fbx":
                    case ".obj":
                    case ".dae":
                    case ".gltf":
                    case ".glb":
                        return "#4EC9B0"; // Teal
                    case ".mat":
                        return "#9B59B6"; // Purple
                    case ".wav":
                    case ".mp3":
                    case ".ogg":
                        return "#1ABC9C"; // Green
                    case ".scene":
                        return "#3FA9F5"; // Accent Blue
                    case ".prefab":
                        return "#2ECC71"; // Green
                    case ".shader":
                    case ".hlsl":
                    case ".glsl":
                        return "#E91E63"; // Pink
                    default:
                        return "#808080"; // Gray
                }
            }
        }

        public FileSystemItem()
        {
            _children = new ObservableCollection<FileSystemItem>();
        }

        public FileSystemItem(string path) : this()
        {
            FullPath = path;
            Name = Path.GetFileName(path);
            IsDirectory = Directory.Exists(path);
        }

        public FileSystemItem(FileInfo fileInfo) : this()
        {
            FullPath = fileInfo.FullName;
            Name = fileInfo.Name;
            IsDirectory = false;
        }

        public FileSystemItem(DirectoryInfo dirInfo) : this()
        {
            FullPath = dirInfo.FullName;
            Name = dirInfo.Name;
            IsDirectory = true;
        }

        /// <summary>
        /// L�dt die Kinder dieses Ordners
        /// </summary>
        public void LoadChildren()
        {
            if (!IsDirectory || string.IsNullOrEmpty(FullPath))
                return;

            Children.Clear();

            try
            {
                var dirInfo = new DirectoryInfo(FullPath);

                // Ordner zuerst
                foreach (var dir in dirInfo.GetDirectories())
                {
                    // Versteckte Ordner und spezielle Ordner �berspringen
                    if (IsIgnoredDir(dir))
                        continue;

                    var child = new FileSystemItem(dir) { Parent = this };
                    Children.Add(child);
                }

                // Dann Dateien
                foreach (var file in dirInfo.GetFiles())
                {
                    // Versteckte Dateien �berspringen
                    if ((file.Attributes & FileAttributes.Hidden) != 0)
                        continue;

                    var child = new FileSystemItem(file) { Parent = this };
                    Children.Add(child);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Zugriff verweigert - ignorieren
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Laden der Kinder: {ex.Message}");
            }
        }

        /// <summary>
        /// L�dt die Ordner (ohne Dateien) f�r den Baum
        /// </summary>
        public void LoadDirectoriesOnly()
        {
            if (!IsDirectory || string.IsNullOrEmpty(FullPath))
                return;

            Children.Clear();

            try
            {
                var dirInfo = new DirectoryInfo(FullPath);

                foreach (var dir in dirInfo.GetDirectories())
                {
                    if (IsIgnoredDir(dir))
                        continue;

                    var child = new FileSystemItem(dir) { Parent = this };
                    Children.Add(child);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Laden der Ordner: {ex.Message}");
            }
        }

        public override string ToString() => Name;
    }
}
