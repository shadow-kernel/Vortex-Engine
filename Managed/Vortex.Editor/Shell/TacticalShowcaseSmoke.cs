using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.AI;
using Editor.Core.Services.Foliage;
using Editor.Core.Services.Terrain;
using Editor.Core.Terrain;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;

namespace VortexEditor.Shell
{
    /// <summary>
    /// A template-authoring helper, not a check: with <c>VORTEX_TEMPLATE_SETUP=1</c> it dresses the open Range with the
    /// engine's FEATURE TOUR — every milestone as a station you can see, shoot and edit (the user's request: "I want to
    /// see all of it in the template and play with it"):
    ///   v3.1 Physics v2 — a hinged door, a rope of distance joints with a ball, a slider gate, a ball pit of bouncy balls
    ///   and ice cubes on a ramp (physics materials), a kinematic moving platform (MovingPlatform.cs);
    ///   v3.2 AI &amp; Navigation — the combat bots already live here (tactical bots setup); the Showcase entity's keys spawn
    ///   more and toggle the navmesh / perception overlays (ShowcaseKeys.cs);
    ///   v3.3 VFX — a burning barrel, a sparking fuse box, dust motes, authored blood and bullet-hole decals on a wall, a
    ///   shadowed lamp over the lab, and the scene settings with volumetric fog on.
    /// Idempotent: entities named "Showcase …" are replaced. Rebakes the navmesh and saves the scene. Run against a copy of
    /// Templates/TacticalShooter with <c>--scene=Range</c>, then copy Range.vscene + Range.vnav back.
    /// </summary>
    internal static class TacticalShowcaseSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("tactical showcase setup", Run);

