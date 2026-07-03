using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Central prefab workflow — the ONE place prefabs are saved / instantiated / applied / reverted, like a
    /// mainstream engine:
    ///  • SaveAsPrefab  — serialize an entity subtree to Assets/Prefabs/&lt;name&gt;.ventity (readable JSON) and LINK
    ///    the source entity to it (it becomes an instance).
    ///  • InstantiatePrefab — add a fresh LINKED copy (new ids, synced to the engine so it renders) into a scene.
    ///  • ApplyToPrefab — write an instance's current state back to the .ventity AND update every other instance of
    ///    that prefab in the active scene (each keeps its own transform).
    ///  • RevertInstance — replace an instance with a fresh copy from the prefab (discard local edits), same transform.
    ///
    /// The old code saved BINARY (ProjectService.SavePrefab) but loaded JSON (SceneService.LoadEntityFromPrefab), so
    /// instantiating a saved prefab always failed — that's why "save works but nothing can be done with it". This
    /// service uses JSON for both. Instances carry <see cref="GameEntity.PrefabPath"/> to stay connected to the asset.
    /// </summary>
    public sealed class PrefabService
    {
        private static PrefabService _instance;
        public static PrefabService Instance => _instance ?? (_instance = new PrefabService());

        public const string PrefabExtension = ".ventity";

        /// <summary>Plain-language description of the whole prefab workflow — surfaced in the Prefab Editor and
        /// tooltips so "what does Save / Apply / Revert do?" is answered right where it's used.</summary>
        public const string WorkflowHelp =
            "A prefab is a reusable template of an object (with its children + components).\n\n" +
            "• Save as Prefab — turn the selected entity into a .ventity asset. The entity becomes a linked INSTANCE of it.\n" +
            "• Add to Scene — drop a new linked instance into the scene. Edit each instance freely.\n" +
            "• Apply to Prefab — push THIS instance's current changes back into the asset, updating every instance.\n" +
            "• Revert to Prefab — throw away this instance's local edits and reload it from the asset (keeps its position).";

        /// <summary>Save an entity subtree as a reusable prefab (JSON) and link the source entity to it (becomes an
        /// instance). Returns the absolute .ventity path, or null on failure.</summary>
        public string SaveAsPrefab(GameEntity entity, string prefabName = null)
        {
            var project = ProjectData.Current;
            if (entity == null || project == null) return null;

            var dir = Path.Combine(project.Path, "Assets", "Prefabs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Sanitize(prefabName ?? entity.Name) + PrefabExtension);

            WriteTemplate(entity, file);                                   // the .ventity is a template (no PrefabPath)
            entity.PrefabPath = ToProjectRelative(file, project.Path);     // link the source entity to it
            try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
            return file;
        }

        /// <summary>Load a .ventity and add a fresh LINKED instance to the scene (new ids, synced to the engine so it
        /// renders immediately). Returns the new instance, or null. <paramref name="undoable"/>=true (the default, for
        /// a user placing an instance) records the add on the undo stack; pass FALSE for internal re-instantiation
        /// (Apply/Revert propagation) whose paired removal is a RAW, non-undoable RemoveFromScene — otherwise the add
        /// pushes a stray CollectionAddCommand with no matching removal and corrupts the undo stack.</summary>
        public GameEntity InstantiatePrefab(string prefabPath, Scene scene, GameEntity parent = null, bool undoable = true)
        {
            var project = ProjectData.Current;
            if (string.IsNullOrEmpty(prefabPath) || scene == null) return null;
            var full = Resolve(prefabPath, project);
            if (!File.Exists(full)) return null;

            var entity = DataSerializer.LoadFromJson<GameEntity>(full);
            if (entity == null) return null;
            entity.RegenerateIds();
            entity.Scene = scene;
            entity.PrefabPath = ToProjectRelative(full, project?.Path);
            SetActiveRecursive(entity, true);   // a freshly-loaded subtree must be active or SubmitEntity skips it

            if (parent != null)
            {
                entity.Parent = parent;
                parent.Children.Add(entity);   // child add is already raw (non-undoable)
                entity.SyncEngineStateRecursive(parent.ActiveInHierarchy);
            }
            else if (undoable)
            {
                scene.AddEntity(entity);   // undoable add (user place) -> syncs to the engine -> the instance renders
            }
            else
            {
                // RAW add — mirrors what scene.AddEntity does MINUS the CollectionAddCommand, so it stays symmetric
                // with RevertInstance's raw RemoveFromScene and never leaves a dangling undo command on the stack.
                entity.Scene = scene;
                scene.Entities.Add(entity);
                entity.SyncEngineStateRecursive(scene.IsActive);
                scene.IsDirty = true;
            }

            // Force the editor's render service to resolve meshes/materials for the WHOLE new subtree so even a
            // LARGE, multi-submesh prefab shows up the same frame instead of staying invisible (the "saved a big
            // prefab, it doesn't render" bug). SubmitScene imports meshes on demand; PreloadSceneAssets primes
            // textures/materials so nothing renders untextured/white on the first frame.
            EnsureRendered(scene);
            return entity;
        }

        /// <summary>Prime the render service so a just-added subtree resolves its meshes/materials immediately.</summary>
        private static void EnsureRendered(Scene scene)
        {
            if (scene == null) return;
            try { scene.IsDirty = true; } catch { }
            try { Editor.Core.Services.SceneRenderService.Instance.PreloadSceneAssets(scene); } catch { }
        }

        private static void SetActiveRecursive(GameEntity e, bool active)
        {
            if (e == null) return;
            try { e.IsActive = active; } catch { }
            if (e.Children != null)
                foreach (var c in e.Children) SetActiveRecursive(c, active);
        }

        /// <summary>Write the instance's current state back to its prefab asset, then update every OTHER instance of
        /// that prefab in the active scene (each keeps its own transform). Returns false if not a prefab instance.</summary>
        public bool ApplyToPrefab(GameEntity instance)
        {
            var project = ProjectData.Current;
            if (instance == null || project == null || !instance.IsPrefabInstance) return false;
            var full = Resolve(instance.PrefabPath, project);
            try { WriteTemplate(instance, full); }
            catch { return false; }

            try { PropagateToOtherInstances(instance); } catch { }
            try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
            // The viewport does NOT watch the Entities collection for changes, so re-instantiated instances would keep
            // showing the stale baked queue until an unrelated event dirties it — force a re-bake so every instance
            // visually updates the moment Apply runs.
            RequestViewportResubmit();
            return true;
        }

        /// <summary>Replace an instance with a fresh copy from its prefab (discards local edits) but KEEP its
        /// transform — transform is treated as a per-instance override. Returns the new instance.</summary>
        public GameEntity RevertInstance(GameEntity instance)
        {
            if (instance == null || instance.Scene == null || !instance.IsPrefabInstance) return null;
            var scene = instance.Scene;
            var parent = instance.Parent;
            var prefabPath = instance.PrefabPath;
            var oldT = instance.Transform;   // still valid — we hold the ref after removing the entity

            RemoveFromScene(instance);

            // undoable:false — RemoveFromScene above is a RAW (non-undoable) removal, so the re-add must be raw too,
            // else each Apply/Revert leaves a stray CollectionAddCommand on the undo stack with no matching removal.
            var fresh = InstantiatePrefab(prefabPath, scene, parent, undoable: false);
            if (fresh?.Transform != null && oldT != null)
            {
                fresh.Transform.LocalPosition = oldT.LocalPosition;
                fresh.Transform.LocalRotation = oldT.LocalRotation;
                fresh.Transform.LocalScale = oldT.LocalScale;
            }
            return fresh;
        }

        /// <summary>A prefab asset (.ventity) — or a folder of them — was DELETED. REMOVE every scene instance that
        /// pointed at it, across ALL open scenes (an instance whose source prefab is gone is meaningless). The removal
        /// is UNDOABLE in lockstep with the (undoable) file delete: Ctrl+Z re-creates the instances and a further
        /// Ctrl+Z restores the .ventity file. Returns how many instances were removed. Pass isDirectory=true to remove
        /// every instance whose prefab lived UNDER the deleted folder.</summary>
        public int OnPrefabDeleted(string deletedPath, bool isDirectory = false)
        {
            var project = ProjectData.Current;
            if (project == null || project.Scenes == null || string.IsNullOrEmpty(deletedPath)) return 0;
            string targetFull;
            try { targetFull = Path.GetFullPath(Resolve(deletedPath, project)); }
            catch { return 0; }

            var toDelete = new List<GameEntity>();
            foreach (var scene in project.Scenes)
            {
                if (scene?.Entities == null) continue;
                CollectMatchingInstances(scene.Entities, targetFull, isDirectory, project, toDelete);
            }
            if (toDelete.Count == 0) return 0;

            // Remove the instances UNDOABLY. DeleteEntitiesCommand only mutates the C# ObservableCollections, so pair
            // it (in a CompositeCommand) with an ActionCommand that detaches/re-attaches each instance from the NATIVE
            // engine via SyncEngineStateRecursive + refreshes the viewport — mirroring Scene.RemoveEntity/AddEntity.
            // Do NOT use SceneRenderService.RemoveEntity here: it DeleteMesh's imported-model meshes that are SHARED
            // across instances (double-free). Order: detach-engine THEN remove-from-collection; Undo runs in reverse
            // (re-insert THEN re-attach), so instances come back rendered.
            int n = toDelete.Count;
            var composite = new Editor.Core.UndoRedo.Commands.CompositeCommand("Delete " + n + " prefab instance" + (n == 1 ? "" : "s"));
            composite.Add(new Editor.Core.UndoRedo.Commands.ActionCommand("detach prefab instances",
                () => { foreach (var e in toDelete) { try { e.SyncEngineStateRecursive(false); } catch { } } RequestViewportResubmit(); },
                () => { foreach (var e in toDelete) { try { e.SyncEngineStateRecursive(true); } catch { } } RequestViewportResubmit(); }));
            composite.Add(new DeleteEntitiesCommand(toDelete));
            Editor.Core.UndoRedo.UndoRedoManager.Instance.Execute(composite);

            // If the currently-selected entity was one of the removed instances (or a descendant of one), clear the
            // selection — DeleteEntitiesCommand only mutates the C# collections, so otherwise the Inspector + the
            // viewport gizmo keep operating on a now-detached ghost entity.
            try
            {
                var sel = SelectionService.Instance.SelectedEntity;
                if (sel != null)
                    foreach (var e in toDelete)
                        if (ContainsEntity(e, sel)) { SelectionService.Instance.Select((GameEntity)null); break; }
            }
            catch { }

            return n;
        }

        /// <summary>True if <paramref name="target"/> is <paramref name="root"/> or anywhere under it.</summary>
        private static bool ContainsEntity(GameEntity root, GameEntity target)
        {
            if (root == null) return false;
            if (ReferenceEquals(root, target)) return true;
            if (root.Children != null)
                foreach (var c in root.Children)
                    if (ContainsEntity(c, target)) return true;
            return false;
        }

        private static void CollectMatchingInstances(IEnumerable<GameEntity> entities, string targetFull, bool isDirectory, ProjectData project, List<GameEntity> outList)
        {
            if (entities == null) return;
            foreach (var e in entities)
            {
                if (e == null) continue;
                if (e.IsPrefabInstance)
                {
                    string instFull = null;
                    try { instFull = Path.GetFullPath(Resolve(e.PrefabPath, project)); } catch { }
                    if (instFull != null)
                    {
                        // Normalize BOTH sides (relative vs absolute, slash direction) before comparing, or instances
                        // stored project-relative won't match an absolute deleted path.
                        bool hit = isDirectory
                            ? instFull.StartsWith(targetFull.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            : string.Equals(instFull, targetFull, StringComparison.OrdinalIgnoreCase);
                        if (hit) outList.Add(e);
                    }
                }
                CollectMatchingInstances(e.Children, targetFull, isDirectory, project, outList);
            }
        }

        // ---- helpers ----

        /// <summary>Serialize an entity as a prefab TEMPLATE — the file must not itself carry a PrefabPath.</summary>
        private static void WriteTemplate(GameEntity entity, string file)
        {
            var saved = entity.PrefabPath;
            entity.PrefabPath = null;
            try { DataSerializer.SaveAsJson(entity, file); }
            finally { entity.PrefabPath = saved; }
        }

        /// <summary>Reload EVERY instance of a prefab (across all open scenes) from the .ventity on disk, each keeping
        /// its own transform. Call after the prefab TEMPLATE was edited directly (the isolated Prefab Editor) so the
        /// change reaches placed instances without needing a source instance to propagate from.</summary>
        public void ReloadInstancesFromPrefab(string prefabPathAbsOrRel)
        {
            var project = ProjectData.Current;
            if (project?.Scenes == null || string.IsNullOrEmpty(prefabPathAbsOrRel)) return;
            var full = Resolve(prefabPathAbsOrRel, project);
            var rel = ToProjectRelative(full, project.Path);
            var targets = new List<GameEntity>();
            foreach (var sc in project.Scenes)
                if (sc?.Entities != null) CollectInstances(sc.Entities, rel, null, targets);
            foreach (var t in targets) { try { RevertInstance(t); } catch { } }
            RequestViewportResubmit();
        }

        private void PropagateToOtherInstances(GameEntity source)
        {
            var project = ProjectData.Current;
            if (project?.Scenes == null) return;
            // Update EVERY instance of this prefab across ALL open scenes (not just the source's scene), each keeping
            // its own transform. Instances in non-active scenes become data+engine-correct and render live once their
            // scene is activated (the viewport can only present the single active scene).
            var targets = new List<GameEntity>();
            foreach (var sc in project.Scenes)
            {
                if (sc?.Entities == null) continue;
                CollectInstances(sc.Entities, source.PrefabPath, source, targets);
            }
            foreach (var t in targets) RevertInstance(t);   // re-instantiates from the just-saved prefab, keeps transform
        }

        /// <summary>Ask the editor viewport to re-bake its render queue next frame. The viewport does not subscribe to
        /// Entities CollectionChanged, so programmatic add/remove/apply must call this or the change stays invisible
        /// until an unrelated event (selection, transform, scene switch) dirties it.</summary>
        private static void RequestViewportResubmit()
        {
            try { Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit(); } catch { }
        }

        private static void CollectInstances(IEnumerable<GameEntity> entities, string prefabPath, GameEntity exclude, List<GameEntity> outList)
        {
            if (entities == null) return;
            foreach (var e in entities)
            {
                if (e == null) continue;
                if (!ReferenceEquals(e, exclude) && string.Equals(e.PrefabPath, prefabPath, StringComparison.OrdinalIgnoreCase))
                    outList.Add(e);
                CollectInstances(e.Children, prefabPath, exclude, outList);
            }
        }

        private static void RemoveFromScene(GameEntity entity)
        {
            try { entity.SyncEngineStateRecursive(false); } catch { }   // unregister from the engine
            if (entity.Parent != null) entity.Parent.Children.Remove(entity);
            else entity.Scene?.Entities.Remove(entity);
        }

        private static string Resolve(string path, ProjectData project)
            => Path.IsPathRooted(path) ? path : Path.Combine(project?.Path ?? "", path ?? "");

        private static string ToProjectRelative(string full, string projectPath)
        {
            try
            {
                if (string.IsNullOrEmpty(projectPath) || !full.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase)) return full;
                return full.Substring(projectPath.Length).TrimStart('\\', '/').Replace('\\', '/');
            }
            catch { return full; }
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder();
            foreach (var c in (name ?? "Prefab")) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.Length == 0 ? "Prefab" : sb.ToString();
        }
    }
}
