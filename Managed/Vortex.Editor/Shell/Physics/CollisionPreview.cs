using System;
using System.Collections.Generic;
using System.Numerics;
using Vector3 = System.Numerics.Vector3;
using Avalonia.Media;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using VortexEditor.Shell.Animation;

namespace VortexEditor.Shell.Physics
{
    /// <summary>
    /// Live preview of the Collision Editor: ONLY the target object in an empty sunlit world (the foundation preview
    /// renderer), its colliders as always-on-top wireframe nets drawn by the engine with the same camera (green = solid,
    /// amber = trigger, blue = the collider selected in the editor), and a fine reference grid under it. Everything is
    /// in the entity's LOCAL space (the collision service scales colliders by the entity transform the same way), so the
    /// net sits exactly where it collides. Orbit / pan / zoom like every preview.
    /// </summary>
    public sealed class CollisionPreview : ProjectedPreview, IDisposable
    {
        private readonly PreviewScene _scene = new PreviewScene();
        private GameEntity _ent;
        private PreviewModel _model;
        private string _meshKey;
        private long _placeholder = -1;
        private float[] _meshBounds;              // local mesh AABB: min xyz, max xyz (null = no mesh)

        // own unlit wire materials + unit meshes (the shared collider gizmo only draws Y-axis capsules and has no highlight)
        private long _matSolid = -1, _matTrigger = -1, _matSelected = -1;
        private long _cube = -1, _sphere = -1, _cylinder = -1;

        /// <summary>The collider drawn highlighted (blue).</summary>
        public Collider Selected { get => _selected; set { _selected = value; Invalidate(); } }
        private Collider _selected;

        public CollisionPreview()
        {
            HelpText = "Drag: orbit · Right/Shift-drag: pan · Wheel: zoom · Double-click: reset · green = solid, amber = trigger";
            SetHint("Select an entity to preview its colliders");
            DefaultCamera = new PreviewCamera { Yaw = 0.9f, Pitch = 0.5f, DistScale = 1.15f, FovDeg = 35f };
            ResetCamera();
            _scene.SubmitGizmos = SubmitColliderWires;
            _scene.RenderGizmos = true;
        }

        public GameEntity Target => _ent;

        /// <summary>Point the preview at an entity (its mesh + colliders). Null clears.</summary>
        public void SetTarget(GameEntity ent)
        {
            bool changed = !ReferenceEquals(ent, _ent);
            _ent = ent;
            LoadMesh();
            RebuildScene(changed);
        }

        /// <summary>Re-read the colliders (after an edit).</summary>
        public void Refresh()
        {
            LoadMesh();
            RebuildScene(false);
        }

        private void LoadMesh()
        {
            // An imported multi-material model is a parent CONTAINER (no MeshRenderer — that's where users put the
            // collider) whose children carry "<model>#submeshN": fall back to the first descendant's model (all
            // submeshes); a "#submeshN" on the entity itself previews exactly that submesh.
            string mp = _ent?.GetComponent<MeshRenderer>()?.MeshPath;
            int sub = -1;
            if (string.IsNullOrEmpty(mp))
            {
                mp = AnimUtil.FindMeshPath(_ent);
                if (!string.IsNullOrEmpty(mp)) mp = StripSubmesh(mp, out _);
            }
            else mp = StripSubmesh(mp, out sub);
            string key = string.IsNullOrEmpty(mp) ? null : mp + "|" + sub;
            if (key == _meshKey && (_model != null || key == null)) return;
            FreeModel();
            _meshKey = key;
            _meshBounds = null;
            if (key == null) return;
            if (mp.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                long m = PreviewModel.CreatePrimitive(mp);
                if (m < 0) m = VortexAPI.CreateCubeMesh(1f);
                if (m >= 0) _model = PreviewModel.FromOwned(new[] { m }, null);
            }
            else
            {
                _model = PreviewModel.Load(AnimUtil.ToAbsolute(mp));
                if (_model != null && sub >= 0 && sub < _model.Scene.Items.Count)
                {
                    var keep = _model.Scene.Items[sub];
                    _model.Scene.Items.Clear();
                    _model.Scene.Items.Add(keep);   // the siblings stay owned by the model and are released with it
                }
            }
            if (_model == null) return;
            if (PosedAabb(_model.Scene.Items, out var mn, out var mx)) _meshBounds = new[] { mn.X, mn.Y, mn.Z, mx.X, mx.Y, mx.Z };
        }

        /// <summary>The matrix an item is drawn with: a rigged mesh through its (bind-pose) palette, then its world matrix
        /// — the entity-local space the colliders live in.</summary>
        public static Matrix4x4 ItemMatrix(PreviewItem it)
        {
            var m = Matrix4x4.Identity;
            if (it?.BonePalette != null && it.BonePalette.Length >= 16 && it.BoneCount > 0)
                m = Editor.Core.Animation.SkeletonDef.ToMatrix(new ArraySegment<float>(it.BonePalette, 0, 16).ToArray());
            if (it?.World != null && it.World.Length >= 16) m = m * Editor.Core.Animation.SkeletonDef.ToMatrix(it.World);
            return m;
        }

