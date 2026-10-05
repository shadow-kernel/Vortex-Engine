using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Editor.Core.Assets.Library;
using Editor.Core.Assets.Store;
using Editor.Core.Audio.SoundStudio;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Controls;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Services;
using VortexEditor.Shell.Library;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;
using AssetActions = VortexEditor.Services.AssetActions;

namespace VortexEditor.Shell.Audio
{
    /// <summary>
    /// Claude Sound Studio (#79): describe a sound, get takes, audition them (A/B by clicking), refine by chatting
    /// ("dumpfer, mehr Hall, kürzer") and save the one you like into the asset library and the project — with its
    /// recipe, so it can be reopened and regenerated later (#83). Backends: Procedural (offline, free), ElevenLabs
    /// sound effects, fal.ai (CassetteAI SFX, Stable Audio for music beds and long ambience). With "Claude designs the
    /// prompts" on, Claude writes the detailed prompts and calls the backend per take (#81); otherwise the prompt goes
    /// to the backend as written. Keys are the user's own (Keys…), never shipped with the editor.
    /// </summary>
    public sealed class SoundStudioWindow : Window
    {
        private static SoundStudioWindow _open;
        public static SoundStudioWindow Current => _open;

        public static SoundStudioWindow Open(SoundRecipe recipe = null, string parentHash = null)
        {
            if (_open == null)
            {
                _open = new SoundStudioWindow();
                _open.Closed += (s, e) => { AssetActions.StopAudition(); _open = null; };
                var owner = EditorKit.ActiveWindow();
                if (owner != null) _open.Show(owner); else _open.Show();
            }
            else _open.Activate();
            if (recipe != null) _open.LoadRecipe(recipe, parentHash);
            return _open;
        }

        private sealed class Take
        {
            public GeneratedSound Sound;
            public Border Card;
            public bool Saved;
        }

