using System.IO;
using System.Linq;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Builds the new structured asset graph (ModelAsset) from UniversalModelData.
    /// This isolates parsing from the runtime/editor asset model.
    /// </summary>
    public static class ModelAssetBuilder
    {
        public static ModelAsset FromUniversalData(UniversalModelData data)
        {
            if (data == null) return null;

            var model = new ModelAsset
            {
                Name = data.FileNameWithoutExtension,
                SourcePath = data.FilePath,
                AssetPath = data.FilePath
            };

            // Materials
            foreach (var srcMat in data.Materials)
            {
                var mat = new MaterialAsset
                {
                    Name = srcMat.Name,
                    AssetPath = data.FilePath,
                    SourcePath = data.FilePath
                };
                mat.InitializeStandardSlots();

                foreach (var slot in srcMat.TextureMaps)
                {
                    var targetSlot = mat.GetSlot(slot.MapType);
                    if (targetSlot != null)
                    {
                        targetSlot.BoundTexturePath = slot.FilePath;
                    }
                }

                mat.BaseColor = srcMat.BaseColor;
                mat.Metallic = srcMat.Metallic;
                mat.Roughness = srcMat.Roughness;
                mat.EmissiveColor = srcMat.EmissiveColor;
                mat.EmissiveStrength = srcMat.EmissiveStrength;
                mat.TwoSided = srcMat.TwoSided;
                model.Materials.Add(mat);
            }

            // Textures (discovered)
            foreach (var tex in data.DiscoveredTextures)
            {
                var texAsset = new TextureAsset
                {
                    Name = tex.FileName,
                    AssetPath = tex.FilePath,
                    SourcePath = tex.FilePath,
                    MapType = tex.DetectedType,
                    FileSize = tex.FileSize
                };
                model.Textures.Add(texAsset);
            }

            // Meshes / Submeshes
            var mesh = new MeshAsset
            {
                Name = data.FileNameWithoutExtension,
                AssetPath = data.FilePath,
                SourcePath = data.FilePath
            };

            foreach (var sub in data.Submeshes)
            {
                var submesh = new SubmeshAsset
                {
                    Index = sub.Index,
                    Name = sub.Name,
                    VertexCount = sub.VertexCount,
                    TriangleCount = sub.TriangleCount,
                    EngineMeshId = sub.EngineMeshId
                };

                if (sub.MaterialIndex >= 0 && sub.MaterialIndex < model.Materials.Count)
                    submesh.MaterialId = model.Materials[sub.MaterialIndex].Id;

                mesh.Submeshes.Add(submesh);
            }

            model.Meshes.Add(mesh);
            return model;
        }
    }
}
