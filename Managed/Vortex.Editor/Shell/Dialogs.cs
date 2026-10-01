using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace VortexEditor.Shell
{
    /// <summary>Sheet-style dialogs (alert, confirm, choose, prompt, text) in the editor's own visual language.</summary>
    public static class Dialogs
    {
        public static Window Owner { get; set; }

        /// <summary>The dialog currently on screen (smoke checks close it); null when none is open.</summary>
        public static Window Current { get; private set; }

        public static Task Alert(string title, string message, string button = "OK", Window owner = null)
            => Run(title, message, new[] { button }, null, null, 0, owner).ContinueWith(_ => { });

        public static async Task<bool> Confirm(string title, string message, string yes = "OK", string no = "Cancel", bool destructive = false, Window owner = null)
        {
            var r = await Run(title, message, new[] { yes, no }, null, destructive ? yes : null, 0, owner);
            return r.button == 0;
        }

        /// <summary>Several choices; returns the index of the clicked button (the LAST button is the cancel button,
        /// also returned for Esc / closing the sheet). <paramref name="destructiveIndex"/> paints that button red.</summary>
        public static async Task<int> Choose(string title, string message, string[] buttons, int destructiveIndex = -1, Window owner = null)
        {
            if (buttons == null || buttons.Length == 0) buttons = new[] { "OK" };
            var r = await Run(title, message, buttons, null, destructiveIndex >= 0 && destructiveIndex < buttons.Length ? buttons[destructiveIndex] : null, destructiveIndex, owner);
            return r.button;
        }

        public static async Task<string> Prompt(string title, string message, string initial = "", string ok = "OK", Window owner = null)
        {
            var r = await Run(title, message, new[] { ok, "Cancel" }, initial ?? "", null, 0, owner);
            return r.button == 0 ? r.text : null;
        }

        /// <summary>A read-only, scrollable, selectable text sheet (help, shortcut lists, long messages).</summary>
        public static async Task ShowText(string title, string text, double width = 560, double height = 480, Window owner = null)
        {
            var win = NewWindow(title, width);
            win.SizeToContent = SizeToContent.Manual;
            win.Height = height;
            var box = new SelectableTextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)Application.Current.FindResource("VxMono"), FontSize = 12 };
            var close = new Button { Content = "Close", MinWidth = 84, IsDefault = true, IsCancel = true, Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            close.Click += (s, e) => win.Close();
            var dock = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
            var head = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(close, Dock.Bottom);
            dock.Children.Add(head); dock.Children.Add(close);
            dock.Children.Add(new ScrollViewer { Content = box, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            win.Content = Frame(dock);
            await ShowModal(win, owner);
        }

        private static Window ActiveOwner()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
                foreach (var w in d.Windows) if (w.IsActive && w.IsVisible && !ReferenceEquals(w, Current)) return w;
            return Owner;
        }

        private static Window NewWindow(string title, double width) => new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SystemDecorations = SystemDecorations.BorderOnly,
            ShowInTaskbar = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }
        };

        private static Border Frame(Control child) => new Border
        {
            Background = (IBrush)Application.Current.FindResource("VxPanelRaisedBrush"),
            BorderBrush = (IBrush)Application.Current.FindResource("VxSeparatorBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = child
        };

        private static async Task<(int button, string text)> Run(string title, string message, string[] buttons, string promptInitial, string destructiveButton, int destructiveIndex = 0, Window owner = null)
        {
            var win = NewWindow(title, 420);
            int result = buttons.Length - 1;
            TextBox box = null;
            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(22, 18, 22, 16) };
            panel.Children.Add(new PathIcon { Data = (Geometry)Application.Current.FindResource("IconVortex"), Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (IBrush)Application.Current.FindResource("VxAccentBrush") });
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
            if (!string.IsNullOrEmpty(message))
                panel.Children.Add(new TextBlock { Text = message, Classes = { "secondary" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 360 });
            if (promptInitial != null)
            {
                box = new TextBox { Text = promptInitial, Margin = new Thickness(0, 6, 0, 0) };
                var b = box;
                b.AttachedToVisualTree += (s, e) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { b.Focus(); b.SelectAll(); });
                b.KeyDown += (s, e) => { if (e.Key == Key.Return) { result = 0; win.Close(); e.Handled = true; } };
                panel.Children.Add(box);
            }
            var row = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            // macOS order: cancel on the left, the default action on the right.
            var order = new List<int>();
            for (int i = buttons.Length - 1; i >= 0; i--) order.Add(i);
            foreach (int i in order)
            {
                int idx = i;
                var b = new Button { Content = buttons[i], MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
                bool danger = destructiveButton != null && i == destructiveIndex;
                if (i == 0) b.Classes.Add(danger ? "danger" : "accent");
                else if (danger) b.Classes.Add("danger");
                b.Click += (s, e) => { result = idx; win.Close(); };
                if (i == 0) b.IsDefault = true; else if (i == buttons.Length - 1) b.IsCancel = true;
                row.Children.Add(b);
            }
            panel.Children.Add(row);
            win.Content = Frame(panel);
            win.KeyDown += (s, e) => { if (e.Key == Key.Escape) { result = buttons.Length - 1; win.Close(); } };
            await ShowModal(win, owner);
            return (result, box?.Text ?? "");
        }

        private static async Task ShowModal(Window win, Window explicitOwner = null)
        {
            // over the window the user is working in (a tool window's prompt must not open behind it)
            var owner = explicitOwner ?? ActiveOwner();
            Current = win;
            try
            {
                if (owner != null && owner.IsVisible) await win.ShowDialog(owner);
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    win.Closed += (s, e) => tcs.TrySetResult(true);
                    win.Show();
                    await tcs.Task;
                }
            }
            finally { if (ReferenceEquals(Current, win)) Current = null; }
        }
    }
}
