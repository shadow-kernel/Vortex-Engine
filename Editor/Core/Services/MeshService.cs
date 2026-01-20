using System;
using System.Collections.Generic;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Service for managing mesh resources in the editor.
    /// </summary>
    public class MeshService : IDisposable
    {
        private static MeshService _instance;
        public static MeshService Instance => _instance ?? (_instance = new MeshService());

        private readonly Dictionary<string, long> _loadedMeshes = new Dictionary<string, long>();
        private readonly Dictionary<string, MeshInfo> _meshInfos = new Dictionary<string, MeshInfo>();

        public class MeshInfo
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public MeshType Type { get; set; }
            public long Handle { get; set; }
            public bool IsBuiltIn { get; set; }
        }

        public enum MeshType
        {
            Primitive,
            Imported,
            Generated
        }

        private MeshService()
        {
            InitializeBuiltInMeshes();
        }

        private void InitializeBuiltInMeshes()
        {
            // Register built-in primitive meshes
            RegisterPrimitive("Cube");
            RegisterPrimitive("Sphere");
            RegisterPrimitive("Plane");
            RegisterPrimitive("Cylinder");
            RegisterPrimitive("Capsule");
            RegisterPrimitive("Cone");
            RegisterPrimitive("Torus");
            RegisterPrimitive("Quad");
        }

        private void RegisterPrimitive(string name)
        {
            _meshInfos[name] = new MeshInfo
            {
                Name = name,
                Path = $"Primitive:{name}",
                Type = MeshType.Primitive,
                Handle = -1,
                IsBuiltIn = true
            };
        }

        /// <summary>
        /// Get or create a mesh by path.
        /// </summary>
        public long GetMesh(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;

            if (_loadedMeshes.TryGetValue(path, out long handle))
            {
                return handle;
            }

            // Check if it's a primitive
            if (path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                handle = CreatePrimitiveMesh(path);
            }
            else
            {
                // Try to load from file
                handle = VortexAPI.LoadMeshResource(path);
            }

            if (handle >= 0)
            {
                _loadedMeshes[path] = handle;
            }

            return handle;
        }

        private long CreatePrimitiveMesh(string path)
        {
            var primitiveType = path.Substring("Primitive:".Length).ToLower();
            
            switch (primitiveType)
            {
                case "cube":
                    return VortexAPI.CreateCubeMesh(1.0f);
                case "sphere":
                    return VortexAPI.CreateSphereMesh(0.5f);
                case "plane":
                case "quad":
                    return VortexAPI.CreatePlaneMesh(1.0f, 1.0f);
                case "cylinder":
                    return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                case "capsule":
                    // Approximation with cylinder
                    return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                case "cone":
                    // Approximation with cylinder
                    return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                case "torus":
                    // Approximation with sphere
                    return VortexAPI.CreateSphereMesh(0.5f);
                default:
                    return -1;
            }
        }

        /// <summary>
        /// Get all available mesh infos.
        /// </summary>
        public IEnumerable<MeshInfo> GetAllMeshes()
        {
            return _meshInfos.Values;
        }

        /// <summary>
        /// Get all primitive mesh names.
        /// </summary>
        public IEnumerable<string> GetPrimitiveNames()
        {
            foreach (var info in _meshInfos.Values)
            {
                if (info.IsBuiltIn)
                    yield return info.Name;
            }
        }

        /// <summary>
        /// Register an imported mesh.
        /// </summary>
        public void RegisterMesh(string name, string filePath)
        {
            _meshInfos[name] = new MeshInfo
            {
                Name = name,
                Path = filePath,
                Type = MeshType.Imported,
                Handle = -1,
                IsBuiltIn = false
            };
        }

        /// <summary>
        /// Remove a mesh from the registry and unload it.
        /// </summary>
        public void RemoveMesh(string path)
        {
            if (_loadedMeshes.TryGetValue(path, out long handle))
            {
                VortexAPI.DeleteMesh(handle);
                _loadedMeshes.Remove(path);
            }

            var name = path.Contains(":") ? path.Split(':')[1] : path;
            if (_meshInfos.ContainsKey(name) && !_meshInfos[name].IsBuiltIn)
            {
                _meshInfos.Remove(name);
            }
        }

        /// <summary>
        /// Unload all meshes.
        /// </summary>
        public void UnloadAll()
        {
            foreach (var handle in _loadedMeshes.Values)
            {
                if (handle >= 0)
                {
                    VortexAPI.DeleteMesh(handle);
                }
            }
            _loadedMeshes.Clear();

            // Reset handles
            foreach (var info in _meshInfos.Values)
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
