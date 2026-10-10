using System;
using System.IO;
using System.Numerics;

namespace Editor.Core.Terrain
{
    /// <summary>An inclusive sample-index rectangle of a terrain (the area a brush stroke touched).</summary>
    public struct SampleRect
    {
        public int X0, Z0, X1, Z1;
        public static readonly SampleRect Empty = new SampleRect { X0 = int.MaxValue, Z0 = int.MaxValue, X1 = int.MinValue, Z1 = int.MinValue };
        public bool IsEmpty => X1 < X0 || Z1 < Z0;
        public int Width => IsEmpty ? 0 : X1 - X0 + 1;
        public int Height => IsEmpty ? 0 : Z1 - Z0 + 1;
        public SampleRect Union(SampleRect o)
        {
            if (o.IsEmpty) return this;
            if (IsEmpty) return o;
            return new SampleRect { X0 = Math.Min(X0, o.X0), Z0 = Math.Min(Z0, o.Z0), X1 = Math.Max(X1, o.X1), Z1 = Math.Max(Z1, o.Z1) };
        }
        public override string ToString() => IsEmpty ? "empty" : "[" + X0 + "," + Z0 + " .. " + X1 + "," + Z1 + "]";
    }

    /// <summary>
    /// The data of one heightfield terrain (#124): <see cref="Resolution"/>² height samples in metres (row z, column x —
    /// sample (x, z) sits at local (x · cellSize, h, z · cellSize), the terrain's corner at the local origin) and the splat
    /// map, RGBA weights of the four texture layers per sample. Saved as the <c>.vterrain</c> asset ("VTER", version 1).
    /// Brushes work in sample space and return the rectangle they changed so the chunk meshes, the collision and the undo
    /// snapshot only touch what moved.
    /// </summary>
    public sealed class TerrainData
    {
        public const int MinResolution = 33;
        public const int MaxResolution = 2049;
        private const uint Magic = 0x52455456;   // "VTER" little-endian
        private const int Version = 1;

        public int Resolution { get; private set; }
        /// <summary>Heights in metres, Resolution² values, index z * Resolution + x.</summary>
        public float[] Heights { get; private set; }
        /// <summary>Splat weights, 4 bytes per sample (layers 0..3), index (z * Resolution + x) * 4. Layer 0 full by default.</summary>
        public byte[] Splat { get; private set; }

        private float _minH, _maxH;
        private bool _rangeDirty = true;

        public TerrainData(int resolution)
        {
            if (resolution < MinResolution || resolution > MaxResolution) throw new ArgumentOutOfRangeException(nameof(resolution));
            Resolution = resolution;
            Heights = new float[resolution * resolution];
            Splat = new byte[resolution * resolution * 4];
            for (int i = 0; i < resolution * resolution; i++) Splat[i * 4] = 255;
        }

        public float MinHeight { get { UpdateRange(); return _minH; } }
        public float MaxHeight { get { UpdateRange(); return _maxH; } }

        private void UpdateRange()
        {
            if (!_rangeDirty) return;
            float mn = float.MaxValue, mx = float.MinValue;
            var h = Heights;
            for (int i = 0; i < h.Length; i++) { if (h[i] < mn) mn = h[i]; if (h[i] > mx) mx = h[i]; }
            _minH = mn; _maxH = mx; _rangeDirty = false;
        }

        public void MarkChanged() => _rangeDirty = true;

        public int ClampIndex(int v) => v < 0 ? 0 : (v >= Resolution ? Resolution - 1 : v);

        public float Get(int x, int z) => Heights[ClampIndex(z) * Resolution + ClampIndex(x)];

        public void Set(int x, int z, float h)
        {
            if (x < 0 || z < 0 || x >= Resolution || z >= Resolution) return;
            Heights[z * Resolution + x] = h;
            _rangeDirty = true;
        }

        /// <summary>Bilinear height at a sample-space position (sx, sz in 0 .. Resolution - 1; clamped outside).</summary>
        public float SampleHeight(float sx, float sz)
        {
            int n = Resolution;
            if (sx < 0f) sx = 0f; else if (sx > n - 1) sx = n - 1;
            if (sz < 0f) sz = 0f; else if (sz > n - 1) sz = n - 1;
            int x0 = (int)sx, z0 = (int)sz;
            if (x0 >= n - 1) x0 = n - 2;
            if (z0 >= n - 1) z0 = n - 2;
            float fx = sx - x0, fz = sz - z0;
            float h00 = Heights[z0 * n + x0], h10 = Heights[z0 * n + x0 + 1];
            float h01 = Heights[(z0 + 1) * n + x0], h11 = Heights[(z0 + 1) * n + x0 + 1];
            return (h00 * (1f - fx) + h10 * fx) * (1f - fz) + (h01 * (1f - fx) + h11 * fx) * fz;
        }

