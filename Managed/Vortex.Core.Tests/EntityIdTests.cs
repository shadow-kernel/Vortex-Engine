using System;
using System.Linq;
using System.Reflection;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Rendering;
using Editor.Utilities;

namespace VortexTests
{
    /// <summary>Engine ids of entities that come out of the serializer — a loaded scene, a duplicate, a paste, a prefab
    /// instance: the id is not saved, and DataContractSerializer skips field initialisers, so it used to read 0, a VALID
    /// native id, and every such entity shared native entity 0. Headless — no native engine (see ZEntityIdNativeTests).</summary>
    public static class EntityIdTests
    {
        internal static GameEntity Entity(Scene scene, string name, Vector3 position, GameEntity parent = null)
        {
            var e = new GameEntity(scene, name);
            e.Transform.LocalPosition = position;
            if (parent != null) { e.Parent = parent; parent.Children.Add(e); }
            else scene.Entities.Add(e);
            return e;
        }

        internal static long Handle(object component, string field)
            => (long)component.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(component);

        internal static long RendererHandle(MeshRenderer mr) => Handle(mr, "_rendererHandle");

        [Test]
        public static void DeserializedEntitiesStartUnregistered(TestContext t)
        {
            var scene = new Scene { Name = "Yard" };
            var crate = Entity(scene, "Crate", new Vector3(1, 0, 0));
            crate.AddComponentDirect(new MeshRenderer(crate));   // no paths: nothing to load, every handle stays unset
            Entity(scene, "Lid", new Vector3(0, 1, 0), crate).AddComponentDirect(new SpriteRenderer());
            Entity(scene, "Barrel", new Vector3(4, 0, 0));

            var back = DataSerializer.FromBinary<Scene>(DataSerializer.ToBinary(scene));
            var all = back.Entities.Concat(back.Entities.SelectMany(e => e.Children)).ToList();
            t.Equal(3, all.Count, "three entities loaded");
            t.True(all.All(e => e.EntityId == ID.INVALID_ID), "a loaded entity has no engine id until its scene registers it (was 0 for all of them)");
            var mr = all[0].GetComponent<MeshRenderer>();
            t.Equal(ID.INVALID_ID, RendererHandle(mr), "nor a native mesh-renderer component (was 0 — another renderer's)");
            foreach (var f in new[] { "_meshHandle", "_materialHandle", "_textureHandle", "_shaderHandle" })
                t.Equal(ID.INVALID_ID, Handle(mr, f), "an unsaved " + f + " starts unset, not as handle 0");
            t.False(mr.HasImportedMaterial, "no imported material without a material handle");
            mr.MaterialHandle = 0;
            t.False(mr.HasImportedMaterial, "material handle 0 is no material (material and resource ids start at 1)");
            t.Equal(ID.INVALID_ID, Handle(all.First(e => e.Name == "Lid").GetComponent<SpriteRenderer>(), "_spriteHandle"), "nor the sprite handle");

            var json = DataSerializer.FromJson<GameEntity>(DataSerializer.ToJson(crate));   // the prefab / prefab-editor path
            t.True(json.EntityId == ID.INVALID_ID && json.Children[0].EntityId == ID.INVALID_ID, "a prefab instance starts unregistered too");
        }

        [Test]
        public static void PastedCopiesBelongToTheTargetScene(TestContext t)
        {
            var scene = new Scene { Name = "Yard" };
            var crate = Entity(scene, "Crate", new Vector3(1, 0, 0));
            Entity(scene, "Lid", new Vector3(0, 1, 0), crate);
            var data = new EntityClipboardData { SerializedData = DataSerializer.ToBinary(crate), OriginalName = crate.Name, OriginalId = crate.Id };
            var copy = EntityClipboardService.Instance.DeserializeEntity(data, scene);
            t.True(copy.EntityId == ID.INVALID_ID && copy.Children[0].EntityId == ID.INVALID_ID, "a pasted copy has its own (not yet any) engine id");
            t.True(ReferenceEquals(copy.Scene, scene) && ReferenceEquals(copy.Children[0].Scene, scene),
                "the whole pasted subtree is in the target scene (a child with no scene registered outside it)");
            t.True(copy.Id != crate.Id && copy.Children[0].Id != crate.Children[0].Id, "fresh ids");
        }

