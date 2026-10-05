using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Scripting;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "claude corridor demo" — the acceptance scene of #89 / #99, built only with the tools Claude
    /// uses: a 14 m corridor (floor, walls, ceiling, material), four warm ceiling lights with a flicker script, debris
    /// scattered on the floor below the ceiling, a looping generated ambience — then the camera looks down the corridor
    /// and the viewport is captured (claude_corridor.jpg). Every call is one undo step; everything is undone afterwards.
    /// </summary>
    internal static class ClaudeCorridorSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("claude corridor demo", Run);

        private const string Flicker =
            "using Vortex;\n\n" +
            "/// A failing fluorescent tube: the light's intensity wavers around its base value.\n" +
            "public class LampFlicker : VortexBehaviour\n{\n" +
            "    public float BaseIntensity = 2.5f;\n" +
            "    public float Speed = 9f;\n" +
            "    private float _t;\n\n" +
            "    public override void Update(float dt)\n    {\n" +
            "        _t += dt;\n" +
            "        var light = GetLight();\n" +
            "        if (light != null) light.Intensity = BaseIntensity * (0.55f + 0.45f * Light.Flicker(_t, Speed));\n" +
            "    }\n}\n";

        private static async Task<JsonNode> C(string tool, JsonObject args)
        {
            var dict = new Dictionary<string, object>();
            using (var doc = JsonDocument.Parse(args.ToJsonString()))
                foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = p.Value.Clone();
            var r = await ToolHost.CallAsync(tool, dict, "smoke");
            string text = string.Join("\n", r.Content.Where(c => c.Text != null).Select(c => c.Text));
            if (r.IsError) throw new InvalidOperationException(tool + ": " + text);
            var img = r.Content.FirstOrDefault(c => c.Image != null);
            if (img != null && !string.IsNullOrEmpty(SmokeRegistry.CaptureDir))
                File.WriteAllBytes(Path.Combine(SmokeRegistry.CaptureDir, "claude_corridor.jpg"), img.Image);
            try { return JsonNode.Parse(text); } catch { return JsonValue.Create(text); }
        }

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("claude corridor demo: " + why); return false; }
            var project = ProjectData.Current;
            if (project?.ActiveScene == null) return Fail("no scene");
            var undo = UndoRedoManager.Instance;
            int undoBefore = undo.UndoCount;
            var files = new List<string>();
            try
            {
                const float x = 300, z = 300;   // away from the level
                // ---- shell: floor, walls, ceiling
                var mat = await C("create_material", new JsonObject { ["name"] = "Corridor Concrete", ["properties"] = new JsonObject { ["base_color"] = "#4a4844", ["roughness"] = 0.9 } });
                files.Add(Path.Combine(project.Path, (string)mat["path"]));
                var root = await C("create_entity", new JsonObject { ["kind"] = "folder", ["name"] = "Corridor" });
                string corridor = (string)root["id"];
                var floor = await C("create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Floor", ["parent"] = corridor, ["position"] = new JsonArray(x, -0.1, z + 7), ["scale"] = new JsonArray(4, 0.2, 14), ["components"] = new JsonArray(new JsonObject { ["type"] = "BoxCollider" }) });
                await C("create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Ceiling", ["parent"] = corridor, ["position"] = new JsonArray(x, 3.1, z + 7), ["scale"] = new JsonArray(4, 0.2, 14) });
                await C("place_grid", new JsonObject { ["source"] = "cube", ["origin"] = new JsonArray(x - 2.1, 1.5, z + 1), ["rows"] = 7, ["cols"] = 1, ["spacing"] = new JsonArray(0, 2), ["scale"] = new JsonArray(0.2, 3, 2), ["name"] = "Left Wall", ["parent"] = corridor });
                await C("place_grid", new JsonObject { ["source"] = "cube", ["origin"] = new JsonArray(x + 2.1, 1.5, z + 1), ["rows"] = 7, ["cols"] = 1, ["spacing"] = new JsonArray(0, 2), ["scale"] = new JsonArray(0.2, 3, 2), ["name"] = "Right Wall", ["parent"] = corridor });
                var walls = (await C("find_entities", new JsonObject { ["under"] = corridor, ["component"] = "MeshRenderer", ["limit"] = 100 }))["entities"].AsArray().Select(e => (string)e["id"]).ToArray();
                await C("assign_material", new JsonObject { ["entities"] = new JsonArray(walls.Select(w => (JsonNode)w).ToArray()), ["material"] = (string)mat["path"] });

                // ---- ceiling lights + flicker
                var lights = await C("place_grid", new JsonObject { ["source"] = "point_light", ["origin"] = new JsonArray(x, 2.8, z + 2), ["rows"] = 4, ["cols"] = 1, ["spacing"] = new JsonArray(0, 3.5), ["name"] = "Ceiling Lights", ["parent"] = corridor });
                var bulk = await C("bulk_set_properties", new JsonObject { ["component"] = "Light", ["under"] = (string)lights["group"]["id"], ["properties"] = new JsonObject { ["intensity"] = 2.5, ["range"] = 6, ["color"] = "#ffd39a" } });
                if ((int)bulk["changed"] != 4) return Fail("bulk light edit changed " + bulk["changed"]);
                var script = await C("write_script", new JsonObject { ["script"] = "LampFlicker", ["code"] = Flicker });
                files.Add(Path.Combine(project.Path, "Assets", "Scripts", "LampFlicker.cs"));
                if ((bool)script["compiles"] != true) return Fail("the flicker script does not compile: " + script.ToJsonString());
                foreach (var id in lights["first_ids"].AsArray().Select(n => (string)n))
                    await C("attach_script", new JsonObject { ["entity"] = id, ["script"] = "LampFlicker", ["fields"] = new JsonObject { ["BaseIntensity"] = 2.5, ["Speed"] = 7 } });

                // ---- debris on the floor (rays start below the ceiling)
                var debris = await C("scatter", new JsonObject
                {
                    ["source"] = "cube", ["area_min"] = new JsonArray(x - 1.6, z + 1), ["area_max"] = new JsonArray(x + 1.6, z + 13), ["count"] = 24, ["seed"] = 11,
                    ["scale_range"] = new JsonArray(0.08, 0.25), ["from_height"] = 2.9, ["min_distance"] = 0.3, ["name"] = "Debris", ["parent"] = corridor,
                });
                if ((int)debris["created"] < 20) return Fail("too little debris: " + debris.ToJsonString());

                // ---- sound: a looping generated hum
                var hum = await C("generate_sound", new JsonObject { ["prompt"] = "low droning ventilation hum in an empty concrete corridor", ["duration"] = 6, ["loop"] = true, ["backend"] = "procedural", ["name"] = "Corridor Hum" });
                files.Add(Path.Combine(project.Path, (string)hum["path"]));
                var amb = await C("create_entity", new JsonObject { ["kind"] = "empty", ["name"] = "Ambience", ["parent"] = corridor, ["position"] = new JsonArray(x, 1.5, z + 7) });
                await C("configure_audio_source", new JsonObject { ["entity"] = (string)amb["id"], ["properties"] = new JsonObject { ["clip"] = (string)hum["path"], ["loop"] = true, ["spatial"] = true, ["max_distance"] = 18, ["bus"] = "Ambience", ["volume"] = 0.6, ["play_on_awake"] = true } });

                // ---- look at it
                await C("focus_camera", new JsonObject { ["position"] = new JsonArray(x, 1.6, z - 0.5), ["look_at"] = new JsonArray(x, 1.2, z + 12) });
                var shot = await C("capture_viewport", new JsonObject { ["max_size"] = 1024 });

                // ---- verify the scene
                var all = SceneModel.All(project.ActiveScene).ToList();
                var lightEntities = all.Where(e => e.GetComponent<Light>() != null && e.Parent?.Name == "Ceiling Lights").ToList();
                if (lightEntities.Count != 4 || lightEntities.Any(l => l.GetComponent<Script>()?.ScriptClassName != "LampFlicker" || Math.Abs(l.GetComponent<Light>().ColorB - 0.604f) > 0.01f))
                    return Fail("ceiling lights are not set up (" + lightEntities.Count + ")");
                var ambience = all.FirstOrDefault(e => e.Name == "Ambience")?.GetComponent<AudioSource>();
                if (ambience == null || !ambience.Loop || ambience.OutputBus != 3) return Fail("the ambience source is not set up");
                var debrisEntities = all.Where(e => e.Parent?.Name == "Debris").ToList();
                foreach (var d in debrisEntities)
                {
                    Bounds.Of(new[] { d }, out var mn, out var mx);
                    if (Math.Abs(mn.Y) > 0.02f) return Fail("debris does not rest on the floor (bottom " + mn.Y + ")");
                }
                int steps = undo.UndoCount - undoBefore;
                log.Log("claude corridor demo: OK — " + all.Count(e => IsUnder(e, "Corridor")) + " entities, 4 flickering lights, " + debrisEntities.Count + " debris, looping ambience, " + steps + " undo steps → claude_corridor.jpg");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                while (undo.UndoCount > undoBefore && undo.Undo()) { }
                EditorCommands.AfterSceneEdit();
                foreach (var f in files)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    try { if (File.Exists(f + ".vmeta")) File.Delete(f + ".vmeta"); } catch { }
                }
            }
        }

        private static bool IsUnder(Editor.ECS.GameEntity e, string name)
        {
            for (var p = e; p != null; p = p.Parent) if (p.Name == name) return true;
            return false;
        }
    }
}
