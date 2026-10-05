using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.Core.Services.Build;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using Editor.UI.Vui;

namespace VortexTests
{
    /// <summary>The engine core every game depends on (#161): scene serialization, the asset pak, UI layout, character
    /// collision. All headless — no native engine, no GPU.</summary>
    public static class EngineCoreTests
    {
        [Test]
        public static void SceneRoundTripKeepsHierarchyAndComponents(TestContext t)
        {
            var scene = new Scene { Name = "Corridor" };
            var lamp = new GameEntity(scene, "Lamp") { Tag = "Light" };
            lamp.Transform.LocalPosition = new Vector3(1, 3.5f, -2);
            lamp.Transform.LocalRotation = new Vector3(90, 0, 15);
            lamp.AddComponentDirect(new Light(lamp, LightType.Spot) { Intensity = 2.5f, Range = 12f, SpotAngle = 40f, ColorR = 1f, ColorG = 0.8f, ColorB = 0.6f });
            var bulb = new GameEntity(scene, "Bulb") { Parent = lamp };
            bulb.Transform.LocalScale = new Vector3(0.2f, 0.2f, 0.2f);
            bulb.AddComponentDirect(new MeshRenderer(bulb) { MeshPath = "Primitive:Sphere", MaterialPath = "Assets/Materials/Glow.vmat" });
            var script = new Script(bulb, "Assets/Scripts/Flicker.cs");
            script.SetFieldValue("Speed", "4.5");
            bulb.AddComponentDirect(script);
            lamp.Children.Add(bulb);
            var wall = new GameEntity(scene, "Wall") { PrefabPath = "Assets/Prefabs/Wall.ventity" };
            wall.AddComponentDirect(new BoxCollider(wall) { Size = new Vector3(4, 3, 0.2f) });
            scene.Entities.Add(lamp);
            scene.Entities.Add(wall);

            var back = DataSerializer.FromBinary<Scene>(DataSerializer.ToBinary(scene));
            t.NotNull(back, "scene deserializes");
            t.Equal("Corridor", back.Name, "scene name");
            t.Equal(2, back.Entities.Count, "root entities");
            var l = back.Entities[0];
            t.Equal("Lamp", l.Name, "entity name");
            t.Equal("Light", l.Tag, "tag");
            t.Equal(3.5f, l.Transform.LocalPosition.Y, "position");
            t.Equal(15f, l.Transform.LocalRotation.Z, "rotation");
            var light = l.GetComponent<Light>();
            t.NotNull(light, "light component");
            t.Equal(LightType.Spot, light.LightType, "light type");
            t.Equal(2.5f, light.Intensity, "intensity");
            t.Equal(0.8f, light.ColorG, "color");
            t.Equal(1, l.Children.Count, "child");
            var b = l.Children[0];
            t.Equal("Bulb", b.Name, "child name");
            t.Equal(0.2f, b.Transform.LocalScale.X, "child scale");
            t.Equal("Assets/Materials/Glow.vmat", b.GetComponent<MeshRenderer>().MaterialPath, "material path");
            t.Equal("4.5", b.GetComponent<Script>().GetFieldValue("Speed"), "script field override");
            t.Equal("Assets/Prefabs/Wall.ventity", back.Entities[1].PrefabPath, "prefab link");
            t.Equal(4f, back.Entities[1].GetComponent<BoxCollider>().Size.X, "collider size");
            t.True(back.Entities[0].Id == lamp.Id, "ids survive");
        }

        [Test]
        public static void PakRoundTripKeepsEveryByte(TestContext t)
        {
            var rnd = new Random(7);
            var blob = new byte[200_000];
            rnd.NextBytes(blob);
            var entries = new Dictionary<string, byte[]>
            {
                ["Assets/Scenes/Main.vscene"] = Encoding.UTF8.GetBytes("{\"scene\": \"main\"}"),
                ["Assets\\Textures\\noise.bin"] = blob,
                ["Assets/Audio/empty.wav"] = new byte[0],
                ["Assets/Texte/Größe_ü.txt"] = Encoding.UTF8.GetBytes("Umlaute ✓"),
            };
            string pak = t.Path("game.vpak");
            VortexPak.Write(pak, entries);
            var raw = File.ReadAllBytes(pak);
            t.True(raw.Length < blob.Length + 4096, "random data is not inflated much");
            t.False(Encoding.UTF8.GetString(raw).Contains("\"scene\""), "contents are not stored as plain text");
            var back = VortexPak.Read(pak);
            t.Equal(4, back.Count, "entries");
            t.True(back["Assets/Textures/noise.bin"].SequenceEqual(blob), "binary data intact (backslash path normalized)");
            t.Equal("{\"scene\": \"main\"}", Encoding.UTF8.GetString(back["assets/scenes/main.vscene"]), "case-insensitive lookup");
            t.Equal(0, back["Assets/Audio/empty.wav"].Length, "empty file");
            t.Equal("Umlaute ✓", Encoding.UTF8.GetString(back["Assets/Texte/Größe_ü.txt"]), "unicode path and text");
            var bad = t.Write("bad.vpak", "PK not a pak");
            bool threw = false;
            try { VortexPak.Read(bad); } catch (InvalidDataException) { threw = true; }
            t.True(threw, "a foreign file is rejected");
        }

