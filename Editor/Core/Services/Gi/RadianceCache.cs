using System;
using System.Collections.Generic;
using Vector3 = System.Numerics.Vector3;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using Editor.Utilities;

namespace Editor.Core.Services.Gi
{
    /// <summary>
    /// The world radiance cache (Lumen step 1, #257 step 3): a grid of probes around the camera that remember the light
    /// arriving from everywhere — the sky where it is open, the sunlit sand's bounce under a palm, the dark under a canopy —
    /// so a surface's ambient term knows about the world beyond the screen (the screen-space bounce only sees what is in
    /// the picture). Built on the CPU: the terrain, every foliage instance (a trunk and a crown) and every static mesh
    /// (its box) are voxelized at one metre around the camera; each probe shoots rays through the voxels, a hit returns
    /// the surface's albedo lit by the sun (a shadow ray) and the sky, a miss returns the sky, and the result is projected
    /// onto SH L1 (L0 in colour, L1 for the luminance). The probes go to the GPU as two small RGBA8 textures — the
    /// lit shaders' gi_volume() decodes them — laid out as y-slices with a padding row each and a header row at the end.
    /// A worker thread rebuilds the cache when the camera moves or the light changes; the main thread only uploads.
    /// </summary>
    public static class RadianceCache
    {
        public const int NX = 24, NY = 8, NZ = 24;        // probes: 96 x 24 x 96 m at 4 / 3 / 4 m
        public const float CellXZ = 4f, CellY = 3f;
        private const int VX = 112, VY = 40, VZ = 112;     // voxels at 1 m: the probe box plus an 8 m margin for the rays
        private const float VCell = 1f;
        private const int Rays = 24;
        private const float RayLength = 24f, ShadowLength = 18f;
        private const float RebuildSeconds = 0.9f, RebuildMoveMetres = 6f;

        public static bool Enabled { get; set; }
        private static readonly bool _log = Environment.GetEnvironmentVariable("VORTEX_GI_LOG") == "1";   // VORTEX_GI_LOG=1: what the cache does, every ~2 s
        public static float Strength { get; set; } = 1f;
        /// <summary>Probes traced by the last build (diagnostics).</summary>
        public static int LastProbes { get; private set; }
        public static double LastBuildMs { get; private set; }
        public static bool Active => _texA != ID.INVALID_ID;

        private static long _texA = ID.INVALID_ID, _texB = ID.INVALID_ID;
        private static readonly List<(long a, long b, int frame)> _graveyard = new List<(long, long, int)>();
        private static int _frame;
        private static Task _job;
        private static volatile bool _ready;
        private static byte[] _outA, _outB;
        private static Vector3 _lastCam, _lastSun; private static float _lastSunI, _lastAmbient, _lastTime; private static string _lastSkyKey;
        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        private struct Plant { public Vector3 Pos; public float TrunkR, TrunkH, CrownR, CrownRy, CrownY; public bool Bush; }
        private struct Box { public Vector3 Min, Max; }
        /// <summary>A mesh one can walk into (it carries a non-convex Mesh Collider): its world-space triangles. The voxelizer
        /// keeps its INSIDE open and its walls solid, so a cave or a house gets dark inside instead of lit like the open ground.</summary>
        private sealed class Hollow { public Vector3 Min, Max; public float[] Tris; public int X0, Y0, Z0, X1, Y1, Z1; public bool[] Inside; }
        private sealed class Snapshot
        {
            public Vector3 VOrigin;                 // the voxel grid's corner (metres)
            public float[] Heights;                 // VX x VZ terrain heights at the voxel columns' centres (NaN = no terrain)
            public readonly List<Plant> Plants = new List<Plant>();
            public readonly List<Box> Boxes = new List<Box>();
            public readonly List<Hollow> Hollows = new List<Hollow>();
            public Vector3 SunDir; public Vector3 SunCol; public float SunI; public float Ambient; public float[] Sky; public bool SkyOn;
            public Vector3 Origin;                  // the probe grid's corner (metres)
        }

        /// <summary>Once per frame from SceneRenderService after the lights: builds / uploads as needed.</summary>
        public static void Tick(Scene scene, Vector3 cam, Vector3 sunDir, Vector3 sunCol, float sunI, float[] sky27, bool skyOn, float ambient)
        {
            _frame++;
            if (_log && (_frame % 120) == 1)
                Console.WriteLine("[gi] tick: enabled=" + Enabled + " scene=" + (scene != null) + " cam=" + cam + " texture=" + (_texA != ID.INVALID_ID) + " job=" + (_job != null) + " last build " + LastBuildMs.ToString("0") + " ms, " + LastProbes + " probes");
            // the textures of a build two frames back are safe to free (the GPU has moved on)
            for (int i = _graveyard.Count - 1; i >= 0; i--)
                if (_frame - _graveyard[i].frame > 3) { try { VortexAPI.DeleteTexture(_graveyard[i].a); VortexAPI.DeleteTexture(_graveyard[i].b); } catch { } _graveyard.RemoveAt(i); }
            if (!Enabled || scene == null)
            {
                if (_texA != ID.INVALID_ID) { try { VortexAPI.SetGiVolume(ID.INVALID_ID, ID.INVALID_ID); } catch { } _graveyard.Add((_texA, _texB, _frame)); _texA = _texB = ID.INVALID_ID; }
                return;
            }
            if (_job != null)
            {
                if (!_ready) return;
                _ready = false; _job = null;
                Upload();
                return;
            }
            float now = (float)_clock.Elapsed.TotalSeconds;
            string skyKey = skyOn && sky27 != null ? (sky27[0].ToString("0.000") + sky27[1].ToString("0.000") + sky27[2].ToString("0.000")) : "";
            bool moved = (cam - _lastCam).Length() > RebuildMoveMetres;
            bool lit = (sunDir - _lastSun).Length() > 0.02f || Math.Abs(sunI - _lastSunI) > 0.05f || Math.Abs(ambient - _lastAmbient) > 0.02f || skyKey != _lastSkyKey;
            if (_texA != ID.INVALID_ID && !moved && !lit && now - _lastTime < RebuildSeconds) return;
            if (_texA != ID.INVALID_ID && !moved && !lit && now - _lastTime < 8f) return;   // a still camera and light: a slow refresh is enough
            _lastCam = cam; _lastSun = sunDir; _lastSunI = sunI; _lastAmbient = ambient; _lastSkyKey = skyKey; _lastTime = now;
            var snap = Capture(scene, cam, sunDir, sunCol, sunI, sky27, skyOn, ambient);
            _job = Task.Run(() => { try { Build(snap); } catch { _outA = null; _outB = null; } _ready = true; });
        }

