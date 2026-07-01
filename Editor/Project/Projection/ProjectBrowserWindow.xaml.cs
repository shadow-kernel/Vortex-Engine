using Editor.Core.Data;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Editor.Project.Projection
{
    public partial class ProjectBrowserWindow : Window
    {
        public ProjectData SelectedProject { get; set; }

        public ProjectBrowserWindow()
        {
            InitializeComponent();

            // First impression: greet returning users with their recent projects, but land brand-new users straight
            // on the template chooser (the Open tab would otherwise be empty).
            bool hasRecent = false;
            try { hasRecent = Editor.Core.Services.ProjectService.Instance.GetAllProjects().Count > 0; } catch { }
            if (hasRecent) ShowOpenProject(); else ShowCreateProject();
        }

        private void RootGrid_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            StartDrag(e);
        }

        private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TitleBar_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            StartDrag(e);
        }

        private void StartDrag(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            try
            {
                if (WindowState == WindowState.Maximized)
                {
                    var mousePos = e.GetPosition(this);
                    var percentX = ActualWidth <= 0 ? 0.5 : mousePos.X / ActualWidth;
                    var percentY = ActualHeight <= 0 ? 0.5 : mousePos.Y / ActualHeight;

                    WindowState = WindowState.Normal;

                    var screenPos = PointToScreen(mousePos);
                    Left = screenPos.X - ActualWidth * percentX;
                    Top = screenPos.Y - ActualHeight * percentY;
                }

                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Ignore drag exceptions (e.g., mouse released mid-drag)
            }
            catch (Exception)
            {
                // Swallow unexpected drag-related exceptions to prevent crash
            }
        }

        private void OnToggleButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender == openProjectButton)
            {
                ShowOpenProject();
            }
            else if (sender == createProjectButton)
            {
                ShowCreateProject();
            }
        }

        private void ShowOpenProject()
        {
            createProjectButton.IsChecked = false;
            openProjectButton.IsChecked = true;
            newProjectView.Visibility = Visibility.Collapsed;
            openProjectView.Visibility = Visibility.Visible;
        }

        private void ShowCreateProject()
        {
            openProjectButton.IsChecked = false;
            createProjectButton.IsChecked = true;
            openProjectView.Visibility = Visibility.Collapsed;
            newProjectView.Visibility = Visibility.Visible;
        }

        /// <summary>Open the browser already on the "Create New Project" tab.</summary>
        public void StartOnCreateTab() => ShowCreateProject();
    }
}
