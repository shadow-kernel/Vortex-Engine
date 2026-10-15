using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.Utilities;

namespace VortexTests
{
    /// <summary>Revert a scene to its file (#411): open_scene on a loaded scene used to keep the in-memory state, and nothing
    /// could read the .vscene again. A reload replaces entities, environment and name inside the same Scene object, tells the
    /// hierarchy, drops exactly the undo steps of that scene and leaves the scene alone when the file cannot be read.</summary>
    public static class SceneReloadTests
    {
        /// <summary>A scene file with a crate (a lid under it) and a barrel, read the way the editor reads one.</summary>
        internal static Scene SavedScene(TestContext t, string name)
        {
            var scene = new Scene { Name = name };
            var crate = EntityIdTests.Entity(scene, "Crate", new Vector3(1, 2, 3));
            EntityIdTests.Entity(scene, "Lid", new Vector3(0, 1, 0), crate);
            EntityIdTests.Entity(scene, "Barrel", new Vector3(4, 5, 6));
            scene.Settings.FogDensity = 0.02f;
            string path = t.Path(name + ".vscene");
            DataSerializer.SaveAsBinary(scene, path);
            return SceneService.Instance.LoadScene(path);
        }

        internal static bool At(GameEntity e, float x, float y, float z)
        {
            var p = e.Transform.LocalPosition;
            return Math.Abs(p.X - x) < 1e-5f && Math.Abs(p.Y - y) < 1e-5f && Math.Abs(p.Z - z) < 1e-5f;
        }

        // one closure per step (closures made in one scope share their captured variables)
        private static void Move(UndoRedoManager undo, GameEntity e, Vector3 to)
        {
            var from = e.Transform.LocalPosition;
            undo.ExecuteAction("Move " + e.Name, () => e.Transform.LocalPosition = to, () => e.Transform.LocalPosition = from);
        }

        private sealed class Note { public string Text; }

        private static void Write(UndoRedoManager undo, Note note, string text)
        {
            string before = note.Text;
            undo.ExecuteAction("Write " + text, () => note.Text = text, () => note.Text = before);
        }

        private static void Rename(UndoRedoManager undo, GameEntity e, string name)
        {
            string before = e.Name;
            undo.ExecuteAction("Rename " + before, () => e.Name = name, () => e.Name = before);
        }

        [Test]
        public static void RevertReadsTheFileIntoTheSameScene(TestContext t)
        {
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            try
            {
                var scene = SavedScene(t, "Yard");
                var oldList = scene.Entities;
                var crate = scene.Entities[0];
                var heard = new List<string>();
                scene.PropertyChanged += (s, e) => heard.Add(e.PropertyName);
                // what a session leaves in memory: a move, a new entity, a renamed scene, other fog
                crate.Transform.LocalPosition = new Vector3(9, 9, 9);
                scene.AddEntity(new GameEntity(scene, "Leak"));
                scene.Name = "Renamed";
                scene.Settings.FogDensity = 0.5f;

                var r = SceneService.Instance.ReloadScene(scene);

                t.True(r.Reloaded && r.Error == null, "reloaded: " + r.Error);
                t.True(r.DiscardedChanges, "it says the changes in memory were discarded");
                t.Equal("Yard", scene.Name, "the name comes from the file");
                t.Equal(2, scene.Entities.Count, "the entity made in memory is gone");
                var crate2 = scene.Entities.First(e => e.Name == "Crate");
                t.False(ReferenceEquals(crate2, crate), "the entities are the file's, not the old objects");
                t.Equal(crate.Id, crate2.Id, "with the ids the file gives them");
                t.True(At(crate2, 1, 2, 3), "the crate is where the file has it");
                t.True(ReferenceEquals(crate2.Scene, scene) && ReferenceEquals(crate2.Children[0].Parent, crate2) && ReferenceEquals(crate2.Children[0].Scene, scene),
                    "parents and scene references point into the same Scene object");
                t.True(Math.Abs(scene.Settings.FogDensity - 0.02f) < 1e-6f, "the environment comes from the file");
                t.False(scene.IsDirty, "not dirty afterwards");
                t.False(SceneService.Instance.HasUnsavedChanges(scene), "and the scene matches its file");
                t.True(heard.Contains(nameof(Scene.Entities)) && !ReferenceEquals(oldList, scene.Entities), "the hierarchy hears about the new entity list");
            }
            finally { undo.RestoreHistory(outer); }
        }

