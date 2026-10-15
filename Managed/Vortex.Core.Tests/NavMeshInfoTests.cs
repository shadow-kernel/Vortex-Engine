using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Editor.Core.Data;
using Editor.Core.Services.AI;
using Editor.Core.Services.Physics;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Physics;

namespace VortexTests
{
    /// <summary>What navmesh_info reports (#449): a bake appends a record of what it read behind the .vnav blob — one entry per
    /// entity — so a later check can tell a stale navmesh (walls moved, props added or gone) from a current one, by entity, in
    /// any order and within a tolerance; the header also gives the settings and stats without loading anything.</summary>
    public static class NavMeshInfoTests
    {
        /// <summary>A 20 m floor with a 12 m wall across it, as box colliders.</summary>
        internal static Scene Level(Vector3 wallAt)
        {
            var scene = new Scene { Name = "Level" };
            scene.Entities.Add(Box("Floor", new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20)));
            scene.Entities.Add(Box("Wall", wallAt, new Vector3(1, 2, 12)));
            return scene;
        }

        private static GameEntity Box(string name, Vector3 pos, Vector3 size)
        {
            var e = new GameEntity(name);
            e.Transform.LocalPosition = pos;
            e.Components.Add(new BoxCollider(e) { Size = size });
            return e;
        }

        private static GameEntity Named(Scene s, string name)
        {
            foreach (var e in s.Entities) if (e.Name == name) return e;
            return null;
        }

        private static NavBakeGeometry Collect(Scene s) => NavigationService.CollectGeometry(s, (int)NavGeometrySource.Default);