        /// <summary>Axis-aligned bounds of the items AS DRAWN (see <see cref="ItemMatrix"/>).</summary>
        public static bool PosedAabb(IEnumerable<PreviewItem> items, out Vector3 mn, out Vector3 mx)
        {
            mn = new Vector3(float.MaxValue); mx = new Vector3(float.MinValue); bool any = false;
            if (items == null) return false;
            foreach (var it in items)
            {
                if (it == null || it.Mesh < 0 || !VortexAPI.GetMeshBounds(it.Mesh, out float sx, out float sy, out float sz)) continue;
                VortexAPI.GetMeshBoundsCenter(it.Mesh, out float cx, out float cy, out float cz);
                var he = new Vector3(sx, sy, sz) * 0.5f; var c = new Vector3(cx, cy, cz);
                var m = ItemMatrix(it);
                for (int k = 0; k < 8; k++)
                {
                    var p = Vector3.Transform(c + new Vector3((k & 1) != 0 ? he.X : -he.X, (k & 2) != 0 ? he.Y : -he.Y, (k & 4) != 0 ? he.Z : -he.Z), m);
                    mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
                }
                any = true;
            }
            return any;
        }

        private static string StripSubmesh(string p, out int sub)
        {
            sub = -1;
            int hash = p.LastIndexOf('#');
            if (hash > 0 && p.Length > hash + 8 && string.CompareOrdinal(p, hash + 1, "submesh", 0, 7) == 0 && int.TryParse(p.Substring(hash + 8), out int si))
            { sub = si; return p.Substring(0, hash); }
            return p;
        }

        private void RebuildScene(bool reframe)
        {
            _scene.Items.Clear();
            if (_ent == null) { Viewport.Scene = null; SetHint("Select an entity to preview its colliders"); ShowHelp = false; Overlay.InvalidateVisual(); return; }
            if (_model != null) foreach (var it in _model.Scene.Items) _scene.Items.Add(it);

            // framing: the mesh (stable while editing); a mesh-less trigger volume frames its colliders
            Vector3 mn, mx;
            if (_meshBounds != null) { mn = new Vector3(_meshBounds[0], _meshBounds[1], _meshBounds[2]); mx = new Vector3(_meshBounds[3], _meshBounds[4], _meshBounds[5]); }
            else if (!ColliderBounds(out mn, out mx)) { mn = new Vector3(-0.5f); mx = new Vector3(0.5f); }
            var c = (mn + mx) * 0.5f;
            _scene.Bounds = new[] { c.X, c.Y, c.Z, Math.Max(0.05f, (mx - mn).Length() * 0.5f) };

            if (_scene.Items.Count == 0)
            {
                // the renderer needs an item to draw a frame: an invisible (zero-scale) placeholder keeps the collider nets visible
                if (_placeholder < 0) _placeholder = VortexAPI.CreateCubeMesh(1f);
                if (_placeholder >= 0) _scene.Items.Add(new PreviewItem { Mesh = _placeholder, World = new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, c.X, c.Y, c.Z, 1 } });
            }
            bool hasColliders = _ent.GetComponents<Collider>().Length > 0;
            SetHint(_model == null && !hasColliders ? "No mesh and no collider on this entity" : null);
            ShowHelp = true;
            Viewport.Scene = _scene;
            if (reframe) ResetCamera();
            Invalidate();
        }

        private bool ColliderBounds(out Vector3 mn, out Vector3 mx)
        {
            mn = new Vector3(float.MaxValue); mx = new Vector3(float.MinValue); bool any = false;
            if (_ent == null) return false;
            foreach (var col in _ent.GetComponents<Collider>())
            {
                var ce = new Vector3(col.Center.X, col.Center.Y, col.Center.Z);
                Vector3 he;
                switch (col)
                {
                    case BoxCollider b: he = new Vector3(Math.Abs(b.Size.X), Math.Abs(b.Size.Y), Math.Abs(b.Size.Z)) * 0.5f; break;
                    case SphereCollider s: he = new Vector3(Math.Abs(s.Radius)); break;
                    case CapsuleCollider cp: { float r = Math.Abs(cp.Radius), hh = Math.Max(r, cp.Height * 0.5f); he = cp.Direction == 0 ? new Vector3(hh, r, r) : cp.Direction == 2 ? new Vector3(r, r, hh) : new Vector3(r, hh, r); break; }
                    default: he = new Vector3(0.5f); break;
                }
                mn = Vector3.Min(mn, ce - he); mx = Vector3.Max(mx, ce + he); any = true;
            }
            return any;
        }

        // ---------------------------------------------------------------- wire nets (engine gizmo pass, same camera)

        private void EnsureResources()
        {
            if (_matSolid < 0) _matSolid = Unlit(0.25f, 0.95f, 0.45f);
            if (_matTrigger < 0) _matTrigger = Unlit(1.0f, 0.62f, 0.12f);
            if (_matSelected < 0) _matSelected = Unlit(0.30f, 0.66f, 1.0f);
            if (_cube < 0) _cube = VortexAPI.CreateCubeMesh(1f);
            if (_sphere < 0) _sphere = VortexAPI.CreateSphereMesh(0.5f);
            if (_cylinder < 0) _cylinder = VortexAPI.CreateCylinderMesh(0.5f, 1f);
        }

