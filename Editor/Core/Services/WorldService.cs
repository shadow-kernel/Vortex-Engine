using System;
using System.Collections.Generic;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Script-driven world geometry: a game script (e.g. a level/lobby builder) imports a model once and places
    /// instances of it via <c>Vortex.World.Add(...)</c>. These persist (re-submitted alongside the scene whenever
    /// either changes, then kept by submit-once), so a script can ASSEMBLE an environment — a motel, a stage —
    /// from meshes without authoring the binary .vscene. Render-only (no collision yet); great for backdrops +
    /// greybox levels. Mirrors StressTestService's batched instanced-submission path.
    /// </summary>
    public static class WorldService
    {
        private sealed class Item { public long[] Mesh; public long[] Mat; public float[] Transform; }
        private static readonly List<Item> _items = new List<Item>();
        private static readonly Dictionary<string, (long[] mesh, long[] mat)> _cache = new Dictionary<string, (long[], long[])>();
        private static volatile bool _dirty;

        public static bool HasItems => _items.Count > 0;
        public static bool Dirty => _dirty;
        public static void ClearDirty() => _dirty = false;

        /// <summary>Place a model instance at (pos) with a Y-rotation (degrees) + uniform scale. Imports + caches
        /// the model on first use. Returns false if the model could not be loaded.</summary>
        public static bool Add(string fullModelPath, float px, float py, float pz, float rotYDeg, float scale)
        {
            try
            {
                if (string.IsNullOrEmpty(fullModelPath)) return false;
                if (!_cache.TryGetValue(fullModelPath, out var ids))
                {
                    if (!System.IO.File.Exists(fullModelPath)) return false;
                    var subs = VortexAPI.ImportModelWithMaterialsFromFile(fullModelPath);
                    if (subs == null || subs.Length == 0) return false;
                    var m = new long[subs.Length]; var mt = new long[subs.Length];
                    for (int i = 0; i < subs.Length; i++) { m[i] = subs[i].MeshId; mt[i] = subs[i].MaterialId; }
                    ids = (m, mt); _cache[fullModelPath] = ids;
                }
                _items.Add(new Item { Mesh = ids.mesh, Mat = ids.mat, Transform = BuildTransform(px, py, pz, rotYDeg, scale) });
                _dirty = true;
                return true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[World] Add failed: " + ex.Message); return false; }
        }

        public static void Clear() { _items.Clear(); _dirty = true; }

        /// <summary>Append every placed instance to the current render submission (call right after SubmitScene).</summary>
        public static void Submit()
        {
            foreach (var it in _items)
            {
                if (it.Mesh == null) continue;
                for (int i = 0; i < it.Mesh.Length; i++)
                    VortexAPI.SubmitMeshInstanced(it.Mesh[i], it.Mat[i], it.Transform, 1);
            }
        }

        // Row-major scale * rotateY * translate (matches the engine's world_matrix layout: mul(float4(pos,1), W)).
        private static float[] BuildTransform(float x, float y, float z, float rotYDeg, float s)
        {
            double r = rotYDeg * Math.PI / 180.0;
            float cs = (float)Math.Cos(r), sn = (float)Math.Sin(r);
            return new float[]
            {
                s * cs, 0f, -s * sn, 0f,
                0f,     s,  0f,      0f,
                s * sn, 0f,  s * cs, 0f,
                x,      y,   z,      1f
            };
        }
    }
}
