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
            ShowProjectBrowser();
        }

        private void ShowProjectBrowser()
        {
            var browserWindow = new ProjectBrowserWindow
            {
                Owner = this
            };

            var result = browserWindow.ShowDialog();

            if (result == true && browserWindow.SelectedProject != null)
            {
                ProjectData.Current?.Unload();
                var project = browserWindow.SelectedProject;
                DataContext = project;
                Title = $"Vortex Engine - {project.Name}";
            }
            else
            {
                Application.Current.Shutdown();
            }
        }
    }
}
