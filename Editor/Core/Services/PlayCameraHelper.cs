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
        /// <summary>Render through the scene's main camera at its CURRENT transform (the live game view),
        /// plus this frame's CameraFX offset (recoil kick, sway) — composed HERE so the camera entity's
        /// transform is never touched by effects.</summary>
        public static void ApplyMainCamera(Scene scene)
        {
            var t = FindMainCamera(scene);
            if (t == null) return;

            var pos = t.LocalPosition;
            var rot = t.LocalRotation;
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

        public static Editor.ECS.Components.Transform FindMainCamera(Scene scene)
        {
            if (scene?.Entities == null) return null;
            foreach (var e in scene.Entities)
            {
                var t = Rec(e);
                if (t != null) return t;
            }
            return null;
        }

        private static Editor.ECS.Components.Transform Rec(GameEntity e)
        {
            if (e == null) return null;
            var cam = e.GetComponent<Editor.ECS.Components.Rendering.Camera>();
            if (cam != null && cam.IsMainCamera && e.Transform != null) return e.Transform;
            if (e.Children != null)
                foreach (var c in e.Children)
                {
                    var t = Rec(c);
                    if (t != null) return t;
                }
            return null;
        }
    }
}
