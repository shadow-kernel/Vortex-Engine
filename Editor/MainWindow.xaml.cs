using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Project.Projection;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Editor
{
    public partial class MainWindow : Window
    {
        // Routed Commands für Save-Operationen
        public static readonly RoutedCommand SaveAllCommand = new RoutedCommand("SaveAll", typeof(MainWindow), 
            new InputGestureCollection { new KeyGesture(Key.S, ModifierKeys.Control | ModifierKeys.Shift) });
        public static readonly RoutedCommand SaveSceneCommand = new RoutedCommand("SaveScene", typeof(MainWindow),
            new InputGestureCollection { new KeyGesture(Key.S, ModifierKeys.Control) });

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

            // Save Shortcuts (Ctrl+S, Ctrl+Shift+S)
            CommandBindings.Add(new CommandBinding(SaveSceneCommand, OnSaveScene, OnCanSave));
            CommandBindings.Add(new CommandBinding(SaveAllCommand, OnSaveAll, OnCanSave));
        }

        private void OnCanSave(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = ProjectData.Current != null;
        }

        private void OnSaveScene(object sender, ExecutedRoutedEventArgs e)
        {
            var project = ProjectData.Current;
            if (project?.ActiveScene != null)
            {
                SceneService.Instance.SaveScene(project.ActiveScene);
            }
            e.Handled = true;
        }

        private void OnSaveAll(object sender, ExecutedRoutedEventArgs e)
        {
            var project = ProjectData.Current;
            if (project != null)
            {
                // Speichere alle Szenen
                SceneService.Instance.SaveAllScenes(project);
                
                // Speichere das Projekt selbst
                ProjectService.Instance.SaveProject(project);
            }
            e.Handled = true;
        }

        private void OnCanGlobalUndo(object sender, CanExecuteRoutedEventArgs e)
        {
            // Always allow execution - sound will play if at limit
            e.CanExecute = true;
        }

        private void OnGlobalUndo(object sender, ExecutedRoutedEventArgs e)
        {
            UndoRedoManager.Instance.Undo();
            e.Handled = true;
        }

        private void OnCanGlobalRedo(object sender, CanExecuteRoutedEventArgs e)
        {
            // Always allow execution - sound will play if at limit
            e.CanExecute = true;
        }

        private void OnGlobalRedo(object sender, ExecutedRoutedEventArgs e)
        {
            UndoRedoManager.Instance.Redo();
            e.Handled = true;
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            Closing -= OnWindowClosing;
            
            // Speichere alle offenen Szenen vor dem Schließen
            var project = ProjectData.Current;
            if (project != null)
            {
                SceneService.Instance.SaveAllScenes(project);
                ProjectService.Instance.SaveProject(project);
            }
            
            UndoRedoManager.Instance.Clear();
            ProjectData.Current?.Unload();
        }

        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnMainWindowLoaded;
            
            // Prüfe ob ein gültiges letztes Projekt existiert
            if (EditorStateService.Instance.IsLastProjectValid())
            {
                TryLoadLastProject();
            }
            else
            {
                // Kein gültiges letztes Projekt - zeige Browser
                EditorStateService.Instance.ClearLastProject();
                OpenProjectBrowser();
            }
        }

        /// <summary>
        /// Versucht das zuletzt geöffnete Projekt zu laden.
        /// Bei Fehlern wird der ProjectBrowser geöffnet.
        /// </summary>
        private void TryLoadLastProject()
        {
            try
            {
                var lastProjectId = EditorStateService.Instance.LastProjectId;
                var lastProjectPath = EditorStateService.Instance.LastProjectPath;

                if (lastProjectId.HasValue)
                {
                    var projects = ProjectService.Instance.GetAllProjects();
                    if (projects.TryGetValue(lastProjectId.Value, out var projectRef))
                    {
                        var project = ProjectService.Instance.LoadProject(projectRef);
                        LoadProject(project);
                        return;
                    }
                }

                // Projekt nicht in Registry gefunden - Browser öffnen
                EditorStateService.Instance.ClearLastProject();
                OpenProjectBrowser();
            }
            catch
            {
                // Bei Fehlern Browser öffnen
                EditorStateService.Instance.ClearLastProject();
                OpenProjectBrowser();
            }
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
            
            // Speichere als letztes geöffnetes Projekt
            EditorStateService.Instance.SetLastProject(project.Id, project.Path);
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
            
            // Lösche letztes Projekt - beim nächsten Start wird Browser geöffnet
            EditorStateService.Instance.ClearLastProject();
        }
    }
}
