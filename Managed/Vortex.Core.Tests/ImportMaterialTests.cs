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