        // ---------------------------------------------------------------- the snapshot (main thread)

        private static Snapshot Capture(Scene scene, Vector3 cam, Vector3 sunDir, Vector3 sunCol, float sunI, float[] sky27, bool skyOn, float ambient)
        {
            var s = new Snapshot();
            // the probe grid snaps to whole cells so a small camera move does not shift every probe
            float ox = (float)Math.Floor(cam.X / CellXZ) * CellXZ - NX * CellXZ * 0.5f;
            float oy = (float)Math.Floor(cam.Y / CellY) * CellY - NY * CellY * 0.5f + CellY;
            float oz = (float)Math.Floor(cam.Z / CellXZ) * CellXZ - NZ * CellXZ * 0.5f;
            s.Origin = new Vector3(ox, oy, oz);
            s.VOrigin = s.Origin - new Vector3((VX - NX * CellXZ) * 0.5f, (VY - NY * CellY) * 0.5f, (VZ - NZ * CellXZ) * 0.5f);
            s.SunDir = sunDir; s.SunCol = sunCol; s.SunI = sunI; s.Ambient = ambient; s.SkyOn = skyOn && sky27 != null; s.Sky = s.SkyOn ? (float[])sky27.Clone() : null;
            // terrain heights at every voxel column
            s.Heights = new float[VX * VZ];
            var terrains = new List<GameEntity>();
            var all = new List<GameEntity>();
            foreach (var root in scene.Entities) Walk(root, all);
            foreach (var e in all) if (e.ActiveInHierarchy && e.GetComponent<Editor.ECS.Components.Rendering.Terrain>() != null) terrains.Add(e);
            for (int z = 0; z < VZ; z++)
                for (int x = 0; x < VX; x++)
                {
                    float wx = s.VOrigin.X + (x + 0.5f) * VCell, wz = s.VOrigin.Z + (z + 0.5f) * VCell, best = float.NaN;
                    foreach (var t in terrains)
                        if (Terrain.TerrainService.TryHeight(t, wx, wz, out float h) && (float.IsNaN(best) || h > best)) best = h;
                    s.Heights[z * VX + x] = best;
                }
            // the plants: every instance inside the voxel box, sized by its type's mesh bounds
            float minX = s.VOrigin.X, maxX = minX + VX * VCell, minZ = s.VOrigin.Z, maxZ = minZ + VZ * VCell;
            var boundsOf = new Dictionary<string, Vector3>();
            Foliage.FoliageService.ForEachInstance(scene, (pos, scale, type) =>
            {
                if (pos.X < minX || pos.X > maxX || pos.Z < minZ || pos.Z > maxZ) return;
                Vector3 size;
                if (!boundsOf.TryGetValue(type.MeshPath ?? "", out size))
                {
                    var b = SceneRenderService.Instance.MeshBounds(type.MeshPath);
                    size = new Vector3(Math.Max(0.2f, b.X), Math.Max(0.2f, b.Y), Math.Max(0.2f, b.Z));
                    boundsOf[type.MeshPath ?? ""] = size;
                }
                float h = size.Y * scale, w = Math.Max(size.X, size.Z) * scale;
                var p = new Plant { Pos = pos };
                if (h < 2.5f) { p.Bush = true; p.CrownR = w * 0.5f; p.CrownRy = h * 0.5f; p.CrownY = h * 0.5f; }
                else { p.TrunkR = Math.Max(0.15f, w * 0.05f); p.TrunkH = h * 0.62f; p.CrownR = w * 0.5f; p.CrownRy = h * 0.2f; p.CrownY = h * 0.8f; }
                s.Plants.Add(p);
            });
            // the static meshes: their world boxes
            foreach (var e in all)
            {
                if (!e.ActiveInHierarchy) continue;
                var mr = e.GetComponent<MeshRenderer>();
                if (mr == null || string.IsNullOrEmpty(mr.MeshPath) || e.Transform == null) continue;
                LocalBounds(mr.MeshPath, out var b, out var c);
                var m = SceneRenderService.Instance.WorldMatrixOf(e);
                if (m == null || m.Length < 16) continue;
                // the world scale from the matrix's columns, the translation from its last column
                var sc = new Vector3(Len(m[0], m[1], m[2]), Len(m[4], m[5], m[6]), Len(m[8], m[9], m[10]));
                var wp = new Vector3(m[12], m[13], m[14]);
                var half = new Vector3(Math.Abs(b.X * sc.X), Math.Abs(b.Y * sc.Y), Math.Abs(b.Z * sc.Z)) * 0.5f;
                var centre = new Vector3(wp.X + c.X * sc.X, wp.Y + c.Y * sc.Y, wp.Z + c.Z * sc.Z);
                // a walk-in mesh (a cave, a tunnel, a room: it has a non-convex Mesh Collider) is voxelized by its triangles so
                // its inside stays open — only when the box is big enough to stand in; a box would fill it solid
                if (IsWalkIn(e) && half.Y > 0.9f && Math.Max(half.X, half.Z) > 0.9f)
                {
                    float[] local = null;
                    try { local = (Physics.CollisionService.MeshTriangleProvider ?? Editor.Scripting.ScriptRuntime.MeshTriangles)(mr.MeshPath); } catch { }   // the provider is installed at play start; edit mode reads the same source
                    if (local != null && local.Length >= 9 && local.Length <= 3 * 3 * 200000)
                    {
                        var tris = new float[local.Length];
                        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                        for (int i = 0; i + 2 < local.Length; i += 3)
                        {
                            float x = local[i], y = local[i + 1], z = local[i + 2];
                            // rows = the axis images, the last row the translation (WorldMatrixOf's layout)
                            float wx = x * m[0] + y * m[4] + z * m[8] + m[12], wy = x * m[1] + y * m[5] + z * m[9] + m[13], wz = x * m[2] + y * m[6] + z * m[10] + m[14];
                            tris[i] = wx; tris[i + 1] = wy; tris[i + 2] = wz;
                            mn = Vector3.Min(mn, new Vector3(wx, wy, wz)); mx = Vector3.Max(mx, new Vector3(wx, wy, wz));
                        }
                        s.Hollows.Add(new Hollow { Min = mn, Max = mx, Tris = tris });
                        continue;
                    }
                }
                if (half.X > 40f || half.Z > 40f || half.Y > 40f) continue;   // a sky dome or a floor plane is not an occluder here
                if (centre.X + half.X < minX || centre.X - half.X > maxX || centre.Z + half.Z < minZ || centre.Z - half.Z > maxZ) continue;
                s.Boxes.Add(new Box { Min = centre - half, Max = centre + half });
            }
            return s;
        }

