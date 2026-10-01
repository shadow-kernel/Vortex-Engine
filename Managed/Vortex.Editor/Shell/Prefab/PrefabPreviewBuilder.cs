using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using Vector3 = System.Numerics.Vector3;

namespace VortexEditor.Shell.Prefab
{
    /// <summary>
    /// Live preview content for the Prefab Editor: builds a <see cref="PreviewScene"/> from the IN-MEMORY prefab
    /// template (so unsaved edits show immediately) — every active MeshRenderer with its entity's world transform,
    /// the prefab's .vmat / inline colour materials, and a wire box around the selected entity. Imported models,
    /// primitives and materials are created once and cached for the builder's lifetime, so rebuilding after every
    /// edit only re-links items (no re-import). <see cref="Dispose"/> releases every engine resource it created.
    /// Mirrors <see cref="PreviewModel.LoadPrefab"/> (template origin at the root, Scale·EulerZXY·Translation).
    /// </summary>
    public sealed class PrefabPreviewBuilder : IDisposable
    {
        public PreviewScene Scene { get; } = new PreviewScene();
        /// <summary>Entity whose bounds get the selection box (null = none).</summary>
        public GameEntity Highlight { get; set; }
        public bool ShowHighlight { get; set; } = true;
        /// <summary>Number of mesh items of the last build.</summary>
        public int MeshItems => Scene.Items.Count;

