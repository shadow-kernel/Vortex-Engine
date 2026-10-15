using System.Collections.Generic;

namespace Editor.ECS
{
    /// <summary>
    /// What changed for the scene renderer since its last submit (#361), reported by the ECS itself: the entities whose
    /// transform changed (the world matrices of their subtrees), the entities whose MeshRenderer changed (mesh, material,
    /// colour, layer, enabled), and whether the structure changed — an entity added, removed, re-parented, (de)activated or
    /// hidden, a component added, removed or switched on or off. The submit reads this instead of walking the whole scene
    /// every frame; a structure change makes the next submit walk the scene once.
    /// </summary>
    internal static class RenderChanges
    {
        /// <summary>Entries per list; beyond that the lists give up and the next submit walks the scene once instead.</summary>
        internal const int MaxTracked = 4096;

        /// <summary>Advanced by <see cref="Clear"/>: an entity is listed at most once per generation.</summary>
        internal static int Generation = 1;

        internal static readonly List<GameEntity> Moved = new List<GameEntity>();
        internal static readonly List<GameEntity> Restyled = new List<GameEntity>();

        /// <summary>Something changed the scene's structure: the next submit walks the whole scene (which also covers every
        /// listed entity).</summary>
        internal static bool Structure = true;

        internal static void TransformChanged(GameEntity e)
        {
            Core.Services.SceneRenderService.RuntimeDirty = true;   // the player submits only when something is dirty
            if (e == null || Structure || e.RenderMovedGeneration == Generation) return;
            e.RenderMovedGeneration = Generation;
            if (Moved.Count >= MaxTracked) { Structure = true; return; }
            Moved.Add(e);
        }

        internal static void RendererChanged(GameEntity e)
        {
            Core.Services.SceneRenderService.RuntimeDirty = true;
            if (e == null || Structure || e.RenderRestyledGeneration == Generation) return;
            e.RenderRestyledGeneration = Generation;
            if (Restyled.Count >= MaxTracked) { Structure = true; return; }
            Restyled.Add(e);
        }

        /// <summary>Counts every structure change (never reset): a cache over the hierarchy is valid while it is unchanged.</summary>
        internal static int StructureVersion;

        internal static void StructureChanged()
        {
            Structure = true;
            StructureVersion++;
            Core.Services.SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>A component was switched on or off: a renderer changes its own look, an Animator decides whether the meshes
        /// under it are drawn per frame (skinned) — a structure change.</summary>
        internal static void ComponentToggled(Component c)
        {
            if (c is Components.Rendering.MeshRenderer mr) { mr.RenderVersion++; RendererChanged(mr.Entity); }
            else if (c is Components.Animation.Animator) StructureChanged();
        }

        /// <summary>The submit took everything listed: start the next generation.</summary>
        internal static void Clear()
        {
            Moved.Clear();
            Restyled.Clear();
            Structure = false;
            Generation++;
        }
    }
}
