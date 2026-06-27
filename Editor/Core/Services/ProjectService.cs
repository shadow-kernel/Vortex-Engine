using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Exceptions;
using Editor.Core.Serialization;
using Editor.Core.Validation;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Zentraler Service f�r alle Projektoperationen.
    /// Verwaltet das Laden, Speichern und die Registry aller Projekte.
    /// Szenen werden separat in .vscene Dateien gespeichert.
    /// </summary>
    public sealed class ProjectService
    {
        private static readonly Lazy<ProjectService> _instance = new Lazy<ProjectService>(() => new ProjectService());
        public static ProjectService Instance => _instance.Value;

        private readonly Dictionary<Guid, ProjectRef> _projectRegistry;
        private readonly string _appDataPath;
        private readonly string _registryFilePath;
        private readonly string _defaultProjectsPath;

        /// <summary>
        /// Konstanten f�r Projektstruktur
        /// </summary>
        public const string ManifestFileName = "project.vortex";
        public const string LegacyManifestPath = ".ve/project.json";
        public const string AssetsFolder = "Assets";
        public const string ScenesFolder = "Scenes";
        public const string PrefabsFolder = "Prefabs";

        private ProjectService()
        {
            _appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VortexEngine");
            _defaultProjectsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VortexEngineProjects");
            _registryFilePath = Path.Combine(_appDataPath, "projects.json");

            EnsureDirectoriesExist();
            _projectRegistry = LoadProjectRegistry();
        }

        /// <summary>
        /// Gibt alle registrierten Projekte zur�ck
        /// </summary>
        public Dictionary<Guid, ProjectRef> GetAllProjects() => _projectRegistry;

        /// <summary>
        /// L�dt ein Projekt anhand seiner Referenz
        /// </summary>
        /// <summary>Load a project directly from its folder (used by the standalone player, which ships inside the project).</summary>
        public ProjectData LoadProjectFromPath(string dir) => LoadProject(new ProjectRef(dir, "Game"));

        public ProjectData LoadProject(ProjectRef projectRef)
        {
            try
            {
                // Versuche neues Format zuerst
                string manifestPath = Path.Combine(projectRef.Path, ManifestFileName);
                if (File.Exists(manifestPath))
                {
                    return LoadProjectFromManifest(projectRef.Path);
                }

                // Fallback auf Legacy-Format
                string legacyPath = Path.Combine(projectRef.Path, LegacyManifestPath);
                if (File.Exists(legacyPath))
                {
                    return LoadLegacyProject(projectRef, legacyPath);
                }

                throw new FileNotFoundException($"Project file not found in {projectRef.Path}");
            }
            catch (ProjectException)
            {
                throw;
            }
            catch (IOException ioEx)
            {
                throw new ProjectIOException(
                    projectRef?.Path ?? "unbekannt",
                    "Fehler beim �ffnen des Projekts.",
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
                    $"Unerwarteter Fehler beim �ffnen des Projekts: {ex.Message}",
                    ex
                );
            }
        }

        /// <summary>
        /// L�dt ein Projekt aus dem neuen Manifest-Format
        /// </summary>
        private ProjectData LoadProjectFromManifest(string projectPath)
        {
            var manifestPath = Path.Combine(projectPath, ManifestFileName);
            var manifest = DataSerializer.LoadFromJson<ProjectManifest>(manifestPath);

            // Erstelle ProjectData aus Manifest
            var project = new ProjectData(manifest.Id, projectPath, manifest.Name)
            {
                LastModified = manifest.LastModified,
                ImagePath = manifest.ThumbnailPath
            };

            // Initialize asset database for this project
            AssetDatabase.Instance.Initialize(projectPath);

            // Lade Szenen
            var scenesPath = Path.Combine(projectPath, AssetsFolder, ScenesFolder);
            foreach (var sceneRef in manifest.Scenes)
            {
                var sceneFilePath = Path.Combine(scenesPath, sceneRef.RelativePath);

                if (File.Exists(sceneFilePath))
                {
                    var scene = DataSerializer.LoadFromBinary<Scene>(sceneFilePath);
                    scene.FilePath = sceneFilePath;
                    scene.Project = project;
                    scene.Load();
                    project.Scenes.Add(scene);

                    // Setze aktive Szene
                    if (manifest.LastOpenSceneId.HasValue && scene.Id == manifest.LastOpenSceneId.Value)
                    {
                        project.ActiveScene = scene;
                    }
                }
            }

            // Falls keine aktive Szene, nehme die erste
            if (project.ActiveScene == null && project.Scenes.Count > 0)
            {
                project.ActiveScene = project.Scenes[0];
            }

            return project;
        }

        /// <summary>
        /// L�dt ein Projekt im Legacy-Format und migriert es
        /// </summary>
        private ProjectData LoadLegacyProject(ProjectRef projectRef, string legacyPath)
        {
            var project = DataSerializer.LoadFromJson<ProjectData>(legacyPath);

            if (project.Scenes == null)
            {
                project.Scenes = new ObservableCollection<Scene>();
            }

            // Initialize asset database
            AssetDatabase.Instance.Initialize(projectRef.Path);

            // Migriere zum neuen Format
            SaveProject(project);

            return project;
        }

        /// <summary>
        /// Erstellt ein neues Projekt
        /// </summary>
        public ProjectData CreateProject(string projectName, string projectPath)
        {
            try
            {
                // Erstelle Projektverzeichnis zuerst
                if (!Directory.Exists(projectPath))
                {
                    Directory.CreateDirectory(projectPath);
                }
                
                var project = new ProjectData(projectPath, projectName);

                // Erstelle Default-Szene
                var defaultScene = SceneService.Instance.CreateDefaultScene(project, "Main Scene");
                project.Scenes.Clear(); // Remove any default scenes from constructor
                project.Scenes.Add(defaultScene);
                project.ActiveScene = defaultScene;

                // Initialize asset database
                AssetDatabase.Instance.Initialize(projectPath);

                SaveProject(project);
                
                System.Diagnostics.Debug.WriteLine($"Project created successfully at: {projectPath}");
                return project;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating project: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Speichert ein Projekt (Manifest + separate Szenen-Dateien)
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

                // Aktualisiere LastModified
                project.LastModified = DateTime.Now;

                // Projektdateien erstellen
                CreateProjectFiles(project);

                // Speichere Manifest
                SaveManifest(project);

                // Speichere alle Szenen separat
                SaveAllScenes(project);

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
        /// Speichert das Projekt-Manifest (ohne Szenen-Inhalte)
        /// </summary>
        private void SaveManifest(ProjectData project)
        {
            try
            {
                var manifest = new ProjectManifest(project.Name)
                {
                    Id = project.Id,
                    LastModified = project.LastModified,
                    ThumbnailPath = project.ImagePath,
                    LastOpenSceneId = project.ActiveScene?.Id,
                };

                // Füge Szenen-Referenzen hinzu
                foreach (var scene in project.Scenes)
                {
                    var relativePath = $"{SanitizeFileName(scene.Name)}.vscene";
                    manifest.Scenes.Add(new SceneReference(scene.Id, scene.Name, relativePath));

                    if (manifest.StartSceneId == null)
                    {
                        manifest.StartSceneId = scene.Id;
                    }
                }

                var manifestPath = Path.Combine(project.Path, ManifestFileName);
                
                // Ensure directory exists
                var dir = Path.GetDirectoryName(manifestPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                DataSerializer.SaveAsJson(manifest, manifestPath);
                System.Diagnostics.Debug.WriteLine($"Saved manifest to: {manifestPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving manifest: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Speichert alle Szenen des Projekts
        /// </summary>
        public void SaveAllScenes(ProjectData project)
        {
            try
            {
                var scenesPath = Path.Combine(project.Path, AssetsFolder, ScenesFolder);

                if (!Directory.Exists(scenesPath))
                {
                    Directory.CreateDirectory(scenesPath);
                }

                foreach (var scene in project.Scenes)
                {
                    SaveScene(project, scene);
                }
                
                System.Diagnostics.Debug.WriteLine($"Saved {project.Scenes.Count} scenes to: {scenesPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving scenes: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Speichert eine einzelne Szene
        /// </summary>
        public void SaveScene(ProjectData project, Scene scene)
        {
            var scenesPath = Path.Combine(project.Path, AssetsFolder, ScenesFolder);

            if (!Directory.Exists(scenesPath))
            {
                Directory.CreateDirectory(scenesPath);
            }

            // Setze FilePath falls nicht gesetzt
            if (string.IsNullOrEmpty(scene.FilePath))
            {
                scene.FilePath = Path.Combine(scenesPath, $"{SanitizeFileName(scene.Name)}.vscene");
            }

            // Speichere Szene als Bin�r
            DataSerializer.SaveAsBinary(scene, scene.FilePath);
            scene.IsDirty = false;
        }

        /// <summary>
        /// Speichert eine Entity als Prefab
        /// </summary>
        public void SavePrefab(ProjectData project, GameEntity entity, string prefabName = null)
        {
            var prefabsPath = Path.Combine(project.Path, AssetsFolder, PrefabsFolder);

            if (!Directory.Exists(prefabsPath))
            {
                Directory.CreateDirectory(prefabsPath);
            }

            var fileName = SanitizeFileName(prefabName ?? entity.Name) + ".ventity";
            var filePath = Path.Combine(prefabsPath, fileName);

            DataSerializer.SaveAsBinary(entity, filePath);
        }

        /// <summary>
        /// L�dt ein Prefab
        /// </summary>
        public GameEntity LoadPrefab(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Prefab file not found: {filePath}");
            }

            return DataSerializer.LoadFromBinary<GameEntity>(filePath);
        }

        /// <summary>
        /// Entfernt ung�ltige Zeichen aus Dateinamen
        /// </summary>
        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars)
            {
                fileName = fileName.Replace(c, '_');
            }
            return fileName;
        }

        /// <summary>
        /// Pr�ft ob ein Projektpfad bereits existiert
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

                // Standard-Projektordner erstellen
                CreateDefaultProjectFolders(project.Path);

                // .ve Verzeichnis erstellen (versteckt)
                string veDir = Path.Combine(project.Path, ".ve");
            if (!Directory.Exists(veDir))
            {
                var dirInfo = Directory.CreateDirectory(veDir);
                try
                {
                    dirInfo.Attributes |= FileAttributes.Hidden;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not set hidden attribute: {ex.Message}");
                }
            }

            // Icon speichern
            try
            {
                var iconPath = SaveIconFromResources("AppIcon", project.Path);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    project.ImagePath = iconPath;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not save icon: {ex.Message}");
            }

            // Projekt als JSON speichern (Legacy-Format für Kompatibilität)
            try
            {
                string projectFilePath = Path.Combine(veDir, "project.json");
                DataSerializer.SaveAsJson(project, projectFilePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not save project.json: {ex.Message}");
                // Don't throw - the main manifest will be saved separately
            }
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

        /// <summary>
        /// Erstellt die Standard-Ordnerstruktur f�r ein neues Projekt.
        /// </summary>
        private void CreateDefaultProjectFolders(string projectPath)
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
