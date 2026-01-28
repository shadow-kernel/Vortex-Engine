using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;

namespace Editor.Editors.WorldEditor.Components.SceneHierarchy
{
    public partial class SceneHierarchyView : UserControl
    {
        private SceneHierarchyViewModel ViewModel => DataContext as SceneHierarchyViewModel;

        public SceneHierarchyView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            
            // PreviewKeyDown f�r Keyboard-Shortcuts (vor TreeView)
            this.PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Verbinde mit dem aktuellen Projekt
            var project = ProjectData.Current;
            if (project != null)
            {
                ViewModel?.SetProject(project);
            }
        }

        /// <summary>
        /// Setzt das aktuelle Projekt f�r die Hierarchy-Ansicht
        /// </summary>
        public void SetProject(ProjectData project)
        {
            ViewModel?.SetProject(project);
        }

        /// <summary>
        /// Setzt die aktuelle Szene f�r die Hierarchy-Ansicht
        /// </summary>
        public void SetScene(Scene scene)
        {
            ViewModel?.SetScene(scene);
        }

        #region Keyboard Shortcuts

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ViewModel == null) return;

            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            switch (e.Key)
            {
                // Clipboard Operations
                case Key.X when ctrl:
                    if (ViewModel.CutCommand.CanExecute(null))
                    {
                        ViewModel.CutCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.C when ctrl:
                    if (ViewModel.CopyCommand.CanExecute(null))
                    {
                        ViewModel.CopyCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.V when ctrl:
                    if (ViewModel.PasteCommand.CanExecute(null))
                    {
                        ViewModel.PasteCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;

                // Delete
                case Key.Delete:
                    if (ViewModel.DeleteEntityCommand.CanExecute(null))
                    {
                        ViewModel.DeleteEntityCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;

                // Duplicate
                case Key.D when ctrl:
                    if (ViewModel.DuplicateEntityCommand.CanExecute(null))
                    {
                        ViewModel.DuplicateEntityCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;

                // Select All
                case Key.A when ctrl:
                    if (ViewModel.SelectAllCommand.CanExecute(null))
                    {
                        ViewModel.SelectAllCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;

                // Rename
                case Key.F2:
                    if (ViewModel.SelectedEntity != null)
                    {
                        RenameEntity_Click(sender, null);
                        e.Handled = true;
                    }
                    break;

                // Escape to clear selection
                case Key.Escape:
                    ViewModel.ClearSelection();
                    e.Handled = true;
                    break;
            }
        }

        #endregion

        #region Toolbar Buttons

        private void AddScene_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateSceneCommand.Execute(null);
        }

        private void AddEntity_Click(object sender, RoutedEventArgs e)
        {
            // Show context menu for adding entities
            var contextMenu = FindResource("SceneContextMenu") as ContextMenu;
            if (contextMenu != null)
            {
                contextMenu.PlacementTarget = AddEntityButton;
                contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                contextMenu.IsOpen = true;
            }
        }

        #endregion

        #region Tree Selection

        private void HierarchyTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (ViewModel == null) return;

            // Bei normaler Selektion (ohne Modifier) - wird bereits �ber PreviewMouseDown behandelt
            if (e.NewValue is GameEntity entity)
            {
                // Setze SelectedEntity f�r Inspector usw.
                ViewModel.SelectedEntity = entity;
                
                // Wenn keine Modifier-Taste gedr�ckt ist, ersetze die Selektion
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && 
                    !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    ViewModel.SetSelection(entity);
                }
            }
            else if (e.NewValue is Scene scene)
            {
                ViewModel.SelectedScene = scene;
                ViewModel.ClearSelection();
            }
        }

        private void HierarchyTree_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Allow right-click on empty space to show scene menu
            var treeViewItem = GetTreeViewItemFromPoint(e.GetPosition(HierarchyTree));
            if (treeViewItem == null && ViewModel?.SelectedScene != null)
            {
                var contextMenu = FindResource("SceneContextMenu") as ContextMenu;
                if (contextMenu != null)
                {
                    contextMenu.PlacementTarget = HierarchyTree;
                    contextMenu.IsOpen = true;
                }
                e.Handled = true;
            }
        }

        private TreeViewItem GetTreeViewItemFromPoint(Point point)
        {
            var element = HierarchyTree.InputHitTest(point) as DependencyObject;
            while (element != null && !(element is TreeViewItem))
            {
                element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            }
            return element as TreeViewItem;
        }

        #endregion

        #region Scene Activation

        private void ActivateScene_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && 
                menuItem.Parent is ContextMenu contextMenu &&
                contextMenu.PlacementTarget is FrameworkElement element &&
                element.DataContext is Scene scene)
            {
				ViewModel?.ActivateScene(scene);
            }
        }

		private void DeactivateScene_Click(object sender, RoutedEventArgs e)
		{
			if (sender is MenuItem menuItem && 
				menuItem.Parent is ContextMenu contextMenu &&
				contextMenu.PlacementTarget is FrameworkElement element &&
				element.DataContext is Scene scene)
			{
				ViewModel?.DeactivateScene(scene);
			}
		}

        #endregion

        #region Clipboard Operations

        private void Cut_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CutCommand.Execute(null);
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CopyCommand.Execute(null);
        }

        private void Paste_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.PasteCommand.Execute(null);
        }

        #endregion

        #region Prefab Operations

        private void SaveAsPrefab_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.SelectedEntity == null) return;

            var project = ProjectData.Current;
            if (project == null) return;

            try
            {
                ProjectService.Instance.SavePrefab(project, ViewModel.SelectedEntity);
                MessageBox.Show(
                    $"Prefab '{ViewModel.SelectedEntity.Name}.ventity' saved successfully!",
                    "Save Prefab",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    $"Error saving prefab: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        #endregion

        #region Entity Creation - 3D Objects

        private void CreateEmpty_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateEmptyEntityCommand.Execute(null);
        }

        private void CreateFolder_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateFolderCommand.Execute(null);
        }

        private void CreateCube_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateCubeCommand.Execute(null);
        }

        private void CreateSphere_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateSphereCommand.Execute(null);
        }

        private void CreateCapsule_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateCapsuleCommand.Execute(null);
        }

        private void CreateCylinder_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateCylinderCommand.Execute(null);
        }

        private void CreatePlane_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreatePlaneCommand.Execute(null);
        }

        private void CreateQuad_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateQuadCommand.Execute(null);
        }


        #endregion

        #region Prefab Instantiation

        private void InstantiatePrefab_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.SelectedScene == null) return;

            var project = ProjectData.Current;
            if (project == null) return;

            // �ffne Datei-Dialog zum Ausw�hlen eines Prefabs
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Prefab to Instantiate",
                Filter = "Prefab Files (*.ventity)|*.ventity",
                InitialDirectory = System.IO.Path.Combine(project.Path, "Assets", "Prefabs")
            };

            // Erstelle Prefabs-Ordner falls nicht vorhanden
            if (!System.IO.Directory.Exists(dialog.InitialDirectory))
            {
                System.IO.Directory.CreateDirectory(dialog.InitialDirectory);
            }

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var entity = SceneService.Instance.LoadEntityFromPrefab(dialog.FileName);
                    if (entity != null)
                    {
                        entity.Scene = ViewModel.SelectedScene;
                        entity.RegenerateIds(); // Neue IDs f�r die Instanz
                        ViewModel.SelectedScene.AddEntity(entity);
                        ViewModel.SelectedEntity = entity;
                    }
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show(
                        $"Error loading prefab: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        #endregion

        #region Entity Creation - Lights

        private void CreateDirectionalLight_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateDirectionalLightCommand.Execute(null);
        }

        private void CreatePointLight_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreatePointLightCommand.Execute(null);
        }

        private void CreateSpotLight_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateSpotLightCommand.Execute(null);
        }

        private void CreateSkybox_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateSkyboxCommand.Execute(null);
        }

        #endregion

        #region Entity Creation - Other

        private void CreateCamera_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateCameraCommand.Execute(null);
        }


        private void CreateAudioSource_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateAudioSourceCommand.Execute(null);
        }

        #endregion

        #region Entity Creation - UI

        private void CreateUICanvas_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateUICanvasCommand.Execute(null);
        }

        private void CreateUIText_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateUITextCommand.Execute(null);
        }

        private void CreateUIImage_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateUIImageCommand.Execute(null);
        }

        private void CreateUIButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateUIButtonCommand.Execute(null);
        }

        #endregion

        #region Entity Actions

        private void CreateChildEntity_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateChildEntityCommand.Execute(null);
        }

        private void RenameEntity_Click(object sender, RoutedEventArgs e)
        {
            // Rename dialog
            if (ViewModel?.SelectedEntity != null)
            {
                var dialog = new Window
                {
                    Title = "Rename Entity",
                    Width = 300,
                    Height = 120,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Window.GetWindow(this),
                    Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252526"))
                };

                var stack = new StackPanel { Margin = new Thickness(10) };
                var textBox = new TextBox 
                { 
                    Text = ViewModel.SelectedEntity.Name,
                    Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E")),
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C5C5C5")),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3C3C3C")),
                    Padding = new Thickness(8, 6, 8, 6),
                    FontSize = 13
                };
                textBox.SelectAll();

                var okButton = new Button 
                { 
                    Content = "OK", 
                    Width = 80, 
                    Height = 28,
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0E639C")),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0)
                };

                okButton.Click += (s, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(textBox.Text))
                    {
                        ViewModel.SelectedEntity.Name = textBox.Text;
                    }
                    dialog.Close();
                };

                stack.Children.Add(textBox);
                stack.Children.Add(okButton);
                dialog.Content = stack;
                
                textBox.KeyDown += (s, args) =>
                {
                    if (args.Key == Key.Enter)
                        okButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (args.Key == Key.Escape)
                        dialog.Close();
                };

                dialog.Loaded += (s, args) => textBox.Focus();
                dialog.ShowDialog();
            }
        }

        private void DuplicateEntity_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.DuplicateEntityCommand.Execute(null);
        }

        private void DeleteEntity_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.DeleteEntityCommand.Execute(null);
        }

        #endregion

        #region Scene Actions

        private void SceneContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Context menu is already bound to the scene via PlacementTarget
            // All items are visible when opened on a scene
        }

        private void SaveScene_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.SaveSceneCommand.Execute(null);
        }

        private void UnloadScene_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && 
                GetContextMenuDataContext(menuItem) is Scene scene)
            {
                if (ViewModel?.Scenes?.Count > 1)
                {
                    ViewModel.Scenes.Remove(scene);
                }
                else
                {
                    MessageBox.Show("Cannot unload the only scene.", "Unload Scene", 
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void LoadExistingScene_Click(object sender, RoutedEventArgs e)
        {
            var project = ProjectData.Current;
            if (project == null) return;

            var scenesFolder = System.IO.Path.Combine(project.Path, "Assets", "Scenes");
            if (!System.IO.Directory.Exists(scenesFolder))
            {
                System.IO.Directory.CreateDirectory(scenesFolder);
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load Scene",
                Filter = "Scene Files (*.vscene)|*.vscene",
                InitialDirectory = scenesFolder
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var scene = SceneService.Instance.LoadScene(dialog.FileName);
                    if (scene != null)
                    {
                        scene.Project = project;
                        ViewModel?.Scenes?.Add(scene);
                        ViewModel?.ActivateScene(scene);
                    }
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"Error loading scene: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private object GetContextMenuDataContext(MenuItem menuItem)
        {
            if (menuItem.Parent is ContextMenu contextMenu &&
                contextMenu.PlacementTarget is FrameworkElement element)
            {
                return element.DataContext;
            }
            return null;
        }

        private void DeleteScene_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.SelectedScene != null && ViewModel.Scenes?.Count > 1)
            {
                var result = MessageBox.Show(
                    $"Are you sure you want to delete the scene '{ViewModel.SelectedScene.Name}'?",
                    "Delete Scene",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    ViewModel.DeleteSceneCommand.Execute(null);
                }
            }
            else
            {
                MessageBox.Show("Cannot delete the only scene in the project.", "Delete Scene", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        #endregion

        #region Drag & Drop

        private Point _dragStartPoint;
        private bool _isDragging = false;
        private GameEntity _draggedEntity = null;

        private void HierarchyTree_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging && _draggedEntity != null)
            {
                var currentPoint = e.GetPosition(HierarchyTree);
                var diff = currentPoint - _dragStartPoint;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isDragging = true;
                    var data = new DataObject(typeof(GameEntity), _draggedEntity);
                    System.Windows.DragDrop.DoDragDrop(HierarchyTree, data, DragDropEffects.Move);
                    _isDragging = false;
                    _draggedEntity = null;
                }
            }
        }

        private void HierarchyTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel == null) return;

            _dragStartPoint = e.GetPosition(HierarchyTree);
            
            // Finde das angeklickte TreeViewItem
            var treeViewItem = GetTreeViewItemFromPoint(e.GetPosition(HierarchyTree));
            if (treeViewItem == null)
            {
                _draggedEntity = null;
                return;
            }

            var entity = treeViewItem.DataContext as GameEntity;
            _draggedEntity = entity;

            if (entity == null) return;

            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            if (ctrl)
            {
                // Toggle selection bei Ctrl+Click
                if (ViewModel.SelectedEntities.Contains(entity))
                {
                    ViewModel.RemoveFromSelection(entity);
                }
                else
                {
                    ViewModel.AddToSelection(entity);
                }
                e.Handled = true;
            }
            else if (shift && ViewModel.SelectedEntity != null)
            {
                // Extend selection bei Shift+Click
                ViewModel.ExtendSelection(entity);
                e.Handled = true;
            }
        }

        private void HierarchyTree_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            
            // Erlaube Drop von .ventity Dateien
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    var ext = Path.GetExtension(files[0]).ToLower();
                    if (ext == ".ventity")
                    {
                        e.Effects = DragDropEffects.Copy;
                    }
                }
            }
            // Erlaube Drop von GameEntities
            else if (e.Data.GetDataPresent(typeof(GameEntity)))
            {
                var draggedEntity = e.Data.GetData(typeof(GameEntity)) as GameEntity;
                var targetItem = GetTreeViewItemFromPoint(e.GetPosition(HierarchyTree));
                
                if (draggedEntity != null && targetItem != null)
                {
                    var targetEntity = targetItem.DataContext as GameEntity;
                    var targetScene = targetItem.DataContext as Scene;
                    
                    // Verhindere Drop auf sich selbst oder eigene Kinder
                    if (targetEntity != null && targetEntity != draggedEntity && !IsDescendantOf(targetEntity, draggedEntity))
                    {
                        e.Effects = DragDropEffects.Move;
                    }
                    else if (targetScene != null)
                    {
                        e.Effects = DragDropEffects.Move;
                    }
                }
            }
            
            e.Handled = true;
        }

        private void HierarchyTree_Drop(object sender, DragEventArgs e)
        {
            // Handle .ventity file drops
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                {
                    foreach (var file in files)
                    {
                        if (Path.GetExtension(file).ToLower() == ".ventity")
                        {
                            try
                            {
                                var entity = SceneService.Instance.LoadEntityFromPrefab(file);
                                if (entity != null && ViewModel?.SelectedScene != null)
                                {
                                    entity.Scene = ViewModel.SelectedScene;
                                    ViewModel.SelectedScene.AddEntity(entity);
                                }
                            }
                            catch
                            {
                                MessageBox.Show($"Error loading prefab: {file}", "Error", 
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                    }
                }
                e.Handled = true;
                return;
            }

            // Handle Entity drops
            if (e.Data.GetDataPresent(typeof(GameEntity)))
            {
                var draggedEntity = e.Data.GetData(typeof(GameEntity)) as GameEntity;
                var targetItem = GetTreeViewItemFromPoint(e.GetPosition(HierarchyTree));

                if (draggedEntity != null && targetItem != null && ViewModel != null)
                {
                    var targetEntity = targetItem.DataContext as GameEntity;
                    var targetScene = targetItem.DataContext as Scene;

                    if (targetEntity != null && targetEntity != draggedEntity)
                    {
                        // Shift gedr�ckt: Als Geschwister einf�gen (Reorder)
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        {
                            ViewModel.MoveEntityToPosition(draggedEntity, targetEntity, insertAfter: true);
                        }
                        // Alt gedr�ckt: Vor das Element einf�gen
                        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                        {
                            ViewModel.MoveEntityToPosition(draggedEntity, targetEntity, insertAfter: false);
                        }
                        // Normal: Als Kind einf�gen
                        else
                        {
                            ViewModel.MoveEntityToParent(draggedEntity, targetEntity);
                        }
                    }
                    else if (targetScene != null && targetScene != draggedEntity.Scene)
                    {
                        // In andere Scene verschieben
                        ViewModel.MoveEntityToScene(draggedEntity, targetScene);
                    }
                    else if (targetScene != null && targetScene == draggedEntity.Scene)
                    {
                        // Auf Root-Ebene der gleichen Scene verschieben
                        ViewModel.MoveEntityToParent(draggedEntity, null);
                    }
                }
                e.Handled = true;
            }
        }

        private bool IsDescendantOf(GameEntity potentialDescendant, GameEntity ancestor)
        {
            var current = potentialDescendant;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = current.Parent;
            }
            return false;
        }

        #endregion

        #region Camera Preview

        /// <summary>
        /// Handle entity context menu opening - show/hide camera-specific options.
        /// </summary>
        private void EntityContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu contextMenu)
            {
                // Find the menu items
                var showCameraPreviewItem = FindContextMenuItem(contextMenu, "ShowCameraPreviewItem");
                var cameraSeparator = FindContextMenuItem(contextMenu, "CameraMenuSeparator");
                
                // Check if the selected entity is a camera
                bool isCamera = false;
                if (ViewModel?.SelectedEntity != null)
                {
                    isCamera = ViewModel.SelectedEntity.GetComponent<ECS.Components.Rendering.Camera>() != null;
                }
                
                // Show/hide camera-specific options
                if (showCameraPreviewItem != null)
                    showCameraPreviewItem.Visibility = isCamera ? Visibility.Visible : Visibility.Collapsed;
                if (cameraSeparator != null)
                    cameraSeparator.Visibility = isCamera ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private FrameworkElement FindContextMenuItem(ContextMenu menu, string name)
        {
            foreach (var item in menu.Items)
            {
                if (item is FrameworkElement element && element.Name == name)
                    return element;
            }
            return null;
        }

        /// <summary>
        /// Show camera preview for the selected camera entity.
        /// </summary>
        private void ShowCameraPreview_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.SelectedEntity == null) return;
            
            var camera = ViewModel.SelectedEntity.GetComponent<ECS.Components.Rendering.Camera>();
            if (camera != null)
            {
                // Notify the GamePreviewView to show the camera preview PIP
                CameraPreviewService.Instance.ShowPreview(ViewModel.SelectedEntity);
            }
        }

        /// <summary>
        /// Handle double-click on tree item to show camera preview.
        /// </summary>
        private void HierarchyTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel?.SelectedEntity == null) return;
            
            // Check if double-clicked on a camera entity
            var camera = ViewModel.SelectedEntity.GetComponent<ECS.Components.Rendering.Camera>();
            if (camera != null)
            {
                // Show camera preview
                CameraPreviewService.Instance.ShowPreview(ViewModel.SelectedEntity);
                e.Handled = true;
            }
        }

        #endregion
    }
}
