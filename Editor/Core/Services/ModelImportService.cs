using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Assets;
using Editor.Dialogs;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Service for importing 3D models and related assets into the project.
    /// Handles file copying, Assimp import, texture discovery, and AssetDatabase registration.
    /// </summary>
    public class ModelImportService
    {
        private static ModelImportService _instance;
        public static ModelImportService Instance => _instance ?? (_instance = new ModelImportService());

        private ModelImportService() { }

        /// <summary>
        /// Event fired when a model is successfully imported.
        /// </summary>
        public event EventHandler<ModelImportResult> ModelImported;

        /// <summary>
        /// Imports a 3D model file into the project.
        /// </summary>
        /// <param name="sourceFilePath">Full path to the source model file</param>
        /// <param name="targetFolder">Target folder relative to Assets (e.g., "Models")</param>
        /// <returns>Import result with details about what was imported</returns>
        public ModelImportResult ImportModel(string sourceFilePath, string targetFolder = "Models")
        {
            var result = new ModelImportResult
            {
                SourcePath = sourceFilePath,
                ModelName = Path.GetFileNameWithoutExtension(sourceFilePath)
            };


            try
            {
                // Validate file exists
                if (!File.Exists(sourceFilePath))
                {
                    result.Success = false;
                    result.ErrorMessage = "Source file not found.";
                    return result;
                }

                // Check for problematic characters in path
                if (sourceFilePath.Any(c => c > 127))
                {
                    result.Success = false;
                    result.ErrorMessage = $"File path contains special characters that Assimp cannot handle.\n" +
                        $"Please move the file to a path without special characters (�, �, �, etc.).\n\n" +
                        $"Path: {sourceFilePath}";
                    return result;
                }

                var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
                var assetDatabase = AssetDatabase.Instance;
                var projectPath = assetDatabase.ProjectPath;

                if (string.IsNullOrEmpty(projectPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "No project is currently open.";
                    return result;
                }

                // Log import attempt
                System.Diagnostics.Debug.WriteLine($"[ModelImportService] Importing: {sourceFilePath}");
                System.Diagnostics.Debug.WriteLine($"[ModelImportService] Extension: {extension}");

                // Check Assimp availability for non-vmesh files
                if (extension != ".vmesh" && !VortexAPI.IsAssimpAvailable())
                {
                    result.Success = false;
                    result.ErrorMessage = "Assimp library is not available. Cannot import this model format.\n\n" +
                        "Make sure assimp.dll is in the application directory.";
                    return result;
                }

                System.Diagnostics.Debug.WriteLine($"[ModelImportService] Assimp available: {VortexAPI.IsAssimpAvailable()}");

                // (GLB/GLTF are handled by the vendored assimp6 native import — no version block needed.)

                // Create target directory structure
                var modelsDir = Path.Combine(projectPath, "Assets", targetFolder);
                var modelSubDir = Path.Combine(modelsDir, result.ModelName);
                
                if (!Directory.Exists(modelSubDir))
                    Directory.CreateDirectory(modelSubDir);

                // Copy the model file
                var targetModelPath = Path.Combine(modelSubDir, Path.GetFileName(sourceFilePath));
                if (!File.Exists(targetModelPath))
                {
                    File.Copy(sourceFilePath, targetModelPath, false);
                }
                result.AssetPath = targetModelPath;

                // Calculate relative path for AssetDatabase
                var relativePath = GetRelativePath(projectPath, targetModelPath);
                result.RelativePath = relativePath;

                // Find and copy associated files (MTL for OBJ, textures)
                var sourceDir = Path.GetDirectoryName(sourceFilePath);
                var associatedFiles = FindAssociatedFiles(sourceFilePath);

                // Copy MTL file if present (for OBJ)
                if (extension == ".obj")
                {
                    var mtlFile = Path.ChangeExtension(sourceFilePath, ".mtl");
                    if (File.Exists(mtlFile))
                    {
                        var targetMtl = Path.Combine(modelSubDir, Path.GetFileName(mtlFile));
                        if (!File.Exists(targetMtl))
                            File.Copy(mtlFile, targetMtl, false);
                    }
                }

                // Find and copy textures - keep them NEXT to the model file, not in subfolder
                // This is important because MTL files reference textures relative to themselves
                var textureDir = Path.Combine(sourceDir, "textures");
                if (Directory.Exists(textureDir))
                {
                    var textureFiles = Directory.GetFiles(textureDir, "*.*")
                        .Where(f => IsTextureFile(f))
                        .ToList();

                    foreach (var texFile in textureFiles)
                    {
                        // Copy texture to same folder as model (not subfolder)
                        var targetTex = Path.Combine(modelSubDir, Path.GetFileName(texFile));
                        if (!File.Exists(targetTex))
                        {
                            File.Copy(texFile, targetTex, false);
                        }
                        // Always add to list (avoid duplicates)
                        if (!result.ImportedTextures.Contains(targetTex))
                        {
                            result.ImportedTextures.Add(targetTex);
                        }
                        if (!result.TexturePaths.Contains(texFile))
                        {
                            result.TexturePaths.Add(texFile);
                        }
                    }
                }

                // Also check for textures in the same directory as the model
                var siblingTextures = Directory.GetFiles(sourceDir, "*.*")
                    .Where(f => IsTextureFile(f))
                    .ToList();

                foreach (var texFile in siblingTextures)
                {
                    var targetTex = Path.Combine(modelSubDir, Path.GetFileName(texFile));
                    if (!File.Exists(targetTex))
                    {
                        File.Copy(texFile, targetTex, false);
                    }
                    // Always add to ImportedTextures list (not just when copying)
                    if (!result.ImportedTextures.Contains(targetTex))
                    {
                        result.ImportedTextures.Add(targetTex);
                    }
                    if (!result.TexturePaths.Contains(texFile))
                    {
                        result.TexturePaths.Add(texFile);
                    }
                }

                // Use multi-material import - Assimp handles material/texture assignments
                if (extension != ".vmesh")
                {
                    System.Diagnostics.Debug.WriteLine($"[ModelImportService] Starting import of: {targetModelPath}");
                    
                    // Check if Assimp is available
                    if (!VortexAPI.IsAssimpAvailable())
                    {
                        result.Success = false;
                        result.ErrorMessage = "Assimp library is not available. Please ensure assimp.dll is in the application directory.";
                        System.Diagnostics.Debug.WriteLine($"[ModelImportService] Assimp not available!");
                        return result;
                    }
                    
                    var submeshData = VortexAPI.ImportModelWithMaterialsFromFile(targetModelPath);
                    
                    if (submeshData != null && submeshData.Length > 0)
                    {
                        result.Success = true;
                        result.SubmeshCount = submeshData.Length;
                        result.MeshId = submeshData[0].MeshId;
                        result.MaterialId = submeshData[0].MaterialId;

                        System.Diagnostics.Debug.WriteLine($"[ModelImportService] Multi-material import: {submeshData.Length} submeshes");

                        // Get the actual submesh names from the model
                        string[] submeshNames = VortexAPI.GetSubmeshNames(targetModelPath, submeshData.Length);

                        // Get diffuse textures
                        var diffuseTextures = result.ImportedTextures
                            .Where(t => {
                                var lower = t.ToLowerInvariant();
                                return (lower.Contains("_col") || lower.Contains("col.") || 
                                        lower.Contains("_diffuse") || lower.Contains("_albedo")) &&
                                       !lower.Contains("_nor") && !lower.Contains("_rough") && 
                                       !lower.Contains("_metal") && !lower.Contains("_ao");
                            })
                            .ToList();

                        // Create submesh entries
                        for (int i = 0; i < submeshData.Length; i++)
                        {
                            var sub = submeshData[i];
                            
                            // Use actual submesh name from model
                            string submeshName = i < submeshNames.Length && !string.IsNullOrEmpty(submeshNames[i])
                                ? submeshNames[i]
                                : $"Submesh_{i}";
                            
                            // Find matching texture for this submesh by name
                            string texturePath = FindTextureForSubmesh(submeshName, diffuseTextures);

                            var submeshResult = new Dialogs.SubmeshImportData
                            {
                                MeshId = sub.MeshId,
                                MaterialId = sub.MaterialId,
                                TextureId = sub.TextureId,
                                Name = submeshName,
                                TexturePath = texturePath
                            };
                            result.Submeshes.Add(submeshResult);
                            result.SubmeshNames.Add(submeshResult.Name);

                            // Register mesh and material in render cache
                            string submeshPath = $"{relativePath}#submesh{i}";
                            SceneRenderService.RegisterMeshIdForPath(submeshPath, sub.MeshId);
                            SceneRenderService.RegisterMaterialForMeshPath(submeshPath, sub.MaterialId);

                            System.Diagnostics.Debug.WriteLine($"[ModelImportService] Submesh {i} '{submeshName}': mesh={sub.MeshId}, material={sub.MaterialId}, texture={texturePath ?? "none"}");
                        }

                        // Also register for base path
                        SceneRenderService.RegisterMaterialForMeshPath(relativePath, result.MaterialId);

                        // ORGANIZE the model's materials as FILES: write one .vmat per unique material into a
                        // materials/ subfolder of the model's folder, with the slots the native import actually
                        // found. The model's whole folder is self-contained -> deleting it removes everything.
                        try
                        {
                            var matDir = Path.Combine(modelSubDir, "materials");
                            Directory.CreateDirectory(matDir);
                            VortexAPI.SubmeshTextureSet[] texSets = null;
                            try { texSets = VortexAPI.GetSubmeshTexturePaths(targetModelPath, submeshData.Length); } catch { }
                            var writtenMats = new HashSet<long>();
                            for (int i = 0; i < submeshData.Length; i++)
                            {
                                if (!writtenMats.Add(submeshData[i].MaterialId)) continue; // one .vmat per unique material
                                string matName = (i < submeshNames.Length && !string.IsNullOrEmpty(submeshNames[i])) ? submeshNames[i] : $"Material_{i}";
                                var vmat = new VortexMaterial { Name = matName };
                                if (texSets != null && i < texSets.Length)
                                {
                                    var t = texSets[i];
                                    if (!string.IsNullOrEmpty(t.Albedo))    vmat.AlbedoTexture = t.Albedo;
                                    if (!string.IsNullOrEmpty(t.Normal))    vmat.NormalTexture = t.Normal;
                                    if (!string.IsNullOrEmpty(t.Metallic))  vmat.MetallicTexture = t.Metallic;
                                    if (!string.IsNullOrEmpty(t.Roughness)) vmat.RoughnessTexture = t.Roughness;
                                    if (!string.IsNullOrEmpty(t.AO))        vmat.AOTexture = t.AO;
                                    if (!string.IsNullOrEmpty(t.Emissive))  vmat.EmissiveTexture = t.Emissive;
                                }
                                vmat.MakePathsRelative(matDir);
                                string safeName = string.Join("_", matName.Split(Path.GetInvalidFileNameChars()));
                                vmat.Save(Path.Combine(matDir, safeName + ".vmat"));
                            }
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ModelImportService] .vmat write failed: {ex.Message}"); }

                        // Build structured asset graph for editor/engine sync
                        try
                        {
                            var parsedData = UniversalModelParser.Instance.ParseModel(targetModelPath);
                            result.BuiltModel = ModelAssetBuilder.FromUniversalData(parsedData);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ModelImportService] Failed to build structured model asset: {ex.Message}");
                        }
                    }
                    else
                    {
                        result.Success = false;
                        result.ErrorMessage = $"Failed to import model '{Path.GetFileName(targetModelPath)}'. " +
                            "The model may be corrupted, use an unsupported format, or the file path may contain special characters. " +
                            "Check the Output window for detailed error information.";
                        System.Diagnostics.Debug.WriteLine($"[ModelImportService] Import failed - no submesh data returned");
                        return result;
                    }
                }
                else
                {
                    // VMesh - simple single mesh
                    long meshId = VortexAPI.LoadVMeshFromFile(targetModelPath);
                    if (meshId < 0)
                    {
                        result.Success = false;
                        result.ErrorMessage = "Failed to load .vmesh file.";
                        return result;
                    }

                    result.MeshId = meshId;
                    result.Success = true;
                    result.SubmeshCount = 1;

                    // Create default material
                    long materialId = VortexAPI.CreateNewMaterial();
                    if (materialId >= 0)
                    {
                        VortexAPI.SetMaterialBaseColor(materialId, 0.9f, 0.9f, 0.9f, 1.0f);
                        result.MaterialId = materialId;
                        SceneRenderService.RegisterMaterialForMeshPath(relativePath, materialId);
                    }

                    // Build structured asset graph for editor/engine sync
                    var model = new ModelAsset
                    {
                        Name = result.ModelName,
                        SourcePath = sourceFilePath,
                        AssetPath = targetModelPath
                    };

                    var matAsset = new MaterialAsset
                    {
                        Name = "Default",
                        AssetPath = targetModelPath,
                        SourcePath = sourceFilePath,
                        EngineMaterialId = materialId
                    };
                    matAsset.InitializeStandardSlots();
                    model.Materials.Add(matAsset);

                    var meshAsset = new MeshAsset
                    {
                        Name = result.ModelName,
                        AssetPath = targetModelPath,
                        SourcePath = sourceFilePath,
                        EngineMeshId = meshId
                    };
                    meshAsset.Submeshes.Add(new SubmeshAsset
                    {
                        Index = 0,
                        Name = result.ModelName,
                        EngineMeshId = meshId,
                        MaterialId = matAsset.Id
                    });
                    model.Meshes.Add(meshAsset);

                    result.BuiltModel = model;
                }

                // Register in AssetDatabase
                var asset = new AssetMetadata(AssetType.Mesh, relativePath, Path.GetFileName(targetModelPath))
                {
                    LastModified = DateTime.Now,
                    FileSize = new FileInfo(targetModelPath).Length
                };

                // Register textures as dependencies
                foreach (var texPath in result.ImportedTextures)
                {
                    var texRelPath = GetRelativePath(projectPath, texPath);
                    var texAsset = new AssetMetadata(AssetType.Texture, texRelPath, Path.GetFileName(texPath));
                    assetDatabase.SaveMetadata(texAsset, texPath + AssetDatabase.MetaFileExtension);
                }

                assetDatabase.SaveMetadata(asset, targetModelPath + AssetDatabase.MetaFileExtension);
                result.AssetGuid = asset.Guid;

                // Fire event
                ModelImported?.Invoke(this, result);

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        private List<string> FindAssociatedFiles(string modelPath)
        {
            var result = new List<string>();
            var dir = Path.GetDirectoryName(modelPath);
            var baseName = Path.GetFileNameWithoutExtension(modelPath);

            // Look for common associated files
            var patterns = new[] { "*.mtl", "*.mat", "*_diffuse.*", "*_normal.*", "*_spec.*" };

            foreach (var pattern in patterns)
            {
                var matches = Directory.GetFiles(dir, pattern);
                result.AddRange(matches);
            }

            return result.Distinct().ToList();
        }

        private bool IsTextureFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || 
                   ext == ".tga" || ext == ".bmp" || ext == ".dds" || ext == ".tif";
        }

        /// <summary>
        /// Finds the best matching texture for a given submesh name.
        /// </summary>
        private string FindTextureForSubmesh(string submeshName, List<string> availableTextures)
        {
            if (string.IsNullOrEmpty(submeshName) || availableTextures == null || availableTextures.Count == 0)
                return availableTextures?.FirstOrDefault();

            // Normalize the submesh name (lowercase, replace spaces with underscores)
            string normalizedName = submeshName.ToLowerInvariant().Replace(" ", "_");

            // Find texture that matches the submesh name
            foreach (var texPath in availableTextures)
            {
                string texFileName = Path.GetFileName(texPath).ToLowerInvariant();
                if (texFileName.Contains(normalizedName))
                {
                    return texPath;
                }
            }

            // If no exact match, return null (C++ side will handle texture loading)
            return null;
        }

        private string GetRelativePath(string basePath, string fullPath)
        {
            var baseUri = new Uri(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var fullUri = new Uri(fullPath);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// Checks if a file extension is a supported model format.
        /// </summary>
        public static bool IsSupportedModelFormat(string extension)
        {
            extension = extension?.ToLowerInvariant() ?? "";
            return extension == ".fbx" || extension == ".obj" || extension == ".gltf" || 
                   extension == ".glb" || extension == ".dae" || extension == ".3ds" || 
                   extension == ".blend" || extension == ".vmesh";
        }
    }
}