        /// <summary>Surface normal at a sample (central differences, the full-resolution heights).</summary>
        public Vector3 Normal(int x, int z, float cellSize)
        {
            float l = Get(x - 1, z), r = Get(x + 1, z), d = Get(x, z - 1), u = Get(x, z + 1);
            var n = new Vector3((l - r) / (2f * cellSize), 1f, (d - u) / (2f * cellSize));
            return Vector3.Normalize(n);
        }

        /// <summary>Surface normal at a sample-space position (bilinear heights).</summary>
        public Vector3 SampleNormal(float sx, float sz, float cellSize)
        {
            float l = SampleHeight(sx - 1f, sz), r = SampleHeight(sx + 1f, sz), d = SampleHeight(sx, sz - 1f), u = SampleHeight(sx, sz + 1f);
            return Vector3.Normalize(new Vector3((l - r) / (2f * cellSize), 1f, (d - u) / (2f * cellSize)));
        }

        /// <summary>Normalised layer weights at a sample.</summary>
        public void Weights(int x, int z, out float w0, out float w1, out float w2, out float w3)
        {
            int i = (ClampIndex(z) * Resolution + ClampIndex(x)) * 4;
            float s = Splat[i] + Splat[i + 1] + Splat[i + 2] + Splat[i + 3];
            if (s <= 0f) { w0 = 1f; w1 = w2 = w3 = 0f; return; }
            w0 = Splat[i] / s; w1 = Splat[i + 1] / s; w2 = Splat[i + 2] / s; w3 = Splat[i + 3] / s;
        }

        /// <summary>The layer with the largest weight at a sample (0..3).</summary>
        public int DominantLayer(int x, int z)
        {
            int i = (ClampIndex(z) * Resolution + ClampIndex(x)) * 4;
            int best = 0;
            for (int k = 1; k < 4; k++) if (Splat[i + k] > Splat[i + best]) best = k;
            return best;
        }

        // ---------------------------------------------------------------- brushes (sample space)

        /// <summary>Brush falloff: 1 at the centre, 0 at the radius, a smooth S-curve in between (hardness 0..1 keeps a
        /// flat plateau of that fraction of the radius).</summary>
        public static float Falloff(float distance, float radius, float hardness)
        {
            if (radius <= 0f) return 0f;
            float flat = radius * Math.Max(0f, Math.Min(0.95f, hardness));
            if (distance <= flat) return 1f;
            float t = 1f - (distance - flat) / Math.Max(1e-4f, radius - flat);
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }

        private SampleRect BrushRect(float cx, float cz, float radius)
        {
            int x0 = ClampIndex((int)Math.Floor(cx - radius)), x1 = ClampIndex((int)Math.Ceiling(cx + radius));
            int z0 = ClampIndex((int)Math.Floor(cz - radius)), z1 = ClampIndex((int)Math.Ceiling(cz + radius));
            return new SampleRect { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1 };
        }

        /// <summary>Raise (amount &gt; 0) or lower (amount &lt; 0) the heights by up to <paramref name="amount"/> metres at
        /// the centre, fading to the radius (all in sample units except the amount).</summary>
        public SampleRect Raise(float cx, float cz, float radius, float amount, float hardness = 0f)
        {
            var r = BrushRect(cx, cz, radius);
            int n = Resolution;
            for (int z = r.Z0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++)
                {
                    float dx = x - cx, dz = z - cz;
                    float w = Falloff((float)Math.Sqrt(dx * dx + dz * dz), radius, hardness);
                    if (w <= 0f) continue;
                    Heights[z * n + x] += amount * w;
                }
            _rangeDirty = true;
            return r;
        }

        /// <summary>Pull the heights towards <paramref name="target"/> metres by <paramref name="strength"/> (0..1) at the centre.</summary>
        public SampleRect Flatten(float cx, float cz, float radius, float target, float strength, float hardness = 0f)
        {
            var r = BrushRect(cx, cz, radius);
            int n = Resolution;
            strength = Math.Max(0f, Math.Min(1f, strength));
            for (int z = r.Z0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++)
                {
                    float dx = x - cx, dz = z - cz;
                    float w = Falloff((float)Math.Sqrt(dx * dx + dz * dz), radius, hardness) * strength;
                    if (w <= 0f) continue;
                    int i = z * n + x;
                    Heights[i] += (target - Heights[i]) * w;
                }
            _rangeDirty = true;
            return r;
        }

