using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Terrain;
using Editor.DllWrapper;
using Editor.ECS;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;
using Editor.ECS.Components.Rendering;
using Editor.Utilities;

namespace Editor.Core.Services.Terrain
{
    /// <summary>
    /// The runtime of every <see cref="Editor.ECS.Components.Rendering.Terrain"/> in the active scene (#124): loads the
    /// <c>.vterrain</c> data, builds the chunk meshes (three LODs with skirts) and the layer atlases + splat map, binds the
    /// terrain material shader and submits the chunks every frame with a per-chunk LOD by camera distance. Sculpting and
    /// painting (the editor tools, <c>Terrain.Deform</c> in scripts) mark sample rectangles dirty; the next submit rebuilds
    /// only the touched chunks, re-uploads the splat map and — while a world is built — refreshes the collision.
    /// Replaced GPU resources die three frames later (the renderer may still reference them).
    /// </summary>
    public static class TerrainService
    {
        /// <summary>Texels per layer in the 2x2 layer atlas when a terrain leaves its Layer Resolution on Auto: 2048 (a 4096²
        /// atlas per map) while textures are block compressed, 1024 when compression is off (VORTEX_TEXTURE_COMPRESSION=0).
        /// <c>VORTEX_TERRAIN_ATLAS</c> = 256 / 512 / 1024 / 2048 overrides every terrain (a debugging switch).</summary>
        public static readonly int AtlasTile = ReadAtlasTile();
        public static readonly int AtlasSize = AtlasTile * 2;
        private static readonly int EnvAtlasTile = ReadEnvAtlasTile();
        /// <summary>The atlas gutter the terrain shaders inset by (pad = 1/32 of a quadrant): the builder fills it with the WRAPPED
        /// texture, so the bilinear footprint and every mip up to the shader's cap stay seamless across tile repeats.</summary>
        public const int AtlasGutterDiv = 32;
        private static int ReadEnvAtlasTile()
        {
            try
            {
                var s = Environment.GetEnvironmentVariable("VORTEX_TERRAIN_ATLAS");
                int v;
                if (!string.IsNullOrEmpty(s) && int.TryParse(s, out v) && (v == 256 || v == 512 || v == 1024 || v == 2048)) return v;
            }
            catch { }
            return 0;
        }
        private static int ReadAtlasTile()
        {
            int env = ReadEnvAtlasTile();
            if (env > 0) return env;
            // 2048 per layer (a 4096² atlas) once the atlas is block compressed; 1024 when compression is off (VORTEX_TEXTURE_COMPRESSION=0)
            try { if (Environment.GetEnvironmentVariable("VORTEX_TEXTURE_COMPRESSION") == "0") return 1024; } catch { }
            return 2048;
        }
        /// <summary>The layer resolution a terrain's atlas is built at (#447): the debugging override, else the terrain's own
        /// Layer Resolution, else the Auto default.</summary>
        internal static int TileFor(Editor.ECS.Components.Rendering.Terrain t)
        {
            if (EnvAtlasTile > 0) return EnvAtlasTile;
            int own = t != null ? Editor.ECS.Components.Rendering.Terrain.SnapLayerResolution(t.LayerResolution) : 0;
            return own > 0 ? own : AtlasTile;
        }
        private const int GraveFrames = 3;

        private sealed class Runtime
        {
            public GameEntity Entity;
            public Editor.ECS.Components.Rendering.Terrain Component;
            public TerrainData Data;
            public float Size;
            public string LayerKey;
            public long Material = ID.INVALID_ID;
            public long AlbedoAtlas = ID.INVALID_ID, NormalAtlas = ID.INVALID_ID, RoughnessAtlas = ID.INVALID_ID, SplatTexture = ID.INVALID_ID;
            public long[] Meshes;            // [chunk * LodCount + lod]
            public float[] ChunkMidHeight;   // for the LOD distance
            public bool[] ChunkDirty;
            public bool AllChunksDirty = true, SplatDirty = true, LayersDirty = true, CollisionDirty, Unsaved;
            public int SeenFrame;
            public int ChunksPerSide => TerrainMeshBuilder.ChunksPerSide(Data.Resolution);
            public float CellSize => Size / (Data.Resolution - 1);

            public void AllocChunks()
            {
                int n = ChunksPerSide * ChunksPerSide;
                if (Meshes == null || Meshes.Length != n * TerrainMeshBuilder.LodCount)
                {
                    Meshes = new long[n * TerrainMeshBuilder.LodCount];
                    for (int i = 0; i < Meshes.Length; i++) Meshes[i] = ID.INVALID_ID;
                    ChunkMidHeight = new float[n];
                    ChunkDirty = new bool[n];
                }
                for (int i = 0; i < n; i++) ChunkDirty[i] = true;
            }
        }

        private static readonly Dictionary<GameEntity, Runtime> _runtimes = new Dictionary<GameEntity, Runtime>();
        private static readonly List<(long id, bool texture, int frame)> _graveyard = new List<(long, bool, int)>();
        private static readonly float[] _world = new float[16];
        private static Scene _scene;
        private static int _frame;
        private static bool _hooked;

