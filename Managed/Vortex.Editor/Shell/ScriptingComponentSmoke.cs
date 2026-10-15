using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Physics;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.Scripting;

namespace VortexEditor.Shell
{
    /// <summary>Components added while playing (#421), on the rendered picture. The game camera looks along +Z; 4 m ahead of it a
    /// red cube that is only a static BoxCollider hangs in the air above eye level, and Physics.AddRigidbody(cube, 5) makes it
    /// a dynamic body that falls out of the frame; below eye level a bare entity gets a Mesh Renderer (Scene.AddComponent +
    /// SetMeshOf) and appears blue. The frame is searched for the colours — the scene's player script may nudge the camera, a
    /// narrow split view on a small CI display changes where things land — and each step waits for the frame that shows its
    /// state, up to a timeout (a software renderer draws a few frames a second). After Stop neither runtime component is left.
    /// Skipped on an engine build without Jolt.</summary>
    internal static class ScriptingComponentSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("scripting add component", Run);

        private const float Height = 620f, Ahead = 4f, Offset = 0.6f, Size = 0.8f;
        /// <summary>Samples of a colour (every 2nd pixel of every 2nd row) that count as "a cube is there": a 0.8 m cube 4 m away
        /// covers some 1,000 of them on a 375-pixel-high frame.</summary>
        private const int Seen = 20;

