using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Editor.Core.UndoRedo;
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
            e.CanExecute = UndoRedoManager.Instance.CanUndo;
        }

        private void OnUndoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            UndoRedoManager.Instance.Undo();
        }

        private void OnCanRedo(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = UndoRedoManager.Instance.CanRedo;
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
            // TODO: Implement delete
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement duplicate
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
    }
}

