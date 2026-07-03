using System;
using System.Windows;
using System.Windows.Controls;
using Editor.Core.Data;
using Editor.Project.Model;

namespace Editor.Project.Projection
{
    public partial class NewProjectView : UserControl
    {
        private NewProjectModel _dataContextModel;

        public NewProjectView()
        {
            InitializeComponent();
            if (DataContext == null)
            {
                DataContext = new NewProjectModel();
            }
            _dataContextModel = DataContext as NewProjectModel;

            if (_dataContextModel != null)
            {
                _dataContextModel.ProjectOpened += OnProjectOpened;
            }
        }

        private void OnProjectOpened(object sender, ProjectData project)
        {
            var window = Window.GetWindow(this) as ProjectBrowserWindow;
            if (window != null)
            {
                window.SelectedProject = project;
                window.DialogResult = true;
                window.Close();
            }
        }

        private void ExitButton_Pressed(object sender, RoutedEventArgs e)
        {
            // "Cancel" just closes the browser; MainWindow shuts the app down only when no project is open.
            var window = Window.GetWindow(this) as ProjectBrowserWindow;
            if (window != null)
            {
                window.DialogResult = false;
                window.Close();
            }
        }

        private void OpenButton_Pressed(object sender, RoutedEventArgs e)
        {
            _dataContextModel?.CreateProject();
        }

        /// <summary>Pick the PARENT folder the new project directory will be created in. Uses the shared STA
        /// FilePicker (a real folder browser) so it can't deadlock the renderer if a project is already open.</summary>
        private void BrowseButton_Pressed(object sender, RoutedEventArgs e)
        {
            if (_dataContextModel == null) return;
            try
            {
                var start = _dataContextModel.Path;
                var startDir = string.IsNullOrEmpty(start) ? null : System.IO.Path.GetDirectoryName(start);

                var parent = Editor.Core.Util.FilePicker.PickFolder("Choose the folder to create the project in", startDir);
                if (!string.IsNullOrEmpty(parent))
                    _dataContextModel.Path = System.IO.Path.Combine(parent, _dataContextModel.ProjectName ?? "My Game");
            }
            catch { }
        }
    }
}