        [Test]
        public static void SyncEngineStateLeavesInactiveSubtreesOut(TestContext t)
        {
            // a scene that is not active: nothing registers (and nothing calls into the engine)
            var scene = new Scene { Name = "Yard" };
            var crate = Entity(scene, "Crate", new Vector3(1, 0, 0));
            var lid = new GameEntity(scene, "Lid");
            crate.AddChild(lid);
            t.True(crate.Children.Contains(lid), "the child is added");
            t.Equal(ID.INVALID_ID, lid.EntityId, "a child added in an inactive scene stays unregistered");
            lid.SyncEngineState();
            t.Equal(ID.INVALID_ID, lid.EntityId, "SyncEngineState in an inactive scene registers nothing");
            var orphan = new GameEntity("Template");
            orphan.SyncEngineState();
            t.Equal(ID.INVALID_ID, orphan.EntityId, "an entity in no scene (a prefab template) never enters the engine");
        }

        [Test]
        public static void FoliageFindHandsOutAHandleTheOtherCallsAccept(TestContext t)
        {
            var scene = new Scene { Name = "Meadow" };
            Entity(scene, "Rock", new Vector3(0, 0, 0));   // another unregistered entity: an engine id must not pick it
            var meadow = Entity(scene, "Meadow", new Vector3(0, 0, 0));
            meadow.AddComponentDirect(new Foliage(meadow));
            var back = DataSerializer.FromBinary<Scene>(DataSerializer.ToBinary(scene));   // as loaded: no engine ids
            var layer = back.Entities.First(e => e.Name == "Meadow");
            var old = ProjectData.Current;
            try
            {
                ProjectData.Current = new ProjectData { Name = "p" };
                ProjectData.Current.ActiveScene = back;
                long handle = Vortex.Foliage.Find();
                t.True(handle != 0 && handle != ID.INVALID_ID, "a loaded foliage layer is found (" + handle + "; it was engine id 0 = \"none\")");
                t.True(ReferenceEquals(Vortex.Foliage.Resolve(handle), layer), "the handle resolves to the layer — Paint / Erase / InstanceCount act on it");
                t.Equal(handle, Editor.Scripting.ScriptRuntime.Instance.HandleForEntity(layer), "the same handle Scene.Find hands out");
                t.True(Vortex.Foliage.Resolve(ID.INVALID_ID) == null, "an unregistered entity's engine id names no layer");
            }
            finally { ProjectData.Current = old; }
        }
    }

    /// <summary>The engine side of <see cref="EntityIdTests"/>: every entity of a loaded scene gets its OWN native entity
    /// when the scene activates — transforms reach it, deactivating one entity removes only its own, ids stay unique
    /// through deactivate / reactivate, children, pastes and prefab instances. The class name sorts it after the headless
    /// tests on purpose: it loads the native engine library into the test process. Skipped without one (VORTEX_NATIVE_DIR
    /// or the repository's CMake build tree).</summary>
    public static class ZEntityIdNativeTests
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

        private static bool At(GameEntity e, float x, float y, float z)
        {
            if (!ID.IsValid(e.EntityId)) return false;
            var p = VortexAPI.ReadEntityPosition(e.EntityId);
            return Math.Abs(p.X - x) < 1e-4f && Math.Abs(p.Y - y) < 1e-4f && Math.Abs(p.Z - z) < 1e-4f;
        }

        private static Scene Loaded(out GameEntity crate, out GameEntity lid, out GameEntity barrel)
        {
            var scene = new Scene { Name = "Yard" };
            var c = EntityIdTests.Entity(scene, "Crate", new Vector3(1, 2, 3));
            EntityIdTests.Entity(scene, "Lid", new Vector3(0, 1, 0), c);
            EntityIdTests.Entity(scene, "Barrel", new Vector3(4, 5, 6));
            var back = DataSerializer.FromBinary<Scene>(DataSerializer.ToBinary(scene));
            crate = back.Entities[0]; lid = crate.Children[0]; barrel = back.Entities[1];
            return back;
        }

        private static bool Unique(params GameEntity[] es)
            => es.All(e => ID.IsValid(e.EntityId)) && es.Select(e => e.EntityId).Distinct().Count() == es.Length;

