using System;
using Editor.Core.Data;
using Editor.Core.Services.AI;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;

namespace VortexTests
{
    /// <summary>Rays that start inside geometry or must skip their caller (#406 / #418), on the managed collision world
    /// (Physics.Raycast with an explicit layer mask, the AI's fallback, foot IK). The Jolt cast follows the same contract —
    /// see ZJoltPhysicsTests, which compares the two paths.</summary>
    public static class PhysicsRaycastTests
    {
        // A closed 2 x 2 x 2 m box of triangles (outward winding) standing on its local origin: a "room" mesh collider.
        internal static readonly float[] RoomTris = BuildRoom();

        // The room without its floor (roof and four walls): an open mesh overhead — a point under its roof is not inside.
        internal static readonly float[] GazeboTris = BuildRoom(skipFloor: true);

        private static float[] BuildRoom(bool skipFloor = false)
        {
            var v = new[]
            {
                new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(1, 0, 1), new Vector3(-1, 0, 1),
                new Vector3(-1, 2, -1), new Vector3(1, 2, -1), new Vector3(1, 2, 1), new Vector3(-1, 2, 1),
            };
            int[] idx = { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1, 2, 6, 7, 2, 7, 3, 1, 5, 6, 1, 6, 2, 3, 7, 4, 3, 4, 0 };
            if (skipFloor) idx = idx[6..];
            var tris = new float[idx.Length * 3];
            for (int i = 0; i < idx.Length; i++) { tris[i * 3] = v[idx[i]].X; tris[i * 3 + 1] = v[idx[i]].Y; tris[i * 3 + 2] = v[idx[i]].Z; }
            return tris;
        }

        internal static GameEntity Add(Scene s, GameEntity parent, string name, Vector3 pos, Action<GameEntity> setup)
        {
            var e = new GameEntity(s, name);
            e.Transform.LocalPosition = pos;
            setup?.Invoke(e);
            if (parent != null) parent.AddChild(e); else s.Entities.Add(e);
            return e;
        }

        /// <summary>A 1 m crate the rays start in, a wall (under a "Walls" group) 2.5 m ahead of it, a ball, a capsule
        /// pillar and a closed mesh room.</summary>
        internal static Scene RayScene(out GameEntity crate, out GameEntity walls, out GameEntity wall, out GameEntity ball,
            out GameEntity pillar, out GameEntity room)
        {
            var s = new Scene { Name = "Rays" };
            crate = Add(s, null, "Crate", new Vector3(0, 0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e)));
            walls = Add(s, null, "Walls", new Vector3(0, 0, 0), null);
            wall = Add(s, walls, "Wall", new Vector3(0, 0.5f, 3f), e => e.AddComponentDirect(new BoxCollider(e)));
            ball = Add(s, null, "Ball", new Vector3(5f, 1f, 0), e => e.AddComponentDirect(new SphereCollider(e) { Radius = 0.5f }));
            pillar = Add(s, null, "Pillar", new Vector3(10f, 1f, 0), e => e.AddComponentDirect(new CapsuleCollider(e) { Radius = 0.35f, Height = 1.8f }));
            room = Add(s, null, "Room", new Vector3(15f, 0, 0), e =>
            {
                e.AddComponentDirect(new MeshRenderer(e) { MeshPath = "Assets/room.glb" });
                e.AddComponentDirect(new MeshCollider(e));
            });
            AsLoaded(s);
            return s;
        }

        /// <summary>Entities as a loaded scene has them: a scene file carries no engine ids, every deserialized entity reads
        /// EntityId 0 — physics must not tell entities apart by it. (Given last: a valid id makes transform edits talk to
        /// the engine.)</summary>
        internal static void AsLoaded(Scene s)
        {
            void Walk(GameEntity e) { e.EntityId = 0; if (e.Children != null) foreach (var c in e.Children) Walk(c); }
            foreach (var e in s.Entities) Walk(e);
        }