        /// <summary>Chunk instances submitted by the last <see cref="Submit"/> (stats, smoke checks).</summary>
        public static int LastChunkSubmits { get; private set; }
        private static float _farApplied = -1f;   // the far plane last pushed to the view (see Submit)
        /// <summary>Terrains alive in the service.</summary>
        public static int Count => _runtimes.Count;
        /// <summary>Milliseconds the last layer-atlas build took (stats).</summary>
        public static double LastAtlasBuildMs { get; private set; }

        public static event Action<GameEntity> Changed;

        // ---------------------------------------------------------------- per frame

        /// <summary>Submit every terrain of the scene for this frame (called from SceneRenderService.SubmitScene).</summary>
        public static void Submit(Scene scene, bool playLike)
        {
            _frame++;
            if (!ReferenceEquals(scene, _scene)) { Clear(); _scene = scene; Hook(); }
            LastChunkSubmits = 0;
            if (scene?.Entities == null || !VortexAPI.TerrainApiAvailable) return;

            Vector3 cam = CameraPosition(scene, playLike);
            foreach (var e in scene.Entities) SubmitRecursive(e, cam, playLike);

            // the editor view's far plane follows the largest terrain: a 4 km backdrop is drawn to its edge, not clipped at 1 km
            float needed = 1000f;
            foreach (var kv in _runtimes) if (kv.Value.SeenFrame == _frame && kv.Value.Size * 1.5f > needed) needed = kv.Value.Size * 1.5f;
            if (needed > 20000f) needed = 20000f;
            if (Math.Abs(RaycastService.EditorFarClip - needed) > 0.5f) RaycastService.EditorFarClip = needed;
            if (!playLike && Math.Abs(_farApplied - needed) > 0.5f)
            {
                _farApplied = needed;
                try { VortexAPI.SetViewClipPlanes(RaycastService.EditorNearClip, needed); } catch { }
            }

            for (int i = _graveyard.Count - 1; i >= 0; i--)
            {
                if (_frame - _graveyard[i].frame <= GraveFrames) continue;
                var g = _graveyard[i];
                try { if (g.texture) VortexAPI.DeleteTexture(g.id); else VortexAPI.DeleteMesh(g.id); } catch { }
                _graveyard.RemoveAt(i);
            }
            List<GameEntity> gone = null;
            foreach (var kv in _runtimes)
                if (kv.Value.SeenFrame != _frame) (gone ?? (gone = new List<GameEntity>())).Add(kv.Key);
            if (gone != null)
                foreach (var g in gone) { Release(_runtimes[g]); _runtimes.Remove(g); }
        }

        private static void SubmitRecursive(GameEntity e, Vector3 cam, bool playLike)
        {
            if (e == null || !e.IsActive) return;
            if (e.IsHiddenInEditor && !playLike) return;
            var t = e.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
            if (t != null && t.IsEnabled)
            {
                try { SubmitOne(e, t, cam); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Terrain] submit failed for '" + e.Name + "': " + ex.Message); }
            }
            if (e.Children != null) foreach (var c in e.Children) SubmitRecursive(c, cam, playLike);
        }

        private static void SubmitOne(GameEntity e, Editor.ECS.Components.Rendering.Terrain t, Vector3 cam)
        {
            var rt = Ensure(e, t);
            if (rt == null) return;
            rt.SeenFrame = _frame;
            Flush(rt);
            if (rt.Material == ID.INVALID_ID || rt.Meshes == null) return;

            Pose(e, out var pos, out var rot);
            WorldMatrix(pos, rot, _world);
            int n = rt.ChunksPerSide;
            float chunkWorld = TerrainMeshBuilder.ChunkCells * rt.CellSize;
            for (int cz = 0; cz < n; cz++)
                for (int cx = 0; cx < n; cx++)
                {
                    int ci = cz * n + cx;
                    var lc = new Vector3((cx + 0.5f) * chunkWorld, rt.ChunkMidHeight[ci], (cz + 0.5f) * chunkWorld);
                    var wc = Vector3.Transform(lc, rot) + pos;
                    int lod = TerrainMeshBuilder.LodFor(Vector3.Distance(wc, cam), t.LodDistance);
                    long mesh = rt.Meshes[ci * TerrainMeshBuilder.LodCount + lod];
                    if (mesh == ID.INVALID_ID) mesh = rt.Meshes[ci * TerrainMeshBuilder.LodCount];
                    if (mesh == ID.INVALID_ID) continue;
                    VortexAPI.SubmitMeshForRenderingTinted(mesh, rt.Material, _world, 1f, 1f, 1f, 1f, 0);
                    LastChunkSubmits++;
                }
        }

