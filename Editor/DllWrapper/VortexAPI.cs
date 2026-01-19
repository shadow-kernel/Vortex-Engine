using Editor.ECS;
using Editor.ECS.Components;
using Editor.EngineAPIStructs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Editor.EngineAPIStructs
{
    [StructLayout(LayoutKind.Sequential)]
    class TransformComponent
    {
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Scale = new Vector3(1,1,1);
    }

    [StructLayout(LayoutKind.Sequential)]
    class GameEntityDescriptor
    {
        public TransformComponent Transform = new TransformComponent();
    }
}

namespace Editor.DllWrapper
{
    static class VortexAPI
    {
        private const string _dllName = "VortexAPI.dll";

        [DllImport(_dllName)]
        private static extern long CreateGameEntity(GameEntityDescriptor descriptor);

        public static long CreateGameEntity(GameEntity gameEntity)
        {
            GameEntityDescriptor descriptor = new GameEntityDescriptor();

            {
                var c = gameEntity.GetComponent<Transform>();
                descriptor.Transform.Position = c.LocalPosition;
                descriptor.Transform.Rotation = c.LocalRotation;
                descriptor.Transform.Scale = c.LocalScale;
            }

            return CreateGameEntity(descriptor);
        }

        [DllImport(_dllName)]
        private static extern void RemoveGameEntity(long id);

        public static void RemoveGameEntity(GameEntity gameEntity)
        {
            RemoveGameEntity(gameEntity.EntityId);
        }

    }
}
