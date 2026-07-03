using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Core.Audio;
using Editor.Core.Data;

namespace Editor.Editors.AudioEditor
{
    /// <summary>
    /// Editor for .vsndc sound containers (issue #16): clip entries with weights,
    /// global pitch/volume ranges, per-entry audition and a "roll" button that plays
    /// the container exactly like the game will (shuffle bag + randomization).
    /// Saves on every change. Dark programmatic UI like the other tool windows.
    /// </summary>
    public sealed class SoundContainerEditorWindow : Window
    {
        private readonly string _path;
        private readonly SoundContainer _container;
        private StackPanel _entryList;
        private ulong _previewVoice = DllWrapper.VortexAudio.InvalidVoice;

        public static SoundContainerEditorWindow Open(Window owner, string absolutePath)
        {
            var win = new SoundContainerEditorWindow(absolutePath) { Owner = owner };
            win.Show();
            return win;
        }

        private SoundContainerEditorWindow(string absolutePath)
        {
            _path = absolutePath;
            _container = SoundContainer.Load(absolutePath);

            Title = "Sound Container — " + System.IO.Path.GetFileNameWithoutExtension(absolutePath);
            Width = 560; Height = 520; MinWidth = 480; MinHeight = 380;
            Background = Br("#FF161618");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { LastChildFill = true };

            var header = new Border { Background = Br("#FF1B1B1E"), BorderBrush = Br("#FF2C2C32"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 12, 16, 12) };
            var hs = new StackPanel();
            hs.Children.Add(new TextBlock { Text = "Sound Container", Foreground = Br("#FFF5F5F7"), FontSize = 15, FontWeight = FontWeights.Bold });
            hs.Children.Add(new TextBlock { Text = "Each Play() rolls a different clip with pitch/volume variation — no-repeat shuffle. Assign the .vsndc anywhere a clip goes.", Foreground = Br("#FF8A8A92"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            header.Child = hs;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Footer: roll preview + ranges.
            var footer = new Border { Background = Br("#FF1B1B1E"), BorderBrush = Br("#FF2C2C32"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10, 16, 12) };
            var fs = new StackPanel();
            var ranges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            ranges.Children.Add(RangeBox("Pitch", () => _container.PitchMin, v => _container.PitchMin = v, () => _container.PitchMax, v => _container.PitchMax = v));
            ranges.Children.Add(RangeBox("Volume", () => _container.VolumeMin, v => _container.VolumeMin = v, () => _container.VolumeMax, v => _container.VolumeMax = v));
            fs.Children.Add(ranges);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var roll = MakeButton("▶  Roll (audition like the game)", "#FF7CE0A3");
            roll.Click += (s, e) => PreviewRoll();
            buttons.Children.Add(roll);
            var addEntry = MakeButton("+  Add clip", "#FFC8C8CE");
            addEntry.Click += (s, e) =>
            {
                // Route through the STA-thread picker — a WPF file dialog on the live UI thread deadlocks against the
                // DX12/DXGI COM apartment and hangs the editor white (the reported "Add crashes the engine").
                var root2 = ProjectData.Current?.Path ?? "";
                var startDir = string.IsNullOrEmpty(root2) ? null : System.IO.Path.Combine(root2, "Assets", "Audio");
                var files = Editor.Core.Util.FilePicker.OpenFiles("Audio|*.wav;*.mp3;*.ogg;*.flac", "Add clips to container", startDir, true);
                if (files != null && files.Length > 0)
                {
                    foreach (var file in files)
                    {
                        var rel = file.StartsWith(root2, StringComparison.OrdinalIgnoreCase)
                            ? file.Substring(root2.Length).TrimStart('\\', '/').Replace('\\', '/')
                            : file;
                        var meta = Core.Assets.AssetDatabase.Instance.GetAssetByPath(rel);
                        _container.Entries.Add(new SoundContainer.Entry { ClipPath = rel, Guid = meta?.Guid.ToString() ?? "" });
                    }
                    Persist();
                    RebuildEntries();
                }
            };
            buttons.Children.Add(addEntry);
            fs.Children.Add(buttons);
            footer.Child = fs;
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 12, 16, 12) };
            _entryList = new StackPanel();
            scroll.Content = _entryList;
            root.Children.Add(scroll);

            Content = root;
            RebuildEntries();

            Closed += (s, e) => StopPreview();
        }

        private void RebuildEntries()
        {
            _entryList.Children.Clear();
            if (_container.Entries.Count == 0)
            {
                _entryList.Children.Add(new TextBlock { Text = "No clips yet — add at least 2 for variation.", Foreground = Br("#FF66666E"), FontSize = 11.5 });
                return;
            }
            foreach (var entry in _container.Entries)
                _entryList.Children.Add(BuildEntryRow(entry));
        }

        private UIElement BuildEntryRow(SoundContainer.Entry entry)
        {
            var row = new Border { Background = Br("#FF1B1B1E"), BorderBrush = Br("#FF2C2C32"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6) };
            var line = new DockPanel { LastChildFill = true };

            var play = MakeButton("▶", "#FF7CE0A3");
            play.Width = 30;
            play.Margin = new Thickness(0, 0, 8, 0);
            play.ToolTip = "Audition this clip";
            play.Click += (s, e) => PreviewClip(SoundContainer.ResolveEntryPath(entry));
            DockPanel.SetDock(play, Dock.Left);
            line.Children.Add(play);

            var remove = MakeButton("✕", "#FFB76B7E");
            remove.Width = 26;
            remove.Margin = new Thickness(8, 0, 0, 0);
            remove.ToolTip = "Remove clip";
            remove.Click += (s, e) => { _container.Entries.Remove(entry); Persist(); RebuildEntries(); };
            DockPanel.SetDock(remove, Dock.Right);
            line.Children.Add(remove);

            var weight = new TextBox { Text = entry.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 42, Background = Br("#FF101013"), Foreground = Br("#FFC8C8CE"), BorderBrush = Br("#FF3A3A42"), ToolTip = "Weight (relative pick probability)", Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(8, 0, 0, 0) };
            weight.LostFocus += (s, e) =>
            {
                if (float.TryParse(weight.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w))
                {
                    entry.Weight = w;
                    Persist();
                }
            };
            DockPanel.SetDock(weight, Dock.Right);
            line.Children.Add(weight);

            line.Children.Add(new TextBlock { Text = entry.ClipPath, Foreground = Br("#FFC8C8CE"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

            row.Child = line;
            return row;
        }

        private UIElement RangeBox(string label, Func<float> getMin, Action<float> setMin, Func<float> getMax, Action<float> setMax)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            panel.Children.Add(new TextBlock { Text = label, Foreground = Br("#FF8A8A92"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            panel.Children.Add(NumBox(getMin, setMin));
            panel.Children.Add(new TextBlock { Text = "…", Foreground = Br("#FF8A8A92"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
            panel.Children.Add(NumBox(getMax, setMax));
            return panel;
        }

        private TextBox NumBox(Func<float> get, Action<float> set)
        {
            var box = new TextBox { Text = get().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), Width = 46, Background = Br("#FF101013"), Foreground = Br("#FFC8C8CE"), BorderBrush = Br("#FF3A3A42"), Padding = new Thickness(4, 2, 4, 2) };
            box.LostFocus += (s, e) =>
            {
                if (float.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    set(v);
                    Persist();
                }
            };
            return box;
        }

        private static Button MakeButton(string text, string accent)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Br("#FF26262B"),
                Foreground = Br(accent),
                BorderBrush = Br("#FF3A3A42"),
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        private void PreviewRoll()
        {
            Persist(); // preview always reflects the current edits
            if (!SoundContainerService.Resolve(_path, out var rolled)) return;
            var root = ProjectData.Current?.Path ?? "";
            var full = System.IO.Path.IsPathRooted(rolled.ClipPath) ? rolled.ClipPath : System.IO.Path.Combine(root, rolled.ClipPath);
            PlayPreview(full, rolled.VolumeScale, rolled.PitchScale);
        }

        private void PreviewClip(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return;
            var root = ProjectData.Current?.Path ?? "";
            var full = System.IO.Path.IsPathRooted(rel) ? rel : System.IO.Path.Combine(root, rel);
            PlayPreview(full, 1f, 1f);
        }

        private void PlayPreview(string fullPath, float volume, float pitch)
        {
            StopPreview();
            if (!System.IO.File.Exists(fullPath)) return;
            _previewVoice = DllWrapper.VortexAudio.PlayVoice(fullPath, volume, pitch, 0f, loop: false, priority: 0, stream: true);
        }

        private void StopPreview()
        {
            if (_previewVoice != DllWrapper.VortexAudio.InvalidVoice)
            {
                DllWrapper.VortexAudio.StopVoice(_previewVoice);
                _previewVoice = DllWrapper.VortexAudio.InvalidVoice;
            }
        }

        private void Persist() => _container.Save(_path);

        private static SolidColorBrush Br(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }
}
