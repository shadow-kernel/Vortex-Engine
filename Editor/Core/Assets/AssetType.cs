namespace Editor.Core.Assets
{
    /// <summary>
    /// Defines the type of asset in the project.
    /// </summary>
    public enum AssetType
    {
        Unknown = 0,
        Scene,
        Mesh,
        Texture,
        Material,
        Prefab,
        Shader,
        Audio,
        Script,
        Font,
        UI,
        Folder,
        // Appended AFTER Folder: AssetType serializes as an int in .vmeta sidecars — inserting mid-enum
        // would silently re-type every existing Folder entry.
        Animation
    }
}