        [Test]
        public static void VuiLayoutAnchorsStretchAndStacks(TestContext t)
        {
            var root = new VuiElement { Kind = VuiKind.Panel, Id = "root", StretchX = true, StretchY = true };
            var badge = new VuiElement { Kind = VuiKind.Panel, Id = "badge", Anchor = AnchorEnum.TopRight, OffX = -20, OffY = 20, W = 100, H = 40 };
            var center = new VuiElement { Kind = VuiKind.Panel, Id = "center", Anchor = AnchorEnum.Center, W = 400, H = 200 };
            var bar = new VuiElement { Kind = VuiKind.Panel, Id = "bar", Anchor = AnchorEnum.BottomLeft, StretchX = true, OffX = 10, W = -10, H = 50, OffY = -50 };
            var list = new VuiElement { Kind = VuiKind.Panel, Id = "list", Anchor = AnchorEnum.TopLeft, W = 300, H = 400, LayoutMode = StackDir.Vertical, Padding = 10, Spacing = 5 };
            list.Children.Add(new VuiElement { Kind = VuiKind.Button, Id = "a", H = 40 });
            list.Children.Add(new VuiElement { Kind = VuiKind.Button, Id = "b", H = 60 });
            root.Children.Add(badge);
            root.Children.Add(center);
            root.Children.Add(bar);
            root.Children.Add(list);
            var canvas = new VuiCanvas { Root = root, DesignW = 1920, DesignH = 1080 };
            canvas.Reindex();

            canvas.Layout(1920, 1080);
            t.True(canvas.TryGetRect("badge", out var r), "badge laid out");
            t.Equal(1920f - 20 - 100, r.X, "top-right anchor: x");
            t.Equal(20f, r.Y, "top-right anchor: y");
            canvas.TryGetRect("center", out r);
            t.Equal(760f, r.X, "centered x"); t.Equal(440f, r.Y, "centered y");
            canvas.TryGetRect("bar", out r);
            t.Equal(10f, r.X, "stretch left margin"); t.Equal(1920f - 20, r.W, "stretch width = parent - margins");
            canvas.TryGetRect("a", out var ra); canvas.TryGetRect("b", out var rb);
            t.Equal(10f, ra.Y, "stack padding"); t.Equal(280f, ra.W, "stack child width = inner width");
            t.Equal(ra.Y + 40 + 5, rb.Y, "stack spacing");

            // half resolution: everything scales with the design size
            canvas.Layout(960, 540);
            canvas.TryGetRect("badge", out r);
            t.Equal(960f - 10 - 50, r.X, "scaled anchor offset");
            t.Equal(50f, r.W, "scaled size");
            canvas.TryGetRect("center", out r);
            t.Equal(380f, r.X, "scaled center");
        }

        [Test]
        public static void CharacterCollisionSlidesAndDepenetrates(TestContext t)
        {
            var scene = new Scene { Name = "Box" };
            var floor = new GameEntity(scene, "Floor");
            floor.Transform.LocalPosition = new Vector3(0, -0.5f, 0);
            floor.Transform.LocalScale = new Vector3(20, 1, 20);
            floor.AddComponentDirect(new BoxCollider(floor));
            var wall = new GameEntity(scene, "Wall");
            wall.Transform.LocalPosition = new Vector3(2.5f, 1.5f, 0);
            wall.Transform.LocalScale = new Vector3(1, 3, 10);
            wall.AddComponentDirect(new BoxCollider(wall));
            scene.Entities.Add(floor);
            scene.Entities.Add(wall);
            try
            {
                CollisionService.Build(scene);
                const float radius = 0.4f, height = 1.8f;
                // walk into the wall: stop at its face (x = 2), never inside
                var p = CollisionService.MoveCharacter(new Vector3(0, 0, 0), radius, height, new Vector3(5, 0, 0), out bool grounded);
                t.True(p.X <= 2f - radius + 0.02f, "stopped at the wall (x = " + p.X + ")");
                t.True(p.X > 2f - radius - 0.3f, "close to the wall, not pushed back (x = " + p.X + ")");
                t.True(grounded, "grounded on the floor");
                t.True(Math.Abs(p.Y) < 0.05f, "stays on the floor (y = " + p.Y + ")");
                // slide along it: the Z part of a diagonal move survives
                p = CollisionService.MoveCharacter(new Vector3(1.5f, 0, 0), radius, height, new Vector3(2, 0, 2), out _);
                t.True(p.Z > 1.8f, "slides along the wall (z = " + p.Z + ")");
                // overlapping the wall face by 15 cm (spawned too close): pushed out to just touching
                p = CollisionService.MoveCharacter(new Vector3(2f - radius + 0.15f, 0, 0), radius, height, new Vector3(0, 0, 0), out _);
                t.True(p.X <= 2f - radius + 0.02f, "depenetrated out of the wall (x = " + p.X + ")");
                // falling: lands on the floor, not through it
                p = CollisionService.MoveCharacter(new Vector3(-3, 2, 0), radius, height, new Vector3(0, -10, 0), out grounded);
                t.True(Math.Abs(p.Y) < 0.05f && grounded, "lands on the floor (y = " + p.Y + ")");
            }
            finally { CollisionService.Clear(); }
        }

