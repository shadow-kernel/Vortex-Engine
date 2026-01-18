using Editor.Core.Data;
using Editor.Core.UndoRedo;
using Editor.Project.Projection;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Editor
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            SetupGlobalKeyboardShortcuts();
            Loaded += OnMainWindowLoaded;
            Closing += OnWindowClosing;
        }

        private void SetupGlobalKeyboardShortcuts()
        {
            // Globale Undo/Redo Shortcuts (Ctrl+Z, Ctrl+Y)
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, OnGlobalUndo, OnCanGlobalUndo));
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, OnGlobalRedo, OnCanGlobalRedo));
        }

        private void OnCanGlobalUndo(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = UndoRedoManager.Instance.CanUndo;
        }

        private void OnGlobalUndo(object sender, ExecutedRoutedEventArgs e)
        {
            UndoRedoManager.Instance.Undo();
            e.Handled = true;
        }

        private void OnCanGlobalRedo(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = UndoRedoManager.Instance.CanRedo;
        }

        private void OnGlobalRedo(object sender, ExecutedRoutedEventArgs e)
        {
            UndoRedoManager.Instance.Redo();
            e.Handled = true;
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            Closing -= OnWindowClosing;
            UndoRedoManager.Instance.Clear();
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
