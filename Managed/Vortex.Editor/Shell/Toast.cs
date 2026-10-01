using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VortexEditor.Controls;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Transient bottom-centre notification (one toast at a time, auto-dismiss). Shown in a popup so it stays visible
    /// over the native 3D view too (Avalonia content never draws above the embedded Metal view).
    /// </summary>
    public sealed class ToastHost : Panel
    {
        private readonly TextBlock _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        private readonly VxIcon _icon = new VxIcon { Icon = "Check", Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0) };
        private readonly Popup _popup;
        private DispatcherTimer _timer;

        /// <summary>Last message shown (smoke checks read it).</summary>
        public string LastMessage { get; private set; }

        public ToastHost()
        {
            IsHitTestVisible = false;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_icon);
            row.Children.Add(_text);
            var border = new Border { Classes = { "toast" }, Child = row };
            _popup = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.AnchorAndGravity,
                PlacementAnchor = PopupAnchor.Bottom,
                PlacementGravity = PopupGravity.Top,
                VerticalOffset = -36,
                IsLightDismissEnabled = false,
                Child = border,
            };
            Children.Add(_popup);
        }

        public void Show(string message, double seconds = 2.2)
        {
            Dispatcher.UIThread.Post(() =>
            {
                LastMessage = message;
                if (TopLevel.GetTopLevel(this) == null || !IsEffectivelyVisible) return;
                bool warn = message != null && (message.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || message.StartsWith("No ", StringComparison.Ordinal) || message.StartsWith("Open a project", StringComparison.Ordinal) || message.StartsWith("Select ", StringComparison.Ordinal));
                _icon.Icon = warn ? "Warning" : "Check";
                _icon.Foreground = (IBrush)Application.Current.FindResource(warn ? "VxYellowBrush" : "VxGreenBrush");
                _text.Text = message;
                _popup.IsOpen = false;
                _popup.IsOpen = true;
                _timer?.Stop();
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                _timer.Tick += (s, e) => { _timer.Stop(); _popup.IsOpen = false; };
                _timer.Start();
            });
        }

        public void Hide() { _timer?.Stop(); _popup.IsOpen = false; }
    }
}
