using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Helpers;
using Anthropic.Models.Beta.Messages;
using Editor.Core.Assets.Store;

namespace Editor.Core.Claude
{
    /// <summary>Ask: Claude reads, looks and explains, and does not change the project. Agent: Claude may change it —
    /// the user approves each change unless they allowed that tool always.</summary>
    public enum ClaudeMode { Ask, Agent }

    /// <summary>
    /// One Claude conversation for the editor's Claude panel, on the official Anthropic C# SDK (Messages API, beta
    /// surface, streamed): the chosen model and effort, adaptive thinking shown as summaries, server-side compaction
    /// at the chosen context size, server-side refusal fallbacks, and the tool loop over the editor's tools.
    /// <para>The conversation is append-only — thinking, compaction and fallback blocks go back exactly as they came,
    /// because the current models bind their thinking to an unedited history. So the tool list stays the same in both
    /// modes; Ask mode refuses tools that change something when they are called, and a short note tells Claude which
    /// mode a turn is in.</para>
    /// </summary>
    public sealed class ClaudeSession
    {
        public ClaudeModelInfo Model = ClaudeModels.All[0];
        /// <summary>Effort level; null = the model's default. Ignored by models that take none.</summary>
        public string Effort;
        /// <summary>The context size the conversation may grow to; it is compacted on the server at about 80 %.</summary>
        public int ContextSize = 1_000_000;
        public ClaudeMode Mode = ClaudeMode.Agent;
        public string SystemPrompt;
        public IReadOnlyList<ChatTool> Tools = Array.Empty<ChatTool>();
        /// <summary>Tool rounds per message before Claude stops and asks to continue.</summary>
        public int MaxRounds = 60;

        /// <summary>The SDK client (credentials + HTTP); tests swap in scripted responses through <see cref="StoreHttp.Handler"/>.</summary>
        public Func<AnthropicClient> CreateClient = () => ClaudeAccount.CreateClient(ClaudeHttp.Client());
        public Func<ChatToolCall, CancellationToken, Task<ChatToolResult>> RunTool;
        /// <summary>Asked before a tool that changes something runs (Agent mode).</summary>
        public Func<ChatToolCall, CancellationToken, Task<ToolApproval>> Approve;

        public event Action TextStarted;
        public event Action<string> TextDelta;
        public event Action ThinkingStarted;
        public event Action<string> ThinkingDelta;
        public event Action<ChatToolCall> ToolStarted;
        public event Action<ChatToolCall> ToolFinished;
        /// <summary>The server summarized earlier turns to stay within the context size.</summary>
        public event Action Compacted;
        /// <summary>The model declined (after the server's fallbacks); the argument explains why.</summary>
        public event Action<string> Refused;
        /// <summary>A turn finished and the token counters moved.</summary>
        public event Action TurnCompleted;

        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public long CacheReadTokens { get; private set; }
        public long CacheWriteTokens { get; private set; }
        /// <summary>How much of the context the last request used (input, cached or not).</summary>
        public long ContextTokens { get; private set; }
        /// <summary>What the conversation cost so far at first-party API prices, in USD (an estimate: each turn at the
        /// prices of the model that ran it).</summary>
        public double Cost { get; private set; }
        public int MessageCount => _messages.Count;

        public string EffectiveEffort => Model.Efforts.Length == 0 ? null
            : Effort != null && Model.Efforts.Contains(Effort) ? Effort : Model.DefaultEffort;

        /// <summary>The input size at which the server compacts the conversation.</summary>
        public int CompactAt => (int)(Math.Min(ContextSize, Model.ContextWindow) * 0.8);

        internal const string AskNote = "[Ask mode] Answer, explain and look around (read the scene, capture the viewport, read scripts). " +
            "Do not change the project: tools that change something are switched off until the user picks Agent mode — describe the change instead.";
        internal const string AgentNote = "[Agent mode] You may change the project with the tools. The user approves each change unless they allowed that tool always.";

        /// <summary>The conversation: role (true = user) and content blocks. Roles alternate — a message after a failed or
        /// stopped turn joins the user turn that is still open.</summary>
        private readonly List<(bool user, List<BetaContentBlockParam> content)> _messages = new List<(bool user, List<BetaContentBlockParam> content)>();
        private readonly List<string> _openToolUses = new List<string>();
        private ClaudeMode? _modeSent;

