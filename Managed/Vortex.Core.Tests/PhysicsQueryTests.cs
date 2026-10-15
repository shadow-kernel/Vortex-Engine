using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using SysQuat = System.Numerics.Quaternion;

namespace VortexTests
{
    /// <summary>#480 on the collision world (no Jolt, or an explicit entity-layer mask): sphere / capsule / box casts, all-hit
    /// rays and shape overlaps. ZJoltPhysicsTests casts the same queries through the Jolt world and compares the two paths.</summary>
    public static class PhysicsQueryTests
    {
        /// <summary>The test level, spread out so the queries never meet each other's colliders:
        /// two posts 0.3 m wide with a 5 cm gap between them at z = 3 (x = 0); a glass pane, a guard (capsule collider) and a
        /// wall in a row along +z at x = 10; a 2 m crate at x = 20 with a wall beyond it (face at x = 24.75); a trigger zone
        /// (x = 29..31) and a box on Layer 5 at x = 30; the closed mesh room at x = 39..41; a target wall (face z = 9.75) at
        /// x = 50 — all on a floor whose top is at y = 0.</summary>
        internal static Scene QueryScene(out Dictionary<string, GameEntity> e)
        {
            var s = new Scene { Name = "Queries" };
            var map = new Dictionary<string, GameEntity>();
            GameEntity Box(string name, Vector3 pos, Vector3 size, Action<GameEntity, BoxCollider> setup = null)
            {
                var ent = PhysicsRaycastTests.Add(s, null, name, pos, x =>
                {
                    var bc = new BoxCollider(x) { Size = size };
                    setup?.Invoke(x, bc);
                    x.AddComponentDirect(bc);
                });
                map[name] = ent;
                return ent;
            }
            Box("Floor", new Vector3(0, -0.5f, 0), new Vector3(140f, 1f, 60f));
            Box("PostL", new Vector3(-0.175f, 1f, 3f), new Vector3(0.3f, 2f, 0.3f));
            Box("PostR", new Vector3(0.175f, 1f, 3f), new Vector3(0.3f, 2f, 0.3f));
            Box("Glass", new Vector3(10f, 1f, 5f), new Vector3(2f, 2f, 0.02f));
            map["Guard"] = PhysicsRaycastTests.Add(s, null, "Guard", new Vector3(10f, 0, 7f), x =>
                x.AddComponentDirect(new CapsuleCollider(x) { Radius = 0.35f, Height = 1.8f, Center = new Vector3(0, 0.9f, 0) }));
            Box("Wall", new Vector3(10f, 1.5f, 10f), new Vector3(4f, 3f, 0.5f));
            Box("Crate", new Vector3(20f, 1f, 0f), new Vector3(2f, 2f, 2f));
            Box("Beyond", new Vector3(25f, 1.5f, 0f), new Vector3(0.5f, 3f, 4f));
            Box("Zone", new Vector3(30f, 1f, 0f), new Vector3(2f, 2f, 2f), (x, bc) => bc.IsTrigger = true);
            Box("Layered", new Vector3(30f, 0.5f, 5f), new Vector3(1f, 1f, 1f), (x, bc) => x.Layer = 5);
            map["Room"] = PhysicsRaycastTests.Add(s, null, "Room", new Vector3(40f, 0, 0), x =>
            {
                x.AddComponentDirect(new MeshRenderer(x) { MeshPath = "Assets/room.glb" });
                x.AddComponentDirect(new MeshCollider(x));
            });
            Box("Target", new Vector3(50f, 1.5f, 10f), new Vector3(4f, 3f, 0.5f));
            PhysicsRaycastTests.AsLoaded(s);
            e = map;
            return s;
        }

        internal static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static readonly Vector3 Fwd = new Vector3(0, 0, 1), Right = new Vector3(1, 0, 0), Up = new Vector3(0, 1, 0);

        /// <summary>Every query of this class on one path: the collision world (managed == true, entity layers, ~0 = all) or the
        /// Jolt world (solid layers).</summary>
        internal sealed class Path
        {
            public readonly bool Managed;
            public Path(bool managed) { Managed = managed; }
            public string Name => Managed ? "managed" : "Jolt";

