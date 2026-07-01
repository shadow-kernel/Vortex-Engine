using Editor.Core;
using Editor.Core.Data;
using Editor.Core.Exceptions;
using Editor.Core.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Editor.Project.Model
{
    public class NewProjectModel : ViewModelBase
    {
        private string _projectName = "My Game";
        private string _path;
        private ProjectTemplate _selectedTemplate;
        private ImageSource _previewImage;

        public event EventHandler<ProjectData> ProjectOpened;

        public ObservableCollection<ProjectTemplate> Templates { get; }

        public NewProjectModel()
        {
            _path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "VortexEngineProjects",
                _projectName);

            Templates = new ObservableCollection<ProjectTemplate>(ProjectTemplateService.Discover());
            // Default to a real 3D template if one shipped (best first impression); otherwise the Empty scaffold.
            SelectedTemplate = Templates.FirstOrDefault(t => !t.IsEmpty) ?? Templates.FirstOrDefault();
        }

        public ProjectTemplate SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                if (SetProperty(ref _selectedTemplate, value, nameof(SelectedTemplate)))
                {
                    LoadPreview();
                    OnPropertyChanged(nameof(SelectedName));
                    OnPropertyChanged(nameof(SelectedTagline));
                    OnPropertyChanged(nameof(SelectedDescription));
                    OnPropertyChanged(nameof(HasPreview));
                }
            }
        }

        public string SelectedName => _selectedTemplate?.Name ?? "";
        public string SelectedTagline => _selectedTemplate?.Tagline ?? "";
        public string SelectedDescription => _selectedTemplate?.Description ?? "";

        public ImageSource PreviewImage
        {
            get => _previewImage;
            private set { SetProperty(ref _previewImage, value, nameof(PreviewImage)); OnPropertyChanged(nameof(HasPreview)); OnPropertyChanged(nameof(HasNoPreview)); }
        }

        public bool HasPreview => _previewImage != null;
        public bool HasNoPreview => _previewImage == null;

        private void LoadPreview()
        {
            var p = _selectedTemplate?.PreviewImagePath;
            if (string.IsNullOrEmpty(p) || !System.IO.File.Exists(p)) { PreviewImage = null; return; }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(p, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                PreviewImage = bmp;
            }
            catch { PreviewImage = null; }
        }

        public string ProjectName
        {
            get => _projectName;
            set
            {
                if (SetProperty(ref _projectName, value, nameof(ProjectName)))
                {
                    var dir = System.IO.Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(_projectName))
                        Path = System.IO.Path.Combine(dir, _projectName);
                }
            }
        }

        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value, nameof(Path));
        }

        public bool CreateProject()
        {
            try
            {
                var project = (_selectedTemplate != null && !_selectedTemplate.IsEmpty && !string.IsNullOrEmpty(_selectedTemplate.ProjectDir))
                    ? ProjectService.Instance.CreateProjectFromTemplate(ProjectName, Path, _selectedTemplate.ProjectDir)
                    : ProjectService.Instance.CreateProject(ProjectName, Path);
                ProjectOpened?.Invoke(this, project);
                return true;
            }
            catch (DuplicateProjectPathException ex)
            {
                MessageBox.Show(
                    $"Fehler: {ex.Message}\n\nBitte wählen Sie einen anderen Pfad.",
                    "Projekt existiert bereits",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (ProjectValidationException ex)
            {
                MessageBox.Show(
                    $"Validierungsfehler: {ex.Message}",
                    "Ungültige Eingabe",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (ProjectIOException ex)
            {
                MessageBox.Show(
                    $"Fehler beim Zugriff auf das Dateisystem: {ex.Message}",
                    "Dateisystemfehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (ProjectException ex)
            {
                MessageBox.Show(
                    $"Ein Fehler ist aufgetreten: {ex.Message}",
                    "Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ein unerwarteter Fehler ist aufgetreten: {ex.Message}",
                    "Unerwarteter Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return false;
        }
    }
}
