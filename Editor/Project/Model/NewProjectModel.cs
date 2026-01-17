using Editor.Core;
using Editor.Core.Data;
using Editor.Core.Exceptions;
using Editor.Core.Services;
using System;
using System.Windows;

namespace Editor.Project.Model
{
    public class NewProjectModel : ViewModelBase
    {
        private string _projectName = "New Project";
        private string _path;

        public event EventHandler<ProjectData> ProjectOpened;

        public NewProjectModel()
        {
            _path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                "VortexEngineProjects", 
                "New Project");
        }

        public string ProjectName
        {
            get => _projectName;
            set
            {
                if (SetProperty(ref _projectName, value, nameof(ProjectName)))
                {
                    Path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path), _projectName);
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
                var project = ProjectService.Instance.CreateProject(ProjectName, Path);
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
