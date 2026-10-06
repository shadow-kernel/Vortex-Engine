using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Editor.Core.Assets.Store;
using Editor.Core.Claude;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Controls;
using VortexEditor.Shell;
using VortexEditor.Shell.Material;

namespace VortexEditor.Claude
{
    /// <summary>
    /// The Claude sidebar (3.0.3): a conversation with Claude in its own column on the right of the editor, shown and
    /// hidden like a sidebar (⌘9, View ▸ Claude, the Claude button in the title bar). It works on the editor's tools —
    /// the same tools as the MCP server (<see cref="ToolCatalog"/>, run through <see cref="ToolHost"/>: same undo
    /// steps, same safety).
    /// <list type="bullet">
    /// <item><b>Ask</b> answers and looks around (scene, viewport, scripts) and changes nothing; <b>Agent</b> builds and
    /// changes the project and asks before each change unless the user allowed that tool always.</item>
    /// <item>The composer picks the model, the effort (how hard Claude thinks) and the context size (the conversation is
    /// compacted on the server before it outgrows it); Claude's thinking shows as collapsible summaries.</item>
    /// <item>Credentials are the user's own (<see cref="ClaudeAccount"/>): the Anthropic account they signed in to with
    /// the Anthropic CLI, or an API key. A Claude subscription works in Claude Code, which drives the editor through
    /// MCP — "Open in Claude Code".</item>
    /// </list>
    /// </summary>
    public sealed class ClaudePanel : UserControl
    {
        public static ClaudePanel Current { get; private set; }

