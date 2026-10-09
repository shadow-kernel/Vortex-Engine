using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.AI;
using Editor.ECS;
using Editor.ECS.Components.AI;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;

namespace VortexEditor.Shell
{
    /// <summary>
    /// A template-authoring helper, not a check (#115): with <c>VORTEX_TEMPLATE_SETUP=1</c> it dresses the open scene for the
    /// Horror monster sample — bakes the navmesh, lays a four-waypoint <c>MonsterPatrol</c> route and a <c>HidingSpot</c>
    /// trigger around the player's start, places the Monster prefab a few metres away and saves the scene. The scene
    /// files are binary, so this is how the template's Demo scene received the sample. Without the variable it does
    /// nothing (and passes), so it is harmless in the CI gate.
    /// Run: <c>VORTEX_TEMPLATE_SETUP=1 VORTEX_SMOKE_FULL=1 VORTEX_SMOKE_ONLY="horror monster setup" Vortex.Editor
    /// --project=&lt;copy of Templates/HorrorStarter&gt; --scene=Demo --smoke=20</c>, then copy Demo.vscene + Demo.vnav back.
    /// </summary>
    internal static class TemplateSetupSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("horror monster setup", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            if (Environment.GetEnvironmentVariable("VORTEX_TEMPLATE_SETUP") != "1") { log.Log("horror monster setup: authoring helper — skipped (set VORTEX_TEMPLATE_SETUP=1 to run it)"); return true; }
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.LogError("horror monster setup: no scene"); return false; }
            if (!NavigationService.Available) { log.LogError("horror monster setup: no Recast in this build"); return false; }

            // 1) the navmesh — everything else samples points on it
            var bake = NavigationService.BakeScene(scene, NavigationService.SettingsFor(scene), save: true, load: true);
            if (!bake.Success) { log.LogError("horror monster setup: bake failed — " + bake.Message); return false; }
            log.Log("horror monster setup: navmesh " + (bake.Stats.PolyCount) + " polygons → " + bake.Path);
            await SmokeRegistry.Settle(200);

            var player = Find(scene, e => e.Name == "Player" || e.Tag == "Player");
            if (player == null) { log.LogError("horror monster setup: no Player entity in the scene"); return false; }
            var start = TransformMath.WorldPosition(player);
            log.Log("horror monster setup: player starts at " + F(start));

            // 2) the patrol route: four reachable points spread around the start
            var points = new List<Vector3>();
            var rng = new Random(7);
            for (int tries = 0; tries < 200 && points.Count < 4; tries++)
            {
                float radius = 9f + (float)rng.NextDouble() * 9f;
                if (!NavigationService.RandomPointAround(start, radius, out var p)) continue;
                if (Dist(p, start) < 4f) continue;
                bool spread = true;
                foreach (var q in points) if (Dist(p, q) < 4.5f) { spread = false; break; }
                if (spread) points.Add(p);
            }
            if (points.Count < 2) { log.LogError("horror monster setup: the navmesh offered " + points.Count + " reachable points around the player — is the scene's floor a collider?"); return false; }
            Remove(scene, "MonsterPatrol"); Remove(scene, "HidingSpot"); Remove(scene, "Monster");
            var route = scene.CreateEntity("MonsterPatrol");
            route.AddComponentDirect(new PatrolPath(route) { Mode = PatrolMode.Loop, UseChildren = true, WaitTime = 2.5f, ShowInGame = false });
            for (int i = 0; i < points.Count; i++)
            {
                var wp = scene.CreateEntity("Waypoint " + (i + 1));
                wp.Transform.LocalPosition = points[i];
                Reparent(scene, wp, route);
            }
            log.Log("horror monster setup: MonsterPatrol with " + points.Count + " waypoints");

