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
    /// <summary>Claude integration (milestone v3.0.0): the project's .mcp.json, the embedded panel's chat loop.</summary>
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

        // ------------------------------------------------------------------ ClaudeChat (embedded panel, #92)

        private static FakeHttp Fake(TestContext t)
        {
            Environment.SetEnvironmentVariable("VORTEX_ASSETDB_DIR", t.Path("lib"));
            GlobalAssetDatabase.ResetInstance();
            StoreHttp.CacheRootOverride = t.Path("cache");
            var fake = new FakeHttp();
            StoreHttp.Handler = fake;
            return fake;
        }

        private static HttpResponseMessage Sse(params string[] events) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Join("", events.Select(e => "event: x\ndata: " + e + "\n\n")), Encoding.UTF8, "text/event-stream"),
        };

        private static ClaudeChat Chat() => new ClaudeChat
        {
            Key = () => "sk-ant-test",
            SystemPrompt = "You operate the Vortex editor.",
            Tools = new[]
            {
                new ChatTool { Name = "scene_outline", Description = "outline", InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}", ReadOnly = true },
                new ChatTool { Name = "create_entity", Description = "create", InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\"}}}" },
            },
        };

        [Test]
        public static async Task ChatRunsTheToolLoopWithApprovalAndImages(TestContext t)
        {
            var fake = Fake(t);
            var bodies = new List<JsonNode>();
            int call = 0;
            fake.Routes.Add((ClaudeChat.ApiUrl, r =>
            {
                bodies.Add(JsonNode.Parse(r.Content.ReadAsStringAsync().Result));
                if (++call == 1)
                    return Sse("{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":50,\"cache_creation_input_tokens\":4000}}}",
                               "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                               "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Ich schaue mir die Szene an.\"}}",
                               "{\"type\":\"content_block_stop\",\"index\":0}",
                               "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_1\",\"name\":\"scene_outline\",\"input\":{}}}",
                               "{\"type\":\"content_block_stop\",\"index\":1}",
                               "{\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_2\",\"name\":\"create_entity\",\"input\":{}}}",
                               "{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"kind\\\": \\\"cu\"}}",
                               "{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"be\\\"}\"}}",
                               "{\"type\":\"content_block_stop\",\"index\":2}",
                               "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":40}}");
                return Sse("{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":30,\"cache_read_input_tokens\":4000}}}",
                           "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                           "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Fertig: ein Würfel.\"}}",
                           "{\"type\":\"content_block_stop\",\"index\":0}",
                           "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":10}}");
            }));
            var chat = Chat();
            var asked = new List<string>();
            var ran = new List<string>();
            var streamed = new StringBuilder();
            chat.TextDelta += s => streamed.Append(s);
            chat.Approve = (c, ct) => { asked.Add(c.Name); return Task.FromResult(ToolApproval.Allow); };
            chat.RunTool = (c, ct) =>
            {
                ran.Add(c.Name + " " + c.InputJson);
                var r = new ChatToolResult { Text = c.Name == "create_entity" ? "{\"id\":\"ab12cd34\"}" : "Scene 'Yard'" };
                if (c.Name == "scene_outline") r.Images.Add((new byte[] { 1, 2, 3 }, "image/jpeg"));
                return Task.FromResult(r);
            };
            string answer = await chat.SendAsync("Bau einen Würfel", CancellationToken.None);
            t.Equal("Fertig: ein Würfel.", answer, "final answer");
            t.Equal("Ich schaue mir die Szene an.Fertig: ein Würfel.", streamed.ToString(), "streamed text");
            t.Equal("create_entity", string.Join(",", asked), "only the mutating tool asks for approval");
            t.Equal("scene_outline {},create_entity {\"kind\":\"cube\"}", string.Join(",", ran), "both tools ran with their streamed input");
            t.Equal(2, bodies.Count, "two turns");
            var first = bodies[0];
            t.Equal("ephemeral", (string)first["tools"].AsArray().Last()["cache_control"]["type"], "tools cached");
            t.Equal("ephemeral", (string)first["system"][0]["cache_control"]["type"], "system cached");
            t.Equal(true, (bool)first["stream"], "streamed");
            var results = bodies[1]["messages"].AsArray().Last()["content"].AsArray();
            t.Equal("tu_1", (string)results[0]["tool_use_id"], "first result");
            t.Equal("image", (string)results[0]["content"][1]["type"], "image block in the tool result");
            t.Equal("AQID", (string)results[0]["content"][1]["source"]["data"], "base64 image data");
            t.Equal("ephemeral", (string)results.Last()["cache_control"]["type"], "rolling cache point on the newest user turn");
            t.True(bodies[1]["messages"].AsArray().Count(m => m["content"].AsArray().Any(b => b["cache_control"] != null)) == 1, "exactly one message cache point");
            t.Equal(4000, chat.CacheReadTokens, "cache reads counted");
            t.Equal(4000, chat.CacheWriteTokens, "cache writes counted");
            t.Equal(50, chat.OutputTokens, "output tokens");
        }

        [Test]
        public static async Task ChatReportsDeniedToolsAndApiErrors(TestContext t)
        {
            var fake = Fake(t);
            int call = 0;
            JsonNode second = null;
            fake.Routes.Add((ClaudeChat.ApiUrl, r =>
            {
                if (++call == 2) second = JsonNode.Parse(r.Content.ReadAsStringAsync().Result);
                if (call == 1)
                    return Sse("{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5}}}",
                               "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_9\",\"name\":\"create_entity\",\"input\":{}}}",
                               "{\"type\":\"content_block_stop\",\"index\":0}",
                               "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":5}}");
                if (call == 2)
                    return Sse("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                               "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Okay, nichts geändert.\"}}",
                               "{\"type\":\"content_block_stop\",\"index\":0}",
                               "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":5}}");
                return new HttpResponseMessage((HttpStatusCode)401) { Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}") };
            }));
            var chat = Chat();
            bool ran = false;
            chat.Approve = (c, ct) => Task.FromResult(ToolApproval.Deny);
            chat.RunTool = (c, ct) => { ran = true; return Task.FromResult(new ChatToolResult { Text = "x" }); };
            string answer = await chat.SendAsync("Lösch alles", CancellationToken.None);
            t.False(ran, "a denied tool does not run");
            t.Equal("Okay, nichts geändert.", answer, "Claude answers after the denial");
            var result = second["messages"].AsArray().Last()["content"][0];
            t.Equal(true, (bool)result["is_error"], "denial is an error result");
            t.True(((string)result["content"][0]["text"]).Contains("denied"), "denial explained to the model");
            bool threw = false;
            try { await chat.SendAsync("noch mal", CancellationToken.None); }
            catch (ClaudeChatException ex) { threw = ex.Message.Contains("rejected") && ex.Status == (HttpStatusCode)401; }
            t.True(threw, "a 401 becomes a readable error");
        }
    }
}