        [Test]
        public static void RevertDropsTheUndoStepsOfThatSceneOnly(TestContext t)
        {
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            try
            {
                undo.Clear();
                var a = SavedScene(t, "A");
                var b = SavedScene(t, "B");
                var aCrate = a.Entities[0];
                var aBarrel = a.Entities[1];
                var bCrate = b.Entities[0];
                var scenes = new ObservableCollection<Scene> { a, b };
                var note = new Note();
                IUndoableCommand Newest() => undo.GetUndoHistory()[0];

                Move(undo, aCrate, new Vector3(7, 7, 7));                       // A: a closure over A's crate
                Move(undo, bCrate, new Vector3(8, 8, 8)); var moveB = Newest(); // B
                Write(undo, note, "written"); var write = Newest();             // a file: no scene in it
                undo.BeginGroup("Claude: two things");                          // a group with one step of A
                Rename(undo, aBarrel, "Keg");
                Write(undo, note, "again");
                undo.EndGroup();
                undo.Execute(new DeleteEntitiesCommand(new[] { aBarrel }));     // holds A and the deleted barrel
                undo.Execute(new CollectionAddCommand<Scene>(scenes, new Scene { Name = "C" }, "Scenes")); var addScene = Newest();   // the scene list holds A, but is about C
                a.AddEntity(new GameEntity(a, "Leak"));
                undo.Undo();                                                    // A's add waits on the redo stack

                var r = SceneService.Instance.ReloadScene(a);

                t.True(r.Reloaded, "reloaded: " + r.Error);
                t.Equal(4, r.UndoStepsDropped, "A's move, the group with A's rename, A's delete and A's add on the redo stack");
                var left = undo.GetUndoHistory();
                t.Equal(3, left.Count, "three steps stay");
                t.True(ReferenceEquals(left[0], addScene) && ReferenceEquals(left[1], write) && ReferenceEquals(left[2], moveB), "the other steps stay, in their order");
                t.Equal(0, undo.RedoCount, "the redo step of A went too");
                while (undo.CanUndo) undo.Undo();
                t.True(At(bCrate, 1, 2, 3) && note.Text == null && scenes.Count == 2, "the kept steps still undo");
                t.True(a.Entities.Count == 2 && At(a.Entities[0], 1, 2, 3) && a.Entities[1].Name == "Barrel", "and none of them touched the reverted scene");
            }
            finally { undo.RestoreHistory(outer); }
        }

        [Test]
        public static void AFileThatCannotBeReadLeavesTheSceneAlone(TestContext t)
        {
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            try
            {
                var scene = SavedScene(t, "Yard");
                var crate = scene.Entities[0];
                Move(undo, crate, new Vector3(9, 9, 9));
                int steps = undo.UndoCount;
                File.WriteAllText(scene.FilePath, "not a scene");

                var r = SceneService.Instance.ReloadScene(scene);

                t.False(r.Reloaded, "nothing was reloaded");
                t.True((r.Error ?? "").Contains("Yard.vscene"), "the error names the file: " + r.Error);
                t.True(ReferenceEquals(scene.Entities[0], crate) && At(crate, 9, 9, 9), "the scene in memory is untouched");
                t.Equal(steps, undo.UndoCount, "and so is its undo history");

                var fresh = new Scene(null, "Fresh") { FilePath = t.Path("Fresh.vscene") };
                var never = SceneService.Instance.ReloadScene(fresh);
                t.True(!never.Reloaded && (never.Error ?? "").Contains("never saved"), "a scene that was never saved has nothing to revert to: " + never.Error);
            }
            finally { undo.RestoreHistory(outer); }
        }

        [Test]
        public static void UnsavedChangesAreWhatTheFileDoesNotHave(TestContext t)
        {
            var scene = SavedScene(t, "Yard");
            byte[] original = File.ReadAllBytes(scene.FilePath);
            t.False(SceneService.Instance.HasUnsavedChanges(scene), "a scene just read has none");

            scene.Entities[0].Transform.LocalPosition = new Vector3(9, 9, 9);
            t.True(SceneService.Instance.HasUnsavedChanges(scene), "a move counts, dirty flag or not");
            SceneService.Instance.SaveScene(scene);
            t.False(SceneService.Instance.HasUnsavedChanges(scene), "saved: none");

            File.WriteAllBytes(scene.FilePath, original);   // a git checkout puts the old file back
            t.True(SceneService.Instance.HasUnsavedChanges(scene), "the file changed on disk: the scene in memory no longer matches it");
            var r = SceneService.Instance.ReloadScene(scene);
            t.True(r.Reloaded && r.DiscardedChanges && At(scene.Entities[0], 1, 2, 3), "the revert reads the checked-out file");
            t.False(SceneService.Instance.HasUnsavedChanges(scene), "and matches it");

            scene.IsDirty = true;
            t.False(SceneService.Instance.HasUnsavedChanges(scene), "the dirty flag alone says nothing: reading a scene sets it");
            var unsaved = new Scene(null, "Fresh") { FilePath = t.Path("Fresh.vscene") };
            t.True(SceneService.Instance.HasUnsavedChanges(unsaved), "a scene without a file has nothing saved");

            var again = SceneService.Instance.ReloadScene(scene);
            t.True(again.Reloaded && !again.DiscardedChanges, "a revert with nothing to discard says so");
        }

