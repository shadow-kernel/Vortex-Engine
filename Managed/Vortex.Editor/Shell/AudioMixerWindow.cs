using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.DllWrapper;
using VortexEditor.Controls;
using VortexEditor.Shell.Audio;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The Audio Mixer (port of the Windows editor's mixer): one strip per bus (Master / Music / SFX / Ambience / UI)
    /// with a fader + dB readout, mute and solo, and live RMS / peak-hold meters from the native per-bus metering; the
    /// ducking-rule editor (trigger bus, target bus, depth, attack, release, threshold); the Steam Audio switch. Every
    /// change goes through the same VortexAudio API the scripts use, applies live, and persists to the project's
    /// ProjectSettings/AudioMixer.json. Solo is a live tool (not saved) and is released when the window closes.
    /// </summary>
    public sealed class AudioMixerWindow : Window
    {
        /// <summary>Open the mixer (one per editor — an open one is brought to front).</summary>
        public static void Open()
        {
            if (string.IsNullOrEmpty(ProjectData.Current?.Path)) { EditorCommands.Toast("Open a project first"); return; }
            var existing = EditorKit.OpenWindows<AudioMixerWindow>().FirstOrDefault();
            if (existing != null) { existing.Activate(); return; }
            EditorWindows.Show(new AudioMixerWindow());
        }

        private sealed class Strip
        {
            public Fader Fader;
            public TextBlock Db;
            public ToggleButton Mute, Solo;
            public LevelMeter Meter;
            public double Rms, PeakHold;
            public DateTime PeakHoldUntil;
        }

        private readonly string _root = ProjectData.Current?.Path;
        private readonly AudioMixerConfig _cfg;
        private readonly Strip[] _strips = new Strip[VortexAudio.BusCount];
        private readonly bool[] _solo = new bool[VortexAudio.BusCount];
        private readonly StackPanel _duckList = new StackPanel { Spacing = 6 };
        private readonly DispatcherTimer _meterTimer;
        private DispatcherTimer _persistTimer;
        private readonly TextBlock _saved = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center };
        private bool _loading, _closeHandled;

        /// <summary>The config being edited (tests / tools).</summary>
        internal AudioMixerConfig Config => _cfg;

        public AudioMixerWindow()
        {
            Title = "Audio Mixer";
            Width = 660; Height = 640; MinWidth = 560; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            _cfg = AudioMixerConfig.Load(_root);

            var root = new DockPanel();
            var header = new Border { Classes = { "hairline-bottom" }, Background = EditorKit.Brush("VxToolbarBrush"), Padding = new Thickness(16, 12) };
            var hs = new StackPanel { Spacing = 3 };
            hs.Children.Add(new TextBlock { Text = "Audio Mixer", FontSize = 15, FontWeight = FontWeight.SemiBold });
            hs.Children.Add(new TextBlock { Text = "Faders and mutes apply live (play mode + audition) and persist to the project.", Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap });
            header.Child = hs;
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

            var footer = new Border { Classes = { "hairline-top" }, Background = EditorKit.Brush("VxToolbarBrush"), Padding = new Thickness(16, 8) };
            var fg = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            _saved.Text = "Saved to " + AudioMixerConfig.RelativePath;
            fg.Children.Add(_saved);
            var close = new Button { Content = "Close", MinWidth = 80 };
            close.Click += (s, e) => Close();
            Grid.SetColumn(close, 1); fg.Children.Add(close);
            footer.Child = fg;
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

            var body = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
            var strips = new UniformGrid { Rows = 1, Columns = VortexAudio.BusCount };
            for (int i = 0; i < VortexAudio.BusCount; i++) strips.Children.Add(BuildStrip(i));
            body.Children.Add(strips);

            body.Children.Add(new TextBlock { Text = "Ducking", FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 18, 0, 2) });
            body.Children.Add(new TextBlock { Text = "While the trigger bus is loud, the target bus dips by the set amount (e.g. a stinger ducks the ambience).", Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            body.Children.Add(_duckList);
            var addDuck = new Button { Content = "+  Add ducking rule", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            addDuck.Click += (s, e) => AddDuckRule();
            body.Children.Add(addDuck);

            body.Children.Add(new TextBlock { Text = "Spatial audio", FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 18, 0, 4) });
            var steam = new CheckBox { Content = "Steam Audio spatialisation (HRTF / occlusion) for sources that enable it, when available", IsChecked = _cfg.SteamAudioEnabled };
            steam.IsCheckedChanged += (s, e) => { if (_loading) return; _cfg.SteamAudioEnabled = steam.IsChecked == true; PersistAndApply(); };
            body.Children.Add(steam);
            root.Children.Add(new ScrollViewer { Content = body });
            Content = root;

            LoadFromConfig();
            RebuildDucks();

            // ~30 Hz meters with peak hold; the display decays when nothing plays (native levels only move while audio flows)
            _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (s, e) => UpdateMeters());
            _meterTimer.Start();
            Closing += (s, e) => ReleaseSolo();
            Closed += (s, e) => { _meterTimer.Stop(); FlushPersist(); EditorSession.Instance.ProjectClosed -= OnProjectClosed; };
            EditorSession.Instance.ProjectClosed += OnProjectClosed;
        }

        private void OnProjectClosed() => Dispatcher.UIThread.Post(Close);

        // ---------------------------------------------------------------- strips
        private Control BuildStrip(int bus)
        {
            var st = new Strip();
            _strips[bus] = st;
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
            col.Children.Add(new TextBlock { Text = VortexAudio.BusNames[bus], FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = bus == VortexAudio.BusMaster ? EditorKit.Brush("VxPurpleBrush") : EditorKit.Brush("VxTextBrush") });

            var mid = new Grid { Height = 220, Margin = new Thickness(0, 6, 0, 2), ColumnDefinitions = new ColumnDefinitions("Auto,8,Auto"), HorizontalAlignment = HorizontalAlignment.Center };
            st.Fader = new Fader { Height = 220 };
            ToolTip.SetTip(st.Fader, VortexAudio.BusNames[bus] + " volume — drag, scroll, double-click for 0 dB");
            st.Fader.ValueChanged += v => OnFader(bus);
            mid.Children.Add(st.Fader);
            st.Meter = new LevelMeter { Height = 220 };
            ToolTip.SetTip(st.Meter, "Level (RMS) with peak hold");
            Grid.SetColumn(st.Meter, 2); mid.Children.Add(st.Meter);
            col.Children.Add(mid);

            st.Db = new TextBlock { Text = "0.0 dB", Classes = { "small", "secondary" }, HorizontalAlignment = HorizontalAlignment.Center, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            ToolTip.SetTip(st.Db, "Double-click: reset to 0 dB");
            st.Db.DoubleTapped += (s, e) => st.Fader.Value = 1.0;
            col.Children.Add(st.Db);

            var toggles = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            st.Mute = Toggle("M", "Mute", "VxRedBrush");
            st.Mute.IsCheckedChanged += (s, e) => OnMute(bus);
            toggles.Children.Add(st.Mute);
            if (bus != VortexAudio.BusMaster)
            {
                st.Solo = Toggle("S", "Solo (live only — not saved)", "VxYellowBrush");
                st.Solo.IsCheckedChanged += (s, e) => OnSolo(bus);
                toggles.Children.Add(st.Solo);
            }
            col.Children.Add(toggles);
            return new Border { Classes = { "card" }, Margin = new Thickness(3, 0), Padding = new Thickness(8, 10), Child = col };
        }

        private static ToggleButton Toggle(string text, string tip, string onBrush)
        {
            // the theme's checked look is a neutral grey: mute / solo get their own colour on an inner face
            var label = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Brush("VxTextSecondaryBrush") };
            var face = new Border { Width = 28, Height = 24, CornerRadius = new CornerRadius(5), Background = EditorKit.Brush("VxControlBrush"), Child = label };
            var t = new ToggleButton { Content = face, Width = 28, Height = 24, MinHeight = 22, Padding = new Thickness(0) };
            ToolTip.SetTip(t, tip);
            t.IsCheckedChanged += (s, e) =>
            {
                bool on = t.IsChecked == true;
                face.Background = on ? EditorKit.Brush(onBrush) : EditorKit.Brush("VxControlBrush");
                label.Foreground = on ? Brushes.Black : EditorKit.Brush("VxTextSecondaryBrush");
            };
            return t;
        }

        private void OnFader(int bus)
        {
            if (_loading) return;
            float v = (float)_strips[bus].Fader.Value;
            try { VortexAudio.SetBusVolume(bus, v); } catch { }
            _strips[bus].Db.Text = DbLabel(v);
            _cfg.BusVolumes[bus] = v;
            PersistDeferred();
        }

        private void OnMute(int bus)
        {
            if (_loading) return;
            _cfg.BusMutes[bus] = _strips[bus].Mute.IsChecked == true;
            ApplyMuteSolo();
            PersistDeferred();
        }

        private void OnSolo(int bus)
        {
            if (_loading) return;
            _solo[bus] = _strips[bus].Solo?.IsChecked == true;
            ApplyMuteSolo();
        }

        /// <summary>Solo: while any child bus is soloed every other child bus is muted; user mutes still apply on top.</summary>
        private void ApplyMuteSolo()
        {
            bool anySolo = false;
            for (int i = 1; i < VortexAudio.BusCount; i++) if (_solo[i]) anySolo = true;
            for (int i = 0; i < VortexAudio.BusCount; i++)
            {
                bool mute = _cfg.BusMutes[i] || (anySolo && i != VortexAudio.BusMaster && !_solo[i]);
                try { VortexAudio.SetBusMute(i, mute); } catch { }
            }
        }

        private void ReleaseSolo()
        {
            if (_closeHandled) return;
            _closeHandled = true;
            bool any = false;
            for (int i = 0; i < _solo.Length; i++) { any |= _solo[i]; _solo[i] = false; }
            if (any) ApplyMuteSolo();
        }

        /// <summary>Solo a bus from code (tests / tools).</summary>
        internal void SetSolo(int bus, bool on) { if (_strips[bus]?.Solo != null) _strips[bus].Solo.IsChecked = on; }

        // ---------------------------------------------------------------- meters
        private void UpdateMeters()
        {
            var now = DateTime.UtcNow;
            for (int i = 0; i < VortexAudio.BusCount; i++)
            {
                var st = _strips[i];
                float peak = 0f, rms = 0f;
                try { VortexAudio.GetBusLevels(i, out peak, out rms); } catch { }
                double r = Math.Min(1.0, Math.Sqrt(Math.Max(0, rms)));   // sqrt reads better than linear for quiet material
                st.Rms = Math.Max(r, st.Rms * 0.85);
                double p = Math.Min(1.0, Math.Sqrt(Math.Max(0, peak)));
                if (p >= st.PeakHold) { st.PeakHold = p; st.PeakHoldUntil = now.AddSeconds(1.0); }
                else if (now > st.PeakHoldUntil) st.PeakHold *= 0.9;
                if (Math.Abs(st.Meter.Rms - st.Rms) > 0.001 || Math.Abs(st.Meter.PeakHold - st.PeakHold) > 0.001)
                {
                    st.Meter.Rms = st.Rms; st.Meter.PeakHold = st.PeakHold;
                    st.Meter.InvalidateVisual();
                }
            }
        }

        // ---------------------------------------------------------------- ducking rules
        /// <summary>Add a rule (Music ducks Ambience by default), persist and apply it.</summary>
        internal void AddDuckRule()
        {
            _cfg.Ducks.Add(new AudioMixerConfig.DuckRule { TriggerBus = VortexAudio.BusMusic, TargetBus = VortexAudio.BusAmbience });
            RebuildDucks();
            PersistAndApply();
        }

        internal void RemoveDuckRule(int index)
        {
            if (index < 0 || index >= _cfg.Ducks.Count) return;
            _cfg.Ducks.RemoveAt(index);
            RebuildDucks();
            PersistAndApply();
        }

        private void RebuildDucks()
        {
            _duckList.Children.Clear();
            if (_cfg.Ducks.Count == 0)
                _duckList.Children.Add(new TextBlock { Text = "No rules — every bus plays at its fader level.", Classes = { "small", "tertiary" } });
            foreach (var rule in _cfg.Ducks) _duckList.Children.Add(BuildDuckRow(rule));
        }

        private Control BuildDuckRow(AudioMixerConfig.DuckRule rule)
        {
            ComboBox Bus(int selected, Action<int> set)
            {
                var c = new ComboBox { MinWidth = 96, MinHeight = 22, VerticalAlignment = VerticalAlignment.Center };
                foreach (var n in VortexAudio.BusNames) c.Items.Add(n);
                c.SelectedIndex = selected >= 0 && selected < VortexAudio.BusCount ? selected : 0;
                c.SelectionChanged += (s, e) => { if (c.SelectedIndex >= 0) { set(c.SelectedIndex); PersistAndApply(); } };
                return c;
            }
            TextBox Num(Func<float> get, Action<float> set, string tip, float min, float max)
            {
                var b = new TextBox { Classes = { "number" }, MinWidth = 50, Width = 54, MinHeight = 22, VerticalAlignment = VerticalAlignment.Center, Text = get().ToString("0.##", CultureInfo.InvariantCulture) };
                ToolTip.SetTip(b, tip);
                void Commit()
                {
                    if (float.TryParse(b.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) { f = Math.Max(min, Math.Min(max, f)); if (f != get()) { set(f); PersistAndApply(); } }
                    b.Text = get().ToString("0.##", CultureInfo.InvariantCulture);
                }
                b.LostFocus += (s, e) => Commit();
                b.KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Return) { Commit(); e.Handled = true; } };
                return b;
            }
            TextBlock L(string t) => new TextBlock { Text = t, Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };

            var line = new WrapPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(L("When")); line.Children.Add(Bus(rule.TriggerBus, v => rule.TriggerBus = v));
            line.Children.Add(L("plays, duck")); line.Children.Add(Bus(rule.TargetBus, v => rule.TargetBus = v));
            line.Children.Add(L("by")); line.Children.Add(Num(() => rule.DuckDb, v => rule.DuckDb = v, "Attenuation in dB (negative, e.g. -12)", -60f, -0.1f)); line.Children.Add(L("dB"));
            line.Children.Add(L("atk")); line.Children.Add(Num(() => rule.AttackMs, v => rule.AttackMs = v, "Attack (ms)", 0f, 5000f)); line.Children.Add(L("ms"));
            line.Children.Add(L("rel")); line.Children.Add(Num(() => rule.ReleaseMs, v => rule.ReleaseMs = v, "Release (ms)", 0f, 10000f)); line.Children.Add(L("ms"));
            line.Children.Add(L("thr")); line.Children.Add(Num(() => rule.Threshold, v => rule.Threshold = v, "Trigger threshold: RMS level (0..1) above which the duck engages", 0f, 1f));
            var remove = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Close", Width = 11, Height = 11 }, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(remove, "Remove rule");
            remove.Click += (s, e) => { _cfg.Ducks.Remove(rule); RebuildDucks(); PersistAndApply(); };
            line.Children.Add(remove);
            return new Border { Classes = { "card" }, Padding = new Thickness(8, 6), Child = line };
        }

        // ---------------------------------------------------------------- load / persist
        private void LoadFromConfig()
        {
            _loading = true;
            try
            {
                for (int i = 0; i < VortexAudio.BusCount; i++)
                {
                    _strips[i].Fader.Value = _cfg.BusVolumes[i];
                    _strips[i].Db.Text = DbLabel(_cfg.BusVolumes[i]);
                    _strips[i].Mute.IsChecked = _cfg.BusMutes[i];
                }
            }
            finally { _loading = false; }
            try { _cfg.Apply(); } catch { }   // window state == native state from the first frame
        }

        /// <summary>Fader drags fire many times per second — save at most twice a second.</summary>
        private void PersistDeferred()
        {
            if (_persistTimer == null)
                _persistTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (s, e) => FlushPersist());
            _persistTimer.Stop();
            _persistTimer.Start();
            _saved.Text = "Saving…";
        }

        private void FlushPersist()
        {
            if (_persistTimer == null || !_persistTimer.IsEnabled) return;
            _persistTimer.Stop();
            Save();
        }

        private void PersistAndApply()
        {
            Save();
            try { _cfg.Apply(); } catch { }
            ApplyMuteSolo();   // Apply resets the mutes: re-overlay the live solo state
        }

        private void Save()
        {
            try { _cfg.Save(_root); _saved.Text = "Saved to " + AudioMixerConfig.RelativePath; }
            catch (Exception ex) { _saved.Text = "Could not save: " + ex.Message; }
        }

        private static string DbLabel(float linear)
            => linear <= 0.0001f ? "-∞ dB" : (20.0 * Math.Log10(linear)).ToString("0.0", CultureInfo.InvariantCulture) + " dB";
    }
}
