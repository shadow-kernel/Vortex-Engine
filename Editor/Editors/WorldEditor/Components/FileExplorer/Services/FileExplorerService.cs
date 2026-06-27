using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Editor.Core.Data;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.Editors.WorldEditor.Components.FileExplorer.Models;

namespace Editor.Editors.WorldEditor.Components.FileExplorer.Services
{
    /// <summary>
    /// Service f�r Datei-Explorer Operationen mit FileSystemWatcher f�r Live-Updates.
    /// </summary>
    public class FileExplorerService : IDisposable
    {
        private static readonly Lazy<FileExplorerService> _instance = 
            new Lazy<FileExplorerService>(() => new FileExplorerService());
        public static FileExplorerService Instance => _instance.Value;

        private FileSystemWatcher _watcher;
        private string _rootPath;
        private FileSystemItem _rootItem;
        private FileSystemItem _currentFolder;
        private readonly ObservableCollection<FileSystemItem> _currentFolderContents;
        private readonly SynchronizationContext _syncContext;

        /// <summary>
        /// Event wenn sich der Inhalt des aktuellen Ordners �ndert.
        /// </summary>
        public event EventHandler FolderContentsChanged;

        /// <summary>
        /// Event wenn sich die Ordnerstruktur �ndert.
        /// </summary>
        public event EventHandler TreeStructureChanged;

        /// <summary>
        /// Event wenn ein neuer Ordner ausgew�hlt wird.
        /// </summary>
        public event EventHandler<FileSystemItem> CurrentFolderChanged;

        /// <summary>
        /// Der Wurzelordner des Projekts.
        /// </summary>
        public FileSystemItem RootItem
        {
            get => _rootItem;
            private set => _rootItem = value;
        }

        /// <summary>
        /// Der aktuell ausgew�hlte Ordner.
        /// </summary>
        public FileSystemItem CurrentFolder
        {
            get => _currentFolder;
            private set
            {
                if (_currentFolder != value)
                {
                    _currentFolder = value;
                    RefreshCurrentFolderContents();
                    CurrentFolderChanged?.Invoke(this, value);
                }
            }
        }

        /// <summary>
        /// Inhalt des aktuellen Ordners (Dateien und Unterordner).
        /// </summary>
        public ObservableCollection<FileSystemItem> CurrentFolderContents => _currentFolderContents;

        private FileExplorerService()
        {
            _currentFolderContents = new ObservableCollection<FileSystemItem>();
            _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
        }

        /// <summary>
        /// Initialisiert den Explorer mit dem Projektpfad.
        /// </summary>
        public void Initialize(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
                return;

            _rootPath = projectPath;

            // FileSystemWatcher stoppen falls vorhanden
            DisposeWatcher();

            // Wurzelelement erstellen
            RootItem = new FileSystemItem(projectPath)
            {
                Name = Path.GetFileName(projectPath) ?? "Project"
            };

            // Ordnerstruktur laden
            LoadTreeStructure(RootItem);

            // Aktuellen Ordner auf Root setzen
            CurrentFolder = RootItem;

            // FileSystemWatcher starten
            SetupFileSystemWatcher();
        }

        /// <summary>
        /// L�dt die Ordnerstruktur rekursiv (nur eine Ebene tief).
        /// </summary>
        private void LoadTreeStructure(FileSystemItem item)
        {
            if (!item.IsDirectory)
                return;

            item.LoadDirectoriesOnly();

            foreach (var child in item.Children)
            {
                // Lazy Loading: Nur die direkten Kinder laden
                child.LoadDirectoriesOnly();
            }
        }

