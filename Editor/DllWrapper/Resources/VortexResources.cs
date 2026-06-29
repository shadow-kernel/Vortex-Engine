using System;
using System.Runtime.InteropServices;

namespace Editor.DllWrapper
{
    /// <summary>
    /// VortexAPI - Mesh and Material creation.
    /// </summary>
    public static partial class VortexAPI
    {
        #region Primitive Mesh Creation

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreatePrimitiveCube(float size);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreatePrimitiveSphere(float radius);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateInvertedSphere(float radius);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreatePrimitivePlane(float width, float height);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreatePrimitiveCylinder(float radius, float height);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreatePrimitiveCone(float radius, float height);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void DestroyMesh(long meshId);

        public static long CreateCubeMesh(float size = 1.0f) => CreatePrimitiveCube(size);
        public static long CreateSphereMesh(float radius = 0.5f) => CreatePrimitiveSphere(radius);
        public static long CreateInvertedSphereMesh(float radius = 0.5f) => CreateInvertedSphere(radius);
        public static long CreatePlaneMesh(float width = 1.0f, float height = 1.0f) => CreatePrimitivePlane(width, height);
        public static long CreateCylinderMesh(float radius = 0.5f, float height = 1.0f) => CreatePrimitiveCylinder(radius, height);
        public static long CreateConeMesh(float radius = 0.5f, float height = 1.0f) => CreatePrimitiveCone(radius, height);
        public static void DeleteMesh(long meshId) => DestroyMesh(meshId);

        #endregion