        private readonly ClaudeSession _session = new ClaudeSession();
        private readonly ClaudePanelSettings _settings = ClaudePanelSettings.Current;
        private readonly StackPanel _log = new StackPanel { Spacing = 10, Margin = new Thickness(12, 12, 12, 8) };
        private readonly ScrollViewer _scroll;
        private readonly ContentControl _body = new ContentControl();
        private readonly Control _signIn;
        private readonly TextBox _input = new TextBox
        {
            Classes = { "bare" }, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 220,
            Watermark = "Ask Claude to build or explain…", Padding = new Thickness(10, 8, 10, 2),
        };
        private readonly Button _send = new Button { Classes = { "accent" }, Width = 28, Height = 26, MinHeight = 26, Padding = new Thickness(0), CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Bottom };
        private readonly Button _modeChip = Chip(), _modelChip = Chip(), _effortChip = Chip(), _contextChip = Chip();
        private readonly WrapPanel _attachments = new WrapPanel { Margin = new Thickness(8, 6, 8, 0) };
        private readonly TextBlock _status = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly ProgressBar _meter = new ProgressBar { Width = 44, MinWidth = 0, Height = 3, MinHeight = 3, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly Button _account = new Button { Classes = { "chip" }, Padding = new Thickness(6, 1) };
        private readonly Control _empty;
        private CancellationTokenSource _cts;
        private MarkdownBlock _streaming;
        private ThinkingBlock _thinking;
        private readonly Dictionary<string, ToolCard> _cards = new Dictionary<string, ToolCard>();
        private bool _busy;
        private bool _excludeSelection;

        // sign-in view parts
        private readonly Button _signInButton = new Button { Classes = { "accent" }, Content = "Sign in with Anthropic", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
        private readonly StackPanel _signInWait = new StackPanel { Spacing = 6, IsVisible = false };
        private readonly TextBlock _signInWaitText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly Button _signInLink = new Button { Classes = { "link" }, Content = "Open the sign-in page", IsVisible = false, FontSize = 12 };
        private readonly StackPanel _cliInstall = new StackPanel { Spacing = 6, IsVisible = false };
        private readonly StackPanel _keyBox = new StackPanel { Spacing = 6, IsVisible = false };
        private readonly TextBox _keyField = new TextBox { PasswordChar = '•', Watermark = "sk-ant-…" };
        private readonly TextBlock _signInError = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, IsVisible = false };
        private readonly Button _terminalSignIn = new Button { Content = "Sign in from a terminal", HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
        private CancellationTokenSource _signInCts;
        private string _signInUrl;

        // Chat (the editor's own chat on the Anthropic API) or Claude Code (the user's own Claude Code in a terminal)
        private readonly ClaudeCodePane _codePane = new ClaudeCodePane();
        private readonly ContentControl _main = new ContentControl();
        private DockPanel _chatRoot;
        private readonly RadioButton _tabChat = new RadioButton { GroupName = "claudemode", Content = "Chat" };
        private readonly RadioButton _tabCode = new RadioButton { GroupName = "claudemode", Content = "Claude Code" };

        /// <summary>Claude Code runs in the sidebar (instead of the chat).</summary>
        public bool IsCodeMode => ReferenceEquals(_main.Content, _codePane);
        internal ClaudeCodePane CodePane => _codePane;
        /// <summary>The user opened the sign-in view on purpose (to change the key) — coming back to the window does not
        /// switch to the chat until they finish or go back.</summary>
        private bool _stayOnSignIn;
        private readonly Button _backToChat = new Button { Classes = { "link" }, Content = "← Back to the chat", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12 };

        public bool IsBusy => _busy;
        /// <summary>The visible transcript (tests).</summary>
        internal StackPanel Log => _log;
        internal ClaudeSession Session => _session;
        /// <summary>The sign-in view is showing (no credentials).</summary>
        internal bool ShowsSignIn => ReferenceEquals(_body.Content, _signIn);

        internal const string SystemPrompt =
            "You are Claude, working inside the Vortex Engine editor on the user's open project. You operate the editor with tools: " +
            "read the scene, create and change entities, lights, materials, scripts and audio, run the game and look at the viewport.\n" +
            "- The user works in one of two modes, named in a note at the start of their message: Ask (answer and look around — " +
            "tools that change the project are switched off) or Agent (build and change the project).\n" +
            "- Answer in the user's language and keep replies short — the user sees every tool call as a card.\n" +
            "- Look before you build (scene_outline, find_entities, get_entity, get_bounds). After building something visible, " +
            "capture_viewport to check it, and fix what looks wrong.\n" +
            "- Prefer one macro (place_grid, scatter, bulk_set_properties) over many single calls.\n" +
            "- The user approves tools that change the project; if they deny one, ask what they want instead of retrying.\n" +
            "- A note in the user's message may name what is selected in the editor; \"this\" or \"it\" usually means that.\n" +
            "- Write game code against the real Vortex API (get_scripting_api), not Unity's.\n\n" + McpHost.Instructions;

        public ClaudePanel()
        {
            Current = this;
            _session.SystemPrompt = SystemPrompt;
            _session.Tools = ToolCatalog.All.Select(t => new ChatTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchema.GetRawText(), ReadOnly = t.ReadOnly }).ToList();
            _session.RunTool = RunToolAsync;
            _session.Approve = ApproveAsync;
            _session.ThinkingStarted += () => Dispatcher.UIThread.Post(StartThinking);
            _session.ThinkingDelta += d => Dispatcher.UIThread.Post(() => _thinking?.Append(d));
            _session.TextStarted += () => Dispatcher.UIThread.Post(() => { EndThinking(); EndText(); });
            _session.TextDelta += d => Dispatcher.UIThread.Post(() => AppendText(d));
            _session.ToolStarted += c => Dispatcher.UIThread.Post(() => { EndThinking(); EndText(); AddCard(c); });
            _session.ToolFinished += c => Dispatcher.UIThread.Post(() => { if (_cards.TryGetValue(c.Id, out var card)) card.Finish(c, _session.Mode); });
            _session.Compacted += () => Dispatcher.UIThread.Post(() => AddDivider("Earlier turns were summarized to stay within " + ClaudeModels.ContextLabel(_session.ContextSize) + " of context"));
            _session.Refused += why => Dispatcher.UIThread.Post(() => AddNote(why, true));
            _session.TurnCompleted += () => Dispatcher.UIThread.Post(UpdateStatus);
            ApplyChoices();

            // ---- header: title, new chat, menu, close
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new VxIcon { Icon = "Sparkle", Width = 14, Height = 14, Foreground = EditorKit.Brush("VxAccentBrush"), VerticalAlignment = VerticalAlignment.Center });
            ToolTip.SetTip(_tabChat, "Chat — the editor's own Claude chat, billed to your Anthropic Console account (API)");
            ToolTip.SetTip(_tabCode, "Claude Code — Anthropic's Claude Code in the sidebar, with your Claude Pro / Max plan or Console account, connected to this editor");
            _tabChat.Click += (s, e) => SetBackend(false);
            _tabCode.Click += (s, e) => SetBackend(true);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            tabs.Children.Add(_tabChat);
            tabs.Children.Add(_tabCode);
            title.Children.Add(new Border { Classes = { "segmented" }, Child = tabs, VerticalAlignment = VerticalAlignment.Center });
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(IconButton("Plus", "New chat", () => { if (IsCodeMode) _codePane.Terminal.SendText("/clear\r"); else NewChat(); }));
            right.Children.Add(MenuButton());
            right.Children.Add(IconButton("Close", "Hide the Claude sidebar (⌘9)", () => EditorCommands.Window?.TogglePanel(MainWindow.PanelClaude)));
            var header = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            header.Children.Add(right);
            header.Children.Add(title);
            var headerBorder = new Border { Classes = { "panelheader" }, Padding = new Thickness(12, 0, 4, 0), Child = header };
            DockPanel.SetDock(headerBorder, Dock.Top);

            // ---- composer: what is attached (selection), the text, the pickers and send; below it context, cost, account
            _send.Content = new VxIcon { Icon = "ArrowUp", Width = 14, Height = 14 };
            ToolTip.SetTip(_send, "Send (Enter)");
            _send.Click += (s, e) => { if (_busy) Stop(); else _ = SendAsync(_input.Text); };
            _input.AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; if (!_busy) _ = SendAsync(_input.Text); }
                else if (e.Key == Key.Escape && _busy) { e.Handled = true; Stop(); }
            }, RoutingStrategies.Tunnel);
            _modeChip.Click += (s, e) => ModeMenu().ShowAt(_modeChip);
            _modelChip.Click += (s, e) => ModelMenu().ShowAt(_modelChip);
            _effortChip.Click += (s, e) => EffortMenu().ShowAt(_effortChip);
            _contextChip.Click += (s, e) => ContextMenuFor().ShowAt(_contextChip);
            var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            chips.Children.Add(_modeChip);
            chips.Children.Add(_modelChip);
            chips.Children.Add(_effortChip);
            chips.Children.Add(_contextChip);
            var tools = new DockPanel { Margin = new Thickness(4, 2, 5, 5) };
            DockPanel.SetDock(_send, Dock.Right);
            tools.Children.Add(_send);
            tools.Children.Add(chips);
            var composerStack = new StackPanel();
            composerStack.Children.Add(_attachments);
            composerStack.Children.Add(_input);
            composerStack.Children.Add(tools);
            var composer = new Border { Classes = { "composer" }, Child = composerStack };

            _account.Click += (s, e) => AccountMenu().ShowAt(_account);
            var statusRow = new DockPanel { Margin = new Thickness(2, 5, 0, 0) };
            DockPanel.SetDock(_account, Dock.Right);
            statusRow.Children.Add(_account);
            var meter = new StackPanel { Orientation = Orientation.Horizontal };
            meter.Children.Add(_meter);
            meter.Children.Add(_status);
            statusRow.Children.Add(meter);
            var footer = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };
            footer.Children.Add(composer);
            footer.Children.Add(statusRow);
            DockPanel.SetDock(footer, Dock.Bottom);

            // ---- transcript, or the sign-in view
            _empty = EmptyState();
            _log.Children.Add(_empty);
            _scroll = new ScrollViewer { Content = _log, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _signIn = SignInView();

            _chatRoot = new DockPanel();
            _chatRoot.Children.Add(footer);
            _chatRoot.Children.Add(_body);
            var root = new DockPanel();
            root.Children.Add(headerBorder);
            root.Children.Add(_main);
            Content = root;
            bool code = _settings.Backend == "code";
            _main.Content = code ? _codePane : _chatRoot;
            _tabChat.IsChecked = !code;
            _tabCode.IsChecked = code;

            SelectionService.Instance.SelectionChanged += (s, e) => Dispatcher.UIThread.Post(() => { _excludeSelection = false; UpdateAttachments(); });
            RefreshAccount();
            UpdateChips();
            UpdateAttachments();
            UpdateStatus();
        }

        /// <summary>The sidebar was shown: check the credentials again (the user may have signed in elsewhere) and put the
        /// cursor in the composer.</summary>
        public void OnShown()
        {
            if (IsCodeMode) { _ = _codePane.ShowAsync(); return; }
            RefreshAccount();
            UpdateAttachments();
            Dispatcher.UIThread.Post(() => { if (!ShowsSignIn) _input.Focus(); }, DispatcherPriority.Background);
        }

        /// <summary>Switch between the chat and Claude Code (remembered).</summary>
        public void SetBackend(bool code)
        {
            _settings.Backend = code ? "code" : "api";
            _settings.Save();
            _tabChat.IsChecked = !code;
            _tabCode.IsChecked = code;
            if (code)
            {
                if (_busy) Stop();
                _main.Content = _codePane;
                _ = _codePane.ShowAsync();
            }
            else
            {
                _main.Content = _chatRoot;
                OnShown();
            }
        }

        // ================================================================== sending

        /// <summary>Send a message (also used by the example prompts and the tests).</summary>
        public async Task SendAsync(string text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || _busy) return;
            if (ProjectData.Current == null) { ShowTranscript(); AddNote("Open a project first — Claude works on the open project.", true); return; }
            RefreshAccount();
            if (ClaudeAccount.Current == ClaudeCredential.None) { ShowSignIn(); return; }
            ApplyChoices();
            string selection = SelectionNote(out string selectionLabel);
            _input.Text = "";
            ShowTranscript();
            _empty.IsVisible = false;
            AddUser(text, selectionLabel);
            string message = selection == null ? text : text + "\n\n" + selection;
            await RunTurnAsync(ct => _session.SendAsync(message, ct));
        }

        /// <summary>Run the open turn again (the Retry button after an error or a stop).</summary>
        private async Task RetryAsync(Control button)
        {
            if (_busy || !_session.CanRetry) return;
            _log.Children.Remove(button);
            await RunTurnAsync(ct => _session.RetryAsync(ct));
        }

        private async Task RunTurnAsync(Func<CancellationToken, Task<string>> run)
        {
            SetBusy(true);
            _cts = new CancellationTokenSource();
            bool retry = false;
            try { await run(_cts.Token); }
            catch (OperationCanceledException) { AddNote("Stopped.", false); retry = true; }
            catch (ClaudeChatException ex)
            {
                AddNote(ex.Message, true);
                if (ex.Status == System.Net.HttpStatusCode.Unauthorized) AddSignInAgain();
                else if (ex.Status == System.Net.HttpStatusCode.PaymentRequired) AddNoCredits();
                else retry = true;
            }
            catch (Exception ex) { AddNote("Something went wrong: " + ex.Message, true); retry = true; }
            finally
            {
                EndThinking();
                EndText();
                SetBusy(false);
                UpdateStatus();
                ScrollToEnd();
            }
            if (retry && _session.CanRetry)
            {
                var b = new Button { Content = "Retry", HorizontalAlignment = HorizontalAlignment.Left };
                ToolTip.SetTip(b, "Send the last message again — or just write the next one");
                b.Click += (s, e) => _ = RetryAsync(b);
                _log.Children.Add(b);
                ScrollToEnd();
            }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
        }

        private void NewChat()
        {
            if (_busy) Stop();
            _session.Reset();
            _cards.Clear();
            _streaming = null;
            _thinking = null;
            _log.Children.Clear();
            _log.Children.Add(_empty);
            _empty.IsVisible = true;
            RefreshAccount();
            UpdateChips();
            UpdateStatus();
            _input.Focus();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _send.Content = new VxIcon { Icon = busy ? "Stop" : "ArrowUp", Width = 14, Height = 14 };
            _send.Classes.Set("accent", !busy);
            ToolTip.SetTip(_send, busy ? "Stop (Esc)" : "Send (Enter)");
        }

        /// <summary>The panel's choices (settings) on the session: model, effort, context, mode.</summary>
        private void ApplyChoices()
        {
            var model = ClaudeModels.Find(_settings.Model);
            _session.Model = model;
            _session.Effort = _settings.Effort.TryGetValue(model.Id, out var e) ? e : null;
            int ctx = _settings.Context > 0 ? _settings.Context : 1_000_000;
            _session.ContextSize = ClaudeModels.ContextSizesFor(model).Contains(ctx) ? ctx : ClaudeModels.ContextSizesFor(model).Max();
            _session.Mode = _settings.Mode == "Ask" ? ClaudeMode.Ask : ClaudeMode.Agent;
        }

        /// <summary>What is selected in the editor, as a note for Claude (null when nothing is, or the user removed it).</summary>
        private string SelectionNote(out string label)
        {
            label = null;
            var e = SelectionService.Instance.SelectedEntity;
            if (e == null || _excludeSelection || !_settings.ShareSelection) return null;
            try
            {
                label = e.Name;
                return "[Selected in the editor: entity \"" + e.Name + "\" (id " + SceneModel.ShortId(e) + ", path " + SceneModel.PathOf(e) + ")]";
            }
            catch { label = null; return null; }
        }

        // ================================================================== tools

        private async Task<ChatToolResult> RunToolAsync(ChatToolCall call, CancellationToken ct)
        {
            var args = new Dictionary<string, object>();
            using (var doc = JsonDocument.Parse(call.InputJson))
                foreach (var p in doc.RootElement.EnumerateObject()) args[p.Name] = p.Value.Clone();
            var r = await ToolHost.CallAsync(call.Name, args, "panel", ct);
            var res = new ChatToolResult { IsError = r.IsError, Text = string.Join("\n", r.Content.Where(c => c.Text != null).Select(c => c.Text)) };
            foreach (var c in r.Content.Where(c => c.Image != null)) res.Images.Add((c.Image, c.MimeType ?? "image/png"));
            return res;
        }

        private Task<ToolApproval> ApproveAsync(ChatToolCall call, CancellationToken ct)
        {
            if (ClaudePermissions.IsAlwaysAllowed(call.Name)) return Task.FromResult(ToolApproval.Allow);
            var tcs = new TaskCompletionSource<ToolApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled());
            Dispatcher.UIThread.Post(() =>
            {
                if (_cards.TryGetValue(call.Id, out var card)) card.AskApproval(a => tcs.TrySetResult(a));
                else tcs.TrySetResult(ToolApproval.Allow);
                ScrollToEnd();
            });
            return tcs.Task;
        }

        // ================================================================== transcript

        private void ShowTranscript() { if (!ReferenceEquals(_body.Content, _scroll)) _body.Content = _scroll; }

        private void ShowSignIn()
        {
            _cliInstall.IsVisible = false;
            _signInError.IsVisible = false;
            bool signedIn = ClaudeAccount.Current != ClaudeCredential.None;
            _stayOnSignIn = signedIn;
            _backToChat.IsVisible = signedIn;
            _body.Content = _signIn;
        }

        private void AddUser(string text, string selectionLabel)
        {
            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
            if (selectionLabel != null)
                stack.Children.Add(new TextBlock { Text = "◇ " + selectionLabel, FontSize = 11, Foreground = EditorKit.Brush("VxTextSecondaryBrush") });
            _log.Children.Add(new Border
            {
                Background = EditorKit.Brush("VxSelectionBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 7),
                HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 560, Margin = new Thickness(36, 4, 0, 0), Child = stack,
            });
            ScrollToEnd();
        }

        private void AppendText(string delta)
        {
            if (_streaming == null)
            {
                _streaming = new MarkdownBlock();
                _log.Children.Add(_streaming);
            }
            _streaming.Append(delta);
            ScrollToEnd();
        }

        private void EndText()
        {
            _streaming?.Flush();
            _streaming = null;
        }

        private void StartThinking()
        {
            EndText();
            EndThinking();
            _thinking = new ThinkingBlock();
            _log.Children.Add(_thinking);
            ScrollToEnd();
        }

        private void EndThinking()
        {
            _thinking?.Finish();
            _thinking = null;
        }

        private void AddCard(ChatToolCall call)
        {
            var card = new ToolCard(call, ToolCatalog.Find(call.Name));
            _cards[call.Id] = card;
            _log.Children.Add(card);
            ScrollToEnd();
        }

        private void AddNote(string text, bool error)
        {
            _empty.IsVisible = false;
            _log.Children.Add(new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Foreground = EditorKit.Brush(error ? "VxRedBrush" : "VxTextTertiaryBrush"),
            });
            ScrollToEnd();
        }

        private void AddDivider(string text)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = 8, Margin = new Thickness(0, 2) };
            g.Children.Add(new Border { Height = 1, Background = EditorKit.Brush("VxHairlineBrush"), VerticalAlignment = VerticalAlignment.Center });
            var t = new TextBlock { Text = text, FontSize = 11, Foreground = EditorKit.Brush("VxTextTertiaryBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 260, TextAlignment = TextAlignment.Center };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var r = new Border { Height = 1, Background = EditorKit.Brush("VxHairlineBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(r, 2);
            g.Children.Add(r);
            _log.Children.Add(g);
            ScrollToEnd();
        }

        /// <summary>The Console account has no API credits: a Claude plan works through Claude Code; or add credits.</summary>
        private void AddNoCredits()
        {
            var row = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
            var code = new Button { Classes = { "accent" }, Content = "Use Claude Code (Claude plan)" };
            ToolTip.SetTip(code, "Claude Code in this sidebar works with a Claude Pro or Max plan");
            code.Click += (s, e) => SetBackend(true);
            var credits = new Button { Content = "Add API credits" };
            credits.Click += (s, e) => EditorCommands.OpenUrl("https://console.anthropic.com/settings/billing");
            row.Children.Add(code);
            row.Children.Add(credits);
            _log.Children.Add(row);
            ScrollToEnd();
        }

        private void AddSignInAgain()
        {
            var b = new Button { Content = "Sign in again", HorizontalAlignment = HorizontalAlignment.Left };
            b.Click += (s, e) => ShowSignIn();
            _log.Children.Add(b);
        }

        private Control EmptyState()
        {
            var sp = new StackPanel { Spacing = 8, Margin = new Thickness(2, 8, 2, 0) };
            sp.Children.Add(new TextBlock { Text = "What should we build?", FontSize = 15, FontWeight = FontWeight.SemiBold });
            sp.Children.Add(new TextBlock
            {
                Text = "Agent builds and changes the open scene — entities, lights, materials, scripts, audio — and you approve each change. " +
                       "Ask answers and looks around without changing anything.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 17, Foreground = EditorKit.Brush("VxTextSecondaryBrush"),
            });
            foreach (var example in new[]
            {
                "What's in this scene? Show me a screenshot.",
                "Build a 12 m dark corridor with four flickering ceiling lights.",
                "Scatter 30 pieces of debris on the floor around the player.",
                "Write a script that makes the selected lamp flicker and attach it.",
            })
            {
                var b = new Button { Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                b.Content = new TextBlock { Text = example, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                string text = example;
                b.Click += (s, e) => { _input.Text = text; _input.Focus(); _input.CaretIndex = text.Length; };
                sp.Children.Add(b);
            }
            return sp;
        }

        private void ScrollToEnd() => Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);

        // ================================================================== composer: attachments, pickers, status

        private void UpdateAttachments()
        {
            _attachments.Children.Clear();
            var e = SelectionService.Instance.SelectedEntity;
            bool show = e != null && !_excludeSelection && _settings.ShareSelection;
            _attachments.IsVisible = show;
            if (!show) return;
            var chip = new Border
            {
                Background = EditorKit.Brush("VxHoverBrush"), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 2, 1),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new VxIcon { Icon = "Cube", Width = 11, Height = 11, Foreground = EditorKit.Brush("VxTextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = e.Name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis });
            var remove = new Button { Classes = { "icon", "small" }, Width = 16, Height = 16, MinHeight = 16, Content = new VxIcon { Icon = "Close", Width = 9, Height = 9 } };
            ToolTip.SetTip(remove, "Don't send the selection with this message");
            remove.Click += (s, ev) => { _excludeSelection = true; UpdateAttachments(); };
            row.Children.Add(remove);
            chip.Child = row;
            ToolTip.SetTip(chip, "Claude sees what is selected in the editor (\"make this brighter\")");
            _attachments.Children.Add(chip);
        }

        private void UpdateChips()
        {
            ApplyChoices();
            var m = _session.Model;
            SetChip(_modeChip, _session.Mode == ClaudeMode.Ask ? "Ask" : "Agent",
                    _session.Mode == ClaudeMode.Ask ? "Ask — Claude answers and looks, and changes nothing" : "Agent — Claude builds and changes the project; you approve each change");
            SetChip(_modelChip, m.Label, "Model — " + m.Blurb);
            string effort = _session.EffectiveEffort;
            _effortChip.IsEnabled = effort != null;
            SetChip(_effortChip, effort == null ? "No effort" : EffortLabel(effort),
                    effort == null ? m.Label + " has no effort setting" : "Effort — how much Claude thinks before it answers");
            SetChip(_contextChip, ClaudeModels.ContextLabel(_session.ContextSize),
                    "Context — the conversation is summarized on the server once it reaches about " + ClaudeModels.ContextLabel(_session.CompactAt) + " tokens");
            _input.Watermark = _session.Mode == ClaudeMode.Ask ? "Ask about your project…" : "Ask Claude to build or change something…";
        }

        private void UpdateStatus()
        {
            long ctx = _session.ContextTokens;
            _meter.Value = Math.Min(1, ctx / (double)Math.Max(1, _session.ContextSize));
            _meter.IsVisible = ctx > 0;
            if (ctx == 0 && _session.OutputTokens == 0)
                _status.Text = ClaudeModels.ContextLabel(_session.ContextSize) + " context";
            else
                _status.Text = K(ctx) + " / " + ClaudeModels.ContextLabel(_session.ContextSize) + " · ≈ $" + _session.Cost.ToString(_session.Cost < 1 ? "0.000" : "0.00", CultureInfo.InvariantCulture);
            ToolTip.SetTip(_status, "Context in use / context size · estimated cost of this chat at API prices (" +
                K(_session.InputTokens + _session.CacheReadTokens + _session.CacheWriteTokens) + " in, " + K(_session.CacheReadTokens) + " of it cached, " + K(_session.OutputTokens) + " out)");
        }

        private static string K(long n) => n >= 1000 ? (n / 1000.0).ToString(n >= 10000 ? "0" : "0.0", CultureInfo.InvariantCulture) + "K" : n.ToString(CultureInfo.InvariantCulture);

        private static string EffortLabel(string e) => e switch
        {
            "low" => "Low", "medium" => "Medium", "high" => "High", "xhigh" => "Extra high", "max" => "Max", _ => e,
        };

        private static Button Chip() => new Button { Classes = { "chip" } };

        private static void SetChip(Button chip, string text, string tip)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new VxIcon { Icon = "ChevronDown", Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            chip.Content = row;
            ToolTip.SetTip(chip, tip);
        }

        private static MenuItem Choice(string title, string detail, bool isChecked, Action pick, bool enabled = true)
        {
            var header = new StackPanel { Spacing = 1, Margin = new Thickness(0, 1) };
            header.Children.Add(new TextBlock { Text = title });
            if (!string.IsNullOrEmpty(detail))
                header.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = EditorKit.Brush("VxTextTertiaryBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = isChecked, IsEnabled = enabled };
            item.Click += (s, e) => pick();
            return item;
        }

        private MenuFlyout Flyout(params MenuItem[] items)
        {
            var f = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
            foreach (var i in items) f.Items.Add(i);
            return f;
        }

        private MenuFlyout ModeMenu() => Flyout(
            Choice("Agent", "Builds and changes the project — asks before each change", _session.Mode == ClaudeMode.Agent, () => SetMode("Agent")),
            Choice("Ask", "Answers, explains and looks around — changes nothing", _session.Mode == ClaudeMode.Ask, () => SetMode("Ask")));

        internal void SetMode(string mode)
        {
            _settings.Mode = mode;
            _settings.Save();
            UpdateChips();
        }

        private MenuFlyout ModelMenu()
        {
            bool started = _session.MessageCount > 0;
            return Flyout(ClaudeModels.All.Select(m =>
            {
                string price = "$" + m.InputPrice.ToString("0.##", CultureInfo.InvariantCulture) + " / $" + m.OutputPrice.ToString("0.##", CultureInfo.InvariantCulture) + " per million tokens";
                bool newChat = started && m.Thinking != _session.Model.Thinking;
                string detail = m.Blurb + " · " + price + (newChat ? " · starts a new chat" : "");
                return Choice(m.Label, detail, m.Id == _session.Model.Id, () => SetModel(m, newChat));
            }).ToArray());
        }

        private void SetModel(ClaudeModelInfo m, bool newChat)
        {
            _settings.Model = m.Id;
            _settings.Save();
            // a model without thinking cannot continue a conversation whose turns carry thinking (and vice versa)
            if (newChat) NewChat();
            UpdateChips();
            UpdateStatus();
        }

        private MenuFlyout EffortMenu()
        {
            var m = _session.Model;
            var details = new Dictionary<string, string>
            {
                ["low"] = "Fastest and cheapest — quick questions and small edits",
                ["medium"] = "Balanced speed and depth",
                ["high"] = "Thinks harder — complex builds and scripts",
                ["xhigh"] = "Extra high — hard problems and long agent runs",
                ["max"] = "Maximum — slowest and most thorough",
            };
            string current = _session.EffectiveEffort;
            return Flyout(m.Efforts.Select(e => Choice(EffortLabel(e) + (e == m.DefaultEffort ? "  (default)" : ""), details.TryGetValue(e, out var d) ? d : null, e == current, () =>
            {
                _settings.Effort[m.Id] = e;
                _settings.Save();
                UpdateChips();
            })).ToArray());
        }

        private MenuFlyout ContextMenuFor()
        {
            var m = _session.Model;
            return Flyout(ClaudeModels.ContextSizesFor(m).Select(c => Choice(ClaudeModels.ContextLabel(c) + " context",
                (c >= 1_000_000 ? "Keeps more of a long session" : "Cheaper long sessions") + " — summarizes earlier turns at about " + ClaudeModels.ContextLabel((int)(c * 0.8)) +
                (m.Compaction ? "" : " (" + m.Label + ": starts to forget instead)"),
                c == _session.ContextSize, () =>
                {
                    _settings.Context = c;
                    _settings.Save();
                    UpdateChips();
                    UpdateStatus();
                })).ToArray());
        }

        // ================================================================== account

        /// <summary>Show the sign-in view or the transcript, and the account in the status row.</summary>
        public void RefreshAccount()
        {
            var c = ClaudeAccount.Current;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Fill = EditorKit.Brush(c == ClaudeCredential.None ? "VxTextTertiaryBrush" : "VxGreenBrush") });
            row.Children.Add(new TextBlock
            {
                Text = c switch
                {
                    ClaudeCredential.AnthropicAccount => "Anthropic account",
                    ClaudeCredential.ApiKey => "API key",
                    ClaudeCredential.Environment => "Environment key",
                    _ => "Sign in",
                },
                VerticalAlignment = VerticalAlignment.Center,
            });
            _account.Content = row;
            ToolTip.SetTip(_account, ClaudeAccount.Describe(c) + (ClaudeAccount.EnvironmentShadowsAccount ? " — ANTHROPIC_API_KEY in the environment is used instead of your signed-in account" : ""));
            if (c == ClaudeCredential.None) { if (!ReferenceEquals(_body.Content, _signIn)) ShowSignIn(); }
            else if (_signInCts == null && !_stayOnSignIn) ShowTranscript();
        }

        private MenuFlyout AccountMenu()
        {
            var c = ClaudeAccount.Current;
            var f = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
            f.Items.Add(new MenuItem { Header = ClaudeAccount.Describe(c), IsEnabled = false });
            if (c == ClaudeCredential.AnthropicAccount)
            {
                var signOut = new MenuItem { Header = "Sign out" };
                signOut.Click += async (s, e) => await SignOutAsync();
                f.Items.Add(signOut);
            }
            if (c == ClaudeCredential.ApiKey)
            {
                var remove = new MenuItem { Header = "Remove the API key" };
                remove.Click += (s, e) => { StoreKeys.Set(ClaudeAccount.KeyName, null); RefreshAccount(); EditorCommands.Toast("API key removed"); };
                f.Items.Add(remove);
            }
            if (c != ClaudeCredential.AnthropicAccount)
            {
                var signIn = new MenuItem { Header = "Sign in with Anthropic…" };
                signIn.Click += (s, e) => { ShowSignIn(); _ = SignInAsync(); };
                f.Items.Add(signIn);
            }
            var key = new MenuItem { Header = c == ClaudeCredential.ApiKey ? "Change the API key…" : "Use an API key…" };
            key.Click += (s, e) => { ShowSignIn(); _keyBox.IsVisible = true; _keyField.Focus(); };
            f.Items.Add(key);
            f.Items.Add(new Separator());
            var code = new MenuItem { Header = "Open in Claude Code (Claude Pro / Max)" };
            code.Click += (s, e) => _ = OpenInClaudeCodeAsync();
            f.Items.Add(code);
            return f;
        }

        private Control SignInView()
        {
            var sp = new StackPanel { Spacing = 10, Margin = new Thickness(18, 22, 18, 12), MaxWidth = 420 };
            _backToChat.Click += (s, e) => { _stayOnSignIn = false; _keyBox.IsVisible = false; RefreshAccount(); };
            sp.Children.Add(_backToChat);
            sp.Children.Add(new VxIcon { Icon = "Sparkle", Width = 26, Height = 26, Foreground = EditorKit.Brush("VxAccentBrush"), HorizontalAlignment = HorizontalAlignment.Left });
            sp.Children.Add(new TextBlock { Text = "Sign in to use Claude", FontSize = 16, FontWeight = FontWeight.SemiBold });
            sp.Children.Add(Para("Claude works on your open project: it builds scenes, lights, materials, scripts and audio, and looks at the viewport. " +
                                 "Ask mode answers questions; Agent mode makes changes you approve."));

            // ---- Anthropic account (the Anthropic CLI's browser sign-in)
            _signInButton.Click += (s, e) => _ = SignInAsync();
            sp.Children.Add(_signInButton);
            sp.Children.Add(Para("Opens your browser to sign in to your Anthropic Console account. Usage is billed to that account at API prices.", true));
            var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Left };
            cancel.Click += (s, e) => { try { _signInCts?.Cancel(); } catch { } };
            _signInLink.Click += (s, e) => { if (_signInUrl != null) EditorCommands.OpenUrl(_signInUrl); };
            var waitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            waitRow.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 60, MinWidth = 0, Height = 3, MinHeight = 3, VerticalAlignment = VerticalAlignment.Center });
            waitRow.Children.Add(_signInWaitText);
            _signInWait.Children.Add(waitRow);
            _signInWait.Children.Add(_signInLink);
            var waitButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            waitButtons.Children.Add(cancel);
            var viaTerminal = new Button { Classes = { "link" }, Content = "Use a terminal instead", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            viaTerminal.Click += (s, e) => _terminalSignIn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            waitButtons.Children.Add(viaTerminal);
            _signInWait.Children.Add(waitButtons);
            sp.Children.Add(_signInWait);
            BuildCliInstall();
            sp.Children.Add(_cliInstall);
            _signInError.Foreground = EditorKit.Brush("VxRedBrush");
            sp.Children.Add(_signInError);
            // the background sign-in did not finish (the CLI may want a terminal): the same sign-in in a terminal window;
            // coming back to the editor picks the new sign-in up
            _terminalSignIn.Click += (s, e) =>
            {
                try { _signInCts?.Cancel(); } catch { }
                if (ClaudeAccount.SignInInTerminal())
                {
                    _signInError.Text = "Finish the sign-in in the terminal and your browser, then come back here.";
                    _signInError.Foreground = EditorKit.Brush("VxTextSecondaryBrush");
                    _signInError.IsVisible = true;
                }
                else EditorCommands.Toast("No terminal could be opened — run: ant auth login");
            };
            sp.Children.Add(_terminalSignIn);

            // ---- API key
            var useKey = new Button { Content = "Use an API key", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 30, Margin = new Thickness(0, 6, 0, 0) };
            useKey.Click += (s, e) => { _keyBox.IsVisible = !_keyBox.IsVisible; if (_keyBox.IsVisible) _keyField.Focus(); };
            sp.Children.Add(useKey);
            var save = new Button { Classes = { "accent" }, Content = "Save" };
            save.Click += (s, e) => SaveKey();
            _keyField.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; SaveKey(); } };
            var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            keyRow.Children.Add(_keyField);
            Grid.SetColumn(save, 1);
            keyRow.Children.Add(save);
            _keyBox.Children.Add(keyRow);
            var keyHelp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            keyHelp.Children.Add(new TextBlock { Text = "Stored only on this computer.", FontSize = 11, Foreground = EditorKit.Brush("VxTextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            keyHelp.Children.Add(LinkButton("Get a key", () => EditorCommands.OpenUrl("https://console.anthropic.com/settings/keys")));
            _keyBox.Children.Add(keyHelp);
            sp.Children.Add(_keyBox);

            // ---- Claude subscription → Claude Code
            sp.Children.Add(new Border { Height = 1, Background = EditorKit.Brush("VxHairlineBrush"), Margin = new Thickness(0, 10, 0, 2) });
            sp.Children.Add(new TextBlock { Text = "Have Claude Pro or Max?", FontWeight = FontWeight.SemiBold, FontSize = 12.5 });
            sp.Children.Add(Para("Use your plan with Claude Code, right here in the sidebar: Anthropic's Claude Code runs on this project with your own sign-in (/login) and works with the editor's tools.", true));
            var code = new Button { Classes = { "accent" }, Content = "Use Claude Code here", HorizontalAlignment = HorizontalAlignment.Left };
            code.Click += (s, e) => SetBackend(true);
            sp.Children.Add(code);
            sp.Children.Add(LinkButton("Open Claude Code in a terminal window instead", () => _ = OpenInClaudeCodeAsync()));
            return new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        private void BuildCliInstall()
        {
            _cliInstall.Children.Add(Para("Signing in uses the official Anthropic CLI (ant). Install it once, then sign in again:"));
            if (OperatingSystem.IsMacOS())
            {
                var cmd = new TextBox { Text = ClaudeAccount.CliInstallMac, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
                if (Application.Current != null && Application.Current.TryFindResource("VxMono", out var mono) && mono is FontFamily ff) cmd.FontFamily = ff;
                var copy = new Button { Content = "Copy" };
                copy.Click += async (s, e) =>
                {
                    try { var clip = TopLevel.GetTopLevel(this)?.Clipboard; if (clip != null) await clip.SetTextAsync(ClaudeAccount.CliInstallMac); copy.Content = "Copied"; } catch { }
                };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
                row.Children.Add(cmd);
                Grid.SetColumn(copy, 1);
                row.Children.Add(copy);
                _cliInstall.Children.Add(row);
                _cliInstall.Children.Add(Para("Run it in Terminal (needs Homebrew).", true));
            }
            else _cliInstall.Children.Add(LinkButton("Download the Anthropic CLI (GitHub releases)", () => EditorCommands.OpenUrl(ClaudeAccount.CliReleases)));
            var again = new Button { Content = "Sign in again", HorizontalAlignment = HorizontalAlignment.Left };
            again.Click += (s, e) => _ = SignInAsync();
            _cliInstall.Children.Add(again);
        }

        /// <summary>The Anthropic CLI's browser sign-in (<c>ant auth login</c>); the SDK then reads the profile it stores.</summary>
        internal async Task SignInAsync()
        {
            if (_signInCts != null) return;
            _signInError.IsVisible = false;
            _signInError.Foreground = EditorKit.Brush("VxRedBrush");
            _terminalSignIn.IsVisible = false;
            if (ClaudeAccount.FindCli() == null) { _cliInstall.IsVisible = true; return; }
            _cliInstall.IsVisible = false;
            _signInCts = new CancellationTokenSource();
            _signInUrl = null;
            _signInLink.IsVisible = false;
            _signInWaitText.Text = "Finish signing in in your browser…";
            _signInWait.IsVisible = true;
            _signInButton.IsEnabled = false;
            try
            {
                var (ok, output) = await ClaudeAccount.SignInAsync(_signInCts.Token, line =>
                {
                    string url = ClaudeAccount.LinkIn(line);
                    if (url != null) Dispatcher.UIThread.Post(() => { _signInUrl ??= url; _signInLink.IsVisible = true; });
                });
                if (ok && ClaudeAccount.HasAccountProfile)
                {
                    _stayOnSignIn = false;
                    EditorCommands.Toast("Signed in to Anthropic");
                    if (ClaudeAccount.EnvironmentShadowsAccount)
                        AddNote("Signed in — but ANTHROPIC_API_KEY is set in the environment, and that key is used instead of your account.", false);
                }
                else if (!_signInCts.IsCancellationRequested)
                {
                    _signInError.Text = "Sign-in did not finish." + (string.IsNullOrWhiteSpace(output) ? "" : "\n" + Tail(output, 6));
                    _signInError.IsVisible = true;
                    _terminalSignIn.IsVisible = true;
                }
            }
            finally
            {
                _signInCts.Dispose();
                _signInCts = null;
                _signInWait.IsVisible = false;
                _signInButton.IsEnabled = true;
                RefreshAccount();
                UpdateStatus();
            }
        }

        private async Task SignOutAsync()
        {
            var (ok, output) = await ClaudeAccount.SignOutAsync(CancellationToken.None);
            RefreshAccount();
            EditorCommands.Toast(ok ? "Signed out of Anthropic" : "Sign-out failed: " + Tail(output, 1));
        }

        private void SaveKey()
        {
            string key = _keyField.Text?.Trim();
            if (string.IsNullOrEmpty(key)) return;
            StoreKeys.Set(ClaudeAccount.KeyName, key);
            _keyField.Text = "";
            _keyBox.IsVisible = false;
            _stayOnSignIn = false;
            RefreshAccount();
            EditorCommands.Toast(key.StartsWith("sk-ant-", StringComparison.Ordinal) ? "API key saved" : "API key saved — Anthropic keys usually start with sk-ant-");
            _input.Focus();
        }

        /// <summary>Claude Code with the user's own Claude plan, in a terminal on the project — wired to this editor's MCP
        /// server through the project's .mcp.json.</summary>
        internal async Task OpenInClaudeCodeAsync()
        {
            var project = ProjectData.Current;
            if (project == null) { EditorCommands.Toast("Open a project first"); return; }
            string claude = ClaudeCode.FindCli();
            if (claude == null)
            {
                ShowTranscript();
                AddNote("Claude Code is not installed. Install it, then try again:", false);
                var link = LinkButton("Set up Claude Code", () => EditorCommands.OpenUrl(ClaudeCode.InstallUrl));
                link.HorizontalAlignment = HorizontalAlignment.Left;
                _log.Children.Add(link);
                return;
            }
            if (!McpHost.IsRunning && !await McpHost.SetEnabledAsync(true))
            {
                EditorCommands.Toast("The MCP server did not start: " + McpHost.LastError);
                return;
            }
            try { McpProjectConfig.Write(project.Path, "vortex", McpHost.Url); }
            catch (Exception ex) { EditorCommands.Toast(ex.Message); return; }
            if (ClaudeCode.OpenInTerminal(project.Path, claude))
                EditorCommands.Toast("Claude Code is starting in a terminal — approve the \"vortex\" MCP server when it asks");
            else
            {
                ShowTranscript();
                AddNote("No terminal could be opened. Run Claude Code in the project folder yourself: cd \"" + project.Path + "\" && claude", false);
            }
        }

        private static string Tail(string text, int lines)
        {
            var all = (text ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }

        private static TextBlock Para(string text, bool small = false) => new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = small ? 11.5 : 12.5, LineHeight = small ? 16 : 18,
            Foreground = EditorKit.Brush(small ? "VxTextTertiaryBrush" : "VxTextSecondaryBrush"),
        };

        // ================================================================== header menu

        private Control MenuButton()
        {
            var b = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "More" } };
            ToolTip.SetTip(b, "More");
            b.Click += (s, e) =>
            {
                var f = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
                var code = new MenuItem { Header = IsCodeMode ? "Restart Claude Code" : "Use Claude Code here (Claude Pro / Max)" };
                code.Click += (s2, e2) => { if (IsCodeMode) _codePane.Restart(); else SetBackend(true); };
                var external = new MenuItem { Header = "Open Claude Code in a terminal window" };
                external.Click += (s2, e2) => _ = OpenInClaudeCodeAsync();
                var connect = new MenuItem { Header = "Connect Claude Code / Desktop (MCP)…" };
                connect.Click += (s2, e2) => _ = ClaudeConnectDialog.Run();
                var ops = new MenuItem { Header = "Operations… (history, revert, diffs)" };
                ops.Click += (s2, e2) => ClaudeOperationsWindow.Open();
                var reset = new MenuItem { Header = "Reset “Always allow” for this project" };
                reset.Click += (s2, e2) => { ClaudePermissions.Reset(); EditorCommands.Toast("Claude will ask again before changes"); };
                var share = new MenuItem { Header = "Send the selection with each message", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.ShareSelection };
                share.Click += (s2, e2) => { _settings.ShareSelection = !_settings.ShareSelection; _settings.Save(); UpdateAttachments(); };
                var docs = new MenuItem { Header = "Documentation" };
                docs.Click += (s2, e2) => EditorCommands.OpenUrl("https://engine.vortexstudio.dev/docs/#/claude");
                f.Items.Add(code);
                f.Items.Add(external);
                f.Items.Add(connect);
                f.Items.Add(new Separator());
                f.Items.Add(ops);
                f.Items.Add(reset);
                f.Items.Add(share);
                f.Items.Add(new Separator());
                f.Items.Add(docs);
                f.ShowAt(b);
            };
            return b;
        }

        private static Button IconButton(string icon, string tip, Action click)
        {
            var b = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = icon } };
            ToolTip.SetTip(b, tip);
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { EditorCommands.Fail(tip, ex); } };
            return b;
        }

        private static Button LinkButton(string text, Action click)
        {
            var b = new Button { Classes = { "link" }, Content = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            b.Click += (s, e) => click();
            return b;
        }

        // ================================================================== thinking

        /// <summary>Claude's thinking (summarized by the API): "Thinking…" with the latest line while it streams, "Thought
        /// for 8 s" after; click to read it all.</summary>
        internal sealed class ThinkingBlock : Border
        {
            private readonly StringBuilder _text = new StringBuilder();
            private readonly TextBlock _title = new TextBlock { Text = "Thinking…", FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            private readonly TextBlock _preview = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            private readonly VxIcon _chevron = new VxIcon { Icon = "ChevronRight", Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
            private readonly SelectableTextBlock _body = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 17, IsVisible = false, Margin = new Thickness(16, 4, 0, 2) };
            private readonly DateTime _start = DateTime.UtcNow;
            private bool _done;

            public ThinkingBlock()
            {
                _title.Foreground = EditorKit.Brush("VxTextSecondaryBrush");
                _preview.Foreground = EditorKit.Brush("VxTextTertiaryBrush");
                _chevron.Foreground = EditorKit.Brush("VxTextTertiaryBrush");
                _body.Foreground = EditorKit.Brush("VxTextSecondaryBrush");
                var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
                head.Children.Add(_chevron);
                Grid.SetColumn(_title, 1);
                head.Children.Add(_title);
                Grid.SetColumn(_preview, 2);
                head.Children.Add(_preview);
                head.PointerPressed += (s, e) => { Toggle(); e.Handled = true; };
                ToolTip.SetTip(head, "Claude's thinking (summarized) — click to read");
                var stack = new StackPanel();
                stack.Children.Add(head);
                stack.Children.Add(_body);
                Child = stack;
            }

            public string Text => _text.ToString();

            public void Append(string delta)
            {
                if (string.IsNullOrEmpty(delta)) return;
                _text.Append(delta);
                if (_body.IsVisible) _body.Text = _text.ToString();
                if (!_done) _preview.Text = LastLine(_text.ToString());
            }

            public void Finish()
            {
                if (_done) return;
                _done = true;
                double s = (DateTime.UtcNow - _start).TotalSeconds;
                _title.Text = s < 1 ? "Thought briefly" : "Thought for " + Math.Round(s).ToString(CultureInfo.InvariantCulture) + " s";
                _preview.Text = "";
                if (_text.Length == 0) IsVisible = false;   // no summary came (short or omitted thinking)
            }

            private void Toggle()
            {
                _body.IsVisible = !_body.IsVisible;
                _body.Text = _text.ToString();
                _chevron.Icon = _body.IsVisible ? "ChevronDown" : "ChevronRight";
            }

            private static string LastLine(string s)
            {
                var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string l = lines[i].Trim().Trim('*').Trim();
                    if (l.Length > 0) return l;
                }
                return "";
            }
        }

        // ================================================================== tool card

        /// <summary>One tool call: status, title, a one-line summary; click for input / result / image; approval buttons
        /// while Claude waits for the user.</summary>
        internal sealed class ToolCard : Border
        {
            private readonly TextBlock _status = new TextBlock { Width = 16, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Text = "…" };
            private readonly TextBlock _summary = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            private readonly StackPanel _details = new StackPanel { Spacing = 6, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };
            private readonly StackPanel _body = new StackPanel { Spacing = 0 };
            private Control _approval;
            private readonly ChatToolCall _call;

            public ToolCard(ChatToolCall call, ToolDef def)
            {
                _call = call;
                CornerRadius = new CornerRadius(6);
                BorderThickness = new Thickness(1);
                BorderBrush = EditorKit.Brush("VxHairlineBrush");
                Background = EditorKit.Brush("VxToolbarBrush");
                Padding = new Thickness(8, 5);
                var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6, Background = Brushes.Transparent };
                head.Children.Add(_status);
                var name = new TextBlock { Text = def?.Title ?? call.Name, FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1);
                head.Children.Add(name);
                _summary.Text = Summarize(call.InputJson);
                _summary.Foreground = EditorKit.Brush("VxTextTertiaryBrush");
                Grid.SetColumn(_summary, 2);
                head.Children.Add(_summary);
                head.Cursor = new Cursor(StandardCursorType.Hand);
                head.PointerPressed += (s, e) => { _details.IsVisible = !_details.IsVisible; e.Handled = true; };
                ToolTip.SetTip(head, call.Name + " — click for details");
                _details.Children.Add(Code("Input", Pretty(call.InputJson)));
                _body.Children.Add(head);
                _body.Children.Add(_details);
                Child = _body;
            }

            public void AskApproval(Action<ToolApproval> decide)
            {
                var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                row.Children.Add(new TextBlock { Text = "Claude wants to " + Verb(_call.Name) + ".", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4), TextWrapping = TextWrapping.Wrap });
                void Done(ToolApproval a)
                {
                    _body.Children.Remove(_approval);
                    _approval = null;
                    _details.IsVisible = false;
                    BorderBrush = EditorKit.Brush("VxHairlineBrush");
                    if (a == ToolApproval.Deny) _status.Text = "⊘";
                    decide(a);
                }
                var allow = new Button { Classes = { "accent" }, Content = "Allow", Margin = new Thickness(0, 0, 6, 4) };
                allow.Click += (s, e) => Done(ToolApproval.Allow);
                var always = new Button { Content = "Always allow", Margin = new Thickness(0, 0, 6, 4) };
                ToolTip.SetTip(always, "Don't ask again for " + _call.Name + " in this project");
                always.Click += (s, e) => { ClaudePermissions.AllowAlways(_call.Name); Done(ToolApproval.Allow); };
                var deny = new Button { Content = "Deny", Margin = new Thickness(0, 0, 6, 4) };
                deny.Click += (s, e) => Done(ToolApproval.Deny);
                row.Children.Add(allow);
                row.Children.Add(always);
                row.Children.Add(deny);
                _approval = row;
                _body.Children.Insert(1, row);
                _details.IsVisible = true;
                BorderBrush = EditorKit.Brush("VxAccentBrush");
            }

            public void Finish(ChatToolCall call, ClaudeMode mode)
            {
                BorderBrush = EditorKit.Brush("VxHairlineBrush");
                if (_approval != null) { _body.Children.Remove(_approval); _approval = null; }
                var r = call.Result;
                _status.Text = call.Denied ? "⊘" : r?.IsError == true ? "✗" : "✓";
                _status.Foreground = EditorKit.Brush(call.Denied ? "VxTextTertiaryBrush" : r?.IsError == true ? "VxRedBrush" : "VxGreenBrush");
                if (call.Denied && mode == ClaudeMode.Ask)
                {
                    _summary.Text = "Ask mode — not run (switch to Agent to let Claude change the project)";
                    ToolTip.SetTip(_status, "Not run: Ask mode changes nothing");
                }
                if (r == null) return;
                if (!string.IsNullOrEmpty(r.Text))
                {
                    _details.Children.Add(Code(r.IsError ? "Error" : "Result", Pretty(r.Text, 2400)));
                    if (r.IsError && !call.Denied) _summary.Text = OneLine(r.Text);
                }
                foreach (var (data, mime) in r.Images)
                {
                    try
                    {
                        using var ms = new MemoryStream(data);
                        var img = new Image { Source = new Bitmap(ms), MaxWidth = 520, MaxHeight = 280, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                        _details.Children.Add(img);
                        _details.IsVisible = true;   // pictures are worth showing right away
                    }
                    catch { }
                }
            }

            private static Control Code(string label, string text)
            {
                var sp = new StackPanel { Spacing = 2 };
                sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = EditorKit.Brush("VxTextTertiaryBrush") });
                var tb = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                if (Application.Current != null && Application.Current.TryFindResource("VxMono", out var mono) && mono is FontFamily ff) tb.FontFamily = ff;
                sp.Children.Add(tb);
                return sp;
            }

            private static readonly System.Text.RegularExpressions.Regex ShortArray =
                new System.Text.RegularExpressions.Regex(@"\[\s*((?:-?[\d.eE+]+|""[^""\n]{0,40}"")(?:\s*,\s*(?:-?[\d.eE+]+|""[^""\n]{0,40}"")){0,5})\s*\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            private static string Verb(string tool) => tool.Replace('_', ' ');

            private static string Summarize(string json)
            {
                try
                {
                    var o = JsonNode.Parse(json) as JsonObject;
                    if (o == null || o.Count == 0) return "";
                    return OneLine(string.Join(", ", o.Take(3).Select(kv => kv.Key + ": " + (kv.Value is JsonValue v ? v.ToString() : kv.Value?.ToJsonString()))));
                }
                catch { return ""; }
            }

            private static string OneLine(string s)
            {
                s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ');
                return s.Length > 120 ? s.Substring(0, 119) + "…" : s;
            }

            private static string Pretty(string json, int max = 4000)
            {
                string text = json ?? "";
                try
                {
                    var node = JsonNode.Parse(text);
                    if (node != null) text = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    // [x, y, z] and short string lists on one line
                    text = ShortArray.Replace(text, m => "[" + string.Join(", ", m.Groups[1].Value.Split(',').Select(x => x.Trim())) + "]");
                }
                catch { }
                return text.Length > max ? text.Substring(0, max) + "\n… (" + (text.Length - max) + " more characters)" : text;
            }
        }
    }
}
