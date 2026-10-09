using System;
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
    /// <summary>The Horror monster sample end to end (#115): in a project that carries it (Assets/AI/Monster.vbt and a
    /// "Monster" entity in the open scene — the Horror Starter's Demo scene), play mode must bring the navmesh up, start
    /// the monster's tree, register its perception and move it along the patrol route. Skips in every other project,
    /// so the CI gate (Default3D) passes it untouched; run it against a copy of the Horror template to verify the sample.</summary>
    internal static class MonsterSampleSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("monster sample", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string root = ProjectData.Current?.Path;
            if (scene == null || root == null || !File.Exists(Path.Combine(root, "Assets", "AI", "Monster.vbt"))) { log.Log("monster sample: this project has no Assets/AI/Monster.vbt — skipped"); return true; }
            var monster = Find(scene, "Monster");
            if (monster == null) { log.Log("monster sample: no Monster entity in the open scene — skipped"); return true; }
            if (!NavigationService.Available) { log.Log("monster sample: no Recast in this build — skipped"); return true; }
            if (monster.GetComponent<BehaviorTreeAgent>() == null || monster.GetComponent<NavAgent>() == null || monster.GetComponent<AIPerception>() == null)
            { log.LogError("monster sample: the Monster entity lacks Behavior Tree / Nav Agent / AI Perception"); return false; }

            bool ok = true, playing = false;
            var start = TransformMath.WorldPosition(monster);
            try
            {
                EditorCommands.Play();
                playing = true;
                await SmokeRegistry.Settle(900);
                if (!NavigationService.IsLoaded) { log.LogError("monster sample: the scene's navmesh did not load (Demo.vnav missing?)"); ok = false; }
                var runner = BehaviorTreeService.RunnerOf(monster);
                if (runner == null) { log.LogError("monster sample: the monster's behaviour tree did not start"); return false; }
                await SmokeRegistry.Settle(3600);
                var now = TransformMath.WorldPosition(monster);
                float dx = now.X - start.X, dz = now.Z - start.Z;
                float moved = (float)Math.Sqrt(dx * dx + dz * dz);
                log.Log("monster sample: tick " + runner.TickIndex + ", active task '" + runner.ActiveTaskLabel + "', moved " + moved.ToString("0.00") + " m, blackboard: " + string.Join(", ", System.Linq.Enumerable.Select(runner.Blackboard.Dump(), kv => kv.Key + "=" + kv.Value)));
                if (runner.MissingTasks.Count > 0) { log.LogError("monster sample: tasks missing in Monster.vbt: " + string.Join(", ", runner.MissingTasks)); ok = false; }
                if (runner.TickIndex < 30) { log.LogError("monster sample: the tree barely ticked (" + runner.TickIndex + ")"); ok = false; }
                if (!runner.Blackboard.Has("PatrolPoint")) { log.LogError("monster sample: PatrolNext never wrote a waypoint — MonsterPatrol route missing?"); ok = false; }
                if (moved < 0.5f) { log.LogError("monster sample: the monster did not walk (" + moved.ToString("0.00") + " m in 3.6 s) — navmesh / Nav Agent?"); ok = false; }
            }
            finally
            {
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(400); }
            }
            return ok;
        }

        private static GameEntity Find(Scene scene, string name)
        {
            foreach (var e in scene.Entities) { var r = FindIn(e, name); if (r != null) return r; }
            return null;
        }

        private static GameEntity FindIn(GameEntity e, string name)
        {
            if (e == null) return null;
            if (e.Name == name) return e;
            if (e.Children != null) foreach (var c in e.Children) { var r = FindIn(c, name); if (r != null) return r; }
            return null;
        }
    }
}