        private readonly Dictionary<string, VortexAPI.SubmeshImportData[]> _models = new Dictionary<string, VortexAPI.SubmeshImportData[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _primitives = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _vmats = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _colors = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<long> _ownedMeshes = new List<long>();
        private readonly List<long> _ownedMaterials = new List<long>();
        private readonly List<(PreviewItem item, GameEntity owner)> _owners = new List<(PreviewItem, GameEntity)>();
        private readonly Dictionary<GameEntity, Matrix4x4> _worlds = new Dictionary<GameEntity, Matrix4x4>();
        private readonly Dictionary<string, SkeletonDef> _skeletons = new Dictionary<string, SkeletonDef>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, (Vector3 center, Vector3 half)> _skinBounds = new Dictionary<long, (Vector3, Vector3)>();
        private float[] _frame;
        private long _boxMesh = -1, _boxMaterial = -1;
        private bool _disposed;

        public PrefabPreviewBuilder()
        {
            Scene.SubmitGizmos = SubmitHighlight;
        }

        /// <summary>Re-link the preview items from the current state of the template.</summary>
        public void Rebuild(GameEntity root)
        {
            if (_disposed) return;
            Scene.Items.Clear();
            _owners.Clear();
            _worlds.Clear();
            if (root != null) AddEntity(root, Matrix4x4.Identity, true);
            Scene.Bounds = ComputeBounds();
            _frame = PreviewRenderer.ComputeFrame(Scene);
            Scene.RenderGizmos = ShowHighlight && Highlight != null;
        }

        private void AddEntity(GameEntity e, Matrix4x4 parentWorld, bool isRoot)
        {
            if (e == null || !e.IsActive) return;
            var t = e.Transform;
            Matrix4x4 local = Matrix4x4.Identity;
            if (t != null)
            {
                var p = t.LocalPosition; var r = t.LocalRotation; var s = t.LocalScale;
                var pos = isRoot ? Vector3.Zero : new Vector3(p.X, p.Y, p.Z);   // the template origin is where it spawns
                local = Matrix4x4.CreateScale(s.X, s.Y, s.Z) * BoneSocketService.EulerZXY(new Vector3(r.X, r.Y, r.Z)) * Matrix4x4.CreateTranslation(pos);
            }
            var world = local * parentWorld;
            _worlds[e] = world;
            foreach (var c in e.Components)
            {
                if (!(c is MeshRenderer mr) || !mr.IsEnabled || string.IsNullOrEmpty(mr.MeshPath)) continue;
                AddMesh(e, mr, world);
            }
            foreach (var ch in e.Children) AddEntity(ch, world, false);
        }

        private void AddMesh(GameEntity owner, MeshRenderer mr, Matrix4x4 world)
        {
            float[] w = ToArray(world);
            long material = MaterialFor(mr);
            string meshPath = mr.MeshPath;
            if (meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                string key = meshPath.Substring(10).ToLowerInvariant();
                if (!_primitives.TryGetValue(key, out long mesh))
                {
                    mesh = PreviewModel.CreatePrimitive(meshPath);
                    _primitives[key] = mesh;
                    if (mesh >= 0) _ownedMeshes.Add(mesh);
                }
                if (mesh < 0) return;
                if (material < 0) material = ColorMaterial(mr);
                Link(owner, new PreviewItem { Mesh = mesh, Material = material, World = w });
                return;
            }
            string file = meshPath; int sub = -1;
            int hash = meshPath.LastIndexOf("#submesh", StringComparison.OrdinalIgnoreCase);
            if (hash > 0 && int.TryParse(meshPath.Substring(hash + 8), out int n)) { file = meshPath.Substring(0, hash); sub = n; }
            string full = ResolveAsset(file);
            if (!_models.TryGetValue(full, out var subs))
            {
                subs = null;
                try
                {
                    if (File.Exists(full)) subs = VortexAPI.ImportModelWithMaterialsFromFile(full);
                    if (subs != null)
                    {
                        var mats = new long[subs.Length];
                        for (int i = 0; i < subs.Length; i++) mats[i] = subs[i].MaterialId;
                        try { MaterialService.Instance.ApplyModelSidecarVmats(mats, full); } catch { }
                        for (int i = 0; i < subs.Length; i++)
                        {
                            subs[i].MaterialId = mats[i];
                            if (subs[i].MeshId >= 0) _ownedMeshes.Add(subs[i].MeshId);
                            if (mats[i] >= 0 && !_ownedMaterials.Contains(mats[i])) _ownedMaterials.Add(mats[i]);
                        }
                    }
                }
                catch { subs = null; }
                _models[full] = subs;
            }
            if (subs == null) return;
            for (int i = 0; i < subs.Length; i++)
            {
                if (sub >= 0 && i != sub) continue;
                if (subs[i].MeshId < 0) continue;
                var item = new PreviewItem { Mesh = subs[i].MeshId, Material = material >= 0 ? material : subs[i].MaterialId, World = w };
                // Skinned meshes draw in their bind pose (what the scene shows when nothing animates) — unskinned, the
                // raw vertices of a rig (Z-up, centimetres) would lie on their side.
                var skel = SkinnedSkeleton(full, subs[i].MeshId);
                if (skel != null) { item.BonePalette = skel.BindPosePalette(); item.BoneCount = skel.Bones.Length; }
                Link(owner, item);
            }
        }

        private SkeletonDef SkinnedSkeleton(string modelFull, long mesh)
        {
            bool skinned = false;
            try { skinned = AnimationService.Instance.IsMeshSkinned(mesh); } catch { }
            if (!skinned) return null;
            if (!_skeletons.TryGetValue(modelFull, out var skel))
            {
                try { skel = AnimationService.Instance.GetSkeleton(modelFull); } catch { skel = null; }
                if (skel != null && !skel.IsValid) skel = null;
                _skeletons[modelFull] = skel;
            }
            if (skel != null && !_skinBounds.ContainsKey(mesh))
            {
                // skinned vertices end up in the skeleton's model space: frame them by the joints (+ a margin for the
                // flesh around them) — the mesh's own bounds are the raw, unskinned ones
                var worlds = skel.BindNodeWorldsCached();
                Vector3 mn = new Vector3(float.MaxValue), mx = new Vector3(float.MinValue);
                foreach (var b in skel.Bones)
                {
                    if (b.NodeIndex < 0 || b.NodeIndex >= worlds.Length) continue;
                    var p = worlds[b.NodeIndex].Translation;
                    mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
                }
                if (mn.X <= mx.X)
                {
                    var ext = mx - mn;
                    float pad = Math.Max(0.02f, Math.Max(ext.X, Math.Max(ext.Y, ext.Z)) * 0.12f);
                    _skinBounds[mesh] = ((mn + mx) * 0.5f, ext * 0.5f + new Vector3(pad));
                }
            }
            return skel;
        }

        /// <summary>World-space AABB of one preview item (joint bounds for skinned meshes).</summary>
        private bool ItemBounds(PreviewItem item, out Vector3 lo, out Vector3 hi)
        {
            lo = hi = Vector3.Zero;
            Vector3 c, he;
            if (item.BonePalette != null && _skinBounds.TryGetValue(item.Mesh, out var sb)) { c = sb.center; he = sb.half; }
            else
            {
                if (!VortexAPI.GetMeshBounds(item.Mesh, out float sx, out float sy, out float sz)) return false;
                VortexAPI.GetMeshBoundsCenter(item.Mesh, out float bx, out float by, out float bz);
                c = new Vector3(bx, by, bz); he = new Vector3(sx, sy, sz) * 0.5f;
            }
            var w = item.World;
            var m = w != null && w.Length >= 16 ? new Matrix4x4(w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7], w[8], w[9], w[10], w[11], w[12], w[13], w[14], w[15]) : Matrix4x4.Identity;
            lo = new Vector3(float.MaxValue); hi = new Vector3(float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var p = c + new Vector3((i & 1) != 0 ? he.X : -he.X, (i & 2) != 0 ? he.Y : -he.Y, (i & 4) != 0 ? he.Z : -he.Z);
                var wp = Vector3.Transform(p, m);
                lo = Vector3.Min(lo, wp); hi = Vector3.Max(hi, wp);
            }
            return true;
        }

