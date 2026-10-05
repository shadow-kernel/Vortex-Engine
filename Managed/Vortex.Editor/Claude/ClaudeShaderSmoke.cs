using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Native;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "claude shaders" (#87): the platform's shader template compiles through write_shader, a broken
    /// shader comes back with the compiler's errors (the fix loop), a material uses the shader through shader_asset,
    /// and Undo removes the shader file again.
    /// </summary>
    internal static class ClaudeShaderSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("claude shaders", Run);

        private static async Task<(JsonNode json, string text, bool error)> Call(string tool, JsonObject args)
        {
            var dict = new Dictionary<string, object>();
            using (var doc = JsonDocument.Parse(args.ToJsonString()))
                foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = p.Value.Clone();
            var r = await ToolHost.CallAsync(tool, dict, "smoke");
            string text = string.Join("\n", r.Content.Where(c => c.Text != null).Select(c => c.Text));
            JsonNode json = null;
            try { json = JsonNode.Parse(text); } catch { }
            return (json, text, r.IsError);
        }

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("claude shaders: " + why); return false; }
            var project = ProjectData.Current;
            if (project?.ActiveScene == null) return Fail("no scene");
            var undo = UndoRedoManager.Instance;
            int undoBefore = undo.UndoCount;
            string ext = NativeLoader.MaterialShaderExtension;
            string shader = Path.Combine(project.Path, "Assets", "Shaders", "SmokeGlow" + ext);
            string mat = Path.Combine(project.Path, "Assets", "Materials", "Smoke Glow.vmat");
            try
            {
                var tpl = await Call("get_shader_template", new JsonObject());
                string source = (string)tpl.json?["source"];
                if (tpl.error || string.IsNullOrEmpty(source) || (string)tpl.json["extension"] != ext) return Fail("template: " + tpl.text);

                var good = await Call("write_shader", new JsonObject { ["shader"] = "SmokeGlow", ["code"] = source });
                if (good.error || (bool?)good.json?["compiles"] != true) return Fail("the template does not compile: " + good.text);
                if (!File.Exists(shader)) return Fail("no shader file at " + shader);

                // break it: the compiler's message must come back
                string broken = source + "\nthis is not valid shader code ;\n";
                var bad = await Call("write_shader", new JsonObject { ["shader"] = "SmokeGlow", ["code"] = broken });
                string errors = (string)bad.json?["errors"];
                if (bad.error || (bool?)bad.json?["compiles"] != false || string.IsNullOrEmpty(errors)) return Fail("a broken shader should report compile errors: " + bad.text);
                var check = await Call("validate_shader", new JsonObject { ["shader"] = "Assets/Shaders/SmokeGlow" + ext });
                if ((bool?)check.json?["compiles"] != false) return Fail("validate_shader on the broken file: " + check.text);
                await Call("write_shader", new JsonObject { ["shader"] = "SmokeGlow", ["code"] = source });

                var m = await Call("create_material", new JsonObject
                {
                    ["name"] = "Smoke Glow",
                    ["properties"] = new JsonObject { ["shader_asset"] = "Assets/Shaders/SmokeGlow" + ext, ["base_color"] = "#40c0ff" },
                });
                if (m.error) return Fail("material with the shader: " + m.text);
                var again = await Call("validate_shader", new JsonObject { ["shader"] = "Assets/Shaders/SmokeGlow" + ext });
                if ((bool?)again.json?["compiles"] != true || !(again.json?["used_by"] as JsonArray ?? new JsonArray()).Any(x => ((string)x).EndsWith("Smoke Glow.vmat"))) return Fail("the material does not show as a user: " + again.text);

                while (undo.UndoCount > undoBefore && undo.Undo()) { }
                if (File.Exists(shader) || File.Exists(mat)) return Fail("Undo did not remove the shader / material files");
                string firstError = errors.Replace('\n', ' ');
                log.Log("claude shaders: OK — " + ext + " template compiles, errors come back (" + (firstError.Length > 400 ? firstError.Substring(0, 400) + "…" : firstError) + "), material link, undo");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                while (undo.UndoCount > undoBefore && undo.Undo()) { }
                EditorCommands.AfterSceneEdit();
                foreach (var f in new[] { shader, mat })
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    try { if (File.Exists(f + ".vmeta")) File.Delete(f + ".vmeta"); } catch { }
                }
            }
        }
    }
}