        [Test]
        public static async Task TemplatePacksDownloadOnceAndCache(TestContext t)
        {
            // an installed template with Git LFS pointers instead of its models
            var installed = t.Path("install", "HorrorStarter");
            Directory.CreateDirectory(Path.Combine(installed, "Assets", "Models"));
            File.WriteAllText(Path.Combine(installed, "project.vortex"), "{}");
            File.WriteAllText(Path.Combine(installed, "Assets", "Models", "crate.glb"),
                "version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 123456\n");
            t.True(TemplatePacks.HasLfsPointers(installed), "pointer files detected");
            var real = t.Path("real");
            Directory.CreateDirectory(real);
            File.WriteAllBytes(Path.Combine(real, "crate.glb"), new byte[] { 0x67, 0x6C, 0x54, 0x46, 2, 0, 0, 0 });
            t.False(TemplatePacks.HasLfsPointers(real), "real content is not a pointer");

            // the release asset: a zip with the real project
            var zipPath = t.Path("Template-HorrorStarter.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("project.vortex").Open())) w.Write("{\"name\":\"Horror Starter\"}");
                using (var s2 = zip.CreateEntry("Assets/Models/crate.glb").Open()) s2.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
            }
            byte[] zipBytes = File.ReadAllBytes(zipPath);
            int downloads = 0;
            var fake = new FakeHttp();
            fake.Routes.Add(("https://api.github.com/", r => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"assets\":[{\"name\":\"Template-HorrorStarter.zip\",\"size\":" + zipBytes.Length + ",\"browser_download_url\":\"https://example.test/Template-HorrorStarter.zip\"}]}"),
            }));
            fake.Routes.Add(("https://example.test/", r => { downloads++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) }; }));
            TemplatePacks.Handler = fake;
            try
            {
                var template = new ProjectTemplate { Id = "HorrorStarter", Name = "Horror Starter", ProjectDir = installed, NeedsDownload = true };
                var pack = await TemplatePacks.FindAsync("HorrorStarter");
                t.NotNull(pack, "pack found on the release");
                t.Equal((long)zipBytes.Length, pack.Size, "pack size");
                double last = 0;
                string dir = await TemplatePacks.EnsureAsync(template, new SyncProgress(v => last = v), CancellationToken.None);
                t.True(File.Exists(Path.Combine(dir, "project.vortex")), "project unpacked");
                t.Equal(4L, new FileInfo(Path.Combine(dir, "Assets", "Models", "crate.glb")).Length, "real model bytes");
                t.Equal(1.0, last, "progress reached 100 %");
                t.True(dir.StartsWith(TemplatePacks.CacheRoot), "cached under the engine version");
                string again = await TemplatePacks.EnsureAsync(template, null, CancellationToken.None);
                t.Equal(dir, again, "second project uses the cache");
                t.Equal(1, downloads, "downloaded once");
                t.Equal(dir, TemplatePacks.CachedProjectDir("HorrorStarter"), "discovery sees the cache");

                // a pack with an entry outside its folder is refused
                var evilZip = t.Path("Template-Evil.zip");
                using (var zip = ZipFile.Open(evilZip, ZipArchiveMode.Create))
                using (var w = new StreamWriter(zip.CreateEntry("../../escape.txt").Open())) w.Write("x");
                byte[] evilBytes = File.ReadAllBytes(evilZip);
                fake.Routes.Clear();
                fake.Routes.Add(("https://api.github.com/", r => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"assets\":[{\"name\":\"Template-Evil.zip\",\"size\":1,\"browser_download_url\":\"https://example.test/evil.zip\"}]}"),
                }));
                fake.Routes.Add(("https://example.test/", r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(evilBytes) }));
                var evil = new ProjectTemplate { Id = "Evil", Name = "Evil", ProjectDir = installed, NeedsDownload = true };
                bool refused = false;
                try { await TemplatePacks.EnsureAsync(evil, null, CancellationToken.None); } catch (InvalidDataException) { refused = true; }
                t.True(refused, "zip slip refused");
                t.False(File.Exists(Path.Combine(Path.GetDirectoryName(TemplatePacks.CacheRoot), "escape.txt")), "nothing written outside");
            }
            finally { TemplatePacks.Handler = null; }
        }

        private sealed class SyncProgress : IProgress<double>
        {
            private readonly Action<double> _a;
            public SyncProgress(Action<double> a) { _a = a; }
            public void Report(double value) => _a(value);
        }
    }
}
