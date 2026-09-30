using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VortexEditor.Controls;

namespace VortexEditor.Shell.Animation
{
    /// <summary>Small UI vocabulary of the Keyframe / Socket / Collision editors (toolbar pieces, notes, headers,
    /// owner-aware dialogs — the shared <see cref="Dialogs"/> always parent to the main window).</summary>
    public static class AnimUi
    {
        public static IBrush Res(string key) => Application.Current?.FindResource(key) as IBrush ?? Brushes.Gray;

        public static readonly Geometry JumpStartIcon = Geometry.Parse("M2,3 H4 V13 H2 Z M10,3 V13 L4.5,8 Z M16,3 V13 L10.5,8 Z");
        public static readonly Geometry StepBackIcon = Geometry.Parse("M3,3 H5 V13 H3 Z M13,3 V13 L6,8 Z");
        public static readonly Geometry StepForwardIcon = Geometry.Parse("M3,3 V13 L10,8 Z M11,3 H13 V13 H11 Z");
        public static readonly Geometry JumpEndIcon = Geometry.Parse("M0,3 V13 L5.5,8 Z M6,3 V13 L11.5,8 Z M12,3 H14 V13 H12 Z");

        public static TextBlock MicroHeader(string text, double top = 2)
            => new TextBlock { Text = text, Classes = { "small", "tertiary" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, top, 0, 6) };

        public static TextBlock ToolLabel(string text)
            => new TextBlock { Text = text, Classes = { "small", "tertiary" }, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 5, 0) };

        public static Border ToolSeparator()
            => new Border { Width = 1, Height = 22, Background = Res("VxSeparatorBrush"), Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

        /// <summary>Rounded note box (instructions, empty states).</summary>
        public static Border NoteBox(string text)
            => new Border
            {
                Background = Res("VxFieldBrush"), BorderBrush = Res("VxHairlineBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 9, 11, 10), Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock { Text = text, Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap, LineHeight = 16 }
            };

        public static PathIcon Icon(string name, double size = 14) => new VxIcon { Icon = name, Width = size, Height = size };
        public static PathIcon Icon(Geometry g, double size = 14) => new PathIcon { Data = g, Width = size, Height = size };

        /// <summary>Toolbar button with an icon (or a text caption when <paramref name="icon"/> is null).</summary>
        public static Button ToolButton(object icon, string text, string tooltip, Action click)
        {
            object content;
            if (icon != null && text != null)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                sp.Children.Add(icon is Geometry g ? Icon(g) : Icon(icon.ToString()));
                sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
                content = sp;
            }
            else content = icon is Geometry g2 ? Icon(g2) : icon != null ? Icon(icon.ToString()) : (object)text;
            var b = new Button { Content = content, MinHeight = 28, MinWidth = 32, Padding = new Thickness(text != null ? 10 : 6, 3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0) };
            if (tooltip != null) ToolTip.SetTip(b, tooltip);
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { EditorCommands.Fail(text ?? tooltip ?? "Action", ex); } };
            return b;
        }

