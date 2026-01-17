using Editor.Core.Data;
using Editor.Project.Projection;
using System.ComponentModel;
using System.Windows;

namespace Editor
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnMainWindowLoaded;
            Closing += OnWindowClosing;
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            Closing -= OnWindowClosing;
            ProjectData.Current?.Unload();
        }

        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnMainWindowLoaded;
            OpenProjectBrowser();
        }

        /// <summary>
        /// Öffnet den ProjectBrowser und lädt das ausgewählte Projekt.
        /// Gibt true zurück wenn ein Projekt geladen wurde.
        /// </summary>
        public bool OpenProjectBrowser()
        {
            var browserWindow = new ProjectBrowserWindow
            {
                Owner = this
            };

            var result = browserWindow.ShowDialog();

            if (result == true && browserWindow.SelectedProject != null)
            {
                LoadProject(browserWindow.SelectedProject);
                return true;
            }
            else if (ProjectData.Current == null)
            {
                Application.Current.Shutdown();
            }

            return false;
        }

        /// <summary>
        /// Lädt ein Projekt und zeigt den Editor-Inhalt an.
        /// </summary>
        public void LoadProject(ProjectData project)
        {
            ProjectData.Current?.Unload();
            DataContext = project;
            Title = $"Vortex Engine - {project.Name}";
            WorldEditor.SetEditorVisible(true);
        }

        /// <summary>
        /// Schließt das aktuelle Projekt und versteckt den Editor-Inhalt.
        /// </summary>
        public void CloseCurrentProject()
        {
            ProjectData.Current?.Unload();
            DataContext = null;
            Title = "Vortex Engine";
            WorldEditor.SetEditorVisible(false);
        }
    }
}
