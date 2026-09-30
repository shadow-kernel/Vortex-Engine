using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Audio;
using Editor.Core.Data;
using Editor.DllWrapper;
using VortexEditor.Controls;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>Sound container (.vsndc): a weighted bag of clips with pitch/volume randomisation.</summary>
    public sealed class SoundContainerEditorWindow : Window
    {
        /// <summary>Open this window (owned by the main window).</summary>
        public static void Open(string fullPath) => EditorWindows.Show(new SoundContainerEditorWindow(fullPath));

        private readonly string _path;
        private readonly SoundContainer _sc;
        private readonly StackPanel _list = new StackPanel { Spacing = 4 };
        private ulong _voice = VortexAudio.InvalidVoice;
        private readonly Random _rng = new Random();
        private static readonly string[] Audio = { "*.wav", "*.mp3", "*.ogg", "*.flac" };

        public SoundContainerEditorWindow(string path)
        {
            _path = path;
            _sc = SoundContainer.Load(path) ?? new SoundContainer();
            Title = "Sound Container — " + Path.GetFileNameWithoutExtension(path); Width = 560; Height = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(16, 12) };
            stack.Children.Add(new TextBlock { Text = "Clips (one is picked at random, weighted; no immediate repeats)", Classes = { "section" } });
            stack.Children.Add(_list);
            stack.Children.Add(new TextBlock { Text = "Randomisation", Classes = { "section" } });
            stack.Children.Add(Row("Pitch min", SliderRow(() => _sc.PitchMin, v => _sc.PitchMin = v, 0.25, 2)));
            stack.Children.Add(Row("Pitch max", SliderRow(() => _sc.PitchMax, v => _sc.PitchMax = v, 0.25, 2)));
            stack.Children.Add(Row("Volume min", SliderRow(() => _sc.VolumeMin, v => _sc.VolumeMin = v, 0, 1)));
            stack.Children.Add(Row("Volume max", SliderRow(() => _sc.VolumeMax, v => _sc.VolumeMax = v, 0, 1)));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var preview = new Button { Content = "Preview" }; preview.Click += (s, e) => Preview();
            var close = new Button { Content = "Close", MinWidth = 90 }; close.Click += (s, e) => Close();
            var save = new Button { Content = "Save", Classes = { "accent" }, MinWidth = 90, IsDefault = true }; save.Click += (s, e) => { try { _sc.Save(_path); EditorCommands.Toast("Sound container saved"); } catch (Exception ex) { EditorCommands.Fail("Save", ex); } };
            buttons.Children.Add(preview); buttons.Children.Add(close); buttons.Children.Add(save);
            stack.Children.Add(buttons);
            Content = new ScrollViewer { Content = stack };
            Rebuild();
            Closed += (s, e) => { if (_voice != VortexAudio.InvalidVoice) VortexAudio.StopVoice(_voice); };
        }

        private void Rebuild()
        {
            _list.Children.Clear();
            foreach (var entry in _sc.Entries)
            {
                var en = entry;
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,70,Auto,Auto") };
                var path = AssetPath(() => en.ClipPath, v => en.ClipPath = v ?? "", "Audio", Audio, () => AssetPickerDialog.Pick("Audio", Audio));
                var weight = FloatBox(() => en.Weight, v => en.Weight = Math.Max(0.01f, v), 0.1, 0.01f); weight.Margin = new Thickness(6, 0, 0, 0);
                ToolTip.SetTip(weight, "Weight (relative pick chance)");
                var play = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Play" } }; play.Click += (s, e) => PlayEntry(en);
                var del = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Minus" } }; del.Click += (s, e) => { _sc.Entries.Remove(en); Rebuild(); };
                Grid.SetColumn(weight, 1); Grid.SetColumn(play, 2); Grid.SetColumn(del, 3);
                g.Children.Add(path); g.Children.Add(weight); g.Children.Add(play); g.Children.Add(del);
                _list.Children.Add(g);
            }
            var add = new Button { Content = "Add clip…", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
            add.Click += async (s, e) => { var p = await AssetPickerDialog.Pick("Audio", Audio); if (!string.IsNullOrEmpty(p)) { _sc.Entries.Add(new SoundContainer.Entry { Guid = Guid.NewGuid().ToString("N"), ClipPath = p, Weight = 1f }); Rebuild(); } };
            _list.Children.Add(add);
        }

        private void PlayEntry(SoundContainer.Entry en)
        {
            try
            {
                string abs = SoundContainer.ResolveEntryPath(en);
                if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) { abs = Panels.Inspector.PropertyRows.Matches(en.ClipPath, Audio) ? Path.Combine(ProjectData.Current?.Path ?? "", en.ClipPath) : en.ClipPath; }
                if (_voice != VortexAudio.InvalidVoice) VortexAudio.StopVoice(_voice);
                float pitch = _sc.PitchMin + (float)_rng.NextDouble() * (_sc.PitchMax - _sc.PitchMin);
                float vol = _sc.VolumeMin + (float)_rng.NextDouble() * (_sc.VolumeMax - _sc.VolumeMin);
                _voice = VortexAudio.PlayVoice(abs, vol, pitch, 0f, false, 128);
            }
            catch (Exception ex) { EditorCommands.Fail("Preview", ex); }
        }

        private void Preview()
        {
            if (_sc.Entries.Count == 0) { EditorCommands.Toast("Add a clip first"); return; }
            float total = _sc.Entries.Sum(e => Math.Max(0.01f, e.Weight));
            float r = (float)_rng.NextDouble() * total;
            foreach (var e in _sc.Entries) { r -= Math.Max(0.01f, e.Weight); if (r <= 0) { PlayEntry(e); return; } }
            PlayEntry(_sc.Entries[_sc.Entries.Count - 1]);
        }
    }
}