        private static Runtime Ensure(GameEntity e, Editor.ECS.Components.Rendering.Terrain t)
        {
            Runtime rt;
            if (!_runtimes.TryGetValue(e, out rt))
            {
                rt = new Runtime { Entity = e, Component = t, Size = t.Size };
                rt.Data = LoadData(t) ?? new TerrainData(t.Resolution);
                rt.Material = VortexAPI.CreateTerrainMaterial();
                _runtimes[e] = rt;
            }
            rt.Component = t;
            if (rt.Data.Resolution != t.Resolution)
            {
                rt.Data = rt.Data.Resample(t.Resolution);
                rt.AllChunksDirty = true; rt.SplatDirty = true; rt.CollisionDirty = true; rt.Unsaved = true;
            }
            if (Math.Abs(rt.Size - t.Size) > 1e-5f) { rt.Size = t.Size; rt.AllChunksDirty = true; rt.CollisionDirty = true; }
            string lk = LayerKey(t);
            if (!string.Equals(lk, rt.LayerKey, StringComparison.Ordinal)) { rt.LayerKey = lk; rt.LayersDirty = true; }
            return rt;
        }

        private static string LayerKey(Editor.ECS.Components.Rendering.Terrain t)
            => TileFor(t) + "|" + (t.Layer0Material ?? "") + "|" + t.Layer0Tile.ToString("R") + "|" + (t.Layer1Material ?? "") + "|" + t.Layer1Tile.ToString("R") + "|"
             + (t.Layer2Material ?? "") + "|" + t.Layer2Tile.ToString("R") + "|" + (t.Layer3Material ?? "") + "|" + t.Layer3Tile.ToString("R");

        private static void Flush(Runtime rt)
        {
            if (rt.LayersDirty) { RebuildLayerTextures(rt); rt.LayersDirty = false; }
            if (rt.AllChunksDirty) { rt.AllocChunks(); rt.AllChunksDirty = false; }
            if (rt.ChunkDirty != null)
            {
                int n = rt.ChunksPerSide;
                float cell = rt.CellSize;
                for (int ci = 0; ci < rt.ChunkDirty.Length; ci++)
                {
                    if (!rt.ChunkDirty[ci]) continue;
                    rt.ChunkDirty[ci] = false;
                    int cx = ci % n, cz = ci / n;
                    for (int lod = 0; lod < TerrainMeshBuilder.LodCount; lod++)
                    {
                        var cm = TerrainMeshBuilder.Build(rt.Data, cx, cz, lod, cell);
                        long mesh = VortexAPI.CreateTerrainMesh(cm.Vertices, cm.VertexCount, cm.Indices, cm.Indices.Length,
                            new[] { cm.Min.X, cm.Min.Y, cm.Min.Z }, new[] { cm.Max.X, cm.Max.Y, cm.Max.Z }, "Terrain_" + cx + "_" + cz + "_L" + lod);
                        int slot = ci * TerrainMeshBuilder.LodCount + lod;
                        if (rt.Meshes[slot] != ID.INVALID_ID) _graveyard.Add((rt.Meshes[slot], false, _frame));
                        rt.Meshes[slot] = mesh;
                        if (lod == 0) rt.ChunkMidHeight[ci] = 0.5f * (cm.Max.Y + (cm.Min.Y + (cm.Max.Y - cm.Min.Y) * 0.5f));
                    }
                }
            }
            if (rt.SplatDirty)
            {
                int res = rt.Data.Resolution;
                long tex = VortexAPI.CreateTextureFromPixels(res, res, rt.Data.Splat, false, false);
                if (rt.SplatTexture != ID.INVALID_ID) _graveyard.Add((rt.SplatTexture, true, _frame));
                rt.SplatTexture = tex;
                VortexAPI.SetTerrainMaterialTextures(rt.Material, rt.AlbedoAtlas, rt.NormalAtlas, rt.RoughnessAtlas, rt.SplatTexture);
                rt.SplatDirty = false;
            }
            if (rt.CollisionDirty)
            {
                rt.CollisionDirty = false;
                try
                {
                    if (Physics.CollisionService.IsBuilt) { Physics.CollisionService.RemoveEntityShapes(rt.Entity, false); Physics.CollisionService.AddEntityShapes(rt.Entity); }
                    if (Physics.PhysicsService.IsBuilt) { Physics.PhysicsService.RemoveEntity(rt.Entity); Physics.PhysicsService.AddEntity(rt.Entity); }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Terrain] collision refresh failed: " + ex.Message); }
            }
        }

        // ---------------------------------------------------------------- layer atlases

        private static readonly (byte r, byte g, byte b)[] DefaultLayerColours =
        {
            (96, 128, 56),     // 0 grass
            (112, 86, 58),     // 1 dirt
            (122, 122, 120),   // 2 rock
            (196, 176, 126),   // 3 sand
        };

