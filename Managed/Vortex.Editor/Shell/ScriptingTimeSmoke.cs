using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Physics;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>The global time scale in editor play (#425), on the rendered picture: a red dynamic cube hangs 4 m in front of
    /// the game camera, high above the scene. At Time.TimeScale 0 it stays in the middle of the frame, at 0.25 it falls a
    /// quarter as fast as at 1 (measured on its body), at 1 it drops out of the frame; the play menu's test multiplier
    /// multiplies in without the game seeing it. Skipped on an engine build without Jolt.</summary>
    internal static class ScriptingTimeSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("scripting time scale", Run);

        private const float Height = 600f;

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("scripting time scale: no scene — skipped"); return true; }
            if (!PhysicsService.Available) { log.Log("scripting time scale: no Jolt world in this engine build — skipped"); return true; }
            var camEntity = PlayCameraHelper.FindMainCameraEntity(scene);
            if (camEntity?.Transform == null) { log.Log("scripting time scale: the scene has no main camera — skipped"); return true; }

            var camT = camEntity.Transform;
            var camPos = camT.LocalPosition; var camRot = camT.LocalRotation;
            var made = new List<GameEntity>();
            bool playing = false, ok = true;
            float oldEditorScale = GameTime.EditorScale;
            try
            {
                GameTime.EditorScale = 1f;
                // the game camera high above everything, looking along +Z; the cube 4 m ahead of it, falling freely
                camT.LocalPosition = new Vector3(0f, Height, 0f);
                camT.LocalRotation = new Vector3(0f, 0f, 0f);
                var cube = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (cube?.Transform == null) { log.LogError("scripting time scale: no cube"); return false; }
                made.Add(cube);
                cube.Name = "SmokeTimeCube";
                cube.Transform.LocalPosition = new Vector3(0f, Height, 4f);
                var mr = cube.GetComponent<MeshRenderer>();
                if (mr != null) { mr.ColorR = 1f; mr.ColorG = 0.05f; mr.ColorB = 0.05f; }
                cube.AddComponent(new BoxCollider(cube));
                cube.AddComponent(new Rigidbody(cube) { Mass = 1f, UseGravity = true });
                EditorViewportSession.RequestResubmit();

                EditorCommands.Play();
                playing = PlayModeService.Instance.State == PlayState.Playing;
                if (!playing) { log.LogError("scripting time scale: play did not start"); return false; }
                Vortex.Time.TimeScale = 0f;   // before the first tick: the world starts frozen
                await SmokeRegistry.Settle(800);
                if (!PhysicsService.HasRigidbody(cube)) { log.LogError("scripting time scale: the cube is not a dynamic body"); return false; }

                // 1) time scale 0: the cube hangs where it was, in the middle of the frame
                float y0 = cube.Transform.LocalPosition.Y;
                var frozen = await CameraSkySmoke.Sample("timescale_frozen.bmp", 0.5, 0.5);
                float yFrozen = cube.Transform.LocalPosition.Y;
                bool held = Math.Abs(yFrozen - Height) < 0.01f && Vortex.Time.Elapsed < 0.05f;
                bool redFrozen = frozen.r > 120 && frozen.r > frozen.g + 60 && frozen.r > frozen.b + 60;
                log.Log("scripting time scale: at 0 the cube is at y=" + yFrozen.ToString("0.000") + " (game time " + Vortex.Time.Elapsed.ToString("0.000")
                    + " s, real " + Vortex.Time.UnscaledElapsed.ToString("0.00") + " s), centre " + CameraSkySmoke.Rgb(frozen));
                if (!held) { log.LogError("scripting time scale: the cube fell (or game time ran) at Time.TimeScale 0"); ok = false; }
                if (!redFrozen) { log.LogError("scripting time scale: the frozen cube is not in the middle of the frame"); ok = false; }

                // 2) time scale 0.25: in about a real second the body falls as far as in a quarter second of game time
                Vortex.Time.TimeScale = 0.25f;
                double g0 = Vortex.Time.Elapsed, r0 = Vortex.Time.UnscaledElapsed;
                await Task.Delay(1000);
                double game = Vortex.Time.Elapsed - g0, real = Vortex.Time.UnscaledElapsed - r0;
                float fall = Height - cube.Transform.LocalPosition.Y;
                float expected = 0.5f * 9.81f * (float)(game * game);
                log.Log("scripting time scale: at 0.25 " + real.ToString("0.00") + " s real = " + game.ToString("0.000") + " s game, the cube fell "
                    + fall.ToString("0.000") + " m (free fall in that game time: " + expected.ToString("0.000") + " m)");
                if (Math.Abs(game - real * 0.25) > 0.02) { log.LogError("scripting time scale: game time did not run at a quarter of real time"); ok = false; }
                if (fall < expected * 0.6f || fall > expected * 1.4f + 0.05f) { log.LogError("scripting time scale: the body's fall does not follow game time"); ok = false; }

                // 3) the play menu's test multiplier: the game keeps reading its own scale, the world runs at the product
                GameTime.EditorScale = 0.5f;
                bool editorScale = Math.Abs(GameTime.EffectiveScale - 0.125f) < 1e-4f && Math.Abs(Vortex.Time.TimeScale - 0.25f) < 1e-4f;
                GameTime.EditorScale = 1f;
                if (!editorScale) { log.LogError("scripting time scale: the editor's test multiplier does not combine with the game's scale"); ok = false; }

                // 4) time scale 1: the cube drops out of the frame
                Vortex.Time.TimeScale = 1f;
                await Task.Delay(1500);
                var gone = await CameraSkySmoke.Sample("timescale_running.bmp", 0.5, 0.5);
                bool fell = cube.Transform.LocalPosition.Y < Height - 5f && !(gone.r > 120 && gone.r > gone.g + 60 && gone.r > gone.b + 60);
                log.Log("scripting time scale: at 1 the cube is at y=" + cube.Transform.LocalPosition.Y.ToString("0.0") + ", centre " + CameraSkySmoke.Rgb(gone));
                if (!fell) { log.LogError("scripting time scale: at time scale 1 the cube did not fall out of the frame"); ok = false; }
            }
            finally
            {
                GameTime.EditorScale = oldEditorScale;
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(300); }
                camT.LocalPosition = camPos; camT.LocalRotation = camRot;
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                EditorViewportSession.RequestResubmit();
            }
            return ok;
        }
    }
}
