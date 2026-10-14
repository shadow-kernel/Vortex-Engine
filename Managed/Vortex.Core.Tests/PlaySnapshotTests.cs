using System;
using System.Linq;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>Play is non-destructive (#318, #345): the snapshot taken at Play puts every serialized property, the
    /// component set and the hierarchy back on Stop — not just lights and transforms. A script's quit stops play only
    /// after the tick (#410).</summary>
    public static class PlaySnapshotTests
    {
        private static GameEntity Entity(string name, GameEntity parent = null)
        {
            var e = new GameEntity { Name = name };
            e.Components.Add(new Transform { Entity = e });
            if (parent != null) parent.AddChild(e);
            return e;
        }

        [Test]
        public static void ComponentStateComesBack(TestContext t)
        {
            var room = Entity("Room");
            var lamp = Entity("Lamp", room);
            var light = new Light(lamp) { Intensity = 3f, Range = 10f, IsEnabled = true }; lamp.Components.Add(light);
            var mr = new MeshRenderer(room, "Primitive:Cube") { IsEnabled = true, MaterialPath = "Assets/Materials/a.vmat" }; room.Components.Add(mr);
            var col = new BoxCollider(room) { IsEnabled = true }; room.Components.Add(col);
            var scene = new Scene { Name = "s" }; scene.Entities.Add(room);

            var snap = PlaySnapshot.Take(scene);
            // what scripts and the tools do while playing
            light.Intensity = 9f; light.IsEnabled = false;
            mr.IsEnabled = false; mr.MaterialPath = "Assets/Materials/b.vmat";
            col.IsEnabled = false;
            room.Name = "Renamed"; room.Tag = "Changed"; lamp.IsActive = false;
            snap.Restore();

            t.True(light.Intensity == 3f && light.IsEnabled, "the light is back");
            t.True(mr.IsEnabled && mr.MaterialPath == "Assets/Materials/a.vmat", "renderer enabled + material back");
            t.True(col.IsEnabled, "collider enabled again (#318)");
            t.True(room.Name == "Room" && room.Tag != "Changed", "entity name/tag back (#345)");
            t.True(lamp.IsActive, "entity active again");
        }

        [Test]
        public static void ComponentsAndHierarchyComeBack(TestContext t)
        {
            var root = Entity("Root");
            var a = Entity("A", root);
            var b = Entity("B", root);
            var audioLike = new Light(a); a.Components.Add(audioLike);
            var scene = new Scene { Name = "s" }; scene.Entities.Add(root);

            var snap = PlaySnapshot.Take(scene);
            a.Components.Remove(audioLike);                  // a component removed during play
            var added = new BoxCollider(b); b.Components.Add(added);   // one added
            root.Children.Remove(b);                         // an authored entity destroyed
            var spawned = Entity("Spawned", root);           // a runtime spawn (the runtime removes it itself)
            snap.Restore();

            t.True(a.Components.Contains(audioLike), "the removed component is back");
            t.False(b.Components.Contains(added), "the component added during play is gone");
            t.True(root.Children.Contains(b) && root.Children.IndexOf(b) == 1, "the destroyed entity is back in its slot");
            t.True(root.Children.Contains(spawned), "a spawn the runtime owns is left alone");
            t.Equal(1, a.Components.Count(c => c is Light), "no duplicates");
        }

        [Test]
        public static void OnlyWhatChangedIsWritten(TestContext t)
        {
            var e = Entity("E");
            var light = new Light(e) { Intensity = 2f }; e.Components.Add(light);
            var scene = new Scene { Name = "s" }; scene.Entities.Add(e);
            var snap = PlaySnapshot.Take(scene);
            int writes = 0;
            light.PropertyChanged += (s, a) => writes++;
            snap.Restore();
            t.Equal(0, writes, "an unchanged component is not touched on Stop");
        }

        // ------------------------------------------------------------------ #410: a script's quit waits for the tick

        [Test]
        public static void ScriptQuitStopsPlayOnlyAfterTheTick(TestContext t)
        {
            var pms = PlayModeService.Instance;
            var runtime = ScriptRuntime.Instance;
            var host = Vortex.Application.Host;
            int stops = 0;
            EventHandler<PlayState> onState = (s, st) => { if (st == PlayState.Editing) stops++; };
            pms.StateChanged += onState;
            try
            {
                pms.Play();
                Vortex.Application.Host = runtime;
                Vortex.Application.Quit();   // what a behaviour's Update, a coroutine or a UI button calls
                t.Equal(PlayState.Playing, pms.State, "Application.Quit inside the tick does not stop play there (#410)");
                t.Equal(0, stops, "no Stop (and no snapshot restore) runs in the middle of the tick");
                t.True(runtime.ConsumeQuitRequest(), "the host sees the request after the tick and stops play");
                t.False(runtime.ConsumeQuitRequest(), "once");
                pms.Stop();
                Vortex.Application.Quit();   // OnDestroy at Stop: play is over, nothing is asked for the next session
                t.False(runtime.ConsumeQuitRequest(), "a quit outside play is not kept");
            }
            finally
            {
                pms.StateChanged -= onState;
                Vortex.Application.Host = host;
                if (pms.State != PlayState.Editing) pms.Stop();
                runtime.ConsumeQuitRequest();
            }
        }
    }
}
