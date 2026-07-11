using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Editor.Core.Animation;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;
using Vec3 = System.Numerics.Vector3;
using Quat = System.Numerics.Quaternion;
using Mat4 = System.Numerics.Matrix4x4;

namespace Editor.Editors.AnimationEditor
{
    /// <summary>
    /// The Keyframe Editor's 3D preview: the bound model rendered POSED through the engine's GPU-skinning
    /// path (AssetPreviewRenderer.RenderSkinnedMeshes), with the skeleton drawn as a managed overlay using
    /// the SAME camera framing as the renderer (FOV 35, model fills 0.92, optional focus point) so joints
    /// line up with the image. Interaction: drag a joint = FK-rotate that bone in the view plane (raises
    /// BoneRotated — the window owns the pose override), drag empty space = orbit, Shift+drag or middle
    /// drag = pan the camera target, wheel = zoom (0.02..12), double-click a joint = focus the camera on it.
    /// </summary>
    public sealed class AnimationPreviewControl : Grid
    {
        private readonly Image _img;
        private readonly Canvas _overlay;
        private readonly TextBlock _hint;
        private readonly TextBlock _help;

        private long[] _meshIds;
        private long[] _matIds;
        private string _modelPathCached;
        private SkeletonDef _skeleton;

        // Socket editor: an optional rigid ATTACHMENT (weapon/accessory) drawn on a bone at a live bone-LOCAL offset.
        private long[] _attMesh, _attMat;
        private string _attModelPathCached;
        private string _attBone;
        private Vec3   _attPos, _attRotEuler;      // bone-local offset (metres, euler degrees)
        private float  _attScale = 1f;
        private bool   _hasAtt;

        // Socket editor renders a cm-authored Mixamo character at ~human scale so a real-size weapon is proportional
        // and the offset can be dialed in metres. Purely visual: the offset math is scale-independent (see
        // BoneSocketService.ComposeBoneLocalMatrix), so editor placement == in-game placement regardless.
        private float _modelScale = 1f;
        private bool  _normalizeToHuman;
        public bool NormalizeToHuman { get { return _normalizeToHuman; } set { _normalizeToHuman = value; } }

        // current pose inputs (window pushes these via SetPose)
        private VortexAnimClip _clip;
        private float _time;
        private Func<string, (Vec3 pos, Quat rot, Vec3 scale)?> _boneOverride;

        private string _selectedBone;
        private bool _showBones = true;

        // camera state: orbit + zoom + optional focus target (null = auto bounds center)
        private float _yaw = 0.9f, _pitch = 0.35f, _distScale = 1.15f;
        private Vec3? _focus;

        // input state — orbit / pan / bone drag are mutually exclusive
        private Point _lastDrag, _downPos, _prevMouse;
        private bool _orbiting, _orbited, _panning, _boneDragging;
        private string _dragBone;
        private int _dragBoneIndex = -1;
        private Point _dragPivot;                    // screen-space rotation pivot (parent joint)

        // camera of the last render — mirror of AssetPreviewRenderer's framing, used to project joints
        private float _ex, _ey, _ez;                 // eye
        private float _bxx, _bxy, _bxz;              // right axis
        private float _byx, _byy, _byz;              // up axis
        private float _bzx, _bzy, _bzz;              // forward axis
        private float _projScale, _near, _camDist;

        // debounce: skip the engine render when nothing that feeds it changed
        private float[] _lastPalette;
        private float _lastYaw, _lastPitch, _lastDist;
        private Vec3 _lastFocus;
        private bool _lastFocusValid;
        private bool _hasRender;

        private Mat4[] _worlds;                      // node worlds of the last pose (overlay + picking)

        private const int RenderSize = 768;
        private const float Fov = 35f;
        private const double PickRadiusPx = 14.0;

        public SkeletonDef Skeleton => _skeleton;
        public string SelectedBone => _selectedBone;

        /// <summary>True once the model's meshes are imported (GPU device was ready). False means the render
        /// device wasn't initialized yet at bind time — the owner can re-bind after a viewport has rendered.</summary>
        public bool HasMeshes => _meshIds != null && _meshIds.Length > 0;

        /// <summary>Raised when a joint is picked (mouse-down within pick radius — also a bone-drag start).</summary>
        public event Action<string> BoneClicked;

