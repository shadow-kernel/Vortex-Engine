using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>The sky reflection follows the sky's rotation (#446). A scripted texture sky with vertical colour bands
    /// (red around azimuth +Z, blue a quarter turn further, grey elsewhere, dark green below the horizon) is turned by
    /// 90°, so the blue band now sits around +Z. A mirror floor seen from above reflects the sky 60° up towards +Z: it
    /// must show the same blue the sky itself shows when the camera looks along that direction. Without the rotation
    /// in the reflection lookup the mirror showed the unturned red band.</summary>
    internal static class SkyReflectionSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("sky reflection rotation", Run);

        private static bool Blue((int r, int g, int b) c) => c.b > c.r + 30 && c.b > c.g + 30;

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("sky reflection rotation: no scene / project — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var prevSky = SceneRenderService.ScriptSky;
            GameEntity floor = null;
            string texRel = "Assets/Materials/SmokeSkyBands.png", matRel = "Assets/Materials/SmokeMirror.vmat";
            try
            {
                CameraSkySmoke.WritePng(Path.Combine(project, texRel), 256, 128, (x, y) =>
                {
                    if (y >= 64) return ((byte)30, (byte)90, (byte)30, (byte)255);   // below the horizon
                    float u = (x + 0.5f) / 256f;
                    if (u >= 0.375f && u < 0.625f) return ((byte)230, (byte)20, (byte)20, (byte)255);   // around +Z unturned
                    if (u >= 0.625f && u < 0.875f) return ((byte)20, (byte)20, (byte)230, (byte)255);   // a quarter turn on
                    return ((byte)70, (byte)70, (byte)70, (byte)255);
                });
                new VortexMaterial { ShaderType = "StandardPBR", BaseColor = new[] { 1f, 1f, 1f, 1f }, Metallic = 1f, Roughness = 0.02f }
                    .Save(Path.Combine(project, matRel));
                SceneRenderService.ScriptSky = new SceneRenderService.SkyOverride { TexturePath = texRel, Exposure = 1f, RotationDeg = 90f };

                floor = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (floor?.Transform == null) { log.LogError("sky reflection rotation: no floor"); return false; }
                floor.Name = "SmokeMirrorFloor";
                floor.Transform.LocalPosition = new Vector3(0, 492, 10);
                floor.Transform.LocalScale = new Vector3(80, 0.2f, 80);
                var mr = floor.GetComponent<MeshRenderer>();
                if (mr != null) mr.MaterialPath = matRel;

                SceneRenderService.HideEditorOverlays = true;
                cam.SetPositionAndRotation(0, 500, 0, 0, 60);    // 60° down onto the mirror: it reflects the sky 60° up along +Z
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(1500);                 // the reflection map is baked off the new sky
                var mirror = await CameraSkySmoke.Sample("sky_reflection_mirror.bmp", 0.5, 0.5);
                cam.SetPositionAndRotation(0, 500, 0, 0, -60);   // the sky itself in that direction
                EditorViewportSession.RequestResubmit();
                var sky = await CameraSkySmoke.Sample("sky_reflection_sky.bmp", 0.5, 0.5);
                log.Log("sky reflection rotation: sky turned 90° — mirror reflects " + CameraSkySmoke.Rgb(mirror) + ", the sky in that direction " + CameraSkySmoke.Rgb(sky));
                bool skyTurned = Blue(sky);
                bool mirrorTurned = Blue(mirror);
                if (!skyTurned) log.LogError("sky reflection rotation: the scripted sky's rotation does not turn the visible sky");
                if (!mirrorTurned) log.LogError("sky reflection rotation: the reflection shows the unturned sky — the sky rotation is missing from the reflection lookup (#446)");
                return skyTurned && mirrorTurned;
            }
            finally
            {
                SceneRenderService.ScriptSky = prevSky;
                SceneRenderService.HideEditorOverlays = false;
                if (floor != null) EditorCommands.DeleteEntities(new List<GameEntity> { floor });
                foreach (var f in new[] { texRel, matRel }) { try { File.Delete(Path.Combine(project, f)); } catch { } }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
