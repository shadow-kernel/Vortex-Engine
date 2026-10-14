using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using Editor.Editors.WorldEditor.Components.SceneHierarchy;
using Editor.Scripting;
using Editor.UI.Vui;

namespace VortexTests
{
    /// <summary>Play is non-destructive (#318, #345): the snapshot taken at Play puts every serialized property, the
    /// component set and the hierarchy back on Stop — not just lights and transforms. Stop gives back exactly the edit
    /// scene of Play: added entities go, re-parented ones return (#423), values edited in place come back (#478), so do
    /// the environment settings (#479); a script's quit stops play only after the tick (#410), and no retained-UI screen
    /// outlives the session (#477).</summary>
    public static class PlaySnapshotTests
    {
        private static GameEntity Entity(string name, GameEntity parent = null)
        {
            var e = new GameEntity { Name = name };
            e.Components.Add(new Transform { Entity = e });
            if (parent != null) parent.AddChild(e);
            return e;
        }

        /// <summary>A scene holding these roots; every entity knows its scene, as after loading.</summary>
        private static Scene SceneOf(params GameEntity[] roots)
        {
            var scene = new Scene { Name = "s" };
            void Own(GameEntity e) { e.Scene = scene; foreach (var c in e.Children) Own(c); }
            foreach (var r in roots) { scene.Entities.Add(r); Own(r); }
            return scene;
        }

        /// <summary>Every place an entity is listed in the scene (an entity listed twice shows up twice).</summary>
        private static List<GameEntity> Listed(Scene scene)
        {
            var all = new List<GameEntity>();
            void Walk(GameEntity e) { all.Add(e); foreach (var c in e.Children) Walk(c); }
            foreach (var r in scene.Entities) Walk(r);
            return all;
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
            var spawned = Entity("Spawned", root);           // a spawn no ledger knows of (#423: it goes too)
            snap.Restore();

            t.True(a.Components.Contains(audioLike), "the removed component is back");
            t.False(b.Components.Contains(added), "the component added during play is gone");
            t.True(root.Children.Contains(b) && root.Children.IndexOf(b) == 1, "the destroyed entity is back in its slot");
            t.False(root.Children.Contains(spawned), "an entity added during play is gone (#423)");
            t.Equal(2, root.Children.Count, "exactly the authored children");
            t.Equal(1, a.Components.Count(c => c is Light), "no duplicates");
        }

        [Test]
        public static void OnlyWhatChangedIsWritten(TestContext t)
        {
            var e = Entity("E");
            var light = new Light(e) { Intensity = 2f }; e.Components.Add(light);
            var script = new Script(e, "Assets/Scripts/Spinner.cs"); script.SetFieldValue("Speed", "45"); e.Components.Add(script);
            var scene = new Scene { Name = "s" }; scene.Entities.Add(e);
            var snap = PlaySnapshot.Take(scene);
            int writes = 0;
            light.PropertyChanged += (s, a) => writes++;
            var fields = script.FieldValues;
            snap.Restore();
            t.Equal(0, writes, "an unchanged component is not touched on Stop");
            t.True(ReferenceEquals(fields, script.FieldValues), "an unchanged field list is compared by value, not replaced");
        }

        // ------------------------------------------------------------------ #423: the hierarchy comes back exactly

