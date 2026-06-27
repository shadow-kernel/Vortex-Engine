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
        /// <summary>Render through the scene's main camera at its CURRENT transform (the live game view).</summary>
        public static void ApplyMainCamera(Scene scene)
        {
            var t = FindMainCamera(scene);
            if (t != null) ApplyPose(t.LocalPosition, t.LocalRotation);
        }

        /// <summary>Render from an explicit pose (used to freeze the editor viewport as a placeholder
        /// while the game runs in the external window).</summary>
        public static void ApplyPose(ECS.Vector3 pos, ECS.Vector3 eulerDeg)
        {
            float pitchDeg = eulerDeg.X;
            if (pitchDeg > 89f) pitchDeg = 89f; else if (pitchDeg < -89f) pitchDeg = -89f;
            double yaw = eulerDeg.Y * System.Math.PI / 180.0;
            double pitch = pitchDeg * System.Math.PI / 180.0;
            float fx = (float)(System.Math.Sin(yaw) * System.Math.Cos(pitch));
            float fy = (float)(-System.Math.Sin(pitch));
            float fz = (float)(System.Math.Cos(yaw) * System.Math.Cos(pitch));
            VortexAPI.SetViewCamera(pos.X, pos.Y, pos.Z, pos.X + fx, pos.Y + fy, pos.Z + fz, 0f, 1f, 0f);
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
