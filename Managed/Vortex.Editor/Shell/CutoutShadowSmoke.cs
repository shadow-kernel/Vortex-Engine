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
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>Alpha-tested casters cast the shape of their texture (#329): a flat slab with a half-transparent cut-out
    /// material hangs over a floor under a sun with shadows. The floor under the opaque half is dark, under the
    /// transparent half it stays lit; the same slab with a solid material shadows the whole floor evenly.</summary>
    internal static class CutoutShadowSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("cutout shadows", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("cutout shadows: no scene / project — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            var suns = new List<Light>();   // the scene's own directional lights, parked for the test (the last submitted one wins)
            string texRel = "Assets/Textures/SmokeShadowCutout.png", matRel = "Assets/Materials/SmokeShadowCutout.vmat";
            try
            {
                string tex = Path.Combine(project, texRel);
                CameraSkySmoke.WritePng(tex, 64, 64, (x, y) => x < 32 ? ((byte)255, (byte)255, (byte)255, (byte)0) : ((byte)255, (byte)255, (byte)255, (byte)255));
                var m = new VortexMaterial { BlendMode = "AlphaTest", AlphaCutoff = 0.5f, TwoSided = true, BaseColor = new[] { 1f, 1f, 1f, 1f }, AlbedoTexture = tex };
                m.Save(Path.Combine(project, matRel));

                Collect(scene.Entities, suns);
                foreach (var s in suns) s.IsEnabled = false;

                var floor = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                var slab = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                var sunEntity = EditorCommands.CreateLight(LightType.Directional);
                if (floor?.Transform == null || slab?.Transform == null || sunEntity?.Transform == null) { log.LogError("cutout shadows: could not create the entities"); return false; }
                made.Add(floor); made.Add(slab); made.Add(sunEntity);
                floor.Name = "SmokeShadowFloor";
                floor.Transform.LocalPosition = new Vector3(0, 490, 0);
                floor.Transform.LocalScale = new Vector3(80, 0.2f, 80);
                var fmr = floor.GetComponent<MeshRenderer>();
                if (fmr != null) { fmr.ColorR = 0.8f; fmr.ColorG = 0.8f; fmr.ColorB = 0.8f; }
                slab.Name = "SmokeShadowCutout";
                slab.Transform.LocalPosition = new Vector3(0, 496, 0);
                slab.Transform.LocalScale = new Vector3(24, 0.2f, 24);
                var smr = slab.GetComponent<MeshRenderer>();
                if (smr == null) { log.LogError("cutout shadows: no MeshRenderer on the slab"); return false; }
                sunEntity.Name = "SmokeShadowSun";
                sunEntity.Transform.LocalPosition = new Vector3(0, 520, 0);
                sunEntity.Transform.LocalRotation = new Vector3(85, 0, 0);   // local +Z turned (almost) straight down
                var sun = sunEntity.GetComponent<Light>();
                if (sun == null) { log.LogError("cutout shadows: no Light"); return false; }
                sun.Intensity = 3f; sun.ShadowType = ShadowType.Hard; sun.ShadowStrength = 1f;

                // below the slab, looking down at the floor under it: four floor points, two per slab half
                cam.SetPositionAndRotation(0, 491.5f, -16, 0, 12);
                SceneRenderService.HideEditorOverlays = true;
                smr.MaterialPath = matRel;
                EditorViewportSession.RequestResubmit();
                var cut = await Four("shadow_cutout.bmp");
                smr.MaterialPath = null;   // the slab's plain material: a solid caster
                EditorViewportSession.RequestResubmit();
                var solid = await Four("shadow_solid.bmp");

                int cutMax = Max(cut), cutMin = Min(cut), solidMax = Max(solid), solidMin = Min(solid);
                log.Log("cutout shadows: floor under the cut-out slab " + Str(cut) + " (spread " + (cutMax - cutMin) + "), under the solid slab " + Str(solid) + " (spread " + (solidMax - solidMin) + ")");
                bool shaped = cutMax - cutMin > 40;
                bool even = solidMax - solidMin < 30;
                bool lighter = cutMax > solidMax + 30;
                if (!shaped) log.LogError("cutout shadows: the floor under the cut-out slab is evenly shadowed — the transparent half casts a solid shadow (#329)");
                if (!even) log.LogError("cutout shadows: the floor under the solid slab is not evenly shadowed (the sun or its shadow is not set up)");
                if (!lighter) log.LogError("cutout shadows: no floor point under the cut-out is lit");
                return shaped && even && lighter;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                foreach (var s in suns) { try { s.IsEnabled = true; } catch { } }
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                foreach (var rel in new[] { texRel, matRel }) { try { File.Delete(Path.Combine(project, rel)); } catch { } }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        private static async Task<int[]> Four(string file)
        {
            // two rows, two columns — the slab's halves split along x, so a column sits inside one half either way round
            var a = await CameraSkySmoke.Sample(file, 0.3, 0.47);
            var b = await CameraSkySmoke.Sample(file, 0.7, 0.47, capture: false);
            var c = await CameraSkySmoke.Sample(file, 0.3, 0.53, capture: false);
            var d = await CameraSkySmoke.Sample(file, 0.7, 0.53, capture: false);
            return new[] { a.r + a.g + a.b, b.r + b.g + b.b, c.r + c.g + c.b, d.r + d.g + d.b };
        }

        private static int Max(int[] v) { int m = int.MinValue; foreach (var x in v) m = Math.Max(m, x); return m; }
        private static int Min(int[] v) { int m = int.MaxValue; foreach (var x in v) m = Math.Min(m, x); return m; }
        private static string Str(int[] v) => string.Join("/", v);

        private static void Collect(IEnumerable<GameEntity> entities, List<Light> into)
        {
            foreach (var e in entities)
            {
                if (e == null) continue;
                var l = e.GetComponent<Light>();
                if (l != null && l.LightType == LightType.Directional && l.IsEnabled) into.Add(l);
                if (e.Children != null) Collect(e.Children, into);
            }
        }
    }
}
