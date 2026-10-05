using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets.Store;

namespace Editor.Core.Claude
{
    /// <summary>A tool Claude may call: name, description, JSON input schema.</summary>
    public sealed class ChatTool
    {
        public string Name;
        public string Description;
        /// <summary>JSON Schema of the input (an object schema).</summary>
        public string InputSchemaJson;
        /// <summary>Read-only tools run without asking.</summary>
        public bool ReadOnly;
    }

    /// <summary>One tool call in a conversation, as the UI shows it.</summary>
    public sealed class ChatToolCall
    {
        public string Id;
        public string Name;
        public JsonObject Input;
        public string InputJson => Input?.ToJsonString() ?? "{}";
        public ChatToolResult Result;
        /// <summary>The user refused the call.</summary>
        public bool Denied;
    }

    /// <summary>What a tool returned: text and images; IsError for failures the model should read.</summary>
    public sealed class ChatToolResult
    {
        public string Text;
        public List<(byte[] data, string mimeType)> Images = new List<(byte[] data, string mimeType)>();
        public bool IsError;
    }

    public enum ToolApproval { Allow, Deny }

    public sealed class ClaudeChatException : Exception
    {
        public HttpStatusCode? Status { get; }
        public ClaudeChatException(string message, HttpStatusCode? status = null) : base(message) { Status = status; }
    }

