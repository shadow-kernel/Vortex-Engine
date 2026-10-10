using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.Core.Services.Terrain;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.Utilities;
using Vector3 = System.Numerics.Vector3;

namespace Editor.Core.Services.Water
{
    /// <summary>The surface mesh of one body of water: a grid over the square whose every vertex carries the depth of the
    /// ground below it in uv.x (negative beyond the shore), so the water shader fades, darkens and foams by itself.</summary>
    public static class WaterMeshBuilder
    {
        public sealed class Surface
        {
            public float[] Vertices;
            public uint[] Indices;
            public float Half;
            public int Cells;
            public int VertexCount => Vertices.Length / 8;
            public int TriangleCount => Indices.Length / 3;
            /// <summary>Vertices whose ground lies below the surface (the wet part).</summary>
            public int WetVertices;
        }

        /// <summary>Build the surface centred on the local origin: <paramref name="ground"/> gives the ground height under a
        /// LOCAL (x, z) or null where nothing lies below; <paramref name="level"/> is the water's height in the same frame.</summary>
        public static Surface Build(float size, float cellSize, float level, float defaultDepth, Func<float, float, float?> ground)
        {
            float half = Math.Max(0.5f, size * 0.5f);
            float cell = Math.Max(0.25f, cellSize);
            int n = Math.Max(1, Math.Min(1024, (int)Math.Ceiling(size / cell)));
            int verts = n + 1;
            var v = new float[verts * verts * 8];
            var idx = new uint[n * n * 6];
            int wet = 0;
            for (int j = 0; j < verts; j++)
            {
                float z = -half + 2f * half * j / n;
                for (int i = 0; i < verts; i++)
                {
                    float x = -half + 2f * half * i / n;
                    float? g = ground(x, z);
                    float depth = g.HasValue ? level - g.Value : defaultDepth;
                    if (depth > 0f) wet++;
                    int o = (j * verts + i) * 8;
                    v[o] = x; v[o + 1] = 0f; v[o + 2] = z;
                    v[o + 3] = 0f; v[o + 4] = 1f; v[o + 5] = 0f;
                    v[o + 6] = depth; v[o + 7] = 0f;
                }
            }
            int k = 0;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    uint a = (uint)(j * verts + i), b = a + 1, c = a + (uint)verts, d = c + 1;
                    idx[k++] = a; idx[k++] = c; idx[k++] = b;
                    idx[k++] = b; idx[k++] = c; idx[k++] = d;
                }
            return new Surface { Vertices = v, Indices = idx, Half = half, Cells = n, WetVertices = wet };
        }
    }

    /// <summary>
    /// The runtime of every <see cref="Editor.ECS.Components.Rendering.Water"/> in the scene (#200): builds the surface mesh
    /// over the terrain (rebuilt when the terrain under it is sculpted or the component changes), binds the water shader
    /// with the component's colours and waves, submits the surface into the transparent pass every frame and answers
    /// <see cref="TryHeight"/> / <see cref="IsUnderwater"/> for scripts, buoyancy and the swim mode.
    /// </summary>
    public static class WaterService
    {
        private const int GraveFrames = 3;

        private sealed class Runtime
        {
            public GameEntity Entity;
            public Editor.ECS.Components.Rendering.Water Component;
            public long Mesh = ID.INVALID_ID;
            public long Material = ID.INVALID_ID;
            public string Key;
            public Vector3 Centre;
            public float Half;
            public int SeenFrame;
            public bool Dirty = true;
            public float DirtyAt;
            public int WetVertices;
        }

        private static readonly Dictionary<GameEntity, Runtime> _runtimes = new Dictionary<GameEntity, Runtime>();
        private static readonly List<(long id, int frame)> _graveyard = new List<(long, int)>();
        private static readonly float[] _world = new float[16];
        private static Scene _scene;
        private static int _frame;
        private static bool _hooked;
        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        public static int Count => _runtimes.Count;
        /// <summary>Surfaces submitted by the last <see cref="Submit"/>.</summary>
        public static int LastSubmitted { get; private set; }
        public static float Time => (float)_clock.Elapsed.TotalSeconds;

        // ---------------------------------------------------------------- per frame

        public static void Submit(Scene scene, bool playLike)
        {
            _frame++;
            if (!ReferenceEquals(scene, _scene)) { Clear(); _scene = scene; }
            Hook();
            LastSubmitted = 0;
            if (scene?.Entities == null || !VortexAPI.TerrainApiAvailable) return;
            foreach (var e in scene.Entities) SubmitRecursive(e, playLike);
            for (int i = _graveyard.Count - 1; i >= 0; i--)
            {
                if (_frame - _graveyard[i].frame <= GraveFrames) continue;
                try { VortexAPI.DeleteMesh(_graveyard[i].id); } catch { }
                _graveyard.RemoveAt(i);
            }
            List<GameEntity> gone = null;
            foreach (var kv in _runtimes) if (kv.Value.SeenFrame != _frame) (gone ?? (gone = new List<GameEntity>())).Add(kv.Key);
            if (gone != null) foreach (var g in gone) { Release(_runtimes[g]); _runtimes.Remove(g); }
        }

        private static void SubmitRecursive(GameEntity e, bool playLike)
        {
            if (e == null || !e.IsActive) return;
            if (e.IsHiddenInEditor && !playLike) return;
            var w = e.GetComponent<Editor.ECS.Components.Rendering.Water>();
            if (w != null && w.IsEnabled)
            {
                try { SubmitOne(e, w); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Water] submit failed for '" + e.Name + "': " + ex.Message); }
            }
            if (e.Children != null) foreach (var c in e.Children) SubmitRecursive(c, playLike);
        }

        private static void SubmitOne(GameEntity e, Editor.ECS.Components.Rendering.Water w)
        {
            var rt = Ensure(e, w);
            rt.SeenFrame = _frame;
            if (rt.Dirty && Time - rt.DirtyAt > 0.25f) Rebuild(rt);
            if (rt.Mesh == ID.INVALID_ID || rt.Material == ID.INVALID_ID) return;
            ApplyParams(rt);
            var c = rt.Centre;
            Array.Clear(_world, 0, 16);
            _world[0] = 1f; _world[5] = 1f; _world[10] = 1f; _world[15] = 1f;
            _world[12] = c.X; _world[13] = c.Y; _world[14] = c.Z;
            VortexAPI.SubmitMeshForRenderingTinted(rt.Mesh, rt.Material, _world, w.ShallowR, w.ShallowG, w.ShallowB, 1f, 0);
            LastSubmitted++;
        }

        private static Runtime Ensure(GameEntity e, Editor.ECS.Components.Rendering.Water w)
        {
            Runtime rt;
            if (!_runtimes.TryGetValue(e, out rt))
            {
                rt = new Runtime { Entity = e, Component = w };
                rt.Material = VortexAPI.CreateColorMaterial(w.DeepR, w.DeepG, w.DeepB, 0.1f);
                if (rt.Material != ID.INVALID_ID)
                {
                    string shader = VortexAPI.BuiltinMaterialShaderPath("water");
                    if (!string.IsNullOrEmpty(shader)) VortexAPI.SetMaterialShader((int)rt.Material, shader);
                    VortexAPI.SetMaterialBlendModeValue(rt.Material, 1);
                    VortexAPI.SetMaterialTwoSided(rt.Material, false);
                }
                _runtimes[e] = rt;
            }
            rt.Component = w;
            var pos = Animation.BoneSocketService.EntityWorld(e).Translation;
            string key = w.Size.ToString("R") + "|" + w.CellSize.ToString("R") + "|" + w.DefaultDepth.ToString("R") + "|" + pos.X.ToString("0.###") + "|" + pos.Y.ToString("0.###") + "|" + pos.Z.ToString("0.###");
            if (!string.Equals(key, rt.Key, StringComparison.Ordinal)) { rt.Key = key; rt.Centre = pos; rt.Half = w.Size * 0.5f; rt.Dirty = true; rt.DirtyAt = -10f; }
            return rt;
        }

        private static void Rebuild(Runtime rt)
        {
            rt.Dirty = false;
            var w = rt.Component;
            var c = rt.Centre;
            var surface = WaterMeshBuilder.Build(w.Size, w.CellSize, c.Y, w.DefaultDepth, (lx, lz) =>
            {
                float gx = c.X + lx, gz = c.Z + lz;
                var terrain = TerrainService.FindAt(gx, gz);
                float y;
                if (terrain != null && TerrainService.TryHeight(terrain, gx, gz, out y)) return y;
                return (float?)null;
            });
            float half = surface.Half;
            long mesh = VortexAPI.CreateTerrainMesh(surface.Vertices, surface.VertexCount, surface.Indices, surface.Indices.Length,
                new[] { -half, -0.2f, -half }, new[] { half, 0.2f, half }, "Water_" + rt.Entity.Name);
            if (rt.Mesh != ID.INVALID_ID) _graveyard.Add((rt.Mesh, _frame));
            rt.Mesh = mesh;
            rt.WetVertices = surface.WetVertices;
        }

        private static void ApplyParams(Runtime rt)
        {
            var w = rt.Component;
            long m = rt.Material;
            VortexAPI.SetMaterialBaseColor(m, w.DeepR, w.DeepG, w.DeepB, w.Reflection);
            VortexAPI.SetMaterialNormalStrengthValue(m, w.Absorption);
            VortexAPI.SetMaterialTiling(m, w.WaveScale, w.WaveSpeed);
            VortexAPI.SetMaterialAlphaCutoffValue(m, w.WaveHeight);
            VortexAPI.SetMaterialHeightDepth(m, Time % 10000f);
            VortexAPI.SetMaterialEmissiveBrightness(m, w.FoamWidth);
            VortexAPI.SetMaterialRoughnessValue(m, w.Roughness);
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            // the terrain under a lake changed: its shore moves
            TerrainService.Changed += terrain => { foreach (var kv in _runtimes) { kv.Value.Dirty = true; kv.Value.DirtyAt = Time; } };
        }

        // ---------------------------------------------------------------- queries

        /// <summary>The water surface height (world Y) covering the world XZ point: the nearest body whose square holds the
        /// point and whose ground lies below its level there. False when no water covers it.</summary>
        public static bool TryHeight(float x, float z, out float y)
        {
            y = 0f;
            foreach (var kv in _runtimes)
            {
                var rt = kv.Value;
                if (Math.Abs(x - rt.Centre.X) > rt.Half || Math.Abs(z - rt.Centre.Z) > rt.Half) continue;
                var terrain = TerrainService.FindAt(x, z);
                float g;
                if (terrain != null && TerrainService.TryHeight(terrain, x, z, out g))
                {
                    if (g >= rt.Centre.Y) continue;   // dry ground above the level: the bank
                }
                else if (rt.Component.DefaultDepth <= 0f) continue;
                y = rt.Centre.Y;
                return true;
            }
            return false;
        }

        public static bool IsUnderwater(Vector3 position, float margin = 0f)
        {
            float y;
            return TryHeight(position.X, position.Z, out y) && position.Y < y - margin;
        }

        /// <summary>Wet vertices of an entity's surface (how much of the square is actually water) — for checks and tools.</summary>
        public static int WetVertices(GameEntity e)
        {
            Runtime rt;
            return e != null && _runtimes.TryGetValue(e, out rt) ? rt.WetVertices : 0;
        }

        /// <summary>Rebuild an entity's surface now (after a wholesale terrain change).</summary>
        /// <summary>The deep part of a surface as world-space boxes (centre xyz, half xyz per entry) for the navmesh bake: every
        /// <paramref name="cell"/>-metre square of the wet area whose ground lies more than <paramref name="minDepth"/> under the
        /// level becomes a box from the lake bed up to the surface — agents walk the shallows, not the lake.</summary>
        public static List<(Vector3 centre, Vector3 half)> DeepBoxes(GameEntity e, float minDepth = 0.7f, float cell = 6f)
        {
            var list = new List<(Vector3, Vector3)>();
            Runtime rt;
            if (e == null || !_runtimes.TryGetValue(e, out rt) || rt.Component == null) return list;
            var w = rt.Component;
            var wp = TransformMath.WorldPosition(e);
            var pos = new Vector3(wp.X, wp.Y, wp.Z);
            var terrain = TerrainService.FindAt(pos.X, pos.Z);
            float half = w.Size * 0.5f;
            int n = Math.Max(1, (int)Math.Ceiling(w.Size / cell));
            for (int zi = 0; zi < n; zi++)
                for (int xi = 0; xi < n; xi++)
                {
                    float cx = pos.X - half + (xi + 0.5f) * cell, cz = pos.Z - half + (zi + 0.5f) * cell;
                    float g = 0f;
                    bool ground = terrain != null && TerrainService.TryHeight(terrain, cx, cz, out g);
                    if (!ground) { var t2 = TerrainService.FindAt(cx, cz); if (t2 == null || !TerrainService.TryHeight(t2, cx, cz, out g)) continue; }
                    float depth = pos.Y - g;
                    if (depth < minDepth) continue;
                    list.Add((new Vector3(cx, pos.Y - depth * 0.5f, cz), new Vector3(cell * 0.5f, depth * 0.5f + 0.5f, cell * 0.5f)));
                }
            return list;
        }

        public static void Invalidate(GameEntity e)
        {
            Runtime rt;
            if (e != null && _runtimes.TryGetValue(e, out rt)) { rt.Dirty = true; rt.DirtyAt = -10f; }
        }

        private static void Release(Runtime rt)
        {
            if (rt == null) return;
            if (rt.Mesh != ID.INVALID_ID) _graveyard.Add((rt.Mesh, _frame));
            if (rt.Material != ID.INVALID_ID) { try { VortexAPI.DeleteMaterial(rt.Material); } catch { } }
            rt.Mesh = ID.INVALID_ID; rt.Material = ID.INVALID_ID;
        }

        public static void Clear()
        {
            foreach (var kv in _runtimes) Release(kv.Value);
            _runtimes.Clear();
        }
    }
}
