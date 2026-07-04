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
            // AssetVfs.Exists = in the shipped pak OR on disk. A shipped Release build has NO loose .vmat on
            // disk (only the in-RAM pak), so a plain File.Exists here made every .vmat collapse to the default
            // material = untextured objects. VortexMaterial.Load is already VFS-aware.
            if (!AssetVfs.Exists(vmatPath)) return GetDefaultMaterial();

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
            // Shipped Release build: the .vmat lives only in the in-RAM pak, not on disk — a plain File.Exists
            // check here was THE "textures missing after Release export" bug (every .vmat -> default material).
            if (string.IsNullOrEmpty(vmatPath) || !AssetVfs.Exists(vmatPath))
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
            ApplyVmatValues(mat, vmat, includeTextures: true);
            return mat;
        }

        /// <summary>Push a saved .vmat's state onto an EXISTING engine material id. Used to make a re-imported model
        /// (Explorer thumbnail, Prefab Editor preview) reflect the Model Editor's edits, which live in the sidecar
        /// .vmat — NOT the model file's embedded materials. <paramref name="includeTextures"/> = false skips texture
        /// (re)import: the engine's import_texture has NO path dedup, so re-uploading maps on every throwaway thumbnail
        /// build leaks GPU textures — thumbnails/previews use scalars-only (base color is what users edit).</summary>
        public void ApplyVmatToMaterial(long materialId, string vmatPath, bool includeTextures = true)
        {
            if (materialId < 0 || string.IsNullOrEmpty(vmatPath) || !File.Exists(vmatPath)) return;
            var vmat = VortexMaterial.Load(vmatPath);
            if (vmat == null) return;
            if (includeTextures) { try { vmat.ResolvePathsAbsolute(Path.GetDirectoryName(vmatPath)); } catch { } }
            ApplyVmatValues(materialId, vmat, includeTextures);
        }

        /// <summary>For a freshly re-imported model, overlay each submesh's sidecar <c>materials/submesh_i.vmat</c>
        /// (what the Model Editor saves) onto the imported material ids — so Explorer thumbnails and the Prefab Editor
        /// preview show the EDITED base colour / PBR. Scalars-only by default (no texture re-import -> no GPU leak on
        /// repeated throwaway renders). No-op if the model has no materials/.</summary>
        public void ApplyModelSidecarVmats(long[] materialIds, string modelFullPath, bool includeTextures = false)
        {
            if (materialIds == null || string.IsNullOrEmpty(modelFullPath)) return;
            string matDir;
            try { matDir = Path.Combine(Path.GetDirectoryName(modelFullPath) ?? "", "materials"); }
            catch { return; }
            if (!Directory.Exists(matDir)) return;
            for (int i = 0; i < materialIds.Length; i++)
            {
                try { ApplyVmatToMaterial(materialIds[i], Path.Combine(matDir, $"submesh_{i}.vmat"), includeTextures); } catch { }
            }
        }

        /// <summary>Apply a VortexMaterial's base color + PBR scalars (+ texture maps + shader when includeTextures) to
        /// an engine material id. Texture paths must already be absolute (call ResolvePathsAbsolute first).</summary>
        private void ApplyVmatValues(long mat, VortexMaterial vmat, bool includeTextures)
        {
            if (mat < 0 || vmat == null) return;

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

            // UV tiling (texture repeat scale) — always applied (it's a scalar, no texture re-import), so tiling shows
            // in the scene AND thumbnails/previews. Falls back to 1 for a missing/zero value.
            var tl = vmat.UVTiling;
            float tu = (tl != null && tl.Length > 0 && tl[0] > 0f) ? tl[0] : 1f;
            float tv = (tl != null && tl.Length > 1 && tl[1] > 0f) ? tl[1] : 1f;
            VortexAPI.SetMaterialTiling(mat, tu, tv);

            // Parallax/displacement depth (scalar) — applied even in the scalars-only path.
            VortexAPI.SetMaterialHeightDepth(mat, vmat.HeightScale);

            if (!includeTextures) return;   // scalars-only path (thumbnails/previews) — skip texture/shader re-import

            // Texture maps (resolved to absolute paths above)
            BindMap(mat, vmat.AlbedoTexture, VortexAPI.SetMaterialAlbedoTexture);
            BindMap(mat, vmat.NormalTexture, VortexAPI.SetMaterialNormalMap);
            BindMap(mat, vmat.MetallicTexture, VortexAPI.SetMaterialMetallicMap);
            BindMap(mat, vmat.RoughnessTexture, VortexAPI.SetMaterialRoughnessMap);
            BindMap(mat, vmat.AOTexture, VortexAPI.SetMaterialAOMap);
            BindMap(mat, vmat.HeightTexture, VortexAPI.SetMaterialHeightMap);

            // Custom shader: compile the assigned .hlsl into a per-material PSO (the 3D pass + material preview bind
            // it instead of the built-in PBR). A compile failure is a no-op engine-side (falls back to built-in).
            if (!string.IsNullOrEmpty(vmat.ShaderAsset))
            {
                var hlsl = ResolveShaderHlsl(vmat.ShaderAsset);
                if (!string.IsNullOrEmpty(hlsl)) VortexAPI.SetMaterialShader((int)mat, hlsl);
            }
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

        // Texture id cache, keyed by absolute path + last-write time. The native importer has NO path dedup and the
        // engine never frees textures, so re-importing the SAME file (e.g. a 4K map on every material-preview render,
        // or every thumbnail rebuild) re-uploads the whole texture to the GPU on the UI thread — a 4K upload stalls
        // the window ("preview hangs / unbedienbar") AND leaks VRAM. Caching by (path, mtime) uploads once and reuses;
        // an edited texture (new mtime) re-imports. Static: texture ids are session-global and shared.
        private static readonly Dictionary<string, long> _textureIdCache =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Import a texture ONCE per (path, mtime) and reuse the GPU id on later calls. Use this everywhere a
        /// texture is bound repeatedly (previews, thumbnails, live scene) instead of VortexAPI.ImportTextureFromFile.</summary>
        public static long ImportTextureCached(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return -1;

            // Shipped game: textures live in the in-RAM pak (no loose files on disk). Editor: loose disk files.
            // Mirror SceneRenderService.ImportTexturePath so the .vmat texture pipeline isn't AssetVfs-blind —
            // that blindness was the "textures missing after Release export" bug (File.Exists always failed in
            // the pak, so a map was never bound). ImportTextureFromBytes sniffs PNG/JPG/DDS from the buffer.
            byte[] vfsBytes = null;
            bool fromVfs = AssetVfs.IsMounted && AssetVfs.TryGetBytes(absPath, out vfsBytes);
            if (!fromVfs && !File.Exists(absPath)) return -1;

            string key;
            if (fromVfs) key = "vfs|" + absPath;              // pak is immutable at runtime -> path alone is a stable key
            else { try { key = absPath + "|" + File.GetLastWriteTimeUtc(absPath).Ticks; } catch { key = absPath; } }

            if (_textureIdCache.TryGetValue(key, out long cached) && cached >= 0) return cached;
            long id = fromVfs ? VortexAPI.ImportTextureFromBytes(vfsBytes) : VortexAPI.ImportTextureFromFile(absPath);
            if (id >= 0) _textureIdCache[key] = id;
            return id;
        }

        private static void BindMap(long materialId, string texturePath, Action<long, long> setter)
        {
            if (string.IsNullOrEmpty(texturePath)) return;
            long tex = ImportTextureCached(texturePath);   // AssetVfs (shipped) or disk (editor); uploaded once per key
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