        private static readonly Dictionary<string, (Vector3 size, Vector3 centre)> _boundsByPath = new Dictionary<string, (Vector3, Vector3)>();

        /// <summary>A model changed on disk: measure the boxes again.</summary>
        public static void ForgetBounds() { lock (_boundsByPath) _boundsByPath.Clear(); }

        /// <summary>A mesh's local box (size, centre). The render service answers by path, but a placed model's parts
        /// ("model.gltf#submeshN") are not always in its path caches and come back as the 1 m default — every rock, cliff
        /// and cave was a 1 m cube in the cache. Then the collision triangles (the same disk cache physics uses) give the
        /// real box, kept per path.</summary>
        private static void LocalBounds(string meshPath, out Vector3 size, out Vector3 centre)
        {
            if (_boundsByPath.TryGetValue(meshPath, out var known)) { size = known.size; centre = known.centre; return; }
            size = SceneRenderService.Instance.MeshBounds(meshPath); centre = SceneRenderService.Instance.MeshCenter(meshPath);
            bool fallback = Math.Abs(size.X - 1f) < 1e-6f && Math.Abs(size.Y - 1f) < 1e-6f && Math.Abs(size.Z - 1f) < 1e-6f
                            && !meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase);
            if (!fallback) { _boundsByPath[meshPath] = (size, centre); return; }
            float[] t = null;
            try { t = (Physics.CollisionService.MeshTriangleProvider ?? Editor.Scripting.ScriptRuntime.MeshTriangles)(meshPath); } catch { }
            if (t == null || t.Length < 9) return;   // not loadable (yet): ask again next build
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int i = 0; i + 2 < t.Length; i += 3) { var v = new Vector3(t[i], t[i + 1], t[i + 2]); mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
            size = mx - mn; centre = (mn + mx) * 0.5f;
            _boundsByPath[meshPath] = (size, centre);
        }

        /// <summary>A mesh one can walk into: it — or the model root it is a part of — carries an enabled, non-convex Mesh
        /// Collider (a cave or a building placed as a model keeps the collider on its root, the meshes on child entities).</summary>
        private static bool IsWalkIn(GameEntity e)
        {
            for (var cur = e; cur != null; cur = cur.Parent)
            {
                var mc = cur.GetComponent<Editor.ECS.Components.Physics.MeshCollider>();
                if (mc != null && mc.IsEnabled) return !mc.Convex;
                if (cur != e && cur.GetComponent<MeshRenderer>() != null) return false;   // a mesh parent is not a model root
            }
            return false;
        }

        private static void Walk(GameEntity e, List<GameEntity> into)
        {
            if (e == null) return;
            into.Add(e);
            if (e.Children != null) foreach (var c in e.Children) Walk(c, into);
        }
        private static float Len(float x, float y, float z) => (float)Math.Sqrt(x * x + y * y + z * z);

        // ---------------------------------------------------------------- the build (worker thread)

        private const byte Empty = 0, KTerrain = 1, KTrunk = 2, KCrown = 3, KMesh = 4;
        private static readonly Vector3[] Albedo = { Vector3.Zero, new Vector3(0.36f, 0.23f, 0.12f), new Vector3(0.28f, 0.19f, 0.11f), new Vector3(0.10f, 0.17f, 0.05f), new Vector3(0.30f, 0.28f, 0.25f) };
        private static readonly float[] SkyVis = { 1f, 0.75f, 0.45f, 0.5f, 0.6f };   // how much sky a surface of that kind still sees (a cheap stand-in for a second bounce)

