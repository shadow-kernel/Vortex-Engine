using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Animation;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;
using AvPoint = Avalonia.Point;

namespace VortexEditor.Shell.Animation
{
    /// <summary>
    /// Skinned character preview (Keyframe Editor, Socket Editor): the bound model rendered POSED through the engine's
    /// GPU skinning (<see cref="PreviewItem.BonePalette"/>) with the skeleton drawn as a projected overlay. Drag a joint
    /// = FK-rotate that bone in the view plane (<see cref="BoneRotated"/>; the window owns the pose override), drag empty
    /// space = orbit, right / middle / Shift+drag = pan, wheel = zoom, double-click a joint = focus it. An optional rigid
    /// ATTACHMENT (weapon / accessory) sits on a bone at a bone-local offset composed exactly like the game
    /// (<see cref="BoneSocketService.ComposeBoneLocalMatrix"/>), so placement here is placement in-game.
    /// </summary>
    public sealed class SkinnedPreview : ProjectedPreview, IDisposable
    {
        private readonly PreviewScene _scene = new PreviewScene();

        private PreviewModel _model;
        private string _modelPath;
        private SkeletonDef _skeleton;
        private bool[] _skinned;                 // per character item: GPU-skinned (palette) vs rigid (world matrix)
        private float[] _charBounds;             // unscaled model-space framing: cx, cy, cz, radius
        private float _modelScale = 1f;

        private PreviewModel _att;
        private string _attPath;
        private string _attBone;
        private Vector3 _attPos, _attRot;
        private float _attScale = 1f;
        private bool _attInScene;

        private VortexAnimClip _clip;
        private float _time;
        private Func<string, (Vector3 pos, Quaternion rot, Vector3 scale)?> _override;

        private Matrix4x4[] _worlds;
        private string _selectedBone;

        // joint drag (FK rotate)
        private bool _dragRotating;
        private int _dragNode = -1;
        private string _dragBone;
        private AvPoint _prevMouse, _pivot;

        private DispatcherTimer _retry;
        private int _retryLeft;

        private const double PickRadius = 14.0;

        public SkeletonDef Skeleton => _skeleton;
        public bool HasMeshes => _model != null && _model.Scene.Items.Count > 0;
        public string ModelPath => _modelPath;
        public string SelectedBone => _selectedBone;
        /// <summary>Node worlds of the pose on screen (model space, scaled by <see cref="ModelScale"/>).</summary>
        public Matrix4x4[] NodeWorlds => _worlds;
        public float ModelScale => _modelScale;
        /// <summary>Render a cm-authored character at ~1.8 m so a real-size attachment is proportional (Socket Editor).
        /// Purely visual — the socket math strips the bone scale, so editor placement == in-game placement.</summary>
        public bool NormalizeToHuman { get; set; }
        /// <summary>Joint drag rotates the bone (Keyframe Editor); off = a joint click only selects it (Socket Editor).</summary>
        public bool AllowBoneRotate { get; set; } = true;
        public bool ShowBones { get => _showBones; set { _showBones = value; Overlay.InvalidateVisual(); } }
        private bool _showBones = true;
        /// <summary>Draw the socket bone's local axes (red X, green Y, blue Z) at the attachment point.</summary>
        public bool ShowSocketAxes { get; set; }
        public bool IsBoneDragActive => _dragRotating;

        /// <summary>A joint was picked (mouse-down within the pick radius — also a bone-drag start).</summary>
        public event Action<string> BoneClicked;
        /// <summary>FK posing: LOCAL-space rotation delta for the dragged bone (compose onto the current local rotation).</summary>
        public event Action<string, Quaternion> BoneRotated;

        public SkinnedPreview()
        {
            HelpText = "Drag joint: rotate · Drag: orbit · Right/Shift-drag: pan · Wheel: zoom · Double-click joint / F: focus · Shift+F: reset view";
            SetHint("Bind a model to begin");
        }

        // ================================================================ content

