using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Data;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using Editor.Utilities;
using Vector3 = System.Numerics.Vector3;

namespace Editor.Core.Services.Decals
{
    /// <summary>
    /// The decal runtime (#120): on every scene submit it hands the renderer the scene's enabled <see cref="Decal"/>
    /// components (their box = the entity transform × Size, projecting along the entity's local +Y) and the decals scripts
    /// spawned (<c>Decal.Spawn</c>: a box built from a hit point and normal, optional lifetime with a fade-out, capped).
    /// Materials are ordinary .vmat assets resolved through the MaterialService.
    /// </summary>
    public static class DecalService
    {
        /// <summary>Spawned decals kept at most; the oldest goes when a new one arrives.</summary>
        public static int MaxSpawned = 512;

        private sealed class Spawned
        {
            public long Id;
            public float[] World;
            public long Material;
            public float R, G, B, A;
            public float AngleFade, FadeDistance;
            public int Blend, SortOrder;
            public float Lifetime, Age;
        }

        private static readonly List<Spawned> _spawned = new List<Spawned>();
        private static readonly Dictionary<string, long> _materials = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static long _nextId;

        /// <summary>Scene (component) decals handed to the renderer by the last submit.</summary>
        public static int SceneDecalCount { get; private set; }
        /// <summary>Spawned decals alive right now.</summary>
        public static int SpawnedCount => _spawned.Count;

        // ---------------------------------------------------------------------------------------------- submit
        /// <summary>Refill the renderer's decal list for this scene (called from SceneRenderService.SubmitScene, like the lights).</summary>
        public static void Submit(Scene scene, bool playLike)
        {
            VortexAPI.ClearAllDecals();
            int count = 0;
            if (scene?.Entities != null)
                foreach (var e in scene.Entities) Visit(e, playLike, ref count, 0);
            SceneDecalCount = count;
            for (int i = 0; i < _spawned.Count; i++)
            {
                var s = _spawned[i];
                VortexAPI.SubmitDecal(s.World, s.Material, s.R, s.G, s.B, s.A * Fade(s), s.AngleFade, s.FadeDistance, s.Blend, s.SortOrder);
            }
        }

        private static void Visit(GameEntity e, bool playLike, ref int count, int depth)
        {
            if (e == null || depth > 64 || !e.IsActive) return;
            if (e.IsHiddenInEditor && !playLike) return;
            var comps = e.Components;
            for (int i = 0; i < comps.Count; i++)
            {
                if (!(comps[i] is Decal d) || !d.IsEnabled) continue;
                VortexAPI.SubmitDecal(BoxWorld(e, d), ResolveMaterial(d.MaterialPath), d.ColorR, d.ColorG, d.ColorB, d.Opacity, d.AngleFade, d.FadeDistance, d.Blend, d.SortOrder);
                count++;
            }
            var children = e.Children;
            if (children != null) for (int i = 0; i < children.Count; i++) Visit(children[i], playLike, ref count, depth + 1);
        }

        /// <summary>The world matrix of a component's box: Size scales the unit box, then the entity's world transform.</summary>
        public static float[] BoxWorld(GameEntity e, Decal d)
        {
            var s = d.Size;
            float sx = s.X <= 0f ? 0.01f : s.X, sy = s.Y <= 0f ? 0.01f : s.Y, sz = s.Z <= 0f ? 0.01f : s.Z;
            var scale = new float[] { sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, sz, 0, 0, 0, 0, 1 };
            return TransformMath.Multiply(scale, TransformMath.World(e));
        }

        // ---------------------------------------------------------------------------------------------- materials
        /// <summary>Project-relative or rooted path → full path (null when empty).</summary>
        public static string ResolvePath(string materialPath)
        {
            if (string.IsNullOrWhiteSpace(materialPath)) return null;
            string p = materialPath.Replace('\\', '/');
            try
            {
                if (Path.IsPathRooted(p)) return Path.GetFullPath(p);
                string root = ProjectData.Current?.Path;
                return Path.GetFullPath(string.IsNullOrEmpty(root) ? p : Path.Combine(root, p));
            }
            catch { return null; }
        }

        /// <summary>The engine material of a .vmat (cached per path; cleared with the scene). Empty = <see cref="ID.INVALID_ID"/>:
        /// the decal projects its tint alone.</summary>
        public static long ResolveMaterial(string materialPath)
        {
            string full = ResolvePath(materialPath);
            if (full == null) return ID.INVALID_ID;
            if (_materials.TryGetValue(full, out long id)) return id;
            long mat = ID.INVALID_ID;
            try { mat = MaterialService.Instance.GetOrBuildVortexMaterial(full); } catch { mat = ID.INVALID_ID; }
            if (mat < 0) mat = ID.INVALID_ID;
            _materials[full] = mat;
            return mat;
        }

