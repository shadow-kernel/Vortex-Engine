using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>The sky's cross-fade (#464), on the rendered picture so the DX12 (WARP) smoke covers the HLSL path too. With no
    /// sun, high above the scene, three things are sampled: the sky straight up, a mirror floor (the reflection map) and a
    /// white cube lit only by the sky light (SH9). Sky.SetTexture(blue, fade 4 s) from a red picture: the first frame is
    /// still red (no pop), half way all three are a mix of red and blue, at the end blue. A gradient fades into a picture the
    /// same way, and Sky.SetBlend(red, blue, 0.5) holds the half-way mix. A fade to a picture that cannot be loaded leaves it
    /// out: three quarters through, the sky, its light and its reflection are still red, not fading towards black. The fade
    /// runs on game time; the check advances it with the runtime's own tick.</summary>
    internal static class SkyFadeSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("sky crossfade", Run);

        private static bool Reddish((int r, int g, int b) c) => c.r > c.b + 30;
        private static int Lum((int r, int g, int b) c) => (c.r * 2126 + c.g * 7152 + c.b * 722) / 10000;
        private static bool Bluish((int r, int g, int b) c) => c.b > c.r + 30;
        private static bool Mixed((int r, int g, int b) c, (int r, int g, int b) a, (int r, int g, int b) b)
            => c.r < a.r - 15 && c.r > b.r + 15 && c.b > a.b + 15 && c.b < b.b - 15;   // strictly between the two ends in red and blue

        private static async Task<((int r, int g, int b) sky, (int r, int g, int b) mirror, (int r, int g, int b) cube)> Probe(EditorCameraController cam, string tag)
        {
            // twice through the submit: the blended reflection map is baked on a worker and shown by the next one
            EditorViewportSession.RequestResubmit();
            await SmokeRegistry.Settle(400);
            EditorViewportSession.RequestResubmit();
            cam.SetPositionAndRotation(0, 500, 0, 0, -70);   // the sky
            var sky = await CameraSkySmoke.Centre("sky_fade_" + tag + "_sky.bmp");
            cam.SetPositionAndRotation(0, 500, 0, 0, 60);    // the mirror floor: it reflects the sky 60° up
            EditorViewportSession.RequestResubmit();
            var mirror = await CameraSkySmoke.Centre("sky_fade_" + tag + "_mirror.bmp");
            cam.SetPositionAndRotation(0, 500, 0, 0, 0);     // the cube's face: the sky light only
            EditorViewportSession.RequestResubmit();
            var cube = await CameraSkySmoke.Centre("sky_fade_" + tag + "_cube.bmp");
            return (sky, mirror, cube);
        }

        private static string Rgb3(((int r, int g, int b) sky, (int r, int g, int b) mirror, (int r, int g, int b) cube) p)
            => "sky " + CameraSkySmoke.Rgb(p.sky) + " mirror " + CameraSkySmoke.Rgb(p.mirror) + " cube " + CameraSkySmoke.Rgb(p.cube);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("sky crossfade: no scene / project — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            // no sun: the cube is lit by the sky light alone (the scene's lights stay, so the default sun stays off)
            var suns = VortexEditor.Claude.SceneModel.All(scene).Select(e => e.GetComponent<Light>()).Where(l => l != null && l.IsEnabled && l.LightType == LightType.Directional).ToList();
            var prevSky = SceneRenderService.ScriptSky;
            string redRel = "Assets/Textures/SmokeFadeRed.png", blueRel = "Assets/Textures/SmokeFadeBlue.png";
            string mirrorRel = "Assets/Materials/SmokeFadeMirror.vmat", matteRel = "Assets/Materials/SmokeFadeMatte.vmat";
            try
            {
                foreach (var l in suns) l.IsEnabled = false;
                CameraSkySmoke.WritePng(Path.Combine(project, redRel), 64, 32, (x, y) => ((byte)230, (byte)30, (byte)30, (byte)255));
                CameraSkySmoke.WritePng(Path.Combine(project, blueRel), 64, 32, (x, y) => ((byte)30, (byte)30, (byte)230, (byte)255));
                new VortexMaterial { ShaderType = "StandardPBR", BaseColor = new[] { 1f, 1f, 1f, 1f }, Metallic = 1f, Roughness = 0.02f }.Save(Path.Combine(project, mirrorRel));
                new VortexMaterial { ShaderType = "StandardPBR", BaseColor = new[] { 1f, 1f, 1f, 1f }, Metallic = 0f, Roughness = 1f }.Save(Path.Combine(project, matteRel));
                var floor = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                var cube = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (floor?.Transform == null || cube?.Transform == null) { log.LogError("sky crossfade: could not build the test scene"); return false; }
                made.Add(floor); made.Add(cube);
                floor.Name = "SmokeFadeMirror"; cube.Name = "SmokeFadeCube";
                floor.Transform.LocalPosition = new Vector3(0, 492, 10);
                floor.Transform.LocalScale = new Vector3(80, 0.2f, 80);
                cube.Transform.LocalPosition = new Vector3(0, 500, 12);
                cube.Transform.LocalScale = new Vector3(6, 6, 1);
                var fmr = floor.GetComponent<MeshRenderer>(); if (fmr != null) fmr.MaterialPath = mirrorRel;
                var cmr = cube.GetComponent<MeshRenderer>(); if (cmr != null) cmr.MaterialPath = matteRel;
                SceneRenderService.HideEditorOverlays = true;

                // red, then a 4 s fade to blue
                Vortex.Sky.SetTexture(redRel);
                var red = await Probe(cam, "red");
                Vortex.Sky.SetTexture(blueRel, 1f, 0f, 4f);
                float p0 = Vortex.Sky.FadeProgress;
                var start = await Probe(cam, "start");
                SceneRenderService.TickSkyFade(2f);   // the script runtime's tick, 2 s of game time
                float p1 = Vortex.Sky.FadeProgress;
                var half = await Probe(cam, "half");
                SceneRenderService.TickSkyFade(2.5f);
                float p2 = Vortex.Sky.FadeProgress;
                var blue = await Probe(cam, "blue");

                // a gradient into a picture, and a blend the script holds
                Vortex.Sky.SetGradient(0.1f, 0.7f, 0.1f, 0.1f, 0.7f, 0.1f, 0.1f, 0.7f, 0.1f);
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(300);
                Vortex.Sky.SetTexture(blueRel, 1f, 0f, 4f);
                SceneRenderService.TickSkyFade(2f);
                cam.SetPositionAndRotation(0, 500, 0, 0, -70);
                EditorViewportSession.RequestResubmit();
                var gradHalf = await CameraSkySmoke.Centre("sky_fade_gradient_half.bmp");
                Vortex.Sky.SetBlend(redRel, blueRel, 0.5f);
                var held = await Probe(cam, "blend");
                float p3 = Vortex.Sky.FadeProgress;

                // a fade to a picture that cannot be loaded: it leaves the blend (with a warning), so three quarters through the
                // sky, its light and its reflection are still the red one's instead of fading towards black
                Vortex.Sky.SetTexture(redRel);
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(300);
                Vortex.Sky.SetTexture("Assets/Textures/SmokeFadeMissing.png", 1f, 0f, 4f);
                SceneRenderService.TickSkyFade(3f);
                var missing = await Probe(cam, "missing");

                log.Log("sky crossfade: red " + Rgb3(red) + " | fade start (progress " + p0.ToString("0.00") + ") " + Rgb3(start)
                    + " | half (" + p1.ToString("0.00") + ") " + Rgb3(half) + " | end (" + p2.ToString("0.00") + ") " + Rgb3(blue)
                    + " | gradient -> picture half way: sky " + CameraSkySmoke.Rgb(gradHalf) + " | SetBlend 0.5 (" + p3.ToString("0.00") + ") " + Rgb3(held)
                    + " | 3/4 into a fade to a missing picture " + Rgb3(missing));
                bool ok = true;
                void Check(bool c, string what) { if (!c) { ok = false; log.LogError("sky crossfade: " + what); } }
                Check(Reddish(red.sky) && Reddish(red.mirror) && Reddish(red.cube), "the red picture does not show (sky / mirror / sky-lit cube)");
                Check(p0 == 0f && Reddish(start.sky) && Reddish(start.mirror) && Reddish(start.cube), "the fade does not start from the sky shown (a pop at its start)");
                Check(p1 > 0.45f && p1 < 0.55f, "FadeProgress is not 0.5 half way through");
                Check(Mixed(half.sky, red.sky, blue.sky), "half way the sky is not between the two pictures");
                Check(Mixed(half.mirror, red.mirror, blue.mirror), "half way the reflection does not follow the blend");
                Check(Mixed(half.cube, red.cube, blue.cube), "half way the sky light (ambient) does not follow the blend");
                Check(p2 == 1f && Bluish(blue.sky) && Bluish(blue.mirror) && Bluish(blue.cube), "after the fade the blue picture does not show everywhere (or FadeProgress is not 1)");
                Check(gradHalf.g > 50 && gradHalf.b > 50 && gradHalf.g < 200 && gradHalf.b < 200, "a gradient does not cross-fade into a picture");
                Check(Math.Abs(p3 - 0.5f) < 1e-3f && Mixed(held.sky, red.sky, blue.sky) && Mixed(held.mirror, red.mirror, blue.mirror), "Sky.SetBlend(red, blue, 0.5) does not hold the mix");
                Check(Reddish(missing.sky) && Reddish(missing.mirror) && Reddish(missing.cube) && Lum(missing.cube) > Lum(red.cube) * 0.8 && Lum(missing.mirror) > Lum(red.mirror) * 0.8,
                    "a fade to a picture that cannot be loaded darkens the sky light or the reflection (it should leave the blend)");
                return ok;
            }
            finally
            {
                SkyFade.Cancel();
                SceneRenderService.ScriptSky = prevSky;
                SceneRenderService.HideEditorOverlays = false;
                foreach (var l in suns) l.IsEnabled = true;
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                SceneRenderService.Instance.ClearSkyboxMeshCache();
                foreach (var f in new[] { redRel, blueRel, mirrorRel, matteRel }) { try { File.Delete(Path.Combine(project, f)); } catch { } }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
