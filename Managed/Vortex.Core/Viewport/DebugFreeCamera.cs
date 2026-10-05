using System;
using Editor.Core.Data;
using Editor.Core.Input;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.Scripting;

namespace Editor.Core.Viewport
{
    /// <summary>
    /// The in-game debug free-fly camera (development builds only; never available in a shipped Release):
    /// P detaches the render camera from the player so you can fly around (WASD/QE, Shift = faster, mouse look)
    /// and look at the character from outside. While it is active the scene renders like any OTHER camera sees
    /// the player - the third-person body (render layer 2) shows, the first-person viewmodel (layer 1) hides.
    /// CAPS LOCK hands all input (mouse + keys) back to the player and freezes the camera, so you can walk,
    /// fire and reload while watching yourself. Shared by the editor viewport and the standalone player.
    /// <c>VM_DBGCAM="x,y,z,yawDeg,pitchDeg"</c> enters it at a fixed vantage on start (deterministic captures);
    /// <c>VM_DBGCAM_DRIVE=1</c> then hands the input (an input script) to the player instead of the camera.
    /// </summary>
    public sealed class DebugFreeCamera
    {
        private bool _active, _seeded, _pPrev, _drivePlayer, _envChecked, _envDrive;
        private float _x, _y, _z, _yaw, _pitch;

        public bool Active => _active;
        public bool DrivingPlayer => _active && _drivePlayer;

        public void Reset()
        {
            _active = false; _seeded = false; _drivePlayer = false; _pPrev = false;
            ScriptRuntime.SuppressGameplayInput = false;
            if (SceneRenderService.DebugThirdPersonView) { SceneRenderService.DebugThirdPersonView = false; SceneRenderService.RuntimeDirty = true; }
        }

        /// <summary>Run BEFORE the gameplay scripts: toggles on P, reads Caps Lock, and - while flying - consumes the
        /// mouse delta so the scripts do not turn the player. <paramref name="allowed"/> = playing and not a release
        /// build; <paramref name="inputActive"/> = the host window has focus / the mouse is captured.</summary>
        public void PreScripts(Scene scene, bool allowed, bool inputActive)
        {
            if (!allowed)
            {
                if (_active) Reset();
                return;
            }
            if (!_envChecked)
            {
                _envChecked = true;
                string seed = Environment.GetEnvironmentVariable("VM_DBGCAM");
                if (!string.IsNullOrEmpty(seed))
                {
                    var t = seed.Split(',');
                    float F(int i, float d) { float v; return t.Length > i && float.TryParse(t[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : d; }
                    _x = F(0, 0f); _y = F(1, 1.5f); _z = F(2, 0f);
                    _yaw = F(3, 0f) * (float)Math.PI / 180f; _pitch = F(4, 0f) * (float)Math.PI / 180f;
                    _active = true; _seeded = true;
                    SceneRenderService.DebugThirdPersonView = true; SceneRenderService.RuntimeDirty = true;
                    SceneRenderService.DebugShowViewmodel = Environment.GetEnvironmentVariable("VM_DBGCAM_FP") == "1";   // inspect the FP arms instead of the body
                    _envDrive = Environment.GetEnvironmentVariable("VM_DBGCAM_DRIVE") == "1";   // captures: the input script drives the player
                }
            }

            bool pDown = inputActive && HostInput.IsKeyDown(0x50);   // P
            if (pDown && !_pPrev)
            {
                _active = !_active;
                if (_active && !_seeded) SeedFromMainCamera(scene);
                SceneRenderService.DebugThirdPersonView = _active;
                SceneRenderService.RuntimeDirty = true;   // rebuild the retained queue with the new layer decision
            }
            _pPrev = pDown;

            _drivePlayer = _active && (HostInput.IsCapsLockOn() || _envDrive);
            ScriptRuntime.SuppressGameplayInput = _active && !_drivePlayer;
            if (!_active || _drivePlayer) return;

            _yaw += Vortex.Input.MouseDeltaX * 0.0035f;
            _pitch += Vortex.Input.MouseDeltaY * 0.0035f;
            Vortex.Input.MouseDeltaX = 0f; Vortex.Input.MouseDeltaY = 0f;
            if (_pitch > 1.5f) _pitch = 1.5f; else if (_pitch < -1.5f) _pitch = -1.5f;
        }

        private void SeedFromMainCamera(Scene scene)
        {
            var t = PlayCameraHelper.FindMainCamera(scene ?? ProjectData.Current?.ActiveScene);
            if (t != null)
            {
                var p = t.LocalPosition; var r = t.LocalRotation;
                _x = p.X; _y = p.Y; _z = p.Z;
                _yaw = r.Y * (float)Math.PI / 180f; _pitch = r.X * (float)Math.PI / 180f;
            }
            _seeded = true;
        }

        /// <summary>Run AFTER the scripts: flies the camera (unless the player is being driven) and sets the render
        /// camera. Returns false when inactive so the caller applies the game's main camera instead.</summary>
        public bool ApplyView(float dt)
        {
            if (!_active) return false;
            double cyp = Math.Cos(_pitch), syp = Math.Sin(_pitch), cya = Math.Cos(_yaw), sya = Math.Sin(_yaw);
            float fx = (float)(sya * cyp), fy = (float)(-syp), fz = (float)(cya * cyp);
            float rx = (float)cya, rz = (float)(-sya);
            if (!_drivePlayer)
            {
                float sp = (HostInput.IsKeyDown(0x10) || HostInput.IsKeyDown(0xA0) ? 12f : 4f) * dt;   // Shift = faster
                if (HostInput.IsKeyDown(0x57)) { _x += fx * sp; _y += fy * sp; _z += fz * sp; }
                if (HostInput.IsKeyDown(0x53)) { _x -= fx * sp; _y -= fy * sp; _z -= fz * sp; }
                if (HostInput.IsKeyDown(0x44)) { _x += rx * sp; _z += rz * sp; }
                if (HostInput.IsKeyDown(0x41)) { _x -= rx * sp; _z -= rz * sp; }
                if (HostInput.IsKeyDown(0x45)) _y += sp;
                if (HostInput.IsKeyDown(0x51)) _y -= sp;
            }
            VortexAPI.SetViewCamera(_x, _y, _z, _x + fx, _y + fy, _z + fz, 0f, 1f, 0f);
            SceneRenderService.RuntimeDirty = true;
            return true;
        }

        /// <summary>The one-line hint at the bottom of the game view (development builds).</summary>
        public void DrawHint(float viewWidth, float viewHeight)
        {
            if (viewWidth < 2 || viewHeight < 2) return;
            if (_active)
            {
                string hud = _drivePlayer
                    ? "DEBUG CAM (frozen)  ·  CAPS LOCK ON = driving the PLAYER (mouse + keys) · CAPS off = fly · P to exit"
                    : "DEBUG CAM (3rd person)  ·  WASD/QE fly · Shift faster · mouse look · CAPS LOCK = drive the player · P to exit";
                VortexAPI.UIText(16, viewHeight - 28, 1000, 22, hud, 13, 1.0f, 0.6f, 0.12f, 1f, 0, 600);
            }
            else VortexAPI.UIText(16, viewHeight - 26, 520, 20, "P = DEBUG CAM (see yourself in 3rd person)", 12, 1.0f, 0.62f, 0.2f, 0.85f, 0, 400);
        }
    }
}
