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
    /// <summary>Lit emission (#450) and the material UV offset (#448), checked on the rendered picture so the DX12 (WARP)
    /// smoke covers the HLSL path too. "lit emission": three dark StandardPBR panels side by side — the left one glows
    /// orange through EmissiveColor × EmissiveStrength, the middle one glows green through a white emission times a green
    /// emissive map, the right one has no emission and stays a dark neutral. "uv offset": two panels share a texture whose
    /// left half is red and right half blue, both with tiling (0.5, 1); the one with offset (0.5, 0) shows the blue half.</summary>
    internal static class MaterialEmissionSmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("lit emission", RunEmission);
            SmokeRegistry.Add("uv offset", RunOffset);
        }

        private static GameEntity Panel(List<GameEntity> made, string name, float x, float width, string material)
        {
            var e = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
            if (e?.Transform == null) return null;
            made.Add(e);
            e.Name = name;
            e.Transform.LocalPosition = new Vector3(x, 500, 20);
            e.Transform.LocalScale = new Vector3(width, 8, 1);
            var mr = e.GetComponent<MeshRenderer>();
            if (mr != null) mr.MaterialPath = material;
            return e;
        }

        private static async Task<bool> RunEmission()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("lit emission: no scene / project — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            string glowRel = "Assets/Materials/SmokeEmissionGlow.vmat", mapRel = "Assets/Materials/SmokeEmissionMap.vmat";
            string plainRel = "Assets/Materials/SmokeEmissionPlain.vmat", texRel = "Assets/Materials/SmokeEmissionGreen.png";
            try
            {
                CameraSkySmoke.WritePng(Path.Combine(project, texRel), 8, 8, (x, y) => ((byte)0, (byte)255, (byte)0, (byte)255));
                float[] dark = { 0.12f, 0.12f, 0.12f, 1f };
                new VortexMaterial { ShaderType = "StandardPBR", BaseColor = dark, Metallic = 0f, Roughness = 0.9f,
                                     EmissiveColor = new[] { 1f, 0.25f, 0.05f }, EmissiveStrength = 3f }.Save(Path.Combine(project, glowRel));
                new VortexMaterial { ShaderType = "StandardPBR", BaseColor = dark, Metallic = 0f, Roughness = 0.9f,
                                     EmissiveColor = new[] { 1f, 1f, 1f }, EmissiveStrength = 3f,
                                     EmissiveTexture = Path.Combine(project, texRel) }.Save(Path.Combine(project, mapRel));
                new VortexMaterial { ShaderType = "StandardPBR", BaseColor = dark, Metallic = 0f, Roughness = 0.9f,
                                     EmissiveColor = new[] { 1f, 0.25f, 0.05f }, EmissiveStrength = 0f }.Save(Path.Combine(project, plainRel));

                var glow = Panel(made, "SmokeEmissionGlow", -5, 7, glowRel);
                var map = Panel(made, "SmokeEmissionMap", 0, 2.4f, mapRel);
                var plain = Panel(made, "SmokeEmissionPlain", 5, 7, plainRel);
                if (glow == null || map == null || plain == null) { log.LogError("lit emission: no panels"); return false; }

                cam.SetPositionAndRotation(0, 500, 0, 0, 0);
                SceneRenderService.HideEditorOverlays = true;
                EditorViewportSession.RequestResubmit();
                var g = await CameraSkySmoke.Sample("emission.bmp", 0.33, 0.5);
                var m = await CameraSkySmoke.Sample("emission.bmp", 0.5, 0.5, capture: false);
                var p = await CameraSkySmoke.Sample("emission.bmp", 0.67, 0.5, capture: false);
                log.Log("lit emission: orange glow " + CameraSkySmoke.Rgb(g) + ", green map " + CameraSkySmoke.Rgb(m) + ", no emission " + CameraSkySmoke.Rgb(p));
                bool orange = g.r > p.r + 60 && g.r > g.g + 40 && g.r > g.b + 40;
                bool green = m.g > p.g + 50 && m.g > m.r + 40 && m.g > m.b + 40;
                bool plainDark = Math.Abs(p.r - p.g) < 30 && Math.Abs(p.g - p.b) < 35;   // strength 0: the colour does not leak
                if (!orange) log.LogError("lit emission: a StandardPBR material with EmissiveColor × EmissiveStrength does not glow (#450)");
                if (!green) log.LogError("lit emission: the emissive map does not modulate a lit material's emission (#450)");
                if (!plainDark) log.LogError("lit emission: EmissiveStrength 0 still adds the emissive colour");
                return orange && green && plainDark;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                foreach (var f in new[] { glowRel, mapRel, plainRel, texRel }) { try { File.Delete(Path.Combine(project, f)); } catch { } }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        private static async Task<bool> RunOffset()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("uv offset: no scene / project — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            string aRel = "Assets/Materials/SmokeOffsetA.vmat", bRel = "Assets/Materials/SmokeOffsetB.vmat", texRel = "Assets/Materials/SmokeOffsetHalves.png";
            try
            {
                // left half red, right half blue — wide enough that the panel centre samples one texel colour cleanly
                CameraSkySmoke.WritePng(Path.Combine(project, texRel), 64, 8, (x, y) => x < 32 ? ((byte)230, (byte)20, (byte)20, (byte)255) : ((byte)20, (byte)20, (byte)230, (byte)255));
                string tex = Path.Combine(project, texRel);
                new VortexMaterial { ShaderType = "StandardPBR", BaseColor = new[] { 1f, 1f, 1f, 1f }, Metallic = 0f, Roughness = 1f, AlbedoTexture = tex,
                                     UVTiling = new[] { 0.5f, 1f }, UVOffset = new[] { 0f, 0f } }.Save(Path.Combine(project, aRel));
                new VortexMaterial { ShaderType = "StandardPBR", BaseColor = new[] { 1f, 1f, 1f, 1f }, Metallic = 0f, Roughness = 1f, AlbedoTexture = tex,
                                     UVTiling = new[] { 0.5f, 1f }, UVOffset = new[] { 0.5f, 0f } }.Save(Path.Combine(project, bRel));

                var a = Panel(made, "SmokeOffsetA", -5, 7, aRel);
                var b = Panel(made, "SmokeOffsetB", 5, 7, bRel);
                if (a == null || b == null) { log.LogError("uv offset: no panels"); return false; }

                cam.SetPositionAndRotation(0, 500, 0, 0, 0);
                SceneRenderService.HideEditorOverlays = true;
                EditorViewportSession.RequestResubmit();
                var ca = await CameraSkySmoke.Sample("uvoffset.bmp", 0.33, 0.5);
                var cb = await CameraSkySmoke.Sample("uvoffset.bmp", 0.67, 0.5, capture: false);
                log.Log("uv offset: tiling 0.5 offset 0 " + CameraSkySmoke.Rgb(ca) + ", offset 0.5 " + CameraSkySmoke.Rgb(cb));
                bool aRed = ca.r > ca.b + 30;
                bool bBlue = cb.b > cb.r + 30;
                if (!aRed) log.LogError("uv offset: the panel without an offset does not show the texture's left (red) half");
                if (!bBlue) log.LogError("uv offset: a material's UVOffset is ignored — offset (0.5, 0) still samples the red half (#448)");
                return aRed && bBlue;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                foreach (var f in new[] { aRel, bRel, texRel }) { try { File.Delete(Path.Combine(project, f)); } catch { } }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
