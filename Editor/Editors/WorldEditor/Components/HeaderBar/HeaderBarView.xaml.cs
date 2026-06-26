using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using Editor.Editors.WorldEditor.Services;

namespace Editor.Editors.WorldEditor.Components.HeaderBar
{
    public partial class HeaderBarView : UserControl
    {
        public HeaderBarView()
        {
            InitializeComponent();
            SetupKeyboardShortcuts();
            UpdateUndoRedoMenuItems();
            UndoRedoManager.Instance.StateChanged += OnUndoRedoStateChanged;
        }

        private void SetupKeyboardShortcuts()
        {
            // Ctrl+Z = Undo
            var undoBinding = new CommandBinding(ApplicationCommands.Undo, OnUndoExecuted, OnCanUndo);
            CommandBindings.Add(undoBinding);

            // Ctrl+Y = Redo
            var redoBinding = new CommandBinding(ApplicationCommands.Redo, OnRedoExecuted, OnCanRedo);
            CommandBindings.Add(redoBinding);
        }

        private void OnCanUndo(object sender, CanExecuteRoutedEventArgs e)
        {
            // Always allow - sound will play if at limit
            e.CanExecute = true;
        }

        private void OnUndoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            UndoRedoManager.Instance.Undo();
        }

        private void OnCanRedo(object sender, CanExecuteRoutedEventArgs e)
        {
            // Always allow - sound will play if at limit
            e.CanExecute = true;
        }

