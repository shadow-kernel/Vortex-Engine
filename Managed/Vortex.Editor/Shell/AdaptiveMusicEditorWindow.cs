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
using Editor.Core.Editing;
using Editor.DllWrapper;
using VortexEditor.Controls;
using VortexEditor.Panels.Inspector;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The adaptive music editor (.vmusic, #437) — a form, not a DAW: the tempo and loop, the stems (each with an optional
    /// parameter it follows), the states as a state × stem table of volumes, the transition rules (when a change lands,
    /// its crossfade and stinger) and the stingers. The preview plays the music exactly like the game (the same player,
    /// sample-locked stems): click a state to hear the change land on its boundary, drag a parameter, fire a stinger.
    /// Every change is saved at once, like the sound container editor.
    /// </summary>
    public sealed class AdaptiveMusicEditorWindow : Window
    {
        private static readonly string[] ClipPatterns = { "*.wav", "*.mp3", "*.ogg", "*.flac" };
        private static readonly string[] SyncLabels = { "Immediately", "Next beat", "Next bar", "End of phrase" };

        /// <summary>Open (or bring to front) the editor for a .vmusic.</summary>
        public static void Open(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            var existing = EditorKit.OpenWindows<AdaptiveMusicEditorWindow>().FirstOrDefault(w => EditorKit.SamePath(w._path, fullPath));
            if (existing != null) { existing.Activate(); return; }
            EditorWindows.Show(new AdaptiveMusicEditorWindow(fullPath));
        }

        /// <summary>A new .vmusic in <paramref name="folder"/>: 120 BPM 4/4, the states "calm" and "intense", changes on the
        /// next bar — stems are added in the editor. Returns its path.</summary>
        public static string CreateNew(string folder, string name = "NewAdaptiveMusic")
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name + AdaptiveMusic.FileExtension);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, name + " " + i + AdaptiveMusic.FileExtension);
            var m = new AdaptiveMusic { InitialState = "calm" };
            m.States.Add(new AdaptiveMusic.State { Name = "calm" });
            m.States.Add(new AdaptiveMusic.State { Name = "intense" });
            m.Save(path);
            return path;
        }

        private readonly string _path;
        private readonly AdaptiveMusic _m;
        private readonly StackPanel _body = new StackPanel { Spacing = 4 };
        private readonly StackPanel _previewBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        private readonly TextBlock _status = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly List<Action> _refreshers = new List<Action>();
        private AdaptiveMusicPlayer _preview;
        private DispatcherTimer _previewTimer;
        private DateTime _lastTick;

        /// <summary>The file, the music being edited and the preview (tests / smoke checks).</summary>
        internal string FilePath => _path;
        internal AdaptiveMusic Music => _m;
        internal AdaptiveMusicPlayer Preview => _preview;

        public AdaptiveMusicEditorWindow(string path)
        {
            _path = Path.GetFullPath(path);
            _m = AdaptiveMusic.Load(_path);
            Title = "Adaptive Music — " + Path.GetFileNameWithoutExtension(_path);
            Width = 860; Height = 720; MinWidth = 640; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

            var root = new DockPanel();
            var header = new Border { Classes = { "hairline-bottom" }, Background = EditorKit.Brush("VxToolbarBrush"), Padding = new Thickness(16, 12) };
            var hs = new StackPanel { Spacing = 3 };
            hs.Children.Add(new TextBlock { Text = "Adaptive Music", FontSize = 15, FontWeight = FontWeight.SemiBold });
            hs.Children.Add(new TextBlock { Text = "Stems of one tempo play sample-locked; states set each stem's volume; Audio.Music.SetState(\"…\") switches on the boundary its rule names. Play it with Audio.Music.Play(\"" + EditorKit.ToProjectRelative(_path) + "\").", Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap });
            header.Child = hs;
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

            var footer = new Border { Classes = { "hairline-top" }, Background = EditorKit.Brush("VxToolbarBrush"), Padding = new Thickness(16, 10, 16, 12) };
            DockPanel.SetDock(footer, Dock.Bottom);
            var fs = new StackPanel { Spacing = 8 };
            fs.Children.Add(new ScrollViewer { Content = _previewBar, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            fs.Children.Add(_status);
            footer.Child = fs;
            root.Children.Add(footer);

            var scroll = new ScrollViewer { Content = new Border { Padding = new Thickness(16, 8, 16, 16), Child = _body }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            DragDrop.SetAllowDrop(scroll, true);
            scroll.AddHandler(DragDrop.DragOverEvent, (s, e) => { e.DragEffects = DroppedClips(e).Count > 0 ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None; e.Handled = true; });
            scroll.AddHandler(DragDrop.DropEvent, (s, e) => { var clips = DroppedClips(e); if (clips.Count > 0) { AddStems(clips); e.Handled = true; } });
            root.Children.Add(scroll);
            Content = root;

            Rebuild();
            SetStatus(_m.Describe());
            Closed += (s, e) => { StopPreview(); EditorSession.Instance.ProjectClosed -= OnProjectClosed; };
            EditorSession.Instance.ProjectClosed += OnProjectClosed;
        }

        private void OnProjectClosed() => Dispatcher.UIThread.Post(Close);

        // ---------------------------------------------------------------- layout
        private void Rebuild()
        {
            _body.Children.Clear();
            _refreshers.Clear();
            using (PropertyRows.CaptureRefreshers(_refreshers))
            {
                BuildTiming();
                BuildStems();
                BuildStates();
                BuildTransitions();
                BuildStingers();
                BuildPreviewBar();
            }
        }

        private void Changed(bool structural = false)
        {
            Persist();
            if (structural) Rebuild();
        }

        /// <summary>A number box of a compact width (the settings are short numbers, not text).</summary>
        private static TextBox Narrow(TextBox box) { box.Width = 90; box.HorizontalAlignment = HorizontalAlignment.Left; return box; }

        private void BuildTiming()
        {
            _body.Children.Add(PropertyRows.Section("Tempo and loop"));
            _body.Children.Add(PropertyRows.Row("Tempo (BPM)", Narrow(PropertyRows.FloatBox(() => _m.Bpm, v => { _m.Bpm = v; Changed(); }, 1, 20, 400, "0.##")), "Beats per minute — the beats of the time signature"));
            var sig = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var beats = PropertyRows.IntBox(() => _m.BeatsPerBar, v => { _m.BeatsPerBar = v; Changed(); }, 1, 32);
            beats.Width = 56;
            sig.Children.Add(beats);
            sig.Children.Add(new TextBlock { Text = "/", VerticalAlignment = VerticalAlignment.Center });
            int[] units = { 2, 4, 8, 16 };
            sig.Children.Add(PropertyRows.Choice(() => Math.Max(0, Array.IndexOf(units, _m.BeatUnit)), i => { _m.BeatUnit = units[i]; Changed(); }, "2", "4", "8", "16"));
            _body.Children.Add(PropertyRows.Row("Time signature", sig, "Beats per bar / note value — OnBar and \"next bar\" count the upper number of beats"));
            _body.Children.Add(PropertyRows.Row("Bar 1 starts at", Narrow(PropertyRows.FloatBox(() => _m.OffsetSeconds, v => { _m.OffsetSeconds = v; Changed(); }, 0.01, 0, 60, "0.###")), "Seconds into the stems where bar 1 begins (a pickup or silence before it)"));
            _body.Children.Add(PropertyRows.Row("Loop bars", Narrow(PropertyRows.IntBox(() => _m.LoopBars, v => { _m.LoopBars = v; Changed(); }, 0, 9999)), "Bars the music loops from bar 1; 0 = the stems' length rounded to whole bars"));
            _body.Children.Add(PropertyRows.Row("Phrase bars", Narrow(PropertyRows.IntBox(() => _m.PhraseBars, v => { _m.PhraseBars = v; Changed(); }, 1, 64)), "\"End of phrase\" changes wait for the next multiple of this many bars"));
            _body.Children.Add(PropertyRows.Row("", PropertyRows.Check("Loop", () => _m.Loop, v => { _m.Loop = v; Changed(); }, "Off: the music plays once and ends")));
            _body.Children.Add(PropertyRows.Row("", PropertyRows.Check("Decode stems into memory (short loops)", () => _m.Decode, v => { _m.Decode = v; Changed(); },
                "Off: the stems stream from disk — about a megabyte each, any length. On: every Play decodes every stem into memory first " +
                "(a 3-minute stereo stem is ~70 MB and a moment of decoding). Both loop the bars above, sample-locked")));
            _body.Children.Add(PropertyRows.Row("Volume", PropertyRows.SliderRow(() => _m.Volume, v => { _m.Volume = v; Changed(); }, 0, 1, "0.##", 0f, 2f)));
            _body.Children.Add(PropertyRows.Row("Parameter fade", Narrow(PropertyRows.FloatBox(() => _m.ParameterFadeSeconds, v => { _m.ParameterFadeSeconds = v; Changed(); }, 0.05, 0, 30, "0.##")), "Seconds Audio.Music.SetParameter glides the mapped stems"));
        }

        private void BuildStems()
        {
            _body.Children.Add(PropertyRows.Section("Stems"));
            if (_m.Stems.Count == 0)
                _body.Children.Add(PropertyRows.Hint("No stems yet — add the layers of the track (same tempo, ideally the same length): Add stems… or drop audio files here."));
            for (int i = 0; i < _m.Stems.Count; i++) _body.Children.Add(BuildStemRow(_m.Stems[i]));
            _body.Children.Add(new WrapPanel { Children = { PropertyRows.Ghost("+  Add stems…", () => _ = AddStemsFromPicker(), "Choose audio clips of the project (several at once) — or drop them on this window") } });
        }

        private Control BuildStemRow(AdaptiveMusic.Stem stem)
        {
            string rel = AdaptiveMusic.ResolveClip(stem.Guid, stem.ClipPath);
            string full = string.IsNullOrEmpty(rel) ? null : EditorKit.ToAbsolute(rel);
            bool exists = full != null && File.Exists(full);

            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,140,*,Auto,Auto,Auto"), Margin = new Thickness(0, 2) };
            var play = EditorKit.IconButton("Play", "Audition this stem alone", () => AuditionClip(full));
            g.Children.Add(play);
            var name = PropertyRows.Text(() => stem.Name, v => RenameStem(stem, v), "stem name");
            name.Margin = new Thickness(6, 0); Grid.SetColumn(name, 1); g.Children.Add(name);
            var clip = new Button { Content = new TextBlock { Text = string.IsNullOrEmpty(rel) ? "(pick a clip)" : Path.GetFileName(rel), Foreground = exists ? EditorKit.Brush("VxTextBrush") : EditorKit.Brush("VxRedBrush"), TextTrimming = TextTrimming.CharacterEllipsis }, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(clip, exists ? rel + ClipInfo(full) : "Missing: " + (rel ?? "no clip") + " — click to choose");
            clip.Click += async (s, e) =>
            {
                var r = await AssetPickerDialog.Pick(new AssetPickerOptions { Kind = "Audio", Title = "Clip of stem '" + stem.Name + "'", Patterns = ClipPatterns, Current = rel, AllowNone = false });
                if (string.IsNullOrEmpty(r)) return;
                SetClip(r, v => stem.ClipPath = v, v => stem.Guid = v);
                Changed(structural: true);
            };
            Grid.SetColumn(clip, 2); g.Children.Add(clip);

            // the parameter this stem follows: name, then the From … To ramp
            var map = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 0, 0) };
            var par = PropertyRows.Text(() => stem.Parameter, v => { stem.Parameter = (v ?? "").Trim(); Changed(structural: true); }, "parameter");
            par.Width = 90;
            ToolTip.SetTip(par, "Optional: a parameter this stem's volume follows (Audio.Music.SetParameter) — e.g. intensity");
            var from = PropertyRows.FloatBox(() => stem.From, v => { stem.From = AdaptiveMusic.Clamp01(v); Changed(); }, 0.05, 0, 1, "0.##");
            var to = PropertyRows.FloatBox(() => stem.To, v => { stem.To = AdaptiveMusic.Clamp01(v); Changed(); }, 0.05, 0, 1, "0.##");
            from.Width = 46; to.Width = 46;
            ToolTip.SetTip(from, "Silent at this parameter value"); ToolTip.SetTip(to, "Full volume at this value (below From: fades out as the parameter rises)");
            map.Children.Add(par); map.Children.Add(from); map.Children.Add(new TextBlock { Text = "…", VerticalAlignment = VerticalAlignment.Center }); map.Children.Add(to);
            Grid.SetColumn(map, 3); g.Children.Add(map);

            var remove = EditorKit.IconButton("Close", "Remove stem", () =>
            {
                _m.Stems.Remove(stem);
                foreach (var st in _m.States) st.Volumes.Remove(stem.Name ?? "");
                Changed(structural: true);
            }, 11);
            remove.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(remove, 5); g.Children.Add(remove);
            return new Border { Classes = { "card" }, Padding = new Thickness(8, 5), Child = g };
        }

        private static string ClipInfo(string full)
            => VortexAudio.GetClipInfo(full, out float dur, out int rate, out int ch) ? "  ·  " + dur.ToString("0.00", CultureInfo.InvariantCulture) + " s, " + rate + " Hz, " + (ch == 1 ? "mono" : ch + " ch") : "";

        private void BuildStates()
        {
            _body.Children.Add(PropertyRows.Section("States — volume of each stem"));
            if (_m.States.Count == 0)
                _body.Children.Add(PropertyRows.Hint("No states: every stem plays at full volume (parameters can still fade them). Add a state to switch layers with Audio.Music.SetState."));
            var grid = new Grid { Margin = new Thickness(0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(140, GridUnitType.Pixel));
            foreach (var _ in _m.Stems) grid.ColumnDefinitions.Add(new ColumnDefinition(72, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < _m.Stems.Count; c++)
            {
                var h = new TextBlock { Text = string.IsNullOrEmpty(_m.Stems[c].Name) ? "#" + (c + 1) : _m.Stems[c].Name, Classes = { "small", "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0) };
                Grid.SetColumn(h, c + 1); grid.Children.Add(h);
            }
            for (int r = 0; r < _m.States.Count; r++)
            {
                var state = _m.States[r];
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var name = PropertyRows.Text(() => state.Name, v => RenameState(state, v), "state name");
                name.Margin = new Thickness(0, 2, 6, 2);
                Grid.SetRow(name, r + 1); grid.Children.Add(name);
                for (int c = 0; c < _m.Stems.Count; c++)
                {
                    var stem = _m.Stems[c];
                    var vol = PropertyRows.FloatBox(() => state.Volumes.TryGetValue(stem.Name ?? "", out float v) ? v : 0f,
                        v => { state.Volumes[stem.Name ?? ""] = Math.Clamp(v, 0f, 4f); Changed(); }, 0.05, 0, 4, "0.##");
                    vol.Margin = new Thickness(2);
                    ToolTip.SetTip(vol, "Volume of '" + stem.Name + "' in '" + state.Name + "' (0 = silent)");
                    Grid.SetRow(vol, r + 1); Grid.SetColumn(vol, c + 1); grid.Children.Add(vol);
                }
                bool initial = string.Equals(_m.StartState()?.Name, state.Name, StringComparison.OrdinalIgnoreCase);
                var init = new RadioButton { GroupName = "initial-" + GetHashCode(), IsChecked = initial, Content = new TextBlock { Text = "start", Classes = { "small" } }, Margin = new Thickness(8, 0) };
                init.IsCheckedChanged += (s, e) => { if (init.IsChecked == true && !string.Equals(_m.InitialState, state.Name, StringComparison.OrdinalIgnoreCase)) { _m.InitialState = state.Name; Changed(); } };
                ToolTip.SetTip(init, "The state the music starts in");
                Grid.SetRow(init, r + 1); Grid.SetColumn(init, _m.Stems.Count + 1); grid.Children.Add(init);
                var remove = EditorKit.IconButton("Close", "Remove state", () => { _m.States.Remove(state); Changed(structural: true); }, 11);
                Grid.SetRow(remove, r + 1); Grid.SetColumn(remove, _m.Stems.Count + 2); grid.Children.Add(remove);
            }
            _body.Children.Add(grid);
            _body.Children.Add(new WrapPanel { Children = { PropertyRows.Ghost("+  Add state", () =>
            {
                var s = new AdaptiveMusic.State { Name = UniqueName("state", _m.States.Select(x => x.Name)) };
                foreach (var stem in _m.Stems) s.Volumes[stem.Name ?? ""] = 1f;
                _m.States.Add(s);
                Changed(structural: true);
            }) } });
        }

        private void BuildTransitions()
        {
            _body.Children.Add(PropertyRows.Section("Transitions — when a state change lands"));
            _body.Children.Add(PropertyRows.Row("Any change", RuleEditor(_m.DefaultTransition, withStates: false), "The rule for every change no rule below covers"));
            var stateNames = new[] { "Any" }.Concat(_m.States.Select(s => s.Name)).ToArray();
            foreach (var rule in _m.Transitions.ToList())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(PropertyRows.LabelWidth + 10, 2, 0, 2) };
                row.Children.Add(StateChoice(() => rule.From, v => rule.From = v, stateNames));
                row.Children.Add(new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(StateChoice(() => rule.To, v => rule.To = v, stateNames));
                row.Children.Add(RuleEditor(rule, withStates: true));
                row.Children.Add(EditorKit.IconButton("Close", "Remove rule", () => { _m.Transitions.Remove(rule); Changed(structural: true); }, 11));
                _body.Children.Add(row);
            }
            _body.Children.Add(PropertyRows.Actions(PropertyRows.Ghost("+  Add rule", () =>
            {
                _m.Transitions.Add(new AdaptiveMusic.Transition { From = "*", To = _m.States.Count > 0 ? _m.States[_m.States.Count - 1].Name : "*", Sync = "beat", FadeSeconds = 0.05f });
                Changed(structural: true);
            }, "A rule for one change (from → to); the most specific rule wins")));
        }

        private Control StateChoice(Func<string> get, Action<string> set, string[] names)
        {
            int Index() { string v = get(); if (string.IsNullOrEmpty(v) || v == "*") return 0; int i = Array.FindIndex(names, n => string.Equals(n, v, StringComparison.OrdinalIgnoreCase)); return i < 0 ? 0 : i; }
            var c = PropertyRows.Choice(Index, i => { set(i <= 0 ? "*" : names[i]); Changed(); }, names);
            c.MinWidth = 96;
            return c;
        }

        private Control RuleEditor(AdaptiveMusic.Transition rule, bool withStates)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var sync = PropertyRows.Choice(() => (int)rule.SyncMode, i => { rule.Sync = AdaptiveMusic.SyncName((MusicSync)i); Changed(); }, SyncLabels);
            sync.MinWidth = 120;
            p.Children.Add(sync);
            var fade = PropertyRows.FloatBox(() => rule.FadeSeconds, v => { rule.FadeSeconds = v; Changed(); }, 0.05, 0, 30, "0.##");
            fade.Width = 52;
            ToolTip.SetTip(fade, "Crossfade seconds from that moment (0 = a 1 ms declick)");
            p.Children.Add(new TextBlock { Text = "fade", Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(fade);
            var stingers = new[] { "No stinger" }.Concat(_m.Stingers.Select(s => s.Name)).ToArray();
            int StIndex() { int i = Array.FindIndex(stingers, n => string.Equals(n, rule.Stinger, StringComparison.OrdinalIgnoreCase)); return string.IsNullOrEmpty(rule.Stinger) || i < 1 ? 0 : i; }
            var st = PropertyRows.Choice(StIndex, i => { rule.Stinger = i <= 0 ? "" : stingers[i]; Changed(); }, stingers);
            st.MinWidth = 110;
            ToolTip.SetTip(st, "A stinger played on the change, on the same sample");
            p.Children.Add(st);
            return p;
        }

        private void BuildStingers()
        {
            _body.Children.Add(PropertyRows.Section("Stingers — one-shots on the beat (Audio.Music.PlayStinger)"));
            foreach (var st in _m.Stingers.ToList())
            {
                string rel = AdaptiveMusic.ResolveClip(st.Guid, st.ClipPath);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*,Auto,Auto,Auto"), Margin = new Thickness(0, 2) };
                var name = PropertyRows.Text(() => st.Name, v => RenameStinger(st, v), "stinger name");
                row.Children.Add(name);
                var clip = new Button { Content = new TextBlock { Text = string.IsNullOrEmpty(rel) ? "(pick a clip)" : Path.GetFileName(rel), TextTrimming = TextTrimming.CharacterEllipsis }, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(6, 0) };
                clip.Click += async (s, e) =>
                {
                    var r = await AssetPickerDialog.Pick(new AssetPickerOptions { Kind = "Audio", Title = "Clip of stinger '" + st.Name + "'", Patterns = ClipPatterns, Current = rel, AllowNone = false });
                    if (string.IsNullOrEmpty(r)) return;
                    SetClip(r, v => st.ClipPath = v, v => st.Guid = v);
                    Changed(structural: true);
                };
                Grid.SetColumn(clip, 1); row.Children.Add(clip);
                var vol = PropertyRows.FloatBox(() => st.Volume, v => { st.Volume = v; Changed(); }, 0.05, 0, 4, "0.##");
                vol.Width = 52; ToolTip.SetTip(vol, "Volume");
                Grid.SetColumn(vol, 2); row.Children.Add(vol);
                var sync = PropertyRows.Choice(() => (int)st.SyncMode, i => { st.Sync = AdaptiveMusic.SyncName((MusicSync)i); Changed(); }, SyncLabels);
                sync.Margin = new Thickness(6, 0); ToolTip.SetTip(sync, "When PlayStinger plays it");
                Grid.SetColumn(sync, 3); row.Children.Add(sync);
                var remove = EditorKit.IconButton("Close", "Remove stinger", () => { _m.Stingers.Remove(st); Changed(structural: true); }, 11);
                Grid.SetColumn(remove, 4); row.Children.Add(remove);
                _body.Children.Add(row);
            }
            _body.Children.Add(new WrapPanel { Children = { PropertyRows.Ghost("+  Add stinger…", () => _ = AddStingerFromPicker()) } });
        }

        // ---------------------------------------------------------------- names (renames follow every reference)
        private static string UniqueName(string stem, IEnumerable<string> taken)
        {
            var set = new HashSet<string>(taken.Where(n => n != null), StringComparer.OrdinalIgnoreCase);
            if (!set.Contains(stem)) return stem;
            for (int i = 2; ; i++) if (!set.Contains(stem + " " + i)) return stem + " " + i;
        }

        private void RenameStem(AdaptiveMusic.Stem stem, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || string.Equals(name, stem.Name, StringComparison.Ordinal)) return;
            name = UniqueName(name, _m.Stems.Where(s => s != stem).Select(s => s.Name));
            foreach (var st in _m.States)
                if (st.Volumes.TryGetValue(stem.Name ?? "", out float v)) { st.Volumes.Remove(stem.Name ?? ""); st.Volumes[name] = v; }
            stem.Name = name;
            Changed(structural: true);
        }

        private void RenameState(AdaptiveMusic.State state, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name == "*" || string.Equals(name, state.Name, StringComparison.Ordinal)) return;
            name = UniqueName(name, _m.States.Where(s => s != state).Select(s => s.Name));
            foreach (var t in _m.Transitions)
            {
                if (string.Equals(t.From, state.Name, StringComparison.OrdinalIgnoreCase)) t.From = name;
                if (string.Equals(t.To, state.Name, StringComparison.OrdinalIgnoreCase)) t.To = name;
            }
            if (string.Equals(_m.InitialState, state.Name, StringComparison.OrdinalIgnoreCase)) _m.InitialState = name;
            state.Name = name;
            Changed(structural: true);
        }

        private void RenameStinger(AdaptiveMusic.Stinger st, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || string.Equals(name, st.Name, StringComparison.Ordinal)) return;
            name = UniqueName(name, _m.Stingers.Where(s => s != st).Select(s => s.Name));
            foreach (var t in _m.Transitions.Append(_m.DefaultTransition))
                if (string.Equals(t.Stinger, st.Name, StringComparison.OrdinalIgnoreCase)) t.Stinger = name;
            st.Name = name;
            Changed(structural: true);
        }

        private static void SetClip(string pathOrRel, Action<string> setPath, Action<string> setGuid)
        {
            string rel = EditorKit.ToProjectRelative(pathOrRel);
            setPath(rel);
            try { setGuid(AssetDatabase.Instance.GetAssetByPath(rel)?.Guid.ToString() ?? ""); } catch { setGuid(""); }
        }

        // ---------------------------------------------------------------- adding clips
        private async Task AddStemsFromPicker()
        {
            var picked = await AssetPickerDialog.PickMany("Audio", ClipPatterns);
            if (picked != null && picked.Length > 0) AddStems(picked.ToList());
        }

        /// <summary>Add stems (project-relative or absolute; files outside the project are copied into Assets/Audio): each
        /// named after its file, at full volume in every state.</summary>
        public void AddStems(IList<string> files)
        {
            int n = 0;
            foreach (var f in files)
            {
                if (string.IsNullOrEmpty(f) || _m.Stems.Count >= 16) continue;
                string path = f;
                try { if (Path.IsPathRooted(path) && !EditorKit.IsInsideProject(path)) path = EditorKit.ImportIntoProject(path, "Audio"); } catch (Exception ex) { EditorCommands.Fail("Add stem", ex); continue; }
                var stem = new AdaptiveMusic.Stem { Name = UniqueName(Path.GetFileNameWithoutExtension(path), _m.Stems.Select(s => s.Name)) };
                SetClip(path, v => stem.ClipPath = v, v => stem.Guid = v);
                _m.Stems.Add(stem);
                foreach (var st in _m.States) st.Volumes[stem.Name] = 1f;
                n++;
            }
            if (n == 0) { if (_m.Stems.Count >= 16) SetStatus("16 stems is the most one music plays"); return; }
            Changed(structural: true);
            SetStatus("Added " + n + (n == 1 ? " stem" : " stems") + " — " + _m.Describe());
        }

        private async Task AddStingerFromPicker()
        {
            var r = await AssetPickerDialog.Pick(new AssetPickerOptions { Kind = "Audio", Title = "Stinger clip", Patterns = ClipPatterns, AllowNone = false });
            if (string.IsNullOrEmpty(r)) return;
            var st = new AdaptiveMusic.Stinger { Name = UniqueName(Path.GetFileNameWithoutExtension(r), _m.Stingers.Select(s => s.Name)) };
            SetClip(r, v => st.ClipPath = v, v => st.Guid = v);
            _m.Stingers.Add(st);
            Changed(structural: true);
        }

        private static List<string> DroppedClips(DragEventArgs e) => EditorKit.DroppedFiles(e).Where(f => EditorKit.HasExtension(f, ClipPatterns) && File.Exists(f)).ToList();

        // ---------------------------------------------------------------- preview (the game's own player)
        private void BuildPreviewBar()
        {
            _previewBar.Children.Clear();
            var play = new Button { Classes = { "accent" }, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new VxIcon { Icon = "Play", Width = 12, Height = 12, Foreground = EditorKit.Brush("VxTextOnAccentBrush") }, new TextBlock { Text = "Play", Foreground = EditorKit.Brush("VxTextOnAccentBrush") } } } };
            ToolTip.SetTip(play, "Play the music like the game does: stems sample-locked, in the start state");
            play.Click += (s, e) => PlayPreview();
            var stop = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new VxIcon { Icon = "Stop", Width = 12, Height = 12 }, new TextBlock { Text = "Stop" } } } };
            stop.Click += (s, e) => StopPreview();
            _previewBar.Children.Add(play);
            _previewBar.Children.Add(stop);
            foreach (var state in _m.States)
            {
                var b = new Button { Content = state.Name, Classes = { "ghost" } };
                ToolTip.SetTip(b, "SetState(\"" + state.Name + "\") — lands on the boundary its rule names");
                b.Click += (s, e) => { if (_preview != null && _preview.SetState(state.Name)) SetStatus("→ " + state.Name + " on the " + SyncLabels[(int)_preview.LastSwitchSync].ToLowerInvariant()); };
                _previewBar.Children.Add(b);
            }
            foreach (var par in _m.Stems.Select(s => s.Parameter).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var label = new TextBlock { Text = par, Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                var slider = new Slider { Minimum = 0, Maximum = 1, Width = 110, Value = _m.Parameters.TryGetValue(par, out float v0) ? v0 : 0f, VerticalAlignment = VerticalAlignment.Center };
                string name = par;
                slider.ValueChanged += (s, e) => _preview?.SetParameter(name, (float)slider.Value);
                ToolTip.SetTip(slider, "SetParameter(\"" + par + "\", 0..1)");
                _previewBar.Children.Add(label);
                _previewBar.Children.Add(slider);
            }
            foreach (var st in _m.Stingers)
            {
                var b = new Button { Content = "♪ " + st.Name, Classes = { "ghost" } };
                ToolTip.SetTip(b, "PlayStinger(\"" + st.Name + "\") — on the " + SyncLabels[(int)st.SyncMode].ToLowerInvariant());
                b.Click += (s, e) => _preview?.PlayStinger(st.Name);
                _previewBar.Children.Add(b);
            }
        }

        /// <summary>Start the preview from the top (stops the one playing).</summary>
        internal bool PlayPreview()
        {
            StopPreview();
            _preview = AdaptiveMusicPlayer.Start(_m, rel => { string full = EditorKit.ToAbsolute(rel); return full != null && File.Exists(full) ? full : null; }, VortexAudio.BusMusic, 1f, out string error);
            if (_preview == null) { SetStatus("Cannot play: " + error); return false; }
            _lastTick = DateTime.Now;
            _previewTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (s, e) => TickPreview());
            _previewTimer.Start();
            return true;
        }

        internal void StopPreview()
        {
            _previewTimer?.Stop();
            if (_preview != null) { try { VortexAudio.StopVoice(_preview.Handle); } catch { } _preview = null; }
        }

        private void TickPreview()
        {
            var p = _preview;
            if (p == null) return;
            if (!VortexAudio.IsVoiceValid(p.Handle)) { StopPreview(); SetStatus("The music ended"); return; }
            var now = DateTime.Now;
            double dt = (now - _lastTick).TotalSeconds;
            _lastTick = now;
            p.Tick(dt, null);
            if (p.TryGetMusical(out double m, out var c))
            {
                long beat = p.Timing.BeatAt(m);
                string where = beat < 0 ? "pickup" : "bar " + (p.Timing.BarAt(m) + 1) + " · beat " + (beat % p.Timing.BeatsPerBar + 1);
                SetStatus(where + "  ·  state " + (p.State.Length > 0 ? p.State : "(none)") + "  ·  " + (c.Position / (double)Math.Max(1, c.SampleRate)).ToString("0.00", CultureInfo.InvariantCulture) + " s"
                          + (c.Stalled != 0 ? "  ·  waiting for the stream" : ""));
            }
        }

        private void AuditionClip(string full)
        {
            if (full == null || !File.Exists(full)) { SetStatus("Clip not found"); return; }
            try { EditorAudition(full); } catch (Exception ex) { EditorCommands.Fail("Audition", ex); }
        }

        private ulong _audition = VortexAudio.InvalidVoice;
        private void EditorAudition(string full)
        {
            if (_audition != VortexAudio.InvalidVoice) VortexAudio.StopVoice(_audition);
            _audition = VortexAudio.PlayVoice(full, 1f, 1f, 0f, false, 0, true, VortexAudio.BusMusic);
        }

        // ---------------------------------------------------------------- persistence
        private void Persist()
        {
            try { _m.Normalize(); _m.Save(_path); }
            catch (Exception ex) { SetStatus("Could not save: " + ex.Message); }
        }

        private void SetStatus(string text) => _status.Text = text;
    }
}