        private static void RebuildLayerTextures(Runtime rt)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var t = rt.Component;
            int tile = TileFor(t), size = tile * 2;
            int innerPx = tile - 2 * (tile / AtlasGutterDiv);
            var albedo = new byte[size * size * 4];
            var normal = new byte[size * size * 4];
            var rough = new byte[size * size * 4];
            var resampled = new List<string>();   // the layer maps a lot smaller or larger than the atlas holds (#447)
            void Note(int layer, string map, int w, int h)
            {
                float k = Math.Max(w, h) / (float)innerPx;
                if (k > 1.25f || k < 0.5f) resampled.Add("layer " + layer + " " + map + " " + w + "×" + h + (k > 1f ? " area-filtered down" : " scaled up") + " to " + innerPx + "²");
            }
            float roughFallback = 0f; int roughCount = 0;
            bool directX = true;
            for (int i = 0; i < 4; i++)
            {
                VortexMaterial mat = null;
                string mp = ResolvePath(t.LayerMaterial(i));
                string matDir = mp != null ? Path.GetDirectoryName(mp) : null;   // a .vmat names its maps relative to its own folder
                string matRelDir = string.IsNullOrEmpty(t.LayerMaterial(i)) ? null : Path.GetDirectoryName(t.LayerMaterial(i).Replace('\\', '/'))?.Replace('\\', '/');
                if (mp != null && File.Exists(mp)) { try { mat = VortexMaterial.Load(mp); } catch { mat = null; } }
                if (mat == null && !string.IsNullOrEmpty(t.LayerMaterial(i)))
                {
                    // a pak-only material (shipped game)
                    byte[] mb;
                    if (AssetVfs.TryGetBytes(t.LayerMaterial(i).Replace('\\', '/'), out mb))
                        try { mat = System.Text.Json.JsonSerializer.Deserialize<VortexMaterial>(System.Text.Encoding.UTF8.GetString(mb)); } catch { mat = null; }
                }
                if (i == 0 && mat != null) directX = mat.UseDirectXNormals;

                // albedo (× base colour) or a flat default colour
                byte[] px; int w, h;
                var dc = DefaultLayerColours[i];
                float br = 1f, bg = 1f, bb = 1f;
                if (mat?.BaseColor != null && mat.BaseColor.Length >= 3) { br = mat.BaseColor[0]; bg = mat.BaseColor[1]; bb = mat.BaseColor[2]; }
                if (mat != null && TryLoadImage(mat.AlbedoTexture, matDir, matRelDir, out px, out w, out h))
                {
                    if (br != 1f || bg != 1f || bb != 1f) Multiply(px, br, bg, bb);
                    Blit(albedo, i, px, w, h, tile); Note(i, "albedo", w, h);
                }
                else FillQuadrant(albedo, i, (byte)Math.Min(255, dc.r * br), (byte)Math.Min(255, dc.g * bg), (byte)Math.Min(255, dc.b * bb), 255, 10, i * 7919, tile);

                // normal map or flat
                if (mat != null && TryLoadImage(mat.NormalTexture, matDir, matRelDir, out px, out w, out h)) { Blit(normal, i, px, w, h, tile); Note(i, "normal", w, h); }
                else FillQuadrant(normal, i, 128, 128, 255, 255, 0, 0, tile);

                // roughness: a dedicated map (R), else the G of a packed metallic-roughness / ORM map, else the scalar
                string roughMap = mat?.RoughnessTexture;
                int channel = 0;
                if (string.IsNullOrEmpty(roughMap) && mat != null)
                {
                    roughMap = !string.IsNullOrEmpty(mat.OcclusionRoughnessMetallicTexture) ? mat.OcclusionRoughnessMetallicTexture : mat.MetallicRoughnessTexture;
                    channel = 1;
                }
                if (mat != null && TryLoadImage(roughMap, matDir, matRelDir, out px, out w, out h))
                {
                    if (channel != 0) ChannelToRed(px, channel);
                    Blit(rough, i, px, w, h, tile); Note(i, "roughness", w, h);
                }
                else
                {
                    byte rv = (byte)Math.Max(0, Math.Min(255, (int)Math.Round((mat != null ? mat.Roughness : 0.9f) * 255f)));
                    FillQuadrant(rough, i, rv, rv, rv, 255, 0, 0, tile);
                }
                if (mat != null) { roughFallback += mat.Roughness; roughCount++; }
            }