            public bool Cast(QueryShape q, Vector3 dir, float max, out PhysicsQueryHit h, GameEntity ignore = null, bool triggers = false, int layerMask = ~0)
                => Managed ? CollisionService.ShapeCast(q, dir, max, layerMask, out h, ignore, triggers)
                           : PhysicsService.ShapeCast(q, dir, max, out h, PhysicsService.SolidLayerMask | (triggers ? 1u << PhysicsService.LayerTrigger : 0u), ignore);

            public List<PhysicsQueryHit> CastAll(QueryShape q, Vector3 dir, float max, GameEntity ignore = null)
            {
                var list = new List<PhysicsQueryHit>();
                if (Managed) CollisionService.ShapeCastAll(q, dir, max, ~0, list, ignore); else PhysicsService.ShapeCastAll(q, dir, max, list, PhysicsService.SolidLayerMask, ignore);
                return list;
            }

            public List<PhysicsQueryHit> RayAll(Vector3 o, Vector3 dir, float max, GameEntity ignore = null, bool triggers = false)
            {
                var list = new List<PhysicsQueryHit>();
                if (Managed) CollisionService.RaycastAll(o, dir, max, ~0, list, ignore, triggers);
                else PhysicsService.RaycastAll(o, dir, max, list, PhysicsService.SolidLayerMask | (triggers ? 1u << PhysicsService.LayerTrigger : 0u), ignore);
                return list;
            }

            public List<GameEntity> Overlap(QueryShape q, GameEntity ignore = null, bool triggers = false)
                => Managed ? CollisionService.OverlapShape(q, ~0, ignore, triggers)
                           : PhysicsService.OverlapShape(q, PhysicsService.SolidLayerMask | (triggers ? 1u << PhysicsService.LayerTrigger : 0u), ignore);

            public bool Check(QueryShape q, GameEntity ignore = null)
                => Managed ? CollisionService.CheckShape(q, ~0, ignore) : PhysicsService.CheckShape(q, PhysicsService.SolidLayerMask, ignore);
        }

        private static string Names(List<PhysicsQueryHit> hits)
        {
            var parts = new List<string>();
            foreach (var h in hits) parts.Add((h.Entity?.Name ?? "-") + "@" + h.Distance.ToString("0.###"));
            return string.Join(", ", parts);
        }

        private static string Names(List<GameEntity> hits) => string.Join(", ", hits.ConvertAll(x => x.Name));

        // ---------------------------------------------------------------------------------------------- the checks
        // Shared by both paths (ZJoltPhysicsTests runs them on the Jolt world). Distances: 2 cm between the paths at most —
        // Jolt rounds the edges of the level's boxes by up to 5 cm, which only shows where a sweep grazes an edge.

        internal const float Tol = 0.02f;

        /// <summary>#480's first acceptance check: a 0.2 m sphere from a camera pivot towards the 5 cm gap between two posts
        /// hits a post where a ray (and a 2 cm sphere) slips through.</summary>
        internal static void SphereCastCatchesTheGap(TestContext t, Path p, Dictionary<string, GameEntity> e)
        {
            var pivot = new Vector3(0, 1f, 0);
            var ray = p.RayAll(pivot, Fwd, 8f);
            t.Equal(0, ray.Count, p.Name + ": the ray through the gap hits nothing (" + Names(ray) + ")");
            t.False(p.Cast(QueryShape.Sphere(pivot, 0.02f), Fwd, 8f, out _), p.Name + ": a 2 cm sphere passes the 5 cm gap too");
            t.True(p.Cast(QueryShape.Sphere(pivot, 0.2f), Fwd, 8f, out var h), p.Name + ": a 0.2 m sphere does not");
            // touching the inner edge of a post: sqrt(0.025² + (2.85 - z)²) = 0.2
            float expect = 2.85f - (float)Math.Sqrt(0.2 * 0.2 - 0.025 * 0.025);
            t.True((h.Entity == e["PostL"] || h.Entity == e["PostR"]) && Math.Abs(h.Distance - expect) < Tol,
                   p.Name + ": it hits a post at " + expect.ToString("0.000") + " (" + h.Entity?.Name + " at " + h.Distance + ")");
            t.True(Dot(h.Normal, Fwd) < -0.5f, p.Name + ": the normal faces back against the sweep (" + h.Normal + ")");
            t.True(h.Point.Z > 2.84f && h.Point.Z < 2.87f && Math.Abs(h.Point.X) < 0.03f, p.Name + ": the point is on the post's inner edge (" + h.Point + ")");
        }

