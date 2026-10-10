// Vortex scripting API — heightfield terrain (#124). Read the ground under a point, trace against it, and deform or
// repaint it at runtime (craters, tracks, scorched earth). Runtime: Editor.Core.Services.Terrain.TerrainService — the
// render chunks, the collision (Jolt height field + the character's collision world) and the data follow every change.
using System;
using System.Numerics;
using Editor.Core.Data;
using Editor.Core.Services.Terrain;
using Editor.Core.Terrain;

namespace Vortex
{
    /// <summary>
    /// The scene's terrains (the <c>Terrain</c> component). Positions are world space.
    /// <code>
    /// float y = Terrain.Height(pos.X, pos.Z);                       // float.NaN off every terrain
    /// if (Physics.Raycast(muzzle, dir, 200f, out var hit) &amp;&amp; Terrain.IsTerrain(hit.Entity))
    ///     Terrain.Deform(hit.Point, 1.2f, 0.35f);                   // a crater 1.2 m wide, 35 cm deep
    /// </code>
    /// </summary>
    public static class Terrain
    {
        /// <summary>Terrains in the active scene.</summary>
        public static int Count { get { return TerrainService.Count; } }

        /// <summary>True when the entity carries a Terrain component.</summary>
        public static bool IsTerrain(long entity)
        {
            var e = Foliage.Resolve(entity);
            return e != null && e.GetComponent<Editor.ECS.Components.Rendering.Terrain>() != null;
        }

        /// <summary>The terrain surface's world Y under (x, z); <see cref="float.NaN"/> when no terrain covers the point.</summary>
        public static float Height(float x, float z)
        {
            float y;
            return TryHeight(x, z, out y) ? y : float.NaN;
        }

        /// <summary>The terrain surface's world Y under (x, z); false when no terrain covers the point.</summary>
        public static bool TryHeight(float x, float z, out float y)
        {
            y = 0f;
            var e = TerrainService.FindAt(x, z);
            return e != null && TerrainService.TryHeight(e, x, z, out y);
        }

        /// <summary>The terrain's surface normal under (x, z) (world up when no terrain covers the point).</summary>
        public static Vector3 Normal(float x, float z)
        {
            var e = TerrainService.FindAt(x, z);
            System.Numerics.Vector3 n;
            if (e != null && TerrainService.TryNormal(e, x, z, out n)) return new Vector3(n.X, n.Y, n.Z);
            return new Vector3(0f, 1f, 0f);
        }

        /// <summary>Trace a ray against every terrain; the nearest surface point.</summary>
        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point)
        {
            System.Numerics.Vector3 hit; Editor.ECS.GameEntity terrain;
            if (TerrainService.Raycast(Vfx.Sys(origin), Vfx.Sys(direction), maxDistance, out hit, out terrain))
            {
                point = new Vector3(hit.X, hit.Y, hit.Z);
                return true;
            }
            point = origin;
            return false;
        }

        /// <summary>Lower (<paramref name="depth"/> &gt; 0) or raise (&lt; 0) the terrain under <paramref name="position"/> by up to
        /// <paramref name="depth"/> metres at the centre, fading to <paramref name="radius"/>. The render chunks and the
        /// collision update on the next frame. False when no terrain covers the point.</summary>
        public static bool Deform(Vector3 position, float radius, float depth, float hardness = 0.2f)
        {
            var e = TerrainService.FindAt(position.X, position.Z);
            if (e == null) return false;
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(e, out data, out cell)) return false;
            var l = TerrainService.WorldToLocal(e, Vfx.Sys(position));
            var rect = data.Raise(l.X / cell, l.Z / cell, Math.Max(0.5f, radius / cell), -depth, hardness);
            TerrainService.MarkDirty(e, rect, true, false);
            return true;
        }

        /// <summary>Pull the terrain under <paramref name="position"/> towards <paramref name="height"/> (world Y).</summary>
        public static bool Flatten(Vector3 position, float radius, float height, float strength = 1f, float hardness = 0.2f)
        {
            var e = TerrainService.FindAt(position.X, position.Z);
            if (e == null) return false;
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(e, out data, out cell)) return false;
            var l = TerrainService.WorldToLocal(e, Vfx.Sys(position));
            var lt = TerrainService.WorldToLocal(e, new System.Numerics.Vector3(position.X, height, position.Z));
            var rect = data.Flatten(l.X / cell, l.Z / cell, Math.Max(0.5f, radius / cell), lt.Y, strength, hardness);
            TerrainService.MarkDirty(e, rect, true, false);
            return true;
        }

        /// <summary>Paint texture layer <paramref name="layer"/> (0..3) onto the terrain under <paramref name="position"/>.</summary>
        public static bool Paint(Vector3 position, float radius, int layer, float strength = 1f, float hardness = 0.2f)
        {
            var e = TerrainService.FindAt(position.X, position.Z);
            if (e == null) return false;
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(e, out data, out cell)) return false;
            var l = TerrainService.WorldToLocal(e, Vfx.Sys(position));
            var rect = data.Paint(l.X / cell, l.Z / cell, Math.Max(0.5f, radius / cell), layer, strength, hardness);
            TerrainService.MarkDirty(e, rect, false, true);
            return true;
        }

        /// <summary>The dominant texture layer (0..3) under (x, z); -1 off every terrain. Footstep and impact logic keys off it.</summary>
        public static int LayerAt(float x, float z)
        {
            var e = TerrainService.FindAt(x, z);
            if (e == null) return -1;
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(e, out data, out cell)) return -1;
            var l = TerrainService.WorldToLocal(e, new System.Numerics.Vector3(x, 0f, z));
            return data.DominantLayer((int)Math.Round(l.X / cell), (int)Math.Round(l.Z / cell));
        }

        /// <summary>Write every terrain's sculpting to its data file (the editor does this after each stroke and on scene save).</summary>
        public static int Save()
        {
            return TerrainService.SaveAll(ProjectData.Current?.ActiveScene);
        }
    }
}