        /// <summary>Import the model (owned copies of its meshes + materials) and load its skeleton. Null / missing clears.
        /// Returns false when nothing could be loaded (a missing file, or the renderer not ready yet — then it retries).</summary>
        public bool BindModel(string fullModelPath)
        {
            if (!string.IsNullOrEmpty(fullModelPath) && fullModelPath == _modelPath && _model != null) return true;
            StopRetry();
            FreeModel();
            _modelPath = null; _skeleton = null; _worlds = null; _charBounds = null; _modelScale = 1f;
            if (string.IsNullOrEmpty(fullModelPath) || !File.Exists(fullModelPath)) { Rebuild(); return false; }

            _model = PreviewModel.Load(fullModelPath);
            if (_model == null)
            {
                // the GPU device may not be ready yet (window opened during boot): re-bind for a while
                StartRetry(fullModelPath);
                Rebuild();
                return false;
            }
            _modelPath = fullModelPath;
            try { _skeleton = AnimationService.Instance.GetSkeleton(fullModelPath); } catch { _skeleton = null; }

            _skinned = new bool[_model.Scene.Items.Count];
            // The skinned output lives in the skeleton's space, which is NOT always the vertex space: a rig whose inverse
            // bind matrices carry the armature scale (soldier.glb: cm vertices, m output) skins into metres. Measure the
            // content through the bind-shape transform (inverseBind x bindWorld of a bone) so framing and the human-size
            // normalization match what is actually drawn.
            Matrix4x4 bindShape = Matrix4x4.Identity;
            if (_skeleton != null && _skeleton.IsValid)
            {
                try
                {
                    var bw = _skeleton.BindNodeWorldsCached();
                    var b0 = _skeleton.Bones[0];
                    if (b0.NodeIndex >= 0 && b0.NodeIndex < bw.Length) bindShape = b0.InverseBind * bw[b0.NodeIndex];
                }
                catch { bindShape = Matrix4x4.Identity; }
            }
            Vector3 mn = new Vector3(float.MaxValue), mx = new Vector3(float.MinValue);
            bool any = false;
            for (int i = 0; i < _model.Scene.Items.Count; i++)
            {
                var it = _model.Scene.Items[i];
                try { _skinned[i] = VortexAPI.MeshIsSkinned(it.Mesh); } catch { }
                if (!VortexAPI.GetMeshBounds(it.Mesh, out float sx, out float sy, out float sz)) continue;
                VortexAPI.GetMeshBoundsCenter(it.Mesh, out float bx, out float by, out float bz);
                var he = new Vector3(sx, sy, sz) * 0.5f; var c = new Vector3(bx, by, bz);
                var m = _skinned[i] ? bindShape : Matrix4x4.Identity;
                for (int k = 0; k < 8; k++)
                {
                    var p = Vector3.Transform(c + new Vector3((k & 1) != 0 ? he.X : -he.X, (k & 2) != 0 ? he.Y : -he.Y, (k & 4) != 0 ? he.Z : -he.Z), m);
                    mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
                }
                any = true;
            }
            if (any)
            {
                var c = (mn + mx) * 0.5f;
                _charBounds = new[] { c.X, c.Y, c.Z, Math.Max(0.02f, (mx - mn).Length() * 0.5f) };
                float height = mx.Y - mn.Y;
                if (NormalizeToHuman && height > 1e-4f) _modelScale = 1.8f / height;
            }
            var cam = Viewport.Camera; cam.Focus = null; Viewport.Camera = cam;
            Rebuild();
            return true;
        }

        /// <summary>Load the attachment model (a .ventity prefab resolves to its first mesh). Null / empty clears it.</summary>
        public void BindAttachment(string fullModelPath)
        {
            fullModelPath = ResolvePrefabMesh(fullModelPath);
            if (!string.IsNullOrEmpty(fullModelPath) && fullModelPath == _attPath && _att != null) return;
            if (_att != null) { try { _att.Dispose(); } catch { } _att = null; }
            _attPath = null;
            if (!string.IsNullOrEmpty(fullModelPath) && File.Exists(fullModelPath))
            {
                _att = PreviewModel.Load(fullModelPath);
                if (_att != null) _attPath = fullModelPath;
            }
            Rebuild();
        }

