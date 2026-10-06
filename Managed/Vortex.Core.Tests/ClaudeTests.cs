using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets.Library;
using Editor.Core.Assets.Store;
using Editor.Core.Claude;

namespace VortexTests
{
    /// <summary>Claude integration: the project's .mcp.json (v3.0.0) and the Claude sidebar's conversation on the official
    /// SDK (v3.0.3): request shape, thinking echo, Ask / Agent, models without effort, errors, the signed-in account.</summary>
    public static class ClaudeTests
    {
        private const string Url = "http://127.0.0.1:7420/mcp";

        [Test]
        public static void McpJsonIsCreated(TestContext t)
        {
            var dir = t.Path("p1");
            Directory.CreateDirectory(dir);
            t.Equal(McpProjectConfig.Outcome.Created, McpProjectConfig.Write(dir, "vortex", Url), "new file");
            using var doc = JsonDocument.Parse(File.ReadAllText(McpProjectConfig.PathFor(dir)));
            var v = doc.RootElement.GetProperty("mcpServers").GetProperty("vortex");
            t.Equal("http", v.GetProperty("type").GetString(), "type");
            t.Equal(Url, v.GetProperty("url").GetString(), "url");
            t.True(McpProjectConfig.IsConfigured(dir, "vortex", Url), "configured");
            t.Equal(McpProjectConfig.Outcome.Unchanged, McpProjectConfig.Write(dir, "vortex", Url), "second write changes nothing");
        }

        [Test]
        public static void McpJsonKeepsOtherServers(TestContext t)
        {
            var dir = t.Path("p2");
            t.Write("p2/.mcp.json", "{\n  // comment\n  \"mcpServers\": {\n    \"github\": { \"type\": \"http\", \"url\": \"https://example.com/mcp\" },\n" +
                                    "    \"vortex\": { \"type\": \"http\", \"url\": \"http://127.0.0.1:9999/mcp\", \"headers\": { \"X\": \"1\" } }\n  },\n  \"other\": true,\n}");
            t.Equal(McpProjectConfig.Outcome.Updated, McpProjectConfig.Write(dir, "vortex", Url), "port changed");
            using var doc = JsonDocument.Parse(File.ReadAllText(McpProjectConfig.PathFor(dir)));
            var servers = doc.RootElement.GetProperty("mcpServers");
            t.Equal("https://example.com/mcp", servers.GetProperty("github").GetProperty("url").GetString(), "other server kept");
            t.Equal(Url, servers.GetProperty("vortex").GetProperty("url").GetString(), "url updated");
            t.Equal("1", servers.GetProperty("vortex").GetProperty("headers").GetProperty("X").GetString(), "extra keys of the entry kept");
            t.True(doc.RootElement.GetProperty("other").GetBoolean(), "other top-level keys kept");
        }

        [Test]
        public static void McpJsonNeverOverwritesBrokenFiles(TestContext t)
        {
            var dir = t.Path("p3");
            var path = t.Write("p3/.mcp.json", "{ this is not json");
            bool threw = false;
            try { McpProjectConfig.Write(dir, "vortex", Url); } catch (InvalidDataException) { threw = true; }
            t.True(threw, "broken file is reported");
            t.Equal("{ this is not json", File.ReadAllText(path), "broken file untouched");
            t.Write("p3/.mcp.json", "{ \"mcpServers\": [] }");
            threw = false;
            try { McpProjectConfig.Write(dir, "vortex", Url); } catch (InvalidDataException) { threw = true; }
            t.True(threw, "mcpServers that is not an object is reported");
        }

        // ------------------------------------------------------------------ ClaudeSession (the Claude sidebar, official SDK)

        private const string Api = "https://api.anthropic.com/v1/messages";

        /// <summary>Sets environment variables for one test and puts the old values back.</summary>
        private sealed class EnvScope : IDisposable
        {
            private readonly Dictionary<string, string> _old = new Dictionary<string, string>();
            public EnvScope Set(string name, string value)
            {
                if (!_old.ContainsKey(name)) _old[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
                return this;
            }
            public void Dispose()
            {
                foreach (var kv in _old) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
                StoreKeys.Reload();
            }
        }