        public static void ClearMaterialCache() => _materials.Clear();

        // ---------------------------------------------------------------------------------------------- spawned
        /// <summary>The box of a decal stamped onto a surface: it projects along <paramref name="normal"/>, its texture's up
        /// follows the world up (or world Z on floors and ceilings), rotated by <paramref name="rotationDeg"/> around the normal;
        /// <paramref name="size"/> is (across, depth, across) in metres.</summary>
        public static float[] BuildWorld(Vector3 position, Vector3 normal, Vector3 size, float rotationDeg)
        {
            Vector3 y = normal.LengthSquared() > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitY;
            Vector3 hint = Math.Abs(y.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 z = hint - y * Vector3.Dot(hint, y);
            z = z.LengthSquared() > 1e-10f ? Vector3.Normalize(z) : Vector3.UnitZ;
            Vector3 x = Vector3.Cross(y, z);
            if (Math.Abs(rotationDeg) > 1e-4f)
            {
                double r = rotationDeg * Math.PI / 180.0;
                float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
                Vector3 x2 = x * c + z * s;
                Vector3 z2 = z * c - x * s;
                x = x2; z = z2;
            }
            float sx = size.X <= 0f ? 0.01f : size.X, sy = size.Y <= 0f ? 0.01f : size.Y, sz = size.Z <= 0f ? 0.01f : size.Z;
            return new float[]
            {
                x.X * sx, x.Y * sx, x.Z * sx, 0,
                y.X * sy, y.Y * sy, y.Z * sy, 0,
                z.X * sz, z.Y * sz, z.Z * sz, 0,
                position.X, position.Y, position.Z, 1,
            };
        }

        /// <summary>Stamp a decal (returns its id, 0 when nothing was spawned). lifetime 0 = until <see cref="Destroy"/> / <see cref="Clear"/>;
        /// otherwise it fades out over the last quarter of its life. Over <see cref="MaxSpawned"/> the oldest decal goes.</summary>
        public static long Spawn(string materialPath, Vector3 position, Vector3 normal, Vector3 size, float lifetime, float rotationDeg,
            float r = 1f, float g = 1f, float b = 1f, float a = 1f, int blend = 0, float angleFade = 0.5f, float fadeDistance = 0f, int sortOrder = 0)
        {
            var s = new Spawned
            {
                Id = ++_nextId,
                World = BuildWorld(position, normal, size, rotationDeg),
                Material = ResolveMaterial(materialPath),
                R = r, G = g, B = b, A = a < 0f ? 0f : (a > 1f ? 1f : a),
                AngleFade = angleFade, FadeDistance = fadeDistance,
                Blend = blend < 0 ? 0 : (blend > 2 ? 2 : blend), SortOrder = sortOrder,
                Lifetime = lifetime < 0f ? 0f : lifetime, Age = 0f,
            };
            while (_spawned.Count >= Math.Max(1, MaxSpawned)) _spawned.RemoveAt(0);
            _spawned.Add(s);
            SceneRenderService.RuntimeDirty = true;
            return s.Id;
        }

        public static bool IsAlive(long id) => id != 0 && Find(id) != null;

        public static bool Destroy(long id)
        {
            var s = Find(id);
            if (s == null) return false;
            _spawned.Remove(s);
            SceneRenderService.RuntimeDirty = true;
            return true;
        }

        public static void SetColor(long id, float r, float g, float b, float a)
        {
            var s = Find(id);
            if (s == null) return;
            s.R = r; s.G = g; s.B = b; s.A = a < 0f ? 0f : (a > 1f ? 1f : a);
            SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>Remove every spawned decal (play stop, scene change).</summary>
        public static void Clear()
        {
            if (_spawned.Count == 0) return;
            _spawned.Clear();
            SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>Age the spawned decals; expired ones go, fading ones ask for a resubmit.</summary>
        public static void Tick(float dt)
        {
            if (_spawned.Count == 0 || dt <= 0f) return;
            bool dirty = false;
            for (int i = _spawned.Count - 1; i >= 0; i--)
            {
                var s = _spawned[i];
                if (s.Lifetime <= 0f) continue;
                s.Age += dt;
                if (s.Age >= s.Lifetime) { _spawned.RemoveAt(i); dirty = true; }
                else if (s.Age >= s.Lifetime * 0.75f) dirty = true;
            }
            if (dirty) SceneRenderService.RuntimeDirty = true;
        }

        private static float Fade(Spawned s)
        {
            if (s.Lifetime <= 0f) return 1f;
            float t = s.Age / s.Lifetime;
            return t <= 0.75f ? 1f : Math.Max(0f, 1f - (t - 0.75f) / 0.25f);
        }

        private static Spawned Find(long id)
        {
            for (int i = 0; i < _spawned.Count; i++) if (_spawned[i].Id == id) return _spawned[i];
            return null;
        }
    }
}