        /// <summary>#480's second check: a ray through a glass pane, a guard and a wall reports all three, nearest first, each
        /// with its point and its normal.</summary>
        internal static void RaycastAllReportsEveryColliderInOrder(TestContext t, Path p, Dictionary<string, GameEntity> e)
        {
            var hits = p.RayAll(new Vector3(10f, 1f, 0f), Fwd, 20f);
            t.Equal(3, hits.Count, p.Name + ": glass, guard, wall (" + Names(hits) + ")");
            if (hits.Count != 3) return;
            var want = new[] { ("Glass", 4.99f), ("Guard", 6.65f), ("Wall", 9.75f) };
            for (int i = 0; i < 3; i++)
            {
                t.True(hits[i].Entity == e[want[i].Item1] && Math.Abs(hits[i].Distance - want[i].Item2) < 1e-3f,
                       p.Name + ": hit " + i + " is the " + want[i].Item1 + " at " + want[i].Item2 + " (" + hits[i].Entity?.Name + " at " + hits[i].Distance + ")");
                t.True(hits[i].Normal.Z < -0.99f, p.Name + ": the " + want[i].Item1 + "'s normal faces the origin (" + hits[i].Normal + ")");
                t.True(Math.Abs(hits[i].Point.Z - want[i].Item2) < 1e-3f && Math.Abs(hits[i].Point.X - 10f) < 1e-3f && Math.Abs(hits[i].Point.Y - 1f) < 1e-3f,
                       p.Name + ": the " + want[i].Item1 + "'s point (" + hits[i].Point + ")");
            }
            var past = p.RayAll(new Vector3(10f, 1f, 0f), Fwd, 20f, e["Guard"]);
            t.True(past.Count == 2 && past[0].Entity == e["Glass"] && past[1].Entity == e["Wall"], p.Name + ": ignoring the guard leaves glass and wall (" + Names(past) + ")");
            var fromInside = p.RayAll(new Vector3(20.3f, 1f, 0f), Right, 10f);
            t.True(fromInside.Count == 1 && fromInside[0].Entity == e["Beyond"] && Math.Abs(fromInside[0].Distance - 4.45f) < 1e-3f,
                   p.Name + ": from inside the crate only what lies beyond it (" + Names(fromInside) + ")");
            var room = p.RayAll(new Vector3(35f, 1f, 0.2f), Right, 10f);
            t.True(room.Count == 1 && room[0].Entity == e["Room"] && Math.Abs(room[0].Distance - 4f) < 1e-3f,
                   p.Name + ": a ray through the closed mesh room reports it once, where it enters (" + Names(room) + ")");
        }

