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
    /// <summary>A glTF's KHR_texture_transform reaches the vertex UVs (#470): a generated quad whose UVs only span 0..1/4 —
    /// the way a Sketchfab scan quantises them — scales its texture by 4. Placed and shown with a texture whose left half is
    /// red and right half green, the quad shows both halves; read raw (as before) it showed only the red quarter. A second
    /// quad whose normal map tiles differently from its base colour cannot be reproduced with one UV set: placing it puts
    /// a warning naming the file and the material in the console.</summary>
    internal static class GltfTextureTransformSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("glTF texture transform", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("glTF texture transform: no scene / project — skipped"); return true; }
            if (!Editor.DllWrapper.VortexAPI.IsAssimpAvailable()) { log.Log("glTF texture transform: no Assimp — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            string dir = Path.Combine(project, "Assets", "Models", "SmokeUvTransform");
            string gltf = Path.Combine(dir, "smoke_scan.gltf");
            string tex = Path.Combine(dir, "smoke_scan.png");
            string mixedGltf = Path.Combine(dir, "smoke_mixed.gltf");
            string matRel = "Assets/Models/SmokeUvTransform/smoke_scan_unlit.vmat";
            GameEntity placed = null, mixed = null;
            try
            {
                Directory.CreateDirectory(dir);
                CameraSkySmoke.WritePng(tex, 64, 64, (x, y) => x < 32 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)255, (byte)0, (byte)255));
                File.WriteAllText(gltf, ScanGltf(5f, "{\"name\":\"SmokeScan\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0," +
                                                      "\"extensions\":{\"KHR_texture_transform\":{\"scale\":[4,4]}}},\"metallicFactor\":0}}"));
                File.WriteAllText(mixedGltf, ScanGltf(5f, "{\"name\":\"SmokeMixed\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0," +
                                                           "\"extensions\":{\"KHR_texture_transform\":{\"scale\":[4,4]}}},\"metallicFactor\":0}," +
                                                           "\"normalTexture\":{\"index\":0,\"extensions\":{\"KHR_texture_transform\":{\"scale\":[2,2]}}}}"));
                // unlit, so the check sees the texture's colours whatever the scene's light
                new VortexMaterial { ShaderType = "Unlit", TwoSided = true, BaseColor = new[] { 1f, 1f, 1f, 1f }, AlbedoTexture = "smoke_scan.png" }.Save(Path.Combine(project, matRel));

                placed = VortexEditor.Services.AssetActions.AddToScene(gltf, new Vector3(0, 500, 0));
                SelectionService.Instance.ClearSelection();
                var mr = placed?.GetComponent<MeshRenderer>();
                if (mr == null) { log.LogError("glTF texture transform: the generated glTF could not be placed"); return false; }
                mr.MaterialPath = matRel;

                cam.SetPositionAndRotation(0, 500, 0, 0, 0);
                SceneRenderService.HideEditorOverlays = true;
                EditorViewportSession.RequestResubmit();
                var left = await CameraSkySmoke.Sample("uv_transform.bmp", 0.35, 0.5);
                var right = await CameraSkySmoke.Sample("uv_transform.bmp", 0.65, 0.5, capture: false);
                log.Log("glTF texture transform: quad " + CameraSkySmoke.Rgb(left) + " | " + CameraSkySmoke.Rgb(right));
                bool both = (Red(left) && Green(right)) || (Green(left) && Red(right));
                if (!both) log.LogError("glTF texture transform: the quad should show the texture's red AND green half — its x4 KHR_texture_transform was not applied (#470)");

                // a transform the engine cannot reproduce is reported, naming the file and the material
                mixed = VortexEditor.Services.AssetActions.AddToScene(mixedGltf, new Vector3(0, 400, 0));
                SelectionService.Instance.ClearSelection();
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(600);
                bool warned = false;
                foreach (var e in ConsoleService.Instance.Entries)
                    if (e.Level == LogLevel.Warning && e.Message != null && e.Message.Contains("smoke_mixed.gltf") && e.Message.Contains("'SmokeMixed'")) warned = true;
                if (!warned) log.LogError("glTF texture transform: no console warning for the material whose maps need different UV transforms (#470)");
                return both && warned;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                var made = new List<GameEntity>();
                if (placed != null) made.Add(placed);
                if (mixed != null) made.Add(mixed);
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                try { SceneRenderService.InvalidateModel(gltf); SceneRenderService.InvalidateModel(mixedGltf); } catch { }
                try { MaterialService.Instance.InvalidateVortexMaterial(Path.Combine(project, matRel)); } catch { }
                try { Directory.Delete(dir, true); } catch { }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        private static bool Red((int r, int g, int b) c) => c.r > 120 && c.r > c.g + 80 && c.r > c.b + 80;
        private static bool Green((int r, int g, int b) c) => c.g > 120 && c.g > c.r + 80 && c.g > c.b + 80;

        /// <summary>A 6 x 6 quad in the XY plane at z = <paramref name="z"/>, its UVs quantised into 0..1/4, wearing
        /// <paramref name="material"/> (JSON) with smoke_scan.png as texture 0; buffer embedded.</summary>
        private static string ScanGltf(float z, string material)
        {
            float[] pos = { -3, -3, z, 3, -3, z, 3, 3, z, -3, 3, z };
            float[] uv = { 0, 0.25f, 0.25f, 0.25f, 0.25f, 0, 0, 0 };
            ushort[] idx = { 0, 1, 2, 0, 2, 3 };
            int pb = pos.Length * 4, ub = uv.Length * 4, ib = idx.Length * 2;
            var bytes = new byte[pb + ub + ib];
            Buffer.BlockCopy(pos, 0, bytes, 0, pb);
            Buffer.BlockCopy(uv, 0, bytes, pb, ub);
            Buffer.BlockCopy(idx, 0, bytes, pb + ub, ib);
            string zs = z.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "{\"asset\":{\"version\":\"2.0\"},\"extensionsUsed\":[\"KHR_texture_transform\"],\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                   "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1},\"indices\":2,\"material\":0}]}]," +
                   "\"materials\":[" + material + "]," +
                   "\"textures\":[{\"source\":0}],\"images\":[{\"uri\":\"smoke_scan.png\"}]," +
                   "\"buffers\":[{\"byteLength\":" + bytes.Length + ",\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes) + "\"}]," +
                   "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":" + pb + "},{\"buffer\":0,\"byteOffset\":" + pb + ",\"byteLength\":" + ub + "}," +
                   "{\"buffer\":0,\"byteOffset\":" + (pb + ub) + ",\"byteLength\":" + ib + "}]," +
                   "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\",\"min\":[-3,-3," + zs + "],\"max\":[3,3," + zs + "]}," +
                   "{\"bufferView\":1,\"componentType\":5126,\"count\":4,\"type\":\"VEC2\"}," +
                   "{\"bufferView\":2,\"componentType\":5123,\"count\":6,\"type\":\"SCALAR\"}]}";
        }
    }
}
