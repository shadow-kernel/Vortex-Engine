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

        /// <summary>
        /// Create a prefab (.ventity) DIRECTLY from a model asset (.glb/.fbx/.obj/…) WITHOUT ever placing a
        /// throwaway instance in the scene. The entity is built IN MEMORY (Scene = null, never synced to the
        /// native engine, never touches the undo stack), faithfully mirroring the drag-into-scene factory
        /// (<see cref="Editor.Editors.WorldEditor.DragDrop.ViewportDropHandler"/>): single- vs multi-submesh
        /// container, per-submesh .vmat binding, the model's default placement scale, and an auto-Animator when
        /// the model has extracted <c>animations/*.vanim</c> clips. Then it serializes that entity as a prefab
        /// template. This is the answer to "I don't want to drop a mesh into the scene just to Save-as-Prefab".
        /// Returns the absolute .ventity path, or null on failure.
        /// </summary>
        public string CreatePrefabFromModel(string modelPath, string prefabName = null)
        {
            var project = ProjectData.Current;
            if (string.IsNullOrEmpty(modelPath) || project == null) return null;

            string full = Resolve(modelPath, project);
            if (!File.Exists(full)) return null;
            string rel = ToProjectRelative(full, project.Path);
            string name = string.IsNullOrWhiteSpace(prefabName) ? Path.GetFileNameWithoutExtension(full) : prefabName;

            // Root entity — NO scene (orphan). Serialization never reads Scene ([IgnoreDataMember]); with no
            // EntityId the MeshRenderer setters' SyncToEngine is a no-op, so nothing hits the native engine.
            var root = new GameEntity(name);

            // Multi-submesh models become a parent container with one LOCKED child per submesh (exactly like the
            // drop handler) so materials map 1:1; single-submesh models put the MeshRenderer on the root.
            int submeshCount = 1;
            try { submeshCount = Editor.DllWrapper.VortexAPI.GetSubmeshCount(full); } catch { submeshCount = 1; }

            if (submeshCount > 1)
            {
                string[] submeshNames = null;
                try { submeshNames = Editor.DllWrapper.VortexAPI.GetSubmeshNames(full, submeshCount); } catch { }
                for (int i = 0; i < submeshCount; i++)
                {
                    string childName = (submeshNames != null && i < submeshNames.Length && !string.IsNullOrEmpty(submeshNames[i]))
                        ? submeshNames[i] : ("Submesh_" + i);
                    var child = new GameEntity(childName) { IsLockedToParent = true };
                    var childMr = new ECS.Components.Rendering.MeshRenderer(child) { MeshPath = rel + "#submesh" + i };
                    BindSubmeshVmat(childMr, rel, i, project.Path);
                    child.AddComponentDirect(childMr);                 // non-undoable, no engine sync
                    child.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);
                    // Raw parent/child wiring — AddChild() would push a CollectionAddCommand for a throwaway entity.
                    child.Parent = root;
                    root.Children.Add(child);
                }
            }
            else
            {
                var mr = new ECS.Components.Rendering.MeshRenderer(root) { MeshPath = rel };
                BindSubmeshVmat(mr, rel, 0, project.Path);
                root.AddComponentDirect(mr);
            }

            // Model's default placement scale (Model Editor -> .vimport sidecar); a no-op (1.0) otherwise.
            try
            {
                float defScale = ModelImportSettings.LoadDefaultScale(full);
                if (Math.Abs(defScale - 1f) > 0.0001f)
                    root.Transform.LocalScale = new ECS.Vector3(defScale, defScale, defScale);
            }
            catch { }

            // Animated model with extracted animations/*.vanim -> pre-fill an Animator so instances move out of
            // the box (PlayOnStart defaults true). No-op if the model has no extracted clips yet.
            TryAddAnimatorForModel(root, rel);

            var dir = Path.Combine(project.Path, "Assets", "Prefabs");
            Directory.CreateDirectory(dir);
            var file = UniquePrefabFile(dir, Sanitize(name));
            WriteTemplate(root, file);   // normalizes asset paths to project-relative + writes JSON template
            try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
            return file;
        }

        /// <summary>Create a blank prefab (.ventity) — just a named entity with a Transform — in Assets/Prefabs,
        /// built in memory and serialized as a proper GameEntity template (so it can actually be instantiated,
        /// unlike a hand-rolled JSON stub). Returns the absolute path, or null if no project is open.</summary>
        public string CreateEmptyPrefab(string prefabName = null)
        {
            var project = ProjectData.Current;
            if (project == null) return null;
            var name = string.IsNullOrWhiteSpace(prefabName) ? "NewPrefab" : prefabName;
            var root = new GameEntity(name);   // just a Transform — a blank reusable template
            var dir = Path.Combine(project.Path, "Assets", "Prefabs");
            Directory.CreateDirectory(dir);
            var file = UniquePrefabFile(dir, Sanitize(name));
            WriteTemplate(root, file);
            try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
            return file;
        }

        /// <summary>Bind the per-submesh sidecar .vmat (materials/submesh_N.vmat) if it exists on disk — the
        /// single source of truth the engine renders from and that Model-Editor saves persist to.</summary>
        private static void BindSubmeshVmat(ECS.Components.Rendering.MeshRenderer mr, string modelRel, int submeshIndex, string projectPath)
        {
            try
            {
                string modelDir = Path.GetDirectoryName(modelRel) ?? "";
                string vmatRel = Path.Combine(modelDir, "materials", "submesh_" + submeshIndex + ".vmat").Replace('\\', '/');
                if (!string.IsNullOrEmpty(projectPath) && File.Exists(Path.Combine(projectPath, vmatRel)))
                    mr.MaterialPath = vmatRel;
            }
            catch { }
        }

        /// <summary>Give an in-memory entity a pre-filled Animator IF the model has sibling animations/*.vanim
        /// clips (mirrors ViewportDropHandler.TryAddAnimatorForModel, but for an orphan prefab-template entity).</summary>
        private static void TryAddAnimatorForModel(GameEntity entity, string meshRel)
        {
            try
            {
                if (entity == null || string.IsNullOrEmpty(meshRel) ||
                    meshRel.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                    return;
                if (entity.GetComponent<ECS.Components.Animation.Animator>() != null) return;
                var animator = new ECS.Components.Animation.Animator(entity);
                if (Editor.Core.Animation.AnimationService.TryPopulateClipsFromModel(animator, meshRel))
                    entity.AddComponentDirect(animator);
            }
            catch { }
        }

        /// <summary>A non-clobbering prefab path: &lt;base&gt;.ventity, then &lt;base&gt;_1.ventity, …</summary>
        private static string UniquePrefabFile(string dir, string baseName)
        {
            string file = Path.Combine(dir, baseName + PrefabExtension);
            int n = 1;
            while (File.Exists(file)) file = Path.Combine(dir, baseName + "_" + (n++) + PrefabExtension);
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
            // VFS-aware (pak OR disk) so a future runtime Instantiate works in shipped builds too;
            // DataSerializer.LoadFromJson below already reads through the VFS.
            if (!AssetVfs.Exists(full)) return null;

            var entity = DataSerializer.LoadFromJson<GameEntity>(full);
            if (entity == null) return null;
            entity.RegenerateIds();
            // Scene refs must be set on the WHOLE subtree (deserialize leaves children with the root's
            // then-null Scene) — otherwise children register into the native DEFAULT scene instead of the
            // active one, unlike scene-loaded entities which are fixed recursively on load.
            SetSceneRecursive(entity, scene);
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

        private static void SetSceneRecursive(GameEntity e, Scene scene)
        {
            if (e == null) return;
            e.Scene = scene;
            if (e.Children != null)
                foreach (var c in e.Children) SetSceneRecursive(c, scene);
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

            // Carry over per-instance components the TEMPLATE doesn't have (like the transform above,
            // they are instance overrides): an Animator or Script configured on the instance used to be
            // silently DELETED by every Apply/Revert/isolated-editor-Save — the character snapped back to
            // bind pose and its behaviours vanished. Only managed-only component types are safe to MOVE
            // (the old entity is discarded); engine-backed ones (MeshRenderer/colliders) come from the template.
            if (fresh != null)
            {
                try { CarryOverInstanceComponents(instance, fresh); } catch { }
            }
            return fresh;
        }

        /// <summary>Move Animator/Script components that exist on the old instance root but have no same-type
        /// counterpart in the fresh template copy. These are pure managed data, so re-homing the object is safe.</summary>
        private static void CarryOverInstanceComponents(GameEntity oldInstance, GameEntity fresh)
        {
            if (oldInstance?.Components == null || fresh == null) return;
            var toMove = new List<ECS.Component>();
            foreach (var c in oldInstance.Components)
            {
                if (c is ECS.Components.Animation.Animator && fresh.GetComponent<ECS.Components.Animation.Animator>() == null)
                    toMove.Add(c);
                else if (c is ECS.Components.Scripting.Script s && !HasScript(fresh, s))
                    toMove.Add(c);
            }
            foreach (var c in toMove)
            {
                oldInstance.Components.Remove(c);
                c.Entity = fresh;
                fresh.Components.Add(c);
            }
        }

        private static bool HasScript(GameEntity e, ECS.Components.Scripting.Script s)
        {
            foreach (var c in e.Components)
                if (c is ECS.Components.Scripting.Script other &&
                    string.Equals(other.ScriptPath, s.ScriptPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
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
            // Normalize asset paths to PROJECT-RELATIVE first (permanently — relative is the correct form
            // for the live entity too). The Asset Browser used to bake ABSOLUTE paths ("C:\...\model.fbx#submesh0")
            // into entities; frozen into a .ventity they break shipped builds (the pak keys are relative, so
            // mesh + skeleton lookups miss the VFS) and any moved/renamed project.
            NormalizeAssetPathsRecursive(entity);

            var saved = entity.PrefabPath;
            entity.PrefabPath = null;
            try { DataSerializer.SaveAsJson(entity, file); }
            finally { entity.PrefabPath = saved; }
        }

        /// <summary>Rewrite absolute under-project asset paths to project-relative on the whole subtree.
        /// MeshRenderer paths are rewritten via their private backing fields (the public setters reload
        /// native handles — pointless churn for a pure string normalization).</summary>
        private static void NormalizeAssetPathsRecursive(GameEntity e)
        {
            var projectPath = ProjectData.Current?.Path;
            if (e == null || string.IsNullOrEmpty(projectPath)) return;

            foreach (var comp in e.Components)
            {
                if (comp is ECS.Components.Rendering.MeshRenderer mr)
                {
                    NormalizeField(mr, "_meshPath", projectPath);
                    NormalizeField(mr, "_materialPath", projectPath);
                    NormalizeField(mr, "_texturePath", projectPath);
                    NormalizeField(mr, "_shaderPath", projectPath);
                }
                else if (comp is ECS.Components.Animation.Animator anim && anim.Clips != null)
                {
                    foreach (var clip in anim.Clips)
                        if (clip != null) clip.Path = ToProjectRelative(clip.Path ?? "", projectPath);
                }
                else if (comp is ECS.Components.Scripting.Script script)
                {
                    script.ScriptPath = ToProjectRelative(script.ScriptPath ?? "", projectPath);
                }
                else if (comp is ECS.Components.Audio.AudioSource audio)
                {
                    audio.AudioClipPath = ToProjectRelative(audio.AudioClipPath ?? "", projectPath);
                }
            }

            if (e.Children != null)
                foreach (var c in e.Children) NormalizeAssetPathsRecursive(c);
        }

        private static void NormalizeField(object target, string fieldName, string projectPath)
        {
            try
            {
                var f = target.GetType().GetField(fieldName,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (f == null) return;
                var val = f.GetValue(target) as string;
                if (string.IsNullOrEmpty(val)) return;
                var rel = ToProjectRelative(val, projectPath);
                if (!string.Equals(rel, val, StringComparison.Ordinal)) f.SetValue(target, rel);
            }
            catch { /* normalization is best-effort — never block a save */ }
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
