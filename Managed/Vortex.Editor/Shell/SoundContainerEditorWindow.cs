using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Assets;
using Editor.Core.Audio;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.DllWrapper;
using VortexEditor.Controls;
using VortexEditor.Panels.Inspector;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Sound container editor (.vsndc, port of the Windows editor's window): the clip entries with their weights (and the
    /// resulting pick chance), per-entry audition, the pitch / volume randomisation ranges, and "Roll" — plays the
    /// container exactly like the game does (weighted no-repeat shuffle bag + pitch/volume rolls). Clips are added with
    /// the asset picker (several at once) or by dropping audio files from the Asset Browser / Finder. Every change is
    /// saved immediately, like the Windows editor.
    /// </summary>
    public sealed class SoundContainerEditorWindow : Window
    {
        private static readonly string[] ClipPatterns = { "*.wav", "*.mp3", "*.ogg", "*.flac" };

        /// <summary>Open (or bring to front) the editor for a .vsndc.</summary>
        public static void Open(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            var existing = EditorKit.OpenWindows<SoundContainerEditorWindow>().FirstOrDefault(w => EditorKit.SamePath(w._path, fullPath));
            if (existing != null) { existing.Activate(); return; }
            EditorWindows.Show(new SoundContainerEditorWindow(fullPath));
        }

        private readonly string _path;
        private readonly SoundContainer _sc;
        private readonly StackPanel _list = new StackPanel { Spacing = 6 };
        private readonly TextBlock _status = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly List<Action> _rangeRefresh = new List<Action>();
        private readonly Random _rng = new Random();
        private readonly Dictionary<SoundContainer.Entry, TextBlock> _chance = new Dictionary<SoundContainer.Entry, TextBlock>();
        private DispatcherTimer _playWatch;
        private readonly List<int> _bag = new List<int>();
        private int _last = -1;
        private ulong _voice = VortexAudio.InvalidVoice;
        private SoundContainer.Entry _playing;

        /// <summary>The container being edited (tests / tools).</summary>
        internal SoundContainer Container => _sc;
        internal int EntryRows => _list.Children.OfType<Border>().Count();

        public SoundContainerEditorWindow(string path)
        {
            _path = Path.GetFullPath(path);
            _sc = SoundContainer.Load(_path) ?? new SoundContainer();
            Title = "Sound Container — " + Path.GetFileNameWithoutExtension(_path);
            Width = 600; Height = 560; MinWidth = 480; MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

            var root = new DockPanel();
            var header = new Border { Classes = { "hairline-bottom" }, Background = EditorKit.Brush("VxToolbarBrush"), Padding = new Thickness(16, 12) };
            var hs = new StackPanel { Spacing = 3 };
            hs.Children.Add(new TextBlock { Text = "Sound Container", FontSize = 15, FontWeight = FontWeight.SemiBold });
            hs.Children.Add(new TextBlock { Text = "Each Play() rolls a different clip with pitch / volume variation — no-repeat shuffle. Assign the .vsndc anywhere a clip goes.", Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
            hs.Children.Add(new TextBlock { Text = EditorKit.ToProjectRelative(_path), Classes = { "small", "tertiary" } });
            header.Child = hs;
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

            root.Children.Add(BuildFooter());   // docked bottom inside

            var scroll = new ScrollViewer { Content = new Border { Padding = new Thickness(16, 12), Child = _list } };
            DragDrop.SetAllowDrop(scroll, true);
            scroll.AddHandler(DragDrop.DragOverEvent, (s, e) => { e.DragEffects = DroppedClips(e).Count > 0 ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None; e.Handled = true; });
            scroll.AddHandler(DragDrop.DropEvent, (s, e) => { var clips = DroppedClips(e); if (clips.Count > 0) { AddClips(clips); e.Handled = true; } });
            root.Children.Add(scroll);
            Content = root;

            Rebuild();
            SetStatus(_sc.Entries.Count == 1 ? "1 clip" : _sc.Entries.Count + " clips");
            Opened += (s, e) =>
            {
                var other = EditorKit.OpenWindows<SoundContainerEditorWindow>().FirstOrDefault(w => !ReferenceEquals(w, this) && EditorKit.SamePath(w._path, _path));
                if (other != null) Dispatcher.UIThread.Post(() => { Close(); other.Activate(); });
            };
            Closed += (s, e) => { StopPreview(); _playWatch?.Stop(); EditorSession.Instance.ProjectClosed -= OnProjectClosed; };
            EditorSession.Instance.ProjectClosed += OnProjectClosed;
        }

        private void OnProjectClosed() => Dispatcher.UIThread.Post(Close);

        // ---------------------------------------------------------------- footer: ranges + actions
        private Control BuildFooter()
        {
            var footer = new Border { Classes = { "hairline-top" }, Background = EditorKit.Brush("VxToolbarBrush"), Padding = new Thickness(16, 10, 16, 12) };
            DockPanel.SetDock(footer, Dock.Bottom);
            var fs = new StackPanel { Spacing = 10 };
            var ranges = new WrapPanel { Orientation = Orientation.Horizontal };
            ranges.Children.Add(Range("Pitch", () => _sc.PitchMin, v => _sc.PitchMin = v, () => _sc.PitchMax, v => _sc.PitchMax = v, 0.1f, 4f, "Playback speed factor (1 = original)"));
            ranges.Children.Add(Range("Volume", () => _sc.VolumeMin, v => _sc.VolumeMin = v, () => _sc.VolumeMax, v => _sc.VolumeMax = v, 0f, 2f, "Volume factor (1 = original)"));
            fs.Children.Add(ranges);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };
            var roll = new Button { Classes = { "accent" }, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new VxIcon { Icon = "Play", Width = 12, Height = 12, Foreground = EditorKit.Brush("VxTextOnAccentBrush") }, new TextBlock { Text = "Roll (audition like the game)", Foreground = EditorKit.Brush("VxTextOnAccentBrush") } } } };
            ToolTip.SetTip(roll, "Pick the next clip exactly like the game: weighted shuffle bag, never the same clip twice in a row, random pitch / volume");
            roll.Click += (s, e) => Roll();
            var stop = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new VxIcon { Icon = "Stop", Width = 12, Height = 12 }, new TextBlock { Text = "Stop" } } }, Margin = new Thickness(8, 0, 0, 0) };
            stop.Click += (s, e) => StopPreview();
            var add = new Button { Content = "+  Add clips…", Margin = new Thickness(8, 0, 0, 0) };
            ToolTip.SetTip(add, "Choose audio clips of the project (several at once) — or drop them on the list");
            add.Click += async (s, e) => await AddFromPicker();
            row.Children.Add(roll);
            Grid.SetColumn(stop, 1); row.Children.Add(stop);
            Grid.SetColumn(add, 2); row.Children.Add(add);
            _status.Margin = new Thickness(12, 0, 0, 0); _status.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_status, 3); row.Children.Add(_status);
            fs.Children.Add(row);
            footer.Child = fs;
            return footer;
        }

        private Control Range(string label, Func<float> getMin, Action<float> setMin, Func<float> getMax, Action<float> setMax, float lo, float hi, string tip)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 22, 0) };
            var l = new TextBlock { Text = label, Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(l, tip);
            p.Children.Add(l);
            var min = PropertyRows.FloatBox(getMin, v => { setMin(v); if (getMax() < v) setMax(v); Persist(); RefreshRanges(); }, 0.01, lo, hi, "0.##");
            var max = PropertyRows.FloatBox(getMax, v => { setMax(v); if (getMin() > v) setMin(v); Persist(); RefreshRanges(); }, 0.01, lo, hi, "0.##");
            PropertyRows.Refreshers.Remove(min); PropertyRows.Refreshers.Remove(max);   // private refresh (see below)
            min.Width = 58; max.Width = 58;
            ToolTip.SetTip(min, label + " minimum"); ToolTip.SetTip(max, label + " maximum");
            _rangeRefresh.Add(() => { if (!min.IsFocused) min.Text = PropertyRows.Fmt(getMin(), "0.##"); if (!max.IsFocused) max.Text = PropertyRows.Fmt(getMax(), "0.##"); });
            p.Children.Add(min);
            p.Children.Add(new TextBlock { Text = "…", Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(max);
            return p;
        }

        private void RefreshRanges() { foreach (var r in _rangeRefresh) r(); }

        // ---------------------------------------------------------------- entries
        private static string ChanceText(SoundContainer.Entry e, float total)
            => (Math.Max(0.01f, e.Weight) / Math.Max(0.0001f, total) * 100f).ToString("0", CultureInfo.InvariantCulture) + " %";

        private void UpdateChances()
        {
            float total = _sc.Entries.Sum(e => Math.Max(0.01f, e.Weight));
            foreach (var kv in _chance) kv.Value.Text = ChanceText(kv.Key, total);
        }

        private void Rebuild()
        {
            _list.Children.Clear();
            _chance.Clear();
            if (_sc.Entries.Count == 0)
            {
                _list.Children.Add(new TextBlock { Text = "No clips yet — add at least 2 for variation (Add clips… or drop audio files here).", Classes = { "tertiary" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
                return;
            }
            float total = _sc.Entries.Sum(e => Math.Max(0.01f, e.Weight));
            foreach (var entry in _sc.Entries) _list.Children.Add(BuildRow(entry, total));
        }

        private Control BuildRow(SoundContainer.Entry entry, float total)
        {
            string rel = SoundContainer.ResolveEntryPath(entry);
            string full = string.IsNullOrEmpty(rel) ? null : EditorKit.ToAbsolute(rel);
            bool exists = full != null && File.Exists(full);

            var play = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Play", Foreground = EditorKit.Brush("VxGreenBrush") } };
            ToolTip.SetTip(play, "Audition this clip");
            play.Click += (s, e) => PlayEntry(entry, 1f, 1f);

            var name = new TextBlock { Text = string.IsNullOrEmpty(rel) ? "(no clip)" : Path.GetFileName(rel), FontWeight = FontWeight.Medium, Foreground = exists ? EditorKit.Brush("VxTextBrush") : EditorKit.Brush("VxRedBrush") };
            string info = !exists ? (string.IsNullOrEmpty(rel) ? "Pick a clip" : "Missing: " + rel) : Path.GetDirectoryName(rel)?.Replace('\\', '/');
            if (exists && VortexAudio.GetClipInfo(full, out float dur, out int rate, out int ch)) info += "  ·  " + dur.ToString("0.00", CultureInfo.InvariantCulture) + " s";
            var sub = new TextBlock { Text = info, Classes = { "small", "tertiary" } };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0), Children = { name, sub } };
            ToolTip.SetTip(text, rel);

            var pick = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Folder" } };
            ToolTip.SetTip(pick, "Replace the clip");
            pick.Click += async (s, e) =>
            {
                var r = await AssetPickerDialog.Pick(new AssetPickerOptions { Kind = "Audio", Title = "Choose a clip", Patterns = ClipPatterns, Current = rel, AllowNone = false });
                if (string.IsNullOrEmpty(r)) return;
                SetClip(entry, r);
                Persist(); Rebuild();
            };

            // a weight edit only updates the chances (no row rebuild: tabbing between weight boxes keeps the focus)
            var weight = PropertyRows.FloatBox(() => entry.Weight, v => { entry.Weight = Math.Max(0.01f, v); _bag.Clear(); Persist(); UpdateChances(); }, 0.1, 0.01f, 100f, "0.##");
            PropertyRows.Refreshers.Remove(weight);
            weight.Width = 56; weight.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(weight, "Weight — relative pick probability (1 = normal, 2 = twice as often)");
            var chance = new TextBlock { Text = ChanceText(entry, total), Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center, Width = 40, TextAlignment = TextAlignment.Right };
            _chance[entry] = chance;
            ToolTip.SetTip(chance, "Chance per roll");

            var remove = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Close", Width = 11, Height = 11, Foreground = EditorKit.Brush("VxRedBrush") } };
            ToolTip.SetTip(remove, "Remove clip");
            remove.Click += (s, e) => { if (ReferenceEquals(_playing, entry)) StopPreview(); _sc.Entries.Remove(entry); _bag.Clear(); _last = -1; Persist(); Rebuild(); };

            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto") };
            g.Children.Add(play);
            Grid.SetColumn(text, 1); g.Children.Add(text);
            Grid.SetColumn(pick, 2); g.Children.Add(pick);
            Grid.SetColumn(weight, 3); weight.Margin = new Thickness(6, 0, 0, 0); g.Children.Add(weight);
            Grid.SetColumn(chance, 4); g.Children.Add(chance);
            Grid.SetColumn(remove, 5); remove.Margin = new Thickness(6, 0, 0, 0); g.Children.Add(remove);
            var card = new Border { Classes = { "card" }, Padding = new Thickness(8, 6), Child = g, Tag = entry };
            if (ReferenceEquals(entry, _playing)) card.BorderBrush = EditorKit.Brush("VxGreenBrush");
            return card;
        }

        private static void SetClip(SoundContainer.Entry entry, string pathOrRel)
        {
            string rel = EditorKit.ToProjectRelative(pathOrRel);
            entry.ClipPath = rel;
            try { entry.Guid = AssetDatabase.Instance.GetAssetByPath(rel)?.Guid.ToString() ?? ""; } catch { entry.Guid = ""; }
        }

        private async Task AddFromPicker()
        {
            var picked = await AssetPickerDialog.PickMany("Audio", ClipPatterns);
            if (picked != null && picked.Length > 0) AddClips(picked.ToList());
        }

        /// <summary>Add clips (project-relative or absolute; files outside the project are copied into Assets/Audio).</summary>
        public void AddClips(IList<string> files)
        {
            int n = 0;
            foreach (var f in files)
            {
                if (string.IsNullOrEmpty(f)) continue;
                string path = f;
                try { if (Path.IsPathRooted(path) && !EditorKit.IsInsideProject(path)) path = EditorKit.ImportIntoProject(path, "Audio"); } catch (Exception ex) { EditorCommands.Fail("Add clip", ex); continue; }
                var entry = new SoundContainer.Entry { Weight = 1f };
                SetClip(entry, path);
                _sc.Entries.Add(entry);
                n++;
            }
            if (n == 0) return;
            _bag.Clear(); _last = -1;
            Persist(); Rebuild();
            SetStatus("Added " + n + (n == 1 ? " clip" : " clips"));
        }

        private static List<string> DroppedClips(DragEventArgs e) => EditorKit.DroppedFiles(e).Where(f => EditorKit.HasExtension(f, ClipPatterns) && File.Exists(f)).ToList();

        // ---------------------------------------------------------------- playback
        /// <summary>Roll like the game (weighted shuffle bag, no immediate repeat, pitch/volume rolls) and play it.</summary>
        public void Roll()
        {
            if (_sc.Entries.Count == 0) { SetStatus("Add a clip first"); return; }
            int pick = NextFromBag();
            var entry = _sc.Entries[pick];
            float vol = _sc.VolumeMin + (float)_rng.NextDouble() * (_sc.VolumeMax - _sc.VolumeMin);
            float pitch = _sc.PitchMin + (float)_rng.NextDouble() * (_sc.PitchMax - _sc.PitchMin);
            if (PlayEntry(entry, vol, pitch))
                SetStatus("Rolled " + Path.GetFileName(entry.ClipPath) + "  ·  pitch " + pitch.ToString("0.00", CultureInfo.InvariantCulture) + "  ·  volume " + vol.ToString("0.00", CultureInfo.InvariantCulture));
        }

        /// <summary>Same policy as SoundContainerService (the game): weight → tickets, drawn without replacement, never the
        /// previous clip twice in a row while there is a choice. Runs on the edited data, so a roll reflects every edit.</summary>
        private int NextFromBag()
        {
            if (_sc.Entries.Count == 1) { _last = 0; return 0; }
            if (_bag.Count == 0 || _bag.Any(i => i >= _sc.Entries.Count))
            {
                _bag.Clear();
                for (int i = 0; i < _sc.Entries.Count; i++)
                {
                    int tickets = Math.Max(1, (int)Math.Round(Math.Max(0.01f, _sc.Entries[i].Weight) * 4f));
                    for (int t = 0; t < tickets; t++) _bag.Add(i);
                }
            }
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int slot = _rng.Next(_bag.Count), cand = _bag[slot];
                if (cand != _last || _bag.TrueForAll(x => x == cand)) { _bag.RemoveAt(slot); _last = cand; return cand; }
            }
            int lastSlot = _bag[_bag.Count - 1];
            _bag.RemoveAt(_bag.Count - 1);
            _last = lastSlot;
            return lastSlot;
        }

        private bool PlayEntry(SoundContainer.Entry entry, float volume, float pitch)
        {
            StopPreview();
            string rel = SoundContainer.ResolveEntryPath(entry);
            string full = string.IsNullOrEmpty(rel) ? null : EditorKit.ToAbsolute(rel);
            if (full == null || !File.Exists(full)) { SetStatus("Clip not found: " + (rel ?? "(none)")); return false; }
            try
            {
                _voice = VortexAudio.PlayVoice(full, volume, pitch, 0f, false, 0, true);
                // clear the "playing" highlight when the clip ends
                _playWatch = _playWatch ?? new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (s, e) =>
                {
                    if (_voice != VortexAudio.InvalidVoice && VortexAudio.IsVoicePlaying(_voice)) return;
                    _playWatch.Stop();
                    _voice = VortexAudio.InvalidVoice; _playing = null; HighlightPlaying();
                });
                _playWatch.Start();
                _playing = entry;
                HighlightPlaying();
                if (volume == 1f && pitch == 1f) SetStatus("Playing " + Path.GetFileName(full));
                return true;
            }
            catch (Exception ex) { EditorCommands.Fail("Audition", ex); return false; }
        }

        private void StopPreview()
        {
            if (_voice != VortexAudio.InvalidVoice) { try { VortexAudio.StopVoice(_voice); } catch { } _voice = VortexAudio.InvalidVoice; }
            _playing = null;
            HighlightPlaying();
        }

        private void HighlightPlaying()
        {
            foreach (var b in _list.Children.OfType<Border>())
                b.BorderBrush = b.Tag != null && ReferenceEquals(b.Tag, _playing) ? EditorKit.Brush("VxGreenBrush") : EditorKit.Brush("VxHairlineBrush");
        }

        // ---------------------------------------------------------------- persistence
        private void Persist()
        {
            try { _sc.Save(_path); } catch (Exception ex) { SetStatus("Could not save: " + ex.Message); }
        }

        private void SetStatus(string text) => _status.Text = text;
    }
}