        public string AttachmentPath => _attPath;
        public bool HasAttachment => _att != null;

        /// <summary>Socket bone + bone-LOCAL offset (pos m, rot Euler degrees ZXY, uniform scale) of the attachment. Live.</summary>
        public void SetSocket(string bone, Vector3 pos, Vector3 rotEulerDeg, float scale)
        {
            _attBone = bone; _attPos = pos; _attRot = rotEulerDeg; _attScale = scale <= 0.0001f ? 1f : scale;
            UpdatePose();
        }

        /// <summary>The pose to show: <paramref name="clip"/> at <paramref name="time"/>, with <paramref name="boneOverride"/>
        /// (per bone name; non-null replaces that bone's local TRS) merged in.</summary>
        public void SetPose(VortexAnimClip clip, float time, Func<string, (Vector3 pos, Quaternion rot, Vector3 scale)?> boneOverride)
        {
            _clip = clip; _time = time; _override = boneOverride;
            UpdatePose();
        }

        public void SetSelectedBone(string bone) { _selectedBone = bone; Overlay.InvalidateVisual(); }

        /// <summary>World position (preview space) of a bone of the current pose.</summary>
        public bool TryGetBonePosition(string bone, out Vector3 p)
        {
            p = default;
            int n = _skeleton != null && !string.IsNullOrEmpty(bone) ? _skeleton.FindNode(bone) : -1;
            if (n < 0 || _worlds == null || n >= _worlds.Length) return false;
            p = _worlds[n].Translation; return true;
        }

        /// <summary>Focus the camera on the selected bone (F); falls back to the default framing.</summary>
        public void FocusSelectedBone()
        {
            if (TryGetBonePosition(_selectedBone, out var p)) FocusOn(p); else ResetFocus();
        }

        /// <summary>Abort an active joint drag without a final rotation (Esc — the window restores its snapshot).</summary>
        public void CancelBoneDrag()
        {
            if (!_dragRotating) return;
            _dragRotating = false; _dragNode = -1; _dragBone = null;
            Cursor = Cursor.Default;
        }

        private void Rebuild()
        {
            _scene.Items.Clear();
            if (_model != null) foreach (var it in _model.Scene.Items) _scene.Items.Add(it);
            _attInScene = false;
            _scene.Bounds = _charBounds == null ? null : new[] { _charBounds[0] * _modelScale, _charBounds[1] * _modelScale, _charBounds[2] * _modelScale, _charBounds[3] * _modelScale };
            if (_model == null)
            {
                Viewport.Scene = null;
                _worlds = null;
                SetHint(_hintOverride ?? "Bind a model to begin");
                ShowHelp = false;
                Overlay.InvalidateVisual();
                return;
            }
            SetHint(_skeleton == null || !_skeleton.IsValid ? "Model has no skeleton" : null);
            ShowHelp = true;
            Viewport.Scene = _scene;
            UpdatePose();
        }

        private string _hintOverride;
        /// <summary>Hint shown while no model is bound ("Model not found: …").</summary>
        public void SetEmptyHint(string text) { _hintOverride = text; if (_model == null) SetHint(text ?? "Bind a model to begin"); }