        [Test]
        public static void EntitiesAddedWhilePlayingGo(TestContext t)
        {
            var room = Entity("Room");
            var lamp = Entity("Lamp", room);
            var crate = Entity("Crate", room);
            var floor = Entity("Floor");
            var scene = SceneOf(room, floor);

            var snap = PlaySnapshot.Take(scene);
            // what create_entity / place_asset / duplicate_entities (a root), a child created in the hierarchy, and
            // place_grid / scatter (a group with copies inside) do while playing
            var leak = scene.CreateEntity("PlayLeak");
            var probe = Entity("Probe", lamp);
            var grid = Entity("Grid"); grid.Scene = scene; scene.Entities.Insert(0, grid);
            var tile = Entity("Tile 1", grid);
            new MoveEntityCommand(floor, grid).Execute();   // parent_entity(Floor, Grid): an authored entity into an added one
            var loot = Entity("Loot", crate);               // added under an authored entity …
            room.Children.Remove(crate);                    // … that is destroyed afterwards
            snap.Restore();

            t.Equal(2, scene.Entities.Count, "only the authored roots are left (create_entity / place_asset leaked before)");
            t.True(scene.Entities[0] == room && scene.Entities[1] == floor, "the authored roots in their order");
            t.Equal(0, lamp.Children.Count, "the child added under an authored entity is gone");
            t.True(floor.Parent == null && !grid.Children.Contains(floor), "the authored entity put into the added group is back at the root, out of the group");
            t.True(room.Children.Count == 2 && room.Children[1] == crate && crate.Children.Count == 0, "the destroyed crate is back — without what was added under it");
            t.Equal(4, Listed(scene).Count, "Room, Lamp, Crate and Floor — nothing else");
            t.True(snap.Removed.Contains(leak) && snap.Removed.Contains(probe) && snap.Removed.Contains(grid) && snap.Removed.Contains(loot), "the removed entities are reported");
            t.False(snap.Removed.Contains(tile), "a copy inside the removed group goes with it");
            t.True(leak.Scene == null && leak.Parent == null && probe.Parent == null && grid.Scene == null && tile.Scene == null,
                "a removed entity is out of every scene — a stale selection of it can no longer add a copy to one");
            t.Equal(2, snap.PutBack, "the moved floor and the destroyed crate were put back");
        }

        [Test]
        public static void EveryLoadedSceneComesBack(TestContext t)
        {
            var hall = Entity("Hall");
            var played = SceneOf(hall);
            var door = Entity("Door");
            var lobby = SceneOf(door);
            var later = new Scene { Name = "later" };
            float fog = lobby.Settings.FogDensity;

            var snap = PlaySnapshot.Take(new[] { played, lobby });
            t.True(snap.Covers(played) && snap.Covers(lobby) && !snap.Covers(later), "Stop covers the scenes loaded at Play");
            // create_entity(parent: an entity of the other loaded scene), a re-parent across the scenes, its environment
            var sign = Entity("Sign", door); sign.Scene = lobby;
            new MoveEntityCommand(hall, door).Execute();
            lobby.Settings.FogDensity = fog + 1f;
            snap.Restore();

            t.Equal(0, door.Children.Count, "the entity made under the other scene's door is gone, the hall left it");
            t.True(played.Entities.Count == 1 && played.Entities[0] == hall && hall.Scene == played && hall.Parent == null, "the hall is back in its own scene");
            t.True(snap.Removed.Contains(sign) && lobby.Settings.FogDensity == fog && snap.SettingsReverted.Contains(lobby), "the other scene is put back too");
        }

        [Test]
        public static void ReparentedEntitiesGoBack(TestContext t)
        {
            var p1 = Entity("P1");
            var a0 = Entity("A0", p1);
            var a = Entity("A", p1);
            var a2 = Entity("A2", p1);
            var b = Entity("B");
            var c = Entity("C", b);
            var scene = SceneOf(p1, b);
            var other = new Scene { Name = "other" };
            var otherRoot = Entity("Other"); otherRoot.Scene = other; other.Entities.Add(otherRoot);

            var snap = PlaySnapshot.Take(scene);
            new MoveEntityCommand(a, b).Execute();                // parent_entity(A, B)
            new MoveEntityCommand(c, null).Execute();             // unparent_entity(C)
            p1.Children.Move(0, 1);                               // a reorder
            new MoveEntityToSceneCommand(a2, other).Execute();    // dragged into another loaded scene
            snap.Restore();

            t.Equal(3, p1.Children.Count, "P1 has its three children again");
            t.True(p1.Children[0] == a0 && p1.Children[1] == a && p1.Children[2] == a2, "each in its old slot");
            t.True(a.Parent == p1 && a2.Parent == p1 && a2.Scene == scene, "parent and scene back");
            t.True(b.Children.Count == 1 && b.Children[0] == c && c.Parent == b, "A left B, C is under B again");
            t.True(scene.Entities.Count == 2 && scene.Entities[0] == p1 && scene.Entities[1] == b, "C is no longer a root");
            t.True(other.Entities.Count == 1 && other.Entities[0] == otherRoot, "the other scene keeps only its own entity");
            var listed = Listed(scene);
            t.Equal(listed.Count, listed.Distinct().Count(), "no entity is listed twice (#423: a re-parented one was)");
            t.Equal(0, snap.Removed.Count, "nothing authored is removed");
        }

