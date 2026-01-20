using System;
using System.Collections.Generic;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services
{
    /// <summary>
    /// Service for synchronizing Editor entities with the Engine runtime.
    /// Maintains a mapping between Editor entity IDs and Engine entity IDs.
    /// </summary>
    public class EntitySyncService : IDisposable
    {
        private static EntitySyncService _instance;
        public static EntitySyncService Instance => _instance ?? (_instance = new EntitySyncService());

        // Maps Editor entity Guid to Engine entity ID
        private readonly Dictionary<Guid, long> _entityMap = new Dictionary<Guid, long>();
        
        // Maps Editor entity Guid to Engine mesh ID
        private readonly Dictionary<Guid, long> _meshMap = new Dictionary<Guid, long>();
        
        // Maps Editor entity Guid to Engine material ID  
        private readonly Dictionary<Guid, long> _materialMap = new Dictionary<Guid, long>();

        // Dirty tracking for entities that need sync
        private readonly HashSet<Guid> _dirtyEntities = new HashSet<Guid>();

        private bool _isInitialized;

        private EntitySyncService() { }

        public void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;
        }

        public void Shutdown()
        {
            ClearAll();
            _isInitialized = false;
        }

        /// <summary>
        /// Synchronize a single entity to the engine.
        /// Creates engine resources if needed and submits for rendering.
        /// </summary>
        public void SyncEntity(GameEntity entity)
        {
            if (entity == null || !entity.IsActive) return;

            var transform = entity.GetComponent<Transform>();
            var meshRenderer = entity.GetComponent<MeshRenderer>();

            if (transform == null) return;

            // Get or create mesh
            long meshId = -1;
            long materialId = -1;

            if (meshRenderer != null && meshRenderer.IsEnabled)
            {
                meshId = GetOrCreateMesh(entity.Id, meshRenderer);
                materialId = GetOrCreateMaterial(entity.Id, meshRenderer);
            }

            if (meshId >= 0)
            {
                // Build world matrix
                float[] worldMatrix = BuildWorldMatrix(transform);

                // Submit for rendering
                VortexAPI.SubmitMeshForRendering(meshId, materialId, worldMatrix);
            }

            // Sync children recursively
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    SyncEntity(child);
                }
            }
        }

        /// <summary>
        /// Synchronize all entities in a scene to the engine.
        /// </summary>
        public void SyncScene(Data.Scene scene)
        {
            if (scene == null || scene.Entities == null) return;

            foreach (var entity in scene.Entities)
            {
                SyncEntity(entity);
            }
        }

        /// <summary>
        /// Mark an entity as dirty (needs re-sync).
        /// </summary>
        public void MarkDirty(GameEntity entity)
        {
            if (entity != null)
            {
                _dirtyEntities.Add(entity.Id);
            }
        }

        /// <summary>
        /// Remove an entity from the sync system.
        /// </summary>
        public void RemoveEntity(GameEntity entity)
        {
            if (entity == null) return;

            var entityId = entity.Id;

            // Clean up mesh
            if (_meshMap.TryGetValue(entityId, out long meshId))
            {
                VortexAPI.DeleteMesh(meshId);
                _meshMap.Remove(entityId);
            }

            // Clean up material
            if (_materialMap.TryGetValue(entityId, out long materialId))
            {
                VortexAPI.DeleteMaterial(materialId);
                _materialMap.Remove(entityId);
            }

            // Clean up entity mapping
            if (_entityMap.TryGetValue(entityId, out long engineEntityId))
            {
                // VortexAPI.RemoveGameEntity(engineEntityId); // If needed
                _entityMap.Remove(entityId);
            }

            _dirtyEntities.Remove(entityId);
        }

        /// <summary>
        /// Clear all sync data.
        /// </summary>
        public void ClearAll()
        {
            foreach (var meshId in _meshMap.Values)
            {
                if (meshId >= 0)
                {
                    try { VortexAPI.DeleteMesh(meshId); } catch { }
                }
            }

            foreach (var materialId in _materialMap.Values)
            {
                if (materialId >= 0)
                {
                    try { VortexAPI.DeleteMaterial(materialId); } catch { }
                }
            }

            _entityMap.Clear();
            _meshMap.Clear();
            _materialMap.Clear();
            _dirtyEntities.Clear();
        }

        private long GetOrCreateMesh(Guid entityId, MeshRenderer renderer)
        {
            // Check if we already have a mesh for this entity
            if (_meshMap.TryGetValue(entityId, out long existingMesh))
            {
                // TODO: Check if mesh path changed, recreate if needed
                return existingMesh;
            }

            // Create new mesh based on mesh path
            long meshId = CreateMeshFromPath(renderer.MeshPath);
            
            if (meshId >= 0)
            {
                _meshMap[entityId] = meshId;
            }

            return meshId;
        }

        private long CreateMeshFromPath(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath)) return -1;

            // Handle primitive meshes
            if (meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                var primitiveName = meshPath.Substring("Primitive:".Length).ToLower();
                
                switch (primitiveName)
                {
                    case "cube":
                        return VortexAPI.CreateCubeMesh(1.0f);
                    case "sphere":
                        return VortexAPI.CreateSphereMesh(0.5f);
                    case "plane":
                    case "quad":
                        return VortexAPI.CreatePlaneMesh(10.0f, 10.0f);
                    case "cylinder":
                        return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                    case "capsule":
                        return VortexAPI.CreateCylinderMesh(0.5f, 1.0f); // Approximation
                    case "cone":
                        return VortexAPI.CreateCylinderMesh(0.5f, 1.0f); // Approximation
                    default:
                        return VortexAPI.CreateCubeMesh(1.0f); // Default to cube
                }
            }

            // Try to load from file
            return VortexAPI.LoadMeshResource(meshPath);
        }

        private long GetOrCreateMaterial(Guid entityId, MeshRenderer renderer)
        {
            // Check if we already have a material for this entity
            if (_materialMap.TryGetValue(entityId, out long existingMaterial))
            {
                return existingMaterial;
            }

            // Create new material
            long materialId = VortexAPI.CreateNewMaterial();
            
            if (materialId >= 0)
            {
                // Set material color based on renderer properties
                // Set material color from renderer
                VortexAPI.SetMaterialBaseColor(materialId, 
                    renderer.ColorR, 
                    renderer.ColorG, 
                    renderer.ColorB, 
                    renderer.ColorA);

                _materialMap[entityId] = materialId;
            }

            return materialId;
        }

        private float[] BuildWorldMatrix(Transform transform)
        {
            // Build TRS matrix
            var position = transform.LocalPosition;
            var rotation = transform.LocalRotation;
            var scale = transform.LocalScale;

            // Convert Euler angles to radians
            float rx = rotation.X * (float)(Math.PI / 180.0);
            float ry = rotation.Y * (float)(Math.PI / 180.0);
            float rz = rotation.Z * (float)(Math.PI / 180.0);

            // Calculate rotation matrix components
            float cosX = (float)Math.Cos(rx), sinX = (float)Math.Sin(rx);
            float cosY = (float)Math.Cos(ry), sinY = (float)Math.Sin(ry);
            float cosZ = (float)Math.Cos(rz), sinZ = (float)Math.Sin(rz);

            // Build rotation matrix (Y * X * Z order - common for game engines)
            float m00 = cosY * cosZ + sinX * sinY * sinZ;
            float m01 = cosX * sinZ;
            float m02 = -sinY * cosZ + sinX * cosY * sinZ;
            
            float m10 = -cosY * sinZ + sinX * sinY * cosZ;
            float m11 = cosX * cosZ;
            float m12 = sinY * sinZ + sinX * cosY * cosZ;
            
            float m20 = cosX * sinY;
            float m21 = -sinX;
            float m22 = cosX * cosY;

            // Apply scale and translation
            return new float[]
            {
                m00 * scale.X, m01 * scale.X, m02 * scale.X, 0,
                m10 * scale.Y, m11 * scale.Y, m12 * scale.Y, 0,
                m20 * scale.Z, m21 * scale.Z, m22 * scale.Z, 0,
                position.X, position.Y, position.Z, 1
            };
        }

        public void Dispose()
        {
            Shutdown();
            _instance = null;
        }
    }
}