        private void OnRedoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            UndoRedoManager.Instance.Redo();
        }

        private void OnUndoRedoStateChanged(object sender, EventArgs e)
        {
            Dispatcher.Invoke(UpdateUndoRedoMenuItems);
        }

        private void UpdateUndoRedoMenuItems()
        {
            if (UndoMenuItem != null)
            {
                UndoMenuItem.IsEnabled = UndoRedoManager.Instance.CanUndo;
                UndoMenuItem.Header = UndoRedoManager.Instance.CanUndo 
                    ? $"_Undo {UndoRedoManager.Instance.UndoName}" 
                    : "_Undo";
            }

            if (RedoMenuItem != null)
            {
                RedoMenuItem.IsEnabled = UndoRedoManager.Instance.CanRedo;
                RedoMenuItem.Header = UndoRedoManager.Instance.CanRedo 
                    ? $"_Redo {UndoRedoManager.Instance.RedoName}" 
                    : "_Redo";
            }
        }

        private MainWindow GetMainWindow()
        {
            return Window.GetWindow(this) as MainWindow;
        }

        private WorldEditorView GetWorldEditorView()
        {
            return this.Parent?.GetType().GetProperty("Parent")?.GetValue(this.Parent) as WorldEditorView
                   ?? FindParent<WorldEditorView>(this);
        }

        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
            while (parent != null && !(parent is T))
            {
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
            return parent as T;
        }

        #region File Menu

        private void OpenOtherProject_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = GetMainWindow();
            mainWindow?.OpenProjectBrowser();
        }

        private void CloseProject_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "M�chten Sie das aktuelle Projekt schlie�en?\n\nUngespeicherte �nderungen gehen verloren.",
                "Projekt schlie�en",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var mainWindow = GetMainWindow();
                if (mainWindow == null)
                    return;

                mainWindow.CloseCurrentProject();
                mainWindow.OpenProjectBrowser();
            }
        }

        private void BuildSettings_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement build settings
            MessageBox.Show("Build-Einstellungen - Noch nicht implementiert", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Build_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement build
            MessageBox.Show("Projekt bauen - Noch nicht implementiert", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BuildAndRun_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement build and run
            MessageBox.Show("Projekt bauen und ausf�hren - Noch nicht implementiert", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "M�chten Sie den Editor wirklich beenden?\n\nUngespeicherte �nderungen gehen verloren.",
                "Editor beenden",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                Application.Current.Shutdown();
            }
        }

        #endregion

        #region Edit Menu

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            UndoRedoManager.Instance.Undo();
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            UndoRedoManager.Instance.Redo();
        }

        private void Cut_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement cut
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement copy
        }

        private void Paste_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement paste
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity != null && entity.Scene != null)
            {
                entity.Scene.RemoveEntity(entity);
            }
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity != null && entity.Scene != null)
            {
                var duplicate = CloneEntity(entity);
                if (duplicate != null)
                {
                    entity.Scene.AddEntity(duplicate);
                    SelectionService.Instance.Select(duplicate);
                }
            }
        }

        private GameEntity CloneEntity(GameEntity source)
        {
            if (source == null) return null;
            
            var clone = new GameEntity(source.Scene, source.Name + " (Copy)");
            
            // Copy transform
            if (source.Transform != null && clone.Transform != null)
            {
                clone.Transform.LocalPosition = new Vector3(
                    source.Transform.LocalPosition.X + 1f,
                    source.Transform.LocalPosition.Y,
                    source.Transform.LocalPosition.Z);
                clone.Transform.LocalRotation = source.Transform.LocalRotation;
                clone.Transform.LocalScale = source.Transform.LocalScale;
            }
            
            // Copy other components (except Transform which is already added)
            foreach (var component in source.Components)
            {
                if (component is Transform) continue;
                
                // Clone MeshRenderer
                if (component is ECS.Components.Rendering.MeshRenderer srcMr)
                {
                    var mr = new ECS.Components.Rendering.MeshRenderer(clone);
                    mr.MeshPath = srcMr.MeshPath;
                    mr.MaterialPath = srcMr.MaterialPath;
                    mr.ColorR = srcMr.ColorR;
                    mr.ColorG = srcMr.ColorG;
                    mr.ColorB = srcMr.ColorB;
                    mr.ColorA = srcMr.ColorA;
                    clone.AddComponent(mr);
                }
            }
            
            clone.Tag = source.Tag;
            clone.IsStatic = source.IsStatic;
            clone.Layer = source.Layer;
            
            return clone;
        }

        #endregion

        #region Window Menu

        private void ToggleWindow_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem?.Tag is string windowName)
            {
                WindowService.Instance.SetWindowVisibility(windowName, menuItem.IsChecked);
            }
        }

        private void ResetLayout_Click(object sender, RoutedEventArgs e)
        {
            var worldEditor = FindParent<WorldEditorView>(this);
            if (worldEditor != null)
            {
                worldEditor.ResetLayout();
            }

            // Update menu checkboxes
            MenuSceneWindow.IsChecked = true;
            MenuProjectWindow.IsChecked = true;
            MenuExplorerWindow.IsChecked = true;
            MenuConsoleWindow.IsChecked = true;
            MenuHierarchyWindow.IsChecked = true;
            MenuInspectorWindow.IsChecked = true;
        }

        #endregion

        #region Help Menu

        private void Documentation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("https://github.com/shadow-kernel/Vortex-Engine/wiki");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Konnte Dokumentation nicht �ffnen: {ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Vortex Engine\n\nVersion 1.0.0\n\n� 2024 Shadow Kernel",
                "�ber Vortex Engine",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        #endregion

        #region GameObject Menu Helpers

        private Scene GetActiveScene()
        {
            return ProjectData.Current?.ActiveScene;
        }

        private GameEntity CreateEntityInActiveScene(string name)
        {
            var scene = GetActiveScene();
            if (scene == null)
            {
                MessageBox.Show("Keine aktive Szene vorhanden.", "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return scene.CreateEntity(name);
        }

        #endregion

        #region GameObject Menu

        private void CreateEmpty_Click(object sender, RoutedEventArgs e)
        {
            CreateEntityInActiveScene("New Entity");
        }

        private void CreateCube_Click(object sender, RoutedEventArgs e)
        {
            GetActiveScene()?.CreatePrimitive(PrimitiveType.Cube);
        }

        private void CreateSphere_Click(object sender, RoutedEventArgs e)
        {
            GetActiveScene()?.CreatePrimitive(PrimitiveType.Sphere);
        }

        private void CreateCapsule_Click(object sender, RoutedEventArgs e)
        {
            GetActiveScene()?.CreatePrimitive(PrimitiveType.Capsule);
        }

        private void CreateCylinder_Click(object sender, RoutedEventArgs e)
        {
            GetActiveScene()?.CreatePrimitive(PrimitiveType.Cylinder);
        }

        private void CreatePlane_Click(object sender, RoutedEventArgs e)
        {
            GetActiveScene()?.CreatePrimitive(PrimitiveType.Plane);
        }

        private void CreateDirectionalLight_Click(object sender, RoutedEventArgs e)
        {
            GetActiveScene()?.CreateLight(LightType.Directional);
        }

        private void CreatePointLight_Click(object sender, RoutedEventArgs e)
        {
            GetActiveScene()?.CreateLight(LightType.Point);
        }

        private void CreateSpotLight_Click(object sender, RoutedEventArgs e)
        {
            GetActiveScene()?.CreateLight(LightType.Spot);
        }

        private void CreateCamera_Click(object sender, RoutedEventArgs e)
        {
            GetActiveScene()?.CreateCamera();
        }

        #endregion

        #region Component Menu

        private GameEntity GetSelectedEntity()
        {
            return GetActiveScene()?.SelectedEntity;
        }

        private void AddRigidbody_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte w�hlen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new Rigidbody(entity));
        }

        private void AddBoxCollider_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte w�hlen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new BoxCollider(entity));
        }

        private void AddSphereCollider_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte w�hlen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new SphereCollider(entity));
        }

        private void AddCapsuleCollider_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte w�hlen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new CapsuleCollider(entity));
        }

        private void AddMeshRenderer_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte w�hlen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new MeshRenderer(entity));
        }

        private void AddAudioSource_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte w�hlen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new AudioSource(entity));
        }

        private void WinClose_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

        private void WinMin_Click(object sender, RoutedEventArgs e)
        {
            var w = Window.GetWindow(this);
            if (w != null) w.WindowState = WindowState.Minimized;
        }

        private void WinMax_Click(object sender, RoutedEventArgs e)
        {
            var w = Window.GetWindow(this);
            if (w != null) w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CreateScript_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectData.Current == null)
            {
                MessageBox.Show("Open a project first.", "Vortex", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                var path = ScriptingService.CreateScript("NewBehaviour");
                ScriptingService.OpenInVisualStudio(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not create script: " + ex.Message, "Vortex", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenScriptsInVS_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectData.Current == null)
            {
                MessageBox.Show("Open a project first.", "Vortex", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ScriptingService.OpenInVisualStudio();
        }

        private void AddScript_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte w�hlen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                var scriptPath = ScriptingService.CreateScript("NewBehaviour");
                entity.AddComponent(new Script(entity, scriptPath));
                ScriptingService.OpenInVisualStudio(scriptPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not create script: " + ex.Message, "Vortex", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion

        #region View Menu

        private void ToggleGrid_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                EditorViewportService.Instance.IsGridVisible = menuItem.IsChecked;
            }
        }

        private void ToggleGizmos_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                EditorViewportService.Instance.AreGizmosVisible = menuItem.IsChecked;
            }
        }

        private void SnapToGrid_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                EditorViewportService.Instance.SnapToGrid = menuItem.IsChecked;
            }
        }


        #endregion

        #region Toolbar Buttons - Transform Tools

        private void MoveTool_Click(object sender, RoutedEventArgs e)
        {
            TransformGizmoService.Instance.SetTranslateMode();
            UpdateToolButtonStates();
        }

        private void RotateTool_Click(object sender, RoutedEventArgs e)
        {
            TransformGizmoService.Instance.SetRotateMode();
            UpdateToolButtonStates();
        }

        private void ScaleTool_Click(object sender, RoutedEventArgs e)
        {
            TransformGizmoService.Instance.SetScaleMode();
            UpdateToolButtonStates();
        }

        private void UpdateToolButtonStates()
        {
            // Visual feedback for active tool could be added here
            // For now, just ensure the mode is set
        }

        #endregion

        #region Toolbar Buttons - View Options

        private void ToggleGridButton_Click(object sender, RoutedEventArgs e)
        {
            EditorViewportService.Instance.ToggleGrid();
            ToggleGridMenuItem.IsChecked = EditorViewportService.Instance.IsGridVisible;
        }

        private void SnapToGridButton_Click(object sender, RoutedEventArgs e)
        {
            EditorViewportService.Instance.SnapToGrid = !EditorViewportService.Instance.SnapToGrid;
            SnapToGridMenuItem.IsChecked = EditorViewportService.Instance.SnapToGrid;
            TransformGizmoService.Instance.SnapEnabled = EditorViewportService.Instance.SnapToGrid;
        }

        private void ToggleGizmosButton_Click(object sender, RoutedEventArgs e)
        {
            EditorViewportService.Instance.ToggleGizmos();
            ToggleGizmosMenuItem.IsChecked = EditorViewportService.Instance.AreGizmosVisible;
        }

        #endregion

        #region Play Mode Controls

        private bool _isPlaying;
        private bool _isPaused;

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle: ▶ starts the game, and while playing the same button STOPS it (returns to the
            // build/edit view). Without this you got stuck in play and the button looked dead.
            if (!_isPlaying)
                StartPlayMode();
            else
                StopPlayMode();
        }

        /// <summary>"Scene" tab: the build view (edit / fly-camera). Stops the game if it's running.</summary>
        private void SceneTab_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlaying) StopPlayMode();
        }

        /// <summary>"Game" tab: run the compiled game in the viewport. Does nothing if already playing.</summary>
        private void GameTab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPlaying) StartPlayMode();
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlaying && !_isPaused)
            {
                PausePlayMode();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlaying)
            {
                StopPlayMode();
            }
        }

        private void StartPlayMode()
        {
            _isPlaying = true;
            _isPaused = false;

            // Initialize input system
            InputBindingsService.Instance.Initialize();
            InputBindingsService.Instance.EnableGameInputForwarding = true;

            // Update button styles
            UpdatePlayModeButtons();

            // Notify other components
            PlayModeChanged?.Invoke(this, new PlayModeEventArgs(true, false));

            // Run the game IN the free-cam viewport — this is stable because it reuses the editor's
            // own DX12 swapchain. (Launching a separate game window re-inited the single global
            // swapchain onto a new HWND AND hid/locked the cursor, which whited-out the windows and
            // stranded the mouse — "alles kaputt". A real external window needs per-window swapchains,
            // deferred.) Render through the scene's main camera while playing.
            var mainCam = Editor.Core.Services.CameraService.Instance.GetMainCamera();
            if (mainCam.IsValid)
                Editor.Core.Services.CameraService.Instance.SetActiveCamera(mainCam);

            Editor.Core.Services.PlayModeService.Instance.Play();
        }

        private void PausePlayMode()
        {
            _isPaused = true;
            InputBindingsService.Instance.EnableGameInputForwarding = false;
            UpdatePlayModeButtons();
            PlayModeChanged?.Invoke(this, new PlayModeEventArgs(true, true));
            Editor.Core.Services.PlayModeService.Instance.Pause();
        }

        private void ResumePlayMode()
        {
            _isPaused = false;
            InputBindingsService.Instance.EnableGameInputForwarding = true;
            UpdatePlayModeButtons();
            PlayModeChanged?.Invoke(this, new PlayModeEventArgs(true, false));
            Editor.Core.Services.PlayModeService.Instance.Resume();
        }

        private void StopPlayMode()
        {
            _isPlaying = false;
            _isPaused = false;
            
            InputBindingsService.Instance.EnableGameInputForwarding = false;
            InputBindingsService.Instance.Shutdown();
            
            UpdatePlayModeButtons();
            PlayModeChanged?.Invoke(this, new PlayModeEventArgs(false, false));

            // Hand the view back to the editor fly-camera.
            Editor.Core.Services.CameraService.Instance.SwitchToEditorCamera();
            Editor.Core.Services.PlayModeService.Instance.Stop();
        }

        private void AssetStore_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Der Vortex Asset Store ist noch nicht verfügbar — er kommt in einer späteren Version.",
                "Asset Store", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private Editor.PlayMode.GameWindow _gameWindow;

        /// <summary>Opens the standalone game window and freezes the editor while it plays.</summary>
        private void LaunchGameWindow()
        {
            if (_gameWindow != null) { _gameWindow.Activate(); return; }
            if (Application.Current?.MainWindow != null)
                Application.Current.MainWindow.IsEnabled = false; // freeze the editor
            _gameWindow = new Editor.PlayMode.GameWindow();
            _gameWindow.Closed += (s, e) =>
            {
                _gameWindow = null;
                if (Application.Current?.MainWindow != null)
                    Application.Current.MainWindow.IsEnabled = true; // unfreeze when the game window closes
            };
            _gameWindow.Show();
        }

        private void UpdatePlayModeButtons()
        {
            var playingColor = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(78, 201, 176)); // #4EC9B0
            var stoppedColor = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(128, 128, 128)); // #808080
            var activeColor = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(63, 169, 245)); // #3FA9F5

            if (PlayButton != null)
            {
                PlayButton.Foreground = _isPlaying && !_isPaused ? activeColor : playingColor;
            }
            if (PauseButton != null)
            {
                PauseButton.Foreground = _isPaused ? activeColor : stoppedColor;
            }

            // Top bar: highlight the active tab (Scene = build/edit, Game = playing) and recolor the
            // ▶ button so it's obvious the game is running and that the same button now stops it.
            try
            {
                var segActive = (System.Windows.Style)FindResource("SegActiveStyle");
                var segNormal = (System.Windows.Style)FindResource("SegStyle");
                if (SceneTab != null) SceneTab.Style = _isPlaying ? segNormal : segActive;
                if (GameTab != null) GameTab.Style = _isPlaying ? segActive : segNormal;
            }
            catch { /* styles resolve at runtime; ignore if not yet loaded */ }

            if (TopPlayBtn != null)
            {
                if (_isPlaying)
                {
                    // Red rounded square = Stop (clear "the game is running, press to stop").
                    TopPlayBtn.Content = new System.Windows.Shapes.Rectangle
                    {
                        Width = 11, Height = 11, RadiusX = 2, RadiusY = 2,
                        Fill = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0xE7, 0x4C, 0x3C))
                    };
                    TopPlayBtn.ToolTip = "Stop (back to build view)";
                }
                else
                {
                    TopPlayBtn.Content = ""; // Play glyph (Segoe MDL2)
                    TopPlayBtn.Foreground = System.Windows.Media.Brushes.White;
                    TopPlayBtn.ToolTip = "Play";
                }
            }
        }

        /// <summary>
        /// Event fired when play mode state changes.
        /// </summary>
        public event EventHandler<PlayModeEventArgs> PlayModeChanged;

        #endregion


        #region Assets Menu

        private void ImportAsset_Click(object sender, RoutedEventArgs e)
        {
            var openDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Asset",
                // Note: GLB/GLTF not supported by Assimp 3.0!
                Filter = "Supported 3D Models|*.fbx;*.obj;*.dae;*.3ds;*.blend|" +
                         "FBX Files|*.fbx|" +
                         "OBJ Files|*.obj|" +
                         "Collada|*.dae|" +
                         "All Files|*.*",
                Multiselect = false
            };

            if (openDialog.ShowDialog() == true)
            {
                // Log the import attempt
                System.Diagnostics.Debug.WriteLine($"[Import] Starting import of: {openDialog.FileName}");
                
                // Check if Assimp is available
                bool assimpAvailable = VortexAPI.IsAssimpAvailable();
                System.Diagnostics.Debug.WriteLine($"[Import] Assimp available: {assimpAvailable}");
                
                if (!assimpAvailable)
                {
                    MessageBox.Show(
                        "Assimp library is not available.\n\n" +
                        "Please ensure assimp.dll is in the application directory.\n" +
                        "The Engine needs Assimp to import 3D models.",
                        "Import Error - Missing Library",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
                
                var result = ModelImportService.Instance.ImportModel(openDialog.FileName);
                
                if (result.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[Import] Success! Submeshes: {result.SubmeshCount}");
                    var dialog = new Dialogs.ImportResultDialog(result);
                    dialog.Owner = Window.GetWindow(this);
                    dialog.ShowDialog();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Import] FAILED: {result.ErrorMessage}");
                    
                    string detailedMessage = $"Asset Import Failed:\n\n{result.ErrorMessage}\n\n" +
                        "Common causes:\n" +
                        "� FBX file was created with a newer version of FBX SDK\n" +
                        "� File path contains special characters (�, �, �, etc.)\n" +
                        "� The model uses unsupported features\n\n" +
                        "Try exporting the model as OBJ or GLTF format.";
                    
                    MessageBox.Show(
                        detailedMessage,
                        "Import Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Event args for play mode changes.
    /// </summary>
    public class PlayModeEventArgs : EventArgs
    {
        public bool IsPlaying { get; }
        public bool IsPaused { get; }

        public PlayModeEventArgs(bool isPlaying, bool isPaused)
        {
            IsPlaying = isPlaying;
            IsPaused = isPaused;
        }
    }
}

