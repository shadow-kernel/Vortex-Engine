using System;
using System.Collections.Generic;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services
{
    /// <summary>
    /// Manages rendering of scene entities in the viewport.
    /// Acts as bridge between Editor entities and Engine rendering.
    /// </summary>
    public class SceneRenderService : IDisposable
    {
        private static SceneRenderService _instance;
        public static SceneRenderService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new SceneRenderService();
                return _instance;
            }
        }

        private readonly Dictionary<Guid, long> _entityMeshes = new Dictionary<Guid, long>();
        private readonly Dictionary<Guid, long> _entityMaterials = new Dictionary<Guid, long>();
        
        // Track mesh paths to detect changes
        private readonly Dictionary<Guid, string> _entityMeshPaths = new Dictionary<Guid, string>();
        
        // Material color cache for dirty checking
        private readonly Dictionary<Guid, (float r, float g, float b, float a)> _entityMaterialColors = 
            new Dictionary<Guid, (float r, float g, float b, float a)>();

        private bool _isInitialized;

        private SceneRenderService() { }

        public void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;
        }

        public void Shutdown()
        {
            ClearAllRenderables();
            _isInitialized = false;
        }

        /// <summary>
        /// Submit an entity for rendering this frame.
        /// </summary>
        public void SubmitEntity(GameEntity entity)
        {
            if (entity == null || !entity.IsActive) return;

            var meshRenderer = entity.GetComponent<MeshRenderer>();
            if (meshRenderer == null || !meshRenderer.IsEnabled) return;

            var transform = entity.GetComponent<Transform>();
            if (transform == null) return;

            // Debug: Log if MeshPath is empty
            if (string.IsNullOrEmpty(meshRenderer.MeshPath))
            {
                System.Diagnostics.Debug.WriteLine($"[SceneRenderService] Entity '{entity.Name}' has empty MeshPath - skipping");
                return;
            }

            // Get or create mesh (with dirty checking)
            long meshId = GetOrCreateMesh(entity.Id, meshRenderer);
            if (meshId < 0)
            {
                System.Diagnostics.Debug.WriteLine($"[SceneRenderService] Failed to create mesh for entity '{entity.Name}' with path '{meshRenderer.MeshPath}'");
                return;
            }

            // Get or create material (with dirty checking)
            long materialId = GetOrCreateMaterial(entity.Id, meshRenderer);

            // Build world matrix from transform
            float[] worldMatrix = BuildWorldMatrix(transform);

            // Submit to renderer
            VortexAPI.SubmitMeshForRendering(meshId, materialId, worldMatrix);
        }


        /// <summary>
        /// Submit all entities in a scene for rendering.
        /// </summary>
        public void SubmitScene(Data.Scene scene)
        {
            if (scene == null || scene.Entities == null) return;

            foreach (var entity in scene.Entities)
            {
                SubmitEntityRecursive(entity);
            }

            // Render selection outline and gizmo for selected entity
            var selected = SelectionService.Instance.SelectedEntity;
            if (selected != null)
            {
                var transform = selected.Transform;
                if (transform != null)
                {
                    var pos = transform.LocalPosition;
                    var rot = transform.LocalRotation;
                    var scale = transform.LocalScale;
                    
                    // Render orange selection outline with rotation
                    VortexAPI.RenderSelectionOutline(pos.X, pos.Y, pos.Z, 
                        scale.X, scale.Y, scale.Z,
                        rot.X, rot.Y, rot.Z);
                    
                    // Render gizmo at object surface (top), pass object's Y scale
                    if (VortexAPI.AreGizmosVisible)
                    {
                        VortexAPI.RenderGizmo(pos.X, pos.Y, pos.Z, scale.Y, 1.0f);
                    }
                }
            }
        }

        private void SubmitEntityRecursive(GameEntity entity)
        {
            SubmitEntity(entity);

            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    SubmitEntityRecursive(child);
                }
            }
        }

        private long GetOrCreateMesh(Guid entityId, MeshRenderer renderer)
        {
            if (string.IsNullOrEmpty(renderer.MeshPath)) return -1;


            // Check if mesh path changed (dirty check)
            bool needsRecreate = false;
            if (_entityMeshPaths.TryGetValue(entityId, out string cachedPath))
            {
                if (cachedPath != renderer.MeshPath)
                {
                    // Path changed, need to recreate
                    if (_entityMeshes.TryGetValue(entityId, out long oldMesh))
                    {
                        VortexAPI.DeleteMesh(oldMesh);
                        _entityMeshes.Remove(entityId);
                    }
                    needsRecreate = true;
                }
            }
            else
            {
                needsRecreate = true;
            }

            // Check if we already have a valid mesh for this entity
            if (!needsRecreate && _entityMeshes.TryGetValue(entityId, out long existingMesh))
            {
                return existingMesh;
            }

            // Create new mesh based on path
            long meshId = CreateMeshFromPath(renderer.MeshPath);
            if (meshId >= 0)
            {
                _entityMeshes[entityId] = meshId;
                _entityMeshPaths[entityId] = renderer.MeshPath;
            }

            return meshId;
        }

        private long CreateMeshFromPath(string meshPath)
        {
            if (meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                var primitiveType = meshPath.Substring("Primitive:".Length);
                switch (primitiveType.ToLower())
                {
                    case "cube":
                        return VortexAPI.CreateCubeMesh(1.0f);
                    case "sphere":
                        return VortexAPI.CreateSphereMesh(0.5f);
                    case "plane":
                        return VortexAPI.CreatePlaneMesh(1.0f, 1.0f);
                    case "cylinder":
                        return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                    case "capsule":
                        // Capsule approximated with cylinder for now
                        return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                    case "cone":
                        // Cone approximated with cylinder for now
                        return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                    case "torus":
                        // Torus approximated with sphere for now
                        return VortexAPI.CreateSphereMesh(0.5f);
                    case "quad":
                        return VortexAPI.CreatePlaneMesh(1.0f, 1.0f);
                    default:
                        return -1;
                }
            }

            // TODO: Load mesh from file path
            return -1;
        }

        private long GetOrCreateMaterial(Guid entityId, MeshRenderer renderer)
        {
            var currentColor = (renderer.ColorR, renderer.ColorG, renderer.ColorB, renderer.ColorA);
            
            // Check if material color changed (dirty check)
            bool needsUpdate = false;
            if (_entityMaterialColors.TryGetValue(entityId, out var cachedColor))
            {
                if (cachedColor != currentColor)
                {
                    needsUpdate = true;
                }
            }
            else
            {
                needsUpdate = true;
            }

            // Check if we already have a material for this entity
            if (_entityMaterials.TryGetValue(entityId, out long existingMaterial))
            {
                // Update color if needed
                if (needsUpdate)
                {
                    VortexAPI.SetMaterialBaseColor(existingMaterial, 
                        renderer.ColorR, renderer.ColorG, renderer.ColorB, renderer.ColorA);
                    _entityMaterialColors[entityId] = currentColor;
                }
                return existingMaterial;
            }

            // Create new material
            long materialId = VortexAPI.CreateNewMaterial();
            if (materialId >= 0)
            {
                VortexAPI.SetMaterialBaseColor(materialId, 
                    renderer.ColorR, renderer.ColorG, renderer.ColorB, renderer.ColorA);
                _entityMaterials[entityId] = materialId;
                _entityMaterialColors[entityId] = currentColor;
            }

            return materialId;
        }

        private float[] BuildWorldMatrix(Transform transform)
        {
            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation; // Rotation in degrees (Euler angles)
            var scale = transform.LocalScale;

            // Convert degrees to radians
            float radX = rot.X * (float)(Math.PI / 180.0);
            float radY = rot.Y * (float)(Math.PI / 180.0);
            float radZ = rot.Z * (float)(Math.PI / 180.0);

            // Pre-calculate sin and cos
            float cosX = (float)Math.Cos(radX), sinX = (float)Math.Sin(radX);
            float cosY = (float)Math.Cos(radY), sinY = (float)Math.Sin(radY);
            float cosZ = (float)Math.Cos(radZ), sinZ = (float)Math.Sin(radZ);

            // Build rotation matrix (ZXY order for Unity-like behavior)
            // R = Rz * Rx * Ry
            float r00 = cosZ * cosY + sinZ * sinX * sinY;
            float r01 = sinZ * cosX;
            float r02 = -cosZ * sinY + sinZ * sinX * cosY;

            float r10 = -sinZ * cosY + cosZ * sinX * sinY;
            float r11 = cosZ * cosX;
            float r12 = sinZ * sinY + cosZ * sinX * cosY;

            float r20 = cosX * sinY;
            float r21 = -sinX;
            float r22 = cosX * cosY;

            // Combine with scale: S * R (scale first, then rotate)
            // Final matrix: Scale * Rotation * Translation (in column-major terms)
            // For row-major DirectX: Transpose the rotation part
            return new float[]
            {
                scale.X * r00, scale.X * r01, scale.X * r02, 0,
                scale.Y * r10, scale.Y * r11, scale.Y * r12, 0,
                scale.Z * r20, scale.Z * r21, scale.Z * r22, 0,
                pos.X,         pos.Y,         pos.Z,         1
            };
        }

        /// <summary>
        /// Notify that an entity's mesh has changed.
        /// </summary>
        public void OnMeshChanged(Guid entityId)
        {
            // Remove old mesh so it gets recreated
            if (_entityMeshes.TryGetValue(entityId, out long meshId))
            {
                VortexAPI.DeleteMesh(meshId);
                _entityMeshes.Remove(entityId);
            }
        }

        /// <summary>
        /// Remove an entity from the render system.
        /// </summary>
        public void RemoveEntity(Guid entityId)
        {
            if (_entityMeshes.TryGetValue(entityId, out long meshId))
            {
                VortexAPI.DeleteMesh(meshId);
                _entityMeshes.Remove(entityId);
            }

            if (_entityMaterials.TryGetValue(entityId, out long materialId))
            {
                VortexAPI.DeleteMaterial(materialId);
                _entityMaterials.Remove(entityId);
            }

            _entityMeshPaths.Remove(entityId);
            _entityMaterialColors.Remove(entityId);
        }

        /// <summary>
        /// Clear all renderables.
        /// </summary>
        public void ClearAllRenderables()
        {
            foreach (var meshId in _entityMeshes.Values)
            {
                VortexAPI.DeleteMesh(meshId);
            }
            _entityMeshes.Clear();

            foreach (var materialId in _entityMaterials.Values)
            {
                VortexAPI.DeleteMaterial(materialId);
            }
            _entityMaterials.Clear();

            _entityMeshPaths.Clear();
            _entityMaterialColors.Clear();
        }

        public void Dispose()
        {
            Shutdown();
        }
    }
}
