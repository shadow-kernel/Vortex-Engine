using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The retained scene submit (#361) draws exactly what the whole-scene submit draws. A test scene is rendered through
    /// both (SceneRenderService.RetainedSubmit on / off) and the frames are compared pixel by pixel:
    /// <list type="bullet">
    /// <item>"perf retained submit: play edits" — in play, the same edits in both runs, a frame after each: entities moved
    /// and a parent moved (its children follow), a colour and a material changed, an entity deactivated and one destroyed
    /// (by the script API), entities spawned at the root and under a parent, one re-parented, one re-activated, a renderer
    /// switched off. The moves and restyles must not walk the scene. After Stop the authored scene is back.</item>
    /// <item>"perf retained submit: edit mode" — edit-mode edits, the FP/3P layer presentation of the Game view, a second
    /// view (an off-screen target like the split layout's), the editor overlays on top, and a preview render, which must
    /// not show the scene's instances while the main view keeps them.</item>
    /// <item>"perf shared meshes" (#359) — twenty-four entities with one .vmesh load it once, the cubes and spheres in ten
    /// colours draw as one instanced run per mesh, and a scene that no longer uses the .vmesh frees it (#358).</item>
    /// </list>
    /// </summary>
    internal static class PerfRetainedSmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("perf retained submit: play edits draw what the full submit draws", PlayEdits);
            SmokeRegistry.Add("perf retained submit: edit mode, game view, second view, overlays, previews", EditViews);
            SmokeRegistry.Add("perf shared meshes: one GPU mesh per .vmesh, one draw run per mesh", SharedMeshes);
        }

        // a pixel differs when a channel is off by more than this; a frame matches when at most MatchFraction of them do
        private const int Tolerance = 24;
        private const double MatchFraction = 0.003;
        // an edit must change at least this much of the frame, or the comparison proves nothing
        private const double ChangeFraction = 0.002;

        // ------------------------------------------------------------------------------------------ play mode

        private static async Task<bool> PlayEdits()
        {
            var log = ConsoleService.Instance;
            var project = ProjectData.Current;
            if (project?.ActiveScene == null || string.IsNullOrEmpty(project.Path)) { log.Log("perf play edits: no project / scene — skipped"); return true; }
            var state = new Restore(project);
            string vmat = WriteMaterial(project);
            try
            {
                var retained = await PlaySession(project, true, vmat, state);
                var full = await PlaySession(project, false, vmat, state);
                if (retained == null || full == null) return false;
                bool ok = true;
                for (int k = 0; k < retained.Shots.Count; k++)
                {
                    double same = Diff(retained.Shots[k], full.Shots[k]);
                    string step = retained.Steps[k];
                    log.Log("perf play edits: " + step + " — " + Pct(same) + " of the pixels differ from the full submit"
                        + (k > 0 ? ", the edit changed " + Pct(Diff(retained.Shots[k], retained.Shots[k - 1])) + (k < retained.Walks.Count + 1 ? ", scene walks " + retained.Walks[k - 1] : "") : ""));
                    if (same > MatchFraction) { log.LogError("perf play edits: after '" + step + "' the retained submit draws something else than the full submit (" + Pct(same) + " of the pixels)"); ok = false; }
                    if (k > 0 && k < retained.Shots.Count - 1 && Diff(full.Shots[k], full.Shots[k - 1]) < ChangeFraction)
                    { log.LogError("perf play edits: '" + step + "' changed nothing on screen — the comparison proves nothing"); ok = false; }
                }
                // moves and restyles are matrix / tint / material updates: no scene walk (#361)
                for (int k = 0; k < 2 && k < retained.Walks.Count; k++)
                    if (retained.Walks[k] != 0) { log.LogError("perf play edits: '" + retained.Steps[k + 1] + "' walked the scene " + retained.Walks[k] + " time(s) — it should only send what changed"); ok = false; }
                double back = Diff(retained.Authored, retained.Shots[retained.Shots.Count - 1]);
                if (back > MatchFraction) { log.LogError("perf play edits: after Stop the scene does not look authored again (" + Pct(back) + " of the pixels)"); ok = false; }
                return ok;
            }
            finally
            {
                await state.Back();
                try { File.Delete(Path.Combine(project.Path, vmat)); } catch { }
            }
        }

        private sealed class Session
        {
            public readonly List<string> Steps = new List<string>();
            public readonly List<Img> Shots = new List<Img>();
            public readonly List<long> Walks = new List<long>();
            public Img Authored;
        }

        private static async Task<Session> PlaySession(ProjectData project, bool retained, string vmat, Restore state)
        {
            var log = ConsoleService.Instance;
            string tag = retained ? "retained" : "full";
            SceneRenderService.RetainedSubmit = retained;
            var rig = BuildRig(project, "PerfRetainedPlay_" + tag);
            await state.Use(rig.Scene);
            var s = new Session();
            s.Authored = await Capture("perf_play_" + tag + "_authored.bmp");
            EditorCommands.Play();
            if (PlayModeService.Instance.State != PlayState.Playing) { log.LogError("perf play edits: play did not start"); return null; }
            try
            {
                await SmokeRegistry.Settle(1500);   // the project's scripts compile on the first play
                long walks = SceneRenderService.SceneWalks;
                async Task Shot(string step)
                {
                    // the reference re-sends its static set after every step: the whole-scene path (the static split, #364 A)
                    // did not notice a restyle of a static entity during play (Scene.SetMaterial, an Inspector edit) until
                    // the next structural change — the retained submit shows it at once
                    if (!retained) SceneRenderService.StaticDirty = true;
                    s.Steps.Add(step);
                    s.Shots.Add(await Capture("perf_play_" + tag + "_" + s.Shots.Count + ".bmp"));
                    if (s.Shots.Count > 1) s.Walks.Add(SceneRenderService.SceneWalks - walks);
                    walks = SceneRenderService.SceneWalks;
                }
                await Shot("the scene");

                rig.Mover.Transform.LocalPosition = new Vector3(4f, 493f, 18f);
                rig.RigA.Transform.LocalPosition = new Vector3(-4f, 502f, 20f);
                rig.RigA.Transform.LocalRotation = new Vector3(0f, 30f, 0f);
                await Shot("an entity and a parent moved");

                var a2 = rig.A2.GetComponent<MeshRenderer>(); a2.ColorR = 0.1f; a2.ColorG = 0.2f; a2.ColorB = 1f;
                rig.A1.GetComponent<MeshRenderer>().MaterialPath = vmat;
                await Shot("a colour and a material changed");

                var rt = Editor.Scripting.ScriptRuntime.Instance;
                Vortex.Scene.SetActive(rt.HandleForEntity(rig.Door), false);
                if (!rt.DestroyEntity(rt.HandleForEntity(rig.Doomed))) { log.LogError("perf play edits: Scene.Destroy did not destroy the entity"); return null; }
                await Shot("an entity deactivated, one destroyed");

                rig.Scene.Entities.Add(Mesh(rig.Scene, "Spawned", "Primitive:Cube", new[] { 0.5f, 0.1f, 0.9f }, new Vector3(0f, 497f, 16f), new Vector3(2f, 2f, 2f)));
                var child = Mesh(rig.Scene, "Spawned Child", "Primitive:Cube", new[] { 1f, 1f, 1f }, new Vector3(0f, -4f, 0f), new Vector3(2.5f, 2.5f, 2.5f));
                child.Parent = rig.RigB; rig.RigB.Children.Add(child);
                await Shot("entities spawned at the root and under a parent");

                rig.A3.SetParent(rig.RigB);
                Vortex.Scene.SetActive(rt.HandleForEntity(rig.Door), true);
                rig.B1.GetComponent<MeshRenderer>().IsEnabled = false;
                await Shot("re-parented, re-activated, a renderer switched off");
            }
            finally
            {
                EditorCommands.Stop();
            }
            await SmokeRegistry.Settle(600);
            s.Steps.Add("stopped");
            s.Shots.Add(await Capture("perf_play_" + tag + "_stopped.bmp"));
            return s;
        }

        // ------------------------------------------------------------------------------------------ edit mode

        private static async Task<bool> EditViews()
        {
            var log = ConsoleService.Instance;
            var project = ProjectData.Current;
            if (project?.ActiveScene == null || string.IsNullOrEmpty(project.Path)) { log.Log("perf edit mode: no project / scene — skipped"); return true; }
            var state = new Restore(project);
            var second = new SecondaryViewSession();
            bool ok = true;
            try
            {
                var rig = BuildRig(project, "PerfRetainedEdit");
                await state.Use(rig.Scene);

                // the same frame through both submits, as authored and after edits — each edit made on a retained scene that
                // is in place (a Compare switches back to it at the end; a few frames send it again), so it goes the way a
                // gizmo drag goes: a matrix or tint update of one instance
                ok &= await Compare("edit mode, as authored", "perf_edit_0");
                await SmokeRegistry.Settle(400);
                rig.Mover.Transform.LocalPosition = new Vector3(-3f, 495f, 18f);
                SelectionService.Instance.NotifyTransformChanged();   // what a gizmo drag does
                rig.RigB.Transform.LocalPosition = new Vector3(5f, 502f, 20f);
                var a2 = rig.A2.GetComponent<MeshRenderer>(); a2.ColorR = 0.2f; a2.ColorG = 0.9f; a2.ColorB = 0.9f;
                ok &= await Compare("edit mode, moved / parent moved / recoloured", "perf_edit_1");
                await SmokeRegistry.Settle(400);
                rig.Door.IsActive = false;
                rig.Scene.Entities.Add(Mesh(rig.Scene, "Placed", "Primitive:Sphere", new[] { 0.9f, 0.9f, 0.2f }, new Vector3(0f, 497f, 16f), new Vector3(2f, 2f, 2f)));
                ok &= await Compare("edit mode, hidden / placed", "perf_edit_2");

                // the Game view presents the layers like play (#175): FP in its own pass, 3P left out
                var hidden = await Capture("perf_edit_layers_hidden.bmp");
                SceneRenderService.EditorViewmodelPreview = SceneRenderService.ViewmodelPreviewMode.GameView;
                ok &= await Compare("Game view layers", "perf_edit_gameview");
                var game = await Capture("perf_edit_layers_game.bmp");
                SceneRenderService.EditorViewmodelPreview = SceneRenderService.ViewmodelPreviewMode.Hidden;
                if (Diff(hidden, game) < ChangeFraction) { log.LogError("perf edit mode: the Game view did not change the FP / 3P cubes — the layer comparison proves nothing"); ok = false; }

                // a second view (the split layout's off-screen target) draws the same instances
                var secondOn = await Second(second, true);
                var secondOff = await Second(second, false);
                double sd = Diff(secondOn, secondOff);
                log.Log("perf edit mode: second view — " + Pct(sd) + " of the pixels differ from the full submit, " + Pct(Busy(secondOn)) + " show the scene's objects");
                if (secondOn == null || secondOff == null) { log.LogError("perf edit mode: the second view rendered nothing"); ok = false; }
                else if (sd > MatchFraction) { log.LogError("perf edit mode: the second view draws something else with the retained submit (" + Pct(sd) + ")"); ok = false; }
                else if (Busy(secondOn) < 0.05) { log.LogError("perf edit mode: the second view does not show the scene's objects"); ok = false; }
                SceneRenderService.RetainedSubmit = true;

                // the editor overlays (gizmo, icons, grid) still draw over the retained instances
                SelectionService.Instance.Select(rig.Mover);
                SceneRenderService.HideEditorOverlays = false;
                var overlays = await Capture("perf_edit_overlays.bmp");
                SceneRenderService.HideEditorOverlays = true;
                var plain = await Capture("perf_edit_no_overlays.bmp");
                SelectionService.Instance.ClearSelection();
                double od = Diff(overlays, plain);
                log.Log("perf edit mode: the overlays change " + Pct(od) + " of the frame");
                if (od < ChangeFraction) { log.LogError("perf edit mode: no editor overlays over the retained scene"); ok = false; }

                // a preview renders its own items only: the wall behind its cube is a retained instance of the scene
                var before = await Capture("perf_edit_before_preview.bmp");
                var withWall = PreviewRenderer.RenderPrimitive("Primitive:Cube", 96);
                var after = await Capture("perf_edit_after_preview.bmp");
                rig.Scene.Entities.Remove(rig.Wall);
                SceneRenderService.Instance.RemoveEntityTree(rig.Wall);
                await Capture("perf_edit_no_wall.bmp");   // a submit without the wall
                var noWall = PreviewRenderer.RenderPrimitive("Primitive:Cube", 96);
                double pd = Diff(Img.From(withWall), Img.From(noWall));
                double md = Diff(before, after);
                log.Log("perf edit mode: preview with the scene's wall behind it vs without — " + Pct(pd) + " differ; the main view before / after the preview — " + Pct(md));
                if (withWall == null || noWall == null) { log.LogError("perf edit mode: the preview rendered nothing"); ok = false; }
                else if (pd > MatchFraction) { log.LogError("perf edit mode: the scene's retained instances show in a preview (" + Pct(pd) + ")"); ok = false; }
                if (md > MatchFraction) { log.LogError("perf edit mode: after a preview the main view lost the retained instances (" + Pct(md) + ")"); ok = false; }
                return ok;
            }
            finally
            {
                second.Shutdown();
                SelectionService.Instance.ClearSelection();
                await state.Back();
            }
        }

        /// <summary>The current frame through the retained submit and through the full submit: the same picture.</summary>
        private static async Task<bool> Compare(string what, string file)
        {
            SceneRenderService.RetainedSubmit = true;
            var on = await Capture(file + "_retained.bmp");
            SceneRenderService.RetainedSubmit = false;
            var off = await Capture(file + "_full.bmp");
            SceneRenderService.RetainedSubmit = true;
            double d = Diff(on, off);
            ConsoleService.Instance.Log("perf edit mode: " + what + " — " + Pct(d) + " of the pixels differ from the full submit");
            if (d <= MatchFraction) return true;
            ConsoleService.Instance.LogError("perf edit mode: " + what + " — the retained submit draws something else than the full submit (" + Pct(d) + ")");
            return false;
        }

        /// <summary>The editor camera's view rendered into an off-screen target (SecondaryViewSession, the split layout's path).</summary>
        private static async Task<Img> Second(SecondaryViewSession view, bool retained)
        {
            SceneRenderService.RetainedSubmit = retained;
            EditorViewportSession.RequestResubmit();
            await SmokeRegistry.Settle(500);   // the main view submits the scene first; the target renders the renderer's queue
            view.SetCamera(null, true);        // a fresh first render
            for (int i = 0; i < 20; i++)
            {
                if (view.RenderIfNeeded(320, 200, out byte[] px, out int w, out int h, out int pitch)) return Img.FromBgra(px, w, h, pitch);
                await Task.Delay(60);
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------ shared meshes (#359)

        private static async Task<bool> SharedMeshes()
        {
            var log = ConsoleService.Instance;
            var project = ProjectData.Current;
            if (project?.ActiveScene == null || string.IsNullOrEmpty(project.Path)) { log.Log("perf shared meshes: no project / scene — skipped"); return true; }
            if (!VortexAPI.TryGetResourceCounts(out _, out _, out _)) { log.Log("perf shared meshes: engine library without GetResourceCounts — skipped"); return true; }
            string rel = "Assets/SmokePerf/perf_shared.vmesh";
            string full = Path.Combine(project.Path, rel);
            // a .vmesh the engine wrote itself: the render-side import cache keeps one per submesh of a static model (#364 C)
            string source = CachedSubmesh(project);
            if (source == null) { log.Log("perf shared meshes: no static model to take a .vmesh from (or no Assimp) — skipped"); return true; }
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.Copy(source, full, true);
            var state = new Restore(project);
            bool ok = true;
            try
            {
                var scene = new Scene(project, "PerfSharedMeshes");
                AddSunAndCamera(scene);
                await state.Use(scene);   // its activation frees the models of the scene before (#358)
                await Capture("perf_shared_0.bmp");
                VortexAPI.TryGetResourceCounts(out int m0, out int mat0, out _);
                int draws0 = await MainDraws();

                var made = new List<GameEntity>();
                for (int i = 0; i < 24; i++)
                {
                    var e = Mesh(scene, "Shared " + i, rel, Palette[i % 10], new Vector3((i % 6) * 2.8f - 7f, 507f - (i / 6) * 2.8f, 20f), new Vector3(2f, 2f, 2f));
                    scene.Entities.Add(e); made.Add(e);
                }
                for (int i = 0; i < 100; i++)
                {
                    var e = Mesh(scene, "Cube " + i, "Primitive:Cube", Palette[i % 10], new Vector3((i % 10) * 1.4f - 6.3f, 495f - (i / 10) * 1.2f, 20f), new Vector3(0.9f, 0.9f, 0.9f));
                    scene.Entities.Add(e);
                }
                await Capture("perf_shared_1.bmp");
                VortexAPI.TryGetResourceCounts(out int m1, out int mat1, out _);
                int draws1 = await MainDraws();
                log.Log("perf shared meshes: 24 .vmesh entities + 100 cubes in 10 colours — meshes " + m0 + " → " + m1 + ", materials " + mat0 + " → " + mat1 + ", main-pass draws " + draws0 + " → " + draws1);
                if (m1 - m0 > 2) { log.LogError("perf shared meshes: 24 entities with one .vmesh created " + (m1 - m0) + " meshes (one per path, #359)"); ok = false; }
                if (mat1 - mat0 > 1) { log.LogError("perf shared meshes: ten colours created " + (mat1 - mat0) + " materials (the colour is the instance tint, #331)"); ok = false; }
                if (draws1 - draws0 != 2) { log.LogError("perf shared meshes: the spheres and cubes took " + (draws1 - draws0) + " main-pass draws (one instanced run per mesh expected)"); ok = false; }

                // the .vmesh belongs to the path cache: deleting its entities keeps it, a scene without it frees it (#358)
                EditorCommands.DeleteEntities(made);
                await Capture("perf_shared_2.bmp");
                VortexAPI.TryGetResourceCounts(out int m2, out _, out _);
                SceneRenderService.EvictModelsUnusedBy(scene);
                await Capture("perf_shared_3.bmp");
                VortexAPI.TryGetResourceCounts(out int m3, out _, out _);
                log.Log("perf shared meshes: entities deleted — meshes " + m2 + ", the scene no longer uses the .vmesh — meshes " + m3);
                if (m2 > m1) { log.LogError("perf shared meshes: deleting the entities grew the mesh count"); ok = false; }
                if (m3 > m2 - 1) { log.LogError("perf shared meshes: the unused .vmesh was not freed (#358)"); ok = false; }
                return ok;
            }
            finally
            {
                await state.Back();
                try { Directory.Delete(Path.GetDirectoryName(full), true); } catch { }
            }
        }

        /// <summary>The main-pass draws of the last frame. An off-screen view (the split layout's second view) adds its draws
        /// to the counter until the next frame: the smallest of a few readings is the main view's own.</summary>
        private static async Task<int> MainDraws()
        {
            int best = int.MaxValue;
            for (int i = 0; i < 6; i++)
            {
                best = Math.Min(best, VortexAPI.DrawCalls - VortexAPI.DrawCallsShadow - VortexAPI.DrawCallsPost);
                await Task.Delay(40);
            }
            return best;
        }

        /// <summary>submesh_0.vmesh of the smallest static model under Assets/, from the render-side import cache (the import
        /// writes it); null when there is none.</summary>
        private static string CachedSubmesh(ProjectData project)
        {
            if (!VortexAPI.IsAssimpAvailable()) return null;
            string assets = Path.Combine(project.Path, "Assets");
            if (!Directory.Exists(assets)) return null;
            var models = new List<FileInfo>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(assets, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".glb" || ext == ".gltf" || ext == ".fbx" || ext == ".obj") models.Add(new FileInfo(f));
                }
            }
            catch { return null; }
            models.Sort((a, b) => a.Length.CompareTo(b.Length));
            for (int i = 0; i < models.Count && i < 8; i++)
            {
                string model = models[i].FullName;
                string relModel = Path.GetRelativePath(project.Path, model).Replace('\\', '/');
                try { SceneRenderService.LoadModelSubmeshes(relModel); } catch { continue; }
                string dir = SceneRenderService.ModelImportCache.Dir(project.Path, model);
                if (dir == null) continue;
                string vmesh = SceneRenderService.ModelImportCache.SubmeshFile(dir, 0);
                if (File.Exists(vmesh)) return vmesh;
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------ the test scene

        private sealed class Rig
        {
            public Scene Scene;
            public GameEntity RigA, RigB, A1, A2, A3, B1, Mover, Door, Doomed, Wall;
        }

        private static readonly float[][] Palette =
        {
            new[] { 0.90f, 0.20f, 0.20f }, new[] { 0.20f, 0.70f, 0.25f }, new[] { 0.20f, 0.35f, 0.90f }, new[] { 0.95f, 0.80f, 0.20f },
            new[] { 0.80f, 0.30f, 0.80f }, new[] { 0.20f, 0.80f, 0.85f }, new[] { 0.95f, 0.55f, 0.15f }, new[] { 0.55f, 0.40f, 0.25f },
            new[] { 0.60f, 0.60f, 0.65f }, new[] { 1.00f, 1.00f, 1.00f },
        };

        /// <summary>A sun, a main camera at (0, 500, 0) looking down +Z (the editor camera goes there too) and, 20 m ahead, a
        /// backdrop, two parents with children, single entities to move, hide, destroy, FP / 3P layer cubes, and a wall near
        /// the origin that only a preview's camera could see.</summary>
        private static Rig BuildRig(ProjectData project, string name)
        {
            var scene = new Scene(project, name);
            var r = new Rig { Scene = scene };
            AddSunAndCamera(scene);
            Add(scene, Mesh(scene, "Backdrop", "Primitive:Cube", new[] { 0.45f, 0.47f, 0.5f }, new Vector3(0f, 500f, 34f), new Vector3(120f, 80f, 2f)));
            // everything within ±8 m of the view axis at 20 m: a narrow view (the split layout, a small runner window) shows all
            r.RigA = Add(scene, new GameEntity(scene, "Rig A")); r.RigA.Transform.LocalPosition = new Vector3(-4f, 500f, 20f);
            r.RigB = Add(scene, new GameEntity(scene, "Rig B")); r.RigB.Transform.LocalPosition = new Vector3(5f, 500f, 20f);
            r.A1 = Child(r.RigA, Mesh(scene, "A1", "Primitive:Cube", null, new Vector3(-2.5f, 4f, 0f), new Vector3(2.5f, 2.5f, 2.5f)));   // default grey: a material shows untinted
            r.A2 = Child(r.RigA, Mesh(scene, "A2", "Primitive:Cube", new[] { 0.9f, 0.15f, 0.1f }, new Vector3(2.5f, 4f, 0f), new Vector3(2.5f, 2.5f, 2.5f)));
            r.A3 = Child(r.RigA, Mesh(scene, "A3", "Primitive:Sphere", new[] { 0.15f, 0.8f, 0.2f }, new Vector3(0f, -1f, 0f), new Vector3(3f, 3f, 3f)));
            r.B1 = Child(r.RigB, Mesh(scene, "B1", "Primitive:Cube", new[] { 0.95f, 0.85f, 0.1f }, new Vector3(0f, 5f, 0f), new Vector3(2.5f, 2.5f, 2.5f)));
            r.Mover = Add(scene, Mesh(scene, "Mover", "Primitive:Cube", new[] { 1f, 0.5f, 0.1f }, new Vector3(0f, 493f, 18f), new Vector3(2.5f, 2.5f, 2.5f)));
            r.Door = Add(scene, Mesh(scene, "Door", "Primitive:Cube", new[] { 0.1f, 0.85f, 0.9f }, new Vector3(-7f, 492f, 22f), new Vector3(2.5f, 4f, 1f)));
            r.Doomed = Add(scene, Mesh(scene, "Doomed", "Primitive:Cube", new[] { 0.85f, 0.1f, 0.8f }, new Vector3(6.5f, 497.5f, 22f), new Vector3(2.5f, 2.5f, 2.5f)));
            Child(r.Doomed, Mesh(scene, "Doomed Cap", "Primitive:Cube", new[] { 1f, 1f, 1f }, new Vector3(0f, 0.75f, 0f), new Vector3(0.4f, 0.4f, 0.4f)));
            var fp = Add(scene, Mesh(scene, "FP Cube", "Primitive:Cube", new[] { 0.2f, 0.3f, 1f }, new Vector3(-2f, 509f, 16f), new Vector3(1.8f, 1.8f, 1.8f)));
            fp.GetComponent<MeshRenderer>().RenderLayer = 1;
            var tp = Add(scene, Mesh(scene, "3P Cube", "Primitive:Cube", new[] { 0.6f, 0.35f, 0.15f }, new Vector3(2.5f, 509f, 16f), new Vector3(1.8f, 1.8f, 1.8f)));
            tp.GetComponent<MeshRenderer>().RenderLayer = 2;
            // behind the origin as a preview's orbit camera sees it (PreviewCamera.Default: yaw 0.74, pitch 0.42)
            r.Wall = Add(scene, Mesh(scene, "Preview Wall", "Primitive:Cube", new[] { 1f, 0f, 1f }, new Vector3(-3.7f, -2.45f, -4f), new Vector3(6f, 6f, 6f)));
            return r;
        }

        private static void AddSunAndCamera(Scene scene)
        {
            var sun = new GameEntity(scene, "Sun");
            sun.AddComponentDirect(new Light(sun, LightType.Directional) { Intensity = 2.5f, ShadowType = ShadowType.Hard });
            sun.Transform.LocalRotation = new Vector3(50f, 30f, 0f);
            scene.Entities.Add(sun);
            var cam = new GameEntity(scene, "Main Camera");
            cam.AddComponentDirect(new Camera(cam, true) { CameraType = CameraType.MainCamera, FieldOfView = 60f, FarClip = 1000f });
            // a camera without a script gets the default player controller in editor play (it would walk and fall): an empty,
            // disabled Script keeps it where it is
            cam.AddComponentDirect(new Editor.ECS.Components.Scripting.Script(cam) { IsEnabled = false });
            cam.Transform.LocalPosition = new Vector3(0f, 500f, 0f);
            scene.Entities.Add(cam);
        }

        private static GameEntity Mesh(Scene scene, string name, string mesh, float[] color, Vector3 pos, Vector3 scale)
        {
            var e = new GameEntity(scene, name);
            var mr = new MeshRenderer(e) { MeshPath = mesh };
            if (color != null) { mr.ColorR = color[0]; mr.ColorG = color[1]; mr.ColorB = color[2]; }
            e.AddComponentDirect(mr);
            e.Transform.LocalPosition = pos;
            e.Transform.LocalScale = scale;
            return e;
        }

        private static GameEntity Add(Scene scene, GameEntity e) { scene.Entities.Add(e); return e; }
        private static GameEntity Child(GameEntity parent, GameEntity e) { e.Parent = parent; parent.Children.Add(e); return e; }

        private static string WriteMaterial(ProjectData project)
        {
            string rel = "Assets/Materials/SmokePerfRetained.vmat";
            new VortexMaterial { BlendMode = "Opaque", BaseColor = new[] { 0.6f, 0.9f, 0.1f, 1f }, Metallic = 0f, Roughness = 0.7f }.Save(Path.Combine(project.Path, rel));
            return rel;
        }

        /// <summary>Puts the editor back: the scene that was active, the camera, the submit path and the view modes.</summary>
        private sealed class Restore
        {
            private readonly ProjectData _project;
            private readonly Scene _previous;
            private readonly List<Scene> _temp = new List<Scene>();
            private readonly float _x, _y, _z, _yaw, _pitch;
            private readonly bool _retained;

            public Restore(ProjectData project)
            {
                _project = project;
                _previous = project.ActiveScene;
                var cam = EditorCameraController.Instance;
                _x = cam.PositionX; _y = cam.PositionY; _z = cam.PositionZ; _yaw = cam.Yaw; _pitch = cam.Pitch;
                _retained = SceneRenderService.RetainedSubmit;
            }

            /// <summary>Make a test scene the active one, looked at from its camera's spot with the overlays off.</summary>
            public async Task Use(Scene scene)
            {
                _temp.Add(scene);
                _project.Scenes.Add(scene);
                EditorSession.Instance.ActivateScene(scene);
                EditorCameraController.Instance.SetPositionAndRotation(0f, 500f, 0f, 0f, 0f);
                SceneRenderService.HideEditorOverlays = true;
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(1000);   // the first submit, the sun's shadows
            }

            public async Task Back()
            {
                if (PlayModeService.Instance.IsPlaying) EditorCommands.Stop();
                SceneRenderService.RetainedSubmit = _retained;
                SceneRenderService.EditorViewmodelPreview = SceneRenderService.ViewmodelPreviewMode.Hidden;
                SceneRenderService.HideEditorOverlays = false;
                if (_previous != null) EditorSession.Instance.ActivateScene(_previous);
                foreach (var s in _temp) _project.Scenes.Remove(s);
                EditorCameraController.Instance.SetPositionAndRotation(_x, _y, _z, _yaw, _pitch);
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(400);
            }
        }

        // ------------------------------------------------------------------------------------------ frames

        /// <summary>The next presented frame of the main view (CaptureFrame writes the back buffer as a 32-bit BMP).</summary>
        private static async Task<Img> Capture(string file)
        {
            string dir = SmokeRegistry.CaptureDir;
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.GetTempPath(), "vortex-smoke-capture");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, file);
            try { File.Delete(path); } catch { }
            EditorViewportSession.RequestResubmit();
            await SmokeRegistry.Settle(500);   // the resubmit reaches the renderer, a light fading in settles
            VortexAPI.CaptureFrame(path);
            for (int i = 0; i < 100 && !File.Exists(path); i++) await Task.Delay(50);
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(100);
                var img = Img.Read(path);
                if (img != null) return img;
            }
            return null;
        }

        private sealed class Img
        {
            public int W, H;
            public byte[] Rgb;   // top-down, 3 bytes a pixel

            public static Img Read(string path)
            {
                try
                {
                    var d = File.ReadAllBytes(path);
                    if (d.Length < 54 || d[0] != (byte)'B' || d[1] != (byte)'M') return null;
                    int off = BitConverter.ToInt32(d, 10), w = BitConverter.ToInt32(d, 18), h = BitConverter.ToInt32(d, 22);
                    int bytes = BitConverter.ToInt16(d, 28) / 8;
                    bool bottomUp = h > 0; h = Math.Abs(h);
                    if (bytes < 3 || w <= 0 || h <= 0) return null;
                    int stride = ((w * bytes + 3) / 4) * 4;
                    if (off + (long)stride * h > d.Length) return null;   // still being written
                    var img = new Img { W = w, H = h, Rgb = new byte[w * h * 3] };
                    for (int y = 0; y < h; y++)
                    {
                        int row = off + (bottomUp ? h - 1 - y : y) * stride;
                        for (int x = 0; x < w; x++)
                        {
                            int i = row + x * bytes, o = (y * w + x) * 3;
                            img.Rgb[o] = d[i + 2]; img.Rgb[o + 1] = d[i + 1]; img.Rgb[o + 2] = d[i];
                        }
                    }
                    return img;
                }
                catch { return null; }
            }

            public static Img FromBgra(byte[] px, int w, int h, int pitch)
            {
                if (px == null || w <= 0 || h <= 0) return null;
                var img = new Img { W = w, H = h, Rgb = new byte[w * h * 3] };
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * pitch + x * 4, o = (y * w + x) * 3;
                        if (i + 2 >= px.Length) continue;
                        img.Rgb[o] = px[i + 2]; img.Rgb[o + 1] = px[i + 1]; img.Rgb[o + 2] = px[i];
                    }
                return img;
            }

            public static Img From(PreviewImage p) => p == null ? null : FromBgra(p.Bgra, p.Width, p.Height, p.Stride);
        }

        /// <summary>The share of pixels where a channel differs by more than <see cref="Tolerance"/> (1 = not comparable) — in
        /// the middle of the frame: the play HUD (the FPS counter, the debug-camera hint, a compile banner) sits at the edges
        /// and changes on its own.</summary>
        private static double Diff(Img a, Img b)
        {
            if (a == null || b == null || a.W != b.W || a.H != b.H) return 1.0;
            int x0 = a.W / 12, x1 = a.W - a.W / 12, y0 = a.H / 8, y1 = a.H - a.H / 8, n = 0, bad = 0;
            var p = a.Rgb; var q = b.Rgb;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int k = (y * a.W + x) * 3;
                    n++;
                    if (Math.Abs(p[k] - q[k]) > Tolerance || Math.Abs(p[k + 1] - q[k + 1]) > Tolerance || Math.Abs(p[k + 2] - q[k + 2]) > Tolerance) bad++;
                }
            return n == 0 ? 1.0 : (double)bad / n;
        }

        /// <summary>The share of pixels that differ from the frame's most common colour (the backdrop): how much shows objects.</summary>
        private static double Busy(Img a)
        {
            if (a == null) return 0;
            var counts = new Dictionary<int, int>();
            int n = a.W * a.H;
            for (int i = 0; i < n; i++)
            {
                int key = (a.Rgb[i * 3] >> 4) << 8 | (a.Rgb[i * 3 + 1] >> 4) << 4 | (a.Rgb[i * 3 + 2] >> 4);
                counts.TryGetValue(key, out int c); counts[key] = c + 1;
            }
            int most = 0;
            foreach (var kv in counts) if (kv.Value > most) most = kv.Value;
            return 1.0 - (double)most / n;
        }

        private static string Pct(double f) => (f * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " %";
    }
}