        [Test]
        public static void LoadedEntitiesGetTheirOwnNativeEntities(TestContext t)
        {
            if (!Native()) return;
            var scene = Loaded(out var crate, out var lid, out var barrel);
            try
            {
                scene.ActivateEntities();
                t.True(Unique(crate, lid, barrel), "two loaded entities (and a child) get different valid ids: " + crate.EntityId + ", " + lid.EntityId + ", " + barrel.EntityId);
                t.True(At(crate, 1, 2, 3) && At(lid, 0, 1, 0) && At(barrel, 4, 5, 6), "each native entity holds its own entity's transform");

                crate.Transform.LocalPosition = new Vector3(7, 8, 9);
                t.True(At(crate, 7, 8, 9) && At(barrel, 4, 5, 6), "a transform edit reaches the edited entity's native entity only");

                long barrelId = barrel.EntityId;
                crate.IsActive = false;
                t.True(crate.EntityId == ID.INVALID_ID && lid.EntityId == ID.INVALID_ID, "a deactivated entity (and its child) leaves the engine");
                t.True(barrel.EntityId == barrelId && At(barrel, 4, 5, 6), "deactivating one loaded entity keeps the other's native entity");

                crate.IsActive = true;
                t.True(Unique(crate, lid, barrel) && At(crate, 7, 8, 9) && At(lid, 0, 1, 0), "reactivated: new unique ids, the current transforms");

                scene.DeactivateEntities();
                t.True(new[] { crate, lid, barrel }.All(e => e.EntityId == ID.INVALID_ID), "a deactivated scene unregisters every entity");
                scene.ActivateEntities();
                t.True(Unique(crate, lid, barrel) && At(barrel, 4, 5, 6), "and registers them again, unique");
            }
            finally { scene.DeactivateEntities(); scene.ReleaseEngineScene(); }
        }

        [Test]
        public static void CreatedChildrenPastesAndPrefabInstancesNeverShareAnId(TestContext t)
        {
            if (!Native()) return;
            var scene = Loaded(out var crate, out var lid, out var barrel);
            var old = ProjectData.Current;
            try
            {
                scene.ActivateEntities();
                ProjectData.Current = new ProjectData { Name = "p" };
                ProjectData.Current.ActiveScene = scene;

                var made = scene.CreateEntity("Made");                    // created in the editor
                made.Transform.LocalPosition = new Vector3(-1, -1, -1);
                var kid = new GameEntity(scene, "Kid");                   // Create Child
                barrel.AddChild(kid);
                t.True(Unique(crate, lid, barrel, made, kid), "loaded, created and child entities: all different ids");

                EntityClipboardService.Instance.Copy(new[] { crate });    // copy + paste: a deserialized copy
                var pasted = EntityClipboardService.Instance.Paste(scene, null);
                t.Equal(1, pasted.Count, "one entity pasted");
                var copy = pasted[0];
                t.True(Unique(crate, lid, barrel, made, kid, copy, copy.Children[0]), "the pasted copy and its child get their own ids");
                copy.Transform.LocalPosition = new Vector3(2, 2, 2);
                t.True(At(copy, 2, 2, 2) && At(crate, 1, 2, 3), "the copy's transform reaches the copy's native entity, not the original's");

                string prefab = t.Write("Crate.ventity", DataSerializer.ToJson(crate));   // a prefab instance: deserialized too
                var instance = PrefabService.Instance.InstantiatePrefab(prefab, scene, null, false);
                t.NotNull(instance, "the prefab instantiates");
                t.True(Unique(crate, lid, barrel, made, kid, copy, copy.Children[0], instance, instance.Children[0]), "the prefab instance and its child get their own ids");

                long madeId = made.EntityId;
                crate.IsActive = false;
                instance.IsActive = false;
                t.True(made.EntityId == madeId && At(made, -1, -1, -1), "deactivating loaded entities never removes the created entity's native entity");

                UndoRedoManager.Instance.Undo();                          // the paste
                t.True(!scene.Entities.Contains(copy) && copy.EntityId == ID.INVALID_ID && copy.Children[0].EntityId == ID.INVALID_ID, "an undone paste leaves the engine");

                barrel.RemoveChild(kid);
                t.Equal(ID.INVALID_ID, kid.EntityId, "a removed child leaves the engine");
            }
            finally
            {
                ProjectData.Current = old;
                scene.DeactivateEntities(); scene.ReleaseEngineScene();
            }
        }

