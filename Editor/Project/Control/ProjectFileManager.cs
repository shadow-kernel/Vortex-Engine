using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Editor.Project.Data;
using Editor.Project.Exceptions;
using Editor.Project.Validation;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Editor.Project.Control
{
    public class ProjectFileManager
    {
        private static Dictionary<Guid, ProjectFileRef> loadedProjects;

        private static readonly ProjectFileManager _instance;
        private static readonly string _projectRegistryFilePath;
        private static readonly string _defaultProjectsPath;

        public static ProjectFileManager Instance => _instance;

        static ProjectFileManager()
        {
            _instance = new ProjectFileManager();
            string _appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/VortexEngine";
            _defaultProjectsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VortexEngineProjects");
            _projectRegistryFilePath = _appDataPath + "/projects.ve";

            if (!Directory.Exists(_appDataPath))
            {
                Directory.CreateDirectory(_appDataPath);
            }

            if (!Directory.Exists(_defaultProjectsPath))
            {
                Directory.CreateDirectory(_defaultProjectsPath);
            }

            loadedProjects = loadProjectsFromAppData();
        }

        private ProjectFileManager()
        {
        }

        public ProjectEntity loadProject(ProjectFileRef projectRef)
        {
            try
            {
                string content = File.ReadAllText(projectRef.Path + "/.ve/project.json");
                System.Diagnostics.Debug.WriteLine($"Geladene JSON:\\n{content}");
                
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                
                var project = JsonSerializer.Deserialize<ProjectEntity>(content, options);
                
                System.Diagnostics.Debug.WriteLine($"Scenes Count nach Deserialisierung: {project?.Scenes?.Count ?? -1}");
                
                // Stelle sicher, dass Scenes nicht null ist
                if (project != null)
                {
                    if (project.Scenes == null)
                    {
                        System.Diagnostics.Debug.WriteLine("WARNING: Scenes ist null nach Deserialisierung!");
                        project.Scenes = new ObservableCollection<Scene>();
                    }
                    
                    // Setze die Project-Referenz für alle Scenes
                    foreach (var scene in project.Scenes)
                    {
                        System.Diagnostics.Debug.WriteLine($"  Setze Project-Referenz für Scene: {scene.Name}");
                        scene.Project = project;
                    }
                }
                
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

        public void SaveProjectFile(ProjectFileRef project)
        {
            try
            {
                // Validierung durchführen
                ProjectValidator.ValidateProject(project, loadedProjects);

                // Projekt speichern
                loadedProjects[project.Id] = project;

                createNessesaryProjectFiles((ProjectEntity)project);

                string content = this.SerializeObject(loadedProjects);
                File.WriteAllText(_projectRegistryFilePath, content);
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

        public bool ProjectPathExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalizedPath = NormalizePathForComparison(path);

            return loadedProjects.Values.Any(p => 
                string.Equals(
                    NormalizePathForComparison(p.Path), 
                    normalizedPath, 
                    StringComparison.OrdinalIgnoreCase
                )
            );
        }

        public Dictionary<Guid, ProjectFileRef> GetAllProjects()
        {
            return loadedProjects;
        }

        private string NormalizePathForComparison(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }

        private static Dictionary<Guid, ProjectFileRef> loadProjectsFromAppData()
        {
            try
            {
                if (!File.Exists(_projectRegistryFilePath))
                {
                    return new Dictionary<Guid, ProjectFileRef>();
                }

                string content = File.ReadAllText(_projectRegistryFilePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var projects = JsonSerializer.Deserialize<Dictionary<Guid, ProjectFileRef>>(content, options);
                return projects ?? new Dictionary<Guid, ProjectFileRef>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Laden der Projekte: {ex.Message}");
                return new Dictionary<Guid, ProjectFileRef>();
            }
        }



        private void createNessesaryProjectFiles(ProjectEntity project)
        {
            if (!Directory.Exists(project.Path))
            {
                Directory.CreateDirectory(project.Path);
            }

            if(!Directory.Exists(project.Path+"/.ve"))
            {
                var veDir = Directory.CreateDirectory(project.Path + "/.ve");
                veDir.Attributes = FileAttributes.Hidden;
            }

            var gameIcon = SaveIconFromResources("AppIcon", project.Path);
            project.ImagePath = gameIcon;

            // Debug: Überprüfe Scenes vor Serialisierung
            System.Diagnostics.Debug.WriteLine($"Scenes Count vor Serialisierung: {project.Scenes?.Count ?? -1}");
            if (project.Scenes != null)
            {
                foreach (var scene in project.Scenes)
                {
                    System.Diagnostics.Debug.WriteLine($"  Scene: {scene.Name}");
                }
            }

            string content = this.SerializeObject(project);
            System.Diagnostics.Debug.WriteLine($"Serialisiertes JSON:\n{content}");
            File.WriteAllText(project.Path + "/.ve/project.json", content);
        }

        private string SerializeObject<T>(T obj)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            
            // Verwende den konkreten Laufzeit-Typ für die Serialisierung
            var actualType = obj.GetType();
            System.Diagnostics.Debug.WriteLine($"Serialisiere Typ: {actualType.Name}");
            
            return JsonSerializer.Serialize(obj, actualType, options);
        }

        public string SaveIconFromResources(string resourceKey, string projectPath, string fileName = "icon.png")
        {
            try
            {
                var resource = Application.Current.FindResource(resourceKey) as BitmapImage;
                if (resource == null)
                {
                    return null;
                }

                string iconPath = Path.Combine(projectPath, ".ve", fileName);

                BitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(resource));

                using (var fileStream = new FileStream(iconPath, FileMode.Create))
                {
                    encoder.Save(fileStream);
                    return iconPath;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void RemoveProject(Guid projectId, bool deleteFiles)
        {
            if (!loadedProjects.ContainsKey(projectId))
                return;

            var project = loadedProjects[projectId];
            string projectPath = project.Path;

            loadedProjects.Remove(projectId);

            try
            {
                string content = this.SerializeObject(loadedProjects);
                File.WriteAllText(_projectRegistryFilePath, content);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Speichern der Projektliste: {ex.Message}");
            }

            if (deleteFiles && !string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath))
            {
                try
                {
                    Directory.Delete(projectPath, true);
                }
                catch (Exception ex)
                {
                    throw new ProjectIOException(
                        projectPath,
                        $"Fehler beim Löschen des Projektordners: {ex.Message}",
                        ex
                    );
                }
            }
        }

    }
}
