using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;

namespace Editor.Editors.AudioEditor
{
    /// <summary>
    /// The Audio Mixer (issue #14): one strip per bus (Master/Music/SFX/Ambience/UI)
    /// with fader + dB label, mute/solo, and live RMS/peak meters fed by the native
    /// per-bus metering nodes; plus the ducking-rule editor. Every change goes through
    /// the same VortexAudio API the scripts use and persists to the project's
    /// AudioMixerConfig (ProjectSettings/AudioMixer.json). Built programmatically so
    /// it matches the engine's dark UI, like the Collision Editor.
    /// </summary>
    public sealed class AudioMixerWindow : Window
    {
        private static AudioMixerWindow _open;

        private readonly AudioMixerConfig _config;
        private readonly BusStrip[] _strips = new BusStrip[VortexAudio.BusCount];
        private readonly bool[] _solo = new bool[VortexAudio.BusCount];
        private readonly DispatcherTimer _meterTimer;
        private StackPanel _duckList;
        private bool _loading;

        private sealed class BusStrip
        {
            public Slider Fader;
            public TextBlock Db;
            public ToggleButton Mute;
            public ToggleButton Solo;
            public Rectangle RmsBar;
            public Rectangle PeakBar;
            public double PeakHold;
            public DateTime PeakHoldUntil;
        }

        public static AudioMixerWindow Open(Window owner)
        {
            if (_open != null) { try { _open.Activate(); return _open; } catch { _open = null; } }
            _open = new AudioMixerWindow { Owner = owner };
            _open.Show();
            return _open;
        }

