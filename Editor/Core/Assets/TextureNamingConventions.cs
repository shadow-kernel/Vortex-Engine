using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Helper class for working with texture naming conventions.
    /// Supports common patterns from various DCC tools and game engines.
    /// </summary>
    public static class TextureNamingConventions
    {
        #region Naming Patterns

        /// <summary>
        /// Common suffixes for each texture type (case-insensitive).
        /// </summary>
        public static readonly Dictionary<TextureMapType, string[]> TypeSuffixes = new Dictionary<TextureMapType, string[]>
        {
            { TextureMapType.Albedo, new[] { 
                "_albedo", "_diffuse", "_basecolor", "_color", "_col", "_d", "_diff", "_alb", "_bc" 
            }},
            { TextureMapType.Normal, new[] { 
                "_normal", "_nrm", "_nor", "_n", "_norm", "_normalmap", "_nmap", "_bump" 
            }},
            { TextureMapType.Metallic, new[] { 
                "_metallic", "_metal", "_met", "_m", "_metalness" 
            }},
            { TextureMapType.Roughness, new[] { 
                "_roughness", "_rough", "_rgh", "_r", "_glossiness", "_gloss" 
            }},
            { TextureMapType.AmbientOcclusion, new[] { 
                "_ao", "_occlusion", "_ambient", "_ambientocclusion" 
            }},
            { TextureMapType.Emissive, new[] { 
                "_emissive", "_emission", "_emit", "_e", "_glow", "_selfillum" 
            }},
            { TextureMapType.Height, new[] { 
                "_height", "_h", "_displacement", "_disp", "_heightmap" 
            }},
            { TextureMapType.Opacity, new[] { 
                "_opacity", "_alpha", "_transparency", "_a" 
            }},
            { TextureMapType.Specular, new[] { 
                "_specular", "_spec", "_s" 
            }},
            { TextureMapType.MetallicRoughness, new[] { 
                "_metallicroughness", "_mr", "_rm" 
            }},
            { TextureMapType.OcclusionRoughnessMetallic, new[] { 
                "_orm", "_arm", "_occlusionroughnessmetallic" 
            }}
        };

        /// <summary>
        /// Prefixes often used before texture type suffix.
        /// </summary>
        public static readonly string[] CommonPrefixes = { 
            "T_", "TX_", "TEX_", "Texture_", "Mat_", "Material_" 
        };

        #endregion

        #region Detection Methods

        /// <summary>
        /// Extracts the base name from a texture filename by removing type suffixes.
        /// Example: "Character_Body_Albedo.png" -> "Character_Body"
        /// </summary>
        public static string ExtractBaseName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";

            var name = Path.GetFileNameWithoutExtension(fileName);
            var lower = name.ToLowerInvariant();

            // Try to find and remove type suffix
            foreach (var kvp in TypeSuffixes)
            {
                foreach (var suffix in kvp.Value)
                {
                    if (lower.EndsWith(suffix))
                    {
                        return name.Substring(0, name.Length - suffix.Length);
                    }
                }
            }

            // Check for pattern like "name_1" or "name_01" at the end
            var numericSuffix = Regex.Match(name, @"_?\d+$");
            if (numericSuffix.Success)
            {
                return name.Substring(0, numericSuffix.Index);
            }

            return name;
        }

        /// <summary>
        /// Detects the texture type from a filename.
        /// </summary>
        public static TextureMapType DetectType(string fileName)
        {
            return DiscoveredTexture.DetectTypeFromFileName(fileName);
        }

        /// <summary>
        /// Groups textures by their base name (material name).
        /// </summary>
        public static Dictionary<string, List<(string path, TextureMapType type)>> GroupTexturesByMaterial(
            IEnumerable<string> texturePaths)
        {
            var groups = new Dictionary<string, List<(string, TextureMapType)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in texturePaths)
            {
                var fileName = Path.GetFileName(path);
                var baseName = ExtractBaseName(fileName);
                var type = DetectType(fileName);

                if (!groups.TryGetValue(baseName, out var list))
                {
                    list = new List<(string, TextureMapType)>();
                    groups[baseName] = list;
                }
                list.Add((path, type));
            }

            return groups;
        }

        /// <summary>
        /// Finds textures that belong to a specific material based on naming conventions.
        /// </summary>
        public static List<(string path, TextureMapType type)> FindTexturesForMaterial(
            IEnumerable<string> texturePaths, 
            string materialName)
        {
            var result = new List<(string, TextureMapType)>();
            var lowerMatName = materialName?.ToLowerInvariant() ?? "";

            foreach (var path in texturePaths)
            {
                var fileName = Path.GetFileName(path);
                var baseName = ExtractBaseName(fileName).ToLowerInvariant();
                var type = DetectType(fileName);

                // Match if base name contains material name or vice versa
                if (baseName.Contains(lowerMatName) || lowerMatName.Contains(baseName))
                {
                    result.Add((path, type));
                }
            }

            return result;
        }

        /// <summary>
        /// Finds the best matching texture for a specific type and material.
        /// </summary>
        public static string FindBestMatch(
            IEnumerable<string> texturePaths,
            string materialName,
            TextureMapType targetType)
        {
            var candidates = new List<(string path, int score)>();
            var lowerMatName = materialName?.ToLowerInvariant() ?? "";

            foreach (var path in texturePaths)
            {
                var fileName = Path.GetFileName(path);
                var detectedType = DetectType(fileName);
                
                if (detectedType != targetType) continue;

                var baseName = ExtractBaseName(fileName).ToLowerInvariant();
                int score = 0;

                // Exact base name match
                if (baseName == lowerMatName) score += 100;
                // Base name contains material name
                else if (baseName.Contains(lowerMatName)) score += 50;
                // Material name contains base name
                else if (lowerMatName.Contains(baseName)) score += 30;

                // Bonus for longer matches (more specific)
                score += Math.Min(baseName.Length, 20);

                candidates.Add((path, score));
            }

            return candidates.OrderByDescending(c => c.score).FirstOrDefault().path;
        }

        #endregion

        #region Validation

        /// <summary>
        /// Checks if a texture name follows a recognized convention.
        /// </summary>
        public static bool FollowsNamingConvention(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            var type = DetectType(fileName);
            return type != TextureMapType.Custom;
        }

        /// <summary>
        /// Suggests a proper name for a texture based on conventions.
        /// </summary>
        public static string SuggestProperName(string currentName, string materialName, TextureMapType type)
        {
            if (string.IsNullOrEmpty(materialName)) 
                materialName = "Material";

            // Clean up material name
            var cleanName = Regex.Replace(materialName, @"[^\w]", "_");

            // Get the appropriate suffix
            var suffixes = TypeSuffixes.TryGetValue(type, out var s) ? s : new[] { "_custom" };
            var suffix = suffixes.FirstOrDefault() ?? "_unknown";

            return $"{cleanName}{suffix}";
        }

        #endregion
    }
}