        /// <summary>
        /// Aktualisiert den Inhalt des aktuellen Ordners.
        /// </summary>
        public void RefreshCurrentFolderContents()
        {
            _currentFolderContents.Clear();

            if (_currentFolder == null || !Directory.Exists(_currentFolder.FullPath))
                return;

            try
            {
                var dirInfo = new DirectoryInfo(_currentFolder.FullPath);

                // Ordner zuerst
                foreach (var dir in dirInfo.GetDirectories())
                {
                    if ((dir.Attributes & FileAttributes.Hidden) != 0 ||
                        dir.Name.StartsWith("."))
                        continue;

                    _currentFolderContents.Add(new FileSystemItem(dir) { Parent = _currentFolder });
                }

                // Dann Dateien
                foreach (var file in dirInfo.GetFiles())
                {
                    if ((file.Attributes & FileAttributes.Hidden) != 0)
                        continue;

                    _currentFolderContents.Add(new FileSystemItem(file) { Parent = _currentFolder });
                }

                FolderContentsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Fehler beim Laden des Ordnerinhalts: {ex.Message}");
            }
        }

        /// <summary>
        /// Navigiert zu einem Ordner.
        /// </summary>
        public void NavigateTo(FileSystemItem folder)
        {
            if (folder == null || !folder.IsDirectory)
                return;

            CurrentFolder = folder;
        }

        /// <summary>Resolve an absolute folder path to its FileSystemItem in the tree (expanding as needed).</summary>
        public FileSystemItem FindFolder(string absolutePath)
        {
            if (RootItem == null || string.IsNullOrEmpty(absolutePath)) return null;
            string target;
            try { target = Path.GetFullPath(absolutePath).TrimEnd('\\', '/'); } catch { return null; }
            if (string.Equals(RootItem.FullPath.TrimEnd('\\', '/'), target, StringComparison.OrdinalIgnoreCase))
                return RootItem;
            return FindFolderRecursive(RootItem, target);
        }

