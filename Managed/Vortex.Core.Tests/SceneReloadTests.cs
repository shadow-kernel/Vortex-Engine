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
        public static void UnsavedChangesFollowTheEditsAndTheFile(TestContext t)
        {
            SceneDirtyTracker.Install();
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            try
            {
                var scene = SavedScene(t, "Yard");
                byte[] original = File.ReadAllBytes(scene.FilePath);
                t.False(scene.IsDirty || SceneService.Instance.HasUnsavedChanges(scene), "a scene just read has none (reading its name used to mark it)");
                t.False(SceneService.Instance.FileChangedOnDisk(scene), "and its file is the one it was read from");

                Move(undo, scene.Entities[0], new Vector3(9, 9, 9));
                t.True(scene.IsDirty && SceneService.Instance.HasUnsavedChanges(scene), "an undo step marks it (a gizmo drag, a hierarchy edit, a tool's step)");
                SceneService.Instance.SaveScene(scene);
                t.False(SceneService.Instance.HasUnsavedChanges(scene), "saved: none");
                undo.Undo();
                t.True(SceneService.Instance.HasUnsavedChanges(scene), "an undo is an edit too");
                SceneService.Instance.SaveScene(scene);

                File.WriteAllBytes(scene.FilePath, original);   // a git checkout puts another version of the file there
                File.SetLastWriteTimeUtc(scene.FilePath, DateTime.UtcNow.AddMinutes(1));
                t.False(SceneService.Instance.HasUnsavedChanges(scene), "nothing in memory is unsaved");
                t.True(SceneService.Instance.FileChangedOnDisk(scene), "but the file changed on disk");

                var unsaved = new Scene(null, "Fresh") { FilePath = t.Path("Fresh.vscene") };
                t.True(SceneService.Instance.HasUnsavedChanges(unsaved), "a new scene has nothing saved");
            }
            finally { undo.RestoreHistory(outer); }
        }

        [Test]
        public static void AValueSetWithoutAnUndoStepIsUnsavedAndRevertAsks(TestContext t)
        {
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            try
            {
                undo.Clear();
                var scene = SavedScene(t, "Yard");
                var crate = scene.Entities[0];
                // an inspector row or a card's control writes the value directly: no undo step marks anything
                crate.Transform.LocalPosition = new Vector3(9, 9, 9);
                t.False(scene.IsDirty, "nothing marked the flag");
                t.True(SceneService.Instance.HasUnsavedChanges(scene), "still the scene is unsaved: it differs from what was read");
                var pv = SceneService.Instance.PreviewReload(scene);
                t.True(pv.Differs && pv.Unsaved, "and Revert Scene asks before it discards the move");
                var r = SceneService.Instance.ReloadScene(scene);
                t.True(r.Reloaded && r.DiscardedChanges && At(scene.Entities[0], 1, 2, 3), "the revert reads the file");
            }
            finally { undo.RestoreHistory(outer); }
        }

        [Test]
        public static void AnEditTakenBackByHandIsNoChange(TestContext t)
        {
            SceneDirtyTracker.Install();
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            try
            {
                undo.Clear();
                var scene = SavedScene(t, "Yard");
                var crate = scene.Entities[0];
                Move(undo, crate, new Vector3(5, 5, 5));
                Move(undo, crate, new Vector3(1, 2, 3));   // where the file has it
                t.True(scene.IsDirty, "the steps marked the flag");
                t.False(SceneService.Instance.HasUnsavedChanges(scene), "but the scene is its file: nothing is unsaved");
                var r = SceneService.Instance.ReloadScene(scene);
                t.True(r.Unchanged && !r.Reloaded, "a revert reads nothing");
                t.False(scene.IsDirty, "and the flag agrees again");
                t.Equal(2, undo.UndoCount, "the steps stay");
            }
            finally { undo.RestoreHistory(outer); }
        }

        [Test]
        public static void AFileOfAnOlderEngineMatchesItsScene(TestContext t)
        {
            var scene = new Scene { Name = "Old" };
            EntityIdTests.Entity(scene, "Crate", new Vector3(1, 2, 3));
            // an older engine wrote what this one does not read: the bytes differ, the scene does not
            var doc = System.Xml.Linq.XDocument.Load(System.Xml.XmlDictionaryReader.CreateBinaryReader(DataSerializer.ToBinary(scene), System.Xml.XmlDictionaryReaderQuotas.Max));
            doc.Root.Add(new System.Xml.Linq.XElement("legacyNote", "written by v2.9"));
            byte[] old;
            using (var ms = new MemoryStream())
            {
                using (var w = System.Xml.XmlDictionaryWriter.CreateBinaryWriter(ms)) doc.WriteTo(w);
                old = ms.ToArray();
            }
            string path = t.Path("Old.vscene");
            File.WriteAllBytes(path, old);
            var loaded = SceneService.Instance.LoadScene(path);
            t.False(DataSerializer.ToBinary(loaded).AsSpan().SequenceEqual(old), "this engine writes the file differently");
            t.False(SceneService.Instance.HasUnsavedChanges(loaded), "a scene just read has no unsaved changes");
            var r = SceneService.Instance.ReloadScene(loaded);
            t.True(r.Unchanged && !r.Reloaded, "and a revert finds nothing to do: " + (r.Error ?? ""));
            loaded.Entities[0].Transform.LocalPosition = new Vector3(4, 4, 4);
            t.True(SceneService.Instance.HasUnsavedChanges(loaded) && SceneService.Instance.PreviewReload(loaded).Differs, "until it is edited");
        }

        [Test]
        public static void ASceneSavedWhilePlayingIsUnsavedAfterStop(TestContext t)
        {
            var scene = SavedScene(t, "Yard");
            var snap = PlaySnapshot.Take(new[] { scene });
            scene.Entities[0].Transform.LocalPosition = new Vector3(77, 0, 0);   // the game moved it
            scene.Save();                                                        // Cmd+S / save_scene while playing
            snap.Restore();                                                      // Stop
            t.True(At(scene.Entities[0], 1, 2, 3), "Stop put the crate back");
            t.True(scene.IsDirty && SceneService.Instance.HasUnsavedChanges(scene), "the file holds the game's crate: the scene is unsaved");
            t.False(SceneService.Instance.FileChangedOnDisk(scene), "the editor wrote that file itself");
        }

        [Test]
        public static void AGizmoDragIsOneUndoStep(TestContext t)
        {
            SceneDirtyTracker.Install();
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            try
            {
                undo.Clear();
                var scene = SavedScene(t, "Yard");
                var crate = scene.Entities[0];
                var p0 = crate.Transform.LocalPosition; var r0 = crate.Transform.LocalRotation; var s0 = crate.Transform.LocalScale;
                t.True(Editor.Core.Viewport.EditorViewportSession.GizmoStep(crate, p0, r0, s0) == null, "a drag that moved nothing is no step");
                for (int i = 1; i <= 20; i++) crate.Transform.LocalPosition = new Vector3(1 + i * 0.1f, 2, 3);   // the drag, frame by frame
                var step = Editor.Core.Viewport.EditorViewportSession.GizmoStep(crate, p0, r0, s0);
                t.True(step != null && step.Name == "Move Crate", "one step for the whole drag: " + step?.Name);
                undo.Execute(step, execute: false);
                t.True(scene.IsDirty, "it marks the scene unsaved");
                undo.Undo();
                t.True(At(crate, 1, 2, 3), "undo puts the crate where the drag began");
                undo.Redo();
                t.True(At(crate, 3, 2, 3), "redo where it ended");
            }
            finally { undo.RestoreHistory(outer); }
        }

        [Test]
        public static void TheInspectorCountsSavedValuesOnly(TestContext t)
        {
            var e = new GameEntity("Probe");
            t.True(SceneDirtyTracker.IsPersisted(e.Transform, "LocalPosition"), "a transform's position is in the file");
            t.True(SceneDirtyTracker.IsPersisted(new Editor.ECS.Components.Physics.BoxCollider(e), "Size"), "so is a collider's size");
            t.True(SceneDirtyTracker.IsPersisted(e, "Name"), "and the entity's name");
            t.False(SceneDirtyTracker.IsPersisted(e, "IsSelected") || SceneDirtyTracker.IsPersisted(e, "IsExpanded"), "the selection and a tree node's state are not");
            t.False(SceneDirtyTracker.IsPersisted(new Scene { Name = "S" }, "IsDirty"), "nor the unsaved flag itself");
        }

        [Test]
        public static void ARevertLeavesADataFileChangedOnDiskAlone(TestContext t)
        {
            var previous = ProjectData.Current;
            try
            {
                ProjectData.Current = new ProjectData(Guid.NewGuid(), t.Dir, "P");
                var scene = new Scene { Name = "Yard" };
                EntityIdsWith(scene, "Zone", "Assets/Areas/Zone.varea");
                string zoneFile = t.Path("Assets", "Areas", "Zone.varea");
                new Editor.Core.Areas.AreaDefinition { Name = "v1-saved" }.Save(zoneFile);
                string path = t.Path("Yard.vscene");
                DataSerializer.SaveAsBinary(scene, path);
                scene = SceneService.Instance.LoadScene(path);

                new Editor.Core.Areas.AreaDefinition { Name = "v2-editor" }.Save(zoneFile, scene);   // an edit in the editor
                File.WriteAllText(zoneFile, File.ReadAllText(zoneFile).Replace("v2-editor", "v3-teammate"));   // a git pull brings another version
                File.SetLastWriteTimeUtc(zoneFile, DateTime.UtcNow.AddSeconds(5));
                var r = SceneService.Instance.ReloadScene(scene);
                t.True(r.Reloaded && r.DataFilesRestored.Count == 0 && r.DataFilesChangedOnDisk.Count == 1, "the revert names the file and leaves it: " + string.Join(", ", r.DataFilesChangedOnDisk));
                t.True(File.ReadAllText(zoneFile).Contains("v3-teammate"), "the pulled version stays");
                t.Equal(0, SceneDataFiles.Pending(scene).Count, "and it is the baseline now");
            }
            finally { ProjectData.Current = previous; SceneDataFiles.Clear(); }
        }

        [Test]
        public static void AFileMadeForAnEntityDeletedAgainGoesWithARevert(TestContext t)
        {
            var previous = ProjectData.Current;
            try
            {
                ProjectData.Current = new ProjectData(Guid.NewGuid(), t.Dir, "P");
                var scene = SavedScene(t, "Yard");
                var zone = EntityIdsWith(scene, "Zone", "Assets/Areas/T3.varea");
                string made = t.Path("Assets", "Areas", "T3.varea");
                new Editor.Core.Areas.AreaDefinition { Name = "t3" }.Save(made, scene);   // the new entity's file
                File.WriteAllText(made + ".vmeta", "{}");                                   // and the asset database's sidecar
                scene.Entities.Remove(zone);                                                 // deleted again before the revert

                t.True(SceneDataFiles.Pending(scene).Count == 1 && SceneService.Instance.HasUnsavedChanges(scene), "the file is the scene's unsaved change");
                var r = SceneService.Instance.ReloadScene(scene);
                t.True(r.Reloaded && !r.Unchanged, "a revert is not 'unchanged' while the file is there");
                t.False(File.Exists(made) || File.Exists(made + ".vmeta"), "the file and its .vmeta are gone");
            }
            finally { ProjectData.Current = previous; SceneDataFiles.Clear(); }
        }

        [Test]
        public static void TheCopiesGoWithTheEditorAndACrashLeavesNoneForLong(TestContext t)
        {
            var previous = ProjectData.Current;
            try
            {
                ProjectData.Current = new ProjectData(Guid.NewGuid(), t.Dir, "P");
                string zoneFile = t.Path("Assets", "Areas", "Zone.varea");
                new Editor.Core.Areas.AreaDefinition { Name = "v1" }.Save(zoneFile);
                SceneDataFiles.Clear();
                SceneDataFiles.BeforeWrite(zoneFile);   // a copy is kept
                string mine = Path.Combine(SceneDataFiles.Root, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                t.True(Directory.Exists(mine) && Directory.GetFiles(mine).Length >= 1, "the copy is in this editor's folder");
                SceneDataFiles.Clear(removeFolder: true);   // the editor quits
                t.False(Directory.Exists(mine), "quitting takes the folder along");

                string dead = Path.Combine(SceneDataFiles.Root, "99999999");   // a process id no system hands out
                Directory.CreateDirectory(dead);
                File.WriteAllText(Path.Combine(dead, "copy.vterrain"), "left by a crash");
                SceneDataFiles.SweepStale();
                t.False(Directory.Exists(dead), "the next editor deletes what a crashed one left");
            }
            finally { ProjectData.Current = previous; SceneDataFiles.Clear(); }
        }

        [Test]
        public static void AStepMarksTheScenesItTouchesAndStopPutsTheFlagsBack(TestContext t)
        {
            var a = SavedScene(t, "A");
            var b = SavedScene(t, "B");
            SceneDirtyTracker.MarkScenesOf(new ActionCommand("move", Capture(a.Entities[0].Transform), Capture(null)));
            t.True(a.IsDirty && !b.IsDirty, "a step on A's transform marks A only");
            SceneDirtyTracker.MarkScenesOf(new ActionCommand("note", Capture(new Note()), Capture(null)));
            t.False(b.IsDirty, "a step about no scene marks none");
            SceneDirtyTracker.MarkScenesOf(new CollectionAddCommand<Scene>(new ObservableCollection<Scene> { a, b }, new Scene(null, "C"), "Scenes"));
            t.False(b.IsDirty, "adding a scene to the project's list is no edit of the others");
            t.False(DataSerializer.FromBinary<Scene>(DataSerializer.ToBinary(a)).IsDirty, "a scene that comes out of the serializer is not dirty");

            var snap = PlaySnapshot.Take(new[] { a, b });
            b.IsDirty = true;
            a.IsDirty = false;
            snap.Restore();
            t.True(a.IsDirty && !b.IsDirty, "Stop puts back the flags of Play: what play changed is gone");
        }

        [Test]
        public static void ARevertWithNothingToDiscardDoesNothing(TestContext t)
        {
            SceneDirtyTracker.Install();
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            try
            {
                undo.Clear();
                var scene = SavedScene(t, "Yard");
                Move(undo, scene.Entities[0], new Vector3(9, 9, 9));
                SceneService.Instance.SaveScene(scene);   // saved after the edit: memory and file agree

                var pv = SceneService.Instance.PreviewReload(scene);
                t.True(pv.Blocked == null && !pv.Differs && pv.UndoSteps == 0, "the preview says there is nothing to do");
                var r = SceneService.Instance.ReloadScene(scene);
                t.True(r.Unchanged && !r.Reloaded && r.Error == null && r.UndoStepsDropped == 0, "nothing was read");
                t.Equal(1, undo.UndoCount, "and the step stays: it can still be undone (the review's save-then-revert)");

                Move(undo, scene.Entities[0], new Vector3(5, 5, 5));
                pv = SceneService.Instance.PreviewReload(scene);
                t.True(pv.Differs && pv.Unsaved && pv.UndoSteps == 2, "after another edit: it differs, and both steps would go");
            }
            finally { undo.RestoreHistory(outer); }
        }

        [Test]
        public static void RevertPutsBackTheDataFilesWrittenSinceTheSave(TestContext t)
        {
            var previous = ProjectData.Current;
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            try
            {
                ProjectData.Current = new ProjectData(Guid.NewGuid(), t.Dir, "P");
                var scene = new Scene { Name = "Yard" };
                var zone = EntityIdsWith(scene, "Zone", "Assets/Areas/Zone.varea");
                string zoneFile = t.Path("Assets", "Areas", "Zone.varea");
                new Editor.Core.Areas.AreaDefinition { Name = "v1" }.Save(zoneFile);
                string path = t.Path("Yard.vscene");
                DataSerializer.SaveAsBinary(scene, path);
                scene = SceneService.Instance.LoadScene(path);   // the baseline: the scene and its area file as saved

                new Editor.Core.Areas.AreaDefinition { Name = "v2" }.Save(zoneFile);                    // written since the save
                EntityIdsWith(scene, "New", "Assets/Areas/New.varea");
                string newFile = t.Path("Assets", "Areas", "New.varea");
                new Editor.Core.Areas.AreaDefinition { Name = "made" }.Save(newFile);                   // made since the save

                var pv = SceneService.Instance.PreviewReload(scene);
                t.True(pv.Differs && pv.DataFiles.Count == 2, "the preview lists both files: " + string.Join(", ", pv.DataFiles));
                var r = SceneService.Instance.ReloadScene(scene);
                t.True(r.Reloaded && r.DiscardedChanges && r.DataFilesRestored.Count == 2, "both put back: " + string.Join(", ", r.DataFilesRestored));
                t.True(File.ReadAllText(zoneFile).Contains("\"v1\""), "the area file is the saved one again");
                t.False(File.Exists(newFile), "and the one made since is gone, like its area");
                t.Equal(0, SceneDataFiles.Pending(scene).Count, "the files on disk are the baseline now");
            }
            finally { ProjectData.Current = previous; undo.RestoreHistory(outer); SceneDataFiles.Clear(); }
        }

        private static GameEntity EntityIdsWith(Scene scene, string name, string areaFile)
        {
            var e = EntityIdTests.Entity(scene, name, new Vector3(0, 0, 0));
            e.AddComponentDirect(new Editor.ECS.Components.World.Area(e) { DataPath = areaFile });
            return e;
        }

        [Test]
        public static void ARevertNeverPutsOneEntityIntoTwoScenes(TestContext t)
        {
            var project = new ProjectData(Guid.NewGuid(), t.Dir, "P");
            var a = SavedScene(t, "A");
            var b = SavedScene(t, "B");
            a.Project = b.Project = project;
            project.Scenes.Add(a);
            project.Scenes.Add(b);
            var crate = a.Entities[0];
            a.Entities.Remove(crate);   // moved to B since A was saved
            b.Entities.Add(crate);
            crate.Scene = b;

            var r = SceneService.Instance.ReloadScene(a);
            t.True(!r.Reloaded && (r.Error ?? "").Contains("'Crate' (now in 'B')"), "refused, naming the entity and where it is: " + r.Error);
            t.True(b.Entities.Contains(crate) && a.Entities.Count == 1, "nothing changed");
            t.True(SceneService.Instance.PreviewReload(a).Blocked != null, "the preview says so too");
        }

        [Test]
        public static void ARevertTakesTheFilesIdentity(TestContext t)
        {
            var project = new ProjectData(Guid.NewGuid(), t.Dir, "P");
            var scene = SavedScene(t, "Yard");
            Guid fileId = scene.Id;
            scene.Project = project;
            project.Scenes.Add(scene);
            scene.Id = Guid.NewGuid();   // the scene in memory came to carry another id
            project.StartSceneId = scene.Id;

            var r = SceneService.Instance.ReloadScene(scene);
            t.True(r.Reloaded && scene.Id == fileId, "the id is the file's again");
            t.True(project.StartSceneId == fileId, "and the project's start scene still points at it");
        }

        private sealed class Hit { }
        private sealed class Counter { public int Runs; }
        private struct Plain { public float X, Y, Z; public int Kind; }
        private static Action Capture(object o) => () => GC.KeepAlive(o);
        private static IEnumerable<object> Lazy(Counter c) { c.Runs++; yield return new Hit(); }

        [Test]
        public static void TheUndoWalkFindsClosuresAndListsButRunsNoQuery(TestContext t)
        {
            UndoRef Classify(object o) => o is Hit ? UndoRef.Target : (o is GameEntity ? UndoRef.Skip : UndoRef.LookInto);

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
            t.True(UndoReferences.Uses(new ActionCommand("big", Capture(Enumerable.Range(0, 500).Select(i => (object)new object()).ToList()), Capture(null)), Classify, budget: 50),
                "a walk out of budget counts as a hit: dropping a step too many is safe");
        }

        [Test]
        public static void AnotherScenesStepStaysWhateverItHolds(TestContext t)
        {
            var a = new Scene { Name = "A" };
            var b = new Scene { Name = "B" };
            var inB = new GameEntity(b, "Foliage B");
            UndoRef Classify(object o) => o switch
            {
                GameEntity e => ReferenceEquals(e.Scene, a) ? UndoRef.Target : UndoRef.Elsewhere,
                Scene s => ReferenceEquals(s, a) ? UndoRef.Target : UndoRef.Elsewhere,
                _ => UndoRef.LookInto,
            };
            // what a foliage stroke of scene B records: its entity and 120,000 instances (plain structs)
            var instances = new Dictionary<string, List<Plain>> { ["grass"] = Enumerable.Repeat(new Plain { X = 1 }, 120_000).ToList() };
            var paint = new ActionCommand("Paint foliage", Capture(new object[] { inB, instances }), Capture(null));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            t.False(UndoReferences.Uses(paint, Classify), "B's stroke is not A's: lists of plain values are not walked");
            t.True(sw.ElapsedMilliseconds < 1000, "and the walk is quick: " + sw.ElapsedMilliseconds + " ms");
            var crowded = new ActionCommand("Crowd", Capture(new object[] { inB, Enumerable.Range(0, 200_000).Select(i => new object()).ToList() }), Capture(null));
            t.False(UndoReferences.Uses(crowded, Classify, budget: 1000), "a step that runs out of budget after meeting another scene's entity stays theirs");
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

        [Test]
        public static void ANewTerrainOwnsItsDataAndARevertPutsTheSculptingBack(TestContext t)
        {
            if (!Native()) return;
            var previous = ProjectData.Current;
            var undo = UndoRedoManager.Instance;
            var outer = undo.SaveHistory();
            try
            {
                ProjectData.Current = new ProjectData(Guid.NewGuid(), t.Dir, "P");
                var scene = new Scene(ProjectData.Current, "Land") { FilePath = t.Path("Land.vscene") };
                var hill = new GameEntity(scene, "Hill");
                var terrain = new Editor.ECS.Components.Rendering.Terrain(hill) { Size = 32, Resolution = 33 };
                hill.AddComponentDirect(terrain);
                scene.Entities.Add(hill);

                // what create_terrain does: the terrain never rendered, yet it owns its file at once (#411 review, finding 12)
                t.True(Editor.Core.Services.Terrain.TerrainService.Save(hill), "a terrain that never rendered saves");
                string file = Editor.Core.Services.Terrain.TerrainService.ResolvePath(terrain.DataPath);
                t.True(file != null && File.Exists(file), "it owns its data file from the start: " + terrain.DataPath);
                scene.Save();
                var back = DataSerializer.LoadFromBinary<Scene>(scene.FilePath);
                t.Equal(terrain.DataPath, back.Entities[0].GetComponent<Editor.ECS.Components.Rendering.Terrain>()?.DataPath, "the scene's first save records the path");
                byte[] saved = File.ReadAllBytes(file);

                // sculpting writes the file at once — what sculpt_terrain and the brush do
                Editor.Core.Services.Terrain.TerrainService.TryGetData(hill, out var data, out _);
                data.Raise(16, 16, 6, 5f, 0.2f);
                Editor.Core.Services.Terrain.TerrainService.Save(hill);
                t.False(File.ReadAllBytes(file).AsSpan().SequenceEqual(saved), "the stroke is on disk");
                t.True(SceneService.Instance.HasUnsavedChanges(scene), "and counts as unsaved");

                var r = SceneService.Instance.ReloadScene(scene);
                t.True(r.Reloaded && r.DataFilesRestored.Any(p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase)), "the revert puts the terrain file back: " + string.Join(", ", r.DataFilesRestored));
                t.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(saved), "to the saved heights");
            }
            finally
            {
                ProjectData.Current = previous;
                undo.RestoreHistory(outer);
                try { Editor.Core.Services.Terrain.TerrainService.Clear(); } catch { }
                SceneDataFiles.Clear();
            }
        }
    }
}
