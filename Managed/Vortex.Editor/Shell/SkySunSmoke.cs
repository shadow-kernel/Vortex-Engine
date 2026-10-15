using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>The sun's disc in the sky (#499), on the rendered picture so the DX12 (WARP) smoke covers the HLSL path too. A
    /// Directional Light shines towards the camera from 20° above the horizon; the camera looks straight at it, high above the
    /// scene. A plain blue 8-bit picture sky draws a white disc there (Auto), still there with the picture turned by 90° (the
    /// disc is the light's); a blue .hdr does not (Auto: it carries its own sun) until the disc is switched On, shown the
    /// classic way and through the exposure pipeline (scene tonemapper, the disc added before it); a 6° disc
    /// covers a point 1.5° off the light that the real-size 0.53° disc leaves blue; a cube in front hides it; the gradient
    /// sky's disc follows the light when the light turns away (it used to sit at a fixed direction).</summary>
    internal static class SkySunSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("sky sun disc", Run);

        private static bool White((int r, int g, int b) c) => c.r > 225 && c.g > 225 && c.b > 225;
        private static bool Bluish((int r, int g, int b) c) => c.b > c.r + 40 && c.r < 200;

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("sky sun disc: no scene / project — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            var others = VortexEditor.Claude.SceneModel.All(scene).Select(e => e.GetComponent<Light>()).Where(l => l != null && l.IsEnabled && l.LightType == LightType.Directional).ToList();
            Skybox sky = VortexEditor.Claude.SceneModel.All(scene).Select(e => e.GetComponent<Skybox>()).FirstOrDefault(s => s != null);
            var keep = sky == null ? null : new { sky.SkyboxType, sky.TexturePath, sky.Exposure, sky.IsEnabled, sky.SunDisc, sky.SunSize, sky.SunIntensity, sky.SunGlow,
                                                  sky.TopColorR, sky.TopColorG, sky.TopColorB, sky.HorizonColorR, sky.HorizonColorG, sky.HorizonColorB, sky.BottomColorR, sky.BottomColorG, sky.BottomColorB };
            var prevSky = SceneRenderService.ScriptSky; var prevSun = SceneRenderService.ScriptSkySun;
            int tonemapper = scene.Settings.Tonemapper;
            string pngRel = "Assets/Textures/SmokeSunSky.png", hdrRel = "Assets/Textures/SmokeSunSky.hdr";
            try
            {
                foreach (var l in others) l.IsEnabled = false;
                if (sky == null)
                {
                    var holder = EditorCommands.CreateEmpty();
                    if (holder == null) { log.LogError("sky sun disc: could not create a Skybox entity"); return false; }
                    holder.Name = "SmokeSunSky"; made.Add(holder);
                    sky = new Skybox(holder);
                    EditorCommands.AddComponent(sky);
                }
                CameraSkySmoke.WritePng(Path.Combine(project, pngRel), 64, 32, (x, y) => ((byte)40, (byte)70, (byte)140, (byte)255));
                WriteHdr(Path.Combine(project, hdrRel), 64, 32, (x, y) => (0.15f, 0.3f, 0.6f));
                var sunEntity = EditorCommands.CreateLight(LightType.Directional);
                if (sunEntity?.Transform == null) { log.LogError("sky sun disc: no light"); return false; }
                made.Add(sunEntity);
                sunEntity.Name = "SmokeSkySun";
                // the light travels towards the camera and 20° down: the sun stands 20° above the horizon along +Z
                sunEntity.Transform.LocalRotation = new Vector3(20, 180, 0);
                var sun = sunEntity.GetComponent<Light>();
                sun.Intensity = 3f; sun.ColorR = sun.ColorG = sun.ColorB = 1f; sun.ShadowType = ShadowType.None;
                // the cube that hides the sun later — created now: a new entity is framed by the editor camera
                var blocker = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (blocker?.Transform == null) { log.LogError("sky sun disc: no cube"); return false; }
                made.Add(blocker);
                blocker.Name = "SmokeSunBlocker";
                blocker.Transform.LocalPosition = new Vector3(0, 500, -60);   // behind the camera until it is needed
                blocker.Transform.LocalScale = new Vector3(6, 6, 1);
                var bmr = blocker.GetComponent<MeshRenderer>();
                if (bmr != null) { bmr.ColorR = 0.1f; bmr.ColorG = 0.6f; bmr.ColorB = 0.1f; }

                sky.IsEnabled = true; sky.SkyboxType = SkyboxType.Texture; sky.TexturePath = pngRel; sky.Exposure = 1f;
                sky.SunDisc = SkySunDisc.Auto; sky.SunSize = 0.53f; sky.SunIntensity = 1f; sky.SunGlow = 1f;
                SceneRenderService.Instance.ClearSkyboxMeshCache();
                SceneRenderService.HideEditorOverlays = true;
                cam.SetPositionAndRotation(0, 500, 0, 0, -20);   // straight at the sun
                EditorViewportSession.RequestResubmit();
                var pngDisc = await CameraSkySmoke.Centre("sky_sun_png.bmp");
                var pngSky = await CameraSkySmoke.Sample("sky_sun_png.bmp", 0.5, 0.12, capture: false);

                SceneRenderService.ScriptSky = new SceneRenderService.SkyOverride { TexturePath = pngRel, Exposure = 1f, RotationDeg = 90f };
                EditorViewportSession.RequestResubmit();
                var turned = await CameraSkySmoke.Centre("sky_sun_turned.bmp");
                SceneRenderService.ScriptSky = null;

                sky.TexturePath = hdrRel;   // a float picture: Auto leaves the disc to the picture
                SceneRenderService.Instance.ClearSkyboxMeshCache();
                EditorViewportSession.RequestResubmit();
                var hdrAuto = await CameraSkySmoke.Centre("sky_sun_hdr_auto.bmp");
                sky.SunDisc = SkySunDisc.On;
                EditorViewportSession.RequestResubmit();
                var hdrOn = await CameraSkySmoke.Centre("sky_sun_hdr_on.bmp");
                // the same through the exposure pipeline (a scene with the neutral tonemapper runs the .hdr through exposure +
                // tonemapper): the disc is added before them
                scene.Settings.Tonemapper = 1;
                EditorViewportSession.RequestResubmit();
                var hdrToneOn = await CameraSkySmoke.Centre("sky_sun_hdr_tonemapped_on.bmp");
                sky.SunDisc = SkySunDisc.Auto;
                EditorViewportSession.RequestResubmit();
                var hdrToneAuto = await CameraSkySmoke.Centre("sky_sun_hdr_tonemapped_auto.bmp");
                scene.Settings.Tonemapper = tonemapper;

                // size: the camera 1.5° off the light — inside a 6° disc, outside the real-size one
                sky.TexturePath = pngRel; sky.SunDisc = SkySunDisc.Auto;
                SceneRenderService.Instance.ClearSkyboxMeshCache();
                cam.SetPositionAndRotation(0, 500, 0, 0, -21.5f);
                EditorViewportSession.RequestResubmit();
                var offSmall = await CameraSkySmoke.Centre("sky_sun_off_small.bmp");
                sky.SunSize = 6f;
                EditorViewportSession.RequestResubmit();
                var offLarge = await CameraSkySmoke.Centre("sky_sun_off_large.bmp");
                sky.SunSize = 0.53f;

                // occlusion: the cube between the camera and the sun
                cam.SetPositionAndRotation(0, 500, 0, 0, -20);
                blocker.Transform.LocalPosition = new Vector3(0, 500 + 30f * 0.364f, 30);   // on the line of sight, 30 m out
                EditorViewportSession.RequestResubmit();
                var hidden = await CameraSkySmoke.Centre("sky_sun_hidden.bmp");
                blocker.Transform.LocalPosition = new Vector3(0, 500, -60);

                // the gradient sky's disc is at the light, and moves with it
                sky.SkyboxType = SkyboxType.Gradient;
                sky.TopColorR = 0.1f; sky.TopColorG = 0.2f; sky.TopColorB = 0.6f;
                sky.HorizonColorR = 0.2f; sky.HorizonColorG = 0.3f; sky.HorizonColorB = 0.6f;
                sky.BottomColorR = 0.1f; sky.BottomColorG = 0.1f; sky.BottomColorB = 0.2f;
                SceneRenderService.Instance.ClearSkyboxMeshCache();
                EditorViewportSession.RequestResubmit();
                var gradient = await CameraSkySmoke.Centre("sky_sun_gradient.bmp");
                sunEntity.Transform.LocalRotation = new Vector3(20, 90, 0);   // the light turns a quarter away
                EditorViewportSession.RequestResubmit();
                var gradientAway = await CameraSkySmoke.Centre("sky_sun_gradient_away.bmp");

                log.Log("sky sun disc: 8-bit picture at the light " + CameraSkySmoke.Rgb(pngDisc) + " / sky " + CameraSkySmoke.Rgb(pngSky)
                    + ", picture turned 90° " + CameraSkySmoke.Rgb(turned) + ", .hdr auto " + CameraSkySmoke.Rgb(hdrAuto) + " / on " + CameraSkySmoke.Rgb(hdrOn)
                    + ", tonemapped .hdr on " + CameraSkySmoke.Rgb(hdrToneOn) + " / auto " + CameraSkySmoke.Rgb(hdrToneAuto)
                    + ", 1.5° off: 0.53° disc " + CameraSkySmoke.Rgb(offSmall) + " / 6° disc " + CameraSkySmoke.Rgb(offLarge)
                    + ", behind a cube " + CameraSkySmoke.Rgb(hidden) + ", gradient " + CameraSkySmoke.Rgb(gradient) + " / light turned away " + CameraSkySmoke.Rgb(gradientAway));
                bool ok = true;
                void Check(bool c, string what) { if (!c) { ok = false; log.LogError("sky sun disc: " + what); } }
                Check(White(pngDisc) && Bluish(pngSky), "an 8-bit picture sky draws no white disc at the directional light (or the sky around it is not the picture)");
                Check(White(turned), "the disc does not stay at the light when the picture is turned");
                Check(Bluish(hdrAuto), "an .hdr sky draws a disc on Auto — it would double the sun the picture carries");
                Check(White(hdrOn), "an .hdr sky with the disc On draws none");
                Check(White(hdrToneOn) && Bluish(hdrToneAuto), "the disc of a tonemapped .hdr sky (the exposure pipeline) is missing, or drawn on Auto");
                Check(!White(offSmall) && White(offLarge), "SunSize does not size the disc (1.5° off the light: a 0.53° disc must miss, a 6° disc cover it)");
                Check(!White(hidden) && hidden.r + hidden.g + hidden.b < 400, "geometry in front of the sun does not hide the disc");
                Check(White(gradient) && !White(gradientAway), "the gradient sky's disc is not at the directional light (or does not follow it)");
                return ok;
            }
            finally
            {
                SceneRenderService.ScriptSky = prevSky; SceneRenderService.ScriptSkySun = prevSun;
                scene.Settings.Tonemapper = tonemapper;
                SceneRenderService.HideEditorOverlays = false;
                if (keep != null)
                {
                    sky.SkyboxType = keep.SkyboxType; sky.TexturePath = keep.TexturePath; sky.Exposure = keep.Exposure; sky.IsEnabled = keep.IsEnabled;
                    sky.SunDisc = keep.SunDisc; sky.SunSize = keep.SunSize; sky.SunIntensity = keep.SunIntensity; sky.SunGlow = keep.SunGlow;
                    sky.TopColorR = keep.TopColorR; sky.TopColorG = keep.TopColorG; sky.TopColorB = keep.TopColorB;
                    sky.HorizonColorR = keep.HorizonColorR; sky.HorizonColorG = keep.HorizonColorG; sky.HorizonColorB = keep.HorizonColorB;
                    sky.BottomColorR = keep.BottomColorR; sky.BottomColorG = keep.BottomColorG; sky.BottomColorB = keep.BottomColorB;
                }
                foreach (var l in others) l.IsEnabled = true;
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                SceneRenderService.Instance.ClearSkyboxMeshCache();
                foreach (var f in new[] { pngRel, hdrRel }) { try { File.Delete(Path.Combine(project, f)); } catch { } }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        /// <summary>A Radiance .hdr (RGBE, flat scanlines) from a per-pixel linear (r, g, b) function — float test skies.</summary>
        internal static void WriteHdr(string file, int w, int h, Func<int, int, (float r, float g, float b)> pixel)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using (var fs = File.Create(file))
            {
                var head = System.Text.Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y " + h + " +X " + w + "\n");
                fs.Write(head, 0, head.Length);
                var px = new byte[4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var c = pixel(x, y);
                        float m = Math.Max(c.r, Math.Max(c.g, c.b));
                        if (m < 1e-32f) { px[0] = px[1] = px[2] = px[3] = 0; }
                        else
                        {
                            int e = (int)Math.Floor(Math.Log(m, 2.0)) + 1;
                            float k = (float)(256.0 / Math.Pow(2.0, e));
                            px[0] = (byte)Math.Min(255, (int)(c.r * k)); px[1] = (byte)Math.Min(255, (int)(c.g * k)); px[2] = (byte)Math.Min(255, (int)(c.b * k));
                            px[3] = (byte)(e + 128);
                        }
                        fs.Write(px, 0, 4);
                    }
            }
        }
    }
}
