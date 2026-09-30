using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using VortexEditor.Viewport;

namespace VortexEditor.Panels
{
    public partial class ViewportPanel : UserControl
    {
        private sealed class CameraChoice { public string Label; public GameEntity Entity; public bool FpPreview; public override string ToString() => Label; }

        private bool _syncing;
        private readonly List<SecondaryViewportView> _secondaries = new List<SecondaryViewportView>();
        private int _layout = 1;
        private DispatcherTimer _cameraPoll;
        private int _lastCameraCount = -1;

        public EditorViewportSession Session => Engine.Session;
        public EngineViewport EngineView => Engine;
        public event Action<string, string> StatusChanged;

        public ViewportPanel()
        {
            InitializeComponent();
            var vs = EditorViewportService.Instance;
            vs.GridVisibilityChanged += (s, v) => Dispatcher.UIThread.Post(SyncToggles);
            vs.GizmosVisibilityChanged += (s, v) => Dispatcher.UIThread.Post(SyncToggles);
            vs.SnapToGridChanged += (s, v) => Dispatcher.UIThread.Post(SyncToggles);
            vs.CollisionVisibilityChanged += (s, v) => Dispatcher.UIThread.Post(SyncToggles);
            TransformGizmoService.Instance.ModeChanged += (s, m) => Dispatcher.UIThread.Post(SyncTools);
            PlayModeService.Instance.StateChanged += (s, st) => Dispatcher.UIThread.Post(SyncPlayState);
            PlayModeService.Instance.GameViewChanged += a => Dispatcher.UIThread.Post(SyncPlayState);
            Engine.Session.FlyModeChanged += fly => Dispatcher.UIThread.Post(() => FlyBadge.IsVisible = fly);
            Engine.Session.StatusUpdated += (st, res) => Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(st, res));
            EditorSession.Instance.ProjectOpened += _ => Dispatcher.UIThread.Post(RefreshCameraList);
            SceneService.Instance.SceneLoaded += (s, sc) => Dispatcher.UIThread.Post(RefreshCameraList);
            SyncToggles(); SyncTools(); SyncPlayState();
            RefreshCameraList();
            _cameraPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _cameraPoll.Tick += (s, e) => { int n = CountCameras(); if (n != _lastCameraCount) RefreshCameraList(); };
            _cameraPoll.Start();
        }

        // ------------------------------------------------------------ toggles
        private void SyncToggles()
        {
            _syncing = true;
            var vs = EditorViewportService.Instance;
            GridToggle.IsChecked = vs.IsGridVisible;
            SnapToggle.IsChecked = vs.SnapToGrid;
            GizmoToggle.IsChecked = vs.AreGizmosVisible;
            ColliderToggle.IsChecked = vs.AreCollidersVisible;
            FpToggle.IsChecked = SceneRenderService.EditorViewmodelPreview != SceneRenderService.ViewmodelPreviewMode.Hidden;
            _syncing = false;
        }

        private void SyncTools()
        {
            _syncing = true;
            switch (TransformGizmoService.Instance.CurrentMode)
            {
                case TransformGizmoService.GizmoMode.Translate: ToolMove.IsChecked = true; break;
                case TransformGizmoService.GizmoMode.Rotate: ToolRotate.IsChecked = true; break;
                case TransformGizmoService.GizmoMode.Scale: ToolScale.IsChecked = true; break;
            }
            bool local = TransformGizmoService.Instance.CurrentSpace == TransformGizmoService.GizmoSpace.Local;
            SpaceToggle.IsChecked = local;
            SpaceText.Text = local ? "Local" : "World";
            _syncing = false;
        }

        private void SyncPlayState()
        {
            var pms = PlayModeService.Instance;
            bool playing = pms.IsPlaying;
            PlayBadge.IsVisible = playing && !pms.IsExternalWindow;
            ExternalBadge.IsVisible = playing && pms.IsExternalWindow;
            Toolbar.IsVisible = !playing;
            GamePlaceholder.IsVisible = pms.IsGameView && !playing;
        }