        private static Scene _scene;
        private static int _made;

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            if (Environment.GetEnvironmentVariable("VORTEX_TEMPLATE_SETUP") != "1") { log.Log("tactical showcase setup: authoring helper — skipped (set VORTEX_TEMPLATE_SETUP=1)"); return true; }
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.LogError("tactical showcase setup: no scene"); return false; }
            if (!NavigationService.Available) { log.LogError("tactical showcase setup: no Recast in this build"); return false; }
            _scene = scene; _made = 0;
            var player = TemplateSetupSmoke.Find(scene, e => e.Name == "Player" || e.Tag == "Player");
            if (player == null) { log.LogError("tactical showcase setup: no Player entity"); return false; }
            var start = TransformMath.WorldPosition(player);

            // a previous tour goes first (idempotent authoring)
            var old = new List<GameEntity>();
            foreach (var e in scene.Entities) if (e.Name == "Showcase" || e.Name.StartsWith("Showcase ", StringComparison.Ordinal)) old.Add(e);
            foreach (var e in old) TemplateSetupSmoke.Remove(scene, e.Name);
            if (old.Count > 0) log.Log("tactical showcase setup: replaced " + old.Count + " entities of the previous tour");

            // the navmesh tells us where the floor is open enough for a station
            var bake = NavigationService.BakeScene(scene, NavigationService.SettingsFor(scene), save: false, load: true);
            if (!bake.Success) { log.LogError("tactical showcase setup: bake failed — " + bake.Message); return false; }
            await SmokeRegistry.Settle(200);
            var rng = new Random(23);
            if (!OpenSpot(start, 9f, 18f, 4.5f, rng, new List<Vector3>(), out var lab)) { log.LogError("tactical showcase setup: no open spot for the physics lab"); return false; }
            if (!OpenSpot(start, 7f, 16f, 3.5f, rng, new List<Vector3> { lab }, out var vfx)) { log.LogError("tactical showcase setup: no open spot for the VFX corner"); return false; }
            log.Log("tactical showcase setup: physics lab at " + TemplateSetupSmoke.F(lab) + ", VFX corner at " + TemplateSetupSmoke.F(vfx) + " (player at " + TemplateSetupSmoke.F(start) + ")");

            // ---- the keys ----
            var root = scene.CreateEntity("Showcase");
            root.AddComponentDirect(new Script(root, "Assets/Scripts/Showcase/ShowcaseKeys.cs"));
            _made++;

            // ---- v3.1 Physics v2: the lab ----
            float y = lab.Y;
            var frame = Box("Showcase Door Frame", lab + new Vector3(0f, 1.1f, 0f), new Vector3(0.15f, 2.2f, 0.15f), 0.35f, 0.3f, 0.28f);
            var door = Box("Showcase Hinge Door", lab + new Vector3(0.6f, 1.1f, 0f), new Vector3(1.0f, 2.1f, 0.08f), 0.55f, 0.38f, 0.22f);
            door.AddComponentDirect(new Rigidbody(door) { BodyType = RigidbodyType.Dynamic, Mass = 25f });
            door.AddComponentDirect(new HingeJoint(door) { ConnectedEntity = frame.Name, Anchor = new Vector3(-0.5f, 0f, 0f), Axis = new Vector3(0f, 1f, 0f), UseLimits = true, MinAngle = -100f, MaxAngle = 100f });

            var anchor = Box("Showcase Rope Anchor", lab + new Vector3(3f, 3.3f, 0f), new Vector3(0.25f, 0.25f, 0.25f), 0.3f, 0.3f, 0.3f);
            string prev = anchor.Name;
            for (int i = 0; i < 6; i++)
            {
                var link = Box("Showcase Rope Link " + (i + 1), lab + new Vector3(3f, 3.3f - 0.36f * (i + 1), 0f), new Vector3(0.12f, 0.12f, 0.12f), 0.75f, 0.7f, 0.6f);
                link.AddComponentDirect(new Rigidbody(link) { BodyType = RigidbodyType.Dynamic, Mass = 0.8f });
                link.AddComponentDirect(new DistanceJoint(link) { ConnectedEntity = prev, MinDistance = 0.2f, MaxDistance = 0.36f });
                prev = link.Name;
            }
            var ball = Ball("Showcase Rope Ball", lab + new Vector3(3f, 3.3f - 0.36f * 7f - 0.1f, 0f), 0.4f, 0.8f, 0.15f, 0.1f);
            ball.AddComponentDirect(new Rigidbody(ball) { BodyType = RigidbodyType.Dynamic, Mass = 5f });
            ball.AddComponentDirect(new DistanceJoint(ball) { ConnectedEntity = prev, MinDistance = 0.2f, MaxDistance = 0.4f });

            var rail = Box("Showcase Slider Rail", lab + new Vector3(-3f, 1.05f, 0f), new Vector3(3.2f, 0.1f, 0.1f), 0.3f, 0.3f, 0.3f);
            var gate = Box("Showcase Slider Gate", lab + new Vector3(-3f, 0.55f, 0f), new Vector3(0.6f, 1.0f, 0.12f), 0.2f, 0.45f, 0.8f);
            gate.AddComponentDirect(new Rigidbody(gate) { BodyType = RigidbodyType.Dynamic, Mass = 8f, UseGravity = false });
            gate.AddComponentDirect(new SliderJoint(gate) { ConnectedEntity = rail.Name, Axis = new Vector3(1f, 0f, 0f), UseLimits = true, MinPosition = -1.3f, MaxPosition = 1.3f });

            var pit = lab + new Vector3(0f, 0f, 3.5f);
            Box("Showcase Pit Wall N", pit + new Vector3(0f, 0.25f, 1.5f), new Vector3(3.2f, 0.5f, 0.1f), 0.4f, 0.4f, 0.42f);
            Box("Showcase Pit Wall S", pit + new Vector3(0f, 0.25f, -1.5f), new Vector3(3.2f, 0.5f, 0.1f), 0.4f, 0.4f, 0.42f);
            Box("Showcase Pit Wall E", pit + new Vector3(1.5f, 0.25f, 0f), new Vector3(0.1f, 0.5f, 3.2f), 0.4f, 0.4f, 0.42f);
            Box("Showcase Pit Wall W", pit + new Vector3(-1.5f, 0.25f, 0f), new Vector3(0.1f, 0.5f, 3.2f), 0.4f, 0.4f, 0.42f);
            for (int i = 0; i < 10; i++)
            {
                var b = Ball("Showcase Bouncy Ball " + (i + 1), pit + new Vector3(-1f + (i % 5) * 0.5f, 1.2f + (i / 5) * 1.4f, -0.6f + (i / 5) * 1.2f), 0.3f, 1f, 0.55f, 0.1f);
                b.AddComponentDirect(new Rigidbody(b) { BodyType = RigidbodyType.Dynamic, Mass = 0.4f });
                b.GetComponent<SphereCollider>().Material = new PhysicsMaterial { Bounciness = 0.85f, Friction = 0.3f };
            }
            var ramp = Box("Showcase Ramp", pit + new Vector3(-3.6f, 0.7f, 0f), new Vector3(2.6f, 0.1f, 3.0f), 0.5f, 0.52f, 0.55f);
            ramp.Transform.LocalRotation = new Vector3(0f, 0f, -14f);
            for (int i = 0; i < 5; i++)
            {
                var c = Box("Showcase Ice Cube " + (i + 1), pit + new Vector3(-4.3f + i * 0.35f, 1.6f, -0.9f + i * 0.45f), new Vector3(0.3f, 0.3f, 0.3f), 0.7f, 0.9f, 1f);
                c.AddComponentDirect(new Rigidbody(c) { BodyType = RigidbodyType.Dynamic, Mass = 1f });
                c.GetComponent<BoxCollider>().Material = new PhysicsMaterial { Friction = 0.02f, Bounciness = 0.1f };
            }

            var platform = Box("Showcase Moving Platform", lab + new Vector3(0f, 0.15f, -3.5f), new Vector3(2f, 0.25f, 2f), 0.15f, 0.65f, 0.6f);
            platform.AddComponentDirect(new Rigidbody(platform) { BodyType = RigidbodyType.Kinematic, Mass = 50f });
            platform.AddComponentDirect(new Script(platform, "Assets/Scripts/Showcase/MovingPlatform.cs"));

            // ---- v3.3 VFX: the corner ----
            var barrel = Primitive("Showcase Fire Barrel", PrimitiveType.Cylinder, vfx + new Vector3(0f, 0.45f, 0f), new Vector3(0.6f, 0.9f, 0.6f), 0.35f, 0.18f, 0.12f);
            barrel.AddComponentDirect(new BoxCollider(barrel));
            var fire = Fx("Showcase Fire", vfx + new Vector3(0f, 0.95f, 0f), "Assets/VFX/Fire_Barrel.vfx");
            var fuse = Box("Showcase Fuse Box", vfx + new Vector3(2.5f, 1.3f, 0f), new Vector3(0.5f, 0.7f, 0.2f), 0.45f, 0.47f, 0.5f);
            Fx("Showcase Sparks", vfx + new Vector3(2.5f, 1.0f, 0.15f), "Assets/VFX/Sparks_Electric.vfx");
            Fx("Showcase Dust", vfx + new Vector3(1f, 1.5f, 1.5f), "Assets/VFX/Dust_Motes.vfx");
            var wall = Box("Showcase Decal Wall", vfx + new Vector3(-2.5f, 1.2f, 0.6f), new Vector3(3f, 2.4f, 0.15f), 0.72f, 0.7f, 0.66f);
            DecalOn("Showcase Decal Blood", vfx + new Vector3(-2.2f, 1.25f, 0.52f), new Vector3(1.3f, 0.3f, 1.3f), "Assets/Materials/Decals/Blood_Splat.vmat", 1, 25f);
            DecalOn("Showcase Decal Hole 1", vfx + new Vector3(-3.3f, 1.6f, 0.52f), new Vector3(0.14f, 0.3f, 0.14f), "Assets/Materials/Decals/BulletHole_Concrete.vmat", 0, 0f);
            DecalOn("Showcase Decal Hole 2", vfx + new Vector3(-3.1f, 0.8f, 0.52f), new Vector3(0.12f, 0.3f, 0.12f), "Assets/Materials/Decals/BulletHole_Concrete.vmat", 0, 70f);
            var lamp = scene.CreateEntity("Showcase Shaft Lamp");
            lamp.Transform.LocalPosition = lab + new Vector3(0f, 3.8f, 1.5f);
            lamp.Transform.LocalRotation = new Vector3(90f, 0f, 0f);   // forward (+Z) turned down
            lamp.AddComponentDirect(new Light(lamp, LightType.Spot) { Range = 12f, Intensity = 40f, SpotAngle = 55f, InnerSpotAngle = 35f, ColorR = 1f, ColorG = 0.93f, ColorB = 0.8f, ShadowType = ShadowType.Soft });
            _made++;

            // ---- v3.4 World: the terrain plot — the largest square of open ground nothing is built on, hills on it,
            //      the rim flush with the range floor ----
            Vector3 plot; float plotHalf;
            if (FindPlot(scene, start, new List<Vector3> { lab, vfx }, rng, out plot, out plotHalf))
            {
                float size = plotHalf * 2f;
                float k = plotHalf / 16f;   // the hills scale with the plot (designed for 32 m)
                var ter = scene.CreateEntity("Showcase Terrain");
                ter.Transform.LocalPosition = new Vector3(plot.X - size * 0.5f, plot.Y + 0.02f, plot.Z - size * 0.5f);
                var tc = new Editor.ECS.Components.Rendering.Terrain(ter)
                {
                    Size = size, Resolution = size >= 24f ? 65 : 33, LodDistance = 40f, Collision = true,
                    Layer0Material = "Assets/Materials/Gen/brown_mud_dry_2x10.vmat", Layer0Tile = 3f,
                    Layer1Material = "Assets/Materials/Gen/coast_sand_01_45x50_t1.15.vmat", Layer1Tile = 4f,
                    Layer2Material = "Assets/Materials/Gen/concrete_floor_02_1.7x0.5.vmat", Layer2Tile = 2f,
                    Layer3Tile = 2f,   // layer 3 stays the flat rock grey
                    DataPath = "Assets/Terrain/Showcase_Terrain.vterrain"
                };
                ter.AddComponentDirect(tc);
                TerrainData data; float cell;
                if (TerrainService.TryGetData(ter, out data, out cell))
                {
                    float c = (data.Resolution - 1) * 0.5f, spm = k / cell;   // the centre sample, (scaled) samples per metre
                    float hk = Math.Min(1f, k);
                    data.Raise(c - 5f * spm, c - 3f * spm, 7f * spm, 2.8f * hk, 0.2f);
                    data.Raise(c + 6f * spm, c + 2f * spm, 5.5f * spm, 1.9f * hk, 0.3f);
                    data.Raise(c + 1f * spm, c + 7f * spm, 4f * spm, 1.2f * hk, 0.4f);
                    data.Raise(c - 2f * spm, c + 1f * spm, 4f * spm, -0.9f * hk, 0.3f);   // a dip to shoot from
                    for (int i = 0; i < 3; i++) data.Smooth(c, c, 11f * spm, 0.6f, 0f);
                    data.Paint(c - 5f * spm, c - 3f * spm, 3.5f * spm, 1, 1f, 0.3f);   // sand on the tops
                    data.Paint(c + 6f * spm, c + 2f * spm, 3f * spm, 1, 1f, 0.3f);
                    int res = data.Resolution;
                    for (int z = 0; z < res; z++)
                        for (int x = 0; x < res; x++)
                        {
                            int o = (z * res + x) * 4;
                            float rim = Math.Max(Math.Abs(x - c), Math.Abs(z - c)) / c;
                            if (rim > 0.82f) { data.Splat[o] = 0; data.Splat[o + 1] = 0; data.Splat[o + 2] = 255; data.Splat[o + 3] = 0; }   // concrete apron
                            else if (data.Normal(x, z, cell).Y < 0.86f) { data.Splat[o] = 40; data.Splat[o + 1] = 0; data.Splat[o + 2] = 0; data.Splat[o + 3] = 215; }   // rock on the steep slopes
                        }
                    TerrainService.MarkDirty(ter, new SampleRect { X0 = 0, Z0 = 0, X1 = res - 1, Z1 = res - 1 }, true, true);
                    TerrainService.Save(ter);
                }
                _made++;
                log.Log("tactical showcase setup: terrain plot at " + TemplateSetupSmoke.F(plot) + " (" + size + " m, " + tc.Resolution + " samples, " + (tc.DataPath ?? "") + ")");

                // ---- v3.4 World: foliage on the plot (#125) — shrubs and dry branches painted over the hills (they sway),
                //      boulders that block the player and the navmesh ----
                var fol = scene.CreateEntity("Showcase Foliage");
                var fc = new Editor.ECS.Components.Rendering.Foliage(fol) { Wind = 1f, DataPath = "Assets/Foliage/Showcase_Foliage.vfoliage" };
                var shrub = Editor.ECS.Components.Rendering.Foliage.DefaultType("Shrub", "Assets/Models/Props/shrub_02/shrub_02.gltf", true);
                shrub.Density = 0.35f; shrub.MinSpacing = 1.1f; shrub.MinScale = 0.8f; shrub.MaxScale = 1.4f; shrub.CullDistance = 120f; shrub.ThinDistance = 45f; shrub.WindStrength = 0.12f; shrub.WindHeight = 1.2f; shrub.MaxSlope = 50f; shrub.Sink = 0.04f;
                var branches = Editor.ECS.Components.Rendering.Foliage.DefaultType("Dry branches", "Assets/Models/Props/dry_branches_medium_01/dry_branches_medium_01.gltf", true);
                branches.Density = 0.08f; branches.MinSpacing = 2f; branches.MinScale = 0.7f; branches.MaxScale = 1.2f; branches.CullDistance = 90f; branches.ThinDistance = 40f; branches.WindStrength = 0.03f; branches.WindHeight = 0.6f; branches.AlignToNormal = true; branches.Sink = 0.02f;
                var boulder = Editor.ECS.Components.Rendering.Foliage.DefaultType("Boulder", "Assets/Models/Props/namaqualand_boulder_02/namaqualand_boulder_02.gltf", false);
                boulder.Density = 0.012f; boulder.MinSpacing = 4f; boulder.MinScale = 0.6f; boulder.MaxScale = 1.3f; boulder.CullDistance = 200f; boulder.Collision = 1; boulder.CollisionRadius = 0.7f; boulder.CollisionHeight = 1.2f; boulder.WindStrength = 0f; boulder.Cutout = false; boulder.MaxTilt = 12f; boulder.MaxSlope = 60f; boulder.Sink = 0.15f;
                fc.Types.Add(shrub); fc.Types.Add(branches); fc.Types.Add(boulder);
                fol.AddComponentDirect(fc);
                int grown = 0;
                var plotCentre = new System.Numerics.Vector3(plot.X, 0f, plot.Z);
                grown += FoliageService.Paint(fol, 0, plotCentre, plotHalf * 0.92f, 1f, 101);
                grown += FoliageService.Paint(fol, 1, plotCentre, plotHalf * 0.9f, 1f, 202);
                grown += FoliageService.Paint(fol, 2, plotCentre, plotHalf * 0.85f, 1f, 303);
                FoliageService.Save(fol);
                _made++;
                log.Log("tactical showcase setup: " + grown + " foliage instances on the plot (shrubs, dry branches, boulders)");
            }
            else log.LogWarning("tactical showcase setup: no open ground for the terrain plot — skipped");

            // ---- scene settings: the fog the lamp and the sun can be seen in ----
            var st = scene.Settings;
            st.VolumetricEnabled = true; st.VolumetricDensity = 0.025f; st.VolumetricAnisotropy = 0.55f; st.VolumetricDistance = 60f;
            st.VolumetricNoise = 0.45f; st.VolumetricNoiseScale = 7f; st.VolumetricNoiseSpeed = 0.3f;
            st.VolumetricSun = 0.3f; st.VolumetricLights = 1.4f; st.VolumetricAmbient = 0.15f; st.VolumetricSteps = 24; st.VolumetricShadows = true;
            st.Apply();

            var rebake = NavigationService.BakeScene(scene, NavigationService.SettingsFor(scene), save: true, load: true);
            if (!rebake.Success) { log.LogError("tactical showcase setup: rebake failed — " + rebake.Message); return false; }
            EditorCommands.SaveScene(scene);
            log.Log("tactical showcase setup: " + _made + " entities placed, navmesh " + rebake.Stats.PolyCount + " polygons, scene saved — copy Range.vscene and Range.vnav back into the template");
            return true;
        }

        /// <summary>The biggest square (7 .. 16 m half size) of reachable, level floor within 8 .. 60 m of the start that no
        /// built structure stands on (every mesh's world box, except the ground-sized ones) and that keeps 6 m from the
        /// other stations — the terrain plot. The best of 600 navmesh samples.</summary>
        private static bool FindPlot(Scene scene, Vector3 start, List<Vector3> away, Random rng, out Vector3 plot, out float half)
        {
            plot = start; half = 0f;
            var boxes = new List<(Vector3 c, Vector3 h)>();
            foreach (var e in scene.Entities) CollectBoxes(e, boxes);
            float best = 0f;
            for (int tries = 0; tries < 400 && best < 16f; tries++)
            {
                Vector3 p;
                if (!NavigationService.RandomPointAround(start, 8f + (float)rng.NextDouble() * 52f, out p)) continue;
                for (float h = 16f; h >= 7f; h -= 1.5f)
                {
                    if (h <= best) break;
                    bool clear = true;
                    foreach (var a in away) if (Math.Abs(a.X - p.X) < h + 6f && Math.Abs(a.Z - p.Z) < h + 6f) { clear = false; break; }
                    if (!clear) continue;
                    foreach (var b in boxes)
                    {
                        if (b.h.X > 40f || b.h.Z > 40f) continue;                 // the ground / the sky
                        if (b.c.Y + b.h.Y < p.Y + 0.15f) continue;               // below the floor
                        if (Math.Abs(b.c.X - p.X) < b.h.X + h + 0.5f && Math.Abs(b.c.Z - p.Z) < b.h.Z + h + 0.5f) { clear = false; break; }
                    }
                    if (!clear) continue;
                    // every 2.5 m across the square must be reachable, level floor (a wall or a pit through the plot fails here;
                    // the merged range model is one mesh the box test above cannot see)
                    int n = Math.Max(2, (int)Math.Ceiling(2f * h / 2.5f));
                    for (int gz = 0; gz <= n && clear; gz++)
                        for (int gx = 0; gx <= n && clear; gx++)
                        {
                            var w = new Vector3(p.X - h + 2f * h * gx / n, p.Y, p.Z - h + 2f * h * gz / n);
                            Vector3 r;
                            if (!NavigationService.RandomPointAround(w, 0.6f, out r) || Math.Abs(r.Y - p.Y) > 0.3f || TemplateSetupSmoke.Dist(r, w) > 0.9f) clear = false;
                        }
                    if (!clear) continue;
                    best = h; plot = p; half = h;
                    break;
                }
            }
            return best > 0f;
        }

        private static void CollectBoxes(GameEntity e, List<(Vector3 c, Vector3 h)> boxes)
        {
            if (e == null || !e.IsActive) return;
            bool skip = e.Name == "Player" || e.Tag == "Player" || e.Name == "Showcase" || e.Name.StartsWith("Showcase ", StringComparison.Ordinal);
            if (!skip && e.GetComponent<MeshRenderer>() != null)
            {
                Vector3f c, h;
                if (SceneRenderService.Instance.TryGetWorldPickBounds(e, out c, out h))
                    boxes.Add((new Vector3(c.X, c.Y, c.Z), new Vector3(Math.Abs(h.X), Math.Abs(h.Y), Math.Abs(h.Z))));
            }
            if (e.Children != null) foreach (var ch in e.Children) CollectBoxes(ch, boxes);
        }

        // A reachable floor spot with open ground around it (the samples at 2.5 m must be reachable and level).
        private static bool OpenSpot(Vector3 start, float minR, float maxR, float clearance, Random rng, List<Vector3> away, out Vector3 spot)
        {
            spot = start;
            for (int tries = 0; tries < 400; tries++)
            {
                if (!NavigationService.RandomPointAround(start, minR + (float)rng.NextDouble() * (maxR - minR), out var p)) continue;
                if (TemplateSetupSmoke.Dist(p, start) < minR) continue;
                bool far = true;
                foreach (var a in away) if (TemplateSetupSmoke.Dist(p, a) < 12f) { far = false; break; }
                if (!far) continue;
                bool open = true;
                for (int k = 0; k < 8 && open; k++)
                {
                    double ang = k * Math.PI / 4.0;
                    var q = new Vector3(p.X + (float)Math.Cos(ang) * clearance, p.Y, p.Z + (float)Math.Sin(ang) * clearance);
                    if (!NavigationService.RandomPointAround(q, 0.4f, out var r) || Math.Abs(r.Y - p.Y) > 0.3f || TemplateSetupSmoke.Dist(r, q) > 0.7f) open = false;
                }
                if (!open) continue;
                spot = p;
                return true;
            }
            return false;
        }

        // The tour on screen (VORTEX_TEMPLATE_SETUP=1): the lab and the VFX corner from the editor camera, in edit mode and a
        // couple of seconds into play — the captures land in the smoke's --capture folder for a look.
        [ModuleInitializer]
        internal static void RegisterCapture() => SmokeRegistry.Add("tactical showcase capture", Capture);

        private static async Task<bool> Capture()
        {
            var log = ConsoleService.Instance;
            if (Environment.GetEnvironmentVariable("VORTEX_TEMPLATE_SETUP") != "1") { log.Log("tactical showcase capture: authoring helper — skipped"); return true; }
            var scene = ProjectData.Current?.ActiveScene;
            var door = scene == null ? null : TemplateSetupSmoke.Find(scene, e => e.Name == "Showcase Door Frame");
            var fire = scene == null ? null : TemplateSetupSmoke.Find(scene, e => e.Name == "Showcase Fire");
            if (door == null || fire == null) { log.Log("tactical showcase capture: no showcase in this scene — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw0 = cam.Yaw, pitch0 = cam.Pitch;
            bool playing = false;
            try
            {
                var lab = TransformMath.WorldPosition(door); lab = new Vector3(lab.X, lab.Y - 1.1f, lab.Z);
                var vfx = TransformMath.WorldPosition(fire); vfx = new Vector3(vfx.X, vfx.Y - 0.95f, vfx.Z);
                Look(cam, lab + new Vector3(0.5f, 3.6f, 9.5f), lab + new Vector3(0f, 0.6f, 0f));
                await CameraSkySmoke.Sample("showcase_lab.bmp", 0.5, 0.5);
                Look(cam, vfx + new Vector3(0.2f, 1.9f, 5.8f), vfx + new Vector3(-0.6f, 1.0f, 0.3f));
                await CameraSkySmoke.Sample("showcase_vfx.bmp", 0.5, 0.5);
                // v3.4: the terrain plot — from the plot's own ground towards the big hill, then straight down from above
                // (with a few craters dug the way the weapon digs them: Terrain.Deform works in edit mode too)
                var terrainEntity = TemplateSetupSmoke.Find(scene, e => e.Name == "Showcase Terrain");
                Vector3 terrainCentre = default(Vector3);
                if (terrainEntity != null)
                {
                    SelectionService.Instance.ClearSelection();   // no transform gizmo in the shot
                    var tc = terrainEntity.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
                    float half = tc != null ? tc.Size * 0.5f : 16f;
                    var corner = TransformMath.WorldPosition(terrainEntity);
                    terrainCentre = corner + new Vector3(half, 0f, half);
                    for (int i = 0; i < 4; i++) Vortex.Terrain.Deform(new Vortex.Vector3(terrainCentre.X + 2f + i * 2.2f, terrainCentre.Y, terrainCentre.Z + 6f - (i % 2) * 3f), 1.3f, 0.4f);
                    await SmokeRegistry.Settle(300);
                    float eyeY; if (!TerrainService.TryHeight(terrainEntity, terrainCentre.X + 6f, terrainCentre.Z + 9f, out eyeY)) eyeY = terrainCentre.Y;
                    Look(cam, new Vector3(terrainCentre.X + 6f, eyeY + 2.2f, terrainCentre.Z + 9f), terrainCentre + new Vector3(-5f, 1.6f, -3f));
                    await CameraSkySmoke.Sample("showcase_terrain.bmp", 0.5, 0.5);
                    Look(cam, terrainCentre + new Vector3(0f, 34f, -0.5f), terrainCentre);
                    await CameraSkySmoke.Sample("showcase_terrain_top.bmp", 0.5, 0.5);
                }
                EditorCommands.Play(); playing = true;
                await SmokeRegistry.Settle(2200);
                Look(cam, lab + new Vector3(0.5f, 3.6f, 9.5f), lab + new Vector3(0f, 0.6f, 0f));
                await CameraSkySmoke.Sample("showcase_lab_play.bmp", 0.5, 0.5);
                log.Log("tactical showcase capture: lab at " + TemplateSetupSmoke.F(lab) + ", VFX corner at " + TemplateSetupSmoke.F(vfx)
                    + (terrainEntity != null ? ", terrain at " + TemplateSetupSmoke.F(terrainCentre) : ", no terrain") + " captured (edit + play)");
                return true;
            }
            finally
            {
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(400); }
                cam.SetPositionAndRotation(px, py, pz, yaw0, pitch0);
                Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            }
        }

        private static void Look(EditorCameraController cam, Vector3 from, Vector3 to)
        {
            TransformMath.LookAngles(from, to, out float yaw, out float pitch);
            cam.SetPositionAndRotation(from.X, from.Y, from.Z, yaw, pitch);
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
        }

        private static GameEntity Primitive(string name, PrimitiveType type, Vector3 pos, Vector3 scale, float r, float g, float b)
        {
            var e = _scene.CreatePrimitive(type);
            e.Name = name;
            e.Transform.LocalPosition = pos;
            e.Transform.LocalScale = scale;
            var mr = e.GetComponent<MeshRenderer>();
            if (mr != null) { mr.ColorR = r; mr.ColorG = g; mr.ColorB = b; }
            _made++;
            return e;
        }

        private static GameEntity Box(string name, Vector3 pos, Vector3 scale, float r, float g, float b)
        {
            var e = Primitive(name, PrimitiveType.Cube, pos, scale, r, g, b);
            e.AddComponentDirect(new BoxCollider(e));
            return e;
        }

        private static GameEntity Ball(string name, Vector3 pos, float diameter, float r, float g, float b)
        {
            var e = Primitive(name, PrimitiveType.Sphere, pos, new Vector3(diameter, diameter, diameter), r, g, b);
            e.AddComponentDirect(new SphereCollider(e));
            return e;
        }

        private static GameEntity Fx(string name, Vector3 pos, string vfx)
        {
            var e = _scene.CreateEntity(name);
            e.Transform.LocalPosition = pos;
            e.AddComponentDirect(new ParticleSystem(e) { VfxPath = vfx, PlayOnStart = true, PreviewInEditor = true });
            _made++;
            return e;
        }

        private static GameEntity DecalOn(string name, Vector3 pos, Vector3 size, string material, int blend, float yawDeg)
        {
            var e = _scene.CreateEntity(name);
            e.Transform.LocalPosition = pos;
            e.Transform.LocalRotation = new Vector3(-90f, yawDeg, 0f);   // local +Y turned to -Z: projects onto the wall's front face
            e.AddComponentDirect(new Decal(e) { MaterialPath = material, Size = size, Blend = blend, AngleFade = 0.5f, Opacity = blend == 1 ? 0.9f : 1f });
            _made++;
            return e;
        }
    }
}
