using System;
using System.Runtime.InteropServices;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Viewport
{
    /// <summary>
    /// A secondary camera view rendered through an engine off-screen target with CPU readback (split/quad
    /// layouts, camera preview). Framework-free: the shell copies the BGRA pixels into its own bitmap.
    /// Throttled and dirty-checked so idle views cost nothing.
    /// </summary>
    public sealed class SecondaryViewSession
    {
        private uint _target;
        private int _w, _h;
        private GameEntity _camera;
        private bool _editorCamera = true;
        private DateTime _last = DateTime.MinValue;
        private float _lx, _ly, _lz, _lyaw, _lpitch, _lfov;
        private bool _first = true;
        private byte[] _pixels;

        public int RenderIntervalMs { get; set; } = 100;
        public bool RenderGrid { get; set; }

        public void SetCamera(GameEntity cameraEntity, bool editorCamera)
        {
            _camera = cameraEntity;
            _editorCamera = editorCamera || cameraEntity == null;
            _first = true;
        }

        public void Resize(int w, int h) { if (w > 0 && h > 0 && (w != _w || h != _h)) { _w = w; _h = h; _first = true; } }

        private bool EnsureTarget(int w, int h)
        {
            if (w <= 0 || h <= 0) return false;
            if (_target != 0 && (_w != w || _h != h))
            {
                if (!VortexAPI.ResizeSecondaryRenderTarget(_target, (uint)w, (uint)h)) { VortexAPI.DestroySecondaryRenderTarget(_target); _target = 0; }
            }
            _w = w; _h = h;
            if (_target == 0) _target = VortexAPI.CreateSecondaryRenderTarget((uint)w, (uint)h);
            return _target != 0;
        }

        private bool CameraMoved()
        {
            float x, y, z, yaw, pitch, fov;
            if (_editorCamera)
            {
                var ec = EditorCameraController.Instance;
                x = ec.PositionX; y = ec.PositionY; z = ec.PositionZ; yaw = ec.Yaw; pitch = ec.Pitch; fov = RaycastService.EditorFovYDegrees;
            }
            else
            {
                var t = _camera?.Transform; var c = _camera?.GetComponent<Camera>();
                if (t == null || c == null) return false;
                x = t.LocalPosition.X; y = t.LocalPosition.Y; z = t.LocalPosition.Z; yaw = t.LocalRotation.Y; pitch = t.LocalRotation.X; fov = c.FieldOfView;
            }
            bool moved = _first || x != _lx || y != _ly || z != _lz || yaw != _lyaw || pitch != _lpitch || fov != _lfov;
            _lx = x; _ly = y; _lz = z; _lyaw = yaw; _lpitch = pitch; _lfov = fov;
            return moved;
        }

        /// <summary>Render + read back when due. Returns false when nothing new is available.</summary>
        public bool RenderIfNeeded(int w, int h, out byte[] pixels, out int pw, out int ph, out int pitch)
        {
            pixels = null; pw = ph = pitch = 0;
            if (EditorViewportSession.Main == null || !EditorViewportSession.Main.IsInitialized) return false;
            if (!EnsureTarget(w, h)) return false;
            var now = DateTime.Now;
            bool moved = CameraMoved() || SceneRenderService.RuntimeDirty || PlayModeService.Instance.IsPlaying;
            if (!_first && !moved) return false;
            if (!_first && (now - _last).TotalMilliseconds < RenderIntervalMs) return false;
            _last = now; _first = false;

            VortexAPI.ViewportCameraDesc desc;
            if (_editorCamera)
            {
                var ec = EditorCameraController.Instance;
                float yawRad = ec.Yaw * (float)Math.PI / 180f, pitchRad = ec.Pitch * (float)Math.PI / 180f;
                float fx = (float)(Math.Sin(yawRad) * Math.Cos(pitchRad)), fy = (float)(-Math.Sin(pitchRad)), fz = (float)(Math.Cos(yawRad) * Math.Cos(pitchRad));
                desc = VortexAPI.ViewportCameraDesc.CreatePerspective(ec.PositionX, ec.PositionY, ec.PositionZ, ec.PositionX + fx, ec.PositionY + fy, ec.PositionZ + fz, 0, 1, 0, RaycastService.EditorFovYDegrees, 0.1f, 1000f);
            }
            else
            {
                var t = _camera?.Transform; var c = _camera?.GetComponent<Camera>();
                if (t == null || c == null) return false;
                var pos = t.LocalPosition; var rot = t.LocalRotation;
                float yawRad = rot.Y * (float)Math.PI / 180f, pitchRad = rot.X * (float)Math.PI / 180f;
                float fx = (float)(Math.Cos(pitchRad) * Math.Sin(yawRad)), fy = (float)(-Math.Sin(pitchRad)), fz = (float)(Math.Cos(pitchRad) * Math.Cos(yawRad));
                desc = c.Projection == CameraProjection.Orthographic
                    ? VortexAPI.ViewportCameraDesc.CreateOrthographic(pos.X, pos.Y, pos.Z, pos.X + fx, pos.Y + fy, pos.Z + fz, 0, 1, 0, c.OrthographicSize, c.NearClip, c.FarClip)
                    : VortexAPI.ViewportCameraDesc.CreatePerspective(pos.X, pos.Y, pos.Z, pos.X + fx, pos.Y + fy, pos.Z + fz, 0, 1, 0, c.FieldOfView, c.NearClip, c.FarClip);
            }
            VortexAPI.RenderToSecondaryTarget(_target, desc, RenderGrid, false);
            if (!VortexAPI.HasSecondaryRenderTarget(_target) || !VortexAPI.PrepareSecondaryRenderTargetReadback(_target)) return false;
            IntPtr data = VortexAPI.ReadSecondaryRenderTargetPixels(_target, out uint ow, out uint oh, out uint rowPitch);
            if (data == IntPtr.Zero) return false;
            try
            {
                int need = (int)(oh * rowPitch);
                if (_pixels == null || _pixels.Length < need) _pixels = new byte[need];
                Marshal.Copy(data, _pixels, 0, need);
                pixels = _pixels; pw = (int)ow; ph = (int)oh; pitch = (int)rowPitch;
                return true;
            }
            finally { VortexAPI.ReleaseSecondaryRenderTargetPixels(_target); }
        }

        public void Shutdown()
        {
            if (_target != 0) { try { VortexAPI.DestroySecondaryRenderTarget(_target); } catch { } _target = 0; }
        }
    }
}
