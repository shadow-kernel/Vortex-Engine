using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets.Store;

namespace Editor.Core.Audio.SoundStudio
{
    /// <summary>
    /// Claude as the sound designer of the Sound Studio (#81). Claude cannot output audio — it turns a terse idea
    /// ("scary door", "dumpfer, mehr Hall") into detailed, sound-design-literate prompts and calls the
    /// <c>generate_sound</c> tool once per take; the tool runs the selected backend and returns the result, and the
    /// conversation continues so feedback refines the last prompt instead of starting over. Plain HTTP against the
    /// Messages API (streamed, tool-use loop) with the user's own Anthropic key; token use is counted per session.
    /// </summary>
    public sealed class SoundDesigner
    {
        public const string ApiUrl = "https://api.anthropic.com/v1/messages";
        public static readonly string[] Models = { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5-20251001" };

        public string Model = Models[0];
        public ISoundBackend Backend;
        public string OutDir;
        /// <summary>Duration / loop / influence the user set (Claude may change them per take).</summary>
        public SoundRequest Defaults = new SoundRequest();
        public int InputTokens { get; private set; }
        public int OutputTokens { get; private set; }
        /// <summary>The user's first idea (recipe "input").</summary>
        public string FirstInput { get; private set; }
        /// <summary>Iteration notes: the user's feedback and Claude's take descriptions (recipe "notes").</summary>
        public readonly List<string> Notes = new List<string>();

        /// <summary>Streamed text from Claude (any thread).</summary>
        public event Action<string> TextDelta;
        /// <summary>A take finished generating (any thread).</summary>
        public event Action<GeneratedSound> TakeReady;
        /// <summary>Progress line ("Generating take 2…").</summary>
        public event Action<string> Status;

        private readonly JsonArray _messages = new JsonArray();
        public int Turns => _messages.Count;

        public const string SystemPrompt =
@"You are the sound designer inside the Vortex game engine's Sound Studio. You cannot output audio yourself: you write prompts for a text-to-audio model and call the generate_sound tool once per take.

Writing a prompt:
- Name the source and its materials (what makes the sound), the action and its timing (attack, sustain, decay), the space (room size, surfaces, reverb), the perspective and distance (close, across the room, far outside) and the texture and mood.
- Layer elements: ""a deep wooden thud, a short dry crack on top, a faint metallic rattle as it settles"".
- Sound effects are not music: avoid musical terms unless the user asks for music or an ambience bed.
- Keep each prompt under 400 characters and always write prompts in English, even when the user writes German.

Durations: UI clicks 0.3-1 s, impacts and footsteps 0.5-2 s, gunshots 1-3 s, stingers 2-5 s, ambience 10-30 s with loop=true. Leave duration_seconds out when unsure and the model picks.

Takes: when asked for several takes, make them meaningfully different (material, distance, intensity, timing, space) - not paraphrases. Give every take a short label in the user's language.

Iterating: when the user gives feedback (""duller, more reverb, shorter"" / ""dumpfer, mehr Hall, kürzer""), rewrite the best previous prompt accordingly (duller = muffled, fewer highs; more reverb = bigger, harder space; shorter = lower duration_seconds) and generate again. Say in one sentence what you changed.

The engine is used for horror games and tactical shooters in the style of Call of Duty: default to gritty, realistic, cinematic sound.";

        public static JsonObject ToolSchema(ISoundBackend b)
        {
            var props = new JsonObject
            {
                ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "Detailed English sound-effect prompt (max 400 characters)." },
                ["label"] = new JsonObject { ["type"] = "string", ["description"] = "One-line description of this take for the user, in the user's language." },
                ["duration_seconds"] = new JsonObject { ["type"] = "number", ["description"] = "Length in seconds (0.5-" + b.MaxSeconds.ToString(CultureInfo.InvariantCulture) + "). Omit to let the model choose." },
            };
            if (b.SupportsLoop) props["loop"] = new JsonObject { ["type"] = "boolean", ["description"] = "Seamless loop (ambience)." };
            if (b.SupportsInfluence) props["prompt_influence"] = new JsonObject { ["type"] = "number", ["description"] = "0-1: how literally to follow the prompt (default 0.3; higher = more literal, less variation)." };
            return new JsonObject
            {
                ["name"] = "generate_sound",
                ["description"] = "Generate one take of a sound effect with the user's selected backend (" + b.Name + "). Call it once per take.",
                ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray("prompt", "label") },
            };
        }

