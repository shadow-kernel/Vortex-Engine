using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Editor
{
    /// <summary>
    /// Branded startup splash. Shown topmost the instant the app launches; the engine init + editor +
    /// project browser load underneath it, then it fades out to reveal a ready workspace (no empty flash).
    /// In the standalone player it shows real load progress and stays up until the game's FIRST frame is
    /// on screen (the native window is revealed already-rendered), so there is no black flash.
    /// </summary>
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
        }

        /// <summary>Update just the status line ("Loading scene…", etc.).</summary>
        public void SetStatus(string text)
        {
            try { StatusText.Text = text; PumpRender(); } catch { }
        }

        /// <summary>Set the determinate progress (0..1) and optional status. The loader runs synchronously on the
        /// UI thread, so we force an immediate repaint after each call — otherwise the bar would only jump at the end.</summary>
        public void SetProgress(double fraction, string status = null)
        {
            try
            {
                if (fraction < 0) fraction = 0; else if (fraction > 1) fraction = 1;
                if (!string.IsNullOrEmpty(status)) StatusText.Text = status;
                Bar.Value = fraction * 100.0;
                PercentText.Text = ((int)Math.Round(fraction * 100.0)) + "%";
                PumpRender();
            }
            catch { }
        }

        // Force WPF to lay out + render the pending change right now, despite the synchronous loader on this thread.
        private void PumpRender()
        {
            try { Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Render); } catch { }
        }

        /// <summary>Fade out smoothly, then close.</summary>
        public void FadeOutAndClose()
        {
            try
            {
                var fade = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(280)));
                fade.Completed += (s, e) => { try { Close(); } catch { } };
                BeginAnimation(OpacityProperty, fade);
            }
            catch { try { Close(); } catch { } }
        }
    }
}
