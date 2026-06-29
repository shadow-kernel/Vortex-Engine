using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AvalonDock.Layout;
using Editor.Core.Data;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;
using Editor.Editors.WorldEditor.Components.GamePreview;
using Editor.Editors.WorldEditor.Services;

namespace Editor.Editors.WorldEditor
{
    public partial class WorldEditorView : UserControl
    {
        private GamePreviewView _gamePreview;

        /// <summary>The live editor view, so components (e.g. the asset browser) can open viewport document tabs.</summary>
        public static WorldEditorView Current { get; private set; }

        public WorldEditorView()
        {
            InitializeComponent();
            Current = this;
            Loaded += OnLoaded;
            
            // Set custom placement for camera preview popup
            CameraPreviewPopup.CustomPopupPlacementCallback = BottomRightPopupPlacement;
        }
        
        /// <summary>
        /// Custom placement callback for positioning the camera preview popup bottom-right
        /// within the GamePreviewView bounds.
        /// </summary>
        private CustomPopupPlacement[] BottomRightPopupPlacement(Size popupSize, Size targetSize, Point offset)
        {
            // Position at bottom-right of the GamePreviewView
            // Offset by toolbar (28px) at top and status bar (22px) at bottom
            double x = targetSize.Width - popupSize.Width - 16;
            double y = targetSize.Height - popupSize.Height - 38; // 22 (status bar) + 16 (margin)
            
            // Ensure it stays within bounds
            x = System.Math.Max(16, x);
            y = System.Math.Max(28, y); // Don't overlap toolbar
            
            var placement = new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None);
            return new[] { placement };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            WindowService.Instance.WindowVisibilityChanged += OnWindowVisibilityChanged;
            
            // Find the GamePreviewView
            _gamePreview = FindGamePreviewView();
            
            // Set the popup placement target to the GamePreviewView
            if (_gamePreview != null)
            {
                CameraPreviewPopup.PlacementTarget = _gamePreview;
            }
            
            // Initialize FileExplorerService when project is loaded
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.DataContextChanged += OnDataContextChanged;
                InitializeProject(window.DataContext as ProjectData);
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            InitializeProject(e.NewValue as ProjectData);
        }

        private void InitializeProject(ProjectData project)
        {
            if (project != null && !string.IsNullOrEmpty(project.Path))
            {
                FileExplorerService.Instance.Initialize(project.Path);
                
                // Set the active scene for rendering
                if (_gamePreview != null && project.ActiveScene != null)
                {
                    _gamePreview.CurrentScene = project.ActiveScene;
                    project.ActiveScene.ActivateEntities();
                }
            }
        }

        /// <summary>
        /// Opens (or re-activates) a dedicated Model-Viewer document TAB next to the Scene tab — a free-camera
        /// viewport that renders ONLY this model so the user can inspect it isolated and large. Triggered by
        /// Ctrl+double-click in the asset browser.
        /// </summary>
        public void OpenModelViewerTab(string fullModelPath, string modelName)
        {
            var pane = DockManager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
            if (pane == null) return;

            string title = "Model: " + modelName;

            // Reuse an already-open viewer for the same model instead of stacking duplicates.
            var existing = DockManager.Layout.Descendents().OfType<LayoutDocument>()
                .FirstOrDefault(d => string.Equals(d.Title, title, System.StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.IsActive = true; return; }

            var viewer = new Components.ModelViewer.ModelViewerControl(fullModelPath, modelName);
            var doc = new LayoutDocument
            {
                Title = title,
                CanClose = true,
                CanFloat = true,
                Content = viewer
            };
            doc.Closed += (s, e) => { try { viewer.Dispose(); } catch { } };
            pane.Children.Add(doc);
            doc.IsActive = true;
        }

        private GamePreviewView FindGamePreviewView()
        {
            var documents = DockManager.Layout.Descendents()
                .OfType<LayoutDocument>()
                .ToList();

            foreach (var doc in documents)
            {
                if (doc.Content is GamePreviewView gpv)
                    return gpv;
            }
            return null;
        }

        /// <summary>
        /// Zeigt oder versteckt den Editor-Inhalt (DockManager).
        /// </summary>
        public void SetEditorVisible(bool isVisible)
        {
            DockManager.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Setzt alle Fenster auf sichtbar zurück.
        /// </summary>
        public void ResetLayout()
        {
            var anchorables = DockManager.Layout.Descendents()
                .OfType<LayoutAnchorable>()
                .ToList();

            foreach (var anchorable in anchorables)
            {
                anchorable.Show();
            }

            // Update WindowService
            WindowService.Instance.IsSceneVisible = true;
            WindowService.Instance.IsProjectVisible = true;
            WindowService.Instance.IsExplorerVisible = true;
            WindowService.Instance.IsConsoleVisible = true;
            WindowService.Instance.IsHierarchyVisible = true;
            WindowService.Instance.IsInspectorVisible = true;
        }

        private void OnWindowVisibilityChanged(object sender, WindowVisibilityChangedEventArgs e)
        {
            var anchorable = FindAnchorableByTitle(e.WindowName);
            if (anchorable != null)
            {
                if (e.IsVisible)
                {
                    anchorable.Show();
                }
                else
                {
                    anchorable.Hide();
                }
            }
        }

        private LayoutAnchorable FindAnchorableByTitle(string title)
        {
            return DockManager.Layout.Descendents()
                .OfType<LayoutAnchorable>()
                .FirstOrDefault(a => a.Title == title);
        }
    }
}