        [Test]
        public static void AnUnchangedHierarchyIsNotTouched(TestContext t)
        {
            var root = Entity("Root");
            var a = Entity("A", root);
            Entity("A1", a);
            var b = Entity("B");
            var scene = SceneOf(root, b);
            var snap = PlaySnapshot.Take(scene);
            int changes = 0;
            scene.Entities.CollectionChanged += (s, e) => changes++;
            foreach (var e in Listed(scene)) e.Children.CollectionChanged += (s, ev) => changes++;
            snap.Restore();
            t.Equal(0, changes, "a Stop without structural changes sends no collection change to the hierarchy");
        }

        // ------------------------------------------------------------------ #478: values changed in place

        [Test]
        public static void ValuesChangedInPlaceComeBack(TestContext t)
        {
            var cam = Entity("Camera");
            var script = new Script(cam, "Assets/Scripts/ThirdPersonCamera.cs"); cam.Components.Add(script);
            script.SetFieldValue("DebugYaw", "-999");
            script.SetFieldValue("DebugPitch", "-999");
            var animator = new Animator(cam); animator.Clips.Add(new AnimatorClipEntry { Name = "Walk", Path = "Assets/Animations/Walk.vanim" }); cam.Components.Add(animator);
            var col = new BoxCollider(cam) { Material = new PhysicsMaterial { Friction = 0.5f } }; cam.Components.Add(col);
            var scene = SceneOf(cam);
            var fields = script.FieldValues; var yaw = fields[0]; var clips = animator.Clips; var walk = clips[0]; var material = col.Material;

            var snap = PlaySnapshot.Take(scene);
            // attach_script / the Inspector's script card while playing: Script.SetFieldValue edits the stored entry in place
            script.SetFieldValue("DebugYaw", "300");
            script.SetFieldValue("DebugPitch", "-8");
            script.SetFieldValue("Speed", "4");                  // a field without an entry before Play
            animator.Clips[0].Path = "Assets/Animations/Run.vanim";
            col.Material.Friction = 0.9f;
            col.Material = new PhysicsMaterial { Friction = 0.2f };   // edited, then replaced
            snap.Restore();

            t.Equal("-999", script.GetFieldValue("DebugYaw"), "a stored field edited in play is back (#478)");
            t.Equal("-999", script.GetFieldValue("DebugPitch"), "the second one too");
            t.Equal(null, script.GetFieldValue("Speed"), "a field first set in play is gone");
            t.Equal("Assets/Animations/Walk.vanim", animator.Clips[0].Path, "a clip slot edited in place is back");
            t.Equal(0.5f, col.Material.Friction, "a physics material edited in place is back");
            // the very objects come back, not copies: the viewport and an open inspector card hold these
            t.True(ReferenceEquals(script.FieldValues, fields) && ReferenceEquals(fields[0], yaw), "the same field list and entry");
            t.True(ReferenceEquals(animator.Clips, clips) && ReferenceEquals(clips[0], walk), "the same clip list and slot");
            t.True(ReferenceEquals(col.Material, material), "the collider's own material object, not the one set in play");

            // a second session is restored as well
            var again = PlaySnapshot.Take(scene);
            script.SetFieldValue("DebugYaw", "12");
            again.Restore();
            t.Equal("-999", script.GetFieldValue("DebugYaw"), "a second play session is restored as well");
        }

