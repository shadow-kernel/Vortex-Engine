using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;

namespace Editor.Editors.PhysicsEditor
{
    /// <summary>
    /// Live preview for the Collision Editor: shows ONLY the selected object in an empty, sunlit world (via the
    /// shared offscreen renderer), with the collider drawn in GREEN as an always-on-top wireframe over a fine
    /// reference grid. Drag to orbit, wheel to zoom. The object image comes from the engine (AssetPreviewRenderer);
    /// the fine grid + green collider are projected in managed code with the SAME camera, so they line up — no
    /// engine/native changes and no mutation of the global scene grid.
    /// </summary>
    public sealed class CollisionPreviewControl : Grid
    {
        private readonly Image _img;
        private readonly Canvas _overlay;
        private readonly TextBlock _hint;

        private GameEntity _ent;
        private long[] _meshIds;
        private long[] _matIds;
        private string _meshPathCached;

        // orbit state
        private float _yaw = 0.9f, _pitch = 0.5f, _distScale = 1.15f;
        private Point _lastDrag; private bool _dragging;

        // camera the last render used (to project the overlay identically)
        private float _cx, _cy, _cz, _radius = 1f;
        private float _ex, _ey, _ez;                 // eye
        private float _bxx, _bxy, _bxz;              // right axis
        private float _byx, _byy, _byz;              // up axis
        private float _bzx, _bzy, _bzz;              // forward axis
        private float _projScale, _near;

        private const int RenderSize = 512;
        private const float Fov = 35f;