        [Test]
        public static void MovingAnEntityFollowsWhereItLands(TestContext t)
        {
            if (!Native()) return;
            var scene = Loaded(out var crate, out var lid, out var barrel);
            var shelf = new Scene { Name = "Shelf" };   // a loaded scene that is not active
            try
            {
                scene.ActivateEntities();
                crate.IsActive = false;
                UndoRedoManager.Instance.Execute(new Editor.Editors.WorldEditor.Components.SceneHierarchy.MoveEntityCommand(barrel, crate));
                t.Equal(ID.INVALID_ID, barrel.EntityId, "moved under an inactive parent: out of the engine");
                UndoRedoManager.Instance.Undo();
                t.True(Unique(barrel) && At(barrel, 4, 5, 6) && barrel.Parent == null, "moved back (undo): registered again");
                crate.IsActive = true;

                UndoRedoManager.Instance.Execute(new Editor.Editors.WorldEditor.Components.SceneHierarchy.MoveEntityToSceneCommand(crate, shelf));
                t.True(crate.EntityId == ID.INVALID_ID && lid.EntityId == ID.INVALID_ID && ReferenceEquals(lid.Scene, shelf), "moved into an inactive scene: out of the engine, children too");
                UndoRedoManager.Instance.Undo();
                t.True(Unique(crate, lid, barrel) && At(crate, 1, 2, 3) && At(lid, 0, 1, 0), "moved back: registered again, each its own transform");
            }
            finally { scene.DeactivateEntities(); scene.ReleaseEngineScene(); }
        }

        [Test]
        public static void PlaySnapshotRestoreRegistersAgain(TestContext t)
        {
            if (!Native()) return;
            var scene = Loaded(out var crate, out var lid, out var barrel);
            try
            {
                scene.ActivateEntities();
                var snap = PlaySnapshot.Take(scene);
                barrel.IsActive = false;                 // SetActive(false) while playing
                lid.IsActive = false;
                t.True(barrel.EntityId == ID.INVALID_ID && lid.EntityId == ID.INVALID_ID, "deactivated while playing: out of the engine");
                snap.Restore();
                t.True(barrel.IsActive && lid.IsActive, "Stop puts the active flags back");
                t.True(Unique(crate, lid, barrel) && At(barrel, 4, 5, 6) && At(lid, 0, 1, 0), "restored entities are registered again with unique ids");
                t.True(At(crate, 1, 2, 3), "and the untouched loaded entity still owns its native entity");
            }
            finally { scene.DeactivateEntities(); scene.ReleaseEngineScene(); }
        }

        [Test]
        public static void DeletedDuringPlayIsRegisteredAgainAfterStop(TestContext t)
        {
            if (!Native()) return;
            var scene = Loaded(out var crate, out var lid, out var barrel);
            // an entity in the scene's list without a scene of its own (no editor path makes one — a test helper does)
            var floating = new GameEntity("Floating");
            scene.Entities.Add(floating);
            try
            {
                scene.ActivateEntities();
                var snap = PlaySnapshot.Take(scene);
                // while playing: the hierarchy's / the Claude tools' Delete takes them out of the engine (#423 puts them back)
                UndoRedoManager.Instance.Execute(new DeleteEntitiesCommand(new[] { barrel, lid }));
                scene.Entities.Remove(floating);
                t.True(barrel.EntityId == ID.INVALID_ID && lid.EntityId == ID.INVALID_ID, "deleted while playing: out of the engine");
                snap.Restore();   // Stop
                t.True(scene.Entities.Contains(barrel) && crate.Children.Contains(lid), "Stop puts the deleted entities back");
                t.True(Unique(crate, lid, barrel) && At(barrel, 4, 5, 6) && At(lid, 0, 1, 0), "and registers them again, each its own transform");
                t.True(scene.Entities.Contains(floating) && floating.EntityId == ID.INVALID_ID,
                    "one without a scene is put back but stays out of the engine (the old re-sync registered it outside any scene)");
            }
            finally { scene.DeactivateEntities(); scene.ReleaseEngineScene(); }
        }