        [Test]
        public static void FoliageTypesComeBackIntoTheSameObjects(TestContext t)
        {
            var layer = Entity("Grass");
            var foliage = new Foliage(layer);
            var grass = Foliage.DefaultType("Grass", "Primitive:Sphere", true);
            grass.WindStrength = 0.1f;
            foliage.Types.Add(grass);
            layer.Components.Add(foliage);
            var scene = SceneOf(layer);
            var types = foliage.Types;

            var snap = PlaySnapshot.Take(scene);
            int version = foliage.Version;
            grass.WindStrength = 2f;   // a storm tuned while playing
            foliage.Types.Add(Foliage.DefaultType("Rock", "Primitive:Cube", false));
            snap.Restore();

            t.True(ReferenceEquals(foliage.Types, types) && types.Count == 1 && ReferenceEquals(types[0], grass) && grass.WindStrength == 0.1f,
                "the layer's own type object has its wind back — the foliage renderer and an open Foliage card hold it (#478)");
            t.True(foliage.Version != version, "the foliage renderer is told to rebuild");
            int unchanged = foliage.Version;
            PlaySnapshot.Take(scene).Restore();
            t.Equal(unchanged, foliage.Version, "an untouched layer is not rebuilt");
        }

        // ------------------------------------------------------------------ #479: the environment settings

