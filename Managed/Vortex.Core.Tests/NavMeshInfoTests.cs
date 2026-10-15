using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Editor.Core.Data;
using Editor.Core.Services.AI;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Physics;

namespace VortexTests
{
    /// <summary>What navmesh_info reports (#449): a bake stamps a fingerprint of the geometry it read into the .vnav header's
    /// reserved words, so a later check can tell a stale navmesh (walls moved, props added) from a current one; the header
    /// also gives the settings and stats without loading anything.</summary>
    public static class NavMeshInfoTests
    {
        /// <summary>A 20 m floor with a 12 m wall across it, as box colliders.</summary>
        internal static Scene Level(Vector3 wallAt)
        {
            var scene = new Scene { Name = "Level" };
            void Box(string name, Vector3 pos, Vector3 size)
            {
                var e = new GameEntity(name);
                e.Transform.LocalPosition = pos;
                e.Components.Add(new BoxCollider(e) { Size = size });
                scene.Entities.Add(e);
            }
            Box("Floor", new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20));
            Box("Wall", wallAt, new Vector3(1, 2, 12));
            return scene;
        }

        private static ulong Print(Scene s) => NavigationService.GeometryFingerprint(NavigationService.CollectGeometry(s, (int)NavGeometrySource.Default));

        private static byte[] Header(NavBakeSettings s, NavBakeStats st)
        {
            var data = new byte[NavigationService.HeaderSize + 64];
            BitConverter.GetBytes(0x56414E56u).CopyTo(data, 0);   // "VNAV"
            BitConverter.GetBytes(1).CopyTo(data, 4);
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(s, h.AddrOfPinnedObject() + 8, false);
                Marshal.StructureToPtr(st, h.AddrOfPinnedObject() + 8 + Marshal.SizeOf<NavBakeSettings>(), false);
            }
            finally { h.Free(); }
            return data;
        }

        [Test]
        public static void TheFingerprintFollowsTheLevel(TestContext t)
        {
            ulong a = Print(Level(new Vector3(0, 1, 0)));
            t.Equal(a, Print(Level(new Vector3(0, 1, 0))), "the same level, the same fingerprint");
            t.Equal(a, Print(Level(new Vector3(0.0001f, 1, 0))), "a tenth of a millimetre is float noise, not a change");
            t.True(a != Print(Level(new Vector3(0.05f, 1, 0))), "a wall moved by 5 cm is a change");
            var more = Level(new Vector3(0, 1, 0));
            var crate = new GameEntity("Crate");
            crate.Transform.LocalPosition = new Vector3(5, 0.5f, 5);
            crate.Components.Add(new BoxCollider(crate));
            more.Entities.Add(crate);
            t.True(a != Print(more), "a new prop is a change");
        }

        [Test]
        public static void TheHeaderSaysHowItWasBakedAndFromWhat(TestContext t)
        {
            t.Equal(92, Marshal.SizeOf<NavBakeSettings>(), "the settings block of NavigationFormat.h");
            t.Equal(64, Marshal.SizeOf<NavBakeStats>(), "the stats block of NavigationFormat.h");
            var s = NavigationService.DefaultSettings();
            s.AgentRadius = 0.3f;
            s.SourceFlags = (int)NavGeometrySource.Colliders;
            var st = new NavBakeStats { PolyCount = 42, TileCount = 3, TilesX = 2, TilesZ = 2, InputBoxes = 2, BoundsMinX = -10, BoundsMaxX = 10 };
            var data = Header(s, st);

            var legacy = NavigationService.ParseHeader(data);
            t.True(legacy != null && legacy.InputFingerprint == null, "a navmesh baked before the stamp has no fingerprint");
            t.True(Math.Abs(legacy.Settings.AgentRadius - 0.3f) < 1e-6f && legacy.Settings.SourceFlags == (int)NavGeometrySource.Colliders, "the settings it was baked with");
            t.True(legacy.Stats.PolyCount == 42 && legacy.Stats.TileCount == 3 && legacy.Stats.InputBoxes == 2 && legacy.Stats.BoundsMaxX == 10f, "and its stats");

            NavigationService.StampFingerprint(data, 0x123456789ABCDEF0UL);
            var stamped = NavigationService.ParseHeader(data);
            t.Equal(0x123456789ABCDEF0UL, stamped.InputFingerprint ?? 0, "the stamped fingerprint");
            t.True(Math.Abs(stamped.Settings.AgentRadius - 0.3f) < 1e-6f && stamped.Stats.PolyCount == 42, "stamping leaves settings and stats alone");
            t.True(NavigationService.ParseHeader(new byte[400]) == null && NavigationService.ParseHeader(new byte[10]) == null, "no navmesh, no header");
        }

        [Test]
        public static void AMovedWallMakesTheNavmeshStale(TestContext t)
        {
            var scene = Level(new Vector3(0, 1, 0));
            var file = new NavMeshFileInfo { Version = 1, Stats = new NavBakeStats { InputBoxes = 2 }, InputFingerprint = Print(scene) };
            t.True(NavigationService.IsStale(scene, file, out var why) == false && why == null, "just baked: current");
            scene.Entities[1].Transform.LocalPosition = new Vector3(3, 1, 0);
            t.True(NavigationService.IsStale(scene, file, out why) == true && why.Contains("changed"), "the wall moved: stale — " + why);
            file.InputFingerprint = null;
            t.True(NavigationService.IsStale(scene, file, out why) == null && why.StartsWith("unknown", StringComparison.Ordinal), "baked before the stamp: unknown — " + why);
        }

        [Test]
        public static void TheFileIsReadWithoutLoadingIt(TestContext t)
        {
            var scene = Level(new Vector3(0, 1, 0));
            scene.FilePath = t.Path("Level.vscene");
            t.True(NavigationService.ReadFileInfo(scene) == null, "no .vnav: nothing");
            var data = Header(NavigationService.DefaultSettings(), new NavBakeStats { PolyCount = 7 });
            NavigationService.StampFingerprint(data, Print(scene));
            File.WriteAllBytes(t.Path("Level.vnav"), data);
            var info = NavigationService.ReadFileInfo(scene);
            t.True(info != null && info.Version == 1 && info.Stats.PolyCount == 7 && info.Size == data.Length && info.Path == t.Path("Level.vnav"), "the header of Level.vnav, next to Level.vscene");
            t.True((DateTime.UtcNow - info.BakedUtc).TotalMinutes < 5, "when it was written");
            t.True(NavigationService.IsStale(scene, info, out _) == false, "and it matches the scene");
            File.WriteAllText(t.Path("Level.vnav"), "not a navmesh");
            t.Equal(-1, NavigationService.ReadFileInfo(scene).Version, "a file that is no navmesh is there, but unreadable");
        }
    }

    /// <summary>A real bake (#449): the stamped navmesh still loads and routes — runs with the native engine library only.</summary>
    public static class ZNavMeshNativeTests
    {
        [Test]
        public static void ABakedNavmeshCarriesItsFingerprintAndStillLoads(TestContext t)
        {
            try { Editor.Core.Native.NativeLoader.Register(); } catch { }
            if (!NavigationService.Available) { Console.WriteLine("    (skipped: no navigation in the engine library — " + NavigationService.UnavailableReason + ")"); return; }
            var scene = NavMeshInfoTests.Level(new Vector3(0, 1, 0));
            var geo = NavigationService.CollectGeometry(scene, (int)NavGeometrySource.Default);
            var r = NavigationService.Bake(geo, NavigationService.DefaultSettings());
            t.True(r.Success, "baked: " + r.Message);
            var info = NavigationService.ParseHeader(r.Data);
            t.True(info != null && info.InputFingerprint == NavigationService.GeometryFingerprint(geo), "the bake stamped the fingerprint of what it read");
            t.True(info.Stats.PolyCount == r.Stats.PolyCount && info.Stats.TileCount == r.Stats.TileCount && info.Stats.InputBoxes == 2, "the header holds the bake's stats");
            var previous = NavigationService.LoadedScene;
            try
            {
                t.True(NavigationService.LoadData(r.Data, scene), "the engine loads a stamped navmesh");
                var corners = new List<Vector3>();
                var status = NavigationService.CalculatePath(new Vector3(-5, 0, 0), new Vector3(5, 0, 0), corners);
                t.True(status == NavPathStatus.Complete && corners.Count >= 3, "and routes on it, around the wall: " + status + ", " + corners.Count + " corners");
            }
            finally { if (previous != null) NavigationService.LoadForScene(previous); else NavigationService.Unload(); }
        }
    }
}