            // block compressed (#179): albedo BC1, normal BC5 (the terrain shader rebuilds z), roughness BC4 — a 4096² atlas set is
            // ~38 MB instead of ~256 MB
            if (resampled.Count > 0)
                try { ConsoleService.Instance.LogSystem("Terrain '" + (rt.Entity?.Name ?? "?") + "': layer resolution " + tile + " (" + innerPx + "² usable per layer) — " + string.Join("; ", resampled) + ". Change the terrain's Layer Resolution (512 / 1024 / 2048) or the maps' size to match."); } catch { }
            long a = VortexAPI.CreateTextureFromPixels(size, size, albedo, false, true, VortexAPI.TextureKindColor);
            long nrm = VortexAPI.CreateTextureFromPixels(size, size, normal, false, true, VortexAPI.TextureKindNormal);
            long r = VortexAPI.CreateTextureFromPixels(size, size, rough, false, true, VortexAPI.TextureKindGray);
            if (rt.AlbedoAtlas != ID.INVALID_ID) _graveyard.Add((rt.AlbedoAtlas, true, _frame));
            if (rt.NormalAtlas != ID.INVALID_ID) _graveyard.Add((rt.NormalAtlas, true, _frame));
            if (rt.RoughnessAtlas != ID.INVALID_ID) _graveyard.Add((rt.RoughnessAtlas, true, _frame));
            rt.AlbedoAtlas = a; rt.NormalAtlas = nrm; rt.RoughnessAtlas = r;
            VortexAPI.SetTerrainMaterialParams(rt.Material, t.Layer0Tile, t.Layer1Tile, t.Layer2Tile, t.Layer3Tile,
                roughCount > 0 ? roughFallback / roughCount : 0.9f, 1f, directX);
            VortexAPI.SetTerrainMaterialTextures(rt.Material, rt.AlbedoAtlas, rt.NormalAtlas, rt.RoughnessAtlas, rt.SplatTexture);
            sw.Stop();
            LastAtlasBuildMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Read + decode a material's map: the path is relative to the material's folder (how .vmat files name their
        /// maps), else project-relative, else a pak entry (shipped game) under either spelling.</summary>
        private static bool TryLoadImage(string path, string matDir, string matRelDir, out byte[] rgba, out int w, out int h)
        {
            rgba = null; w = h = 0;
            if (string.IsNullOrWhiteSpace(path)) return false;
            byte[] bytes = null;
            try
            {
                string p = path.Replace('\\', '/');
                string abs = null;
                if (!Path.IsPathRooted(p) && !string.IsNullOrEmpty(matDir)) abs = Path.GetFullPath(Path.Combine(matDir, p));
                if (abs == null || !File.Exists(abs)) abs = ResolvePath(p);
                if (abs != null && File.Exists(abs)) bytes = File.ReadAllBytes(abs);
                else
                {
                    string rel = p;
                    if (!Path.IsPathRooted(p) && !string.IsNullOrEmpty(matRelDir))
                    {
                        // normalise "Assets/Materials/Gen/../../Textures/x.jpg" -> "Assets/Textures/x.jpg" for the pak
                        var parts = new List<string>();
                        foreach (var seg in (matRelDir + "/" + p).Split('/'))
                        {
                            if (seg == "" || seg == ".") continue;
                            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                            parts.Add(seg);
                        }
                        rel = string.Join("/", parts);
                    }
                    if (!AssetVfs.TryGetBytes(rel, out bytes) && !AssetVfs.TryGetBytes(p, out bytes)) bytes = null;
                }
            }
            catch { bytes = null; }
            if (bytes == null || bytes.Length == 0) return false;
            rgba = VortexAPI.DecodeImage(bytes, out w, out h);
            return rgba != null && w > 0 && h > 0;
        }

        private static void Multiply(byte[] px, float r, float g, float b)
        {
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                px[i] = (byte)Math.Min(255f, px[i] * r);
                px[i + 1] = (byte)Math.Min(255f, px[i + 1] * g);
                px[i + 2] = (byte)Math.Min(255f, px[i + 2] * b);
            }
        }

        private static void ChannelToRed(byte[] px, int channel)
        {
            for (int i = 0; i + 3 < px.Length; i += 4) px[i] = px[i + channel];
        }

        /// <summary>Resample an RGBA image into quadrant <paramref name="q"/> of an atlas with <paramref name="tile"/> texels per
        /// layer (wraps for tiling textures). The picture fills the quadrant's INNER region (inset by the shader's gutter) and the
        /// gutter around it holds the wrapped continuation — so a tile repeat is seamless and no mip pulls the neighbouring layer
        /// in. A larger picture is AREA-filtered (#447): every source texel counts by how much of the atlas texel it covers; one
        /// bilinear tap per atlas texel skipped most texels of a 2K / 4K map and the terrain sparkled. A smaller one is scaled up
        /// bilinearly.</summary>
        internal static void Blit(byte[] atlas, int q, byte[] src, int sw, int sh, int tile)
        {
            int size = tile * 2;
            int ox = (q & 1) * tile, oy = (q >> 1) * tile;
            int gutter = tile / AtlasGutterDiv;
            float inner = tile - 2f * gutter;
            var wx = Taps(tile, gutter, sw / inner, sw);
            var wy = Taps(tile, gutter, sh / inner, sh);
            System.Threading.Tasks.Parallel.For(0, tile, y =>
            {
                var row = new float[tile * 4];
                foreach (var (sy, wyk) in wy[y])
                {
                    int srow = sy * sw * 4;
                    for (int x = 0; x < tile; x++)
                    {
                        float r = 0f, g = 0f, b = 0f, a = 0f;
                        foreach (var (sx, w) in wx[x])
                        {
                            int i = srow + sx * 4;
                            r += src[i] * w; g += src[i + 1] * w; b += src[i + 2] * w; a += src[i + 3] * w;
                        }
                        int o = x * 4;
                        row[o] += r * wyk; row[o + 1] += g * wyk; row[o + 2] += b * wyk; row[o + 3] += a * wyk;
                    }
                }
                int dst = ((oy + y) * size + ox) * 4;
                for (int i = 0; i < row.Length; i++) atlas[dst + i] = (byte)Math.Min(255f, row[i] + 0.5f);
            });
        }