        [Test]
        public static void EnvironmentSettingsComeBack(TestContext t)
        {
            var scene = SceneOf(Entity("Floor"));
            var st = scene.Settings;
            st.TaaEnabled = false; st.Sharpen = 0f; st.Tonemapper = 0; st.GiWorldEnabled = false; st.FogEnabled = true; st.FogDensity = 0.002f;

            var snap = PlaySnapshot.Take(scene);
            // set_environment(temporal_aa: true, sharpen: 0.4, tonemapper: neutral, gi_world: true), set_weather, the Environment panel
            st.TaaEnabled = true; st.Sharpen = 0.4f; st.Tonemapper = 1; st.GiWorldEnabled = true; st.FogDensity = 0.012f; st.BloomEnabled = true;
            snap.Restore();

            t.False(st.TaaEnabled, "temporal AA off again (#479)");
            t.Equal(0f, st.Sharpen, "sharpen back");
            t.Equal(0, st.Tonemapper, "the tonemapper back");
            t.False(st.GiWorldEnabled, "the world cache off again");
            t.Equal(0.002f, st.FogDensity, "the fog back");
            t.False(st.BloomEnabled, "bloom off again");

            var snap2 = PlaySnapshot.Take(scene);
            scene.Settings = new SceneSettings { FogEnabled = false };   // the whole block replaced
            snap2.Restore();
            t.True(ReferenceEquals(scene.Settings, st) && st.FogEnabled, "the scene's own settings object is back");
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

        // ------------------------------------------------------------------ #477: no screen outlives the session

        [Test]
        public static void ResetLeavesNoScreenOrCachedCanvas(TestContext t)
        {
            string path = t.Write("Assets/UI/PauseMenu.vui",
                "{ \"vui\": 1, \"designW\": 1920, \"designH\": 1080, \"root\": { \"kind\": \"Panel\", \"id\": \"pauseRoot\", \"blocksInput\": true, \"blocksGameplay\": true } }");
            var stack = VuiStack.Instance;
            stack.Reset();
            try
            {
                var pause = stack.Push(path);   // the template's PlayerController on ESC: Gui.Load("PauseMenu.vui").Show()
                t.NotNull(pause, "the screen loads");
                VuiDialogs.Confirm("Quit?", "", null, null);
                t.True(stack.HasActiveScreens && stack.GameplayInputBlocked, "the screens are up");
                stack.Reset();   // play stops / the project closes
                t.False(stack.HasActiveScreens, "no screen survives (#477)");
                t.False(stack.GameplayInputBlocked, "nothing keeps freezing gameplay input");
                t.False(ReferenceEquals(pause, stack.Load(path)), "Gui.Load reads the .vui again instead of the old session's canvas");
                stack.Reset();
                t.False(stack.HasActiveScreens, "Load alone shows nothing");
            }
            finally { stack.Reset(); }
        }

        // ------------------------------------------------------------------ #423: what Stop does beyond the scenes

        [Test]
        public static void UndoStepsMadeWhilePlayingAreDropped(TestContext t)
        {
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();   // whatever ran before this test — put back at the end
            try
            {
                var root = Entity("Root");
                var a = Entity("A", root);
                var b = Entity("B", root);
                var scene = SceneOf(root);
                undo.Execute(new MoveEntityCommand(b, null));   // an edit made before Play: B to the scene root
                var editStep = undo.GetUndoHistory()[0];

                var atPlay = undo.SaveHistory();
                var snap = PlaySnapshot.Take(scene);
                undo.Execute(new MoveEntityCommand(a, b));      // parent_entity while playing
                undo.Execute(new CollectionAddCommand<GameEntity>(scene.Entities, new GameEntity("Leak"), "Entities"));   // a create
                snap.Restore();
                int dropped = undo.RestoreHistory(atPlay);

                t.Equal(2, dropped, "the two steps made while playing are gone with the session");
                t.True(ReferenceEquals(undo.GetUndoHistory()[0], editStep) && undo.RedoCount == 0, "the history is the one of Play");
                undo.Undo();   // Ctrl+Z after Stop undoes the edit made before Play — not a play step against the restored scene
                t.True(scene.Entities.Count == 1 && root.Children.Count == 2 && root.Children[1] == b, "B is back under Root, once");
                var listed = Listed(scene);
                t.Equal(listed.Count, listed.Distinct().Count(), "no entity is listed twice (an undo after Stop used to do that)");
            }
            finally { undo.RestoreHistory(outer); }
        }

        [Test]
        public static void DataFilesMadeWhilePlayingGoWithTheirEntity(TestContext t)
        {
            string orphan = t.Write("Assets/Terrain/PlayTerrain.vterrain", "heights");
            t.Write("Assets/Terrain/PlayTerrain.vterrain.vmeta", "meta");
            string used = t.Write("Assets/Foliage/Grass.vfoliage", "instances");
            string older = t.Write("Assets/Areas/Cave.varea", "{}");
            string material = t.Write("Assets/Materials/Rust.vmat", "{}");
            var grass = Entity("Grass");
            grass.Components.Add(new Foliage(grass) { DataPath = used });
            var scene = SceneOf(grass);
            try
            {
                PlaySession.Begin();
                PlaySession.NoteWrite(orphan, true);     // create_terrain while playing: its entity is gone after Stop
                PlaySession.NoteWrite(used, true);       // a layer still in a scene keeps its file
                PlaySession.NoteWrite(older, false);     // a file that was there before Play is never deleted
                PlaySession.NoteWrite(material, true);   // not a data file: it stays (the Stop line names it)
                PlaySession.End();
                t.True(PlaySession.CreatedDataFile(orphan) && !PlaySession.CreatedDataFile(older) && !PlaySession.CreatedDataFile(material), "what play created is known");
                var deleted = PlaySession.DeleteOrphanedDataFiles(new[] { scene }, out string kept);
                t.True(deleted.Count == 1 && !File.Exists(orphan) && !File.Exists(orphan + ".vmeta"), "the orphaned terrain data goes, with its .vmeta");
                t.True(File.Exists(used) && File.Exists(older) && File.Exists(material) && kept == null, "everything else stays");

                // a scene saved while playing may refer to them: then nothing is deleted, and Stop says why
                string second = t.Write("Assets/Terrain/Second.vterrain", "heights");
                PlaySession.Begin();
                PlaySession.NoteWrite(second, true);
                PlaySession.NoteWrite(t.Path("Assets", "Scenes", "Main.vscene"), true);
                PlaySession.End();
                deleted = PlaySession.DeleteOrphanedDataFiles(new[] { scene }, out kept);
                t.True(deleted.Count == 0 && File.Exists(second) && kept != null, "kept while a scene saved in play may use it");
                t.True(PlaySession.FilesWrittenSince(t.Dir, DateTime.UtcNow.AddMinutes(-5)).Exists(f => f.EndsWith("Rust.vmat")), "the files written are found by their time stamps");
            }
            finally { PlaySession.End(); }
        }
    }
}