        /// <summary>Blur the heights towards their 3×3 neighbourhood mean by <paramref name="strength"/> (0..1) at the centre.</summary>
        public SampleRect Smooth(float cx, float cz, float radius, float strength, float hardness = 0f)
        {
            var r = BrushRect(cx, cz, radius);
            int n = Resolution;
            strength = Math.Max(0f, Math.Min(1f, strength));
            var src = (float[])Heights.Clone();
            for (int z = r.Z0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++)
                {
                    float dx = x - cx, dz = z - cz;
                    float w = Falloff((float)Math.Sqrt(dx * dx + dz * dz), radius, hardness) * strength;
                    if (w <= 0f) continue;
                    float sum = 0f; int cnt = 0;
                    for (int oz = -1; oz <= 1; oz++)
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int xx = x + ox, zz = z + oz;
                            if (xx < 0 || zz < 0 || xx >= n || zz >= n) continue;
                            sum += src[zz * n + xx]; cnt++;
                        }
                    int i = z * n + x;
                    Heights[i] += (sum / cnt - Heights[i]) * w;
                }
            _rangeDirty = true;
            return r;
        }

        /// <summary>Paint layer <paramref name="layer"/> (0..3): move splat weight onto it by <paramref name="strength"/> (0..1) at the centre.</summary>
        public SampleRect Paint(float cx, float cz, float radius, int layer, float strength, float hardness = 0f)
        {
            var r = BrushRect(cx, cz, radius);
            int n = Resolution;
            layer = Math.Max(0, Math.Min(3, layer));
            strength = Math.Max(0f, Math.Min(1f, strength));
            for (int z = r.Z0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++)
                {
                    float dx = x - cx, dz = z - cz;
                    float w = Falloff((float)Math.Sqrt(dx * dx + dz * dz), radius, hardness) * strength;
                    if (w <= 0f) continue;
                    int i = (z * n + x) * 4;
                    float tot = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float target = k == layer ? 255f : 0f;
                        float v = Splat[i + k] + (target - Splat[i + k]) * w;
                        Splat[i + k] = (byte)Math.Max(0f, Math.Min(255f, v + 0.5f));
                        tot += Splat[i + k];
                    }
                    if (tot < 1f) Splat[i + layer] = 255;
                }
            return r;
        }

        // ---------------------------------------------------------------- undo snapshots

        public float[] CopyHeights(SampleRect r)
        {
            if (r.IsEmpty) return Array.Empty<float>();
            var o = new float[r.Width * r.Height];
            for (int z = r.Z0, k = 0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++, k++) o[k] = Heights[z * Resolution + x];
            return o;
        }

        public void RestoreHeights(SampleRect r, float[] data)
        {
            if (r.IsEmpty || data == null) return;
            for (int z = r.Z0, k = 0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++, k++) if (k < data.Length) Heights[z * Resolution + x] = data[k];
            _rangeDirty = true;
        }

        public byte[] CopySplat(SampleRect r)
        {
            if (r.IsEmpty) return Array.Empty<byte>();
            var o = new byte[r.Width * r.Height * 4];
            for (int z = r.Z0, k = 0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++, k += 4) Buffer.BlockCopy(Splat, (z * Resolution + x) * 4, o, k, 4);
            return o;
        }

        public void RestoreSplat(SampleRect r, byte[] data)
        {
            if (r.IsEmpty || data == null) return;
            for (int z = r.Z0, k = 0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++, k += 4) if (k + 4 <= data.Length) Buffer.BlockCopy(data, k, Splat, (z * Resolution + x) * 4, 4);
        }

        // ---------------------------------------------------------------- queries

        /// <summary>Ray against the surface in LOCAL space (metres; corner at the origin, +X / +Z across). Marches half a cell
        /// at a time inside the terrain's box and bisects the crossing. False when the ray misses.</summary>
        public bool Raycast(Vector3 origin, Vector3 direction, float cellSize, float maxDistance, out Vector3 hit)
        {
            hit = default(Vector3);
            float len = direction.Length();
            if (len < 1e-6f || cellSize <= 0f) return false;
            direction /= len;
            float extent = (Resolution - 1) * cellSize;
            float yMin = MinHeight - 1f, yMax = MaxHeight + 1f;

            // clip to the box [0, extent] x [yMin, yMax] x [0, extent]
            float t0 = 0f, t1 = maxDistance;
            if (!Slab(origin.X, direction.X, 0f, extent, ref t0, ref t1)) return false;
            if (!Slab(origin.Y, direction.Y, yMin, yMax, ref t0, ref t1)) return false;
            if (!Slab(origin.Z, direction.Z, 0f, extent, ref t0, ref t1)) return false;
            if (t1 < t0) return false;

            float inv = 1f / cellSize;
            float step = cellSize * 0.5f;
            float horiz = (float)Math.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
            if (horiz > 1e-4f) step = Math.Min(step, cellSize * 0.5f / horiz);
            if (step < 1e-3f) step = 1e-3f;

            Vector3 p = origin + direction * t0;
            float above = p.Y - SampleHeight(p.X * inv, p.Z * inv);
            if (above <= 0f) { hit = p; return true; }   // starts under the surface: the entry point counts
            float t = t0;
            while (t < t1)
            {
                float tn = Math.Min(t + step, t1);
                Vector3 q = origin + direction * tn;
                float a2 = q.Y - SampleHeight(q.X * inv, q.Z * inv);
                if (a2 <= 0f)
                {
                    // bisect between t and tn
                    float lo = t, hi = tn;
                    for (int i = 0; i < 10; i++)
                    {
                        float mid = 0.5f * (lo + hi);
                        Vector3 m = origin + direction * mid;
                        if (m.Y - SampleHeight(m.X * inv, m.Z * inv) <= 0f) hi = mid; else lo = mid;
                    }
                    hit = origin + direction * hi;
                    hit.Y = SampleHeight(hit.X * inv, hit.Z * inv);
                    return true;
                }
                t = tn;
            }
            return false;
        }

        private static bool Slab(float o, float d, float lo, float hi, ref float t0, ref float t1)
        {
            if (Math.Abs(d) < 1e-8f) return o >= lo && o <= hi;
            float a = (lo - o) / d, b = (hi - o) / d;
            if (a > b) { float tmp = a; a = b; b = tmp; }
            if (a > t0) t0 = a;
            if (b < t1) t1 = b;
            return t1 >= t0;
        }

        /// <summary>A copy at another resolution (bilinear heights, nearest splat).</summary>
        public TerrainData Resample(int newResolution)
        {
            var o = new TerrainData(newResolution);
            int n = Resolution, m = newResolution;
            float scale = (n - 1) / (float)(m - 1);
            for (int z = 0; z < m; z++)
                for (int x = 0; x < m; x++)
                {
                    float sx = x * scale, sz = z * scale;
                    o.Heights[z * m + x] = SampleHeight(sx, sz);
                    int ix = ClampIndex((int)Math.Round(sx)), iz = ClampIndex((int)Math.Round(sz));
                    Buffer.BlockCopy(Splat, (iz * n + ix) * 4, o.Splat, (z * m + x) * 4, 4);
                }
            o._rangeDirty = true;
            return o;
        }

        // ---------------------------------------------------------------- file

        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(Resolution);
                w.Write(1);   // flags: bit 0 = splat present
                for (int i = 0; i < Heights.Length; i++) w.Write(Heights[i]);
                w.Write(Splat);
                return ms.ToArray();
            }
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, ToBytes());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static TerrainData FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 16) return null;
            using (var r = new BinaryReader(new MemoryStream(bytes)))
            {
                if (r.ReadUInt32() != Magic) return null;
                int version = r.ReadInt32();
                if (version < 1 || version > Version) return null;
                int res = r.ReadInt32();
                int flags = r.ReadInt32();
                if (res < MinResolution || res > MaxResolution) return null;
                var d = new TerrainData(res);
                for (int i = 0; i < d.Heights.Length; i++) d.Heights[i] = r.ReadSingle();
                if ((flags & 1) != 0)
                {
                    var s = r.ReadBytes(res * res * 4);
                    if (s.Length == res * res * 4) d.Splat = s;
                }
                d._rangeDirty = true;
                return d;
            }
        }

        public static TerrainData Load(string path)
        {
            try { return File.Exists(path) ? FromBytes(File.ReadAllBytes(path)) : null; }
            catch { return null; }
        }
    }
}