        /// <summary>A native entity that is gone reads the origin (GetEntityPosition answers zeros for a dead id).</summary>
        private static bool Gone(long nativeId) => ID.IsValid(nativeId) && VortexAPI.ReadEntityPosition(nativeId).X == 0f
            && VortexAPI.ReadEntityPosition(nativeId).Y == 0f && VortexAPI.ReadEntityPosition(nativeId).Z == 0f;

        [Test]
        public static void DeleteAndUndoTakeTheNativeTwinOutAndBack(TestContext t)
        {
            if (!Native()) return;
            var scene = Loaded(out var crate, out var lid, out var barrel);
            var old = ProjectData.Current;
            try
            {
                scene.ActivateEntities();
                ProjectData.Current = new ProjectData { Name = "p" };
                ProjectData.Current.ActiveScene = scene;
                var undo = UndoRedoManager.Instance;

                // Delete (the hierarchy, the Claude tools) and its undo
                long twin = barrel.EntityId;
                undo.Execute(new DeleteEntitiesCommand(new[] { barrel }));
                t.True(barrel.EntityId == ID.INVALID_ID && Gone(twin), "a deleted loaded entity's native twin is gone");
                scene.DeactivateEntities(); scene.ActivateEntities();
                t.True(Gone(twin) && barrel.EntityId == ID.INVALID_ID, "also after the scene was deactivated and activated again");
                undo.Undo();
                t.True(scene.Entities.Contains(barrel) && Unique(crate, lid, barrel) && At(barrel, 4, 5, 6), "undo: back in the scene and in the engine");
                long crateTwin = crate.EntityId, lidTwin = lid.EntityId;
                undo.Execute(new DeleteEntitiesCommand(new[] { crate }));
                t.True(Gone(crateTwin) && Gone(lidTwin) && lid.EntityId == ID.INVALID_ID, "deleting a parent takes its children's twins too");
                undo.Undo();
                t.True(Unique(crate, lid, barrel) && At(lid, 0, 1, 0), "and undo brings them back");

                // undo / redo of create, Create Child and a prefab placement
                var made = scene.CreateEntity("Made");
                made.Transform.LocalPosition = new Vector3(-1, -1, -1);
                long madeTwin = made.EntityId;
                undo.Undo();
                t.True(!scene.Entities.Contains(made) && made.EntityId == ID.INVALID_ID && Gone(madeTwin), "an undone create leaves the engine");
                undo.Redo();
                t.True(scene.Entities.Contains(made) && Unique(crate, lid, barrel, made) && At(made, -1, -1, -1), "redo: registered again");

                var kid = new GameEntity(scene, "Kid");
                kid.Transform.LocalPosition = new Vector3(3, 3, 3);
                barrel.AddChild(kid);
                long kidTwin = kid.EntityId;
                t.True(Unique(crate, lid, barrel, made, kid), "Create Child registers the child");
                undo.Undo();
                t.True(kid.EntityId == ID.INVALID_ID && Gone(kidTwin), "an undone Create Child leaves the engine");
                undo.Redo();
                t.True(Unique(crate, lid, barrel, made, kid) && At(kid, 3, 3, 3), "redo: registered again");
                barrel.RemoveChild(kid);
                t.Equal(ID.INVALID_ID, kid.EntityId, "RemoveChild unregisters it");
                undo.Undo();
                t.True(ReferenceEquals(kid.Parent, barrel) && Unique(crate, lid, barrel, made, kid), "its undo puts it back under its parent, registered");

                string prefab = t.Write("Crate.ventity", DataSerializer.ToJson(crate));
                var instance = PrefabService.Instance.InstantiatePrefab(prefab, scene);   // placed by the user: undoable
                long instanceTwin = instance.EntityId, instanceChildTwin = instance.Children[0].EntityId;
                t.True(Unique(crate, lid, barrel, made, kid, instance, instance.Children[0]), "a placed prefab instance is registered");
                undo.Undo();
                t.True(!scene.Entities.Contains(instance) && Gone(instanceTwin) && Gone(instanceChildTwin), "an undone prefab placement leaves the engine, children too");
                undo.Redo();
                t.True(Unique(crate, lid, barrel, made, kid, instance, instance.Children[0]), "redo: registered again");
            }
            finally
            {
                ProjectData.Current = old;
                scene.DeactivateEntities(); scene.ReleaseEngineScene();
            }
        }