        private sealed class Hit { }
        private sealed class Counter { public int Runs; }
        private static Action Capture(object o) => () => GC.KeepAlive(o);
        private static IEnumerable<object> Lazy(Counter c) { c.Runs++; yield return new Hit(); }

        [Test]
        public static void TheUndoWalkFindsClosuresAndListsButRunsNoQuery(TestContext t)
        {
            bool? Classify(object o) => o is Hit ? true : (o is GameEntity ? false : (bool?)null);

            t.True(UndoReferences.Uses(new ActionCommand("closure", Capture(new Hit()), Capture(null)), Classify), "a closure that captured it");
            t.True(UndoReferences.Uses(new CollectionAddCommand<object>(new List<object> { 1, "x", new Hit() }, "y", "List"), Classify), "a list that holds it");
            t.True(UndoReferences.Uses(new CompositeCommand("group", new IUndoableCommand[] { new ActionCommand("other", Capture("x"), Capture(null)), new ActionCommand("it", Capture(new object[] { new Hit() }), Capture(null)) }), Classify),
                "a group with a step that holds it in an array");
            t.False(UndoReferences.Uses(new ActionCommand("other", Capture(new GameEntity("Other")), Capture(null)), Classify), "a step about something else");

            var counter = new Counter();
            t.False(UndoReferences.Uses(new ActionCommand("iterator", Capture(Lazy(counter)), Capture(null)), Classify), "a lazy sequence is no list it holds");
            var query = Enumerable.Range(0, 3).Select(i => { counter.Runs++; return (object)new Hit(); });
            t.False(UndoReferences.Uses(new ActionCommand("query", Capture(query), Capture(null)), Classify), "nor is a LINQ query");
            t.Equal(0, counter.Runs, "the walk never runs one");
            t.True(UndoReferences.Uses(new ActionCommand("big", Capture(Enumerable.Range(0, 500).Select(i => (object)new GameEntity("E" + i)).ToList()), Capture(null)), Classify, budget: 50),
                "a walk out of budget counts as a hit: dropping a step too many is safe");
        }
    }

    /// <summary>The engine side of a revert (#411): the discarded entities leave the engine, the file's join it — runs with the
    /// native engine library only (the Z sorts it after the headless tests).</summary>
    public static class ZSceneReloadNativeTests
    {
        private static bool Native()
        {
            try { Editor.Core.Native.NativeLoader.Register(); } catch { }
            try { VortexAPI.DestroyEngineScene(VortexAPI.CreateEngineScene()); return true; }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                Console.WriteLine("    (skipped: no native engine library — " + ex.Message + ")");
                return false;
            }
        }

        private static List<GameEntity> All(Scene scene)
        {
            var list = new List<GameEntity>();
            void Walk(GameEntity e) { list.Add(e); foreach (var c in e.Children) Walk(c); }
            foreach (var e in scene.Entities) Walk(e);
            return list;
        }

        [Test]
        public static void RevertedEntitiesTakeTheirNativeTwinsAlong(TestContext t)
        {
            if (!Native()) return;
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            var scene = SceneReloadTests.SavedScene(t, "Yard");
            try
            {
                scene.ActivateEntities();
                var old = All(scene);
                var leak = new GameEntity(scene, "Leak");
                scene.AddEntity(leak);
                old[0].Transform.LocalPosition = new Vector3(9, 9, 9);
                t.True(old.Concat(new[] { leak }).All(e => ID.IsValid(e.EntityId)), "the open scene's entities are in the engine");

                var r = SceneService.Instance.ReloadScene(scene);

                t.True(r.Reloaded, "reloaded: " + r.Error);
                t.True(old.All(e => e.EntityId == ID.INVALID_ID) && leak.EntityId == ID.INVALID_ID, "every discarded entity left the engine");
                var now = All(scene);
                t.True(now.All(e => ID.IsValid(e.EntityId)) && now.Select(e => e.EntityId).Distinct().Count() == now.Count, "the file's entities joined it, each with its own id");
                var crate = now.First(e => e.Name == "Crate");
                var p = VortexAPI.ReadEntityPosition(crate.EntityId);
                t.True(Math.Abs(p.X - 1) < 1e-4f && Math.Abs(p.Y - 2) < 1e-4f && Math.Abs(p.Z - 3) < 1e-4f, "its native entity holds the file's transform");
                t.True(scene.IsActive, "the scene is still the open one");
            }
            finally
            {
                try { scene.DeactivateEntities(); scene.ReleaseEngineScene(); } catch { }
                undo.RestoreHistory(outer);
            }
        }
    }
}
