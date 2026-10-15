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
using Editor.Scripting;

namespace VortexEditor.Shell
{
    /// <summary>Components added while playing (#421), on the rendered picture: right of the game camera's view a red cube that
    /// is only a static BoxCollider hangs in the air; Physics.AddRigidbody(cube, 5) makes it a dynamic body that falls out of
    /// the frame. Left of it a bare entity gets a Mesh Renderer (Scene.AddComponent + SetMeshOf) and appears blue the next
    /// frames. After Stop neither runtime component is left. Skipped on an engine build without Jolt.</summary>
    internal static class ScriptingComponentSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("scripting add component", Run);

        private const float Height = 620f;

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
            bool playing = false, ok = true;
            try
            {
                camT.LocalPosition = new Vector3(0f, Height, 0f);
                camT.LocalRotation = new Vector3(0f, 0f, 0f);
                var cube = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (cube?.Transform == null) { log.LogError("scripting add component: no cube"); return false; }
                made.Add(cube);
                cube.Name = "SmokeStaticProp";
                cube.Transform.LocalPosition = new Vector3(0.7f, Height, 5f);   // 0.8 m cubes: the front faces cover x 0.4 / 0.6 of a landscape and a portrait view
                cube.Transform.LocalScale = new Vector3(0.8f, 0.8f, 0.8f);
                var mr = cube.GetComponent<MeshRenderer>();
                if (mr != null) { mr.ColorR = 1f; mr.ColorG = 0.05f; mr.ColorB = 0.05f; }
                cube.AddComponent(new BoxCollider(cube));   // a static collider only — no Rigidbody
                var bare = new GameEntity(scene, "SmokeBareEntity");
                scene.AddEntity(bare);
                made.Add(bare);
                bare.Transform.LocalPosition = new Vector3(-0.7f, Height, 5f);
                bare.Transform.LocalScale = new Vector3(0.8f, 0.8f, 0.8f);
                EditorViewportSession.RequestResubmit();

                EditorCommands.Play();
                playing = PlayModeService.Instance.State == PlayState.Playing;
                if (!playing) { log.LogError("scripting add component: play did not start"); return false; }
                await SmokeRegistry.Settle(700);
                var rt = ScriptRuntime.Instance;
                long cubeId = rt.HandleForEntity(cube), bareId = rt.HandleForEntity(bare);

                var before = await CameraSkySmoke.Sample("addcomp_before.bmp", 0.6, 0.5);
                var bareBefore = await CameraSkySmoke.Sample("addcomp_before.bmp", 0.4, 0.5, capture: false);
                bool staticHangs = Red(before) && !PhysicsService.HasRigidbody(cube) && Math.Abs(cube.Transform.LocalPosition.Y - Height) < 0.01f;
                log.Log("scripting add component: static prop " + CameraSkySmoke.Rgb(before) + " at y=" + cube.Transform.LocalPosition.Y.ToString("0.00")
                    + ", bare entity's spot " + CameraSkySmoke.Rgb(bareBefore));
                if (!staticHangs) { log.LogError("scripting add component: the static prop is not hanging in the frame before AddRigidbody"); ok = false; }

                // a Mesh Renderer for the bare entity
                bool added = Vortex.Scene.AddComponent(bareId, "MeshRenderer") && Vortex.Scene.SetMeshOf(bareId, "Primitive:Cube");
                Vortex.Scene.SetColorOf(bareId, 0.05f, 0.1f, 1f);
                var appeared = await CameraSkySmoke.Sample("addcomp_renderer.bmp", 0.4, 0.5);
                log.Log("scripting add component: Mesh Renderer added " + added + ", its spot now " + CameraSkySmoke.Rgb(appeared));
                if (!added || !Blue(appeared) || Blue(bareBefore)) { log.LogError("scripting add component: a Mesh Renderer added while playing does not render"); ok = false; }

                // the static prop becomes a dynamic body and falls out of the frame
                bool rb = Vortex.Physics.AddRigidbody(cubeId, 5f);
                float mass = Vortex.Physics.GetMass(cubeId);
                await Task.Delay(1500);
                var after = await CameraSkySmoke.Sample("addcomp_fell.bmp", 0.6, 0.5);
                float y = cube.Transform.LocalPosition.Y;
                log.Log("scripting add component: AddRigidbody " + rb + " (mass " + mass.ToString("0.##") + "), now y=" + y.ToString("0.0") + ", its spot " + CameraSkySmoke.Rgb(after));
                if (!rb || Math.Abs(mass - 5f) > 1e-3f) { log.LogError("scripting add component: Physics.AddRigidbody did not make a 5 kg body"); ok = false; }
                if (y > Height - 5f || Red(after)) { log.LogError("scripting add component: the prop did not fall out of the frame"); ok = false; }
            }
            finally
            {
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(300); }
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
    }
}
