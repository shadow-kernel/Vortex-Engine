using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Editor.DllWrapper;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Universal model parser that can read model data from any supported format.
    /// Provides consistent data structures regardless of source format.
    /// </summary>
    public class UniversalModelParser
    {
        private static UniversalModelParser _instance;
        public static UniversalModelParser Instance => _instance ?? (_instance = new UniversalModelParser());

        private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".hdr", ".tif", ".tiff" };
        private static readonly string[] TextureSubfolders = { "textures", "Textures", "texture", "Texture", "tex", "maps", "Materials", "material" };

        #region Main Parse Method

        /// <summary>
        /// Parses a 3D model file and returns a UniversalModelData structure.
        /// </summary>
        public UniversalModelData ParseModel(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Model file not found", filePath);

            var modelData = new UniversalModelData { FilePath = filePath };
            var format = UniversalModelData.DetectFormat(Path.GetExtension(filePath));

            try
            {
                switch (format)
                {
                    case ModelFormat.OBJ:
                        ParseObjModel(modelData);
                        break;
                    
                    case ModelFormat.FBX:
                    case ModelFormat.DAE:
                    case ModelFormat.ThreeDS:
                    case ModelFormat.Blend:
                        ParseAssimpModel(modelData);
                        break;
                    
                    case ModelFormat.GLTF:
                    case ModelFormat.GLB:
                        // GLTF not supported by Assimp 3.0, provide helpful error
                        throw new NotSupportedException(
                            "GLTF/GLB format requires Assimp 4.0+. Please convert to OBJ or FBX.");
                    
                    case ModelFormat.VMesh:
                        ParseVMeshModel(modelData);
                        break;
                    
                    default:
                        throw new NotSupportedException($"Unsupported model format: {format}");
                }

                // Discover textures in the model's directory
                DiscoverTextures(modelData);

                // Auto-assign textures to materials
                AutoAssignTextures(modelData);

                modelData.IsLoaded = true;
                modelData.RefreshStats();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ModelParser] Error parsing {filePath}: {ex.Message}");
                throw;
            }

            return modelData;
        }

        #endregion

        #region OBJ Parser

        private void ParseObjModel(UniversalModelData modelData)
        {
            var objPath = modelData.FilePath;
            var directory = modelData.Directory;

            System.Diagnostics.Debug.WriteLine($"[OBJ] Parsing OBJ file: {objPath}");
            System.Diagnostics.Debug.WriteLine($"[OBJ] Directory: {directory}");

            // First, find and parse the MTL file
            var mtlPath = FindMtlFile(objPath, directory);
            var parsedMaterials = new List<ParsedMtlMaterial>();
            
            if (!string.IsNullOrEmpty(mtlPath) && File.Exists(mtlPath))
            {
                System.Diagnostics.Debug.WriteLine($"[OBJ] Parsing MTL file: {mtlPath}");
                parsedMaterials = ParseMtlFile(mtlPath, directory);
                System.Diagnostics.Debug.WriteLine($"[OBJ] Parsed {parsedMaterials.Count} materials from MTL");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[OBJ] No MTL file found or MTL path is null");
            }

            // Parse the OBJ file for geometry info
            var objGroups = ParseObjGeometry(objPath);
            System.Diagnostics.Debug.WriteLine($"[OBJ] Found {objGroups.Count} groups/objects in OBJ");

            // Create materials from parsed MTL data
            foreach (var mtlMat in parsedMaterials)
            {
                var material = CreateMaterialFromMtl(mtlMat, modelData);
                material.Index = modelData.Materials.Count;
                modelData.Materials.Add(material);
                System.Diagnostics.Debug.WriteLine($"[OBJ] Added material: {material.Name} with {material.AssignedTextureCount} textures");
            }

            // If no materials found, create a default one
            if (modelData.Materials.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[OBJ] No materials found, creating default");
                var defaultMat = new UniversalMaterial
                {
                    Index = 0,
                    Name = "Default",
                    BaseColor = Colors.White
                };
                defaultMat.InitializeStandardSlots();
                modelData.Materials.Add(defaultMat);
            }

            // Create submeshes from OBJ groups
            foreach (var group in objGroups)
            {
                var submesh = new SubmeshData
                {
                    Index = modelData.Submeshes.Count,
                    Name = group.Name,
                    VertexCount = group.VertexCount,
                    TriangleCount = group.FaceCount,
                    MaterialIndex = FindMaterialIndex(modelData.Materials, group.MaterialName)
                };
                modelData.Submeshes.Add(submesh);
            }

            // If no groups found, create a single submesh
            if (modelData.Submeshes.Count == 0)
            {
                modelData.Submeshes.Add(new SubmeshData
                {
                    Index = 0,
                    Name = modelData.FileNameWithoutExtension,
                    MaterialIndex = 0
                });
            }
        }

        /// <summary>
        /// Parsed group/object from OBJ file.
        /// </summary>
        private class ObjGroup
        {
            public string Name { get; set; }
            public string MaterialName { get; set; }
            public int VertexCount { get; set; }
            public int FaceCount { get; set; }
        }

        private List<ObjGroup> ParseObjGeometry(string objPath)
        {
            var groups = new List<ObjGroup>();
            ObjGroup currentGroup = null;
            string currentMaterial = null;
            int totalVertices = 0;
            int groupStartVertex = 0;
            int currentFaceCount = 0;

            try
            {
                var lines = File.ReadAllLines(objPath);
                
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                        continue;

                    if (line.StartsWith("v "))
                    {
                        totalVertices++;
                    }
                    else if (line.StartsWith("g ") || line.StartsWith("o "))
                    {
                        // Save previous group
                        if (currentGroup != null)
                        {
                            currentGroup.VertexCount = totalVertices - groupStartVertex;
                            currentGroup.FaceCount = currentFaceCount;
                            groups.Add(currentGroup);
                        }

                        // Start new group
                        var name = line.Substring(2).Trim();
                        currentGroup = new ObjGroup
                        {
                            Name = name,
                            MaterialName = currentMaterial
                        };
                        groupStartVertex = totalVertices;
                        currentFaceCount = 0;
                    }
                    else if (line.StartsWith("usemtl "))
                    {
                        currentMaterial = line.Substring(7).Trim();
                        if (currentGroup != null)
                            currentGroup.MaterialName = currentMaterial;
                        
                        // If no group exists yet, create one with material name
                        if (currentGroup == null)
                        {
                            currentGroup = new ObjGroup
                            {
                                Name = currentMaterial,
                                MaterialName = currentMaterial
                            };
                            groupStartVertex = totalVertices;
                        }
                    }
                    else if (line.StartsWith("f "))
                    {
                        if (currentGroup == null)
                        {
                            currentGroup = new ObjGroup
                            {
                                Name = "default",
                                MaterialName = currentMaterial ?? "default"
                            };
                            groupStartVertex = totalVertices;
                        }
                        currentFaceCount++;
                    }
                }

                // Save last group
                if (currentGroup != null)
                {
                    currentGroup.VertexCount = totalVertices - groupStartVertex;
                    currentGroup.FaceCount = currentFaceCount;
                    groups.Add(currentGroup);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ObjParser] Error: {ex.Message}");
            }

            return groups;
        }

        private string FindMtlFile(string objPath, string directory)
        {
            try
            {
                // First, look for mtllib directive in OBJ
                var objLines = File.ReadAllLines(objPath);
                foreach (var line in objLines)
                {
                    if (line.StartsWith("mtllib ", StringComparison.OrdinalIgnoreCase))
                    {
                        var mtlFileName = line.Substring(7).Trim();
                        System.Diagnostics.Debug.WriteLine($"[MTL] Found mtllib directive: {mtlFileName}");
                        
                        // Try the exact path first
                        var mtlPath = Path.Combine(directory, mtlFileName);
                        if (File.Exists(mtlPath))
                        {
                            System.Diagnostics.Debug.WriteLine($"[MTL] Found MTL file at: {mtlPath}");
                            return mtlPath;
                        }
                        
                        // Try with normalized path separators
                        mtlFileName = mtlFileName.Replace('/', Path.DirectorySeparatorChar)
                                                  .Replace('\\', Path.DirectorySeparatorChar);
                        mtlPath = Path.Combine(directory, mtlFileName);
                        if (File.Exists(mtlPath))
                        {
                            System.Diagnostics.Debug.WriteLine($"[MTL] Found MTL file at: {mtlPath}");
                            return mtlPath;
                        }
                        
                        // Try just the filename part
                        var justName = Path.GetFileName(mtlFileName);
                        mtlPath = Path.Combine(directory, justName);
                        if (File.Exists(mtlPath))
                        {
                            System.Diagnostics.Debug.WriteLine($"[MTL] Found MTL file at: {mtlPath}");
                            return mtlPath;
                        }
                    }
                }

                // Look for MTL files in directory
                var mtlFiles = Directory.GetFiles(directory, "*.mtl");
                System.Diagnostics.Debug.WriteLine($"[MTL] Found {mtlFiles.Length} MTL files in directory");
                
                foreach (var mtlFile in mtlFiles)
                {
                    System.Diagnostics.Debug.WriteLine($"[MTL]   - {Path.GetFileName(mtlFile)}");
                }
                
                if (mtlFiles.Length == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[MTL] No MTL files found in: {directory}");
                    return null;
                }
                
                // Look for MTL files with similar name to OBJ
                var baseName = Path.GetFileNameWithoutExtension(objPath);
                System.Diagnostics.Debug.WriteLine($"[MTL] Looking for MTL matching base name: {baseName}");
                
                // Try exact match first
                var exactMatch = mtlFiles.FirstOrDefault(f => 
                    Path.GetFileNameWithoutExtension(f).Equals(baseName, StringComparison.OrdinalIgnoreCase));
                if (exactMatch != null) 
                {
                    System.Diagnostics.Debug.WriteLine($"[MTL] Found exact match: {exactMatch}");
                    return exactMatch;
                }
                
                // Extract core name (without special characters like parentheses)
                string SimplifyName(string name)
                {
                    // Remove common suffixes and special chars
                    return new string(name.ToLowerInvariant()
                        .Replace("(wavefront obj)", "")
                        .Replace("-", "_")
                        .Where(c => char.IsLetterOrDigit(c) || c == '_')
                        .ToArray())
                        .Trim('_');
                }
                
                var simpleBaseName = SimplifyName(baseName);
                System.Diagnostics.Debug.WriteLine($"[MTL] Simplified base name: {simpleBaseName}");
                
                // Try to find MTL that contains the base name or simplified name
                foreach (var mtlFile in mtlFiles)
                {
                    var mtlName = Path.GetFileNameWithoutExtension(mtlFile);
                    var simpleMtlName = SimplifyName(mtlName);
                    
                    if (simpleMtlName.Contains(simpleBaseName) || simpleBaseName.Contains(simpleMtlName) ||
                        mtlName.Contains(baseName) || baseName.Contains(mtlName))
                    {
                        System.Diagnostics.Debug.WriteLine($"[MTL] Found match: {mtlFile}");
                        return mtlFile;
                    }
                }

                // Return any MTL file as fallback
                System.Diagnostics.Debug.WriteLine($"[MTL] Using first MTL file as fallback: {mtlFiles[0]}");
                return mtlFiles[0];
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region MTL Parser

        private class ParsedMtlMaterial
        {
            public string Name { get; set; }
            public float[] DiffuseColor { get; set; } // Kd
            public float[] SpecularColor { get; set; } // Ks
            public float[] AmbientColor { get; set; } // Ka
            public float[] EmissiveColor { get; set; } // Ke
            public float SpecularExponent { get; set; } = 100f; // Ns
            public float Opacity { get; set; } = 1f; // d or Tr
            public float Metallic { get; set; } = 0f; // Pm (PBR extension)
            public float Roughness { get; set; } = 0.5f; // Pr (PBR extension)
            
            // Texture maps
            public string AlbedoMap { get; set; } // map_Kd
            public string NormalMap { get; set; } // map_Bump, bump, norm, map_Kn
            public string SpecularMap { get; set; } // map_Ks
            public string RoughnessMap { get; set; } // map_Pr, map_Ns
            public string MetallicMap { get; set; } // map_Pm
            public string AOMap { get; set; } // map_Ka
            public string EmissiveMap { get; set; } // map_Ke
            public string OpacityMap { get; set; } // map_d
            public string DisplacementMap { get; set; } // disp
        }

        private List<ParsedMtlMaterial> ParseMtlFile(string mtlPath, string modelDirectory)
        {
            var materials = new List<ParsedMtlMaterial>();
            ParsedMtlMaterial current = null;
            var mtlDirectory = Path.GetDirectoryName(mtlPath);

            System.Diagnostics.Debug.WriteLine($"[MTL] Starting to parse MTL file: {mtlPath}");
            System.Diagnostics.Debug.WriteLine($"[MTL] MTL directory: {mtlDirectory}");
            System.Diagnostics.Debug.WriteLine($"[MTL] Model directory: {modelDirectory}");

            try
            {
                var lines = File.ReadAllLines(mtlPath);
                System.Diagnostics.Debug.WriteLine($"[MTL] Read {lines.Length} lines from MTL file");
                
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                        continue;

                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    var key = parts[0].ToLowerInvariant();
                    
                    if (key == "newmtl")
                    {
                        if (current != null)
                        {
                            materials.Add(current);
                            System.Diagnostics.Debug.WriteLine($"[MTL] Saved material: {current.Name} (Albedo: {current.AlbedoMap ?? "none"}, Normal: {current.NormalMap ?? "none"})");
                        }
                        
                        var materialName = string.Join(" ", parts.Skip(1));
                        System.Diagnostics.Debug.WriteLine($"[MTL] Found new material: {materialName}");
                        
                        current = new ParsedMtlMaterial
                        {
                            Name = materialName
                        };
                        continue;
                    }

                    if (current == null) continue;

                    // Resolve texture paths - handles relative paths like "..\filename.jpg"
                    string ResolveTexture(string texName)
                    {
                        if (string.IsNullOrEmpty(texName)) return null;
                        
                        // Clean up texture name (remove -options like -bm 1.0)
                        var cleanName = texName.Trim();
                        
                        // Handle options like "-bm 1.0 filename.jpg"
                        if (cleanName.StartsWith("-"))
                        {
                            // Find the last space and take everything after it as filename
                            var idx = cleanName.LastIndexOf(' ');
                            if (idx > 0) cleanName = cleanName.Substring(idx + 1).Trim();
                        }
                        
                        // Normalize path separators
                        cleanName = cleanName.Replace('/', Path.DirectorySeparatorChar)
                                             .Replace('\\', Path.DirectorySeparatorChar);
                        
                        // Get just the filename without any path
                        var justFileName = Path.GetFileName(cleanName);
                        
                        // Try many different path combinations
                        var pathsToTry = new List<string>();
                        
                        // 1. Try the exact path relative to MTL directory
                        try
                        {
                            var exactPath = Path.GetFullPath(Path.Combine(mtlDirectory, cleanName));
                            pathsToTry.Add(exactPath);
                        }
                        catch { }
                        
                        // 2. Try the exact path relative to model directory
                        try
                        {
                            var exactPath = Path.GetFullPath(Path.Combine(modelDirectory, cleanName));
                            pathsToTry.Add(exactPath);
                        }
                        catch { }
                        
                        // 3. Try just the filename in MTL directory (most common case when relative paths are wrong)
                        pathsToTry.Add(Path.Combine(mtlDirectory, justFileName));
                        
                        // 4. Try just the filename in model directory
                        pathsToTry.Add(Path.Combine(modelDirectory, justFileName));
                        
                        // 5. Try in textures subfolder of MTL directory
                        pathsToTry.Add(Path.Combine(mtlDirectory, "textures", justFileName));
                        pathsToTry.Add(Path.Combine(mtlDirectory, "Textures", justFileName));
                        
                        // 6. Try in textures subfolder of model directory
                        pathsToTry.Add(Path.Combine(modelDirectory, "textures", justFileName));
                        pathsToTry.Add(Path.Combine(modelDirectory, "Textures", justFileName));
                        
                        // 7. Try parent directory (in case MTL is in subfolder)
                        var parentDir = Path.GetDirectoryName(mtlDirectory);
                        if (!string.IsNullOrEmpty(parentDir))
                        {
                            pathsToTry.Add(Path.Combine(parentDir, justFileName));
                            pathsToTry.Add(Path.Combine(parentDir, "textures", justFileName));
                        }
                        
                        // 8. Try with different case variations of the filename
                        pathsToTry.Add(Path.Combine(mtlDirectory, justFileName.ToLowerInvariant()));
                        pathsToTry.Add(Path.Combine(modelDirectory, justFileName.ToLowerInvariant()));

                        // Check each path
                        foreach (var p in pathsToTry)
                        {
                            try
                            {
                                if (File.Exists(p)) 
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MTL] Found texture: {justFileName} at {p}");
                                    return p;
                                }
                            }
                            catch { }
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[MTL] Texture NOT FOUND: {texName} (tried {pathsToTry.Count} paths)");
                        return null;
                    }

                    float ParseFloat(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
                    
                    float[] ParseColor(string[] p) => p.Length >= 4 
                        ? new[] { ParseFloat(p[1]), ParseFloat(p[2]), ParseFloat(p[3]) }
                        : new[] { 1f, 1f, 1f };

                    switch (key)
                    {
                        // Colors
                        case "kd": current.DiffuseColor = ParseColor(parts); break;
                        case "ks": current.SpecularColor = ParseColor(parts); break;
                        case "ka": current.AmbientColor = ParseColor(parts); break;
                        case "ke": current.EmissiveColor = ParseColor(parts); break;
                        
                        // Properties
                        case "ns": current.SpecularExponent = ParseFloat(parts[1]); break;
                        case "d": current.Opacity = ParseFloat(parts[1]); break;
                        case "tr": current.Opacity = 1f - ParseFloat(parts[1]); break;
                        case "pm": current.Metallic = ParseFloat(parts[1]); break;
                        case "pr": current.Roughness = ParseFloat(parts[1]); break;
                        
                        // Texture maps
                        case "map_kd": current.AlbedoMap = ResolveTexture(string.Join(" ", parts.Skip(1))); break;
                        case "map_bump":
                        case "bump":
                        case "map_kn":
                        case "norm":
                            current.NormalMap = ResolveTexture(string.Join(" ", parts.Skip(1))); 
                            break;
                        case "map_ks": current.SpecularMap = ResolveTexture(string.Join(" ", parts.Skip(1))); break;
                        case "map_pr":
                        case "map_ns": 
                            current.RoughnessMap = ResolveTexture(string.Join(" ", parts.Skip(1))); 
                            break;
                        case "map_pm": current.MetallicMap = ResolveTexture(string.Join(" ", parts.Skip(1))); break;
                        case "map_ka": current.AOMap = ResolveTexture(string.Join(" ", parts.Skip(1))); break;
                        case "map_ke": current.EmissiveMap = ResolveTexture(string.Join(" ", parts.Skip(1))); break;
                        case "map_d": current.OpacityMap = ResolveTexture(string.Join(" ", parts.Skip(1))); break;
                        case "disp": current.DisplacementMap = ResolveTexture(string.Join(" ", parts.Skip(1))); break;
                    }
                }

                if (current != null)
                {
                    materials.Add(current);
                    System.Diagnostics.Debug.WriteLine($"[MTL] Saved final material: {current.Name}");
                }
                
                System.Diagnostics.Debug.WriteLine($"[MTL] Finished parsing, found {materials.Count} materials");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MtlParser] Error: {ex.Message}");
            }

            return materials;
        }

        private UniversalMaterial CreateMaterialFromMtl(ParsedMtlMaterial mtl, UniversalModelData modelData)
        {
            System.Diagnostics.Debug.WriteLine($"[MTL] Creating UniversalMaterial from: {mtl.Name}");
            
            var material = new UniversalMaterial
            {
                Name = mtl.Name,
                Metallic = mtl.Metallic,
                Roughness = mtl.SpecularExponent > 0 ? 1f - Math.Min(mtl.SpecularExponent / 1000f, 1f) : mtl.Roughness
            };

            // Set base color from diffuse
            if (mtl.DiffuseColor != null && mtl.DiffuseColor.Length >= 3)
            {
                material.BaseColor = Color.FromScRgb(1f, 
                    mtl.DiffuseColor[0], 
                    mtl.DiffuseColor[1], 
                    mtl.DiffuseColor[2]);
            }

            // Set emissive
            if (mtl.EmissiveColor != null && mtl.EmissiveColor.Length >= 3)
            {
                var emSum = mtl.EmissiveColor[0] + mtl.EmissiveColor[1] + mtl.EmissiveColor[2];
                if (emSum > 0.01f)
                {
                    material.EmissiveColor = Color.FromScRgb(1f,
                        mtl.EmissiveColor[0],
                        mtl.EmissiveColor[1],
                        mtl.EmissiveColor[2]);
                    material.EmissiveStrength = 1f;
                }
            }

            // Initialize texture slots
            material.InitializeStandardSlots();

            // Assign textures from MTL
            if (!string.IsNullOrEmpty(mtl.AlbedoMap))
            {
                System.Diagnostics.Debug.WriteLine($"[MTL]   Setting Albedo: {mtl.AlbedoMap}");
                material.SetTexture(TextureMapType.Albedo, mtl.AlbedoMap);
                RegisterDiscoveredTexture(modelData, mtl.AlbedoMap, TextureMapType.Albedo);
            }
            
            if (!string.IsNullOrEmpty(mtl.NormalMap))
            {
                System.Diagnostics.Debug.WriteLine($"[MTL]   Setting Normal: {mtl.NormalMap}");
                material.SetTexture(TextureMapType.Normal, mtl.NormalMap);
                RegisterDiscoveredTexture(modelData, mtl.NormalMap, TextureMapType.Normal);
            }
            
            if (!string.IsNullOrEmpty(mtl.MetallicMap))
            {
                System.Diagnostics.Debug.WriteLine($"[MTL]   Setting Metallic: {mtl.MetallicMap}");
                material.SetTexture(TextureMapType.Metallic, mtl.MetallicMap);
                RegisterDiscoveredTexture(modelData, mtl.MetallicMap, TextureMapType.Metallic);
            }
            
            if (!string.IsNullOrEmpty(mtl.RoughnessMap))
            {
                System.Diagnostics.Debug.WriteLine($"[MTL]   Setting Roughness: {mtl.RoughnessMap}");
                material.SetTexture(TextureMapType.Roughness, mtl.RoughnessMap);
                RegisterDiscoveredTexture(modelData, mtl.RoughnessMap, TextureMapType.Roughness);
            }
            else if (!string.IsNullOrEmpty(mtl.SpecularMap))
            {
                // Use specular map as roughness fallback (common in Blender exports)
                System.Diagnostics.Debug.WriteLine($"[MTL]   Setting Roughness from Specular: {mtl.SpecularMap}");
                material.SetTexture(TextureMapType.Roughness, mtl.SpecularMap);
                RegisterDiscoveredTexture(modelData, mtl.SpecularMap, TextureMapType.Roughness);
            }
            
            if (!string.IsNullOrEmpty(mtl.AOMap))
            {
                System.Diagnostics.Debug.WriteLine($"[MTL]   Setting AO: {mtl.AOMap}");
                material.SetTexture(TextureMapType.AmbientOcclusion, mtl.AOMap);
                RegisterDiscoveredTexture(modelData, mtl.AOMap, TextureMapType.AmbientOcclusion);
            }
            
            if (!string.IsNullOrEmpty(mtl.EmissiveMap))
            {
                System.Diagnostics.Debug.WriteLine($"[MTL]   Setting Emissive: {mtl.EmissiveMap}");
                material.SetTexture(TextureMapType.Emissive, mtl.EmissiveMap);
                RegisterDiscoveredTexture(modelData, mtl.EmissiveMap, TextureMapType.Emissive);
            }

            if (!string.IsNullOrEmpty(mtl.OpacityMap))
            {
                System.Diagnostics.Debug.WriteLine($"[MTL]   Setting Opacity: {mtl.OpacityMap}");
                material.SetTexture(TextureMapType.Opacity, mtl.OpacityMap);
                RegisterDiscoveredTexture(modelData, mtl.OpacityMap, TextureMapType.Opacity);
            }

            if (!string.IsNullOrEmpty(mtl.DisplacementMap))
            {
                System.Diagnostics.Debug.WriteLine($"[MTL]   Setting Height/Displacement: {mtl.DisplacementMap}");
                material.SetTexture(TextureMapType.Height, mtl.DisplacementMap);
                RegisterDiscoveredTexture(modelData, mtl.DisplacementMap, TextureMapType.Height);
            }

            System.Diagnostics.Debug.WriteLine($"[MTL]   Material created with {material.AssignedTextureCount} textures");
            return material;
        }

        private void RegisterDiscoveredTexture(UniversalModelData modelData, string texturePath, TextureMapType mapType)
        {
            if (modelData == null || string.IsNullOrEmpty(texturePath))
                return;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(texturePath);
            }
            catch
            {
                fullPath = texturePath;
            }

            var existing = modelData.DiscoveredTextures
                .FirstOrDefault(t => string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (existing.DetectedType == TextureMapType.Custom && mapType != TextureMapType.Custom)
                {
                    existing.DetectedType = mapType;
                }
                if (existing.FileSize == 0)
                {
                    try { existing.FileSize = new FileInfo(fullPath).Length; } catch { }
                }
                return;
            }

            var discovered = new DiscoveredTexture
            {
                FilePath = fullPath,
                DetectedType = mapType
            };

            try { discovered.FileSize = new FileInfo(fullPath).Length; } catch { }
            discovered.LoadPreview();
            modelData.DiscoveredTextures.Add(discovered);
        }

        private int FindMaterialIndex(IList<UniversalMaterial> materials, string materialName)
        {
            if (string.IsNullOrEmpty(materialName)) return 0;
            
            for (int i = 0; i < materials.Count; i++)
            {
                if (string.Equals(materials[i].Name, materialName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        #endregion

        #region Assimp-based Parser (FBX, DAE, etc.)

        private void ParseAssimpModel(UniversalModelData modelData)
        {
            // Check Assimp availability
            if (!VortexAPI.IsAssimpAvailable())
            {
                throw new InvalidOperationException(
                    "Assimp library is not available. Cannot import this format.");
            }

            try
            {
                // Get submesh data from engine
                var submeshData = VortexAPI.ImportModelWithMaterialsFromFile(modelData.FilePath);
                
                if (submeshData == null || submeshData.Length == 0)
                {
                    throw new InvalidOperationException("Failed to import model via Assimp.");
                }

                // Get submesh names
                string[] submeshNames = null;
                try
                {
                    submeshNames = VortexAPI.GetSubmeshNames(modelData.FilePath, submeshData.Length);
                }
                catch { }

                // Create materials and submeshes
                var materialCache = new Dictionary<long, int>(); // MaterialId -> Index

                for (int i = 0; i < submeshData.Length; i++)
                {
                    var data = submeshData[i];
                    
                    // Create or reuse material
                    int materialIndex;
                    if (materialCache.TryGetValue(data.MaterialId, out materialIndex))
                    {
                        // Material already exists
                    }
                    else
                    {
                        // Create new material
                        var material = new UniversalMaterial
                        {
                            Index = modelData.Materials.Count,
                            Name = submeshNames != null && i < submeshNames.Length 
                                ? submeshNames[i] 
                                : $"Material_{modelData.Materials.Count}",
                            EngineMaterialId = data.MaterialId
                        };
                        material.InitializeStandardSlots();
                        materialIndex = modelData.Materials.Count;
                        materialCache[data.MaterialId] = materialIndex;
                        modelData.Materials.Add(material);
                    }

                    // Create submesh
                    var submesh = new SubmeshData
                    {
                        Index = i,
                        Name = submeshNames != null && i < submeshNames.Length 
                            ? submeshNames[i] 
                            : $"Submesh_{i}",
                        EngineMeshId = data.MeshId,
                        MaterialIndex = materialIndex
                    };
                    modelData.Submeshes.Add(submesh);
                }

                // If no materials, add default
                if (modelData.Materials.Count == 0)
                {
                    var defaultMat = new UniversalMaterial
                    {
                        Index = 0,
                        Name = "Default"
                    };
                    defaultMat.InitializeStandardSlots();
                    modelData.Materials.Add(defaultMat);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AssimpParser] Error: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region VMesh Parser

        private void ParseVMeshModel(UniversalModelData modelData)
        {
            try
            {
                long meshId = VortexAPI.LoadVMeshFromFile(modelData.FilePath);
                
                if (meshId < 0)
                {
                    throw new InvalidOperationException("Failed to load VMesh file.");
                }

                // Create default material
                var material = new UniversalMaterial
                {
                    Index = 0,
                    Name = "Default"
                };
                material.InitializeStandardSlots();
                modelData.Materials.Add(material);

                // Create single submesh
                var submesh = new SubmeshData
                {
                    Index = 0,
                    Name = modelData.FileNameWithoutExtension,
                    EngineMeshId = meshId,
                    MaterialIndex = 0
                };
                modelData.Submeshes.Add(submesh);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VMeshParser] Error: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Texture Discovery

        /// <summary>
        /// Discovers all texture files in and around the model directory.
        /// </summary>
        private void DiscoverTextures(UniversalModelData modelData)
        {
            var directory = modelData.Directory;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                System.Diagnostics.Debug.WriteLine($"[Textures] Directory not found: {directory}");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Textures] Discovering textures in: {directory}");
            var foundTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Search directories
            var searchDirs = new List<string> { directory };
            
            // Add common subfolders
            foreach (var sub in TextureSubfolders)
            {
                var subPath = Path.Combine(directory, sub);
                if (Directory.Exists(subPath))
                    searchDirs.Add(subPath);
            }

            // Add parent directory
            var parentDir = Path.GetDirectoryName(directory);
            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
            {
                searchDirs.Add(parentDir);
                
                // Also check parent's texture subfolders
                foreach (var sub in TextureSubfolders)
                {
                    var subPath = Path.Combine(parentDir, sub);
                    if (Directory.Exists(subPath))
                        searchDirs.Add(subPath);
                }
            }

            System.Diagnostics.Debug.WriteLine($"[Textures] Searching {searchDirs.Count} directories");

            // Find all textures
            foreach (var dir in searchDirs.Distinct())
            {
                try
                {
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (TextureExtensions.Contains(ext))
                        {
                            foundTextures.Add(file);
                        }
                    }
                }
                catch { }
            }

            System.Diagnostics.Debug.WriteLine($"[Textures] Found {foundTextures.Count} texture files");

            // Create DiscoveredTexture objects
            foreach (var texPath in foundTextures.OrderBy(t => Path.GetFileName(t)))
            {
                try
                {
                    var existing = modelData.DiscoveredTextures
                        .FirstOrDefault(t => string.Equals(t.FilePath, texPath, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        // If we already registered it (e.g. via MTL), only fill missing metadata
                        if (existing.DetectedType == TextureMapType.Custom)
                        {
                            existing.DetectedType = DiscoveredTexture.DetectTypeFromFileName(texPath);
                        }
                        if (existing.FileSize == 0)
                        {
                            try { existing.FileSize = new FileInfo(texPath).Length; } catch { }
                        }
                        if (existing.Preview == null)
                            existing.LoadPreview();
                        continue;
                    }

                    var fileInfo = new FileInfo(texPath);
                    var discovered = new DiscoveredTexture
                    {
                        FilePath = texPath,
                        FileSize = fileInfo.Length,
                        DetectedType = DiscoveredTexture.DetectTypeFromFileName(texPath)
                    };
                    discovered.LoadPreview();
                    modelData.DiscoveredTextures.Add(discovered);
                    System.Diagnostics.Debug.WriteLine($"[Textures]   {Path.GetFileName(texPath)} -> {discovered.DetectedType}");
                }
                catch { }
            }
        }

        #endregion

        #region Auto-Assign Textures

        /// <summary>
        /// Automatically assigns discovered textures to materials based on naming conventions.
        /// </summary>
        public void AutoAssignTextures(UniversalModelData modelData)
        {
            foreach (var material in modelData.Materials)
            {
                AutoAssignTexturesForMaterial(modelData, material);
            }
        }

        public void AutoAssignTexturesForMaterial(UniversalModelData modelData, UniversalMaterial material)
        {
            var matName = material.Name?.ToLowerInvariant() ?? "";
            var assignedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Collect already assigned textures
            foreach (var slot in material.TextureMaps)
            {
                if (slot.IsAssigned)
                    assignedPaths.Add(slot.FilePath);
            }

            // Try to find matching textures for each slot
            foreach (var slot in material.TextureMaps)
            {
                if (slot.IsAssigned) continue; // Skip already assigned
                if (slot.MapType == TextureMapType.Custom) continue; // Skip custom slots

                var bestMatch = FindBestTextureMatch(modelData.DiscoveredTextures, matName, slot.MapType, assignedPaths);
                if (bestMatch != null)
                {
                    slot.FilePath = bestMatch.FilePath;
                    assignedPaths.Add(bestMatch.FilePath);
                }
            }

            material.RefreshStats();
        }

        private DiscoveredTexture FindBestTextureMatch(
            IEnumerable<DiscoveredTexture> textures,
            string materialName,
            TextureMapType targetType,
            HashSet<string> excludePaths)
        {
            // Filter to textures of matching type
            var matchingType = textures
                .Where(t => t.DetectedType == targetType && !excludePaths.Contains(t.FilePath))
                .ToList();

            if (matchingType.Count == 0) return null;

            // If only one match, use it
            if (matchingType.Count == 1) return matchingType[0];

            // Try to match by material name
            if (!string.IsNullOrEmpty(materialName))
            {
                var nameMatch = matchingType.FirstOrDefault(t =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(t.FilePath).ToLowerInvariant();
                    return fileName.Contains(materialName) || materialName.Contains(fileName);
                });
                if (nameMatch != null) return nameMatch;
            }

            // Return first match
            return matchingType.FirstOrDefault();
        }

        #endregion
    }
}
