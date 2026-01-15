using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Project.Data;
using Editor.Project.Model;

namespace Editor.Project.Projection
{
    public partial class OpenProjectView : UserControl
    {
        private OpenProjectModel _dataContextModel;

        public OpenProjectView()
        {
            InitializeComponent();
            if (DataContext == null)
            {
                DataContext = new OpenProjectModel();
            }
            _dataContextModel = DataContext as OpenProjectModel;
            
            if (_dataContextModel != null)
            {
                _dataContextModel.ProjectOpened += OnProjectOpened;
            }
        }

        private void OnProjectOpened(object sender, ProjectEntity project)
        {
            var window = Window.GetWindow(this) as ProjectBrowserWindow;
            if (window != null)
            {
                window.SelectedProject = project;
                window.DialogResult = true;
                window.Close();
            }
        }

        private void ExitButton_Pressed(object sender, System.Windows.RoutedEventArgs e)
        {
            var window = Window.GetWindow(this) as ProjectBrowserWindow;
            if (window != null)
            {
                window.DialogResult = false;
                window.Close();
            }
        }

        private void OpenButton_Pressed(object sender, System.Windows.RoutedEventArgs e)
        {
            var item = ProjectsListView.SelectedItem as ProjectFileRef;
            if (item != null)
            {
                _dataContextModel.OpenProject(item);
            }
        }

        private void DoubleClickListItem(object sender, System.Windows.RoutedEventArgs e)
        {
            OpenButton_Pressed(sender, e);
        }

        private void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_dataContextModel != null)
            {
                var textBox = sender as TextBox;
                _dataContextModel.SearchText = textBox?.Text ?? string.Empty;
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var project = button?.Tag as ProjectEntity;

            if (project == null)
                return;

            var result = MessageBox.Show(
                $"Möchten Sie das Projekt '{project.Name}' löschen?\n\n" +
                $"Ja: Projekt aus der Liste entfernen UND alle Projektdateien löschen\n" +
                $"Nein: Nur aus der Liste entfernen (Dateien bleiben erhalten)\n" +
                $"Abbrechen: Nichts tun",
                "Projekt löschen",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
                return;

            bool deleteFiles = (result == MessageBoxResult.Yes);

            try
            {
                _dataContextModel.DeleteProject(project, deleteFiles);

                string message = deleteFiles
                    ? "Projekt wurde aus der Liste entfernt und alle Dateien wurden gelöscht."
                    : "Projekt wurde aus der Liste entfernt. Die Dateien bleiben erhalten.";

                MessageBox.Show(message, "Erfolg", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Fehler beim Löschen des Projekts: {ex.Message}",
                    "Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
