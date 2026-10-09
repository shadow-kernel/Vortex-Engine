using System;
using System.IO;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>What an Asset Browser tile represents (drives icon, colour, type label, default action, menus).</summary>
    public enum AssetKind
    {
        Folder, ParentFolder, Primitive, Model, Texture, Material, Shader, Script, Scene, Prefab,
        AudioClip, SoundContainer, AnimationClip, UiScreen, Font, Text, BuiltInMaterial, BuiltInTexture, Vfx, BehaviorTree, Other
    }

    /// <summary>
    /// File-type knowledge of the Asset Browser (port of the WPF AssetBrowserView.BuildFileItem switch): which
    /// extensions are models / textures / audio …, the type label shown under a tile ("Model", "WAV Audio", …),
    /// the icon + colour of tiles without a thumbnail, and the small extension badge.
    /// </summary>
    public static class AssetKinds
    {
        public static readonly string[] ModelExt = { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".3ds", ".blend", ".vmesh" };
        public static readonly string[] TextureExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tga", ".dds", ".hdr", ".exr", ".psd" };
        public static readonly string[] AudioExt = { ".wav", ".mp3", ".ogg", ".flac" };
        public static readonly string[] ShaderExt = { ".hlsl", ".metal", ".glsl", ".shader", ".vshader" };
        public static readonly string[] TextExt = { ".json", ".txt", ".md", ".xml", ".yaml", ".yml", ".csv", ".ini", ".cfg", ".log" };
        public static readonly string[] FontExt = { ".ttf", ".otf" };

        /// <summary>Built-in primitives listed in the Meshes tab (WPF: Cube, Sphere, Plane, Cylinder, Cone, Capsule,
        /// Torus; plus Quad which the Avalonia GameObject menu offers). The scene renderer resolves every one.</summary>
        public static readonly string[] Primitives = { "Cube", "Sphere", "Capsule", "Cylinder", "Cone", "Plane", "Quad", "Torus" };

        public static bool Is(string ext, string[] set) => Array.IndexOf(set, ext) >= 0;

        public static AssetKind Classify(string path, bool isDirectory)
        {
            if (isDirectory) return AssetKind.Folder;
            if (string.IsNullOrEmpty(path)) return AssetKind.Other;
            if (path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return AssetKind.Primitive;
            if (path.StartsWith("Material:", StringComparison.OrdinalIgnoreCase)) return AssetKind.BuiltInMaterial;
            if (path.StartsWith("Texture:", StringComparison.OrdinalIgnoreCase)) return AssetKind.BuiltInTexture;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Is(ext, ModelExt)) return AssetKind.Model;
            if (Is(ext, TextureExt)) return AssetKind.Texture;
            if (Is(ext, AudioExt)) return AssetKind.AudioClip;
            if (Is(ext, ShaderExt)) return AssetKind.Shader;
            if (Is(ext, FontExt)) return AssetKind.Font;
            if (Is(ext, TextExt)) return AssetKind.Text;
            switch (ext)
            {
                case ".vmat": case ".mat": return AssetKind.Material;
                case ".cs": return AssetKind.Script;
                case ".vscene": return AssetKind.Scene;
                case ".ventity": case ".vprefab": return AssetKind.Prefab;
                case ".vsndc": return AssetKind.SoundContainer;
                case ".vanim": return AssetKind.AnimationClip;
                case ".vui": return AssetKind.UiScreen;
                case ".vfx": return AssetKind.Vfx;
                case ".vbt": return AssetKind.BehaviorTree;
            }
            return AssetKind.Other;
        }

        /// <summary>The second line under a tile — the same labels as the Windows editor.</summary>
        public static string TypeName(AssetKind kind, string ext)
        {
            string e = (ext ?? "").TrimStart('.').ToUpperInvariant();
            switch (kind)
            {
                case AssetKind.Folder: return "Folder";
                case AssetKind.ParentFolder: return "Up one level";
                case AssetKind.Primitive: return "Primitive";
                case AssetKind.Model: return ext == ".glb" || ext == ".gltf" ? "GLTF Model" : ext == ".vmesh" ? "Binary Mesh" : e + " Model";
                case AssetKind.Texture: return "Texture";
                case AssetKind.Material: return "Material";
                case AssetKind.Shader: return "Shader";
                case AssetKind.Script: return "Script";
                case AssetKind.Scene: return "Scene";
                case AssetKind.Prefab: return "Prefab";
                case AssetKind.AudioClip: return e + " Audio";
                case AssetKind.SoundContainer: return "Sound Container";
                case AssetKind.AnimationClip: return "Animation Clip";
                case AssetKind.UiScreen: return "UI Screen";
                case AssetKind.Font: return "Font";
                case AssetKind.Vfx: return "Visual Effect";
                case AssetKind.BehaviorTree: return "Behavior Tree";
                case AssetKind.BuiltInMaterial: return "Built-in Material";
                case AssetKind.BuiltInTexture: return "Built-in Texture";
                default: return string.IsNullOrEmpty(e) ? "File" : e + " File";
            }
        }

        /// <summary>VxIcon geometry name for tiles without a thumbnail (Theme/Icons.axaml).</summary>
        public static string Icon(AssetKind kind, string path = null)
        {
            switch (kind)
            {
                case AssetKind.Folder: return "FolderFill";
                case AssetKind.ParentFolder: return "ChevronLeft";
                case AssetKind.Primitive: return path != null && path.EndsWith("Sphere", StringComparison.OrdinalIgnoreCase) ? "Sphere" : "Cube";
                case AssetKind.Model: return "Cube";
                case AssetKind.Texture: case AssetKind.BuiltInTexture: return "Image";
                case AssetKind.Material: case AssetKind.BuiltInMaterial: return "Material";
                case AssetKind.Shader: return "Sparkle";
                case AssetKind.Script: return "Script";
                case AssetKind.Scene: return "Scene";
                case AssetKind.Prefab: return "Prefab";
                case AssetKind.AudioClip: return "Audio";
                case AssetKind.SoundContainer: return "Layers";
                case AssetKind.AnimationClip: return "Play";
                case AssetKind.UiScreen: return "LayoutSingle";
                case AssetKind.Vfx: return "Sparkle";
                case AssetKind.BehaviorTree: return "Flow";
                default: return "File";
            }
        }

        /// <summary>Theme brush key for the icon colour.</summary>
        public static string BrushKey(AssetKind kind)
        {
            switch (kind)
            {
                case AssetKind.Folder: return "VxAccentBrush";
                case AssetKind.ParentFolder: return "VxTextTertiaryBrush";
                case AssetKind.Primitive: case AssetKind.Model: return "VxTealBrush";
                case AssetKind.Texture: case AssetKind.BuiltInTexture: return "VxGreenBrush";
                case AssetKind.Material: case AssetKind.BuiltInMaterial: case AssetKind.AnimationClip: return "VxPurpleBrush";
                case AssetKind.Shader: return "VxAccentBrush";
                case AssetKind.Script: return "VxOrangeBrush";
                case AssetKind.Scene: return "VxYellowBrush";
                case AssetKind.Prefab: return "VxPinkBrush";
                case AssetKind.AudioClip: case AssetKind.SoundContainer: return "VxRedBrush";
                case AssetKind.UiScreen: return "VxTealBrush";
                case AssetKind.Vfx: return "VxOrangeBrush";
                case AssetKind.BehaviorTree: return "VxTealBrush";
                default: return "VxTextSecondaryBrush";
            }
        }

        /// <summary>Short uppercase extension badge ("GLB", "WAV", …) for source formats, where several extensions share
        /// one type (models, images, audio, code, fonts); null for folders, built-ins and the engine's own formats
        /// (.vmat, .ventity, .vanim …) whose type line already says what they are.</summary>
        public static string Badge(AssetKind kind, string ext)
        {
            switch (kind)
            {
                case AssetKind.Model: case AssetKind.Texture: case AssetKind.AudioClip: case AssetKind.Script:
                case AssetKind.Shader: case AssetKind.Font: case AssetKind.Text: case AssetKind.Other:
                    string e = (ext ?? "").TrimStart('.').ToUpperInvariant();
                    return e.Length == 0 ? null : e.Length > 5 ? e.Substring(0, 5) : e;
                default: return null;
            }
        }

        /// <summary>Kinds that a plain double-click drops into the scene.</summary>
        public static bool IsPlaceable(AssetKind kind) => kind == AssetKind.Model || kind == AssetKind.Prefab || kind == AssetKind.Primitive || kind == AssetKind.Vfx;

        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return bytes + " B";
            double v = bytes / 1024.0;
            if (v < 1024) return v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB";
            v /= 1024.0;
            if (v < 1024) return v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            return (v / 1024.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " GB";
        }
    }
}
