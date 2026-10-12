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
        private sealed class Snapshot
        {
            public Vector3 VOrigin;                 // the voxel grid's corner (metres)
            public float[] Heights;                 // VX x VZ terrain heights at the voxel columns' centres (NaN = no terrain)
            public readonly List<Plant> Plants = new List<Plant>();
            public readonly List<Box> Boxes = new List<Box>();
            public Vector3 SunDir; public Vector3 SunCol; public float SunI; public float Ambient; public float[] Sky; public bool SkyOn;
            public Vector3 Origin;                  // the probe grid's corner (metres)
        }

        /// <summary>Once per frame from SceneRenderService after the lights: builds / uploads as needed.</summary>
        public static void Tick(Scene scene, Vector3 cam, Vector3 sunDir, Vector3 sunCol, float sunI, float[] sky27, bool skyOn, float ambient)
        {
            _frame++;
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
                var b = SceneRenderService.Instance.MeshBounds(mr.MeshPath); var c = SceneRenderService.Instance.MeshCenter(mr.MeshPath);
                var m = SceneRenderService.Instance.WorldMatrixOf(e);
                if (m == null || m.Length < 16) continue;
                // the world scale from the matrix's columns, the translation from its last column
                var sc = new Vector3(Len(m[0], m[1], m[2]), Len(m[4], m[5], m[6]), Len(m[8], m[9], m[10]));
                var wp = new Vector3(m[12], m[13], m[14]);
                var half = new Vector3(Math.Abs(b.X * sc.X), Math.Abs(b.Y * sc.Y), Math.Abs(b.Z * sc.Z)) * 0.5f;
                var centre = new Vector3(wp.X + c.X * sc.X, wp.Y + c.Y * sc.Y, wp.Z + c.Z * sc.Z);
                if (half.X > 40f || half.Z > 40f || half.Y > 40f) continue;   // a sky dome or a floor plane is not an occluder here
                if (centre.X + half.X < minX || centre.X - half.X > maxX || centre.Z + half.Z < minZ || centre.Z - half.Z > maxZ) continue;
                s.Boxes.Add(new Box { Min = centre - half, Max = centre + half });
            }
            return s;
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
            // an invalid probe takes its upper neighbour's light, so the lerp at the ground does not darken
            for (int iy = 0; iy < NY - 1; iy++)
                for (int iz = 0; iz < NZ; iz++)
                    for (int ix = 0; ix < NX; ix++)
                    {
                        int k = (iy * NZ + iz) * NX + ix, up = ((iy + 1) * NZ + iz) * NX + ix;
                        if (!valid[k] && valid[up]) { L0[k] = L0[up]; L1[k] = L1[up]; }
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
        {
            hit = o; normal = Vector3.UnitY;
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
                if (t > maxLen) return Empty;
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
            byte k = March(s, vox, p, d, RayLength, out var hit, out var n);
            if (k == Empty) return SkyRadiance(s, d);
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
            // the sky on the hit (its irradiance for that normal, scaled by how open that kind of surface is)
            E += SkyIrradiance(s, n) * (s.Ambient * SkyVis[k]);
            return albedo * (E * (1f / (float)Math.PI));
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