        private void OnToolMove(object s, RoutedEventArgs e) { if (!_syncing) TransformGizmoService.Instance.SetTranslateMode(); }
        private void OnToolRotate(object s, RoutedEventArgs e) { if (!_syncing) TransformGizmoService.Instance.SetRotateMode(); }
        private void OnToolScale(object s, RoutedEventArgs e) { if (!_syncing) TransformGizmoService.Instance.SetScaleMode(); }
        private void OnSpaceToggle(object s, RoutedEventArgs e) { if (!_syncing) { TransformGizmoService.Instance.ToggleSpace(); SyncTools(); } }
        private void OnGridToggle(object s, RoutedEventArgs e) { if (!_syncing) EditorViewportService.Instance.IsGridVisible = GridToggle.IsChecked == true; }
        private void OnSnapToggle(object s, RoutedEventArgs e) { if (!_syncing) { EditorViewportService.Instance.SnapToGrid = SnapToggle.IsChecked == true; TransformGizmoService.Instance.SnapEnabled = SnapToggle.IsChecked == true; } }
        private void OnGizmoToggle(object s, RoutedEventArgs e) { if (!_syncing) EditorViewportService.Instance.AreGizmosVisible = GizmoToggle.IsChecked == true; }
        private void OnColliderToggle(object s, RoutedEventArgs e) { if (!_syncing) { EditorViewportService.Instance.AreCollidersVisible = ColliderToggle.IsChecked == true; SceneRenderService.RuntimeDirty = true; } }
        private void OnColliderPressed(object s, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
            var m = new MenuFlyout();
            var all = new MenuItem { Header = "Show all colliders (not only the selection)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = EditorViewportService.Instance.ShowAllColliders };
            all.Click += (a, b) => { EditorViewportService.Instance.ShowAllColliders = !EditorViewportService.Instance.ShowAllColliders; SceneRenderService.RuntimeDirty = true; };
            m.Items.Add(all);
            m.ShowAt(ColliderToggle);
            e.Handled = true;
        }
        private void OnFpToggle(object s, RoutedEventArgs e)
        {
            if (_syncing) return;
            var m = new MenuFlyout();
            foreach (var mode in new[] { SceneRenderService.ViewmodelPreviewMode.Hidden, SceneRenderService.ViewmodelPreviewMode.AsWorld, SceneRenderService.ViewmodelPreviewMode.GameView })
            {
                var mm = mode;
                var mi = new MenuItem { Header = mode == SceneRenderService.ViewmodelPreviewMode.Hidden ? "Hide first-person layer" : mode == SceneRenderService.ViewmodelPreviewMode.AsWorld ? "Show in world" : "Game view (as the player sees it)", ToggleType = MenuItemToggleType.Radio, IsChecked = SceneRenderService.EditorViewmodelPreview == mode };
                mi.Click += (a, b) => { SceneRenderService.EditorViewmodelPreview = mm; SceneRenderService.RuntimeDirty = true; SyncToggles(); };
                m.Items.Add(mi);
            }
            m.ShowAt(FpToggle);
            SyncToggles();
        }

        // ------------------------------------------------------------ cameras
        private int CountCameras()
        {
            var scene = ProjectData.Current?.ActiveScene; int n = 0;
            if (scene?.Entities != null) foreach (var e in scene.Entities) n += Count(e);
            return n;
        }
        private static int Count(GameEntity e) { int n = e.GetComponent<Camera>() != null ? 1 : 0; if (e.Children != null) foreach (var c in e.Children) n += Count(c); return n; }

        public void RefreshCameraList()
        {
            _syncing = true;
            var items = new List<CameraChoice> { new CameraChoice { Label = "Free Camera" } };
            var scene = ProjectData.Current?.ActiveScene;
            if (scene?.Entities != null) foreach (var e in scene.Entities) AddCameras(e, items);
            items.Add(new CameraChoice { Label = "FP Preview (in-game)", FpPreview = true });
            CameraSelector.ItemsSource = items;
            CameraSelector.SelectedIndex = 0;
            _lastCameraCount = CountCameras();
            _syncing = false;
        }
        private static void AddCameras(GameEntity e, List<CameraChoice> items)
        {
            var cam = e.GetComponent<Camera>();
            if (cam != null) items.Add(new CameraChoice { Label = (cam.IsMainCamera ? "★ " : "") + e.Name, Entity = e });
            if (e.Children != null) foreach (var c in e.Children) AddCameras(c, items);
        }
        private void OnCameraSelected(object s, SelectionChangedEventArgs e)
        {
            if (_syncing || !(CameraSelector.SelectedItem is CameraChoice ch)) return;
            if (ch.FpPreview)
            {
                Session.ViewThroughCamera(null);
                SceneRenderService.EditorViewmodelPreview = SceneRenderService.ViewmodelPreviewMode.GameView;
                PlayCameraHelper.ApplyMainCamera(ProjectData.Current?.ActiveScene);
                SceneRenderService.RuntimeDirty = true;
                SyncToggles();
                return;
            }
            Session.ViewThroughCamera(ch.Entity);
        }
        private void OnPipToggle(object s, RoutedEventArgs e)
        {
            var cam = CameraSelector.SelectedItem is CameraChoice ch && ch.Entity != null ? ch.Entity : FindFirstCamera();
            if (cam == null) { EditorCommands_Toast("No camera in the scene"); PipToggle.IsChecked = false; return; }
            if (PipToggle.IsChecked == true) CameraPreviewService.Instance.ShowPreview(cam); else CameraPreviewService.Instance.ClosePreview();
        }
        private GameEntity FindFirstCamera()
        {
            var scene = ProjectData.Current?.ActiveScene; if (scene?.Entities == null) return null;
            foreach (var e in scene.Entities) { var r = Find(e); if (r != null) return r; }
            return null;
        }
        private static GameEntity Find(GameEntity e) { if (e.GetComponent<Camera>() != null) return e; if (e.Children != null) foreach (var c in e.Children) { var r = Find(c); if (r != null) return r; } return null; }
        private static void EditorCommands_Toast(string m) => Shell.EditorCommands.Toast(m);

        // ------------------------------------------------------------ layout (secondary camera views)
        private void OnLayoutClick(object s, RoutedEventArgs e)
        {
            if (!(s is RadioButton rb) || !int.TryParse(rb.Tag as string, out int layout)) return;
            SetLayout(layout);
        }

        public void SetLayout(int layout)
        {
            _layout = layout;
            bool two = layout >= 2, quad = layout == 4;
            bool vertical = layout == 2 || quad, horizontal = layout == 3 || quad;
            ViewGrid.ColumnDefinitions[1].Width = vertical ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ViewGrid.RowDefinitions[1].Height = horizontal ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Cell2.IsVisible = vertical; Cell3.IsVisible = horizontal; Cell4.IsVisible = quad;
            Grid.SetColumnSpan(MainCell, vertical ? 1 : 2);
            Grid.SetRowSpan(MainCell, horizontal ? 1 : 2);
            Grid.SetColumnSpan(Cell3, quad ? 1 : 2);
            EnsureSecondary(Secondary1, 0, vertical);
            EnsureSecondary(Secondary2, 1, horizontal);
            EnsureSecondary(Secondary3, 2, quad);
            // Secondary panes render through readback while the main view keeps its swapchain: cheap when idle.
            Engine.Session.SecondaryViewsActive = two;
        }

        private void EnsureSecondary(ContentControl host, int index, bool wanted)
        {
            var existing = host.Content as SecondaryViewportView;
            if (wanted && existing == null) { var v = new SecondaryViewportView(index); host.Content = v; _secondaries.Add(v); }
            else if (!wanted && existing != null) { existing.Shutdown(); host.Content = null; _secondaries.Remove(existing); }
        }
    }
}