        [Test]
        public static void ClosingTheProjectTakesItsScenesOutOfTheEngine(TestContext t)
        {
            if (!Native()) return;
            var scene = Loaded(out var crate, out var lid, out var barrel);
            var old = ProjectData.Current;
            try
            {
                scene.ActivateEntities();
                var project = new ProjectData { Name = "p" };
                project.Scenes.Add(scene);
                project.ActiveScene = scene;
                ProjectData.Current = project;
                long[] twins = { crate.EntityId, lid.EntityId, barrel.EntityId };
                Editor.Core.Editing.EditorSession.Instance.CloseProject(keepLastProject: true);
                t.True(new[] { crate, lid, barrel }.All(e => e.EntityId == ID.INVALID_ID), "every entity of the closed project is unregistered");
                t.True(twins.All(Gone), "their native twins are gone");
                t.False(scene.EngineHandle.IsValid, "and the engine scene is released");
            }
            finally
            {
                ProjectData.Current = old;
                scene.DeactivateEntities(); scene.ReleaseEngineScene();
            }
        }

        [Test]
        public static void ScenesThatAreNotOpenStayOutOfTheEngine(TestContext t)
        {
            if (!Native()) return;
            var open = Loaded(out var crate, out _, out _);
            var closed = Loaded(out var shed, out _, out _);   // loaded with the project, never opened
            closed.Name = "Shed";
            var old = ProjectData.Current;
            try
            {
                open.ActivateEntities();
                var project = new ProjectData { Name = "p", Path = t.Dir };
                project.Scenes.Add(open);
                project.Scenes.Add(closed);
                project.ActiveScene = open;
                ProjectData.Current = project;
                bool Untouched() => !closed.IsActive && !closed.EngineHandle.IsValid && closed.Entities.All(e => e.EntityId == ID.INVALID_ID);

                // the active flag of an entity there: no registration, and the scene is not activated
                shed.IsActive = false;
                shed.IsActive = true;
                t.True(Untouched() && shed.EntityId == ID.INVALID_ID, "toggling a top-level entity of a scene that is not open leaves it and its scene alone");

                // a prefab Apply re-creates the instances of every scene
                string prefab = t.Write("Crate.ventity", DataSerializer.ToJson(crate));
                var mine = PrefabService.Instance.InstantiatePrefab(prefab, open);
                var theirs = PrefabService.Instance.InstantiatePrefab(prefab, closed, null, false);
                t.True(Unique(mine) && theirs.EntityId == ID.INVALID_ID && Untouched(), "an instance placed into the scene that is not open stays unregistered");
                t.True(PrefabService.Instance.ApplyToPrefab(mine), "Apply runs");
                var fresh = closed.Entities.FirstOrDefault(e => e.IsPrefabInstance);
                t.True(fresh != null && !ReferenceEquals(fresh, theirs), "Apply re-created the other scene's instance");
                t.True(Untouched() && open.IsActive, "after Apply the other scene is still not open, its entities unregistered (it used to be activated)");

                // deleting the prefab removes every instance; the undo puts them back — without opening the other scene
                int n = PrefabService.Instance.OnPrefabDeleted(prefab);
                t.True(n == 2 && !closed.Entities.Contains(fresh), "deleting the prefab removes both instances");
                UndoRedoManager.Instance.Undo();
                t.True(closed.Entities.Contains(fresh) && Untouched() && open.Entities.Any(e => e.IsPrefabInstance && ID.IsValid(e.EntityId)),
                    "the undo restores both — the open scene's registered, the other scene still untouched");
            }
            finally
            {
                ProjectData.Current = old;
                open.DeactivateEntities(); open.ReleaseEngineScene();
                closed.DeactivateEntities(); closed.ReleaseEngineScene();
            }
        }

