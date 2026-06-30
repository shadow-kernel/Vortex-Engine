using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Editor.UI.Vui;

namespace Editor.Editors.UIEditor
{
    /// <summary>
    /// The visual UI builder (a peer of the Material/Mesh editors). Edits a .vui screen with a live preview that
    /// runs the IDENTICAL runtime layout (VuiPreviewRenderer) — so what you build is exactly what ships. Left:
    /// palette + hierarchy; center: preview + resolution switcher; right: property inspector + 3x3 anchor picker.
    /// </summary>
    public sealed class UIEditorWindow : Window
    {
        private static readonly Color C_Bg = Color.FromRgb(22, 22, 24);
        private static readonly Color C_Panel = Color.FromRgb(32, 32, 35);
        private static readonly Color C_PanelAlt = Color.FromRgb(42, 42, 46);
        private static readonly Color C_Border = Color.FromRgb(58, 58, 62);
        private static readonly Color C_Text = Color.FromRgb(245, 245, 247);
        private static readonly Color C_TextSec = Color.FromRgb(152, 152, 159);
        private static readonly Color C_Accent = Color.FromRgb(108, 92, 231);

        private readonly string _path;
        private VuiCanvas _canvas;
        private VuiElement _selected;
        private bool _dirty;

        private Canvas _preview;
        private TreeView _tree;
        private StackPanel _inspector;
        private int _prevW = 1920, _prevH = 1080;

        public static void Open(Window owner, string path)
        {
            var w = new UIEditorWindow(path) { Owner = owner };
            w.ShowDialog();
        }

        public UIEditorWindow(string path)
        {
            _path = path;
            _canvas = VuiDocument.Load(path);
            if (_canvas == null)
            {
                _canvas = new VuiCanvas { DesignW = 1920, DesignH = 1080, Name = path };
                _canvas.Root = new VuiElement { Kind = VuiKind.Panel, Id = "root", StretchX = true, StretchY = true, Bg = new float[] { 0.05f, 0.05f, 0.07f, 1f }, BlocksInput = true };
                _canvas.Reindex();
            }
            _prevW = _canvas.DesignW; _prevH = _canvas.DesignH;
            _selected = _canvas.Root;

            Title = "UI Editor — " + System.IO.Path.GetFileName(path);
            Width = 1480; Height = 920; MinWidth = 1100; MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(C_Bg);
            BuildUI();
            RefreshHierarchy();
            RefreshInspector();
            RefreshPreview();
        }