        /// <summary>The source texels (wrapped) and weights, summing to 1, of each of <paramref name="count"/> atlas texels: a box
        /// over the texel's footprint when the picture is shrunk (<paramref name="scale"/> = source texels per atlas texel > 1), a
        /// two-tap tent when it is enlarged.</summary>
        internal static (int index, float weight)[][] Taps(int count, int gutter, float scale, int sourceLength)
        {
            var taps = new (int index, float weight)[count][];
            for (int d = 0; d < count; d++)
            {
                if (scale <= 1f)
                {
                    float f = (d - gutter + 0.5f) * scale - 0.5f;
                    int i0 = (int)Math.Floor(f); float t = f - i0;
                    taps[d] = new[] { (Wrap(i0, sourceLength), 1f - t), (Wrap(i0 + 1, sourceLength), t) };
                    continue;
                }
                float a = (d - gutter) * scale, b = a + scale;
                int lo = (int)Math.Floor(a), hi = (int)Math.Ceiling(b);
                var list = new (int index, float weight)[hi - lo];
                float sum = 0f;
                for (int i = lo; i < hi; i++)
                {
                    float w = Math.Min(b, i + 1) - Math.Max(a, i);
                    list[i - lo] = (Wrap(i, sourceLength), w); sum += w;
                }
                for (int k = 0; k < list.Length; k++) list[k].weight /= sum;
                taps[d] = list;
            }
            return taps;
        }

        private static int Wrap(int i, int n) => ((i % n) + n) % n;

        /// <summary>Fill quadrant <paramref name="q"/> with a colour, optionally dithered by ±<paramref name="noise"/> so a flat
        /// default layer still shows its tiling.</summary>
        private static void FillQuadrant(byte[] atlas, int q, byte r, byte g, byte b, byte a, int noise, int seed, int tile)
        {
            int size = tile * 2;
            int ox = (q & 1) * tile, oy = (q >> 1) * tile;
            uint s = (uint)seed * 2654435761u + 12345u;
            for (int y = 0; y < tile; y++)
                for (int x = 0; x < tile; x++)
                {
                    int o = ((oy + y) * size + ox + x) * 4;
                    int d = 0;
                    if (noise > 0)
                    {
                        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                        d = (int)(s % (uint)(2 * noise + 1)) - noise;
                    }
                    atlas[o] = (byte)Math.Max(0, Math.Min(255, r + d));
                    atlas[o + 1] = (byte)Math.Max(0, Math.Min(255, g + d));
                    atlas[o + 2] = (byte)Math.Max(0, Math.Min(255, b + d));
                    atlas[o + 3] = a;
                }
        }

        // ---------------------------------------------------------------- data, paths, saving

        private static TerrainData LoadData(Editor.ECS.Components.Rendering.Terrain t)
        {
            if (string.IsNullOrWhiteSpace(t.DataPath)) return null;
            try
            {
                string abs = ResolvePath(t.DataPath);
                if (abs != null && File.Exists(abs)) return TerrainData.Load(abs);
                byte[] bytes;
                if (AssetVfs.TryGetBytes(t.DataPath.Replace('\\', '/'), out bytes)) return TerrainData.FromBytes(bytes);
            }
            catch { }
            return null;
        }

        /// <summary>Absolute path of a project-relative asset path (null for empty).</summary>
        public static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string p = path.Replace('\\', '/');
            try
            {
                if (Path.IsPathRooted(p)) return Path.GetFullPath(p);
                string root = ProjectData.Current?.Path;
                return Path.GetFullPath(string.IsNullOrEmpty(root) ? p : Path.Combine(root, p));
            }
            catch { return null; }
        }

        /// <summary>Where a terrain without a data file is saved: Assets/Terrain/&lt;entity&gt;.vterrain.</summary>
        public static string DefaultDataPath(GameEntity e)
        {
            string name = e?.Name ?? "Terrain";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Replace(' ', '_');
            if (string.IsNullOrEmpty(name)) name = "Terrain";
            string rel = "Assets/Terrain/" + name + ".vterrain";
            // never share a file between two terrains
            string abs = ResolvePath(rel);
            int k = 2;
            while (abs != null && File.Exists(abs) && !OwnsPath(e, rel))
            {
                rel = "Assets/Terrain/" + name + "_" + k++ + ".vterrain";
                abs = ResolvePath(rel);
            }
            return rel;
        }