        public void Reset()
        {
            _messages.Clear();
            _openToolUses.Clear();
            _modeSent = null;
            InputTokens = OutputTokens = CacheReadTokens = CacheWriteTokens = ContextTokens = 0;
            Cost = 0;
        }

        /// <summary>Send a message and run the tool loop until Claude is done. Returns Claude's last text.</summary>
        public Task<string> SendAsync(string userText, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userText)) return Task.FromResult("");
            var content = new List<BetaContentBlockParam>();
            // tool calls of a turn that ended early (output limit) still need their results, first in the next message
            foreach (var id in _openToolUses)
                content.Add(new BetaToolResultBlockParam { ToolUseID = id, IsError = true, Content = "Not run: the answer ended before this call." });
            _openToolUses.Clear();
            if (_modeSent != Mode) content.Add(new BetaTextBlockParam { Text = Mode == ClaudeMode.Ask ? AskNote : AgentNote });
            content.Add(new BetaTextBlockParam { Text = userText });
            AppendUser(content);
            _modeSent = Mode;
            return RunAsync(ct);
        }

        /// <summary>True when the conversation ends with the user's turn (a request failed or was stopped): <see cref="RetryAsync"/>
        /// runs it again.</summary>
        public bool CanRetry => _messages.Count > 0 && _messages[_messages.Count - 1].user;

        /// <summary>Run the open user turn again (after an error or a stop).</summary>
        public Task<string> RetryAsync(CancellationToken ct) => CanRetry ? RunAsync(ct) : Task.FromResult("");

        private async Task<string> RunAsync(CancellationToken ct)
        {
            try { return await LoopAsync(ct).ConfigureAwait(false); }
            catch
            {
                // stopped or failed between a tool request and its results: answer the open calls, so the conversation
                // stays valid for the next message
                RepairOpenToolUses();
                throw;
            }
        }

        /// <summary>A user turn: a new message after Claude's turn, or more content for the user turn that is still open
        /// (the API rejects two user messages in a row; nothing after that turn carries thinking, so this edits no
        /// thinking block's history).</summary>
        private void AppendUser(List<BetaContentBlockParam> content)
        {
            if (_messages.Count > 0 && _messages[_messages.Count - 1].user) _messages[_messages.Count - 1].content.AddRange(content);
            else _messages.Add((true, content));
        }

        private void RepairOpenToolUses()
        {
            if (_openToolUses.Count == 0) return;
            var results = new List<BetaContentBlockParam>();
            foreach (var id in _openToolUses)
                results.Add(new BetaToolResultBlockParam { ToolUseID = id, IsError = true, Content = "Stopped by the user before this call ran." });
            _openToolUses.Clear();
            AppendUser(results);
        }

        private async Task<string> LoopAsync(CancellationToken ct)
        {
            var client = CreateClient();
            string lastText = "";
            for (int round = 0; round < MaxRounds; round++)
            {
                BetaMessage msg = await StreamTurnAsync(client, ct).ConfigureAwait(false);
                string stop = StopReasonOf(msg);
                if (stop == "refusal")
                {
                    // the model declined (after the server's fallbacks): a partial turn is not an answer and does not go
                    // into the history, and a declined request of the user's own goes too (they rephrase it); tool results
                    // stay — they answer Claude's earlier calls
                    var last = _messages[_messages.Count - 1];
                    if (last.user && !last.content.Any(b => b.TryPickToolResult(out _)))
                    {
                        _messages.RemoveAt(_messages.Count - 1);
                        _modeSent = null;
                    }
                    string why = msg.StopDetails?.Explanation;
                    try { Refused?.Invoke(string.IsNullOrWhiteSpace(why) ? "Claude declined this request." : why); } catch { }
                    return lastText;
                }
                var echo = Echo(msg, out var toolUses);
                if (echo.Count == 0) echo.Add(new BetaTextBlockParam { Text = "(no answer)" });
                _messages.Add((false, echo));
                _openToolUses.Clear();
                _openToolUses.AddRange(toolUses.Select(t => t.ID));

                var texts = msg.Content.Select(b => b.TryPickText(out BetaTextBlock t) ? t.Text : null).Where(s => !string.IsNullOrEmpty(s)).ToList();
                if (texts.Count > 0) lastText = string.Join("\n", texts);

                if (stop == "max_tokens") return lastText + "\n\n(The answer hit the output limit — say \"continue\" to go on.)";
                if (stop != "tool_use" || toolUses.Count == 0) return lastText;

                var results = new List<BetaContentBlockParam>();
                foreach (var use in toolUses)
                {
                    var call = new ChatToolCall { Id = use.ID, Name = use.Name, Input = ToJsonObject(use.Input) };
                    await RunCallAsync(call, ct).ConfigureAwait(false);
                    results.Add(ResultBlock(call));
                }
                _openToolUses.Clear();
                AppendUser(results);
                ct.ThrowIfCancellationRequested();
            }
            return lastText + "\n\n(Stopped after " + MaxRounds + " tool rounds — say \"continue\" to go on.)";
        }

        private async Task RunCallAsync(ChatToolCall call, CancellationToken ct)
        {
            var tool = Tools.FirstOrDefault(t => t.Name == call.Name);
            try { ToolStarted?.Invoke(call); } catch { }
            if (ct.IsCancellationRequested) call.Result = new ChatToolResult { IsError = true, Text = "Stopped by the user before this call ran." };
            else if (tool == null) call.Result = new ChatToolResult { IsError = true, Text = "Unknown tool " + call.Name + "." };
            else if (!tool.ReadOnly && Mode == ClaudeMode.Ask)
            {
                call.Denied = true;
                call.Result = new ChatToolResult
                {
                    IsError = true,
                    Text = "Ask mode: " + call.Name + " would change the project, and changes are switched off in Ask mode. " +
                           "Describe the change instead; the user switches to Agent mode to let you make it.",
                };
            }
            else
            {
                var approval = ToolApproval.Allow;
                if (!tool.ReadOnly && Approve != null)
                {
                    try { approval = await Approve(call, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { approval = ToolApproval.Deny; }
                }
                if (ct.IsCancellationRequested) call.Result = new ChatToolResult { IsError = true, Text = "Stopped by the user before this call ran." };
                else if (approval == ToolApproval.Deny)
                {
                    call.Denied = true;
                    call.Result = new ChatToolResult { IsError = true, Text = "The user denied this tool call. Do not retry it; ask what they want instead." };
                }
                else
                {
                    try { call.Result = RunTool != null ? await RunTool(call, ct).ConfigureAwait(false) : new ChatToolResult { IsError = true, Text = "No tool runner." }; }
                    catch (OperationCanceledException) { call.Result = new ChatToolResult { IsError = true, Text = "Stopped by the user while this call ran." }; }
                    catch (Exception ex) { call.Result = new ChatToolResult { IsError = true, Text = call.Name + " failed: " + ex.Message }; }
                }
            }
            try { ToolFinished?.Invoke(call); } catch { }
        }

        // ------------------------------------------------------------------ one streamed turn

        private async Task<BetaMessage> StreamTurnAsync(AnthropicClient client, CancellationToken ct)
        {
            var parameters = BuildParams();
            var aggregator = new BetaMessageContentAggregator();
            try
            {
                await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, ct).CollectAsync(aggregator).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (ev.TryPickContentBlockStart(out var start))
                    {
                        if (start.ContentBlock.TryPickBetaText(out _)) { try { TextStarted?.Invoke(); } catch { } }
                        else if (start.ContentBlock.TryPickBetaThinking(out _)) { try { ThinkingStarted?.Invoke(); } catch { } }
                    }
                    else if (ev.TryPickContentBlockDelta(out var d))
                    {
                        if (d.Delta.TryPickText(out var text)) { try { TextDelta?.Invoke(text.Text); } catch { } }
                        else if (d.Delta.TryPickThinking(out var thinking)) { try { ThinkingDelta?.Invoke(thinking.Thinking); } catch { } }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (Friendly(ex) is ClaudeChatException friendly) { throw friendly; }

            var msg = aggregator.Message();
            var u = msg.Usage;
            if (u != null)
            {
                long input = u.InputTokens, read = u.CacheReadInputTokens ?? 0, write = u.CacheCreationInputTokens ?? 0;
                InputTokens += input;
                CacheReadTokens += read;
                CacheWriteTokens += write;
                OutputTokens += u.OutputTokens;
                ContextTokens = input + read + write;
                Cost += (input * Model.InputPrice + write * Model.InputPrice * 1.25 + read * Model.CacheReadPrice + u.OutputTokens * Model.OutputPrice) / 1_000_000.0;
            }
            try { TurnCompleted?.Invoke(); } catch { }
            return msg;
        }

        /// <summary>The request of the next turn (internal for tests).</summary>
        internal MessageCreateParams BuildParams()
        {
            var betas = new List<Anthropic.Core.ApiEnum<string, Anthropic.Models.Beta.AnthropicBeta>>();
            if (Model.Compaction) betas.Add(Anthropic.Models.Beta.AnthropicBeta.Compact2026_01_12);
            if (Model.Fallbacks) betas.Add(Anthropic.Models.Beta.AnthropicBeta.ServerSideFallback2026_07_01);
            string effort = EffectiveEffort;
            return new MessageCreateParams
            {
                Model = Model.Id,
                MaxTokens = Math.Min(64_000, Model.MaxOutputTokens),
                System = new List<BetaTextBlockParam> { new BetaTextBlockParam { Text = SystemPrompt ?? "", CacheControl = new BetaCacheControlEphemeral() } },
                Tools = BuildTools(),
                Messages = _messages.Select(m => new BetaMessageParam { Role = m.user ? Role.User : Role.Assistant, Content = new List<BetaContentBlockParam>(m.content) }).ToList(),
                // automatic cache point at the end of the conversation: every turn reuses the history before it
                CacheControl = new BetaCacheControlEphemeral(),
                Thinking = Model.Thinking ? new BetaThinkingConfigAdaptive { Display = Display.Summarized } : null,
                OutputConfig = effort != null ? new BetaOutputConfig { Effort = EffortOf(effort) } : null,
                ContextManagement = Model.Compaction
                    ? new BetaContextManagementConfig { Edits = [new BetaCompact20260112Edit { Trigger = new BetaInputTokensTrigger { ValueValue = CompactAt } }] }
                    : null,
                Fallbacks = Model.Fallbacks ? new BetaFallbacksParam(new Default()) : null,
                Betas = betas.Count > 0 ? betas : null,
            };
        }

        private List<BetaToolUnion> BuildTools()
        {
            var list = new List<BetaToolUnion>();
            for (int i = 0; i < Tools.Count; i++)
            {
                var t = Tools[i];
                var (props, required) = Schema(t.InputSchemaJson);
                list.Add(new BetaTool
                {
                    Name = t.Name,
                    Description = t.Description ?? "",
                    InputSchema = new InputSchema { Properties = props, Required = required },
                    CacheControl = i == Tools.Count - 1 ? new BetaCacheControlEphemeral() : null,
                });
            }
            return list;
        }

        private static (Dictionary<string, JsonElement> props, List<string> required) Schema(string json)
        {
            var props = new Dictionary<string, JsonElement>();
            var required = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return (props, required);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var kv in p.EnumerateObject()) props[kv.Name] = kv.Value.Clone();
            if (doc.RootElement.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array)
                foreach (var v in r.EnumerateArray()) if (v.ValueKind == JsonValueKind.String) required.Add(v.GetString());
            return (props, required);
        }

        private static Effort EffortOf(string level) => level switch
        {
            "low" => Anthropic.Models.Beta.Messages.Effort.Low,
            "medium" => Anthropic.Models.Beta.Messages.Effort.Medium,
            "xhigh" => Anthropic.Models.Beta.Messages.Effort.Xhigh,
            "max" => Anthropic.Models.Beta.Messages.Effort.Max,
            _ => Anthropic.Models.Beta.Messages.Effort.High,
        };

        // ------------------------------------------------------------------ the assistant turn as it goes back

        /// <summary>Claude's turn as the next request sends it back — every block unchanged (thinking with its signature,
        /// compaction, fallback), empty text dropped (the API rejects it).</summary>
        private List<BetaContentBlockParam> Echo(BetaMessage msg, out List<BetaToolUseBlock> toolUses)
        {
            var list = new List<BetaContentBlockParam>();
            toolUses = new List<BetaToolUseBlock>();
            foreach (var b in msg.Content)
            {
                if (b.TryPickText(out BetaTextBlock text)) { if (!string.IsNullOrEmpty(text.Text)) list.Add(new BetaTextBlockParam { Text = text.Text }); }
                // thinking, compaction and fallback blocks go back byte for byte (their raw JSON): the current models bind
                // their thinking to an unedited history
                else if (b.TryPickThinking(out BetaThinkingBlock thinking)) list.Add(BetaThinkingBlockParam.FromRawUnchecked(thinking.RawData));
                else if (b.TryPickRedactedThinking(out BetaRedactedThinkingBlock redacted)) list.Add(BetaRedactedThinkingBlockParam.FromRawUnchecked(redacted.RawData));
                else if (b.TryPickToolUse(out BetaToolUseBlock use))
                {
                    list.Add(new BetaToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                    toolUses.Add(use);
                }
                else if (b.TryPickCompaction(out BetaCompactionBlock compaction))
                {
                    list.Add(BetaCompactionBlockParam.FromRawUnchecked(compaction.RawData));
                    try { Compacted?.Invoke(); } catch { }
                }
                else if (b.TryPickFallback(out BetaFallbackBlock fallback))
                    list.Add(BetaFallbackBlockParam.FromRawUnchecked(fallback.RawData));
            }
            return list;
        }

        private static BetaToolResultBlockParam ResultBlock(ChatToolCall call)
        {
            var r = call.Result ?? new ChatToolResult { Text = "ok" };
            var content = new List<Block>();
            if (!string.IsNullOrEmpty(r.Text)) content.Add(new BetaTextBlockParam { Text = r.Text });
            foreach (var (data, mime) in r.Images)
                content.Add(new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(data), MediaType = mime } });
            if (content.Count == 0) content.Add(new BetaTextBlockParam { Text = "ok" });
            return new BetaToolResultBlockParam { ToolUseID = call.Id, Content = content, IsError = r.IsError ? true : null };
        }

        private static System.Text.Json.Nodes.JsonObject ToJsonObject(IReadOnlyDictionary<string, JsonElement> input)
        {
            var o = new System.Text.Json.Nodes.JsonObject();
            if (input != null)
                foreach (var kv in input) o[kv.Key] = System.Text.Json.Nodes.JsonNode.Parse(kv.Value.GetRawText());
            return o;
        }

        private static string StopReasonOf(BetaMessage msg)
        {
            try { return msg.StopReason?.ToString()?.Trim('"').ToLowerInvariant(); } catch { return null; }
        }

        /// <summary>The API's own sentence from an error ("Status Code: BadRequest {"type":"error","error":{…,"message":"…"}}").</summary>
        internal static string ApiMessage(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "the request failed";
            int brace = raw.IndexOf('{');
            if (brace >= 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw.Substring(brace));
                    if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        return m.GetString();
                }
                catch { }
            }
            return raw;
        }

        /// <summary>The SDK's errors as the short sentences the panel shows.</summary>
        private static ClaudeChatException Friendly(Exception ex)
        {
            switch (ex)
            {
                case Anthropic.Exceptions.AnthropicUnauthorizedException:
                    return new ClaudeChatException("Claude: the credentials were rejected — sign in again or check the API key.", HttpStatusCode.Unauthorized);
                case Anthropic.Exceptions.AnthropicForbiddenException:
                    return new ClaudeChatException("Claude: this account or workspace may not use that model.", HttpStatusCode.Forbidden);
                case Anthropic.Exceptions.AnthropicRateLimitException:
                    return new ClaudeChatException("Claude: rate limited — wait a moment and try again.", (HttpStatusCode)429);
                case Anthropic.Exceptions.Anthropic5xxException:
                    return new ClaudeChatException("Claude: the API is busy or unavailable — try again in a moment.", HttpStatusCode.ServiceUnavailable);
                case Anthropic.Exceptions.AnthropicApiException api:
                    {
                        string msg = ApiMessage(api.Message);
                        // a Console account without credits (a Claude Pro / Max plan is billed separately and can't be used here)
                        if (msg.IndexOf("credit balance", StringComparison.OrdinalIgnoreCase) >= 0)
                            return new ClaudeChatException("Claude: this Anthropic Console account has no API credits — add some under Plans & Billing. " +
                                                           "A Claude Pro / Max plan is billed separately: Anthropic lets it work only in its own apps.", HttpStatusCode.PaymentRequired);
                        return new ClaudeChatException("Claude: " + msg);
                    }
                case HttpRequestException http:
                    return new ClaudeChatException("Claude: no connection to the Anthropic API (" + http.Message + ").");
                default:
                    return null;
            }
        }
    }

    /// <summary>The HTTP client the panel's SDK client uses: one pooled client for the editor's lifetime, or the tests'
    /// scripted transport (<see cref="StoreHttp.Handler"/>). No HttpClient timeout — the SDK's own (long) timeout
    /// applies, so long thinking turns are not cut off.</summary>
    public static class ClaudeHttp
    {
        private static HttpClient _shared;
        private static readonly object Gate = new object();

        public static HttpClient Client()
        {
            var handler = StoreHttp.Handler;
            if (handler != null) return new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            lock (Gate)
                return _shared ??= new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
                {
                    Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                };
        }
    }
}
