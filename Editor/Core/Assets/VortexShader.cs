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

        /// <summary>Starter .hlsl source for a new shader (entry points VSMain/PSMain). The vertex layout +
        /// PerFrame/PerObject cbuffers mirror the engine's standard pipeline, so an edited shader can be assigned to
        /// a material. The user opens this in Visual Studio and customizes it.</summary>
        public static string HlslTemplate(ShaderType type)
        {
            bool unlit = type == ShaderType.Unlit;
            string psBody = unlit
                ? "    return BaseColor; // unlit: flat base color (sample a texture / add effects here)"
                : "    float3 n = normalize(i.norm);\n" +
                  "    float3 L = normalize(-LightDirection);\n" +
                  "    float ndl = saturate(dot(n, L)) * DirectionalIntensity;\n" +
                  "    float3 lit = BaseColor.rgb * (LightColor * ndl + AmbientStrength);\n" +
                  "    return float4(lit, BaseColor.a);";
            return
"// Custom shader — entry points VSMain (vertex) + PSMain (pixel). The vertex layout and the PerFrame/PerObject\n" +
"// cbuffers match the engine's standard pipeline, so this can be assigned to a material. Edit freely in VS;\n" +
"// with shader hot-reload on, re-focusing the viewport picks up your changes.\n\n" +
"cbuffer PerFrame : register(b0)\n{\n" +
"    row_major float4x4 ViewProjection;\n    float3 CameraPosition;      float Padding0;\n" +
"    float3 LightDirection;      float DirectionalIntensity;\n    float3 LightColor;          float AmbientStrength;\n" +
"    uint PointLightCount; uint SpotLightCount; uint2 FramePadding;\n};\n\n" +
"cbuffer PerObject : register(b1)\n{\n" +
"    row_major float4x4 World;\n    float4 BaseColor;\n    float Metallic; float Roughness; float AO; float NormalStrength;\n" +
"    uint HasAlbedoTexture; uint HasNormalTexture; uint HasMetallicTexture; uint HasRoughnessTexture;\n" +
"    uint HasAOTexture; uint UseDirectXNormals; uint IsUnlit; float EmissiveStrength;\n};\n\n" +
"struct VS_IN\n{\n    float3 pos : POSITION;\n    float3 norm : NORMAL;\n    float2 uv : TEXCOORD0;\n" +
"    float4 iw0 : INSTANCEWORLD0;\n    float4 iw1 : INSTANCEWORLD1;\n    float4 iw2 : INSTANCEWORLD2;\n    float4 iw3 : INSTANCEWORLD3;\n};\n\n" +
"struct PS_IN\n{\n    float4 pos : SV_POSITION;\n    float3 worldPos : TEXCOORD1;\n    float3 norm : TEXCOORD2;\n    float2 uv : TEXCOORD0;\n};\n\n" +
"PS_IN VSMain(VS_IN input)\n{\n    PS_IN o;\n    float4x4 W = float4x4(input.iw0, input.iw1, input.iw2, input.iw3);\n" +
"    float4 wp = mul(float4(input.pos, 1), W);\n    o.worldPos = wp.xyz;\n    o.pos = mul(wp, ViewProjection);\n" +
"    o.norm = normalize(mul(input.norm, (float3x3)W));\n    o.uv = input.uv;\n    return o;\n}\n\n" +
"float4 PSMain(PS_IN i) : SV_TARGET\n{\n" + psBody + "\n}\n";
        }
    }
}
