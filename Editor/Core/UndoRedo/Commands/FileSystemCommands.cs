using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Editor.Core.UndoRedo.Commands
{
    /// <summary>
    /// Befehl für das Erstellen einer Datei.
    /// Löscht die Datei bei Undo.
    /// </summary>
    public class CreateFileCommand : UndoableCommandBase
    {
        private readonly string _filePath;
        private readonly string _content;
        private readonly string _fileName;

        public override string Name => $"Create {_fileName}";

        /// <summary>
        /// Erstellt einen neuen CreateFileCommand.
        /// </summary>
        /// <param name="filePath">Vollständiger Pfad der zu erstellenden Datei.</param>
        /// <param name="content">Inhalt der Datei.</param>
        public CreateFileCommand(string filePath, string content = "")
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _content = content ?? "";
            _fileName = Path.GetFileName(filePath);
        }

        public override void Execute()
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(_filePath, _content);
        }

        public override void Undo()
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
    }

    /// <summary>
    /// Befehl für das Löschen einer Datei.
    /// Stellt die Datei bei Undo wieder her.
    /// </summary>
    public class DeleteFileCommand : UndoableCommandBase
    {
        private readonly string _filePath;
        private readonly string _fileName;
        private byte[] _backupContent;

        public override string Name => $"Delete {_fileName}";

        /// <summary>
        /// Erstellt einen neuen DeleteFileCommand.
        /// </summary>
        /// <param name="filePath">Vollständiger Pfad der zu löschenden Datei.</param>
        public DeleteFileCommand(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _fileName = Path.GetFileName(filePath);
        }

        public override void Execute()
        {
            if (File.Exists(_filePath))
            {
                // Binäres Backup für alle Dateitypen
                _backupContent = File.ReadAllBytes(_filePath);
                File.Delete(_filePath);
            }
        }

        public override void Undo()
        {
            if (_backupContent != null)
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(_filePath, _backupContent);
            }
        }
    }

    /// <summary>
    /// Befehl für das Löschen eines Ordners mit allem Inhalt.
    /// Speichert alle Dateien und Unterordner für Wiederherstellung.
    /// </summary>
    public class DeleteFolderCommand : UndoableCommandBase
    {
        private readonly string _folderPath;
        private readonly string _folderName;
        private Dictionary<string, byte[]> _fileBackups;
        private List<string> _directoryStructure;

        public override string Name => $"Delete Folder {_folderName}";

        /// <summary>
        /// Erstellt einen neuen DeleteFolderCommand.
        /// </summary>
        /// <param name="folderPath">Vollständiger Pfad des zu löschenden Ordners.</param>
        public DeleteFolderCommand(string folderPath)
        {
            _folderPath = folderPath ?? throw new ArgumentNullException(nameof(folderPath));
            _folderName = Path.GetFileName(folderPath);
        }

        public override void Execute()
        {
            if (!Directory.Exists(_folderPath))
                return;

            // Alle Dateien sichern
            _fileBackups = new Dictionary<string, byte[]>();
            _directoryStructure = new List<string>();

            BackupDirectory(_folderPath);

            // Ordner löschen
            Directory.Delete(_folderPath, true);
        }

        private void BackupDirectory(string path)
        {
            // Relativen Pfad speichern
            var relativePath = path.Substring(_folderPath.Length).TrimStart(Path.DirectorySeparatorChar);
            if (!string.IsNullOrEmpty(relativePath))
            {
                _directoryStructure.Add(relativePath);
            }

            // Dateien sichern
            foreach (var file in Directory.GetFiles(path))
            {
                var relativeFilePath = file.Substring(_folderPath.Length).TrimStart(Path.DirectorySeparatorChar);
                try
                {
                    _fileBackups[relativeFilePath] = File.ReadAllBytes(file);
                }
                catch
                {
                    // Datei konnte nicht gelesen werden - überspringen
                }
            }

            // Rekursiv für Unterordner
            foreach (var dir in Directory.GetDirectories(path))
            {
                BackupDirectory(dir);
            }
        }

        public override void Undo()
        {
            if (_fileBackups == null)
                return;

            // Hauptordner erstellen
            Directory.CreateDirectory(_folderPath);

            // Unterordner erstellen (sortiert nach Tiefe)
            foreach (var dir in _directoryStructure.OrderBy(d => d.Count(c => c == Path.DirectorySeparatorChar)))
            {
                var fullPath = Path.Combine(_folderPath, dir);
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
            }

            // Dateien wiederherstellen
            foreach (var kvp in _fileBackups)
            {
                var fullPath = Path.Combine(_folderPath, kvp.Key);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(fullPath, kvp.Value);
            }
        }
    }

    /// <summary>
    /// Befehl für das Umbenennen einer Datei oder eines Ordners.
    /// </summary>
    public class RenameFileCommand : UndoableCommandBase
    {
        private readonly string _oldPath;
        private readonly string _newPath;
        private readonly string _oldName;
        private readonly string _newName;
        private readonly bool _isDirectory;

        public override string Name => $"Rename {_oldName}";

        /// <summary>
        /// Erstellt einen neuen RenameFileCommand.
        /// </summary>
        /// <param name="oldPath">Alter Pfad der Datei.</param>
        /// <param name="newPath">Neuer Pfad der Datei.</param>
        /// <param name="isDirectory">True wenn es sich um einen Ordner handelt.</param>
        public RenameFileCommand(string oldPath, string newPath, bool isDirectory = false)
        {
            _oldPath = oldPath ?? throw new ArgumentNullException(nameof(oldPath));
            _newPath = newPath ?? throw new ArgumentNullException(nameof(newPath));
            _oldName = Path.GetFileName(oldPath);
            _newName = Path.GetFileName(newPath);
            _isDirectory = isDirectory;
        }

        public override void Execute()
        {
            if (_isDirectory)
            {
                if (Directory.Exists(_oldPath) && !Directory.Exists(_newPath))
                {
                    Directory.Move(_oldPath, _newPath);
                }
            }
            else
            {
                if (File.Exists(_oldPath) && !File.Exists(_newPath))
                {
                    File.Move(_oldPath, _newPath);
                }
            }
        }

        public override void Undo()
        {
            if (_isDirectory)
            {
                if (Directory.Exists(_newPath) && !Directory.Exists(_oldPath))
                {
                    Directory.Move(_newPath, _oldPath);
                }
            }
            else
            {
                if (File.Exists(_newPath) && !File.Exists(_oldPath))
                {
                    File.Move(_newPath, _oldPath);
                }
            }
        }
    }

    /// <summary>
    /// Befehl für das Erstellen eines Ordners.
    /// </summary>
    public class CreateFolderCommand : UndoableCommandBase
    {
        private readonly string _folderPath;
        private readonly string _folderName;

        public override string Name => $"Create Folder {_folderName}";

        /// <summary>
        /// Erstellt einen neuen CreateFolderCommand.
        /// </summary>
        /// <param name="folderPath">Vollständiger Pfad des zu erstellenden Ordners.</param>
        public CreateFolderCommand(string folderPath)
        {
            _folderPath = folderPath ?? throw new ArgumentNullException(nameof(folderPath));
            _folderName = Path.GetFileName(folderPath);
        }

        public override void Execute()
        {
            if (!Directory.Exists(_folderPath))
            {
                Directory.CreateDirectory(_folderPath);
            }
        }

        public override void Undo()
        {
            if (Directory.Exists(_folderPath) && IsDirectoryEmpty(_folderPath))
            {
                Directory.Delete(_folderPath);
            }
        }

        private bool IsDirectoryEmpty(string path)
        {
            try
            {
                return Directory.GetFiles(path).Length == 0 && 
                       Directory.GetDirectories(path).Length == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
        /// Befehl für das Verschieben einer Datei oder eines Ordners.
        /// </summary>
        public class MoveItemCommand : UndoableCommandBase
        {
            private readonly string _oldPath;
            private readonly string _newPath;
            private readonly bool _isDirectory;

            public override string Name => $"Move {Path.GetFileName(_oldPath)}";

            /// <summary>
            /// Erstellt einen neuen MoveItemCommand.
            /// </summary>
            /// <param name="oldPath">Alter Pfad.</param>
            /// <param name="newPath">Neuer Pfad.</param>
            /// <param name="isDirectory">True wenn es sich um einen Ordner handelt.</param>
            public MoveItemCommand(string oldPath, string newPath, bool isDirectory)
            {
                _oldPath = oldPath ?? throw new ArgumentNullException(nameof(oldPath));
                _newPath = newPath ?? throw new ArgumentNullException(nameof(newPath));
                _isDirectory = isDirectory;
            }

            public override void Execute()
            {
                if (_isDirectory && Directory.Exists(_oldPath))
                {
                    Directory.Move(_oldPath, _newPath);
                }
                else if (!_isDirectory && File.Exists(_oldPath))
                {
                    File.Move(_oldPath, _newPath);
                }
            }

            public override void Undo()
            {
                if (_isDirectory && Directory.Exists(_newPath))
                {
                    Directory.Move(_newPath, _oldPath);
                }
                else if (!_isDirectory && File.Exists(_newPath))
                {
                    File.Move(_newPath, _oldPath);
                }
            }
        }

        /// <summary>
        /// Befehl für das Kopieren einer Datei.
        /// Bei Undo wird die kopierte Datei gelöscht.
        /// </summary>
        public class CopyFileCommand : UndoableCommandBase
        {
            private readonly string _sourcePath;
            private readonly string _destPath;
            private readonly string _fileName;

            public override string Name => $"Copy {_fileName}";

            /// <summary>
            /// Erstellt einen neuen CopyFileCommand.
            /// </summary>
            /// <param name="sourcePath">Quellpfad der zu kopierenden Datei.</param>
            /// <param name="destPath">Zielpfad für die kopierte Datei.</param>
            public CopyFileCommand(string sourcePath, string destPath)
            {
                _sourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
                _destPath = destPath ?? throw new ArgumentNullException(nameof(destPath));
                _fileName = Path.GetFileName(sourcePath);
            }

            public override void Execute()
            {
                if (File.Exists(_sourcePath) && !File.Exists(_destPath))
                {
                    var directory = Path.GetDirectoryName(_destPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    File.Copy(_sourcePath, _destPath, false);
                }
            }

            public override void Undo()
            {
                if (File.Exists(_destPath))
                {
                    File.Delete(_destPath);
                }
            }
        }

        /// <summary>
        /// Befehl für das Kopieren eines Ordners.
        /// Bei Undo wird der kopierte Ordner gelöscht.
        /// </summary>
        public class CopyFolderCommand : UndoableCommandBase
        {
            private readonly string _sourcePath;
            private readonly string _destPath;
            private readonly string _folderName;

            public override string Name => $"Copy Folder {_folderName}";

            /// <summary>
            /// Erstellt einen neuen CopyFolderCommand.
            /// </summary>
            /// <param name="sourcePath">Quellpfad des zu kopierenden Ordners.</param>
            /// <param name="destPath">Zielpfad für den kopierten Ordner.</param>
            public CopyFolderCommand(string sourcePath, string destPath)
            {
                _sourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
                _destPath = destPath ?? throw new ArgumentNullException(nameof(destPath));
                _folderName = Path.GetFileName(sourcePath);
            }

            public override void Execute()
            {
                if (Directory.Exists(_sourcePath) && !Directory.Exists(_destPath))
                {
                    CopyDirectoryRecursive(_sourcePath, _destPath);
                }
            }

            private void CopyDirectoryRecursive(string sourceDir, string destDir)
            {
                Directory.CreateDirectory(destDir);

                foreach (var file in Directory.GetFiles(sourceDir))
                {
                    string destFile = Path.Combine(destDir, Path.GetFileName(file));
                    File.Copy(file, destFile, false);
                }

                foreach (var dir in Directory.GetDirectories(sourceDir))
                {
                    string destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                    CopyDirectoryRecursive(dir, destSubDir);
                }
            }

            public override void Undo()
            {
                if (Directory.Exists(_destPath))
                {
                    Directory.Delete(_destPath, true);
                }
            }
        }

        /// <summary>
            /// Composite-Befehl für das Einfügen mehrerer Dateien/Ordner.
            /// </summary>
            public class PasteItemsCommand : UndoableCommandBase
            {
                private readonly List<IUndoableCommand> _commands;
                private readonly bool _isCutOperation;
                private readonly int _itemCount;

                public override string Name => _isCutOperation 
                    ? $"Move {_itemCount} item(s)" 
                    : $"Paste {_itemCount} item(s)";

                /// <summary>
                /// Erstellt einen neuen PasteItemsCommand.
                /// </summary>
                /// <param name="commands">Liste der auszuführenden Befehle.</param>
                /// <param name="isCutOperation">True wenn es sich um eine Ausschneiden-Operation handelt.</param>
                public PasteItemsCommand(List<IUndoableCommand> commands, bool isCutOperation)
                {
                    _commands = commands ?? throw new ArgumentNullException(nameof(commands));
                    _isCutOperation = isCutOperation;
                    _itemCount = commands.Count;
                }

                public override void Execute()
                {
                    foreach (var command in _commands)
                    {
                        command.Execute();
                    }
                }

                public override void Undo()
                {
                    // Rückwärts ausführen
                    for (int i = _commands.Count - 1; i >= 0; i--)
                    {
                        _commands[i].Undo();
                    }
                }

                public override void Redo()
                {
                    foreach (var command in _commands)
                    {
                        command.Redo();
                    }
                }
            }

            /// <summary>
            /// Composite-Befehl für das Löschen mehrerer Dateien/Ordner.
            /// </summary>
            public class DeleteItemsCommand : UndoableCommandBase
            {
                private readonly List<IUndoableCommand> _commands;
                private readonly int _itemCount;

                public override string Name => $"Delete {_itemCount} item(s)";

                /// <summary>
                /// Erstellt einen neuen DeleteItemsCommand.
                /// </summary>
                /// <param name="commands">Liste der Delete-Befehle.</param>
                /// <param name="itemCount">Anzahl der zu löschenden Elemente.</param>
                public DeleteItemsCommand(List<IUndoableCommand> commands, int itemCount)
                {
                    _commands = commands ?? throw new ArgumentNullException(nameof(commands));
                    _itemCount = itemCount;
                }

                public override void Execute()
                {
                    foreach (var command in _commands)
                    {
                        command.Execute();
                    }
                }

                public override void Undo()
                {
                    // Rückwärts ausführen (wichtig für Ordnerstruktur)
                    for (int i = _commands.Count - 1; i >= 0; i--)
                    {
                        _commands[i].Undo();
                    }
                }

                public override void Redo()
                {
                    foreach (var command in _commands)
                    {
                        command.Redo();
                    }
                }
            }
        }
