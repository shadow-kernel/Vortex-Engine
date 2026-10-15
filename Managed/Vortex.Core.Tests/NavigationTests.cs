using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Data;
using Editor.Core.Services.AI;
using Editor.DllWrapper;
using Editor.ECS;

namespace VortexTests
{
    /// <summary>#517 through the managed navigation service: large bake bounds no longer cost dense tiles their navmesh
    /// (64-bit poly refs), failed tiles reach <see cref="NavBakeResult.Warnings"/> per reason with world-space rectangles,
    /// and the navmeshes the templates ship (.vnav version 1, 32-bit refs) still load. The class name sorts it after the
    /// headless tests: it loads the native engine library. Skipped without one that has Recast (VORTEX_NATIVE_DIR or the
    /// repository's CMake build tree).</summary>
    public static class ZNavigationNativeTests
    {
        private static bool Native()
        {
            try { Editor.Core.Native.NativeLoader.Register(); } catch { }
            if (NavigationService.Available) return true;
            Console.WriteLine("    (skipped: " + NavigationService.UnavailableReason + ")");
            return false;
        }

        private static NavBakeGeometry Boxes(params float[][] boxes)
        {
            var g = new NavBakeGeometry { Boxes = boxes.SelectMany(b => b.Concat(new[] { 0f, 0f, 0f, 1f })).ToArray(), BoxCount = boxes.Length };
            return g;
        }

        private static void Bounds(ref NavBakeSettings s, float x0, float y0, float z0, float x1, float y1, float z1)
        {
            s.BoundsMinX = x0; s.BoundsMinY = y0; s.BoundsMinZ = z0;
            s.BoundsMaxX = x1; s.BoundsMaxY = y1; s.BoundsMaxZ = z1;
        }

        /// <summary>Paddi's bake bounds (1,550 x 16,050 m, Cell Size 0.15, Tile Size 512: 4,389 tiles of 76.8 m) with a forest
        /// clearing of 1,600 trunks in one tile. The 32-bit poly ref left a tile of that grid 512 polygons and dropped the
        /// forest's; now it bakes, saves, reads its settings back from the file and loads.</summary>
        [Test]
        public static void LargeLevelBakesItsDenseTile(TestContext t)
        {
            if (!Native()) return;
            var s = NavigationService.DefaultSettings();
            s.CellSize = 0.15f; s.CellHeight = 0.25f; s.TileSize = 512;
            Bounds(ref s, 500f, -70f, -15800f, 2050f, 1900f, 250f);
            float cx = 500f + 5.5f * 76.8f, cz = -15800f + 100.5f * 76.8f;
            var boxes = new List<float[]> { new[] { cx, -0.5f, cz, 36f, 0.5f, 36f } };
            for (int i = 0; i < 40; i++)
                for (int k = 0; k < 40; k++)
                    boxes.Add(new[] { cx - 35.1f + 1.8f * i, 1.5f, cz - 35.1f + 1.8f * k, 0.25f, 1.5f, 0.25f });
            boxes.Add(new[] { 1900f, 1199.5f, 100f, 20f, 0.5f, 20f });   // the other region, 15.8 km away
            var r = NavigationService.Bake(Boxes(boxes.ToArray()), s);
            Console.WriteLine("        " + r.Message);
            try
            {
                t.True(r.Success, "the bake succeeds: " + r.Message);
                t.Equal(0, r.Warnings.Count, "warnings (a failed tile)");
                t.Equal(21, r.Stats.TilesX, "tiles along x");
                t.Equal(209, r.Stats.TilesZ, "tiles along z");
                t.True(r.Stats.PolyCount > 4000, "the forest's polygons are in the navmesh (" + r.Stats.PolyCount + ")");

                var scene = new Scene { Name = "Valley", FilePath = t.Path("Assets", "Scenes", "Valley.vscene") };
                t.True(NavigationService.Save(scene, r.Data, out var path) && File.Exists(path), "saved next to the scene");
                var back = NavigationService.SettingsFor(scene);   // read from the .vnav header (version 2)
                t.Equal(512, back.TileSize, "Tile Size read back from the file");
                t.True(Math.Abs(back.CellSize - 0.15f) < 1e-6f, "Cell Size read back from the file");
                t.True(NavigationService.LoadForScene(scene), "the saved navmesh loads");
                t.True(NavigationService.SamplePosition(new Vector3(cx - 34.2f, 0f, cz - 34.2f), 0.5f, out _), "navmesh between the trunks");
                var corners = new List<Vector3>();
                t.Equal(NavPathStatus.Complete, NavigationService.CalculatePath(new Vector3(cx - 34.2f, 0f, cz - 34.2f), new Vector3(cx + 34.2f, 0f, cz + 34.2f), corners),
                    "a path through the forest");
            }
            finally { NavigationService.Unload(); }
        }

