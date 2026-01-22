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
        private static extern long CreatePrimitivePlane(float width, float height);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreatePrimitiveCylinder(float radius, float height);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreatePrimitiveCone(float radius, float height);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void DestroyMesh(long meshId);

        public static long CreateCubeMesh(float size = 1.0f) => CreatePrimitiveCube(size);
        public static long CreateSphereMesh(float radius = 0.5f) => CreatePrimitiveSphere(radius);
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
        private static extern void DestroyMaterial(long materialId);

        public static long CreateNewMaterial() => CreateMaterial();
        public static void SetMaterialBaseColor(long materialId, float r, float g, float b, float a = 1.0f) 
            => SetMaterialColor(materialId, r, g, b, a);
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

        public static long ImportModelFromFile(string filepath) => ImportModel(filepath);
        public static long ImportTextureFromFile(string filepath) => ImportTexture(filepath);
        public static long LoadVMeshFromFile(string filepath) => LoadVMesh(filepath);
        public static bool SaveMeshToVMesh(long meshId, string filepath) => ExportMeshToVMesh(meshId, filepath);
        public static bool IsAssimpAvailable() => HasAssimpSupport();

        #endregion
    }
}
