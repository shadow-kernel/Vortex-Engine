using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>A glTF's alphaMode MASK survives the import (#441): a generated glTF quad — material MASK at 0.5,
    /// doubleSided, a texture whose left half is transparent blue and right half opaque red, no .vmat — is placed the way
    /// place_asset places it, above a floor under a sun. Its shadow has the texture's shape (one half of the floor lit, one
    /// dark), and from above the transparent half shows the floor while the other half shows red. The opaque import of
    /// before cast a solid square and showed the transparent half's blue.</summary>
    internal static class GltfCutoutSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("glTF cutout shadows", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("glTF cutout shadows: no scene / project — skipped"); return true; }
            if (!Editor.DllWrapper.VortexAPI.IsAssimpAvailable()) { log.Log("glTF cutout shadows: no Assimp — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            var suns = new List<Light>();   // the scene's own directional lights, parked for the test
            string dir = Path.Combine(project, "Assets", "Models", "SmokeGltfCutout");
            string gltf = Path.Combine(dir, "smoke_leaf.gltf");
            try
            {
                Directory.CreateDirectory(dir);
                CameraSkySmoke.WritePng(Path.Combine(dir, "smoke_leaf.png"), 64, 64,
                    (x, y) => x < 32 ? ((byte)0, (byte)0, (byte)255, (byte)0) : ((byte)255, (byte)0, (byte)0, (byte)255));
                File.WriteAllText(gltf, LeafGltf());

                Collect(scene.Entities, suns);
                foreach (var s in suns) s.IsEnabled = false;

                var leaf = VortexEditor.Services.AssetActions.AddToScene(gltf, new Vector3(0, 496, 0));
                var floor = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                var sunEntity = EditorCommands.CreateLight(LightType.Directional);
                if (leaf?.Transform == null || floor?.Transform == null || sunEntity?.Transform == null) { log.LogError("glTF cutout shadows: could not create the entities (the glTF did not place?)"); return false; }
                made.Add(leaf); made.Add(floor); made.Add(sunEntity);
                SelectionService.Instance.ClearSelection();
                var lmr = leaf.GetComponent<MeshRenderer>();
                if (lmr == null) { log.LogError("glTF cutout shadows: the placed glTF has no MeshRenderer"); return false; }
                if (!string.IsNullOrEmpty(lmr.MaterialPath) || !string.IsNullOrEmpty(lmr.TexturePath)) { log.LogError("glTF cutout shadows: placement bound a .vmat or texture of its own — the check must see the import material"); return false; }
                leaf.Transform.LocalScale = new Vector3(24, 1, 24);
                floor.Name = "SmokeGltfCutoutFloor";
                floor.Transform.LocalPosition = new Vector3(0, 490, 0);
                floor.Transform.LocalScale = new Vector3(80, 0.2f, 80);
                var fmr = floor.GetComponent<MeshRenderer>();
                if (fmr != null) { fmr.ColorR = 0.8f; fmr.ColorG = 0.8f; fmr.ColorB = 0.8f; }
                sunEntity.Name = "SmokeGltfCutoutSun";
                sunEntity.Transform.LocalPosition = new Vector3(0, 520, 0);
                sunEntity.Transform.LocalRotation = new Vector3(85, 0, 0);   // local +Z turned (almost) straight down
                var sun = sunEntity.GetComponent<Light>();
                if (sun == null) { log.LogError("glTF cutout shadows: no Light"); return false; }
                sun.Intensity = 3f; sun.ShadowType = ShadowType.Hard; sun.ShadowStrength = 1f;
                SceneRenderService.HideEditorOverlays = true;

                // below the quad, looking down at the floor under it: two floor points per texture half
                cam.SetPositionAndRotation(0, 491.5f, -16, 0, 12);
                EditorViewportSession.RequestResubmit();
                var a = await CameraSkySmoke.Sample("gltf_cutout_shadow.bmp", 0.3, 0.47);
                var b = await CameraSkySmoke.Sample("gltf_cutout_shadow.bmp", 0.7, 0.47, capture: false);
                var c = await CameraSkySmoke.Sample("gltf_cutout_shadow.bmp", 0.3, 0.53, capture: false);
                var d = await CameraSkySmoke.Sample("gltf_cutout_shadow.bmp", 0.7, 0.53, capture: false);
                int[] shade = { a.r + a.g + a.b, b.r + b.g + b.b, c.r + c.g + c.b, d.r + d.g + d.b };
                int spread = Math.Max(Math.Max(shade[0], shade[1]), Math.Max(shade[2], shade[3])) - Math.Min(Math.Min(shade[0], shade[1]), Math.Min(shade[2], shade[3]));

                // above the quad, looking straight down: one half shows the red texture, the other the floor through it
                cam.SetPositionAndRotation(0, 515, 0, 0, 89);
                EditorViewportSession.RequestResubmit();
                var left = await CameraSkySmoke.Sample("gltf_cutout_surface.bmp", 0.35, 0.5);
                var right = await CameraSkySmoke.Sample("gltf_cutout_surface.bmp", 0.65, 0.5, capture: false);

                log.Log("glTF cutout shadows: floor under the quad " + string.Join("/", shade) + " (spread " + spread + "), from above "
                        + CameraSkySmoke.Rgb(left) + " | " + CameraSkySmoke.Rgb(right));
                bool shaped = spread > 40;
                bool cutOut = (Red(left) && Floor(right)) || (Floor(left) && Red(right));
                bool noBlue = !Blue(left) && !Blue(right);
                if (!shaped) log.LogError("glTF cutout shadows: the quad casts a solid shadow — its alphaMode MASK was not imported (#441)");
                if (!noBlue) log.LogError("glTF cutout shadows: the transparent half of the texture renders (blue) — alphaMode MASK was not imported (#441)");
                else if (!cutOut) log.LogError("glTF cutout shadows: from above, one half should show the red texture and the other the floor");
                return shaped && cutOut && noBlue;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                foreach (var s in suns) { try { s.IsEnabled = true; } catch { } }
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                try { SceneRenderService.InvalidateModel(gltf); } catch { }
                try { Directory.Delete(dir, true); } catch { }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        private static bool Red((int r, int g, int b) c) => c.r > 100 && c.r > c.g + 60 && c.r > c.b + 60;
        private static bool Blue((int r, int g, int b) c) => c.b > 100 && c.b > c.r + 60 && c.b > c.g + 40;
        private static bool Floor((int r, int g, int b) c) => c.r > 60 && Math.Abs(c.r - c.g) < 40 && Math.Abs(c.g - c.b) < 40;

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

        /// <summary>A minimal glTF 2.0: a 1 x 1 quad in the XZ plane, lit side up, UVs 0..1 across it, its material MASK at
        /// 0.5 and doubleSided, the texture next to the file, the buffer embedded.</summary>
        private static string LeafGltf()
        {
            float[] pos = { -0.5f, 0, -0.5f, 0.5f, 0, -0.5f, 0.5f, 0, 0.5f, -0.5f, 0, 0.5f };
            float[] nrm = { 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0 };
            float[] uv = { 0, 0, 1, 0, 1, 1, 0, 1 };
            ushort[] idx = { 0, 1, 2, 0, 2, 3 };
            int pb = pos.Length * 4, nb = nrm.Length * 4, ub = uv.Length * 4, ib = idx.Length * 2;
            var bytes = new byte[pb + nb + ub + ib];
            Buffer.BlockCopy(pos, 0, bytes, 0, pb);
            Buffer.BlockCopy(nrm, 0, bytes, pb, nb);
            Buffer.BlockCopy(uv, 0, bytes, pb + nb, ub);
            Buffer.BlockCopy(idx, 0, bytes, pb + nb + ub, ib);
            return "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                   "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"indices\":3,\"material\":0}]}]," +
                   "\"materials\":[{\"name\":\"SmokeLeaf\",\"alphaMode\":\"MASK\",\"alphaCutoff\":0.5,\"doubleSided\":true," +
                   "\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0,\"roughnessFactor\":1}}]," +
                   "\"textures\":[{\"source\":0}],\"images\":[{\"uri\":\"smoke_leaf.png\"}]," +
                   "\"buffers\":[{\"byteLength\":" + bytes.Length + ",\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes) + "\"}]," +
                   "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":" + pb + "},{\"buffer\":0,\"byteOffset\":" + pb + ",\"byteLength\":" + nb + "}," +
                   "{\"buffer\":0,\"byteOffset\":" + (pb + nb) + ",\"byteLength\":" + ub + "},{\"buffer\":0,\"byteOffset\":" + (pb + nb + ub) + ",\"byteLength\":" + ib + "}]," +
                   "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\",\"min\":[-0.5,0,-0.5],\"max\":[0.5,0,0.5]}," +
                   "{\"bufferView\":1,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\"}," +
                   "{\"bufferView\":2,\"componentType\":5126,\"count\":4,\"type\":\"VEC2\"}," +
                   "{\"bufferView\":3,\"componentType\":5123,\"count\":6,\"type\":\"SCALAR\"}]}";
        }
    }
}
