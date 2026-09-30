using System.Threading.Tasks;
using Editor.ECS;

namespace VortexEditor.Services
{
    /// <summary>
    /// Default ("plain double-click") actions on assets, shared by the Asset Browser, the viewport drop target and the
    /// hierarchy drop target: add a model / prefab / primitive to the scene (optionally at a world position and under
    /// a parent), open a scene, apply a material, … Implemented by the Asset Browser work package.
    /// </summary>
    public static class AssetActions
    {
        /// <summary>Add a model (.glb/.gltf/.fbx/.obj …), prefab (.ventity) or primitive ("Primitive:Cube") to the active
        /// scene — undoable, selected afterwards. <paramref name="position"/> null = in front of the editor camera.
        /// Returns the created entity (null when the asset can't be placed).</summary>
        public static GameEntity AddToScene(string fullPathOrPrimitive, Vector3? position = null, GameEntity parent = null) => null;

        /// <summary>The plain double-click action for any asset (add to scene, open scene, open script, …).
        /// Returns false when the asset type has no default action.</summary>
        public static Task<bool> OpenDefault(string fullPath) => Task.FromResult(false);
    }
}
