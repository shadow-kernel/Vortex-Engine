using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Shell.AssetImport;

namespace VortexEditor.Shell
{
    /// <summary>The import dialog's sidecar .vmat files carry what the glTF says (#441, #450, #453): a generated glTF with a
    /// MASK + doubleSided leaf, an emissive neon (emissiveFactor + KHR_materials_emissive_strength) and a colour-only rubber,
    /// imported next to a colour-named image, gives materials/submesh_N.vmat with BlendMode AlphaTest at the glTF cutoff and
    /// TwoSided, EmissiveColor / EmissiveStrength from the glTF, and no albedo for the rubber.</summary>
    internal static class GltfImportSidecarSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("glTF import sidecars", Run);

        private static Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            string project = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(project)) { log.Log("glTF import sidecars: no project — skipped"); return Task.FromResult(true); }
            if (!Editor.DllWrapper.VortexAPI.IsAssimpAvailable()) { log.Log("glTF import sidecars: no Assimp — skipped"); return Task.FromResult(true); }
            string src = Path.Combine(Path.GetTempPath(), "vortex-smoke-sidecars-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string target = Path.Combine(project, "Assets", "SmokeSidecars");
            try
            {
                Directory.CreateDirectory(src);
                CameraSkySmoke.WritePng(Path.Combine(src, "leaf.png"), 8, 8, (x, y) => ((byte)40, (byte)160, (byte)40, (byte)(x < 4 ? 0 : 255)));
                CameraSkySmoke.WritePng(Path.Combine(src, "paint_albedo.png"), 8, 8, (x, y) => ((byte)0, (byte)0, (byte)255, (byte)255));
                string source = Path.Combine(src, "props.gltf");
                File.WriteAllText(source, PropsGltf());

                var o = new ImportOptions { RegisterInLibrary = false, GenerateMeta = false, ConvertToLeftHanded = false };
                var r = ModelImportPipeline.Import(source, target, o);
                if (!r.Success) { log.LogError("glTF import sidecars: the import failed: " + r.Error); return Task.FromResult(false); }
                var byName = new Dictionary<string, VortexMaterial>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in r.MaterialFiles) { var m = VortexMaterial.Load(f); if (m != null) byName[m.Name ?? ""] = m; }
                byName.TryGetValue("LeafMesh", out var leaf);
                byName.TryGetValue("NeonMesh", out var neon);
                byName.TryGetValue("RubberMesh", out var rubber);
                if (leaf == null || neon == null || rubber == null) { log.LogError("glTF import sidecars: expected the sidecars LeafMesh / NeonMesh / RubberMesh, got " + string.Join(", ", byName.Keys)); return Task.FromResult(false); }

                bool cutout = leaf.BlendMode == "AlphaTest" && Math.Abs(leaf.AlphaCutoff - 0.3f) < 1e-4f && leaf.TwoSided && !string.IsNullOrEmpty(leaf.AlbedoTexture);
                bool glows = neon.EmissiveColor != null && neon.EmissiveColor.Length >= 3 && Math.Abs(neon.EmissiveColor[0] - 1f) < 1e-4f && Math.Abs(neon.EmissiveColor[1] - 0.5f) < 1e-4f
                             && Math.Abs(neon.EmissiveColor[2] - 0.25f) < 1e-4f && Math.Abs(neon.EmissiveStrength - 4f) < 1e-4f;
                bool plain = string.IsNullOrEmpty(rubber.AlbedoTexture) && rubber.BlendMode == "Opaque" && !rubber.TwoSided && rubber.EmissiveStrength == 0f;
                log.Log("glTF import sidecars: leaf " + leaf.BlendMode + " @" + leaf.AlphaCutoff + (leaf.TwoSided ? " two-sided" : "") + ", neon emissive "
                        + string.Join("/", neon.EmissiveColor ?? new float[0]) + " x" + neon.EmissiveStrength + ", rubber albedo '" + rubber.AlbedoTexture + "' " + rubber.BlendMode);
                if (!cutout) log.LogError("glTF import sidecars: the MASK leaf's sidecar is not AlphaTest at 0.3, two-sided, with its map (#441)");
                if (!glows) log.LogError("glTF import sidecars: the neon's sidecar lacks the glTF emissiveFactor x strength (#450)");
                if (!plain) log.LogError("glTF import sidecars: the colour-only rubber got a texture or a blend mode (#453)");
                return Task.FromResult(cutout && glows && plain);
            }
            finally
            {
                try { SceneRenderService.InvalidateModel(Path.Combine(target, "props", "props.gltf")); } catch { }
                try { Directory.Delete(target, true); } catch { }
                try { Directory.Delete(src, true); } catch { }
            }
        }

        /// <summary>Three one-triangle meshes on their own nodes: LeafMesh (MASK at 0.3, doubleSided, leaf.png), NeonMesh
        /// (emissiveFactor [1, 0.5, 0.25] x KHR_materials_emissive_strength 4) and RubberMesh (a dark baseColorFactor only).</summary>
        private static string PropsGltf()
        {
            float[] pos = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] uv = { 0, 0, 1, 0, 0, 1 };
            ushort[] idx = { 0, 1, 2, 0 };
            int pb = pos.Length * 4, ub = uv.Length * 4, ib = idx.Length * 2;
            var bytes = new byte[pb + ub + ib];
            Buffer.BlockCopy(pos, 0, bytes, 0, pb);
            Buffer.BlockCopy(uv, 0, bytes, pb, ub);
            Buffer.BlockCopy(idx, 0, bytes, pb + ub, ib);
            string Mesh(string name, int material) => "{\"name\":\"" + name + "\",\"primitives\":[{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1},\"indices\":2,\"material\":" + material + "}]}";
            return "{\"asset\":{\"version\":\"2.0\"},\"extensionsUsed\":[\"KHR_materials_emissive_strength\"],\"scene\":0,\"scenes\":[{\"nodes\":[0,1,2]}]," +
                   "\"nodes\":[{\"mesh\":0},{\"mesh\":1,\"translation\":[2,0,0]},{\"mesh\":2,\"translation\":[4,0,0]}]," +
                   "\"meshes\":[" + Mesh("LeafMesh", 0) + "," + Mesh("NeonMesh", 1) + "," + Mesh("RubberMesh", 2) + "]," +
                   "\"materials\":[{\"name\":\"Leaf\",\"alphaMode\":\"MASK\",\"alphaCutoff\":0.3,\"doubleSided\":true,\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}," +
                   "{\"name\":\"Neon\",\"emissiveFactor\":[1,0.5,0.25],\"extensions\":{\"KHR_materials_emissive_strength\":{\"emissiveStrength\":4}}}," +
                   "{\"name\":\"Rubber\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.05,0.05,0.05,1]}}]," +
                   "\"textures\":[{\"source\":0}],\"images\":[{\"uri\":\"leaf.png\"}]," +
                   "\"buffers\":[{\"byteLength\":" + bytes.Length + ",\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes) + "\"}]," +
                   "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":" + pb + "},{\"buffer\":0,\"byteOffset\":" + pb + ",\"byteLength\":" + ub + "}," +
                   "{\"buffer\":0,\"byteOffset\":" + (pb + ub) + ",\"byteLength\":6}]," +
                   "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}," +
                   "{\"bufferView\":1,\"componentType\":5126,\"count\":3,\"type\":\"VEC2\"}," +
                   "{\"bufferView\":2,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]}";
        }
    }
}
