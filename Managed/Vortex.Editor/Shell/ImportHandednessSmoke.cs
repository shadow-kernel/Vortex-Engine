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
using CoreAssetActions = Editor.Core.Assets.AssetActions;

namespace VortexEditor.Shell
{
    /// <summary>The left-handed import conversion (#352): a generated glTF quad at z = +5 sits in front of a camera
    /// looking down +Z when read as-is; with "leftHanded" in the model's .vimport sidecar the importer flips Z and the
    /// quad ends up behind the camera. The placed instance follows the sidecar after an invalidation.</summary>
    internal static class ImportHandednessSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("import handedness", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("import handedness: no scene / project — skipped"); return true; }
            if (!Editor.DllWrapper.VortexAPI.IsAssimpAvailable()) { log.Log("import handedness: no Assimp — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            string dir = Path.Combine(project, "Assets", "Models", "SmokeHand");
            string gltf = Path.Combine(dir, "smoke_hand.gltf");
            string matRel = "Assets/Materials/SmokeHandRed.vmat";
            GameEntity placed = null;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(gltf, QuadGltf(5f));
                try { File.Delete(gltf + ".vimport"); } catch { }
                new VortexMaterial { ShaderType = "Unlit", BlendMode = "Opaque", TwoSided = true, BaseColor = new[] { 1f, 0f, 0f, 1f } }.Save(Path.Combine(project, matRel));

                placed = CoreAssetActions.AddModelToScene(gltf);
                if (placed?.Transform == null) { log.LogError("import handedness: the generated glTF could not be placed"); return false; }
                placed.Transform.LocalPosition = new Vector3(0, 500, 0);
                var mr = placed.GetComponent<MeshRenderer>() ?? (placed.Children != null && placed.Children.Count > 0 ? placed.Children[0].GetComponent<MeshRenderer>() : null);
                if (mr == null) { log.LogError("import handedness: no MeshRenderer on the placed model"); return false; }
                mr.MaterialPath = matRel;

                cam.SetPositionAndRotation(0, 500, 0, 0, 0);
                SceneRenderService.HideEditorOverlays = true;
                EditorViewportSession.RequestResubmit();
                var asIs = await CameraSkySmoke.Centre("hand_right.bmp");

                // the sidecar flips Z: the quad moves behind the camera
                ModelImportSettings.SaveLeftHanded(gltf, true);
                SceneRenderService.InvalidateModel(gltf);
                EditorViewportSession.RequestResubmit();
                var flipped = await CameraSkySmoke.Centre("hand_left.bmp");

                log.Log("import handedness: quad at +Z read as-is " + CameraSkySmoke.Rgb(asIs) + ", with the left-handed sidecar " + CameraSkySmoke.Rgb(flipped));
                bool visible = asIs.r > 150 && asIs.g < 60 && asIs.b < 60;
                bool gone = !(flipped.r > 150 && flipped.g < 60 && flipped.b < 60);
                if (!visible) log.LogError("import handedness: the generated glTF quad is not in front of the camera (import or placement broke)");
                if (!gone) log.LogError("import handedness: the left-handed sidecar did not flip the model's Z (#352)");
                return visible && gone;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                if (placed != null) EditorCommands.DeleteEntities(new List<GameEntity> { placed });
                try { SceneRenderService.InvalidateModel(gltf); } catch { }
                try { Directory.Delete(dir, true); } catch { }
                try { File.Delete(Path.Combine(project, matRel)); } catch { }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        /// <summary>A minimal glTF 2.0: one 6 x 6 quad in the XY plane at z = <paramref name="z"/>, buffer embedded.</summary>
        private static string QuadGltf(float z)
        {
            float[] pos = { -3, -3, z, 3, -3, z, 3, 3, z, -3, 3, z };
            ushort[] idx = { 0, 1, 2, 0, 2, 3 };
            var bytes = new byte[pos.Length * 4 + idx.Length * 2];
            Buffer.BlockCopy(pos, 0, bytes, 0, pos.Length * 4);
            Buffer.BlockCopy(idx, 0, bytes, pos.Length * 4, idx.Length * 2);
            string zs = z.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                   "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]," +
                   "\"buffers\":[{\"byteLength\":" + bytes.Length + ",\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes) + "\"}]," +
                   "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":" + (pos.Length * 4) + "},{\"buffer\":0,\"byteOffset\":" + (pos.Length * 4) + ",\"byteLength\":" + (idx.Length * 2) + "}]," +
                   "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\",\"min\":[-3,-3," + zs + "],\"max\":[3,3," + zs + "]}," +
                   "{\"bufferView\":1,\"componentType\":5123,\"count\":6,\"type\":\"SCALAR\"}]}";
        }
    }
}
