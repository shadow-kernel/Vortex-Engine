using System;
using System.IO;
using Editor.Core.Assets;
using Editor.DllWrapper;

namespace VortexTests
{
    /// <summary>What the model import writes into a submesh's sidecar .vmat. The native importer's side (glTF files read
    /// through Assimp) is covered by the VortexImportTest ctest.</summary>
    public static class ImportMaterialTests
    {
        [Test]
        public static void GltfAlphaModesReachTheSidecar(TestContext t)
        {
            // #441: MASK -> AlphaTest at the glTF cutoff, BLEND -> AlphaBlend, doubleSided -> TwoSided
            var leaves = new VortexMaterial();
            new VortexAPI.SubmeshMaterialProps { BaseColor = new[] { 0.2f, 0.6f, 0.1f, 1f }, Metallic = 0f, Roughness = 0.9f, BlendMode = 3, AlphaCutoff = 0.3f, TwoSided = true }.ApplyTo(leaves);
            t.Equal("AlphaTest", leaves.BlendMode, "MASK cuts out");
            t.Equal(0.3f, leaves.AlphaCutoff, "at the glTF alphaCutoff");
            t.True(leaves.TwoSided, "doubleSided");
            t.Equal(0.6f, leaves.BaseColor[1], "the base colour still comes along");
            t.Equal(0.9f, leaves.Roughness, "and the factors");

            var glass = new VortexMaterial();
            new VortexAPI.SubmeshMaterialProps { BlendMode = 1 }.ApplyTo(glass);
            t.Equal("AlphaBlend", glass.BlendMode, "BLEND blends");
            t.Equal(0.5f, glass.AlphaCutoff, "the cutoff keeps its default outside MASK");
            t.False(glass.TwoSided, "one-sided");

            var plain = new VortexMaterial { BlendMode = "AlphaBlend", TwoSided = true };
            new VortexAPI.SubmeshMaterialProps().ApplyTo(plain);
            t.Equal("Opaque", plain.BlendMode, "OPAQUE (and every non-glTF material) imports opaque");
            t.False(plain.TwoSided, "and one-sided");

            // the sidecar keeps it
            string path = t.Path("submesh_0.vmat");
            t.True(leaves.Save(path), "saved");
            var back = VortexMaterial.Load(path);
            t.Equal("AlphaTest", back.BlendMode, "BlendMode round-trips");
            t.Equal(0.3f, back.AlphaCutoff, "AlphaCutoff round-trips");
            t.True(back.TwoSided, "TwoSided round-trips");
        }

        [Test]
        public static void ColourOnlyMaterialsTakeNoFolderTexture(TestContext t)
        {
            // #453: the placement's folder fallback stands in only where the model wants an albedo it could not load
            var sources = new[] { 0, 1, 2 };   // colour-only, names a map, no material in the file
            t.False(AssetActions.MayUseFolderTexture("/p/car.fbx", sources, 0, false), "a colour-only material keeps its colour");
            t.True(AssetActions.MayUseFolderTexture("/p/car.fbx", sources, 1, false), "a map the material names but the import could not load: rescued");
            t.False(AssetActions.MayUseFolderTexture("/p/car.fbx", sources, 1, true), "...unless its own map is bound");
            t.True(AssetActions.MayUseFolderTexture("/p/crate.obj", sources, 2, false), "an OBJ without .mtl has nothing else");
            t.False(AssetActions.MayUseFolderTexture("/p/car.glb", sources, 1, false), "a glTF never takes a folder image");
            t.False(AssetActions.MayUseFolderTexture("/p/car.gltf", null, 0, false), "not even from an engine that cannot tell");
            t.True(AssetActions.MayUseFolderTexture("/p/old.fbx", null, 0, false), "other formats with an engine that cannot tell: as before");
            t.False(AssetActions.MayUseFolderTexture("/p/car.fbx", sources, 5, false), "a submesh the engine did not report: no guess");
        }

        [Test]
        public static void FolderTexturesSkipExtractedMapsInAStableOrder(TestContext t)
        {
            // #453: the embedded_<stem>_N images are the model's own normal / ORM / emissive maps (its albedo is bound by
            // role) or another model's — never a stand-in; and the list does not depend on the file system's order
            string dir = t.Path("Models", "Defender");
            Directory.CreateDirectory(dir);
            string model = Path.Combine(dir, "defender.glb");
            File.WriteAllBytes(model, new byte[] { 1 });
            foreach (var name in new[] { "embedded_defender_10.png", "embedded_defender_0.png", "embedded_tree_0.png", "zz_paint.png", "Body_albedo.png", "aa_decal.png", "Body_normal.png" })
                File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 1 });
            var list = AssetActions.FindTexturesForModel(model);
            var names = list.ConvertAll(Path.GetFileName);
            t.Equal("Body_albedo.png|aa_decal.png|zz_paint.png", string.Join("|", names), "colour-named first, then the rest, each in name order; no extracted or normal maps");
        }

        [Test]
        public static void EngineBlendModesHaveVmatNames(TestContext t)
        {
            t.Equal("Opaque", VortexMaterial.BlendModeName(0), "0");
            t.Equal("AlphaBlend", VortexMaterial.BlendModeName(1), "1");
            t.Equal("Additive", VortexMaterial.BlendModeName(2), "2");
            t.Equal("AlphaTest", VortexMaterial.BlendModeName(3), "3");
            t.Equal("Opaque", VortexMaterial.BlendModeName(7), "anything else");
        }
    }
}