        /// <summary>Where a sweep starts: deep in a crate it is not stopped by the crate when it moves out, it is at 0 when it
        /// moves deeper; resting on the floor it is not stopped going up or sideways, it is at 0 going down — 5 cm into the
        /// floor, the same.</summary>
        internal static void SweepsThatStartInContact(TestContext t, Path p, Dictionary<string, GameEntity> e)
        {
            var inCrate = QueryShape.Sphere(new Vector3(20.5f, 1f, 0f), 0.3f);   // right of the crate's centre, deep inside it
            t.True(p.Cast(inCrate, Right, 10f, out var h) && h.Entity == e["Beyond"] && Math.Abs(h.Distance - 3.95f) < 1e-3f,
                   p.Name + ": moving out of the crate the sweep reports the wall beyond it (" + h.Entity?.Name + " at " + h.Distance + ")");
            t.True(p.Cast(inCrate, new Vector3(-1, 0, 0), 10f, out h) && h.Entity == e["Crate"] && h.Distance == 0f,
                   p.Name + ": moving deeper into the crate it is hit at 0 (" + h.Entity?.Name + " at " + h.Distance + ")");
            t.True(h.Normal.X > 0.99f, p.Name + ": … the normal is the way out (" + h.Normal + ")");
            var all = p.CastAll(inCrate, new Vector3(-1, 0, 0), 10f);
            t.True(all.Count >= 1 && all[0].Entity == e["Crate"] && all[0].Distance == 0f, p.Name + ": the all-hit sweep starts with the crate at 0 (" + Names(all) + ")");

            foreach (float y in new[] { 0.5f, 0.45f })
            {
                string how = y == 0.5f ? "resting on the floor" : "5 cm into the floor";
                var ball = QueryShape.Sphere(new Vector3(5f, y, -5f), 0.5f);
                t.False(p.Cast(ball, Up, 3f, out h), p.Name + ": " + how + ", a sweep up is free (" + h.Entity?.Name + ")");
                t.False(p.Cast(ball, Right, 3f, out h), p.Name + ": " + how + ", a sweep along the floor is free (" + h.Entity?.Name + ")");
                t.True(p.Cast(ball, new Vector3(0, -1, 0), 3f, out h) && h.Entity == e["Floor"] && h.Distance == 0f && h.Normal.Y > 0.99f,
                       p.Name + ": " + how + ", a sweep down hits it at 0 (" + h.Entity?.Name + " at " + h.Distance + ", " + h.Normal + ")");
            }
            var above = QueryShape.Sphere(new Vector3(5f, 1.5f, -5f), 0.5f);
            t.True(p.Cast(above, new Vector3(0, -1, 0), 3f, out h) && h.Entity == e["Floor"] && Math.Abs(h.Distance - 1f) < 1e-3f && Math.Abs(h.Point.Y) < 1e-3f,
                   p.Name + ": from 1 m above, the floor at 1 m, the point on it (" + h.Distance + ", " + h.Point + ")");
        }

        /// <summary>Capsule and box casts meet a wall at the distance of their leading face; a box turned 45° leads with an edge.</summary>
        internal static void CapsuleAndBoxCasts(TestContext t, Path p, Dictionary<string, GameEntity> e)
        {
            var guard = QueryShape.Capsule(new Vector3(50f, 0.4f, 0f), new Vector3(50f, 1.45f, 0f), 0.35f);
            t.True(p.Cast(guard, Fwd, 20f, out var h) && h.Entity == e["Target"] && Math.Abs(h.Distance - 9.4f) < 1e-3f && h.Normal.Z < -0.99f,
                   p.Name + ": a standing capsule meets the wall at 9.4 (" + h.Entity?.Name + " at " + h.Distance + ")");
            var lying = QueryShape.Capsule(new Vector3(49f, 1f, 0f), new Vector3(51f, 1f, 0f), 0.25f);
            t.True(p.Cast(lying, Fwd, 20f, out h) && h.Entity == e["Target"] && Math.Abs(h.Distance - 9.5f) < 1e-3f,
                   p.Name + ": a lying capsule at 9.5 (" + h.Distance + ")");
            var box = QueryShape.Box(new Vector3(50f, 1f, 0f), new Vector3(0.5f, 0.5f, 0.5f), SysQuat.Identity);
            t.True(p.Cast(box, Fwd, 20f, out h) && h.Entity == e["Target"] && Math.Abs(h.Distance - 9.25f) < 1e-3f && h.Normal.Z < -0.99f,
                   p.Name + ": a box at 9.25 (" + h.Distance + ")");
            var turned = QueryShape.Box(new Vector3(50f, 1f, 0f), new Vector3(0.5f, 0.5f, 0.5f), SysQuat.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, (float)Math.PI / 4f));
            t.True(p.Cast(turned, Fwd, 20f, out h) && h.Entity == e["Target"] && Math.Abs(h.Distance - (9.75f - 0.5f * (float)Math.Sqrt(2))) < 1e-3f,
                   p.Name + ": a box turned 45° leads with its edge (" + h.Distance + ")");
            t.False(p.Cast(box, Fwd, 9f, out _), p.Name + ": a sweep that stops short of the wall hits nothing");
            t.True(p.Cast(box, Fwd, 9.26f, out h) && Math.Abs(h.Distance - 9.25f) < 1e-3f, p.Name + ": a wall 1 cm inside maxDist counts (" + h.Distance + ")");
        }