        #region Material Creation

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateMaterial();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialColor(long materialId, float r, float g, float b, float a);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialTexture(long materialId, long textureId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialNormalTexture(long materialId, long textureId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialMetallicTexture(long materialId, long textureId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialRoughnessTexture(long materialId, long textureId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialAOTexture(long materialId, long textureId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialMetallic(long materialId, float value);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialRoughness(long materialId, float value);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialNormalStrength(long materialId, float value);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialAO(long materialId, float value);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialUseDirectXNormals(long materialId, bool useDirectX);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialUnlit(long materialId, [MarshalAs(UnmanagedType.I1)] bool isUnlit);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMaterialEmissiveStrength(long materialId, float strength);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool MaterialHasTexture(long materialId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void DestroyMaterial(long materialId);

        public static long CreateNewMaterial() => CreateMaterial();
        public static void SetMaterialBaseColor(long materialId, float r, float g, float b, float a = 1.0f) 
            => SetMaterialColor(materialId, r, g, b, a);
        public static void SetMaterialAlbedoTexture(long materialId, long textureId)
            => SetMaterialTexture(materialId, textureId);
        public static void SetMaterialNormalMap(long materialId, long textureId)
            => SetMaterialNormalTexture(materialId, textureId);
        public static void SetMaterialMetallicMap(long materialId, long textureId)
            => SetMaterialMetallicTexture(materialId, textureId);
        public static void SetMaterialRoughnessMap(long materialId, long textureId)
            => SetMaterialRoughnessTexture(materialId, textureId);
        public static void SetMaterialAOMap(long materialId, long textureId)
            => SetMaterialAOTexture(materialId, textureId);
        public static void SetMaterialMetallicValue(long materialId, float value)
            => SetMaterialMetallic(materialId, value);
        public static void SetMaterialRoughnessValue(long materialId, float value)
            => SetMaterialRoughness(materialId, value);
        public static void SetMaterialNormalStrengthValue(long materialId, float value)
            => SetMaterialNormalStrength(materialId, value);
        public static void SetMaterialAOValue(long materialId, float value)
            => SetMaterialAO(materialId, value);
        public static void SetMaterialNormalFormat(long materialId, bool useDirectX)
            => SetMaterialUseDirectXNormals(materialId, useDirectX);
        
        /// <summary>
        /// Set material to unlit mode (no lighting, just texture/color * emissive strength).
        /// Used for skyboxes, UI elements, etc.
        /// </summary>
        public static void SetMaterialAsUnlit(long materialId, bool isUnlit)
            => SetMaterialUnlit(materialId, isUnlit);
        
        /// <summary>
        /// Set emissive brightness multiplier for unlit materials.
        /// Default is 1.0, higher values make it brighter.
        /// </summary>
        public static void SetMaterialEmissiveBrightness(long materialId, float strength)
            => SetMaterialEmissiveStrength(materialId, strength);

        public static bool HasMaterialTexture(long materialId)
            => MaterialHasTexture(materialId);
        public static void DeleteMaterial(long materialId) => DestroyMaterial(materialId);

        #endregion

        #region Resource Loading

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long LoadMesh([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long LoadTexture([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long LoadMaterial([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long LoadShader([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long LoadAudio([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void UnloadResource(long handle);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long LoadPrefab([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long InstantiatePrefab(long sceneId, long prefabHandle, Editor.EngineAPIStructs.GameEntityDescriptor descriptor);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void UnloadPrefab(long prefabHandle);

        public static long LoadMeshResource(string path) => LoadMesh(path);
        public static long LoadTextureResource(string path) => LoadTexture(path);
        public static long LoadMaterialResource(string path) => LoadMaterial(path);
        public static long LoadShaderResource(string path) => LoadShader(path);
        public static long LoadAudioResource(string path) => LoadAudio(path);
        public static void UnloadResourceHandle(long handle) => UnloadResource(handle);
        public static long LoadPrefabResource(string path) => LoadPrefab(path);
        public static void UnloadPrefabResource(long prefabHandle) => UnloadPrefab(prefabHandle);

        public static long InstantiatePrefabInScene(SceneHandle sceneHandle, long prefabHandle, Editor.ECS.GameEntity gameEntity)
        {
            if (gameEntity == null) return Editor.Utilities.ID.INVALID_ID;

            var descriptor = new Editor.EngineAPIStructs.GameEntityDescriptor();
            var c = gameEntity.GetComponent<Editor.ECS.Components.Transform>();
            descriptor.Transform.Position = c.LocalPosition;
            descriptor.Transform.Rotation = c.LocalRotation;
            descriptor.Transform.Scale = c.LocalScale;

            var sceneId = sceneHandle.IsValid ? sceneHandle.Id : Editor.Utilities.ID.INVALID_ID;
            return InstantiatePrefab(sceneId, prefabHandle, descriptor);
        }

        #endregion

        #region Model Import

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long ImportModel([MarshalAs(UnmanagedType.LPStr)] string filepath);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long ImportTexture([MarshalAs(UnmanagedType.LPStr)] string filepath);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long LoadVMesh([MarshalAs(UnmanagedType.LPStr)] string filepath);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern bool ExportMeshToVMesh(long meshId, [MarshalAs(UnmanagedType.LPStr)] string filepath);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern bool HasAssimpSupport();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int ImportModelWithMaterials(
            [MarshalAs(UnmanagedType.LPStr)] string filepath,
            [In, Out] long[] meshIds,
            [In, Out] long[] materialIds,
            [In, Out] long[] textureIds,
            int maxSubmeshes);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetModelSubmeshCount([MarshalAs(UnmanagedType.LPStr)] string filepath);

        // In-memory variants (packed/encrypted asset pak loaded into RAM — no file on disk).
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long ImportTextureFromMemory(byte[] data, int length);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int ImportModelFromMemoryWithMaterials(
            byte[] data, int length,
            [MarshalAs(UnmanagedType.LPStr)] string extHint,
            [MarshalAs(UnmanagedType.LPStr)] string virtualDir,
            [In, Out] long[] meshIds,
            [In, Out] long[] materialIds,
            [In, Out] long[] textureIds,
            int maxSubmeshes);

        public static long ImportModelFromFile(string filepath) => ImportModel(filepath);
        public static long ImportTextureFromFile(string filepath) => ImportTexture(filepath);

        /// <summary>Import a texture from an in-memory buffer (asset pak). -1 on failure.</summary>
        public static long ImportTextureFromBytes(byte[] data)
            => (data == null || data.Length == 0) ? -1 : ImportTextureFromMemory(data, data.Length);
        public static long LoadVMeshFromFile(string filepath) => LoadVMesh(filepath);
        public static bool SaveMeshToVMesh(long meshId, string filepath) => ExportMeshToVMesh(meshId, filepath);
        public static bool IsAssimpAvailable() => HasAssimpSupport();

        /// <summary>
        /// Result of multi-material model import
        /// </summary>
        public class SubmeshImportData
        {
            public long MeshId { get; set; }
            public long MaterialId { get; set; }
            public long TextureId { get; set; }
        }

        /// <summary>
        /// Import a model with separate meshes and materials for each submesh
        /// </summary>
        public static SubmeshImportData[] ImportModelWithMaterialsFromFile(string filepath)
        {
            const int maxSubmeshes = 64;
            var meshIds = new long[maxSubmeshes];
            var materialIds = new long[maxSubmeshes];
            var textureIds = new long[maxSubmeshes];

            int count = ImportModelWithMaterials(filepath, meshIds, materialIds, textureIds, maxSubmeshes);

            var result = new SubmeshImportData[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = new SubmeshImportData
                {
                    MeshId = meshIds[i],
                    MaterialId = materialIds[i],
                    TextureId = textureIds[i]
                };
            }
            return result;
        }

        /// <summary>
        /// Import a multi-material model from an in-memory buffer (asset pak loaded into RAM).
        /// extHint is the bare extension ("obj","fbx",...); virtualDir is the model's pak folder (for textures).
        /// </summary>
        public static SubmeshImportData[] ImportModelFromBytes(byte[] data, string extHint, string virtualDir)
        {
            if (data == null || data.Length == 0) return new SubmeshImportData[0];
            const int maxSubmeshes = 64;
            var meshIds = new long[maxSubmeshes];
            var materialIds = new long[maxSubmeshes];
            var textureIds = new long[maxSubmeshes];

            int count = ImportModelFromMemoryWithMaterials(data, data.Length, extHint ?? "", virtualDir ?? "",
                meshIds, materialIds, textureIds, maxSubmeshes);

            var result = new SubmeshImportData[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = new SubmeshImportData
                {
                    MeshId = meshIds[i],
                    MaterialId = materialIds[i],
                    TextureId = textureIds[i]
                };
            }
            return result;
        }

        /// <summary>
        /// Get the number of submeshes in a model file without fully importing it
        /// </summary>
        public static int GetSubmeshCount(string filepath) => GetModelSubmeshCount(filepath);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetModelSubmeshNames(
            [MarshalAs(UnmanagedType.LPStr)] string filepath,
            IntPtr outNames,
            int maxSubmeshes,
            int maxNameLength);

        /// <summary>
        /// Get the submesh names from a model file without fully importing it
        /// </summary>
        public static string[] GetSubmeshNames(string filepath, int maxSubmeshes = 64)
        {
            const int maxNameLength = 256;
            
            // Allocate memory for string pointers and strings
            IntPtr[] namePtrs = new IntPtr[maxSubmeshes];
            for (int i = 0; i < maxSubmeshes; i++)
            {
                namePtrs[i] = System.Runtime.InteropServices.Marshal.AllocHGlobal(maxNameLength);
            }

            try
            {
                // Create pointer to array of pointers
                IntPtr namesArray = System.Runtime.InteropServices.Marshal.AllocHGlobal(IntPtr.Size * maxSubmeshes);
                System.Runtime.InteropServices.Marshal.Copy(namePtrs, 0, namesArray, maxSubmeshes);

                int count = GetModelSubmeshNames(filepath, namesArray, maxSubmeshes, maxNameLength);

                System.Runtime.InteropServices.Marshal.FreeHGlobal(namesArray);

                // Read the strings
                string[] result = new string[count];
                for (int i = 0; i < count; i++)
                {
                    result[i] = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(namePtrs[i]) ?? $"Submesh_{i}";
                }

                return result;
            }
            finally
            {
                // Free allocated memory
                for (int i = 0; i < maxSubmeshes; i++)
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(namePtrs[i]);
                }
            }
        }

        /// <summary>Per-submesh PBR texture paths the model's materials actually reference (empty when absent).</summary>
        public class SubmeshTextureSet { public string Albedo = "", Normal = "", Metallic = "", Roughness = "", AO = "", Emissive = ""; }

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetModelTexturePaths(
            [MarshalAs(UnmanagedType.LPStr)] string filepath,
            IntPtr outAlbedo, IntPtr outNormal, IntPtr outMetallic,
            IntPtr outRoughness, IntPtr outAo, IntPtr outEmissive,
            int maxSubmeshes, int maxLen);

        /// <summary>Interpret a model's per-submesh texture slots from the native assimp import (all formats incl. GLB).</summary>
        public static SubmeshTextureSet[] GetSubmeshTexturePaths(string filepath, int maxSubmeshes = 64)
        {
            const int maxLen = 512;
            var blocks = new IntPtr[6];
            var bufs = new IntPtr[6][];
            try
            {
                for (int s = 0; s < 6; s++)
                {
                    bufs[s] = new IntPtr[maxSubmeshes];
                    for (int i = 0; i < maxSubmeshes; i++) bufs[s][i] = System.Runtime.InteropServices.Marshal.AllocHGlobal(maxLen);
                    blocks[s] = System.Runtime.InteropServices.Marshal.AllocHGlobal(IntPtr.Size * maxSubmeshes);
                    System.Runtime.InteropServices.Marshal.Copy(bufs[s], 0, blocks[s], maxSubmeshes);
                }
                int count = GetModelTexturePaths(filepath, blocks[0], blocks[1], blocks[2], blocks[3], blocks[4], blocks[5], maxSubmeshes, maxLen);
                if (count < 0) count = 0;
                var result = new SubmeshTextureSet[count];
                for (int i = 0; i < count; i++)
                {
                    result[i] = new SubmeshTextureSet
                    {
                        Albedo = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(bufs[0][i]) ?? "",
                        Normal = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(bufs[1][i]) ?? "",
                        Metallic = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(bufs[2][i]) ?? "",
                        Roughness = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(bufs[3][i]) ?? "",
                        AO = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(bufs[4][i]) ?? "",
                        Emissive = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(bufs[5][i]) ?? "",
                    };
                }
                return result;
            }
            catch { return new SubmeshTextureSet[0]; }
            finally
            {
                for (int s = 0; s < 6; s++)
                {
                    if (bufs[s] != null) for (int i = 0; i < maxSubmeshes; i++) if (bufs[s][i] != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(bufs[s][i]);
                    if (blocks[s] != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(blocks[s]);
                }
            }
        }

        #endregion

        #region MeshRenderer Component

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateMeshRenderer(long entityId, long meshId, long materialId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void RemoveMeshRenderer(long rendererId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMeshRendererMesh(long rendererId, long meshId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetMeshRendererMaterial(long rendererId, long materialId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long GetMeshRendererMesh(long rendererId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long GetMeshRendererMaterial(long rendererId);

        public static long CreateMeshRendererComponent(long entityId, long meshId, long materialId) 
            => CreateMeshRenderer(entityId, meshId, materialId);
        public static void DestroyMeshRendererComponent(long rendererId) 
            => RemoveMeshRenderer(rendererId);
        public static void UpdateMeshRendererMesh(long rendererId, long meshId) 
            => SetMeshRendererMesh(rendererId, meshId);
        public static void UpdateMeshRendererMaterial(long rendererId, long materialId) 
            => SetMeshRendererMaterial(rendererId, materialId);
        public static long GetMeshFromRenderer(long rendererId) 
            => GetMeshRendererMesh(rendererId);
        public static long GetMaterialFromRenderer(long rendererId) 
            => GetMeshRendererMaterial(rendererId);

        #endregion

        #region Mesh Bounds

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern bool QueryMeshBounds(long meshId, out float sizeX, out float sizeY, out float sizeZ);

        /// <summary>
        /// Get the bounding box size of a mesh.
        /// Returns true if bounds were retrieved successfully.
        /// </summary>
        public static bool GetMeshBounds(long meshId, out float sizeX, out float sizeY, out float sizeZ)
        {
            if (meshId < 0)
            {
                sizeX = sizeY = sizeZ = 1f;
                return false;
            }
            
            try
            {
                return QueryMeshBounds(meshId, out sizeX, out sizeY, out sizeZ);
            }
            catch
            {
                // Fallback to default unit cube if DLL call fails
                sizeX = sizeY = sizeZ = 1f;
                return false;
            }
        }

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern bool QueryMeshBoundsCenter(long meshId, out float centerX, out float centerY, out float centerZ);

        /// <summary>
        /// Get the center of the bounding box of a mesh.
        /// Returns true if center was retrieved successfully.
        /// </summary>
        public static bool GetMeshBoundsCenter(long meshId, out float centerX, out float centerY, out float centerZ)
        {
            if (meshId < 0)
            {
                centerX = centerY = centerZ = 0f;
                return false;
            }
            
            try
            {
                return QueryMeshBoundsCenter(meshId, out centerX, out centerY, out centerZ);
            }
            catch
            {
                centerX = centerY = centerZ = 0f;
                return false;
            }
        }

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SubmitAllMeshRenderers();

        /// <summary>
        /// Submits all active MeshRenderer components to the render queue.
        /// Should be called every frame before rendering.
        /// </summary>
        public static void SubmitMeshRenderersToQueue() => SubmitAllMeshRenderers();

        #endregion
    }
}
