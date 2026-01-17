using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Editor.Core.Data;
using Editor.Core.Exceptions;
using Editor.Core.Serialization;
using Editor.Core.Validation;

namespace Editor.Core.Services
{
    /// <summary>
    /// Zentraler Service für alle Projektoperationen.
    /// Verwaltet das Laden, Speichern und die Registry aller Projekte.
    /// </summary>
    public sealed class ProjectService
    {
        private static readonly Lazy<ProjectService> _instance = new Lazy<ProjectService>(() => new ProjectService());
        public static ProjectService Instance => _instance.Value;

        private readonly Dictionary<Guid, ProjectRef> _projectRegistry;
        private readonly string _appDataPath;
        private readonly string _registryFilePath;
        private readonly string _defaultProjectsPath;

        private ProjectService()
        {
            _appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VortexEngine");
            _defaultProjectsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VortexEngineProjects");
            _registryFilePath = Path.Combine(_appDataPath, "projects.json");

            EnsureDirectoriesExist();
            _projectRegistry = LoadProjectRegistry();
        }

        /// <summary>
        /// Gibt alle registrierten Projekte zurück
        /// </summary>
        public Dictionary<Guid, ProjectRef> GetAllProjects() => _projectRegistry;

        /// <summary>
        /// Lädt ein Projekt anhand seiner Referenz
        /// </summary>
        public ProjectData LoadProject(ProjectRef projectRef)
        {
            try
            {
                string projectFilePath = Path.Combine(projectRef.Path, ".ve", "project.json");
                var project = DataSerializer.LoadFromJson<ProjectData>(projectFilePath);

                // Stelle sicher dass Scenes initialisiert sind
                if (project.Scenes == null)
                {
                    project.Scenes = new ObservableCollection<Scene>();
                }

                // OnDeserialized wird automatisch aufgerufen und setzt Project-Referenzen

                return project;
            }
            catch (ProjectException)
            {
                throw;
            }
            catch (IOException ioEx)
            {
                throw new ProjectIOException(
                    projectRef?.Path ?? "unbekannt",
                    "Fehler beim Öffnen des Projekts.",
                    ioEx
                );
            }
            catch (UnauthorizedAccessException uaEx)
            {
                throw new ProjectIOException(
                    projectRef?.Path ?? "unbekannt",
                    "Keine Berechtigung zum Zugriff auf den Projektpfad.",
                    uaEx
                );
            }
            catch (Exception ex)
            {
                throw new ProjectException(
                    $"Unerwarteter Fehler beim Öffnen des Projekts: {ex.Message}",
                    ex
                );
            }
        }

        /// <summary>
        /// Erstellt ein neues Projekt
        /// </summary>
        public ProjectData CreateProject(string projectName, string projectPath)
        {
            var project = new ProjectData(projectPath, projectName);
            SaveProject(project);
            return project;
        }

        /// <summary>
        /// Speichert ein Projekt
        /// </summary>
        public void SaveProject(ProjectData project)
        {
            try
            {
                ProjectValidator.ValidateProject(project, _projectRegistry);

                // Projekt in Registry speichern
                _projectRegistry[project.Id] = new ProjectRef(project.Id, project.Path, project.Name)
                {
                    ImagePath = project.ImagePath
                };

                // Projektdateien erstellen
                CreateProjectFiles(project);

                // Registry speichern
                SaveProjectRegistry();
            }
            catch (ProjectException)
            {
                throw;
            }
            catch (IOException ioEx)
            {
                throw new ProjectIOException(
                    project?.Path ?? "unbekannt",
                    "Fehler beim Speichern des Projekts.",
                    ioEx
                );
            }
            catch (UnauthorizedAccessException uaEx)
            {
                throw new ProjectIOException(
                    project?.Path ?? "unbekannt",
                    "Keine Berechtigung zum Zugriff auf den Projektpfad.",
                    uaEx
                );
            }
            catch (Exception ex)
            {
                throw new ProjectException(
                    $"Unerwarteter Fehler beim Speichern des Projekts: {ex.Message}",
                    ex
                );
            }
        }

        /// <summary>
        /// Prüft ob ein Projektpfad bereits existiert
        /// </summary>
        public bool ProjectPathExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string normalizedPath = NormalizePath(path);

            foreach (var project in _projectRegistry.Values)
            {
                if (string.Equals(NormalizePath(project.Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
            }

            private void CreateProjectFiles(ProjectData project)
            {
                // Projektverzeichnis erstellen
                if (!Directory.Exists(project.Path))
                {
                    Directory.CreateDirectory(project.Path);
                }

                // .ve Verzeichnis erstellen (versteckt)
                string veDir = Path.Combine(project.Path, ".ve");
            if (!Directory.Exists(veDir))
            {
                var dirInfo = Directory.CreateDirectory(veDir);
                dirInfo.Attributes = FileAttributes.Hidden;
            }

            // Icon speichern
            var iconPath = SaveIconFromResources("AppIcon", project.Path);
            if (!string.IsNullOrEmpty(iconPath))
            {
                project.ImagePath = iconPath;
            }

            // Projekt als JSON speichern
            string projectFilePath = Path.Combine(veDir, "project.json");
            DataSerializer.SaveAsJson(project, projectFilePath);
        }

        private void EnsureDirectoriesExist()
        {
            if (!Directory.Exists(_appDataPath))
            {
                Directory.CreateDirectory(_appDataPath);
            }

            if (!Directory.Exists(_defaultProjectsPath))
            {
                Directory.CreateDirectory(_defaultProjectsPath);
            }
        }

        private Dictionary<Guid, ProjectRef> LoadProjectRegistry()
        {
            try
            {
                if (!File.Exists(_registryFilePath))
                {
                    return new Dictionary<Guid, ProjectRef>();
                }

                var json = File.ReadAllText(_registryFilePath);
                return DataSerializer.FromJson<Dictionary<Guid, ProjectRef>>(json) 
                       ?? new Dictionary<Guid, ProjectRef>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Laden der Projekt-Registry: {ex.Message}");
                return new Dictionary<Guid, ProjectRef>();
            }
        }

        private void SaveProjectRegistry()
        {
            var json = DataSerializer.ToJson(_projectRegistry);
            File.WriteAllText(_registryFilePath, json);
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }

        private string SaveIconFromResources(string resourceKey, string projectPath, string fileName = "icon.png")
        {
            try
            {
                var resource = Application.Current.FindResource(resourceKey) as BitmapImage;
                if (resource == null)
                    return null;

                string iconPath = Path.Combine(projectPath, ".ve", fileName);

                using (var fileStream = new FileStream(iconPath, FileMode.Create))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(resource));
                    encoder.Save(fileStream);
                }

                return iconPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Speichern des Icons: {ex.Message}");
                return null;
            }
        }
    }
}