        /// <summary>A scripted Messages API, a test key, and an empty Anthropic config folder (the user's real profile is
        /// never read).</summary>
        private static (FakeHttp fake, EnvScope env) Api_(TestContext t, string key = "sk-ant-test")
        {
            Environment.SetEnvironmentVariable("VORTEX_ASSETDB_DIR", t.Path("lib"));
            GlobalAssetDatabase.ResetInstance();
            StoreHttp.CacheRootOverride = t.Path("cache");
            var env = new EnvScope()
                .Set("VORTEX_STORE_KEY_ANTHROPIC", key)
                .Set("ANTHROPIC_API_KEY", null).Set("ANTHROPIC_AUTH_TOKEN", null).Set("ANTHROPIC_PROFILE", null)
                .Set("ANTHROPIC_CONFIG_DIR", t.Path("anthropic-config")).Set("ANTHROPIC_BASE_URL", null);
            StoreKeys.Reload();
            var fake = new FakeHttp();
            StoreHttp.Handler = fake;
            return (fake, env);
        }

        private static string Ev(string json)
        {
            string type = (string)JsonNode.Parse(json)["type"];
            return "event: " + type + "\ndata: " + json + "\n\n";
        }

        private static HttpResponseMessage Stream(params string[] events) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(events.Select(Ev)), Encoding.UTF8, "text/event-stream"),
        };

        private static string Start(string model, int input, int cacheWrite = 0, int cacheRead = 0) =>
            "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"" + model + "\",\"content\":[]," +
            "\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":1,\"cache_creation_input_tokens\":" + cacheWrite +
            ",\"cache_read_input_tokens\":" + cacheRead + "}}}";

