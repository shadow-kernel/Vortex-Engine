using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "mcp server": the MCP server end to end, the way Claude Code talks to it — JSON-RPC over
    /// Streamable HTTP on a free port: initialize, tools/list, then tool calls that build, inspect, capture and undo;
    /// error results that name the valid values; and the request guard (foreign Origin / Host → 403).
    /// </summary>
    internal static class McpSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("mcp server", Run);

        public static async Task<bool> Run()
        {
            Func<HttpClient, string, JsonObject, Task<JsonNode>> Rpc = McpTestClient.Rpc;
            Func<HttpClient, string, JsonObject, Task<JsonNode>> Call = McpTestClient.Call;
            Func<JsonNode, string> Text = McpTestClient.Text;
            Func<JsonNode, JsonNode> Json = McpTestClient.Json;
            Func<HttpClient, string, string, Task<HttpStatusCode>> Status = McpTestClient.Status;
            Func<string, string> Shorten = McpTestClient.Shorten;
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("mcp server: " + why); return false; }
            if (ProjectData.Current?.ActiveScene == null) return Fail("no scene");
            if (McpHost.IsRunning) return Fail("a server is already running in this smoke run");
            int port = McpTestClient.FreePort();
            if (!await McpHost.StartAsync(port)) return Fail("did not start: " + McpHost.LastError);
            var created = new List<string>();
            int undoBefore = UndoRedoManager.Instance.UndoCount;
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(30) };
            try
            {
                // ---- handshake + listing
                var init = await Rpc(http, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "vortex-smoke", ["version"] = "1" },
                });
                McpTestClient.Protocol = (string)init?["result"]?["protocolVersion"];
                if ((string)init?["result"]?["serverInfo"]?["name"] != "vortex") return Fail("initialize: " + init?.ToJsonString());
                var list = await Rpc(http, "tools/list", new JsonObject());
                var tools = list?["result"]?["tools"] as JsonArray;
                if (tools == null || tools.Count < 20) return Fail("tools/list returned " + (tools?.Count ?? 0) + " tools");
                var create = tools.FirstOrDefault(t => (string)t["name"] == "create_entity");
                if (create?["inputSchema"]?["properties"]?["kind"] == null) return Fail("create_entity has no input schema");
                if ((bool?)tools.First(t => (string)t["name"] == "scene_outline")["annotations"]?["readOnlyHint"] != true) return Fail("scene_outline is not marked read-only");

                // ---- build
                var info = await Call(http, "get_editor_info", new JsonObject());
                if (!Text(info).Contains(ProjectData.Current.Name)) return Fail("get_editor_info: " + Text(info));
                var cube = Json(await Call(http, "create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Mcp Cube", ["position"] = new JsonArray(0, 1, 4), ["components"] = new JsonArray(new JsonObject { ["type"] = "BoxCollider" }) }));
                string cubeId = (string)cube?["id"];
                if (cubeId == null) return Fail("create_entity: " + cube?.ToJsonString());
                created.Add(cubeId);
                if (!(cube["components"] as JsonArray).Any(c => (string)c == "BoxCollider")) return Fail("the BoxCollider was not added: " + cube.ToJsonString());
                var lamp = Json(await Call(http, "create_entity", new JsonObject { ["kind"] = "point_light", ["name"] = "Mcp Lamp", ["parent"] = cubeId, ["position"] = new JsonArray(0, 1.5, 0) }));
                string lampId = (string)lamp?["id"];
                if (lampId == null || (string)lamp["path"] != "Mcp Cube/Mcp Lamp") return Fail("child light: " + lamp?.ToJsonString());
                var set = Json(await Call(http, "set_component_properties", new JsonObject { ["entity"] = lampId, ["component"] = "Light", ["properties"] = new JsonObject { ["intensity"] = 3.5, ["color"] = "#ff8800" } }));
                if ((double?)set?["properties"]?["intensity"] != 3.5 || (double?)set?["properties"]?["color_g"] is not double g || Math.Abs(g - 0.533) > 0.01) return Fail("set_component_properties: " + set?.ToJsonString());
                var moved = Json(await Call(http, "set_transform", new JsonObject { ["entity"] = "Mcp Cube", ["rotation"] = new JsonArray(0, 45, 0), ["scale"] = new JsonArray(2) }));
                if ((double?)moved?["transform"]?["rotation"]?[1] != 45 || (double?)moved?["transform"]?["scale"]?[2] != 2) return Fail("set_transform: " + moved?.ToJsonString());
                // world placement under the rotated, scaled parent
                var placed = Json(await Call(http, "set_transform", new JsonObject { ["entity"] = lampId, ["position"] = new JsonArray(1, 2, 3), ["space"] = "world" }));
                var wp = placed?["transform"]?["world_position"] as JsonArray;
                if (wp == null || Math.Abs((double)wp[0] - 1) > 0.01 || Math.Abs((double)wp[1] - 2) > 0.01 || Math.Abs((double)wp[2] - 3) > 0.01) return Fail("world placement: " + placed?.ToJsonString());

                // ---- read back
                var found = Json(await Call(http, "find_entities", new JsonObject { ["name"] = "Mcp*" }));
                if ((int?)found?["count"] != 2) return Fail("find_entities: " + found?.ToJsonString());
                var outline = Text(await Call(http, "scene_outline", new JsonObject { ["root"] = cubeId, ["max_depth"] = 5 }));
                if (!outline.Contains("Mcp Cube [" + cubeId + "] MeshRenderer, BoxCollider") || !outline.Contains("    Mcp Lamp [" + lampId + "] Light")) return Fail("scene_outline (root): " + outline);
                var whole = Text(await Call(http, "scene_outline", new JsonObject { ["max_entities"] = 20 }));
                int sceneSize = SceneModel.All(ProjectData.Current.ActiveScene).Count();
                if (whole.Split('\n').Length > 24) return Fail("scene_outline must stay within max_entities: " + whole);
                if (sceneSize > 20 && !whole.Contains(" more (raise max_entities")) return Fail("scene_outline must say what it left out: " + whole);
                var ent = Json(await Call(http, "get_entity", new JsonObject { ["entity"] = "Mcp Cube/Mcp Lamp" }));
                if ((string)ent?["components"]?[0]?["properties"]?["light_type"] != "Point") return Fail("get_entity: " + ent?.ToJsonString());

                // ---- errors the model can act on
                var bad = await Call(http, "set_component_properties", new JsonObject { ["entity"] = lampId, ["component"] = "Light", ["properties"] = new JsonObject { ["light_type"] = "Laser" } });
                if ((bool?)bad?["isError"] != true || !Text(bad).Contains("Spot")) return Fail("an invalid enum should list the valid values: " + bad?.ToJsonString());
                var missing = await Call(http, "get_entity", new JsonObject { ["entity"] = "No Such Thing" });
                if ((bool?)missing?["isError"] != true) return Fail("an unknown entity should be an error result");
                int steps = UndoRedoManager.Instance.UndoCount;
                var failed = await Call(http, "create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Mcp Broken", ["components"] = new JsonArray(new JsonObject { ["type"] = "NoSuchComponent" }) });
                if ((bool?)failed?["isError"] != true) return Fail("an unknown component type should fail the call");
                if (UndoRedoManager.Instance.UndoCount != steps || SceneModel.All(ProjectData.Current.ActiveScene).Any(e => e.Name == "Mcp Broken")) return Fail("a failed call must be rolled back completely");

                // ---- one undo step per call
                var history = Json(await Call(http, "get_history", new JsonObject { ["limit"] = 10 }));
                var names = (history?["undo"] as JsonArray)?.Select(n => (string)n).ToList() ?? new List<string>();
                if (names.Count < 5 || names[0] != "Claude: move Mcp Lamp" || !names.Contains("Claude: create Mcp Cube")) return Fail("undo history: " + history?.ToJsonString());
                if (UndoRedoManager.Instance.UndoCount - undoBefore != 5) return Fail("expected 5 undo steps for 5 mutating calls, got " + (UndoRedoManager.Instance.UndoCount - undoBefore));

                // ---- see it
                var shot = await Call(http, "capture_viewport", new JsonObject { ["focus"] = new JsonArray(cubeId), ["max_size"] = 960 });
                var img = (shot?["content"] as JsonArray)?.FirstOrDefault(c => (string)c["type"] == "image");
                string data = (string)img?["data"];
                if (data == null || (string)img["mimeType"] != "image/jpeg" || data.Length < 2000) return Fail("capture_viewport: " + Shorten(shot?.ToJsonString()));
                if (!string.IsNullOrEmpty(SmokeRegistry.CaptureDir)) File.WriteAllBytes(Path.Combine(SmokeRegistry.CaptureDir, "mcp_capture.jpg"), Convert.FromBase64String(data));

                // ---- the guard
                if (await Status(http, "http://evil.example", null) != HttpStatusCode.Forbidden) return Fail("a foreign Origin must be refused");
                if (await Status(http, null, "evil.example:" + port) != HttpStatusCode.Forbidden) return Fail("a foreign Host (DNS rebinding) must be refused");
                if (await Status(http, "http://localhost:6274", null) == HttpStatusCode.Forbidden) return Fail("a localhost Origin (MCP Inspector) must be allowed");

                // ---- undo through the tool: the last call (lamp moved) goes back
                var undone = Json(await Call(http, "undo", new JsonObject { ["steps"] = 1 }));
                if ((string)undone?["undone"]?[0] != "Claude: move Mcp Lamp") return Fail("undo: " + undone?.ToJsonString());
                log.Log("mcp server: OK — " + tools.Count + " tools on " + McpHost.Url + ", " + names.Count + " undo steps, capture " + data.Length * 3 / 4 / 1024 + " KB");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                // take every step of this check back, then stop the server
                while (UndoRedoManager.Instance.UndoCount > undoBefore && UndoRedoManager.Instance.Undo()) { }
                EditorCommands.AfterSceneEdit();
                await McpHost.StopAsync();
            }
        }
    }

    /// <summary>A tiny MCP client for the smoke checks: JSON-RPC over Streamable HTTP, JSON or SSE responses.</summary>
    internal static class McpTestClient
    {
        private static int _id;
        internal static string Protocol;

        internal static async Task<JsonNode> Call(HttpClient http, string tool, JsonObject args)
        {
            var r = await Rpc(http, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });
            if (r?["error"] != null) throw new InvalidOperationException(tool + ": JSON-RPC error " + r["error"].ToJsonString());
            return r?["result"];
        }

        internal static string Text(JsonNode result) => string.Join("\n", (result?["content"] as JsonArray)?.Where(c => (string)c["type"] == "text").Select(c => (string)c["text"]) ?? Array.Empty<string>());

        internal static JsonNode Json(JsonNode result)
        {
            if ((bool?)result?["isError"] == true) throw new InvalidOperationException("tool error: " + Text(result));
            return JsonNode.Parse(Text(result));
        }

        internal static async Task<JsonNode> Rpc(HttpClient http, string method, JsonObject prms)
        {
            int id = ++_id;
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms };
            using var req = new HttpRequestMessage(HttpMethod.Post, McpHost.Path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (Protocol != null) req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", Protocol);
            using var res = await http.SendAsync(req);
            string text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException(method + ": HTTP " + (int)res.StatusCode + " " + Shorten(text));
            if (res.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                foreach (var line in text.Split('\n'))
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var node = JsonNode.Parse(line.Substring(5).Trim());
                    if ((int?)node?["id"] == id) return node;
                }
                throw new InvalidOperationException(method + ": no response event in " + Shorten(text));
            }
            return JsonNode.Parse(text);
        }

        internal static async Task<HttpStatusCode> Status(HttpClient http, string origin, string host)
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = "tools/list", ["params"] = new JsonObject() };
            using var req = new HttpRequestMessage(HttpMethod.Post, McpHost.Path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (origin != null) req.Headers.TryAddWithoutValidation("Origin", origin);
            if (host != null) req.Headers.Host = host;
            using var res = await http.SendAsync(req);
            return res.StatusCode;
        }

        internal static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        internal static string Shorten(string s) => s == null ? "null" : s.Length > 400 ? s.Substring(0, 400) + "…" : s;
    }
}
