using Editor.Project.Control;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Editor.Project.Data
{
    public class ProjectEntity : ProjectFileRef
    {
        [JsonPropertyName("last_modified")]
        public DateTime LastModified { get; set; }

        [JsonIgnore]
        public ImageSource Thumbnail { get; set; }

        [JsonIgnore]
        public string LastModifiedDisplay => LastModified.ToString("dd.MM.yyyy HH:mm");

        [JsonPropertyName("scenes")]
        private ObservableCollection<Scene> _scenes;

        public ObservableCollection<Scene> Scenes
        {
            get => _scenes ?? (_scenes = new ObservableCollection<Scene>());
            set => _scenes = value ?? new ObservableCollection<Scene>();
        }

        public ProjectEntity()
        {
        }

        public ProjectEntity(string path, string name) : base(path, name)
        {
            LastModified = DateTime.Now;
            _scenes = new ObservableCollection<Scene>();
            _scenes.Add(new Scene(this, "Default Scene"));
        }

        public ProjectEntity(Guid id, string path, string name) : base(id, path, name)
        {
            LastModified = DateTime.Now;
            _scenes = new ObservableCollection<Scene>();
        }

        public static void Save(ProjectEntity project)
        {
            ProjectFileManager.Instance.SaveProjectFile(project);
        }

        public void Unload()
        {

        }

        public static ProjectEntity Current => Application.Current.MainWindow.DataContext as ProjectEntity;

    }
}
