using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
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
                "Möchten Sie das aktuelle Projekt schließen?\n\nUngespeicherte Änderungen gehen verloren.",
                "Projekt schließen",
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
            MessageBox.Show("Projekt bauen und ausführen - Noch nicht implementiert", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Möchten Sie den Editor wirklich beenden?\n\nUngespeicherte Änderungen gehen verloren.",
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
                MessageBox.Show($"Konnte Dokumentation nicht öffnen: {ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Vortex Engine\n\nVersion 1.0.0\n\n© 2024 Shadow Kernel",
                "Über Vortex Engine",
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
                MessageBox.Show("Bitte wählen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new Rigidbody(entity));
        }

        private void AddBoxCollider_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte wählen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new BoxCollider(entity));
        }

        private void AddSphereCollider_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte wählen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new SphereCollider(entity));
        }

        private void AddCapsuleCollider_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte wählen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new CapsuleCollider(entity));
        }

        private void AddMeshRenderer_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte wählen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new MeshRenderer(entity));
        }

        private void AddAudioSource_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte wählen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new AudioSource(entity));
        }

        private void AddScript_Click(object sender, RoutedEventArgs e)
        {
            var entity = GetSelectedEntity();
            if (entity == null)
            {
                MessageBox.Show("Bitte wählen Sie zuerst eine Entity aus.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            entity.AddComponent(new Script(entity));
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
    }
}

