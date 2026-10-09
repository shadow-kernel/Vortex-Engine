using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Particles;
using Editor.Core.Viewport;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace VortexEditor.Shell
{
    /// <summary>Particles on the real backend (#117): a bright additive glow spawned 6 m in front of the editor camera must
    /// be simulated (alive particles), gathered by the renderer (drawn particles, batches) and visible — the centre of the
    /// next frame gets brighter. Runs on DX12 / WARP in the gates and on Metal locally; the effect is a throw-away .vfx
    /// written into the project's Assets.</summary>
    internal static class ParticleSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("particles", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string root = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(root)) { log.Log("particles: no project — skipped"); return true; }
            if (!ParticleService.Available) { log.LogError("particles: the particle module is not available in this engine"); return false; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            string dir = Path.Combine(root, "Assets", "VFX");
            string file = Path.Combine(dir, "SmokeGlow.vfx");
            bool madeDir = !Directory.Exists(dir);
            long spawned = 0;
            try
            {
                Directory.CreateDirectory(dir);
                var asset = new VfxAsset { Name = "Smoke glow" };
                var e = new VfxEmitter
                {
                    Name = "Glow", MaxParticles = 512, Rate = 400f, Looping = true, Prewarm = true,
                    Lifetime = new[] { 3f, 3f }, Speed = new[] { 0f, 0f }, Size = new[] { 6f, 6f },
                    Color = new[] { 1f, 0.6f, 0.1f, 1f }, Color2 = new[] { 1f, 0.6f, 0.1f, 1f }, Gravity = 0f,
                };
                e.Shape.Type = VfxShapeType.Sphere; e.Shape.Radius = 0.3f;
                e.Render.Blend = VfxBlend.Additive; e.Render.Emissive = 2f; e.Render.SoftDistance = 0f;
                asset.Emitters.Add(e);
                if (!asset.Save(file)) { log.LogError("particles: could not write " + file); return false; }

                cam.SetPositionAndRotation(0, 500, 0, 0, 0);   // far above the scene, looking down +Z at the sky / clear colour
                EditorViewportSession.RequestResubmit();
                var before = await CameraSkySmoke.Centre("particles_off.bmp");
                spawned = ParticleService.SpawnAt(file, new Vector3(0, 500, 6), Quaternion.Identity);
                if (spawned == 0) { log.LogError("particles: SpawnAt failed — " + VortexAPIErrors()); return false; }
                await SmokeRegistry.Settle(900);
                EditorViewportSession.RequestResubmit();
                var during = await CameraSkySmoke.Centre("particles_on.bmp");
                var stats = ParticleService.Stats();
                log.Log("particles: renderer draws " + stats.RendererDraws + ", alive " + stats.AliveParticles + ", drawn " + stats.DrawnParticles
                    + " in " + stats.DrawBatches + " batches — centre " + CameraSkySmoke.Rgb(before) + " -> " + CameraSkySmoke.Rgb(during));
                bool draws = stats.RendererDraws == 1;
                bool alive = stats.AliveParticles > 0;
                bool drawn = stats.DrawnParticles > 0 && stats.DrawBatches > 0;
                bool brighter = during.r >= 0 && (during.r + during.g + during.b) > (before.r + before.g + before.b) + 40;
                if (!draws) log.LogError("particles: this backend reports that it does not draw particles (RendererDraws = " + stats.RendererDraws + ")");
                if (!alive) log.LogError("particles: no particle alive — the simulation did not tick (frame callback / auto update)");
                if (!drawn) log.LogError("particles: the renderer gathered nothing — the particle pass did not run");
                if (!brighter) log.LogError("particles: the glow is not visible at the centre of the frame");
                return draws && alive && drawn && brighter;
            }
            finally
            {
                if (spawned != 0) ParticleService.DestroySpawned(spawned);
                try { File.Delete(file); if (madeDir && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch { }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        private static string VortexAPIErrors()
        {
            try { return Editor.DllWrapper.VortexAPI.ParticleLastError(); } catch { return "?"; }
        }
    }
}
