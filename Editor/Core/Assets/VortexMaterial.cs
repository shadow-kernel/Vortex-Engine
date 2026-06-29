using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Represents a PBR Material that can be saved/loaded from .vmat files.
    /// Supports full PBR workflow with all standard texture maps.
    /// </summary>
    public class VortexMaterial
    {
        public string Name { get; set; } = "New Material";
        public string Version { get; set; } = "2.0";
        
        // Base Color (RGBA 0-1)
        public float[] BaseColor { get; set; } = { 1f, 1f, 1f, 1f };
        
        // PBR Properties
        public float Metallic { get; set; } = 0f;
        public float Roughness { get; set; } = 0.5f;
        public float AmbientOcclusion { get; set; } = 1f;
        public float NormalStrength { get; set; } = 1f;
        public float HeightScale { get; set; } = 0.05f;
        public float AlphaCutoff { get; set; } = 0.5f;
        
        // Normal map format (true = DirectX, false = OpenGL)
        public bool UseDirectXNormals { get; set; } = true;
        
        // Texture paths (relative to material file)
        public string AlbedoTexture { get; set; }
        public string NormalTexture { get; set; }
        public string MetallicTexture { get; set; }
        public string RoughnessTexture { get; set; }
        public string AOTexture { get; set; }
        public string EmissiveTexture { get; set; }
        public string HeightTexture { get; set; }
        public string OpacityTexture { get; set; }
        
        // Combined texture maps (for packed textures)
        public string MetallicRoughnessTexture { get; set; }  // GLTF style
        public string OcclusionRoughnessMetallicTexture { get; set; }  // ORM maps
        
        // Emissive properties
        public float EmissiveStrength { get; set; } = 0f;
        public float[] EmissiveColor { get; set; } = { 0f, 0f, 0f };
        
        // Material settings
        public bool TwoSided { get; set; } = false;
        public string BlendMode { get; set; } = "Opaque"; // Opaque, AlphaBlend, AlphaTest, Additive
        public string ShaderType { get; set; } = "StandardPBR"; // StandardPBR, Unlit, Subsurface
        
        // Rendering options
        public bool CastShadows { get; set; } = true;
        public bool ReceiveShadows { get; set; } = true;
        
        // UV Tiling and Offset
        public float[] UVTiling { get; set; } = { 1f, 1f };
        public float[] UVOffset { get; set; } = { 0f, 0f };
        
        /// <summary>
        /// Saves the material to a .vmat file.
        /// </summary>
        public bool Save(string filePath)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(filePath, json);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving material: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Loads a material from a .vmat file.
        /// </summary>
        public static VortexMaterial Load(string filePath)
        {
            try
            {
                // Shipped game: read from the in-RAM pak; editor: read the loose .vmat file.
                string json;
                if (Editor.Core.Services.AssetVfs.IsMounted && Editor.Core.Services.AssetVfs.Contains(filePath))
                    json = Editor.Core.Services.AssetVfs.GetText(filePath);
                else if (File.Exists(filePath))
                    json = File.ReadAllText(filePath);
                else
                    return null;

                return JsonSerializer.Deserialize<VortexMaterial>(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading material: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Creates a material from WPF Color.
        /// </summary>
        public void SetBaseColor(Color color)
        {
            BaseColor = new float[]
            {
                color.R / 255f,
                color.G / 255f,
                color.B / 255f,
                color.A / 255f
            };
        }
        
        /// <summary>
        /// Gets the base color as WPF Color.
        /// </summary>
        public Color GetBaseColor()
        {
            return Color.FromArgb(
                (byte)(BaseColor[3] * 255),
                (byte)(BaseColor[0] * 255),
                (byte)(BaseColor[1] * 255),
                (byte)(BaseColor[2] * 255)
            );
        }
        
        /// <summary>
        /// Makes texture paths relative to the material file location.
        /// </summary>
        public void MakePathsRelative(string materialDirectory)
        {
            AlbedoTexture = MakeRelative(AlbedoTexture, materialDirectory);
            NormalTexture = MakeRelative(NormalTexture, materialDirectory);
            MetallicTexture = MakeRelative(MetallicTexture, materialDirectory);
            RoughnessTexture = MakeRelative(RoughnessTexture, materialDirectory);
            AOTexture = MakeRelative(AOTexture, materialDirectory);
            EmissiveTexture = MakeRelative(EmissiveTexture, materialDirectory);
            HeightTexture = MakeRelative(HeightTexture, materialDirectory);
            OpacityTexture = MakeRelative(OpacityTexture, materialDirectory);
            MetallicRoughnessTexture = MakeRelative(MetallicRoughnessTexture, materialDirectory);
            OcclusionRoughnessMetallicTexture = MakeRelative(OcclusionRoughnessMetallicTexture, materialDirectory);
        }
        
        /// <summary>
        /// Alias for MakePathsRelative for consistency.
        /// </summary>
        public void ResolvePathsRelative(string materialDirectory)
        {
            MakePathsRelative(materialDirectory);
        }
        
        /// <summary>
        /// Resolves texture paths to absolute paths.
        /// </summary>
        public void ResolvePathsAbsolute(string materialDirectory)
        {
            AlbedoTexture = ResolveAbsolute(AlbedoTexture, materialDirectory);
            NormalTexture = ResolveAbsolute(NormalTexture, materialDirectory);
            MetallicTexture = ResolveAbsolute(MetallicTexture, materialDirectory);
            RoughnessTexture = ResolveAbsolute(RoughnessTexture, materialDirectory);
            AOTexture = ResolveAbsolute(AOTexture, materialDirectory);
            EmissiveTexture = ResolveAbsolute(EmissiveTexture, materialDirectory);
            HeightTexture = ResolveAbsolute(HeightTexture, materialDirectory);
            OpacityTexture = ResolveAbsolute(OpacityTexture, materialDirectory);
            MetallicRoughnessTexture = ResolveAbsolute(MetallicRoughnessTexture, materialDirectory);
            OcclusionRoughnessMetallicTexture = ResolveAbsolute(OcclusionRoughnessMetallicTexture, materialDirectory);
        }
        
        /// <summary>
        /// Creates a UniversalMaterial from this VortexMaterial.
        /// </summary>
        public UniversalMaterial ToUniversalMaterial()
        {
            var material = new UniversalMaterial
            {
                Name = Name,
                Metallic = Metallic,
                Roughness = Roughness,
                NormalStrength = NormalStrength,
                AOStrength = AmbientOcclusion,
                EmissiveStrength = EmissiveStrength,
                TwoSided = TwoSided
            };
            
            material.BaseColor = GetBaseColor();
            
            if (EmissiveColor != null && EmissiveColor.Length >= 3)
            {
                material.EmissiveColor = Color.FromScRgb(1f, EmissiveColor[0], EmissiveColor[1], EmissiveColor[2]);
            }
            
            // Dynamic slots: create a slot ONLY for each texture the .vmat actually carries — no placeholders.
            if (!string.IsNullOrEmpty(AlbedoTexture))
                material.SetTexture(TextureMapType.Albedo, AlbedoTexture);
            if (!string.IsNullOrEmpty(NormalTexture))
                material.SetTexture(TextureMapType.Normal, NormalTexture);
            if (!string.IsNullOrEmpty(MetallicTexture))
                material.SetTexture(TextureMapType.Metallic, MetallicTexture);
            if (!string.IsNullOrEmpty(RoughnessTexture))
                material.SetTexture(TextureMapType.Roughness, RoughnessTexture);
            if (!string.IsNullOrEmpty(AOTexture))
                material.SetTexture(TextureMapType.AmbientOcclusion, AOTexture);
            if (!string.IsNullOrEmpty(EmissiveTexture))
                material.SetTexture(TextureMapType.Emissive, EmissiveTexture);
            if (!string.IsNullOrEmpty(HeightTexture))
                material.SetTexture(TextureMapType.Height, HeightTexture);
            if (!string.IsNullOrEmpty(OpacityTexture))
                material.SetTexture(TextureMapType.Opacity, OpacityTexture);
            if (!string.IsNullOrEmpty(MetallicRoughnessTexture))
                material.SetTexture(TextureMapType.MetallicRoughness, MetallicRoughnessTexture);
            if (!string.IsNullOrEmpty(OcclusionRoughnessMetallicTexture))
                material.SetTexture(TextureMapType.OcclusionRoughnessMetallic, OcclusionRoughnessMetallicTexture);

            return material;
        }
        
        /// <summary>
        /// Creates a VortexMaterial from a UniversalMaterial.
        /// </summary>
        public static VortexMaterial FromUniversalMaterial(UniversalMaterial source)
        {
            var vmat = new VortexMaterial
            {
                Name = source.Name,
                Metallic = source.Metallic,
                Roughness = source.Roughness,
                NormalStrength = source.NormalStrength,
                AmbientOcclusion = source.AOStrength,
                EmissiveStrength = source.EmissiveStrength,
                TwoSided = source.TwoSided
            };
            
            vmat.SetBaseColor(source.BaseColor);
            vmat.EmissiveColor = new[] { source.EmissiveColor.ScR, source.EmissiveColor.ScG, source.EmissiveColor.ScB };
            
            // Persist every map the material actually has (not just the old fixed 6).
            vmat.AlbedoTexture = source.GetTextureSlot(TextureMapType.Albedo)?.FilePath;
            vmat.NormalTexture = source.GetTextureSlot(TextureMapType.Normal)?.FilePath;
            vmat.MetallicTexture = source.GetTextureSlot(TextureMapType.Metallic)?.FilePath;
            vmat.RoughnessTexture = source.GetTextureSlot(TextureMapType.Roughness)?.FilePath;
            vmat.AOTexture = source.GetTextureSlot(TextureMapType.AmbientOcclusion)?.FilePath;
            vmat.EmissiveTexture = source.GetTextureSlot(TextureMapType.Emissive)?.FilePath;
            vmat.HeightTexture = source.GetTextureSlot(TextureMapType.Height)?.FilePath;
            vmat.OpacityTexture = source.GetTextureSlot(TextureMapType.Opacity)?.FilePath;
            vmat.MetallicRoughnessTexture = source.GetTextureSlot(TextureMapType.MetallicRoughness)?.FilePath;
            vmat.OcclusionRoughnessMetallicTexture = source.GetTextureSlot(TextureMapType.OcclusionRoughnessMetallic)?.FilePath;

            return vmat;
        }
        
        private string MakeRelative(string absolutePath, string baseDirectory)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return null;
                
            try
            {
                Uri pathUri = new Uri(absolutePath);
                Uri baseUri = new Uri(baseDirectory.EndsWith("\\") ? baseDirectory : baseDirectory + "\\");
                return Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString().Replace('/', '\\'));
            }
            catch
            {
                return absolutePath;
            }
        }
        
        private string ResolveAbsolute(string relativePath, string baseDirectory)
        {
            if (string.IsNullOrEmpty(relativePath))
                return null;
                
            if (Path.IsPathRooted(relativePath))
                return relativePath;
                
            return Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        }
    }
}
