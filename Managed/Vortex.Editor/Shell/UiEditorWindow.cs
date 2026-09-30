using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.UI.Vui;
using VortexEditor.Controls;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Retained-UI screen editor (.vui): element tree, per-kind properties, add / remove / reorder, and a live
    /// preview drawn by the engine in the main viewport (Preview toggle) — exactly what the game will render.
    /// </summary>
    public sealed class UiEditorWindow : Window
    {
        /// <summary>Open this window (owned by the main window).</summary>
        public static void Open(string fullPath) => EditorWindows.Show(new UiEditorWindow(fullPath));

        private readonly string _path;
        private VuiCanvas _canvas;
        private readonly TreeView _tree = new TreeView();
        private readonly StackPanel _props = new StackPanel { Spacing = 2 };
        private readonly ToggleButton _preview = new ToggleButton { Content = "Preview in viewport" };
        private VuiElement _selected;
        private bool _dirty;

        public UiEditorWindow(string path)
        {
            _path = path;
            Title = "UI Screen — " + Path.GetFileNameWithoutExtension(path); Width = 900; Height = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            try { _canvas = VuiCanvas.Load(path); } catch (Exception ex) { EditorCommands.Fail("Open UI", ex); }
            if (_canvas == null || _canvas.Root == null)
            {
                _canvas = new VuiCanvas { Name = path, Root = new VuiElement { Kind = VuiKind.Panel, Id = "root", StretchX = true, StretchY = true, Bg = new[] { 0.06f, 0.06f, 0.08f, 1f }, BlocksInput = true } };
            }
            var root = new DockPanel();
            var toolbar = new DockPanel { Height = 40, Margin = new Thickness(12, 8, 12, 0) };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var addBtn = new Button { Content = "Add element" }; addBtn.Click += (s, e) => ShowAddMenu(addBtn);
            var del = new Button { Content = "Remove" }; del.Click += (s, e) => RemoveSelected();
            var up = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "ChevronLeft" } }; ToolTip.SetTip(up, "Move up"); up.Click += (s, e) => Move(-1);
            var down = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "ChevronRight" } }; ToolTip.SetTip(down, "Move down"); down.Click += (s, e) => Move(1);
            left.Children.Add(addBtn); left.Children.Add(del); left.Children.Add(up); left.Children.Add(down);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            _preview.IsCheckedChanged += (s, e) => TogglePreview();
            var save = new Button { Content = "Save", Classes = { "accent" }, MinWidth = 90 }; save.Click += (s, e) => Save();
            right.Children.Add(_preview); right.Children.Add(save);
            DockPanel.SetDock(right, Dock.Right); toolbar.Children.Add(right); toolbar.Children.Add(left);
            DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,5,*"), Margin = new Thickness(12, 8, 12, 12) };
            var treeCard = new Border { Classes = { "card" }, Padding = new Thickness(4) };
            _tree.SelectionChanged += (s, e) => Select(_tree.SelectedItem as VuiElement);
            _tree.DataTemplates.Add(new Avalonia.Controls.Templates.FuncTreeDataTemplate<VuiElement>((el, _) =>
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Height = 24 };
                sp.Children.Add(new VxIcon { Icon = IconFor(el.Kind), Foreground = (IBrush)Application.Current.FindResource("VxAccentBrush") });
                sp.Children.Add(new TextBlock { Text = (string.IsNullOrEmpty(el.Id) ? el.Kind.ToString() : el.Id) + (string.IsNullOrEmpty(el.Text) ? "" : "  “" + el.Text + "”"), VerticalAlignment = VerticalAlignment.Center });
                return sp;
            }, el => el.Children));
            treeCard.Child = _tree;
            grid.Children.Add(treeCard);
            var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(split, 1); grid.Children.Add(split);
            var propCard = new Border { Classes = { "card" }, Padding = new Thickness(10) };
            propCard.Child = new ScrollViewer { Content = _props };
            Grid.SetColumn(propCard, 2); grid.Children.Add(propCard);
            root.Children.Add(grid);
            Content = root;
            RebuildTree();
            Closing += (s, e) => { if (_preview.IsChecked == true) { _preview.IsChecked = false; TogglePreview(); } };
        }

        private static string IconFor(VuiKind k)
        {
            switch (k) { case VuiKind.Text: return "File"; case VuiKind.Image: return "Image"; case VuiKind.Button: return "Check"; case VuiKind.Bar: case VuiKind.Slider: return "Minus"; case VuiKind.Crosshair: return "Crosshair"; case VuiKind.List: return "Hierarchy"; default: return "LayoutSingle"; }
        }

        private void RebuildTree()
        {
            var sel = _selected;
            _tree.ItemsSource = new List<VuiElement> { _canvas.Root };
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { ExpandAll(_canvas.Root); if (sel != null) _tree.SelectedItem = sel; }, Avalonia.Threading.DispatcherPriority.Background);
        }

        private void ExpandAll(VuiElement e)
        {
            try { if (_tree.TreeContainerFromItem(e) is TreeViewItem c) c.IsExpanded = true; } catch { }
            foreach (var ch in e.Children) ExpandAll(ch);
        }

        private void ShowAddMenu(Control anchor)
        {
            var m = new MenuFlyout();
            foreach (VuiKind k in System.Enum.GetValues(typeof(VuiKind)))
            {
                var kk = k;
                var mi = new MenuItem { Header = k.ToString() };
                mi.Click += (s, e) => Add(kk);
                m.Items.Add(mi);
            }
            m.ShowAt(anchor);
        }

        private void Add(VuiKind kind)
        {
            var parent = _selected != null && _selected.Kind == VuiKind.Panel ? _selected : (_selected?.Parent ?? _canvas.Root);
            var el = new VuiElement { Kind = kind, Id = kind.ToString().ToLowerInvariant() + (CountAll(_canvas.Root) + 1), Parent = parent, W = 240, H = kind == VuiKind.Text ? 32 : 48, Anchor = AnchorEnum.Center };
            switch (kind)
            {
                case VuiKind.Text: el.Text = "Text"; el.FontSize = 20; break;
                case VuiKind.Button: el.Text = "Button"; el.Bg = new[] { 0.2f, 0.2f, 0.24f, 1f }; el.FontSize = 18; el.Align = 1; break;
                case VuiKind.Panel: el.Bg = new[] { 0.1f, 0.1f, 0.12f, 0.8f }; el.W = 400; el.H = 300; break;
                case VuiKind.Bar: el.Bg = new[] { 0.15f, 0.15f, 0.18f, 1f }; el.Fg = new[] { 0.3f, 0.85f, 0.4f, 1f }; el.Value = 0.7f; el.H = 16; break;
                case VuiKind.Slider: el.Bg = new[] { 0.15f, 0.15f, 0.18f, 1f }; el.Value = 0.5f; el.H = 20; break;
                case VuiKind.Toggle: el.Text = "Option"; el.H = 32; break;
                case VuiKind.Stepper: el.Options = new[] { "Low", "Medium", "High" }; el.H = 32; break;
                case VuiKind.TextField: el.Text = ""; el.Bg = new[] { 0.1f, 0.1f, 0.12f, 1f }; el.H = 36; break;
                case VuiKind.Crosshair: el.W = 24; el.H = 24; break;
                case VuiKind.Image: el.W = 128; el.H = 128; break;
            }
            parent.Children.Add(el);
            _selected = el; _dirty = true;
            RebuildTree();
            Preview();
        }

        private static int CountAll(VuiElement e) { int n = 1; foreach (var c in e.Children) n += CountAll(c); return n; }

        private void RemoveSelected()
        {
            if (_selected == null || _selected.Parent == null) return;
            var p = _selected.Parent; p.Children.Remove(_selected); _selected = p; _dirty = true;
            RebuildTree(); Preview();
        }

        private void Move(int dir)
        {
            var p = _selected?.Parent; if (p == null) return;
            int i = p.Children.IndexOf(_selected), j = i + dir;
            if (i < 0 || j < 0 || j >= p.Children.Count) return;
            p.Children[i] = p.Children[j]; p.Children[j] = _selected; _dirty = true;
            RebuildTree(); Preview();
        }

        private void Select(VuiElement el)
        {
            _selected = el;
            ClearRefreshers();
            _props.Children.Clear();
            if (el == null) return;
            void Changed() { _dirty = true; Preview(); }
            _props.Children.Add(new TextBlock { Text = el.Kind + " properties", Classes = { "section" }, Margin = new Thickness(0, 0, 0, 6) });
            _props.Children.Add(Row("Id", Text(() => el.Id, v => { el.Id = v; Changed(); RebuildTree(); }, "unique id (scripts use it)")));
            _props.Children.Add(Row("Anchor", Enum<AnchorEnum>(() => el.Anchor, v => { el.Anchor = v; Changed(); })));
            _props.Children.Add(Row("Offset", Pair(() => el.OffX, v => { el.OffX = v; Changed(); }, () => el.OffY, v => { el.OffY = v; Changed(); })));
            _props.Children.Add(Row("Offset %", Pair(() => el.PctX, v => { el.PctX = v; Changed(); }, () => el.PctY, v => { el.PctY = v; Changed(); })));
            _props.Children.Add(Row("Size", Pair(() => el.W, v => { el.W = v; Changed(); }, () => el.H, v => { el.H = v; Changed(); })));
            _props.Children.Add(Row("Size %", Pair(() => el.WPct, v => { el.WPct = v; Changed(); }, () => el.HPct, v => { el.HPct = v; Changed(); })));
            _props.Children.Add(Row("Stretch", Pair2(() => el.StretchX, v => { el.StretchX = v; Changed(); }, () => el.StretchY, v => { el.StretchY = v; Changed(); })));
            _props.Children.Add(Row("Visible", Bool(() => el.Visible, v => { el.Visible = v; Changed(); })));
            _props.Children.Add(Row("Opacity", SliderRow(() => el.Opacity, v => { el.Opacity = v; Changed(); }, 0, 1)));
            _props.Children.Add(Row("Background", Rgba(() => el.Bg, v => { el.Bg = v; Changed(); })));
            _props.Children.Add(Row("Foreground", Rgba(() => el.Fg, v => { el.Fg = v; Changed(); })));
            _props.Children.Add(Row("Corner radius", FloatBox(() => el.Radius, v => { el.Radius = v; Changed(); }, 1, 0)));
            if (el.Kind == VuiKind.Text || el.Kind == VuiKind.Button || el.Kind == VuiKind.Toggle || el.Kind == VuiKind.TextField || el.Kind == VuiKind.Stepper)
            {
                _props.Children.Add(Row("Text", Text(() => el.Text, v => { el.Text = v; Changed(); RebuildTree(); })));
                _props.Children.Add(Row("Font size", FloatBox(() => el.FontSize, v => { el.FontSize = v; Changed(); }, 1, 6)));
                _props.Children.Add(Row("Align", Choice(() => el.Align, v => { el.Align = v; Changed(); }, "Left", "Center", "Right")));
                _props.Children.Add(Row("Weight", Choice(() => el.Weight >= 700 ? 2 : el.Weight >= 600 ? 1 : 0, v => { el.Weight = v == 2 ? 700 : v == 1 ? 600 : 400; Changed(); }, "Regular", "Semibold", "Bold")));
            }
            if (el.Kind == VuiKind.Image) _props.Children.Add(Row("Image", AssetPath(() => el.ImageAsset, v => { el.ImageAsset = v; Changed(); }, "Texture", new[] { "*.png", "*.jpg", "*.jpeg", "*.tga" }, () => AssetPickerDialog.Pick("Textures", new[] { "*.png", "*.jpg", "*.jpeg", "*.tga" }))));
            if (el.Kind == VuiKind.Bar || el.Kind == VuiKind.Slider)
            {
                _props.Children.Add(Row("Value", SliderRow(() => el.Value, v => { el.Value = v; Changed(); }, 0, 1)));
                _props.Children.Add(Row("Range", Pair(() => el.Min, v => { el.Min = v; Changed(); }, () => el.Max, v => { el.Max = v; Changed(); })));
            }
            if (el.Kind == VuiKind.Button)
            {
                _props.Children.Add(Row("Click action", Text(() => el.ClickAction, v => { el.ClickAction = v; Changed(); }, "C# method name")));
                _props.Children.Add(Row("Hover tint", Rgba(() => el.HoverTint ?? new[] { 1f, 1f, 1f, 0.15f }, v => { el.HoverTint = v; Changed(); })));
                _props.Children.Add(Row("Captures key", Bool(() => el.CapturesKey, v => { el.CapturesKey = v; Changed(); })));
            }
            if (el.Kind == VuiKind.Toggle) _props.Children.Add(Row("On", Bool(() => el.On, v => { el.On = v; Changed(); })));
            if (el.Kind == VuiKind.Stepper) _props.Children.Add(Row("Options", Text(() => string.Join(", ", el.Options ?? Array.Empty<string>()), v => { el.Options = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(); Changed(); }, "comma separated")));
            if (el.Kind == VuiKind.TextField) _props.Children.Add(Row("Max chars", IntBox(() => el.MaxChars, v => { el.MaxChars = v; Changed(); }, 1, 4096)));
            if (el.Kind == VuiKind.Panel || el.Kind == VuiKind.List)
            {
                _props.Children.Add(Row("Layout", Enum<StackDir>(() => el.LayoutMode, v => { el.LayoutMode = v; Changed(); })));
                _props.Children.Add(Row("Spacing / pad", Pair(() => el.Spacing, v => { el.Spacing = v; Changed(); }, () => el.Padding, v => { el.Padding = v; Changed(); })));
                _props.Children.Add(Row("Grid columns", IntBox(() => el.GridCols, v => { el.GridCols = v; Changed(); }, 1, 32)));
                _props.Children.Add(Row("Clip children", Bool(() => el.ClipChildren, v => { el.ClipChildren = v; Changed(); })));
            }
            _props.Children.Add(Row("Tooltip", Text(() => el.Tooltip, v => { el.Tooltip = v; Changed(); })));
            _props.Children.Add(Row("Target setting", Text(() => el.TargetSetting, v => { el.TargetSetting = v; Changed(); }, "e.g. Camera.Fov")));
            _props.Children.Add(Row("Click sound", AssetPath(() => el.ClickSound, v => { el.ClickSound = v; Changed(); }, "Audio", new[] { "*.wav", "*.mp3", "*.ogg", "*.vsndc" }, () => AssetPickerDialog.Pick("Audio", new[] { "*.wav", "*.mp3", "*.ogg", "*.vsndc" }))));
            if (el.Parent == null)
            {
                _props.Children.Add(new TextBlock { Text = "Screen", Classes = { "section" } });
                _props.Children.Add(Row("Blocks input", Bool(() => el.BlocksInput, v => { el.BlocksInput = v; Changed(); }), "A menu that owns the mouse"));
                _props.Children.Add(Row("Cursor locked", Bool(() => el.CursorLocked, v => { el.CursorLocked = v; Changed(); }), "HUD: keep mouse-look"));
                _props.Children.Add(Row("Blocks gameplay", Bool(() => el.BlocksGameplay, v => { el.BlocksGameplay = v; Changed(); })));
                _props.Children.Add(Row("Design size", Pair(() => _canvas.DesignW, v => { _canvas.DesignW = (int)v; Changed(); }, () => _canvas.DesignH, v => { _canvas.DesignH = (int)v; Changed(); })));
            }
        }

        private static Control Pair(Func<float> gx, Action<float> sx, Func<float> gy, Action<float> sy)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sp.Children.Add(FloatBox(gx, sx, 1)); sp.Children.Add(FloatBox(gy, sy, 1));
            return sp;
        }
        private static Control Pair2(Func<bool> gx, Action<bool> sx, Func<bool> gy, Action<bool> sy)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var a = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }; a.Children.Add(Bool(gx, sx)); a.Children.Add(new TextBlock { Text = "X", Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center });
            var b = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }; b.Children.Add(Bool(gy, sy)); b.Children.Add(new TextBlock { Text = "Y", Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(a); sp.Children.Add(b);
            return sp;
        }
        private static Control Rgba(Func<float[]> get, Action<float[]> set)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            float[] cur() { var v = get(); return v != null && v.Length >= 4 ? v : new[] { 0f, 0f, 0f, 0f }; }
            var picker = new ColorPicker { Width = 110, Height = 22, IsAlphaEnabled = true, Color = Avalonia.Media.Color.FromArgb(B(cur()[3]), B(cur()[0]), B(cur()[1]), B(cur()[2])) };
            picker.ColorChanged += (s, e) => set(new[] { e.NewColor.R / 255f, e.NewColor.G / 255f, e.NewColor.B / 255f, e.NewColor.A / 255f });
            sp.Children.Add(picker);
            return sp;
        }

        // ---- preview through the engine (main viewport draws the canvas every frame while enabled) ----
        private void TogglePreview()
        {
            var session = EditorViewportSession.Main;
            if (session == null) { _preview.IsChecked = false; EditorCommands.Toast("Open a project with a viewport first"); return; }
            session.PreviewCanvas = _preview.IsChecked == true ? _canvas : null;
            SceneRenderService.RuntimeDirty = true;
        }
        private void Preview() { var session = EditorViewportSession.Main; if (session != null && _preview.IsChecked == true) { try { _canvas.Reindex(); } catch { } session.PreviewCanvas = _canvas; } }

        private void Save()
        {
            try { VuiDocument.Save(_canvas, _path); _dirty = false; EditorCommands.Toast("UI screen saved"); }
            catch (Exception ex) { EditorCommands.Fail("Save UI", ex); }
        }
    }
}
