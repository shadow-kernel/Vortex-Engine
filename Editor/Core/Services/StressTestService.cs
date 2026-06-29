using System;
using System.Collections.Generic;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Rendering stress-test / benchmark harness (a dev tool, not gameplay). Submits one or MANY models many times
    /// via the batched instanced path so instancing + geometric LOD + frustum culling can all be exercised. Two
    /// modes: Start (one model in a grid) and StartBenchmark (several different models spread near→far with varied
    /// scale/rotation — a realistic "generated scene" that tests every optimization at once).
    /// </summary>
    public static class StressTestService
    {
        public static bool Active { get; private set; }
        public static int Count { get; private set; }       // total copies (for the HUD)
        public static string ModelName { get; private set; }

        private sealed class Entry { public long[] Mesh; public long[] Mat; public float[] Transforms; public int Count; }
        private static readonly List<Entry> _entries = new List<Entry>();
        private static volatile bool _dirty;

        /// <summary>Single-model grid stress test.</summary>
        public static void Start(string fullModelPath, int count)
        {
            try
            {
                if (string.IsNullOrEmpty(fullModelPath) || !System.IO.File.Exists(fullModelPath) || count <= 0) return;
                Stop();
                var e = BuildEntry(fullModelPath, count, gridLayout: true, seed: 1);
                if (e == null) return;
                _entries.Add(e);
                ModelName = System.IO.Path.GetFileNameWithoutExtension(fullModelPath);
                Count = count;
                Active = true; _dirty = true;
                try
                {
                    int side = (int)Math.Ceiling(Math.Sqrt(count));
                    float extent = side * 3.5f;
                    EditorCameraController.Instance.FocusOn(0f, extent * 0.25f, 0f, Math.Max(10f, extent * 0.9f));
                }
                catch { }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StressTest] {ex.Message}"); }
        }

        /// <summary>Generated benchmark scene: each model spawned <paramref name="perModel"/> times, spread from very
        /// near to very far with varied scale + rotation — exercises instancing, LOD and culling together.</summary>
        public static void StartBenchmark(IEnumerable<string> modelPaths, int perModel)
        {
            try
            {
                Stop();
                int total = 0, seed = 7;
                foreach (var p in modelPaths)
                {
                    if (string.IsNullOrEmpty(p) || !System.IO.File.Exists(p)) continue;
                    var e = BuildEntry(p, perModel, gridLayout: false, seed: seed++);
                    if (e != null) { _entries.Add(e); total += perModel; }
                }
                if (_entries.Count == 0) return;
                ModelName = "Benchmark (" + _entries.Count + " models)";
                Count = total;
                Active = true; _dirty = true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StressTest] benchmark: {ex.Message}"); }
        }

        public static void Stop()
        {
            Active = false; _dirty = false; _entries.Clear(); Count = 0;
            try { Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit(); } catch { }
        }

        public static bool Dirty => Active && _dirty;
        public static void ClearDirty() => _dirty = false;

        /// <summary>Append every model's instances to the current render submission (one instanced call per submesh).</summary>
        public static void Submit()
        {
            if (!Active) return;
            foreach (var e in _entries)
            {
                if (e.Mesh == null || e.Transforms == null) continue;
                for (int i = 0; i < e.Mesh.Length; i++)
                    VortexAPI.SubmitMeshInstanced(e.Mesh[i], e.Mat[i], e.Transforms, e.Count);
            }
        }

        private static Entry BuildEntry(string fullPath, int count, bool gridLayout, int seed)
        {
            var subs = VortexAPI.ImportModelWithMaterialsFromFile(fullPath);
            if (subs == null || subs.Length == 0) return null;
            var e = new Entry { Mesh = new long[subs.Length], Mat = new long[subs.Length], Count = count };
            for (int i = 0; i < subs.Length; i++) { e.Mesh[i] = subs[i].MeshId; e.Mat[i] = subs[i].MaterialId; }
            e.Transforms = gridLayout ? BuildGrid(count) : BuildVaried(count, seed);
            return e;
        }

        // Square XZ grid centered on the origin (single-model test).
        private static float[] BuildGrid(int n)
        {
            int side = (int)Math.Ceiling(Math.Sqrt(n));
            if (side < 1) side = 1;
            const float spacing = 3.5f;
            float half = side * spacing * 0.5f;
            var t = new float[n * 16];
            for (int k = 0; k < n; k++)
            {
                int gx = k % side, gz = k / side;
                int b = k * 16;
                t[b + 0] = 1f; t[b + 5] = 1f; t[b + 10] = 1f; t[b + 15] = 1f;
                t[b + 12] = gx * spacing - half; t[b + 13] = 0f; t[b + 14] = gz * spacing - half;
            }
            return t;
        }

        // Varied distribution for the benchmark: copies spread on an XZ disk from very near (~4u) to very far
        // (~600u), area-uniform density, with random Y-rotation + scale. Deterministic (seeded) so it's stable.
        private static float[] BuildVaried(int n, int seed)
        {
            var rng = new Random(seed);
            var t = new float[n * 16];
            const float maxDist = 600f;
            for (int k = 0; k < n; k++)
            {
                float d = 4f + (maxDist - 4f) * (float)Math.Sqrt(rng.NextDouble()); // area-uniform
                float ang = (float)(rng.NextDouble() * Math.PI * 2.0);
                float x = d * (float)Math.Cos(ang);
                float z = d * (float)Math.Sin(ang);
                float s = 0.5f + (float)rng.NextDouble() * 2.5f;
                float ry = (float)(rng.NextDouble() * Math.PI * 2.0);
                float cs = (float)Math.Cos(ry), sn = (float)Math.Sin(ry);
                int b = k * 16;
                // Row-major: scale * rotateY * translate.
                t[b + 0] = s * cs;  t[b + 1] = 0f; t[b + 2] = -s * sn; t[b + 3] = 0f;
                t[b + 4] = 0f;      t[b + 5] = s;  t[b + 6] = 0f;      t[b + 7] = 0f;
                t[b + 8] = s * sn;  t[b + 9] = 0f; t[b + 10] = s * cs; t[b + 11] = 0f;
                t[b + 12] = x;      t[b + 13] = 0f; t[b + 14] = z;     t[b + 15] = 1f;
            }
            return t;
        }
    }
}
