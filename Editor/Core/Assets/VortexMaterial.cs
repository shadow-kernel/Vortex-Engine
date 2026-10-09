using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        /// <summary>Channel the metallic / roughness / AO maps are read from: null or "Auto" (default), "R", "G", "B",
        /// "A". Auto reads packed maps correctly — MetallicRoughnessTexture (glTF), OcclusionRoughnessMetallicTexture
        /// (ORM/ARM) or ONE file assigned to both metallic and roughness keep roughness in G and metallic in B,
        /// occlusion in R; separate grayscale maps read R.</summary>
        public string MetallicChannel { get; set; }
        public string RoughnessChannel { get; set; }
        public string AOChannel { get; set; }
        
        // Emissive properties
        public float EmissiveStrength { get; set; } = 0f;
        public float[] EmissiveColor { get; set; } = { 0f, 0f, 0f };
        
        // Material settings
        public bool TwoSided { get; set; } = false;
        public string BlendMode { get; set; } = "Opaque"; // Opaque, AlphaBlend, AlphaTest, Additive
        public string ShaderType { get; set; } = "StandardPBR"; // StandardPBR, Unlit, Subsurface
        /// <summary>Optional path (project-relative) to a custom shader asset (.vshader or .hlsl) assigned in the
        /// Material Editor. Null = the built-in shader for ShaderType. Round-trips via System.Text.Json.</summary>
        public string ShaderAsset { get; set; }

        /// <summary>Optional project-relative path to a FOOTSTEP sound (a .wav/.mp3/.ogg/.flac clip, or a .vsndc Sound
        /// Container for variation) assigned in the Material Editor. This is authoring DATA only — it does not affect
        /// rendering. The game's FootstepAudio script reads it via Physics.GroundStepSound(pos) and plays it when the
        /// player walks on a surface using this material, so footsteps are configured entirely in the editor (assign a
        /// material to a floor, give the material a step sound — done, no code). Null = the surface plays no step.</summary>
        public string FootstepSound { get; set; }

        // Rendering options
        public bool CastShadows { get; set; } = true;
        public bool ReceiveShadows { get; set; } = true;
        
        // UV Tiling and Offset
        public float[] UVTiling { get; set; } = { 1f, 1f };
        public float[] UVOffset { get; set; } = { 0f, 0f };

        /// <summary>Real-world size of one texture tile in metres ([width, height]) — store materials know it (Poly
        /// Haven, ambientCG). Assigning such a material to a cube or plane primitive tiles it for the object's size
        /// instead of stretching one tile over the whole object. Null = unknown.</summary>
        public float[] RealWorldSize { get; set; }
        
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
        
        
        // .vmat files store texture references relative to the material, with backslashes (the format's
        // canonical separator, so files written on Windows and macOS stay identical). Resolution accepts either
        // separator and produces the host's native path.
        private string MakeRelative(string absolutePath, string baseDirectory)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return null;
                
            try
            {
                string sep = Path.DirectorySeparatorChar.ToString();
                Uri pathUri = new Uri(absolutePath);
                Uri baseUri = new Uri(baseDirectory.EndsWith(sep) ? baseDirectory : baseDirectory + sep);
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

            if (Path.DirectorySeparatorChar != '\\')
                relativePath = relativePath.Replace('\\', '/');
                
            if (Path.IsPathRooted(relativePath))
                return relativePath;
                
            return Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        }
    }
}
