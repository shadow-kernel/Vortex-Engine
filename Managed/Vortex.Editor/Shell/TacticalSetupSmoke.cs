using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.AI;
using Editor.ECS;
using Editor.ECS.Components.AI;

namespace VortexEditor.Shell
{
    /// <summary>
    /// A template-authoring helper, not a check (#193): with <c>VORTEX_TEMPLATE_SETUP=1</c> it dresses the open scene for the
    /// Tactical Shooter's combat bots — bakes the navmesh, lays a four-waypoint <c>BotPatrol</c> route, places
    /// <c>VORTEX_BOT_COUNT</c> (default 4) Bot prefabs on the far side of the range and saves the scene. Without the
    /// variable it does nothing (and passes). Run against a copy of Templates/TacticalShooter with <c>--scene=Range</c>,
    /// then copy Range.vscene + Range.vnav back.
    /// </summary>
    internal static class TacticalSetupSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("tactical bots setup", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            if (Environment.GetEnvironmentVariable("VORTEX_TEMPLATE_SETUP") != "1") { log.Log("tactical bots setup: authoring helper — skipped (set VORTEX_TEMPLATE_SETUP=1 to run it)"); return true; }
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.LogError("tactical bots setup: no scene"); return false; }
            if (!NavigationService.Available) { log.LogError("tactical bots setup: no Recast in this build"); return false; }
            int count = 4;
            int.TryParse(Environment.GetEnvironmentVariable("VORTEX_BOT_COUNT") ?? "4", out count);
            count = Math.Max(1, Math.Min(16, count));

            var bake = NavigationService.BakeScene(scene, NavigationService.SettingsFor(scene), save: true, load: true);
            if (!bake.Success) { log.LogError("tactical bots setup: bake failed — " + bake.Message); return false; }
            log.Log("tactical bots setup: navmesh " + bake.Stats.PolyCount + " polygons → " + bake.Path);
            await SmokeRegistry.Settle(200);

            var player = TemplateSetupSmoke.Find(scene, e => e.Name == "Player" || e.Tag == "Player");
            if (player == null) { log.LogError("tactical bots setup: no Player entity in the scene"); return false; }
            var start = TransformMath.WorldPosition(player);
            log.Log("tactical bots setup: player starts at " + TemplateSetupSmoke.F(start));

            // the route: four reachable points at a distance, spread out
            var rng = new Random(11);
            var points = new List<Vector3>();
            for (int tries = 0; tries < 300 && points.Count < 4; tries++)
            {
                if (!NavigationService.RandomPointAround(start, 12f + (float)rng.NextDouble() * 20f, out var p)) continue;
                if (TemplateSetupSmoke.Dist(p, start) < 8f) continue;
                bool spread = true;
                foreach (var q in points) if (TemplateSetupSmoke.Dist(p, q) < 6f) { spread = false; break; }
                if (spread) points.Add(p);
            }
            if (points.Count < 2) { log.LogError("tactical bots setup: the navmesh offered " + points.Count + " reachable points away from the player"); return false; }
            TemplateSetupSmoke.Remove(scene, "BotPatrol");
            for (int i = 1; i <= 16; i++) TemplateSetupSmoke.Remove(scene, "Bot " + i);
            var route = scene.CreateEntity("BotPatrol");
            route.AddComponentDirect(new PatrolPath(route) { Mode = PatrolMode.Loop, UseChildren = true, WaitTime = 3f, ShowInGame = false });
            for (int i = 0; i < points.Count; i++)
            {
                var wp = scene.CreateEntity("Waypoint " + (i + 1));
                wp.Transform.LocalPosition = points[i];
                TemplateSetupSmoke.Reparent(scene, wp, route);
            }
            log.Log("tactical bots setup: BotPatrol with " + points.Count + " waypoints");

            // the bots: spread over the far side, never within 12 m of the start
            int placed = 0;
            var spots = new List<Vector3>();
            for (int tries = 0; tries < 400 && placed < count; tries++)
            {
                if (!NavigationService.RandomPointAround(start, 14f + (float)rng.NextDouble() * 22f, out var p)) continue;
                if (TemplateSetupSmoke.Dist(p, start) < 12f) continue;
                bool spread = true;
                foreach (var q in spots) if (TemplateSetupSmoke.Dist(p, q) < 4f) { spread = false; break; }
                if (!spread) continue;
                GameEntity bot = null;
                try { bot = PrefabService.Instance.InstantiatePrefab("Assets/Prefabs/Bot.ventity", scene, null, undoable: false); }
                catch (Exception ex) { log.LogError("tactical bots setup: could not place the Bot prefab — " + ex.Message); return false; }
                if (bot == null) { log.LogError("tactical bots setup: Bot.ventity did not instantiate"); return false; }
                placed++;
                bot.Name = "Bot " + placed;
                bot.Transform.LocalPosition = p;
                bot.Transform.LocalRotation = new Vector3(0f, (float)(rng.NextDouble() * 360.0), 0f);
                spots.Add(p);
                log.Log("tactical bots setup: " + bot.Name + " at " + TemplateSetupSmoke.F(p) + " (" + TemplateSetupSmoke.Dist(p, start).ToString("0.0") + " m)");
            }
            if (placed == 0) { log.LogError("tactical bots setup: no spot for a bot"); return false; }

            EditorCommands.SaveScene(scene);
            SceneRenderService.RuntimeDirty = true;
            await SmokeRegistry.Settle(300);
            log.Log("tactical bots setup: scene saved with " + placed + " bots — copy Range.vscene and Range.vnav back into the template");
            return true;
        }
    }
}
