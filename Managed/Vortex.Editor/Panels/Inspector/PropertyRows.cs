using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Editor.ECS;
using VortexEditor.Controls;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>
    /// The inspector's property-row vocabulary: label column + editor column, macOS-style (label right-aligned,
    /// controls left-aligned). Every row edits the component through its property setter, so undo is automatic.
    /// Shared by the inspector cards and every editor window (collision / socket / material / …).
    /// </summary>
    public static class PropertyRows
    {
        public const double LabelWidth = 112;

        public static Grid Row(string label, Control editor, string tooltip = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(LabelWidth + ",*"), Classes = { "proprow" } };
            var l = new TextBlock { Text = label, Classes = { "label" }, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            // long labels are trimmed to the column: the tooltip always carries the full text
            if (tooltip != null) ToolTip.SetTip(l, tooltip);
            else if (!string.IsNullOrEmpty(label) && label.Length > 13) ToolTip.SetTip(l, label);
            g.Children.Add(l);
            Grid.SetColumn(editor, 1);
            editor.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(editor);
            return g;
        }

        /// <summary>Like <see cref="Row"/> for tall editors (lists, tables): the label sits at the top.</summary>
        public static Grid RowTop(string label, Control editor, string tooltip = null)
        {
            var g = Row(label, editor, tooltip);
            if (g.Children[0] is TextBlock l) { l.VerticalAlignment = VerticalAlignment.Top; l.Margin = new Thickness(0, 4, 10, 0); }
            editor.VerticalAlignment = VerticalAlignment.Top;
            return g;
        }

        public static TextBlock Note(string text)
            => new TextBlock { Text = text, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(LabelWidth + 10, 0, 0, 2) };

        /// <summary>Full-width hint text (no label indent).</summary>
        public static TextBlock Hint(string text)
            => new TextBlock { Text = text, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 4) };

        public static Border Warning(string text)
            => new Border { Background = (IBrush)Application.Current.FindResource("VxAccentSoftBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5), Margin = new Thickness(0, 2, 0, 4),
                Child = new TextBlock { Text = text, Classes = { "small" }, TextWrapping = TextWrapping.Wrap } };

        /// <summary>A red-tinted warning for states that silently do nothing (e.g. a mesh collider without a mesh).</summary>
        public static Border Danger(string text)
            => new Border { Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x38, 0xFF, 0x45, 0x3A)), BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x70, 0xFF, 0x45, 0x3A)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5), Margin = new Thickness(0, 2, 0, 4),
                Child = new TextBlock { Text = text, Classes = { "small" }, TextWrapping = TextWrapping.Wrap } };

        /// <summary>Small caps section header inside a card ("TARGET", "OFFSET (BONE SPACE)", …).</summary>
        public static TextBlock Section(string text)
            => new TextBlock { Text = text.ToUpperInvariant(), Classes = { "small", "tertiary" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 10, 0, 3) };

        /// <summary>A left-aligned row of ghost buttons (card actions).</summary>
        public static WrapPanel Actions(params Control[] buttons)
        {
            var w = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(LabelWidth + 6, 4, 0, 2) };
            foreach (var b in buttons) { if (b == null) continue; b.Margin = new Thickness(0, 0, 6, 4); w.Children.Add(b); }
            return w;
        }

        public static Button Ghost(string text, Action click, string tooltip = null)
        {
            var b = new Button { Content = text, Classes = { "ghost" } };
            if (tooltip != null) ToolTip.SetTip(b, tooltip);
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { VortexEditor.Shell.EditorCommands.Fail(text, ex); } };
            return b;
        }

        // ---------------------------------------------------------------- numbers
        public static TextBox FloatBox(Func<float> get, Action<float> set, double step = 0.1, float? min = null, float? max = null, string format = "0.###")
        {
            var box = new TextBox { Classes = { "number" }, MinHeight = 22, Text = Fmt(get(), format) };
            void Commit()
            {
                // focusing a box and leaving it must not round the value to the displayed precision
                if (box.Text == Fmt(get(), format)) return;
                if (TryParse(box.Text, out var v))
                {
                    if (min.HasValue) v = Math.Max(min.Value, v);
                    if (max.HasValue) v = Math.Min(max.Value, v);
                    if (v != get()) set(v);
                }
                box.Text = Fmt(get(), format);
            }
            box.LostFocus += (s, e) => Commit();
            box.KeyDown += (s, e) => { if (e.Key == Key.Return) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { box.Text = Fmt(get(), format); e.Handled = true; } };
            box.PointerWheelChanged += (s, e) =>
            {
                if (!box.IsFocused) return;
                float v = get() + (float)(e.Delta.Y > 0 ? step : -step) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
                if (min.HasValue) v = Math.Max(min.Value, v); if (max.HasValue) v = Math.Min(max.Value, v);
                set(v); box.Text = Fmt(get(), format); e.Handled = true;
            };
            Refreshers[box] = () => { if (!box.IsFocused) box.Text = Fmt(get(), format); };
            return box;
        }

        public static TextBox IntBox(Func<int> get, Action<int> set, int? min = null, int? max = null)
        {
            var box = new TextBox { Classes = { "number" }, MinHeight = 22, Text = get().ToString(CultureInfo.InvariantCulture) };
            void Commit()
            {
                if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { if (min.HasValue) v = Math.Max(min.Value, v); if (max.HasValue) v = Math.Min(max.Value, v); if (v != get()) set(v); }
                box.Text = get().ToString(CultureInfo.InvariantCulture);
            }
            box.LostFocus += (s, e) => Commit();
            box.KeyDown += (s, e) => { if (e.Key == Key.Return) { Commit(); e.Handled = true; } };
            box.PointerWheelChanged += (s, e) =>
            {
                if (!box.IsFocused) return;
                int v = get() + (e.Delta.Y > 0 ? 1 : -1) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
                if (min.HasValue) v = Math.Max(min.Value, v); if (max.HasValue) v = Math.Min(max.Value, v);
                set(v); box.Text = get().ToString(CultureInfo.InvariantCulture); e.Handled = true;
            };
            Refreshers[box] = () => { if (!box.IsFocused) box.Text = get().ToString(CultureInfo.InvariantCulture); };
            return box;
        }

        public static Control SliderRow(Func<float> get, Action<float> set, double min, double max, string format = "0.##")
            => SliderRow(get, set, min, max, format, (float)min, (float)max);

        /// <summary>Slider over [min, max] plus a number box whose typed values may leave the slider range
        /// (typedMin / typedMax, null = unbounded) — e.g. a light intensity of 25 while the slider tops out at 10.</summary>
        public static Control SliderRow(Func<float> get, Action<float> set, double min, double max, string format, float? typedMin, float? typedMax)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,60") };
            var slider = new Slider { Minimum = min, Maximum = max, Value = Clamp(get(), min, max), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            bool guard = false;
            var box = FloatBox(get, v => { set(v); guard = true; slider.Value = Clamp(get(), min, max); guard = false; }, (max - min) / 100, typedMin, typedMax, format);
            box.VerticalAlignment = VerticalAlignment.Center;
            slider.ValueChanged += (s, e) => { if (guard) return; guard = true; float v = (float)slider.Value; if (Math.Abs(v - get()) > 1e-6) set(v); box.Text = Fmt(get(), format); guard = false; };
            Grid.SetColumn(box, 1);
            g.Children.Add(slider); g.Children.Add(box);
            Refreshers[slider] = () => { guard = true; slider.Value = Clamp(get(), min, max); guard = false; };
            return g;
        }
        private static double Clamp(double v, double a, double b) => v < a ? a : v > b ? b : v;

        public static Control Vector3(Func<Editor.ECS.Vector3> get, Action<Editor.ECS.Vector3> set, double step = 0.1, float? min = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto,*") };
            string[] names = { "X", "Y", "Z" }; string[] keys = { "VxAxisXBrush", "VxAxisYBrush", "VxAxisZBrush" };
            for (int i = 0; i < 3; i++)
            {
                int axis = i;
                var lbl = new TextBlock { Text = names[i], FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = (IBrush)Application.Current.FindResource(keys[i]), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i == 0 ? 0 : 8, 0, 4, 0), Cursor = new Cursor(StandardCursorType.SizeWestEast) };
                ToolTip.SetTip(lbl, "Drag to scrub (Shift ×10, Alt ×0.1)");
                Grid.SetColumn(lbl, i * 2);
                Action<float> setAxis = v => { var c = get(); set(axis == 0 ? new Editor.ECS.Vector3(v, c.Y, c.Z) : axis == 1 ? new Editor.ECS.Vector3(c.X, v, c.Z) : new Editor.ECS.Vector3(c.X, c.Y, v)); };
                var box = FloatBox(() => Comp(get(), axis), setAxis, step, min);
                box.MinWidth = 40;
                Grid.SetColumn(box, i * 2 + 1);
                AttachScrub(lbl, () => Comp(get(), axis), v => { if (min.HasValue) v = Math.Max(min.Value, v); setAxis(v); box.Text = Fmt(Comp(get(), axis)); }, step);
                g.Children.Add(lbl); g.Children.Add(box);
            }
            return g;
        }
        private static float Comp(Editor.ECS.Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

        /// <summary>Horizontal drag on a label scrubs a value (one step per 4 px; Shift ×10, Alt ×0.1).</summary>
        public static void AttachScrub(Control handle, Func<float> get, Action<float> set, double step)
        {
            bool drag = false; double x0 = 0; float v0 = 0;
            handle.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
                drag = true; x0 = e.GetPosition(handle).X; v0 = get();
                e.Pointer.Capture(handle); e.Handled = true;
            };
            handle.PointerMoved += (s, e) =>
            {
                if (!drag) return;
                double dx = e.GetPosition(handle).X - x0;
                double k = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 0.1 : 1;
                float v = (float)(v0 + Math.Round(dx / 4.0) * step * k);
                if (Math.Abs(v - get()) > 1e-7) set(v);
            };
            handle.PointerReleased += (s, e) => { if (drag) { drag = false; e.Pointer.Capture(null); } };
            handle.PointerCaptureLost += (s, e) => drag = false;
        }

        // ---------------------------------------------------------------- bool / enum / text
        public static Control Bool(Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { IsChecked = get(), MinHeight = 22 };
            cb.IsCheckedChanged += (s, e) => { bool v = cb.IsChecked == true; if (v != get()) set(v); };
            Refreshers[cb] = () => cb.IsChecked = get();
            return cb;
        }

        /// <summary>Checkbox with its caption to the right (full-width option lines).</summary>
        public static Control Check(string caption, Func<bool> get, Action<bool> set, string tooltip = null)
        {
            var cb = new CheckBox { IsChecked = get(), MinHeight = 22, Content = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap } };
            if (tooltip != null) ToolTip.SetTip(cb, tooltip);
            cb.IsCheckedChanged += (s, e) => { bool v = cb.IsChecked == true; if (v != get()) set(v); };
            Refreshers[cb] = () => cb.IsChecked = get();
            return cb;
        }

        public static Control Enum<T>(Func<T> get, Action<T> set, IReadOnlyList<string> labels = null) where T : struct, System.Enum
        {
            var values = (T[])System.Enum.GetValues(typeof(T));
            var combo = new ComboBox { MinHeight = 22, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };
            for (int i = 0; i < values.Length; i++) combo.Items.Add(labels != null && i < labels.Count ? labels[i] : Pretty(values[i].ToString()));
            combo.SelectedIndex = Array.IndexOf(values, get());
            combo.SelectionChanged += (s, e) => { if (combo.SelectedIndex >= 0) { var v = values[combo.SelectedIndex]; if (!v.Equals(get())) set(v); } };
            Refreshers[combo] = () => combo.SelectedIndex = Array.IndexOf(values, get());
            return combo;
        }

        public static Control Choice(Func<int> get, Action<int> set, params string[] labels)
        {
            var combo = new ComboBox { MinHeight = 22, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };
            foreach (var l in labels) combo.Items.Add(l);
            combo.SelectedIndex = Math.Max(0, Math.Min(labels.Length - 1, get()));
            combo.SelectionChanged += (s, e) => { if (combo.SelectedIndex >= 0 && combo.SelectedIndex != get()) set(combo.SelectedIndex); };
            Refreshers[combo] = () => combo.SelectedIndex = Math.Max(0, Math.Min(labels.Length - 1, get()));
            return combo;
        }

        public static TextBox Text(Func<string> get, Action<string> set, string watermark = null)
        {
            var box = new TextBox { Text = get() ?? "", MinHeight = 22, Watermark = watermark };
            void Commit() { var v = box.Text ?? ""; if (v != (get() ?? "")) set(v); }
            box.LostFocus += (s, e) => Commit();
            box.KeyDown += (s, e) => { if (e.Key == Key.Return) { Commit(); e.Handled = true; } };
            Refreshers[box] = () => { if (!box.IsFocused) box.Text = get() ?? ""; };
            return box;
        }

        // ---------------------------------------------------------------- searchable picker (bones, entities, clips)
        /// <summary>
        /// Searchable picker: a button showing the current value that opens a flyout with a filter box and the candidate
        /// list. Candidates are fetched each time the flyout opens (a skeleton that resolves later just appears).
        /// Type to filter, arrows to move, Return to pick; <paramref name="allowCustom"/> lets Return commit typed text
        /// that matches nothing. <paramref name="display"/> shortens names for the list (full name in the tooltip).
        /// </summary>
        public static Control SearchPicker(Func<string> get, Action<string> set, Func<IEnumerable<string>> items, string placeholder = "None",
                                           Func<string, string> display = null, bool allowCustom = false, string tooltip = null)
        {
            display = display ?? (s => s);
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var chevron = new VxIcon { Icon = "ChevronDown", Width = 11, Height = 11, Margin = new Thickness(6, 0, 0, 0) };
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(chevron, 1);
            content.Children.Add(text); content.Children.Add(chevron);
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 24, Padding = new Thickness(8, 2) };
            void Sync()
            {
                string v = get();
                bool empty = string.IsNullOrEmpty(v);
                text.Text = empty ? placeholder : display(v);
                text.Opacity = empty ? 0.55 : 1;
                ToolTip.SetTip(button, empty ? tooltip : (tooltip != null ? v + "\n" + tooltip : v));
            }
            Sync();

            var search = new TextBox { Classes = { "search" }, Watermark = "Search…", Margin = new Thickness(0, 0, 0, 6) };
            var list = new ListBox { MaxHeight = 320, MinHeight = 60 };
            list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((n, _) =>
            {
                var tb = new TextBlock { Text = display(n), TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(tb, n);
                return tb;
            });
            var empty2 = new TextBlock { Text = "Nothing to pick.", Classes = { "small", "tertiary" }, Margin = new Thickness(4, 6), IsVisible = false };
            var panel = new StackPanel { Width = 300, Margin = new Thickness(2) };
            panel.Children.Add(search); panel.Children.Add(list); panel.Children.Add(empty2);
            var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
            List<string> all = new List<string>();
            bool filling = false;
            void Fill()
            {
                filling = true;
                string q = search.Text?.Trim() ?? "";
                var shown = q.Length == 0 ? all : all.Where(n => n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || display(n).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                list.ItemsSource = shown;
                string cur = get();
                list.SelectedItem = shown.FirstOrDefault(n => string.Equals(n, cur, StringComparison.Ordinal)) ?? (q.Length > 0 ? shown.FirstOrDefault() : null);
                if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
                empty2.IsVisible = shown.Count == 0;
                empty2.Text = all.Count == 0 ? "Nothing to pick." : (allowCustom ? "No match — Return uses the typed name." : "No match.");
                filling = false;
            }
            void Commit(string v)
            {
                flyout.Hide();
                if (v != null && !string.Equals(v, get(), StringComparison.Ordinal)) set(v);
                Sync();
            }
            flyout.Opened += (s, e) =>
            {
                try { all = (items() ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList(); } catch { all = new List<string>(); }
                search.Text = "";
                panel.Width = Math.Max(260, button.Bounds.Width);
                Fill();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => search.Focus());
            };
            search.TextChanged += (s, e) => Fill();
            search.KeyDown += (s, e) =>
            {
                var shown = list.ItemsSource as List<string> ?? new List<string>();
                if (e.Key == Key.Down && shown.Count > 0) { list.SelectedIndex = Math.Min(shown.Count - 1, list.SelectedIndex + 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
                else if (e.Key == Key.Up && shown.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
                else if (e.Key == Key.Return)
                {
                    if (list.SelectedItem is string sel) Commit(sel);
                    else if (allowCustom && !string.IsNullOrWhiteSpace(search.Text)) Commit(search.Text.Trim());
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape) { flyout.Hide(); e.Handled = true; }
            };
            list.PointerReleased += (s, e) => { if (!filling && list.SelectedItem is string sel) Commit(sel); };
            list.KeyDown += (s, e) => { if (e.Key == Key.Return && list.SelectedItem is string sel) { Commit(sel); e.Handled = true; } };
            FlyoutBase.SetAttachedFlyout(button, flyout);
            button.Click += (s, e) => FlyoutBase.ShowAttachedFlyout(button);
            Refreshers[button] = Sync;
            return button;
        }

        // ---------------------------------------------------------------- colour
        public static Control Color(Func<(float r, float g, float b)> get, Action<float, float, float> set)
        {
            var (r, g, b) = get();
            var picker = new ColorPicker { Color = Avalonia.Media.Color.FromRgb(B(r), B(g), B(b)), Width = 110, Height = 22, IsAlphaEnabled = false, IsAlphaVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
            bool guard = false;
            picker.ColorChanged += (s, e) => { if (!guard) set(e.NewColor.R / 255f, e.NewColor.G / 255f, e.NewColor.B / 255f); };
            Refreshers[picker] = () => { var (rr, gg, bb) = get(); var c = Avalonia.Media.Color.FromRgb(B(rr), B(gg), B(bb)); if (picker.Color != c) { guard = true; picker.Color = c; guard = false; } };
            return picker;
        }

        // ---------------------------------------------------------------- asset path
        public static Control AssetPath(Func<string> get, Action<string> set, string kind, string[] patterns, Func<Task<string>> pick, string dropFormat = "vortex/asset")
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            var box = new TextBox { Text = get() ?? "", MinHeight = 22, Watermark = "None (" + kind + ")", IsReadOnly = true };
            ToolTip.SetTip(box, "Drop a " + kind.ToLowerInvariant() + " here, or pick one");
            var browse = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Folder" }, Margin = new Thickness(4, 0, 0, 0) };
            ToolTip.SetTip(browse, "Choose " + kind.ToLowerInvariant());
            browse.Click += async (s, e) => { var p = await pick(); if (p != null) { set(p.Length == 0 ? p : ToProjectRelative(p)); box.Text = get() ?? ""; } };
            var clear = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Close" } };
            ToolTip.SetTip(clear, "Clear");
            clear.Click += (s, e) => { set(null); box.Text = get() ?? ""; };
            DragDrop.SetAllowDrop(box, true);
            box.AddHandler(DragDrop.DragOverEvent, (s, e) => { e.DragEffects = Accepts(e, patterns, dropFormat) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; });
            box.AddHandler(DragDrop.DropEvent, (s, e) => { var p = DroppedPath(e, dropFormat); if (p != null && Matches(p, patterns)) { set(ToProjectRelative(p)); box.Text = get() ?? ""; e.Handled = true; } });
            Grid.SetColumn(browse, 1); Grid.SetColumn(clear, 2);
            g.Children.Add(box); g.Children.Add(browse); g.Children.Add(clear);
            Refreshers[box] = () => box.Text = get() ?? "";
            return g;
        }

        public static string DroppedPath(DragEventArgs e, string format)
        {
            if (e.Data.Get(format) is string s) return s;
            if (e.Data.Contains(DataFormats.Files)) { foreach (var f in e.Data.GetFiles() ?? Array.Empty<Avalonia.Platform.Storage.IStorageItem>()) { var p = f.TryGetLocalPath(); if (p != null) return p; } }
            return null;
        }
        private static bool Accepts(DragEventArgs e, string[] patterns, string format) { var p = DroppedPath(e, format); return p != null && Matches(p, patterns); }
        public static bool Matches(string path, string[] patterns)
        {
            if (patterns == null || patterns.Length == 0) return true;
            string ext = System.IO.Path.GetExtension(path ?? "")?.ToLowerInvariant() ?? "";
            if (ext.Length == 0) return false;   // an extension-less path (a folder, "Primitive:Cube") matches no file pattern
            foreach (var p in patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (p == "*" || p == "*.*") return true;
                if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Absolute path inside the project -> project-relative with forward slashes (what scenes store);
        /// anything else passes through unchanged.</summary>
        public static string ToProjectRelative(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.Path.IsPathRooted(path)) return path?.Replace('\\', '/');
            var root = Editor.Core.Data.ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return path;
            try
            {
                string full = System.IO.Path.GetFullPath(path), r = System.IO.Path.GetFullPath(root).TrimEnd('/', '\\');
                if (full.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
                    return full.Substring(r.Length + 1).Replace('\\', '/');
            }
            catch { }
            return path;
        }

        // ---------------------------------------------------------------- misc helpers
        public static string Fmt(float v, string format = "0.###")
        {
            string s = v.ToString(format, CultureInfo.InvariantCulture);
            // a negative zero / tiny negative rounded away prints "-0" ("-0.00") — reads like a sign bug, drop the sign
            if (s.Length > 1 && s[0] == '-')
            {
                bool zero = true;
                for (int i = 1; i < s.Length && zero; i++) zero = s[i] == '0' || s[i] == '.';
                if (zero) s = s.Substring(1);
            }
            return s;
        }
        public static bool TryParse(string s, out float v)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v);
        public static byte B(float v) => (byte)Math.Max(0, Math.Min(255, (int)(v * 255f + 0.5f)));
        public static string Pretty(string enumName)
        {
            if (string.IsNullOrEmpty(enumName)) return enumName ?? "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < enumName.Length; i++)
            {
                char c = enumName[i];
                if (c == '_') { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                if (char.IsUpper(c) && sb.Length > 0 && sb[sb.Length - 1] != ' ' && (!char.IsUpper(enumName[i - 1]) || (i + 1 < enumName.Length && char.IsLower(enumName[i + 1])))) sb.Append(' ');
                sb.Append(sb.Length == 0 ? char.ToUpperInvariant(c) : c);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Controls that can re-read their value from the model (transform drag, undo).</summary>
        public static readonly Dictionary<Control, Action> Refreshers = new Dictionary<Control, Action>();
        /// <summary>Re-read every live control (a snapshot is iterated, so a refresher may rebuild rows safely).</summary>
        public static void RefreshAll() { foreach (var kv in Refreshers.ToArray()) { try { kv.Value(); } catch { } } }
        public static void ClearRefreshers() => Refreshers.Clear();

        /// <summary>
        /// Editor windows build rows with these helpers but must not share the INSPECTOR's refresher table (it is cleared on
        /// every selection change): <c>using (PropertyRows.CaptureRefreshers(myList)) { …build rows… }</c> moves the
        /// refreshers of every control created inside the scope into <paramref name="target"/>.
        /// </summary>
        public static IDisposable CaptureRefreshers(List<Action> target) => new RefresherCapture(target);

        private sealed class RefresherCapture : IDisposable
        {
            private readonly List<Action> _target;
            private readonly HashSet<Control> _before;
            public RefresherCapture(List<Action> target) { _target = target; _before = new HashSet<Control>(Refreshers.Keys); }
            public void Dispose()
            {
                foreach (var kv in Refreshers.ToArray())
                {
                    if (_before.Contains(kv.Key)) continue;
                    _target?.Add(kv.Value);
                    Refreshers.Remove(kv.Key);
                }
            }
        }
    }
}