        /// <summary>Tiles over a per-tile limit are reported, not just counted: the first warning says how many failed, the
        /// next one per reason names the limit, the remedies and where the tiles are. Platforms of 2 x 2 cells, one cell
        /// apart, need ~70,000 contour vertices per 40 m tile (65,533 fit); 12 such tiles fail, the floor beside them bakes.</summary>
        [Test]
        public static void FailedTilesAreListedPerReason(TestContext t)
        {
            if (!Native()) return;
            var s = NavigationService.DefaultSettings();
            s.CellSize = 0.1f; s.TileSize = 400; s.AgentRadius = 0f; s.RegionMinSize = 0f; s.RegionMergeSize = 0f;
            s.Partition = 1;     // monotone: watershed seeds no region where every cell is an edge cell
            s.FilterFlags = 0;   // the ledge filter would take the platforms' edge cells
            Bounds(ref s, 0f, -1f, 0f, 200f, 2f, 120f);
            var verts = new List<float>();
            var tris = new List<int>();
            void Quad(float x0, float z0, float x1, float z1)
            {
                int b = verts.Count / 3;
                verts.AddRange(new[] { x0, 0f, z0, x0, 0f, z1, x1, 0f, z1, x1, 0f, z0 });
                tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
            for (int i = 0; i < 533; i++)
                for (int k = 0; k < 400; k++)
                    Quad(0.3f * i + 0.01f, 0.3f * k + 0.01f, 0.3f * i + 0.19f, 0.3f * k + 0.19f);
            Quad(161f, 1f, 199f, 119f);
            var g = new NavBakeGeometry { Verts = verts.ToArray(), Tris = tris.ToArray(), TriangleCount = tris.Count / 3 };
            var r = NavigationService.Bake(g, s);
            Console.WriteLine("        " + r.Message);
            t.True(r.Success, "the rest of the level bakes: " + r.Message);
            t.Equal(2, r.Warnings.Count, "warnings: the count, then one per reason");
            t.Equal("12 tile(s) failed to build and have no navmesh.", r.Warnings[0], "the count");
            t.True(r.Warnings[1].StartsWith("12 tile(s) - too many vertices in one tile (at most 65533 fit; lower the Tile Size", StringComparison.Ordinal),
                "the reason names the limit and the remedies: " + r.Warnings[1]);
            t.True(r.Warnings[1].Contains("tile (0, 0) at x 0.0..40.0, z 0.0..40.0 (") && r.Warnings[1].EndsWith("; ... and 2 more.", StringComparison.Ordinal),
                "the tiles' world-space rectangles, the rest counted: " + r.Warnings[1]);
            t.True(r.Message.Contains(" Warning: 12 tile(s) failed to build and have no navmesh. 12 tile(s) - too many vertices"), "the message carries them");
        }

        /// <summary>The 16,384-tile limit came from the 32-bit poly ref; what is left bounds the bake's own bookkeeping, and
        /// the error names it.</summary>
        [Test]
        public static void TooManyTilesNamesTheLimit(TestContext t)
        {
            t.True(NavigationService.ErrorText(-7).Contains("262144"), "the error text names the limit: " + NavigationService.ErrorText(-7));
            if (!Native()) return;
            var s = NavigationService.DefaultSettings();
            s.CellSize = 0.2f; s.TileSize = 16;
            Bounds(ref s, 0f, 0f, 0f, 100000f, 10f, 100000f);   // 100 x 100 km of 3.2 m tiles
            var r = NavigationService.Bake(Boxes(new[] { 0f, 0f, 0f, 5f, 0.5f, 5f }), s);
            t.Equal(-7, r.Error, "refused");
            t.True(r.Message.Contains("262144"), r.Message);
        }

        /// <summary>The navmeshes the templates ship were baked before #517 (.vnav version 1, 32-bit poly refs): they load
        /// and answer path queries. Skipped when the template submodules are not checked out.</summary>
        [Test]
        public static void TemplateNavMeshesStillLoad(TestContext t)
        {
            if (!Native()) return;
            string root = FindRepoRoot();
            var files = root == null ? new string[0]
                : Directory.GetFiles(Path.Combine(root, "Templates"), "*.vnav", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            if (files.Length == 0) { Console.WriteLine("        skipped: no template navmeshes checked out"); return; }
            try
            {
                foreach (var f in files)
                {
                    var data = File.ReadAllBytes(f);
                    string name = Path.GetRelativePath(root, f);
                    t.True(NavigationService.LoadData(data, null), name + " loads (version " + BitConverter.ToInt32(data, 4) + ")");
                    t.True(NavigationService.TryGetInfo(out _, out var st) && st.PolyCount > 0, name + ": stats");
                    bool path = false;
                    for (int i = 0; i < 20 && !path; i++)
                        path = NavigationService.RandomPoint(out var a) && NavigationService.RandomPointAround(a, 15f, out var b)
                            && NavigationService.CalculatePath(a, b, null) == NavPathStatus.Complete;
                    t.True(path, name + ": a path between two reachable points");
                    Console.WriteLine("        " + name + ": " + st.PolyCount + " polygons in " + st.TileCount + " tiles");
                }
            }
            finally { NavigationService.Unload(); }
        }

        private static string FindRepoRoot()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
                    if (Directory.Exists(Path.Combine(dir.FullName, "Templates")) && Directory.Exists(Path.Combine(dir.FullName, "Managed"))) return dir.FullName;
            }
            catch { }
            return null;
        }
    }
}
