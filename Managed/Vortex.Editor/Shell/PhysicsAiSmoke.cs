using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.AI;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.AI;
using Editor.ECS.Components.Physics;
using SysVec = System.Numerics.Vector3;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Smoke checks of the physics queries (#480) and the navigation features of #434 / #435, each on its own in-memory
    /// level (the editor's scene, navmesh and physics worlds are left as they were):
    /// <list type="bullet">
    /// <item>"physics shape casts" — sphere / capsule / box casts, RaycastAll and the overlaps through the script API on the
    /// Jolt world and on the managed collision world (a layer mask routes there): the same entities at the same
    /// distances, and the Queries debug layer records them.</item>
    /// <item>"navigation update position off" — the agent plans and steers, the game moves the entity by its desired
    /// velocity, the agent follows and arrives.</item>
    /// <item>"navigation dynamic obstacle" — a door's obstacle carves the doorway; shut in front of a walking agent, the agent
    /// re-paths and arrives the other way.</item>
    /// </list>
    /// </summary>
    internal static class PhysicsAiSmoke
    {
        private const float Dt = 1f / 60f;

        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("physics shape casts: sphere / capsule / box casts, RaycastAll and overlaps agree on Jolt and the collision world", ShapeCasts);
            SmokeRegistry.Add("navigation update position off: the agent plans and steers, the game moves the entity", UpdatePositionOff);
            SmokeRegistry.Add("navigation dynamic obstacle: a door shuts in front of an agent, it re-paths and arrives the other way", DynamicObstacle);
            SmokeRegistry.Add("navigation moving obstacles: a cart pushed beside a guard's way, 36 crates moving at once, a crate dropped on a guard", MovingObstacles);
        }

        private static void Log(string s) => ConsoleService.Instance.Log("[smoke] " + s);
        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static GameEntity Add(Scene s, string name, Vector3 pos, Action<GameEntity> setup)
        {
            var e = new GameEntity(name);   // no engine entity: transform writes stay in the managed scene
            e.Transform.LocalPosition = pos;
            setup?.Invoke(e);
            s.Entities.Add(e);
            return e;
        }

        private static GameEntity Box(Scene s, string name, Vector3 pos, Vector3 size)
            => Add(s, name, pos, e => e.AddComponentDirect(new BoxCollider(e) { Size = size }));

        // ------------------------------------------------------------------------------------------ physics (#480)

        private static bool ShapeCasts()
        {
            if (PlayModeService.Instance.IsPlaying) { ConsoleService.Instance.LogWarning("[smoke] physics shape casts: play mode runs"); return false; }
            bool collisionWasBuilt = CollisionService.IsBuilt;   // the editor keeps the last play's collision world
            var s = new Scene { Name = "SmokeQueries" };
            Box(s, "Floor", new Vector3(0, -0.5f, 0), new Vector3(30, 1, 30));
            Box(s, "Crate", new Vector3(0, 0.5f, 3f), new Vector3(1, 1, 1));
            Add(s, "Ball", new Vector3(3f, 1f, 0), e => e.AddComponentDirect(new SphereCollider(e) { Radius = 0.5f }));
            Add(s, "Pillar", new Vector3(-3f, 0.9f, 0), e => e.AddComponentDirect(new CapsuleCollider(e) { Radius = 0.35f, Height = 1.8f }));
            Box(s, "Glass", new Vector3(-5f, 1f, 8f), new Vector3(0.05f, 2, 2));
            Box(s, "Guard", new Vector3(0f, 1f, 8f), new Vector3(0.6f, 2, 0.6f));
            Box(s, "BackWall", new Vector3(5f, 1f, 8f), new Vector3(0.5f, 2, 4));
            bool debugWas = PhysicsService.ShowPhysicsDebug;
            var layersWas = PhysicsService.DebugLayers;
            try
            {
                CollisionService.Build(s);
                PhysicsService.Build(s);
                if (!PhysicsService.IsBuilt) { ConsoleService.Instance.LogWarning("[smoke] no Jolt world (engine without Jolt)"); return false; }
                bool ok = true;
                void Expect(bool cond, string what) { if (!cond) { ok = false; ConsoleService.Instance.LogError("[smoke] physics shape casts: " + what); } }
                const int Managed = 0x7FFFFFFF;   // any mask but ~0 is answered by the collision world (entity layer bits)
                foreach (int mask in new[] { ~0, Managed })
                {
                    string path = mask == ~0 ? "Jolt" : "collision world";
                    var fwd = new Vortex.Vector3(0, 0, 1);
                    bool sphere = Vortex.Physics.SphereCast(new Vortex.Vector3(0, 0.5f, 0), 0.3f, fwd, 10f, out var sh, mask);
                    Expect(sphere && sh.Name == "Crate" && Math.Abs(sh.Distance - 2.2f) < 0.02f, path + ": the sphere cast stops 2.2 m on at the crate (" + sh.Name + " " + F(sh.Distance) + ")");
                    bool capsule = Vortex.Physics.CapsuleCast(new Vortex.Vector3(3f, 0.4f, -3f), new Vortex.Vector3(3f, 1.6f, -3f), 0.3f, fwd, 10f, out var ch, mask);
                    Expect(capsule && ch.Name == "Ball" && Math.Abs(ch.Distance - 2.2f) < 0.02f, path + ": the capsule cast stops at the ball (" + ch.Name + " " + F(ch.Distance) + ")");
                    bool box = Vortex.Physics.BoxCast(new Vortex.Vector3(-3f, 0.9f, -3f), new Vortex.Vector3(0.25f, 0.25f, 0.25f), default(Vortex.Vector3), fwd, 10f, out var bh, mask);
                    Expect(box && bh.Name == "Pillar" && Math.Abs(bh.Distance - 2.4f) < 0.02f, path + ": the box cast stops at the pillar (" + bh.Name + " " + F(bh.Distance) + ")");
                    var all = Vortex.Physics.RaycastAll(new Vortex.Vector3(-10f, 1f, 8f), new Vortex.Vector3(1, 0, 0), 30f, mask);
                    Expect(all.Length == 3 && all[0].Name == "Glass" && all[1].Name == "Guard" && all[2].Name == "BackWall"
                        && all[0].Distance < all[1].Distance && all[1].Distance < all[2].Distance, path + ": RaycastAll hits glass, guard and wall in order (" + all.Length + ")");
                    var inBox = Vortex.Physics.OverlapBox(new Vortex.Vector3(0, 0.5f, 3f), new Vortex.Vector3(0.6f, 0.4f, 0.6f), default(Vortex.Vector3), mask);
                    var inCapsule = Vortex.Physics.OverlapCapsule(new Vortex.Vector3(3f, 0.4f, 0), new Vortex.Vector3(3f, 2f, 0), 0.2f, mask);
                    var inSphere = Vortex.Physics.OverlapSphere(new Vortex.Vector3(-3f, 0.9f, 0.5f), 0.3f, mask);
                    Expect(inBox.Length == 1 && inCapsule.Length == 1 && inSphere.Length == 1, path + ": each overlap finds its one collider ("
                        + inBox.Length + ", " + inCapsule.Length + ", " + inSphere.Length + ")");
                    Log("physics shape casts on " + path + ": sphere " + sh.Name + " " + F(sh.Distance) + ", capsule " + ch.Name + " " + F(ch.Distance) + ", box "
                        + bh.Name + " " + F(bh.Distance) + ", RaycastAll " + all.Length + " hits");
                }
                // the Queries debug layer records script queries for the next debug draw
                PhysicsService.SetDebugDraw(true, PhysicsService.DebugLayer.Queries);
                Vortex.Physics.SphereCast(new Vortex.Vector3(0, 0.5f, 0), 0.3f, new Vortex.Vector3(0, 0, 1), 10f, out _);
                Expect(PhysicsService.RecordedQueryCount >= 1, "the Queries debug layer records the sweep");
                PhysicsService.SubmitDebugDraw();
                Expect(PhysicsService.RecordedQueryCount == 0, "… and draws it once");
                return ok;
            }
            finally
            {
                PhysicsService.SetDebugDraw(debugWas, layersWas);
                PhysicsService.Clear();
                var active = ProjectData.Current?.ActiveScene;
                if (collisionWasBuilt && active != null) CollisionService.Build(active); else CollisionService.Clear();
            }
        }

        // ------------------------------------------------------------------------------------------ navigation (#434 / #435)

        /// <summary>A 30 m floor with a 12 m wall across the middle (x -0.5..0.5, z -6..6), baked and loaded, the runtime
        /// begun; the caller's entities are added first. Restore with <see cref="EndLevel"/>.</summary>
        private static Scene Level(Action<Scene> extra, out Scene previous, out string error)
        {
            previous = NavigationService.LoadedScene;
            error = null;
            var s = new Scene { Name = "SmokeNavigation", IsActive = true };
            Box(s, "Floor", new Vector3(0, -0.5f, 0), new Vector3(30, 1, 30));
            Box(s, "Wall", new Vector3(0, 1f, 0), new Vector3(1, 2, 12));
            extra?.Invoke(s);
            var settings = NavigationService.DefaultSettings();
            var geo = NavigationService.CollectGeometry(s, settings.SourceFlags);
            if (geo.BoxCount != 2) { error = "the bake input is the floor and the wall only (" + geo.BoxCount + " boxes)"; return null; }
            var r = NavigationService.Bake(geo, settings);
            if (!r.Success || !NavigationService.LoadData(r.Data, s)) { error = "bake / load: " + r.Message; return null; }
            NavigationService.Begin(s);
            return s;
        }

        private static void EndLevel(Scene previous)
        {
            NavigationService.End();
            if (previous != null) NavigationService.LoadForScene(previous); else NavigationService.Unload();
        }

        private static SysVec Feet(GameEntity e) { var p = e.Transform.LocalPosition; return new SysVec(p.X, p.Y, p.Z); }

        private static bool UpdatePositionOff()
        {
            if (!NavigationService.Available) { ConsoleService.Instance.LogWarning("[smoke] navigation unavailable: " + NavigationService.UnavailableReason); return false; }
            if (NavigationService.IsRunning) { ConsoleService.Instance.LogWarning("[smoke] navigation runs (play mode?)"); return false; }
            GameEntity guard = null;
            var s = Level(sc => guard = Add(sc, "Guard", new Vector3(-5f, 0, 0), e => e.AddComponentDirect(new NavAgent(e)
                { UpdatePosition = false, ResolveCollisions = false, Speed = 3.5f, Radius = 0.4f })), out var previous, out var error);
            try
            {
                if (s == null) { ConsoleService.Instance.LogError("[smoke] " + error); return false; }
                if (!NavigationService.SetDestination(guard, new Vector3(5f, 0, 0))) return false;
                for (int i = 0; i < 30; i++) NavigationService.Tick(Dt);
                bool stayed = Feet(guard) == new SysVec(-5f, 0, 0);
                var dv = NavigationService.GetDesiredVelocity(guard);
                float speed = (float)Math.Sqrt(dv.X * dv.X + dv.Z * dv.Z);
                int arrived = -1;
                float clearance = float.MaxValue;
                for (int i = 0; i < 60 * 10 && arrived < 0; i++)
                {
                    var v = NavigationService.GetDesiredVelocity(guard);   // the game's own step
                    var p = Feet(guard);
                    guard.Transform.LocalPosition = new Vector3(p.X + v.X * Dt, 0f, p.Z + v.Z * Dt);
                    NavigationService.Tick(Dt);
                    var f = Feet(guard);
                    clearance = Math.Min(clearance, Math.Max(Math.Abs(f.X) - 0.5f, Math.Abs(f.Z) - 6f));
                    if (NavigationService.HasArrived(guard)) arrived = i;
                }
                Log("update position off: entity untouched by the ticks " + stayed + ", desired speed " + F(speed) + " m/s, arrived after "
                    + F(arrived / 60f) + " s, wall clearance " + F(clearance));
                return stayed && speed > 3f && arrived > 0 && clearance > 0.1f && (Feet(guard) - new SysVec(5f, 0, 0)).Length() < 0.6f;
            }
            finally { EndLevel(previous); }
        }

        private static bool DynamicObstacle()
        {
            if (!NavigationService.Available) { ConsoleService.Instance.LogWarning("[smoke] navigation unavailable: " + NavigationService.UnavailableReason); return false; }
            if (NavigationService.IsRunning) { ConsoleService.Instance.LogWarning("[smoke] navigation runs (play mode?)"); return false; }
            GameEntity door = null, guard = null;
            var s = Level(sc =>
            {
                // the doorway north of the wall (z 6.2 .. 14.8): a door with a collider and a carving obstacle (left out of the
                // bake), switched off — open — at the start
                door = Add(sc, "Door", new Vector3(0, 1f, 10.5f), e =>
                {
                    e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(1f, 2f, 8.6f) });
                    e.AddComponentDirect(new NavMeshObstacle(e) { Size = new Vector3(1f, 2f, 8.6f) });
                });
                guard = Add(sc, "Guard", new Vector3(-5f, 0, 3f), e => e.AddComponentDirect(new NavAgent(e) { ResolveCollisions = false, Speed = 3.5f, Radius = 0.4f }));
            }, out var previous, out var error);
            try
            {
                if (s == null) { ConsoleService.Instance.LogError("[smoke] " + error); return false; }
                NavigationService.SetObstacleEnabled(door, 0, false);
                NavigationService.Tick(Dt);
                bool carved = NavigationService.FlushRebuilds() && NavigationService.IsObstacleReady(door, 0);
                if (!NavigationService.SetDestination(guard, new Vector3(5f, 0, 3f))) return false;
                for (int i = 0; i < 40; i++) NavigationService.Tick(Dt);
                var at = Feet(guard);
                NavigationService.SetObstacleEnabled(door, 0, true);   // shut in front of it
                var corners = new List<Vector3>();
                var status = NavigationService.CalculatePath(new Vector3(-5f, 0, 3f), new Vector3(5f, 0, 3f), corners);
                float south = 0f;
                foreach (var c in corners) south = Math.Min(south, c.Z);
                float maxZ = float.MinValue;
                int arrived = -1;
                for (int i = 0; i < 60 * 15 && arrived < 0; i++)
                {
                    NavigationService.Tick(Dt);
                    maxZ = Math.Max(maxZ, Feet(guard).Z);
                    if (NavigationService.HasArrived(guard)) arrived = i;
                }
                var st = NavigationService.GetRebuildStats();
                Log("dynamic obstacle: door carved " + carved + " (" + st.TilesSwapped + " tiles, slowest build " + F(st.BuildMsMax) + " ms on the worker, slowest swap "
                    + F(st.SwapMsMax) + " ms, geometry gathered in " + F(NavigationService.SourceGatherMs) + " ms); shut with the guard at z " + F(at.Z)
                    + ": path via z " + F(south) + ", guard no further north than z " + F(maxZ) + ", arrived after " + F(arrived / 60f) + " s");
                return carved && at.Z > 3.3f && status == NavPathStatus.Complete && south < -6f && maxZ < 6.2f && arrived > 0
                    && (Feet(guard) - new SysVec(5f, 0, 3f)).Length() < 0.6f;
            }
            finally { EndLevel(previous); }
        }
    
        /// <summary>#435 review, in real time: (1) a cart moved every frame beside a guard's way is only steered around while
        /// it moves — the guard is never re-planned into a standstill and arrives; (2) 36 crates whose obstacles re-carve
        /// as they move (Carve Only Stationary off) are all carved where they are while they keep moving — every tile gets its
        /// turn; (3) a crate dropped on a standing guard: the guard is placed beside it and, the crate gone, walks on.</summary>
        private static async Task<bool> MovingObstacles()
        {
            if (!NavigationService.Available) { ConsoleService.Instance.LogWarning("[smoke] navigation unavailable: " + NavigationService.UnavailableReason); return false; }
            if (NavigationService.IsRunning) { ConsoleService.Instance.LogWarning("[smoke] navigation runs (play mode?)"); return false; }
            bool ok = true;
            void Expect(bool cond, string what) { if (!cond) { ok = false; ConsoleService.Instance.LogError("[smoke] navigation moving obstacles: " + what); } }
            bool OnMesh(Vector3 q) => NavigationService.SamplePosition(q, 0.1f, out var h) && Math.Abs(h.X - q.X) < 0.05f && Math.Abs(h.Z - q.Z) < 0.05f;

            // (1) the cart beside the way
            GameEntity runner = null;
            var s = Level(sc => runner = Add(sc, "Runner", new Vector3(-5f, 0, 3f), e => e.AddComponentDirect(new NavAgent(e) { ResolveCollisions = false, Speed = 5f, Radius = 0.4f })),
                out var previous, out var error);
            if (s == null) { ConsoleService.Instance.LogError("[smoke] " + error); return false; }
            float arrived = -1f;
            try
            {
                NavigationService.Tick(Dt);
                NavigationService.SetDestination(runner, new Vector3(5f, 0, 3f));
                long cart = NavigationService.AddObstacle(new SysVec(2.5f, 1f, -0.5f), new SysVec(0.4f, 1f, 0.4f), System.Numerics.Quaternion.Identity, false, true);
                for (int i = 0; i < 60 * 20 && arrived < 0f; i++)
                {
                    NavigationService.MoveObstacle(cart, new SysVec(2.5f + (float)Math.Sin(i * 0.02), 1f, -0.5f), System.Numerics.Quaternion.Identity);
                    NavigationService.Tick(Dt);
                    if (NavigationService.HasArrived(runner)) arrived = i * Dt;
                    if (i % 6 == 0) await Task.Delay(1);
                }
                Expect(arrived > 0f && arrived < 5f, "the guard beside the pushed cart arrives (" + arrived + " s)");
            }
            finally { EndLevel(previous); }

            // (2) 36 moving crates, re-carved as they go
            previous = NavigationService.LoadedScene;
            var big = new Scene { Name = "SmokeCrates", IsActive = true };
            Box(big, "Floor", new Vector3(0, -0.5f, 0), new Vector3(96, 1, 96));
            var crates = new List<GameEntity>();
            for (int ix = 0; ix < 6; ix++)
                for (int iz = 0; iz < 6; iz++)
                    crates.Add(Add(big, "Crate" + ix + iz, new Vector3(-40f + 16f * ix, 1f, -40f + 16f * iz), e =>
                        e.AddComponentDirect(new NavMeshObstacle(e) { Size = new Vector3(2f, 2f, 2f), CarveOnlyStationary = false })));
            int carvedWhileMoving = 0;
            try
            {
                var settings = NavigationService.DefaultSettings();
                var geo = NavigationService.CollectGeometry(big, settings.SourceFlags);
                var r = NavigationService.Bake(geo, settings);
                if (!r.Success || !NavigationService.LoadData(r.Data, big)) { ConsoleService.Instance.LogError("[smoke] bake: " + r.Message); return false; }
                NavigationService.Begin(big);
                for (int frame = 0; frame < 150; frame++)
                {
                    foreach (var c in crates) { var p = c.Transform.LocalPosition; c.Transform.LocalPosition = new Vector3(p.X + 0.02f, p.Y, p.Z); }   // 1.2 m/s
                    NavigationService.Tick(Dt);
                    await Task.Delay(16);   // real frames: the worker builds meanwhile
                }
                foreach (var c in crates)
                {
                    var p = c.Transform.LocalPosition;
                    if (!OnMesh(new Vector3(p.X, 0f, p.Z))) carvedWhileMoving++;
                }
                var st = NavigationService.GetRebuildStats();
                Log("moving obstacles: 36 crates re-carved while moving for 2.5 s — " + carvedWhileMoving + " carved where they are (built " + st.TilesBuilt
                    + ", swapped " + st.TilesSwapped + ", slowest swap " + F(st.SwapMsMax) + " ms)");
                Expect(carvedWhileMoving == 36, "every moving crate is carved where it is (" + carvedWhileMoving + " of 36)");
            }
            finally { EndLevel(previous); }

            // (3) a crate dropped on a standing guard
            GameEntity guard = null;
            s = Level(sc => guard = Add(sc, "Guard", new Vector3(-5f, 0, 3f), e => e.AddComponentDirect(new NavAgent(e) { ResolveCollisions = false, Speed = 3.5f, Radius = 0.4f })),
                out previous, out error);
            if (s == null) { ConsoleService.Instance.LogError("[smoke] " + error); return false; }
            try
            {
                for (int i = 0; i < 5; i++) NavigationService.Tick(Dt);
                long crate = NavigationService.AddObstacle(new SysVec(-5f, 1f, 3f), new SysVec(2.6f, 1f, 2.6f), System.Numerics.Quaternion.Identity, false, true);
                NavigationService.Tick(Dt);
                NavigationService.FlushRebuilds();
                for (int i = 0; i < 30; i++) NavigationService.Tick(Dt);
                var beside = Feet(guard);
                bool placed = Math.Abs(beside.X + 5f) > 2.6f || Math.Abs(beside.Z - 3f) > 2.6f;
                NavigationService.RemoveObstacle(crate);
                NavigationService.Tick(Dt);
                NavigationService.FlushRebuilds();
                NavigationService.SetDestination(guard, new Vector3(5f, 0, -10f));
                float walked = -1f;
                for (int i = 0; i < 60 * 10 && walked < 0f; i++) { NavigationService.Tick(Dt); if (NavigationService.HasArrived(guard)) walked = i * Dt; }
                Log("moving obstacles: a cart pushed beside the way — guard arrived after " + F(arrived) + " s; a crate dropped on a guard — placed at ("
                    + F(beside.X) + ", " + F(beside.Z) + "), then it walked on and arrived after " + F(walked) + " s");
                Expect(placed, "the guard under the crate is placed beside it (" + beside + ")");
                Expect(walked > 0f, "the crate gone, the guard walks to its destination");
            }
            finally { EndLevel(previous); }
            return ok;
        }
    }
}