        public CollisionPreviewControl()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x12));
            _img = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
            _overlay = new Canvas { IsHitTestVisible = false };
            _hint = new TextBlock
            {
                Text = "Select an object with a collider to preview",
                Foreground = new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x7A)),
                FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            Children.Add(_img);
            Children.Add(_overlay);
            Children.Add(_hint);

            ClipToBounds = true;
            Cursor = Cursors.SizeAll;
            MouseDown += OnDown;
            MouseUp += OnUp;
            MouseMove += OnMove;
            MouseWheel += OnWheel;
            SizeChanged += (s, e) => Redraw();
        }

        /// <summary>Point the preview at an entity (its mesh + collider). Pass null to clear.</summary>
        public void SetTarget(GameEntity ent)
        {
            _ent = ent;
            LoadMesh();
            Redraw();
        }

        // ---- orbit input ----
        private void OnDown(object s, MouseButtonEventArgs e) { _dragging = true; _lastDrag = e.GetPosition(this); CaptureMouse(); }
        private void OnUp(object s, MouseButtonEventArgs e) { _dragging = false; ReleaseMouseCapture(); }
        private void OnMove(object s, MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(this);
            _yaw += (float)(p.X - _lastDrag.X) * 0.01f;
            _pitch += (float)(p.Y - _lastDrag.Y) * 0.01f;
            _pitch = Math.Max(-1.4f, Math.Min(1.4f, _pitch));
            _lastDrag = p;
            Redraw();
        }
        private void OnWheel(object s, MouseWheelEventArgs e)
        {
            _distScale *= e.Delta > 0 ? 0.9f : 1.111f;
            _distScale = Math.Max(0.4f, Math.Min(4f, _distScale));
            Redraw();
        }

        // ---- mesh loading (cached by path) ----
        private void LoadMesh()
        {
            // Imported multi-material models: the SELECTED entity is often the parent CONTAINER (no MeshRenderer;
            // the drop handler auto-selects it and that's where users put the collider) and its children carry
            // "<model>#submeshN" paths — neither is a file on disk. Resolve both, otherwise the preview is blank
            // for every imported model ("preview not rendered at all"): fall back to the first descendant's mesh
            // path, and strip the '#submeshN' selector before importing (keeping just that submesh when indexed).
            var mr = _ent?.GetComponent<MeshRenderer>();
            string mp = mr?.MeshPath;
            if (string.IsNullOrEmpty(mp)) mp = FindDescendantMeshPath(_ent);
            if (string.IsNullOrEmpty(mp)) { FreeMeshes(); _meshPathCached = null; return; }
            if (mp == _meshPathCached && _meshIds != null) return; // same mesh already loaded — no re-import

            FreeMeshes();   // release the previously-loaded (different) meshes so selecting around doesn't leak
            _meshPathCached = mp;
            try
            {
                if (mp.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                {
                    long m = CreatePrimitive(mp.Substring("Primitive:".Length).ToLowerInvariant());
                    _meshIds = m >= 0 ? new[] { m } : null;
                    _matIds = null;
                }
                else
                {
                    // Strip a real "#submeshN" token (matching the authoritative resolver in SceneRenderService:
                    // LastIndexOf + token check, so a legal '#' in a folder/file name isn't truncated).
                    string full = mp;
                    int submeshIndex = -1;
                    int hash = full.LastIndexOf('#');
                    if (hash > 0 && full.Length > hash + 8 &&
                        string.CompareOrdinal(full, hash + 1, "submesh", 0, 7) == 0 &&
                        int.TryParse(full.Substring(hash + 8), out int si))
                    {
                        submeshIndex = si;
                        full = full.Substring(0, hash);
                    }

                    var root = Editor.Core.Data.ProjectData.Current?.Path;
                    if (!System.IO.Path.IsPathRooted(full) && !string.IsNullOrEmpty(root))
                        full = System.IO.Path.Combine(root, full);
                    var subs = VortexAPI.ImportModelWithMaterialsFromFile(full);
                    if (subs != null && subs.Length > 0)
                    {
                        if (submeshIndex >= 0 && submeshIndex < subs.Length)
                        {
                            // A single "#submeshN" child was selected — preview exactly that submesh, and free
                            // the sibling imports we don't keep (the native import allocates fresh copies).
                            for (int i = 0; i < subs.Length; i++)
                                if (i != submeshIndex) { try { if (subs[i].MeshId >= 0) VortexAPI.DeleteMesh(subs[i].MeshId); } catch { } }
                            _meshIds = new[] { subs[submeshIndex].MeshId };
                            _matIds = new[] { subs[submeshIndex].MaterialId };
                        }
                        else
                        {
                            _meshIds = new long[subs.Length]; _matIds = new long[subs.Length];
                            for (int i = 0; i < subs.Length; i++) { _meshIds[i] = subs[i].MeshId; _matIds[i] = subs[i].MaterialId; }
                        }
                    }
                    else { _meshIds = null; _matIds = null; }
                }
            }
            catch { _meshIds = null; _matIds = null; }
        }

        /// <summary>First MeshPath found in the entity's descendants (depth-first). For an imported model's parent
        /// container this returns a child's "<model>#submeshN" path; LoadMesh strips the selector and, since the
        /// parent represents the WHOLE model, previews all submeshes (submeshIndex stays -1 only for real files —
        /// so pass the raw child path through and let the parent case import everything via the stripped file).</summary>
        private static string FindDescendantMeshPath(GameEntity ent)
        {
            if (ent?.Children == null) return null;
            foreach (var child in ent.Children)
            {
                var mr = child?.GetComponent<MeshRenderer>();
                if (!string.IsNullOrEmpty(mr?.MeshPath))
                {
                    string p = mr.MeshPath;
                    // The parent stands for the whole model: drop the "#submeshN" selector so ALL submeshes render.
                    int hash = p.LastIndexOf('#');
                    if (hash > 0 && p.Length > hash + 8 &&
                        string.CompareOrdinal(p, hash + 1, "submesh", 0, 7) == 0 &&
                        int.TryParse(p.Substring(hash + 8), out _))
                        p = p.Substring(0, hash);
                    return p;
                }
                var deeper = FindDescendantMeshPath(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>Delete the engine meshes this control created/imported. They are owned COPIES (the native
        /// import allocates fresh per call), separate from the scene's meshes — so freeing them is safe.</summary>
        private void FreeMeshes()
        {
            if (_meshIds != null)
                foreach (var m in _meshIds) { try { if (m >= 0) VortexAPI.DeleteMesh(m); } catch { } }
            _meshIds = null; _matIds = null;
        }

        /// <summary>Release all engine resources — call when the Collision Editor closes.</summary>
        public void Dispose() { FreeMeshes(); _meshPathCached = null; }

        private static long CreatePrimitive(string prim)
        {
            switch (prim)
            {
                case "cube": return VortexAPI.CreateCubeMesh(1.0f);
                case "sphere": return VortexAPI.CreateSphereMesh(0.5f);
                case "plane":
                case "quad": return VortexAPI.CreatePlaneMesh(1.0f, 1.0f);
                case "cylinder":
                case "capsule":
                case "cone": return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                default: return VortexAPI.CreateCubeMesh(1.0f);
            }
        }

        // ---- render ----
        private void Redraw()
        {
            var col = _ent?.GetComponent<Collider>();
            bool ready = _meshIds != null && _meshIds.Length > 0;
            _hint.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            _overlay.Children.Clear();
            if (!ready) { _img.Source = null; return; }

            // 1) Submit the collider wireframe into the GIZMO queue BEFORE the offscreen render swaps + draws it,
            //    so the ENGINE draws it with the SAME camera as the object — perfectly aligned, exactly like the
            //    scene view (no fragile managed projection).
            if (col != null && col.IsEnabled) { try { SubmitColliderGizmo(col); } catch { } }

            // 2) Object (empty sunlit world) + the green collider gizmo, both rendered by the engine into the target.
            try { _img.Source = AssetPreviewRenderer.RenderMeshes(_meshIds, _matIds, RenderSize, _yaw, _pitch, _distScale, renderGizmos: true); }
            catch { _img.Source = null; }

            // 3) Fine reference grid (managed overlay, faint) using the same camera framing.
            ComputeCamera();
            double w = ActualWidth, h = ActualHeight;
            if (w >= 4 && h >= 4) DrawGrid(w, h);
        }

        /// <summary>Submit the collider as a GREEN gizmo at the object's identity/origin position (the preview draws
        /// the mesh at identity). Reuses the exact scene-view collider gizmo API, so it renders identically + aligned.</summary>
        private void SubmitColliderGizmo(Collider col)
        {
            float cx = col.Center.X, cy = col.Center.Y, cz = col.Center.Z;
            bool trig = col.IsTrigger; // amber net for a trigger, green for a solid — so the toggle is visible here too
            if (col is BoxCollider box)
                VortexAPI.RenderColliderBox(cx, cy, cz, Math.Abs(box.Size.X * 0.5f), Math.Abs(box.Size.Y * 0.5f), Math.Abs(box.Size.Z * 0.5f), 0f, trig);
            else if (col is SphereCollider sph)
                VortexAPI.RenderColliderSphere(cx, cy, cz, sph.Radius, trig);
            else if (col is CapsuleCollider cap)
            {
                float cr = cap.Radius;
                VortexAPI.RenderColliderCapsule(cx, cy, cz, cr, Math.Max(0f, cap.Height * 0.5f - cr), trig);
            }
            else // mesh / base collider: a box over the object's combined mesh bounds (edge-accurate collides vs the real tris)
            {
                if (TryCombinedBounds(out float bcx, out float bcy, out float bcz, out float hx, out float hy, out float hz))
                    VortexAPI.RenderColliderBox(bcx, bcy, bcz, hx, hy, hz, 0f, trig);
            }
        }

        private bool TryCombinedBounds(out float cx, out float cy, out float cz, out float hx, out float hy, out float hz)
        {
            cx = cy = cz = hx = hy = hz = 0f;
            float minX = 1e30f, minY = 1e30f, minZ = 1e30f, maxX = -1e30f, maxY = -1e30f, maxZ = -1e30f; bool any = false;
            foreach (var m in _meshIds)
            {
                if (VortexAPI.GetMeshBounds(m, out float sx, out float sy, out float sz) &&
                    VortexAPI.GetMeshBoundsCenter(m, out float mcx, out float mcy, out float mcz))
                {
                    any = true;
                    minX = Math.Min(minX, mcx - sx * 0.5f); maxX = Math.Max(maxX, mcx + sx * 0.5f);
                    minY = Math.Min(minY, mcy - sy * 0.5f); maxY = Math.Max(maxY, mcy + sy * 0.5f);
                    minZ = Math.Min(minZ, mcz - sz * 0.5f); maxZ = Math.Max(maxZ, mcz + sz * 0.5f);
                }
            }
            if (!any) return false;
            cx = (minX + maxX) * 0.5f; cy = (minY + maxY) * 0.5f; cz = (minZ + maxZ) * 0.5f;
            hx = (maxX - minX) * 0.5f; hy = (maxY - minY) * 0.5f; hz = (maxZ - minZ) * 0.5f;
            return true;
        }

        private void ComputeCamera()
        {
            // Match AssetPreviewRenderer.RenderMeshes framing exactly.
            float radius = 0.4f, cx = 0, cy = 0, cz = 0; bool gotCenter = false;
            foreach (var m in _meshIds)
            {
                if (VortexAPI.GetMeshBounds(m, out float sx, out float sy, out float sz))
                {
                    float rr = 0.5f * (float)Math.Sqrt(sx * sx + sy * sy + sz * sz);
                    if (rr > radius) radius = rr;
                }
                if (!gotCenter && VortexAPI.GetMeshBoundsCenter(m, out float bx, out float by, out float bz))
                { cx = bx; cy = by; cz = bz; gotCenter = true; }
            }
            _cx = cx; _cy = cy; _cz = cz; _radius = radius;

            float fovHalf = Fov * 0.5f * (float)Math.PI / 180f;
            float dist = radius / (0.92f * (float)Math.Tan(fovHalf));
            float d = dist * _distScale;
            _ex = cx + d * (float)(Math.Cos(_pitch) * Math.Sin(_yaw));
            _ey = cy + d * (float)Math.Sin(_pitch);
            _ez = cz + d * (float)(Math.Cos(_pitch) * Math.Cos(_yaw));
            _near = Math.Max(0.02f, d * 0.01f);
            _projScale = 1f / (float)Math.Tan(fovHalf); // aspect 1 (square target stretched to fill canvas)

            // LH look-at basis: z forward (eye->center), x = up×z, y = z×x
            float zx = cx - _ex, zy = cy - _ey, zz = cz - _ez; Norm(ref zx, ref zy, ref zz);
            float ux = 0, uy = 1, uz = 0;
            float xx = uy * zz - uz * zy, xy = uz * zx - ux * zz, xz = ux * zy - uy * zx; Norm(ref xx, ref xy, ref xz);
            float yx = zy * xz - zz * xy, yy = zz * xx - zx * xz, yz = zx * xy - zy * xx;
            _bxx = xx; _bxy = xy; _bxz = xz;
            _byx = yx; _byy = yy; _byz = yz;
            _bzx = zx; _bzy = zy; _bzz = zz;
        }

        private static void Norm(ref float x, ref float y, ref float z)
        {
            float l = (float)Math.Sqrt(x * x + y * y + z * z);
            if (l > 1e-8f) { x /= l; y /= l; z /= l; }
        }

        // Project a world point to canvas pixels. Returns false if behind the camera.
        private bool Project(float wx, float wy, float wz, double w, double h, out double sx, out double sy)
        {
            float vx = wx - _ex, vy = wy - _ey, vz = wz - _ez;
            float cxv = vx * _bxx + vy * _bxy + vz * _bxz;
            float cyv = vx * _byx + vy * _byy + vz * _byz;
            float czv = vx * _bzx + vy * _bzy + vz * _bzz; // view-space depth (LH: +z forward)
            if (czv <= _near) { sx = sy = 0; return false; }
            float ndcX = (cxv * _projScale) / czv;
            float ndcY = (cyv * _projScale) / czv;
            sx = (ndcX * 0.5 + 0.5) * w;
            sy = (0.5 - ndcY * 0.5) * h;
            return true;
        }

        private void AddSeg(float ax, float ay, float az, float bx, float by, float bz, double w, double h, Brush brush, double thick)
        {
            if (!Project(ax, ay, az, w, h, out double x1, out double y1)) return;
            if (!Project(bx, by, bz, w, h, out double x2, out double y2)) return;
            _overlay.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thick, SnapsToDevicePixels = true });
        }

        private void DrawGrid(double w, double h)
        {
            var faint = new SolidColorBrush(Color.FromArgb(0x30, 0xB0, 0xB0, 0xB8));
            var faint2 = new SolidColorBrush(Color.FromArgb(0x55, 0xB8, 0xB8, 0xC2)); // accent lines
            float ext = Math.Max(1.0f, _radius * 1.9f);
            float spacing = ext / 20f;           // fine, many cells
            int n = (int)(ext / spacing);
            float y = _cy - _radius;             // ground plane just under the object
            for (int i = -n; i <= n; i++)
            {
                float p = i * spacing;
                bool major = (i % 5) == 0;
                var b = major ? faint2 : faint;
                double th = major ? 1.1 : 0.7;
                AddSeg(_cx + p, y, _cz - ext, _cx + p, y, _cz + ext, w, h, b, th); // lines along Z
                AddSeg(_cx - ext, y, _cz + p, _cx + ext, y, _cz + p, w, h, b, th); // lines along X
            }
        }

    }
}
