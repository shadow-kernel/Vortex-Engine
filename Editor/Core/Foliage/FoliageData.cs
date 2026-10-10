using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Editor.Core.Foliage
{
    /// <summary>One painted instance in WORLD space: position, orientation, uniform scale.</summary>
    public struct FoliageInstance
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Scale;

        public FoliageInstance(Vector3 position, Quaternion rotation, float scale) { Position = position; Rotation = rotation; Scale = scale; }
    }

    /// <summary>The instances of one foliage type, with a 16 m cell index for culling and spacing queries.</summary>
    public sealed class FoliageLayer
    {
        public string TypeName;
        public readonly List<FoliageInstance> Instances = new List<FoliageInstance>();
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private int _indexedCount = -1;
        private int _indexedVersion = -1;
        /// <summary>Bumped on every change (the service re-reads the layer when it differs from what it drew).</summary>
        public int Version { get; private set; }

        public int Count => Instances.Count;

        public void Touch() { Version++; }

        public int Add(FoliageInstance inst)
        {
            Instances.Add(inst);
            Version++;
            return Instances.Count - 1;
        }

        /// <summary>Every instance whose position lies within <paramref name="radius"/> of <paramref name="centre"/> (XZ distance).</summary>
        public List<int> Within(Vector3 centre, float radius)
        {
            EnsureIndex();
            var hits = new List<int>();
            float r2 = radius * radius;
            ForEachCell(centre, radius, list =>
            {
                foreach (int i in list)
                {
                    var p = Instances[i].Position;
                    float dx = p.X - centre.X, dz = p.Z - centre.Z;
                    if (dx * dx + dz * dz <= r2) hits.Add(i);
                }
            });
            return hits;
        }

        /// <summary>True when another instance stands within <paramref name="minDistance"/> (XZ) of the point.</summary>
        public bool AnyWithin(Vector3 point, float minDistance)
        {
            EnsureIndex();
            float r2 = minDistance * minDistance;
            bool found = false;
            ForEachCell(point, minDistance, list =>
            {
                if (found) return;
                foreach (int i in list)
                {
                    var p = Instances[i].Position;
                    float dx = p.X - point.X, dz = p.Z - point.Z;
                    if (dx * dx + dz * dz < r2) { found = true; return; }
                }
            });
            return found;
        }

        /// <summary>Remove the instances at these indices (any order).</summary>
        public void RemoveAt(List<int> indices)
        {
            if (indices == null || indices.Count == 0) return;
            indices.Sort();
            for (int k = indices.Count - 1; k >= 0; k--)
            {
                int i = indices[k];
                if (i < 0 || i >= Instances.Count) continue;
                int last = Instances.Count - 1;
                Instances[i] = Instances[last];
                Instances.RemoveAt(last);
            }
            Version++;
        }

        public void Clear() { Instances.Clear(); Version++; }

        /// <summary>Replace every instance (undo / redo, loading).</summary>
        public void Set(List<FoliageInstance> instances)
        {
            Instances.Clear();
            if (instances != null) Instances.AddRange(instances);
            Version++;
        }

        public List<FoliageInstance> Snapshot() => new List<FoliageInstance>(Instances);

        // ---------------------------------------------------------------- cell index

        public const float CellSize = 16f;

        public static long CellKey(float x, float z)
        {
            int cx = (int)Math.Floor(x / CellSize), cz = (int)Math.Floor(z / CellSize);
            return ((long)cx << 32) ^ (uint)cz;
        }

        private void EnsureIndex()
        {
            if (_indexedVersion == Version && _indexedCount == Instances.Count) return;
            _cells.Clear();
            for (int i = 0; i < Instances.Count; i++)
            {
                long key = CellKey(Instances[i].Position.X, Instances[i].Position.Z);
                List<int> list;
                if (!_cells.TryGetValue(key, out list)) _cells[key] = list = new List<int>(8);
                list.Add(i);
            }
            _indexedVersion = Version;
            _indexedCount = Instances.Count;
        }

        /// <summary>Visit the index lists of every cell that intersects the XZ square of <paramref name="radius"/> around the centre.</summary>
        public void ForEachCell(Vector3 centre, float radius, Action<List<int>> visit)
        {
            EnsureIndex();
            int cx0 = (int)Math.Floor((centre.X - radius) / CellSize), cx1 = (int)Math.Floor((centre.X + radius) / CellSize);
            int cz0 = (int)Math.Floor((centre.Z - radius) / CellSize), cz1 = (int)Math.Floor((centre.Z + radius) / CellSize);
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    List<int> list;
                    if (_cells.TryGetValue(((long)cx << 32) ^ (uint)cz, out list)) visit(list);
                }
        }

        /// <summary>The occupied cells as (cx, cz, indices) — the service culls per cell.</summary>
        public IEnumerable<KeyValuePair<long, List<int>>> Cells()
        {
            EnsureIndex();
            return _cells;
        }

        public static void CellCentre(long key, out float x, out float z)
        {
            int cx = (int)(key >> 32), cz = (int)(key & 0xffffffff);
            x = (cx + 0.5f) * CellSize; z = (cz + 0.5f) * CellSize;
        }
    }

    /// <summary>
    /// The instances of one Foliage component (#125): a layer per type (bound by the type's name), saved as the
    /// <c>.vfoliage</c> asset ("VFOL", version 1: per layer the name, the count and 8 floats per instance).
    /// </summary>
    public sealed class FoliageData
    {
        private const uint Magic = 0x4C4F4656;   // "VFOL" little-endian
        private const int Version = 1;

        public readonly List<FoliageLayer> Layers = new List<FoliageLayer>();

        public FoliageLayer Layer(string typeName, bool create)
        {
            foreach (var l in Layers) if (string.Equals(l.TypeName, typeName, StringComparison.OrdinalIgnoreCase)) return l;
            if (!create) return null;
            var n = new FoliageLayer { TypeName = typeName };
            Layers.Add(n);
            return n;
        }

        public int TotalCount
        {
            get { int n = 0; foreach (var l in Layers) n += l.Count; return n; }
        }

        /// <summary>The sum of every layer's version (cheap change detection).</summary>
        public int VersionSum
        {
            get { int n = Layers.Count * 7919; foreach (var l in Layers) n += l.Version; return n; }
        }

        public void RenameLayer(string from, string to)
        {
            var l = Layer(from, false);
            if (l != null) l.TypeName = to;
        }

        public void RemoveLayer(string typeName)
        {
            Layers.RemoveAll(l => string.Equals(l.TypeName, typeName, StringComparison.OrdinalIgnoreCase));
        }

        // ---------------------------------------------------------------- file

        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(Layers.Count);
                foreach (var l in Layers)
                {
                    var name = Encoding.UTF8.GetBytes(l.TypeName ?? "");
                    w.Write(name.Length);
                    w.Write(name);
                    w.Write(l.Instances.Count);
                    foreach (var i in l.Instances)
                    {
                        w.Write(i.Position.X); w.Write(i.Position.Y); w.Write(i.Position.Z);
                        w.Write(i.Rotation.X); w.Write(i.Rotation.Y); w.Write(i.Rotation.Z); w.Write(i.Rotation.W);
                        w.Write(i.Scale);
                    }
                }
                return ms.ToArray();
            }
        }

        public static FoliageData FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12) return null;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes)))
                {
                    if (r.ReadUInt32() != Magic) return null;
                    int version = r.ReadInt32();
                    if (version < 1 || version > Version) return null;
                    int layers = r.ReadInt32();
                    if (layers < 0 || layers > 4096) return null;
                    var d = new FoliageData();
                    for (int k = 0; k < layers; k++)
                    {
                        int nameLen = r.ReadInt32();
                        if (nameLen < 0 || nameLen > 4096) return null;
                        string name = Encoding.UTF8.GetString(r.ReadBytes(nameLen));
                        int count = r.ReadInt32();
                        if (count < 0 || count > 50_000_000) return null;
                        var l = new FoliageLayer { TypeName = name };
                        l.Instances.Capacity = count;
                        for (int i = 0; i < count; i++)
                        {
                            var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            float s = r.ReadSingle();
                            l.Instances.Add(new FoliageInstance(p, q, s));
                        }
                        l.Touch();
                        d.Layers.Add(l);
                    }
                    return d;
                }
            }
            catch { return null; }
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

        public static FoliageData Load(string path)
        {
            try { return File.Exists(path) ? FromBytes(File.ReadAllBytes(path)) : null; }
            catch { return null; }
        }

        // ---------------------------------------------------------------- placement helpers

        /// <summary>An instance's rotation: yaw around the up axis, optionally tilted onto the surface normal, plus a random lean.</summary>
        public static Quaternion Orientation(Vector3 normal, bool alignToNormal, float yawDeg, float tiltDeg, float tiltDirDeg)
        {
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDeg * (float)Math.PI / 180f);
            if (tiltDeg > 0f)
            {
                float d = tiltDirDeg * (float)Math.PI / 180f;
                var axis = new Vector3((float)Math.Cos(d), 0f, (float)Math.Sin(d));
                q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, tiltDeg * (float)Math.PI / 180f) * q);
            }
            if (alignToNormal)
            {
                var n = normal.LengthSquared() > 1e-6f ? Vector3.Normalize(normal) : Vector3.UnitY;
                float dot = Math.Max(-1f, Math.Min(1f, Vector3.Dot(Vector3.UnitY, n)));
                if (dot < 0.9999f)
                {
                    var axis = Vector3.Cross(Vector3.UnitY, n);
                    if (axis.LengthSquared() > 1e-8f)
                        q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), (float)Math.Acos(dot)) * q);
                }
            }
            return q;
        }

        /// <summary>The 16-float row-major world matrix (translation in row 3) of an instance.</summary>
        public static void WorldMatrix(in FoliageInstance i, float[] into, int offset)
        {
            var m = Matrix4x4.CreateScale(i.Scale) * Matrix4x4.CreateFromQuaternion(i.Rotation) * Matrix4x4.CreateTranslation(i.Position);
            into[offset] = m.M11; into[offset + 1] = m.M12; into[offset + 2] = m.M13; into[offset + 3] = m.M14;
            into[offset + 4] = m.M21; into[offset + 5] = m.M22; into[offset + 6] = m.M23; into[offset + 7] = m.M24;
            into[offset + 8] = m.M31; into[offset + 9] = m.M32; into[offset + 10] = m.M33; into[offset + 11] = m.M34;
            into[offset + 12] = m.M41; into[offset + 13] = m.M42; into[offset + 14] = m.M43; into[offset + 15] = m.M44;
        }
    }
}
