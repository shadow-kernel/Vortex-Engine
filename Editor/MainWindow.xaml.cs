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
            Activated += OnEditorActivated;
        }

        /// <summary>Editor-side hot-reload on focus (Alt-Tab back from VS): recompile changed material shaders (scene
        /// viewport + Asset Browser thumbnails), AND — while the game is playing IN THE VIEWPORT — recompile + re-run
        /// changed gameplay scripts. The blocking GameHost loop already does this for the external/standalone window;
        /// this wires the same live hot-reload for the editor's own play modes ("world build" + viewport play), all
        /// from the SAME project source you edit. Cheap dirty-checks so re-focusing with no edits does nothing.</summary>
        private void OnEditorActivated(object sender, EventArgs e)
        {
            try
            {
                if (ProjectData.Current == null) return;

                // (1) Shader hot-reload — edit mode AND viewport play (the scene viewport + thumbnails show it).
                if (DllWrapper.VortexAPI.AnyMaterialShaderDirty())
                {
                    int n = DllWrapper.VortexAPI.ReloadMaterialShaders();
                    if (n > 0)
                    {
                        Editor.Editors.WorldEditor.Components.AssetBrowser.AssetBrowserView.InvalidateMaterialThumbnails();
                        try { Editor.Editors.WorldEditor.Components.FileExplorer.Services.FileExplorerService.Instance.RefreshCurrentFolderContents(); } catch { }
                        ShowToast(n == 1 ? "1 shader hot-reloaded" : n + " shaders hot-reloaded");
                    }
                }

                // (2) SCRIPT hot-reload while playing IN THE VIEWPORT (▶). The external/standalone window handles this
                //     in the blocking GameHost loop; the viewport play runs on the editor tick, so wire it here.
                var sr = Editor.Scripting.ScriptRuntime.Instance;
                var pms = Editor.Core.Services.PlayModeService.Instance;
                if (pms.State == Editor.Core.Services.PlayState.Playing && !pms.IsExternalWindow && sr.ScriptsChanged())
                {
                    sr.ReloadScripts();
                    if (sr.LastReloadOutcome == Editor.Scripting.ScriptRuntime.ReloadOutcome.Reloaded)
                        ShowToast("Scripts hot-reloaded — " + sr.LastReloadSummary);
                    else if (sr.LastReloadOutcome == Editor.Scripting.ScriptRuntime.ReloadOutcome.CompileError)
                        ShowToast("Hot-reload failed: " + sr.LastReloadError);
                }
            }
            catch { }
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

        // Persistent "you are editing a prefab" banner (see ShowEditBanner) — a top-docked strip with a title and up
        // to two action buttons. Unlike the toast it stays open until HideEditBanner() is called.
        private System.Windows.Controls.Primitives.Popup _bannerPopup;
        private System.Windows.Controls.TextBlock _bannerText;
        private System.Windows.Controls.StackPanel _bannerButtons;

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

        /// <summary>Show a persistent top-docked banner with a title and up to two action buttons. Used by the Prefab
        /// Edit Session so the user always sees they are editing a prefab and has an explicit Save / Cancel — the fix
        /// for "Edit just added an object and I didn't know how to save it". Call <see cref="HideEditBanner"/> to close.
        /// primary/secondary actions run on the UI thread; the banner auto-hides after either fires.</summary>
        public void ShowEditBanner(string message, string primaryLabel, Action primaryAction,
                                   string secondaryLabel = null, Action secondaryAction = null)
        {
            try
            {
                var conv = new System.Windows.Media.BrushConverter();
                if (_bannerPopup == null)
                {
                    var icon = new System.Windows.Controls.TextBlock
                    {
                        Text = "", // Edit glyph
                        FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                        FontSize = 14, Margin = new Thickness(0, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (System.Windows.Media.Brush)conv.ConvertFromString("#9C8CFF")
                    };
                    _bannerText = new System.Windows.Controls.TextBlock
                    {
                        Foreground = System.Windows.Media.Brushes.White, FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    _bannerButtons = new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
                    };
                    var sp = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    sp.Children.Add(icon); sp.Children.Add(_bannerText); sp.Children.Add(_bannerButtons);
                    var border = new System.Windows.Controls.Border
                    {
                        Background = (System.Windows.Media.Brush)conv.ConvertFromString("#F02A2440"),
                        BorderBrush = (System.Windows.Media.Brush)conv.ConvertFromString("#5A4F9C"),
                        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(16, 9, 12, 9), Child = sp
                    };
                    _bannerPopup = new System.Windows.Controls.Primitives.Popup
                    {
                        PlacementTarget = this,
                        Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
                        AllowsTransparency = true, StaysOpen = true, Child = border
                    };
                }
                _bannerText.Text = message;
                _bannerButtons.Children.Clear();
                _bannerButtons.Children.Add(MakeBannerButton(primaryLabel, "#5A4F9C", "#7C6CFF", () => { HideEditBanner(); primaryAction?.Invoke(); }));
                if (!string.IsNullOrEmpty(secondaryLabel))
                    _bannerButtons.Children.Add(MakeBannerButton(secondaryLabel, "#33333A", "#4A4A55", () => { HideEditBanner(); secondaryAction?.Invoke(); }));
                // Dock just below the header bar, centred horizontally over the window.
                _bannerPopup.HorizontalOffset = 0;
                _bannerPopup.VerticalOffset = -(ActualHeight - 96);
                _bannerPopup.IsOpen = false;
                _bannerPopup.IsOpen = true;
            }
            catch { }
        }

        /// <summary>Hide the persistent edit banner if it is showing.</summary>
        public void HideEditBanner()
        {
            try { if (_bannerPopup != null) _bannerPopup.IsOpen = false; } catch { }
        }

        private System.Windows.Controls.Button MakeBannerButton(string label, string bg, string hover, Action onClick)
        {
            var conv = new System.Windows.Media.BrushConverter();
            var btn = new System.Windows.Controls.Button
            {
                Content = label, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5),
                Foreground = System.Windows.Media.Brushes.White, FontSize = 12, Cursor = Cursors.Hand,
                Background = (System.Windows.Media.Brush)conv.ConvertFromString(bg),
                BorderThickness = new Thickness(0)
            };
            // A minimal rounded template so the button reads as a pill, not the default chrome.
            var tpl = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button));
            var bd = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            bd.SetValue(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(12, 5, 12, 5));
            var cp = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            cp.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;
            btn.Click += (s, e) => { try { onClick?.Invoke(); } catch { } };
            return btn;
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
