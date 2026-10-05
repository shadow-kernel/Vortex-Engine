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
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "claude safety" (#94): dry runs change nothing (scene, undo stack, files), the operation log
    /// knows which later operations depend on an earlier one, an older operation reverts out of order, a written file
    /// reverts to its previous state, and the Operations window shows it all (claude_operations.png).
    /// </summary>
    internal static class ClaudeSafetySmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("claude safety", Run);

        private static async Task<JsonNode> Call(string tool, string json)
        {
            var args = new Dictionary<string, object>();
            using (var doc = JsonDocument.Parse(json))
                foreach (var p in doc.RootElement.EnumerateObject()) args[p.Name] = p.Value.Clone();
            var r = await ToolHost.CallAsync(tool, args, "smoke");
            string text = string.Join("\n", r.Content.Where(c => c.Text != null).Select(c => c.Text));
            if (r.IsError) throw new InvalidOperationException(tool + ": " + text);
            try { return JsonNode.Parse(text); } catch { return JsonValue.Create(text); }
        }

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("claude safety: " + why); return false; }
            var project = ProjectData.Current;
            if (project?.ActiveScene == null) return Fail("no scene");
            var undo = UndoRedoManager.Instance;
            int undoBefore = undo.UndoCount;
            string script = Path.Combine(project.Path, "Assets", "Scripts", "OpsScript.cs");
            string dryScript = Path.Combine(project.Path, "Assets", "Scripts", "OpsDry.cs");
            bool Exists(string name) => SceneModel.All(project.ActiveScene).Any(e => e.Name == name);
            ToolOperation Last() => OperationLog.Items[OperationLog.Items.Count - 1];
            try
            {
                await Call("create_entity", "{\"kind\":\"cube\",\"name\":\"Ops A\",\"position\":[0,0.5,-20]}");
                var opA = Last();
                await Call("create_entity", "{\"kind\":\"sphere\",\"name\":\"Ops B\",\"position\":[2,0.5,-20]}");
                var opB = Last();
                await Call("set_transform", "{\"entity\":\"Ops A\",\"position\":[5,0.5,-20]}");
                var opMove = Last();

                // ---- dry runs change nothing
                int steps = undo.UndoCount;
                var dry = await Call("create_entity", "{\"kind\":\"cube\",\"name\":\"Ops C\",\"dry_run\":true}");
                if ((bool?)dry["dry_run"] != true || !(dry["would_create"] as JsonArray).Any(x => ((string)x).EndsWith("Ops C"))) return Fail("dry run plan: " + dry.ToJsonString());
                if (Exists("Ops C") || undo.UndoCount != steps) return Fail("a dry run left something behind");
                var dryWrite = await Call("write_script", "{\"script\":\"OpsDry\",\"code\":\"using Vortex;\\npublic class OpsDry : VortexBehaviour { }\\n\",\"dry_run\":true}");
                if (File.Exists(dryScript) || undo.UndoCount != steps) return Fail("a dry-run script write left the file");
                if (!(dryWrite["files"] as JsonArray).Any(x => ((string)x) == "create Assets/Scripts/OpsDry.cs")) return Fail("dry-run file plan: " + dryWrite.ToJsonString());
                if (!Last().DryRun || Last().UndoStep != null) return Fail("the dry run is not logged as one");
                var dryMove = await Call("set_transform", "{\"entity\":\"Ops A\",\"position\":[9,9,9],\"dry_run\":true}");
                if (SceneModel.Resolve("Ops A").Transform.LocalPosition.X != 5f) return Fail("a dry-run move moved the entity");
                if (!(dryMove["would_change"] as JsonArray).Any(x => ((string)x).EndsWith("Ops A"))) return Fail("dry-run change plan: " + dryMove.ToJsonString());

                // ---- dependencies
                var depsA = OperationLog.DependentsOf(opA);
                if (!depsA.Contains(opMove)) return Fail("moving Ops A should depend on creating it");
                if (OperationLog.DependentsOf(opB).Count != 0) return Fail("nothing depends on Ops B");

                // ---- revert an older operation out of order
                if (!opB.CanRevert || !OperationLog.Revert(opB)) return Fail("Ops B could not be reverted");
                if (Exists("Ops B") || !Exists("Ops A") || SceneModel.Resolve("Ops A").Transform.LocalPosition.X != 5f) return Fail("out-of-order revert touched the wrong things");
                if (opB.CanRevert) return Fail("a reverted operation still offers Revert");

                // ---- files: logged with their diff, reverted to the previous state
                await Call("write_script", "{\"script\":\"OpsScript\",\"code\":\"using Vortex;\\npublic class OpsScript : VortexBehaviour\\n{\\n    public float A = 1f;\\n}\\n\"}");
                var opWrite = Last();
                if (opWrite.Files.Count != 1 || !opWrite.Files[0].Created || !File.Exists(script)) return Fail("script write not logged");
                await Call("edit_script", "{\"script\":\"OpsScript\",\"old_text\":\"public float A = 1f;\",\"new_text\":\"public float A = 2f;\\n    public float B = 3f;\"}");
                var opEdit = Last();
                var diff = LineDiff.Compute(opEdit.Files[0].Before, opEdit.Files[0].After);
                if (!diff.Any(d => d.kind == '-' && d.text.Contains("A = 1f")) || !diff.Any(d => d.kind == '+' && d.text.Contains("B = 3f"))) return Fail("diff: " + string.Join(" | ", diff.Select(d => d.kind + d.text)));
                if (!OperationLog.DependentsOf(opWrite).Contains(opEdit)) return Fail("editing a file should depend on creating it");
                OperationLog.Revert(opEdit);
                if (!File.ReadAllText(script).Contains("A = 1f")) return Fail("reverting the edit did not restore the file");
                OperationLog.Revert(opWrite);
                if (File.Exists(script)) return Fail("reverting the creation did not remove the file");

                ClaudeOperationsWindow.Open();
                await SmokeRegistry.Settle(600);
                var w = EditorCommands.Window?.OwnedWindows.OfType<ClaudeOperationsWindow>().FirstOrDefault();
                if (w == null) return Fail("no Operations window");
                SmokeRegistry.Capture(w, "claude_operations.png");
                w.Close();
                log.Log("claude safety: OK — dry runs leave no trace, dependencies found, out-of-order revert, file diff + revert");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                while (undo.UndoCount > undoBefore && undo.Undo()) { }
                EditorCommands.AfterSceneEdit();
                foreach (var f in new[] { script, dryScript })
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    try { if (File.Exists(f + ".vmeta")) File.Delete(f + ".vmeta"); } catch { }
                }
            }
        }
    }
}
