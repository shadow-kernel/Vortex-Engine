using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace VortexEditor.Shell
{
    /// <summary>Transient bottom-centre notification host (one toast at a time, auto-dismiss).</summary>
    public sealed class ToastHost : Border
    {
        private readonly TextBlock _text = new TextBlock();
        private DispatcherTimer _timer;

        public ToastHost()
        {
            Classes.Add("toast");
            Child = _text;
            IsVisible = false;
            IsHitTestVisible = false;
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            Margin = new Thickness(0, 0, 0, 40);
        }

        public void Show(string message, double seconds = 2.2)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _text.Text = message;
                IsVisible = true;
                Opacity = 1;
                _timer?.Stop();
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                _timer.Tick += (s, e) => { _timer.Stop(); IsVisible = false; };
                _timer.Start();
            });
        }
    }
}
