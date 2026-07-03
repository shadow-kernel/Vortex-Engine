using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services.Physics
{
    /// <summary>
    /// The generic engine collision system (reusable — NOT game-specific). It builds a world of collision shapes
    /// from the active scene's Collider components and resolves a character (a vertical capsule) against them with
    /// collide-and-slide, so the ground is solid, you can't walk through walls/props/models, and you can't clip
    /// through even up close. The character is sampled as a row of spheres along its capsule and resolved by
    /// depenetration in small substeps — robust and tunnel-free for a walking/running character.
    ///
    /// Shapes: Box (OBB), Sphere, Capsule (analytic) and Mesh (edge-accurate triangle soup). A primitive mesh
    /// (Primitive:Cube/Plane/…) collides as its exact analytic shape; an imported model collides against its real
    /// triangles (via <see cref="MeshTriangleProvider"/>), so collision matches what's rendered — not a bounding box.
    /// </summary>
    public static class CollisionService
    {
        // ---- tiny math (kept local so it doesn't depend on Vector3 having operators) ----
        private struct V3
        {
            public float X, Y, Z;
            public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static V3 operator *(V3 a, float s) => new V3(a.X * s, a.Y * s, a.Z * s);
            public float Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
            public float Len() => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
            public V3 Norm() { float l = Len(); return l > 1e-8f ? new V3(X / l, Y / l, Z / l) : new V3(0, 0, 0); }
        }
        private static V3 From(Vector3 v) => new V3(v.X, v.Y, v.Z);
        private static Vector3 To(V3 v) => new Vector3(v.X, v.Y, v.Z);

        private enum Kind { Box, Sphere, Capsule, Tris }
        private sealed class Shape
        {
            public Kind Kind;
            public V3 Center;              // box/sphere
            public V3 Half;               // box half-extents
            public V3 AxX, AxY, AxZ;      // box orientation (unit)
            public float Radius;          // sphere/capsule radius
            public V3 A, B;               // capsule segment
            public V3[] Tris;             // triangles: flat [v0,v1,v2, v0,v1,v2, ...]
            public V3 Min, Max;           // world AABB (broadphase)
            public GameEntity Owner;      // the entity this shape belongs to (for trigger/collision event dispatch)
        }

        private static readonly List<Shape> _world = new List<Shape>();
        // Trigger colliders (IsTrigger): NOT solid — they never block, only report overlap enter/stay/exit.
        private static readonly List<Shape> _triggers = new List<Shape>();
        public static bool IsBuilt { get; private set; }

        /// <summary>A trigger/collision contact: a character (by its script handle) touched an entity's collider.</summary>
        public struct Contact { public long CharacterId; public GameEntity Other; }

        // Overlap state for enter/exit diffing, keyed by (characterHandle, shapeIndex).
        private static readonly HashSet<(long, int)> _prevTrig = new HashSet<(long, int)>();
        private static readonly HashSet<(long, int)> _curTrig = new HashSet<(long, int)>();
        private static readonly HashSet<(long, int)> _prevSolid = new HashSet<(long, int)>();
        private static readonly HashSet<(long, int)> _curSolid = new HashSet<(long, int)>();

        // Dynamic character capsules (multiplayer): each character auto-registers when it moves, so OTHER characters
        // collide against it — you can't walk through another player's character. Keyed by a caller id (e.g. entity id).
        private struct CharCap { public V3 Feet; public float R, H; }
        private static readonly Dictionary<long, CharCap> _chars = new Dictionary<long, CharCap>();
        public static void RemoveCharacter(long id) { _chars.Remove(id); }
        public static void ClearCharacters() { _chars.Clear(); }

        /// <summary>Optional hook that returns an imported model's local-space triangles (flat float[] x,y,z…) for a
        /// MeshRenderer MeshPath. Set by the runtime (native mesh export). Null → imported models fall back to a box.</summary>
        public static Func<string, float[]> MeshTriangleProvider;

        public static void Clear() { _world.Clear(); _triggers.Clear(); _chars.Clear(); ResetEvents(); IsBuilt = false; }

        /// <summary>Steam Audio v2 (#21): flatten the SOLID world colliders into a world-space triangle soup
        /// (vertex xyz array + per-triangle vertex indices) for the acoustic occlusion scene. Mesh colliders emit
        /// their triangles directly; box colliders emit their oriented box; spheres/capsules emit their AABB box as
        /// a coarse occluder. Returns false when there is nothing solid to occlude with. Triggers are excluded
        /// (they never block).</summary>
        public static bool ExportOcclusionGeometry(out float[] verts, out int[] indices)
        {
            verts = null; indices = null;
            if (_world.Count == 0) return false;
            var vs = new List<float>();
            var idx = new List<int>();

            void AddTri(V3 a, V3 b, V3 c)
            {
                int bi = vs.Count / 3;
                vs.Add(a.X); vs.Add(a.Y); vs.Add(a.Z);
                vs.Add(b.X); vs.Add(b.Y); vs.Add(b.Z);
                vs.Add(c.X); vs.Add(c.Y); vs.Add(c.Z);
                idx.Add(bi); idx.Add(bi + 1); idx.Add(bi + 2);
            }
            void AddBox(V3 c, V3 hx, V3 hy, V3 hz)
            {
                // 8 corners indexed by sign bits (sx,sy,sz), then 12 triangles (6 quad faces).
                V3 P(int sx, int sy, int sz) => c + hx * sx + hy * sy + hz * sz;
                V3 ppp = P(1, 1, 1), ppm = P(1, 1, -1), pmp = P(1, -1, 1), pmm = P(1, -1, -1);
                V3 mpp = P(-1, 1, 1), mpm = P(-1, 1, -1), mmp = P(-1, -1, 1), mmm = P(-1, -1, -1);
                void Quad(V3 a, V3 b, V3 d, V3 e) { AddTri(a, b, d); AddTri(a, d, e); }
                Quad(ppp, ppm, pmm, pmp);   // +X
                Quad(mpp, mmp, mmm, mpm);   // -X
                Quad(ppp, mpp, mpm, ppm);   // +Y
                Quad(pmp, pmm, mmm, mmp);   // -Y
                Quad(ppp, pmp, mmp, mpp);   // +Z
                Quad(ppm, mpm, mmm, pmm);   // -Z
            }

            foreach (var s in _world)
            {
                if (s == null) continue;
                if (s.Kind == Kind.Tris && s.Tris != null)
                {
                    for (int i = 0; i + 2 < s.Tris.Length; i += 3)
                        AddTri(s.Tris[i], s.Tris[i + 1], s.Tris[i + 2]);
                }
                else if (s.Kind == Kind.Box)
                {
                    AddBox(s.Center, s.AxX * s.Half.X, s.AxY * s.Half.Y, s.AxZ * s.Half.Z);
                }
                else // Sphere / Capsule -> coarse AABB occluder
                {
                    var c = (s.Min + s.Max) * 0.5f;
                    AddBox(c, new V3((s.Max.X - s.Min.X) * 0.5f, 0, 0),
                              new V3(0, (s.Max.Y - s.Min.Y) * 0.5f, 0),
                              new V3(0, 0, (s.Max.Z - s.Min.Z) * 0.5f));
                }
            }

            if (idx.Count < 3) return false;
            verts = vs.ToArray();
            indices = idx.ToArray();
            return true;
        }

        /// <summary>Cast a ray straight DOWN from <paramref name="origin"/> up to <paramref name="maxDist"/> against the
        /// solid world colliders, and return the closest hit point + the owning entity's Tag (the surface material).
        /// The standard "what am I standing on?" query — used for material-based footsteps. Box/sphere/capsule use their
        /// world AABB (exact for the flat, axis-aligned floors this is meant for); mesh colliders use ray-vs-triangle.
        /// Returns false when nothing is under the point.</summary>
        public static bool RaycastDown(Vector3 origin, float maxDist, out Vector3 hit, out string tag)
        {
            hit = origin; tag = "";
            V3 o = From(origin);
            float bestT = maxDist; Shape best = null;
            foreach (var s in _world)
            {
                if (s == null) continue;
                float t;
                bool got = (s.Kind == Kind.Tris && s.Tris != null)
                    ? RayDownTris(o, s.Tris, bestT, out t)
                    : RayDownAabb(o, s.Min, s.Max, bestT, out t);
                if (got && t <= bestT) { bestT = t; best = s; }
            }
            if (best == null) return false;
            hit = new Vector3(origin.X, origin.Y - bestT, origin.Z);
            tag = best.Owner != null ? (best.Owner.Tag ?? "") : "";
            return true;
        }

        // Downward ray (dir = -Y) vs world AABB. Exact for axis-aligned boxes; misses in XZ => no hit; a point below
        // the box never hits; above/inside => distance down to the top face (0 if already inside).
        private static bool RayDownAabb(V3 o, V3 min, V3 max, float maxDist, out float t)
        {
            t = 0f;
            if (o.X < min.X || o.X > max.X || o.Z < min.Z || o.Z > max.Z) return false;
            if (o.Y < min.Y) return false;
            t = o.Y - max.Y; if (t < 0f) t = 0f;
            return t <= maxDist;
        }

        // Downward ray vs a flat triangle soup — Möller–Trumbore per triangle, closest hit.
        private static bool RayDownTris(V3 o, V3[] tris, float maxDist, out float t)
        {
            t = maxDist; bool any = false;
            V3 d = new V3(0f, -1f, 0f);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                V3 v0 = tris[i], v1 = tris[i + 1], v2 = tris[i + 2];
                V3 e1 = v1 - v0, e2 = v2 - v0;
                V3 p = new V3(d.Y * e2.Z - d.Z * e2.Y, d.Z * e2.X - d.X * e2.Z, d.X * e2.Y - d.Y * e2.X);
                float det = e1.Dot(p);
                if (det > -1e-7f && det < 1e-7f) continue;
                float inv = 1f / det;
                V3 tv = o - v0;
                float u = tv.Dot(p) * inv; if (u < 0f || u > 1f) continue;
                V3 q = new V3(tv.Y * e1.Z - tv.Z * e1.Y, tv.Z * e1.X - tv.X * e1.Z, tv.X * e1.Y - tv.Y * e1.X);
                float vv = d.Dot(q) * inv; if (vv < 0f || u + vv > 1f) continue;
                float hitT = e2.Dot(q) * inv;
                if (hitT >= 0f && hitT < t) { t = hitT; any = true; }
            }
            return any;
        }

        /// <summary>Reset the per-frame overlap state (call on Build / scene switch / play end so stale pairs
        /// don't fire phantom Enter/Exit after a reload).</summary>
        public static void ResetEvents() { _prevTrig.Clear(); _curTrig.Clear(); _prevSolid.Clear(); _curSolid.Clear(); }

        /// <summary>Rebuild the collision world from every Collider in the scene (world-space). Call on scene load /
        /// play start; the static world doesn't change as the character moves.</summary>
        public static void Build(Scene scene)
        {
            _world.Clear();
            _triggers.Clear();
            _chars.Clear();   // characters re-register on their next MoveCharacter — don't leak across scene switches / replays
            ResetEvents();
            IsBuilt = true;
            if (scene == null || scene.Entities == null) return;
            foreach (var e in scene.Entities) AddRecursive(e);
        }

        private static void AddRecursive(GameEntity e)
        {
            if (e == null) return;
            var col = e.GetComponent<Collider>();
            if (col != null && col.IsEnabled)
            {
                // Solid colliders block (go into _world); triggers only report overlap (go into _triggers).
                try { var s = BuildShape(e, col); if (s != null) { s.Owner = e; if (col.IsTrigger) _triggers.Add(s); else _world.Add(s); } } catch { }
            }
            if (e.Children != null) foreach (var c in e.Children) AddRecursive(c);
        }

        // ---- world transform (walk the parent chain; good enough for level geometry) ----
        private static void WorldTransform(GameEntity e, out V3 pos, out V3 rotDeg, out V3 scale)
        {
            pos = new V3(0, 0, 0); rotDeg = new V3(0, 0, 0); scale = new V3(1, 1, 1);
            var chain = new List<GameEntity>();
            for (var cur = e; cur != null; cur = cur.Parent) chain.Add(cur);
            // apply from root down: accumulate scale + rotation(Y only, level geometry) + translate
            var p = new V3(0, 0, 0); var sc = new V3(1, 1, 1); float yaw = 0f, pitch = 0f, roll = 0f;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var t = chain[i].GetComponent<Transform>();
                if (t == null) continue;
                var lp = t.LocalPosition; var lr = t.LocalRotation; var ls = t.LocalScale;
                // rotate the child local offset by the current yaw before translating (level geometry is Y-up)
                var off = RotY(new V3(lp.X * sc.X, lp.Y * sc.Y, lp.Z * sc.Z), yaw);
                p = p + off;
                sc = new V3(sc.X * ls.X, sc.Y * ls.Y, sc.Z * ls.Z);
                yaw += lr.Y; pitch += lr.X; roll += lr.Z;
            }
            pos = p; scale = sc; rotDeg = new V3(pitch, yaw, roll);
        }

        private static V3 RotY(V3 v, float deg)
        {
            double r = deg * Math.PI / 180.0; float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
            return new V3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
        }

        private static Shape BuildShape(GameEntity e, Collider col)
        {
            WorldTransform(e, out var wpos, out var wrot, out var wscale);
            var center = wpos + RotY(new V3(col.Center.X * wscale.X, col.Center.Y * wscale.Y, col.Center.Z * wscale.Z), wrot.Y);

            if (col is BoxCollider box)
            {
                var s = new Shape { Kind = Kind.Box, Center = center };
                s.Half = new V3(Math.Abs(box.Size.X * 0.5f * wscale.X), Math.Abs(box.Size.Y * 0.5f * wscale.Y), Math.Abs(box.Size.Z * 0.5f * wscale.Z));
                s.AxX = RotY(new V3(1, 0, 0), wrot.Y); s.AxY = new V3(0, 1, 0); s.AxZ = RotY(new V3(0, 0, 1), wrot.Y);
                Aabb(s); return s;
            }
            if (col is SphereCollider sph)
            {
                float r = sph.Radius * Math.Max(Math.Abs(wscale.X), Math.Max(Math.Abs(wscale.Y), Math.Abs(wscale.Z)));
                var s = new Shape { Kind = Kind.Sphere, Center = center, Radius = r };
                s.Min = center - new V3(r, r, r); s.Max = center + new V3(r, r, r); return s;
            }
            if (col is CapsuleCollider cap)
            {
                float r = cap.Radius * Math.Max(Math.Abs(wscale.X), Math.Abs(wscale.Z));
                float half = Math.Max(0f, cap.Height * 0.5f * Math.Abs(wscale.Y) - r);
                V3 axis = cap.Direction == 0 ? new V3(1, 0, 0) : (cap.Direction == 2 ? new V3(0, 0, 1) : new V3(0, 1, 0));
                var s = new Shape { Kind = Kind.Capsule, Radius = r, A = center - axis * half, B = center + axis * half };
                s.Min = new V3(Math.Min(s.A.X, s.B.X) - r, Math.Min(s.A.Y, s.B.Y) - r, Math.Min(s.A.Z, s.B.Z) - r);
                s.Max = new V3(Math.Max(s.A.X, s.B.X) + r, Math.Max(s.A.Y, s.B.Y) + r, Math.Max(s.A.Z, s.B.Z) + r);
                return s;
            }
            // Mesh collider (or a base Collider): primitives collide as exact analytic shapes; imported models as
            // real triangles; anything else falls back to the mesh's bounding box.
            return BuildMeshShape(e, center, wrot, wscale);
        }

        private static Shape BuildMeshShape(GameEntity e, V3 center, V3 wrot, V3 wscale)
        {
            var mr = e.GetComponent<MeshRenderer>();
            string mp = mr?.MeshPath;
            if (!string.IsNullOrEmpty(mp) && mp.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                var prim = mp.Substring("Primitive:".Length).ToLowerInvariant();
                if (prim == "cube")
                {
                    var s = new Shape { Kind = Kind.Box, Center = center, Half = new V3(0.5f * Math.Abs(wscale.X), 0.5f * Math.Abs(wscale.Y), 0.5f * Math.Abs(wscale.Z)) };
                    s.AxX = RotY(new V3(1, 0, 0), wrot.Y); s.AxY = new V3(0, 1, 0); s.AxZ = RotY(new V3(0, 0, 1), wrot.Y); Aabb(s); return s;
                }
                if (prim == "plane" || prim == "quad")
                {
                    var s = new Shape { Kind = Kind.Box, Center = center, Half = new V3(0.5f * Math.Abs(wscale.X), 0.05f, 0.5f * Math.Abs(wscale.Z)) };
                    s.AxX = RotY(new V3(1, 0, 0), wrot.Y); s.AxY = new V3(0, 1, 0); s.AxZ = RotY(new V3(0, 0, 1), wrot.Y); Aabb(s); return s;
                }
                if (prim == "sphere")
                {
                    float r = 0.5f * Math.Max(Math.Abs(wscale.X), Math.Max(Math.Abs(wscale.Y), Math.Abs(wscale.Z)));
                    return new Shape { Kind = Kind.Sphere, Center = center, Radius = r, Min = center - new V3(r, r, r), Max = center + new V3(r, r, r) };
                }
                if (prim == "cylinder" || prim == "capsule" || prim == "cone")
                {
                    float r = 0.5f * Math.Max(Math.Abs(wscale.X), Math.Abs(wscale.Z));
                    float half = Math.Max(0f, 0.5f * Math.Abs(wscale.Y) - r);
                    var s = new Shape { Kind = Kind.Capsule, Radius = r, A = center - new V3(0, half, 0), B = center + new V3(0, half, 0) };
                    s.Min = new V3(center.X - r, center.Y - r - half, center.Z - r); s.Max = new V3(center.X + r, center.Y + r + half, center.Z + r); return s;
                }
            }
            // imported model -> real triangles if a provider is wired
            if (!string.IsNullOrEmpty(mp) && MeshTriangleProvider != null)
            {
                var raw = MeshTriangleProvider(mp);
                if (raw != null && raw.Length >= 9)
                {
                    int triCount = raw.Length / 9;
                    var tris = new V3[triCount * 3];
                    var mn = new V3(1e30f, 1e30f, 1e30f); var mx = new V3(-1e30f, -1e30f, -1e30f);
                    for (int i = 0; i < triCount * 3; i++)
                    {
                        var lv = new V3(raw[i * 3] * wscale.X, raw[i * 3 + 1] * wscale.Y, raw[i * 3 + 2] * wscale.Z);
                        var wv = center + RotY(lv, wrot.Y);
                        tris[i] = wv;
                        mn = new V3(Math.Min(mn.X, wv.X), Math.Min(mn.Y, wv.Y), Math.Min(mn.Z, wv.Z));
                        mx = new V3(Math.Max(mx.X, wv.X), Math.Max(mx.Y, wv.Y), Math.Max(mx.Z, wv.Z));
                    }
                    return new Shape { Kind = Kind.Tris, Tris = tris, Min = mn, Max = mx };
                }
            }
            // last resort: mesh AABB as a box (approximate) — better than no collision
            return null;
        }

        private static void Aabb(Shape s)
        {
            V3 e = new V3(
                Math.Abs(s.AxX.X) * s.Half.X + Math.Abs(s.AxY.X) * s.Half.Y + Math.Abs(s.AxZ.X) * s.Half.Z,
                Math.Abs(s.AxX.Y) * s.Half.X + Math.Abs(s.AxY.Y) * s.Half.Y + Math.Abs(s.AxZ.Y) * s.Half.Z,
                Math.Abs(s.AxX.Z) * s.Half.X + Math.Abs(s.AxY.Z) * s.Half.Y + Math.Abs(s.AxZ.Z) * s.Half.Z);
            s.Min = s.Center - e; s.Max = s.Center + e;
        }

        /// <summary>Collide-and-slide a character capsule (feet at <paramref name="feet"/>, given radius+height) by a
        /// displacement. Returns the resolved feet position; <paramref name="grounded"/> is true if it's resting on a
        /// surface. Tunnel-free (substepped) and clip-free (iterated depenetration).</summary>
        public static Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 displacement, out bool grounded)
            => MoveCharacter(feet, radius, height, displacement, out grounded, 0);

        /// <summary>Same, but <paramref name="selfId"/> registers this character's capsule so OTHER characters can't
        /// walk through it (multiplayer). Pass a stable id (e.g. the entity id); 0 = anonymous (no registration).</summary>
        public static Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 displacement, out bool grounded, long selfId)
        {
            grounded = false;
            radius = Math.Max(0.05f, radius);
            float segLen = Math.Max(0f, height - 2f * radius);
            if (!IsBuilt || (_world.Count == 0 && _chars.Count == 0))
            {
                var np = new Vector3(feet.X + displacement.X, feet.Y + displacement.Y, feet.Z + displacement.Z);
                if (selfId != 0) _chars[selfId] = new CharCap { Feet = From(np), R = radius, H = height };
                return np;
            }
            V3 p = From(feet); V3 disp = From(displacement);

            float dlen = disp.Len();
            int steps = Math.Max(1, (int)Math.Ceiling(dlen / (radius * 0.5f)));
            V3 step = disp * (1f / steps);
            bool g = false;
            for (int i = 0; i < steps; i++)
            {
                p = p + step;
                for (int iter = 0; iter < 5; iter++)
                {
                    if (!Depenetrate(ref p, radius, segLen, ref g, selfId)) break;
                }
            }
            grounded = g;
            if (selfId != 0) _chars[selfId] = new CharCap { Feet = p, R = radius, H = height };
            return To(p);
        }

        // Returns true if any push happened this pass.
        private static bool Depenetrate(ref V3 feet, float r, float segLen, ref bool grounded, long selfId)
        {
            // capsule segment: from feet+r to feet+r+segLen (vertical)
            V3 c0 = new V3(feet.X, feet.Y + r, feet.Z);
            V3 c1 = new V3(feet.X, feet.Y + r + segLen, feet.Z);
            // sample spheres along the segment
            int samples = Math.Max(2, (int)Math.Ceiling(segLen / r) + 1);
            V3 capMin = new V3(feet.X - r, feet.Y - r, feet.Z - r);
            V3 capMax = new V3(feet.X + r, feet.Y + r + segLen + r, feet.Z + r);

            V3 bestNormal = new V3(0, 0, 0); float bestDepth = 0f;
            for (int si = 0; si < _world.Count; si++)
            {
                var s = _world[si];
                if (!AabbOverlap(capMin, capMax, s.Min, s.Max)) continue;
                for (int k = 0; k < samples; k++)
                {
                    float t = samples == 1 ? 0f : (float)k / (samples - 1);
                    V3 c = new V3(c0.X + (c1.X - c0.X) * t, c0.Y + (c1.Y - c0.Y) * t, c0.Z + (c1.Z - c0.Z) * t);
                    V3 q; if (!ClosestOnShape(s, c, out q)) continue;
                    V3 d = c - q; float dl = d.Len();
                    float sr = ShapeRadius(s);
                    if (dl < r + sr && dl > 1e-6f)
                    {
                        float depth = (r + sr) - dl;
                        if (depth > bestDepth) { bestDepth = depth; bestNormal = d * (1f / dl); }
                    }
                    else if (dl <= 1e-6f)
                    {
                        // dead-center: push straight up (typical for standing on flat ground)
                        if (r + sr > bestDepth) { bestDepth = r + sr; bestNormal = new V3(0, 1, 0); }
                    }
                }
            }
            // other characters (multiplayer): capsule vs capsule — can't walk through another player.
            if (_chars.Count > 0)
            {
                foreach (var kv in _chars)
                {
                    if (kv.Key == selfId) continue;
                    var cc = kv.Value;
                    V3 oA = new V3(cc.Feet.X, cc.Feet.Y + cc.R, cc.Feet.Z);
                    V3 oB = new V3(cc.Feet.X, cc.Feet.Y + Math.Max(cc.R, cc.H - cc.R), cc.Feet.Z);
                    for (int k = 0; k < samples; k++)
                    {
                        float t = samples == 1 ? 0f : (float)k / (samples - 1);
                        V3 c = new V3(c0.X + (c1.X - c0.X) * t, c0.Y + (c1.Y - c0.Y) * t, c0.Z + (c1.Z - c0.Z) * t);
                        V3 q = ClosestOnSeg(oA, oB, c);
                        V3 d = c - q; float dl = d.Len();
                        if (dl < r + cc.R && dl > 1e-6f)
                        {
                            float depth = (r + cc.R) - dl;
                            V3 n = d * (1f / dl); if (n.Y < -0.2f) n = new V3(n.X, 0f, n.Z).Norm(); // don't get shoved into the floor
                            if (depth > bestDepth) { bestDepth = depth; bestNormal = n; }
                        }
                    }
                }
            }

            if (bestDepth > 1e-5f)
            {
                feet = feet + bestNormal * bestDepth;
                if (bestNormal.Y > 0.5f) grounded = true;
                return true;
            }
            return false;
        }

        /// <summary>Detect trigger + solid overlaps for every registered character this frame and diff against last
        /// frame. Fills <paramref name="enter"/>/<paramref name="stay"/>/<paramref name="exit"/> (trigger colliders)
        /// and <paramref name="collisionEnter"/> (solid colliders). Call ONCE per tick, AFTER all characters have
        /// moved (MoveCharacter registered them). Each Contact = (character handle, the OTHER entity touched).</summary>
        public static void StepEvents(List<Contact> enter, List<Contact> stay, List<Contact> exit, List<Contact> collisionEnter)
        {
            enter?.Clear(); stay?.Clear(); exit?.Clear(); collisionEnter?.Clear();
            _curTrig.Clear(); _curSolid.Clear();

            if (_chars.Count > 0 && (_triggers.Count > 0 || _world.Count > 0))
            {
                foreach (var kv in _chars)
                {
                    long cid = kv.Key; var cc = kv.Value;
                    CapsuleBounds(cc, out var capMin, out var capMax);

                    for (int ti = 0; ti < _triggers.Count; ti++)
                    {
                        var s = _triggers[ti];
                        if (!AabbOverlap(capMin, capMax, s.Min, s.Max)) continue;
                        if (!CapsuleOverlapsShape(cc, s)) continue;
                        var key = (cid, ti);
                        _curTrig.Add(key);
                        stay?.Add(new Contact { CharacterId = cid, Other = s.Owner });
                        if (!_prevTrig.Contains(key)) enter?.Add(new Contact { CharacterId = cid, Other = s.Owner });
                    }

                    for (int wi = 0; wi < _world.Count; wi++)
                    {
                        var s = _world[wi];
                        if (!AabbOverlap(capMin, capMax, s.Min, s.Max)) continue;
                        if (!CapsuleOverlapsShape(cc, s)) continue;
                        var key = (cid, wi);
                        _curSolid.Add(key);
                        if (!_prevSolid.Contains(key)) collisionEnter?.Add(new Contact { CharacterId = cid, Other = s.Owner });
                    }
                }
            }

            // Exits: pairs that were overlapping last frame but not now.
            foreach (var key in _prevTrig)
                if (!_curTrig.Contains(key) && key.Item2 < _triggers.Count)
                    exit?.Add(new Contact { CharacterId = key.Item1, Other = _triggers[key.Item2].Owner });

            _prevTrig.Clear(); foreach (var k in _curTrig) _prevTrig.Add(k);
            _prevSolid.Clear(); foreach (var k in _curSolid) _prevSolid.Add(k);
        }

        private static void CapsuleBounds(CharCap cc, out V3 min, out V3 max)
        {
            float r = cc.R;
            min = new V3(cc.Feet.X - r, cc.Feet.Y - r, cc.Feet.Z - r);
            max = new V3(cc.Feet.X + r, cc.Feet.Y + Math.Max(cc.H, 2f * r) + r, cc.Feet.Z + r);
        }

        // Contact skin: collide-and-slide pushes a character to EXACTLY a solid's surface, so a strict "penetrating"
        // test would miss it. A small skin makes OnCollisionEnter (and trigger touch) fire when the character is at /
        // just within reach of the surface — reliable "touched it" detection.
        private const float ContactSkin = 0.06f;

        /// <summary>Boolean overlap test: the character capsule (sampled as spheres) vs a shape — same math as
        /// Depenetrate but reports overlap (within a small contact skin) instead of pushing.</summary>
        private static bool CapsuleOverlapsShape(CharCap cc, Shape s)
        {
            float r = cc.R;
            float segLen = Math.Max(0f, cc.H - 2f * r);
            V3 c0 = new V3(cc.Feet.X, cc.Feet.Y + r, cc.Feet.Z);
            V3 c1 = new V3(cc.Feet.X, cc.Feet.Y + r + segLen, cc.Feet.Z);
            int samples = Math.Max(2, (int)Math.Ceiling(segLen / r) + 1);
            float sr = ShapeRadius(s);
            for (int k = 0; k < samples; k++)
            {
                float t = samples == 1 ? 0f : (float)k / (samples - 1);
                V3 c = new V3(c0.X + (c1.X - c0.X) * t, c0.Y + (c1.Y - c0.Y) * t, c0.Z + (c1.Z - c0.Z) * t);
                if (!ClosestOnShape(s, c, out var q)) continue;
                if ((c - q).Len() < r + sr + ContactSkin) return true;
            }
            return false;
        }

        private static float ShapeRadius(Shape s) => s.Kind == Kind.Sphere || s.Kind == Kind.Capsule ? s.Radius : 0f;

        private static bool ClosestOnShape(Shape s, V3 c, out V3 q)
        {
            switch (s.Kind)
            {
                case Kind.Box: q = ClosestOnBox(s, c); return true;
                case Kind.Sphere: q = s.Center; return true;
                case Kind.Capsule: q = ClosestOnSeg(s.A, s.B, c); return true;
                case Kind.Tris:
                    {
                        // nearest triangle (broadphase already gated the whole mesh)
                        float best = 1e30f; V3 bq = new V3(0, 0, 0); bool any = false;
                        int tc = s.Tris.Length / 3;
                        for (int i = 0; i < tc; i++)
                        {
                            V3 p = ClosestOnTri(s.Tris[i * 3], s.Tris[i * 3 + 1], s.Tris[i * 3 + 2], c);
                            float dl = (c - p).Len();
                            if (dl < best) { best = dl; bq = p; any = true; }
                        }
                        q = bq; return any;
                    }
            }
            q = c; return false;
        }

        private static V3 ClosestOnBox(Shape s, V3 c)
        {
            V3 d = c - s.Center;
            float x = Clamp(d.Dot(s.AxX), -s.Half.X, s.Half.X);
            float y = Clamp(d.Dot(s.AxY), -s.Half.Y, s.Half.Y);
            float z = Clamp(d.Dot(s.AxZ), -s.Half.Z, s.Half.Z);
            return s.Center + s.AxX * x + s.AxY * y + s.AxZ * z;
        }

        private static V3 ClosestOnSeg(V3 a, V3 b, V3 c)
        {
            V3 ab = b - a; float t = ab.Dot(ab); if (t < 1e-8f) return a;
            t = Clamp((c - a).Dot(ab) / t, 0f, 1f); return a + ab * t;
        }

        private static V3 ClosestOnTri(V3 a, V3 b, V3 cc, V3 p)
        {
            V3 ab = b - a, ac = cc - a, ap = p - a;
            float d1 = ab.Dot(ap), d2 = ac.Dot(ap);
            if (d1 <= 0 && d2 <= 0) return a;
            V3 bp = p - b; float d3 = ab.Dot(bp), d4 = ac.Dot(bp);
            if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) { float v = d1 / (d1 - d3); return a + ab * v; }
            V3 cp = p - cc; float d5 = ab.Dot(cp), d6 = ac.Dot(cp);
            if (d6 >= 0 && d5 <= d6) return cc;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) { float w = d2 / (d2 - d6); return a + ac * w; }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return b + (cc - b) * w; }
            float denom = 1f / (va + vb + vc); float vv = vb * denom, ww = vc * denom;
            return a + ab * vv + ac * ww;
        }

        private static bool AabbOverlap(V3 amin, V3 amax, V3 bmin, V3 bmax)
            => amin.X <= bmax.X && amax.X >= bmin.X && amin.Y <= bmax.Y && amax.Y >= bmin.Y && amin.Z <= bmax.Z && amax.Z >= bmin.Z;

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
