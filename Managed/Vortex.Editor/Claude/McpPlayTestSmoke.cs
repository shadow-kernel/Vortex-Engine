using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "mcp play test input and script state" (#457): a play test driven end to end over MCP, the way
    /// Claude Code runs one. A probe behaviour walks forward while W is held and fires on LButton; send_input walks it 2 s,
    /// fires once at a UI cursor it placed, looks 40 px, turns the wheel one notch and presses + releases Space inside one
    /// frame — GetKeyDown sees each tap exactly once — with no OS event and the cursor never captured. get_script_state
    /// reads the probe's fields and a static class's property, set_script_state writes both, read_console(since_id) returns
    /// only the line logged since, and after Stop and a new Play the state is the code's again (live only). Errors: input
    /// outside play mode, an unknown key, an entity without scripts.
    /// </summary>
    internal static class McpPlayTestSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("mcp play test input and script state", Run);

        private const string Probe =
            "using Vortex;\n\n" +
            "public static class McpProbeState\n{\n    public static int Fired { get; set; }\n    public static string Beat = \"intro\";\n}\n\n" +
            "public class McpInputProbe : VortexBehaviour\n{\n" +
            "    public int Marker = 1;\n" +
            "    public int WTicks, SpaceDowns, FireDowns;\n" +
            "    public float Walked, LookX, LookY, Scroll, ClickX = -1, ClickY = -1;\n" +
            "    public override void Start() { Cursor.Locked = false; Debug.Log(\"[MCP PROBE] started\"); }\n" +
            "    public override void Update(float dt)\n    {\n" +
            "        if (Input.GetKey(\"W\")) { WTicks++; Walked += 2f * dt; var p = Position; p.Z += 2f * dt; Position = p; }\n" +
            "        if (Input.GetKeyDown(\"Space\")) SpaceDowns++;\n" +
            "        if (Input.GetKeyDown(\"LButton\")) { FireDowns++; McpProbeState.Fired++; ClickX = UI.MouseX; ClickY = UI.MouseY; Debug.Log(\"[MCP PROBE] fired \" + McpProbeState.Fired); }\n" +
            "        LookX += Input.MouseDeltaX; LookY += Input.MouseDeltaY; Scroll += Input.ScrollDelta;\n" +
            "    }\n" +
            "    // the free cursor: the real one is never captured while this check plays\n" +
            "    public override void LateUpdate(float dt) { Cursor.Locked = false; }\n}\n";

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("mcp play test: " + why); return false; }
            var project = ProjectData.Current;
            var scene = project?.ActiveScene;
            if (scene == null) return Fail("no scene");
            if (McpHost.IsRunning) return Fail("a server is already running");
            if (PlayModeService.Instance.IsPlaying) return Fail("the editor is already playing");
            int port = McpTestClient.FreePort();
            if (!await McpHost.StartAsync(port)) return Fail("server did not start: " + McpHost.LastError);
            int undoBefore = UndoRedoManager.Instance.UndoCount;
            string scriptFile = Path.Combine(project.Path, "Assets", "Scripts", "McpInputProbe.cs");
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(90) };
            async Task<JsonNode> Raw(string tool, JsonObject args) => await McpTestClient.Call(http, tool, args);
            async Task<JsonNode> C(string tool, JsonObject args) => McpTestClient.Json(await Raw(tool, args));
            static double D(JsonNode n) => n == null ? double.NaN : (double)n;
            try
            {
                await McpTestClient.Rpc(http, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "vortex-smoke-playtest", ["version"] = "1" },
                });
                var ws = await C("write_script", new JsonObject { ["script"] = "McpInputProbe", ["code"] = Probe });
                if ((bool)ws["compiles"] != true) return Fail("the probe does not compile: " + ws.ToJsonString());
                string probe = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Input Probe", ["position"] = new JsonArray(400, 1, 400) }))["id"];
                await C("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpInputProbe" });
                var bare = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Input Bare", ["position"] = new JsonArray(402, 1, 400) }))["id"];

                var early = await Raw("send_input", new JsonObject { ["actions"] = JsonNode.Parse("[{\"press\": \"W\"}]") });
                if ((bool?)early["isError"] != true || !McpTestClient.Text(early).Contains("enter_play_mode")) return Fail("send_input outside play mode must say to enter it: " + McpTestClient.Text(early));

                var play = await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.6 });
                if ((string)play["state"] != "Playing") return Fail("enter_play_mode: " + play.ToJsonString());
                var cursor0 = await C("read_console", new JsonObject { ["limit"] = 1 });
                long since = (long)cursor0["next_id"];
                if (!log.Entries.Any(e => e.Message == "[MCP PROBE] started")) return Fail("the probe did not start");

                // ---- 2 s walk, a fire at a UI cursor, mouse movement, one wheel notch, Space down and up inside one frame
                float z0 = SceneModel.Resolve(probe).Transform.LocalPosition.Z;
                var sent = await C("send_input", new JsonObject
                {
                    ["actions"] = JsonNode.Parse("[{\"t\": 0, \"press\": \"W\"}, {\"t\": 2, \"release\": \"W\"}, {\"t\": 2, \"cursor\": [123, 45]}, {\"t\": 2.1, \"tap\": \"LButton\"}," +
                                                 " {\"t\": 0.5, \"look\": [40, -10, 0.5]}, {\"t\": 1.2, \"scroll\": -1}, {\"t\": 1.5, \"press\": \"Space\"}, {\"t\": 1.5, \"release\": \"Space\"}]"),
                });
                if ((int?)sent["ran"] != 8 || (string)sent["state"] != "Playing" || sent["keys_held"] != null || (long?)sent["frames"] < 10)
                    return Fail("send_input: " + sent.ToJsonString());
                var st = await C("get_script_state", new JsonObject { ["target"] = probe });
                var s = st["behaviours"]?[0]?["state"];
                if ((string)st["behaviours"]?[0]?["script"] != "McpInputProbe" || s == null) return Fail("get_script_state: " + st.ToJsonString());
                double walked = D(s["Walked"]);
                float z1 = SceneModel.Resolve(probe).Transform.LocalPosition.Z;
                // ± one frame at either end: 0.3 m covers 10 fps
                if (Math.Abs(walked - 4.0) > 0.3 || Math.Abs((z1 - z0) - walked) > 0.01) return Fail("W held for 2 s should walk 4 m at 2 m/s: walked " + walked + ", moved " + (z1 - z0) + " — " + s.ToJsonString());
                if ((int?)s["FireDowns"] != 1) return Fail("one LButton tap must be one GetKeyDown: " + s.ToJsonString());
                if ((int?)s["SpaceDowns"] != 1) return Fail("a press and release inside one frame must be one GetKeyDown: " + s.ToJsonString());
                if (Math.Abs(D(s["LookX"]) - 40) > 0.01 || Math.Abs(D(s["LookY"]) + 10) > 0.01) return Fail("look [40, -10] should add up exactly: " + s.ToJsonString());
                if (D(s["Scroll"]) != -1) return Fail("one wheel notch down: " + s.ToJsonString());
                if (D(s["ClickX"]) != 123 || D(s["ClickY"]) != 45) return Fail("the click should land at the UI cursor [123, 45]: " + s.ToJsonString());
                var stat = await C("get_script_state", new JsonObject { ["target"] = "static:McpProbeState" });
                if ((int?)stat["state"]?["Fired"] != 1 || (string)stat["state"]?["Beat"] != "intro") return Fail("get_script_state static: " + stat.ToJsonString());
                if (Editor.Core.Viewport.EditorViewportSession.Main?.IsMouseCaptured == true) return Fail("the real cursor was captured while the probe kept it free");

                // ---- read_console(since_id): only what is new
                var fresh = await C("read_console", new JsonObject { ["since_id"] = since, ["contains"] = "[MCP PROBE]" });
                var lines = (fresh["lines"] as JsonArray)?.Select(l => (string)l).ToList() ?? new List<string>();
                if (lines.Count != 1 || !lines[0].EndsWith("[MCP PROBE] fired 1", StringComparison.Ordinal)) return Fail("read_console(since_id) should return the one line logged since: " + fresh.ToJsonString());
                var again = await C("read_console", new JsonObject { ["since_id"] = (long)fresh["next_id"], ["contains"] = "[MCP PROBE]" });
                if ((again["lines"] as JsonArray)?.Count != 0) return Fail("nothing new — nothing again: " + again.ToJsonString());

                // ---- the player's --input format, async input and cancel
                var scripted = await C("send_input", new JsonObject { ["script"] = "0 tap Space\n0.2 capture shot.bmp" });
                if (!((string)scripted["note"] ?? "").Contains("capture")) return Fail("a pasted player script says its capture was ignored: " + scripted.ToJsonString());
                var queued = await C("send_input", new JsonObject { ["actions"] = JsonNode.Parse("[{\"press\": \"W\"}, {\"t\": 30, \"release\": \"W\"}]"), ["wait"] = false });
                await Task.Delay(300);
                if ((int?)queued["queued"] != 2 || !Editor.Core.Input.HostInput.IsKeyDown('W')) return Fail("wait: false queues and returns: " + queued.ToJsonString());
                var cancelled = await C("send_input", new JsonObject { ["cancel"] = true });
                if (cancelled["keys_held"] != null || Editor.Core.Input.HostInput.IsKeyDown('W')) return Fail("cancel releases W: " + cancelled.ToJsonString());
                var badKey = await Raw("send_input", new JsonObject { ["actions"] = JsonNode.Parse("[{\"tap\": \"Spacebar\"}]") });
                if ((bool?)badKey["isError"] != true || !McpTestClient.Text(badKey).Contains("LeftShift")) return Fail("an unknown key lists the names: " + McpTestClient.Text(badKey));
                var noScript = await Raw("get_script_state", new JsonObject { ["target"] = bare });
                if ((bool?)noScript["isError"] != true || !McpTestClient.Text(noScript).Contains("runs no script")) return Fail("an entity without scripts says so: " + McpTestClient.Text(noScript));

                // ---- set_script_state: live only — the next Play starts from the code again
                var set = await C("set_script_state", new JsonObject { ["target"] = probe, ["values"] = new JsonObject { ["Marker"] = 42 } });
                var setStatic = await C("set_script_state", new JsonObject { ["target"] = "static:McpProbeState", ["values"] = new JsonObject { ["Beat"] = "act4", ["Fired"] = 9 } });
                if ((int?)set["set"]?["Marker"] != 42 || (string)setStatic["set"]?["Beat"] != "act4" || set["runtime_only"] == null || (bool)set["runtime_only"] != true)
                    return Fail("set_script_state: " + set.ToJsonString() + " " + setStatic.ToJsonString());
                var check = await C("get_script_state", new JsonObject { ["target"] = probe, ["fields"] = new JsonArray("Marker") });
                if ((int?)check["behaviours"]?[0]?["state"]?["Marker"] != 42) return Fail("the field did not take the value: " + check.ToJsonString());
                var badSet = await Raw("set_script_state", new JsonObject { ["target"] = probe, ["values"] = new JsonObject { ["Marker"] = 1, ["Walkd"] = 2 } });
                if ((bool?)badSet["isError"] != true || !McpTestClient.Text(badSet).Contains("Walked")) return Fail("an unknown member lists the real ones: " + McpTestClient.Text(badSet));
                await C("exit_play_mode", new JsonObject());
                await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.4 });
                var after = await C("get_script_state", new JsonObject { ["target"] = probe, ["fields"] = new JsonArray("Marker", "FireDowns") });
                var afterStatic = await C("get_script_state", new JsonObject { ["target"] = "static:McpProbeState" });
                if ((int?)after["behaviours"]?[0]?["state"]?["Marker"] != 1 || (string)afterStatic["state"]?["Beat"] != "intro" || (int?)afterStatic["state"]?["Fired"] != 0)
                    return Fail("set_script_state must not outlive Stop: " + after.ToJsonString() + " " + afterStatic.ToJsonString());
                await C("exit_play_mode", new JsonObject());
                var edit = await Raw("get_script_state", new JsonObject { ["target"] = probe });
                if ((bool?)edit["isError"] != true) return Fail("get_script_state in edit mode must say to enter play mode");

                log.Log("mcp play test: OK — send_input walked the probe " + walked.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m in 2 s (" + (int?)s["WTicks"] +
                        " ticks), one fire at the UI cursor, look 40/-10, one notch, a one-frame Space; script state read and set (behaviour + static), gone after Stop; read_console(since_id) returned only the new line");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                if (PlayModeService.Instance.IsPlaying) EditorCommands.Stop();
                Editor.Core.Input.HostInput.Simulated.Clear();
                while (UndoRedoManager.Instance.UndoCount > undoBefore && UndoRedoManager.Instance.Undo()) { }
                EditorCommands.AfterSceneEdit();
                foreach (var f in new[] { scriptFile, scriptFile + ".vmeta" }) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
                await McpHost.StopAsync();
            }
        }
    }
}