        private static void Build(Snapshot s)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var vox = new byte[VX * VY * VZ];
            Voxelize(s, vox);
            // the ray directions: a Fibonacci sphere, the same set for every probe (the SH projection averages it out)
            var dirs = new Vector3[Rays];
            for (int i = 0; i < Rays; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / Rays, r = (float)Math.Sqrt(Math.Max(0f, 1f - y * y)), ph = 2.39996323f * i;
                dirs[i] = new Vector3(r * (float)Math.Cos(ph), y, r * (float)Math.Sin(ph));
            }
            int count = NX * NY * NZ;
            var L0 = new Vector3[count]; var L1 = new Vector3[count]; var valid = new bool[count];
            float wRay = 4f * (float)Math.PI / Rays;
            for (int iy = 0; iy < NY; iy++)
                for (int iz = 0; iz < NZ; iz++)
                    for (int ix = 0; ix < NX; ix++)
                    {
                        int k = (iy * NZ + iz) * NX + ix;
                        var p = s.Origin + new Vector3((ix + 0.5f) * CellXZ, (iy + 0.5f) * CellY, (iz + 0.5f) * CellXZ);
                        // a probe inside something (under the ground, in a trunk) moves up to one cell up; else it is invalid
                        bool ok = false;
                        for (int lift = 0; lift < 3 && !ok; lift++) { if (Voxel(s, vox, p) == Empty) ok = true; else p.Y += CellY * 0.5f; }
                        if (!ok) continue;
                        Vector3 c0 = Vector3.Zero, c1 = Vector3.Zero;
                        for (int r = 0; r < Rays; r++)
                        {
                            var d = dirs[r];
                            var L = Trace(s, vox, p, d);
                            c0 += L * (0.282095f * wRay);
                            float lum = 0.2126f * L.X + 0.7152f * L.Y + 0.0722f * L.Z;
                            c1 += d * (0.488603f * lum * wRay);
                        }
                        L0[k] = c0; L1[k] = c1; valid[k] = true;
                    }
            // an invalid probe (inside rock, under the ground) stays zero: the shaders weight the probes by validity, so a
            // surface near the ground or a cave wall reads only the probes in the open (they used to copy the upper
            // neighbour's light — through a hill that was the sky's)
            if (_log)
            {
                // VORTEX_GI_LOG: the walk-in meshes found, the voxels they opened, and the probes inside them against the open air
                int nIn = 0, vIn = 0, nOut = 0, insideVox = 0; double lIn = 0, lOut = 0;
                foreach (var h in s.Hollows) if (h.Inside != null) foreach (var b in h.Inside) if (b) insideVox++;
                for (int iy = 0; iy < NY; iy++) for (int iz = 0; iz < NZ; iz++) for (int ix = 0; ix < NX; ix++)
                {
                    int k = (iy * NZ + iz) * NX + ix;
                    var p = s.Origin + new Vector3((ix + 0.5f) * CellXZ, (iy + 0.5f) * CellY, (iz + 0.5f) * CellXZ);
                    bool inH = false;
                    foreach (var h in s.Hollows)
                    {
                        if (h.Inside == null) continue;
                        int x = (int)Math.Floor((p.X - s.VOrigin.X) / VCell), y = (int)Math.Floor((p.Y - s.VOrigin.Y) / VCell), z = (int)Math.Floor((p.Z - s.VOrigin.Z) / VCell);
                        if (x < h.X0 || y < h.Y0 || z < h.Z0 || x > h.X1 || y > h.Y1 || z > h.Z1) continue;
                        int w = h.X1 - h.X0 + 1, d = h.Z1 - h.Z0 + 1;
                        if (h.Inside[((y - h.Y0) * d + (z - h.Z0)) * w + (x - h.X0)]) { inH = true; break; }
                    }
                    float lum = 0.2126f * L0[k].X + 0.7152f * L0[k].Y + 0.0722f * L0[k].Z;
                    if (inH) { nIn++; if (valid[k]) { vIn++; lIn += lum; } } else if (valid[k]) { nOut++; lOut += lum; }
                }
                Console.WriteLine("[gi] build: " + s.Hollows.Count + " walk-in mesh(es), " + insideVox + " voxels opened, " + s.Boxes.Count + " boxes, " + s.Plants.Count
                    + " plants; probes inside " + nIn + " (valid " + vIn + ", mean L0 " + (vIn > 0 ? lIn / vIn : 0).ToString("0.000") + "), outside valid " + nOut
                    + " (mean L0 " + (nOut > 0 ? lOut / nOut : 0).ToString("0.000") + "), sun " + s.SunI.ToString("0.00") + ", sky " + s.SkyOn + ", ambient " + s.Ambient.ToString("0.00"));
            }
            Encode(s, L0, L1, valid);
            LastProbes = count; LastBuildMs = sw.Elapsed.TotalMilliseconds;
        }

        private static void Voxelize(Snapshot s, byte[] vox)
        {
            // the terrain: every column filled up to its height
            for (int z = 0; z < VZ; z++)
                for (int x = 0; x < VX; x++)
                {
                    float h = s.Heights[z * VX + x];
                    if (float.IsNaN(h)) continue;
                    int top = (int)Math.Floor((h - s.VOrigin.Y) / VCell);
                    if (top < 0) continue;
                    if (top >= VY) top = VY - 1;
                    for (int y = 0; y <= top; y++) vox[(y * VZ + z) * VX + x] = KTerrain;
                }
            // the plants
            foreach (var p in s.Plants)
            {
                if (!p.Bush) Cylinder(s, vox, p.Pos, p.TrunkR, p.TrunkH, KTrunk);
                Ellipsoid(s, vox, p.Pos + new Vector3(0f, p.CrownY, 0f), p.CrownR, p.CrownRy, KCrown);
            }
            // the meshes
            foreach (var b in s.Boxes)
            {
                int x0 = Clamp((int)Math.Floor((b.Min.X - s.VOrigin.X) / VCell), 0, VX - 1), x1 = Clamp((int)Math.Floor((b.Max.X - s.VOrigin.X) / VCell), 0, VX - 1);
                int y0 = Clamp((int)Math.Floor((b.Min.Y - s.VOrigin.Y) / VCell), 0, VY - 1), y1 = Clamp((int)Math.Floor((b.Max.Y - s.VOrigin.Y) / VCell), 0, VY - 1);
                int z0 = Clamp((int)Math.Floor((b.Min.Z - s.VOrigin.Z) / VCell), 0, VZ - 1), z1 = Clamp((int)Math.Floor((b.Max.Z - s.VOrigin.Z) / VCell), 0, VZ - 1);
                for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) vox[(y * VZ + z) * VX + x] = KMesh;
            }
            // the walk-in meshes: first every inside becomes air, then every wall becomes solid (in that order, so one
            // hollow's opening never erases another one's wall)
            foreach (var h in s.Hollows) OpenInside(s, vox, h);
            foreach (var h in s.Hollows) RasterWalls(s, vox, h);
        }

        /// <summary>Open the inside of a hollow mesh in the voxel grid: a voxel is inside when the mesh's surface lies on at
        /// least five of its six sides (along +x, -x, +y, -y, +z, -z — one side may stay open: the mouth). No winding is
        /// needed, so a cave's inward walls and its outward rock shell both count, and the terrain's column fill around a
        /// buried cave stays where the mesh does not enclose.</summary>
        private static void OpenInside(Snapshot s, byte[] vox, Hollow h)
        {
            h.X0 = Clamp((int)Math.Floor((h.Min.X - s.VOrigin.X) / VCell), 0, VX - 1); h.X1 = Clamp((int)Math.Floor((h.Max.X - s.VOrigin.X) / VCell), 0, VX - 1);
            h.Y0 = Clamp((int)Math.Floor((h.Min.Y - s.VOrigin.Y) / VCell), 0, VY - 1); h.Y1 = Clamp((int)Math.Floor((h.Max.Y - s.VOrigin.Y) / VCell), 0, VY - 1);
            h.Z0 = Clamp((int)Math.Floor((h.Min.Z - s.VOrigin.Z) / VCell), 0, VZ - 1); h.Z1 = Clamp((int)Math.Floor((h.Max.Z - s.VOrigin.Z) / VCell), 0, VZ - 1);
            if (h.X1 < h.X0 || h.Y1 < h.Y0 || h.Z1 < h.Z0) return;
            int w = h.X1 - h.X0 + 1, hh = h.Y1 - h.Y0 + 1, d = h.Z1 - h.Z0 + 1;
            var hits = new byte[w * hh * d];
            var t = h.Tris; int n = t.Length / 9;
            float[] org = { s.VOrigin.X, s.VOrigin.Y, s.VOrigin.Z };
            int[] lo = { h.X0, h.Y0, h.Z0 }, hi = { h.X1, h.Y1, h.Z1 };
            var bMin = new float[n]; var bMax = new float[n]; var cMin = new float[n]; var cMax = new float[n];
            var xs = new List<float>(16);
            var ijk = new int[3];
            for (int a = 0; a < 3; a++)
            {
                int b = (a + 1) % 3, c = (a + 2) % 3;
                for (int i = 0; i < n; i++)
                {
                    int o = i * 9;
                    bMin[i] = Math.Min(t[o + b], Math.Min(t[o + 3 + b], t[o + 6 + b])); bMax[i] = Math.Max(t[o + b], Math.Max(t[o + 3 + b], t[o + 6 + b]));
                    cMin[i] = Math.Min(t[o + c], Math.Min(t[o + 3 + c], t[o + 6 + c])); cMax[i] = Math.Max(t[o + c], Math.Max(t[o + 3 + c], t[o + 6 + c]));
                }
                for (int ib = lo[b]; ib <= hi[b]; ib++)
                    for (int ic = lo[c]; ic <= hi[c]; ic++)
                    {
                        float pb = org[b] + (ib + 0.5f) * VCell, pc = org[c] + (ic + 0.5f) * VCell;
                        xs.Clear();
                        for (int i = 0; i < n; i++)
                        {
                            if (pb < bMin[i] || pb > bMax[i] || pc < cMin[i] || pc > cMax[i]) continue;
                            int o = i * 9;
                            // the row (b = pb, c = pc) through the triangle: barycentrics in the b-c plane, then the value along a
                            float u0 = t[o + b], v0 = t[o + c], u1 = t[o + 3 + b], v1 = t[o + 3 + c], u2 = t[o + 6 + b], v2 = t[o + 6 + c];
                            float det = (u1 - u0) * (v2 - v0) - (v1 - v0) * (u2 - u0);
                            if (Math.Abs(det) < 1e-9f) continue;
                            float l1 = ((pb - u0) * (v2 - v0) - (pc - v0) * (u2 - u0)) / det;
                            float l2 = ((u1 - u0) * (pc - v0) - (v1 - v0) * (pb - u0)) / det;
                            if (l1 < 0f || l2 < 0f || l1 + l2 > 1f) continue;
                            xs.Add(t[o + a] + l1 * (t[o + 3 + a] - t[o + a]) + l2 * (t[o + 6 + a] - t[o + a]));
                        }
                        if (xs.Count == 0) continue;
                        float first = float.MaxValue, last = float.MinValue;
                        foreach (var x in xs) { if (x < first) first = x; if (x > last) last = x; }
                        ijk[b] = ib; ijk[c] = ic;
                        for (int ia = lo[a]; ia <= hi[a]; ia++)
                        {
                            float pa = org[a] + (ia + 0.5f) * VCell;
                            ijk[a] = ia;
                            int k = ((ijk[1] - h.Y0) * d + (ijk[2] - h.Z0)) * w + (ijk[0] - h.X0);
                            hits[k] += (byte)((first < pa ? 1 : 0) + (last > pa ? 1 : 0));
                        }
                    }
            }
            h.Inside = new bool[hits.Length];
            for (int y = h.Y0; y <= h.Y1; y++) for (int z = h.Z0; z <= h.Z1; z++) for (int x = h.X0; x <= h.X1; x++)
            {
                int k = ((y - h.Y0) * d + (z - h.Z0)) * w + (x - h.X0);
                if (hits[k] >= 5) { h.Inside[k] = true; vox[(y * VZ + z) * VX + x] = Empty; }
            }
        }

        /// <summary>A hollow mesh's walls: every triangle sampled half a voxel apart marks the voxels it passes through as
        /// solid — except the ones inside it (there the wall sits on the voxel's far side).</summary>
        private static void RasterWalls(Snapshot s, byte[] vox, Hollow h)
        {
            if (h.Inside == null) return;
            int w = h.X1 - h.X0 + 1, d = h.Z1 - h.Z0 + 1;
            var t = h.Tris; int n = t.Length / 9;
            for (int i = 0; i < n; i++)
            {
                int o = i * 9;
                var a = new Vector3(t[o], t[o + 1], t[o + 2]); var e1 = new Vector3(t[o + 3], t[o + 4], t[o + 5]) - a; var e2 = new Vector3(t[o + 6], t[o + 7], t[o + 8]) - a;
                int nu = Math.Max(1, (int)Math.Ceiling(e1.Length() / (VCell * 0.5f))), nv = Math.Max(1, (int)Math.Ceiling(e2.Length() / (VCell * 0.5f)));
                for (int iu = 0; iu <= nu; iu++)
                    for (int iv = 0; iv <= nv; iv++)
                    {
                        float u = iu / (float)nu, v = iv / (float)nv;
                        if (u + v > 1f) continue;
                        var p = a + e1 * u + e2 * v;
                        int x = (int)Math.Floor((p.X - s.VOrigin.X) / VCell), y = (int)Math.Floor((p.Y - s.VOrigin.Y) / VCell), z = (int)Math.Floor((p.Z - s.VOrigin.Z) / VCell);
                        if (x < h.X0 || y < h.Y0 || z < h.Z0 || x > h.X1 || y > h.Y1 || z > h.Z1) continue;
                        if (h.Inside[((y - h.Y0) * d + (z - h.Z0)) * w + (x - h.X0)]) continue;
                        vox[(y * VZ + z) * VX + x] = KMesh;
                    }
            }
        }

        private static void Cylinder(Snapshot s, byte[] vox, Vector3 basePos, float radius, float height, byte kind)
        {
            int x0 = Clamp((int)Math.Floor((basePos.X - radius - s.VOrigin.X) / VCell), 0, VX - 1), x1 = Clamp((int)Math.Floor((basePos.X + radius - s.VOrigin.X) / VCell), 0, VX - 1);
            int z0 = Clamp((int)Math.Floor((basePos.Z - radius - s.VOrigin.Z) / VCell), 0, VZ - 1), z1 = Clamp((int)Math.Floor((basePos.Z + radius - s.VOrigin.Z) / VCell), 0, VZ - 1);
            int y0 = Clamp((int)Math.Floor((basePos.Y - s.VOrigin.Y) / VCell), 0, VY - 1), y1 = Clamp((int)Math.Floor((basePos.Y + height - s.VOrigin.Y) / VCell), 0, VY - 1);
            for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
            {
                int i = (y * VZ + z) * VX + x;
                if (vox[i] == Empty) vox[i] = kind;   // a thin trunk still claims its voxel: the sun's shadow and the dark under the crown come from it
            }
        }

        private static void Ellipsoid(Snapshot s, byte[] vox, Vector3 centre, float r, float ry, byte kind)
        {
            int x0 = Clamp((int)Math.Floor((centre.X - r - s.VOrigin.X) / VCell), 0, VX - 1), x1 = Clamp((int)Math.Floor((centre.X + r - s.VOrigin.X) / VCell), 0, VX - 1);
            int z0 = Clamp((int)Math.Floor((centre.Z - r - s.VOrigin.Z) / VCell), 0, VZ - 1), z1 = Clamp((int)Math.Floor((centre.Z + r - s.VOrigin.Z) / VCell), 0, VZ - 1);
            int y0 = Clamp((int)Math.Floor((centre.Y - ry - s.VOrigin.Y) / VCell), 0, VY - 1), y1 = Clamp((int)Math.Floor((centre.Y + ry - s.VOrigin.Y) / VCell), 0, VY - 1);
            float ir2 = 1f / Math.Max(r * r, 0.01f), iry2 = 1f / Math.Max(ry * ry, 0.01f);
            for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
            {
                float dx = s.VOrigin.X + (x + 0.5f) * VCell - centre.X, dy = s.VOrigin.Y + (y + 0.5f) * VCell - centre.Y, dz = s.VOrigin.Z + (z + 0.5f) * VCell - centre.Z;
                if ((dx * dx + dz * dz) * ir2 + dy * dy * iry2 > 1f) continue;
                int i = (y * VZ + z) * VX + x;
                if (vox[i] == Empty) vox[i] = kind;
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static byte Voxel(Snapshot s, byte[] vox, Vector3 p)
        {
            int x = (int)Math.Floor((p.X - s.VOrigin.X) / VCell), y = (int)Math.Floor((p.Y - s.VOrigin.Y) / VCell), z = (int)Math.Floor((p.Z - s.VOrigin.Z) / VCell);
            if (x < 0 || y < 0 || z < 0 || x >= VX || y >= VY || z >= VZ) return Empty;
            return vox[(y * VZ + z) * VX + x];
        }

        /// <summary>Walk the voxels along a ray (Amanatides &amp; Woo). Returns the kind hit, the hit point and the face normal; Empty = left the grid or ran out.</summary>
        private static byte March(Snapshot s, byte[] vox, Vector3 o, Vector3 d, float maxLen, out Vector3 hit, out Vector3 normal)
            => March(s, vox, o, d, maxLen, out hit, out normal, out _);

        /// <param name="ranOut">true when the ray used up its length inside the grid (it did not leave it).</param>
        private static byte March(Snapshot s, byte[] vox, Vector3 o, Vector3 d, float maxLen, out Vector3 hit, out Vector3 normal, out bool ranOut)
        {
            hit = o; normal = Vector3.UnitY; ranOut = false;
            float lx = (o.X - s.VOrigin.X) / VCell, ly = (o.Y - s.VOrigin.Y) / VCell, lz = (o.Z - s.VOrigin.Z) / VCell;
            int x = (int)Math.Floor(lx), y = (int)Math.Floor(ly), z = (int)Math.Floor(lz);
            int sx = d.X > 0f ? 1 : -1, sy = d.Y > 0f ? 1 : -1, sz = d.Z > 0f ? 1 : -1;
            float tdx = Math.Abs(d.X) > 1e-6f ? VCell / Math.Abs(d.X) : float.MaxValue;
            float tdy = Math.Abs(d.Y) > 1e-6f ? VCell / Math.Abs(d.Y) : float.MaxValue;
            float tdz = Math.Abs(d.Z) > 1e-6f ? VCell / Math.Abs(d.Z) : float.MaxValue;
            float tmx = Math.Abs(d.X) > 1e-6f ? ((sx > 0 ? (x + 1) - lx : lx - x) * VCell) / Math.Abs(d.X) : float.MaxValue;
            float tmy = Math.Abs(d.Y) > 1e-6f ? ((sy > 0 ? (y + 1) - ly : ly - y) * VCell) / Math.Abs(d.Y) : float.MaxValue;
            float tmz = Math.Abs(d.Z) > 1e-6f ? ((sz > 0 ? (z + 1) - lz : lz - z) * VCell) / Math.Abs(d.Z) : float.MaxValue;
            float t = 0f;
            for (int step = 0; step < 200; step++)
            {
                int axis;
                if (tmx < tmy && tmx < tmz) { t = tmx; tmx += tdx; x += sx; axis = 0; }
                else if (tmy < tmz) { t = tmy; tmy += tdy; y += sy; axis = 1; }
                else { t = tmz; tmz += tdz; z += sz; axis = 2; }
                if (t > maxLen) { ranOut = true; return Empty; }
                if (x < 0 || y < 0 || z < 0 || x >= VX || y >= VY || z >= VZ) return Empty;
                byte k = vox[(y * VZ + z) * VX + x];
                if (k != Empty)
                {
                    hit = o + d * t;
                    normal = axis == 0 ? new Vector3(-sx, 0f, 0f) : (axis == 1 ? new Vector3(0f, -sy, 0f) : new Vector3(0f, 0f, -sz));
                    if (k == KTerrain) normal = TerrainNormal(s, hit);
                    return k;
                }
            }
            return Empty;
        }

        private static Vector3 TerrainNormal(Snapshot s, Vector3 p)
        {
            int x = Clamp((int)Math.Floor((p.X - s.VOrigin.X) / VCell), 1, VX - 2), z = Clamp((int)Math.Floor((p.Z - s.VOrigin.Z) / VCell), 1, VZ - 2);
            float hl = s.Heights[z * VX + x - 1], hr = s.Heights[z * VX + x + 1], hd = s.Heights[(z - 1) * VX + x], hu = s.Heights[(z + 1) * VX + x];
            if (float.IsNaN(hl) || float.IsNaN(hr) || float.IsNaN(hd) || float.IsNaN(hu)) return Vector3.UnitY;
            var n = new Vector3(hl - hr, 2f * VCell, hd - hu);
            return Vector3.Normalize(n);
        }

        /// <summary>The radiance arriving at a probe from direction d.</summary>
        private static Vector3 Trace(Snapshot s, byte[] vox, Vector3 p, Vector3 d)
        {
            byte k = March(s, vox, p, d, RayLength, out var hit, out var n, out bool ranOut);
            if (k == Empty) return ranOut && Underground(s, p + d * RayLength) ? Vector3.Zero : SkyRadiance(s, d);
            var albedo = Albedo[k];
            // the sun on the hit, through a shadow ray
            Vector3 E = Vector3.Zero;
            if (s.SunI > 0.001f)
            {
                var toSun = -s.SunDir;
                float ndl = Vector3.Dot(n, toSun);
                if (ndl > 0f)
                {
                    byte sh = March(s, vox, hit + n * 0.6f, toSun, ShadowLength, out _, out _);
                    if (sh == Empty) E += s.SunCol * (s.SunI * ndl);
                }
            }
            // the sky on the hit (its irradiance for that normal, scaled by how open that kind of surface is), times how much
            // of the sky the hit sees: three rays around the normal, tilted up — inside a cave or under a crown they are
            // blocked and almost nothing stays (a ray that runs out under the ground counts as blocked)
            E += SkyIrradiance(s, n) * (s.Ambient * SkyVis[k] * SkyOpen(s, vox, hit + n * 0.6f, n));
            return albedo * (E * (1f / (float)Math.PI));
        }

        /// <summary>Below the terrain's surface (the voxel columns' heights): a ray that ends there ran along a tunnel.</summary>
        private static bool Underground(Snapshot s, Vector3 p)
        {
            int x = (int)Math.Floor((p.X - s.VOrigin.X) / VCell), z = (int)Math.Floor((p.Z - s.VOrigin.Z) / VCell);
            if (x < 0 || z < 0 || x >= VX || z >= VZ) return false;
            float h = s.Heights[z * VX + x];
            return !float.IsNaN(h) && p.Y < h - 1f;
        }

        /// <summary>How much sky a surface point sees: 0.04 .. 1 from three rays around its normal, tilted up.</summary>
        private static float SkyOpen(Snapshot s, byte[] vox, Vector3 o, Vector3 n)
        {
            // a frame around the normal: the up-tilted normal and two rays leaning to its sides
            var up = Vector3.Normalize(n + new Vector3(0f, 0.7f, 0f));
            var side = Vector3.Cross(up, Math.Abs(up.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX);
            side = side.LengthSquared() > 1e-6f ? Vector3.Normalize(side) : Vector3.UnitX;
            int free = 0;
            for (int i = 0; i < 3; i++)
            {
                var d = i == 0 ? up : Vector3.Normalize(up + side * (i == 1 ? 0.8f : -0.8f));
                if (March(s, vox, o, d, ShadowLength, out _, out _, out bool ranOut) == Empty && !(ranOut && Underground(s, o + d * ShadowLength))) free++;
            }
            return 0.04f + 0.96f * free / 3f;
        }

        /// <summary>The sky's radiance in a direction: its irradiance there over pi (the shaders use the same SH).</summary>
        private static Vector3 SkyRadiance(Snapshot s, Vector3 d) => SkyIrradiance(s, d) * (s.Ambient / (float)Math.PI);

        private static Vector3 SkyIrradiance(Snapshot s, Vector3 n)
        {
            if (!s.SkyOn)
            {
                float up = n.Y * 0.5f + 0.5f;
                return Vector3.Lerp(new Vector3(0.15f, 0.15f, 0.18f), new Vector3(0.5f, 0.55f, 0.7f), up);
            }
            var c = s.Sky; float x = n.X, y = n.Y, z = n.Z;
            Vector3 C(int i) => new Vector3(c[i * 3], c[i * 3 + 1], c[i * 3 + 2]);
            var e = C(0) * 0.282095f;
            e += (C(1) * (0.488603f * y) + C(2) * (0.488603f * z) + C(3) * (0.488603f * x)) * (2f / 3f);
            e += (C(4) * (1.092548f * x * y) + C(5) * (1.092548f * y * z) + C(6) * (0.315392f * (3f * z * z - 1f)) + C(7) * (1.092548f * x * z) + C(8) * (0.546274f * (x * x - y * y))) * 0.25f;
            return Vector3.Max(e, Vector3.Zero);
        }

        // ---------------------------------------------------------------- the encoding and the upload

        private static void Encode(Snapshot s, Vector3[] L0, Vector3[] L1, bool[] valid)
        {
            int W = NX, H = NY * (NZ + 1) + 1;
            var A = new byte[W * H * 4]; var B = new byte[W * H * 4];
            float peak = 1e-3f;
            for (int k = 0; k < L0.Length; k++) if (valid[k]) peak = Math.Max(peak, Math.Max(L0[k].X, Math.Max(L0[k].Y, L0[k].Z)));
            int code = Clamp((int)Math.Round(128.0 + 16.0 * Math.Log(peak * 1.05, 2.0)), 0, 255);
            float scale = (float)Math.Pow(2.0, (code - 128) / 16.0);
            for (int iy = 0; iy < NY; iy++)
                for (int iz = 0; iz <= NZ; iz++)
                {
                    int srcZ = Math.Min(iz, NZ - 1), row = iy * (NZ + 1) + iz;
                    for (int ix = 0; ix < NX; ix++)
                    {
                        int k = (iy * NZ + srcZ) * NX + ix, o = (row * W + ix) * 4;
                        var l0 = L0[k] / scale; var l1 = L1[k] / scale;
                        A[o] = U8(Math.Sqrt(Clamp01(l0.X))); A[o + 1] = U8(Math.Sqrt(Clamp01(l0.Y))); A[o + 2] = U8(Math.Sqrt(Clamp01(l0.Z))); A[o + 3] = U8(l1.X * 0.5f + 0.5f);
                        B[o] = U8(l1.Y * 0.5f + 0.5f); B[o + 1] = U8(l1.Z * 0.5f + 0.5f); B[o + 2] = (byte)(valid[k] ? 255 : 0); B[o + 3] = 255;
                    }
                }
            // the header row: origin in cells, cell sizes x100, dims, strength x64, the scale code, the magic
            int hrow = H - 1;
            int ox = (int)Math.Round(s.Origin.X / CellXZ) + 32768, oy = (int)Math.Round(s.Origin.Y / CellY) + 32768, oz = (int)Math.Round(s.Origin.Z / CellXZ) + 32768;
            int cxz = (int)Math.Round(CellXZ * 100f), cy = (int)Math.Round(CellY * 100f);
            void Px(byte[] t, int x, byte r, byte g, byte b, byte a) { int o = (hrow * W + x) * 4; t[o] = r; t[o + 1] = g; t[o + 2] = b; t[o + 3] = a; }
            Px(A, 0, (byte)(ox & 255), (byte)(ox >> 8), (byte)(oy & 255), (byte)(oy >> 8));
            Px(A, 1, (byte)(oz & 255), (byte)(oz >> 8), (byte)(cxz & 255), (byte)(cxz >> 8));
            Px(A, 2, (byte)(cy & 255), (byte)(cy >> 8), (byte)NX, (byte)NY);
            Px(A, 3, (byte)NZ, (byte)Clamp((int)Math.Round(Strength * 64f), 0, 255), (byte)code, 90);
            for (int x = 0; x < W; x++) { int o = (hrow * W + x) * 4; B[o + 2] = 0; B[o + 3] = 255; }
            _outA = A; _outB = B;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static byte U8(double v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255.0)));

        private static void Upload()
        {
            if (_outA == null || _outB == null) return;
            int W = NX, H = NY * (NZ + 1) + 1;
            long a = VortexAPI.CreateTextureFromPixels(W, H, _outA, false, false, 0);
            long b = VortexAPI.CreateTextureFromPixels(W, H, _outB, false, false, 0);
            if (a == ID.INVALID_ID || b == ID.INVALID_ID) { if (a != ID.INVALID_ID) VortexAPI.DeleteTexture(a); if (b != ID.INVALID_ID) VortexAPI.DeleteTexture(b); return; }
            if (_texA != ID.INVALID_ID) _graveyard.Add((_texA, _texB, _frame));
            _texA = a; _texB = b;
            try { VortexAPI.SetGiVolume(a, b); } catch { }
            _outA = null; _outB = null;
        }
    }
}
