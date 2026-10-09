using System;
using System.Numerics;
using Editor.Core.Services.Decals;

namespace VortexTests
{
    /// <summary>Projected decals (#120): the box a spawned decal builds from a hit point and normal, and the spawned-decal
    /// bookkeeping (lifetime, fade, cap) — the managed half that needs no renderer.</summary>
    public static class DecalTests
    {
        private static Vector3 Row(float[] m, int r) => new Vector3(m[r * 4], m[r * 4 + 1], m[r * 4 + 2]);

        [Test]
        public static void SpawnBoxProjectsAlongTheNormal(TestContext t)
        {
            // a wall facing -Z: the box's Y (projection axis) follows the normal, its Z (texture up) follows the world up
            var w = DecalService.BuildWorld(new Vector3(1, 2, 3), new Vector3(0, 0, -1), new Vector3(2f, 0.5f, 4f), 0f);
            t.True((Row(w, 1) - new Vector3(0, 0, -0.5f)).Length() < 1e-4f, "row 1 = normal × depth (" + Row(w, 1) + ")");
            t.True((Row(w, 2) - new Vector3(0, 4f, 0)).Length() < 1e-4f, "row 2 = world up × size (" + Row(w, 2) + ")");
            t.True((Row(w, 0) - new Vector3(2f, 0, 0)).Length() < 1e-4f, "row 0 = across × size, right-handed (" + Row(w, 0) + ")");
            t.True((Row(w, 3) - new Vector3(1, 2, 3)).Length() < 1e-5f, "row 3 = position");
            // a floor: the up hint switches to world Z so the basis stays well defined
            var f = DecalService.BuildWorld(Vector3.Zero, Vector3.UnitY, Vector3.One, 0f);
            t.True((Row(f, 1) - Vector3.UnitY).Length() < 1e-4f, "floor: projection axis = +Y");
            t.True((Row(f, 2) - Vector3.UnitZ).Length() < 1e-4f, "floor: texture up = +Z");
            t.True((Row(f, 0) - Vector3.UnitX).Length() < 1e-4f, "floor: across = +X");
            // rotation around the normal turns the across / up axes, not the normal
            var r = DecalService.BuildWorld(Vector3.Zero, Vector3.UnitY, Vector3.One, 90f);
            t.True((Row(r, 1) - Vector3.UnitY).Length() < 1e-4f, "rotation keeps the projection axis");
            t.True((Row(r, 0) - Vector3.UnitZ).Length() < 1e-4f, "90° turns across onto +Z (" + Row(r, 0) + ")");
            t.True((Row(r, 2) + Vector3.UnitX).Length() < 1e-4f, "90° turns up onto -X (" + Row(r, 2) + ")");
            // a degenerate normal still yields a usable box
            var d = DecalService.BuildWorld(Vector3.Zero, Vector3.Zero, Vector3.One, 0f);
            t.True(Math.Abs(Row(d, 1).Length() - 1f) < 1e-4f, "zero normal falls back to +Y");
        }

        [Test]
        public static void SpawnedDecalsLiveFadeAndExpire(TestContext t)
        {
            DecalService.Clear();
            int cap = DecalService.MaxSpawned;
            try
            {
                long a = DecalService.Spawn("", Vector3.Zero, Vector3.UnitY, Vector3.One, 1f, 0f);
                long b = DecalService.Spawn("", Vector3.Zero, Vector3.UnitY, Vector3.One, 0f, 0f);
                t.True(a != 0 && b != 0 && a != b, "spawns return distinct ids");
                t.Equal(2, DecalService.SpawnedCount, "two decals alive");
                DecalService.Tick(0.6f);
                t.True(DecalService.IsAlive(a), "alive at 0.6 of a 1 s lifetime");
                DecalService.Tick(0.6f);
                t.False(DecalService.IsAlive(a), "expired after 1.2 s");
                t.True(DecalService.IsAlive(b), "a decal without a lifetime stays");
                t.True(DecalService.Destroy(b), "destroy removes it");
                t.False(DecalService.IsAlive(b), "destroyed");
                t.Equal(0, DecalService.SpawnedCount, "none left");
                DecalService.MaxSpawned = 3;
                long first = DecalService.Spawn("", Vector3.Zero, Vector3.UnitY, Vector3.One, 0f, 0f);
                for (int i = 0; i < 4; i++) DecalService.Spawn("", Vector3.Zero, Vector3.UnitY, Vector3.One, 0f, 0f);
                t.Equal(3, DecalService.SpawnedCount, "the cap holds");
                t.False(DecalService.IsAlive(first), "the oldest decal went first");
            }
            finally
            {
                DecalService.MaxSpawned = cap;
                DecalService.Clear();
            }
        }
    }
}
