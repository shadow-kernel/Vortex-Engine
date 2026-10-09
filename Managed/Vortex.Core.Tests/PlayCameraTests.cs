using System;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Rendering;

namespace VortexTests
{
    /// <summary>The play camera renders from its WORLD transform (#327): a camera under a rig composes the parent chain,
    /// an unparented camera keeps its local values exactly.</summary>
    public static class PlayCameraTests
    {
        private static GameEntity Entity(string name, Vector3 pos, Vector3 rot, GameEntity parent = null)
        {
            var e = new GameEntity { Name = name };
            var t = new Transform { Entity = e }; e.Components.Add(t);
            t.LocalPosition = pos; t.LocalRotation = rot;
            if (parent != null) parent.AddChild(e);
            return e;
        }

        private static bool Near(float a, float b, float eps = 0.01f) => Math.Abs(a - b) <= eps;

        [Test]
        public static void AnUnparentedCameraKeepsItsLocalValues(TestContext t)
        {
            var cam = Entity("Camera", new Vector3(1, 2, 3), new Vector3(-10, 45, 5));
            cam.Components.Add(new Camera(cam) { IsMainCamera = true, FieldOfView = 70, NearClip = 0.3f, FarClip = 2500 });
            var scene = new Scene { Name = "s" }; scene.Entities.Add(cam);

            t.True(PlayCameraHelper.TryGetMainCameraWorld(scene, out var pos, out var rot, out var c), "a main camera is found");
            t.True(pos.X == 1 && pos.Y == 2 && pos.Z == 3, "position unchanged");
            t.True(rot.X == -10 && rot.Y == 45 && rot.Z == 5, "rotation unchanged");
            t.True(c != null && c.FarClip == 2500, "the component comes along (its clip planes drive the view)");

            PlayCameraHelper.WorldPose(cam, out var wpos, out var wrot);
            t.True(Near(wpos.X, 1) && Near(wpos.Y, 2) && Near(wpos.Z, 3), "the matrix path agrees on the position");
            t.True(Near(wrot.X, -10) && Near(wrot.Y, 45) && Near(wrot.Z, 5), "…and inverts the Euler composition: " + wrot.X + "," + wrot.Y + "," + wrot.Z);
        }

        [Test]
        public static void ACameraUnderARigRendersFromItsWorldTransform(TestContext t)
        {
            var rig = Entity("Rig", new Vector3(10, 0, -4), new Vector3(0, 90, 0));
            var head = Entity("Head", new Vector3(0, 1.7f, 0), new Vector3(0, 0, 0), rig);
            var cam = Entity("Camera", new Vector3(0, 0, 0.1f), new Vector3(15, 0, 0), head);
            cam.Components.Add(new Camera(cam) { IsMainCamera = true });
            var scene = new Scene { Name = "s" }; scene.Entities.Add(rig);

            t.True(PlayCameraHelper.TryGetMainCameraWorld(scene, out var pos, out var rot, out _), "found through the hierarchy");
            // yaw 90 turns the rig's +Z onto world +X: the camera's 0.1 m forward offset lands on +X
            t.True(Near(pos.X, 10.1f) && Near(pos.Y, 1.7f) && Near(pos.Z, -4f), "world position " + pos.X + "," + pos.Y + "," + pos.Z);
            t.True(Near(rot.Y, 90) && Near(rot.X, 15) && Near(rot.Z, 0), "world rotation " + rot.X + "," + rot.Y + "," + rot.Z);
            t.False(ReferenceEquals(PlayCameraHelper.FindMainCamera(scene), rig.Transform), "the camera's transform, not the rig's");
        }

        [Test]
        public static void NoMainCameraNoView(TestContext t)
        {
            var e = Entity("Thing", new Vector3(1, 1, 1), new Vector3(0, 0, 0));
            e.Components.Add(new Camera(e) { IsMainCamera = false });
            var scene = new Scene { Name = "s" }; scene.Entities.Add(e);
            t.False(PlayCameraHelper.TryGetMainCameraWorld(scene, out _, out _, out _), "a non-main camera does not count");
            t.False(PlayCameraHelper.TryGetMainCameraWorld(null, out _, out _, out _), "no scene");
        }
    }
}
