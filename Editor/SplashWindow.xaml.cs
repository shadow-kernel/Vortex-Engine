using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Editor
{
    /// <summary>
    /// Branded startup splash. Shown topmost the instant the app launches; the engine init + editor +
    /// project browser load underneath it, then it fades out to reveal a ready workspace (no empty flash).
    /// </summary>
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                VersionText.Text = v != null ? ("v" + v.Major + "." + v.Minor + " · Preview") : "Preview";
            }
            catch { VersionText.Text = "Preview"; }
        }

        /// <summary>Optionally update the status line ("Loading…", "Starting engine…", etc.).</summary>
        public void SetStatus(string text)
        {
            try { StatusText.Text = text; } catch { }
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
