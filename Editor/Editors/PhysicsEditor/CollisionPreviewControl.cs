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
            var mr = _ent?.GetComponent<MeshRenderer>();
            string mp = mr?.MeshPath;
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
                    string full = mp;
                    var root = Editor.Core.Data.ProjectData.Current?.Path;
                    if (!System.IO.Path.IsPathRooted(full) && !string.IsNullOrEmpty(root))
                        full = System.IO.Path.Combine(root, full);
                    var subs = VortexAPI.ImportModelWithMaterialsFromFile(full);
                    if (subs != null && subs.Length > 0)
                    {
                        _meshIds = new long[subs.Length]; _matIds = new long[subs.Length];
                        for (int i = 0; i < subs.Length; i++) { _meshIds[i] = subs[i].MeshId; _matIds[i] = subs[i].MaterialId; }
                    }
                    else { _meshIds = null; _matIds = null; }
                }
            }
            catch { _meshIds = null; _matIds = null; }
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

            // 1) object image (empty sunlit world) from the shared offscreen renderer
            try { _img.Source = AssetPreviewRenderer.RenderMeshes(_meshIds, _matIds, RenderSize, _yaw, _pitch, _distScale); }
            catch { _img.Source = null; }

            // 2) recompute the SAME camera the renderer used, so the overlay lines up
            ComputeCamera();

            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return;

            // 3) fine reference grid on the ground plane (faint)
            DrawGrid(w, h);

            // 4) green collider wireframe (always-on-top, like the scene view)
            if (col != null && col.IsEnabled) DrawCollider(col, w, h);
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

        private void DrawCollider(Collider col, double w, double h)
        {
            var green = new SolidColorBrush(Color.FromRgb(0x40, 0xF2, 0x73)); // matches the scene collider gizmo
            double t = 1.6;
            // The mesh is submitted at IDENTITY (its local origin at world 0), so the collider — whose Center is
            // relative to the entity origin — anchors at world origin + col.Center (NOT the bounds center).
            float cx = col.Center.X, cy = col.Center.Y, cz = col.Center.Z;

            if (col is BoxCollider box)
            {
                float hx = Math.Abs(box.Size.X * 0.5f), hy = Math.Abs(box.Size.Y * 0.5f), hz = Math.Abs(box.Size.Z * 0.5f);
                DrawBox(cx, cy, cz, hx, hy, hz, w, h, green, t);
            }
            else if (col is SphereCollider sph)
            {
                DrawSphere(cx, cy, cz, sph.Radius, w, h, green, t);
            }
            else if (col is CapsuleCollider cap)
            {
                DrawCapsule(cx, cy, cz, cap.Radius, cap.Height, w, h, green, t);
            }
            else // mesh / base collider: outline the mesh bounds
            {
                if (VortexAPI.GetMeshBounds(_meshIds[0], out float sx, out float sy, out float sz))
                    DrawBox(_cx, _cy, _cz, Math.Abs(sx * 0.5f), Math.Abs(sy * 0.5f), Math.Abs(sz * 0.5f), w, h, green, t);
            }
        }

        private void DrawBox(float cx, float cy, float cz, float hx, float hy, float hz, double w, double h, Brush b, double t)
        {
            // 8 corners
            float[,] c = new float[8, 3];
            int k = 0;
            for (int xi = -1; xi <= 1; xi += 2)
                for (int yi = -1; yi <= 1; yi += 2)
                    for (int zi = -1; zi <= 1; zi += 2)
                    { c[k, 0] = cx + xi * hx; c[k, 1] = cy + yi * hy; c[k, 2] = cz + zi * hz; k++; }
            // edges connect corners differing in exactly one axis
            for (int i = 0; i < 8; i++)
                for (int j = i + 1; j < 8; j++)
                {
                    int diff = 0;
                    if (Math.Abs(c[i, 0] - c[j, 0]) > 1e-4) diff++;
                    if (Math.Abs(c[i, 1] - c[j, 1]) > 1e-4) diff++;
                    if (Math.Abs(c[i, 2] - c[j, 2]) > 1e-4) diff++;
                    if (diff == 1) AddSeg(c[i, 0], c[i, 1], c[i, 2], c[j, 0], c[j, 1], c[j, 2], w, h, b, t);
                }
        }

        private void DrawSphere(float cx, float cy, float cz, float r, double w, double h, Brush b, double t)
        {
            DrawCircle(cx, cy, cz, r, 0, w, h, b, t); // XY
            DrawCircle(cx, cy, cz, r, 1, w, h, b, t); // XZ
            DrawCircle(cx, cy, cz, r, 2, w, h, b, t); // YZ
        }

        // plane: 0 = XY (normal Z), 1 = XZ (normal Y), 2 = YZ (normal X)
        private void DrawCircle(float cx, float cy, float cz, float r, int plane, double w, double h, Brush b, double t)
        {
            const int SEG = 40;
            float px = 0, py = 0, pz = 0; bool first = true;
            float fx0 = 0, fy0 = 0, fz0 = 0;
            for (int i = 0; i <= SEG; i++)
            {
                double a = i * 2.0 * Math.PI / SEG;
                float ca = (float)Math.Cos(a) * r, sa = (float)Math.Sin(a) * r;
                float x, y, z;
                if (plane == 0) { x = cx + ca; y = cy + sa; z = cz; }
                else if (plane == 1) { x = cx + ca; y = cy; z = cz + sa; }
                else { x = cx; y = cy + ca; z = cz + sa; }
                if (!first) AddSeg(px, py, pz, x, y, z, w, h, b, t);
                else { fx0 = x; fy0 = y; fz0 = z; }
                px = x; py = y; pz = z; first = false;
            }
        }

        private void DrawCapsule(float cx, float cy, float cz, float r, float height, double w, double h, Brush b, double t)
        {
            float halfCyl = Math.Max(0f, height * 0.5f - r);
            float topY = cy + halfCyl, botY = cy - halfCyl;
            // two rings (top/bottom of the cylinder)
            DrawCircle(cx, topY, cz, r, 1, w, h, b, t);
            DrawCircle(cx, botY, cz, r, 1, w, h, b, t);
            // 4 vertical connectors
            AddSeg(cx + r, topY, cz, cx + r, botY, cz, w, h, b, t);
            AddSeg(cx - r, topY, cz, cx - r, botY, cz, w, h, b, t);
            AddSeg(cx, topY, cz + r, cx, botY, cz + r, w, h, b, t);
            AddSeg(cx, topY, cz - r, cx, botY, cz - r, w, h, b, t);
            // dome arcs (top + bottom hemispheres, two planes each)
            DrawArc(cx, topY, cz, r, 0, +1, w, h, b, t);
            DrawArc(cx, topY, cz, r, 2, +1, w, h, b, t);
            DrawArc(cx, botY, cz, r, 0, -1, w, h, b, t);
            DrawArc(cx, botY, cz, r, 2, -1, w, h, b, t);
        }

        // half-circle arc in a vertical plane (0 = XY, 2 = YZ), dir +1 = upper dome, -1 = lower
        private void DrawArc(float cx, float cy, float cz, float r, int plane, int dir, double w, double h, Brush b, double t)
        {
            const int SEG = 20;
            float px = 0, py = 0, pz = 0; bool first = true;
            for (int i = 0; i <= SEG; i++)
            {
                double a = Math.PI * i / SEG;                 // 0..pi
                float horiz = (float)Math.Cos(a) * r;         // +r..-r
                float vert = (float)Math.Sin(a) * r * dir;    // 0..r..0 (up or down)
                float x, y, z;
                if (plane == 0) { x = cx + horiz; y = cy + vert; z = cz; }
                else { x = cx; y = cy + vert; z = cz + horiz; }
                if (!first) AddSeg(px, py, pz, x, y, z, w, h, b, t);
                px = x; py = y; pz = z; first = false;
            }
        }
    }
}
