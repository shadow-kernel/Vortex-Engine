using Editor.Project.Data;
using Editor.Project.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Editor.Project.Control
{
    public class ProjectManager
    {
        private static readonly ProjectManager _instance;

        public static ProjectManager Instance => _instance;

        static ProjectManager()
        {
            _instance = new ProjectManager();
        }

        private ProjectManager()
        {

        }

        public bool CreateNewProject(string projectName, string projectPath)
        {
            try
            {
                var project = new ProjectEntity(projectPath, projectName);
                ProjectFileManager.Instance.SaveProjectFile(project);
                return true;
            }
            catch (DuplicateProjectPathException ex)
            {
                MessageBox.Show(
                    $"Fehler: {ex.Message}\n\nBitte wählen Sie einen anderen Pfad.",
                    "Projekt existiert bereits",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
            catch (ProjectValidationException ex)
            {
                MessageBox.Show(
                    $"Validierungsfehler: {ex.Message}",
                    "Ungültige Eingabe",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            catch (ProjectIOException ex)
            {
                MessageBox.Show(
                    $"Fehler beim Zugriff auf das Dateisystem: {ex.Message}",
                    "Dateisystemfehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            catch (ProjectException ex)
            {
                MessageBox.Show(
                    $"Ein Fehler ist aufgetreten: {ex.Message}",
                    "Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ein unerwarteter Fehler ist aufgetreten: {ex.Message}",
                    "Unerwarteter Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }

            return false;
        }

    }
}