        // ===================== shell =====================
        private void BuildUI()
        {
            var grid = new Grid { Margin = new Thickness(8) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // toolbar
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

            // toolbar
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 8) };
            Grid.SetRow(bar, 0); Grid.SetColumnSpan(bar, 3); grid.Children.Add(bar);
            var resCombo = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var r in new[] { "1920 x 1080", "1280 x 720", "2560 x 1440", "3440 x 1440", "1024 x 768" }) resCombo.Items.Add(r);
            resCombo.SelectedIndex = 0;
            resCombo.SelectionChanged += (s, e) =>
            {
                int[][] res = { new[] { 1920, 1080 }, new[] { 1280, 720 }, new[] { 2560, 1440 }, new[] { 3440, 1440 }, new[] { 1024, 768 } };
                int i = resCombo.SelectedIndex; if (i < 0) i = 0;
                _prevW = res[i][0]; _prevH = res[i][1]; RefreshPreview();
            };
            bar.Children.Add(new TextBlock { Text = "Preview:", Foreground = new SolidColorBrush(C_TextSec), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            bar.Children.Add(resCombo);
            bar.Children.Add(MakeButton("Save", C_Accent, 90, (s, e) => Save()));
            bar.Children.Add(MakeButton("Delete element", C_PanelAlt, 130, (s, e) => DeleteSelected()));

            // LEFT: palette + hierarchy
            var left = new Grid { Margin = new Thickness(0, 0, 6, 0) }; Grid.SetRow(left, 1); Grid.SetColumn(left, 0);
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // palette — a clearly-labelled box of "+ Kind" buttons that ADD INTO the selected element
            var palStack = new StackPanel();
            palStack.Children.Add(new TextBlock { Text = "➕  ADD ELEMENT", Foreground = new SolidColorBrush(C_Accent), FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(2, 0, 0, 2) });
            palStack.Children.Add(new TextBlock { Text = "click to add into the selected element", Foreground = new SolidColorBrush(C_TextSec), FontSize = 10, Margin = new Thickness(2, 0, 0, 6) });
            var palette = new WrapPanel();
            palStack.Children.Add(palette);
            foreach (VuiKind k in Enum.GetValues(typeof(VuiKind)))
            {
                var kk = k;
                palette.Children.Add(MakeButton("＋ " + k.ToString(), C_PanelAlt, 86, (s, e) => AddChild(kk)));
            }
            var palBox = InPanel(palStack); palBox.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(palBox, 0); left.Children.Add(palBox);

            // hierarchy
            var treeBox = new Grid();
            treeBox.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            treeBox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var hdr = new TextBlock { Text = "HIERARCHY", Foreground = new SolidColorBrush(C_TextSec), FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(2, 0, 0, 4) };
            Grid.SetRow(hdr, 0); treeBox.Children.Add(hdr);
            _tree = new TreeView { Background = new SolidColorBrush(C_Panel), BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(C_Text) };
            _tree.SelectedItemChanged += (s, e) => { if (_tree.SelectedItem is TreeViewItem tvi && tvi.Tag is VuiElement el) { _selected = el; RefreshInspector(); RefreshPreview(); } };
            var treeHost = InPanel(_tree); Grid.SetRow(treeHost, 1); treeBox.Children.Add(treeHost);
            Grid.SetRow(treeBox, 1); left.Children.Add(treeBox);
            grid.Children.Add(left);

            // CENTER: preview (a Viewbox scales the design-resolution canvas to fit)
            _preview = new Canvas { Background = new SolidColorBrush(Color.FromRgb(12, 12, 14)) };
            _preview.MouseLeftButtonDown += OnPreviewDown;
            _preview.MouseMove += OnPreviewMove;
            _preview.MouseLeftButtonUp += OnPreviewUp;
            var vb = new Viewbox { Stretch = Stretch.Uniform, Child = _preview, Margin = new Thickness(6) };
            var center = new Border { Background = new SolidColorBrush(Color.FromRgb(14, 14, 16)), BorderBrush = new SolidColorBrush(C_Border), BorderThickness = new Thickness(1), Child = vb, Margin = new Thickness(8, 0, 8, 0) };
            Grid.SetRow(center, 1); Grid.SetColumn(center, 1); grid.Children.Add(center);

            // RIGHT: inspector
            _inspector = new StackPanel { Margin = new Thickness(8) };
            var sv = new ScrollViewer { Content = _inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var right = InPanel(sv); Grid.SetRow(right, 1); Grid.SetColumn(right, 2); grid.Children.Add(right);

            Content = grid;
        }

        private Border InPanel(UIElement child)
            => new Border { Background = new SolidColorBrush(C_Panel), BorderBrush = new SolidColorBrush(C_Border), BorderThickness = new Thickness(1), Padding = new Thickness(4), Child = child };

        private Button MakeButton(string text, Color bg, double w, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Width = w, Height = 28, Margin = new Thickness(2), Background = new SolidColorBrush(bg), Foreground = new SolidColorBrush(C_Text), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, FontSize = 12 };
            b.Click += click; return b;
        }

        // ===================== hierarchy =====================
        private void RefreshHierarchy()
        {
            _tree.Items.Clear();
            if (_canvas.Root != null) _tree.Items.Add(BuildTreeItem(_canvas.Root));
        }
        private TreeViewItem BuildTreeItem(VuiElement e)
        {
            string label = e.Kind + (string.IsNullOrEmpty(e.Id) ? "" : "  #" + e.Id);
            if (e.Kind == VuiKind.Button && !string.IsNullOrEmpty(e.ClickAction)) label += "  → " + e.ClickAction + "()";
            var tvi = new TreeViewItem { Header = label, Tag = e, IsExpanded = true, Foreground = new SolidColorBrush(C_Text) };
            if (ReferenceEquals(e, _selected)) tvi.IsSelected = true;
            if (e.RowTemplate != null) { var rt = BuildTreeItem(e.RowTemplate); rt.Header = "[rowTemplate] " + rt.Header; tvi.Items.Add(rt); }
            foreach (var c in e.Children) tvi.Items.Add(BuildTreeItem(c));
            return tvi;
        }

        // ===================== preview =====================
        private void RefreshPreview()
        {
            _preview.Width = _prevW; _preview.Height = _prevH;
            VuiPreviewRenderer.Render(_canvas, _preview, _prevW, _prevH, _selected);
        }

        // ---- click-to-select + drag-to-move directly on the preview canvas ----
        private VuiElement _dragEl; private System.Windows.Point _dragLast; private bool _dragging;
        private void OnPreviewDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var p = e.GetPosition(_preview);
            var hit = HitTestCanvas(_canvas != null ? _canvas.Root : null, (float)p.X, (float)p.Y);
            if (hit != null)
            {
                _selected = hit; _dragEl = hit; _dragLast = p; _dragging = true;
                _preview.CaptureMouse();
                RefreshHierarchy(); RefreshInspector(); RefreshPreview();
            }
        }
        private void OnPreviewMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragging || _dragEl == null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var p = e.GetPosition(_preview);
            float s = _canvas != null && _canvas.Scale > 0 ? _canvas.Scale : 1f;
            _dragEl.OffX += (float)(p.X - _dragLast.X) / s;
            _dragEl.OffY += (float)(p.Y - _dragLast.Y) / s;
            _dragLast = p; _dirty = true;
            RefreshPreview();
        }
        private void OnPreviewUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_dragging) { _dragging = false; _dragEl = null; _preview.ReleaseMouseCapture(); RefreshInspector(); }
        }
        // topmost non-root element under the cursor (reverse DFS = drawn-on-top first)
        private VuiElement HitTestCanvas(VuiElement e, float x, float y)
        {
            if (e == null || !e.Visible) return null;
            var kids = (e.Kind == VuiKind.List && e.RowPool != null && e.RowPool.Count > 0) ? e.RowPool : e.Children;
            for (int i = kids.Count - 1; i >= 0; i--) { var h = HitTestCanvas(kids[i], x, y); if (h != null) return h; }
            if (e.Parent != null && e.Resolved.Contains(x, y)) return e;
            return null;
        }

        // ===================== add / delete =====================
        private void AddChild(VuiKind kind)
        {
            var parent = _selected ?? _canvas.Root;
            var e = new VuiElement { Kind = kind, Id = SuggestId(kind), Anchor = AnchorEnum.TopLeft, OffX = 20, OffY = 20, W = 160, H = (kind == VuiKind.Text || kind == VuiKind.Bar) ? 28 : 48, Parent = parent };
            if (kind == VuiKind.Text || kind == VuiKind.Button || kind == VuiKind.TextField) e.Text = kind.ToString();
            if (kind == VuiKind.Panel || kind == VuiKind.Button || kind == VuiKind.Bar || kind == VuiKind.TextField) e.Bg = new float[] { 0.2f, 0.2f, 0.24f, 1f };
            if (kind == VuiKind.Stepper) e.Options = new[] { "Option A", "Option B" };
            parent.Children.Add(e);
            _selected = e; _canvas.Reindex(); _dirty = true;
            RefreshHierarchy(); RefreshInspector(); RefreshPreview();
        }
        private string SuggestId(VuiKind kind)
        {
            string baseId = char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString().Substring(1);
            int n = 1; string id = baseId;
            while (FindById(_canvas.Root, id) != null) id = baseId + (++n);
            return id;
        }
        private VuiElement FindById(VuiElement e, string id)
        {
            if (e == null) return null;
            if (e.Id == id) return e;
            if (e.RowTemplate != null) { var r = FindById(e.RowTemplate, id); if (r != null) return r; }
            foreach (var c in e.Children) { var r = FindById(c, id); if (r != null) return r; }
            return null;
        }
        private void DeleteSelected()
        {
            if (_selected == null || ReferenceEquals(_selected, _canvas.Root)) return;
            if (_selected.Parent != null) _selected.Parent.Children.Remove(_selected);
            _selected = _canvas.Root; _canvas.Reindex(); _dirty = true;
            RefreshHierarchy(); RefreshInspector(); RefreshPreview();
        }

        private void Save()
        {
            try { _canvas.DesignW = _canvas.DesignW; VuiDocument.Save(_canvas, _path); _dirty = false; Title = "UI Editor — " + System.IO.Path.GetFileName(_path); }
            catch (Exception ex) { MessageBox.Show("Save failed:\n" + ex.Message, "UI Editor", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        private void Touch() { _dirty = true; RefreshPreview(); }

        // ===================== inspector =====================
        private void RefreshInspector()
        {
            _inspector.Children.Clear();
            var e = _selected;
            if (e == null) { _inspector.Children.Add(Lbl("(nothing selected)")); return; }
            _inspector.Children.Add(Title2(e.Kind.ToString()));

            AddTextRow("Id", e.Id, v => { e.Id = v; _canvas.Reindex(); RefreshHierarchy(); Touch(); });
            AddAnchorPicker(e);
            AddNumRow("Off X", e.OffX, v => { e.OffX = v; Touch(); });
            AddNumRow("Off Y", e.OffY, v => { e.OffY = v; Touch(); });
            AddNumRow("Width", e.W, v => { e.W = v; Touch(); });
            AddNumRow("Height", e.H, v => { e.H = v; Touch(); });
            AddCheckRow("Stretch X", e.StretchX, v => { e.StretchX = v; Touch(); });
            AddCheckRow("Stretch Y", e.StretchY, v => { e.StretchY = v; Touch(); });
            AddNumRow("Radius", e.Radius, v => { e.Radius = v; Touch(); });
            AddColorRow("Background", e.Bg, c => { e.Bg = c; Touch(); });
            AddColorRow("Foreground", e.Fg, c => { e.Fg = c; Touch(); });

            if (e.Kind == VuiKind.Text || e.Kind == VuiKind.Button || e.Kind == VuiKind.TextField)
            {
                AddTextRow("Text", e.Text, v => { e.Text = v; Touch(); });
                AddNumRow("Font size", e.FontSize, v => { e.FontSize = v; Touch(); });
                AddComboRow("Align", new[] { "Left", "Center", "Right" }, e.Align, i => { e.Align = i; Touch(); });
                AddComboRow("Weight", new[] { "400", "600", "700" }, e.Weight >= 700 ? 2 : (e.Weight >= 600 ? 1 : 0), i => { e.Weight = i == 2 ? 700 : (i == 1 ? 600 : 400); Touch(); });
            }
            if (e.Kind == VuiKind.Image) AddAssetRow("Image", e.ImageAsset, p => { e.ImageAsset = p; Touch(); });
            if (e.Kind == VuiKind.Bar || e.Kind == VuiKind.Slider)
            {
                AddNumRow("Value", e.Value, v => { e.Value = v; Touch(); });
                AddNumRow("Min", e.Min, v => { e.Min = v; Touch(); });
                AddNumRow("Max", e.Max, v => { e.Max = v; Touch(); });
            }
            if (e.Kind == VuiKind.Toggle) AddCheckRow("On", e.On, v => { e.On = v; Touch(); });
            if (e.Kind == VuiKind.Stepper) AddTextRow("Options (csv)", e.Options != null ? string.Join(",", e.Options) : "", v => { e.Options = (v ?? "").Split(','); Touch(); });
            if (e.Kind == VuiKind.Button)
            {
                _inspector.Children.Add(Lbl("On Click → C# method"));
                var actBox = new TextBox { Text = e.ClickAction ?? "", Background = new SolidColorBrush(C_PanelAlt), Foreground = new SolidColorBrush(C_Text), BorderBrush = new SolidColorBrush(C_Border), CaretBrush = new SolidColorBrush(C_Text), Padding = new Thickness(4, 3, 4, 3) };
                actBox.LostFocus += (s, ev) => BindClickAction(e, actBox.Text);
                actBox.KeyDown += (s, ev) => { if (ev.Key == System.Windows.Input.Key.Enter) { BindClickAction(e, actBox.Text); RefreshInspector(); } };
                _inspector.Children.Add(actBox);
                var arow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                arow.Children.Add(MakeButton("✚ Create / Bind", C_Accent, 130, (s, ev) => { BindClickAction(e, actBox.Text); RefreshInspector(); }));
                arow.Children.Add(MakeButton("⟶ Open code", C_PanelAlt, 110, (s, ev) => OpenActionsScript()));
                _inspector.Children.Add(arow);
                if (!string.IsNullOrEmpty(e.ClickAction))
                    _inspector.Children.Add(new TextBlock { Text = "runs  " + ActionsClassName() + "." + e.ClickAction + "()", Foreground = new SolidColorBrush(Color.FromRgb(120, 200, 120)), FontSize = 11, Margin = new Thickness(0, 4, 0, 0), FontFamily = new FontFamily("Consolas") });
                else
                    _inspector.Children.Add(new TextBlock { Text = "(no action — type a name + Create/Bind)", Foreground = new SolidColorBrush(C_TextSec), FontSize = 10, Margin = new Thickness(0, 4, 0, 0) });
            }
            if (e.Kind == VuiKind.Panel || e.Kind == VuiKind.List)
            {
                AddComboRow("Layout", new[] { "None", "Vertical", "Horizontal", "Grid" }, (int)e.LayoutMode, i => { e.LayoutMode = (StackDir)i; Touch(); });
                AddNumRow("Spacing", e.Spacing, v => { e.Spacing = v; Touch(); });
                AddNumRow("Padding", e.Padding, v => { e.Padding = v; Touch(); });
                AddCheckRow("Clip children", e.ClipChildren, v => { e.ClipChildren = v; Touch(); });
            }
            AddCheckRow("Blocks input (screen)", e.BlocksInput, v => { e.BlocksInput = v; Touch(); });
            AddCheckRow("Cursor locked (HUD)", e.CursorLocked, v => { e.CursorLocked = v; Touch(); });
            AddCheckRow("Freeze gameplay (chest/menu)", e.BlocksGameplay, v => { e.BlocksGameplay = v; Touch(); });
        }

        // ---- button -> C# action codegen (the transparent button↔code link) ----
        // ONE actions class per UI: PauseMenu.vui -> Assets/Scripts/UI/PauseMenuActions.cs (class PauseMenuActions).
        // The runtime routes a button on this screen to this class (ScriptRuntime.InvokeUiActions), so screens never
        // share a single dumping-ground file and method names can't collide across UIs.
        private string ActionsClassName()
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(_path) ?? "Screen";
            var sb = new System.Text.StringBuilder();
            foreach (char c in name) if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            if (sb.Length == 0) sb.Append("Screen");
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            sb.Append("Actions");
            return sb.ToString();
        }
        private string ActionsScriptPath()
        {
            var proj = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.Path : null;
            return string.IsNullOrEmpty(proj) ? null : System.IO.Path.Combine(proj, "Assets", "Scripts", "UI", ActionsClassName() + ".cs");
        }
        private void BindClickAction(VuiElement e, string raw)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in raw ?? "") if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            if (sb.Length == 0) { e.ClickAction = null; Touch(); return; }
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            string method = sb.ToString();
            e.ClickAction = method;
            EnsureActionStub(method);
            Touch();
        }
        // Create this screen's actions file (empty class) if it doesn't exist yet. Returns the path (or null).
        private string EnsureActionsFile()
        {
            try
            {
                var path = ActionsScriptPath(); if (path == null) return null;
                var cls = ActionsClassName();
                var screen = System.IO.Path.GetFileName(_path);
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                if (!System.IO.File.Exists(path))
                    System.IO.File.WriteAllText(path,
                        "using Vortex;\n\n" +
                        "// Button actions for " + screen + " — ONE class per UI screen.\n" +
                        "// Each Button's \"On Click\" method lands here and is called when that button is clicked.\n" +
                        "// The engine wires this up automatically (no scene attachment needed): a click on " + screen + "\n" +
                        "// is routed to " + cls + ". UI actions use the static facades (Scene.Load, Application.Quit,\n" +
                        "// Settings.*, Gui.*) — they have no scene entity, so don't use Position/Rotation here.\n" +
                        "public class " + cls + " : VortexBehaviour\n{\n}\n");
                return path;
            }
            catch { return null; }
        }
        private void EnsureActionStub(string method)
        {
            try
            {
                var path = EnsureActionsFile(); if (path == null) return;
                var text = System.IO.File.ReadAllText(path);
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + System.Text.RegularExpressions.Regex.Escape(method) + @"\s*\(")) return; // already there
                int idx = text.LastIndexOf('}');
                if (idx < 0) return;
                string stub = "\n    public void " + method + "()\n    {\n        // TODO: handle the '" + method + "' button click\n    }\n";
                System.IO.File.WriteAllText(path, text.Substring(0, idx) + stub + text.Substring(idx));
            }
            catch { }
        }
        private void OpenActionsScript()
        {
            var path = EnsureActionsFile();
            if (path != null) { try { Editor.Core.Services.ScriptingService.OpenInVisualStudio(path); } catch { } }
        }

        // ---- inspector field helpers ----
        private TextBlock Lbl(string t) => new TextBlock { Text = t, Foreground = new SolidColorBrush(C_TextSec), Margin = new Thickness(0, 8, 0, 2), FontSize = 11 };
        private TextBlock Title2(string t) => new TextBlock { Text = t, Foreground = new SolidColorBrush(C_Text), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

        private void AddTextRow(string label, string val, Action<string> set)
        {
            _inspector.Children.Add(Lbl(label));
            var tb = new TextBox { Text = val ?? "", Background = new SolidColorBrush(C_PanelAlt), Foreground = new SolidColorBrush(C_Text), BorderBrush = new SolidColorBrush(C_Border), CaretBrush = new SolidColorBrush(C_Text), Padding = new Thickness(4, 3, 4, 3) };
            tb.LostFocus += (s, e) => set(tb.Text);
            tb.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) set(tb.Text); };
            _inspector.Children.Add(tb);
        }
        private void AddNumRow(string label, float val, Action<float> set)
        {
            _inspector.Children.Add(Lbl(label));
            var tb = new TextBox { Text = val.ToString(System.Globalization.CultureInfo.InvariantCulture), Background = new SolidColorBrush(C_PanelAlt), Foreground = new SolidColorBrush(C_Text), BorderBrush = new SolidColorBrush(C_Border), CaretBrush = new SolidColorBrush(C_Text), Padding = new Thickness(4, 3, 4, 3) };
            Action commit = () => { if (float.TryParse(tb.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)) set(f); };
            tb.LostFocus += (s, e) => commit();
            tb.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) commit(); };
            _inspector.Children.Add(tb);
        }
        private void AddCheckRow(string label, bool val, Action<bool> set)
        {
            var cb = new CheckBox { Content = label, IsChecked = val, Foreground = new SolidColorBrush(C_Text), Margin = new Thickness(0, 6, 0, 0) };
            cb.Checked += (s, e) => set(true); cb.Unchecked += (s, e) => set(false);
            _inspector.Children.Add(cb);
        }
        private void AddComboRow(string label, string[] items, int sel, Action<int> set)
        {
            _inspector.Children.Add(Lbl(label));
            var cb = new ComboBox { Margin = new Thickness(0, 0, 0, 0) };
            foreach (var it in items) cb.Items.Add(it);
            cb.SelectedIndex = sel >= 0 && sel < items.Length ? sel : 0;
            cb.SelectionChanged += (s, e) => set(cb.SelectedIndex);
            _inspector.Children.Add(cb);
        }
        private void AddColorRow(string label, float[] col, Action<float[]> set)
        {
            _inspector.Children.Add(Lbl(label));
            var c = col != null && col.Length >= 4 ? col : new float[] { 1, 1, 1, 1 };
            var swatch = new Border { Height = 24, Background = new SolidColorBrush(Color.FromArgb(B(c[3]), B(c[0]), B(c[1]), B(c[2]))), BorderBrush = new SolidColorBrush(C_Border), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand };
            swatch.MouseLeftButtonUp += (s, e) =>
            {
                var dlg = new Dialogs.ColorPickerDialog(Color.FromArgb(B(c[3]), B(c[0]), B(c[1]), B(c[2]))) { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    var nc = dlg.SelectedColor;
                    var arr = new[] { nc.R / 255f, nc.G / 255f, nc.B / 255f, nc.A / 255f };
                    swatch.Background = new SolidColorBrush(nc);
                    set(arr);
                }
            };
            _inspector.Children.Add(swatch);
        }
        private void AddAssetRow(string label, string path, Action<string> set)
        {
            _inspector.Children.Add(Lbl(label));
            var row = new DockPanel();
            var tb = new TextBox { Text = path ?? "", Background = new SolidColorBrush(C_PanelAlt), Foreground = new SolidColorBrush(C_Text), BorderBrush = new SolidColorBrush(C_Border), Padding = new Thickness(4, 3, 4, 3) };
            var btn = MakeButton("…", C_PanelAlt, 30, (s, e) =>
            {
                try
                {
                    var dlg = new Dialogs.AssetPickerDialog("Textures", ".png", ".jpg", ".jpeg") { Owner = this };
                    if (dlg.ShowDialog() == true) { tb.Text = dlg.SelectedFullPath; set(dlg.SelectedFullPath); }
                }
                catch { }
            });
            DockPanel.SetDock(btn, Dock.Right); row.Children.Add(btn); row.Children.Add(tb);
            tb.LostFocus += (s, e) => set(tb.Text);
            _inspector.Children.Add(row);
        }
        private void AddAnchorPicker(VuiElement e)
        {
            _inspector.Children.Add(Lbl("Anchor"));
            var g = new UniformGrid { Rows = 3, Columns = 3, Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left };
            for (int i = 0; i < 9; i++)
            {
                var a = (AnchorEnum)i;
                var b = new Button { Width = 30, Height = 30, Margin = new Thickness(1), Background = new SolidColorBrush(a == e.Anchor ? C_Accent : C_PanelAlt), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
                var aa = a;
                b.Click += (s, ev) => { e.Anchor = aa; Touch(); RefreshInspector(); };
                g.Children.Add(b);
            }
            _inspector.Children.Add(g);
        }
        private static byte B(float v) { int i = (int)(v * 255f + 0.5f); return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i)); }
    }
}
