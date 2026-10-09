using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor.Core.Audio;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Shell;
using VortexEditor.Shell.Prefab;
using CoreAssetActions = Editor.Core.Assets.AssetActions;

namespace VortexEditor.Services
{
    /// <summary>
    /// Default ("plain double-click") actions on assets, shared by the Asset Browser, the viewport drop target and the
    /// hierarchy drop target: add a model / prefab / primitive to the scene (optionally at a world position and under
    /// a parent), open a scene, open a script in the code editor, open an asset's editor, audition audio …
    /// </summary>
    public static class AssetActions
    {
        private static readonly string[] ModelExt = { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".3ds", ".blend", ".vmesh" };

        public static string ProjectRoot => ProjectData.Current?.Path;

        public static bool IsPrimitive(string path) => path != null && path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase);
        public static bool IsVfx(string path) => !string.IsNullOrEmpty(path) && path.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase);
        public static bool IsModel(string path) => !string.IsNullOrEmpty(path) && !IsPrimitive(path) && Array.IndexOf(ModelExt, Path.GetExtension(path).ToLowerInvariant()) >= 0;
        public static bool IsPrefab(string path)
        {
            string ext = string.IsNullOrEmpty(path) ? "" : Path.GetExtension(path).ToLowerInvariant();
            return ext == ".ventity" || ext == ".vprefab";
        }

        /// <summary>True for everything <see cref="AddToScene"/> can place (drop targets use it for DragOver).</summary>
        public static bool CanAddToScene(string path) => IsPrimitive(path) || IsModel(path) || IsPrefab(path);

        /// <summary>Project-relative (the drag payload form) or absolute → absolute; pseudo paths unchanged.</summary>
        public static string ToFullPath(string path)
        {
            if (string.IsNullOrEmpty(path) || IsPrimitive(path) || path.Contains(':') && !Path.IsPathRooted(path)) return path;
            if (Path.IsPathRooted(path)) return path;
            string root = ProjectRoot;
            return string.IsNullOrEmpty(root) ? path : Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        }

        // ============================================================ add to scene
        /// <summary>Add a model (.glb/.gltf/.fbx/.obj …), prefab (.ventity) or primitive ("Primitive:Cube") to the active
        /// scene — ONE undo step, selected afterwards. <paramref name="position"/> is a WORLD position; null = in front of
        /// the editor camera. With a <paramref name="parent"/> the entity becomes its child (world position kept).
        /// Returns the created entity (null when the asset can't be placed).</summary>
        public static GameEntity AddToScene(string fullPathOrPrimitive, Vector3? position = null, GameEntity parent = null)
        {
            if (string.IsNullOrEmpty(fullPathOrPrimitive)) return null;
            var scene = parent?.Scene ?? ProjectData.Current?.ActiveScene;
            if (scene == null) { EditorCommands.Toast("No active scene — open or create a scene first"); return null; }
            try
            {
                string path = ToFullPath(fullPathOrPrimitive);
                GameEntity root;
                bool attached = false;
                float radius = 0.6f;
                if (IsPrimitive(path)) root = BuildPrimitive(scene, path.Substring("Primitive:".Length));
                else if (!File.Exists(path)) return null;
                else if (IsPrefab(path))
                {
                    // the prefab workflow builds + places the linked instance (new ids, engine sync, selection, toast);
                    // at the scene root that add is already an undo step, under a parent the prefab service adds raw
                    root = PrefabWorkflow.PlaceInScene(path, parent, select: true);
                    if (root == null) return null;
                    scene = root.Scene ?? scene;
                    attached = true;
                    radius = 1.5f;
                }
                else if (IsModel(path)) root = BuildModel(scene, path, out radius);
                else if (IsVfx(path)) root = BuildVfx(scene, path);
                else return null;
                if (root == null) return null;

                Vector3 world = position ?? InFrontOfCamera(Math.Clamp(radius * 2.4f + 1.2f, 3f, 80f));
                root.Transform.LocalPosition = parent != null ? WorldToLocal(parent, world) : world;

                var cmd = new AddEntityCommand(scene, parent, root, "Add " + root.Name);
                if (attached)
                {
                    // exactly one undo step per placement: record the raw add under a parent ourselves
                    if (parent != null) UndoRedoManager.Instance.Execute(cmd, execute: false);
                }
                else
                {
                    UndoRedoManager.Instance.Execute(cmd);
                    if (!cmd.IsAttached) cmd.Execute();   // an undo step was already executing (nested) — attach anyway
                }

                if (parent != null) parent.IsExpanded = true;
                SelectionService.Instance.Select(root);
                SceneRenderService.RuntimeDirty = true;
                EditorViewportSession.RequestResubmit();
                try { SceneRenderService.Instance.PreloadSceneAssets(scene); } catch { }
                return root;
            }
            catch (Exception ex) { EditorCommands.Fail("Add to scene", ex); return null; }
        }

        /// <summary>An entity that plays the effect: Particle System with the .vfx, playing on start and previewed in the
        /// editor viewport (one-shot effects replay on demand from the inspector card).</summary>
        private static GameEntity BuildVfx(Scene scene, string full)
        {
            var e = new GameEntity(scene, Path.GetFileNameWithoutExtension(full));
            e.AddComponentDirect(new Editor.ECS.Components.Rendering.ParticleSystem(e)
            {
                VfxPath = AssetFileOps.ToRelative(full).Replace('\\', '/'), PlayOnStart = true, PreviewInEditor = true
            });
            return e;
        }

        private static GameEntity BuildPrimitive(Scene scene, string prim)
        {
            if (string.IsNullOrWhiteSpace(prim)) prim = "Cube";
            var match = AssetKinds.Primitives.FirstOrDefault(p => string.Equals(p, prim, StringComparison.OrdinalIgnoreCase));
            prim = match ?? prim;
            var e = new GameEntity(scene, prim);
            e.AddComponentDirect(new MeshRenderer(e) { MeshPath = "Primitive:" + prim });   // same MeshPath as Scene.CreatePrimitive
            return e;
        }

        /// <summary>Port of the Windows editor's drag-into-scene factory: one entity for single-submesh models, a
        /// container with one LOCKED child per submesh otherwise; each part binds its per-submesh .vmat
        /// (materials/submesh_N.vmat) when present, else the best matching texture next to the model; the model's
        /// default placement scale (.vimport) and an auto-Animator when the model has extracted animations/*.vanim.</summary>
        private static GameEntity BuildModel(Scene scene, string full, out float radius)
        {
            radius = 0.6f;
            string projectPath = ProjectRoot ?? "";
            string rel = AssetFileOps.ToRelative(full);
            string name = Path.GetFileNameWithoutExtension(full);
            string ext = Path.GetExtension(full).ToLowerInvariant();

            VortexAPI.SubmeshImportData[] subs = null;
            if (ext != ".vmesh")
            {
                // through the render cache: one load per model and session, no import per placement (#357)
                try { subs = SceneRenderService.LoadModelSubmeshes(rel); } catch { subs = null; }
            }
            float defScale = 1f;
            try { defScale = ModelImportSettings.LoadDefaultScale(full); } catch { }
            radius = Radius(subs) * defScale;

            GameEntity root = new GameEntity(scene, name);
            if (subs != null && subs.Length > 1)
            {
                var textures = CoreAssetActions.FindTexturesForModel(full);
                string[] names = null;
                try { names = VortexAPI.GetSubmeshNames(full, subs.Length); } catch { }
                for (int i = 0; i < subs.Length; i++)
                {
                    var sm = subs[i];
                    string childName = names != null && i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : "Submesh_" + i;
                    var child = new GameEntity(scene, childName) { IsLockedToParent = true };
                    string submeshPath = rel + "#submesh" + i;
                    var mr = new MeshRenderer(child) { MeshPath = submeshPath, MaterialHandle = sm.MaterialId };
                    BindSubmeshVmat(mr, rel, i, projectPath);
                    if (string.IsNullOrEmpty(mr.MaterialPath) && textures.Count > 0 && !HasOwnTexture(sm.MaterialId)) BindTexture(mr, sm.MaterialId, TextureForSubmesh(childName, textures));
                    if (sm.MaterialId >= 0) SceneRenderService.RegisterMaterialForMeshPath(submeshPath, sm.MaterialId);
                    child.AddComponentDirect(mr);
                    child.Parent = root;             // raw wiring: the whole placement is ONE undo step
                    root.Children.Add(child);
                }
            }
            else
            {
                long matId = subs != null && subs.Length == 1 ? subs[0].MaterialId : -1;
                var mr = new MeshRenderer(root) { MeshPath = rel };
                if (matId >= 0) mr.MaterialHandle = matId;
                BindSubmeshVmat(mr, rel, 0, projectPath);
                if (string.IsNullOrEmpty(mr.MaterialPath) && !HasOwnTexture(matId))
                {
                    var textures = CoreAssetActions.FindTexturesForModel(full);
                    if (textures.Count > 0) BindTexture(mr, matId, textures[0]);
                }
                if (matId >= 0) SceneRenderService.RegisterMaterialForMeshPath(rel, matId);
                root.AddComponentDirect(mr);
            }
            if (Math.Abs(defScale - 1f) > 0.0001f) root.Transform.LocalScale = new Vector3(defScale, defScale, defScale);
            TryAddAnimator(root, rel);
            return root;
        }

        private static float Radius(VortexAPI.SubmeshImportData[] subs)
        {
            float r = 0.6f;
            if (subs == null) return r;
            foreach (var s in subs)
            {
                try
                {
                    if (s.MeshId >= 0 && VortexAPI.GetMeshBounds(s.MeshId, out float sx, out float sy, out float sz))
                        r = Math.Max(r, 0.5f * (float)Math.Sqrt(sx * sx + sy * sy + sz * sz));
                }
                catch { }
            }
            return r;
        }

        private static void BindSubmeshVmat(MeshRenderer mr, string modelRel, int index, string projectPath)
        {
            try
            {
                string vmatRel = Path.Combine(Path.GetDirectoryName(modelRel) ?? "", "materials", "submesh_" + index + ".vmat").Replace('\\', '/');
                if (!string.IsNullOrEmpty(projectPath) && File.Exists(Path.Combine(projectPath, vmatRel))) mr.MaterialPath = vmatRel;
            }
            catch { }
        }

        /// <summary>The import material already carries the model's own albedo map (#351): the folder-scan fallback
        /// must not replace it with whatever image lies next to the file.</summary>
        private static bool HasOwnTexture(long materialId)
        {
            if (materialId < 0) return false;
            try { return VortexAPI.HasMaterialTexture(materialId); } catch { return false; }
        }

        private static void BindTexture(MeshRenderer mr, long materialId, string texPath)
        {
            if (string.IsNullOrEmpty(texPath)) return;
            try
            {
                // a GRAPHICS texture id (cached per file): LoadTextureResource returned a resource-manager handle from
                // another id space, which bound a random texture or none (#351)
                long tex = MaterialService.ImportTextureCached(texPath);
                if (tex >= 0 && materialId >= 0) VortexAPI.SetMaterialAlbedoTexture(materialId, tex);
            }
            catch { }
            mr.TexturePath = AssetFileOps.ToRelative(texPath);
        }

        private static string TextureForSubmesh(string submeshName, List<string> textures)
        {
            if (textures == null || textures.Count == 0) return null;
            string n = (submeshName ?? "").ToLowerInvariant().Replace(" ", "_");
            if (n.Length > 0) foreach (var t in textures) if (Path.GetFileName(t).ToLowerInvariant().Contains(n)) return t;
            return textures[0];
        }

        private static void TryAddAnimator(GameEntity e, string meshRel)
        {
            try
            {
                if (e.GetComponent<Editor.ECS.Components.Animation.Animator>() != null) return;
                var animator = new Editor.ECS.Components.Animation.Animator(e);
                if (Editor.Core.Animation.AnimationService.TryPopulateClipsFromModel(animator, meshRel)) e.AddComponentDirect(animator);
            }
            catch { }
        }

        // ------------------------------------------------------------ placement
        /// <summary>A point <paramref name="distance"/> units in front of the editor camera (the scene view's look direction).</summary>
        public static Vector3 InFrontOfCamera(float distance = 5f)
        {
            var cam = EditorViewportSession.Main?.Camera ?? EditorCameraController.Instance;
            double yaw = cam.Yaw * Math.PI / 180.0, pitch = cam.Pitch * Math.PI / 180.0;
            double fx = Math.Sin(yaw) * Math.Cos(pitch), fy = -Math.Sin(pitch), fz = Math.Cos(yaw) * Math.Cos(pitch);
            return new Vector3(Round(cam.PositionX + (float)(fx * distance)), Round(cam.PositionY + (float)(fy * distance)), Round(cam.PositionZ + (float)(fz * distance)));
        }

        private static float Round(float v) => (float)Math.Round(v, 2);

        /// <summary>World position → <paramref name="parent"/>'s local space (the renderer's row-major TRS, Euler ZXY).</summary>
        public static Vector3 WorldToLocal(GameEntity parent, Vector3 world)
        {
            var m = WorldMatrix(parent);
            if (!System.Numerics.Matrix4x4.Invert(m, out var inv)) return world;
            var p = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(world.X, world.Y, world.Z), inv);
            return new Vector3(p.X, p.Y, p.Z);
        }

        private static System.Numerics.Matrix4x4 WorldMatrix(GameEntity e)
        {
            if (e?.Transform == null) return System.Numerics.Matrix4x4.Identity;
            var local = LocalMatrix(e.Transform);
            return e.Parent != null ? local * WorldMatrix(e.Parent) : local;
        }

        private static System.Numerics.Matrix4x4 LocalMatrix(Editor.ECS.Components.Transform t)
        {
            // identical to SceneRenderService.BuildWorldMatrix (R = Rz * Rx * Ry, scale first, translation last row)
            var pos = t.LocalPosition; var rot = t.LocalRotation; var s = t.LocalScale;
            double rx = rot.X * Math.PI / 180.0, ry = rot.Y * Math.PI / 180.0, rz = rot.Z * Math.PI / 180.0;
            float cx = (float)Math.Cos(rx), sx = (float)Math.Sin(rx), cy = (float)Math.Cos(ry), sy = (float)Math.Sin(ry), cz = (float)Math.Cos(rz), sz = (float)Math.Sin(rz);
            float r00 = cz * cy + sz * sx * sy, r01 = sz * cx, r02 = -cz * sy + sz * sx * cy;
            float r10 = -sz * cy + cz * sx * sy, r11 = cz * cx, r12 = sz * sy + cz * sx * cy;
            float r20 = cx * sy, r21 = -sx, r22 = cx * cy;
            return new System.Numerics.Matrix4x4(
                s.X * r00, s.X * r01, s.X * r02, 0,
                s.Y * r10, s.Y * r11, s.Y * r12, 0,
                s.Z * r20, s.Z * r21, s.Z * r22, 0,
                pos.X, pos.Y, pos.Z, 1);
        }

        /// <summary>Attach a built entity subtree to the scene (or a parent) as one undo step. Undo detaches it from
        /// the engine and the hierarchy (like Scene.RemoveEntity), Redo re-attaches it.</summary>
        private sealed class AddEntityCommand : UndoableCommandBase
        {
            private readonly Scene _scene; private readonly GameEntity _parent, _entity; private readonly string _name;
            public AddEntityCommand(Scene scene, GameEntity parent, GameEntity entity, string name) { _scene = scene; _parent = parent; _entity = entity; _name = name; }
            public override string Name => _name;
            public bool IsAttached => _parent != null ? _parent.Children.Contains(_entity) : _scene.Entities.Contains(_entity);

            public override void Execute()
            {
                if (IsAttached) return;
                _entity.Scene = _scene;
                if (_parent != null)
                {
                    _entity.Parent = _parent;
                    _parent.Children.Add(_entity);
                    _entity.SyncEngineStateRecursive(_parent.ActiveInHierarchy);
                }
                else
                {
                    _scene.Entities.Add(_entity);
                    _entity.SyncEngineStateRecursive(_scene.IsActive);
                }
                Touch();
            }

            public override void Undo()
            {
                try { _entity.SyncEngineStateRecursive(false); } catch { }
                if (_parent != null) _parent.Children.Remove(_entity); else _scene.Entities.Remove(_entity);
                var sel = SelectionService.Instance.SelectedEntity;
                for (var e = sel; e != null; e = e.Parent)
                    if (ReferenceEquals(e, _entity)) { SelectionService.Instance.ClearSelection(); break; }
                Touch();
            }

            private void Touch()
            {
                try { _scene.IsDirty = true; } catch { }
                SceneRenderService.RuntimeDirty = true;
                EditorViewportSession.RequestResubmit();
            }
        }

        // ============================================================ open (plain double-click)
        /// <summary>The plain double-click action for any asset: models / prefabs / primitives → add to the scene,
        /// scene → open, script / shader → code editor, sound container / animation clip / material / UI screen /
        /// texture → its editor, audio clip → audition, folder → browse it, anything else → the OS default app.
        /// Returns false when the asset has no default action (e.g. it no longer exists).</summary>
        public static Task<bool> OpenDefault(string fullPath) => Task.FromResult(OpenDefaultCore(fullPath));

        private static bool OpenDefaultCore(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            try
            {
                if (IsPrimitive(fullPath)) return Placed(AddToScene(fullPath));
                string full = ToFullPath(fullPath);
                if (Directory.Exists(full))
                {
                    var browser = EditorCommands.Window?.AssetBrowser;
                    if (browser != null) browser.Navigate(full); else AssetNavigation.NavigateTo(full);
                    return true;
                }
                if (!File.Exists(full)) return false;
                if (IsPrefab(full)) return AddToScene(full) != null;   // the prefab workflow toasts itself
                if (IsModel(full)) return Placed(AddToScene(full));
                string ext = Path.GetExtension(full).ToLowerInvariant();
                switch (ext)
                {
                    case ".vscene": return OpenScene(full);
                    case ".cs": EditorCommands.OpenInIde(full); return true;
                    case ".vsndc": EditorWindows.SoundContainerEditor(full); return true;
                    case ".vanim": EditorWindows.AnimationEditor(full); return true;
                    case ".vmat": EditorWindows.MaterialEditor(full); return true;
                    case ".vui": EditorWindows.UiEditor(full); return true;
                }
                if (AssetKinds.Is(ext, AssetKinds.AudioExt)) { Audition(full); return true; }
                if (AssetKinds.Is(ext, AssetKinds.ShaderExt)) { EditorCommands.OpenInIde(full); return true; }
                if (AssetKinds.Is(ext, AssetKinds.TextureExt)) { EditorWindows.TextureEditor(full); return true; }
                if (AssetKinds.Is(ext, AssetKinds.TextExt)) { AssetFileOps.OpenInTextEditor(full); return true; }
                AssetFileOps.OpenWithDefaultApp(full);
                return true;
            }
            catch (Exception ex) { EditorCommands.Fail("Open", ex); return false; }
        }

        private static bool Placed(GameEntity e)
        {
            if (e == null) return false;
            EditorCommands.Toast("Added " + e.Name + " to the scene");
            return true;
        }

        /// <summary>Load (if needed) and activate a .vscene — switches the editor's active scene.</summary>
        public static bool OpenScene(string fullPath)
        {
            var project = ProjectData.Current;
            if (project == null || !File.Exists(fullPath)) return false;
            try
            {
                var target = project.Scenes?.FirstOrDefault(s => s != null && !string.IsNullOrEmpty(s.FilePath) && AssetFileOps.PathsEqual(s.FilePath, fullPath));
                if (target == null)
                {
                    target = SceneService.Instance.LoadScene(fullPath);
                    if (target != null && !project.Scenes.Contains(target)) project.AddScene(target);
                }
                if (target == null) return false;
                EditorSession.Instance.ActivateScene(target);
                EditorCommands.Toast("Opened scene " + target.Name);
                return true;
            }
            catch (Exception ex) { EditorCommands.Fail("Open scene", ex); return false; }
        }

        // ============================================================ audio audition
        private static ulong _voice = VortexAudio.InvalidVoice;

        /// <summary>True while an audition voice started by <see cref="Audition"/> is live.</summary>
        public static bool IsAuditioning => _voice != VortexAudio.InvalidVoice;

        /// <summary>Play a clip (or roll one clip of a sound container) as an editor preview; replaces the previous one.</summary>
        public static void Audition(string fullPath)
        {
            StopAudition();
            try
            {
                string clip = ToFullPath(fullPath);
                float volume = 1f, pitch = 1f;
                if (SoundContainerService.IsContainerPath(clip))
                {
                    // container tiles roll a fresh clip each time — click twice, hear the variation
                    if (!SoundContainerService.Resolve(clip, out var rolled)) return;
                    clip = Path.Combine(ProjectRoot ?? "", rolled.ClipPath ?? "");
                    volume = rolled.VolumeScale; pitch = rolled.PitchScale;
                }
                if (!File.Exists(clip)) return;
                // stream: never fully decode a long clip on the UI thread; priority 0 so play-mode audio can't steal it
                _voice = VortexAudio.PlayVoice(clip, volume, pitch, 0f, loop: false, priority: 0, stream: true);
            }
            catch (Exception ex) { ConsoleService.Instance.LogWarning("Audition failed: " + ex.Message); }
        }

        public static void StopAudition()
        {
            if (_voice == VortexAudio.InvalidVoice) return;
            try { VortexAudio.StopVoice(_voice); } catch { }
            _voice = VortexAudio.InvalidVoice;
        }
    }
}
