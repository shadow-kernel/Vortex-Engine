using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Lighting;

namespace VortexEditor.Shell
{
    /// <summary>The light budget (#416): 40 equal point lamps along a road, and the editor camera driven past them at 30 m/s
    /// without a single scene re-submit. The renderer has to follow the camera on every frame — it used to choose from the
    /// list it had cut at the last submit, so the choice froze until something changed in the scene — keep exactly 16 lamps in
    /// the slots, and change its choice gradually: never more than one light in and one out per frame. Runs on the real
    /// backend (Metal here, DX12 on Windows).</summary>
    internal static class LightBudgetSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("light budget", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            if (ProjectData.Current?.ActiveScene == null) { log.Log("light budget: no scene — skipped"); return true; }
            if (!VortexAPI.TryGetLightBudgetStats(out _, out _, out _, out _, out _)) { log.Log("light budget: the engine library has no light stats — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var lamps = new List<GameEntity>();
            try
            {
                // the road, high above the level so nothing else lights it
                for (int i = 0; i < 40; i++)
                {
                    var e = EditorCommands.CreateLight(LightType.Point);
                    var l = e?.GetComponent<Light>();
                    if (e == null || l == null || e.Transform == null) { log.LogError("light budget: could not create a lamp"); return false; }
                    lamps.Add(e);
                    e.Name = "SmokeLamp" + i;
                    e.Transform.LocalPosition = new Vector3(4f, 1005f, 10f * i);
                    l.Intensity = 10f; l.Range = 15f; l.ColorR = 1f; l.ColorG = 0.9f; l.ColorB = 0.7f;
                    l.ShadowType = ShadowType.None;
                }
                SceneRenderService.HideEditorOverlays = true;
                cam.SetPositionAndRotation(0f, 1002f, -20f, 0f, 5f);
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(600);

                // drive: the camera moves, the scene does not change — nothing re-submits the lights. 30 m/s, but never more than
                // 2 m between two samples: a hitch (or a slow software renderer) must not turn into a jump the renderer takes for
                // a camera cut, and the drive ends after 350 m, so a slow runner still passes enough lamps
                int samples = 0, mostShown = 0, fewestShown = int.MaxValue, mostChanges = 0, changes = 0, submitted = 0;
                float z = -20f;
                var start = DateTime.UtcNow;
                var last = start;
                while (z < 330f && (DateTime.UtcNow - start).TotalSeconds < 30.0)
                {
                    var now = DateTime.UtcNow;
                    z += Math.Min(30f * (float)(now - last).TotalSeconds, 2f);
                    last = now;
                    cam.SetPositionAndRotation(0f, 1002f, z, 0f, 5f);
                    await Task.Delay(16);
                    VortexAPI.TryGetLightBudgetStats(out submitted, out _, out int shown, out _, out int changed);
                    samples++;
                    mostShown = Math.Max(mostShown, shown);
                    fewestShown = Math.Min(fewestShown, shown);
                    mostChanges = Math.Max(mostChanges, changed);
                    changes += changed;
                }
                float driven = z + 20f;
                log.Log("light budget: " + samples + " samples while driving " + (int)driven + " m past " + submitted + " point lights — " + fewestShown + ".." + mostShown
                    + " held a slot, " + changes + " slot changes seen, at most " + mostChanges + " in one frame");
                bool ok = true;
                if (mostShown > 16 || fewestShown < 16) { log.LogError("light budget: the slots were not exactly 16 lamps (" + fewestShown + ".." + mostShown + ")"); ok = false; }
                if (driven >= 60f && changes < 4) { log.LogError("light budget: the choice did not follow the moving camera (" + changes + " changes) — frozen between scene submits?"); ok = false; }
                if (mostChanges > 2) { log.LogError("light budget: " + mostChanges + " slot changes in one frame — the choice jumps instead of changing gradually"); ok = false; }
                return ok;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                if (lamps.Count > 0) EditorCommands.DeleteEntities(lamps);
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(200);
            }
        }
    }
}
