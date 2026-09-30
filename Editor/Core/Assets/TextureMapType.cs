namespace Editor.Core.Assets
{
    /// <summary>
    /// Supported texture slot types for PBR materials. Part of the material/asset data model (shared by the
    /// runtime core and the editors), so it must stay free of UI-framework types.
    /// </summary>
    public enum TextureMapType
    {
        Albedo,         // Diffuse/Base Color
        Normal,         // Normal Map
        Metallic,       // Metallic Map
        Roughness,      // Roughness Map
        AmbientOcclusion, // AO Map
        Emissive,       // Emissive/Emission Map
        Height,         // Height/Displacement Map
        Opacity,        // Alpha/Opacity Map
        Specular,       // Specular Map (legacy)
        MetallicRoughness, // Combined Metallic-Roughness (GLTF style)
        OcclusionRoughnessMetallic, // Combined ORM Map
        Custom          // User-defined slot
    }
}