        /// <summary>Checked-state toolbar toggle ("Snap", "Loop").</summary>
        public static ToggleButton ToolToggle(string text, bool initial, string tooltip, Action<bool> changed)
        {
            var t = new ToggleButton { Content = text, IsChecked = initial, MinHeight = 26, Padding = new Thickness(10, 2), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "accentcheck" } };
            if (tooltip != null) ToolTip.SetTip(t, tooltip);
            t.IsCheckedChanged += (s, e) => changed(t.IsChecked == true);
            return t;
        }

        public static TextBox ToolTextBox(double width, Action<string> commit, Func<bool> suppressed = null)
        {
            var tb = new TextBox { Width = width, MinHeight = 24, VerticalAlignment = VerticalAlignment.Center };
            void Commit() { if (suppressed != null && suppressed()) return; try { commit(tb.Text ?? ""); } catch { } }
            tb.LostFocus += (s, e) => Commit();
            tb.KeyDown += (s, e) => { if (e.Key == Key.Return) { Commit(); e.Handled = true; } };
            return tb;
        }

        /// <summary>X/Y/Z number row that commits LIVE while typing (the pose override follows every keystroke), with
        /// wheel steps while focused and drag-scrub on the axis letters. <paramref name="refresh"/> receives an action
        /// that re-reads the values (skipping a focused box).</summary>
        public static Grid LiveVec3(Func<System.Numerics.Vector3> get, Action<System.Numerics.Vector3> set, double step, out Action refresh)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto,*"), Margin = new Thickness(0, 0, 0, 10) };
            string[] names = { "X", "Y", "Z" }; string[] keys = { "VxAxisXBrush", "VxAxisYBrush", "VxAxisZBrush" };
            var boxes = new TextBox[3];
            bool guard = false;
            for (int i = 0; i < 3; i++)
            {
                int axis = i;
                var lbl = new TextBlock { Text = names[i], FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Res(keys[i]), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i == 0 ? 0 : 8, 0, 4, 0), Cursor = new Cursor(StandardCursorType.SizeWestEast) };
                var box = new TextBox { Classes = { "number" }, MinWidth = 40, MinHeight = 22 };
                boxes[i] = box;
                Func<float> gv = () => { var v = get(); return axis == 0 ? v.X : axis == 1 ? v.Y : v.Z; };
                Action<float> sv = f => { var v = get(); if (axis == 0) v.X = f; else if (axis == 1) v.Y = f; else v.Z = f; set(v); };
                // Avalonia raises TextChanged asynchronously: only a FOCUSED box (the user typing) may write, and text that
                // still shows the displayed (rounded) value is not an edit — otherwise refreshes would round the model
                box.TextChanged += (s, e) =>
                {
                    if (guard || !box.IsFocused) return;
                    if (box.Text == Panels.Inspector.PropertyRows.Fmt(gv())) return;
                    if (Panels.Inspector.PropertyRows.TryParse(box.Text, out var f) && Math.Abs(f - gv()) > 1e-7) sv(f);
                };
                box.PointerWheelChanged += (s, e) =>
                {
                    if (!box.IsFocused) return;
                    float f = gv() + (float)(e.Delta.Y > 0 ? step : -step) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
                    sv(f); guard = true; box.Text = Panels.Inspector.PropertyRows.Fmt(gv()); guard = false; e.Handled = true;
                };
                Panels.Inspector.PropertyRows.AttachScrub(lbl, gv, f => { sv(f); guard = true; box.Text = Panels.Inspector.PropertyRows.Fmt(gv()); guard = false; }, step);
                Grid.SetColumn(lbl, i * 2); Grid.SetColumn(box, i * 2 + 1);
                g.Children.Add(lbl); g.Children.Add(box);
            }
            refresh = () =>
            {
                var v = get();
                guard = true;
                if (!boxes[0].IsFocused) boxes[0].Text = Panels.Inspector.PropertyRows.Fmt(v.X);
                if (!boxes[1].IsFocused) boxes[1].Text = Panels.Inspector.PropertyRows.Fmt(v.Y);
                if (!boxes[2].IsFocused) boxes[2].Text = Panels.Inspector.PropertyRows.Fmt(v.Z);
                guard = false;
            };
            refresh();
            return g;
        }

        // ---------------------------------------------------------------- owner-aware dialogs

        /// <summary>Modal sheet over <paramref name="owner"/>: returns the clicked button index (the last button on
        /// Esc / close) and the prompt text when <paramref name="promptInitial"/> is non-null.</summary>
        public static async Task<(int button, string text)> Ask(Window owner, string title, string message, string[] buttons, string promptInitial = null, int accentButton = 0, bool destructive = false)
        {
            var win = new Window
            {
                Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, ShowInTaskbar = false,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                SystemDecorations = SystemDecorations.BorderOnly, Background = Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }
            };
            int result = buttons.Length - 1;
            TextBox box = null;
            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(22, 18, 22, 16) };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
            if (!string.IsNullOrEmpty(message))
                panel.Children.Add(new TextBlock { Text = message, Classes = { "secondary" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 360 });
            if (promptInitial != null)
            {
                box = new TextBox { Text = promptInitial, Margin = new Thickness(0, 6, 0, 0) };
                panel.Children.Add(box);
                win.Opened += (s, e) => { box.Focus(); box.SelectAll(); };
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = new Button { Content = buttons[i], MinWidth = 84 };
                if (i == accentButton) { b.Classes.Add(destructive ? "danger" : "accent"); b.IsDefault = true; }
                if (i == buttons.Length - 1) b.IsCancel = true;
                b.Click += (s, e) => { result = idx; win.Close(); };
                row.Children.Add(b);
            }
            panel.Children.Add(row);
            win.Content = new Border
            {
                Background = Res("VxPanelRaisedBrush"), BorderBrush = Res("VxSeparatorBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Child = panel
            };
            if (owner != null && owner.IsVisible) await win.ShowDialog(owner);
            else { var tcs = new TaskCompletionSource<bool>(); win.Closed += (s, e) => tcs.TrySetResult(true); win.Show(); await tcs.Task; }
            return (result, box?.Text ?? "");
        }

        public static async Task<bool> Confirm(Window owner, string title, string message, string yes = "OK", string no = "Cancel", bool destructive = false)
            => (await Ask(owner, title, message, new[] { yes, no }, null, 0, destructive)).button == 0;

        public static Task Alert(Window owner, string title, string message) => Ask(owner, title, message, new[] { "OK" });

        public static async Task<string> Prompt(Window owner, string title, string message, string initial, string ok = "OK")
        {
            var r = await Ask(owner, title, message, new[] { ok, "Cancel" }, initial ?? "");
            return r.button == 0 ? r.text : null;
        }

        /// <summary>Fit a large editor window into the owner's screen (13" MacBooks) and keep it centred.</summary>
        public static void FitToScreen(Window w)
        {
            try
            {
                var screen = w.Screens?.ScreenFromWindow(w) ?? w.Screens?.Primary;
                if (screen == null) return;
                double scale = screen.Scaling > 0 ? screen.Scaling : 1;
                double maxW = screen.WorkingArea.Width / scale - 40, maxH = screen.WorkingArea.Height / scale - 40;
                if (maxW < 400 || maxH < 300) return;
                if (w.MinWidth > maxW) w.MinWidth = maxW;
                if (w.MinHeight > maxH) w.MinHeight = maxH;
                if (w.Width > maxW) w.Width = maxW;
                if (w.Height > maxH) w.Height = maxH;
            }
            catch { }
        }
    }
}
