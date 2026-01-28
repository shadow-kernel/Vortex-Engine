using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Shader type enumeration.
    /// </summary>
    public enum ShaderType
    {
        Standard,
        Unlit,
        Transparent,
        Custom
    }

    /// <summary>
    /// Shader property for custom parameters.
    /// </summary>
    public class ShaderProperty
    {
        public string Name { get; set; }
        public string Type { get; set; } // float, float2, float3, float4, texture2D
        public object DefaultValue { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Represents a shader asset that can be saved/loaded from .vshader files.
    /// Shaders can be assigned to materials or directly to MeshRenderers.
    /// </summary>
    public class VortexShader
    {
        public string Name { get; set; } = "New Shader";
        public string Version { get; set; } = "1.0";
        public ShaderType ShaderType { get; set; } = ShaderType.Standard;
        
        /// <summary>
        /// Path to the vertex shader source file (.hlsl)
        /// </summary>
        public string VertexShaderPath { get; set; }
        
        /// <summary>
        /// Path to the pixel/fragment shader source file (.hlsl)
        /// </summary>
        public string PixelShaderPath { get; set; }
        
        /// <summary>
        /// Inline vertex shader source (if not using external file)
        /// </summary>
        public string VertexShaderSource { get; set; }
        
        /// <summary>
        /// Inline pixel shader source (if not using external file)
        /// </summary>
        public string PixelShaderSource { get; set; }
        
        /// <summary>
        /// Custom shader properties exposed in the inspector
        /// </summary>
        public List<ShaderProperty> Properties { get; set; } = new List<ShaderProperty>();
        
        /// <summary>
        /// Render queue priority (lower = rendered first)
        /// </summary>
        public int RenderQueue { get; set; } = 2000;
        
        /// <summary>
        /// Enable depth writing
        /// </summary>
        public bool ZWrite { get; set; } = true;
        
        /// <summary>
        /// Enable depth testing
        /// </summary>
        public bool ZTest { get; set; } = true;
        
        /// <summary>
        /// Cull mode: None, Front, Back
        /// </summary>
        public string CullMode { get; set; } = "Back";
        
        /// <summary>
        /// Blend mode for transparent shaders
        /// </summary>
        public string BlendMode { get; set; } = "Opaque";
        
        /// <summary>
        /// Tags for categorization
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();
        
        /// <summary>
        /// Saves the shader to a .vshader file.
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
                System.Diagnostics.Debug.WriteLine($"Error saving shader: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Loads a shader from a .vshader file.
        /// </summary>
        public static VortexShader Load(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;
                    
                string json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<VortexShader>(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading shader: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Creates a default Standard PBR shader template.
        /// </summary>
        public static VortexShader CreateStandardPBR()
        {
            return new VortexShader
            {
                Name = "Standard PBR",
                ShaderType = ShaderType.Standard,
                Properties = new List<ShaderProperty>
                {
                    new ShaderProperty { Name = "_BaseColor", Type = "float4", DefaultValue = new float[] { 1, 1, 1, 1 }, Description = "Base Color" },
                    new ShaderProperty { Name = "_Metallic", Type = "float", DefaultValue = 0f, Description = "Metallic (0-1)" },
                    new ShaderProperty { Name = "_Roughness", Type = "float", DefaultValue = 0.5f, Description = "Roughness (0-1)" },
                    new ShaderProperty { Name = "_NormalStrength", Type = "float", DefaultValue = 1f, Description = "Normal Map Strength" },
                    new ShaderProperty { Name = "_MainTex", Type = "texture2D", DefaultValue = "white", Description = "Albedo Texture" },
                    new ShaderProperty { Name = "_NormalMap", Type = "texture2D", DefaultValue = "bump", Description = "Normal Map" },
                    new ShaderProperty { Name = "_MetallicMap", Type = "texture2D", DefaultValue = "black", Description = "Metallic Map" },
                    new ShaderProperty { Name = "_RoughnessMap", Type = "texture2D", DefaultValue = "gray", Description = "Roughness Map" }
                },
                Tags = new List<string> { "Opaque", "Standard" }
            };
        }
        
        /// <summary>
        /// Creates a default Unlit shader template.
        /// </summary>
        public static VortexShader CreateUnlit()
        {
            return new VortexShader
            {
                Name = "Unlit",
                ShaderType = ShaderType.Unlit,
                Properties = new List<ShaderProperty>
                {
                    new ShaderProperty { Name = "_BaseColor", Type = "float4", DefaultValue = new float[] { 1, 1, 1, 1 }, Description = "Color" },
                    new ShaderProperty { Name = "_MainTex", Type = "texture2D", DefaultValue = "white", Description = "Main Texture" }
                },
                Tags = new List<string> { "Opaque", "Unlit" }
            };
        }
        
        /// <summary>
        /// Creates a default Transparent shader template.
        /// </summary>
        public static VortexShader CreateTransparent()
        {
            return new VortexShader
            {
                Name = "Transparent",
                ShaderType = ShaderType.Transparent,
                BlendMode = "AlphaBlend",
                ZWrite = false,
                RenderQueue = 3000,
                Properties = new List<ShaderProperty>
                {
                    new ShaderProperty { Name = "_BaseColor", Type = "float4", DefaultValue = new float[] { 1, 1, 1, 0.5f }, Description = "Color with Alpha" },
                    new ShaderProperty { Name = "_MainTex", Type = "texture2D", DefaultValue = "white", Description = "Main Texture" }
                },
                Tags = new List<string> { "Transparent" }
            };
        }
    }
}
