using System;
using System.Collections.Generic;
using System.Numerics;
using Editor.Core.Foliage;

namespace VortexTests
{
    /// <summary>Painted foliage's data layer (#125): the cell index answers spacing and disc queries, removal keeps the rest,
    /// the file round-trips, orientations align to the ground, and the type presets make sense.</summary>
    public static class FoliageTests
    {
        private static FoliageLayer Grid(int n, float step)
        {
            var l = new FoliageLayer { TypeName = "Grass" };
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    l.Add(new FoliageInstance(new Vector3(x * step, 0f, z * step), Quaternion.Identity, 1f));
            return l;
        }

        [Test]
        public static void CellIndexAnswersSpacingAndDiscQueries(TestContext t)
        {
            var l = Grid(20, 2f);   // 400 instances, 2 m apart, over 38 × 38 m
            t.True(l.Count == 400, "400 instances");
            t.True(l.AnyWithin(new Vector3(10.5f, 0f, 10.5f), 1f), "a point 0.7 m from an instance is blocked at 1 m spacing");
            t.True(!l.AnyWithin(new Vector3(11f, 0f, 11f), 1f), "the cell centre is 1.41 m from the nearest: free at 1 m spacing");
            t.True(l.AnyWithin(new Vector3(100f, 0f, 100f), 200f), "a huge radius finds something far away (cells beyond the instances)");
            t.True(!l.AnyWithin(new Vector3(100f, 0f, 100f), 5f), "nothing within 5 m of a point off the grid");
            var hits = l.Within(new Vector3(20f, 0f, 20f), 3f);
            t.True(hits.Count == 9, "a 3 m disc on the 2 m grid holds the centre, its 4 neighbours and the 4 diagonals (" + hits.Count + ")");
            int cells = 0; foreach (var c in l.Cells()) cells++;
            t.True(cells == 9, "38 m of instances span 3 × 3 cells of 16 m (" + cells + ")");

            l.RemoveAt(hits);
            t.True(l.Count == 391, "removal drops exactly the disc (" + l.Count + ")");
            t.True(l.Within(new Vector3(20f, 0f, 20f), 3f).Count == 0, "the disc is empty afterwards");
            t.True(l.Within(new Vector3(0f, 0f, 0f), 1f).Count == 1, "the rest is intact (the corner instance remains)");
        }

        [Test]
        public static void DataRoundTripsAndLayersBindByName(TestContext t)
        {
            var d = new FoliageData();
            var trees = d.Layer("Tree", true);
            trees.Add(new FoliageInstance(new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f), 1.3f));
            trees.Add(new FoliageInstance(new Vector3(-4, 0.5f, 9), Quaternion.Identity, 0.9f));
            var grass = d.Layer("Grass", true);
            for (int i = 0; i < 50; i++) grass.Add(new FoliageInstance(new Vector3(i, 0, i * 0.5f), Quaternion.Identity, 1f));
            t.True(d.TotalCount == 52 && d.Layers.Count == 2, "two layers, 52 instances");
            t.True(ReferenceEquals(d.Layer("tree", false), trees), "layers bind by name, case-insensitively");
            t.True(d.Layer("Rock", false) == null, "an unknown layer is null without create");

            var bytes = d.ToBytes();
            var back = FoliageData.FromBytes(bytes);
            t.True(back != null && back.TotalCount == 52 && back.Layers.Count == 2, "the file loads back");
            var bt = back.Layer("Tree", false);
            t.True(bt != null && bt.Count == 2 && (bt.Instances[0].Position - new Vector3(1, 2, 3)).Length() < 1e-6f && Math.Abs(bt.Instances[0].Scale - 1.3f) < 1e-6f, "positions and scales survive");
            t.True(Math.Abs(bt.Instances[0].Rotation.Y - trees.Instances[0].Rotation.Y) < 1e-6f, "rotations survive");
            t.True(FoliageData.FromBytes(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }) == null, "garbage is rejected");

            d.RenameLayer("Grass", "Meadow");
            t.True(d.Layer("Meadow", false) != null && d.Layer("Grass", false) == null, "renaming re-binds the layer");
            d.RemoveLayer("Meadow");
            t.True(d.Layers.Count == 1 && d.TotalCount == 2, "removing a layer drops its instances");
        }

        [Test]
        public static void OrientationsAlignToTheGround(TestContext t)
        {
            var up = FoliageData.Orientation(Vector3.UnitY, true, 0f, 0f, 0f);
            t.True((Vector3.Transform(Vector3.UnitY, up) - Vector3.UnitY).Length() < 1e-5f, "flat ground keeps the model upright");
            var slope = Vector3.Normalize(new Vector3(1f, 1f, 0f));
            var aligned = FoliageData.Orientation(slope, true, 0f, 0f, 0f);
            t.True((Vector3.Transform(Vector3.UnitY, aligned) - slope).Length() < 1e-4f, "aligning tilts the model's up onto the normal (" + Vector3.Transform(Vector3.UnitY, aligned) + ")");
            var upright = FoliageData.Orientation(slope, false, 90f, 0f, 0f);
            t.True((Vector3.Transform(Vector3.UnitY, upright) - Vector3.UnitY).Length() < 1e-5f, "without aligning the model stays upright on a slope");
            var fwd = Vector3.Transform(Vector3.UnitZ, upright);
            t.True(Math.Abs(fwd.Y) < 1e-5f && Math.Abs(Math.Abs(fwd.X) - 1f) < 1e-4f, "a 90° yaw turns +Z onto ±X (" + fwd + ")");
            var leaning = FoliageData.Orientation(Vector3.UnitY, false, 0f, 10f, 0f);
            float dot = Vector3.Dot(Vector3.Transform(Vector3.UnitY, leaning), Vector3.UnitY);
            t.True(Math.Abs(Math.Acos(Math.Min(1f, dot)) * 180.0 / Math.PI - 10.0) < 0.01, "a 10° tilt leans the model by 10°");

            var m = new float[16];
            FoliageData.WorldMatrix(new FoliageInstance(new Vector3(5, 6, 7), Quaternion.Identity, 2f), m, 0);
            t.True(m[0] == 2f && m[5] == 2f && m[10] == 2f && m[12] == 5f && m[13] == 6f && m[14] == 7f && m[15] == 1f, "the world matrix scales and translates, row 3 = position");
        }

        [Test]
        public static void TypePresetsAndComponentHelpers(TestContext t)
        {
            var tree = Editor.ECS.Components.Rendering.Foliage.DefaultType("Pine", "Assets/Models/pine.glb", false);
            var plant = Editor.ECS.Components.Rendering.Foliage.DefaultType("Fern", "Assets/Models/fern.glb", true);
            t.True(!tree.AlignToNormal && tree.Collision == 1 && tree.CullDistance > plant.CullDistance, "trees stand upright, collide and draw farther");
            t.True(plant.AlignToNormal && plant.Collision == 0 && plant.ThinDistance > 0f && plant.Density > tree.Density, "plants align, thin with distance and grow denser");
            var f = new Editor.ECS.Components.Rendering.Foliage();
            f.Types.Add(tree); f.Types.Add(plant);
            t.True(f.IndexOfType("fern") == 1 && f.IndexOfType("Oak") == -1, "types are found by name, case-insensitively");
            int v = f.Version; f.Touch();
            t.True(f.Version == v + 1, "Touch bumps the version the service watches");
            t.True(f.Wind == 1f, "a breeze by default");
            f.Wind = -3f;
            t.True(f.Wind == 0f, "wind clamps at 0");
        }
    }
}
