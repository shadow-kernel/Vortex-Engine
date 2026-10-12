using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Data;
using Editor.Core.Foliage;
using Editor.Core.Services.Terrain;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using Editor.Utilities;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace Editor.Core.Services.Foliage
{
    /// <summary>
    /// The runtime of every <see cref="Editor.ECS.Components.Rendering.Foliage"/> in the active scene (#125): loads the
    /// <c>.vfoliage</c> instances, loads each type's model (every submesh + material, the materials set up for cut-out
    /// leaves and the wind shader), and every frame submits the instances inside each type's cull distance through the
    /// renderer's instancing path — thinned with distance where the type asks for it. Painting (the editor brush, the
    /// MCP tools, <c>Foliage.Paint</c> in scripts) goes through <see cref="Paint"/> / <see cref="Erase"/>; the collision world,
    /// the physics and the navmesh bake read the colliding types through <see cref="Collidables"/>.
    /// </summary>
    public static class FoliageService
    {
        private const int FlushEvery = 1024;   // instances per submit call

        private sealed class TypeRuntime
        {
            public FoliageType Type;
            public string Key;
            public long[] Meshes = Array.Empty<long>();
            public long[] Materials = Array.Empty<long>();
            public bool Loaded;
        }

        private sealed class Runtime
        {
            public GameEntity Entity;
            public Editor.ECS.Components.Rendering.Foliage Component;
            public FoliageData Data;
            public readonly List<TypeRuntime> Types = new List<TypeRuntime>();
            public int ComponentVersion = -1;
            public int SeenFrame;
            public bool Unsaved;
        }

        private static readonly Dictionary<GameEntity, Runtime> _runtimes = new Dictionary<GameEntity, Runtime>();
        private static readonly HashSet<long> _preparedMaterials = new HashSet<long>();
        private static Scene _scene;
        private static int _frame;
        private static bool _hooked;
        private static float[] _matrices = new float[FlushEvery * 16];
        private static float[] _tints = new float[FlushEvery * 4];
        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>Instances drawn by the last <see cref="Submit"/> (before thinning: those inside the cull distance).</summary>
        public static int LastInstancesDrawn { get; private set; }
        /// <summary>Submit calls of the last frame.</summary>
        public static int LastDrawCalls { get; private set; }
        public static int Count => _runtimes.Count;
        /// <summary>Seconds since the editor started — the wind clock.</summary>
        public static float Time => (float)_clock.Elapsed.TotalSeconds;

        public static event Action<GameEntity> Changed;

        // ---------------------------------------------------------------- per frame

        public static void Submit(Scene scene, bool playLike)
        {
            _frame++;
            if (!ReferenceEquals(scene, _scene)) { Clear(); _scene = scene; Hook(); }
            LastInstancesDrawn = 0; LastDrawCalls = 0;
            if (scene?.Entities == null) return;
            Vector3 cam = TerrainService.CameraPosition(scene, playLike);
            float time = Time;
            foreach (var e in scene.Entities) SubmitRecursive(e, cam, time, playLike);

            List<GameEntity> gone = null;
            foreach (var kv in _runtimes)
                if (kv.Value.SeenFrame != _frame) (gone ?? (gone = new List<GameEntity>())).Add(kv.Key);
            if (gone != null) foreach (var g in gone) _runtimes.Remove(g);
        }

        private static void SubmitRecursive(GameEntity e, Vector3 cam, float time, bool playLike)
        {
            if (e == null || !e.IsActive) return;
            if (e.IsHiddenInEditor && !playLike) return;
            var f = e.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (f != null && f.IsEnabled)
            {
                try { SubmitOne(e, f, cam, time); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Foliage] submit failed for '" + e.Name + "': " + ex.Message); }
            }
            if (e.Children != null) foreach (var c in e.Children) SubmitRecursive(c, cam, time, playLike);
        }

        private static void SubmitOne(GameEntity e, Editor.ECS.Components.Rendering.Foliage f, Vector3 cam, float time)
        {
            var rt = Ensure(e, f);
            rt.SeenFrame = _frame;
            float viewScale = f.ViewDistanceScale;
            for (int ti = 0; ti < rt.Types.Count; ti++)
            {
                var tr = rt.Types[ti];
                var type = tr.Type;
                if (!tr.Loaded || tr.Meshes.Length == 0) continue;
                var layer = rt.Data.Layer(type.Name, false);
                if (layer == null || layer.Count == 0) continue;

                // the wind, packed into fields the foliage never uses for their original purpose (see foliage.hlsl)
                float sway = Math.Max(0f, type.WindStrength * f.Wind);
                float packedTime = MathF.Floor(Math.Min(sway, 15f) * 1000f) * 1000f + (time % 1000f);
                float packedHeight = MathF.Floor(Math.Max(0.1f, Math.Min(99f, type.WindSpeed)) * 10f) * 100f + Math.Max(0.05f, Math.Min(99f, type.WindHeight));
                foreach (var mat in tr.Materials)
                {
                    if (mat == ID.INVALID_ID) continue;
                    VortexAPI.SetMaterialHeightDepth(mat, packedTime);
                    VortexAPI.SetMaterialEmissiveBrightness(mat, packedHeight);
                }

                float cull = Math.Max(1f, type.CullDistance * viewScale);
                float cull2 = cull * cull;
                float thin = type.ThinDistance > 0f ? type.ThinDistance * viewScale : 0f;
                float thin2 = thin * thin, thin4 = thin2 * 4f;
                float cellReach = cull + FoliageLayer.CellSize * 0.71f;
                int n = 0;
                foreach (var cell in layer.Cells())
                {
                    float cx, cz;
                    FoliageLayer.CellCentre(cell.Key, out cx, out cz);
                    float ddx = cx - cam.X, ddz = cz - cam.Z;
                    if (ddx * ddx + ddz * ddz > cellReach * cellReach) continue;
                    var list = cell.Value;
                    for (int k = 0; k < list.Count; k++)
                    {
                        int i = list[k];
                        var inst = layer.Instances[i];
                        float dx = inst.Position.X - cam.X, dy = inst.Position.Y - cam.Y, dz = inst.Position.Z - cam.Z;
                        float d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 > cull2) continue;
                        if (thin > 0f)
                        {
                            if (d2 > thin4) { if ((i & 3) != 0) continue; }
                            else if (d2 > thin2) { if ((i & 1) != 0) continue; }
                        }
                        FoliageData.WorldMatrix(inst, _matrices, n * 16);
                        int t4 = n * 4;
                        _tints[t4] = 1f; _tints[t4 + 1] = 1f; _tints[t4 + 2] = 1f; _tints[t4 + 3] = 1f;
                        n++;
                        if (n == FlushEvery) { Flush(tr, n); n = 0; }
                    }
                }
                if (n > 0) Flush(tr, n);
            }
        }

        private static void Flush(TypeRuntime tr, int count)
        {
            for (int s = 0; s < tr.Meshes.Length; s++)
            {
                if (tr.Meshes[s] == ID.INVALID_ID) continue;
                VortexAPI.SubmitMeshInstancedTinted(tr.Meshes[s], tr.Materials[s], _matrices, _tints, count, 0);
                LastDrawCalls++;
            }
            LastInstancesDrawn += count;
        }

        // ---------------------------------------------------------------- runtimes, types, materials

        private static Runtime Ensure(GameEntity e, Editor.ECS.Components.Rendering.Foliage f)
        {
            Runtime rt;
            if (!_runtimes.TryGetValue(e, out rt))
            {
                rt = new Runtime { Entity = e, Component = f };
                rt.Data = LoadData(f) ?? new FoliageData();
                _runtimes[e] = rt;
            }
            rt.Component = f;
            if (rt.ComponentVersion != f.Version || rt.Types.Count != f.Types.Count) SyncTypes(rt, f);
            return rt;
        }

        private static string TypeKey(FoliageType t) => (t.MeshPath ?? "") + "|" + (t.Cutout ? 1 : 0) + "|" + (t.WindStrength > 0f ? 1 : 0);

        private static void SyncTypes(Runtime rt, Editor.ECS.Components.Rendering.Foliage f)
        {
            rt.ComponentVersion = f.Version;
            var next = new List<TypeRuntime>(f.Types.Count);
            for (int i = 0; i < f.Types.Count; i++)
            {
                var type = f.Types[i];
                string key = TypeKey(type);
                TypeRuntime tr = null;
                foreach (var old in rt.Types) if (ReferenceEquals(old.Type, type) || old.Key == key) { tr = old; break; }
                if (tr == null || tr.Key != key) tr = new TypeRuntime { Type = type, Key = key };
                tr.Type = type;
                if (!tr.Loaded) LoadType(tr);
                next.Add(tr);
            }
            rt.Types.Clear(); rt.Types.AddRange(next);
        }

        private static void LoadType(TypeRuntime tr)
        {
            var type = tr.Type;
            tr.Meshes = Array.Empty<long>(); tr.Materials = Array.Empty<long>();
            tr.Loaded = true;   // one attempt per key (a missing model is logged once)
            string path = type.MeshPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                {
                    long mesh = SceneRenderService.SharedPrimitiveMesh(path);
                    if (mesh == ID.INVALID_ID) return;
                    long mat = VortexAPI.CreateColorMaterial(0.33f, 0.52f, 0.22f, 0.85f);
                    tr.Meshes = new[] { mesh }; tr.Materials = new[] { mat };
                }
                else
                {
                    var subs = SceneRenderService.LoadModelSubmeshes(path);
                    if (subs == null || subs.Length == 0) { Log("Foliage type '" + type.Name + "': no meshes in '" + path + "'"); return; }
                    var meshes = new List<long>(); var mats = new List<long>();
                    foreach (var s in subs)
                    {
                        if (s.MeshId < 0) continue;
                        meshes.Add(s.MeshId); mats.Add(s.MaterialId);
                    }
                    tr.Meshes = meshes.ToArray(); tr.Materials = mats.ToArray();
                }
                foreach (var mat in tr.Materials) PrepareMaterial(mat, type);
            }
            catch (Exception ex) { Log("Foliage type '" + type.Name + "' failed to load '" + path + "': " + ex.Message); }
        }

        /// <summary>Cut-out leaves (alpha test, both sides) and the wind shader on a type's materials — once per material.</summary>
        private static void PrepareMaterial(long mat, FoliageType type)
        {
            if (mat == ID.INVALID_ID) return;
            if (type.Cutout)
            {
                VortexAPI.SetMaterialBlendModeValue(mat, 3);
                VortexAPI.SetMaterialAlphaCutoffValue(mat, 0.5f);
                VortexAPI.SetMaterialTwoSided(mat, true);
            }
            VortexAPI.SetMaterialTiling(mat, 1f, 1f);
            if (type.WindStrength > 0f && _preparedMaterials.Add(mat))
            {
                string shader = VortexAPI.FoliageShaderPath();
                if (!string.IsNullOrEmpty(shader)) VortexAPI.SetMaterialShader((int)mat, shader);
            }
        }

        private static void Log(string msg)
        {
            try { ConsoleService.Instance.LogWarning(msg); } catch { System.Diagnostics.Debug.WriteLine(msg); }
        }

        // ---------------------------------------------------------------- data, paths, saving

        private static FoliageData LoadData(Editor.ECS.Components.Rendering.Foliage f)
        {
            if (string.IsNullOrWhiteSpace(f.DataPath)) return null;
            try
            {
                string abs = TerrainService.ResolvePath(f.DataPath);
                if (abs != null && File.Exists(abs)) return FoliageData.Load(abs);
                byte[] bytes;
                if (AssetVfs.TryGetBytes(f.DataPath.Replace('\\', '/'), out bytes)) return FoliageData.FromBytes(bytes);
            }
            catch { }
            return null;
        }

        public static string DefaultDataPath(GameEntity e)
        {
            string name = e?.Name ?? "Foliage";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Replace(' ', '_');
            if (string.IsNullOrEmpty(name)) name = "Foliage";
            string rel = "Assets/Foliage/" + name + ".vfoliage";
            string abs = TerrainService.ResolvePath(rel);
            int k = 2;
            while (abs != null && File.Exists(abs) && !OwnsPath(e, rel))
            {
                rel = "Assets/Foliage/" + name + "_" + k++ + ".vfoliage";
                abs = TerrainService.ResolvePath(rel);
            }
            return rel;
        }

        private static bool OwnsPath(GameEntity e, string rel)
        {
            var f = e?.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            return f != null && string.Equals((f.DataPath ?? "").Replace('\\', '/'), rel, StringComparison.OrdinalIgnoreCase);
        }

        public static bool Save(GameEntity e)
        {
            Runtime rt;
            if (e == null || !_runtimes.TryGetValue(e, out rt)) return false;
            var f = rt.Component;
            if (string.IsNullOrWhiteSpace(f.DataPath)) f.DataPath = DefaultDataPath(e);
            string abs = TerrainService.ResolvePath(f.DataPath);
            if (abs == null) return false;
            try { rt.Data.Save(abs); rt.Unsaved = false; return true; }
            catch (Exception ex) { Log("Foliage save failed: " + ex.Message); return false; }
        }

        public static int SaveAll(Scene scene)
        {
            int n = 0;
            foreach (var kv in _runtimes)
                if ((kv.Value.Unsaved || (string.IsNullOrWhiteSpace(kv.Value.Component.DataPath) && kv.Value.Data.TotalCount > 0)) && Save(kv.Key)) n++;
            return n;
        }

        public static bool HasUnsavedChanges(GameEntity e) => e != null && _runtimes.TryGetValue(e, out var rt) && rt.Unsaved;

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            try { SceneService.Instance.SceneSaved += (s, scene) => { try { SaveAll(scene); } catch { } }; } catch { }
        }

        // ---------------------------------------------------------------- editing

        /// <summary>The foliage data of an entity (loaded on demand).</summary>
        public static bool TryGetData(GameEntity e, out FoliageData data)
        {
            data = null;
            var f = e?.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (f == null) return false;
            data = Ensure(e, f).Data;
            return true;
        }

        public static void MarkDirty(GameEntity e)
        {
            Runtime rt;
            if (e == null || !_runtimes.TryGetValue(e, out rt)) return;
            rt.Unsaved = true;
            Changed?.Invoke(e);
        }

        /// <summary>Instances of every type (or one) of the entity.</summary>
        public static int InstanceCount(GameEntity e, int typeIndex = -1)
        {
            FoliageData data;
            if (!TryGetData(e, out data)) return 0;
            var f = e.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (typeIndex < 0) return data.TotalCount;
            if (typeIndex >= f.Types.Count) return 0;
            var layer = data.Layer(f.Types[typeIndex].Name, false);
            return layer?.Count ?? 0;
        }

        /// <summary>The surface under a world XZ. From the sky (<paramref name="fromY"/> NaN): the terrains first, then the
        /// meshes (their real triangles). From a height (<paramref name="fromY"/> set): the first surface below it, terrain or
        /// mesh — a cave's floor under a hill, the ground under an overhang (a terrain whose surface lies above that height is
        /// the rock around the cave and does not count). False over nothing.</summary>
        public static bool TrySurface(Scene scene, float x, float z, out Vector3 position, out Vector3 normal, float fromY = float.NaN)
        {
            position = default(Vector3); normal = Vector3.UnitY;
            bool fromSky = float.IsNaN(fromY);
            float top = fromSky ? 2000f : fromY;
            var o = new Vector3(x, top, z); var down = new Vector3(0f, -1f, 0f);
            Vector3 th; GameEntity terrain;
            bool haveTerrain = TerrainService.Raycast(o, down, 4000f, out th, out terrain);
            if (haveTerrain && !fromSky && th.Y >= top - 0.01f) haveTerrain = false;   // under the terrain's surface: inside the rock
            if (fromSky && haveTerrain)
            {
                position = th;
                Vector3 n;
                if (TerrainService.TryNormal(terrain, x, z, out n)) normal = n;
                return true;
            }
            Vector3 mh = default(Vector3), mn = Vector3.UnitY; GameEntity me;
            bool haveMesh = scene != null && MeshRaycast(scene, o, down, 4000f, out mh, out mn, out me);
            if (haveMesh && (!haveTerrain || mh.Y >= th.Y)) { position = mh; normal = mn; return true; }
            if (haveTerrain)
            {
                position = th;
                Vector3 n;
                if (TerrainService.TryNormal(terrain, x, z, out n)) normal = n;
                return true;
            }
            return false;
        }

        // ---- exact rays against the scene's meshes (their collision triangles in world space, cached per entity) ----
        private sealed class MeshSoup { public string Path; public float[] Matrix; public float[] Tris; public Vector3 Min, Max; }
        private static readonly Dictionary<Guid, MeshSoup> _soups = new Dictionary<Guid, MeshSoup>();
        private static Scene _soupScene;

        /// <summary>A model changed on disk: rebuild the cached triangles.</summary>
        public static void ForgetMeshes() { _soups.Clear(); }

        /// <summary>The nearest hit of a ray on the scene's static meshes — their real triangles (the collision mesh the
        /// physics uses), not their boxes, so a brush or a painted plant lands on a cave's floor, a rock's top, under an arch.
        /// Foliage and terrains are left out. The normal faces the ray.</summary>
        public static bool MeshRaycast(Scene scene, Vector3 o, Vector3 d, float maxDist, out Vector3 hit, out Vector3 normal, out GameEntity hitEntity)
        {
            hit = default(Vector3); normal = Vector3.UnitY; hitEntity = null;
            if (scene?.Entities == null) return false;
            if (!ReferenceEquals(scene, _soupScene)) { _soups.Clear(); _soupScene = scene; }
            var provider = Physics.CollisionService.MeshTriangleProvider ?? Editor.Scripting.ScriptRuntime.MeshTriangles;
            float best = maxDist;
            var stack = new Stack<GameEntity>();
            foreach (var r in scene.Entities) stack.Push(r);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (e == null || !e.IsActive) continue;
                if (e.Children != null) foreach (var c in e.Children) stack.Push(c);
                if (e.GetComponent<Editor.ECS.Components.Rendering.Foliage>() != null || e.GetComponent<Editor.ECS.Components.Rendering.Terrain>() != null) continue;
                var mr = e.GetComponent<MeshRenderer>();
                if (mr == null || !mr.IsEnabled || string.IsNullOrEmpty(mr.MeshPath) || mr.MeshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) continue;
                float[] m;
                try { m = SceneRenderService.Instance.WorldMatrixOf(e); } catch { continue; }
                if (m == null || m.Length < 16) continue;
                MeshSoup soup;
                if (!_soups.TryGetValue(e.Id, out soup) || soup.Path != mr.MeshPath || !SameMatrix(soup.Matrix, m))
                {
                    float[] local = null;
                    try { local = provider(mr.MeshPath); } catch { }
                    if (local == null || local.Length < 9) { _soups.Remove(e.Id); continue; }
                    soup = new MeshSoup { Path = mr.MeshPath, Matrix = (float[])m.Clone(), Tris = new float[local.Length] };
                    var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                    for (int i = 0; i + 2 < local.Length; i += 3)
                    {
                        float x = local[i], y = local[i + 1], z = local[i + 2];
                        // rows = the axis images, the last row the translation (WorldMatrixOf's layout)
                        var w = new Vector3(x * m[0] + y * m[4] + z * m[8] + m[12], x * m[1] + y * m[5] + z * m[9] + m[13], x * m[2] + y * m[6] + z * m[10] + m[14]);
                        soup.Tris[i] = w.X; soup.Tris[i + 1] = w.Y; soup.Tris[i + 2] = w.Z;
                        mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
                    }
                    soup.Min = mn; soup.Max = mx;
                    _soups[e.Id] = soup;
                }
                if (!RayBox(o, d, soup.Min, soup.Max, best)) continue;
                var t = soup.Tris;
                for (int i = 0; i + 8 < t.Length; i += 9)
                {
                    var a = new Vector3(t[i], t[i + 1], t[i + 2]);
                    var e1 = new Vector3(t[i + 3], t[i + 4], t[i + 5]) - a;
                    var e2 = new Vector3(t[i + 6], t[i + 7], t[i + 8]) - a;
                    var pv = Vector3.Cross(d, e2);
                    float det = Vector3.Dot(e1, pv);
                    if (Math.Abs(det) < 1e-9f) continue;
                    float inv = 1f / det;
                    var tv = o - a;
                    float u = Vector3.Dot(tv, pv) * inv;
                    if (u < 0f || u > 1f) continue;
                    var qv = Vector3.Cross(tv, e1);
                    float v = Vector3.Dot(d, qv) * inv;
                    if (v < 0f || u + v > 1f) continue;
                    float dist = Vector3.Dot(e2, qv) * inv;
                    if (dist <= 1e-4f || dist >= best) continue;
                    best = dist; hitEntity = e; hit = o + d * dist;
                    var n = Vector3.Cross(e1, e2);
                    float nl = n.Length();
                    n = nl > 1e-9f ? n / nl : Vector3.UnitY;
                    normal = Vector3.Dot(n, d) > 0f ? -n : n;
                }
            }
            return hitEntity != null;
        }

        private static bool SameMatrix(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 1e-5f) return false;
            return true;
        }

        private static bool RayBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx, float maxDist)
        {
            float t0 = 0f, t1 = maxDist;
            for (int k = 0; k < 3; k++)
            {
                float oo = k == 0 ? o.X : (k == 1 ? o.Y : o.Z), dd = k == 0 ? d.X : (k == 1 ? d.Y : d.Z);
                float lo = k == 0 ? mn.X : (k == 1 ? mn.Y : mn.Z), hi = k == 0 ? mx.X : (k == 1 ? mx.Y : mx.Z);
                if (Math.Abs(dd) < 1e-9f) { if (oo < lo || oo > hi) return false; continue; }
                float a = (lo - oo) / dd, b = (hi - oo) / dd;
                if (a > b) { var tmp = a; a = b; b = tmp; }
                if (a > t0) t0 = a; if (b < t1) t1 = b;
                if (t0 > t1) return false;
            }
            return true;
        }

        /// <summary>
        /// Paint instances of one type inside a disc: <paramref name="strength"/> × the type's density × the disc area
        /// candidates, each kept when the surface is flat enough and no instance of the type stands within the minimum
        /// spacing. Deterministic per <paramref name="seed"/>. Returns the number placed.
        /// </summary>
        /// <param name="fromY">NaN: the surfaces seen from the sky. A height: the surfaces below it — paint a cave's floor
        /// under a hill, the ground under an overhang.</param>
        public static int Paint(GameEntity e, int typeIndex, Vector3 centre, float radius, float strength, int seed, float fromY = float.NaN)
        {
            var f = e?.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (f == null || typeIndex < 0 || typeIndex >= f.Types.Count || radius <= 0f) return 0;
            var rt = Ensure(e, f);
            var type = f.Types[typeIndex];
            var layer = rt.Data.Layer(type.Name, true);
            var rng = new Random(seed);
            float area = (float)Math.PI * radius * radius;
            int candidates = Math.Max(1, (int)Math.Round(area * Math.Max(0f, type.Density) * Math.Max(0f, strength)));
            if (candidates > 20000) candidates = 20000;
            float slopeCos = (float)Math.Cos(Math.Max(0f, Math.Min(89f, type.MaxSlope)) * Math.PI / 180.0);
            var scene = ProjectData.Current?.ActiveScene;
            int placed = 0;
            for (int c = 0; c < candidates; c++)
            {
                double a = rng.NextDouble() * Math.PI * 2.0, r = Math.Sqrt(rng.NextDouble()) * radius;
                float x = centre.X + (float)(Math.Cos(a) * r), z = centre.Z + (float)(Math.Sin(a) * r);
                if (type.MinSpacing > 0f && layer.AnyWithin(new Vector3(x, 0f, z), type.MinSpacing)) continue;
                Vector3 pos, normal;
                if (!TrySurface(scene, x, z, out pos, out normal, fromY)) continue;
                if (normal.Y < slopeCos) continue;
                float scale = type.MinScale + (float)rng.NextDouble() * Math.Max(0f, type.MaxScale - type.MinScale);
                float yaw = (float)(rng.NextDouble() * 360.0);
                float tilt = (float)(rng.NextDouble() * Math.Max(0f, type.MaxTilt)), tiltDir = (float)(rng.NextDouble() * 360.0);
                var rot = FoliageData.Orientation(normal, type.AlignToNormal, yaw, tilt, tiltDir);
                pos.Y -= type.Sink * scale;
                layer.Add(new FoliageInstance(pos, rot, scale));
                placed++;
            }
            if (placed > 0) { rt.Unsaved = true; Changed?.Invoke(e); }
            return placed;
        }

        /// <summary>Remove the instances inside a disc (one type, or every type with <paramref name="typeIndex"/> -1). Returns the count.</summary>
        public static int Erase(GameEntity e, int typeIndex, Vector3 centre, float radius)
        {
            var f = e?.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (f == null || radius <= 0f) return 0;
            var rt = Ensure(e, f);
            int removed = 0;
            for (int i = 0; i < f.Types.Count; i++)
            {
                if (typeIndex >= 0 && i != typeIndex) continue;
                var layer = rt.Data.Layer(f.Types[i].Name, false);
                if (layer == null || layer.Count == 0) continue;
                var hits = layer.Within(centre, radius);
                if (hits.Count == 0) continue;
                layer.RemoveAt(hits);
                removed += hits.Count;
            }
            if (removed > 0) { rt.Unsaved = true; Changed?.Invoke(e); }
            return removed;
        }

        /// <summary>Drop every instance of a type (or all with -1).</summary>
        /// <summary>Every placed instance of every foliage entity the service has seen (world position, scale, its type) —
        /// the radiance cache voxelizes them as a trunk and a crown.</summary>
        public static void ForEachInstance(Scene scene, Action<System.Numerics.Vector3, float, FoliageType> visit)
        {
            foreach (var kv in _runtimes)
            {
                var rt = kv.Value;
                if (rt?.Data == null || rt.Component == null) continue;
                foreach (var type in rt.Component.Types)
                {
                    if (type == null) continue;
                    var layer = rt.Data.Layer(type.Name, false);
                    if (layer == null) continue;
                    foreach (var inst in layer.Instances) visit(inst.Position, inst.Scale, type);
                }
            }
        }

        public static int ClearInstances(GameEntity e, int typeIndex)
        {
            var f = e?.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (f == null) return 0;
            var rt = Ensure(e, f);
            int removed = 0;
            for (int i = 0; i < f.Types.Count; i++)
            {
                if (typeIndex >= 0 && i != typeIndex) continue;
                var layer = rt.Data.Layer(f.Types[i].Name, false);
                if (layer == null) continue;
                removed += layer.Count;
                layer.Clear();
            }
            if (removed > 0) { rt.Unsaved = true; Changed?.Invoke(e); }
            return removed;
        }

        // ---------------------------------------------------------------- collision feeds

        /// <summary>A colliding instance as a vertical capsule: base point (world), radius, height.</summary>
        public struct Collidable
        {
            public Vector3 Base;
            public float Radius, Height;
        }

        /// <summary>Every instance of the entity's colliding types (capsules for trunks; a box type yields a capsule of its
        /// bounds' width too). Scaled by the instance scale.</summary>
        public static List<Collidable> Collidables(GameEntity e)
        {
            var result = new List<Collidable>();
            var f = e?.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (f == null) return result;
            var rt = Ensure(e, f);
            for (int i = 0; i < f.Types.Count; i++)
            {
                var type = f.Types[i];
                if (type.Collision == 0 || type.CollisionRadius <= 0f || type.CollisionHeight <= 0f) continue;
                var layer = rt.Data.Layer(type.Name, false);
                if (layer == null) continue;
                foreach (var inst in layer.Instances)
                    result.Add(new Collidable { Base = inst.Position, Radius = type.CollisionRadius * inst.Scale, Height = type.CollisionHeight * inst.Scale });
            }
            return result;
        }

        public static void Clear()
        {
            _runtimes.Clear();
        }
    }
}
