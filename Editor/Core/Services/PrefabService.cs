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
        /// renders immediately). Returns the new instance, or null.</summary>
        public GameEntity InstantiatePrefab(string prefabPath, Scene scene, GameEntity parent = null)
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
                parent.Children.Add(entity);
                entity.SyncEngineStateRecursive(parent.ActiveInHierarchy);
            }
            else
            {
                scene.AddEntity(entity);   // AddEntity syncs to the engine -> the instance renders
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

            var fresh = InstantiatePrefab(prefabPath, scene, parent);
            if (fresh?.Transform != null && oldT != null)
            {
                fresh.Transform.LocalPosition = oldT.LocalPosition;
                fresh.Transform.LocalRotation = oldT.LocalRotation;
                fresh.Transform.LocalScale = oldT.LocalScale;
            }
            return fresh;
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

        private void PropagateToOtherInstances(GameEntity source)
        {
            var scene = source.Scene;
            if (scene == null) return;
            var targets = new List<GameEntity>();
            CollectInstances(scene.Entities, source.PrefabPath, source, targets);
            foreach (var t in targets) RevertInstance(t);   // re-instantiates from the just-saved prefab, keeps transform
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