    /// <summary>
    /// A Claude conversation with tools over the Messages API (plain HTTP, streamed SSE, the user's own key) — the
    /// engine of the embedded Claude panel (#92). <see cref="SendAsync"/> runs the tool-use loop: stream a turn, run
    /// the requested tools (asking <see cref="Approve"/> first for tools that change something), send the results,
    /// repeat until Claude answers without tools. Tool schemas and the system prompt are prompt-cached, so the 60-odd
    /// tool definitions are paid for once per conversation, not per turn.
    /// </summary>
    public sealed class ClaudeChat
    {
        public const string ApiUrl = "https://api.anthropic.com/v1/messages";
        public static readonly string[] Models = { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5-20251001" };

        public string Model = Models[0];
        public string SystemPrompt;
        public IReadOnlyList<ChatTool> Tools = Array.Empty<ChatTool>();
        public int MaxTokens = 8192;
        /// <summary>Tool rounds per user message before Claude has to stop and report.</summary>
        public int MaxRounds = 40;
        /// <summary>The key; default: the user's Anthropic key from the key store.</summary>
        public Func<string> Key = () => StoreKeys.Get("anthropic");

        /// <summary>Runs a tool (on whatever thread the host wants — the editor marshals to its UI thread).</summary>
        public Func<ChatToolCall, CancellationToken, Task<ChatToolResult>> RunTool;
        /// <summary>Asked before a tool that is not read-only runs; null = allow everything.</summary>
        public Func<ChatToolCall, CancellationToken, Task<ToolApproval>> Approve;

        public event Action<string> TextDelta;
        /// <summary>A new assistant text block begins (the UI starts a new paragraph).</summary>
        public event Action TextStarted;
        public event Action<ChatToolCall> ToolStarted;
        public event Action<ChatToolCall> ToolFinished;

        public int InputTokens { get; private set; }
        public int OutputTokens { get; private set; }
        public int CacheReadTokens { get; private set; }
        public int CacheWriteTokens { get; private set; }

        private readonly JsonArray _messages = new JsonArray();
        public int MessageCount => _messages.Count;

        /// <summary>Forget the conversation.</summary>
        public void Reset()
        {
            _messages.Clear();
            InputTokens = OutputTokens = CacheReadTokens = CacheWriteTokens = 0;
        }

        /// <summary>Send a user message and run the tool loop until Claude is done. Returns the final assistant text.</summary>
        public async Task<string> SendAsync(string userText, CancellationToken ct)
        {
            string key = Key?.Invoke();
            if (string.IsNullOrWhiteSpace(key)) throw new ClaudeChatException("Add your Anthropic API key first (the key button in the Claude panel).");
            if (string.IsNullOrWhiteSpace(userText)) return "";
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = userText }) });
            try { return await LoopAsync(key, ct).ConfigureAwait(false); }
            catch
            {
                // stopped or failed between a tool request and its results: answer the open calls, so the conversation
                // stays valid for the next message (consecutive user turns are fine, unanswered tool_use is not)
                RepairPendingToolUses();
                throw;
            }
        }

        private void RepairPendingToolUses()
        {
            if (_messages.Count == 0 || (string)_messages[_messages.Count - 1]["role"] != "assistant") return;
            var open = (_messages[_messages.Count - 1]["content"] as JsonArray)?.OfType<JsonObject>().Where(b => (string)b["type"] == "tool_use").ToList();
            if (open == null || open.Count == 0) return;
            var results = new JsonArray();
            foreach (var b in open)
                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = (string)b["id"], ["is_error"] = true, ["content"] = "Stopped by the user before this call ran." });
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }

        private async Task<string> LoopAsync(string key, CancellationToken ct)
        {
            string lastText = "";
            for (int round = 0; round < MaxRounds; round++)
            {
                var (assistant, stop) = await StreamTurnAsync(key, ct).ConfigureAwait(false);
                if (assistant.Count == 0) assistant.Add(new JsonObject { ["type"] = "text", ["text"] = "(no answer)" });
                _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistant });
                var texts = assistant.OfType<JsonObject>().Where(b => (string)b["type"] == "text").Select(b => (string)b["text"]).ToList();
                if (texts.Count > 0) lastText = string.Join("\n", texts);
                if (stop != "tool_use") return lastText;

                var results = new JsonArray();
                var pending = new JsonObject { ["role"] = "user", ["content"] = results };
                foreach (var block in assistant.OfType<JsonObject>().Where(b => (string)b["type"] == "tool_use").ToList())
                {
                    if (ct.IsCancellationRequested)
                    {
                        results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = (string)block["id"], ["is_error"] = true, ["content"] = "Stopped by the user before this call ran." });
                        continue;
                    }
                    var call = new ChatToolCall { Id = (string)block["id"], Name = (string)block["name"], Input = block["input"] as JsonObject ?? new JsonObject() };
                    var tool = Tools.FirstOrDefault(t => t.Name == call.Name);
                    try { ToolStarted?.Invoke(call); } catch { }
                    ToolApproval approval = ToolApproval.Allow;
                    if (tool != null && !tool.ReadOnly && Approve != null)
                    {
                        try { approval = await Approve(call, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { approval = ToolApproval.Deny; }
                    }
                    if (tool == null) call.Result = new ChatToolResult { IsError = true, Text = "Unknown tool " + call.Name + "." };
                    else if (ct.IsCancellationRequested) call.Result = new ChatToolResult { IsError = true, Text = "Stopped by the user before this call ran." };
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
                    try { ToolFinished?.Invoke(call); } catch { }
                    results.Add(ToolResultBlock(call));
                }
                _messages.Add(pending);
                ct.ThrowIfCancellationRequested();
            }
            // the round budget is spent
            return lastText + "\n\n(Stopped after " + MaxRounds + " tool rounds — say \"continue\" to go on.)";
        }

        private static JsonObject ToolResultBlock(ChatToolCall call)
        {
            var r = call.Result ?? new ChatToolResult { Text = "ok" };
            var content = new JsonArray();
            if (!string.IsNullOrEmpty(r.Text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = r.Text });
            foreach (var (data, mime) in r.Images)
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = Convert.ToBase64String(data) },
                });
            if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = "ok" });
            var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = call.Id, ["content"] = content };
            if (r.IsError) block["is_error"] = true;
            return block;
        }

        /// <summary>The request body of the next turn: cached system + tools, the conversation with a rolling cache
        /// point on the newest user message, streaming on.</summary>
        internal JsonObject BuildRequest()
        {
            var tools = new JsonArray();
            for (int i = 0; i < Tools.Count; i++)
            {
                var t = Tools[i];
                var o = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description ?? "",
                    ["input_schema"] = JsonNode.Parse(string.IsNullOrEmpty(t.InputSchemaJson) ? "{\"type\":\"object\",\"properties\":{}}" : t.InputSchemaJson),
                };
                if (i == Tools.Count - 1) o["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                tools.Add(o);
            }
            var messages = JsonNode.Parse(_messages.ToJsonString()).AsArray();
            // rolling cache point: the last block of the newest user message (the whole history before it is reused)
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if ((string)messages[i]["role"] != "user") continue;
                if (messages[i]["content"] is JsonArray blocks && blocks.Count > 0 && blocks[blocks.Count - 1] is JsonObject last)
                    last["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                break;
            }
            var body = new JsonObject
            {
                ["model"] = Model,
                ["max_tokens"] = MaxTokens,
                ["stream"] = true,
                ["messages"] = messages,
            };
            if (!string.IsNullOrEmpty(SystemPrompt))
                body["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = SystemPrompt, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } });
            if (tools.Count > 0) body["tools"] = tools;
            return body;
        }

        private async Task<(JsonArray content, string stop)> StreamTurnAsync(string key, CancellationToken ct)
        {
            var body = BuildRequest();
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await StoreHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string msg = null;
                try { msg = JsonNode.Parse(err)?["error"]?["message"]?.ToString(); } catch { }
                int code = (int)resp.StatusCode;
                string why = code == 401 ? "the Anthropic API key was rejected"
                           : code == 429 ? "rate limited — wait a moment and try again"
                           : code == 529 || code == 503 ? "the API is overloaded — try again in a moment"
                           : msg ?? ("HTTP " + code);
                throw new ClaudeChatException("Claude: " + why + ".", resp.StatusCode);
            }
            var blocks = new SortedDictionary<int, JsonObject>();
            var partial = new Dictionary<int, StringBuilder>();
            string stop = null;
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                JsonNode ev;
                try { ev = JsonNode.Parse(line.Substring(5).Trim()); } catch { continue; }
                switch ((string)ev?["type"])
                {
                    case "message_start":
                    {
                        var u = ev["message"]?["usage"];
                        InputTokens += (int?)u?["input_tokens"] ?? 0;
                        CacheReadTokens += (int?)u?["cache_read_input_tokens"] ?? 0;
                        CacheWriteTokens += (int?)u?["cache_creation_input_tokens"] ?? 0;
                        break;
                    }
                    case "content_block_start":
                    {
                        int i = (int)ev["index"];
                        var cb = (JsonObject)JsonNode.Parse(ev["content_block"].ToJsonString());
                        blocks[i] = cb;
                        partial[i] = new StringBuilder();
                        if ((string)cb["type"] == "text") { try { TextStarted?.Invoke(); } catch { } }
                        break;
                    }
                    case "content_block_delta":
                    {
                        int i = (int)ev["index"];
                        var delta = ev["delta"];
                        switch ((string)delta?["type"])
                        {
                            case "text_delta":
                                string t = (string)delta["text"];
                                blocks[i]["text"] = ((string)blocks[i]["text"] ?? "") + t;
                                try { TextDelta?.Invoke(t); } catch { }
                                break;
                            case "input_json_delta":
                                partial[i].Append((string)delta["partial_json"]);
                                break;
                        }
                        break;
                    }
                    case "content_block_stop":
                    {
                        int i = (int)ev["index"];
                        if (blocks.TryGetValue(i, out var b) && (string)b["type"] == "tool_use")
                        {
                            string json = partial[i].ToString();
                            try { b["input"] = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json); }
                            catch { b["input"] = new JsonObject(); }
                        }
                        break;
                    }
                    case "message_delta":
                        stop = (string)ev["delta"]?["stop_reason"] ?? stop;
                        OutputTokens += (int?)ev["usage"]?["output_tokens"] ?? 0;
                        break;
                    case "error":
                        throw new ClaudeChatException("Claude: " + (ev["error"]?["message"]?.ToString() ?? "stream error") + ".");
                }
            }
            var content = new JsonArray();
            foreach (var kv in blocks)
            {
                // empty text blocks are rejected by the API when sent back
                if ((string)kv.Value["type"] == "text" && string.IsNullOrEmpty((string)kv.Value["text"])) continue;
                content.Add(kv.Value);
            }
            return (content, stop);
        }
    }
}