        /// <summary>FK posing: LOCAL-space rotation delta for a bone while its joint is dragged. The WINDOW
        /// owns the pose override — it composes newLocal = Concatenate(currentLocal, delta) and re-SetPoses.</summary>
        public event Action<string, System.Numerics.Quaternion> BoneRotated;

        public bool ShowBones
        {
            get => _showBones;
            set { _showBones = value; DrawOverlay(); }
        }

        /// <summary>True while a joint is being dragged (window uses this to route ESC = cancel).</summary>
        public bool IsBoneDragActive => _boneDragging;

        public AnimationPreviewControl()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x12));
            _img = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
            _overlay = new Canvas { IsHitTestVisible = false };
            _hint = new TextBlock
            {
                Text = "Bind a model to begin",
                Foreground = new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x7A)),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _help = new TextBlock
            {
                Text = "drag joint: rotate  •  Shift+drag / MMB: pan  •  wheel: zoom  •  dbl-click joint / F: focus  •  Shift+F: reset view",
                Foreground = new SolidColorBrush(Color.FromArgb(0xB4, 0x6E, 0x6E, 0x77)),
                FontSize = 10,
                Margin = new Thickness(10, 0, 10, 7),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Children.Add(_img);
            Children.Add(_overlay);
            Children.Add(_hint);
            Children.Add(_help);

            ClipToBounds = true;
            Cursor = Cursors.SizeAll;
            MouseDown += OnDown;
            MouseUp += OnUp;
            MouseMove += OnMove;
            MouseWheel += OnWheel;
            SizeChanged += (s, e) => { ComputeCamera(); DrawOverlay(); };
        }

        /// <summary>Import the model's meshes/materials (owned copies) + load its skeleton. Null/missing clears.</summary>
        public void BindModel(string fullModelPath)
        {
            if (!string.IsNullOrEmpty(fullModelPath) && fullModelPath == _modelPathCached && _meshIds != null)
                return; // same model already loaded

            FreeMeshes();
            _modelPathCached = null;
            _skeleton = null;
            _hasRender = false;
            _worlds = null;
            _focus = null;

            if (string.IsNullOrEmpty(fullModelPath) || !System.IO.File.Exists(fullModelPath))
            {
                Redraw();
                return;
            }

            try
            {
                var subs = VortexAPI.ImportModelWithMaterialsFromFile(fullModelPath);
                if (subs != null && subs.Length > 0)
                {
                    _meshIds = new long[subs.Length];
                    _matIds = new long[subs.Length];
                    for (int i = 0; i < subs.Length; i++) { _meshIds[i] = subs[i].MeshId; _matIds[i] = subs[i].MaterialId; }
                    _modelPathCached = fullModelPath;
                }
            }
            catch { _meshIds = null; _matIds = null; }

            try { _skeleton = AnimationService.Instance.GetSkeleton(fullModelPath); } catch { _skeleton = null; }

            // Socket editor: normalize a cm-authored character to ~human height so real-size attachments are proportional.
            _modelScale = 1f;
            if (_normalizeToHuman && _meshIds != null)
            {
                float maxY = 0f;
                foreach (var m in _meshIds)
                    if (VortexAPI.GetMeshBounds(m, out float sx, out float sy, out float sz) && sy > maxY) maxY = sy;
                if (maxY > 0.001f) _modelScale = 1.8f / maxY;   // ~1.8 m tall
            }
            Redraw();
        }

        /// <summary>Push the pose to show: clip evaluated at `time`, then boneOverride (per bone NAME —
        /// non-null result replaces that bone's local TRS) merged in. Re-renders (debounced).</summary>
        public void SetPose(VortexAnimClip clip, float time, Func<string, (Vec3 pos, Quat rot, Vec3 scale)?> boneOverride)
        {
            _clip = clip;
            _time = time;
            _boneOverride = boneOverride;
            Redraw();
        }

        /// <summary>Load the attachment model's meshes (owned copies). Null/empty clears the attachment.</summary>
        public void BindAttachment(string fullModelPath)
        {
            if (!string.IsNullOrEmpty(fullModelPath) && fullModelPath == _attModelPathCached && _attMesh != null) return;
            if (_attMesh != null) foreach (var m in _attMesh) { try { if (m >= 0) VortexAPI.DeleteMesh(m); } catch { } }
            _attMesh = null; _attMat = null; _attModelPathCached = null; _hasAtt = false;
            if (string.IsNullOrEmpty(fullModelPath) || !System.IO.File.Exists(fullModelPath)) { Redraw(); return; }
            try
            {
                var subs = VortexAPI.ImportModelWithMaterialsFromFile(fullModelPath);
                if (subs != null && subs.Length > 0)
                {
                    _attMesh = new long[subs.Length]; _attMat = new long[subs.Length];
                    for (int i = 0; i < subs.Length; i++) { _attMesh[i] = subs[i].MeshId; _attMat[i] = subs[i].MaterialId; }
                    _attModelPathCached = fullModelPath; _hasAtt = true;
                }
            }
            catch { _attMesh = null; _attMat = null; _hasAtt = false; }
            Redraw();
        }

        /// <summary>Socket bone + bone-LOCAL offset (pos m, rot euler deg, uniform scale) for the attachment. Live.</summary>
        public void SetSocket(string bone, Vec3 pos, Vec3 rotEulerDeg, float scale)
        {
            _attBone = bone;
            _attPos = pos; _attRotEuler = rotEulerDeg; _attScale = scale <= 0f ? 1f : scale;
            _hasRender = false;   // socket changed -> force a re-render
            Redraw();
        }

        /// <summary>Highlight a bone in the overlay (window's bone tree drives this too).</summary>
        public void SetSelectedBone(string bone)
        {
            _selectedBone = bone;
            DrawOverlay();
        }

        public void SetHint(string text) => _hint.Text = text ?? "";

        /// <summary>Focus the camera on the selected bone's joint (window routes the F key here).</summary>
        public void FocusSelectedBone()
        {
            int node = _skeleton != null && !string.IsNullOrEmpty(_selectedBone) ? _skeleton.FindNode(_selectedBone) : -1;
            if (node < 0 || _worlds == null || node >= _worlds.Length) { ResetFocus(); return; }
            FocusOnNode(node);
        }

        /// <summary>Back to the default framing: auto bounds-center target, distance 1 (Shift+F).</summary>
        public void ResetFocus()
        {
            _focus = null;
            _distScale = 1f;
            Redraw();
        }

        /// <summary>Abort an active joint drag without a final rotation (window routes ESC here and
        /// restores the pose override it snapshotted at drag start).</summary>
        public void CancelBoneDrag()
        {
            if (!_boneDragging) return;
            _boneDragging = false;
            _dragBoneIndex = -1;
            _dragBone = null;
            try { ReleaseMouseCapture(); } catch { }
            Cursor = Cursors.SizeAll;
        }

        /// <summary>Delete the owned engine meshes — call when the editor closes.</summary>
        public void Dispose()
        {
            FreeMeshes();
            _modelPathCached = null;
        }

        private void FreeMeshes()
        {
            if (_meshIds != null)
                foreach (var m in _meshIds) { try { if (m >= 0) VortexAPI.DeleteMesh(m); } catch { } }
            _meshIds = null;
            _matIds = null;
        }

        // ===================== input =====================

        private void OnDown(object s, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // pan: middle button, or Shift+left (checked before joint picking / orbit)
            if (e.ChangedButton == System.Windows.Input.MouseButton.Middle ||
                (e.ChangedButton == System.Windows.Input.MouseButton.Left && shift))
            {
                _panning = true;
                _lastDrag = p;
                Cursor = Cursors.SizeAll;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;

            int joint = HitTestJoint(p);

            if (e.ClickCount == 2)
            {
                // double-click a joint = focus the camera on it and move in close
                if (joint >= 0) FocusOnNode(joint);
                _boneDragging = false;
                _orbiting = false;
                e.Handled = true;
                return;
            }

            if (joint >= 0)
            {
                // FK posing: dragging this joint rotates the bone in the view plane around its parent joint
                _boneDragging = true;
                _dragBoneIndex = joint;
                _dragBone = _skeleton.Nodes[joint].Name;
                _selectedBone = _dragBone;
                _prevMouse = p;
                _dragPivot = GetDragPivot(joint, p);
                Cursor = Cursors.Hand;
                CaptureMouse();
                DrawOverlay();
                BoneClicked?.Invoke(_dragBone); // select immediately (window snapshots the override for ESC)
                e.Handled = true;
                return;
            }

            // empty space: orbit
            _orbiting = true;
            _orbited = false;
            _downPos = _lastDrag = p;
            CaptureMouse();
        }

        private void OnUp(object s, MouseButtonEventArgs e)
        {
            if (_panning &&
                (e.ChangedButton == System.Windows.Input.MouseButton.Middle ||
                 e.ChangedButton == System.Windows.Input.MouseButton.Left))
            {
                _panning = false;
                ReleaseMouseCapture();
                Cursor = Cursors.SizeAll;
                return;
            }

            if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;

            if (_boneDragging)
            {
                _boneDragging = false;
                _dragBoneIndex = -1;
                _dragBone = null;
                ReleaseMouseCapture();
                Cursor = Cursors.SizeAll;
                return;
            }

            if (_orbiting)
            {
                _orbiting = false;
                ReleaseMouseCapture();
            }
        }

        private void OnMove(object s, MouseEventArgs e)
        {
            var p = e.GetPosition(this);

            if (_panning)
            {
                PanBy(p.X - _lastDrag.X, p.Y - _lastDrag.Y);
                _lastDrag = p;
                return;
            }

            if (_boneDragging)
            {
                DragRotateBone(p);
                return;
            }

            if (_orbiting)
            {
                // a real drag orbits; tiny jitter is ignored
                if (!_orbited && Math.Abs(p.X - _downPos.X) < 3 && Math.Abs(p.Y - _downPos.Y) < 3) return;
                _orbited = true;
                _yaw += (float)(p.X - _lastDrag.X) * 0.01f;
                _pitch += (float)(p.Y - _lastDrag.Y) * 0.01f;
                _pitch = Math.Max(-1.4f, Math.Min(1.4f, _pitch));
                _lastDrag = p;
                Redraw();
                return;
            }

            // hover feedback: hand over grabbable joints, move-cross elsewhere
            Cursor = HitTestJoint(p) >= 0 ? Cursors.Hand : Cursors.SizeAll;
        }

        private void OnWheel(object s, MouseWheelEventArgs e)
        {
            // multiplicative: fine steps close to the model, fast far away (matches renderer clamp)
            _distScale *= e.Delta > 0 ? 1f / 1.15f : 1.15f;
            _distScale = Math.Max(0.02f, Math.Min(12f, _distScale));
            Redraw();
        }

        // ===================== pan / focus =====================

        private void PanBy(double dx, double dy)
        {
            double side = Math.Min(ActualWidth, ActualHeight);
            if (side < 4 || _camDist <= 0f) return;

            // world units per screen pixel at the target depth -> the model tracks the mouse ~1:1
            float fovHalf = Fov * 0.5f * (float)Math.PI / 180f;
            float scale = 2f * _camDist * (float)Math.Tan(fovHalf) / (float)side;

            var right = new Vec3(_bxx, _bxy, _bxz);
            var up = new Vec3(_byx, _byy, _byz);
            GetBounds(out _, out float acx, out float acy, out float acz);
            var cur = _focus ?? new Vec3(acx, acy, acz);
            _focus = cur + (right * (float)(-dx) + up * (float)dy) * scale;
            Redraw();
        }

        private void FocusOnNode(int node)
        {
            if (_worlds == null || node < 0 || node >= _worlds.Length) return;
            var t = _worlds[node].Translation;
            _focus = new Vec3(t.X, t.Y, t.Z);
            _distScale = Math.Min(_distScale, 0.35f); // move in close but never zoom OUT on focus
            Redraw();
        }

        // ===================== joint picking + FK bone drag =====================

        private int HitTestJoint(Point p)
        {
            if (_skeleton == null || _worlds == null) return -1;
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return -1;
            int best = -1;
            double bestD = PickRadiusPx;
            int count = Math.Min(_skeleton.Nodes.Length, _worlds.Length);
            for (int i = 0; i < count; i++)
            {
                var tr = _worlds[i].Translation;
                if (!Project(tr.X, tr.Y, tr.Z, w, h, out double sx, out double sy)) continue;
                double d = Math.Sqrt((sx - p.X) * (sx - p.X) + (sy - p.Y) * (sy - p.Y));
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Screen-space rotation pivot: the PARENT joint (the bone rotates about its parent);
        /// falls back to the bone's own joint, then the mouse point, when not projectable.</summary>
        private Point GetDragPivot(int nodeIndex, Point fallback)
        {
            double w = ActualWidth, h = ActualHeight;
            int parent = _skeleton.Nodes[nodeIndex].Parent;
            if (parent >= 0 && parent < _worlds.Length)
            {
                var t = _worlds[parent].Translation;
                if (Project(t.X, t.Y, t.Z, w, h, out double px, out double py)) return new Point(px, py);
            }
            var o = _worlds[nodeIndex].Translation;
            if (Project(o.X, o.Y, o.Z, w, h, out double ox, out double oy)) return new Point(ox, oy);
            return fallback;
        }

        private void DragRotateBone(Point cur)
        {
            if (_worlds == null || _skeleton == null || _dragBoneIndex < 0 || _dragBoneIndex >= _worlds.Length) return;

            // atan2 is unstable right at the pivot — wait until the cursor has some lever arm
            double pdx = cur.X - _dragPivot.X, pdy = cur.Y - _dragPivot.Y;
            if (Math.Sqrt(pdx * pdx + pdy * pdy) < 8) { _prevMouse = cur; return; }

            double a0 = Math.Atan2(_prevMouse.Y - _dragPivot.Y, _prevMouse.X - _dragPivot.X);
            double a1 = Math.Atan2(cur.Y - _dragPivot.Y, cur.X - _dragPivot.X);
            double delta = a1 - a0;
            while (delta > Math.PI) delta -= 2 * Math.PI;
            while (delta < -Math.PI) delta += 2 * Math.PI;
            _prevMouse = cur;
            if (Math.Abs(delta) < 1e-5) return;

            // World-space rotation axis = camera forward (points INTO the screen). System.Numerics
            // rotations are right-handed: a positive angle about F appears clockwise to the viewer,
            // matching increasing atan2 angle in y-down screen coords. If hand-testing ever shows the
            // drag inverted, flip the sign of `delta` here — it's a one-character fix.
            var f = new Vec3(_bzx, _bzy, _bzz);
            var qA = Quat.CreateFromAxisAngle(f, (float)delta);

            // Convert the world delta into the bone's LOCAL space (row-vector, Concatenate(a,b) = a then b):
            // world = Concatenate(local, P); want worldNew = Concatenate(world, qA)
            //   => localNew = Concatenate(local, P·qA·P⁻¹)  => localDelta = P·qA·P⁻¹
            int parent = _skeleton.Nodes[_dragBoneIndex].Parent;
            Quat qP = Quat.Identity;
            if (parent >= 0 && parent < _worlds.Length)
                qP = Quat.Normalize(Quat.CreateFromRotationMatrix(_worlds[parent]));
            var localDelta = Quat.Concatenate(Quat.Concatenate(qP, qA), Quat.Inverse(qP));

            BoneRotated?.Invoke(_dragBone, localDelta);
            // the window updates its override and calls SetPose -> we re-render + re-project the overlay
        }

        // ===================== pose evaluation + render =====================

        private void Redraw()
        {
            bool ready = _meshIds != null && _meshIds.Length > 0;
            _hint.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            _help.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
            if (!ready)
            {
                _img.Source = null;
                _overlay.Children.Clear();
                _worlds = null;
                return;
            }

            float[] palette = null;
            int boneCount = 0;
            _worlds = null;
            if (_skeleton != null && _skeleton.IsValid)
            {
                _worlds = EvaluateWorldsWithOverride();
                palette = _skeleton.FlattenPalette(_worlds);
                boneCount = _skeleton.Bones.Length;
            }

            bool focusSame = _lastFocusValid == _focus.HasValue && (!_focus.HasValue || _lastFocus == _focus.Value);
            bool cameraSame = _hasRender && _yaw == _lastYaw && _pitch == _lastPitch && _distScale == _lastDist && focusSame;
            if (!cameraSame || !PaletteEquals(palette, _lastPalette))
            {
                float[] focusArr = _focus.HasValue ? new[] { _focus.Value.X, _focus.Value.Y, _focus.Value.Z } : null;
                try
                {
                    // Attachment world: the SAME bone-local composition the game uses (ComposeBoneLocalMatrix), so
                    // what you dial here is exactly what WeaponMount renders in-game.
                    long[] attM = null, attMt = null; float[] attW = null;
                    if (_hasAtt && _attMesh != null && _skeleton != null && _worlds != null && !string.IsNullOrEmpty(_attBone))
                    {
                        int an = _skeleton.FindNode(_attBone);
                        if (an >= 0 && an < _worlds.Length)
                        {
                            Mat4 w = Editor.Core.Animation.BoneSocketService.ComposeBoneLocalMatrix(
                                _worlds[an], _attPos, _attRotEuler, _attScale);
                            attW = new[] { w.M11, w.M12, w.M13, w.M14, w.M21, w.M22, w.M23, w.M24,
                                           w.M31, w.M32, w.M33, w.M34, w.M41, w.M42, w.M43, w.M44 };
                            attM = _attMesh; attMt = _attMat;
                        }
                    }
                    _img.Source = attM != null
                        ? AssetPreviewRenderer.RenderSkinnedWithAttachment(_meshIds, _matIds, palette, boneCount,
                            attM, attMt, attW, RenderSize, _yaw, _pitch, _distScale, focusArr, _modelScale)
                        : AssetPreviewRenderer.RenderSkinnedMeshes(_meshIds, _matIds, palette, boneCount,
                            RenderSize, _yaw, _pitch, _distScale, renderGizmos: false, focusPoint: focusArr, boundsScale: _modelScale);
                    _hasRender = _img.Source != null;
                }
                catch { _img.Source = null; _hasRender = false; }
                _lastPalette = palette;
                _lastYaw = _yaw; _lastPitch = _pitch; _lastDist = _distScale;
                _lastFocusValid = _focus.HasValue;
                if (_focus.HasValue) _lastFocus = _focus.Value;
            }

            ComputeCamera();
            DrawOverlay();
        }

        /// <summary>Clip pose at _time with the window's per-bone override merged in, composed to node worlds
        /// (same math as AnimationService: EvaluateLocals -> ComposeWorlds).</summary>
        private Mat4[] EvaluateWorldsWithOverride()
        {
            int n = _skeleton.Nodes.Length;
            var t = new Vec3[n];
            var r = new Quat[n];
            var s = new Vec3[n];
            AnimationService.EvaluateLocals(_skeleton, _clip, null, _time, t, r, s);
            if (_boneOverride != null)
            {
                for (int i = 0; i < n; i++)
                {
                    var ov = _boneOverride(_skeleton.Nodes[i].Name);
                    if (ov.HasValue) { t[i] = ov.Value.pos; r[i] = ov.Value.rot; s[i] = ov.Value.scale; }
                }
            }
            var worlds = AnimationService.ComposeWorlds(_skeleton, t, r, s);
            // Socket editor: scale the whole rig about the origin so a cm-authored character renders at ~human size
            // and a real-size weapon is proportional. Bone sockets read these SAME scaled worlds, so the preview and
            // the bounds/overlay/camera all stay consistent. (No-op at ModelScale 1 = Keyframe Editor.)
            if (_modelScale != 1f && _modelScale > 0f)
            {
                var sc = Mat4.CreateScale(_modelScale);
                for (int i = 0; i < worlds.Length; i++) worlds[i] = worlds[i] * sc;
            }
            return worlds;
        }

        private static bool PaletteEquals(float[] a, float[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // ===================== bone overlay =====================

        private void DrawOverlay()
        {
            _overlay.Children.Clear();
            if (!_showBones || _skeleton == null || _worlds == null) return;
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return;

            var accent = new SolidColorBrush(Color.FromRgb(0x6C, 0x5C, 0xE7));
            var green = new SolidColorBrush(Color.FromArgb(0x99, 0x7C, 0xE0, 0xA3)); // #7CE0A3 @ 60%

            int count = Math.Min(_skeleton.Nodes.Length, _worlds.Length);
            // parent->child lines first so joints draw on top
            for (int i = 0; i < count; i++)
            {
                int parent = _skeleton.Nodes[i].Parent;
                if (parent < 0 || parent >= count) continue;
                var a = _worlds[parent].Translation;
                var b = _worlds[i].Translation;
                if (!Project(a.X, a.Y, a.Z, w, h, out double x1, out double y1)) continue;
                if (!Project(b.X, b.Y, b.Z, w, h, out double x2, out double y2)) continue;
                bool sel = _selectedBone != null && _skeleton.Nodes[i].Name == _selectedBone;
                _overlay.Children.Add(new Line
                {
                    X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                    Stroke = sel ? accent : (Brush)green,
                    StrokeThickness = sel ? 2.0 : 1.2,
                    SnapsToDevicePixels = true
                });
            }
            for (int i = 0; i < count; i++)
            {
                var tr = _worlds[i].Translation;
                if (!Project(tr.X, tr.Y, tr.Z, w, h, out double sx, out double sy)) continue;
                bool sel = _selectedBone != null && _skeleton.Nodes[i].Name == _selectedBone;
                double size = sel ? 8 : 5;
                var joint = new Rectangle
                {
                    Width = size, Height = size,
                    Fill = sel ? accent : (Brush)green,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(45)
                };
                Canvas.SetLeft(joint, sx - size * 0.5);
                Canvas.SetTop(joint, sy - size * 0.5);
                _overlay.Children.Add(joint);
            }
        }

        // ===================== camera mirror (AssetPreviewRenderer framing) =====================

        private void GetBounds(out float radius, out float cx, out float cy, out float cz)
        {
            radius = 0.4f; cx = 0; cy = 0; cz = 0;
            if (_meshIds == null) return;
            bool gotCenter = false;
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
        }

        private void ComputeCamera()
        {
            if (_meshIds == null || _meshIds.Length == 0) return;
            GetBounds(out float radius, out float cx, out float cy, out float cz);
            // Match the renderer: the skinned character is scaled by _modelScale (Socket Editor), but mesh bounds
            // are un-scaled — scale the framing so the overlay joints land on the rendered (scaled) character.
            if (_modelScale != 1f && _modelScale > 0f) { radius *= _modelScale; cx *= _modelScale; cy *= _modelScale; cz *= _modelScale; }
            // mirror the renderer: focusPoint replaces the TARGET only — radius/dist still come from bounds
            if (_focus.HasValue) { cx = _focus.Value.X; cy = _focus.Value.Y; cz = _focus.Value.Z; }

            float fovHalf = Fov * 0.5f * (float)Math.PI / 180f;
            float dist = radius / (0.92f * (float)Math.Tan(fovHalf));
            float d = dist * Math.Max(0.02f, Math.Min(12f, _distScale));
            _ex = cx + d * (float)(Math.Cos(_pitch) * Math.Sin(_yaw));
            _ey = cy + d * (float)Math.Sin(_pitch);
            _ez = cz + d * (float)(Math.Cos(_pitch) * Math.Cos(_yaw));
            _near = Math.Max(0.02f, d * 0.01f);
            _projScale = 1f / (float)Math.Tan(fovHalf); // aspect 1 (square render target)
            _camDist = d;

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

        // Project a world point to overlay pixels. The Image is Stretch=Uniform, so the SQUARE render sits
        // letterboxed in the control — project into that centered square, not the full control rect.
        private bool Project(float wx, float wy, float wz, double w, double h, out double sx, out double sy)
        {
            float vx = wx - _ex, vy = wy - _ey, vz = wz - _ez;
            float cxv = vx * _bxx + vy * _bxy + vz * _bxz;
            float cyv = vx * _byx + vy * _byy + vz * _byz;
            float czv = vx * _bzx + vy * _bzy + vz * _bzz; // view-space depth (LH: +z forward)
            if (czv <= _near) { sx = sy = 0; return false; }
            float ndcX = (cxv * _projScale) / czv;
            float ndcY = (cyv * _projScale) / czv;
            double side = Math.Min(w, h);
            double x0 = (w - side) * 0.5, y0 = (h - side) * 0.5;
            sx = x0 + (ndcX * 0.5 + 0.5) * side;
            sy = y0 + (0.5 - ndcY * 0.5) * side;
            return true;
        }
    }
}