        private static bool OwnsPath(GameEntity e, string rel)
        {
            var t = e?.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
            return t != null && string.Equals((t.DataPath ?? "").Replace('\\', '/'), rel, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Write the terrain's heights and splat map to its <c>.vterrain</c> (assigning the default path when it has none).
        /// A terrain that has not rendered yet gets its data now: a new terrain owns its file — and its path is in the scene — from
        /// the start, not only from the scene save after the first one (#411 review).</summary>
        public static bool Save(GameEntity e)
        {
            Runtime rt;
            if (e == null) return false;
            if (!_runtimes.TryGetValue(e, out rt))
            {
                var tc = e.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
                if (tc == null) return false;
                try { rt = Ensure(e, tc); Hook(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Terrain] no data to save: " + ex.Message); return false; }
            }
            var t = rt.Component;
            if (string.IsNullOrWhiteSpace(t.DataPath)) t.DataPath = DefaultDataPath(e);
            string abs = ResolvePath(t.DataPath);
            if (abs == null) return false;
            bool created = !File.Exists(abs);
            SceneDataFiles.BeforeWrite(abs);   // a revert puts the saved version back (#411)
            try { rt.Data.Save(abs); rt.Unsaved = false; PlaySession.NoteWrite(abs, created); return true; }   // a terrain made in play takes its file along (#423)
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Terrain] save failed: " + ex.Message); return false; }
        }

        /// <summary>Save every terrain of the scene that has unsaved sculpting (scene save hook).</summary>
        public static int SaveAll(Scene scene)
        {
            int n = 0;
            foreach (var kv in _runtimes)
                if ((kv.Value.Unsaved || string.IsNullOrWhiteSpace(kv.Value.Component.DataPath)) && kv.Key.IsActive && Save(kv.Key)) n++;
            return n;
        }

        public static bool HasUnsavedChanges(GameEntity e) => e != null && _runtimes.TryGetValue(e, out var rt) && rt.Unsaved;

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            // before the scene file is written, by every save path: a data path assigned now is saved with the scene (#411 review)
            Scene.Saving += scene => { try { SaveAll(scene); } catch { } };
        }

        // ---------------------------------------------------------------- editing

        /// <summary>The terrain's data for editing (loads it when the terrain has not rendered yet).</summary>
        public static bool TryGetData(GameEntity e, out TerrainData data, out float cellSize)
        {
            data = null; cellSize = 0f;
            var t = e?.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
            if (t == null) return false;
            var rt = Ensure(e, t);
            if (rt == null) return false;
            data = rt.Data; cellSize = rt.CellSize;
            return true;
        }

        /// <summary>Tell the service a sample rectangle changed: the touched chunks rebuild, the splat map re-uploads and a
        /// built collision world refreshes on the next submit.</summary>
        public static void MarkDirty(GameEntity e, SampleRect rect, bool heights, bool splat)
        {
            Runtime rt;
            if (e == null || !_runtimes.TryGetValue(e, out rt)) return;
            rt.Unsaved = true;
            rt.Data.MarkChanged();
            if (heights && rt.ChunkDirty != null && !rect.IsEmpty)
            {
                int n = rt.ChunksPerSide;
                TerrainMeshBuilder.ChunkRange(rect, rt.Data.Resolution, out int cx0, out int cz0, out int cx1, out int cz1);
                for (int cz = cz0; cz <= cz1; cz++)
                    for (int cx = cx0; cx <= cx1; cx++)
                        rt.ChunkDirty[cz * n + cx] = true;
                rt.CollisionDirty = true;
            }
            else if (heights) { rt.AllChunksDirty = true; rt.CollisionDirty = true; }
            if (splat) rt.SplatDirty = true;
            Changed?.Invoke(e);
        }

        /// <summary>Rebuild everything of a terrain (after a wholesale data change).</summary>
        public static void Invalidate(GameEntity e)
        {
            Runtime rt;
            if (e == null || !_runtimes.TryGetValue(e, out rt)) return;
            rt.AllChunksDirty = true; rt.SplatDirty = true; rt.CollisionDirty = true; rt.Unsaved = true;
            rt.Data.MarkChanged();
            Changed?.Invoke(e);
        }

        // ---------------------------------------------------------------- queries (world space)

        /// <summary>The terrain's world pose (the entity's position + rotation; its scale is ignored).</summary>
        public static void Pose(GameEntity e, out Vector3 position, out Quaternion rotation)
        {
            var m = Animation.BoneSocketService.EntityWorld(e);
            Vector3 scale; Quaternion rot; Vector3 trans;
            if (!Matrix4x4.Decompose(m, out scale, out rot, out trans)) { rot = Quaternion.Identity; trans = m.Translation; }
            position = trans;
            rotation = Quaternion.Normalize(rot);
        }

        private static void WorldMatrix(Vector3 pos, Quaternion rot, float[] into)
        {
            var m = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
            into[0] = m.M11; into[1] = m.M12; into[2] = m.M13; into[3] = m.M14;
            into[4] = m.M21; into[5] = m.M22; into[6] = m.M23; into[7] = m.M24;
            into[8] = m.M31; into[9] = m.M32; into[10] = m.M33; into[11] = m.M34;
            into[12] = m.M41; into[13] = m.M42; into[14] = m.M43; into[15] = m.M44;
        }

        public static Vector3 WorldToLocal(GameEntity e, Vector3 world)
        {
            Pose(e, out var pos, out var rot);
            return Vector3.Transform(world - pos, Quaternion.Inverse(rot));
        }

        public static Vector3 LocalToWorld(GameEntity e, Vector3 local)
        {
            Pose(e, out var pos, out var rot);
            return Vector3.Transform(local, rot) + pos;
        }

        /// <summary>The terrains of the active scene (those the service has seen).</summary>
        public static IEnumerable<GameEntity> Terrains()
        {
            foreach (var kv in _runtimes) yield return kv.Key;
        }

        /// <summary>The terrain whose footprint contains the world XZ position (null when none).</summary>
        public static GameEntity FindAt(float wx, float wz)
        {
            foreach (var kv in _runtimes)
            {
                var rt = kv.Value;
                var l = WorldToLocal(kv.Key, new Vector3(wx, 0f, wz));
                if (l.X >= 0f && l.Z >= 0f && l.X <= rt.Size && l.Z <= rt.Size) return kv.Key;
            }
            return null;
        }

        /// <summary>Surface height (world Y) of a terrain under the world XZ position; false outside it.</summary>
        public static bool TryHeight(GameEntity e, float wx, float wz, out float y)
        {
            y = 0f;
            Runtime rt;
            if (e == null || !_runtimes.TryGetValue(e, out rt)) return false;
            var l = WorldToLocal(e, new Vector3(wx, 0f, wz));
            if (l.X < 0f || l.Z < 0f || l.X > rt.Size || l.Z > rt.Size) return false;
            float cell = rt.CellSize;
            if (rt.Data.IsHoleAt(l.X / cell, l.Z / cell)) return false;   // a hole: no ground here (the cave mesh under it is the ground)
            float h = rt.Data.SampleHeight(l.X / cell, l.Z / cell);
            y = LocalToWorld(e, new Vector3(l.X, h, l.Z)).Y;
            return true;
        }

        /// <summary>Surface normal (world) of a terrain under the world XZ position.</summary>
        public static bool TryNormal(GameEntity e, float wx, float wz, out Vector3 normal)
        {
            normal = Vector3.UnitY;
            Runtime rt;
            if (e == null || !_runtimes.TryGetValue(e, out rt)) return false;
            var l = WorldToLocal(e, new Vector3(wx, 0f, wz));
            if (l.X < 0f || l.Z < 0f || l.X > rt.Size || l.Z > rt.Size) return false;
            float cell = rt.CellSize;
            Pose(e, out _, out var rot);
            normal = Vector3.Normalize(Vector3.Transform(rt.Data.SampleNormal(l.X / cell, l.Z / cell, cell), rot));
            return true;
        }

        /// <summary>Ray against the terrains (all, or <paramref name="only"/>): the nearest surface hit in world space.</summary>
        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hit, out GameEntity terrain, GameEntity only = null)
        {
            hit = default(Vector3); terrain = null;
            float best = float.MaxValue;
            foreach (var kv in _runtimes)
            {
                if (only != null && !ReferenceEquals(kv.Key, only)) continue;
                var rt = kv.Value;
                Pose(kv.Key, out var pos, out var rot);
                var inv = Quaternion.Inverse(rot);
                var lo = Vector3.Transform(origin - pos, inv);
                var ld = Vector3.Transform(direction, inv);
                Vector3 lh;
                if (!rt.Data.Raycast(lo, ld, rt.CellSize, maxDistance, out lh)) continue;
                var wh = Vector3.Transform(lh, rot) + pos;
                float d = Vector3.Distance(origin, wh);
                if (d < best) { best = d; hit = wh; terrain = kv.Key; }
            }
            return terrain != null;
        }

