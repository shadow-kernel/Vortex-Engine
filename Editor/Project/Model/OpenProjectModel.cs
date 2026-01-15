using Editor.Project.Control;
using Editor.Project.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Editor.Project.Model
{
    public class OpenProjectModel : ViewModelBase
    {
        private ObservableCollection<ProjectEntity> _filteredProjects;
        private List<ProjectEntity> _allProjects;
        private string _searchText;

        public OpenProjectModel()
        {
            LoadProjects();
        }

        public ObservableCollection<ProjectEntity> Projects
        {
            get => _filteredProjects;
            set
            {
                if (_filteredProjects != value)
                {
                    _filteredProjects = value;
                    OnPropertyChanged(nameof(Projects));
                }
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText != value)
                {
                    _searchText = value;
                    OnPropertyChanged(nameof(SearchText));
                    ApplyFilter();
                }
            }
        }

        public void LoadProjects()
        {
            var projectDict = ProjectFileManager.Instance.GetAllProjects();
            _allProjects = new List<ProjectEntity>();

            foreach (var projectRef in projectDict.Values)
            {
                var projectEntity = ConvertToProjectEntity(projectRef);
                UpdateProjectMetadata(projectEntity);
                _allProjects.Add(projectEntity);
            }

            _allProjects = _allProjects.OrderByDescending(p => p.LastModified).ToList();
            _filteredProjects = new ObservableCollection<ProjectEntity>(_allProjects);
            OnPropertyChanged(nameof(Projects));
        }

        private ProjectEntity ConvertToProjectEntity(ProjectRef projectRef)
        {
            return new ProjectEntity(projectRef.Id, projectRef.Path, projectRef.Name)
            {
                ImagePath = projectRef.ImagePath
            };
        }

        private void UpdateProjectMetadata(ProjectEntity project)
        {
            try
            {
                if (Directory.Exists(project.Path))
                {
                    var dirInfo = new DirectoryInfo(project.Path);
                    project.LastModified = dirInfo.LastWriteTime;

                    string thumbnailPath = Path.Combine(project.Path, ".ve", "icon.png");
                    if (File.Exists(thumbnailPath))
                    {
                        project.Thumbnail = LoadThumbnail(thumbnailPath);
                    }
                    else
                    {
                        project.Thumbnail = LoadDefaultThumbnail();
                    }
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
                _filteredProjects = new ObservableCollection<ProjectEntity>(_allProjects);
            }
            else
            {
                var filtered = _allProjects.Where(p =>
                    ContainsIgnoreCase(p.Name, _searchText) ||
                    ContainsIgnoreCase(p.Path, _searchText)
                ).ToList();

                _filteredProjects = new ObservableCollection<ProjectEntity>(filtered);
            }

            OnPropertyChanged(nameof(Projects));
        }

        private bool ContainsIgnoreCase(string source, string value)
        {
            if (string.IsNullOrEmpty(source))
                return false;
            return source.IndexOf(value ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
