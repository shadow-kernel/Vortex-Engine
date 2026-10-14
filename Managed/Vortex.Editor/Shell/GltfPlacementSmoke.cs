using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>Placing a glTF gives a colour-only material no image from the model's folder (#453): a generated glTF with a
    /// textured "Body" and a factor-only "Rubber" sits next to a colour-named image and an extracted map of the model
    /// (embedded_car_0.png — what a normal or ORM map is called after the import). Placed the way place_asset places it,
    /// the Body keeps its own map, the Rubber no texture at all, and no part gets a texture_path.</summary>
    internal static class GltfPlacementSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("glTF colour-only placement", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("glTF colour-only placement: no scene / project — skipped"); return true; }
            if (!VortexAPI.IsAssimpAvailable()) { log.Log("glTF colour-only placement: no Assimp — skipped"); return true; }
            string dir = Path.Combine(project, "Assets", "Models", "SmokeColourOnly");
            string gltf = Path.Combine(dir, "car.gltf");
            GameEntity placed = null;
            try
            {
                Directory.CreateDirectory(dir);
                CameraSkySmoke.WritePng(Path.Combine(dir, "body.png"), 8, 8, (x, y) => ((byte)255, (byte)0, (byte)0, (byte)255));
                CameraSkySmoke.WritePng(Path.Combine(dir, "embedded_car_0.png"), 8, 8, (x, y) => ((byte)128, (byte)128, (byte)255, (byte)255));
                CameraSkySmoke.WritePng(Path.Combine(dir, "paint_albedo.png"), 8, 8, (x, y) => ((byte)0, (byte)255, (byte)0, (byte)255));
                File.WriteAllText(gltf, CarGltf());

                placed = VortexEditor.Services.AssetActions.AddToScene(gltf, new Vector3(0, 500, 0));
                await SmokeRegistry.Settle(200);
                SelectionService.Instance.ClearSelection();
                if (placed == null || placed.Children == null || placed.Children.Count != 2) { log.LogError("glTF colour-only placement: the glTF did not place as two parts"); return false; }
                int textured = 0, plain = 0;
                var paths = new List<string>();
                foreach (var child in placed.Children)
                {
                    var mr = child.GetComponent<MeshRenderer>();
                    if (mr == null) { log.LogError("glTF colour-only placement: a part has no MeshRenderer"); return false; }
                    paths.Add(child.Name + "=" + (mr.TexturePath ?? "") + (string.IsNullOrEmpty(mr.MaterialPath) ? "" : " vmat " + mr.MaterialPath));
                    bool hasMap = mr.MaterialHandle >= 0 && VortexAPI.HasMaterialTexture(mr.MaterialHandle);
                    if (hasMap) textured++; else plain++;
                    if (!string.IsNullOrEmpty(mr.TexturePath)) log.LogError("glTF colour-only placement: " + child.Name + " got a folder texture " + mr.TexturePath + " (#453)");
                }
                log.Log("glTF colour-only placement: " + textured + " part(s) with their own map, " + plain + " colour-only, texture paths " + string.Join(", ", paths));
                bool ok = textured == 1 && plain == 1 && paths.TrueForAll(p => p.EndsWith("="));
                if (textured != 1 || plain != 1) log.LogError("glTF colour-only placement: expected the Body with its map and the Rubber without any (#453)");
                return ok;
            }
            finally
            {
                if (placed != null) EditorCommands.DeleteEntities(new List<GameEntity> { placed });
                try { SceneRenderService.InvalidateModel(gltf); } catch { }
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>Two quads: "Body" with baseColorTexture body.png, "Rubber" with only a dark baseColorFactor.</summary>
        private static string CarGltf()
        {
            float[] pos = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };
            float[] uv = { 0, 0, 1, 0, 1, 1, 0, 1 };
            ushort[] idx = { 0, 1, 2, 0, 2, 3 };
            int pb = pos.Length * 4, ub = uv.Length * 4, ib = idx.Length * 2;
            var bytes = new byte[pb + ub + ib];
            Buffer.BlockCopy(pos, 0, bytes, 0, pb);
            Buffer.BlockCopy(uv, 0, bytes, pb, ub);
            Buffer.BlockCopy(idx, 0, bytes, pb + ub, ib);
            const string prim = "{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1},\"indices\":2,\"material\":";
            return "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                   "\"meshes\":[{\"primitives\":[" + prim + "0}," + prim + "1}]}]," +
                   "\"materials\":[{\"name\":\"Body\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}," +
                   "{\"name\":\"Rubber\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.05,0.05,0.05,1]}}]," +
                   "\"textures\":[{\"source\":0}],\"images\":[{\"uri\":\"body.png\"}]," +
                   "\"buffers\":[{\"byteLength\":" + bytes.Length + ",\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes) + "\"}]," +
                   "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":" + pb + "},{\"buffer\":0,\"byteOffset\":" + pb + ",\"byteLength\":" + ub + "}," +
                   "{\"buffer\":0,\"byteOffset\":" + (pb + ub) + ",\"byteLength\":" + ib + "}]," +
                   "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}," +
                   "{\"bufferView\":1,\"componentType\":5126,\"count\":4,\"type\":\"VEC2\"}," +
                   "{\"bufferView\":2,\"componentType\":5123,\"count\":6,\"type\":\"SCALAR\"}]}";
        }
    }
}