        private static long Unlit(float r, float g, float b)
        {
            long m = VortexAPI.CreateNewMaterial();
            if (m >= 0)
            {
                VortexAPI.SetMaterialBaseColor(m, r, g, b, 1f);
                VortexAPI.SetMaterialAsUnlit(m, true);
                VortexAPI.SetMaterialEmissiveBrightness(m, 1f);
            }
            return m;
        }

        private void SubmitColliderWires()
        {
            if (_ent == null) return;
            EnsureResources();
            foreach (var col in _ent.GetComponents<Collider>())
            {
                if (!col.IsEnabled) continue;
                long mat = ReferenceEquals(col, _selected) ? _matSelected : col.IsTrigger ? _matTrigger : _matSolid;
                if (mat < 0) continue;
                var ce = new Vector3(col.Center.X, col.Center.Y, col.Center.Z);
                switch (col)
                {
                    case BoxCollider b:
                        Wire(_cube, mat, Matrix4x4.CreateScale(Math.Max(1e-4f, Math.Abs(b.Size.X)), Math.Max(1e-4f, Math.Abs(b.Size.Y)), Math.Max(1e-4f, Math.Abs(b.Size.Z))) * Matrix4x4.CreateTranslation(ce));
                        break;
                    case SphereCollider s:
                        { float d = Math.Max(1e-4f, Math.Abs(s.Radius) * 2f); Wire(_sphere, mat, Matrix4x4.CreateScale(d) * Matrix4x4.CreateTranslation(ce)); }
                        break;
                    case CapsuleCollider cp:
                        {
                            float r = Math.Max(1e-4f, Math.Abs(cp.Radius)), halfH = Math.Max(0f, cp.Height * 0.5f - r);
                            // local capsule along +Y, turned onto the chosen axis (0 X, 1 Y, 2 Z) like the physics service
                            Matrix4x4 axis = cp.Direction == 0 ? Matrix4x4.CreateRotationZ(-(float)Math.PI * 0.5f) : cp.Direction == 2 ? Matrix4x4.CreateRotationX((float)Math.PI * 0.5f) : Matrix4x4.Identity;
                            var place = axis * Matrix4x4.CreateTranslation(ce);
                            if (halfH > 0.005f) Wire(_cylinder, mat, Matrix4x4.CreateScale(r * 2f, halfH * 2f, r * 2f) * place);
                            Wire(_sphere, mat, Matrix4x4.CreateScale(r * 2f) * Matrix4x4.CreateTranslation(0, halfH, 0) * place);
                            if (halfH > 0.005f) Wire(_sphere, mat, Matrix4x4.CreateScale(r * 2f) * Matrix4x4.CreateTranslation(0, -halfH, 0) * place);
                        }
                        break;
                    default:
                        // mesh collider: the object's real triangles as the net (the collision mesh IS the render mesh);
                        // base / convex colliders: a box over the mesh bounds
                        if (col is MeshCollider && _model != null)
                            foreach (var it in _model.Scene.Items) Wire(it.Mesh, mat, ItemMatrix(it));
                        else if (_meshBounds != null)
                        {
                            var mn = new Vector3(_meshBounds[0], _meshBounds[1], _meshBounds[2]); var mx = new Vector3(_meshBounds[3], _meshBounds[4], _meshBounds[5]);
                            var size = Vector3.Max(mx - mn, new Vector3(1e-4f));
                            Wire(_cube, mat, Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation((mn + mx) * 0.5f));
                        }
                        break;
                }
            }
        }

        private static void Wire(long mesh, long mat, Matrix4x4 m)
        {
            if (mesh < 0) return;
            VortexAPI.SubmitGizmoWireForRendering(mesh, mat, new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 });
        }

        protected override void DrawOverlay(DrawingContext ctx, PreviewProjection proj)
        {
            if (_ent == null || _scene.Bounds == null) return;
            float cx = _scene.Bounds[0], cy = _scene.Bounds[1], cz = _scene.Bounds[2], r = _scene.Bounds[3];
            Grid(ctx, proj, cx, cy - r, cz, Math.Max(1.0f, r * 1.9f));
        }

        private void FreeModel()
        {
            if (_model != null) { try { _model.Dispose(); } catch { } _model = null; }
        }

        public void Dispose()
        {
            Viewport.Scene = null;
            FreeModel();
            _meshKey = null;
            foreach (var m in new[] { _cube, _sphere, _cylinder, _placeholder }) if (m >= 0) { try { VortexAPI.DeleteMesh(m); } catch { } }
            foreach (var m in new[] { _matSolid, _matTrigger, _matSelected }) if (m >= 0) { try { VortexAPI.DeleteMaterial(m); } catch { } }
            _cube = _sphere = _cylinder = _placeholder = -1; _matSolid = _matTrigger = _matSelected = -1;
        }
    }
}
