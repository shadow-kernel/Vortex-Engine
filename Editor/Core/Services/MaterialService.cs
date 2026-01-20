using System;
using System.Collections.Generic;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Service for managing material resources in the editor.
    /// </summary>
    public class MaterialService : IDisposable
    {
        private static MaterialService _instance;
        public static MaterialService Instance => _instance ?? (_instance = new MaterialService());

        private readonly Dictionary<string, long> _loadedMaterials = new Dictionary<string, long>();
        private readonly Dictionary<string, MaterialInfo> _materialInfos = new Dictionary<string, MaterialInfo>();

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

            // Red material
            _materialInfos["Red"] = new MaterialInfo
            {
                Name = "Red",
                Path = "Material:Red",
                Handle = -1,
                IsBuiltIn = true,
                ColorR = 1.0f, ColorG = 0.2f, ColorB = 0.2f, ColorA = 1.0f
            };

            // Green material
            _materialInfos["Green"] = new MaterialInfo
            {
                Name = "Green",
                Path = "Material:Green",
                Handle = -1,
                IsBuiltIn = true,
                ColorR = 0.2f, ColorG = 1.0f, ColorB = 0.2f, ColorA = 1.0f
            };

            // Blue material
            _materialInfos["Blue"] = new MaterialInfo
            {
                Name = "Blue",
                Path = "Material:Blue",
                Handle = -1,
                IsBuiltIn = true,
                ColorR = 0.2f, ColorG = 0.2f, ColorB = 1.0f, ColorA = 1.0f
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
