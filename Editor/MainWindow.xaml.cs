using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Project.Projection;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

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
            SaveCurrentProject();
            e.Handled = true;
        }

        private void OnSaveAll(object sender, ExecutedRoutedEventArgs e)
        {
            SaveAll();
            e.Handled = true;
        }

        /// <summary>Ctrl+S — saves the active scene and the project file. Shows a brief confirmation.</summary>
        public void SaveCurrentProject()
        {
            var project = ProjectData.Current;
            if (project == null) return;
            if (project.ActiveScene != null)
                SceneService.Instance.SaveScene(project.ActiveScene);
            ProjectService.Instance.SaveProject(project);
            ShowToast("Project saved");
        }

        /// <summary>Ctrl+Shift+S — saves every open scene and the project file.</summary>
        public void SaveAll()
        {
            var project = ProjectData.Current;
            if (project == null) return;
            SceneService.Instance.SaveAllScenes(project);
            ProjectService.Instance.SaveProject(project);
            ShowToast("All scenes saved");
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
        public bool OpenProjectBrowser(bool createTab = false)
        {
            var browserWindow = new ProjectBrowserWindow
            {
                Owner = this
            };
            if (createTab) browserWindow.StartOnCreateTab();

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
            
            // Initialize asset services for this project
            Core.Assets.AssetTagService.Instance.Initialize(project.Path);
            
            // Speichere als letztes geöffnetes Projekt
            // Hinweis: Die Engine-Aktivierung erfolgt im SceneHierarchyViewModel.SelectedScene-Setter
            EditorStateService.Instance.SetLastProject(project.Id, project.Path);

            // Version the project with Git: init repo + .gitignore/.gitattributes + LFS on first open
            // (idempotent afterwards). Fire-and-forget, guarded, never throws — no-op without git.
            _ = Editor.Core.Services.Git.GitService.Instance.EnsureRepoAsync(project.Path);
        }

        /// <summary>Re-show all editor panels in their default docked positions.</summary>
        public void ResetEditorLayout()
        {
            try { WorldEditor.ResetLayout(); } catch { }
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

        // ---- transient confirmation toast (e.g. after Ctrl+S) ----
        private System.Windows.Controls.Primitives.Popup _toastPopup;
        private System.Windows.Controls.TextBlock _toastText;
        private System.Windows.Threading.DispatcherTimer _toastTimer;

        public void ShowToast(string message)
        {
            try
            {
                if (_toastPopup == null)
                {
                    var conv = new System.Windows.Media.BrushConverter();
                    _toastText = new System.Windows.Controls.TextBlock
                    {
                        Foreground = System.Windows.Media.Brushes.White,
                        FontSize = 13, VerticalAlignment = VerticalAlignment.Center
                    };
                    var icon = new System.Windows.Controls.TextBlock
                    {
                        Text = "",
                        FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                        FontSize = 13, Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (System.Windows.Media.Brush)conv.ConvertFromString("#7CE0A3")
                    };
                    var sp = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    sp.Children.Add(icon); sp.Children.Add(_toastText);
                    var border = new System.Windows.Controls.Border
                    {
                        Background = (System.Windows.Media.Brush)conv.ConvertFromString("#F0202023"),
                        BorderBrush = (System.Windows.Media.Brush)conv.ConvertFromString("#3A3A42"),
                        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(16, 10, 16, 10), Child = sp
                    };
                    _toastPopup = new System.Windows.Controls.Primitives.Popup
                    {
                        PlacementTarget = this,
                        Placement = System.Windows.Controls.Primitives.PlacementMode.Center,
                        AllowsTransparency = true, StaysOpen = true, Child = border
                    };
                    _toastTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(1400)
                    };
                    _toastTimer.Tick += (s, ev) => { _toastTimer.Stop(); if (_toastPopup != null) _toastPopup.IsOpen = false; };
                }
                _toastText.Text = message;
                _toastPopup.VerticalOffset = Math.Max(40, ActualHeight / 2 - 60);
                _toastPopup.IsOpen = false;
                _toastPopup.IsOpen = true;
                _toastTimer.Stop(); _toastTimer.Start();
            }
            catch { }
        }

        #region Borderless maximize fix

        // With WindowStyle=None + WindowChrome, a maximized window overflows the monitor by
        // the resize border (~7px), clipping the top of the header AND covering the taskbar.
        // Hooking WM_GETMINMAXINFO constrains the maximized size to the monitor work area.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);

            // Win11 rounded window corners (matches the Aurora reference). No-op on Win10.
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, sizeof(int));
            }
            catch { /* DWM not available */ }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            const int MONITOR_DEFAULTTONEAREST = 0x00000002;
            var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(monitor, ref info))
                {
                    RECT work = info.rcWork;
                    RECT mon = info.rcMonitor;
                    mmi.ptMaxPosition.x = Math.Abs(work.left - mon.left);
                    mmi.ptMaxPosition.y = Math.Abs(work.top - mon.top);
                    mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                    mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
                    // Keep a sane minimum so the window can't be maximized to nothing.
                    mmi.ptMinTrackSize.x = 960;
                    mmi.ptMinTrackSize.y = 540;
                }
            }
            Marshal.StructureToPtr(mmi, lParam, true);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        #endregion
    }
}