        private readonly ComboBox _backend = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _backendInfo = new TextBlock { Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _keyInfo = new TextBlock { Classes = { "small" }, TextWrapping = TextWrapping.Wrap };
        private readonly CheckBox _claude = new CheckBox { Content = "Claude designs the prompts" };
        private readonly ComboBox _model = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = SoundDesigner.Models, SelectedIndex = 0 };
        private readonly Slider _length = new Slider { Minimum = 0, Maximum = 30, Value = 0, SmallChange = 0.5, LargeChange = 5 };
        private readonly TextBlock _lengthText = new TextBlock { Classes = { "small", "secondary" } };
        private readonly CheckBox _loop = new CheckBox { Content = "Seamless loop" };
        private readonly CheckBox _pcm = new CheckBox { Content = "Uncompressed WAV", FontSize = 11 };
        private readonly Slider _influence = new Slider { Minimum = 0, Maximum = 1, Value = 0.3, SmallChange = 0.05 };
        private readonly TextBlock _influenceText = new TextBlock { Classes = { "small", "secondary" } };
        private readonly ComboBox _takes = new ComboBox { ItemsSource = new[] { 1, 2, 3, 4 }, SelectedIndex = 1, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock _costs = new TextBlock { Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _prompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, Watermark = "Describe the sound — e.g. “wet footsteps on basement concrete, slow, echoing”" };
        private readonly WrapPanel _presets = new WrapPanel();
        private readonly Button _generate;
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly WrapPanel _takeCards = new WrapPanel();
        private readonly StackPanel _chat = new StackPanel { Spacing = 4 };
        private readonly Border _chatHost;
        private readonly TextBox _feedback = new TextBox { Watermark = "Feedback — “duller, more reverb, shorter”" };
        private readonly ScrollViewer _chatScroll;
        private readonly List<Take> _taken = new List<Take>();
        private TextBlock _streaming;
        private SoundDesigner _designer;
        private CancellationTokenSource _cts;
        private string _parentHash;
        private string _directInput;
        private int _seed;
        private string _outDir;

        /// <summary>The takes of this session (tests).</summary>
        public IReadOnlyList<GeneratedSound> Takes => _taken.Select(t => t.Sound).ToList();
        public bool Busy { get; private set; }
        public string LastError { get; private set; }
        private ISoundBackend Backend => SoundBackends.All[Math.Max(0, _backend.SelectedIndex)];

        public SoundStudioWindow()
        {
            Title = "Sound Studio";
            Width = 1020; Height = 740; MinWidth = 820; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _outDir = Path.Combine(GlobalAssetDatabase.Instance.TempDir, "soundstudio", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

            // ---- left: settings
            _backend.ItemsSource = SoundBackends.All.Select(b => b.Name).ToList();
            _backend.SelectedIndex = SoundBackends.All.ToList().FindIndex(b => b.KeyId != null && StoreKeys.Has(b.KeyId)) is int i && i >= 0 ? i : 0;
            _backend.SelectionChanged += (s, e) => OnBackendChanged();
            _pcm.IsCheckedChanged += (s, e) => ApplyFormat();
            _claude.IsCheckedChanged += (s, e) => { _model.IsEnabled = _claude.IsChecked == true; UpdateKeyInfo(); _chatHost.IsVisible = _claude.IsChecked == true; };
            _length.PropertyChanged += (s, e) => { if (e.Property == RangeBase_Value) UpdateLength(); };
            _influence.PropertyChanged += (s, e) => { if (e.Property == RangeBase_Value) _influenceText.Text = "Prompt influence " + _influence.Value.ToString("0.00") + " (higher = more literal)"; };
            var left = new StackPanel { Margin = new Thickness(18, 16, 14, 16), Spacing = 6 };
            left.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new VxIcon { Icon = "Audio", Width = 20, Height = 20, Foreground = EditorKit.Brush("VxAccentBrush") }, new TextBlock { Text = "Sound Studio", FontSize = 18, FontWeight = FontWeight.SemiBold } } });
            left.Children.Add(LibraryUi.Small("Describe a sound, audition the takes, refine, save the best one into your library."));
            left.Children.Add(Ui.Header("Backend", new Thickness(0, 10, 0, 2)));
            left.Children.Add(_backend);
            left.Children.Add(_backendInfo);
            left.Children.Add(_keyInfo);
            var keys = Ui.Button("Keys…", () => _ = OpenKeys(), "Your own API keys (ElevenLabs, fal.ai, Anthropic) — stored only on this machine");
            keys.HorizontalAlignment = HorizontalAlignment.Left;
            left.Children.Add(keys);
            left.Children.Add(Ui.Header("Designer", new Thickness(0, 10, 0, 2)));
            left.Children.Add(_claude);
            left.Children.Add(_model);
            left.Children.Add(LibraryUi.Small("Claude writes detailed prompts (any language in, English prompts out), makes distinct takes and refines them from your feedback. Uses your Anthropic key."));
            left.Children.Add(Ui.Header("Takes", new Thickness(0, 10, 0, 2)));
            left.Children.Add(_lengthText);
            left.Children.Add(_length);
            left.Children.Add(_loop);
            left.Children.Add(_pcm);
            left.Children.Add(_influenceText);
            left.Children.Add(_influence);
            left.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Takes per run", VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } }, _takes } });
            left.Children.Add(_costs);
            var leftHost = new Border { Width = 300, BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 0, 1, 0), Child = new ScrollViewer { Content = left } };
            DockPanel.SetDock(leftHost, Dock.Left);

