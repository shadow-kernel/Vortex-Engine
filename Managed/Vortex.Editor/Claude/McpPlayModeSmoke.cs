using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using Editor.UI.Vui;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "mcp play mode leaves no trace": Stop gives back exactly the edit scene of Play, end to end over
    /// MCP. While playing (and paused) the scene tools create, duplicate, place, grid, scatter and re-parent entities, change
    /// a stored script field and the environment — each result says runtime_only — and after exit_play_mode the hierarchy,
    /// the field and the environment are the ones of Play (#423, #478, #479), with no retained-UI screen left (#477). Then a
    /// behaviour quits from its Update: play stops after the tick, through the toolbar's Stop (#410).
    /// </summary>
    internal static class McpPlayModeSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("mcp play mode leaves no trace", Run);

        private const string Probe =
            "using Vortex;\n\npublic class McpPlayProbe : VortexBehaviour\n{\n" +
            "    public float Marker = 1f;\n    public bool QuitInUpdate = false;\n    public string PushPath = \"\";\n\n" +
            "    public override void Start() { if (PushPath != \"\") Gui.Push(PushPath); }\n\n" +
            "    public override void Update(float dt) { if (QuitInUpdate) Application.Quit(); }\n}\n";

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("mcp play mode: " + why); return false; }
            var project = ProjectData.Current;
            var scene = project?.ActiveScene;
            if (scene == null) return Fail("no scene");
            if (McpHost.IsRunning) return Fail("a server is already running");
            if (PlayModeService.Instance.IsPlaying) return Fail("the editor is already playing");
            int port = McpTestClient.FreePort();
            if (!await McpHost.StartAsync(port)) return Fail("server did not start: " + McpHost.LastError);
            var files = new List<string>();
            var roots = new List<string>();
            var settings = scene.Settings;
            bool taa = settings.TaaEnabled; float sharpen = settings.Sharpen;
            int stopInsideTick = -1;   // 1 = play stopped from inside ScriptRuntime.Update (#410), 0 = after the tick
            EventHandler<PlayState> onState = (s, state) =>
            {
                if (state == PlayState.Editing) stopInsideTick = Environment.StackTrace.Contains("ScriptRuntime.Update(") ? 1 : 0;
            };
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(60) };
            async Task<JsonNode> C(string tool, JsonObject args) => McpTestClient.Json(await McpTestClient.Call(http, tool, args));
            bool RuntimeOnly(JsonNode r) => r?["runtime_only"] != null && (bool)r["runtime_only"];
            try
            {
                await McpTestClient.Rpc(http, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "vortex-smoke-play", ["version"] = "1" },
                });

                // ================= the edit scene: a probe behaviour, two parents with a child each
                string vui = Path.Combine(project.Path, "Assets", "UI", "Mcp Play Probe.vui");
                Directory.CreateDirectory(Path.GetDirectoryName(vui));
                File.WriteAllText(vui, "{ \"vui\": 1, \"designW\": 1920, \"designH\": 1080, \"root\": { \"kind\": \"Panel\", \"id\": \"probeRoot\", \"blocksInput\": true } }");
                files.Add(vui);
                var ws = await C("write_script", new JsonObject { ["script"] = "McpPlayProbe", ["code"] = Probe });
                files.Add(Path.Combine(project.Path, "Assets", "Scripts", "McpPlayProbe.cs"));
                if ((bool)ws["compiles"] != true) return Fail("write_script: " + ws.ToJsonString());
                string probe = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Play Probe", ["position"] = new JsonArray(300, 1, 300) }))["id"];
                string a = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Play A", ["position"] = new JsonArray(300, 0, 304) }))["id"];
                string b = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Play B", ["position"] = new JsonArray(304, 0, 304) }))["id"];
                roots.AddRange(new[] { probe, a, b });
                string childA = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Play A Child", ["parent"] = a }))["id"];
                string childB = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Play B Child", ["parent"] = b }))["id"];
                await C("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpPlayProbe", ["fields"] = new JsonObject { ["Marker"] = 5, ["PushPath"] = vui } });
                var script = SceneModel.Resolve(probe).GetComponent<Script>();
                string shape = Shape(scene);

                // ================= session 1: what the tools change while playing or paused goes again on Stop
                var play = await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.5 });
                if ((string)play["state"] != "Playing") return Fail("enter_play_mode: " + play.ToJsonString());
                var probeUi = VuiStack.Instance.Load(vui);
                if (!VuiStack.Instance.HasActiveScreens || probeUi == null) return Fail("the probe's Start did not push its screen");
                var calls = new (string tool, JsonObject args)[]
                {
                    ("create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Mcp Play Leak", ["position"] = new JsonArray(300, 1, 302) }),
                    ("duplicate_entities", new JsonObject { ["entities"] = new JsonArray(probe) }),
                    ("place_asset", new JsonObject { ["asset"] = "Primitive:Cube", ["position"] = new JsonArray(302, 0, 300), ["name"] = "Mcp Play Placed" }),
                    ("place_grid", new JsonObject { ["source"] = "cube", ["origin"] = new JsonArray(304, 0, 300), ["rows"] = 2, ["cols"] = 2, ["spacing"] = new JsonArray(1), ["name"] = "Mcp Play Grid" }),
                    ("scatter", new JsonObject { ["source"] = "sphere", ["area_min"] = new JsonArray(304, 300), ["area_max"] = new JsonArray(306, 302), ["count"] = 3, ["fallback_y"] = 0, ["name"] = "Mcp Play Scatter" }),
                    ("parent_entity", new JsonObject { ["entity"] = childA, ["parent"] = b }),
                    ("unparent_entity", new JsonObject { ["entity"] = childB }),
                    ("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpPlayProbe", ["fields"] = new JsonObject { ["Marker"] = 300 } }),
                    ("set_environment", new JsonObject { ["temporal_aa"] = !taa, ["sharpen"] = sharpen > 0.2f ? 0.1 : 0.4 }),
                };
                foreach (var (tool, args) in calls)
                {
                    var r = await C(tool, args);
                    if (!RuntimeOnly(r)) return Fail(tool + " in play mode does not say runtime_only: " + McpTestClient.Shorten(r?.ToJsonString()));
                }
                if (script.GetFieldValue("Marker") != "300") return Fail("attach_script did not set the field in play");
                PlayModeService.Instance.Pause();
                var paused = await C("create_entity", new JsonObject { ["name"] = "Mcp Play Paused" });
                if (!RuntimeOnly(paused)) return Fail("create_entity while paused does not say runtime_only (#423)");
                var stop = await C("exit_play_mode", new JsonObject());
                if ((string)stop["state"] != "Editing") return Fail("exit_play_mode: " + stop.ToJsonString());

                string after = Shape(scene);
                if (after != shape) return Fail("the hierarchy after Stop is not the one of Play (#423):\n  before " + McpTestClient.Shorten(shape) + "\n  after  " + McpTestClient.Shorten(after));
                if (script.GetFieldValue("Marker") != "5") return Fail("the script field changed in play stayed: " + script.GetFieldValue("Marker") + " (#478)");
                if (settings.TaaEnabled != taa || settings.Sharpen != sharpen || !ReferenceEquals(scene.Settings, settings))
                    return Fail("the environment changed in play stayed: temporal_aa " + scene.Settings.TaaEnabled + ", sharpen " + scene.Settings.Sharpen + " (#479)");
                if (VuiStack.Instance.HasActiveScreens) return Fail("a retained-UI screen survived Stop (#477)");
                if (ReferenceEquals(VuiStack.Instance.Load(vui), probeUi)) return Fail("Gui.Load returns the previous session's canvas (#477)");
                VuiStack.Instance.Reset();

                // ================= session 2: Application.Quit() from Update stops play after the tick (#410)
                await C("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpPlayProbe", ["fields"] = new JsonObject { ["QuitInUpdate"] = true, ["PushPath"] = "" } });
                shape = Shape(scene);
                PlayModeService.Instance.StateChanged += onState;
                await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.5 });
                for (int i = 0; i < 150 && PlayModeService.Instance.IsPlaying; i++) await Task.Delay(100);
                if (PlayModeService.Instance.IsPlaying) return Fail("Application.Quit() from Update did not stop play");
                if (stopInsideTick != 0) return Fail("play stopped inside the script tick — the rest of the tick then writes into the restored scene (#410)");
                if (CameraService.Instance.IsGameCameraActive) return Fail("after a script's quit the viewport still looks through the game camera (not the toolbar's Stop)");
                if (Shape(scene) != shape || script.GetFieldValue("QuitInUpdate") != "true" || script.GetFieldValue("Marker") != "5")
                    return Fail("the scene after a script's quit is not the one of Play");

                log.Log("mcp play mode: OK — " + calls.Length + " tool edits in play + 1 paused reported runtime_only and were gone after Stop (hierarchy, script field, environment); no screen survived; Application.Quit() stopped play after the tick");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                PlayModeService.Instance.StateChanged -= onState;
                if (PlayModeService.Instance.IsPlaying) EditorCommands.Stop();
                try { if (roots.Count > 0) await C("delete_entities", new JsonObject { ["entities"] = new JsonArray(roots.ConvertAll(r => (JsonNode)r).ToArray()) }); } catch { }
                scene.Settings.TaaEnabled = taa; scene.Settings.Sharpen = sharpen;
                try { scene.Settings.Apply(); } catch { }
                VuiStack.Instance.Reset();
                foreach (var f in files)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    try { if (File.Exists(f + ".vmeta")) File.Delete(f + ".vmeta"); } catch { }
                }
                EditorCommands.AfterSceneEdit();
                await McpHost.StopAsync();
            }
        }

        /// <summary>The hierarchy as text: every list position with its entity, depth first — two shapes are equal only when
        /// every entity is in the same parent, at the same index, once.</summary>
        private static string Shape(Scene scene)
        {
            var sb = new StringBuilder();
            void Walk(IList<GameEntity> list, string parent, int depth)
            {
                if (list == null || depth > 64) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    string id = e == null ? "null" : SceneModel.ShortId(e);
                    sb.Append(parent).Append('[').Append(i).Append("]=").Append(id).Append(' ');
                    if (e != null) Walk(e.Children, id, depth + 1);
                }
            }
            Walk(scene.Entities, "", 0);
            return sb.ToString();
        }
    }
}
