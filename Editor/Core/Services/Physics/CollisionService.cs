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
            public TriGrid Grid;          // uniform grid over Tris (perf #342): null for small meshes (brute force)
            public V3 Min, Max;           // world AABB (broadphase)
            public GameEntity Owner;      // the entity this shape belongs to (for trigger/collision event dispatch)
            public uint BodyId;           // physics body handle for DYNAMIC shapes (0 for the static world)
            public bool Dynamic;          // published per step by PhysicsService (a Jolt rigid body), not baked
        }

        // ---- uniform grid over a MeshCollider's triangles (perf #342) --------------------------------------------
        // A terrain-sized MeshCollider (tens of thousands of triangles) otherwise makes the character controller test
        // EVERY triangle for each capsule sample, walkability raycast and ground snap, every frame (40 → 4 FPS). The
        // grid buckets each triangle by its AABB; the controller then only looks at the few cells it touches.
        private const int GridMinTris = 256;   // below this the brute-force loop is as fast and simpler
        private const int GridMaxDim = 128;    // cap cells per axis (build cost + memory)

        /// <summary>Triangle count examined by the most recent Tris query — for tests to confirm the grid is selective.</summary>
        internal static int DebugLastTriTests;

        private sealed class TriGrid
        {
            private V3 _origin; private float _cell, _inv; private int _nx, _ny, _nz;
            private int[] _cellStart;   // CSR: length _nx*_ny*_nz + 1
            private int[] _items;       // triangle indices grouped by cell (a tri is listed in every cell its AABB spans)
            private readonly int[] _stamp; private int _gen;   // per-query dedup (single-threaded physics step)
            private int[] _buf = new int[64]; private int _n;  // reusable candidate buffer

            private TriGrid(int triCount) { _stamp = new int[triCount]; }

            public static TriGrid Build(V3[] tris, V3 mn, V3 mx)
            {
                int triCount = tris.Length / 3;
                if (triCount < GridMinTris) return null;
                V3 ext = mx - mn;
                float big = Math.Max(ext.X, Math.Max(ext.Y, ext.Z));
                if (big <= 1e-5f) return null;
                float horiz = Math.Max(ext.X, ext.Z); if (horiz < 1e-4f) horiz = big;
                float cell = horiz / (float)Math.Sqrt(triCount);   // aim for ~1 triangle per cell (terrain-friendly)
                float minCell = big / GridMaxDim;                  // keep each axis within GridMaxDim cells
                if (cell < minCell) cell = minCell;
                if (cell < 1e-4f) cell = 1e-4f;
                var g = new TriGrid(triCount)
                {
                    _origin = mn, _cell = cell, _inv = 1f / cell,
                    _nx = ClampI((int)(ext.X / cell) + 1, 1, GridMaxDim),
                    _ny = ClampI((int)(ext.Y / cell) + 1, 1, GridMaxDim),
                    _nz = ClampI((int)(ext.Z / cell) + 1, 1, GridMaxDim),
                };
                int cells = g._nx * g._ny * g._nz;
                var counts = new int[cells + 1];
                for (int i = 0; i < triCount; i++)
                {
                    g.TriCells(tris, i, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
                    for (int z = z0; z <= z1; z++) for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                        counts[g.Idx(x, y, z) + 1]++;
                }
                for (int i = 0; i < cells; i++) counts[i + 1] += counts[i];
                g._cellStart = counts;
                g._items = new int[counts[cells]];
                var cursor = (int[])counts.Clone();
                for (int i = 0; i < triCount; i++)
                {
                    g.TriCells(tris, i, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
                    for (int z = z0; z <= z1; z++) for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                        g._items[cursor[g.Idx(x, y, z)]++] = i;
                }
                return g;
            }

            private int Idx(int x, int y, int z) => (z * _ny + y) * _nx + x;
            private int Cx(float v) => ClampI((int)((v - _origin.X) * _inv), 0, _nx - 1);
            private int Cy(float v) => ClampI((int)((v - _origin.Y) * _inv), 0, _ny - 1);
            private int Cz(float v) => ClampI((int)((v - _origin.Z) * _inv), 0, _nz - 1);
            // (not Math.Clamp: the classic WPF editor compiles this file for net48, which has none)
            private static int ClampI(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

            private void TriCells(V3[] tris, int tri, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
            {
                V3 a = tris[tri * 3], b = tris[tri * 3 + 1], c = tris[tri * 3 + 2];
                x0 = Cx(Math.Min(a.X, Math.Min(b.X, c.X))); x1 = Cx(Math.Max(a.X, Math.Max(b.X, c.X)));
                y0 = Cy(Math.Min(a.Y, Math.Min(b.Y, c.Y))); y1 = Cy(Math.Max(a.Y, Math.Max(b.Y, c.Y)));
                z0 = Cz(Math.Min(a.Z, Math.Min(b.Z, c.Z))); z1 = Cz(Math.Max(a.Z, Math.Max(b.Z, c.Z)));
            }

            private void Begin() { _gen++; _n = 0; }
            private void Add(int tri)
            {
                if (_stamp[tri] == _gen) return;
                _stamp[tri] = _gen;
                if (_n == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
                _buf[_n++] = tri;
            }

            /// <summary>Triangle indices whose cells overlap the box [qmin,qmax]. Returns the shared buffer; read [0,count).</summary>
            public int[] QueryBox(V3 qmin, V3 qmax, out int count)
            {
                Begin();
                int x0 = Cx(qmin.X), x1 = Cx(qmax.X), y0 = Cy(qmin.Y), y1 = Cy(qmax.Y), z0 = Cz(qmin.Z), z1 = Cz(qmax.Z);
                for (int z = z0; z <= z1; z++) for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                {
                    int ci = Idx(x, y, z);
                    for (int k = _cellStart[ci]; k < _cellStart[ci + 1]; k++) Add(_items[k]);
                }
                count = _n; return _buf;
            }

            /// <summary>Triangle indices along the ray o+dir*t, t in [0,maxDist], by 3D-DDA. Returns the shared buffer.</summary>
            public int[] QueryRay(V3 o, V3 dir, float maxDist, out int count)
            {
                Begin();
                float x0 = _origin.X, y0 = _origin.Y, z0 = _origin.Z;
                float x1 = x0 + _nx * _cell, y1 = y0 + _ny * _cell, z1 = z0 + _nz * _cell;
                float tmin = 0f, tmax = maxDist;
                if (!Slab(o.X, dir.X, x0, x1, ref tmin, ref tmax) ||
                    !Slab(o.Y, dir.Y, y0, y1, ref tmin, ref tmax) ||
                    !Slab(o.Z, dir.Z, z0, z1, ref tmin, ref tmax)) { count = 0; return _buf; }
                float t = Math.Max(tmin, 0f);
                int ix = Cx(o.X + dir.X * t), iy = Cy(o.Y + dir.Y * t), iz = Cz(o.Z + dir.Z * t);
                int sx = dir.X >= 0 ? 1 : -1, sy = dir.Y >= 0 ? 1 : -1, sz = dir.Z >= 0 ? 1 : -1;
                float dX = Math.Abs(dir.X) > 1e-12f ? Math.Abs(_cell / dir.X) : 1e30f;
                float dY = Math.Abs(dir.Y) > 1e-12f ? Math.Abs(_cell / dir.Y) : 1e30f;
                float dZ = Math.Abs(dir.Z) > 1e-12f ? Math.Abs(_cell / dir.Z) : 1e30f;
                float mX = Math.Abs(dir.X) > 1e-12f ? (_origin.X + (ix + (sx > 0 ? 1 : 0)) * _cell - o.X) / dir.X : 1e30f;
                float mY = Math.Abs(dir.Y) > 1e-12f ? (_origin.Y + (iy + (sy > 0 ? 1 : 0)) * _cell - o.Y) / dir.Y : 1e30f;
                float mZ = Math.Abs(dir.Z) > 1e-12f ? (_origin.Z + (iz + (sz > 0 ? 1 : 0)) * _cell - o.Z) / dir.Z : 1e30f;
                while (true)
                {
                    int ci = Idx(ix, iy, iz);
                    for (int k = _cellStart[ci]; k < _cellStart[ci + 1]; k++) Add(_items[k]);
                    if (mX < mY && mX < mZ) { if (mX > tmax) break; ix += sx; if (ix < 0 || ix >= _nx) break; mX += dX; }
                    else if (mY < mZ) { if (mY > tmax) break; iy += sy; if (iy < 0 || iy >= _ny) break; mY += dY; }
                    else { if (mZ > tmax) break; iz += sz; if (iz < 0 || iz >= _nz) break; mZ += dZ; }
                }
                count = _n; return _buf;
            }

            private static bool Slab(float o, float d, float lo, float hi, ref float tmin, ref float tmax)
            {
                if (Math.Abs(d) < 1e-9f) return o >= lo && o <= hi;
                float inv = 1f / d, t1 = (lo - o) * inv, t2 = (hi - o) * inv;
                if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tmin) tmin = t1;
                if (t2 < tmax) tmax = t2;
                return tmin <= tmax;
            }
        }

        private static readonly List<Shape> _world = new List<Shape>();
        // Trigger colliders (IsTrigger): NOT solid — they never block, only report overlap enter/stay/exit.
        private static readonly List<Shape> _triggers = new List<Shape>();
        // Physics v2 (#100): the CURRENT pose of every simulated rigid body (barrels, crates, ...), refreshed by
        // PhysicsService after each physics step. Solid for the character exactly like _world (Depenetrate,
        // Raycast, RaycastDown, StepEvents consult both), but the character records its pushes against them in
        // CharacterContacts so PhysicsService can shove the body instead of the character bouncing off a ghost.
        private static readonly List<Shape> _dynamic = new List<Shape>();
        public static bool IsBuilt { get; private set; }

        /// <summary>One simulated rigid body as the character sees it: an oriented box (centre, half extents, unit
        /// axes) — or a sphere when <see cref="SphereRadius"/> &gt; 0 (a rolling ball gets round contact normals).
        /// Published every physics step through <see cref="SetDynamicBodies"/>.</summary>
        public struct DynamicBody
        {
            /// <summary>Physics body handle (reported back in <see cref="DynamicContact.BodyId"/>).</summary>
            public uint BodyId;
            /// <summary>The entity the body belongs to (collision/trigger event owner, raycast hit entity).</summary>
            public GameEntity Owner;
            /// <summary>World-space centre of the shape.</summary>
            public Vector3 Center;
            /// <summary>Box half extents along the body's local axes (ignored for spheres).</summary>
            public Vector3 HalfExtents;
            /// <summary>Unit axes of the body's rotation (ignored for spheres).</summary>
            public Vector3 AxisX, AxisY, AxisZ;
            /// <summary>&gt; 0 = this body is a sphere of that radius instead of a box.</summary>
            public float SphereRadius;
        }

        /// <summary>A character push against a simulated rigid body, recorded by <see cref="MoveCharacter"/>:
        /// the body handle, the contact normal (unit, pointing from the body towards the character), the
        /// penetration depth that was resolved, the contact point on the body and the character's displacement
        /// for that move call (its velocity × dt). PhysicsService drains <see cref="CharacterContacts"/> once per
        /// frame and turns each into an impulse on the body.</summary>
        public struct DynamicContact
        {
            public uint BodyId;
            public long CharacterId;
            public Vector3 Normal;
            public float Depth;
            public Vector3 Point;
            public Vector3 Move;
        }

        /// <summary>Character-vs-rigid-body pushes since the last drain (see <see cref="DynamicContact"/>).
        /// Cleared by PhysicsService each physics frame and on Build/Clear.</summary>
        public static readonly List<DynamicContact> CharacterContacts = new List<DynamicContact>();

        // The move currently being resolved (set by MoveCharacter so Depenetrate can stamp it on recorded contacts).
        private static V3 _curMove;
        private static long _curCharId;

        /// <summary>Replace the set of DYNAMIC shapes (simulated rigid bodies) with their current poses. Called by
        /// PhysicsService after every physics step; pass null/empty when there are no bodies. The static world
        /// (<see cref="Build"/>) is untouched.</summary>
        public static void SetDynamicBodies(List<DynamicBody> bodies)
        {
            _dynamic.Clear();
            if (bodies == null) return;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                var c = From(b.Center);
                Shape s;
                if (b.SphereRadius > 0f)
                {
                    float r = b.SphereRadius;
                    s = new Shape { Kind = Kind.Sphere, Center = c, Radius = r, Min = c - new V3(r, r, r), Max = c + new V3(r, r, r) };
                }
                else
                {
                    s = new Shape
                    {
                        Kind = Kind.Box, Center = c,
                        Half = new V3(Math.Abs(b.HalfExtents.X), Math.Abs(b.HalfExtents.Y), Math.Abs(b.HalfExtents.Z)),
                        AxX = From(b.AxisX).Norm(), AxY = From(b.AxisY).Norm(), AxZ = From(b.AxisZ).Norm()
                    };
                    if (s.AxX.Len() < 0.5f) s.AxX = new V3(1, 0, 0);
                    if (s.AxY.Len() < 0.5f) s.AxY = new V3(0, 1, 0);
                    if (s.AxZ.Len() < 0.5f) s.AxZ = new V3(0, 0, 1);
                    Aabb(s);
                }
                s.Owner = b.Owner; s.BodyId = b.BodyId; s.Dynamic = true;
                _dynamic.Add(s);
            }
        }

        /// <summary>Number of simulated rigid bodies currently published as dynamic shapes.</summary>
        public static int DynamicShapeCount => _dynamic.Count;

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

        public static void Clear() { _world.Clear(); _triggers.Clear(); _dynamic.Clear(); CharacterContacts.Clear(); _chars.Clear(); ResetEvents(); IsBuilt = false; }

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
            var best = RaycastDownShape(From(origin), maxDist, out float bestT);
            if (best == null) return false;
            hit = new Vector3(origin.X, origin.Y - bestT, origin.Z);
            tag = best.Owner != null ? (best.Owner.Tag ?? "") : "";
            return true;
        }

        /// <summary>Like <see cref="RaycastDown"/> but returns the surface's MATERIAL name (the file name of the hit
        /// entity's MeshRenderer material, e.g. "grass" from "Assets/Materials/grass.vmat"). This is the SCALABLE way
        /// to drive surface-aware audio/effects: map material -> sound ONCE and every object using that material works
        /// automatically, across every scene — no per-entity tagging. "" when the surface has no material.</summary>
        public static bool RaycastDownMaterial(Vector3 origin, float maxDist, out Vector3 hit, out string material)
        {
            hit = origin; material = "";
            var best = RaycastDownShape(From(origin), maxDist, out float bestT);
            if (best == null) return false;
            hit = new Vector3(origin.X, origin.Y - bestT, origin.Z);
            var mr = best.Owner != null ? best.Owner.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>() : null;
            var path = mr != null ? mr.MaterialPath : null;
            material = string.IsNullOrEmpty(path) ? "" : System.IO.Path.GetFileNameWithoutExtension(path);
            return true;
        }

        /// <summary>Downward ray: returns the FOOTSTEP SOUND assigned (in the Material Editor) to the material of the
        /// surface below <paramref name="origin"/> — a project-relative clip / .vsndc path, or "" if the surface has
        /// no material or no footstep sound. This is what makes footsteps fully EDITOR-authored: assign a material to a
        /// floor, give the material a step sound in the Material Editor, and every floor using it plays that step — no
        /// per-material script dictionary. The game's FootstepAudio behaviour reads this via Physics.GroundStepSound.</summary>
        public static bool RaycastDownStepSound(Vector3 origin, float maxDist, out Vector3 hit, out string stepSound)
        {
            hit = origin; stepSound = "";
            var best = RaycastDownShape(From(origin), maxDist, out float bestT);
            if (best == null) return false;
            hit = new Vector3(origin.X, origin.Y - bestT, origin.Z);
            var mr = best.Owner != null ? best.Owner.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>() : null;
            var path = mr != null ? mr.MaterialPath : null;
            if (string.IsNullOrEmpty(path)) return true;
            try
            {
                var proj = Editor.Core.Data.ProjectData.Current?.Path ?? "";
                var full = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(proj, path);
                // Editor: load the loose absolute .vmat; shipped game: fall back to the project-relative VFS key.
                var vmat = Editor.Core.Assets.VortexMaterial.Load(full) ?? Editor.Core.Assets.VortexMaterial.Load(path);
                if (vmat != null && !string.IsNullOrEmpty(vmat.FootstepSound)) stepSound = vmat.FootstepSound;
            }
            catch { }
            return true;
        }

        // Closest solid shape straight below `o` within maxDist (shared by the tag + material raycasts).
        // Static world first, then the simulated rigid bodies (standing on a crate counts as ground too).
        private static Shape RaycastDownShape(V3 o, float maxDist, out float bestT)
        {
            bestT = maxDist; Shape best = null;
            for (int list = 0; list < 2; list++)
            {
                var shapes = list == 0 ? _world : _dynamic;
                foreach (var s in shapes)
                {
                    if (s == null) continue;
                    float t;
                    bool got = (s.Kind == Kind.Tris && s.Tris != null)
                        ? RayDownTris(o, s, bestT, out t)
                        : RayDownAabb(o, s.Min, s.Max, bestT, out t);
                    if (got && t <= bestT) { bestT = t; best = s; }
                }
            }
            return best;
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
        private static bool RayDownTris(V3 o, Shape s, float maxDist, out float t)
        {
            t = maxDist; bool any = false;
            var tris = s.Tris;
            if (s.Grid != null)
            {
                var buf = s.Grid.QueryRay(o, new V3(0f, -1f, 0f), maxDist, out int cnt);
                DebugLastTriTests = cnt;
                for (int j = 0; j < cnt; j++) { int i = buf[j] * 3; if (TriRayDown(o, tris[i], tris[i + 1], tris[i + 2], ref t)) any = true; }
            }
            else
            {
                DebugLastTriTests = tris.Length / 3;
                for (int i = 0; i + 2 < tris.Length; i += 3) if (TriRayDown(o, tris[i], tris[i + 1], tris[i + 2], ref t)) any = true;
            }
            return any;
        }

        private static bool TriRayDown(V3 o, V3 v0, V3 v1, V3 v2, ref float t)
        {
            V3 d = new V3(0f, -1f, 0f);
            V3 e1 = v1 - v0, e2 = v2 - v0;
            V3 p = new V3(d.Y * e2.Z - d.Z * e2.Y, d.Z * e2.X - d.X * e2.Z, d.X * e2.Y - d.Y * e2.X);
            float det = e1.Dot(p);
            if (det > -1e-7f && det < 1e-7f) return false;
            float inv = 1f / det;
            V3 tv = o - v0;
            float u = tv.Dot(p) * inv; if (u < 0f || u > 1f) return false;
            V3 q = new V3(tv.Y * e1.Z - tv.Z * e1.Y, tv.Z * e1.X - tv.X * e1.Z, tv.X * e1.Y - tv.Y * e1.X);
            float vv = d.Dot(q) * inv; if (vv < 0f || u + vv > 1f) return false;
            float hitT = e2.Dot(q) * inv;
            if (hitT >= 0f && hitT < t) { t = hitT; return true; }
            return false;
        }

        /// <summary>General raycast (#35): closest SOLID-world hit along an arbitrary direction. Boxes test as
        /// exact OBBs (ray transformed into box space), spheres analytically, mesh colliders per triangle
        /// (Möller–Trumbore), capsules as their AABB. <paramref name="layerMask"/> filters by the owning
        /// entity's Layer bit. Returns hit point, unit normal (facing the ray origin) and the hit entity.</summary>
        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDist, int layerMask,
            out Vector3 hitPoint, out Vector3 hitNormal, out GameEntity hitEntity, out float hitDist)
        {
            hitPoint = origin; hitNormal = new Vector3(0, 1, 0); hitEntity = null; hitDist = 0f;
            V3 o = From(origin);
            V3 d = From(direction).Norm();
            if (d.Len() < 0.5f || maxDist <= 0f) return false;

            float bestT = maxDist; Shape best = null; V3 bestN = new V3(0, 1, 0);
            for (int list = 0; list < 2; list++)
            {
                var shapes = list == 0 ? _world : _dynamic;   // static level + the simulated rigid bodies
                foreach (var s in shapes)
                {
                    if (s == null) continue;
                    if (s.Owner != null && (layerMask & (1 << (s.Owner.Layer & 31))) == 0) continue;
                    float t; V3 n;
                    bool got;
                    if (s.Kind == Kind.Tris && s.Tris != null) got = RayTris(o, d, s, bestT, out t, out n);
                    else if (s.Kind == Kind.Box) got = RayObb(o, d, s, bestT, out t, out n);
                    else if (s.Kind == Kind.Sphere) got = RaySphere(o, d, s.Center, s.Radius, bestT, out t, out n);
                    else got = RayAabbGeneric(o, d, s.Min, s.Max, bestT, out t, out n);   // capsule: coarse AABB
                    if (got && t < bestT) { bestT = t; best = s; bestN = n; }
                }
            }
            if (best == null) return false;

            hitDist = bestT;
            hitPoint = To(o + d * bestT);
            hitNormal = To(bestN);
            hitEntity = best.Owner;
            return true;
        }

        // Ray vs triangle soup — Möller–Trumbore per triangle, closest hit + geometric normal flipped
        // to face the ray origin.
        private static bool RayTris(V3 o, V3 d, Shape s, float maxDist, out float t, out V3 n)
        {
            t = maxDist; n = new V3(0, 1, 0); bool any = false;
            var tris = s.Tris;
            if (s.Grid != null)
            {
                var buf = s.Grid.QueryRay(o, d, maxDist, out int cnt);
                DebugLastTriTests = cnt;
                for (int j = 0; j < cnt; j++) { int i = buf[j] * 3; if (TriRay(o, d, tris[i], tris[i + 1], tris[i + 2], ref t, ref n)) any = true; }
            }
            else
            {
                DebugLastTriTests = tris.Length / 3;
                for (int i = 0; i + 2 < tris.Length; i += 3) if (TriRay(o, d, tris[i], tris[i + 1], tris[i + 2], ref t, ref n)) any = true;
            }
            return any;
        }

        private static bool TriRay(V3 o, V3 d, V3 v0, V3 v1, V3 v2, ref float t, ref V3 n)
        {
            V3 e1 = v1 - v0, e2 = v2 - v0;
            V3 p = Cross(d, e2);
            float det = e1.Dot(p);
            if (det > -1e-7f && det < 1e-7f) return false;
            float inv = 1f / det;
            V3 tv = o - v0;
            float u = tv.Dot(p) * inv; if (u < 0f || u > 1f) return false;
            V3 q = Cross(tv, e1);
            float vv = d.Dot(q) * inv; if (vv < 0f || u + vv > 1f) return false;
            float hitT = e2.Dot(q) * inv;
            if (hitT >= 0f && hitT < t)
            {
                t = hitT;
                var tn = Cross(e1, e2).Norm();
                n = tn.Dot(d) > 0f ? tn * -1f : tn;
                return true;
            }
            return false;
        }

        // Ray vs oriented box: transform the ray into box space (project on the OBB axes), slab-test there,
        // return the entry face's world-space normal.
        private static bool RayObb(V3 o, V3 d, Shape s, float maxDist, out float t, out V3 n)
        {
            t = 0f; n = new V3(0, 1, 0);
            V3 rel = o - s.Center;
            float[] ro = { rel.Dot(s.AxX), rel.Dot(s.AxY), rel.Dot(s.AxZ) };
            float[] rd = { d.Dot(s.AxX), d.Dot(s.AxY), d.Dot(s.AxZ) };
            float[] h = { s.Half.X, s.Half.Y, s.Half.Z };
            float tmin = 0f, tmax = maxDist; int axis = -1; float sign = 1f;
            for (int i = 0; i < 3; i++)
            {
                if (rd[i] > -1e-8f && rd[i] < 1e-8f)
                {
                    if (ro[i] < -h[i] || ro[i] > h[i]) return false;
                    continue;
                }
                float inv = 1f / rd[i];
                float t1 = (-h[i] - ro[i]) * inv, t2 = (h[i] - ro[i]) * inv;
                float lo = t1 < t2 ? t1 : t2, hi = t1 < t2 ? t2 : t1;
                if (lo > tmin) { tmin = lo; axis = i; sign = rd[i] > 0f ? -1f : 1f; }
                if (hi < tmax) tmax = hi;
                if (tmin > tmax) return false;
            }
            if (axis < 0) return false;   // started inside — no clean entry face
            t = tmin;
            n = (axis == 0 ? s.AxX : axis == 1 ? s.AxY : s.AxZ) * sign;
            return t <= maxDist;
        }

        private static bool RaySphere(V3 o, V3 d, V3 c, float r, float maxDist, out float t, out V3 n)
        {
            t = 0f; n = new V3(0, 1, 0);
            V3 m = o - c;
            float b = m.Dot(d), cc = m.Dot(m) - r * r;
            if (cc > 0f && b > 0f) return false;
            float disc = b * b - cc;
            if (disc < 0f) return false;
            t = -b - (float)Math.Sqrt(disc);
            if (t < 0f || t > maxDist) return false;
            n = ((o + d * t) - c).Norm();
            return true;
        }

        private static bool RayAabbGeneric(V3 o, V3 d, V3 min, V3 max, float maxDist, out float t, out V3 n)
        {
            t = 0f; n = new V3(0, 1, 0);
            float tmin = 0f, tmax = maxDist; int axis = -1; float sign = 1f;
            float[] ov = { o.X, o.Y, o.Z }, dv = { d.X, d.Y, d.Z }, mn = { min.X, min.Y, min.Z }, mx = { max.X, max.Y, max.Z };
            for (int i = 0; i < 3; i++)
            {
                if (dv[i] > -1e-8f && dv[i] < 1e-8f)
                {
                    if (ov[i] < mn[i] || ov[i] > mx[i]) return false;
                    continue;
                }
                float inv = 1f / dv[i];
                float t1 = (mn[i] - ov[i]) * inv, t2 = (mx[i] - ov[i]) * inv;
                float lo = t1 < t2 ? t1 : t2, hi = t1 < t2 ? t2 : t1;
                if (lo > tmin) { tmin = lo; axis = i; sign = dv[i] > 0f ? -1f : 1f; }
                if (hi < tmax) tmax = hi;
                if (tmin > tmax) return false;
            }
            if (axis < 0) return false;
            t = tmin;
            n = axis == 0 ? new V3(sign, 0, 0) : axis == 1 ? new V3(0, sign, 0) : new V3(0, 0, sign);
            return true;
        }

        private static V3 Cross(V3 a, V3 b)
            => new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /// <summary>Incrementally add a (runtime-spawned) entity subtree's colliders to the built world —
        /// unlike a full Build() this does NOT reset trigger overlap state, so no phantom Enter events.</summary>
        public static void AddEntityShapes(GameEntity e) { if (IsBuilt && e != null) AddRecursive(e); }

        /// <summary>Remove every collider shape owned by the given entity subtree (runtime Destroy).
        /// <paramref name="includeChildren"/> false removes only the entity's OWN shapes (PhysicsService takes a
        /// simulated dynamic entity out of the static world without touching its children).</summary>
        public static void RemoveEntityShapes(GameEntity root, bool includeChildren = true)
        {
            if (root == null) return;
            var set = new HashSet<GameEntity>();
            void Collect(GameEntity e) { if (e == null) return; set.Add(e); if (includeChildren && e.Children != null) foreach (var c in e.Children) Collect(c); }
            Collect(root);
            _world.RemoveAll(s => s != null && s.Owner != null && set.Contains(s.Owner));
            _triggers.RemoveAll(s => s != null && s.Owner != null && set.Contains(s.Owner));
            _dynamic.RemoveAll(s => s != null && s.Owner != null && set.Contains(s.Owner));
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
            _dynamic.Clear();
            CharacterContacts.Clear();
            _chars.Clear();   // characters re-register on their next MoveCharacter — don't leak across scene switches / replays
            ResetEvents();
            CharacterStepHeight = 0.35f;   // #48: back to stock — scripts re-apply their tuning in Start()
            CharacterMaxSlopeDeg = 50f;
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
                    return new Shape { Kind = Kind.Tris, Tris = tris, Min = mn, Max = mx, Grid = TriGrid.Build(tris, mn, mx) };
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
        /// walk through it (multiplayer). Pass a stable id (e.g. the entity id); 0 = anonymous (no registration).
        /// Stair-step + slope handling (#48): <paramref name="stepHeight"/> is the tallest ledge auto-climbed
        /// (0 disables); <paramref name="maxSlopeDeg"/> is the steepest walkable slope — steeper surfaces act
        /// like walls (slide, no climb, never grounded).</summary>
        /// <summary>Project-tunable character-controller defaults (#48): the tallest auto-climbed ledge
        /// and the steepest walkable slope. Scripts set them via Physics.SetCharacterOptions in Start();
        /// reset to the stock values on every scene (re)build so projects can't leak into each other.</summary>
        public static float CharacterStepHeight = 0.35f;
        public static float CharacterMaxSlopeDeg = 50f;

        public static Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 displacement, out bool grounded, long selfId,
            float stepHeight = -1f, float maxSlopeDeg = -1f)
        {
            if (stepHeight < 0f) stepHeight = CharacterStepHeight;
            if (maxSlopeDeg < 0f) maxSlopeDeg = CharacterMaxSlopeDeg;
            grounded = false;
            radius = Math.Max(0.05f, radius);
            float segLen = Math.Max(0f, height - 2f * radius);
            if (!IsBuilt || (_world.Count == 0 && _chars.Count == 0 && _dynamic.Count == 0))
            {
                var np = new Vector3(feet.X + displacement.X, feet.Y + displacement.Y, feet.Z + displacement.Z);
                if (selfId != 0) _chars[selfId] = new CharCap { Feet = From(np), R = radius, H = height };
                return np;
            }
            if (maxSlopeDeg < 10f) maxSlopeDeg = 10f; else if (maxSlopeDeg > 85f) maxSlopeDeg = 85f;
            float slopeCos = (float)Math.Cos(maxSlopeDeg * Math.PI / 180.0);
            V3 p = From(feet); V3 disp = From(displacement);
            _curMove = disp; _curCharId = selfId;   // stamped onto pushes against rigid bodies (see DynamicContact)

            float dlen = disp.Len();
            int steps = Math.Max(1, (int)Math.Ceiling(dlen / (radius * 0.5f)));
            V3 step = disp * (1f / steps);
            bool g = false;
            for (int i = 0; i < steps; i++)
            {
                V3 before = p;
                p = p + step;
                for (int iter = 0; iter < 5; iter++)
                {
                    if (!Depenetrate(ref p, radius, segLen, ref g, selfId, slopeCos)) break;
                }

                // ---- stair STEP-UP (#48) ----
                // If a wall push ate most of this substep's horizontal motion, retry it from a position
                // lifted by stepHeight and settle back down. A real step (ledge <= stepHeight) lets the
                // lifted capsule pass and land on top; a tall wall still blocks the lifted try -> reject.
                float wantH = step.X * step.X + step.Z * step.Z;   // full forward progress == this (dot metric)
                if (stepHeight > 0.001f && wantH > 1e-8f)
                {
                    // SIGNED progress along the intended direction — a backward wall push must read as
                    // "blocked", not as movement (a squared-distance metric counted it as progress and
                    // the climb stalled after the first corner frame).
                    float got = (p.X - before.X) * step.X + (p.Z - before.Z) * step.Z;
                    if (got < wantH * 0.5f)   // achieved < 50% of the intended forward progress
                    {
                        V3 lifted = new V3(before.X + step.X, before.Y + stepHeight + 0.02f, before.Z + step.Z);
                        bool gLift = false;
                        for (int iter = 0; iter < 5; iter++)
                            if (!Depenetrate(ref lifted, radius, segLen, ref gLift, selfId, slopeCos)) break;
                        float liftGot = (lifted.X - before.X) * step.X + (lifted.Z - before.Z) * step.Z;
                        if (liftGot > got + 1e-8f && liftGot > wantH * 0.5f)
                        {
                            // Settle onto the step surface in tunnel-safe increments — VERTICAL contact
                            // resolution, so the round capsule bottom can rest on the step's top edge
                            // mid-climb instead of being ejected off it.
                            V3 settle = lifted; bool gSet = false;
                            float drop = stepHeight + 0.04f;
                            while (drop > 0f && !gSet)
                            {
                                float dd = Math.Min(radius * 0.5f, drop);
                                settle = new V3(settle.X, settle.Y - dd, settle.Z);
                                drop -= dd;
                                for (int iter = 0; iter < 5; iter++)
                                    if (!Depenetrate(ref settle, radius, segLen, ref gSet, selfId, slopeCos, settleVertical: true)) break;
                            }
                            // Walkability guard: the surface under the landing must be walkable ground —
                            // without it, repeated step-ups would slowly climb a slope steeper than the
                            // limit. Probe down far enough to also cover mid-corner-climb frames.
                            bool walkable = false;
                            if (gSet)
                            {
                                if (Raycast(To(new V3(settle.X, settle.Y + radius, settle.Z)), new Vector3(0f, -1f, 0f),
                                        stepHeight + radius + 0.2f, ~0, out _, out Vector3 gn, out _, out _))
                                    walkable = gn.Y >= slopeCos;
                            }
                            // Accept only a REAL step-up: the settle must end higher than the plain (blocked) move
                            // did — a lifted capsule that slid back down onto the same floor is not a climb, and
                            // taking its position would also swallow a jump started against the ledge.
                            if (gSet && walkable && settle.Y <= before.Y + stepHeight + 0.01f && settle.Y >= before.Y - 0.05f && settle.Y > p.Y + 0.02f)
                            {
                                p = settle;
                                g = true;
                            }
                        }
                    }
                }
            }

            // ---- ground SNAP (#48) ----
            // Walking down steps/slopes keeps contact instead of briefly floating (gravity alone takes
            // several frames per 20 cm step, which reads as bouncing + kills footstep grounding checks).
            // Only when the caller isn't moving upward (jump ascent must not be glued to the floor).
            if (!g && stepHeight > 0.001f && disp.Y <= 0.001f && dlen > 1e-6f)
            {
                V3 snap = p; bool gSnap = false;
                // Snap distance per call scales with the distance moved this call: a 40 cm curb is descended over
                // a few frames (a glide) instead of in ONE frame — the one-frame drop plus the wall push of the
                // round capsule bottom sliding past the curb's face read as a teleport.
                float drop = Math.Min(stepHeight, Math.Max(0.02f, dlen * 1.5f));
                while (drop > 0f && !gSnap)
                {
                    float dd = Math.Min(radius * 0.5f, drop);
                    snap = new V3(snap.X, snap.Y - dd, snap.Z);
                    drop -= dd;
                    for (int iter = 0; iter < 5; iter++)
                        if (!Depenetrate(ref snap, radius, segLen, ref gSnap, selfId, slopeCos)) break;
                }
                if (gSnap && snap.Y <= p.Y + 0.001f) { p = snap; g = true; }
            }

            grounded = g;
            if (selfId != 0) _chars[selfId] = new CharCap { Feet = p, R = radius, H = height };
            return To(p);
        }

        // Returns true if any push happened this pass. slopeCos (#48): surfaces whose contact normal
        // has Y >= slopeCos count as walkable ground; steeper ones act like walls — their push is
        // flattened to horizontal (slide, no slow depenetration-climb) and never sets grounded.
        // settleVertical (#48): the step-up SETTLE resolves upward-ish contacts PURELY VERTICALLY —
        // a capsule's round bottom resting on a step's top EDGE otherwise gets pushed back out
        // horizontally and the climb never completes (the walkability raycast guards steep ramps).
        private static bool Depenetrate(ref V3 feet, float r, float segLen, ref bool grounded, long selfId, float slopeCos = 0.5f, bool settleVertical = false)
        {
            // capsule segment: from feet+r to feet+r+segLen (vertical)
            V3 c0 = new V3(feet.X, feet.Y + r, feet.Z);
            V3 c1 = new V3(feet.X, feet.Y + r + segLen, feet.Z);
            // sample spheres along the segment
            int samples = Math.Max(2, (int)Math.Ceiling(segLen / r) + 1);
            V3 capMin = new V3(feet.X - r, feet.Y - r, feet.Z - r);
            V3 capMax = new V3(feet.X + r, feet.Y + r + segLen + r, feet.Z + r);

            V3 bestNormal = new V3(0, 0, 0); float bestDepth = 0f;
            Shape bestShape = null; V3 bestPoint = new V3(0, 0, 0);
            // Static world, then the simulated rigid bodies (same math — a crate is as solid as a wall for the
            // character; the difference is that a push against a rigid body is RECORDED so physics can move it).
            for (int list = 0; list < 2; list++)
            {
                var shapes = list == 0 ? _world : _dynamic;
                for (int si = 0; si < shapes.Count; si++)
                {
                    var s = shapes[si];
                    if (!AabbOverlap(capMin, capMax, s.Min, s.Max)) continue;
                    for (int k = 0; k < samples; k++)
                    {
                        float t = samples == 1 ? 0f : (float)k / (samples - 1);
                        V3 c = new V3(c0.X + (c1.X - c0.X) * t, c0.Y + (c1.Y - c0.Y) * t, c0.Z + (c1.Z - c0.Z) * t);
                        V3 q; if (!ClosestOnShape(s, c, out q, r + ShapeRadius(s) + 1e-3f)) continue;
                        V3 d = c - q; float dl = d.Len();
                        float sr = ShapeRadius(s);
                        if (dl < r + sr && dl > 1e-6f)
                        {
                            float depth = (r + sr) - dl;
                            if (depth > bestDepth) { bestDepth = depth; bestNormal = d * (1f / dl); bestShape = s; bestPoint = q; }
                        }
                        else if (dl <= 1e-6f)
                        {
                            // dead-center: push straight up (typical for standing on flat ground)
                            if (r + sr > bestDepth) { bestDepth = r + sr; bestNormal = new V3(0, 1, 0); bestShape = s; bestPoint = q; }
                        }
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
                // A push against a simulated rigid body: remember it (body, normal, depth, point, this move) so
                // PhysicsService can shove the body — the character itself is still resolved below as usual.
                if (bestShape != null && bestShape.Dynamic)
                {
                    CharacterContacts.Add(new DynamicContact
                    {
                        BodyId = bestShape.BodyId, CharacterId = _curCharId,
                        Normal = To(bestNormal), Depth = bestDepth, Point = To(bestPoint), Move = To(_curMove)
                    });
                }
                // A contact on a box's TOP face — including its top edge, which the round capsule bottom hits
                // with an almost horizontal normal when only a sliver of the step is under it (high frame rates
                // move a centimetre per call) — counts as "landing on it" during the settle.
                bool onTopFace = bestShape != null && bestShape.Kind == Kind.Box && bestPoint.Y >= bestShape.Max.Y - 0.002f;
                if (settleVertical && (bestNormal.Y > 0.15f || onTopFace))
                {
                    // Landing phase of a step-up: resolve the contact straight up so the capsule comes
                    // to rest ON the step instead of being ejected off its edge.
                    feet = new V3(feet.X, feet.Y + bestDepth / Math.Max(bestNormal.Y, 0.35f), feet.Z);
                    grounded = true;
                    return true;
                }
                // Slope limit (#48): a too-steep surface must not be climbable via repeated push-out —
                // flatten its push to horizontal so the character slides along it like a wall.
                if (bestNormal.Y > 0.15f && bestNormal.Y < slopeCos)
                {
                    V3 flat = new V3(bestNormal.X, 0f, bestNormal.Z);
                    float fl = flat.Len();
                    if (fl > 1e-6f) bestNormal = flat * (1f / fl);
                }
                // Walkable contact = GROUND: resolve it straight up. The round capsule bottom hanging over a step
                // or box edge otherwise gets shoved diagonally (up AND forward/back), which read as a 20-30 cm
                // teleport when running off curbs and as back-and-forth jitter along low cover.
                if (bestNormal.Y >= slopeCos)
                {
                    feet = new V3(feet.X, feet.Y + bestDepth / bestNormal.Y, feet.Z);
                    grounded = true;
                    return true;
                }
                feet = feet + bestNormal * bestDepth;
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

            if (_chars.Count > 0 && (_triggers.Count > 0 || _world.Count > 0 || _dynamic.Count > 0))
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

                    // Simulated rigid bodies: touching a crate fires OnCollisionEnter like touching a wall. Keyed by
                    // the body handle (negative range, so it can't collide with a _world index) — the list is
                    // rebuilt every step, so indices would not be stable.
                    for (int di = 0; di < _dynamic.Count; di++)
                    {
                        var s = _dynamic[di];
                        if (s.Owner == null) continue;
                        if (!AabbOverlap(capMin, capMax, s.Min, s.Max)) continue;
                        if (!CapsuleOverlapsShape(cc, s)) continue;
                        var key = (cid, -1 - (int)(s.BodyId & 0x7FFFFFFF));
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
                if (!ClosestOnShape(s, c, out var q, r + sr + ContactSkin + 1e-3f)) continue;
                if ((c - q).Len() < r + sr + ContactSkin) return true;
            }
            return false;
        }

        private static float ShapeRadius(Shape s) => s.Kind == Kind.Sphere || s.Kind == Kind.Capsule ? s.Radius : 0f;

        // maxDist bounds the search for a MeshCollider: the character only cares about triangles within the capsule
        // radius, so the grid can skip everything else (a triangle whose closest point is within maxDist of c has its
        // AABB inside [c-maxDist, c+maxDist], so it sits in a queried cell). Infinite = search all (small meshes).
        private static bool ClosestOnShape(Shape s, V3 c, out V3 q, float maxDist = float.PositiveInfinity)
        {
            switch (s.Kind)
            {
                case Kind.Box: q = ClosestOnBox(s, c); return true;
                case Kind.Sphere: q = s.Center; return true;
                case Kind.Capsule: q = ClosestOnSeg(s.A, s.B, c); return true;
                case Kind.Tris:
                    {
                        float best = 1e30f; V3 bq = new V3(0, 0, 0); bool any = false;
                        if (s.Grid != null && !float.IsPositiveInfinity(maxDist))
                        {
                            var buf = s.Grid.QueryBox(new V3(c.X - maxDist, c.Y - maxDist, c.Z - maxDist),
                                                      new V3(c.X + maxDist, c.Y + maxDist, c.Z + maxDist), out int cnt);
                            DebugLastTriTests = cnt;
                            for (int j = 0; j < cnt; j++)
                            {
                                int i = buf[j];
                                V3 p = ClosestOnTri(s.Tris[i * 3], s.Tris[i * 3 + 1], s.Tris[i * 3 + 2], c);
                                float dl = (c - p).Len();
                                if (dl < best) { best = dl; bq = p; any = true; }
                            }
                        }
                        else
                        {
                            int tc = s.Tris.Length / 3;
                            DebugLastTriTests = tc;
                            for (int i = 0; i < tc; i++)
                            {
                                V3 p = ClosestOnTri(s.Tris[i * 3], s.Tris[i * 3 + 1], s.Tris[i * 3 + 2], c);
                                float dl = (c - p).Len();
                                if (dl < best) { best = dl; bq = p; any = true; }
                            }
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

        // ---- test hooks for the MeshCollider grid (perf #342) ----------------------------------------------------
        // `tris` is a flat world-space triangle soup (x,y,z per vertex, 3 vertices per triangle). Tests run the same
        // query with and without the grid, compare the answers, and read DebugLastTriTests for selectivity.

        internal static Vector3 TestClosestOnTris(float[] tris, Vector3 c, float maxDist, bool useGrid, out bool hit)
        {
            var s = MakeTrisShape(tris, useGrid);
            hit = ClosestOnShape(s, From(c), out var q, useGrid ? maxDist : float.PositiveInfinity);
            return To(q);
        }

        internal static bool TestRaycastTris(float[] tris, Vector3 o, Vector3 dir, float maxDist, bool useGrid, out float t, out Vector3 n)
        {
            var s = MakeTrisShape(tris, useGrid);
            bool got = RayTris(From(o), From(dir), s, maxDist, out t, out var nn);
            n = To(nn); return got;
        }

        internal static bool TestRayDownTris(float[] tris, Vector3 o, float maxDist, bool useGrid, out float t)
            => RayDownTris(From(o), MakeTrisShape(tris, useGrid), maxDist, out t);

        internal static bool TestGridBuilt(float[] tris) => MakeTrisShape(tris, true).Grid != null;

        private static Shape MakeTrisShape(float[] triXyz, bool useGrid)
        {
            int n = triXyz.Length / 3;
            var tris = new V3[n];
            var mn = new V3(1e30f, 1e30f, 1e30f); var mx = new V3(-1e30f, -1e30f, -1e30f);
            for (int i = 0; i < n; i++)
            {
                var v = new V3(triXyz[i * 3], triXyz[i * 3 + 1], triXyz[i * 3 + 2]);
                tris[i] = v;
                mn = new V3(Math.Min(mn.X, v.X), Math.Min(mn.Y, v.Y), Math.Min(mn.Z, v.Z));
                mx = new V3(Math.Max(mx.X, v.X), Math.Max(mx.Y, v.Y), Math.Max(mx.Z, v.Z));
            }
            return new Shape { Kind = Kind.Tris, Tris = tris, Min = mn, Max = mx, Grid = useGrid ? TriGrid.Build(tris, mn, mx) : null };
        }
    }
}
