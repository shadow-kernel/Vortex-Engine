// Vortex scripting API — projected decals (#120). Bullet holes, blood, scorch marks: a box stamped onto a surface that
// projects a material (.vmat) onto the geometry inside it. Runtime: Editor.Core.Services.Decals.DecalService; the
// renderer draws them between the opaque and the transparent meshes on every backend.
using System;
using Editor.Core.Services.Decals;

namespace Vortex
{
    /// <summary>How a decal combines with the surface under it.</summary>
    public enum DecalBlend
    {
        /// <summary>Shaded like a surface (ambient, sun, point / spot lights) and alpha-blended — stickers, posters, holes.</summary>
        Lit = 0,
        /// <summary>Multiplied into the lit surface — blood, grime, soot darken whatever they land on.</summary>
        Multiply = 1,
        /// <summary>Added on top — glowing marks, light pools.</summary>
        Additive = 2,
    }

    /// <summary>A spawned decal (<see cref="Decal.Spawn(Vector3, Vector3, string, float, float)"/>). Copyable; the default value is invalid.</summary>
    public struct DecalHandle
    {
        public long Id;
        public DecalHandle(long id) { Id = id; }
        /// <summary>True while the decal exists (not expired, destroyed or pushed out by the cap).</summary>
        public bool IsAlive { get { return Id != 0 && DecalService.IsAlive(Id); } }
        /// <summary>Remove the decal now.</summary>
        public void Destroy() { if (Id != 0) DecalService.Destroy(Id); Id = 0; }
        /// <summary>Re-tint the decal (rgb multiplies the material, a is the opacity).</summary>
        public void SetColor(float r, float g, float b, float a = 1f) { if (Id != 0) DecalService.SetColor(Id, r, g, b, a); }
        public override string ToString() { return "DecalHandle(" + Id + ")"; }
    }

    /// <summary>
    /// Projected decals. Spawn one at a <c>Physics.Raycast</c> hit: the box projects along the hit normal onto the geometry
    /// around the point, its texture's up follows the world up. Materials are ordinary .vmat assets (albedo + base colour);
    /// an empty material path projects the tint alone. The scene's authored decals are the <c>Decal</c> component.
    /// <code>
    /// if (Physics.Raycast(muzzle, dir, 200f, out var hit))
    ///     Decal.Spawn(hit.Point, hit.Normal, "Assets/Materials/BulletHole.vmat", 0.12f, 30f);
    /// Decal.Spawn(hit.Point, hit.Normal, "Assets/Materials/Blood.vmat", new Vector3(0.6f, 0.3f, 0.6f), 0f, 90f, DecalBlend.Multiply);
    /// </code>
    /// </summary>
    public static class Decal
    {
        private static readonly Random _random = new Random(1234);

        /// <summary>Stamp a square decal of <paramref name="size"/> metres at <paramref name="position"/>, projecting along
        /// <paramref name="normal"/>, randomly rotated; <paramref name="lifetime"/> 0 = stays until destroyed or cleared,
        /// otherwise it fades out over its last quarter.</summary>
        public static DecalHandle Spawn(Vector3 position, Vector3 normal, string material, float size = 0.5f, float lifetime = 0f)
        {
            float depth = Math.Max(0.05f, size * 0.5f);
            return Spawn(position, normal, material, new Vector3(size, depth, size), lifetime, (float)(_random.NextDouble() * 360.0));
        }

        /// <summary>Full control: <paramref name="size"/> = (across, projection depth, across) in metres, <paramref name="rotationDeg"/>
        /// around the normal, the blend mode, tint and opacity, the angle fade (facing at which the decal is fully opaque; 0 =
        /// hard), the view distance at which it has faded (0 = never) and the sort order (higher draws on top).</summary>
        public static DecalHandle Spawn(Vector3 position, Vector3 normal, string material, Vector3 size, float lifetime, float rotationDeg,
            DecalBlend blend = DecalBlend.Lit, float r = 1f, float g = 1f, float b = 1f, float opacity = 1f,
            float angleFade = 0.5f, float fadeDistance = 0f, int sortOrder = 0)
        {
            long id = DecalService.Spawn(material, Vfx.Sys(position), Vfx.Sys(normal), Vfx.Sys(size), lifetime, rotationDeg,
                r, g, b, opacity, (int)blend, angleFade, fadeDistance, sortOrder);
            return new DecalHandle(id);
        }

        /// <summary>Remove every spawned decal (the scene's Decal components stay).</summary>
        public static void Clear() { DecalService.Clear(); }

        /// <summary>Spawned decals alive right now.</summary>
        public static int Count { get { return DecalService.SpawnedCount; } }

        /// <summary>Spawned decals kept at most (default 512); the oldest goes when a new one arrives.</summary>
        public static int MaxSpawned
        {
            get { return DecalService.MaxSpawned; }
            set { DecalService.MaxSpawned = value < 1 ? 1 : value; }
        }
    }
}