        private void UpdatePose()
        {
            if (_model == null) { Invalidate(); return; }
            float[] palette = null; int boneCount = 0;
            _worlds = null;
            if (_skeleton != null && _skeleton.IsValid)
            {
                try
                {
                    _worlds = EvaluateWorldsWithOverride();
                    palette = _skeleton.FlattenPalette(_worlds);
                    boneCount = _skeleton.Bones.Length;
                }
                catch { _worlds = null; palette = null; boneCount = 0; }
            }
            float[] scaleWorld = _modelScale != 1f ? Flatten(Matrix4x4.CreateScale(_modelScale)) : null;
            for (int i = 0; i < _model.Scene.Items.Count; i++)
            {
                var it = _model.Scene.Items[i];
                bool skinned = _skinned != null && i < _skinned.Length && _skinned[i] && palette != null;
                it.BonePalette = skinned ? palette : null;
                it.BoneCount = skinned ? boneCount : 0;
                it.World = skinned ? null : scaleWorld;     // the palette carries the scale of skinned meshes
            }

            // attachment on its socket bone
            bool place = false;
            if (_att != null && _worlds != null && !string.IsNullOrEmpty(_attBone))
            {
                int an = _skeleton.FindNode(_attBone);
                if (an >= 0 && an < _worlds.Length)
                {
                    var w = Flatten(BoneSocketService.ComposeBoneLocalMatrix(_worlds[an], _attPos, _attRot, _attScale));
                    foreach (var it in _att.Scene.Items) it.World = w;
                    place = true;
                }
            }
            if (place != _attInScene)
            {
                if (place) foreach (var it in _att.Scene.Items) _scene.Items.Add(it);
                else if (_att != null) foreach (var it in _att.Scene.Items) _scene.Items.Remove(it);
                _attInScene = place;
            }
            Invalidate();
        }

        /// <summary>Clip pose at the current time with the override merged in, composed to node worlds (the same math as
        /// AnimationService: EvaluateLocals -> ComposeWorlds), then scaled for <see cref="NormalizeToHuman"/>.</summary>
        private Matrix4x4[] EvaluateWorldsWithOverride()
        {
            int n = _skeleton.Nodes.Length;
            var t = new Vector3[n]; var r = new Quaternion[n]; var s = new Vector3[n];
            AnimationService.EvaluateLocals(_skeleton, _clip, null, _time, t, r, s);
            if (_override != null)
                for (int i = 0; i < n; i++)
                {
                    var ov = _override(_skeleton.Nodes[i].Name);
                    if (ov.HasValue) { t[i] = ov.Value.pos; r[i] = ov.Value.rot; s[i] = ov.Value.scale; }
                }
            var worlds = AnimationService.ComposeWorlds(_skeleton, t, r, s);
            if (_modelScale != 1f && _modelScale > 0f)
            {
                var sc = Matrix4x4.CreateScale(_modelScale);
                for (int i = 0; i < worlds.Length; i++) worlds[i] = worlds[i] * sc;
            }
            return worlds;
        }

        private static float[] Flatten(Matrix4x4 m) => new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };

