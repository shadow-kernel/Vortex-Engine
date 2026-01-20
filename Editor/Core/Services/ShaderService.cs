using System;
using System.Collections.Generic;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Service for managing shader resources in the editor.
    /// </summary>
    public class ShaderService : IDisposable
    {
        private static ShaderService _instance;
        public static ShaderService Instance => _instance ?? (_instance = new ShaderService());

        private readonly Dictionary<string, long> _loadedShaders = new Dictionary<string, long>();
        private readonly Dictionary<string, ShaderInfo> _shaderInfos = new Dictionary<string, ShaderInfo>();

        public class ShaderInfo
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public ShaderType Type { get; set; }
            public long Handle { get; set; }
            public bool IsBuiltIn { get; set; }
        }

        public enum ShaderType
        {
            Standard,
            Unlit,
            Wireframe,
            Grid,
            Custom
        }

        private ShaderService()
        {
            InitializeBuiltInShaders();
        }

        private void InitializeBuiltInShaders()
        {
            // Register built-in shaders
            _shaderInfos["Standard"] = new ShaderInfo
            {
                Name = "Standard",
                Path = "Shader:Standard",
                Type = ShaderType.Standard,
                Handle = -1,
                IsBuiltIn = true
            };

            _shaderInfos["Unlit"] = new ShaderInfo
            {
                Name = "Unlit",
                Path = "Shader:Unlit",
                Type = ShaderType.Unlit,
                Handle = -1,
                IsBuiltIn = true
            };

            _shaderInfos["Wireframe"] = new ShaderInfo
            {
                Name = "Wireframe",
                Path = "Shader:Wireframe",
                Type = ShaderType.Wireframe,
                Handle = -1,
                IsBuiltIn = true
            };

            _shaderInfos["Grid"] = new ShaderInfo
            {
                Name = "Grid",
                Path = "Shader:Grid",
                Type = ShaderType.Grid,
                Handle = -1,
                IsBuiltIn = true
            };
        }

        /// <summary>
        /// Get or load a shader by path.
        /// </summary>
        public long GetShader(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;

            if (_loadedShaders.TryGetValue(path, out long handle))
            {
                return handle;
            }

            // Try to load the shader
            handle = VortexAPI.LoadShaderResource(path);
            if (handle >= 0)
            {
                _loadedShaders[path] = handle;
            }

            return handle;
        }

        /// <summary>
        /// Get all available shader infos.
        /// </summary>
        public IEnumerable<ShaderInfo> GetAllShaders()
        {
            return _shaderInfos.Values;
        }

        /// <summary>
        /// Register a custom shader.
        /// </summary>
        public void RegisterShader(string name, string path, ShaderType type = ShaderType.Custom)
        {
            _shaderInfos[name] = new ShaderInfo
            {
                Name = name,
                Path = path,
                Type = type,
                Handle = -1,
                IsBuiltIn = false
            };
        }

        /// <summary>
        /// Unload all shaders.
        /// </summary>
        public void UnloadAll()
        {
            foreach (var handle in _loadedShaders.Values)
            {
                if (handle >= 0)
                {
                    VortexAPI.UnloadResourceHandle(handle);
                }
            }
            _loadedShaders.Clear();
        }

        public void Dispose()
        {
            UnloadAll();
            _instance = null;
        }
    }
}
