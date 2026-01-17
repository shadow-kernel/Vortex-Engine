using Editor.Core;
using Editor.Core.Data;
using Editor.Core.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Editor.Project.Model
{
    public class OpenProjectModel : ViewModelBase
    {
        private ObservableCollection<ProjectData> _filteredProjects;
        private List<ProjectData> _allProjects;
        private string _searchText;

        public OpenProjectModel()
        {
            LoadProjects();
        }

        public ObservableCollection<ProjectData> Projects
        {
            get => _filteredProjects;
            set => SetProperty(ref _filteredProjects, value, nameof(Projects));
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value, nameof(SearchText)))
                {
                    ApplyFilter();
                }
            }
        }

        public void LoadProjects()
        {
            var projectDict = ProjectService.Instance.GetAllProjects();
            _allProjects = new List<ProjectData>();

            foreach (var projectRef in projectDict.Values)
            {
                var project = ConvertToProject(projectRef);
                UpdateProjectMetadata(project);
                _allProjects.Add(project);
            }

            _allProjects = _allProjects.OrderByDescending(p => p.LastModified).ToList();
                _filteredProjects = new ObservableCollection<ProjectData>(_allProjects);
                OnPropertyChanged(nameof(Projects));
            }

            private ProjectData ConvertToProject(ProjectRef projectRef)
            {
                return new ProjectData(projectRef.Id, projectRef.Path, projectRef.Name)
                {
                    ImagePath = projectRef.ImagePath
                };
            }

            private void UpdateProjectMetadata(ProjectData project)
            {
            try
            {
                if (Directory.Exists(project.Path))
                {
                    var dirInfo = new DirectoryInfo(project.Path);
                    project.LastModified = dirInfo.LastWriteTime;

                    string thumbnailPath = Path.Combine(project.Path, ".ve", "icon.png");
                    project.Thumbnail = File.Exists(thumbnailPath) 
                        ? LoadThumbnail(thumbnailPath) 
                        : LoadDefaultThumbnail();
                }
                else
                {
                    project.LastModified = DateTime.MinValue;
                    project.Thumbnail = LoadDefaultThumbnail();
                }
            }
            catch
            {
                project.LastModified = DateTime.MinValue;
                project.Thumbnail = LoadDefaultThumbnail();
            }
        }

        private ImageSource LoadThumbnail(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return LoadDefaultThumbnail();
            }
        }

        private ImageSource LoadDefaultThumbnail()
        {
            try
            {
                return new BitmapImage(new Uri("pack://application:,,,/Assets/Images/Logo.png", UriKind.Absolute));
            }
            catch
            {
                return null;
            }
        }

        private void ApplyFilter()
        {
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                _filteredProjects = new ObservableCollection<ProjectData>(_allProjects);
            }
            else
            {
                var filtered = _allProjects.Where(p =>
                    ContainsIgnoreCase(p.Name, _searchText) ||
                    ContainsIgnoreCase(p.Path, _searchText)
                ).ToList();

                _filteredProjects = new ObservableCollection<ProjectData>(filtered);
            }

            OnPropertyChanged(nameof(Projects));
        }

        private bool ContainsIgnoreCase(string source, string value)
        {
            if (string.IsNullOrEmpty(source))
                return false;
            return source.IndexOf(value ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void DeleteProject(ProjectData project, bool deleteFiles)
        {
            if (project == null)
                return;

            // TODO: Implement RemoveProject in ProjectService
            _allProjects.Remove(project);
            ApplyFilter();
        }

        public event EventHandler<ProjectData> ProjectOpened;

        public void OpenProject(ProjectRef item)
        {
            try
            {
                var project = ProjectService.Instance.LoadProject(item);
                if (project != null)
                {
                    ProjectOpened?.Invoke(this, project);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Fehler beim Laden des Projekts: {ex.Message}",
                    "Ladefehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