        /// <summary>A .ventity prefab stands for its (first) mesh — attach prefab-to-prefab; a raw model passes through.</summary>
        public static string ResolvePrefabMesh(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".ventity", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return path;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip }))
                {
                    string mp = FirstMeshPath(doc.RootElement);
                    if (string.IsNullOrEmpty(mp) || mp.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return path;
                    int h = mp.IndexOf('#'); if (h > 0) mp = mp.Substring(0, h);
                    string abs = AnimUtil.ToAbsolute(mp);
                    return File.Exists(abs) ? abs : path;
                }
            }
            catch { return path; }
        }

        private static string FirstMeshPath(System.Text.Json.JsonElement e)
        {
            if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (e.TryGetProperty("components", out var comps) && comps.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var c in comps.EnumerateArray())
                    if (c.ValueKind == System.Text.Json.JsonValueKind.Object && c.TryGetProperty("meshPath", out var mp) && mp.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(mp.GetString()))
                        return mp.GetString();
            if (e.TryGetProperty("children", out var kids) && kids.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var k in kids.EnumerateArray()) { var r = FirstMeshPath(k); if (r != null) return r; }
            return null;
        }

        // ================================================================ retry (renderer not ready at bind time)

        private void StartRetry(string path)
        {
            _retryLeft = 40;
            _retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _retry.Tick += (s, e) =>
            {
                if (_retryLeft-- <= 0 || _model != null) { StopRetry(); return; }
                var m = PreviewModel.Load(path);
                if (m == null) return;
                m.Dispose();
                StopRetry();
                string p = path; _modelPath = null;
                BindModel(p);
                ModelRebound?.Invoke();
            };
            _retry.Start();
        }
        private void StopRetry() { if (_retry != null) { _retry.Stop(); _retry = null; } }
        /// <summary>A delayed (retried) model bind succeeded — refresh bone lists etc.</summary>
        public event Action ModelRebound;

        private void FreeModel()
        {
            if (_model != null) { try { _model.Dispose(); } catch { } _model = null; }
        }

        public void Dispose()
        {
            StopRetry();
            Viewport.Scene = null;
            FreeModel();
            if (_att != null) { try { _att.Dispose(); } catch { } _att = null; }
            _modelPath = null; _attPath = null;
        }

        // ================================================================ joint picking + FK drag

        private static bool Hidden(string name) => AnimUtil.IsHiddenNode(name);

        private int HitTestJoint(AvPoint p)
        {
            if (!_showBones || _skeleton == null || _worlds == null) return -1;
            var proj = Projection;
            if (!proj.Valid) return -1;
            int best = -1; double bestD = PickRadius;
            int count = Math.Min(_skeleton.Nodes.Length, _worlds.Length);
            for (int i = 0; i < count; i++)
            {
                if (Hidden(_skeleton.Nodes[i].Name)) continue;
                if (!proj.Project(_worlds[i].Translation, out var s)) continue;
                double d = Math.Sqrt((s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y));
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        protected override bool OnPress(PointerPressedEventArgs e, AvPoint p)
        {
            int joint = HitTestJoint(p);
            if (e.ClickCount >= 2)
            {
                if (joint < 0) return false;          // the viewport's own double-click (reset view)
                FocusOn(_worlds[joint].Translation);
                return true;
            }
            if (joint < 0) return false;              // orbit
            string name = _skeleton.Nodes[joint].Name;
            _selectedBone = name;
            Overlay.InvalidateVisual();
            BoneClicked?.Invoke(name);                // select first (the window snapshots its override for Esc)
            if (AllowBoneRotate)
            {
                _dragRotating = true; _dragNode = joint; _dragBone = name;
                _prevMouse = p; _pivot = DragPivot(joint, p);
                Cursor = new Cursor(StandardCursorType.Hand);
            }
            return true;
        }

        protected override void OnDrag(PointerEventArgs e, AvPoint p)
        {
            if (_dragRotating) DragRotateBone(p);
        }

        protected override void OnRelease(PointerReleasedEventArgs e, AvPoint p)
        {
            _dragRotating = false; _dragNode = -1; _dragBone = null;
            Cursor = Cursor.Default;
        }

        protected override void OnHover(PointerEventArgs e, AvPoint p)
        {
            bool over = HitTestJoint(p) >= 0;
            Cursor = over ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        }

        /// <summary>Screen pivot of a joint drag: the PARENT joint (the bone rotates about its parent), else the joint itself.</summary>
        private AvPoint DragPivot(int node, AvPoint fallback)
        {
            var proj = Projection;
            int parent = _skeleton.Nodes[node].Parent;
            if (parent >= 0 && parent < _worlds.Length && proj.Project(_worlds[parent].Translation, out var pp)) return pp;
            if (proj.Project(_worlds[node].Translation, out var op)) return op;
            return fallback;
        }

        private void DragRotateBone(AvPoint cur)
        {
            if (_worlds == null || _skeleton == null || _dragNode < 0 || _dragNode >= _worlds.Length) return;
            // atan2 is unstable right at the pivot — wait for some lever arm
            double pdx = cur.X - _pivot.X, pdy = cur.Y - _pivot.Y;
            if (Math.Sqrt(pdx * pdx + pdy * pdy) < 8) { _prevMouse = cur; return; }
            double a0 = Math.Atan2(_prevMouse.Y - _pivot.Y, _prevMouse.X - _pivot.X);
            double a1 = Math.Atan2(cur.Y - _pivot.Y, cur.X - _pivot.X);
            double delta = a1 - a0;
            while (delta > Math.PI) delta -= 2 * Math.PI;
            while (delta < -Math.PI) delta += 2 * Math.PI;
            _prevMouse = cur;
            if (Math.Abs(delta) < 1e-5) return;

            // World rotation about the camera forward axis (into the screen): a positive angle turns clockwise on screen,
            // matching an increasing atan2 angle in y-down screen coordinates.
            var f = Projection.Forward;
            var qA = Quaternion.CreateFromAxisAngle(f, (float)delta);
            // world = Concatenate(local, P); want Concatenate(world, qA) => localDelta = P·qA·P⁻¹ (row-vector convention)
            int parent = _skeleton.Nodes[_dragNode].Parent;
            var qP = Quaternion.Identity;
            if (parent >= 0 && parent < _worlds.Length)
            {
                var pw = _worlds[parent];
                if (_modelScale != 1f && _modelScale > 0f) pw = pw * Matrix4x4.CreateScale(1f / _modelScale);
                qP = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(NormalizeBasis(pw)));
            }
            var localDelta = Quaternion.Concatenate(Quaternion.Concatenate(qP, qA), Quaternion.Inverse(qP));
            BoneRotated?.Invoke(_dragBone, localDelta);
        }

        private static Matrix4x4 NormalizeBasis(Matrix4x4 m)
        {
            var r0 = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
            var r1 = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
            var r2 = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
            return new Matrix4x4(r0.X, r0.Y, r0.Z, 0, r1.X, r1.Y, r1.Z, 0, r2.X, r2.Y, r2.Z, 0, 0, 0, 0, 1);
        }

        // ================================================================ overlay

        protected override void DrawOverlay(DrawingContext ctx, PreviewProjection proj)
        {
            if (_skeleton == null || _worlds == null) return;
            int count = Math.Min(_skeleton.Nodes.Length, _worlds.Length);
            if (_showBones)
            {
                var pts = new AvPoint?[count];
                for (int i = 0; i < count; i++)
                    if (!Hidden(_skeleton.Nodes[i].Name) && proj.Project(_worlds[i].Translation, out var s)) pts[i] = s;
                // parent -> child lines first (hidden pivot nodes collapse onto the nearest visible ancestor)
                for (int i = 0; i < count; i++)
                {
                    if (pts[i] == null) continue;
                    int p = VisibleParent(i);
                    if (p < 0 || p >= count || pts[p] == null) continue;
                    bool sel = _selectedBone != null && _skeleton.Nodes[i].Name == _selectedBone;
                    ctx.DrawLine(sel ? AccentPen : JointPen, pts[p].Value, pts[i].Value);
                }
                for (int i = 0; i < count; i++)
                {
                    if (pts[i] == null) continue;
                    bool sel = _selectedBone != null && _skeleton.Nodes[i].Name == _selectedBone;
                    Diamond(ctx, pts[i].Value, sel ? 8 : 5, sel ? AccentBrush : JointBrush);
                }
            }
            if (ShowSocketAxes && !string.IsNullOrEmpty(_attBone))
            {
                int an = _skeleton.FindNode(_attBone);
                if (an >= 0 && an < count)
                {
                    var m = BoneSocketService.ComposeBoneLocalMatrix(_worlds[an], _attPos, _attRot, 1f);
                    var o = m.Translation;
                    float len = Math.Max(0.02f, (_scene.Bounds != null ? _scene.Bounds[3] : 1f) * 0.12f);
                    Segment(ctx, proj, o, o + Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13)) * len, AxisX);
                    Segment(ctx, proj, o, o + Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23)) * len, AxisY);
                    Segment(ctx, proj, o, o + Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33)) * len, AxisZ);
                }
            }
        }

        private static readonly Pen AxisX = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x58)), 2);
        private static readonly Pen AxisY = new Pen(new SolidColorBrush(Color.FromRgb(0x7C, 0xD6, 0x5C)), 2);
        private static readonly Pen AxisZ = new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0xA9, 0xFF)), 2);

        private int VisibleParent(int i)
        {
            int p = _skeleton.Nodes[i].Parent, guard = 0, n = _skeleton.Nodes.Length;
            while (p >= 0 && p < n && Hidden(_skeleton.Nodes[p].Name) && guard++ < n) p = _skeleton.Nodes[p].Parent;
            return p;
        }
    }
}