        /// <summary>Send the user's idea / feedback, let Claude generate takes, return them.</summary>
        public async Task<List<GeneratedSound>> SendAsync(string userText, int takes, CancellationToken ct)
        {
            if (Backend == null) throw new InvalidOperationException("No backend selected.");
            string key = StoreKeys.Get("anthropic");
            if (string.IsNullOrWhiteSpace(key)) throw new SoundGenException("Add your Anthropic API key first (Keys…) — or switch off \"Claude designs the prompts\".");
            if (FirstInput == null) FirstInput = userText;
            Notes.Add("user: " + userText);
            string content = _messages.Count == 0
                ? "Idea: " + userText + "\n\nMake " + takes + " take" + (takes == 1 ? "" : "s") + ". Backend: " + Backend.Name + " (max " + Backend.MaxSeconds.ToString(CultureInfo.InvariantCulture) + " s" +
                  (Backend.SupportsLoop ? ", loop supported" : ", no loop") + (Backend.SupportsInfluence ? ", prompt_influence supported" : "") + ")." +
                  (Defaults.DurationSeconds.HasValue ? " The user set the length to " + Defaults.DurationSeconds.Value.ToString("0.#", CultureInfo.InvariantCulture) + " s." : "") +
                  (Defaults.Loop ? " The user wants a seamless loop." : "")
                : userText + "\n\n(" + takes + " take" + (takes == 1 ? "" : "s") + ")";
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });

            var result = new List<GeneratedSound>();
            for (int round = 0; round < 6; round++)
            {
                var (assistant, stop) = await StreamTurnAsync(key, ct).ConfigureAwait(false);
                _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistant });
                if (stop != "tool_use") break;
                var toolResults = new JsonArray();
                foreach (var block in assistant.OfType<JsonObject>().Where(b => (string)b["type"] == "tool_use"))
                {
                    string id = (string)block["id"];
                    var input = block["input"] as JsonObject ?? new JsonObject();
                    var req = Defaults.Clone();
                    req.Prompt = (string)input["prompt"] ?? Defaults.Prompt;
                    req.Label = (string)input["label"];
                    if (input["duration_seconds"] is JsonValue dv && dv.TryGetValue(out double d)) req.DurationSeconds = Math.Max(0.5, Math.Min(Backend.MaxSeconds, d));
                    if (input["loop"] is JsonValue lv && lv.TryGetValue(out bool loop)) req.Loop = loop && Backend.SupportsLoop;
                    if (input["prompt_influence"] is JsonValue iv && iv.TryGetValue(out double inf)) req.PromptInfluence = Math.Max(0, Math.Min(1, inf));
                    req.Seed = result.Count + Turns * 17;
                    Status?.Invoke("Generating “" + (req.Label ?? "take") + "”…");
                    try
                    {
                        var g = await Backend.GenerateAsync(req, OutDir, ct).ConfigureAwait(false);
                        g.Label = req.Label;
                        result.Add(g);
                        Notes.Add("take: " + req.Label + " — " + req.Prompt);
                        try { TakeReady?.Invoke(g); } catch { }
                        toolResults.Add(new JsonObject
                        {
                            ["type"] = "tool_result", ["tool_use_id"] = id,
                            ["content"] = "Generated take " + result.Count + " (" + (req.DurationSeconds?.ToString("0.#", CultureInfo.InvariantCulture) ?? "auto") + " s, cost " + g.CostNote + "). The user can audition it now.",
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        toolResults.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["is_error"] = true, ["content"] = ex.Message });
                    }
                }
                _messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
            }
            return result;
        }

        /// <summary>One streamed Messages API call: returns the assistant content blocks and the stop reason.</summary>
        private async Task<(JsonArray content, string stop)> StreamTurnAsync(string key, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = Model,
                ["max_tokens"] = 2048,
                ["system"] = SystemPrompt,
                ["stream"] = true,
                ["tools"] = new JsonArray(ToolSchema(Backend)),
                ["messages"] = JsonNode.Parse(_messages.ToJsonString()),
            };
            using (var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl))
            {
                req.Headers.TryAddWithoutValidation("x-api-key", key);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using (var resp = await StoreHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        string msg = null;
                        try { msg = JsonNode.Parse(err)?["error"]?["message"]?.ToString(); } catch { }
                        throw new SoundGenException("Claude: " + ((int)resp.StatusCode == 401 ? "the Anthropic API key was rejected" : msg ?? ("HTTP " + (int)resp.StatusCode)) + ".", resp.StatusCode);
                    }
                    var blocks = new Dictionary<int, JsonObject>();
                    var partial = new Dictionary<int, StringBuilder>();
                    string stop = null;
                    using (var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                            JsonNode ev;
                            try { ev = JsonNode.Parse(line.Substring(5).Trim()); } catch { continue; }
                            string type = (string)ev?["type"];
                            switch (type)
                            {
                                case "message_start":
                                    InputTokens += (int?)ev["message"]?["usage"]?["input_tokens"] ?? 0;
                                    break;
                                case "content_block_start":
                                    {
                                        int i = (int)ev["index"];
                                        blocks[i] = (JsonObject)JsonNode.Parse(ev["content_block"].ToJsonString());
                                        partial[i] = new StringBuilder();
                                        break;
                                    }
                                case "content_block_delta":
                                    {
                                        int i = (int)ev["index"];
                                        var delta = ev["delta"];
                                        string dt = (string)delta?["type"];
                                        if (dt == "text_delta")
                                        {
                                            string t = (string)delta["text"];
                                            blocks[i]["text"] = ((string)blocks[i]["text"] ?? "") + t;
                                            try { TextDelta?.Invoke(t); } catch { }
                                        }
                                        else if (dt == "input_json_delta") partial[i].Append((string)delta["partial_json"]);
                                        break;
                                    }
                                case "content_block_stop":
                                    {
                                        int i = (int)ev["index"];
                                        if ((string)blocks[i]["type"] == "tool_use")
                                        {
                                            string json = partial[i].ToString();
                                            blocks[i]["input"] = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json);
                                        }
                                        break;
                                    }
                                case "message_delta":
                                    stop = (string)ev["delta"]?["stop_reason"] ?? stop;
                                    OutputTokens += (int?)ev["usage"]?["output_tokens"] ?? 0;
                                    break;
                                case "error":
                                    throw new SoundGenException("Claude: " + (ev["error"]?["message"]?.ToString() ?? "stream error") + ".");
                            }
                        }
                    }
                    var content = new JsonArray();
                    foreach (var kv in blocks.OrderBy(k => k.Key)) content.Add(kv.Value);
                    return (content, stop);
                }
            }
        }

        /// <summary>Forget the conversation (a new idea).</summary>
        public void Reset()
        {
            _messages.Clear();
            Notes.Clear();
            FirstInput = null;
        }
    }
}
