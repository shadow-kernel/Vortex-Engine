using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.AI;
using Editor.ECS;
using Editor.ECS.Components.AI;

namespace VortexEditor.Shell
{
    /// <summary>The Tactical Shooter's combat bots end to end (#193): in a project that carries them (Assets/AI/Bot.vbt
    /// and "Bot N" entities in the open scene — the Range), play mode must load the navmesh, start every bot's tree
    /// and move at least one bot along the route within a few seconds. Skips in every other project.</summary>
    internal static class BotsSampleSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("bots sample", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string root = ProjectData.Current?.Path;
            if (scene == null || root == null || !File.Exists(Path.Combine(root, "Assets", "AI", "Bot.vbt"))) { log.Log("bots sample: this project has no Assets/AI/Bot.vbt — skipped"); return true; }
            var bots = new List<GameEntity>();
            foreach (var e in scene.Entities) Collect(e, bots);
            if (bots.Count == 0) { log.Log("bots sample: no Bot entities in the open scene — skipped"); return true; }
            if (!NavigationService.Available) { log.Log("bots sample: no Recast in this build — skipped"); return true; }

            bool ok = true, playing = false;
            var starts = new Dictionary<GameEntity, Vector3>();
            foreach (var b in bots) starts[b] = TransformMath.WorldPosition(b);
            try
            {
                EditorCommands.Play();
                playing = true;
                await SmokeRegistry.Settle(900);
                if (!NavigationService.IsLoaded) { log.LogError("bots sample: the scene's navmesh did not load (Range.vnav missing?)"); ok = false; }
                int running = 0;
                foreach (var b in bots) if (BehaviorTreeService.RunnerOf(b) != null) running++;
                if (running != bots.Count) { log.LogError("bots sample: " + running + " of " + bots.Count + " bots run their tree"); ok = false; }
                await SmokeRegistry.Settle(3600);
                int moved = 0; float best = 0f; string tasks = "";
                foreach (var b in bots)
                {
                    var now = TransformMath.WorldPosition(b);
                    float dx = now.X - starts[b].X, dz = now.Z - starts[b].Z;
                    float d = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (d > best) best = d;
                    if (d >= 0.5f) moved++;
                    var r = BehaviorTreeService.RunnerOf(b);
                    if (r != null) { tasks += (tasks.Length > 0 ? ", " : "") + b.Name + ": " + (r.ActiveTaskLabel == "" ? r.LastStatus.ToString() : r.ActiveTaskLabel); if (r.MissingTasks.Count > 0) { log.LogError("bots sample: tasks missing in Bot.vbt: " + string.Join(", ", r.MissingTasks)); ok = false; } }
                }
                log.Log("bots sample: " + bots.Count + " bots, " + moved + " moved (best " + best.ToString("0.00") + " m) — " + tasks);
                if (moved == 0) { log.LogError("bots sample: no bot walked in 3.6 s — navmesh / Nav Agent / BotPatrol route?"); ok = false; }
            }
            finally
            {
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(400); }
            }
            return ok;
        }

        private static void Collect(GameEntity e, List<GameEntity> into)
        {
            if (e == null) return;
            if (e.Name.StartsWith("Bot ", StringComparison.Ordinal) && e.GetComponent<BehaviorTreeAgent>() != null) into.Add(e);
            if (e.Children != null) foreach (var c in e.Children) Collect(c, into);
        }
    }
}
