using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VortexEditor.Shell
{
    /// <summary>Sheet-style dialogs (alert, confirm, prompt) in the editor's own visual language.</summary>
    public static class Dialogs
    {
        public static Window Owner { get; set; }

        public static Task Alert(string title, string message, string button = "OK")
            => Run(title, message, new[] { button }, null, null).ContinueWith(_ => { });

        public static async Task<bool> Confirm(string title, string message, string yes = "OK", string no = "Cancel", bool destructive = false)
        {
            var r = await Run(title, message, new[] { yes, no }, null, destructive ? yes : null);
            return r.button == 0;
        }

        public static async Task<string> Prompt(string title, string message, string initial = "", string ok = "OK")
        {
            var r = await Run(title, message, new[] { ok, "Cancel" }, initial ?? "", null);
            return r.button == 0 ? r.text : null;
        }

        private static async Task<(int button, string text)> Run(string title, string message, string[] buttons, string promptInitial, string destructiveButton)
        {
            var owner = Owner;
            var win = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly,
                ShowInTaskbar = false,
                Background = Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }
            };
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
                panel.Children.Add(box);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = new Button { Content = buttons[i], MinWidth = 84 };
                if (i == 0) b.Classes.Add(destructiveButton != null ? "danger" : "accent");
                b.Click += (s, e) => { result = idx; win.Close(); };
                if (i == 0) b.IsDefault = true; else if (i == buttons.Length - 1) b.IsCancel = true;
                row.Children.Add(b);
            }
            panel.Children.Add(row);
            win.Content = new Border
            {
                Background = (IBrush)Application.Current.FindResource("VxPanelRaisedBrush"),
                BorderBrush = (IBrush)Application.Current.FindResource("VxSeparatorBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = panel
            };
            if (owner != null) await win.ShowDialog(owner); else { win.Show(); var tcs = new TaskCompletionSource<bool>(); win.Closed += (s, e) => tcs.TrySetResult(true); await tcs.Task; }
            return (result, box?.Text ?? "");
        }
    }
}