        [Test]
        public static void ASpawnSwitchedOffDuringPlayLeavesNoNativeEntity(TestContext t)
        {
            if (!Native()) return;
            var scene = Loaded(out var crate, out var lid, out var barrel);
            var old = ProjectData.Current;
            var rt = Editor.Scripting.ScriptRuntime.Instance;
            try
            {
                scene.ActivateEntities();
                var project = new ProjectData { Name = "p", Path = t.Dir };
                project.Scenes.Add(scene);
                project.ActiveScene = scene;
                ProjectData.Current = project;
                string prefab = t.Write("Crate.ventity", DataSerializer.ToJson(crate));

                rt.Begin(scene, typeof(ZEntityIdNativeTests).Assembly);   // play: no behaviours, the runtime alone
                long kept = rt.InstantiatePrefabAt(prefab, new Vortex.Vector3(8, 0, 8), 0f);
                long destroyed = rt.InstantiatePrefabAt(prefab, new Vortex.Vector3(9, 0, 9), 0f);
                var a = rt.FindEntityByHandle(kept);
                var b = rt.FindEntityByHandle(destroyed);
                t.True(a != null && b != null && Unique(crate, lid, barrel, a, b), "runtime spawns are registered");
                rt.SetEntityActive(kept, false);        // a script switches them off …
                rt.SetEntityActive(destroyed, false);
                rt.DestroyEntity(destroyed);            // … and destroys one of them
                rt.End();                               // Stop

                t.True(!scene.Entities.Contains(a) && !scene.Entities.Contains(b), "the spawns are gone after play");
                t.True(a.EntityId == ID.INVALID_ID && b.EntityId == ID.INVALID_ID && a.Children[0].EntityId == ID.INVALID_ID,
                    "and stay out of the engine: restoring their active flags did not re-register them outside the scene");
                t.True(a.IsActive && b.IsActive, "their flags are back");
                t.True(Unique(crate, lid, barrel), "the authored entities are registered as before");
            }
            finally
            {
                try { rt.End(); } catch { }
                try { PhysicsNative.Shutdown(); } catch { }
                ProjectData.Current = old;
                scene.DeactivateEntities(); scene.ReleaseEngineScene();
            }
        }

        [Test]
        public static void LoadedRenderersGetTheirOwnNativeComponents(TestContext t)
        {
            if (!Native()) return;
            var s = new Scene { Name = "Yard" };
            var a0 = EntityIdTests.Entity(s, "A", new Vector3(1, 0, 0));
            a0.AddComponentDirect(new MeshRenderer(a0) { MeshPath = "Primitive:Cube" });
            var b0 = EntityIdTests.Entity(s, "B", new Vector3(2, 0, 0));
            b0.AddComponentDirect(new MeshRenderer(b0) { MeshPath = "Primitive:Sphere" });
            var scene = DataSerializer.FromBinary<Scene>(DataSerializer.ToBinary(s));
            var a = scene.Entities[0]; var b = scene.Entities[1];
            var ra = a.GetComponent<MeshRenderer>(); var rb = b.GetComponent<MeshRenderer>();
            // the resource manager is not started in a test process (no InitializeRuntime): give the renderers the mesh
            // handles a started one hands out for their paths
            var meshField = typeof(MeshRenderer).GetField("_meshHandle", BindingFlags.NonPublic | BindingFlags.Instance);
            meshField.SetValue(ra, 1001L);
            meshField.SetValue(rb, 1002L);
            try
            {
                scene.ActivateEntities();
                long ha = EntityIdTests.RendererHandle(ra), hb = EntityIdTests.RendererHandle(rb);
                t.True(ID.IsValid(ha) && ID.IsValid(hb) && ha != hb, "two loaded renderers get two native renderer components (" + ha + ", " + hb + ")");
                t.True(VortexAPI.GetMeshFromRenderer(ha) == 1001 && VortexAPI.GetMeshFromRenderer(hb) == 1002, "each component holds its own renderer's mesh");
                a.IsActive = false;
                t.Equal(ID.INVALID_ID, EntityIdTests.RendererHandle(ra), "deactivating an entity removes its renderer component");
                t.Equal(1002L, VortexAPI.GetMeshFromRenderer(hb), "and leaves the other one alone");
            }
            finally { scene.DeactivateEntities(); scene.ReleaseEngineScene(); }
        }
    }
}