        private AudioMixerWindow()
        {
            Title = "Audio Mixer";
            Width = 640; Height = 620; MinWidth = 560; MinHeight = 480;
            Background = Br("#FF161618");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _config = AudioMixerConfig.Load(ProjectData.Current?.Path);

            var root = new DockPanel { LastChildFill = true };

            var header = new Border { Background = Br("#FF1B1B1E"), BorderBrush = Br("#FF2C2C32"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 12, 16, 12) };
            var hs = new StackPanel();
            hs.Children.Add(new TextBlock { Text = "Audio Mixer", Foreground = Br("#FFF5F5F7"), FontSize = 15, FontWeight = FontWeights.Bold });
            hs.Children.Add(new TextBlock { Text = "Faders and mutes apply live (play mode + audition) and persist to the project.", Foreground = Br("#FF8A8A92"), FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0) });
            header.Child = hs;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 14, 16, 16) };
            var body = new StackPanel();
            scroll.Content = body;
            root.Children.Add(scroll);

            // ---- bus strips -----------------------------------------------------------
            var stripRow = new UniformGrid { Rows = 1, Columns = VortexAudio.BusCount };
            for (int i = 0; i < VortexAudio.BusCount; i++)
                stripRow.Children.Add(BuildStrip(i));
            body.Children.Add(stripRow);

            // ---- ducking rules --------------------------------------------------------
            body.Children.Add(new TextBlock { Text = "Ducking", Foreground = Br("#FFE9E9ED"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 2) });
            body.Children.Add(new TextBlock { Text = "While the trigger bus is loud, the target bus dips by the set amount (e.g. a stinger ducks the ambience).", Foreground = Br("#FF8A8A92"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            _duckList = new StackPanel();
            body.Children.Add(_duckList);
            var addDuck = new Button { Content = "+  Add ducking rule", Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 6, 10, 6), Background = Br("#FF26262B"), Foreground = Br("#FFC8C8CE"), BorderBrush = Br("#FF3A3A42"), HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand };
            addDuck.Click += (s, e) =>
            {
                _config.Ducks.Add(new AudioMixerConfig.DuckRule { TriggerBus = VortexAudio.BusMusic, TargetBus = VortexAudio.BusAmbience });
                RebuildDuckList();
                PersistAndApply();
            };
            body.Children.Add(addDuck);

            Content = root;

            LoadFromConfig();
            RebuildDuckList();

            // ~30 Hz meter refresh with peak-hold; display decays when nothing plays
            // (the native meters only move while audio flows through them).
            _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _meterTimer.Tick += (s, e) => UpdateMeters();
            _meterTimer.Start();

            Closed += (s, e) => { try { _meterTimer.Stop(); } catch { } _open = null; };
        }

        private UIElement BuildStrip(int bus)
        {
            var strip = new BusStrip();
            _strips[bus] = strip;

            var panel = new Border
            {
                Background = Br("#FF1B1B1E"),
                BorderBrush = Br("#FF2C2C32"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(8, 10, 8, 10)
            };
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            col.Children.Add(new TextBlock { Text = VortexAudio.BusNames[bus], Foreground = bus == 0 ? Br("#FFB48CFF") : Br("#FFC8C8CE"), FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });

            // Fader + meters side by side.
            var mid = new Grid { Height = 220, Margin = new Thickness(0, 8, 0, 4) };
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            strip.Fader = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = 0, Maximum = 1, Value = 1,
                SmallChange = 0.01, LargeChange = 0.1,
                Height = 220, Width = 26,
                Foreground = Br("#FF6C5CE7")
            };
            strip.Fader.ValueChanged += (s, e) => OnFader(bus);
            Grid.SetColumn(strip.Fader, 0);
            mid.Children.Add(strip.Fader);

            // Meter track: RMS fill + peak line, drawn bottom-up.
            var meterTrack = new Grid { Width = 10, Height = 220, Background = Br("#FF101013") };
            strip.RmsBar = new Rectangle { Fill = Br("#FF3FBF7F"), VerticalAlignment = VerticalAlignment.Bottom, Height = 0, Width = 10 };
            strip.PeakBar = new Rectangle { Fill = Br("#FFE7C55C"), VerticalAlignment = VerticalAlignment.Bottom, Height = 2, Width = 10, Margin = new Thickness(0, 0, 0, 0), Visibility = Visibility.Collapsed };
            meterTrack.Children.Add(strip.RmsBar);
            meterTrack.Children.Add(strip.PeakBar);
            Grid.SetColumn(meterTrack, 2);
            mid.Children.Add(meterTrack);
            col.Children.Add(mid);

            strip.Db = new TextBlock { Text = "0.0 dB", Foreground = Br("#FF8A8A92"), FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(strip.Db);

            var toggles = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            strip.Mute = MakeToggle("M", "#FFB76B7E");
            strip.Mute.Checked += (s, e) => OnMute(bus);
            strip.Mute.Unchecked += (s, e) => OnMute(bus);
            toggles.Children.Add(strip.Mute);
            if (bus != VortexAudio.BusMaster)
            {
                strip.Solo = MakeToggle("S", "#FFE7C55C");
                strip.Solo.Checked += (s, e) => OnSolo(bus);
                strip.Solo.Unchecked += (s, e) => OnSolo(bus);
                toggles.Children.Add(strip.Solo);
            }
            col.Children.Add(toggles);

            panel.Child = col;
            return panel;
        }

        private static ToggleButton MakeToggle(string text, string accent)
        {
            return new ToggleButton
            {
                Content = text,
                Width = 26, Height = 22,
                Margin = new Thickness(2, 0, 2, 0),
                Background = Br("#FF26262B"),
                Foreground = Br(accent),
                BorderBrush = Br("#FF3A3A42"),
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        // ---- change handlers (all route through the script-facing API) ---------------

        private void OnFader(int bus)
        {
            if (_loading) return;
            var v = (float)_strips[bus].Fader.Value;
            VortexAudio.SetBusVolume(bus, v);
            _strips[bus].Db.Text = DbLabel(v);
            _config.BusVolumes[bus] = v;
            PersistDeferred();
        }

        private void OnMute(int bus)
        {
            if (_loading) return;
            _config.BusMutes[bus] = _strips[bus].Mute.IsChecked == true;
            ApplyMuteSolo();
            PersistDeferred();
        }

        private void OnSolo(int bus)
        {
            if (_loading) return;
            _solo[bus] = _strips[bus].Solo?.IsChecked == true;
            ApplyMuteSolo();
        }

        /// <summary>Solo is a live tool (not persisted): when any child is soloed, all
        /// other children are muted; user mutes still apply on top.</summary>
        private void ApplyMuteSolo()
        {
            bool anySolo = false;
            for (int i = 1; i < VortexAudio.BusCount; i++) if (_solo[i]) anySolo = true;

            for (int i = 0; i < VortexAudio.BusCount; i++)
            {
                bool mute = _config.BusMutes[i];
                if (anySolo && i != VortexAudio.BusMaster && !_solo[i]) mute = true;
                VortexAudio.SetBusMute(i, mute);
            }
        }

        // ---- ducking editor -----------------------------------------------------------

        private void RebuildDuckList()
        {
            _duckList.Children.Clear();
            foreach (var rule in _config.Ducks)
                _duckList.Children.Add(BuildDuckRow(rule));
            if (_config.Ducks.Count == 0)
                _duckList.Children.Add(new TextBlock { Text = "No rules — every bus plays at its fader level.", Foreground = Br("#FF66666E"), FontSize = 11 });
        }

        private UIElement BuildDuckRow(AudioMixerConfig.DuckRule rule)
        {
            var row = new Border { Background = Br("#FF1B1B1E"), BorderBrush = Br("#FF2C2C32"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6) };
            var line = new StackPanel { Orientation = Orientation.Horizontal };

            ComboBox MakeBusCombo(int selected, Action<int> onChange)
            {
                var combo = new ComboBox { Width = 92, Margin = new Thickness(0, 0, 6, 0) };
                foreach (var name in VortexAudio.BusNames) combo.Items.Add(name);
                combo.SelectedIndex = selected >= 0 && selected < VortexAudio.BusCount ? selected : 0;
                combo.SelectionChanged += (s, e) => { if (!_loading) { onChange(combo.SelectedIndex); PersistAndApply(); } };
                return combo;
            }

            TextBox MakeNum(float value, string tip, double width, Action<float> onChange)
            {
                var box = new TextBox { Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = width, Margin = new Thickness(0, 0, 6, 0), Background = Br("#FF101013"), Foreground = Br("#FFC8C8CE"), BorderBrush = Br("#FF3A3A42"), ToolTip = tip, Padding = new Thickness(4, 2, 4, 2) };
                box.LostFocus += (s, e) =>
                {
                    if (float.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                    {
                        onChange(f);
                        PersistAndApply();
                    }
                };
                return box;
            }

            line.Children.Add(new TextBlock { Text = "When", Foreground = Br("#FF8A8A92"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            line.Children.Add(MakeBusCombo(rule.TriggerBus, v => rule.TriggerBus = v));
            line.Children.Add(new TextBlock { Text = "duck", Foreground = Br("#FF8A8A92"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            line.Children.Add(MakeBusCombo(rule.TargetBus, v => rule.TargetBus = v));
            line.Children.Add(MakeNum(rule.DuckDb, "Attenuation in dB (negative, e.g. -12)", 44, v => rule.DuckDb = v));
            line.Children.Add(new TextBlock { Text = "dB  atk", Foreground = Br("#FF8A8A92"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            line.Children.Add(MakeNum(rule.AttackMs, "Attack (ms)", 44, v => rule.AttackMs = v));
            line.Children.Add(new TextBlock { Text = "rel", Foreground = Br("#FF8A8A92"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            line.Children.Add(MakeNum(rule.ReleaseMs, "Release (ms)", 44, v => rule.ReleaseMs = v));

            var remove = new Button { Content = "✕", FontSize = 11, Foreground = Br("#FFB76B7E"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Remove rule", Margin = new Thickness(2, 0, 0, 0) };
            remove.Click += (s, e) => { _config.Ducks.Remove(rule); RebuildDuckList(); PersistAndApply(); };
            line.Children.Add(remove);

            row.Child = line;
            return row;
        }

        // ---- meters ---------------------------------------------------------------------

        private void UpdateMeters()
        {
            const double track = 220.0;
            for (int i = 0; i < VortexAudio.BusCount; i++)
            {
                VortexAudio.GetBusLevels(i, out var peak, out var rms);

                // sqrt curve reads better than linear for quiet material.
                var rmsH = Math.Min(1.0, Math.Sqrt(rms)) * track;
                var strip = _strips[i];
                // Display-side decay so meters fall to zero when nothing plays
                // (the native values only update while audio flows).
                strip.RmsBar.Height = Math.Max(rmsH, strip.RmsBar.Height * 0.85);

                var peakH = Math.Min(1.0, Math.Sqrt(peak)) * track;
                if (peakH >= strip.PeakHold)
                {
                    strip.PeakHold = peakH;
                    strip.PeakHoldUntil = DateTime.UtcNow.AddSeconds(1.0);
                }
                else if (DateTime.UtcNow > strip.PeakHoldUntil)
                {
                    strip.PeakHold *= 0.9;
                }
                if (strip.PeakHold > 1.5)
                {
                    strip.PeakBar.Visibility = Visibility.Visible;
                    strip.PeakBar.Margin = new Thickness(0, 0, 0, Math.Min(track - 2, strip.PeakHold));
                }
                else strip.PeakBar.Visibility = Visibility.Collapsed;
            }
        }

        // ---- config load/persist ----------------------------------------------------------

        private void LoadFromConfig()
        {
            _loading = true;
            for (int i = 0; i < VortexAudio.BusCount; i++)
            {
                _strips[i].Fader.Value = _config.BusVolumes[i];
                _strips[i].Db.Text = DbLabel(_config.BusVolumes[i]);
                _strips[i].Mute.IsChecked = _config.BusMutes[i];
            }
            _loading = false;
            _config.Apply(); // window state == native state from the first frame
        }

        private DispatcherTimer _persistTimer;
        /// <summary>Fader drags fire many times per second — save at most twice a second.</summary>
        private void PersistDeferred()
        {
            if (_persistTimer == null)
            {
                _persistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _persistTimer.Tick += (s, e) => { _persistTimer.Stop(); _config.Save(ProjectData.Current?.Path); };
            }
            _persistTimer.Stop();
            _persistTimer.Start();
        }

        private void PersistAndApply()
        {
            _config.Save(ProjectData.Current?.Path);
            _config.Apply();
            ApplyMuteSolo(); // re-overlay live solo state after Apply reset the mutes
        }

        private static string DbLabel(float linear)
        {
            if (linear <= 0.0001f) return "-inf dB";
            return (20.0 * Math.Log10(linear)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
        }

        private static SolidColorBrush Br(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }
}
