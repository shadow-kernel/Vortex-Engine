using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Animation;
using Vector3 = Editor.ECS.Vector3;

namespace VortexEditor.Shell
{
    /// <summary>The animators' level of detail in editor play (#361), with the real game camera and play view: of two animated
    /// rigs, the one 30 m ahead of the camera poses every frame, the one 40 m behind it keeps its clock but skips its pose
    /// (the camera looks towards the sun, so its shadow falls out of view too); moved in front of the camera it poses again
    /// from the next frame; with a split view showing the play world from another camera both pose; Animation.SetLod(false)
    /// poses both.</summary>
    internal static class ScriptingAnimationLodSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("scripting animation lod", Run);

        private const float Height = 660f;

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("scripting animation lod: no scene — skipped"); return true; }
            var camEntity = PlayCameraHelper.FindMainCameraEntity(scene);
            if (camEntity?.Transform == null) { log.Log("scripting animation lod: the scene has no main camera — skipped"); return true; }
            var camT = camEntity.Transform;
            var camPos = camT.LocalPosition; var camRot = camT.LocalRotation;
            var viewport = EditorCommands.Window?.ViewportPanel;
            int layout = viewport?.Layout ?? 1;
            var made = new List<GameEntity>();
            bool playing = false, ok = true;
            try
            {
                viewport?.SetLayout(1);   // a split view renders the play world from other cameras: the level of detail poses all then
                // the camera looks towards the sun (when it throws shadows): the shadow of the rig behind it falls further behind,
                // out of view — the level of detail keeps a character posed whose shadow it can see
                float yaw = 0f;
                var sun = Editor.DllWrapper.VortexAPI.LastSun;
                if (sun != null && sun.ShadowsVisible && Math.Abs(sun.DirX) + Math.Abs(sun.DirZ) > 0.01f)
                    yaw = (float)(Math.Atan2(-sun.DirX, -sun.DirZ) * 180.0 / Math.PI);
                float fx = (float)Math.Sin(yaw * Math.PI / 180.0), fz = (float)Math.Cos(yaw * Math.PI / 180.0);
                Vector3 At(float along, float side = 0f) => new Vector3(fx * along + fz * side, Height, fz * along - fx * side);
                camT.LocalPosition = new Vector3(0f, Height, 0f);
                camT.LocalRotation = new Vector3(0f, yaw, 0f);
                GameEntity Rig(string name, float along)
                {
                    var e = new GameEntity(scene, name);
                    scene.AddEntity(e);
                    e.Transform.LocalPosition = At(along);
                    e.AddComponent(new Animator(e) { PlayOnStart = false });
                    made.Add(e);
                    return e;
                }
                var ahead = Rig("SmokeLodAhead", 30f);
                var behind = Rig("SmokeLodBehind", -40f);
                EditorViewportSession.RequestResubmit();

                EditorCommands.Play();
                playing = PlayModeService.Instance.State == PlayState.Playing;
                if (!playing) { log.LogError("scripting animation lod: play did not start"); return false; }
                await SmokeRegistry.Settle(400);
                var svc = AnimationService.Instance;
                var clip = new VortexAnimClip { Name = "sway", DurationSec = 4f, Loop = true };
                var track = new AnimTrack { Bone = "Hips" };
                for (int k = 0; k <= 4; k++)
                {
                    var q = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, k * 0.4f);
                    track.Rot.Add(new AnimKeyQuat { T = k, X = q.X, Y = q.Y, Z = q.Z, W = q.W });
                }
                clip.Tracks.Add(track);
                svc.PlayWithRig(ahead, Rig2(), clip);
                svc.PlayWithRig(behind, Rig2(), clip);
                await Task.Delay(600);   // the split view's last frame ages out too (it asks for every pose for half a second)

                // a few frames: the one ahead gets new poses, the one behind keeps its pose while its clock runs
                var aheadBefore = svc.CurrentNodeWorlds(ahead); var behindBefore = svc.CurrentNodeWorlds(behind);
                float clockBefore = svc.GetTime(behind);
                await Task.Delay(500);
                bool aheadPosed = !ReferenceEquals(aheadBefore, svc.CurrentNodeWorlds(ahead));
                bool behindHeld = ReferenceEquals(behindBefore, svc.CurrentNodeWorlds(behind));
                float clockAfter = svc.GetTime(behind);
                var sunNow = Editor.DllWrapper.VortexAPI.LastSun;
                log.Log("scripting animation lod: ahead posed " + aheadPosed + ", behind held its pose " + behindHeld + " while its clock ran "
                    + clockBefore.ToString("0.00") + " -> " + clockAfter.ToString("0.00") + " s; last frame " + Vortex.Animation.PosesEvaluated
                    + " poses evaluated, " + Vortex.Animation.PosesSkipped + " skipped (camera yaw " + yaw.ToString("0") + "°, sun "
                    + (sunNow == null ? "none" : "shadows " + sunNow.ShadowsVisible + " to " + sunNow.ShadowDistance + " m") + ")");
                if (!aheadPosed) { log.LogError("scripting animation lod: the animator in view did not pose"); ok = false; }
                if (!behindHeld) { log.LogError("scripting animation lod: the animator behind the camera was posed (the level of detail did not see the game camera)"); ok = false; }
                if (clockAfter <= clockBefore + 0.2f) { log.LogError("scripting animation lod: the skipped animator's clock stood still"); ok = false; }

                // a split view shows the play world from another camera: the one behind poses while it does
                if (viewport != null)
                {
                    viewport.SetLayout(2);
                    await Task.Delay(400);
                    var split = svc.CurrentNodeWorlds(behind);
                    await Task.Delay(300);
                    bool splitPosed = !ReferenceEquals(split, svc.CurrentNodeWorlds(behind));
                    viewport.SetLayout(1);
                    await Task.Delay(700);
                    var single = svc.CurrentNodeWorlds(behind);
                    await Task.Delay(300);
                    bool singleHeld = ReferenceEquals(single, svc.CurrentNodeWorlds(behind));
                    log.Log("scripting animation lod: with a split view the one behind poses " + splitPosed + ", back to one view it waits again " + singleHeld);
                    if (!splitPosed) { log.LogError("scripting animation lod: a split view showed the play world, but the animator behind the game camera kept a frozen pose"); ok = false; }
                    if (!singleHeld) { log.LogError("scripting animation lod: after the split view closed, the animator behind the camera still posed"); ok = false; }
                }

                // brought into view: it poses again
                behind.Transform.LocalPosition = At(25f, 2f);
                var held = svc.CurrentNodeWorlds(behind);
                await Task.Delay(250);
                bool resumed = !ReferenceEquals(held, svc.CurrentNodeWorlds(behind));
                log.Log("scripting animation lod: moved into view, posing again " + resumed);
                if (!resumed) { log.LogError("scripting animation lod: an animator that came into view did not pose again"); ok = false; }

                // switched off: both pose behind the camera too
                behind.Transform.LocalPosition = At(-40f);
                Vortex.Animation.SetLod(false);
                await Task.Delay(150);
                var off = svc.CurrentNodeWorlds(behind);
                await Task.Delay(250);
                bool offPosed = !ReferenceEquals(off, svc.CurrentNodeWorlds(behind));
                if (!offPosed) { log.LogError("scripting animation lod: Animation.SetLod(false) still skipped the pose"); ok = false; }
            }
            finally
            {
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(300); }
                camT.LocalPosition = camPos; camT.LocalRotation = camRot;
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                viewport?.SetLayout(layout);
                EditorViewportSession.RequestResubmit();
            }
            return ok;
        }

        private static SkeletonDef Rig2()
        {
            var root = new SkeletonNode { Name = "Root", Parent = -1, LocalBind = System.Numerics.Matrix4x4.Identity };
            var hips = new SkeletonNode { Name = "Hips", Parent = 0, BindTranslation = new System.Numerics.Vector3(0, 1f, 0), LocalBind = System.Numerics.Matrix4x4.CreateTranslation(0, 1f, 0) };
            return new SkeletonDef { Nodes = new[] { root, hips }, Bones = new[] { new SkeletonBone { NodeIndex = 1 } } };
        }
    }
}
