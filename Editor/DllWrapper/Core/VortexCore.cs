using System;
using System.Runtime.InteropServices;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.EngineAPIStructs;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>
    /// Core VortexAPI functionality - runtime, scenes, entities.
    /// </summary>
    public static partial class VortexAPI
    {
        private const string _dllName = "VortexAPI.dll";
        private const CallingConvention _cc = CallingConvention.Cdecl;

        #region Runtime

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void InitializeRuntime();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ShutdownRuntime();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void StepRuntime(float deltaTime);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern float GetGameTime();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ResetGameTime();

        public static void InitEngineRuntime() => InitializeRuntime();
        public static void ShutdownEngineRuntime() => ShutdownRuntime();

        /// <summary>
        /// Advances the game simulation by one tick (dt seconds). Call once per
        /// frame in play mode / from the standalone player, before rendering.
        /// </summary>
        public static void StepEngineRuntime(float deltaTime) => StepRuntime(deltaTime);

        /// <summary>Elapsed in-game seconds since play started (fixed-timestep game clock).</summary>
        public static float GameTime() => GetGameTime();

        /// <summary>Reset the game clock — call when Play starts.</summary>
        public static void ResetGameClock() => ResetGameTime();

        #endregion

        #region Scenes

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateScene();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void DestroyScene(long sceneId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ActivateScene(long sceneId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void DeactivateScene(long sceneId);

        public static SceneHandle CreateEngineScene()
        {
            var id = CreateScene();
            return new SceneHandle { Id = id };
        }

        public static void DestroyEngineScene(SceneHandle handle)
        {
            if (!handle.IsValid) return;
            DestroyScene(handle.Id);
        }

        public static void ActivateEngineScene(SceneHandle handle)
        {
            if (!handle.IsValid) return;
            ActivateScene(handle.Id);
        }

        public static void DeactivateEngineScene(SceneHandle handle)
        {
            if (!handle.IsValid) return;
            DeactivateScene(handle.Id);
        }

        #endregion

        #region Entities

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateGameEntity(GameEntityDescriptor descriptor);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateGameEntityInScene(long sceneId, GameEntityDescriptor descriptor);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void RemoveGameEntity(long id);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void RemoveGameEntityInScene(long sceneId, long id);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetGameEntityTransform(long entityId, GameEntityDescriptor descriptor);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetEntityRigidbody(long entityId, [MarshalAs(UnmanagedType.I1)] bool useGravity);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ClearRigidbodies();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void GetEntityPosition(long entityId, [In, Out] float[] outXyz);

        public static long CreateGameEntity(GameEntity gameEntity)
        {
            GameEntityDescriptor descriptor = new GameEntityDescriptor();
            var c = gameEntity.GetComponent<Transform>();
            descriptor.Transform.Position = c.LocalPosition;
            descriptor.Transform.Rotation = c.LocalRotation;
            descriptor.Transform.Scale = c.LocalScale;
            return CreateGameEntity(descriptor);
        }

        public static long CreateGameEntity(GameEntity gameEntity, SceneHandle sceneHandle)
        {
            GameEntityDescriptor descriptor = new GameEntityDescriptor();
            var c = gameEntity.GetComponent<Transform>();
            descriptor.Transform.Position = c.LocalPosition;
            descriptor.Transform.Rotation = c.LocalRotation;
            descriptor.Transform.Scale = c.LocalScale;

            if (sceneHandle.IsValid)
                return CreateGameEntityInScene(sceneHandle.Id, descriptor);
            return CreateGameEntity(descriptor);
        }

        /// <summary>
        /// Push an entity's current transform to the engine so its engine-side transform stays
        /// authoritative/live. No-op for entities not yet created in the engine. Rotation is Euler
        /// (the engine converts to a quaternion via the same path used at creation).
        /// </summary>
        public static void SetEntityTransform(long entityId, Vector3 position, Vector3 rotationEuler, Vector3 scale)
        {
            if (!ID.IsValid(entityId)) return;

            var descriptor = new GameEntityDescriptor();
            descriptor.Transform.Position = position;
            descriptor.Transform.Rotation = rotationEuler;
            descriptor.Transform.Scale = scale;
            SetGameEntityTransform(entityId, descriptor);
        }

        /// <summary>Register an entity as a gravity-affected dynamic body for the play-mode tick.</summary>
        public static void RegisterRigidbody(long entityId, bool useGravity)
        {
            if (ID.IsValid(entityId)) SetEntityRigidbody(entityId, useGravity);
        }

        /// <summary>Clear all play-mode rigidbodies (call on Stop).</summary>
        public static void ClearAllRigidbodies() => ClearRigidbodies();

        /// <summary>Read an entity's current engine-side position (the runtime authority during play).</summary>
        public static Vector3 ReadEntityPosition(long entityId)
        {
            var a = new float[3];
            if (ID.IsValid(entityId)) GetEntityPosition(entityId, a);
            return new Vector3(a[0], a[1], a[2]);
        }

        public static void RemoveGameEntity(GameEntity gameEntity)
        {
            RemoveGameEntity(gameEntity.EntityId);
        }

        public static void RemoveGameEntity(GameEntity gameEntity, SceneHandle sceneHandle)
        {
            if (sceneHandle.IsValid)
            {
                RemoveGameEntityInScene(sceneHandle.Id, gameEntity.EntityId);
                return;
            }
            RemoveGameEntity(gameEntity.EntityId);
        }

        #endregion
    }

    /// <summary>
    /// Handle to an engine scene.
    /// </summary>
    public struct SceneHandle
    {
        public long Id;
        public bool IsValid => ID.IsValid(Id);
        public static SceneHandle Invalid => new SceneHandle { Id = ID.INVALID_ID };
    }
}
