using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>A placed glTF glows from its own material (#450) — no import dialog, no sidecar .vmat, as a store download
    /// placed with place_asset: three dark panels of one generated glTF. The left one has an orange emissiveFactor with
    /// KHR_materials_emissive_strength 3, the middle one a white factor times a green emissive map, the right one the same
    /// map without a factor — black in glTF, so it stays a dark neutral.</summary>
    internal static class GltfEmissionSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("glTF emission import", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("glTF emission import: no scene / project — skipped"); return true; }
            if (!Editor.DllWrapper.VortexAPI.IsAssimpAvailable()) { log.Log("glTF emission import: no Assimp — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            string dir = Path.Combine(project, "Assets", "Models", "SmokeGltfEmission");
            string gltf = Path.Combine(dir, "smoke_signs.gltf");
            GameEntity placed = null;
            try
            {
                Directory.CreateDirectory(dir);
                CameraSkySmoke.WritePng(Path.Combine(dir, "smoke_green.png"), 8, 8, (x, y) => ((byte)0, (byte)255, (byte)0, (byte)255));
                File.WriteAllText(gltf, SignsGltf());
                placed = VortexEditor.Services.AssetActions.AddToScene(gltf, new Vector3(0, 500, 20));
                SelectionService.Instance.ClearSelection();
                if (placed == null || placed.Children == null || placed.Children.Count != 3) { log.LogError("glTF emission import: the glTF did not place as three panels"); return false; }
                foreach (var child in placed.Children)
                {
                    var mr = child.GetComponent<MeshRenderer>();
                    if (mr != null && (!string.IsNullOrEmpty(mr.MaterialPath) || !string.IsNullOrEmpty(mr.TexturePath))) { log.LogError("glTF emission import: a panel got a .vmat or texture of its own — the check must see the import material"); return false; }
                }

                cam.SetPositionAndRotation(0, 500, 0, 0, 0);
                SceneRenderService.HideEditorOverlays = true;
                EditorViewportSession.RequestResubmit();
                var g = await CameraSkySmoke.Sample("gltf_emission.bmp", 0.33, 0.5);
                var m = await CameraSkySmoke.Sample("gltf_emission.bmp", 0.5, 0.5, capture: false);
                var p = await CameraSkySmoke.Sample("gltf_emission.bmp", 0.67, 0.5, capture: false);
                log.Log("glTF emission import: orange factor " + CameraSkySmoke.Rgb(g) + ", green map " + CameraSkySmoke.Rgb(m) + ", map without a factor " + CameraSkySmoke.Rgb(p));
                bool orange = g.r > p.r + 60 && g.r > g.g + 40 && g.r > g.b + 40;
                bool green = m.g > p.g + 50 && m.g > m.r + 40 && m.g > m.b + 40;
                bool plainDark = Math.Abs(p.r - p.g) < 30 && Math.Abs(p.g - p.b) < 35;
                if (!orange) log.LogError("glTF emission import: a placed glTF with an emissiveFactor x KHR_materials_emissive_strength does not glow (#450)");
                if (!green) log.LogError("glTF emission import: a placed glTF's emissive map does not modulate its emission (#450)");
                if (!plainDark) log.LogError("glTF emission import: an emissive map without an emissiveFactor glows — glTF's default factor is black");
                return orange && green && plainDark;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                if (placed != null) EditorCommands.DeleteEntities(new List<GameEntity> { placed });
                try { SceneRenderService.InvalidateModel(gltf); } catch { }
                try { Directory.Delete(dir, true); } catch { }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        /// <summary>Three upright panels (8 high; 7, 2.4 and 7 wide) at x = -5, 0, 5 facing -Z, each its own mesh and
        /// material: "Orange" (emissiveFactor [1, 0.25, 0.05] x strength 3), "GreenMap" (white factor x strength 3 x
        /// smoke_green.png) and "MapNoFactor" (the map alone); every base colour a dark grey. Buffer embedded.</summary>
        private static string SignsGltf()
        {
            var floats = new List<float>();
            int Quad(float w, float h)   // positions (4 x vec3) then normals (4 x vec3) then uvs (4 x vec2); returns the float offset
            {
                int at = floats.Count;
                floats.AddRange(new[] { -w, -h, 0, w, -h, 0, w, h, 0, -w, h, 0 });
                floats.AddRange(new[] { 0f, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1 });
                floats.AddRange(new[] { 0f, 1, 1, 1, 1, 0, 0, 0 });
                return at;
            }
            int wide = Quad(3.5f, 4f), narrow = Quad(1.2f, 4f);
            // clockwise as the camera at the origin looking down +Z sees them: the engine's front face
            ushort[] idx = { 0, 3, 2, 0, 2, 1 };
            var bytes = new byte[floats.Count * 4 + idx.Length * 2];
            Buffer.BlockCopy(floats.ToArray(), 0, bytes, 0, floats.Count * 4);
            Buffer.BlockCopy(idx, 0, bytes, floats.Count * 4, idx.Length * 2);
            int ib = floats.Count * 4;
            string View(int offsetFloats, int countFloats) => "{\"buffer\":0,\"byteOffset\":" + (offsetFloats * 4) + ",\"byteLength\":" + (countFloats * 4) + "}";
            string views = string.Join(",", new[]
            {
                View(wide, 12), View(wide + 12, 12), View(wide + 24, 8),
                View(narrow, 12), View(narrow + 12, 12), View(narrow + 24, 8),
                "{\"buffer\":0,\"byteOffset\":" + ib + ",\"byteLength\":12}"
            });
            string Acc(int view, string type, string minmax = "") => "{\"bufferView\":" + view + ",\"componentType\":5126,\"count\":4,\"type\":\"" + type + "\"" + minmax + "}";
            string accessors = string.Join(",", new[]
            {
                Acc(0, "VEC3", ",\"min\":[-3.5,-4,0],\"max\":[3.5,4,0]"), Acc(1, "VEC3"), Acc(2, "VEC2"),
                Acc(3, "VEC3", ",\"min\":[-1.2,-4,0],\"max\":[1.2,4,0]"), Acc(4, "VEC3"), Acc(5, "VEC2"),
                "{\"bufferView\":6,\"componentType\":5123,\"count\":6,\"type\":\"SCALAR\"}"
            });
            string Mesh(string name, int first, int material) =>
                "{\"name\":\"" + name + "\",\"primitives\":[{\"attributes\":{\"POSITION\":" + first + ",\"NORMAL\":" + (first + 1) + ",\"TEXCOORD_0\":" + (first + 2) + "},\"indices\":6,\"material\":" + material + "}]}";
            const string dark = "\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.12,0.12,0.12,1],\"metallicFactor\":0,\"roughnessFactor\":0.9}";
            return "{\"asset\":{\"version\":\"2.0\"},\"extensionsUsed\":[\"KHR_materials_emissive_strength\"],\"scene\":0,\"scenes\":[{\"nodes\":[0,1,2]}]," +
                   "\"nodes\":[{\"mesh\":0,\"translation\":[-5,0,0]},{\"mesh\":1},{\"mesh\":2,\"translation\":[5,0,0]}]," +
                   "\"meshes\":[" + Mesh("Orange", 0, 0) + "," + Mesh("GreenMap", 3, 1) + "," + Mesh("MapNoFactor", 0, 2) + "]," +
                   "\"materials\":[{\"name\":\"Orange\"," + dark + ",\"emissiveFactor\":[1,0.25,0.05],\"extensions\":{\"KHR_materials_emissive_strength\":{\"emissiveStrength\":3}}}," +
                   "{\"name\":\"GreenMap\"," + dark + ",\"emissiveFactor\":[1,1,1],\"emissiveTexture\":{\"index\":0},\"extensions\":{\"KHR_materials_emissive_strength\":{\"emissiveStrength\":3}}}," +
                   "{\"name\":\"MapNoFactor\"," + dark + ",\"emissiveTexture\":{\"index\":0}}]," +
                   "\"textures\":[{\"source\":0}],\"images\":[{\"uri\":\"smoke_green.png\"}]," +
                   "\"buffers\":[{\"byteLength\":" + bytes.Length + ",\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes) + "\"}]," +
                   "\"bufferViews\":[" + views + "],\"accessors\":[" + accessors + "]}";
        }
    }
}