        /// <summary>A mesh collider is a surface for a sweep too: from inside the closed room the sphere hits its wall from
        /// behind, from outside its outer face.</summary>
        internal static void SweepsAndMeshes(TestContext t, Path p, Dictionary<string, GameEntity> e)
        {
            t.True(p.Cast(QueryShape.Sphere(new Vector3(40f, 1f, 0f), 0.2f), Right, 5f, out var h) && h.Entity == e["Room"] && Math.Abs(h.Distance - 0.8f) < 1e-3f && h.Normal.X < -0.99f,
                   p.Name + ": inside the mesh room the sphere hits its wall at 0.8, the normal facing back (" + h.Entity?.Name + " at " + h.Distance + ", " + h.Normal + ")");
            t.True(p.Cast(QueryShape.Sphere(new Vector3(35f, 1f, 0f), 0.2f), Right, 10f, out h) && h.Entity == e["Room"] && Math.Abs(h.Distance - 3.8f) < 1e-3f,
                   p.Name + ": from outside, the outer face at 3.8 (" + h.Distance + ")");
            t.Equal(0, p.Overlap(QueryShape.Sphere(new Vector3(40f, 1f, 0f), 0.3f)).Count, p.Name + ": a sphere inside the room touching nothing overlaps nothing");
            var touching = p.Overlap(QueryShape.Sphere(new Vector3(40.8f, 1f, 0f), 0.3f));
            t.True(touching.Count == 1 && touching[0] == e["Room"], p.Name + ": one reaching through its wall overlaps the room (" + Names(touching) + ")");
        }

