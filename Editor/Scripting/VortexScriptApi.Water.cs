// Vortex scripting API — bodies of water (#200). Where the surface is, whether something is under it: swimming,
// splashes, drowning, floating crates. Runtime: Editor.Core.Services.Water.WaterService (the Water component).
using Editor.Core.Services.Water;

namespace Vortex
{
    /// <summary>
    /// The scene's water (the <c>Water</c> component: lakes, ponds, pools). Positions are world space.
    /// <code>
    /// if (Water.IsUnderwater(PlayerRig.EyePos)) ...            // the camera dipped under: muffle, tint, swim
    /// float surface = Water.Height(pos.X, pos.Z);               // float.NaN over dry land
    /// </code>
    /// </summary>
    public static class Water
    {
        /// <summary>Bodies of water in the active scene.</summary>
        public static int Count { get { return WaterService.Count; } }

        /// <summary>The surface height (world Y) of the water covering (x, z); <see cref="float.NaN"/> over dry land.</summary>
        public static float Height(float x, float z)
        {
            float y;
            return WaterService.TryHeight(x, z, out y) ? y : float.NaN;
        }

        /// <summary>The surface height of the water covering (x, z); false over dry land.</summary>
        public static bool TryHeight(float x, float z, out float y) { return WaterService.TryHeight(x, z, out y); }

        /// <summary>True when the point lies below a water surface (<paramref name="margin"/> metres below it, for a head that is still above water).</summary>
        public static bool IsUnderwater(Vector3 position, float margin = 0f)
        {
            return WaterService.IsUnderwater(Vfx.Sys(position), margin);
        }

        /// <summary>How deep the point is under the surface (0 above water or over dry land).</summary>
        public static float Depth(Vector3 position)
        {
            float y;
            if (!WaterService.TryHeight(position.X, position.Z, out y)) return 0f;
            float d = y - position.Y;
            return d > 0f ? d : 0f;
        }
    }
}
