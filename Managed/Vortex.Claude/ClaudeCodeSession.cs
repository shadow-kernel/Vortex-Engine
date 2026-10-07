using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Core.Claude
{
    /// <summary>
    /// The editor's Claude panel driven by the user's own installed Claude Code (<c>claude</c>), run headless — no
    /// terminal, the panel stays the UI. This is how a Claude plan (Pro / Max) works in the panel: Claude Code signs in
    /// by itself and bills the plan; the editor never sees those credentials. Anthropic allows running Claude Code,
    /// unmodified, inside a product as long as each user authenticates with their own plan — this does exactly that.
    /// <para>One <c>claude -p --output-format stream-json</c> process runs each turn and exits; the conversation
    /// continues across turns with <c>--resume</c> on a session id this class owns. The stream is parsed into the same
    /// events <see cref="ClaudeSession"/> raises, so the panel cannot tell the two engines apart.</para>
    /// <para>The editor's tools are reached over the editor's own MCP server (the panel passes the server's config and a
    /// hook that starts it): the same tools, same undo steps, same safety as the SDK engine. Built-in Claude Code tools
    /// (Bash, file edits, web) are switched off — the panel is about operating the editor, not the user's disk.</para>
    /// </summary>
    public sealed class ClaudeCodeSession : IClaudeEngine
    {
        public ClaudeModelInfo Model { get; set; } = ClaudeModels.All[0];
        public string Effort { get; set; }
        public int ContextSize { get; set; } = 1_000_000;
        public ClaudeMode Mode { get; set; } = ClaudeMode.Agent;
        public string SystemPrompt { get; set; }
        public IReadOnlyList<ChatTool> Tools { get; set; } = Array.Empty<ChatTool>();

        /// <summary>The folder Claude Code runs on (the open project).</summary>
        public string ProjectDir;
        /// <summary>The <c>--mcp-config</c> JSON for the editor's MCP server (the panel builds it from the live URL).</summary>
        public string McpConfigJson;
        /// <summary>Called before the first turn: start the editor's MCP server. False aborts with a clear message.</summary>
        public Func<CancellationToken, Task<bool>> EnsureToolsReady;
        /// <summary>The MCP server's tool prefix Claude Code uses, e.g. <c>mcp__vortex__</c>.</summary>
        public string ToolPrefix = "mcp__vortex__";
        /// <summary>The bare name of the MCP tool the editor exposes to answer permission prompts (Agent mode).</summary>
        public const string ApproveTool = "approve";
        /// <summary>Tools the user chose to always allow (bare names) — pre-allowed so they never prompt.</summary>
        public IReadOnlyCollection<string> AlwaysAllowedTools;

        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public long CacheReadTokens { get; private set; }
        public long CacheWriteTokens { get; private set; }
        public long ContextTokens { get; private set; }
        public double Cost { get; private set; }
        public int MessageCount { get; private set; }
        public int CompactAt => (int)(Math.Min(ContextSize, Model.ContextWindow) * 0.8);

        public string EffectiveEffort => Model.Efforts.Length == 0 ? null
            : Effort != null && Model.Efforts.Contains(Effort) ? Effort : Model.DefaultEffort;

        public event Action TextStarted;
        public event Action<string> TextDelta;
        public event Action ThinkingStarted;
        public event Action<string> ThinkingDelta;
        public event Action<ChatToolCall> ToolStarted;
        public event Action<ChatToolCall> ToolFinished;
        public event Action Compacted;
        public event Action<string> Refused;
        public event Action TurnCompleted;

        private string _sessionId;
        private bool _started;
        private string _lastUserText;
        private bool _canRetry;
        public bool CanRetry => _canRetry && _lastUserText != null;

        private readonly Dictionary<string, ChatToolCall> _pending = new Dictionary<string, ChatToolCall>();

        public void Reset()
        {
            _sessionId = null;
            _started = false;
            _lastUserText = null;
            _canRetry = false;
            _pending.Clear();
            InputTokens = OutputTokens = CacheReadTokens = CacheWriteTokens = ContextTokens = 0;
            Cost = 0;
            MessageCount = 0;
        }

        public Task<string> SendAsync(string message, CancellationToken ct)
        {
            _lastUserText = message;
            return RunTurnAsync(message, ct);
        }

        public Task<string> RetryAsync(CancellationToken ct) =>
            CanRetry ? RunTurnAsync(_lastUserText, ct) : Task.FromResult("");

        private async Task<string> RunTurnAsync(string message, CancellationToken ct)
        {
            string claude = ClaudeCode.FindCli();
            if (claude == null) throw new ClaudeChatException("Claude Code (claude) is not installed.");
            if (EnsureToolsReady != null && !await EnsureToolsReady(ct).ConfigureAwait(false))
                throw new ClaudeChatException("The editor's MCP server did not start — Claude Code cannot reach the editor's tools.");
            if (string.IsNullOrEmpty(McpConfigJson))
                throw new ClaudeChatException("No MCP configuration — the editor's tools are not available.");

            _sessionId ??= Guid.NewGuid().ToString();
            _pending.Clear();

            var (file, args) = BuildCommand(claude, message);
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                WorkingDirectory = Directory.Exists(ProjectDir) ? ProjectDir : Environment.CurrentDirectory,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            try { proc.Start(); }
            catch (Exception ex) { _canRetry = true; throw new ClaudeChatException("Claude Code did not start: " + ex.Message); }

            try { proc.StandardInput.Close(); } catch { }
            var stderr = new StringBuilder();
            var stderrTask = Task.Run(async () =>
            {
                try { string l; while ((l = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) lock (stderr) stderr.AppendLine(l); }
                catch { }
            });

            string result = null;
            bool sawResult = false;
            try
            {
                var reader = proc.StandardOutput;
                string line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    if (line.Length == 0 || line[0] != '{') continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); } catch { continue; }
                    using (doc)
                    {
                        if (!doc.RootElement.TryGetProperty("type", out var typeEl)) continue;
                        if (typeEl.GetString() == "result") { result = HandleResult(doc.RootElement); sawResult = true; }
                        else Handle(doc.RootElement);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                _canRetry = true;
                throw;
            }
            catch (Exception ex)
            {
                TryKill(proc);
                _canRetry = true;
                throw new ClaudeChatException("Claude Code stream error: " + ex.Message);
            }

            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); } catch { }
            await stderrTask.ConfigureAwait(false);

            if (!sawResult)
            {
                _canRetry = true;
                string err = stderr.ToString().Trim();
                throw new ClaudeChatException(string.IsNullOrEmpty(err)
                    ? "Claude Code ended without a reply."
                    : "Claude Code failed:\n" + Tail(err, 6));
            }

            _started = true;
            _canRetry = false;
            MessageCount += 2;
            TurnCompleted?.Invoke();
            return result ?? "";
        }

        // ------------------------------------------------------------------ command

        private (string file, List<string> args) BuildCommand(string claude, string message)
        {
            var args = new List<string>
            {
                "--print",
                "--output-format", "stream-json",
                "--include-partial-messages",
                "--verbose",
                "--model", Model.Id,
                "--strict-mcp-config",
                "--mcp-config", McpConfigJson,
                // Agent mode's changes are answered by the editor's approval card through this MCP tool
                "--permission-prompt-tool", ToolPrefix + ApproveTool,
            };
            // no built-in Claude Code tools (Bash, file edits, web): the panel operates the editor, not the user's disk
            args.Add("--tools"); args.Add("");
            // the context-size chip: summarize on Claude Code's side as the conversation nears this size
            if (ContextSize > 0) { args.Add("--autocompact"); args.Add(ContextSize.ToString(CultureInfo.InvariantCulture)); }
            if (EffectiveEffort != null) { args.Add("--effort"); args.Add(EffectiveEffort); }
            if (!string.IsNullOrWhiteSpace(SystemPrompt)) { args.Add("--append-system-prompt"); args.Add(SystemPrompt); }

            // read-only tools (and anything the user chose to always allow) run without a prompt in either mode
            var allowed = Tools.Where(t => t.ReadOnly).Select(t => ToolPrefix + t.Name)
                .Concat((AlwaysAllowedTools ?? Array.Empty<string>()).Select(n => ToolPrefix + n))
                .Distinct().ToArray();
            if (allowed.Length > 0) { args.Add("--allowedTools"); args.Add(string.Join(",", allowed)); }

            // Ask mode: changes are hard-denied (no prompt). Agent mode: they go to the approval card via the prompt tool.
            if (Mode == ClaudeMode.Ask)
            {
                var mutating = Tools.Where(t => !t.ReadOnly).Select(t => ToolPrefix + t.Name).ToArray();
                if (mutating.Length > 0) { args.Add("--disallowedTools"); args.Add(string.Join(",", mutating)); }
            }

            if (_started && _sessionId != null) { args.Add("--resume"); args.Add(_sessionId); }
            else { args.Add("--session-id"); args.Add(_sessionId); }

            args.Add(message);

            // an npm install exposes claude as a .cmd/.bat that must run through cmd on Windows
            if (OperatingSystem.IsWindows() &&
                (claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || claude.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                args.InsertRange(0, new[] { "/c", claude });
                return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", args);
            }
            return (claude, args);
        }

        // ------------------------------------------------------------------ stream

        private bool _anyTextStreamed;

        private void Handle(JsonElement root)
        {
            switch (root.GetProperty("type").GetString())
            {
                case "stream_event":
                    if (root.TryGetProperty("event", out var ev)) HandleStreamEvent(ev);
                    break;
                case "assistant":
                    if (root.TryGetProperty("message", out var am)) HandleAssistant(am);
                    break;
                case "user":
                    if (root.TryGetProperty("message", out var um)) HandleUser(um);
                    break;
            }
        }

        private void HandleStreamEvent(JsonElement ev)
        {
            string t = ev.TryGetProperty("type", out var te) ? te.GetString() : null;
            switch (t)
            {
                case "content_block_start":
                    if (ev.TryGetProperty("content_block", out var cb) && cb.TryGetProperty("type", out var cbt))
                    {
                        if (cbt.GetString() == "text") { _anyTextStreamed = false; TextStarted?.Invoke(); }
                        else if (cbt.GetString() == "thinking") ThinkingStarted?.Invoke();
                    }
                    break;
                case "content_block_delta":
                    if (ev.TryGetProperty("delta", out var d) && d.TryGetProperty("type", out var dt))
                    {
                        if (dt.GetString() == "text_delta" && d.TryGetProperty("text", out var tx)) { _anyTextStreamed = true; TextDelta?.Invoke(tx.GetString()); }
                        else if (dt.GetString() == "thinking_delta" && d.TryGetProperty("thinking", out var th)) ThinkingDelta?.Invoke(th.GetString());
                    }
                    break;
            }
        }

        private void HandleAssistant(JsonElement message)
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
            foreach (var block in content.EnumerateArray())
            {
                string bt = block.TryGetProperty("type", out var b) ? b.GetString() : null;
                if (bt == "tool_use")
                {
                    string id = block.TryGetProperty("id", out var i) ? i.GetString() : Guid.NewGuid().ToString();
                    string name = block.TryGetProperty("name", out var n) ? n.GetString() : "tool";
                    var call = new ChatToolCall { Id = id, Name = ShortName(name), Input = InputObject(block) };
                    _pending[id] = call;
                    ToolStarted?.Invoke(call);
                }
                else if (bt == "text" && !_anyTextStreamed && block.TryGetProperty("text", out var tx))
                {
                    // the text block did not stream as deltas (rare with partial messages on) — show it whole
                    string s = tx.GetString();
                    if (!string.IsNullOrEmpty(s)) { TextStarted?.Invoke(); TextDelta?.Invoke(s); }
                }
            }
            _anyTextStreamed = false;
        }

        private void HandleUser(JsonElement message)
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var b) || b.GetString() != "tool_result") continue;
                string id = block.TryGetProperty("tool_use_id", out var i) ? i.GetString() : null;
                if (id == null || !_pending.TryGetValue(id, out var call)) continue;
                bool isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                call.Result = new ChatToolResult { Text = ResultText(block), IsError = isError };
                call.Denied = false;
                _pending.Remove(id);
                ToolFinished?.Invoke(call);
            }
        }

        private string HandleResult(JsonElement root)
        {
            if (root.TryGetProperty("usage", out var u))
            {
                long inp = Long(u, "input_tokens");
                long read = Long(u, "cache_read_input_tokens");
                long write = Long(u, "cache_creation_input_tokens");
                InputTokens += inp;
                OutputTokens += Long(u, "output_tokens");
                CacheReadTokens += read;
                CacheWriteTokens += write;
                ContextTokens = inp + read + write;
            }
            if (root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var cost)) Cost += cost;

            if (root.TryGetProperty("context_management", out var cm) && cm.TryGetProperty("applied_edits", out var edits)
                && edits.ValueKind == JsonValueKind.Array && edits.GetArrayLength() > 0)
                Compacted?.Invoke();

            bool isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            string subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
            string text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;

            // In Ask mode a change is blocked without a card, so say so. In Agent mode the user denied it on the card
            // (the card already shows ⊘), so no extra note.
            if (Mode == ClaudeMode.Ask && root.TryGetProperty("permission_denials", out var pd)
                && pd.ValueKind == JsonValueKind.Array && pd.GetArrayLength() > 0)
            {
                var names = pd.EnumerateArray()
                    .Select(x => x.TryGetProperty("tool_name", out var tn) ? ShortName(tn.GetString()) : null)
                    .Where(x => x != null).Distinct().ToArray();
                if (names.Length > 0)
                    Refused?.Invoke("Not done in Ask mode (it would change the project): " + string.Join(", ", names) + ". Switch to Agent to make changes.");
            }

            if (isError || (subtype != null && subtype != "success"))
            {
                string why = text;
                if (string.IsNullOrWhiteSpace(why) && root.TryGetProperty("api_error_status", out var ae) && ae.ValueKind == JsonValueKind.String) why = ae.GetString();
                Refused?.Invoke(string.IsNullOrWhiteSpace(why) ? "Claude Code could not finish this turn." : why);
            }
            return text;
        }

        // ------------------------------------------------------------------ helpers

        private string ShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (!string.IsNullOrEmpty(ToolPrefix) && name.StartsWith(ToolPrefix, StringComparison.Ordinal))
                return name.Substring(ToolPrefix.Length);
            if (name.StartsWith("mcp__", StringComparison.Ordinal))
            {
                var parts = name.Split(new[] { "__" }, StringSplitOptions.None);
                if (parts.Length >= 3) return string.Join("__", parts.Skip(2));
            }
            return name;
        }

        private static JsonObject InputObject(JsonElement block)
        {
            try
            {
                if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                    return JsonNode.Parse(input.GetRawText()) as JsonObject ?? new JsonObject();
            }
            catch { }
            return new JsonObject();
        }

        private static string ResultText(JsonElement block)
        {
            if (!block.TryGetProperty("content", out var content)) return "";
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text" && part.TryGetProperty("text", out var tx))
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(tx.GetString());
                    }
                }
                return sb.ToString();
            }
            return "";
        }

        private static long Long(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : 0;

        private static void TryKill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }

        private static string Tail(string text, int lines)
        {
            var all = (text ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }

        /// <summary>The command-line args for a turn (tests): model, effort, context, tool allow/deny, permission tool.</summary>
        internal List<string> BuildArgsForTest(string message)
        {
            _sessionId ??= "test-session";
            McpConfigJson ??= "{}";
            return BuildCommand("claude", message).args;
        }

        /// <summary>Feed one stream-json line to the parser (tests). Returns the result text when the line is a result.</summary>
        internal string FeedLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line[0] != '{') return null;
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var t)) return null;
            if (t.GetString() == "result") return HandleResult(doc.RootElement);
            Handle(doc.RootElement);
            return null;
        }
    }
}