        private float[] ComputeBounds()
        {
            Vector3 mn = new Vector3(float.MaxValue), mx = new Vector3(float.MinValue);
            bool any = false;
            foreach (var (item, _) in _owners)
            {
                if (!ItemBounds(item, out var lo, out var hi)) continue;
                mn = Vector3.Min(mn, lo); mx = Vector3.Max(mx, hi); any = true;
            }
            if (!any) return null;
            var center = (mn + mx) * 0.5f;
            return new[] { center.X, center.Y, center.Z, Math.Max(0.02f, (mx - mn).Length() * 0.5f) };
        }

        private void Link(GameEntity owner, PreviewItem item)
        {
            Scene.Items.Add(item);
            _owners.Add((item, owner));
        }

        private long MaterialFor(MeshRenderer mr)
        {
            string mp = mr.MaterialPath;
            if (string.IsNullOrEmpty(mp) || mp.StartsWith("Material:", StringComparison.OrdinalIgnoreCase)) return -1;
            string full = ResolveAsset(mp);
            if (_vmats.TryGetValue(full, out long cached)) return cached;
            long mat = -1;
            try
            {
                if (File.Exists(full))
                {
                    var vm = Editor.Core.Assets.VortexMaterial.Load(full);
                    if (vm != null)
                    {
                        try { vm.ResolvePathsAbsolute(Path.GetDirectoryName(full)); } catch { }
                        mat = MaterialService.Instance.BuildEngineMaterial(vm);
                        if (mat >= 0) _ownedMaterials.Add(mat);
                    }
                }
            }
            catch { mat = -1; }
            _vmats[full] = mat;
            return mat;
        }

        /// <summary>Inline base colour (+ metallic / roughness) material for primitives without a .vmat — the look the
        /// scene gives them (the WPF prefab preview did the same).</summary>
        private long ColorMaterial(MeshRenderer mr)
        {
            string key = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###},{3:0.###},{4:0.##},{5:0.##}", mr.ColorR, mr.ColorG, mr.ColorB, mr.ColorA, mr.Metallic, mr.Roughness);
            if (_colors.TryGetValue(key, out long m)) return m;
            long mid = -1;
            try
            {
                mid = VortexAPI.CreateNewMaterial();
                if (mid >= 0)
                {
                    VortexAPI.SetMaterialBaseColor(mid, mr.ColorR, mr.ColorG, mr.ColorB, mr.ColorA);
                    try { VortexAPI.SetMaterialMetallicValue(mid, mr.Metallic); VortexAPI.SetMaterialRoughnessValue(mid, mr.Roughness); } catch { }
                    _ownedMaterials.Add(mid);
                }
            }
            catch { mid = -1; }
            _colors[key] = mid;
            return mid;
        }