            // ---- right: prompt, takes, chat
            foreach (var p in SoundPresets.All)
            {
                var preset = p;
                var chip = new Button { Content = p.name, Classes = { "ghost" }, FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 5, 5) };
                ToolTip.SetTip(chip, p.prompt);
                chip.Click += (s, e) => ApplyPreset(preset);
                _presets.Children.Add(chip);
            }
            _generate = Ui.Button("Generate", () => _ = Generate(), "Make takes (⌘↩)", "accent", 120);
            var newIdea = Ui.Button("New Idea", NewIdea, "Start over (forget the conversation)");
            var stop = Ui.Button("Stop", () => _cts?.Cancel(), "Cancel the running generation");
            _prompt.KeyDown += (s, e) => { if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control))) { _ = Generate(); e.Handled = true; } };
            _feedback.KeyDown += (s, e) => { if (e.Key == Key.Enter) { _ = Refine(); e.Handled = true; } };
            var refine = Ui.Button("Refine", () => _ = Refine(), "Send your feedback — Claude rewrites the prompt and makes new takes", "accent");
            var fbRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
            fbRow.Children.Add(_feedback);
            Grid.SetColumn(refine, 1); refine.Margin = new Thickness(8, 0, 0, 0); fbRow.Children.Add(refine);
            _chatScroll = new ScrollViewer { Content = _chat, MaxHeight = 190 };
            _chatHost = new Border
            {
                Classes = { "card" }, Padding = new Thickness(10), Margin = new Thickness(0, 10, 0, 0), IsVisible = false,
                Child = new StackPanel { Children = { new TextBlock { Text = "Conversation", Classes = { "small", "secondary" }, FontWeight = FontWeight.SemiBold }, _chatScroll, fbRow } },
            };
            var right = new StackPanel { Margin = new Thickness(18, 16, 18, 16), Spacing = 8 };
            right.Children.Add(_prompt);
            right.Children.Add(_presets);
            right.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _generate, stop, newIdea } });
            right.Children.Add(_status);
            right.Children.Add(Ui.Header("Takes", new Thickness(0, 6, 0, 0)));
            right.Children.Add(LibraryUi.Small("Click a take to hear it — click another to compare. Save the one you like."));
            right.Children.Add(_takeCards);
            right.Children.Add(_chatHost);

            var dock = new DockPanel();
            dock.Children.Add(leftHost);
            dock.Children.Add(new ScrollViewer { Content = right });
            Content = dock;
            OnBackendChanged();
            UpdateLength();
            _influenceText.Text = "Prompt influence 0.30 (higher = more literal)";
            _claude.IsChecked = StoreKeys.Has("anthropic");
            _model.IsEnabled = _claude.IsChecked == true;
            _chatHost.IsVisible = _claude.IsChecked == true;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) AssetActions.StopAudition(); };
        }

        private static readonly AvaloniaProperty RangeBase_Value = Avalonia.Controls.Primitives.RangeBase.ValueProperty;

        // ================================================================== settings
        private void OnBackendChanged()
        {
            var b = Backend;
            _backendInfo.Text = b.Tagline + ". " + b.Notice;
            _length.Maximum = b.MaxSeconds;
            _loop.IsVisible = b.SupportsLoop;
            _influence.IsVisible = _influenceText.IsVisible = b.SupportsInfluence;
            _pcm.IsVisible = b is ElevenLabsBackend || b is StabilityAudioBackend;
            _pcm.Content = b is ElevenLabsBackend ? "Uncompressed WAV (ElevenLabs Creator/Pro plans)" : "Uncompressed WAV";
            ApplyFormat();
            UpdateLength();
            UpdateKeyInfo();
        }

        /// <summary>MP3 by default; the WAV option where the backend offers it.</summary>
        private void ApplyFormat()
        {
            bool wav = _pcm.IsChecked == true;
            if (Backend is ElevenLabsBackend el) el.OutputFormat = wav ? "pcm_44100" : "mp3_44100_128";
            else if (Backend is StabilityAudioBackend sa) sa.OutputFormat = wav ? "wav" : "mp3";
        }

        private void UpdateLength()
        {
            double v = Math.Round(_length.Value * 2) / 2;
            _lengthText.Text = "Length: " + (v < 0.5 ? "auto (the model decides)" : v.ToString("0.#") + " s");
            UpdateCosts();
        }

        private void UpdateKeyInfo()
        {
            var b = Backend;
            var parts = new List<string>();
            if (b.KeyId != null) parts.Add(StoreKeys.Has(b.KeyId) ? "✓ " + b.Name + " key set" : "⚠ " + b.Name + " needs your key");
            if (_claude.IsChecked == true) parts.Add(StoreKeys.Has("anthropic") ? "✓ Anthropic key set" : "⚠ Claude needs your Anthropic key");
            _keyInfo.Text = string.Join("\n", parts);
            _keyInfo.Foreground = parts.Any(p => p.StartsWith("⚠")) ? EditorKit.Brush("VxOrangeBrush") : EditorKit.Brush("VxTextSecondaryBrush");
            _keyInfo.IsVisible = parts.Count > 0;
            UpdateCosts();
        }

        private void UpdateCosts()
        {
            var req = Request("");
            string per = Backend.CostHint(req);
            string tokens = _designer != null && (_designer.InputTokens + _designer.OutputTokens) > 0 ? " · Claude this session: " + _designer.InputTokens + " in / " + _designer.OutputTokens + " out tokens" : "";
            _costs.Text = "Cost per take: " + per + tokens;
        }

        private async Task OpenKeys()
        {
            await StoreKeysDialog.Run(null);
            UpdateKeyInfo();
        }

        private SoundRequest Request(string prompt)
        {
            double v = Math.Round(_length.Value * 2) / 2;
            return new SoundRequest
            {
                Prompt = prompt, DurationSeconds = v < 0.5 ? (double?)null : v, Loop = Backend.SupportsLoop && _loop.IsChecked == true,
                PromptInfluence = _influence.Value,
            };
        }

        private void ApplyPreset((string name, string prompt, double? seconds, bool loop) p)
        {
            _prompt.Text = p.prompt;
            if (p.seconds.HasValue) _length.Value = Math.Min(Backend.MaxSeconds, p.seconds.Value);
            _loop.IsChecked = p.loop;
        }

        /// <summary>Reopen a saved sound's recipe: the next generation makes a sibling linked to it.</summary>
        public void LoadRecipe(SoundRecipe r, string parentHash)
        {
            NewIdea();
            _parentHash = parentHash;
            int idx = SoundBackends.All.ToList().FindIndex(b => b.Id == r.Backend);
            if (idx >= 0) _backend.SelectedIndex = idx;
            _prompt.Text = r.Prompt;
            _length.Value = r.DurationSeconds ?? 0;
            _loop.IsChecked = r.Loop;
            _influence.Value = r.PromptInfluence;
            if (Backend is ElevenLabsBackend || Backend is StabilityAudioBackend)
                _pcm.IsChecked = r.Format != null && (r.Format.StartsWith("pcm", StringComparison.Ordinal) || r.Format == "wav");
            _seed = r.Seed + 1;
            if (r.Designer != null && SoundDesigner.Models.Contains(r.Designer)) _model.SelectedItem = r.Designer;
            SetStatus("Recipe loaded" + (string.IsNullOrEmpty(r.Label) ? "" : " (“" + r.Label + "”)") + ". Generating again makes a new sibling — the saved sound stays as it is.", false);
        }

        // ================================================================== generate
        private void SetStatus(string text, bool error)
        {
            _status.Text = text ?? "";
            _status.Foreground = error ? EditorKit.Brush("VxRedBrush") : EditorKit.Brush("VxTextSecondaryBrush");
            if (error) LastError = text;
        }

        private void NewIdea()
        {
            _designer = null;
            _directInput = null;
            _parentHash = null;
            _chat.Children.Clear();
            SetStatus("", false);
            UpdateCosts();
        }

        /// <summary>The Generate button (tests await it).</summary>
        public async Task Generate()
        {
            string text = (_prompt.Text ?? "").Trim();
            if (text.Length == 0) { SetStatus("Describe the sound first (or click a preset).", true); return; }
            if (Busy) return;
            int count = _takes.SelectedItem is int n ? n : 2;
            if (_claude.IsChecked == true) { await RunDesigner(text, count, firstTurn: _designer == null); return; }
            await RunDirect(text, count);
        }

        private async Task RunDirect(string prompt, int count)
        {
            var b = Backend;
            if (b.KeyId != null && !StoreKeys.Has(b.KeyId)) { SetStatus(b.Name + " needs your key — click Keys….", true); return; }
            _directInput = _directInput ?? prompt;
            Busy = true; _generate.IsEnabled = false; LastError = null;
            _cts = new CancellationTokenSource();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    SetStatus("Generating take " + (i + 1) + " of " + count + " with " + b.Name + "…", false);
                    var req = Request(prompt);
                    req.Seed = _seed++;
                    req.Label = "Take " + (_taken.Count + 1);
                    var g = await Task.Run(() => b.GenerateAsync(req, _outDir, _cts.Token));
                    AddTake(g);
                }
                SetStatus("Done — click a take to hear it.", false);
            }
            catch (OperationCanceledException) { SetStatus("Stopped.", false); }
            catch (HttpRequestException ex) { SetStatus(NetworkProblem(ex), true); }
            catch (Exception ex) { SetStatus(ex.Message, true); }
            finally { Busy = false; _generate.IsEnabled = true; UpdateCosts(); }
        }

        private async Task RunDesigner(string text, int count, bool firstTurn)
        {
            if (!StoreKeys.Has("anthropic")) { SetStatus("Claude needs your Anthropic key — click Keys… (or switch off “Claude designs the prompts”).", true); return; }
            var b = Backend;
            if (b.KeyId != null && !StoreKeys.Has(b.KeyId)) { SetStatus(b.Name + " needs your key — click Keys….", true); return; }
            if (_designer == null || firstTurn)
            {
                _designer = new SoundDesigner { OutDir = _outDir };
                _designer.TextDelta += t => Dispatcher.UIThread.Post(() => AppendStreaming(t));
                _designer.TakeReady += g => Dispatcher.UIThread.Post(() => AddTake(g));
                _designer.Status += s => Dispatcher.UIThread.Post(() => SetStatus(s, false));
            }
            _designer.Backend = b;
            _designer.Model = _model.SelectedItem as string ?? SoundDesigner.Models[0];
            _designer.Defaults = Request(text);
            AddChat("You", text);
            _streaming = null;
            Busy = true; _generate.IsEnabled = false; LastError = null;
            _cts = new CancellationTokenSource();
            SetStatus("Claude is designing…", false);
            try
            {
                var takes = await Task.Run(() => _designer.SendAsync(text, count, _cts.Token));
                SetStatus(takes.Count > 0 ? "Done — click a take to hear it, then give feedback or save." : "Claude made no takes — see the conversation.", takes.Count == 0);
            }
            catch (OperationCanceledException) { SetStatus("Stopped.", false); }
            catch (HttpRequestException ex) { SetStatus(NetworkProblem(ex), true); }
            catch (Exception ex) { SetStatus(ex.Message, true); }
            finally { Busy = false; _generate.IsEnabled = true; UpdateCosts(); }
        }

        private static string NetworkProblem(HttpRequestException ex)
            => "No connection to the service (" + (ex.InnerException?.Message ?? ex.Message) + "). Check the internet connection and try again.";

        private async Task Refine()
        {
            string fb = (_feedback.Text ?? "").Trim();
            if (fb.Length == 0) return;
            if (_designer == null) { _prompt.Text = (_prompt.Text ?? "") + ", " + fb; await Generate(); _feedback.Text = ""; return; }
            _feedback.Text = "";
            int count = _takes.SelectedItem is int n ? n : 2;
            await RunDesigner(fb, count, firstTurn: false);
        }

        private void AddChat(string who, string text)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            tb.Inlines = new Avalonia.Controls.Documents.InlineCollection
            {
                new Avalonia.Controls.Documents.Run(who + ": ") { FontWeight = FontWeight.SemiBold, Foreground = who == "You" ? EditorKit.Brush("VxAccentBrush") : EditorKit.Brush("VxGreenBrush") },
                new Avalonia.Controls.Documents.Run(text),
            };
            _chat.Children.Add(tb);
            Dispatcher.UIThread.Post(() => _chatScroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        private void AppendStreaming(string delta)
        {
            if (_streaming == null)
            {
                _streaming = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                _chat.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "Claude: ", FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = EditorKit.Brush("VxGreenBrush") } } });
                _chat.Children.Add(_streaming);
            }
            _streaming.Text += delta;
            _chatScroll.ScrollToEnd();
        }

        // ================================================================== takes
        private void AddTake(GeneratedSound g)
        {
            var t = new Take { Sound = g };
            var wave = new Border { Width = 222, Height = 64, CornerRadius = new CornerRadius(6), Background = EditorKit.Brush("VxFieldBrush"), ClipToBounds = true, Child = new VxIcon { Icon = "Audio", Width = 22, Height = 22 } };
            WaveformThumbs.Request(g.FilePath, (bmp, tip) => { if (bmp != null) wave.Child = new Image { Source = bmp, Stretch = Stretch.Fill }; });
            var label = new TextBlock { Text = g.Label ?? "Take " + (_taken.Count + 1), FontWeight = FontWeight.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 222 };
            var facts = new TextBlock { Text = SoundBackends.Get(g.BackendId).Name + " · " + (g.Request.DurationSeconds.HasValue ? g.Request.DurationSeconds.Value.ToString("0.#") + " s" : "auto") + (g.Request.Loop ? " · loop" : ""), Classes = { "small", "tertiary" } };
            var play = Ui.Button("▶ Play", () => Audition(t));
            var save = Ui.Button("Save", () => SaveTake(t, false), "Into the asset library");
            var add = Ui.Button("Save + Add", () => SaveTake(t, true), "Into the library and the open project (Assets/Audio)", "accent");
            add.IsEnabled = ProjectData.Current != null;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { play, save, add } };
            var card = new Border
            {
                Width = 244, Padding = new Thickness(10), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(10),
                Background = EditorKit.Brush("VxPanelRaisedBrush"), BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1.5),
                Child = new StackPanel { Spacing = 4, Children = { wave, label, facts, buttons } },
            };
            ToolTip.SetTip(card, g.Request.Prompt);
            card.PointerPressed += (s, e) => { if (!(e.Source is Button)) Audition(t); };
            t.Card = card;
            _taken.Add(t);
            _takeCards.Children.Insert(0, card);
        }

        private void Audition(Take t)
        {
            foreach (var x in _taken) x.Card.BorderBrush = EditorKit.Brush(x.Saved ? "VxGreenBrush" : "VxHairlineBrush");
            t.Card.BorderBrush = EditorKit.Brush("VxAccentBrush");
            AssetActions.Audition(t.Sound.FilePath);
        }

        /// <summary>Switch the Claude designer on/off (tests, menu).</summary>
        public void UseClaude(bool on) => _claude.IsChecked = on;

        /// <summary>Save a take (tests use index 0).</summary>
        public SoundStudioLibrary.SaveResult SaveTake(int index, bool addToProject) => SaveTake(_taken[index], addToProject);

        private SoundStudioLibrary.SaveResult SaveTake(Take t, bool addToProject)
        {
            var recipe = SoundRecipe.From(t.Sound, _designer?.FirstInput ?? _directInput ?? t.Sound.Request.Prompt,
                                          _designer != null ? _designer.Model : null, _designer?.Notes, _parentHash);
            var p = ProjectData.Current;
            var r = SoundStudioLibrary.Save(t.Sound, recipe, addToProject && p != null, p?.Path, p?.Name);
            if (!r.Success) { SetStatus("Saving failed: " + r.Error, true); return r; }
            t.Saved = true;
            t.Card.BorderBrush = EditorKit.Brush("VxGreenBrush");
            LibraryThumbs.Ensure(r.Entry.Hash, t.Sound.FilePath, r.Entry.Type);
            if (r.ProjectPath != null) { try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { } EditorCommands.Window?.FileTree?.Reload(); }
            string where = r.ProjectPath != null ? "library + " + Ui.ProjectRelative(r.ProjectPath) : "your library";
            SetStatus("Saved “" + r.Entry.Name + "” to " + where + ".", false);
            EditorCommands.Toast("Saved “" + r.Entry.Name + "”");
            return r;
        }
    }
}