        // A 2 x 2 m mesh "pad" at its local origin's height, facing up: a surface a ray can start exactly on.
        internal static readonly float[] PadTris =
        {
            -1, 0, -1,   1, 0, 1,   1, 0, -1,
            -1, 0, -1,  -1, 0, 1,   1, 0, 1,
        };

        internal static Func<string, float[]> RoomProvider => p => p == "Assets/room.glb" ? RoomTris : p == "Assets/pad.glb" ? PadTris
            : p == "Assets/gazebo.glb" ? GazeboTris : null;

        private static bool Cast(Vector3 o, Vector3 d, out Vector3 n, out GameEntity e, out float dist, GameEntity ignore = null)
            => CollisionService.Raycast(o, d, 50f, ~0, out _, out n, out e, out dist, ignore);

        [Test]
        public static void RayFromInsideAConvexColliderReportsWhatIsBeyond(TestContext t)
        {
            var scene = RayScene(out var crate, out _, out var wall, out _, out _, out var room);
            var oldProvider = CollisionService.MeshTriangleProvider;
            CollisionService.MeshTriangleProvider = RoomProvider;
            try
            {
                CollisionService.Build(scene);
                var fwd = new Vector3(0, 0, 1);
                t.True(Cast(new Vector3(0, 0.5f, 0), fwd, out var n, out var e, out float d), "a ray from inside the crate hits something");
                t.True(e == wall && Math.Abs(d - 2.5f) < 1e-3f, "… the wall 2.5 m ahead, not the crate it starts in (" + e?.Name + " at " + d + ")");
                t.True(n.Z < -0.99f, "… with the wall's normal facing the origin");
                t.False(Cast(new Vector3(5f, 1f, 0), fwd, out _, out _, out _), "a ray from inside the ball does not hit the ball");
                t.False(Cast(new Vector3(10f, 1f, 0), fwd, out _, out _, out _), "a ray from inside the capsule pillar does not hit it");
                // a mesh is a surface: from inside the room the ray hits its far wall from behind, the normal turned to the origin
                t.True(Cast(new Vector3(15f, 0.7f, 0.2f), fwd, out n, out e, out d), "a ray inside the mesh room hits its wall");
                t.True(e == room && Math.Abs(d - 0.8f) < 1e-3f && n.Z < -0.99f, "… from behind, 0.8 m away, normal towards the origin (" + d + ", " + n.Z + ")");
                // from outside, the crate is the first hit as before
                t.True(Cast(new Vector3(0, 0.5f, -2f), fwd, out n, out e, out d) && e == crate && Math.Abs(d - 1.5f) < 1e-3f && n.Z < -0.99f,
                       "from outside the crate is hit on its front face");
            }
            finally { CollisionService.Clear(); CollisionService.MeshTriangleProvider = oldProvider; }
        }

        [Test]
        public static void CapsuleCollidersAreHitExactlyNotAsTheirBox(TestContext t)
        {
            var scene = RayScene(out _, out _, out _, out _, out var pillar, out _);
            try
            {
                CollisionService.Build(scene);
                // the pillar: centre (10, 1, 0), r 0.35, segment y 0.45 .. 1.55 — its box reaches up to y 1.9
                t.False(Cast(new Vector3(10.3f, 1.88f, -5f), new Vector3(0, 0, 1), out _, out _, out _),
                        "a ray through the box corner beside the rounded top misses the capsule");
                t.True(Cast(new Vector3(10f, 1f, -5f), new Vector3(0, 0, 1), out var n, out var e, out float d) && e == pillar && Math.Abs(d - 4.65f) < 1e-3f && n.Z < -0.99f,
                       "a ray at the side hits the surface (" + d + ")");
                // an origin inside the capsule's box but outside the capsule: the old box test had no entry face and skipped it
                var o = new Vector3(10.3f, 1.88f, 0f);
                var toTop = new Vector3(10f - o.X, 1.55f - o.Y, 0f);
                float len = (float)Math.Sqrt(toTop.X * toTop.X + toTop.Y * toTop.Y);
                t.True(Cast(o, new Vector3(toTop.X / len, toTop.Y / len, 0f), out n, out e, out d) && e == pillar && Math.Abs(d - (len - 0.35f)) < 1e-3f,
                       "a ray from beside the cap hits the rounded top (" + d + " vs " + (len - 0.35f) + ")");
            }
            finally { CollisionService.Clear(); }
        }