        /// <summary>Overlaps and checks: boxes, capsules and spheres find what they overlap or touch; ignoreEntity leaves an
        /// entity out; triggers count only when asked for.</summary>
        internal static void OverlapsFindWhatTouches(TestContext t, Path p, Dictionary<string, GameEntity> e)
        {
            var crate = p.Overlap(QueryShape.Box(new Vector3(20f, 1f, 0f), new Vector3(0.5f, 0.5f, 0.5f), SysQuat.Identity));
            t.True(crate.Count == 1 && crate[0] == e["Crate"], p.Name + ": a box inside the crate overlaps the crate (" + Names(crate) + ")");
            var posts = p.Overlap(QueryShape.Capsule(new Vector3(-0.5f, 1f, 3f), new Vector3(0.5f, 1f, 3f), 0.1f));
            t.True(posts.Count == 2 && posts.Contains(e["PostL"]) && posts.Contains(e["PostR"]), p.Name + ": a capsule across the posts overlaps both (" + Names(posts) + ")");
            var resting = p.Overlap(QueryShape.Sphere(new Vector3(5f, 0.499f, -5f), 0.5f));
            t.True(resting.Count == 1 && resting[0] == e["Floor"], p.Name + ": a ball resting on the floor (1 mm in) touches it (" + Names(resting) + ")");
            t.Equal(0, p.Overlap(QueryShape.Sphere(new Vector3(5f, 0.6f, -5f), 0.5f)).Count, p.Name + ": 10 cm above it, nothing");
            var turned = p.Overlap(QueryShape.Box(new Vector3(50f, 1f, 9.1f), new Vector3(0.5f, 0.5f, 0.5f), SysQuat.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, (float)Math.PI / 4f)));
            t.True(turned.Count == 1 && turned[0] == e["Target"], p.Name + ": a box turned 45° reaches the wall with its edge (" + Names(turned) + ")");
            t.True(p.Check(QueryShape.Sphere(new Vector3(20f, 1f, 1.1f), 0.2f)), p.Name + ": CheckSphere at the crate's face");
            t.False(p.Check(QueryShape.Sphere(new Vector3(15f, 1f, -5f), 0.5f)), p.Name + ": CheckSphere in open air");
            t.False(p.Check(QueryShape.Sphere(new Vector3(20f, 1f, 0f), 0.5f), e["Crate"]), p.Name + ": CheckSphere ignoring the crate it is in");
            var zone = QueryShape.Box(new Vector3(30f, 1f, 0f), new Vector3(0.2f, 0.2f, 0.2f), SysQuat.Identity);
            t.Equal(0, p.Overlap(zone).Count, p.Name + ": a trigger zone is no collider for an overlap");
            var withTriggers = p.Overlap(zone, null, true);
            t.True(withTriggers.Count == 1 && withTriggers[0] == e["Zone"], p.Name + ": … unless triggers are asked for (" + Names(withTriggers) + ")");
            t.True(p.Cast(QueryShape.Sphere(new Vector3(26.5f, 1f, 0f), 0.3f), Right, 5f, out var h, null, true) && h.Entity == e["Zone"] && Math.Abs(h.Distance - 2.2f) < 1e-3f,
                   p.Name + ": a sweep with triggers meets the zone (" + h.Entity?.Name + " at " + h.Distance + ")");
            t.False(p.Cast(QueryShape.Sphere(new Vector3(26.5f, 1f, 0f), 0.3f), Right, 5f, out h), p.Name + ": without, it passes (" + h.Entity?.Name + ")");
        }

        /// <summary>Casts filter like Raycast: ignoreEntity passes through a collider (also from inside it), and the all-hit
        /// sweep lists every entity it meets, nearest first.</summary>
        internal static void SweepsFilterAndCollect(TestContext t, Path p, Dictionary<string, GameEntity> e)
        {
            var ball = QueryShape.Sphere(new Vector3(10f, 1f, 0f), 0.25f);
            var all = p.CastAll(ball, Fwd, 20f);
            t.True(all.Count == 3 && all[0].Entity == e["Glass"] && all[1].Entity == e["Guard"] && all[2].Entity == e["Wall"],
                   p.Name + ": the sweep along the row meets glass, guard, wall (" + Names(all) + ")");
            if (all.Count == 3)
                t.True(Math.Abs(all[0].Distance - 4.74f) < 1e-3f && Math.Abs(all[1].Distance - 6.4f) < 1e-3f && Math.Abs(all[2].Distance - 9.5f) < 1e-3f,
                       p.Name + ": at 4.74, 6.4 and 9.5 (" + Names(all) + ")");
            t.True(p.Cast(ball, Fwd, 20f, out var h, e["Glass"]) && h.Entity == e["Guard"], p.Name + ": ignoring the glass the guard is first (" + h.Entity?.Name + ")");
            var inGuard = QueryShape.Sphere(new Vector3(10f, 1f, 7f), 0.2f);
            t.True(p.Cast(inGuard, Fwd, 20f, out h, e["Guard"]) && h.Entity == e["Wall"] && Math.Abs(h.Distance - 2.55f) < 1e-3f,
                   p.Name + ": from inside the ignored guard the sweep reaches the wall (" + h.Entity?.Name + " at " + h.Distance + ")");
        }

        // ---------------------------------------------------------------------------------------------- managed path

        private static void OnTheCollisionWorld(TestContext t, Action<TestContext, Path, Dictionary<string, GameEntity>> check)
        {
            var scene = QueryScene(out var e);
            var oldProvider = CollisionService.MeshTriangleProvider;
            CollisionService.MeshTriangleProvider = PhysicsRaycastTests.RoomProvider;
            try
            {
                CollisionService.Build(scene);
                t.False(PhysicsService.IsBuilt, "(no Jolt world: the collision world answers)");
                check(t, new Path(true), e);
            }
            finally { CollisionService.Clear(); CollisionService.MeshTriangleProvider = oldProvider; }
        }

        [Test] public static void SphereCastCatchesTheGapWithoutJolt(TestContext t) => OnTheCollisionWorld(t, SphereCastCatchesTheGap);
        [Test] public static void RaycastAllReportsEveryColliderWithoutJolt(TestContext t) => OnTheCollisionWorld(t, RaycastAllReportsEveryColliderInOrder);
        [Test] public static void SweepsThatStartInContactWithoutJolt(TestContext t) => OnTheCollisionWorld(t, SweepsThatStartInContact);
        [Test] public static void CapsuleAndBoxCastsWithoutJolt(TestContext t) => OnTheCollisionWorld(t, CapsuleAndBoxCasts);
        [Test] public static void SweepsAndMeshesWithoutJolt(TestContext t) => OnTheCollisionWorld(t, SweepsAndMeshes);
        [Test] public static void OverlapsFindWhatTouchesWithoutJolt(TestContext t) => OnTheCollisionWorld(t, OverlapsFindWhatTouches);
        [Test] public static void SweepsFilterAndCollectWithoutJolt(TestContext t) => OnTheCollisionWorld(t, SweepsFilterAndCollect);

        /// <summary>The script API on the collision world: RaycastHit carries the entity's handle, name and tag; NonAlloc fills
        /// the caller's array with the nearest hits; an explicit layer mask filters by the entity's Layer; the trigger option
        /// and the global switch decide about trigger colliders.</summary>
        [Test]
        public static void ScriptQueriesReportHandlesAndFilter(TestContext t)
        {
            var scene = QueryScene(out var e);
            var rt = Editor.Scripting.ScriptRuntime.Instance;
            try
            {
                CollisionService.Build(scene);
                Vortex.Vector3 V(float x, float y, float z) => new Vortex.Vector3(x, y, z);
                t.True(Vortex.Physics.SphereCast(V(0, 1, 0), 0.2f, V(0, 0, 1), 8f, out var hit), "SphereCast hits a post");
                t.True(hit.EntityId == rt.HandleForEntity(e["PostL"]) || hit.EntityId == rt.HandleForEntity(e["PostR"]), "… and names it by its handle (" + hit.Name + ")");
                var all = Vortex.Physics.RaycastAll(V(10, 1, 0), V(0, 0, 1), 20f);
                t.True(all.Length == 3 && all[0].Name == "Glass" && all[1].Name == "Guard" && all[2].Name == "Wall" && all[2].EntityId == rt.HandleForEntity(e["Wall"]),
                       "RaycastAll: glass, guard, wall");
                var two = new Vortex.RaycastHit[2];
                t.Equal(2, Vortex.Physics.RaycastNonAlloc(V(10, 1, 0), V(0, 0, 1), 20f, two), "RaycastNonAlloc fills the two slots it has");
                t.True(two[0].Name == "Glass" && two[1].Name == "Guard", "… with the nearest hits");
                t.Equal(3, Vortex.Physics.SphereCastNonAlloc(V(10, 1, 0), 0.25f, V(0, 0, 1), 20f, new Vortex.RaycastHit[8]), "SphereCastNonAlloc: three");
                t.True(Vortex.Physics.CapsuleCast(V(50, 0.4f, 0), V(50, 1.45f, 0), 0.35f, V(0, 0, 1), 20f, out hit) && hit.Name == "Target" && Math.Abs(hit.Distance - 9.4f) < 1e-3f,
                       "CapsuleCast meets the target wall at 9.4 (" + hit.Distance + ")");
                t.True(Vortex.Physics.BoxCast(V(50, 1, 0), V(0.5f, 0.5f, 0.5f), V(0, 45f, 0), V(0, 0, 1), 20f, out hit)
                       && Math.Abs(hit.Distance - (9.75f - 0.5f * (float)Math.Sqrt(2))) < 1e-3f, "BoxCast with Rotation (0, 45, 0) leads with its edge (" + hit.Distance + ")");
                t.Equal(3, Vortex.Physics.BoxCastAll(V(10, 1, 0), V(0.2f, 0.2f, 0.2f), V(0, 0, 0), V(0, 0, 1), 20f).Length, "BoxCastAll: three");
                t.Equal(3, Vortex.Physics.CapsuleCastAll(V(10, 0.5f, 0), V(10, 1.5f, 0), 0.2f, V(0, 0, 1), 20f).Length, "CapsuleCastAll: three");

                // Layer 5 (the box at x = 30, z = 5): an explicit mask without it passes, with it hits
                t.False(Vortex.Physics.SphereCast(V(30, 0.5f, 2), 0.2f, V(0, 0, 1), 5f, out hit, ~(1 << 5)), "a mask without Layer 5 passes the layered box");
                t.True(Vortex.Physics.SphereCast(V(30, 0.5f, 2), 0.2f, V(0, 0, 1), 5f, out hit, 1 << 5) && hit.Name == "Layered", "a mask of Layer 5 hits it");
                t.True(Vortex.Physics.OverlapBox(V(30, 0.5f, 5), V(0.2f, 0.2f, 0.2f), default(Vortex.Vector3), 1 << 5).Length == 1, "OverlapBox with Layer 5 finds it");
                t.Equal(0, Vortex.Physics.OverlapBox(V(30, 0.5f, 5), V(0.2f, 0.2f, 0.2f), default(Vortex.Vector3), ~(1 << 5)).Length, "… without Layer 5 not");

                long crate = rt.HandleForEntity(e["Crate"]);
                t.True(Vortex.Physics.CheckSphere(V(20, 1, 0), 0.5f), "CheckSphere in the crate");
                t.False(Vortex.Physics.CheckSphere(V(20, 1, 0), 0.5f, ~0, crate), "… not when ignoring the crate");
                t.True(Vortex.Physics.CheckCapsule(V(-0.5f, 1, 3), V(0.5f, 1, 3), 0.1f), "CheckCapsule across the posts");
                t.True(Vortex.Physics.CheckBox(V(20, 1, 1.2f), V(0.25f, 0.25f, 0.25f)), "CheckBox at the crate's face");
                var near = Vortex.Physics.OverlapCapsule(V(-0.5f, 1, 3), V(0.5f, 1, 3), 0.1f);
                t.Equal(2, near.Length, "OverlapCapsule: both posts");
                t.Equal(1, Vortex.Physics.OverlapSphere(V(5, 0.49f, -5), 0.5f).Length, "OverlapSphere(center, radius) works without Jolt now: the floor");

                t.Equal(0, Vortex.Physics.OverlapSphere(V(30, 1, 0), 0.2f, ~0).Length, "a trigger zone: not by default");
                t.Equal(1, Vortex.Physics.OverlapSphere(V(30, 1, 0), 0.2f, ~0, 0, Vortex.QueryTriggerInteraction.Collide).Length, "Collide: the zone");
                t.False(Vortex.Physics.Raycast(V(27, 1, 0), V(1, 0, 0), 2.5f, out hit), "Raycast passes the zone");
                t.True(Vortex.Physics.Raycast(V(27, 1, 0), V(1, 0, 0), 2.5f, out hit, ~0, 0, Vortex.QueryTriggerInteraction.Collide) && hit.Name == "Zone" && Math.Abs(hit.Distance - 2f) < 1e-3f,
                       "Raycast with Collide hits it at 2 (" + hit.Distance + ")");
                Vortex.Physics.QueriesHitTriggers = true;
                t.True(Vortex.Physics.Raycast(V(27, 1, 0), V(1, 0, 0), 2.5f, out hit) && hit.Name == "Zone", "QueriesHitTriggers: the plain Raycast hits it");
                t.False(Vortex.Physics.Raycast(V(27, 1, 0), V(1, 0, 0), 2.5f, out hit, ~0, 0, Vortex.QueryTriggerInteraction.Ignore), "… unless the query says Ignore");
                PhysicsService.Clear();
                t.False(Vortex.Physics.QueriesHitTriggers, "a scene start switches it off again");
            }
            finally { Vortex.Physics.QueriesHitTriggers = false; CollisionService.Clear(); }
        }
    }
}