            // 3) a hiding spot near the start: a trigger volume with the HidingSpot script and a dark floor mark
            Vector3 hidePos = start;
            for (int tries = 0; tries < 60; tries++)
            {
                if (!NavigationService.RandomPointAround(start, 6f, out var p)) continue;
                if (Dist(p, start) >= 2.5f) { hidePos = p; break; }
            }
            var hide = scene.CreateEntity("HidingSpot");
            hide.Transform.LocalPosition = new Vector3(hidePos.X, hidePos.Y + 1f, hidePos.Z);
            hide.AddComponentDirect(new BoxCollider(hide) { IsTrigger = true, Size = new Vector3(1.8f, 2f, 1.8f) });
            hide.AddComponentDirect(new Script(hide, "Assets/Scripts/AI/HidingSpot.cs") { ScriptClassName = "HidingSpot" });
            var mark = scene.CreateEntity("HidingSpotMark");
            mark.Transform.LocalPosition = new Vector3(0f, -0.97f, 0f);
            mark.Transform.LocalScale = new Vector3(1.8f, 0.04f, 1.8f);
            mark.AddComponentDirect(new MeshRenderer(mark) { MeshPath = "Primitive:Cube", ColorR = 0.05f, ColorG = 0.05f, ColorB = 0.07f, CastShadows = false });
            Reparent(scene, mark, hide);
            log.Log("horror monster setup: HidingSpot at " + F(hidePos));

            // 4) the monster, out of sight to begin with
            Vector3 monsterPos = points[points.Count - 1];
            for (int tries = 0; tries < 60; tries++)
            {
                if (!NavigationService.RandomPointAround(start, 16f, out var p)) continue;
                if (Dist(p, start) >= 9f) { monsterPos = p; break; }
            }
            GameEntity monster = null;
            try { monster = PrefabService.Instance.InstantiatePrefab("Assets/Prefabs/Monster.ventity", scene, null, undoable: false); }
            catch (Exception ex) { log.LogError("horror monster setup: could not place the Monster prefab — " + ex.Message); return false; }
            if (monster == null) { log.LogError("horror monster setup: Monster.ventity did not instantiate"); return false; }
            monster.Name = "Monster";
            monster.Transform.LocalPosition = monsterPos;
            log.Log("horror monster setup: Monster at " + F(monsterPos) + " (" + Dist(monsterPos, start).ToString("0.0") + " m from the player)");

            // 5) save
            EditorCommands.SaveScene(scene);
            SceneRenderService.RuntimeDirty = true;
            await SmokeRegistry.Settle(300);
            log.Log("horror monster setup: scene saved — copy Demo.vscene and Demo.vnav back into the template");
            return true;
        }

        internal static void Reparent(Scene scene, GameEntity child, GameEntity parent)
        {
            // CreateEntity adds a root entity; the route / spot own their markers
            scene.Entities.Remove(child);
            parent.AddChild(child);
        }

        internal static void Remove(Scene scene, string name)
        {
            var e = Find(scene, x => x.Name == name);
            if (e == null) return;
            if (e.Parent != null) e.Parent.Children.Remove(e); else scene.Entities.Remove(e);
        }

        internal static GameEntity Find(Scene scene, Func<GameEntity, bool> pred)
        {
            foreach (var e in scene.Entities) { var r = FindIn(e, pred); if (r != null) return r; }
            return null;
        }

        private static GameEntity FindIn(GameEntity e, Func<GameEntity, bool> pred)
        {
            if (e == null) return null;
            if (pred(e)) return e;
            if (e.Children != null) foreach (var c in e.Children) { var r = FindIn(c, pred); if (r != null) return r; }
            return null;
        }

        internal static float Dist(Vector3 a, Vector3 b) { float x = a.X - b.X, z = a.Z - b.Z; return (float)Math.Sqrt(x * x + z * z); }
        internal static string F(Vector3 v) => "(" + v.X.ToString("0.0") + ", " + v.Y.ToString("0.0") + ", " + v.Z.ToString("0.0") + ")";
    }
}
