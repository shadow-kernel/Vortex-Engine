using System;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// A rendering stress-test / benchmark harness (a dev tool, not gameplay): submits a chosen model MANY times
    /// in a grid via the batched instanced-submission path, so the GPU instancing can be pushed to its limit.
    /// The viewport's status bar (FPS / Draw Calls / Vertices) then shows how performance scales. Each unique
    /// (mesh, material) of the model becomes ONE DrawIndexedInstanced over all copies.
    /// </summary>
    public static class StressTestService
    {
        public static bool Active { get; private set; }
        public static int Count { get; private set; }
        public static string ModelName { get; private set; }

        private static long[] _meshIds;
        private static long[] _matIds;
        private static float[] _transforms;   // Count * 16 floats (row-major 4x4), shared by every submesh
        private static volatile bool _dirty;

        /// <summary>Begin stress-testing: render <paramref name="count"/> copies of the model in a grid.</summary>
        public static void Start(string fullModelPath, int count)
        {
            try
            {
                if (string.IsNullOrEmpty(fullModelPath) || !System.IO.File.Exists(fullModelPath) || count <= 0)
                    return;

                var subs = VortexAPI.ImportModelWithMaterialsFromFile(fullModelPath);
                if (subs == null || subs.Length == 0) return;

                _meshIds = new long[subs.Length];
                _matIds = new long[subs.Length];
                for (int i = 0; i < subs.Length; i++) { _meshIds[i] = subs[i].MeshId; _matIds[i] = subs[i].MaterialId; }

                ModelName = System.IO.Path.GetFileNameWithoutExtension(fullModelPath);
                Count = count;
                BuildGrid(count);
                Active = true;
                _dirty = true;
                System.Diagnostics.Debug.WriteLine($"[StressTest] {ModelName} x{count} ({subs.Length} submeshes -> {subs.Length} instanced draws of {count})");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StressTest] {ex.Message}"); }
        }

        public static void Stop()
        {
            Active = false; _dirty = false; _meshIds = null; _matIds = null; _transforms = null; Count = 0;
            try { Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit(); } catch { }
        }

        /// <summary>True if the grid needs (re)submitting this frame (set on Start / count change).</summary>
        public static bool Dirty => Active && _dirty;
        public static void ClearDirty() => _dirty = false;

        /// <summary>Append the stress instances to the current render submission (call right after SubmitScene).</summary>
        public static void Submit()
        {
            if (!Active || _meshIds == null || _transforms == null) return;
            for (int i = 0; i < _meshIds.Length; i++)
                VortexAPI.SubmitMeshInstanced(_meshIds[i], _matIds[i], _transforms, Count);
        }

        // Lay the copies out on a square XZ grid centered on the origin.
        private static void BuildGrid(int n)
        {
            int side = (int)Math.Ceiling(Math.Sqrt(n));
            if (side < 1) side = 1;
            const float spacing = 3.5f;
            float half = side * spacing * 0.5f;
            _transforms = new float[n * 16];
            for (int k = 0; k < n; k++)
            {
                int gx = k % side, gz = k / side;
                float x = gx * spacing - half;
                float z = gz * spacing - half;
                int b = k * 16;
                // Row-major identity with translation in the last row (_41,_42,_43) — matches the engine's
                // world_matrix layout (mul(float4(pos,1), World)).
                _transforms[b + 0] = 1f; _transforms[b + 5] = 1f; _transforms[b + 10] = 1f; _transforms[b + 15] = 1f;
                _transforms[b + 12] = x; _transforms[b + 13] = 0f; _transforms[b + 14] = z;
            }
        }
    }
}
