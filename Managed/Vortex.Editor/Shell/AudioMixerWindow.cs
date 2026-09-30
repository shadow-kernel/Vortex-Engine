using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>Bus volumes / mutes / ducking rules of the project's audio mixer (ProjectSettings/AudioMixer.json).</summary>
    public sealed class AudioMixerWindow : Window
    {
        /// <summary>Open this window (owned by the main window).</summary>
        public static void Open() => EditorWindows.Show(new AudioMixerWindow());

        private readonly AudioMixerConfig _cfg;
        private readonly string _root = ProjectData.Current?.Path;

        public AudioMixerWindow()
        {
            Title = "Audio Mixer"; Width = 620; Height = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            _cfg = AudioMixerConfig.Load(_root);
            var root = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14) };
            var save = new Button { Content = "Save", Classes = { "accent" }, MinWidth = 90 }; save.Click += (s, e) => { try { _cfg.Save(_root); _cfg.Apply(); EditorCommands.Toast("Mixer saved"); } catch (Exception ex) { EditorCommands.Fail("Save mixer", ex); } };
            var close = new Button { Content = "Close", MinWidth = 90 }; close.Click += (s, e) => Close();
            buttons.Children.Add(close); buttons.Children.Add(save);
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

            var strips = new Grid { Margin = new Thickness(14, 14, 14, 0), ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*") };
            for (int i = 0; i < VortexAudio.BusCount; i++)
            {
                int bus = i;
                var strip = new Border { Classes = { "card" }, Margin = new Thickness(3), Padding = new Thickness(8) };
                var sp = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                sp.Children.Add(new TextBlock { Text = VortexAudio.BusNames[i], FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
                var slider = new Slider { Minimum = 0, Maximum = 1, Value = _cfg.BusVolumes[i], Orientation = Orientation.Vertical, Height = 180, HorizontalAlignment = HorizontalAlignment.Center };
                var val = new TextBlock { Text = Db(_cfg.BusVolumes[i]), Classes = { "small", "secondary" }, HorizontalAlignment = HorizontalAlignment.Center };
                slider.ValueChanged += (s, e) => { _cfg.BusVolumes[bus] = (float)slider.Value; val.Text = Db((float)slider.Value); try { _cfg.Apply(); } catch { } };
                var mute = new ToggleButton { Content = "Mute", IsChecked = _cfg.BusMutes[i], HorizontalAlignment = HorizontalAlignment.Center, Classes = { "accentcheck" } };
                mute.IsCheckedChanged += (s, e) => { _cfg.BusMutes[bus] = mute.IsChecked == true; try { _cfg.Apply(); } catch { } };
                sp.Children.Add(slider); sp.Children.Add(val); sp.Children.Add(mute);
                strip.Child = sp;
                Grid.SetColumn(strip, i);
                strips.Children.Add(strip);
            }
            DockPanel.SetDock(strips, Dock.Top); root.Children.Add(strips);

            var ducks = new StackPanel { Margin = new Thickness(14, 10, 14, 0), Spacing = 4 };
            ducks.Children.Add(new TextBlock { Text = "Ducking (lower a bus while another plays)", Classes = { "section" }, Margin = new Thickness(0, 0, 0, 4) });
            var list = new StackPanel { Spacing = 4 };
            void Rebuild()
            {
                list.Children.Clear();
                foreach (var d in _cfg.Ducks)
                {
                    var dd = d;
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    row.Children.Add(new TextBlock { Text = "When", VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } });
                    row.Children.Add(Choice(() => dd.TriggerBus, v => dd.TriggerBus = v, VortexAudio.BusNames));
                    row.Children.Add(new TextBlock { Text = "plays, lower", VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } });
                    row.Children.Add(Choice(() => dd.TargetBus, v => dd.TargetBus = v, VortexAudio.BusNames));
                    row.Children.Add(new TextBlock { Text = "by", VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } });
                    row.Children.Add(FloatBox(() => dd.DuckDb, v => dd.DuckDb = v, 1, -60, 0, "0"));
                    row.Children.Add(new TextBlock { Text = "dB", VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } });
                    var rm = new Button { Classes = { "icon" }, Content = new Controls.VxIcon { Icon = "Minus" } }; rm.Click += (s, e) => { _cfg.Ducks.Remove(dd); Rebuild(); };
                    row.Children.Add(rm);
                    list.Children.Add(row);
                }
                var add = new Button { Content = "Add rule", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left }; add.Click += (s, e) => { _cfg.Ducks.Add(new AudioMixerConfig.DuckRule { TriggerBus = 2, TargetBus = 1 }); Rebuild(); };
                list.Children.Add(add);
            }
            Rebuild();
            ducks.Children.Add(list);
            var steam = new CheckBox { Content = "Steam Audio spatialisation (HRTF / occlusion), when available", IsChecked = _cfg.SteamAudioEnabled, Margin = new Thickness(0, 8, 0, 0) };
            steam.IsCheckedChanged += (s, e) => _cfg.SteamAudioEnabled = steam.IsChecked == true;
            ducks.Children.Add(steam);
            root.Children.Add(new ScrollViewer { Content = ducks });
            Content = root;
        }

        private static string Db(float v) => v <= 0.0001f ? "-∞ dB" : (20 * Math.Log10(v)).ToString("0.0") + " dB";
    }
}
