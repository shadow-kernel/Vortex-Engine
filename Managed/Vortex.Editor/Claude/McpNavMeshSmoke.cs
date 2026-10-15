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
using Editor.Core.Services.AI;
using Editor.Core.UndoRedo;
using Editor.ECS;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "mcp navmesh" (#449): a floor and a wall (box colliders) far from the template's own level; bake_navmesh
    /// writes and loads &lt;Scene&gt;.vnav and a path runs around the wall; navmesh_info reports the bake (polygons, tiles, bounds,
    /// settings) as current; a nudge under a millimetre keeps it current, the wall moved out of the way makes it stale (naming the
    /// wall, not the floor); a rebake with overrides (agent radius,
    /// colliders only) stores them, is current again and the path goes straight. A bad value names its range, a bake while
    /// playing is refused, clear_navmesh deletes and unloads it. The scene's own .vnav is put back afterwards.
    /// </summary>
    internal static class McpNavMeshSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("mcp navmesh", Run);

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("mcp navmesh: " + why); return false; }
            var project = ProjectData.Current;
            var scene = project?.ActiveScene;
            if (scene == null) return Fail("no scene");
            if (!NavigationService.Available) return Fail("navigation unavailable: " + NavigationService.UnavailableReason);
            if (McpHost.IsRunning) return Fail("a server is already running");
            if (PlayModeService.Instance.IsPlaying) return Fail("the editor is already playing");
            int port = McpTestClient.FreePort();
            if (!await McpHost.StartAsync(port)) return Fail("server did not start: " + McpHost.LastError);
            var undo = UndoRedoManager.Instance;
            int undoBefore = undo.UndoCount;
            string vnav = NavigationService.NavMeshPathFor(scene);
            byte[] backup = vnav != null && File.Exists(vnav) ? File.ReadAllBytes(vnav) : null;
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(180) };
            async Task<JsonNode> Raw(string tool, JsonObject args) => await McpTestClient.Call(http, tool, args);
            async Task<JsonNode> C(string tool, JsonObject args) => McpTestClient.Json(await Raw(tool, args));
            // how far the path from one side of the wall to the other strays from the straight line
            (NavPathStatus status, float detour) Path()
            {
                var corners = new List<Vector3>();
                var st = NavigationService.CalculatePath(new Vector3(295, 0, 300), new Vector3(305, 0, 300), corners);
                return (st, corners.Count == 0 ? 0f : corners.Max(c => Math.Abs(c.Z - 300f)));
            }
            try
            {
                await McpTestClient.Rpc(http, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "vortex-smoke-navmesh", ["version"] = "1" },
                });
                var collider = new JsonArray(new JsonObject { ["type"] = "BoxCollider" });
                await C("create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Mcp Nav Floor", ["position"] = new JsonArray(300, -0.5, 300), ["scale"] = new JsonArray(20, 1, 20), ["components"] = collider.DeepClone() });
                string wall = (string)(await C("create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Mcp Nav Wall", ["position"] = new JsonArray(300, 1, 300), ["scale"] = new JsonArray(1, 2, 12), ["components"] = collider.DeepClone() }))["id"];

                // ================= bake: written next to the scene, loaded, a path around the wall
                var bake = await C("bake_navmesh", new JsonObject());
                if (((int?)bake["polygons"] ?? 0) <= 0 || (bool?)bake["loaded"] != true || !((string)bake["file"] ?? "").EndsWith(".vnav", StringComparison.Ordinal))
                    return Fail("bake_navmesh: " + bake.ToJsonString());
                if (!File.Exists(vnav) || !ReferenceEquals(NavigationService.LoadedScene, scene)) return Fail("the .vnav was not written and loaded");
                var around = Path();
                if (around.status != NavPathStatus.Complete || around.detour < 6f) return Fail("the path does not go around the wall: " + around.status + ", detour " + around.detour);

                var info = await C("navmesh_info", new JsonObject());
                if ((bool?)info["exists"] != true || (bool?)info["loaded"] != true || (bool?)info["stale"] != false || (int?)info["polygons"] != (int?)bake["polygons"])
                    return Fail("navmesh_info after the bake: " + info.ToJsonString());
                var max = info["bounds"]?["max"] as JsonArray;
                if (max == null || (double)max[0] < 305 || (double)max[2] < 305) return Fail("the bounds miss the test level: " + info["bounds"]?.ToJsonString());

                // ================= a nudge under a millimetre is no change; the wall moved out of the way is — named, alone
                await C("set_transform", new JsonObject { ["entity"] = wall, ["position"] = new JsonArray(300.0004, 1, 300) });
                var nudged = await C("navmesh_info", new JsonObject());
                if ((bool?)nudged["stale"] != false) return Fail("a 0.4 mm nudge must not make the navmesh stale: " + nudged.ToJsonString());
                await C("set_transform", new JsonObject { ["entity"] = wall, ["position"] = new JsonArray(300, 1, 330) });
                var moved = await C("navmesh_info", new JsonObject());
                string reason = (string)moved["stale_reason"] ?? "";
                if ((bool?)moved["stale"] != true || !reason.Contains("changed since the bake: 'Mcp Nav Wall'") || reason.Contains("Floor"))
                    return Fail("a moved wall must make the navmesh stale, naming the wall only: " + moved.ToJsonString());

                var rebake = await C("bake_navmesh", new JsonObject { ["agent_radius"] = 0.3, ["sources"] = new JsonArray("colliders") });
                var after = await C("navmesh_info", new JsonObject());
                var settings = after["settings"];
                if ((bool?)after["stale"] != false || (double?)settings?["agent_radius"] != 0.3 || settings?["sources"]?.ToJsonString() != "[\"colliders\"]")
                    return Fail("the rebake with overrides: " + rebake.ToJsonString() + " / " + after.ToJsonString());
                var straight = Path();
                if (straight.status != NavPathStatus.Complete || straight.detour > 1f) return Fail("with the wall gone the path should be straight: " + straight.status + ", detour " + straight.detour);

                // ================= a bad value names its range; while playing a bake is refused
                var bad = await Raw("bake_navmesh", new JsonObject { ["agent_radius"] = 9 });
                if ((bool?)bad["isError"] != true || !McpTestClient.Text(bad).Contains("between")) return Fail("agent_radius 9 must be refused with its range: " + McpTestClient.Text(bad));
                await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.3 });
                var playing = await Raw("bake_navmesh", new JsonObject());
                await C("exit_play_mode", new JsonObject());
                if ((bool?)playing["isError"] != true || !McpTestClient.Text(playing).Contains("exit_play_mode")) return Fail("a bake while playing must be refused: " + McpTestClient.Text(playing));

                // ================= clear
                var cleared = await C("clear_navmesh", new JsonObject());
                if ((string)cleared["deleted"] == null || File.Exists(vnav) || ReferenceEquals(NavigationService.LoadedScene, scene)) return Fail("clear_navmesh: " + cleared.ToJsonString());
                var gone = await C("navmesh_info", new JsonObject());
                if ((bool?)gone["exists"] != false) return Fail("navmesh_info after clear: " + gone.ToJsonString());
                log.Log("[smoke] mcp navmesh: " + bake["message"] + " | rebake: " + rebake["message"]);
                return true;
            }
            catch (Exception ex) { return Fail(ex.Message); }
            finally
            {
                try { if (PlayModeService.Instance.IsPlaying) Editor.Core.Editing.EditorSession.Instance.Stop(); } catch { }
                while (undo.UndoCount > undoBefore && undo.Undo()) { }
                EditorCommands.AfterSceneEdit();
                try
                {
                    if (backup != null) { File.WriteAllBytes(vnav, backup); NavigationService.LoadForScene(scene); }
                    else
                    {
                        if (vnav != null && File.Exists(vnav)) File.Delete(vnav);
                        if (ReferenceEquals(NavigationService.LoadedScene, scene)) NavigationService.Unload();
                    }
                }
                catch { }
                await McpHost.StopAsync();
            }
        }
    }
}
