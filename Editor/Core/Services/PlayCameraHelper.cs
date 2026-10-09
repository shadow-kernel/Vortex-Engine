using Editor.Core.Data;
using Editor.DllWrapper;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Applies a play camera view to the renderer. Shared by the editor play tick (in-viewport play)
    /// and the standalone game window so both render through the scene's main camera consistently.
    /// </summary>
    public static class PlayCameraHelper
    {
        private static float _appliedFov = float.NaN, _appliedNear = float.NaN, _appliedFar = float.NaN;
        private static bool _wasPlaying;

        /// <summary>Forget what was pushed to the renderer, so the next <see cref="ApplyMainCamera"/> pushes the
        /// camera's FOV and clip planes again (play start; the editor view re-asserts its own FOV in between).</summary>
        public static void ResetApplied() { _appliedFov = _appliedNear = _appliedFar = float.NaN; }

        /// <summary>Render through the scene's main camera at its CURRENT transform (the live game view),
        /// plus this frame's CameraFX offset (recoil kick, sway) — composed HERE so the camera entity's
        /// transform is never touched by effects. A camera under a rig renders from its WORLD transform, and its
        /// FOV / near / far drive the view's projection (#327).</summary>
        public static void ApplyMainCamera(Scene scene)
        {
            if (!TryGetMainCameraWorld(scene, out var pos, out var rot, out var cam)) return;

            // The projection is the camera component's: pushed when its values change and once per play session,
            // not every frame — a script's Camera.SetFov (ADS zoom) must survive. Edit-mode game view keeps the
            // editor's FOV as before.
            bool playing = PlayModeService.Instance.State == PlayState.Playing;
            if (playing && !_wasPlaying) ResetApplied();
            _wasPlaying = playing;
            if (cam != null && playing)
            {
                if (cam.FieldOfView != _appliedFov) { VortexAPI.SetViewFOV(cam.FieldOfView); _appliedFov = cam.FieldOfView; }
                if (cam.NearClip != _appliedNear || cam.FarClip != _appliedFar)
                {
                    VortexAPI.SetViewClipPlanes(cam.NearClip, cam.FarClip);
                    _appliedNear = cam.NearClip; _appliedFar = cam.FarClip;
                }
            }

            if (CameraFXService.Instance.TryGetCameraOffset(out var fxPos, out var fxRot))
            {
                // Positional kick is camera-relative: rotate the offset into the camera's yaw/pitch frame
                // so Kick(pos: (0,0,-0.05)) always means "5 cm back", whatever the player faces.
                double yaw0 = rot.Y * System.Math.PI / 180.0, pitch0 = rot.X * System.Math.PI / 180.0;
                float cy = (float)System.Math.Cos(yaw0), sy = (float)System.Math.Sin(yaw0);
                float cp = (float)System.Math.Cos(pitch0), sp = (float)System.Math.Sin(pitch0);
                var fwd = new ECS.Vector3(sy * cp, -sp, cy * cp);
                var right = new ECS.Vector3(cy, 0f, -sy);
                var up = new ECS.Vector3(sy * sp, cp, cy * sp);
                pos = new ECS.Vector3(
                    pos.X + right.X * fxPos.X + up.X * fxPos.Y + fwd.X * fxPos.Z,
                    pos.Y + right.Y * fxPos.X + up.Y * fxPos.Y + fwd.Y * fxPos.Z,
                    pos.Z + right.Z * fxPos.X + up.Z * fxPos.Y + fwd.Z * fxPos.Z);
                rot = new ECS.Vector3(rot.X + fxRot.X, rot.Y + fxRot.Y, rot.Z + fxRot.Z);
            }
            ApplyPose(pos, rot);
        }

        /// <summary>Render from an explicit pose (used to freeze the editor viewport as a placeholder
        /// while the game runs in the external window). eulerDeg.Z rolls the camera (CameraFX kick).</summary>
        public static void ApplyPose(ECS.Vector3 pos, ECS.Vector3 eulerDeg)
        {
            float pitchDeg = eulerDeg.X;
            if (pitchDeg > 89f) pitchDeg = 89f; else if (pitchDeg < -89f) pitchDeg = -89f;
            double yaw = eulerDeg.Y * System.Math.PI / 180.0;
            double pitch = pitchDeg * System.Math.PI / 180.0;
            float fx = (float)(System.Math.Sin(yaw) * System.Math.Cos(pitch));
            float fy = (float)(-System.Math.Sin(pitch));
            float fz = (float)(System.Math.Cos(yaw) * System.Math.Cos(pitch));

            // Roll: rotate the up vector around the forward axis (Rodrigues). Zero roll = exactly the old path.
            float ux = 0f, uy = 1f, uz = 0f;
            if (eulerDeg.Z > 0.0001f || eulerDeg.Z < -0.0001f)
            {
                double roll = eulerDeg.Z * System.Math.PI / 180.0;
                float cr = (float)System.Math.Cos(roll), sr = (float)System.Math.Sin(roll);
                // up' = up*cos + (f x up)*sin + f*(f.up)*(1-cos); f.up = fy here
                float cxx = fy * uz - fz * uy, cxy = fz * ux - fx * uz, cxz = fx * uy - fy * ux;
                float d = fy;
                ux = ux * cr + cxx * sr + fx * d * (1f - cr);
                uy = uy * cr + cxy * sr + fy * d * (1f - cr);
                uz = uz * cr + cxz * sr + fz * d * (1f - cr);
            }
            VortexAPI.SetViewCamera(pos.X, pos.Y, pos.Z, pos.X + fx, pos.Y + fy, pos.Z + fz, ux, uy, uz);
        }

        public static Editor.ECS.Components.Transform FindMainCamera(Scene scene) => FindMainCameraEntity(scene)?.Transform;

        /// <summary>The entity carrying the scene's main camera (first in hierarchy order), or null.</summary>
        public static GameEntity FindMainCameraEntity(Scene scene)
        {
            if (scene?.Entities == null) return null;
            foreach (var e in scene.Entities)
            {
                var found = Rec(e);
                if (found != null) return found;
            }
            return null;
        }

        private static GameEntity Rec(GameEntity e)
        {
            if (e == null) return null;
            var cam = e.GetComponent<Editor.ECS.Components.Rendering.Camera>();
            if (cam != null && cam.IsMainCamera && e.Transform != null) return e;
            if (e.Children != null)
                foreach (var c in e.Children)
                {
                    var found = Rec(c);
                    if (found != null) return found;
                }
            return null;
        }

        /// <summary>The main camera's WORLD position and rotation (Euler degrees: pitch, yaw, roll) plus its Camera
        /// component. A camera under a rig composes its parent chain (#327); an unparented one returns its local
        /// values unchanged. False without a main camera.</summary>
        public static bool TryGetMainCameraWorld(Scene scene, out ECS.Vector3 pos, out ECS.Vector3 eulerDeg,
            out Editor.ECS.Components.Rendering.Camera cam)
        {
            pos = default; eulerDeg = default; cam = null;
            var entity = FindMainCameraEntity(scene);
            var t = entity?.Transform;
            if (t == null) return false;
            cam = entity.GetComponent<Editor.ECS.Components.Rendering.Camera>();
            if (entity.Parent == null || entity.Parent.Transform == null) { pos = t.LocalPosition; eulerDeg = t.LocalRotation; return true; }
            WorldPose(entity, out pos, out eulerDeg);
            return true;
        }

        /// <summary>World position + Euler rotation of an entity from its parent chain (row-major S·R·T matrices,
        /// child first, as <see cref="SceneRenderService.LocalMatrixInto"/> builds them). The angles invert that
        /// composition — r21 = −sin(pitch), r20/r22 give the yaw, r01/r11 the roll — so an unparented entity gets
        /// its own values back.</summary>
        public static void WorldPose(GameEntity entity, out ECS.Vector3 pos, out ECS.Vector3 eulerDeg)
        {
            var m = new float[16]; var local = new float[16]; var tmp = new float[16];
            SceneRenderService.LocalMatrixInto(entity.Transform, m);
            for (var p = entity.Parent; p != null; p = p.Parent)
            {
                if (p.Transform == null) continue;
                SceneRenderService.LocalMatrixInto(p.Transform, local);
                SceneRenderService.MultiplyInto(m, local, tmp);
                var s = m; m = tmp; tmp = s;
            }
            pos = new ECS.Vector3(m[12], m[13], m[14]);
            float l0 = Len(m[0], m[1], m[2]), l1 = Len(m[4], m[5], m[6]), l2 = Len(m[8], m[9], m[10]);
            if (l0 < 1e-6f) l0 = 1f;
            if (l1 < 1e-6f) l1 = 1f;
            if (l2 < 1e-6f) l2 = 1f;
            float r01 = m[1] / l0, r11 = m[5] / l1, r20 = m[8] / l2, r21 = m[9] / l2, r22 = m[10] / l2;
            const float toDeg = (float)(180.0 / System.Math.PI);
            float pitch = (float)System.Math.Asin(System.Math.Max(-1.0, System.Math.Min(1.0, -r21))) * toDeg;
            float yaw = (float)System.Math.Atan2(r20, r22) * toDeg;
            float roll = (float)System.Math.Atan2(r01, r11) * toDeg;
            eulerDeg = new ECS.Vector3(pitch, yaw, roll);
        }

        private static float Len(float x, float y, float z) => (float)System.Math.Sqrt(x * x + y * y + z * z);
    }
}
