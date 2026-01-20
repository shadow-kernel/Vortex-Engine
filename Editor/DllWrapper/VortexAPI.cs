using System.Runtime.InteropServices;
using Editor.ECS;

namespace Editor.EngineAPIStructs
{
    /// <summary>
    /// Transform component data for engine communication.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public class TransformComponent
    {
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Scale = new Vector3(1, 1, 1);
    }

    /// <summary>
    /// Descriptor for creating game entities in the engine.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public class GameEntityDescriptor
    {
        public TransformComponent Transform = new TransformComponent();
    }
}

namespace Editor.DllWrapper
{
    /// <summary>
    /// Main entry point for the Vortex Engine API.
    /// This is a partial class - implementation is split across multiple files:
    /// - Core/VortexCore.cs - Runtime, Scenes, Entities
    /// - Rendering/VortexRendering.cs - Viewport, Camera, Rendering
    /// - Resources/VortexResources.cs - Mesh, Material, Resource loading
    /// - Gizmos/TransformGizmo.cs - Transform gizmo (Move tool)
    /// - Gizmos/RotationGizmo.cs - Rotation gizmo (Rotate tool)
    /// - Gizmos/ScaleGizmo.cs - Scale gizmo (Scale tool)
    /// - Gizmos/SelectionOutline.cs - Selection outline rendering
    /// </summary>
    public static partial class VortexAPI
    {
        // This file serves as the main entry point and documentation.
        // All implementation is in the partial class files in subfolders.
    }
}
