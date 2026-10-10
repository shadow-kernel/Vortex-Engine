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

        /// <summary>The surface under a world XZ: the terrains first, then the tops of mesh bounding boxes (platforms, roofs,
        /// a floor). False over nothing.</summary>
        public static bool TrySurface(Scene scene, float x, float z, out Vector3 position, out Vector3 normal)
        {
            position = default(Vector3); normal = Vector3.UnitY;
            Vector3 hit; GameEntity terrain;
            if (TerrainService.Raycast(new Vector3(x, 2000f, z), new Vector3(0f, -1f, 0f), 4000f, out hit, out terrain))
            {
                position = hit;
                Vector3 n;
                if (TerrainService.TryNormal(terrain, x, z, out n)) normal = n;
                return true;
            }
            if (scene != null)
            {
                try
                {
                    var ray = new Ray { Origin = new Vector3f(x, 2000f, z), Direction = new Vector3f(0f, -1f, 0f) };
                    var hits = RaycastService.Instance.RaycastAll(ray, scene);
                    if (hits != null && hits.Count > 0)
                    {
                        // the highest box top wins
                        float best = float.MinValue; bool any = false;
                        foreach (var h in hits)
                        {
                            if (h.Entity != null && (h.Entity.GetComponent<Editor.ECS.Components.Rendering.Foliage>() != null
                                                     || h.Entity.GetComponent<Editor.ECS.Components.Rendering.Terrain>() != null)) continue;
                            if (h.Point.Y > best) { best = h.Point.Y; any = true; }
                        }
                        if (any) { position = new Vector3(x, best, z); return true; }
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// Paint instances of one type inside a disc: <paramref name="strength"/> × the type's density × the disc area
        /// candidates, each kept when the surface is flat enough and no instance of the type stands within the minimum
        /// spacing. Deterministic per <paramref name="seed"/>. Returns the number placed.
        /// </summary>
        public static int Paint(GameEntity e, int typeIndex, Vector3 centre, float radius, float strength, int seed)
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
                if (!TrySurface(scene, x, z, out pos, out normal)) continue;
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
