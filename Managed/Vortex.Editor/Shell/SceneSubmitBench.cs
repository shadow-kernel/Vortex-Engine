using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The scene-submit bench (#361): what a frame costs the CPU in edit and play mode as the scene grows — the scene submit
    /// (all phases and the meshes alone), what it allocates, the renderer's queue (swap, sort + draw runs, cull + pack, shadow
    /// casters) and the draw calls. A measuring tool, not a CI check: it does nothing unless VORTEX_PERF_BENCH is set.
    /// <list type="bullet">
    /// <item>VORTEX_PERF_BENCH=generate writes the bench scenes into the open project and registers them: PerfStatic10k
    /// (10,000 static primitives in 100 blocks + one animated character when Assets/Models/Character/soldier.glb and a
    /// .vanim next to it exist), PerfCubesWhite (1,000 white cubes) and PerfCubesColors (the same cubes in 10 colours).</item>
    /// <item>VORTEX_PERF_BENCH=run measures every scene in VORTEX_PERF_BENCH_SCENES (default: those three) — edit mode idle,
    /// edit mode with one entity dragged every frame, play, and play with one static entity moved every frame (a scripted
    /// door) — and logs one "BENCH …" line per phase.</item>
    /// <item>VORTEX_PERF_BENCH=mtcull: crowds of the project's smallest model (the stress test) from 2,000 to 200,000
    /// instances, the renderer's cull + pack with the multithreading off and forced on — where it starts to pay.</item>
    /// </list>
    /// The standalone player logs the same figures in its FPS line (see PlayerHost).
    /// </summary>
    internal static class SceneSubmitBench
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("scene submit bench", Run);

        private static readonly string[] DefaultScenes = { "PerfStatic10k", "PerfCubesWhite", "PerfCubesColors" };

        private static async Task<bool> Run()
        {
            string mode = Environment.GetEnvironmentVariable("VORTEX_PERF_BENCH");
            if (string.IsNullOrEmpty(mode)) return true;
            var log = ConsoleService.Instance;
            var project = ProjectData.Current;
            if (project == null) { log.LogError("bench: no project"); return false; }
            if (mode.Equals("generate", StringComparison.OrdinalIgnoreCase)) return Generate(project);
            if (mode.Equals("mtcull", StringComparison.OrdinalIgnoreCase)) return await MultithreadedCull(project);

            string list = Environment.GetEnvironmentVariable("VORTEX_PERF_BENCH_SCENES");
            var names = string.IsNullOrWhiteSpace(list) ? DefaultScenes : list.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            float seconds = 6f;
            if (float.TryParse(Environment.GetEnvironmentVariable("VORTEX_PERF_BENCH_SECONDS"), NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s > 0.5f) seconds = s;
            bool ok = true;
            foreach (var name in names)
            {
                var scene = project.Scenes.FirstOrDefault(x => x != null && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (scene == null) { log.LogError("bench: no scene '" + name + "' in the project (VORTEX_PERF_BENCH=generate first)"); ok = false; continue; }
                ok &= await Measure(scene, seconds);
            }
            return ok;
        }

        // ------------------------------------------------------------------ measuring

        private sealed class Sample
        {
            public int Submits, Frames;
            public double Seconds, SubmitMs, SubmitMax, MeshMs, Swap, Sort, Cull, Shadow;
            public long Alloc;
            public readonly double[] Phases = new double[6];
        }

        private static async Task<bool> Measure(Scene scene, float seconds)
        {
            var log = ConsoleService.Instance;
            Editor.Core.Editing.EditorSession.Instance.ActivateScene(scene);
            EditorViewportSession.RequestResubmit();
            await SmokeRegistry.Settle(2500);   // models import, the first submit, shader warm-up
            LookThroughMainCamera(scene);
            SceneRenderService.HideEditorOverlays = true;
            try
            {
                Report(scene.Name, "edit-idle", await Collect(seconds * 0.5f, null));
                var mover = FindMovable(scene);
                if (mover != null)
                {
                    var p0 = mover.Transform.LocalPosition; int step = 0;
                    Report(scene.Name, "edit-drag", await Collect(seconds * 0.5f, () =>
                    {
                        mover.Transform.LocalPosition = new Vector3(p0.X + (float)Math.Sin(++step * 0.05) * 2f, p0.Y, p0.Z);
                        SelectionService.Instance.NotifyTransformChanged();   // what the gizmo drag does
                    }));
                    mover.Transform.LocalPosition = p0;
                }

                EditorCommands.Play();
                if (PlayModeService.Instance.State != PlayState.Playing) { log.LogError("bench: play did not start"); return false; }
                await SmokeRegistry.Settle(2000);
                Report(scene.Name, "play", await Collect(seconds, null));
                if (mover != null)
                {
                    var p0 = mover.Transform.LocalPosition; int step = 0;
                    Report(scene.Name, "play-mover", await Collect(seconds * 0.5f, () =>
                        mover.Transform.LocalPosition = new Vector3(p0.X, p0.Y + 1f + (float)Math.Sin(++step * 0.05), p0.Z)));
                }
                EditorCommands.Stop();
                await SmokeRegistry.Settle(500);
                return true;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                if (PlayModeService.Instance.IsPlaying) EditorCommands.Stop();
            }
        }

        /// <summary>The crossover of the multithreaded cull + pack (#361): the same crowd culled on one thread and on the workers.</summary>
        private static async Task<bool> MultithreadedCull(ProjectData project)
        {
            var log = ConsoleService.Instance;
            string assets = Path.Combine(project.Path, "Assets");
            string model = Directory.Exists(assets)
                ? Directory.EnumerateFiles(assets, "*.*", SearchOption.AllDirectories)
                    .Where(f => new[] { ".glb", ".gltf", ".fbx", ".obj" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => new FileInfo(f).Length).FirstOrDefault()
                : null;
            if (model == null) { log.LogError("bench: no model for the crowd"); return false; }
            SceneRenderService.HideEditorOverlays = true;
            try
            {
                foreach (int n in new[] { 2000, 5000, 10000, 20000, 50000, 100000, 200000 })
                {
                    StressTestService.Start(model, n);
                    EditorViewportSession.RequestResubmit();
                    await SmokeRegistry.Settle(1500);
                    foreach (bool mt in new[] { false, true, false, true })
                    {
                        VortexAPI.Multithreading(mt);
                        VortexAPI.MultithreadingForce(mt);
                        await SmokeRegistry.Settle(300);
                        var s = await Collect(2f, null);
                        log.Log("BENCH mtcull instances=" + n + " mt=" + (mt ? "on" : "off") + " active=" + VortexAPI.MultithreadingActive
                            + " fps=" + (s.Frames / Math.Max(0.001, s.Seconds)).ToString("0.0", CultureInfo.InvariantCulture)
                            + " native_cull=" + (s.Cull / Math.Max(1, s.Frames)).ToString("0.000", CultureInfo.InvariantCulture)
                            + " drawn=" + VortexAPI.InstancesDrawn + "/" + VortexAPI.InstancesTested);
                    }
                }
                return true;
            }
            finally
            {
                StressTestService.Stop();
                VortexAPI.Multithreading(true);
                VortexAPI.MultithreadingForce(false);
                SceneRenderService.HideEditorOverlays = false;
                EditorViewportSession.RequestResubmit();
            }
        }

        /// <summary>Sample every frame for <paramref name="seconds"/>; <paramref name="perFrame"/> runs once per frame before
        /// it is rendered (the bench's stand-in for a gizmo drag or a script).</summary>
        private static async Task<Sample> Collect(float seconds, Action perFrame)
        {
            var s = new Sample();
            var cpu = new float[7];
            long lastSubmit = SceneRenderService.SubmitCount;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float lastFrame = VortexAPI.TryGetRenderCpuStats(cpu) ? cpu[6] : -1f;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                perFrame?.Invoke();
                await Task.Delay(1);
                long n = SceneRenderService.SubmitCount;
                if (n != lastSubmit)
                {
                    s.Submits += (int)(n - lastSubmit);
                    lastSubmit = n;
                    s.SubmitMs += SceneRenderService.LastSceneSubmitMs;
                    s.SubmitMax = Math.Max(s.SubmitMax, SceneRenderService.LastSceneSubmitMs);
                    s.MeshMs += SceneRenderService.LastSubmitMs;
                    s.Alloc += SceneRenderService.LastSceneSubmitAllocBytes;
                    for (int i = 0; i < 6; i++) s.Phases[i] += SceneRenderService.LastSubmitPhaseMs[i];
                }
                if (VortexAPI.TryGetRenderCpuStats(cpu) && cpu[6] != lastFrame)
                {
                    lastFrame = cpu[6];
                    s.Frames++;
                    s.Swap += cpu[0]; s.Sort += cpu[1]; s.Cull += cpu[2]; s.Shadow += cpu[3];
                }
            }
            s.Seconds = sw.Elapsed.TotalSeconds;
            return s;
        }

        private static void Report(string scene, string phase, Sample s)
        {
            var ci = CultureInfo.InvariantCulture;
            double sub = Math.Max(1, s.Submits), fr = Math.Max(1, s.Frames);
            int draws = VortexAPI.DrawCalls, main = draws - VortexAPI.DrawCallsShadow - VortexAPI.DrawCallsPost;
            var cpu = new float[6]; VortexAPI.TryGetRenderCpuStats(cpu);
            VortexAPI.TryGetResourceCounts(out int meshes, out int materials, out int textures);
            ConsoleService.Instance.Log("BENCH scene=" + scene + " phase=" + phase + " fps=" + (s.Frames / Math.Max(0.001, s.Seconds)).ToString("0.0", ci)
                + " frames=" + s.Frames + " submits=" + s.Submits
                + " submit_ms=" + (s.SubmitMs / sub).ToString("0.000", ci) + " submit_max=" + s.SubmitMax.ToString("0.000", ci)
                + " meshes_ms=" + (s.MeshMs / sub).ToString("0.000", ci)
                + " phases_ms=" + string.Join("/", s.Phases.Select(p => (p / sub).ToString("0.000", ci)))
                + " alloc_kb=" + (s.Alloc / sub / 1024.0).ToString("0.0", ci)
                + " native_swap=" + (s.Swap / fr).ToString("0.000", ci) + " native_sort=" + (s.Sort / fr).ToString("0.000", ci)
                + " native_cull=" + (s.Cull / fr).ToString("0.000", ci) + " native_shadow=" + (s.Shadow / fr).ToString("0.000", ci)
                + " queue=" + (int)cpu[5] + " draws=" + draws + " main=" + main + " shadow_draws=" + VortexAPI.DrawCallsShadow
                + " drawn=" + VortexAPI.InstancesDrawn + "/" + VortexAPI.InstancesTested
                + " gpu_meshes=" + meshes + " gpu_materials=" + materials);
        }

        private static void LookThroughMainCamera(Scene scene)
        {
            var cam = PlayCameraHelper.FindMainCameraEntity(scene);
            if (cam?.Transform == null) return;
            var p = cam.Transform.LocalPosition; var r = cam.Transform.LocalRotation;
            EditorCameraController.Instance.SetPositionAndRotation(p.X, p.Y, p.Z, r.Y, r.X);
        }

        /// <summary>A static entity with a mesh near the camera's view: the "Mover" the generator puts in, else the first cube.</summary>
        private static GameEntity FindMovable(Scene scene)
        {
            GameEntity found = null, first = null;
            void Walk(GameEntity e)
            {
                if (e == null || found != null) return;
                if (e.Name == "Mover") { found = e; return; }
                if (first == null && e.GetComponent<MeshRenderer>() != null && e.GetComponent<Animator>() == null) first = e;
                for (int i = 0; i < e.Children.Count; i++) Walk(e.Children[i]);
            }
            foreach (var e in scene.Entities.ToList()) Walk(e);
            return found ?? first;
        }

        // ------------------------------------------------------------------ the scenes

        private static bool Generate(ProjectData project)
        {
            var log = ConsoleService.Instance;
            string dir = Path.Combine(project.Path, "Assets", "Scenes");
            Directory.CreateDirectory(dir);
            string character = Path.Combine(project.Path, "Assets", "Models", "Character", "soldier.glb");
            string clip = File.Exists(character)
                ? Directory.EnumerateFiles(Path.Combine(project.Path, "Assets", "Models", "Character"), "*.vanim", SearchOption.AllDirectories).OrderBy(f => f.Contains("walk") ? 0 : 1).FirstOrDefault()
                : null;
            var scenes = new[]
            {
                Static10k(project, File.Exists(character) ? Rel(project, character) : null, clip != null ? Rel(project, clip) : null),
                Cubes(project, "PerfCubesWhite", false),
                Cubes(project, "PerfCubesColors", true),
            };
            foreach (var sc in scenes)
            {
                sc.FilePath = Path.Combine(dir, sc.Name + Scene.FileExtension);
                DataSerializer.SaveAsBinary(sc, sc.FilePath);
                var old = project.Scenes.FirstOrDefault(x => x != null && x.Name == sc.Name);
                if (old != null) project.Scenes.Remove(old);
                project.Scenes.Add(sc);
                log.Log("bench: wrote " + sc.FilePath + " (" + Count(sc.Entities) + " entities)");
            }
            ProjectService.Instance.SaveProject(project);
            return true;
        }

        private static string Rel(ProjectData p, string full) => Path.GetRelativePath(p.Path, full).Replace('\\', '/');

        private static int Count(IList<GameEntity> list) { int n = 0; foreach (var e in list) n += 1 + Count(e.Children); return n; }

        private static readonly float[][] Palette =
        {
            new[] { 0.90f, 0.20f, 0.20f }, new[] { 0.20f, 0.70f, 0.25f }, new[] { 0.20f, 0.35f, 0.90f }, new[] { 0.95f, 0.80f, 0.20f },
            new[] { 0.80f, 0.30f, 0.80f }, new[] { 0.20f, 0.80f, 0.85f }, new[] { 0.95f, 0.55f, 0.15f }, new[] { 0.55f, 0.40f, 0.25f },
            new[] { 0.60f, 0.60f, 0.65f }, new[] { 1.00f, 1.00f, 1.00f },
        };

        private static GameEntity Mesh(string name, string mesh, float[] color)
        {
            var e = new GameEntity(name);
            var mr = new MeshRenderer(e) { MeshPath = mesh };
            if (color != null) { mr.ColorR = color[0]; mr.ColorG = color[1]; mr.ColorB = color[2]; }
            e.AddComponentDirect(mr);
            return e;
        }

        private static void Child(GameEntity parent, GameEntity child) { child.Parent = parent; parent.Children.Add(child); }

        private static void AddSunAndCamera(Scene scene, Vector3 camPos, Vector3 camRot)
        {
            var sun = new GameEntity("Sun");
            sun.AddComponentDirect(new Light(sun, LightType.Directional) { Intensity = 2.5f, ShadowType = ShadowType.Hard });
            sun.Transform.LocalRotation = new Vector3(50, 30, 0);
            scene.Entities.Add(sun);
            var cam = new GameEntity("Main Camera");
            cam.AddComponentDirect(new Camera(cam, true) { CameraType = CameraType.MainCamera, FieldOfView = 60f, FarClip = 1000f });
            cam.Transform.LocalPosition = camPos;
            cam.Transform.LocalRotation = camRot;
            scene.Entities.Add(cam);
        }

        /// <summary>10,000 static primitives (cubes, spheres, cylinders in 10 looks) in 100 blocks of 100, a ground, the sun,
        /// a camera over one corner, a "Mover" cube in view, and the animated character when the model is there.</summary>
        private static Scene Static10k(ProjectData project, string character, string clip)
        {
            var scene = new Scene(project, "PerfStatic10k");
            var rng = new Random(361);
            string[] kinds = { "Primitive:Cube", "Primitive:Cube", "Primitive:Cube", "Primitive:Sphere", "Primitive:Cylinder" };
            var ground = Mesh("Ground", "Primitive:Plane", Palette[8]);
            ground.Transform.LocalScale = new Vector3(400, 1, 400);
            scene.Entities.Add(ground);
            for (int b = 0; b < 100; b++)
            {
                int bx = b % 10, bz = b / 10;
                var block = new GameEntity("Block " + b);
                block.Transform.LocalPosition = new Vector3((bx - 5) * 30f, 0, bz * 30f);
                block.Transform.LocalRotation = new Vector3(0, (float)(rng.NextDouble() * 20 - 10), 0);
                for (int i = 0; i < 100; i++)
                {
                    int ix = i % 10, iz = i / 10;
                    var e = Mesh("Prop " + b + "." + i, kinds[rng.Next(kinds.Length)], Palette[rng.Next(Palette.Length)]);
                    float sc = 0.6f + (float)rng.NextDouble() * 1.4f;
                    e.Transform.LocalPosition = new Vector3(ix * 2.8f - 12.6f, sc * 0.5f, iz * 2.8f - 12.6f);
                    e.Transform.LocalRotation = new Vector3(0, (float)(rng.NextDouble() * 360), 0);
                    e.Transform.LocalScale = new Vector3(sc, sc, sc);
                    Child(block, e);
                }
                scene.Entities.Add(block);
            }
            var mover = Mesh("Mover", "Primitive:Cube", Palette[3]);
            mover.Transform.LocalPosition = new Vector3(0, 2, 18);
            mover.Transform.LocalScale = new Vector3(2, 2, 2);
            scene.Entities.Add(mover);
            if (character != null)
            {
                var hero = new GameEntity("Character");
                hero.AddComponentDirect(new MeshRenderer(hero) { MeshPath = character });
                var anim = new Animator(hero) { PlayOnStart = true };
                if (clip != null) { anim.Clips.Add(new AnimatorClipEntry { Name = "Walk", Path = clip }); anim.DefaultClip = "Walk"; }
                hero.AddComponentDirect(anim);
                hero.Transform.LocalPosition = new Vector3(4, 0, 14);
                scene.Entities.Add(hero);
            }
            AddSunAndCamera(scene, new Vector3(0, 18, -14), new Vector3(28, 0, 0));
            return scene;
        }

        /// <summary>1,000 cubes in a 40 x 25 grid the camera sees whole — white, or in 10 colours (100 each).</summary>
        private static Scene Cubes(ProjectData project, string name, bool colours)
        {
            var scene = new Scene(project, name);
            for (int i = 0; i < 1000; i++)
            {
                var e = Mesh("Cube " + i, "Primitive:Cube", colours ? Palette[i % 10] : Palette[9]);
                e.Transform.LocalPosition = new Vector3((i % 40) * 1.6f - 31.2f, 0.5f, (i / 40) * 1.6f);
                if (i == 500) e.Name = "Mover";
                scene.Entities.Add(e);
            }
            AddSunAndCamera(scene, new Vector3(0, 34, -22), new Vector3(48, 0, 0));
            return scene;
        }
    }
}