        private static string ResolveAsset(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string p = path.Replace('\\', '/');
            if (Path.IsPathRooted(p)) return p;
            var root = ProjectData.Current?.Path;
            return string.IsNullOrEmpty(root) ? p : Path.Combine(root, p);
        }

        private static float[] ToArray(Matrix4x4 m) => new[]
        {
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44
        };

        private static bool IsUnder(GameEntity e, GameEntity ancestor)
        {
            for (var p = e; p != null; p = p.Parent) if (ReferenceEquals(p, ancestor)) return true;
            return false;
        }

        /// <summary>World-space bounds (centre + size) of the highlighted entity's subtree; a small box at its origin
        /// when it draws nothing itself (lights, empties).</summary>
        public bool TryGetHighlightBounds(out Vector3 center, out Vector3 size)
        {
            center = Vector3.Zero; size = Vector3.Zero;
            var h = Highlight;
            if (h == null) return false;
            Vector3 mn = new Vector3(float.MaxValue), mx = new Vector3(float.MinValue);
            bool any = false;
            foreach (var (item, owner) in _owners)
            {
                if (!IsUnder(owner, h)) continue;
                if (!ItemBounds(item, out var lo, out var hi)) continue;
                mn = Vector3.Min(mn, lo); mx = Vector3.Max(mx, hi);
                any = true;
            }
            if (any)
            {
                center = (mn + mx) * 0.5f;
                size = Vector3.Max(mx - mn, new Vector3(0.002f));
                return true;
            }
            if (_worlds.TryGetValue(h, out var w))
            {
                float r = _frame != null && _frame.Length >= 4 ? _frame[3] : 0.5f;
                float s = Math.Max(0.01f, r * 0.08f);
                center = new Vector3(w.M41, w.M42, w.M43);
                size = new Vector3(s);
                return true;
            }
            return false;
        }

        private void SubmitHighlight()
        {
            if (!ShowHighlight || Highlight == null) return;
            if (!TryGetHighlightBounds(out var c, out var s)) return;
            try
            {
                if (_boxMesh < 0) { _boxMesh = VortexAPI.CreateCubeMesh(1f); }
                if (_boxMaterial < 0)
                {
                    _boxMaterial = VortexAPI.CreateNewMaterial();
                    if (_boxMaterial >= 0)
                    {
                        VortexAPI.SetMaterialBaseColor(_boxMaterial, 1f, 0.62f, 0.12f, 1f);
                        VortexAPI.SetMaterialAsUnlit(_boxMaterial, true);
                        VortexAPI.SetMaterialEmissiveBrightness(_boxMaterial, 1f);
                    }
                }
                if (_boxMesh < 0 || _boxMaterial < 0) return;
                float pad = 1.02f;
                VortexAPI.SubmitGizmoWireForRendering(_boxMesh, _boxMaterial, new[]
                {
                    s.X * pad, 0, 0, 0,
                    0, s.Y * pad, 0, 0,
                    0, 0, s.Z * pad, 0,
                    c.X, c.Y, c.Z, 1
                });
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Scene.Items.Clear();
            _owners.Clear();
            foreach (var m in _ownedMeshes) { try { VortexAPI.DeleteMesh(m); } catch { } }
            foreach (var m in _ownedMaterials) { try { VortexAPI.DeleteMaterial(m); } catch { } }
            if (_boxMesh >= 0) { try { VortexAPI.DeleteMesh(_boxMesh); } catch { } }
            if (_boxMaterial >= 0) { try { VortexAPI.DeleteMaterial(_boxMaterial); } catch { } }
            _ownedMeshes.Clear(); _ownedMaterials.Clear(); _models.Clear(); _primitives.Clear(); _vmats.Clear(); _colors.Clear(); _skeletons.Clear(); _skinBounds.Clear();
            _boxMesh = _boxMaterial = -1;
        }
    }
}