        private static string Stop(string reason, int output) =>
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + reason + "\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":" + output + "}}\u0001" +
            "{\"type\":\"message_stop\"}";

        private static string TextBlock(int index, string text) =>
            "{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\u0001" +
            "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(text) + "}}\u0001" +
            "{\"type\":\"content_block_stop\",\"index\":" + index + "}";

        private static string ToolBlock(int index, string id, string name, string inputJson) =>
            "{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"input\":{}}}\u0001" +
            "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(inputJson) + "}}\u0001" +
            "{\"type\":\"content_block_stop\",\"index\":" + index + "}";

        /// <summary>The events of a whole turn; blocks are joined with \u0001 so one helper can return several events.</summary>
        private static HttpResponseMessage Turn(params string[] parts) => Stream(parts.SelectMany(p => p.Split('\u0001')).ToArray());

        private static ClaudeSession Session() => new ClaudeSession
        {
            SystemPrompt = "You operate the Vortex editor.",
            Tools = new[]
            {
                new ChatTool { Name = "scene_outline", Description = "outline", InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}", ReadOnly = true },
                new ChatTool { Name = "create_entity", Description = "create", InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\"}},\"required\":[\"kind\"]}" },
            },
        };

        private static string Header(HttpRequestMessage r, string name) =>
            r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;

        /// <summary>The API rejects two messages of the same role in a row and a first message that is not the user's.</summary>
        private static void Alternates(TestContext t, JsonNode body, string what)
        {
            var roles = body["messages"].AsArray().Select(m => (string)m["role"]).ToList();
            for (int i = 0; i < roles.Count; i++)
                t.Equal(i % 2 == 0 ? "user" : "assistant", roles[i], what + ": roles alternate (" + string.Join(",", roles) + ")");
        }

        [Test]
        public static async Task SessionRunsTheToolLoopAndEchoesThinking(TestContext t)
        {
            var (fake, env) = Api_(t);
            using var _ = env;
            var bodies = new List<JsonNode>();
            var requests = new List<HttpRequestMessage>();
            fake.Routes.Add((Api, r =>
            {
                requests.Add(r);
                bodies.Add(JsonNode.Parse(r.Content.ReadAsStringAsync().Result));
                if (bodies.Count == 1)
                    return Turn(Start("claude-opus-5-5", 50, cacheWrite: 4000),
                        "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}",
                        "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Erst die Szene ansehen, \"}}",
                        "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"dann bauen.\"}}",
                        "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig-abc123\"}}",
                        "{\"type\":\"content_block_stop\",\"index\":0}",
                        TextBlock(1, "Ich schaue mir die Szene an."),
                        ToolBlock(2, "tu_1", "scene_outline", "{}"),
                        ToolBlock(3, "tu_2", "create_entity", "{\"kind\": \"cube\"}"),
                        Stop("tool_use", 40));
                return Turn(Start("claude-opus-5-5", 30, cacheRead: 4000), TextBlock(0, "Fertig: ein Würfel."), Stop("end_turn", 10));
            }));
            var session = Session();
            session.Effort = "xhigh";
            session.ContextSize = 200_000;
            var asked = new List<string>();
            var ran = new List<string>();
            var text = new StringBuilder();
            var thinking = new StringBuilder();
            int turns = 0;
            session.TextDelta += s => text.Append(s);
            session.ThinkingDelta += s => thinking.Append(s);
            session.TurnCompleted += () => turns++;
            session.Approve = (c, ct) => { asked.Add(c.Name); return Task.FromResult(ToolApproval.Allow); };
            session.RunTool = (c, ct) =>
            {
                ran.Add(c.Name + " " + c.InputJson);
                var r = new ChatToolResult { Text = c.Name == "create_entity" ? "{\"id\":\"ab12cd34\"}" : "Scene 'Yard'" };
                if (c.Name == "scene_outline") r.Images.Add((new byte[] { 1, 2, 3 }, "image/jpeg"));
                return Task.FromResult(r);
            };

            string answer = await session.SendAsync("Bau einen Würfel", CancellationToken.None);
            t.Equal("Fertig: ein Würfel.", answer, "final answer");
            t.Equal("Ich schaue mir die Szene an.Fertig: ein Würfel.", text.ToString(), "streamed text");
            t.Equal("Erst die Szene ansehen, dann bauen.", thinking.ToString(), "streamed thinking");
            t.Equal("create_entity", string.Join(",", asked), "only the tool that changes something asks");
            t.Equal("scene_outline {},create_entity {\"kind\":\"cube\"}", string.Join(",", ran), "both tools ran with their streamed input");
            t.Equal(2, bodies.Count, "two requests");
            t.Equal(2, turns, "two turns reported");

            // the request: model, effort, adaptive thinking shown as summaries, compaction at 80 % of the context, fallbacks, caching
            var first = bodies[0];
            t.Equal("claude-opus-5-5", (string)first["model"], "model");
            t.Equal(true, (bool)first["stream"], "streamed");
            t.Equal("adaptive", (string)first["thinking"]["type"], "adaptive thinking");
            t.Equal("summarized", (string)first["thinking"]["display"], "thinking summaries");
            t.Equal("xhigh", (string)first["output_config"]["effort"], "effort");
            var edit = first["context_management"]["edits"][0];
            t.Equal("compact_20260112", (string)edit["type"], "server-side compaction");
            t.Equal(160_000, (int)edit["trigger"]["value"], "compacts at 80 % of 200K");
            t.Equal("default", (string)first["fallbacks"], "refusal fallbacks");
            t.Equal("ephemeral", (string)first["cache_control"]["type"], "automatic cache point on the conversation");
            t.Equal("ephemeral", (string)first["tools"].AsArray().Last()["cache_control"]["type"], "tools cached");
            t.Equal("ephemeral", (string)first["system"][0]["cache_control"]["type"], "system cached");
            t.Equal("kind", (string)first["tools"][1]["input_schema"]["required"][0], "tool schema carries required");
            t.Equal(ClaudeSession.AgentNote, (string)first["messages"][0]["content"][0]["text"], "the first message names the mode");
            t.Equal("Bau einen Würfel", (string)first["messages"][0]["content"][1]["text"], "the user's text");
            string betas = Header(requests[0], "anthropic-beta") ?? "";
            t.True(betas.Contains("compact-2026-01-12") && betas.Contains("server-side-fallback-2026-07-01"), "beta headers: " + betas);
            t.Equal("sk-ant-test", Header(requests[0], "x-api-key"), "the user's key");

            // the next turn sends Claude's turn back unchanged: thinking with its signature, text, both tool calls
            var assistant = bodies[1]["messages"][1];
            t.Equal("assistant", (string)assistant["role"], "assistant turn");
            t.Equal("thinking", (string)assistant["content"][0]["type"], "thinking block first");
            t.Equal("Erst die Szene ansehen, dann bauen.", (string)assistant["content"][0]["thinking"], "thinking text unchanged");
            t.Equal("sig-abc123", (string)assistant["content"][0]["signature"], "signature echoed");
            t.Equal("tool_use", (string)assistant["content"][3]["type"], "tool call echoed");
            t.Equal("cube", (string)assistant["content"][3]["input"]["kind"], "tool input echoed");
            var results = bodies[1]["messages"][2]["content"].AsArray();
            t.Equal("tu_1", (string)results[0]["tool_use_id"], "first result");
            t.Equal("image", (string)results[0]["content"][1]["type"], "image in the tool result");
            t.Equal("AQID", (string)results[0]["content"][1]["source"]["data"], "base64 image data");
            t.Equal("tu_2", (string)results[1]["tool_use_id"], "second result");
            t.Equal(3, bodies[1]["messages"].AsArray().Count, "the history is append-only");

            t.Equal(4000, (int)session.CacheReadTokens, "cache reads counted");
            t.Equal(4000, (int)session.CacheWriteTokens, "cache writes counted");
            t.Equal(4030, (int)session.ContextTokens, "context of the last request");
            t.True(session.Cost > 0, "cost estimated");
        }

        [Test]
        public static async Task SessionAskModeChangesNothing(TestContext t)
        {
            var (fake, env) = Api_(t);
            using var _ = env;
            var bodies = new List<JsonNode>();
            fake.Routes.Add((Api, r =>
            {
                bodies.Add(JsonNode.Parse(r.Content.ReadAsStringAsync().Result));
                if (bodies.Count == 1)
                    return Turn(Start("claude-sonnet-5-5", 20), ToolBlock(0, "tu_a", "scene_outline", "{}"), ToolBlock(1, "tu_b", "create_entity", "{\"kind\":\"cube\"}"), Stop("tool_use", 5));
                return Turn(Start("claude-sonnet-5-5", 20), TextBlock(0, "Okay."), Stop("end_turn", 5));
            }));
            var session = Session();
            session.Model = ClaudeModels.Find("claude-sonnet-5-5");
            session.Mode = ClaudeMode.Ask;
            var ran = new List<string>();
            bool asked = false;
            ChatToolCall blocked = null;
            session.ToolFinished += c => { if (c.Name == "create_entity") blocked = c; };
            session.Approve = (c, ct) => { asked = true; return Task.FromResult(ToolApproval.Allow); };
            session.RunTool = (c, ct) => { ran.Add(c.Name); return Task.FromResult(new ChatToolResult { Text = "ok" }); };
            await session.SendAsync("Was ist in der Szene?", CancellationToken.None);
            t.Equal("scene_outline", string.Join(",", ran), "Ask mode runs only the read-only tool");
            t.False(asked, "nothing to approve in Ask mode");
            t.True(blocked != null && blocked.Denied, "the change is refused");
            var result = bodies[1]["messages"][2]["content"][1];
            t.Equal(true, (bool)result["is_error"], "refusal is an error result");
            t.True(((string)result["content"][0]["text"]).StartsWith("Ask mode", StringComparison.Ordinal), "Claude reads why");
            t.Equal(ClaudeSession.AskNote, (string)bodies[0]["messages"][0]["content"][0]["text"], "Ask note on the first message");
            t.Equal("high", (string)bodies[0]["output_config"]["effort"], "Sonnet 5.5's default effort");

            // switching to Agent: the next message says so, the tools and system prompt stay the same (append-only history)
            session.Mode = ClaudeMode.Agent;
            await session.SendAsync("Dann bau ihn.", CancellationToken.None);
            var third = bodies[2];
            var lastUser = third["messages"].AsArray()[4];
            t.Equal(ClaudeSession.AgentNote, (string)lastUser["content"][0]["text"], "Agent note when the mode changes");
            t.Equal(bodies[0]["tools"].ToJsonString(), third["tools"].ToJsonString(), "same tools in both modes");
            t.Equal(bodies[0]["system"].ToJsonString(), third["system"].ToJsonString(), "same system prompt");
            await session.SendAsync("Danke.", CancellationToken.None);
            t.Equal(1, bodies[3]["messages"].AsArray().Last()["content"].AsArray().Count, "no note while the mode stays");
        }

        [Test]
        public static async Task SessionHaikuSendsNoEffortOrThinking(TestContext t)
        {
            var (fake, env) = Api_(t);
            using var _ = env;
            JsonNode body = null;
            HttpRequestMessage request = null;
            fake.Routes.Add((Api, r =>
            {
                request = r;
                body = JsonNode.Parse(r.Content.ReadAsStringAsync().Result);
                return Turn(Start("claude-haiku-4-5", 10), TextBlock(0, "Hallo."), Stop("end_turn", 3));
            }));
            var session = Session();
            session.Model = ClaudeModels.Find("claude-haiku-4-5");
            session.Effort = "max";   // ignored: Haiku 4.5 takes no effort
            string answer = await session.SendAsync("Hi", CancellationToken.None);
            t.Equal("Hallo.", answer, "answer");
            t.Equal("claude-haiku-4-5", (string)body["model"], "model id");
            t.True(body["output_config"] == null, "no effort");
            t.True(body["thinking"] == null, "no thinking");
            t.True(body["context_management"] == null, "no compaction");
            t.True(body["fallbacks"] == null, "no fallbacks");
            string betas = Header(request, "anthropic-beta") ?? "";
            t.False(betas.Contains("compact-2026-01-12") || betas.Contains("server-side-fallback"), "no beta headers: " + betas);
            t.Equal(null, session.EffectiveEffort, "effective effort");
        }

        [Test]
        public static async Task SessionReportsDeniedToolsAndApiErrors(TestContext t)
        {
            var (fake, env) = Api_(t);
            using var _ = env;
            int call = 0;
            JsonNode second = null;
            fake.Routes.Add((Api, r =>
            {
                string body = r.Content.ReadAsStringAsync().Result;
                if (++call == 2) second = JsonNode.Parse(body);
                if (call == 1) return Turn(Start("claude-opus-5-5", 5), ToolBlock(0, "tu_9", "create_entity", "{\"kind\":\"cube\"}"), Stop("tool_use", 5));
                if (call == 2) return Turn(Start("claude-opus-5-5", 5), TextBlock(0, "Okay, nichts geändert."), Stop("end_turn", 5));
                return new HttpResponseMessage((HttpStatusCode)401)
                {
                    Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}", Encoding.UTF8, "application/json"),
                };
            }));
            var session = Session();
            bool ran = false;
            session.Approve = (c, ct) => Task.FromResult(ToolApproval.Deny);
            session.RunTool = (c, ct) => { ran = true; return Task.FromResult(new ChatToolResult { Text = "x" }); };
            string answer = await session.SendAsync("Lösch alles", CancellationToken.None);
            t.False(ran, "a denied tool does not run");
            t.Equal("Okay, nichts geändert.", answer, "Claude answers after the denial");
            var result = second["messages"].AsArray().Last()["content"][0];
            t.Equal(true, (bool)result["is_error"], "denial is an error result");
            t.True(((string)result["content"][0]["text"]).Contains("denied"), "denial explained to the model");
            ClaudeChatException error = null;
            try { await session.SendAsync("noch mal", CancellationToken.None); }
            catch (ClaudeChatException ex) { error = ex; }
            t.True(error != null && error.Status == HttpStatusCode.Unauthorized && error.Message.Contains("sign in again"), "a 401 becomes a readable error: " + error?.Message);
        }

        [Test]
        public static async Task SessionAnswersCutOffToolCallsAndDropsRefusals(TestContext t)
        {
            var (fake, env) = Api_(t);
            using var _ = env;
            var bodies = new List<JsonNode>();
            fake.Routes.Add((Api, r =>
            {
                bodies.Add(JsonNode.Parse(r.Content.ReadAsStringAsync().Result));
                switch (bodies.Count)
                {
                    case 1: return Turn(Start("claude-opus-5-5", 5), TextBlock(0, "Ich baue"), ToolBlock(1, "tu_cut", "create_entity", "{\"kind\":\"cube\"}"), Stop("max_tokens", 9));
                    case 2:
                        return Turn(Start("claude-opus-5-5", 5), TextBlock(0, "Teil"),
                            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"refusal\",\"stop_sequence\":null,\"stop_details\":{\"type\":\"refusal\",\"category\":\"cyber\",\"explanation\":\"Not this.\"}},\"usage\":{\"output_tokens\":3}}",
                            "{\"type\":\"message_stop\"}");
                    default: return Turn(Start("claude-opus-5-5", 5), TextBlock(0, "Gern."), Stop("end_turn", 2));
                }
            }));
            var session = Session();
            bool ran = false;
            session.RunTool = (c, ct) => { ran = true; return Task.FromResult(new ChatToolResult { Text = "x" }); };
            string first = await session.SendAsync("Bau was", CancellationToken.None);
            t.False(ran, "a call cut off by the output limit does not run");
            t.True(first.Contains("output limit"), "the user hears why it stopped");
            string refused = null;
            session.Refused += why => refused = why;
            await session.SendAsync("weiter", CancellationToken.None);
            var second = bodies[1]["messages"].AsArray().Last()["content"].AsArray();
            t.Equal("tool_result", (string)second[0]["type"], "the cut-off call is answered first");
            t.Equal("tu_cut", (string)second[0]["tool_use_id"], "for the right call");
            t.Equal("weiter", (string)second.Last()["text"], "then the user's text");
            t.Equal("Not this.", refused, "the refusal is reported");
            await session.SendAsync("Etwas anderes", CancellationToken.None);
            var third = bodies[2]["messages"].AsArray();
            t.False(third.Any(m => (string)m["role"] == "assistant" && m["content"].AsArray().Any(b => (string)b["text"] == "Teil")), "the refused partial turn is not in the history");
            t.Equal("Etwas anderes", (string)third.Last()["content"].AsArray().Last()["text"], "the conversation continues");
            foreach (var b in bodies) Alternates(t, b, "after a cut-off turn and a refusal");
        }

        [Test]
        public static async Task SessionRetriesAndJoinsTheOpenUserTurn(TestContext t)
        {
            var (fake, env) = Api_(t);
            using var _ = env;
            var bodies = new List<JsonNode>();
            fake.Routes.Add((Api, r =>
            {
                bodies.Add(JsonNode.Parse(r.Content.ReadAsStringAsync().Result));
                if (bodies.Count == 1 || bodies.Count == 3)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"try again\"}}", Encoding.UTF8, "application/json"),
                    };
                return Turn(Start("claude-opus-5-5", 5), TextBlock(0, "Antwort " + bodies.Count), Stop("end_turn", 2));
            }));
            var session = Session();
            bool failed = false;
            try { await session.SendAsync("Erste Frage", CancellationToken.None); } catch (ClaudeChatException) { failed = true; }
            t.True(failed && session.CanRetry, "a failed request can be retried");
            t.Equal("Antwort 2", await session.RetryAsync(CancellationToken.None), "the retry answers");
            t.Equal(1, bodies[1]["messages"].AsArray().Count, "the retry sends the same single user turn");
            t.False(session.CanRetry, "nothing to retry after an answer");

            failed = false;
            try { await session.SendAsync("Zweite Frage", CancellationToken.None); } catch (ClaudeChatException) { failed = true; }
            t.True(failed, "the second request fails");
            await session.SendAsync("Andere Frage", CancellationToken.None);
            var last = bodies[3]["messages"].AsArray().Last()["content"].AsArray();
            t.Equal("Zweite Frage", (string)last[last.Count - 2]["text"], "the failed message stays in the open user turn");
            t.Equal("Andere Frage", (string)last[last.Count - 1]["text"], "the new message joins it");
            foreach (var b in bodies) Alternates(t, b, "after failures");
        }

        [Test]
        public static async Task SessionUsesTheSignedInAnthropicAccount(TestContext t)
        {
            // what `ant auth login` leaves behind: the active profile's config and its OAuth tokens
            var (fake, env) = Api_(t, key: null);
            using var _ = env;
            string dir = t.Path("anthropic-config");
            t.Write("anthropic-config/active_config", "default");
            t.Write("anthropic-config/configs/default.json", "{\"authentication\":{\"type\":\"user_oauth\",\"client_id\":\"test-client\"},\"workspace_id\":\"wrkspc_test\"}");
            long expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            string creds = t.Write("anthropic-config/credentials/default.json",
                "{\"version\":\"1\",\"type\":\"oauth_token\",\"access_token\":\"tok-123\",\"expires_at\":" + expires + ",\"refresh_token\":\"refresh-1\"}");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(creds, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            t.Equal(ClaudeCredential.AnthropicAccount, ClaudeAccount.Current, "the signed-in account is used");
            t.Equal(dir, ClaudeAccount.ConfigDir, "config folder from ANTHROPIC_CONFIG_DIR");

            HttpRequestMessage request = null;
            fake.Routes.Add((Api, r =>
            {
                request = r;
                return Turn(Start("claude-opus-5-5", 5), TextBlock(0, "Hallo."), Stop("end_turn", 2));
            }));
            string answer = await Session().SendAsync("Hi", CancellationToken.None);
            t.Equal("Hallo.", answer, "answer");
            t.Equal("Bearer tok-123", Header(request, "Authorization"), "the profile's token");
            t.True(Header(request, "x-api-key") == null, "no API key sent");
            t.True((Header(request, "anthropic-beta") ?? "").Contains("oauth-2025-04-20"), "OAuth beta header");

            // an API key in the editor wins over the account (and is what the panel shows)
            env.Set("VORTEX_STORE_KEY_ANTHROPIC", "sk-ant-other");
            StoreKeys.Reload();
            t.Equal(ClaudeCredential.ApiKey, ClaudeAccount.Current, "the editor's key wins");
        }
    }
}
