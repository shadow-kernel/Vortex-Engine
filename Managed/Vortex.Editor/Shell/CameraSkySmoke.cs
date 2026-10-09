using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>Two renderer checks from the "My Spectre" report, run on the real backend (Metal here, DX12 / WARP on
    /// the Windows runner): the live view's far plane follows what is set (#327), and a Texture skybox is the
    /// renderer's own equirect sky pass — behind everything, around whichever camera renders the frame (#326).</summary>
    internal static class CameraSkySmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("camera clip planes", ClipPlanes);
            SmokeRegistry.Add("sky texture", SkyTexture);
        }

        internal static bool Green((int r, int g, int b) c) => c.g > 110 && c.g > c.r + 50 && c.g > c.b + 50;
        private static bool Blue((int r, int g, int b) c) => c.b > 110 && c.b > c.r + 50 && c.b > c.g + 50;
        private static bool Red((int r, int g, int b) c) => c.r > 110 && c.r > c.g + 50 && c.r > c.b + 50;
        internal static string Rgb((int r, int g, int b) c) => c.r + "/" + c.g + "/" + c.b;

        /// <summary>A green wall 120 m ahead of the editor camera is there with the far plane at 1000 m and gone with
        /// the far plane at 60 m — the main view's projection reads the clip planes instead of a fixed 1000.</summary>
        private static async Task<bool> ClipPlanes()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("camera clip planes: no scene — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            GameEntity wall = null;
            try
            {
                wall = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (wall?.Transform == null) { log.LogError("camera clip planes: no cube"); return false; }
                wall.Name = "SmokeFarWall";
                wall.Transform.LocalPosition = new Vector3(0, 500, 120);
                wall.Transform.LocalScale = new Vector3(80, 80, 1);
                var mr = wall.GetComponent<MeshRenderer>();
                if (mr != null) { mr.ColorR = 0f; mr.ColorG = 1f; mr.ColorB = 0f; }
                cam.SetPositionAndRotation(0, 500, 0, 0, 0);   // high above the scene, looking +Z at the wall
                VortexAPI.SetViewClipPlanes(0.1f, 1000f);
                EditorViewportSession.RequestResubmit();
                var far = await Centre("clip_far1000.bmp");

                VortexAPI.SetViewClipPlanes(0.1f, 60f);
                EditorViewportSession.RequestResubmit();
                var near = await Centre("clip_far60.bmp");

                log.Log("camera clip planes: wall at 120 m — far 1000 m: " + Rgb(far) + ", far 60 m: " + Rgb(near));
                bool seen = Green(far), gone = !Green(near);
                if (!seen) log.LogError("camera clip planes: the wall is not in the centre with the far plane at 1000 m");
                if (!gone) log.LogError("camera clip planes: the wall is still drawn with the far plane at 60 m");
                return seen && gone;
            }
            finally
            {
                VortexAPI.SetViewClipPlanes(RaycastService.EditorNearClip, RaycastService.EditorFarClip);
                if (wall != null) EditorCommands.DeleteEntities(new List<GameEntity> { wall });
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        /// <summary>An equirect test map (blue above the horizon, red below) as the scene's Texture sky: blue straight
        /// up, blue above / red below the horizon, and the same from 300 m away — the sky is the renderer's pass
        /// around the camera, not a sphere left behind somewhere.</summary>
        private static async Task<bool> SkyTexture()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("sky texture: no scene / project — skipped"); return true; }
            Skybox sky = null;
            void Walk(GameEntity e) { if (e == null || sky != null) return; sky = e.GetComponent<Skybox>(); if (sky == null && e.Children != null) foreach (var c in e.Children) Walk(c); }
            foreach (var e in scene.Entities?.ToList() ?? new List<GameEntity>()) Walk(e);
            GameEntity made = null;
            if (sky == null)
            {
                // a scene without a sky gets one for the check (removed again below)
                made = EditorCommands.CreateEmpty();
                if (made == null) { log.LogError("sky texture: could not create a Skybox entity"); return false; }
                made.Name = "SmokeSky";
                sky = new Skybox(made);
                EditorCommands.AddComponent(sky);
            }

            var type = sky.SkyboxType; string path = sky.TexturePath; float exposure = sky.Exposure; bool enabled = sky.IsEnabled;
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            string file = Path.Combine(project, "Assets", "Textures", "SmokeSky.png");
            try
            {
                WriteEquirect(file, 256, 128);
                sky.IsEnabled = true;
                sky.SkyboxType = SkyboxType.Texture;
                sky.TexturePath = "Assets/Textures/SmokeSky.png";
                sky.Exposure = 1f;
                SceneRenderService.Instance.ClearSkyboxMeshCache();

                cam.SetPositionAndRotation(0, 500, 0, 0, -80);   // straight up: nothing but sky
                EditorViewportSession.RequestResubmit();
                var up = await Centre("sky_up.bmp");

                cam.SetPositionAndRotation(0, 500, 0, 0, 0);     // horizon from 500 m up: the scene is far below the view
                EditorViewportSession.RequestResubmit();
                var above = await Sample("sky_horizon.bmp", 0.5, 0.2);
                var below = await Sample("sky_horizon.bmp", 0.5, 0.8, capture: false);

                cam.SetPositionAndRotation(300, 500, 300, 0, 0);  // 400 m further: no parallax, no stale sphere
                EditorViewportSession.RequestResubmit();
                var away = await Sample("sky_away.bmp", 0.5, 0.2);

                // a script's sky (#349) wins over the component: a pure red gradient, then back to the texture
                SceneRenderService.ScriptSky = new SceneRenderService.SkyOverride { Gradient = new[] { 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f } };
                cam.SetPositionAndRotation(0, 500, 0, 0, -80);
                EditorViewportSession.RequestResubmit();
                var scripted = await Centre("sky_scripted.bmp");
                SceneRenderService.ScriptSky = null;
                EditorViewportSession.RequestResubmit();
                var back = await Centre("sky_back.bmp");

                log.Log("sky texture: up " + Rgb(up) + ", above the horizon " + Rgb(above) + ", below " + Rgb(below) + ", 400 m away " + Rgb(away) + "; scripted red gradient " + Rgb(scripted) + ", cleared " + Rgb(back));
                bool ok = Blue(up) && Blue(above) && Red(below) && Blue(away);
                bool okScript = Red(scripted) && Blue(back);
                if (!ok) log.LogError("sky texture: expected blue up / above the horizon and from 400 m away, red below the horizon");
                if (!okScript) log.LogError("sky texture: the scripted sky did not take over (or did not let go) — #349");
                return ok && okScript;
            }
            finally
            {
                SceneRenderService.ScriptSky = null;
                sky.SkyboxType = type; sky.TexturePath = path; sky.Exposure = exposure; sky.IsEnabled = enabled;
                if (made != null) EditorCommands.DeleteEntities(new List<GameEntity> { made });
                SceneRenderService.Instance.ClearSkyboxMeshCache();
                try { File.Delete(file); } catch { }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        // ---- capture helpers --------------------------------------------------------------------------------------

        internal static Task<(int r, int g, int b)> Centre(string file) => Sample(file, 0.5, 0.5);

        /// <summary>The average colour of a 9×9 block of the next presented frame at (fx, fy) in 0..1 — captured by the
        /// renderer itself (CaptureFrame writes the real back buffer as a 32-bit BMP).</summary>
        internal static async Task<(int r, int g, int b)> Sample(string file, double fx, double fy, bool capture = true)
        {
            string dir = SmokeRegistry.CaptureDir;
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.GetTempPath(), "vortex-smoke-capture");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, file);
            if (capture)
            {
                try { File.Delete(path); } catch { }
                await SmokeRegistry.Settle(350);   // the resubmit must reach the renderer first
                VortexAPI.CaptureFrame(path);
                for (int i = 0; i < 60 && !File.Exists(path); i++) await Task.Delay(50);
                await Task.Delay(150);
            }
            return ReadBmp(path, fx, fy);
        }

        private static (int r, int g, int b) ReadBmp(string path, double fx, double fy)
        {
            try
            {
                var d = File.ReadAllBytes(path);
                if (d.Length < 54 || d[0] != (byte)'B' || d[1] != (byte)'M') return (-1, -1, -1);
                int off = BitConverter.ToInt32(d, 10), w = BitConverter.ToInt32(d, 18), h = BitConverter.ToInt32(d, 22);
                int bpp = BitConverter.ToInt16(d, 28);
                bool bottomUp = h > 0; h = Math.Abs(h);
                int bytes = bpp / 8; if (bytes < 3) return (-1, -1, -1);
                int stride = ((w * bytes + 3) / 4) * 4;
                int cx = (int)(w * fx), cy = (int)(h * fy);
                long r = 0, g = 0, b = 0; int n = 0;
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        int row = bottomUp ? h - 1 - y : y;
                        int i = off + row * stride + x * bytes;
                        if (i + 2 >= d.Length) continue;
                        b += d[i]; g += d[i + 1]; r += d[i + 2]; n++;
                    }
                return n == 0 ? (-1, -1, -1) : ((int)(r / n), (int)(g / n), (int)(b / n));
            }
            catch { return (-1, -1, -1); }
        }

        /// <summary>An equirectangular test map: the upper half (above the horizon) blue, the lower half red.</summary>
        private static void WriteEquirect(string file, int w, int h) =>
            WritePng(file, w, h, (x, y) => y < h / 2 ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)255, (byte)0, (byte)0, (byte)255));

        /// <summary>A PNG from a per-pixel (r, g, b, a) function — test textures for the smoke checks.</summary>
        internal static void WritePng(string file, int w, int h, Func<int, int, (byte r, byte g, byte b, byte a)> pixel)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var fb = bmp.Lock())
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var p = pixel(x, y);
                        row[x * 4 + 0] = p.b; row[x * 4 + 1] = p.g; row[x * 4 + 2] = p.r; row[x * 4 + 3] = p.a;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), row.Length);
                }
            }
            using (var fs = File.Create(file)) bmp.Save(fs);
        }
    }
}
