using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;
using Editor.Core.Data;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;
using Editor.Editors.WorldEditor.Services;

namespace Editor.Editors.WorldEditor
{
    public partial class WorldEditorView : UserControl
    {
        public WorldEditorView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            WindowService.Instance.WindowVisibilityChanged += OnWindowVisibilityChanged;
            
            // Initialize FileExplorerService when project is loaded
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.DataContextChanged += OnDataContextChanged;
                InitializeFileExplorer(window.DataContext as ProjectData);
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            InitializeFileExplorer(e.NewValue as ProjectData);
        }

        private void InitializeFileExplorer(ProjectData project)
        {
            if (project != null && !string.IsNullOrEmpty(project.Path))
            {
                FileExplorerService.Instance.Initialize(project.Path);
            }
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
