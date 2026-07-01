using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Assets;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Service for managing material resources in the editor.
    /// Supports both built-in materials and custom PBR materials.
    /// </summary>
    public class MaterialService : IDisposable
    {
        private static MaterialService _instance;
        public static MaterialService Instance => _instance ?? (_instance = new MaterialService());

        private readonly Dictionary<string, long> _loadedMaterials = new Dictionary<string, long>();
        private readonly Dictionary<string, MaterialInfo> _materialInfos = new Dictionary<string, MaterialInfo>();
        private readonly Dictionary<long, UniversalMaterial> _universalMaterials = new Dictionary<long, UniversalMaterial>();

        // Fully-applied .vmat materials, cached by absolute path and shared across every entity
        // that references the same file. MaterialService owns their lifecycle (see UnloadAll) — do
        // NOT register these in per-entity caches that DeleteMaterial on cleanup, or they get freed
        // out from under other users.
        private readonly Dictionary<string, long> _vmatMaterials =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public class MaterialInfo
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public long Handle { get; set; }
            public bool IsBuiltIn { get; set; }
            public float ColorR { get; set; } = 1.0f;
            public float ColorG { get; set; } = 1.0f;
            public float ColorB { get; set; } = 1.0f;
            public float ColorA { get; set; } = 1.0f;
            public string ShaderPath { get; set; } = "Shader:Standard";
        }

        private MaterialService()
        {
            InitializeBuiltInMaterials();
        }

        private void InitializeBuiltInMaterials()
        {
            // Default white material
            _materialInfos["Default"] = new MaterialInfo
            {
                Name = "Default",
                Path = "Material:Default",
                Handle = -1,
                IsBuiltIn = true,
                ColorR = 0.8f, ColorG = 0.8f, ColorB = 0.8f, ColorA = 1.0f
            };

            // Unlit white material
            _materialInfos["UnlitWhite"] = new MaterialInfo
            {
                Name = "Unlit White",
                Path = "Material:UnlitWhite",
                Handle = -1,
                IsBuiltIn = true,
                ShaderPath = "Shader:Unlit",
                ColorR = 1.0f, ColorG = 1.0f, ColorB = 1.0f, ColorA = 1.0f
            };

            // Grid material
            _materialInfos["Grid"] = new MaterialInfo
            {
                Name = "Grid",
                Path = "Material:Grid",
                Handle = -1,
                IsBuiltIn = true,
                ShaderPath = "Shader:Grid",
                ColorR = 0.5f, ColorG = 0.5f, ColorB = 0.5f, ColorA = 0.5f
            };

            // Color materials
            AddColorMaterial("Red", 1.0f, 0.2f, 0.2f);
            AddColorMaterial("Green", 0.2f, 1.0f, 0.2f);
            AddColorMaterial("Blue", 0.2f, 0.2f, 1.0f);
            AddColorMaterial("Yellow", 1.0f, 1.0f, 0.2f);
            AddColorMaterial("Cyan", 0.2f, 1.0f, 1.0f);
            AddColorMaterial("Magenta", 1.0f, 0.2f, 1.0f);
            AddColorMaterial("Orange", 1.0f, 0.5f, 0.1f);
        }

        private void AddColorMaterial(string name, float r, float g, float b)
        {
            _materialInfos[name] = new MaterialInfo
            {
                Name = name,
                Path = $"Material:{name}",
                Handle = -1,
                IsBuiltIn = true,
                ColorR = r, ColorG = g, ColorB = b, ColorA = 1.0f
            };
        }

        /// <summary>
        /// Get or create a material by path.
        /// </summary>
        public long GetMaterial(string path)
        {
            if (string.IsNullOrEmpty(path)) return GetDefaultMaterial();

            if (_loadedMaterials.TryGetValue(path, out long handle))
            {
                return handle;
            }

            // Check if it's a built-in material
            var name = path.StartsWith("Material:") ? path.Substring("Material:".Length) : path;
            if (_materialInfos.TryGetValue(name, out var info))
            {
                handle = CreateMaterialFromInfo(info);
                if (handle >= 0)
                {
                    _loadedMaterials[path] = handle;
                    info.Handle = handle;
                }
                return handle;
            }

            // Try to load from file
            handle = VortexAPI.LoadMaterialResource(path);
            if (handle >= 0)
            {
                _loadedMaterials[path] = handle;
            }

            return handle >= 0 ? handle : GetDefaultMaterial();
        }

        private long CreateMaterialFromInfo(MaterialInfo info)
        {
            var handle = VortexAPI.CreateNewMaterial();
            if (handle >= 0)
            {
                VortexAPI.SetMaterialBaseColor(handle, info.ColorR, info.ColorG, info.ColorB, info.ColorA);
            }
            return handle;
        }

        /// <summary>
        /// Get the default material handle.
        /// </summary>
        public long GetDefaultMaterial()
        {
            return GetMaterial("Material:Default");
        }

        /// <summary>
        /// Get all available material infos.
        /// </summary>
        public IEnumerable<MaterialInfo> GetAllMaterials()
        {
            return _materialInfos.Values;
        }

        /// <summary>
        /// Create a new custom material.
        /// </summary>
        public long CreateCustomMaterial(string name, float r, float g, float b, float a = 1.0f)
        {
            var handle = VortexAPI.CreateNewMaterial();
            if (handle >= 0)
            {
                VortexAPI.SetMaterialBaseColor(handle, r, g, b, a);
                
                var path = $"Material:Custom_{handle}";
                _loadedMaterials[path] = handle;
                _materialInfos[name] = new MaterialInfo
                {
                    Name = name,
                    Path = path,
                    Handle = handle,
                    IsBuiltIn = false,
                    ColorR = r, ColorG = g, ColorB = b, ColorA = a
                };
            }
            return handle;
        }

        /// <summary>
        /// Creates a material from a UniversalMaterial definition.
        /// </summary>
        public long CreateFromUniversalMaterial(UniversalMaterial material)
        {
            if (material == null) return GetDefaultMaterial();
            
            // Check if already created
            if (material.EngineMaterialId >= 0)
            {
                return material.EngineMaterialId;
            }

            var handle = VortexAPI.CreateNewMaterial();
            if (handle >= 0)
            {
                VortexAPI.SetMaterialBaseColor(handle, 
                    material.BaseColor.ScR, 
                    material.BaseColor.ScG, 
                    material.BaseColor.ScB, 
                    material.BaseColor.ScA);
                
                // Apply PBR properties if the API supports them
                // Note: Metallic/Roughness textures are set via the material's texture properties
                
                material.EngineMaterialId = handle;
                _universalMaterials[handle] = material;
                _loadedMaterials[$"Material:Universal_{handle}"] = handle;
            }

            return handle >= 0 ? handle : GetDefaultMaterial();
        }

        /// <summary>
        /// Gets the UniversalMaterial associated with an engine material ID.
        /// </summary>
        public UniversalMaterial GetUniversalMaterial(long materialId)
        {
            _universalMaterials.TryGetValue(materialId, out var material);
            return material;
        }

        /// <summary>
        /// Loads a VortexMaterial from a .vmat file and creates engine material.
        /// </summary>
        public long LoadVortexMaterial(string vmatPath)
        {
            if (!File.Exists(vmatPath)) return GetDefaultMaterial();

            try
            {
                var vmat = VortexMaterial.Load(vmatPath);
                if (vmat == null) return GetDefaultMaterial();

                var directory = Path.GetDirectoryName(vmatPath);
                vmat.ResolvePathsAbsolute(directory);

                var universal = vmat.ToUniversalMaterial();
                return CreateFromUniversalMaterial(universal);
            }
            catch
            {
                return GetDefaultMaterial();
            }
        }

        /// <summary>
        /// Loads a .vmat and builds a fully-applied engine material — base color, all PBR scalars
        /// (metallic/roughness/normal-strength/normal-format/unlit/emissive) AND every texture map
        /// (albedo/normal/metallic/roughness/AO). The result is a real ResourceRegistry material id
        /// in the same id-space the renderer samples, so an edited .vmat actually shows up in the
        /// viewport. Cached by absolute path; call <see cref="InvalidateVortexMaterial"/> after the
        /// .vmat is edited on disk to force a rebuild on the next frame.
        /// </summary>
        public long GetOrBuildVortexMaterial(string vmatPath)
        {
            if (string.IsNullOrEmpty(vmatPath) || !File.Exists(vmatPath))
                return GetDefaultMaterial();

            string key = Path.GetFullPath(vmatPath);
            if (_vmatMaterials.TryGetValue(key, out long existing) && existing >= 0)
                return existing;

            long handle = BuildVortexMaterial(key);
            if (handle >= 0)
            {
                _vmatMaterials[key] = handle;
                return handle;
            }
            return GetDefaultMaterial();
        }

        /// <summary>
        /// Drop the cached engine material for a .vmat path (after it was edited/saved) so the next
        /// <see cref="GetOrBuildVortexMaterial"/> rebuilds it. Frees the old engine material.
        /// </summary>
        public void InvalidateVortexMaterial(string vmatPath)
        {
            if (string.IsNullOrEmpty(vmatPath)) return;
            string key;
            try { key = Path.GetFullPath(vmatPath); }
            catch { key = vmatPath; }

            if (_vmatMaterials.TryGetValue(key, out long handle))
            {
                if (handle >= 0) VortexAPI.DeleteMaterial(handle);
                _vmatMaterials.Remove(key);
            }
        }

        private long BuildVortexMaterial(string vmatPath)
        {
            var vmat = VortexMaterial.Load(vmatPath);
            if (vmat == null) return -1;

            vmat.ResolvePathsAbsolute(Path.GetDirectoryName(vmatPath));
            return BuildEngineMaterial(vmat);
        }

        /// <summary>
        /// Creates a fresh engine material and applies a VortexMaterial's full state — base color, all
        /// PBR scalars (metallic/roughness/AO/normal/unlit/emissive) and every texture map. Texture
        /// paths must already be absolute. The caller OWNS the returned id (not cached here); used by
        /// GetOrBuildVortexMaterial and by editor live-previews.
        /// </summary>
        public long BuildEngineMaterial(VortexMaterial vmat)
        {
            if (vmat == null) return -1;

            long mat = VortexAPI.CreateNewMaterial();
            if (mat < 0) return -1;

            // Base color
            var c = vmat.BaseColor;
            float r = (c != null && c.Length > 0) ? c[0] : 1f;
            float g = (c != null && c.Length > 1) ? c[1] : 1f;
            float b = (c != null && c.Length > 2) ? c[2] : 1f;
            float a = (c != null && c.Length > 3) ? c[3] : 1f;
            VortexAPI.SetMaterialBaseColor(mat, r, g, b, a);

            // PBR scalars
            VortexAPI.SetMaterialMetallicValue(mat, vmat.Metallic);
            VortexAPI.SetMaterialRoughnessValue(mat, vmat.Roughness);
            VortexAPI.SetMaterialAOValue(mat, vmat.AmbientOcclusion);
            VortexAPI.SetMaterialNormalStrengthValue(mat, vmat.NormalStrength);
            VortexAPI.SetMaterialNormalFormat(mat, vmat.UseDirectXNormals);

            // Unlit / emissive
            bool unlit = string.Equals(vmat.ShaderType, "Unlit", StringComparison.OrdinalIgnoreCase);
            VortexAPI.SetMaterialAsUnlit(mat, unlit);
            if (vmat.EmissiveStrength > 0f)
                VortexAPI.SetMaterialEmissiveBrightness(mat, vmat.EmissiveStrength);

            // Texture maps (resolved to absolute paths above)
            BindMap(mat, vmat.AlbedoTexture, VortexAPI.SetMaterialAlbedoTexture);
            BindMap(mat, vmat.NormalTexture, VortexAPI.SetMaterialNormalMap);
            BindMap(mat, vmat.MetallicTexture, VortexAPI.SetMaterialMetallicMap);
            BindMap(mat, vmat.RoughnessTexture, VortexAPI.SetMaterialRoughnessMap);
            BindMap(mat, vmat.AOTexture, VortexAPI.SetMaterialAOMap);

            // Custom shader: compile the assigned .hlsl into a per-material PSO (the 3D pass + material preview bind
            // it instead of the built-in PBR). A compile failure is a no-op engine-side (falls back to built-in).
            if (!string.IsNullOrEmpty(vmat.ShaderAsset))
            {
                var hlsl = ResolveShaderHlsl(vmat.ShaderAsset);
                if (!string.IsNullOrEmpty(hlsl)) VortexAPI.SetMaterialShader((int)mat, hlsl);
            }

            return mat;
        }

        /// <summary>Resolve a material's ShaderAsset (.vshader/.hlsl, project-relative) to the absolute .hlsl to compile.</summary>
        private static string ResolveShaderHlsl(string shaderAsset)
        {
            if (string.IsNullOrEmpty(shaderAsset)) return null;
            var proj = Editor.Core.Data.ProjectData.Current?.Path ?? "";
            string full = System.IO.Path.IsPathRooted(shaderAsset) ? shaderAsset : System.IO.Path.Combine(proj, shaderAsset);
            if (full.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase)) return System.IO.File.Exists(full) ? full : null;
            try
            {
                var vs = Editor.Core.Assets.VortexShader.Load(full);
                if (vs != null && !string.IsNullOrEmpty(vs.PixelShaderPath))
                {
                    var p = vs.PixelShaderPath;
                    var h = System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(proj, p);
                    if (System.IO.File.Exists(h)) return h;
                }
            }
            catch { }
            var sib = System.IO.Path.ChangeExtension(full, ".hlsl");
            return System.IO.File.Exists(sib) ? sib : null;
        }

        private static void BindMap(long materialId, string texturePath, Action<long, long> setter)
        {
            if (string.IsNullOrEmpty(texturePath) || !File.Exists(texturePath)) return;
            long tex = VortexAPI.ImportTextureFromFile(texturePath);
            if (tex >= 0) setter(materialId, tex);
        }

        /// <summary>
        /// Unload all materials.
        /// </summary>
        public void UnloadAll()
        {
            foreach (var handle in _loadedMaterials.Values)
            {
                if (handle >= 0)
                {
                    VortexAPI.DeleteMaterial(handle);
                }
            }
            _loadedMaterials.Clear();

            foreach (var handle in _vmatMaterials.Values)
            {
                if (handle >= 0)
                {
                    VortexAPI.DeleteMaterial(handle);
                }
            }
            _vmatMaterials.Clear();

            // Reset built-in handles
            foreach (var info in _materialInfos.Values)
            {
                info.Handle = -1;
            }
        }

        public void Dispose()
        {
            UnloadAll();
            _instance = null;
        }
    }
}