        [Test]
        public static void IgnoredEntityIsPassedThroughFromDeepInside(TestContext t)
        {
            var scene = RayScene(out var crate, out var walls, out var wall, out _, out _, out _);
            try
            {
                CollisionService.Build(scene);
                var fwd = new Vector3(0, 0, 1);
                var behind = new Vector3(0, 0.5f, -2f);
                t.True(Cast(behind, fwd, out _, out var e, out float d, crate) && e == wall && Math.Abs(d - 4.5f) < 1e-3f,
                       "the ignored crate is passed through: the wall behind it is the hit (" + e?.Name + " at " + d + ")");
                t.True(Cast(new Vector3(0, 0.5f, 0.45f), fwd, out _, out e, out d, crate) && e == wall && Math.Abs(d - 2.05f) < 1e-3f,
                       "… also from deep inside it");
                t.True(Cast(behind, fwd, out _, out e, out _, walls) && e == crate, "ignoring the wall's parent group still hits the crate");
                t.False(Cast(new Vector3(0, 0.5f, 1f), fwd, out _, out _, out _, walls), "ignoring the parent ignores the wall under it");
            }
            finally { CollisionService.Clear(); }
        }

        /// <summary>RayScene on a 40 m floor (top at y 0) — the crate, the room and the pillar stand on it — and, beyond the
        /// floor's edge (nothing under them), a mesh pad at y 0 and a floorless "gazebo" (the room without its floor).</summary>
        internal static Scene RaySceneOnAFloor(out GameEntity floor, out GameEntity crate, out GameEntity ball, out GameEntity pillar, out GameEntity room,
            out GameEntity pad)
        {
            var s = RayScene(out crate, out _, out _, out ball, out pillar, out room);
            floor = Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));
            pad = Add(s, null, "Pad", new Vector3(25f, 0, 0), e =>
            {
                e.AddComponentDirect(new MeshRenderer(e) { MeshPath = "Assets/pad.glb" });
                e.AddComponentDirect(new MeshCollider(e));
            });
            Add(s, null, "Gazebo", new Vector3(25f, 0, 6f), e =>
            {
                e.AddComponentDirect(new MeshRenderer(e) { MeshPath = "Assets/gazebo.glb" });
                e.AddComponentDirect(new MeshCollider(e));
            });
            AsLoaded(s);
            return s;
        }

        /// <summary>The rays of <see cref="RaysStartingOnASurfaceHitIt"/>: origin, direction, the entity hit (null = none)
        /// and its distance — the ZJolt test casts the same ones on the Jolt path.</summary>
        internal static (Vector3 o, Vector3 d, GameEntity expect, float dist, string what)[] SurfaceRays(GameEntity floor, GameEntity crate,
            GameEntity ball, GameEntity pillar, GameEntity room, GameEntity pad)
        {
            var down = new Vector3(0, -1, 0); var up = new Vector3(0, 1, 0);
            return new (Vector3, Vector3, GameEntity, float, string)[]
            {
                (new Vector3(0, 1f, 0), down, crate, 0f, "down from the crate's top face"),
                (new Vector3(0.2f, 0.9995f, 0.1f), down, crate, 0f, "down from half a millimetre inside the crate"),
                (new Vector3(0.2f, 0.99f, 0.1f), down, floor, 0.99f, "down from 1 cm inside the crate (inside: the floor below)"),
                (new Vector3(0, 1f, 0), up, null, 0f, "up from the crate's top face"),
                (new Vector3(0, 0.5f, -0.5f), new Vector3(0, 0, 1), crate, 0f, "into the crate from its side face"),
                (new Vector3(0, 0.5f, -0.5f), new Vector3(0, 0, -1), null, 0f, "away from the crate's side face"),
                (new Vector3(5f, 1.5f, 0), down, ball, 0f, "down from the ball's top"),
                (new Vector3(10f, 1.9f, 0), down, pillar, 0f, "down from the capsule's top"),
                (new Vector3(15f, 2f, 0.2f), down, room, 0f, "down from the mesh room's roof"),
                (new Vector3(15f, 2f, 0.2f), up, null, 0f, "up from the mesh room's roof"),
                (new Vector3(3f, 0f, 3f), down, floor, 0f, "down from the floor's top face"),
                // 1 mm above a mesh at y 0 the cast starts exactly on it (fraction 0): still the surface the origin lies on
                (new Vector3(25f, 0.001f, 0.3f), up, null, 0f, "up from 1 mm above the mesh pad"),
                (new Vector3(25f, 0.001f, 0.3f), down, pad, 0.001f, "down from 1 mm above the mesh pad"),
            };
        }

        /// <summary>A ray that starts ON a surface and points into it hits it at distance 0 (the cast starts 1 mm behind its
        /// origin) — the probe of a mantle check from a crate's top. Pointing away it does not, for a mesh too; 1 cm inside a
        /// box is inside (#418).</summary>
        [Test]
        public static void RaysStartingOnASurfaceHitIt(TestContext t)
        {
            var scene = RaySceneOnAFloor(out var floor, out var crate, out var ball, out var pillar, out var room, out var pad);
            var oldProvider = CollisionService.MeshTriangleProvider;
            CollisionService.MeshTriangleProvider = RoomProvider;
            try
            {
                CollisionService.Build(scene);
                foreach (var r in SurfaceRays(floor, crate, ball, pillar, room, pad))
                {
                    bool got = CollisionService.Raycast(r.o, r.d, 50f, ~0, out var p, out var n, out var e, out float d);
                    t.Equal(r.expect != null, got, r.what + ": hit");
                    if (r.expect == null || !got) continue;
                    t.True(e == r.expect && Math.Abs(d - r.dist) < 1e-3f, r.what + ": " + r.expect.Name + " at " + r.dist + " (" + e?.Name + " at " + d + ")");
                    t.True(n.X * r.d.X + n.Y * r.d.Y + n.Z * r.d.Z < -0.99f, r.what + ": the normal faces the origin");
                    t.True(Math.Abs(p.X - (r.o.X + r.d.X * d)) < 2e-3f && Math.Abs(p.Y - (r.o.Y + r.d.Y * d)) < 2e-3f && Math.Abs(p.Z - (r.o.Z + r.d.Z * d)) < 2e-3f,
                           r.what + ": the point is on the surface (" + p.X + ", " + p.Y + ", " + p.Z + ")");
                }
                t.True(CollisionService.Raycast(new Vector3(0, 3f, 0), new Vector3(0, -1, 0), 2f, ~0, out _, out _, out var hit, out float dist) && hit == crate && Math.Abs(dist - 2f) < 1e-3f,
                       "a surface exactly maxDist away is still hit (" + dist + ")");
                t.False(CollisionService.Raycast(new Vector3(0, 3.01f, 0), new Vector3(0, -1, 0), 2f, ~0, out _, out _, out _, out _), "… one 1 cm beyond it is not");
            }
            finally { CollisionService.Clear(); CollisionService.MeshTriangleProvider = oldProvider; }
        }

        /// <summary>#418 without a Jolt world (an engine build without physics): Physics.OverlapPoint is answered by the
        /// collision world with the Jolt query's rules — the inside of boxes, spheres, capsules and a closed mesh; not a
        /// trigger, not open air, not above the mesh, not under an open mesh's roof.</summary>
        [Test]
        public static void OverlapPointWorksWithoutJolt(TestContext t)
        {
            var scene = RaySceneOnAFloor(out var floor, out var crate, out var ball, out var pillar, out var room, out _);
            var zone = Add(scene, null, "Zone", new Vector3(0, 1f, -6f), e => e.AddComponentDirect(new BoxCollider(e) { IsTrigger = true, Size = new Vector3(2f, 2f, 2f) }));
            AsLoaded(scene);
            var oldProvider = CollisionService.MeshTriangleProvider;
            CollisionService.MeshTriangleProvider = RoomProvider;
            try
            {
                CollisionService.Build(scene);
                t.False(PhysicsService.IsBuilt, "(no Jolt world)");
                foreach (var (p, expect, what) in OverlapPoints(floor, crate, ball, pillar, room))
                {
                    var found = CollisionService.OverlapPoint(p);
                    t.True(expect == null ? found.Count == 0 : found.Count == 1 && found[0] == expect,
                           what + ": " + (expect?.Name ?? "nothing") + " (found " + string.Join(", ", found.ConvertAll(x => x.Name)) + ")");
                }
            }
            finally { CollisionService.Clear(); CollisionService.MeshTriangleProvider = oldProvider; }
        }

        /// <summary>Points of the OverlapPoint tests and the entity that contains each (null = none).</summary>
        internal static (Vector3 p, GameEntity expect, string what)[] OverlapPoints(GameEntity floor, GameEntity crate, GameEntity ball, GameEntity pillar, GameEntity room)
            => new (Vector3, GameEntity, string)[]
            {
                (new Vector3(0.2f, 0.4f, -0.1f), crate, "in the crate"),
                (new Vector3(5.1f, 1.2f, 0.1f), ball, "in the ball"),
                (new Vector3(10f, 1.6f, 0.1f), pillar, "in the capsule"),
                (new Vector3(15f, 0.7f, 0.2f), room, "in the closed mesh room"),
                (new Vector3(2f, -0.3f, 2f), floor, "in the floor slab"),
                (new Vector3(2f, 0.5f, 0f), null, "in open air"),
                (new Vector3(15f, 2.5f, 0.2f), null, "above the mesh room"),
                (new Vector3(0f, 1f, -6f), null, "in a trigger zone"),
                (new Vector3(25f, 1f, 6.2f), null, "under the floorless gazebo's roof (an open mesh overhead)"),
            };

        /// <summary>#406 on the managed path: a perceiving guard carries a capsule collider (so it can be shot) and its eye
        /// sits inside it — the sight ray must neither stop at the guard's own collider nor at a collider it starts in.</summary>
        [Test]
        public static void LineOfSightIgnoresTheLookersOwnCollider(TestContext t)
        {
            var s = new Scene { Name = "Sight" };
            var guard = Add(s, null, "Guard", new Vector3(0, 0, 0), e => e.AddComponentDirect(new CapsuleCollider(e) { Radius = 0.35f, Height = 1.8f, Center = new Vector3(0, 0.9f, 0) }));
            var target = Add(s, null, "Target", new Vector3(0, 0.9f, 6f), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(0.6f, 1.8f, 0.6f) }));
            var door = Add(s, null, "Doorway", new Vector3(0, 1.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(2f, 1f, 2f) }));   // the eye is inside it too
            var eye = new Vector3(0, 1.6f, 0);
            try
            {
                CollisionService.Build(s);
                t.False(PhysicsService.IsBuilt, "(the managed fallback is under test)");
                t.True(PerceptionService.HasLineOfSight(eye, new Vector3(0, 1.2f, 6f), guard, target), "the guard sees the target past its own capsule and the doorway it stands in");
                var wall = Add(s, null, "Wall", new Vector3(0, 1f, 3f), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(4f, 3f, 0.3f) }));
                CollisionService.AddEntityShapes(wall);
                t.False(PerceptionService.HasLineOfSight(eye, new Vector3(0, 1.2f, 6f), guard, target), "a wall in between blocks the sight");
            }
            finally { CollisionService.Clear(); }
        }
    }
}