        private static byte[] Header(NavBakeSettings s, NavBakeStats st)
        {
            var data = new byte[NavigationService.HeaderSize + 64];
            BitConverter.GetBytes(0x56414E56u).CopyTo(data, 0);   // "VNAV"
            BitConverter.GetBytes(2).CopyTo(data, 4);
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(s, h.AddrOfPinnedObject() + 8, false);
                Marshal.StructureToPtr(st, h.AddrOfPinnedObject() + 8 + Marshal.SizeOf<NavBakeSettings>(), false);
            }
            finally { h.Free(); }
            return data;
        }

        /// <summary>The file a bake of the scene would write, as navmesh_info reads it.</summary>
        private static NavMeshFileInfo Baked(Scene s)
            => NavigationService.ParseHeader(NavigationService.StampInputs(Header(NavigationService.DefaultSettings(), new NavBakeStats { PolyCount = 1 }), Collect(s)));

        private static bool? Stale(NavMeshFileInfo file, Scene now, out string why) => NavigationService.IsStale(file, Collect(now), out why);

        [Test]
        public static void EachEntityIsRecordedWithWhatItAdded(TestContext t)
        {
            var g = Collect(Level(new Vector3(0, 1, 0)));
            t.Equal(2, g.Owners.Count, "the floor and the wall");
            var wall = g.Owners.Find(o => o.Name == "Wall");
            t.True(wall != null && wall.Boxes == 1 && wall.Triangles == 0, "the wall added one box");
            t.True(wall.Pose[9] == 0f && wall.Pose[10] == 1f && wall.Pose[11] == 0f && wall.Pose[0] == 1f && wall.Pose[4] == 1f && wall.Pose[8] == 1f, "its pose: lifted by 1 m, not turned");
            t.True(Math.Abs(wall.Extent[0] + 0.5f) < 1e-5f && Math.Abs(wall.Extent[1]) < 1e-5f && Math.Abs(wall.Extent[2] + 6f) < 1e-5f
                && Math.Abs(wall.Extent[3] - 0.5f) < 1e-5f && Math.Abs(wall.Extent[4] - 2f) < 1e-5f && Math.Abs(wall.Extent[5] - 6f) < 1e-5f, "its bounds");
            t.True(Math.Abs(wall.Extent[7] - 1f) < 1e-5f && Math.Abs(wall.Extent[9] - 24f) < 1e-3f, "its centre and volume (1 × 2 × 12)");
        }

        [Test]
        public static void TheRecordTravelsBehindTheBlob(TestContext t)
        {
            t.Equal(92, Marshal.SizeOf<NavBakeSettings>(), "the settings block of NavigationFormat.h");
            t.Equal(64, Marshal.SizeOf<NavBakeStats>(), "the stats block of NavigationFormat.h");
            var s = NavigationService.DefaultSettings();
            s.AgentRadius = 0.3f;
            s.SourceFlags = (int)NavGeometrySource.Colliders;
            var st = new NavBakeStats { PolyCount = 42, TileCount = 3, TilesX = 2, TilesZ = 2, InputBoxes = 2, BoundsMinX = -10, BoundsMaxX = 10 };
            var blob = Header(s, st);

            var legacy = NavigationService.ParseHeader(blob);
            t.True(legacy != null && legacy.Inputs == null, "a navmesh baked before bakes recorded their input has no record");
            t.True(Math.Abs(legacy.Settings.AgentRadius - 0.3f) < 1e-6f && legacy.Settings.SourceFlags == (int)NavGeometrySource.Colliders, "the settings it was baked with");
            t.True(legacy.Stats.PolyCount == 42 && legacy.Stats.TileCount == 3 && legacy.Stats.InputBoxes == 2 && legacy.Stats.BoundsMaxX == 10f, "and its stats");

            var g = Collect(Level(new Vector3(0, 1, 0)));
            var data = NavigationService.StampInputs(blob, g);
            t.True(data.Length > blob.Length && data.AsSpan(0, NavigationService.HeaderSize - 16).SequenceEqual(blob.AsSpan(0, NavigationService.HeaderSize - 16))
                && data.AsSpan(NavigationService.HeaderSize, blob.Length - NavigationService.HeaderSize).SequenceEqual(blob.AsSpan(NavigationService.HeaderSize)),
                "the blob is untouched but for the reserved words; the record goes behind it");
            var stamped = NavigationService.ParseHeader(data);
            t.True(Math.Abs(stamped.Settings.AgentRadius - 0.3f) < 1e-6f && stamped.Stats.PolyCount == 42, "settings and stats as before");
            t.True(stamped.Inputs != null && stamped.Inputs.Count == g.Owners.Count, "one entry per entity");
            for (int i = 0; i < g.Owners.Count; i++)
            {
                var a = g.Owners[i];
                var b = stamped.Inputs[i];
                t.True(a.Id == b.Id && a.Signature == b.Signature && a.Triangles == b.Triangles && a.Boxes == b.Boxes
                    && a.Pose.AsSpan().SequenceEqual(b.Pose) && a.Extent.AsSpan().SequenceEqual(b.Extent), "entry " + i + " reads back as written");
                t.True(NavigationService.SameInput(a, b), "and is the same input");
            }
            var cut = data.AsSpan(0, data.Length - 5).ToArray();
            t.True(NavigationService.ParseHeader(cut).Inputs == null, "a cut-off record reads as none");
            t.True(NavigationService.ParseHeader(new byte[400]) == null && NavigationService.ParseHeader(new byte[10]) == null, "no navmesh, no header");
        }

        [Test]
        public static void StaleByEntityInAnyOrderWithinATolerance(TestContext t)
        {
            var scene = Level(new Vector3(0, 1, 0));
            var file = Baked(scene);
            var wall = Named(scene, "Wall");
            t.True(Stale(file, scene, out var why) == false && why == null, "just baked: current");

            var floor = Named(scene, "Floor");
            scene.Entities.Remove(floor);
            scene.Entities.Add(floor);
            t.True(Stale(file, scene, out why) == false, "the same entities in another order: current — " + why);

            wall.Transform.LocalPosition = new Vector3(0, MathF.BitIncrement(1f), MathF.BitDecrement(0f));
            t.True(Stale(file, scene, out why) == false, "a float step is noise — " + why);
            wall.Transform.LocalPosition = new Vector3(0.0001f, 1, 0);
            t.True(Stale(file, scene, out why) == false, "a tenth of a millimetre is below the tolerance — " + why);
            wall.Transform.LocalRotation = new Vector3(0, 0.0003f, 0);
            t.True(Stale(file, scene, out why) == false, "so is a turn of 0.0003° — " + why);

            wall.Transform.LocalPosition = new Vector3(0.05f, 1, 0);
            t.True(Stale(file, scene, out why) == true && why.Contains("changed since the bake: 'Wall'") && !why.Contains("Floor"), "a wall moved by 5 cm is named — " + why);
            wall.Transform.LocalPosition = new Vector3(0, 1, 0);
            wall.Transform.LocalRotation = new Vector3(0, 1, 0);
            t.True(Stale(file, scene, out why) == true && why.Contains("'Wall'"), "turned by 1° — " + why);
            wall.Transform.LocalRotation = Vector3.Zero;
            wall.GetComponent<BoxCollider>().Size = new Vector3(1, 2.1f, 12);
            t.True(Stale(file, scene, out why) == true && why.Contains("'Wall'"), "made 10 cm higher — " + why);
            wall.GetComponent<BoxCollider>().Size = new Vector3(1, 2, 12);
            t.True(Stale(file, scene, out why) == false, "back where it was: current again — " + why);

            var crate = Box("Crate", new Vector3(5, 0.5f, 5), new Vector3(1, 1, 1));
            scene.Entities.Add(crate);
            t.True(Stale(file, scene, out why) == true && why.Contains("new: 'Crate'") && !why.Contains("changed"), "a new prop — " + why);
            scene.Entities.Remove(crate);
            scene.Entities.Remove(wall);
            t.True(Stale(file, scene, out why) == true && why.Contains("1 entity is gone"), "a removed wall — " + why);
        }

        [Test]
        public static void AParentsMoveMovesItsChildren(TestContext t)
        {
            var scene = new Scene { Name = "Level" };
            scene.Entities.Add(Box("Floor", new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20)));
            var group = new GameEntity("Walls");
            scene.Entities.Add(group);
            var wall = Box("Wall", new Vector3(0, 1, 0), new Vector3(1, 2, 12));
            wall.SetParent(group);
            var file = Baked(scene);
            bool? stale = Stale(file, scene, out var why);
            t.True(file.Inputs.Count == 2 && stale == false, "baked: the floor and the wall in its group — " + why);
            group.Transform.LocalPosition = new Vector3(2, 0, 0);
            t.True(Stale(file, scene, out why) == true && why.Contains("'Wall'") && !why.Contains("Walls'"), "the group moved, so did the wall — " + why);
        }

        [Test]
        public static void AnotherModelFileIsAnotherInput(TestContext t)
        {
            var model = t.Path("Rock.obj");
            File.WriteAllText(model, "v 0 0 0\nv 1 0 0\nv 0 0 1\nf 1 2 3\n");
            var previous = CollisionService.MeshTriangleProvider;
            CollisionService.MeshTriangleProvider = path => string.Equals(path, model, StringComparison.OrdinalIgnoreCase) ? new float[] { 0, 0, 0, 0, 0, 1, 1, 0, 0 } : null;
            try
            {
                var scene = Level(new Vector3(0, 1, 0));
                var rock = new GameEntity("Rock");
                rock.Components.Add(new MeshCollider(rock) { MeshPath = model });
                scene.Entities.Add(rock);
                var file = Baked(scene);
                var entry = file.Inputs.Find(o => o.Triangles == 1);
                t.True(entry != null && entry.Signature != 0, "the rock's entry carries its model file");
                t.True(Stale(file, scene, out var why) == false, "the same file: current — " + why);
                File.WriteAllText(model, "# exported again\nv 0 0 0\nv 1 0 0\nv 0 0 1\nf 1 2 3\n");
                t.True(Stale(file, scene, out why) == true && why.Contains("'Rock'"), "another export of the model: stale — " + why);
            }
            finally { CollisionService.MeshTriangleProvider = previous; }
        }

        [Test]
        public static void ANavmeshFromBeforeTheRecordIsUnknown(TestContext t)
        {
            var scene = Level(new Vector3(0, 1, 0));
            var file = new NavMeshFileInfo { Version = 2, Settings = NavigationService.DefaultSettings() };
            t.True(NavigationService.IsStale(scene, file, out var why) == null && why.StartsWith("unknown", StringComparison.Ordinal), "unknown — " + why);
        }

        [Test]
        public static void TheToleranceIsAMillimetreAndAThousandthOfADegree(TestContext t)
        {
            NavGeometryOwner Owner(float x, float axis = 1f, float measure = 24f, ulong signature = 7)
            {
                var o = new NavGeometryOwner { Id = Guid.NewGuid(), Signature = signature };
                o.Pose[0] = axis; o.Pose[4] = 1f; o.Pose[8] = 1f; o.Pose[9] = x;
                o.Extent[0] = x - 0.5f; o.Extent[3] = x + 0.5f; o.Extent[6] = x; o.Extent[9] = measure;
                return o;
            }
            var a = Owner(10f);
            t.True(NavigationService.SameInput(a, Owner(MathF.BitIncrement(10f))), "a float step");
            t.True(NavigationService.SameInput(a, Owner(10.0009f)), "0.9 mm");
            t.False(NavigationService.SameInput(a, Owner(10.002f)), "2 mm");
            t.True(NavigationService.SameInput(Owner(5000f), Owner(5000f + 4 * (MathF.BitIncrement(5000f) - 5000f))), "four float steps 5 km out");
            t.True(NavigationService.SameInput(a, Owner(10f, axis: 1.000005f)), "a scale or rotation change of 5e-6");
            t.False(NavigationService.SameInput(a, Owner(10f, axis: 1.0001f)), "of 1e-4");
            t.True(NavigationService.SameInput(a, Owner(10f, measure: 24.001f)), "area plus volume within 0.01 %");
            t.False(NavigationService.SameInput(a, Owner(10f, measure: 24.1f)), "not within");
            t.False(NavigationService.SameInput(a, Owner(10f, signature: 8)), "the stored data is exact");
        }

        [Test]
        public static void TheFileIsReadWithoutLoadingIt(TestContext t)
        {
            var scene = Level(new Vector3(0, 1, 0));
            scene.FilePath = t.Path("Level.vscene");
            t.True(NavigationService.ReadFileInfo(scene) == null, "no .vnav: nothing");
            var data = NavigationService.StampInputs(Header(NavigationService.DefaultSettings(), new NavBakeStats { PolyCount = 7 }), Collect(scene));
            File.WriteAllBytes(t.Path("Level.vnav"), data);
            var info = NavigationService.ReadFileInfo(scene);
            t.True(info != null && info.Version == 2 && info.Stats.PolyCount == 7 && info.Size == data.Length && info.Path == t.Path("Level.vnav"), "the header of Level.vnav, next to Level.vscene");
            t.True(info.Inputs != null && info.Inputs.Count == 2, "and the record at its end");
            t.True((DateTime.UtcNow - info.BakedUtc).TotalMinutes < 5, "when it was written");
            t.True(NavigationService.IsStale(scene, info, out var why) == false, "it matches the scene — " + why);
            Named(scene, "Wall").Transform.LocalPosition = new Vector3(3, 1, 0);
            t.True(NavigationService.IsStale(scene, info, out why) == true && why.Contains("'Wall'"), "until the wall moves — " + why);
            File.WriteAllText(t.Path("Level.vnav"), "not a navmesh");
            t.Equal(-1, NavigationService.ReadFileInfo(scene).Version, "a file that is no navmesh is there, but unreadable");
        }
    }

    /// <summary>A real bake (#449): the navmesh with its record still loads and routes — runs with the native engine library only.</summary>
    public static class ZNavMeshNativeTests
    {
        [Test]
        public static void ABakedNavmeshCarriesItsInputsAndStillLoads(TestContext t)
        {
            try { Editor.Core.Native.NativeLoader.Register(); } catch { }
            if (!NavigationService.Available) { Console.WriteLine("    (skipped: no navigation in the engine library — " + NavigationService.UnavailableReason + ")"); return; }
            var scene = NavMeshInfoTests.Level(new Vector3(0, 1, 0));
            var geo = NavigationService.CollectGeometry(scene, (int)NavGeometrySource.Default);
            var r = NavigationService.Bake(geo, NavigationService.DefaultSettings());
            t.True(r.Success, "baked: " + r.Message);
            var info = NavigationService.ParseHeader(r.Data);
            t.True(info != null && info.Inputs != null && info.Inputs.Count == geo.Owners.Count, "the bake recorded what it read");
            for (int i = 0; i < geo.Owners.Count; i++)
                t.True(info.Inputs[i].Id == geo.Owners[i].Id && NavigationService.SameInput(info.Inputs[i], geo.Owners[i]), "entity " + geo.Owners[i].Name);
            t.True(NavigationService.IsStale(info, NavigationService.CollectGeometry(scene, info.Settings.SourceFlags), out var why) == false, "and it is current — " + why);
            t.True(info.Stats.PolyCount == r.Stats.PolyCount && info.Stats.TileCount == r.Stats.TileCount && info.Stats.InputBoxes == 2, "the header holds the bake's stats");
            var previous = NavigationService.LoadedScene;
            try
            {
                t.True(NavigationService.LoadData(r.Data, scene), "the engine loads a navmesh with the record behind it");
                var corners = new List<Vector3>();
                var status = NavigationService.CalculatePath(new Vector3(-5, 0, 0), new Vector3(5, 0, 0), corners);
                t.True(status == NavPathStatus.Complete && corners.Count >= 3, "and routes on it, around the wall: " + status + ", " + corners.Count + " corners");
            }
            finally { if (previous != null) NavigationService.LoadForScene(previous); else NavigationService.Unload(); }
        }
    }
}
