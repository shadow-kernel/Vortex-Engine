using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// </summary>
    public static class PropertyRows
    {
        public const double LabelWidth = 112;

        public static Grid Row(string label, Control editor, string tooltip = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(LabelWidth + ",*"), Classes = { "proprow" } };
            var l = new TextBlock { Text = label, Classes = { "label" }, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            if (tooltip != null) ToolTip.SetTip(l, tooltip);
            g.Children.Add(l);
            Grid.SetColumn(editor, 1);
            editor.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(editor);
            return g;
        }

        public static TextBlock Note(string text)
            => new TextBlock { Text = text, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(LabelWidth + 10, 0, 0, 2) };

        public static Border Warning(string text)
            => new Border { Background = (IBrush)Application.Current.FindResource("VxAccentSoftBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5), Margin = new Thickness(0, 2, 0, 4),
                Child = new TextBlock { Text = text, Classes = { "small" }, TextWrapping = TextWrapping.Wrap } };

        // ---------------------------------------------------------------- numbers
        public static TextBox FloatBox(Func<float> get, Action<float> set, double step = 0.1, float? min = null, float? max = null, string format = "0.###")
        {
            var box = new TextBox { Classes = { "number" }, MinHeight = 22, Text = Fmt(get(), format) };
            void Commit()
            {
                if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
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
            var box = new TextBox { Classes = { "number" }, MinHeight = 22, Text = get().ToString() };
            void Commit()
            {
                if (int.TryParse(box.Text, out var v)) { if (min.HasValue) v = Math.Max(min.Value, v); if (max.HasValue) v = Math.Min(max.Value, v); if (v != get()) set(v); }
                box.Text = get().ToString();
            }
            box.LostFocus += (s, e) => Commit();
            box.KeyDown += (s, e) => { if (e.Key == Key.Return) { Commit(); e.Handled = true; } };
            Refreshers[box] = () => { if (!box.IsFocused) box.Text = get().ToString(); };
            return box;
        }

        public static Control SliderRow(Func<float> get, Action<float> set, double min, double max, string format = "0.##")
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,60") };
            var slider = new Slider { Minimum = min, Maximum = max, Value = get(), Margin = new Thickness(0, 0, 6, 0) };
            var box = FloatBox(get, v => { set(v); slider.Value = v; }, (max - min) / 100, (float)min, (float)max, format);
            bool guard = false;
            slider.ValueChanged += (s, e) => { if (guard) return; guard = true; float v = (float)slider.Value; if (Math.Abs(v - get()) > 1e-6) set(v); box.Text = Fmt(get(), format); guard = false; };
            Grid.SetColumn(box, 1);
            g.Children.Add(slider); g.Children.Add(box);
            Refreshers[slider] = () => { guard = true; slider.Value = get(); guard = false; };
            return g;
        }

        public static Control Vector3(Func<Editor.ECS.Vector3> get, Action<Editor.ECS.Vector3> set, double step = 0.1, float? min = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto,*") };
            string[] names = { "X", "Y", "Z" }; string[] keys = { "VxAxisXBrush", "VxAxisYBrush", "VxAxisZBrush" };
            for (int i = 0; i < 3; i++)
            {
                int axis = i;
                var lbl = new TextBlock { Text = names[i], FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = (IBrush)Application.Current.FindResource(keys[i]), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i == 0 ? 0 : 8, 0, 4, 0) };
                Grid.SetColumn(lbl, i * 2);
                var box = FloatBox(() => Comp(get(), axis), v => { var c = get(); set(axis == 0 ? new Editor.ECS.Vector3(v, c.Y, c.Z) : axis == 1 ? new Editor.ECS.Vector3(c.X, v, c.Z) : new Editor.ECS.Vector3(c.X, c.Y, v)); }, step, min);
                box.MinWidth = 40;
                Grid.SetColumn(box, i * 2 + 1);
                g.Children.Add(lbl); g.Children.Add(box);
            }
            return g;
        }
        private static float Comp(Editor.ECS.Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

        // ---------------------------------------------------------------- bool / enum / text
        public static Control Bool(Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { IsChecked = get(), MinHeight = 22 };
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

        // ---------------------------------------------------------------- colour
        public static Control Color(Func<(float r, float g, float b)> get, Action<float, float, float> set)
        {
            var (r, g, b) = get();
            var picker = new ColorPicker { Color = Avalonia.Media.Color.FromRgb(B(r), B(g), B(b)), Width = 110, Height = 22, IsAlphaEnabled = false, IsAlphaVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
            picker.ColorChanged += (s, e) => set(e.NewColor.R / 255f, e.NewColor.G / 255f, e.NewColor.B / 255f);
            Refreshers[picker] = () => { var (rr, gg, bb) = get(); var c = Avalonia.Media.Color.FromRgb(B(rr), B(gg), B(bb)); if (picker.Color != c) picker.Color = c; };
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
            browse.Click += async (s, e) => { var p = await pick(); if (p != null) { set(p); box.Text = get() ?? ""; } };
            var clear = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Close" } };
            ToolTip.SetTip(clear, "Clear");
            clear.Click += (s, e) => { set(null); box.Text = ""; };
            DragDrop.SetAllowDrop(box, true);
            box.AddHandler(DragDrop.DragOverEvent, (s, e) => { e.DragEffects = Accepts(e, patterns, dropFormat) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; });
            box.AddHandler(DragDrop.DropEvent, (s, e) => { var p = DroppedPath(e, dropFormat); if (p != null && Matches(p, patterns)) { set(p); box.Text = get() ?? ""; e.Handled = true; } });
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
            string ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            foreach (var p in patterns) if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---------------------------------------------------------------- misc helpers
        public static string Fmt(float v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);
        public static byte B(float v) => (byte)Math.Max(0, Math.Min(255, (int)(v * 255f + 0.5f)));
        public static string Pretty(string enumName)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in enumName) { if (char.IsUpper(c) && sb.Length > 0) sb.Append(' '); sb.Append(c); }
            return sb.ToString();
        }

        /// <summary>Controls that can re-read their value from the model (transform drag, undo).</summary>
        public static readonly Dictionary<Control, Action> Refreshers = new Dictionary<Control, Action>();
        public static void RefreshAll() { foreach (var kv in Refreshers) { try { kv.Value(); } catch { } } }
        public static void ClearRefreshers() => Refreshers.Clear();
    }
}