        private FileSystemItem FindFolderRecursive(FileSystemItem node, string target)
        {
            try { node.LoadDirectoriesOnly(); } catch { }
            foreach (var child in node.Children)
            {
                if (!child.IsDirectory) continue;
                var cp = child.FullPath.TrimEnd('\\', '/');
                if (string.Equals(cp, target, StringComparison.OrdinalIgnoreCase)) return child;
                if (target.StartsWith(cp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    var found = FindFolderRecursive(child, target);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>Navigate to a folder by absolute path (resolves it in the tree first). Used for browser-&gt;tree reveal.</summary>
        public void NavigateToPath(string absolutePath)
        {
            var item = FindFolder(absolutePath);
            if (item != null) NavigateTo(item);
        }

        /// <summary>Filter the folder tree by name. Matching folders + their ancestors stay visible and
        /// expand; everything else is hidden. Empty text resets the tree.</summary>
        public void ApplyFilter(string text)
        {
            if (RootItem == null) return;
            if (string.IsNullOrWhiteSpace(text)) { ResetFilter(RootItem); RootItem.IsVisible = true; return; }
            FilterNode(RootItem, text.Trim());
            RootItem.IsVisible = true;
        }

        private bool FilterNode(FileSystemItem node, string text)
        {
            try { node.LoadDirectoriesOnly(); } catch { }
            bool selfMatch = (node.Name ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            bool anyChild = false;
            foreach (var child in node.Children)
            {
                if (FilterNode(child, text)) anyChild = true;
            }
            bool visible = selfMatch || anyChild;
            node.IsVisible = visible;
            if (anyChild) node.IsExpanded = true; // open the path to matches
            return visible;
        }

        private void ResetFilter(FileSystemItem node)
        {
            node.IsVisible = true;
            foreach (var child in node.Children) ResetFilter(child);
        }

        /// <summary>
        /// Navigiert zum �bergeordneten Ordner.
        /// </summary>
        public void NavigateUp()
        {
            if (_currentFolder?.Parent != null)
            {
                CurrentFolder = _currentFolder.Parent;
            }
        }

        /// <summary>
        /// Erstellt einen neuen Ordner im aktuellen Verzeichnis.
        /// Diese Operation ist Undo-f�hig.
        /// </summary>
        public FileSystemItem CreateFolder(string name = "New Folder")
        {
            if (_currentFolder == null)
                return null;

            try
            {
                string baseName = name;
                string newPath = Path.Combine(_currentFolder.FullPath, name);
                int counter = 1;

                while (Directory.Exists(newPath))
                {
                    name = $"{baseName} ({counter++})";
                    newPath = Path.Combine(_currentFolder.FullPath, name);
                }

                // Undo-f�higes Command erstellen
                var command = new CreateFolderCommand(newPath);
                UndoRedoManager.Instance.Execute(command);
                
                var newItem = new FileSystemItem(newPath) { Parent = _currentFolder };
                return newItem;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Erstellen des Ordners: {ex.Message}", 
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        /// <summary>
        /// Erstellt eine neue Datei im aktuellen Verzeichnis.
        /// Diese Operation ist Undo-f�hig.
        /// </summary>
        public FileSystemItem CreateFile(string name, string content = "")
        {
            if (_currentFolder == null)
                return null;

            try
            {
                string baseName = Path.GetFileNameWithoutExtension(name);
                string extension = Path.GetExtension(name);
                string newPath = Path.Combine(_currentFolder.FullPath, name);
                int counter = 1;

                while (File.Exists(newPath))
                {
                    name = $"{baseName} ({counter++}){extension}";
                    newPath = Path.Combine(_currentFolder.FullPath, name);
                }

                // Undo-f�higes Command erstellen
                var command = new CreateFileCommand(newPath, content);
                UndoRedoManager.Instance.Execute(command);
                
                var newItem = new FileSystemItem(newPath) { Parent = _currentFolder };
                return newItem;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Erstellen der Datei: {ex.Message}", 
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        /// <summary>
        /// Benennt ein Element um.
        /// Diese Operation ist Undo-f�hig.
        /// </summary>
        public bool Rename(FileSystemItem item, string newName)
        {
            if (item == null || string.IsNullOrWhiteSpace(newName))
                return false;

            try
            {
                // Den echten aktuellen Namen aus dem Dateipfad extrahieren
                // (item.Name k�nnte schon durch das UI-Binding ge�ndert worden sein)
                string currentActualName = Path.GetFileName(item.FullPath);
                
                // Keine �nderung wenn Name gleich bleibt
                if (currentActualName == newName)
                {
                    // Name zur�cksetzen falls Binding ihn ge�ndert hat
                    item.Name = currentActualName;
                    return true;
                }

                string parentPath = Path.GetDirectoryName(item.FullPath);
                string newPath = Path.Combine(parentPath, newName);
                string oldPath = item.FullPath;

                if (item.IsDirectory)
                {
                    if (Directory.Exists(newPath))
                    {
                        // Name zur�cksetzen
                        item.Name = currentActualName;
                        MessageBox.Show("Ein Ordner mit diesem Namen existiert bereits.", 
                            "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }
                else
                {
                    if (File.Exists(newPath))
                    {
                        // Name zur�cksetzen
                        item.Name = currentActualName;
                        MessageBox.Show("Eine Datei mit diesem Namen existiert bereits.", 
                            "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }

                // Undo-f�higes Command erstellen und ausf�hren
                var command = new RenameFileCommand(oldPath, newPath, item.IsDirectory);
                UndoRedoManager.Instance.Execute(command);

                // FileSystemItem aktualisieren nach erfolgreicher Umbenennung
                item.FullPath = newPath;
                item.Name = newName;
                return true;
            }
            catch (Exception ex)
            {
                // Bei Fehler den urspr�nglichen Namen wiederherstellen
                item.Name = Path.GetFileName(item.FullPath);
                MessageBox.Show($"Fehler beim Umbenennen: {ex.Message}", 
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// L�scht ein Element.
        /// Diese Operation ist Undo-f�hig.
        /// </summary>
        public bool Delete(FileSystemItem item)
        {
            if (item == null)
                return false;

            try
            {
                var result = MessageBox.Show(
                    $"M�chten Sie '{item.Name}' wirklich l�schen?\n\nDiese Aktion kann mit Strg+Z r�ckg�ngig gemacht werden.",
                    "L�schen best�tigen",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return false;

                // Undo-f�higes Command erstellen
                if (item.IsDirectory)
                {
                    var command = new DeleteFolderCommand(item.FullPath);
                    UndoRedoManager.Instance.Execute(command);
                }
                else
                {
                    var command = new DeleteFileCommand(item.FullPath);
                    UndoRedoManager.Instance.Execute(command);
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim L�schen: {ex.Message}", 
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Verschiebt ein Element in einen Zielordner.
        /// Diese Operation ist Undo-f�hig.
        /// </summary>
        public bool MoveItem(FileSystemItem item, FileSystemItem targetFolder)
        {
            if (item == null || targetFolder == null || !targetFolder.IsDirectory)
                return false;

            // Kann nicht in sich selbst verschieben
            if (item.FullPath == targetFolder.FullPath)
                return false;

            // Kann nicht in einen Unterordner von sich selbst verschieben
            if (targetFolder.FullPath.StartsWith(item.FullPath + Path.DirectorySeparatorChar))
                return false;

            try
            {
                string oldPath = item.FullPath;
                string newPath = Path.Combine(targetFolder.FullPath, item.Name);

                if (item.IsDirectory)
                {
                    if (Directory.Exists(newPath))
                    {
                        MessageBox.Show("Ein Ordner mit diesem Namen existiert bereits im Zielordner.", 
                            "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }
                else
                {
                    if (File.Exists(newPath))
                    {
                        MessageBox.Show("Eine Datei mit diesem Namen existiert bereits im Zielordner.", 
                            "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }

                // Undo-f�higes Command erstellen und ausf�hren
                var command = new MoveItemCommand(oldPath, newPath, item.IsDirectory);
                UndoRedoManager.Instance.Execute(command);

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Verschieben: {ex.Message}", 
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// �ffnet einen Ordner im Windows Explorer.
        /// </summary>
        public void OpenInExplorer(FileSystemItem item)
        {
            if (item == null)
                return;

            try
            {
                if (item.IsDirectory)
                {
                    Process.Start("explorer.exe", item.FullPath);
                }
                else
                {
                    Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Fehler beim �ffnen im Explorer: {ex.Message}");
            }
        }

        /// <summary>
        /// �ffnet eine Datei mit dem Standard-Programm.
        /// </summary>
        public void OpenFile(FileSystemItem item)
        {
            if (item == null || item.IsDirectory)
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = item.FullPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim �ffnen der Datei: {ex.Message}", 
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region FileSystemWatcher

        private void SetupFileSystemWatcher()
        {
            if (string.IsNullOrEmpty(_rootPath) || !Directory.Exists(_rootPath))
                return;

            _watcher = new FileSystemWatcher(_rootPath)
            {
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | 
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnFileSystemChanged;
            _watcher.Deleted += OnFileSystemChanged;
            _watcher.Renamed += OnFileSystemRenamed;
            _watcher.Changed += OnFileSystemChanged;
        }

        private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
        {
            // Versteckte Dateien/Ordner ignorieren
            if (e.Name?.StartsWith(".") == true)
                return;

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                // Tree neu laden
                if (RootItem != null)
                {
                    LoadTreeStructure(RootItem);
                    TreeStructureChanged?.Invoke(this, EventArgs.Empty);
                }

                // Aktuellen Ordner aktualisieren
                RefreshCurrentFolderContents();
            }), DispatcherPriority.Background);
        }

        private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
        {
            OnFileSystemChanged(sender, e);
        }

        private void DisposeWatcher()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileSystemChanged;
                _watcher.Deleted -= OnFileSystemChanged;
                _watcher.Renamed -= OnFileSystemRenamed;
                _watcher.Changed -= OnFileSystemChanged;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        #endregion

        #region Default Project Folders

        /// <summary>
        /// Erstellt die Standard-Ordnerstruktur f�r ein neues Projekt.
        /// </summary>
        public static void CreateDefaultProjectFolders(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return;

            string[] defaultFolders = new[]
            {
                "Assets",
                "Assets/Materials",
                "Assets/Models",
                "Assets/Prefabs",
                "Assets/Scenes",
                "Assets/Scripts",
                "Assets/Textures",
                "Assets/Audio",
                "Assets/Shaders",
                "Assets/Fonts",
                "Assets/UI",
                "Packages",
                "ProjectSettings"
            };

            foreach (var folder in defaultFolders)
            {
                string fullPath = Path.Combine(projectPath, folder);
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
            }
        }

        #endregion

        public void Dispose()
        {
            DisposeWatcher();
        }
    }
}