        // ---------------------------------------------------------------- collision feeds

        /// <summary>Local-space triangle soup (9 floats per triangle) for the managed collision world and the navmesh bake:
        /// every sample up to 257², coarser beyond so the soup stays bounded.</summary>
        public static float[] CollisionTriangles(GameEntity e, out float cellSize)
        {
            cellSize = 0f;
            TerrainData data;
            if (!TryGetData(e, out data, out cellSize)) return null;
            int stride = 1;
            while ((data.Resolution - 1) / stride > 256) stride *= 2;
            return TerrainMeshBuilder.Triangles(data, cellSize, stride);
        }

        /// <summary>The heights (metres, row z / column x) and spacing for the Jolt height field.</summary>
        public static bool HeightField(GameEntity e, out float[] heights, out int sampleCount, out float cellSize)
        {
            heights = null; sampleCount = 0;
            TerrainData data;
            if (!TryGetData(e, out data, out cellSize)) return false;
            heights = data.Heights; sampleCount = data.Resolution;
            return true;
        }

        // ---------------------------------------------------------------- lifetime

        /// <summary>The eye the LODs and cull distances are measured from: the main camera while playing, else the editor camera.</summary>
        public static Vector3 CameraPosition(Scene scene, bool playLike)
        {
            if (playLike)
            {
                try
                {
                    var cam = PlayCameraHelper.FindMainCameraEntity(scene);
                    if (cam != null) return Animation.BoneSocketService.EntityWorld(cam).Translation;
                }
                catch { }
            }
            var c = EditorCameraController.Instance;
            return new Vector3(c.PositionX, c.PositionY, c.PositionZ);
        }

        private static void Release(Runtime rt)
        {
            if (rt == null) return;
            try
            {
                if (rt.Meshes != null) foreach (var m in rt.Meshes) if (m != ID.INVALID_ID) _graveyard.Add((m, false, _frame));
                foreach (var tx in new[] { rt.AlbedoAtlas, rt.NormalAtlas, rt.RoughnessAtlas, rt.SplatTexture })
                    if (tx != ID.INVALID_ID) _graveyard.Add((tx, true, _frame));
                if (rt.Material != ID.INVALID_ID) VortexAPI.DeleteMaterial(rt.Material);
            }
            catch { }
            rt.Meshes = null; rt.Material = ID.INVALID_ID;
        }

        /// <summary>Drop every terrain runtime (scene switch). GPU resources retire through the graveyard.</summary>
        public static void Clear()
        {
            foreach (var kv in _runtimes) Release(kv.Value);
            _runtimes.Clear();
        }
    }
}
