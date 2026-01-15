using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Editor.Project.Data;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Editor.Project.Control
{
    public class ProjectFileManager
    {
        private static Dictionary<Guid, ProjectRef> loadedProjects;

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

        public void SaveProjectFile(ProjectRef project)
        {
            loadedProjects[project.Id] = project;

            createNessesaryProjectFiles((ProjectEntity) project);

            string content = this.SerializeObject(loadedProjects);
            File.WriteAllText(_projectRegistryFilePath, content);
        }

        private static Dictionary<Guid, ProjectRef> loadProjectsFromAppData()
        {
            if (!File.Exists(_projectRegistryFilePath))
            {
                return new Dictionary<Guid, ProjectRef>();
            }

            string content = File.ReadAllText(_projectRegistryFilePath);
            return JsonSerializer.Deserialize<Dictionary<Guid, ProjectRef>>(content);
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

            string content = this.SerializeObject(project);
            File.WriteAllText(project.Path + "/.ve/project.json", content);
        }

        private string SerializeObject(Object obj)
        {
            return JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                WriteIndented = true
            });
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

    }
}
