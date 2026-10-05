using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Editor.Core.Assets.Store;
using Editor.Core.Claude;
using Editor.Core.Data;
using VortexEditor.Controls;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Shell;
using VortexEditor.Shell.Material;

namespace VortexEditor.Claude
{
    /// <summary>
    /// The embedded Claude panel (#92): a chat in the editor's right column that works with the same tools as the MCP
    /// server (<see cref="ToolCatalog"/>, run through <see cref="ToolHost"/> — same undo steps, same safety). Streams the
    /// answer, shows every tool call as a card (input, result, image), asks before tools that change something
    /// (Allow / Always allow for this project / Deny), and uses the user's own Anthropic key. Independent of the MCP
    /// server: it works with the server off.
    /// </summary>
    public sealed class ClaudePanel : UserControl
    {
        public static ClaudePanel Current { get; private set; }

        private readonly ClaudeChat _chat = new ClaudeChat();
        private readonly StackPanel _log = new StackPanel { Spacing = 8, Margin = new Thickness(10, 10, 10, 6) };
        private readonly ScrollViewer _scroll;
        private readonly TextBox _input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 160,
            Watermark = "Ask Claude… (Enter sends)",
        };
        private readonly Button _send;
        private readonly ComboBox _model = new ComboBox { MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _usage = new TextBlock { Classes = { "small", "tertiary" }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly Control _empty;
        private CancellationTokenSource _cts;
        private SelectableTextBlock _streaming;
        private readonly Dictionary<string, ToolCard> _cards = new Dictionary<string, ToolCard>();
        private bool _busy;

        public bool IsBusy => _busy;
        /// <summary>The visible transcript (tests).</summary>
        internal StackPanel Log => _log;

        internal const string SystemPrompt =
            "You are Claude, working inside the Vortex Engine editor on the user's open project. You operate the editor with tools: " +
            "read the scene, create and change entities, lights, materials, scripts and audio, run the game and look at the viewport.\n" +
            "- Answer in the user's language and keep replies short — the user sees every tool call as a card.\n" +
            "- Look before you build (scene_outline, find_entities, get_entity, get_bounds). After building something visible, " +
            "capture_viewport to check it, and fix what looks wrong.\n" +
            "- Prefer one macro (place_grid, scatter, bulk_set_properties) over many single calls.\n" +
            "- The user approves tools that change the project; if they deny one, ask what they want instead of retrying.\n" +
            "- Write game code against the real Vortex API (get_scripting_api), not Unity's.\n\n" + McpHost.Instructions;

        public ClaudePanel()
        {
            Current = this;
            _chat.SystemPrompt = SystemPrompt;
            _chat.Tools = ToolCatalog.All.Select(t => new ChatTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchema.GetRawText(), ReadOnly = t.ReadOnly }).ToList();
            _chat.RunTool = RunToolAsync;
            _chat.Approve = ApproveAsync;
            _chat.TextStarted += () => Dispatcher.UIThread.Post(() => _streaming = null);
            _chat.TextDelta += d => Dispatcher.UIThread.Post(() => AppendText(d));
            _chat.ToolStarted += c => Dispatcher.UIThread.Post(() => { _streaming = null; AddCard(c); });
            _chat.ToolFinished += c => Dispatcher.UIThread.Post(() => { if (_cards.TryGetValue(c.Id, out var card)) card.Finish(c); });

            // ---- header: title, model, new chat, key, menu
            foreach (var m in ClaudeChat.Models) _model.Items.Add(ModelLabel(m));
            _model.SelectedIndex = 0;
            _model.SelectionChanged += (s, e) => { if (_model.SelectedIndex >= 0) _chat.Model = ClaudeChat.Models[_model.SelectedIndex]; };
            ToolTip.SetTip(_model, "Model — Opus is the most capable, Sonnet faster and cheaper, Haiku the fastest");
            var newChat = IconButton("Plus", "New conversation", NewChat);
            var key = IconButton("Gear", "Anthropic API key", () => _ = StoreKeysDialog.Run("anthropic"));
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            title.Children.Add(new VxIcon { Icon = "Sparkle", Width = 14, Height = 14, Foreground = EditorKit.Brush("VxAccentBrush"), VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = "Claude", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            right.Children.Add(_model);
            right.Children.Add(newChat);
            right.Children.Add(key);
            right.Children.Add(MenuButton());
            var header = new DockPanel { Height = 36 };
            DockPanel.SetDock(right, Dock.Right);
            header.Children.Add(right);
            header.Children.Add(title);
            var headerBorder = new Border { Classes = { "panelheader" }, Child = header };
            DockPanel.SetDock(headerBorder, Dock.Top);

            // ---- footer: input, send/stop, usage
            _send = new Button { Classes = { "accent" }, Content = "Send", MinWidth = 64, VerticalAlignment = VerticalAlignment.Bottom };
            _send.Click += (s, e) => { if (_busy) Stop(); else _ = SendAsync(_input.Text); };
            _input.AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; if (!_busy) _ = SendAsync(_input.Text); }
                else if (e.Key == Key.Escape && _busy) { e.Handled = true; Stop(); }
            }, RoutingStrategies.Tunnel);
            var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            inputRow.Children.Add(_input);
            Grid.SetColumn(_send, 1);
            inputRow.Children.Add(_send);
            var footer = new StackPanel { Spacing = 4, Margin = new Thickness(10, 6, 10, 8) };
            footer.Children.Add(inputRow);
            footer.Children.Add(_usage);
            var footerBorder = new Border { Classes = { "hairline-top" }, Child = footer };
            DockPanel.SetDock(footerBorder, Dock.Bottom);

            // ---- transcript
            _empty = EmptyState();
            _log.Children.Add(_empty);
            _scroll = new ScrollViewer { Content = _log, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

            var root = new DockPanel();
            root.Children.Add(headerBorder);
            root.Children.Add(footerBorder);
            root.Children.Add(_scroll);
            Content = root;
            UpdateUsage();
        }

        // ================================================================== sending

        /// <summary>Send a message (also used by the example chips and tests).</summary>
        public async Task SendAsync(string text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || _busy) return;
            if (ProjectData.Current == null) { AddNote("Open a project first — Claude works on the open project.", true); return; }
            if (!StoreKeys.Has("anthropic")) { AddKeyPrompt(); return; }
            _input.Text = "";
            _empty.IsVisible = false;
            AddUser(text);
            SetBusy(true);
            _cts = new CancellationTokenSource();
            try { await _chat.SendAsync(text, _cts.Token); }
            catch (OperationCanceledException) { AddNote("Stopped.", false); }
            catch (ClaudeChatException ex)
            {
                AddNote(ex.Message, true);
                if (ex.Status == System.Net.HttpStatusCode.Unauthorized) AddKeyPrompt();
            }
            catch (Exception ex) { AddNote("Something went wrong: " + ex.Message, true); }
            finally
            {
                SetBusy(false);
                _streaming = null;
                UpdateUsage();
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
            _chat.Reset();
            _cards.Clear();
            _log.Children.Clear();
            _log.Children.Add(_empty);
            _empty.IsVisible = true;
            UpdateUsage();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _send.Content = busy ? "Stop" : "Send";
            _send.Classes.Set("accent", !busy);
            _model.IsEnabled = !busy;
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

        private void AddUser(string text)
        {
            var b = new Border
            {
                Background = EditorKit.Brush("VxSelectionBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7),
                HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 560, Margin = new Thickness(30, 4, 0, 0),
                Child = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            };
            _log.Children.Add(b);
            ScrollToEnd();
        }

        private void AppendText(string delta)
        {
            if (_streaming == null)
            {
                _streaming = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
                _log.Children.Add(_streaming);
            }
            _streaming.Text += delta;
            ScrollToEnd();
        }

        private void AddCard(ChatToolCall call)
        {
            var def = ToolCatalog.Find(call.Name);
            var card = new ToolCard(call, def);
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

        private void AddKeyPrompt()
        {
            _empty.IsVisible = false;
            var box = new StackPanel { Spacing = 6 };
            box.Children.Add(new TextBlock { Text = "The Claude panel uses your own Anthropic API key (billed per token by Anthropic; it never leaves this computer except to api.anthropic.com).", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            box.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    ModelTools(),
                    LinkButton("Get a key", () => EditorCommands.OpenUrl("https://console.anthropic.com/settings/keys")),
                },
            });
            box.Children.Add(new TextBlock { Text = "No key? Claude Code with your Claude subscription can drive the editor too: Tools ▸ Claude ▸ Connect Claude Code.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = EditorKit.Brush("VxTextTertiaryBrush") });
            _log.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1), Child = box });
            ScrollToEnd();

            Control ModelTools()
            {
                var b = new Button { Classes = { "accent" }, Content = "Add API Key…" };
                b.Click += (s, e) => _ = StoreKeysDialog.Run("anthropic");
                return b;
            }
        }

        private Control EmptyState()
        {
            var sp = new StackPanel { Spacing = 8, Margin = new Thickness(2, 6, 2, 0) };
            sp.Children.Add(new TextBlock { Text = "Build with Claude", FontSize = 15, FontWeight = FontWeight.SemiBold });
            sp.Children.Add(new TextBlock
            {
                Text = "Claude can see and change the open scene: entities, lights, materials, scripts, audio — and look at the viewport. " +
                       "Each change is one undo step; you approve changes before they happen.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = EditorKit.Brush("VxTextSecondaryBrush"),
            });
            foreach (var example in new[]
            {
                "What's in this scene? Show me a screenshot.",
                "Build a 12 m dark corridor with four flickering ceiling lights.",
                "Scatter 30 pieces of debris on the floor around the player.",
                "Write a script that makes this lamp flicker and attach it.",
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

        private void UpdateUsage()
        {
            int input = _chat.InputTokens + _chat.CacheReadTokens + _chat.CacheWriteTokens;
            if (input == 0 && _chat.OutputTokens == 0) { _usage.Text = ModelLabel(_chat.Model) + " · your Anthropic key"; return; }
            int cachedPct = input > 0 ? (int)Math.Round(100.0 * _chat.CacheReadTokens / input) : 0;
            _usage.Text = ModelLabel(_chat.Model) + " · " + K(input) + " in (" + cachedPct + "% cached) · " + K(_chat.OutputTokens) + " out";
        }

        private static string K(int n) => n >= 1000 ? (n / 1000.0).ToString(n >= 10000 ? "0" : "0.0", CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);

        private static string ModelLabel(string id) => id switch
        {
            "claude-opus-5-5" => "Opus 5.5",
            "claude-sonnet-5-5" => "Sonnet 5.5",
            "claude-haiku-4-5-20251001" => "Haiku 4.5",
            _ => id,
        };

        private void ScrollToEnd() => Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);

        private Control MenuButton()
        {
            var b = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "More" } };
            ToolTip.SetTip(b, "More");
            var menu = new ContextMenu();
            var reset = new MenuItem { Header = "Reset “Always allow” for this project" };
            reset.Click += (s, e) => { ClaudePermissions.Reset(); EditorCommands.Toast("Claude will ask again before changes"); };
            var ops = new MenuItem { Header = "Operations… (history, revert, diffs)" };
            ops.Click += (s, e) => ClaudeOperationsWindow.Open();
            var connect = new MenuItem { Header = "Connect Claude Code / Desktop…" };
            connect.Click += (s, e) => _ = ClaudeConnectDialog.Run();
            var docs = new MenuItem { Header = "Documentation" };
            docs.Click += (s, e) => EditorCommands.OpenUrl("https://github.com/shadow-kernel/Vortex-Engine/wiki/Claude-Integration");
            menu.Items.Add(ops);
            menu.Items.Add(reset);
            menu.Items.Add(connect);
            menu.Items.Add(new Separator());
            menu.Items.Add(docs);
            b.ContextMenu = menu;
            b.Click += (s, e) => menu.Open(b);
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
            var b = new Button { Classes = { "link" }, Content = text, VerticalAlignment = VerticalAlignment.Center };
            b.Click += (s, e) => click();
            return b;
        }

        // ================================================================== tool card

        /// <summary>One tool call: status, title, a one-line summary; click for input / result / image; approval buttons
        /// while Claude waits for the user.</summary>
        private sealed class ToolCard : Border
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
                var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
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
                void Done(ToolApproval a, string label)
                {
                    _body.Children.Remove(_approval);
                    _approval = null;
                    _details.IsVisible = false;
                    BorderBrush = EditorKit.Brush("VxHairlineBrush");
                    if (a == ToolApproval.Deny) _status.Text = "⊘";
                    decide(a);
                }
                var allow = new Button { Classes = { "accent" }, Content = "Allow", Margin = new Thickness(0, 0, 6, 4) };
                allow.Click += (s, e) => Done(ToolApproval.Allow, "allowed");
                var always = new Button { Content = "Always allow", Margin = new Thickness(0, 0, 6, 4) };
                ToolTip.SetTip(always, "Don't ask again for " + _call.Name + " in this project");
                always.Click += (s, e) => { ClaudePermissions.AllowAlways(_call.Name); Done(ToolApproval.Allow, "always"); };
                var deny = new Button { Content = "Deny", Margin = new Thickness(0, 0, 6, 4) };
                deny.Click += (s, e) => Done(ToolApproval.Deny, "denied");
                row.Children.Add(allow);
                row.Children.Add(always);
                row.Children.Add(deny);
                _approval = row;
                _body.Children.Insert(1, row);
                _details.IsVisible = true;
                BorderBrush = EditorKit.Brush("VxAccentBrush");
            }

            public void Finish(ChatToolCall call)
            {
                BorderBrush = EditorKit.Brush("VxHairlineBrush");
                if (_approval != null) { _body.Children.Remove(_approval); _approval = null; }
                var r = call.Result;
                _status.Text = call.Denied ? "⊘" : r?.IsError == true ? "✗" : "✓";
                _status.Foreground = EditorKit.Brush(call.Denied ? "VxTextTertiaryBrush" : r?.IsError == true ? "VxRedBrush" : "VxGreenBrush");
                if (r == null) return;
                if (!string.IsNullOrEmpty(r.Text))
                {
                    _details.Children.Add(Code(r.IsError ? "Error" : "Result", Pretty(r.Text, 2400)));
                    if (r.IsError) _summary.Text = OneLine(r.Text);
                }
                foreach (var (data, mime) in r.Images)
                {
                    try
                    {
                        using var ms = new MemoryStream(data);
                        var img = new Image { Source = new Bitmap(ms), MaxWidth = 520, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
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
