using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.AI;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.AI;
using Editor.ECS;
using Editor.ECS.Components.AI;

namespace VortexEditor.Shell
{
    /// <summary>Behaviour trees end to end (#111): a .vbt written to the project, an entity with a Behavior Tree component,
    /// play mode — the script runtime starts the AI runtime, the tree ticks, its tasks write the blackboard — and the
    /// Behavior Tree editor opened on the asset sees the live runner. Needs no GPU and no scripts.</summary>
    internal static class BehaviorTreeSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("behavior tree", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string root = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(root)) { log.Log("behavior tree: no project / scene — skipped"); return true; }

            string rel = "Assets/AI/SmokeTree.vbt";
            string full = Path.Combine(root, "Assets", "AI", "SmokeTree.vbt");
            var asset = BehaviorTreeAsset.NewDefault("SmokeTree");
            var seq = new BtNodeData { Kind = BtNodeKind.Sequence, Name = "Idle" };
            seq.Children.Add(new BtNodeData { Kind = BtNodeKind.Task, Task = "Wait", Params = { ["seconds"] = "0.1" } });
            seq.Children.Add(new BtNodeData { Kind = BtNodeKind.Task, Task = "SetValue", Params = { ["key"] = "Done", ["value"] = "yes" } });
            seq.Children.Add(new BtNodeData { Kind = BtNodeKind.Task, Task = "Log", Params = { ["message"] = "smoke tree ran ({Done})" } });
            asset.Root.Children.Add(seq);
            if (!asset.Save(full)) { log.LogError("behavior tree: could not write " + rel); return false; }

            GameEntity agent = null;
            bool ok = true, playing = false;
            BehaviorTreeEditorWindow window = null;
            try
            {
                agent = EditorCommands.CreateEmpty();
                if (agent == null) { log.LogError("behavior tree: could not create the agent entity"); return false; }
                agent.Name = "SmokeAgent";
                agent.AddComponent(new BehaviorTreeAgent(agent) { TreePath = rel });
                await SmokeRegistry.Settle(200);

                EditorCommands.Play();
                playing = true;
                await SmokeRegistry.Settle(1200);
                if (!BehaviorTreeService.IsRunning || BehaviorTreeService.Count < 1) { log.LogError("behavior tree: no runner after play start (running=" + BehaviorTreeService.IsRunning + ", count=" + BehaviorTreeService.Count + ")"); ok = false; }
                var runner = BehaviorTreeService.RunnerOf(agent);
                if (runner == null) { log.LogError("behavior tree: the agent entity has no runner"); ok = false; }
                else
                {
                    log.Log("behavior tree: runner tick " + runner.TickIndex + ", status " + runner.LastStatus + ", blackboard Done=" + runner.Blackboard.GetString("Done", "(unset)") + ", missing=" + runner.MissingTasks.Count);
                    if (runner.TickIndex < 5) { log.LogError("behavior tree: the tree barely ticked (" + runner.TickIndex + ")"); ok = false; }
                    if (runner.Blackboard.GetString("Done") != "yes") { log.LogError("behavior tree: SetValue did not write the blackboard"); ok = false; }
                    if (runner.MissingTasks.Count > 0) { log.LogError("behavior tree: built-in tasks were not found: " + string.Join(", ", runner.MissingTasks)); ok = false; }
                }
                // the editor on the asset finds the live runner
                BehaviorTreeEditorWindow.Open(full);
                window = BehaviorTreeEditorWindow.Find(full);
                await SmokeRegistry.Settle(500);
                if (window == null) { log.LogError("behavior tree: the editor window did not open"); ok = false; }
                else if (!window.Title.StartsWith("Behavior Tree", StringComparison.Ordinal)) { log.LogError("behavior tree: unexpected window title '" + window.Title + "'"); ok = false; }
                if (BehaviorTreeService.FirstRunnerOf(rel) == null) { log.LogError("behavior tree: FirstRunnerOf did not find the runner by project-relative path"); ok = false; }
            }
            finally
            {
                try { window?.CloseDiscarding(); } catch { }
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(300); }
                if (BehaviorTreeService.Count != 0) { log.LogError("behavior tree: Stop left " + BehaviorTreeService.Count + " runners"); ok = false; }
                if (agent != null) EditorCommands.DeleteEntities(new[] { agent });
                try { File.Delete(full); } catch { }
            }
            return ok;
        }
    }
}