        private static bool Red((int r, int g, int b) c) => c.r > 90 && c.r > c.g + 50 && c.r > c.b + 50;
        private static bool Blue((int r, int g, int b) c) => c.b > 80 && c.b > c.r + 40 && c.b > c.g + 30;

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("scripting add component: no scene — skipped"); return true; }
            if (!PhysicsService.Available) { log.Log("scripting add component: no Jolt world in this engine build — skipped"); return true; }
            var camEntity = PlayCameraHelper.FindMainCameraEntity(scene);
            if (camEntity?.Transform == null) { log.Log("scripting add component: the scene has no main camera — skipped"); return true; }

            var camT = camEntity.Transform;
            var camPos = camT.LocalPosition; var camRot = camT.LocalRotation;
            var made = new List<GameEntity>();
            // the scene's own scripts on the camera and above it (the template's player controller stands the camera on the
            // floor 620 m below, turns it with the mouse, sets its field of view) are off for the check: the camera stays put
            var paused = new List<Editor.ECS.Components.Scripting.Script>();
            for (var p = camEntity; p != null; p = p.Parent)
                foreach (var c in p.Components)
                    if (c is Editor.ECS.Components.Scripting.Script sc && sc.IsEnabled) { sc.IsEnabled = false; paused.Add(sc); }
            bool playing = false, ok = true;
            try
            {
                camT.LocalPosition = new Vector3(0f, Height, 0f);
                camT.LocalRotation = new Vector3(0f, 0f, 0f);
                var cube = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (cube?.Transform == null) { log.LogError("scripting add component: no cube"); return false; }
                made.Add(cube);
                cube.Name = "SmokeStaticProp";
                cube.Transform.LocalPosition = new Vector3(0f, Height + Offset, Ahead);
                cube.Transform.LocalScale = new Vector3(Size, Size, Size);
                var mr = cube.GetComponent<MeshRenderer>();
                if (mr != null) { mr.ColorR = 1f; mr.ColorG = 0.05f; mr.ColorB = 0.05f; }
                cube.AddComponent(new BoxCollider(cube));   // a static collider only — no Rigidbody
                var bare = new GameEntity(scene, "SmokeBareEntity");
                scene.AddEntity(bare);
                made.Add(bare);
                bare.Transform.LocalPosition = new Vector3(0f, Height - Offset, Ahead);
                bare.Transform.LocalScale = new Vector3(Size, Size, Size);
                EditorViewportSession.RequestResubmit();

                EditorCommands.Play();
                playing = PlayModeService.Instance.State == PlayState.Playing;
                if (!playing) { log.LogError("scripting add component: play did not start"); return false; }
                await SmokeRegistry.Settle(300);
                var rt = ScriptRuntime.Instance;
                long cubeId = rt.HandleForEntity(cube), bareId = rt.HandleForEntity(bare);

                // the static prop is in the frame
                var before = await CaptureUntil("addcomp_before.bmp", f => Find(f, Red).count >= Seen, 8);
                var red = Find("addcomp_before.bmp", Red);
                var blueBefore = Find("addcomp_before.bmp", Blue);
                float y0 = cube.Transform.LocalPosition.Y;
                bool staticHangs = before.Met && !PhysicsService.HasRigidbody(cube) && Math.Abs(y0 - (Height + Offset)) < 0.01f;
                var eye = Editor.Core.Animation.BoneSocketService.EntityWorld(camEntity).Translation;
                log.Log("scripting add component: static prop at y=" + y0.ToString("0.00") + ", " + Describe(red, "red") + " (expected at least " + Seen
                    + ") after " + before.Frames + " frame(s); " + Describe(blueBefore, "blue") + " before the Mesh Renderer; camera at ("
                    + eye.X.ToString("0.00") + ", " + eye.Y.ToString("0.00") + ", " + eye.Z.ToString("0.00") + "), " + Editor.DllWrapper.VortexAPI.ViewFov.ToString("0") + "°");
                if (!staticHangs)
                {
                    log.LogError("scripting add component: the static prop is not hanging in the frame before AddRigidbody (" + Describe(red, "red") + ", expected at least "
                        + Seen + "; y=" + y0.ToString("0.00") + ", expected " + (Height + Offset).ToString("0.00") + "; Rigidbody " + PhysicsService.HasRigidbody(cube) + ", expected none)");
                    ok = false;
                }

                // a Mesh Renderer for the bare entity: blue appears
                bool added = Vortex.Scene.AddComponent(bareId, "MeshRenderer") && Vortex.Scene.SetMeshOf(bareId, "Primitive:Cube");
                Vortex.Scene.SetColorOf(bareId, 0.05f, 0.1f, 1f);
                var appeared = await CaptureUntil("addcomp_renderer.bmp", f => Find(f, Blue).count >= blueBefore.count + Seen, 8);
                var blue = Find("addcomp_renderer.bmp", Blue);
                log.Log("scripting add component: Mesh Renderer added " + added + ", " + Describe(blue, "blue") + " (expected at least " + (blueBefore.count + Seen)
                    + ") after " + appeared.Frames + " frame(s)");
                if (!added || !appeared.Met)
                {
                    log.LogError("scripting add component: a Mesh Renderer added while playing does not render (" + Describe(blue, "blue") + ", expected at least "
                        + (blueBefore.count + Seen) + "; added " + added + ")");
                    ok = false;
                }

                // the static prop becomes a dynamic body and falls out of the frame
                bool rb = Vortex.Physics.AddRigidbody(cubeId, 5f);
                float mass = Vortex.Physics.GetMass(cubeId);
                int gone = Math.Max(5, red.count / 10);
                var after = await CaptureUntil("addcomp_fell.bmp", f => cube.Transform.LocalPosition.Y < Height - 5f && Find(f, Red).count <= gone, 10);
                var redAfter = Find("addcomp_fell.bmp", Red);
                float y = cube.Transform.LocalPosition.Y;
                log.Log("scripting add component: AddRigidbody " + rb + " (mass " + mass.ToString("0.##") + "), now y=" + y.ToString("0.0") + ", "
                    + Describe(redAfter, "red") + " (expected at most " + gone + ") after " + after.Frames + " frame(s)");
                if (!rb || Math.Abs(mass - 5f) > 1e-3f) { log.LogError("scripting add component: Physics.AddRigidbody did not make a 5 kg body"); ok = false; }
                if (!after.Met)
                {
                    log.LogError("scripting add component: the prop did not fall out of the frame (y=" + y.ToString("0.0") + ", expected below " + (Height - 5f).ToString("0")
                        + "; " + Describe(redAfter, "red") + ", expected at most " + gone + ")");
                    ok = false;
                }
            }
            finally
            {
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(300); }
                foreach (var sc in paused) sc.IsEnabled = true;
                camT.LocalPosition = camPos; camT.LocalRotation = camRot;
                foreach (var e in made)
                {
                    if (e == null) continue;
                    if (e.GetComponent<Rigidbody>() != null || (e.Name == "SmokeBareEntity" && e.GetComponent<MeshRenderer>() != null))
                    { log.LogError("scripting add component: '" + e.Name + "' still has a runtime component after Stop"); ok = false; }
                }
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                EditorViewportSession.RequestResubmit();
            }
            return ok;
        }

        /// <summary>Capture frames until <paramref name="done"/> holds for the captured file, at most for
        /// <paramref name="seconds"/>: a slow renderer gets the frames it needs, a fast one is not held up.</summary>
        private static async Task<(bool Met, int Frames)> CaptureUntil(string file, Func<string, bool> done, double seconds)
        {
            var sw = Stopwatch.StartNew();
            int n = 0;
            do
            {
                await CameraSkySmoke.Sample(file, 0.5, 0.5);   // captures the next presented frame into the file
                n++;
                if (done(file)) return (true, n);
            }
            while (sw.Elapsed.TotalSeconds < seconds);
            return (false, n);
        }

        /// <summary>The colour in the middle of the frame, where the cubes are — away from the play view's overlay text (the FPS
        /// counter top right, the debug-camera hint bottom left, which is orange enough to count as red).</summary>
        private static (int count, double fx, double fy, int w, int h) Find(string file, Func<(int r, int g, int b), bool> match)
            => CameraSkySmoke.Find(file, match, 0.2, 0.1, 0.8, 0.9);

        /// <summary>A colour's samples in a frame, for the log: how many and where — or that there is none.</summary>
        private static string Describe((int count, double fx, double fy, int w, int h) f, string name)
            => f.count == 0 ? "no " + name + " in the middle of the " + f.w + " × " + f.h + " frame"
               : f.count + " " + name + " samples around (" + f.fx.ToString("0.00") + ", " + f.fy.ToString("0.00") + ") of the " + f.w + " × " + f.h + " frame";
    }
}
