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
            Application.Current.Shutdown();
        }

        private void OpenButton_Pressed(object sender, RoutedEventArgs e)
        {
            _dataContextModel.CreateProject();
        }
    }
}
