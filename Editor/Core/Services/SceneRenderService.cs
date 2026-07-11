using System;
using System.Collections.Generic;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services
{
    /// <summary>
    /// Manages rendering of scene entities in the viewport.
    /// Acts as bridge between Editor entities and Engine rendering.
    /// </summary>
    public class SceneRenderService : IDisposable
    {
        private static SceneRenderService _instance;
        public static SceneRenderService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new SceneRenderService();
                return _instance;
            }
        }

        /// <summary>
        /// Set when a script/runtime change invalidated the baked render queue (Transform.SyncToEngine
        /// during play). The submit-once GameHost loop re-submits the scene and clears it — without this,
        /// script-moved entities render frozen in shipped games (editor play re-submits every frame anyway).
        /// </summary>
        public static bool RuntimeDirty;

        /// <summary>How the EDIT-mode viewport presents non-world render layers (#175: 1 = FP viewmodel,
        /// 2 = third-person only). Play mode ignores this — while playing the layers always behave like
        /// the shipped game (1 = FP overlay pass, 2 = skipped for the local player).</summary>
        public enum ViewmodelPreviewMode
        {
            /// <summary>Default build view: FP meshes are NOT rendered (they belong to the game's FP pass),
            /// third-person-only meshes render as normal world geometry.</summary>
            Hidden,
            /// <summary>Placement mode (viewport toolbar toggle): FP meshes render as plain, depth-tested
            /// world geometry so they can be positioned; no FP overlay pass runs.</summary>
            AsWorld,
            /// <summary>"FP Preview (In-Game)" view mode: submit exactly like play mode — layer 1 goes to
            /// the native FP overlay pass (own FOV, cleared depth), layer 2 is skipped. The viewport shows
            /// the frame the game would render.</summary>
            GameView,
        }

        /// <summary>Edit-mode presentation of the FP/3P layers; set by the viewport (View dropdown +
        /// toolbar toggle). Static so the GameHost path (which never touches it) keeps the default.</summary>
        public static ViewmodelPreviewMode EditorViewmodelPreview = ViewmodelPreviewMode.Hidden;

        /// <summary>Set while the in-game DEBUG FREECAM is active (editor play / GameHost debug builds):
        /// the render view has detached from the player and is looking AT them, so render like another
        /// camera — third-person body (layer 2) visible, first-person viewmodel (layer 1) hidden.
        /// Cleared the instant the freecam exits. Never set in a shipped Release.</summary>
        public static bool DebugThirdPersonView;

        /// <summary>"We are the game, not the editor build view": editor play (viewport/game window)
        /// sets IsPlaying via PlayModeService.Play(); the standalone player additionally always sets
        /// NativeGameHostRunning before RunGameHost — belt and braces so a shipped game can NEVER lose
        /// its viewmodel to the edit-mode layer presentation.</summary>
        private static bool IsPlayLike =>
            PlayModeService.Instance.IsPlaying || PlayModeService.Instance.NativeGameHostRunning;

        /// <summary>Whether an entity's SUBTREE is part of the edit-viewport render (eye toggle +
        /// activeSelf cascade). The pick path (RaycastService) prunes recursion with this so an
        /// invisible entity never swallows clicks or gizmo drags — pick == render.</summary>
        public static bool IsEditorSubtreeVisible(GameEntity entity)
        {
            if (entity == null || !entity.IsActive) return false;
            if (entity.IsHiddenInEditor && !IsPlayLike) return false;
            return true;
        }

        /// <summary>Whether an entity's OWN mesh is drawn — and therefore pickable — under the current
        /// layer presentation. Mirrors SubmitEntity's layer branch exactly: layer-1 meshes are hidden in
        /// the default build view, and in FP Preview / play they draw with the FP overlay projection
        /// (clicks would misalign), so they are only pickable in the AsWorld placement mode. Non-mesh
        /// entities stay pickable (icons/gizmos represent them).</summary>
        public static bool IsEditorMeshPickable(GameEntity entity)
        {
            var mr = entity?.GetComponent<MeshRenderer>();
            if (mr == null) return true;
            int layer = mr.RenderLayer;
            if (layer <= 0 || layer > 2) return true;
            if (IsPlayLike || EditorViewmodelPreview == ViewmodelPreviewMode.GameView)
                return false;                                        // 2 = not drawn; 1 = FP projection, misaligned
            if (EditorViewmodelPreview == ViewmodelPreviewMode.Hidden)
                return layer != 1;                                   // FP meshes aren't drawn -> not pickable
            return true;                                             // AsWorld: drawn as world geometry
        }

        /// <summary>
        /// Asset-pipeline diagnostics, opt-in via VORTEX_VERBOSE_LOG=1. With a VS debugger attached EVERY
        /// Debug.WriteLine is a ~0.5-2ms cross-process round-trip — this service used to log per asset
        /// during scene load (250+ writes for a real level), which alone added seconds of F5-only stall
        /// while the same build loaded instantly standalone.
        /// </summary>
        private static readonly bool _verboseLog =
            Environment.GetEnvironmentVariable("VORTEX_VERBOSE_LOG") == "1";
        private static void Log(string msg)
        {
            if (_verboseLog) System.Diagnostics.Debug.WriteLine(msg);
        }

        private readonly Dictionary<Guid, long> _entityMeshes = new Dictionary<Guid, long>();
        private static int _meshDbg; // diagnostic: log first few mesh creations
        private static int _submitN, _ssDbg; // diagnostic: count submits per SubmitScene
        private readonly Dictionary<Guid, long> _entityMaterials = new Dictionary<Guid, long>();
        
        // Track mesh paths to detect changes
        private readonly Dictionary<Guid, string> _entityMeshPaths = new Dictionary<Guid, string>();
        
        // Material color cache for dirty checking
        private readonly Dictionary<Guid, (float r, float g, float b, float a)> _entityMaterialColors = 
            new Dictionary<Guid, (float r, float g, float b, float a)>();

        // STATIC cache: Map mesh paths to their imported material IDs
        // This survives entity serialization/deserialization
        private static readonly Dictionary<string, long> _meshPathToMaterialId = new Dictionary<string, long>();

        // FAST cache: resolved .vmat MaterialPath -> engine material id. The submit-once render loop calls
        // GetOrCreateMaterial for EVERY entity EVERY frame whenever any dynamic entity (e.g. the first-person
        // viewmodel) is moving. Without this, each call did a per-entity filesystem AssetVfs.Exists + Path.Combine
        // — with ~360 objects that was ~60us x 360 = the CPU bottleneck capping the scene at ~30 FPS. The material
        // id is stable at runtime (live edits mutate the native material in place, keeping the same id), so caching
        // by path is safe. Cleared on scene (pre)load.
        private static readonly Dictionary<string, long> _vmatPathCache = new Dictionary<string, long>();


        /// <summary>
        /// Register a material for a mesh path (called during import)
        /// </summary>
        public static void RegisterMaterialForMeshPath(string meshPath, long materialId)
        {
            if (!string.IsNullOrEmpty(meshPath) && materialId >= 0)
            {
                _meshPathToMaterialId[meshPath] = materialId;
                Log($"[SceneRenderService] Registered material {materialId} for mesh path: {meshPath}");
            }
        }

        /// <summary>
        /// Register a mesh ID for a submesh path (called during import to avoid re-import)
        /// </summary>
        public static void RegisterMeshIdForPath(string meshPath, long meshId)
        {
            if (!string.IsNullOrEmpty(meshPath) && meshId >= 0)
            {
                _submeshMeshCache[meshPath] = meshId;
                Log($"[SceneRenderService] Registered mesh {meshId} for path: {meshPath}");
            }
        }

        /// <summary>
        /// Get the material ID for a mesh path (if one was imported)
        /// </summary>
        public static long GetMaterialForMeshPath(string meshPath)
        {
            if (!string.IsNullOrEmpty(meshPath) && _meshPathToMaterialId.TryGetValue(meshPath, out long materialId))
            {
                return materialId;
            }
            return -1;
        }

        /// <summary>Apply <paramref name="apply"/> to EVERY live scene material of a model's submesh, matched by the
        /// ACTUAL registered mesh-path key resolved to an absolute file — so a material edit reaches the object no
        /// matter how it stored its model path (project-relative, absolute, or a prefab that stored just the file
        /// name). This is what makes an edited material's colour update in the live viewport / placed instances, where
        /// a single fixed-key lookup missed prefab-placed models. Returns how many live materials were updated.</summary>
        public static int ApplyToLiveMaterialsForModel(string modelAbsPath, int submeshIndex, Action<long> apply)
        {
            if (string.IsNullOrEmpty(modelAbsPath) || apply == null) return 0;
            string modelFull;
            try { modelFull = System.IO.Path.GetFullPath(modelAbsPath); } catch { modelFull = modelAbsPath; }
            var proj = Data.ProjectData.Current?.Path;
            int n = 0;
            // Snapshot the keys: apply() only mutates native materials, not the dictionary, but be defensive.
            foreach (var kv in new List<KeyValuePair<string, long>>(_meshPathToMaterialId))
            {
                var key = kv.Key;
                int hash = key.LastIndexOf('#');
                if (hash <= 0 || !(key.Length > hash + 7 && key.Substring(hash + 1, 7) == "submesh")) continue;
                if (!int.TryParse(key.Substring(hash + 8), out int idx) || idx != submeshIndex) continue;

                var basePath = key.Substring(0, hash);
                bool match = false;
                try
                {
                    var baseAbs = System.IO.Path.IsPathRooted(basePath) || string.IsNullOrEmpty(proj)
                        ? basePath : System.IO.Path.Combine(proj, basePath);
                    match = string.Equals(System.IO.Path.GetFullPath(baseAbs), modelFull, StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                // Match on the RESOLVED absolute path only — NEVER a bare filename fallback, which would apply this
                // model's edit to a DIFFERENT model that merely shares a file name (e.g. two washer.glb) and corrupt it.

                if (match && kv.Value >= 0) { try { apply(kv.Value); n++; } catch { } }
            }
            return n;
        }

        private bool _isInitialized;

        private SceneRenderService() { }

        public void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;
        }

        public void Shutdown()
        {
            ClearAllRenderables();
            _isInitialized = false;
        }

        /// <summary>
        /// Preloads all textures and materials for entities in a scene.
        /// Should be called when a scene is activated.
        /// </summary>
        public void PreloadSceneAssets(Data.Scene scene)
        {
            if (scene == null) return;

            _vmatPathCache.Clear();   // fresh material resolution for the (re)loaded scene
            Log($"[SceneRenderService] Preloading assets for scene: {scene.Name}");
            var projectPath = Data.ProjectData.Current?.Path ?? "";

            PreloadEntitiesRecursive(scene.Entities, projectPath);
        }

        private void PreloadEntitiesRecursive(IEnumerable<GameEntity> entities, string projectPath)
        {
            foreach (var entity in entities)
            {
                var meshRenderer = entity.GetComponent<MeshRenderer>();
                if (meshRenderer != null && !string.IsNullOrEmpty(meshRenderer.TexturePath))
                {
                    PreloadTextureForEntity(entity.Id, meshRenderer, projectPath);
                }

                // Recursively preload children
                if (entity.Children != null && entity.Children.Count > 0)
                {
                    PreloadEntitiesRecursive(entity.Children, projectPath);
                }
            }
        }

        private void PreloadTextureForEntity(Guid entityId, MeshRenderer meshRenderer, string projectPath)
        {
            string meshPath = meshRenderer.MeshPath;
            
            // Check if we already have a material cached for this mesh path
            if (_meshPathToMaterialId.ContainsKey(meshPath))
            {
                return; // Already loaded
            }

            string texturePath = meshRenderer.TexturePath;
            if (string.IsNullOrEmpty(texturePath))
            {
                return;
            }

            // Build full path
            string fullTexturePath = texturePath;
            if (!System.IO.Path.IsPathRooted(texturePath))
            {
                fullTexturePath = System.IO.Path.Combine(projectPath, texturePath);
            }

            if (!AssetVfs.Exists(fullTexturePath))
            {
                Log($"[SceneRenderService] Texture not found: {fullTexturePath}");
                return;
            }

            try
            {
                // Import texture
                long textureId = ImportTexturePath(fullTexturePath);
                if (textureId >= 0)
                {
                    // Create material with texture
                    long materialId = VortexAPI.CreateNewMaterial();
                    if (materialId >= 0)
                    {
                        VortexAPI.SetMaterialBaseColor(materialId, 
                            meshRenderer.ColorR, meshRenderer.ColorG, meshRenderer.ColorB, meshRenderer.ColorA);
                        VortexAPI.SetMaterialAlbedoTexture(materialId, textureId);

                        // Cache the material
                        RegisterMaterialForMeshPath(meshPath, materialId);
                        _entityMaterials[entityId] = materialId;

                        Log($"[SceneRenderService] Preloaded texture for {meshPath}: {fullTexturePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[SceneRenderService] Error preloading texture: {ex.Message}");
            }
        }

        /// <summary>
        /// Submit an entity for rendering this frame.
        /// </summary>
        public void SubmitEntity(GameEntity entity)
        {
            if (entity == null || !entity.IsActive) return;

            var meshRenderer = entity.GetComponent<MeshRenderer>();
            if (meshRenderer == null || !meshRenderer.IsEnabled) return;

            var transform = entity.GetComponent<Transform>();
            if (transform == null) return;

            // Skip if MeshPath is empty (don't log every frame)
            if (string.IsNullOrEmpty(meshRenderer.MeshPath))
            {
                return;
            }

            // Get or create mesh (with dirty checking)
            long meshId = GetOrCreateMesh(entity.Id, meshRenderer);
            if (meshId < 0)
            {
                return;
            }

            // Build world matrix from transform (including parent transforms)
            float[] worldMatrix = BuildWorldMatrixWithParent(entity);

            // Skinned characters: an entity with an Animator + a skinned model renders through the GPU
            // skinning path — the AnimationService supplies the bone palette (animated pose while playing,
            // bind pose in edit mode). Rigid submeshes of the same model still go through the normal path.
            // The Animator may sit on an ANCESTOR: multi-submesh models import as a parent container with
            // '#submeshN' child entities, and the user drops the Animator on the container.
            float[] bonePalette = null; int boneCount = 0;
            var animatorOwner = entity;
            var animator = entity.GetComponent<Editor.ECS.Components.Animation.Animator>();
            while (animator == null && animatorOwner.Parent != null)
            {
                animatorOwner = animatorOwner.Parent;
                animator = animatorOwner.GetComponent<Editor.ECS.Components.Animation.Animator>();
            }
            if (animator != null && animator.IsEnabled)
                Core.Animation.AnimationService.Instance.TryGetPalette(animatorOwner, meshRenderer.MeshPath, out bonePalette, out boneCount);

            // Render layer (#175): 0 = world, 1 = first-person viewmodel (second pass, own FOV, depth
            // cleared, casts no shadows), 2 = third-person only (hidden for the local player in play).
            // Travels with every submit variant below. While playing (editor play, game window, GameHost)
            // the layers always behave like the shipped game; in edit mode the viewport's preview mode
            // decides how FP/3P meshes are presented (see ViewmodelPreviewMode).
            int layer = meshRenderer.RenderLayer;
            if (layer < 0 || layer > 2) layer = 0;   // unknown layers = world geometry, in editor AND play
            if (layer != 0)
            {
                if (DebugThirdPersonView)
                {
                    // In-game debug freecam: you are now an EXTERNAL camera looking AT the player, so render
                    // like OTHER cameras see them — show the third-person body (layer 2) as world geometry,
                    // hide the local first-person viewmodel (layer 1). Takes priority over the play-like FP
                    // behaviour so flying out shows the character cleanly instead of nothing + floating arms.
                    if (layer == 1) return;
                    layer = 0;
                }
                else if (IsPlayLike || EditorViewmodelPreview == ViewmodelPreviewMode.GameView)
                {
                    if (layer == 2) return;            // third-person only: the local player never sees it
                }
                else if (EditorViewmodelPreview == ViewmodelPreviewMode.Hidden)
                {
                    if (layer == 1) return;            // FP meshes live in the game's FP pass, not the build view
                    layer = 0;                          // 3P body: normal world object while editing
                }
                else // AsWorld — placement mode: draw FP/3P meshes as plain world geometry
                {
                    layer = 0;
                }
            }

            // Imported models (no explicit .vmat) are multi-submesh with per-submesh colored materials —
            // submit EVERY submesh, not just the first, so e.g. a Kenney tree shows trunk + leaves.
            var ext = System.IO.Path.GetExtension(meshRenderer.MeshPath)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(meshRenderer.MaterialPath) && IsModelFileExtension(ext))
            {
                bool any = false;
                for (int n = 0; n < 64; n++)
                {
                    string sub = meshRenderer.MeshPath + "#submesh" + n;
                    if (_submeshMeshCache.TryGetValue(sub, out long subMesh) && subMesh >= 0)
                    {
                        if (bonePalette != null && Core.Animation.AnimationService.Instance.IsMeshSkinned(subMesh))
                            VortexAPI.SubmitSkinnedMesh(subMesh, GetMaterialForMeshPath(sub), worldMatrix, bonePalette, boneCount, layer);
                        else
                            VortexAPI.SubmitMeshForRenderingLayered(subMesh, GetMaterialForMeshPath(sub), worldMatrix, layer);
                        _submitN++;
                        any = true;
                    }
                    else if (n > 0) break;
                }
                if (any) return;
            }

            // Primitive / single mesh / explicitly-assigned .vmat:
            long materialId = GetOrCreateMaterial(entity.Id, meshRenderer);
            if (bonePalette != null && Core.Animation.AnimationService.Instance.IsMeshSkinned(meshId))
                VortexAPI.SubmitSkinnedMesh(meshId, materialId, worldMatrix, bonePalette, boneCount, layer);
            else
                VortexAPI.SubmitMeshForRenderingLayered(meshId, materialId, worldMatrix, layer);
            _submitN++;
        }






        /// <summary>
        /// Submit all entities in a scene for rendering.
        /// </summary>
        public void SubmitScene(Data.Scene scene)
        {
            if (scene == null || scene.Entities == null) return;

            // Clear and submit all lights first
            SubmitSceneLights(scene);

            _submitN = 0;
            foreach (var entity in scene.Entities)
            {
                SubmitEntityRecursive(entity);
            }
            if (scene.Name != "Lobby" && _ssDbg < 12) { _ssDbg++; try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex_submit.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " SubmitScene '" + scene.Name + "' topEnts=" + System.Linq.Enumerable.Count(scene.Entities) + " submitted=" + _submitN + "\r\n"); } catch { } }

        }

        /// <summary>One entity's collider as a wireframe net (green solid / amber trigger) into the gizmo
        /// queue — shared by the selected-entity overlay and the "show all colliders" walk (#49).</summary>
        private void SubmitColliderGizmoFor(GameEntity entity)
        {
            var transform = entity != null ? entity.Transform : null;
            if (transform == null) return;
            var col = entity.GetComponent<Editor.ECS.Components.Physics.Collider>();
            if (col == null || !col.IsEnabled) return;

            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation;
            float sx = transform.LocalScale.X, sy = transform.LocalScale.Y, sz = transform.LocalScale.Z;
            float ccx = pos.X + col.Center.X * sx, ccy = pos.Y + col.Center.Y * sy, ccz = pos.Z + col.Center.Z * sz;
            bool trig = col.IsTrigger; // amber net for a trigger, green for a solid — visible toggle feedback
            if (col is Editor.ECS.Components.Physics.BoxCollider bc)
                VortexAPI.RenderColliderBox(ccx, ccy, ccz, Math.Abs(bc.Size.X * 0.5f * sx), Math.Abs(bc.Size.Y * 0.5f * sy), Math.Abs(bc.Size.Z * 0.5f * sz), rot.Y, trig);
            else if (col is Editor.ECS.Components.Physics.SphereCollider spc)
                VortexAPI.RenderColliderSphere(ccx, ccy, ccz, spc.Radius * Math.Max(Math.Abs(sx), Math.Max(Math.Abs(sy), Math.Abs(sz))), trig);
            else if (col is Editor.ECS.Components.Physics.CapsuleCollider cpc)
            {
                float cr = cpc.Radius * Math.Max(Math.Abs(sx), Math.Abs(sz));
                VortexAPI.RenderColliderCapsule(ccx, ccy, ccz, cr, Math.Max(0f, cpc.Height * 0.5f * Math.Abs(sy) - cr), trig);
            }
            else // Mesh / base collider: draw the ACTUAL render mesh as a green net (the collision mesh IS the
                 // render mesh), so a round object shows a round net — not a box. Falls back to a bounds net box.
            {
                if (!RenderMeshColliderWireframe(entity, trig))
                {
                    var b = CalculateCombinedBounds(entity);
                    VortexAPI.RenderColliderBox(pos.X + b.CenterOffset.X, pos.Y + b.CenterOffset.Y, pos.Z + b.CenterOffset.Z, b.Size.X * 0.5f, b.Size.Y * 0.5f, b.Size.Z * 0.5f, rot.Y, trig);
                }
            }
        }

        private void SubmitColliderGizmosRecursive(GameEntity entity, GameEntity skip)
        {
            if (entity == null || !entity.IsActive || entity.IsHiddenInEditor) return;
            if (!ReferenceEquals(entity, skip)) SubmitColliderGizmoFor(entity);
            if (entity.Children != null)
                foreach (var c in entity.Children) SubmitColliderGizmosRecursive(c, skip);
        }

        /// <summary>Editor overlays: camera/light icons + the selected entity's outline (or camera frustum) + the
        /// transform gizmo. These all go into the always-on-top GIZMO queue, and this is called EVERY frame in edit
        /// mode (decoupled from SubmitScene's static-reuse) so the gizmo instantly reflects the current tool mode /
        /// drag / hover / selection + camera — without it, switching tools or releasing a drag wouldn't update on a
        /// static scene.</summary>
        public void SubmitOverlays(Data.Scene scene)
        {
            if (scene == null || scene.Entities == null) return;

            var selected = SelectionService.Instance.SelectedEntity;
            RenderAllCameraIcons(scene, selected);
            RenderAllLightIcons(scene, selected);
            RenderAllAudioIcons(scene, selected);

            // #49: "Show ALL colliders" — every entity's collision shape at a glance (trigger zones
            // amber, solids green) while level-building. The selected entity's collider still draws
            // through the block below, so skip it here to avoid a double net.
            if (EditorViewportService.Instance.AreCollidersVisible && EditorViewportService.Instance.ShowAllColliders)
                foreach (var entity in scene.Entities)
                    SubmitColliderGizmosRecursive(entity, selected);

            if (selected == null) return;
            var transform = selected.Transform;
            if (transform == null) return;

            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation;

            var camera = selected.GetComponent<Camera>();
            if (camera != null)
            {
                // Selected camera: show its FOV frustum instead of a box outline.
                VortexAPI.RenderCameraGizmo(
                    pos.X, pos.Y, pos.Z,
                    rot.X, rot.Y, rot.Z,
                    camera.FieldOfView, 16f / 9f,
                    camera.CameraType == CameraType.MainCamera);
            }
            else
            {
                var bounds = CalculateCombinedBounds(selected);
                VortexAPI.RenderSelectionOutline(
                    pos.X + bounds.CenterOffset.X,
                    pos.Y + bounds.CenterOffset.Y,
                    pos.Z + bounds.CenterOffset.Z,
                    bounds.Size.X, bounds.Size.Y, bounds.Size.Z,
                    rot.X, rot.Y, rot.Z);
            }

            // Green collider wireframe for the selected entity, so you SEE its collision shape where it sits.
            // Gated by the "Show Collision" viewport toggle (EditorViewportService.AreCollidersVisible).
            if (EditorViewportService.Instance.AreCollidersVisible)
                SubmitColliderGizmoFor(selected);

            // Audio gizmos (issue #18): the selected AudioSource's min/max distance spheres and a
            // selected ReverbZone's boundary + falloff shell. Values are read fresh every frame,
            // so inspector edits and entity drags update the shapes live.
            var audioSrc = selected.GetComponent<ECS.Components.Audio.AudioSource>();
            if (audioSrc != null && audioSrc.IsEnabled)
                VortexAPI.RenderAudioRangeSpheres(pos.X, pos.Y, pos.Z, audioSrc.MinDistance, audioSrc.MaxDistance);
            var reverbZone = selected.GetComponent<ECS.Components.Audio.ReverbZone>();
            if (reverbZone != null && reverbZone.IsEnabled)
                // Max(0.01, extent) — NOT Abs — mirrors the runtime test (ZoneWeight), so the drawn box is
                // exactly the audible one even for hand-edited negative extents.
                VortexAPI.RenderReverbZoneGizmo(pos.X, pos.Y, pos.Z, reverbZone.Shape, reverbZone.Radius,
                    Math.Max(0.01f, reverbZone.BoxExtents.X), Math.Max(0.01f, reverbZone.BoxExtents.Y), Math.Max(0.01f, reverbZone.BoxExtents.Z),
                    reverbZone.Falloff);

            if (VortexAPI.AreGizmosVisible)
            {
                // Constant on-screen size (Blender/Unreal feel) — the identical scale is used by the picker in
                // GamePreviewView (RaycastService.ComputeGizmoScale), so the clickable boxes sit on the drawn arrows.
                float gizmoScale = RaycastService.ComputeGizmoScale(new Vector3f(pos.X, pos.Y, pos.Z));
                VortexAPI.RenderGizmo(pos.X, pos.Y, pos.Z, transform.LocalScale.Y, gizmoScale);
            }
        }

        /// <summary>Speaker icons at every AudioSource and a head icon at every AudioListener —
        /// camera-facing billboards, drawn regardless of selection (like camera icons).</summary>
        private void RenderAllAudioIcons(Data.Scene scene, GameEntity selected)
        {
            if (!VortexAPI.AreGizmosVisible) return;
            var cam = EditorCameraController.Instance;
            foreach (var entity in scene.Entities)
                RenderAudioIconRecursive(entity, selected, cam.PositionX, cam.PositionY, cam.PositionZ);
        }

        private void RenderAudioIconRecursive(GameEntity entity, GameEntity selected, float camX, float camY, float camZ)
        {
            if (entity == null || !entity.IsActive || entity.IsHiddenInEditor) return;   // eye toggle hides the icon too
            var transform = entity.Transform;
            if (transform != null)
            {
                var pos = transform.LocalPosition;
                if (entity.GetComponent<ECS.Components.Audio.AudioSource>() != null)
                    VortexAPI.RenderAudioSourceIcon(pos.X, pos.Y, pos.Z, camX, camY, camZ, entity == selected);
                if (entity.GetComponent<ECS.Components.Audio.AudioListener>() != null)
                {
                    // Listeners usually sit on a camera entity — float the head above the camera icon.
                    float lift = entity.GetComponent<Camera>() != null ? 0.45f : 0f;
                    VortexAPI.RenderAudioListenerIcon(pos.X, pos.Y + lift, pos.Z, camX, camY, camZ);
                }
            }

            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                    RenderAudioIconRecursive(child, selected, camX, camY, camZ);
            }
        }

        /// <summary>Draw a Mesh Collider as a green wireframe net over the entity's ACTUAL render mesh (the collision
        /// mesh is the render mesh), at the same world transform the mesh renders with. Mirrors the submesh resolution
        /// in RenderMesh so multi-submesh imports net every part. Returns false (caller falls back to a bounds box)
        /// when the entity has no usable render mesh.</summary>
        private bool RenderMeshColliderWireframe(GameEntity entity, bool isTrigger = false)
        {
            var meshRenderer = entity.GetComponent<MeshRenderer>();
            if (meshRenderer == null || !meshRenderer.IsEnabled || string.IsNullOrEmpty(meshRenderer.MeshPath))
                return false;

            long meshId = GetOrCreateMesh(entity.Id, meshRenderer);
            if (meshId < 0) return false;

            float[] worldMatrix = BuildWorldMatrixWithParent(entity);

            // Multi-submesh imported model: net every cached submesh (same path the renderer submits).
            var ext = System.IO.Path.GetExtension(meshRenderer.MeshPath)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(meshRenderer.MaterialPath) && IsModelFileExtension(ext))
            {
                bool any = false;
                for (int n = 0; n < 64; n++)
                {
                    string sub = meshRenderer.MeshPath + "#submesh" + n;
                    if (_submeshMeshCache.TryGetValue(sub, out long subMesh) && subMesh >= 0)
                    {
                        VortexAPI.RenderColliderMeshWire(subMesh, worldMatrix, isTrigger);
                        any = true;
                    }
                    else if (n > 0) break;
                }
                if (any) return true;
            }

            VortexAPI.RenderColliderMeshWire(meshId, worldMatrix, isTrigger);
            return true;
        }

        /// <summary>
        /// Represents bounds with size and center offset
        /// </summary>
        private struct EntityBounds
        {
            public ECS.Vector3 Size;
            public ECS.Vector3 CenterOffset;
        }

        /// <summary>
        /// Calculate combined bounds for an entity, including all children with MeshRenderers.
        /// </summary>
        private EntityBounds CalculateCombinedBounds(GameEntity entity)
        {
            var bounds = new EntityBounds
            {
                Size = entity.Transform?.LocalScale ?? ECS.Vector3.One,
                CenterOffset = ECS.Vector3.Zero
            };

            // If entity has no children, just use its own scale
            if (entity.Children == null || entity.Children.Count == 0)
            {
                return bounds;
            }

            // Check if any children have mesh renderers
            bool hasChildMeshes = false;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            foreach (var child in entity.Children)
            {
                var meshRenderer = child.GetComponent<MeshRenderer>();
                if (meshRenderer != null && !string.IsNullOrEmpty(meshRenderer.MeshPath))
                {
                    hasChildMeshes = true;
                    var childPos = child.Transform?.LocalPosition ?? ECS.Vector3.Zero;
                    var childScale = child.Transform?.LocalScale ?? ECS.Vector3.One;

                    // Get mesh bounds and center from cache
                    var meshBoundsInfo = GetMeshBoundsAndCenter(meshRenderer.MeshPath);
                    
                    // Apply mesh center offset and scale
                    float centerX = meshBoundsInfo.Center.X * childScale.X;
                    float centerY = meshBoundsInfo.Center.Y * childScale.Y;
                    float centerZ = meshBoundsInfo.Center.Z * childScale.Z;
                    
                    // Calculate world-space bounds for this child
                    float halfX = (meshBoundsInfo.Size.X * childScale.X) * 0.5f;
                    float halfY = (meshBoundsInfo.Size.Y * childScale.Y) * 0.5f;
                    float halfZ = (meshBoundsInfo.Size.Z * childScale.Z) * 0.5f;

                    // Add child position + mesh center offset
                    float worldCenterX = childPos.X + centerX;
                    float worldCenterY = childPos.Y + centerY;
                    float worldCenterZ = childPos.Z + centerZ;

                    minX = Math.Min(minX, worldCenterX - halfX);
                    minY = Math.Min(minY, worldCenterY - halfY);
                    minZ = Math.Min(minZ, worldCenterZ - halfZ);
                    maxX = Math.Max(maxX, worldCenterX + halfX);
                    maxY = Math.Max(maxY, worldCenterY + halfY);
                    maxZ = Math.Max(maxZ, worldCenterZ + halfZ);
                }
            }

            if (hasChildMeshes)
            {
                bounds.Size = new ECS.Vector3(maxX - minX, maxY - minY, maxZ - minZ);
                bounds.CenterOffset = new ECS.Vector3(
                    (minX + maxX) * 0.5f,
                    (minY + maxY) * 0.5f,
                    (minZ + maxZ) * 0.5f
                );
            }


            return bounds;
        }

        /// <summary>World-space AABB for viewport PICKING — the real hitbox that should match what's drawn.
        /// Uses the actual mesh bounds (cached from the engine) transformed by the entity's full world matrix
        /// (so it's correct for children of scaled/moved parents), instead of the old "LocalScale is the size"
        /// guess that made imported models almost unclickable. Non-mesh entities (cameras/lights/empties) get a
        /// small clickable box at their world position. Returns false only if the entity has no transform.</summary>
        public bool TryGetWorldPickBounds(GameEntity entity, out Vector3f center, out Vector3f halfExtents)
        {
            center = new Vector3f(0, 0, 0);
            halfExtents = new Vector3f(0.25f, 0.25f, 0.25f);
            var t = entity?.Transform;
            if (t == null) return false;

            // World transform (walks the parent chain). Translation is the last row; scale is each basis row length.
            float[] wm;
            try { wm = BuildWorldMatrixWithParent(entity); }
            catch { wm = BuildWorldMatrix(t); }
            float wpx = wm[12], wpy = wm[13], wpz = wm[14];
            float sx = (float)Math.Sqrt(wm[0] * wm[0] + wm[1] * wm[1] + wm[2] * wm[2]);
            float sy = (float)Math.Sqrt(wm[4] * wm[4] + wm[5] * wm[5] + wm[6] * wm[6]);
            float sz = (float)Math.Sqrt(wm[8] * wm[8] + wm[9] * wm[9] + wm[10] * wm[10]);

            var mr = entity.GetComponent<MeshRenderer>();
            if (mr != null && mr.IsEnabled && !string.IsNullOrEmpty(mr.MeshPath))
            {
                var mb = GetMeshBoundsAndCenter(mr.MeshPath);
                // World AABB of the ROTATED local box. The old version applied centre + extents on world axes
                // (scale only), so any rotated non-uniform object (a wall turned 90°) had its hitbox crossways
                // to the drawn mesh — clicks on the visible object missed and empty air hit. Row-vector matrix:
                // local axis i is row i (rows already include scale), so the centre offset transforms with the
                // full 3x3, and the tight world half-extent per axis is the |R·S| absolute-column sum.
                float cx = mb.Center.X * wm[0] + mb.Center.Y * wm[4] + mb.Center.Z * wm[8];
                float cy = mb.Center.X * wm[1] + mb.Center.Y * wm[5] + mb.Center.Z * wm[9];
                float cz = mb.Center.X * wm[2] + mb.Center.Y * wm[6] + mb.Center.Z * wm[10];
                center = new Vector3f(wpx + cx, wpy + cy, wpz + cz);
                float hx = Math.Abs(mb.Size.X) * 0.5f, hy = Math.Abs(mb.Size.Y) * 0.5f, hz = Math.Abs(mb.Size.Z) * 0.5f;
                halfExtents = new Vector3f(
                    Math.Max(Math.Abs(wm[0]) * hx + Math.Abs(wm[4]) * hy + Math.Abs(wm[8]) * hz, 0.1f),
                    Math.Max(Math.Abs(wm[1]) * hx + Math.Abs(wm[5]) * hy + Math.Abs(wm[9]) * hz, 0.1f),
                    Math.Max(Math.Abs(wm[2]) * hx + Math.Abs(wm[6]) * hy + Math.Abs(wm[10]) * hz, 0.1f));
                return true;
            }

            // No mesh: a modest box at the world pivot so lights/cameras/empties stay clickable.
            center = new Vector3f(wpx, wpy, wpz);
            halfExtents = new Vector3f(
                Math.Max(Math.Abs(sx) * 0.5f, 0.35f),
                Math.Max(Math.Abs(sy) * 0.5f, 0.35f),
                Math.Max(Math.Abs(sz) * 0.5f, 0.35f));
            return true;
        }

        // Cache for mesh bounds (size in local space)
        private static readonly Dictionary<string, ECS.Vector3> _meshBoundsCache = new Dictionary<string, ECS.Vector3>();
        private static readonly Dictionary<string, ECS.Vector3> _meshBoundsCenterCache = new Dictionary<string, ECS.Vector3>();

        /// <summary>
        /// Mesh bounds information (size and center)
        /// </summary>
        private struct MeshBoundsInfo
        {
            public ECS.Vector3 Size;
            public ECS.Vector3 Center;
        }

        /// <summary>
        /// Get bounds and center for a mesh path
        /// </summary>
        private MeshBoundsInfo GetMeshBoundsAndCenter(string meshPath)
        {
            var info = new MeshBoundsInfo
            {
                Size = ECS.Vector3.One,
                Center = ECS.Vector3.Zero
            };

            if (string.IsNullOrEmpty(meshPath))
                return info;

            // Check caches
            if (_meshBoundsCache.TryGetValue(meshPath, out var cachedSize))
            {
                info.Size = cachedSize;
                if (_meshBoundsCenterCache.TryGetValue(meshPath, out var cachedCenter))
                {
                    info.Center = cachedCenter;
                }
                return info;
            }

            // Try to get from engine
            float sizeX = 1f, sizeY = 1f, sizeZ = 1f;
            float centerX = 0f, centerY = 0f, centerZ = 0f;
            
            long meshId = -1;
            if (_submeshMeshCache.TryGetValue(meshPath, out meshId) && meshId >= 0)
            {
                // Get size
                if (VortexAPI.GetMeshBounds(meshId, out sizeX, out sizeY, out sizeZ))
                {
                    info.Size = new ECS.Vector3(sizeX, sizeY, sizeZ);
                    _meshBoundsCache[meshPath] = info.Size;
                }
                
                // Get center
                if (VortexAPI.GetMeshBoundsCenter(meshId, out centerX, out centerY, out centerZ))
                {
                    info.Center = new ECS.Vector3(centerX, centerY, centerZ);
                    _meshBoundsCenterCache[meshPath] = info.Center;
                }
            }
            else
            {
                // Try to find mesh in entity cache
                meshId = GetOrLoadMeshForBounds(meshPath);
                if (meshId >= 0)
                {
                    if (VortexAPI.GetMeshBounds(meshId, out sizeX, out sizeY, out sizeZ))
                    {
                        info.Size = new ECS.Vector3(sizeX, sizeY, sizeZ);
                        _meshBoundsCache[meshPath] = info.Size;
                    }
                    if (VortexAPI.GetMeshBoundsCenter(meshId, out centerX, out centerY, out centerZ))
                    {
                        info.Center = new ECS.Vector3(centerX, centerY, centerZ);
                        _meshBoundsCenterCache[meshPath] = info.Center;
                    }
                }
            }

            return info;
        }

        /// <summary>
        /// Get or calculate bounds for a mesh path
        /// </summary>
        private ECS.Vector3 GetMeshBounds(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath))
                return ECS.Vector3.One;

            if (_meshBoundsCache.TryGetValue(meshPath, out var cached))
                return cached;

            // Try to get bounds from engine
            float sizeX = 1f, sizeY = 1f, sizeZ = 1f;
            
            // Check if mesh is loaded and get its bounds
            if (_submeshMeshCache.TryGetValue(meshPath, out long meshId) && meshId >= 0)
            {
                if (VortexAPI.GetMeshBounds(meshId, out sizeX, out sizeY, out sizeZ))
                {
                    var bounds = new ECS.Vector3(sizeX, sizeY, sizeZ);
                    _meshBoundsCache[meshPath] = bounds;
                    return bounds;
                }
            }
            
            // If not found, try to load the mesh to get bounds
            // This happens during the first frame after scene load
            long loadedMeshId = GetOrLoadMeshForBounds(meshPath);
            if (loadedMeshId >= 0)
            {
                if (VortexAPI.GetMeshBounds(loadedMeshId, out sizeX, out sizeY, out sizeZ))
                {
                    var bounds = new ECS.Vector3(sizeX, sizeY, sizeZ);
                    _meshBoundsCache[meshPath] = bounds;
                    return bounds;
                }
            }

            // Return default bounds
            return new ECS.Vector3(1f, 1f, 1f);
        }

        /// <summary>
        /// Try to load a mesh just to get its bounds (without caching the mesh itself)
        /// </summary>
        private long GetOrLoadMeshForBounds(string meshPath)
        {
            try
            {
                // Check the entity mesh cache first
                foreach (var kvp in _entityMeshes)
                {
                    if (_entityMeshPaths.TryGetValue(kvp.Key, out var path) && path == meshPath)
                    {
                        return kvp.Value;
                    }
                }
                
                // Return -1, bounds will be calculated later when mesh is loaded
                return -1;
            }
            catch
            {
                return -1;
            }
        }
        
        /// <summary>
        /// Render camera icons for all cameras in the scene (simplified icon for non-selected).
        /// </summary>
        private void RenderAllCameraIcons(Data.Scene scene, GameEntity selected)
        {
            if (!VortexAPI.AreGizmosVisible) return;
            
            foreach (var entity in scene.Entities)
            {
                RenderCameraIconRecursive(entity, selected);
            }
        }
        
        private void RenderCameraIconRecursive(GameEntity entity, GameEntity selected)
        {
            if (entity == null || !entity.IsActive || entity.IsHiddenInEditor) return;   // eye toggle hides the icon too
            // Skip the selected entity (it gets the full frustum gizmo)
            if (entity != selected)
            {
                var camera = entity.GetComponent<Camera>();
                if (camera != null)
                {
                    var pos = entity.Transform.LocalPosition;
                    var rot = entity.Transform.LocalRotation;
                    
                    // Render simple camera icon (just the body, no frustum)
                    VortexAPI.RenderCameraIcon(
                        pos.X, pos.Y, pos.Z,
                        rot.X, rot.Y, rot.Z,
                        camera.CameraType == CameraType.MainCamera);
                }
            }
            
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    RenderCameraIconRecursive(child, selected);
                }
            }
        }

        private void SubmitEntityRecursive(GameEntity entity)
        {
            if (entity == null) return;

            // Editor eye toggle: a hidden entity takes its whole subtree out of the render.
            // Edit-mode only — during play the runtime activeSelf (SetActive) is in charge.
            if (entity.IsHiddenInEditor && !IsPlayLike) return;

            // activeSelf cascades: an inactive parent hides its children too (ActiveInHierarchy),
            // matching SubmitEntityLightsRecursive / SubmitColliderGizmosRecursive.
            if (!entity.IsActive) return;

            SubmitEntity(entity);

            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    SubmitEntityRecursive(child);
                }
            }
        }

        private long GetOrCreateMesh(Guid entityId, MeshRenderer renderer)
        {
            if (string.IsNullOrEmpty(renderer.MeshPath)) return -1;


            // Check if mesh path changed (dirty check)
            bool needsRecreate = false;
            if (_entityMeshPaths.TryGetValue(entityId, out string cachedPath))
            {
                if (cachedPath != renderer.MeshPath)
                {
                    // Path changed, need to recreate
                    if (_entityMeshes.TryGetValue(entityId, out long oldMesh))
                    {
                        VortexAPI.DeleteMesh(oldMesh);
                        _entityMeshes.Remove(entityId);
                    }
                    needsRecreate = true;
                }
            }
            else
            {
                needsRecreate = true;
            }

            // Check if we already have a valid mesh for this entity
            if (!needsRecreate && _entityMeshes.TryGetValue(entityId, out long existingMesh))
            {
                return existingMesh;
            }

            // Create new mesh based on path
            long meshId = CreateMeshFromPath(renderer.MeshPath);
            if (meshId >= 0)
            {
                _entityMeshes[entityId] = meshId;
                _entityMeshPaths[entityId] = renderer.MeshPath;
            }
            if (_meshDbg < 16) { _meshDbg++; try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex_mesh.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " path='" + renderer.MeshPath + "' id=" + meshId + " projPath='" + (Data.ProjectData.Current != null ? Data.ProjectData.Current.Path : "?") + "'\r\n"); } catch { } }

            return meshId;
        }

        private long CreateMeshFromPath(string meshPath)
        {
            if (meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                var primitiveType = meshPath.Substring("Primitive:".Length);
                switch (primitiveType.ToLower())
                {
                    case "cube":
                        return VortexAPI.CreateCubeMesh(1.0f);
                    case "sphere":
                        return VortexAPI.CreateSphereMesh(0.5f);
                    case "plane":
                        return VortexAPI.CreatePlaneMesh(1.0f, 1.0f);
                    case "cylinder":
                        return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                    case "capsule":
                        // Capsule approximated with cylinder for now
                        return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                    case "cone":
                        // Cone approximated with cylinder for now
                        return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                    case "torus":
                        // Torus approximated with sphere for now
                        return VortexAPI.CreateSphereMesh(0.5f);
                    case "quad":
                        return VortexAPI.CreatePlaneMesh(1.0f, 1.0f);
                    default:
                        return -1;
                }
            }

            // Load mesh from external file
            return LoadMeshFromFile(meshPath);
        }

        // Cache for submesh mesh IDs (keyed by submesh path like "path#submesh0")
        private static readonly Dictionary<string, long> _submeshMeshCache = new Dictionary<string, long>();

        private long LoadMeshFromFile(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath))
                return -1;

            try
            {
                // Check if this is a submesh path (format: "path#submeshN")
                string actualPath = meshPath;
                int submeshIndex = -1;
                
                int hashIndex = meshPath.LastIndexOf('#');
                if (hashIndex > 0 && meshPath.Length > hashIndex + 7 && meshPath.Substring(hashIndex + 1, 7) == "submesh")
                {
                    actualPath = meshPath.Substring(0, hashIndex);
                    if (int.TryParse(meshPath.Substring(hashIndex + 8), out int idx))
                    {
                        submeshIndex = idx;
                    }
                }

                // Check submesh cache first (most common path for already-imported models)
                if (_submeshMeshCache.TryGetValue(meshPath, out long cachedMeshId))
                {
                    Log($"[SceneRenderService] Using cached mesh {cachedMeshId} for {meshPath}");
                    return cachedMeshId;
                }

                // Get full path
                var projectPath = Data.ProjectData.Current?.Path;
                string fullPath = actualPath;
                
                // If it's a relative path, combine with project path
                if (!System.IO.Path.IsPathRooted(actualPath) && !string.IsNullOrEmpty(projectPath))
                {
                    fullPath = System.IO.Path.Combine(projectPath, actualPath);
                }

                if (!AssetVfs.Exists(fullPath))
                {
                    return -1;
                }

                var extension = System.IO.Path.GetExtension(fullPath)?.ToLowerInvariant();

                // Shipped game: the bytes live in the in-RAM pak, not on disk.
                byte[] vfsBytes = null;
                bool fromVfs = AssetVfs.IsMounted && AssetVfs.TryGetBytes(fullPath, out vfsBytes);

                // Check if it's a .vmesh file (binary format - fast load)
                if (extension == ".vmesh")
                {
                    if (fromVfs)
                    {
                        // Shipped game: the native .vmesh loader is disk-path only (no bytes overload). Spill the
                        // packed bytes to a per-run temp file (mirrors AudioPlaybackService's container handling) so
                        // packed .vmesh meshes still load — otherwise a Release build renders them as missing geometry.
                        try
                        {
                            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VortexVMesh",
                                (uint)fullPath.ToLowerInvariant().GetHashCode() + "_" + vfsBytes.Length + ".vmesh");
                            if (!System.IO.File.Exists(tmp))
                            {
                                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tmp));
                                System.IO.File.WriteAllBytes(tmp, vfsBytes);
                            }
                            return VortexAPI.LoadVMeshFromFile(tmp);
                        }
                        catch { return -1; }
                    }
                    return VortexAPI.LoadVMeshFromFile(fullPath);
                }

                // For model files (FBX, OBJ, etc.) - use multi-material import
                if (IsModelFileExtension(extension))
                {
                    if (!fromVfs && !VortexAPI.IsAssimpAvailable())
                    {
                        return -1;
                    }

                    // Import with materials (this creates all submeshes at once) — from RAM if packed, else disk.
                    Log($"[SceneRenderService] Importing model with materials: {fullPath} (vfs={fromVfs})");
                    var virtualDir = (System.IO.Path.GetDirectoryName(actualPath) ?? "").Replace('\\', '/');
                    var submeshes = fromVfs
                        ? VortexAPI.ImportModelFromBytes(vfsBytes, extension.TrimStart('.'), virtualDir)
                        : VortexAPI.ImportModelWithMaterialsFromFile(fullPath);
                    if (submeshes != null && submeshes.Length > 0)
                    {
                        // Cache all submeshes for future use
                        for (int i = 0; i < submeshes.Length; i++)
                        {
                            string subPath = $"{actualPath}#submesh{i}";
                            _submeshMeshCache[subPath] = submeshes[i].MeshId;
                            
                            // Also register materials
                            if (submeshes[i].MaterialId >= 0)
                            {
                                RegisterMaterialForMeshPath(subPath, submeshes[i].MaterialId);
                            }
                        }
                        
                        // Also cache the base path with first mesh
                        _submeshMeshCache[actualPath] = submeshes[0].MeshId;
                        
                        // Return requested submesh or first mesh
                        if (submeshIndex >= 0 && submeshIndex < submeshes.Length)
                        {
                            return submeshes[submeshIndex].MeshId;
                        }
                        return submeshes[0].MeshId;
                    }
                    return -1;
                }

                return -1;
            }
            catch (Exception ex)
            {
                Log($"[SceneRenderService] Error loading mesh: {ex.Message}");
                return -1;
            }
        }

        private static bool IsModelFileExtension(string extension)
        {
            return extension switch
            {
                ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".3ds" or ".blend" => true,
                _ => false
            };
        }

        /// <summary>Load a texture by path — from the in-RAM asset pak (shipped game) or from disk (editor).</summary>
        private static long ImportTexturePath(string fullPath)
        {
            if (AssetVfs.IsMounted && AssetVfs.TryGetBytes(fullPath, out var bytes))
                return VortexAPI.ImportTextureFromBytes(bytes);
            return VortexAPI.ImportTextureFromFile(fullPath);
        }

        private long GetOrCreateMaterial(Guid entityId, MeshRenderer renderer)
        {
            // Highest precedence: an explicitly assigned .vmat material. Build it FULLY (all PBR
            // scalars + every texture map, not just base color) via MaterialService, which owns the
            // engine material and shares one instance across every entity referencing the same file.
            // Deliberately NOT stored in _entityMaterials — those get DeleteMaterial'd per entity on
            // cleanup, which would free a shared material out from under other meshes.
            if (!string.IsNullOrEmpty(renderer.MaterialPath))
            {
                // Fast path: already resolved this .vmat -> skip the per-frame filesystem check + rebuild.
                if (_vmatPathCache.TryGetValue(renderer.MaterialPath, out long fastMat) && fastMat >= 0)
                    return fastMat;

                string vmatPath = renderer.MaterialPath;
                if (!System.IO.Path.IsPathRooted(vmatPath))
                {
                    var projectPath = Data.ProjectData.Current?.Path;
                    if (!string.IsNullOrEmpty(projectPath))
                        vmatPath = System.IO.Path.Combine(projectPath, vmatPath);
                }

                if (AssetVfs.Exists(vmatPath))
                {
                    long vmatMaterial = MaterialService.Instance.GetOrBuildVortexMaterial(vmatPath);
                    if (vmatMaterial >= 0)
                    {
                        _vmatPathCache[renderer.MaterialPath] = vmatMaterial;   // cache for every subsequent frame
                        return vmatMaterial;
                    }
                }
            }

            // First, check if we have a cached material for this mesh path
            string meshPath = renderer.MeshPath;
            long cachedMaterial = GetMaterialForMeshPath(meshPath);
            
            if (cachedMaterial >= 0)
            {
                // Use the cached material with textures
                if (!_entityMaterials.ContainsKey(entityId))
                {
                    _entityMaterials[entityId] = cachedMaterial;
                }
                return cachedMaterial;
            }

            // Fallback: Check if the renderer has an imported material directly
            if (renderer.HasImportedMaterial)
            {
                if (!_entityMaterials.ContainsKey(entityId))
                {
                    _entityMaterials[entityId] = renderer.MaterialHandle;
                }
                // Register in cache for future lookups
                if (!string.IsNullOrEmpty(meshPath))
                {
                    RegisterMaterialForMeshPath(meshPath, renderer.MaterialHandle);
                }
                return renderer.MaterialHandle;
            }

            // Check if renderer has a texture path but no cached material (e.g., after restart)
            if (!string.IsNullOrEmpty(renderer.TexturePath) && !_entityMaterials.ContainsKey(entityId))
            {
                var projectPath = Data.ProjectData.Current?.Path;
                string fullTexturePath = renderer.TexturePath;
                
                if (!System.IO.Path.IsPathRooted(renderer.TexturePath) && !string.IsNullOrEmpty(projectPath))
                {
                    fullTexturePath = System.IO.Path.Combine(projectPath, renderer.TexturePath);
                }

                if (AssetVfs.Exists(fullTexturePath))
                {
                    try
                    {
                        // Import texture and create material
                        long textureId = ImportTexturePath(fullTexturePath);
                        if (textureId >= 0)
                        {
                            long newMaterialId = VortexAPI.CreateNewMaterial();
                            if (newMaterialId >= 0)
                            {
                                VortexAPI.SetMaterialBaseColor(newMaterialId, 0.9f, 0.9f, 0.9f, 1.0f);
                                VortexAPI.SetMaterialMetallicValue(newMaterialId, renderer.Metallic);
                                VortexAPI.SetMaterialRoughnessValue(newMaterialId, renderer.Roughness);
                                VortexAPI.SetMaterialAlbedoTexture(newMaterialId, textureId);
                                _entityMaterials[entityId] = newMaterialId;
                                
                                // Register in cache for future lookups
                                if (!string.IsNullOrEmpty(meshPath))
                                {
                                    RegisterMaterialForMeshPath(meshPath, newMaterialId);
                                }
                                
                                Log($"[SceneRenderService] Created material with texture for {meshPath}: {fullTexturePath}");
                                return newMaterialId;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[SceneRenderService] Error loading texture: {ex.Message}");
                    }
                }
            }

            var currentColor = (renderer.ColorR, renderer.ColorG, renderer.ColorB, renderer.ColorA);
            
            // Check if material color changed (dirty check)
            bool needsUpdate = false;
            if (_entityMaterialColors.TryGetValue(entityId, out var cachedColor))
            {
                if (cachedColor != currentColor)
                {
                    needsUpdate = true;
                }
            }
            else
            {
                needsUpdate = true;
            }

            // Check if we already have a material for this entity
            if (_entityMaterials.TryGetValue(entityId, out long existingMaterial))
            {
                // Update color if needed
                if (needsUpdate)
                {
                    VortexAPI.SetMaterialBaseColor(existingMaterial, 
                        renderer.ColorR, renderer.ColorG, renderer.ColorB, renderer.ColorA);
                    _entityMaterialColors[entityId] = currentColor;
                }
                return existingMaterial;
            }

            // Create new material
            long materialId = VortexAPI.CreateNewMaterial();
            if (materialId >= 0)
            {
                VortexAPI.SetMaterialBaseColor(materialId,
                    renderer.ColorR, renderer.ColorG, renderer.ColorB, renderer.ColorA);
                // Push PBR scalars too — otherwise the engine keeps its material defaults and a
                // freshly created primitive renders far too dark (metallic surface, one weak light).
                VortexAPI.SetMaterialMetallicValue(materialId, renderer.Metallic);
                VortexAPI.SetMaterialRoughnessValue(materialId, renderer.Roughness);
                _entityMaterials[entityId] = materialId;
                _entityMaterialColors[entityId] = currentColor;
            }

            return materialId;
        }

        private float[] BuildWorldMatrix(Transform transform)
        {
            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation; // Rotation in degrees (Euler angles)
            var scale = transform.LocalScale;

            // Convert degrees to radians
            float radX = rot.X * (float)(Math.PI / 180.0);
            float radY = rot.Y * (float)(Math.PI / 180.0);
            float radZ = rot.Z * (float)(Math.PI / 180.0);

            // Pre-calculate sin and cos
            float cosX = (float)Math.Cos(radX), sinX = (float)Math.Sin(radX);
            float cosY = (float)Math.Cos(radY), sinY = (float)Math.Sin(radY);
            float cosZ = (float)Math.Cos(radZ), sinZ = (float)Math.Sin(radZ);

            // Build rotation matrix (ZXY order for Unity-like behavior)
            // R = Rz * Rx * Ry
            float r00 = cosZ * cosY + sinZ * sinX * sinY;
            float r01 = sinZ * cosX;
            float r02 = -cosZ * sinY + sinZ * sinX * cosY;

            float r10 = -sinZ * cosY + cosZ * sinX * sinY;
            float r11 = cosZ * cosX;
            float r12 = sinZ * sinY + cosZ * sinX * cosY;

            float r20 = cosX * sinY;
            float r21 = -sinX;
            float r22 = cosX * cosY;

            // Combine with scale: S * R (scale first, then rotate)
            // Final matrix: Scale * Rotation * Translation (in column-major terms)
            // For row-major DirectX: Transpose the rotation part
            return new float[]
            {
                scale.X * r00, scale.X * r01, scale.X * r02, 0,
                scale.Y * r10, scale.Y * r11, scale.Y * r12, 0,
                scale.Z * r20, scale.Z * r21, scale.Z * r22, 0,
                pos.X,         pos.Y,         pos.Z,         1
            };
        }

        /// <summary>
        /// Build world matrix including parent transformations.
        /// This allows child entities to inherit transforms from their parent.
        /// </summary>
        private float[] BuildWorldMatrixWithParent(GameEntity entity)
        {
            if (entity == null || entity.Transform == null)
                return BuildIdentityMatrix();

            // Get local matrix
            float[] localMatrix = BuildWorldMatrix(entity.Transform);

            // If no parent, return local matrix
            if (entity.Parent == null || entity.Parent.Transform == null)
                return localMatrix;

            // Get parent world matrix (recursive)
            float[] parentMatrix = BuildWorldMatrixWithParent(entity.Parent);

            // Multiply: local * parent (row-major order)
            return MultiplyMatrices(localMatrix, parentMatrix);
        }

        private float[] BuildIdentityMatrix()
        {
            return new float[]
            {
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1
            };
        }

        private float[] MultiplyMatrices(float[] a, float[] b)
        {
            float[] result = new float[16];
            
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    result[row * 4 + col] = 
                        a[row * 4 + 0] * b[0 * 4 + col] +
                        a[row * 4 + 1] * b[1 * 4 + col] +
                        a[row * 4 + 2] * b[2 * 4 + col] +
                        a[row * 4 + 3] * b[3 * 4 + col];
                }
            }
            
            return result;
        }

        /// <summary>
        /// Set an entity's base color at runtime (used by scripts — e.g. change color when a trigger is touched).
        /// Updates the C# MeshRenderer color and pushes it to the engine material immediately so it shows this
        /// frame, even under submit-once (the material is referenced by id, so changing its color is live).
        /// </summary>
        public void SetEntityColor(GameEntity entity, float r, float g, float b, float a = 1f)
        {
            if (entity == null) return;
            var mr = entity.GetComponent<MeshRenderer>();
            if (mr != null) { mr.ColorR = r; mr.ColorG = g; mr.ColorB = b; mr.ColorA = a; }

            // Per-entity material (primitives / single mesh / texture fallback).
            if (_entityMaterials.TryGetValue(entity.Id, out long matId) && matId >= 0)
            {
                VortexAPI.SetMaterialBaseColor(matId, r, g, b, a);
                _entityMaterialColors[entity.Id] = (r, g, b, a);
            }

            // Imported multi-submesh models have no per-entity material — tint the shared per-mesh-path materials
            // instead (note: this tints every instance that shares the same mesh path).
            if (mr != null && !string.IsNullOrEmpty(mr.MeshPath))
            {
                long baseMat = GetMaterialForMeshPath(mr.MeshPath);
                if (baseMat >= 0) VortexAPI.SetMaterialBaseColor(baseMat, r, g, b, a);
                for (int n = 0; n < 64; n++)
                {
                    long sm = GetMaterialForMeshPath(mr.MeshPath + "#submesh" + n);
                    if (sm >= 0) VortexAPI.SetMaterialBaseColor(sm, r, g, b, a);
                    else if (n > 0) break;
                }
            }
        }

        /// <summary>
        /// Notify that an entity's mesh has changed.
        /// </summary>
        public void OnMeshChanged(Guid entityId)
        {
            // Remove old mesh so it gets recreated
            if (_entityMeshes.TryGetValue(entityId, out long meshId))
            {
                VortexAPI.DeleteMesh(meshId);
                _entityMeshes.Remove(entityId);
            }
        }

        /// <summary>
        /// Notify that an entity's camera properties have changed.
        /// </summary>
        public void OnCameraChanged(Guid entityId)
        {
            // Fire event so viewport can update camera view if previewing this camera
            CameraPropertiesChanged?.Invoke(this, entityId);
        }

        /// <summary>
        /// Event fired when camera properties are modified.
        /// </summary>
        public event EventHandler<Guid> CameraPropertiesChanged;

        /// <summary>
        /// Remove an entity from the render system.
        /// </summary>
        public void RemoveEntity(Guid entityId)
        {
            if (_entityMeshes.TryGetValue(entityId, out long meshId))
            {
                VortexAPI.DeleteMesh(meshId);
                _entityMeshes.Remove(entityId);
            }

            if (_entityMaterials.TryGetValue(entityId, out long materialId))
            {
                VortexAPI.DeleteMaterial(materialId);
                _entityMaterials.Remove(entityId);
            }

            _entityMeshPaths.Remove(entityId);
            _entityMaterialColors.Remove(entityId);
            
            // Also remove camera if exists
            RemoveEntityCamera(entityId);
        }

        /// <summary>
        /// Clear all renderables.
        /// </summary>
        public void ClearAllRenderables()
        {
            // CRITICAL: imported MODEL meshes are SHARED + owned by _submeshMeshCache (loaded ONCE from a file,
            // reused by every entity that references the same path — incl. all Ctrl+D duplicates — and kept
            // resident across scene reloads so re-loading never re-imports). _entityMeshes maps MANY entities to
            // the SAME shared mesh id, so deleting per-entry called DeleteMesh up to 50x ON ONE id (double-free
            // -> native registry corruption + leaks that made repeated loads exponentially slower, and a 4-min
            // startup with 50 copies). Here: delete ONLY non-shared (e.g. primitive) meshes, each id at most once,
            // and NEVER delete a shared cached model mesh — it stays loaded for reuse.
            var sharedMeshIds = new HashSet<long>(_submeshMeshCache.Values);
            var alreadyDeleted = new HashSet<long>();
            foreach (var meshId in _entityMeshes.Values)
            {
                if (meshId < 0 || sharedMeshIds.Contains(meshId)) continue;  // shared model mesh -> keep resident
                if (!alreadyDeleted.Add(meshId)) continue;                   // delete each unique id only once
                VortexAPI.DeleteMesh(meshId);
            }
            _entityMeshes.Clear();

            foreach (var materialId in _entityMaterials.Values)
            {
                VortexAPI.DeleteMaterial(materialId);
            }
            _entityMaterials.Clear();

            _entityMeshPaths.Clear();
            _entityMaterialColors.Clear();
            
            // Clear cameras
            foreach (var handle in _entityCameras.Values)
            {
                VortexAPI.DestroyEngineCamera(handle);
            }
            _entityCameras.Clear();
        }

        #region Camera Management

        private readonly Dictionary<Guid, CameraHandle> _entityCameras = new Dictionary<Guid, CameraHandle>();
        private CameraHandle _previewCamera = CameraHandle.Invalid;
        private bool _isPreviewingCamera;

        /// <summary>
        /// Create or update an engine camera for an entity.
        /// </summary>
        public CameraHandle GetOrCreateEntityCamera(Guid entityId, Camera cameraComponent, Transform transform)
        {
            if (_entityCameras.TryGetValue(entityId, out var existingHandle))
            {
                // Update existing camera
                UpdateEngineCamera(existingHandle, cameraComponent, transform);
                return existingHandle;
            }

            // Create new engine camera
            var desc = new CameraDescriptor
            {
                Position = new float[] { transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z },
                Rotation = EulerToQuaternion(transform.LocalRotation.X, transform.LocalRotation.Y, transform.LocalRotation.Z),
                Projection = (byte)cameraComponent.Projection,
                FieldOfView = cameraComponent.FieldOfView,
                OrthographicSize = cameraComponent.OrthographicSize,
                NearClip = cameraComponent.NearClip,
                FarClip = cameraComponent.FarClip,
                AspectRatio = 16f / 9f,
                ClearFlags = (byte)cameraComponent.ClearFlags,
                BackgroundColor = new float[] { cameraComponent.BackgroundR, cameraComponent.BackgroundG, cameraComponent.BackgroundB, 1f },
                Depth = cameraComponent.Depth,
                CullingMask = cameraComponent.CullingMask,
                CameraType = (byte)cameraComponent.CameraType,
                IsEnabled = true
            };

            var handle = VortexAPI.CreateEngineCamera(desc);
            if (handle.IsValid)
            {
                _entityCameras[entityId] = handle;
            }
            return handle;
        }

        /// <summary>
        /// Update an existing engine camera with new properties.
        /// </summary>
        private void UpdateEngineCamera(CameraHandle handle, Camera camera, Transform transform)
        {
            if (!handle.IsValid) return;

            VortexAPI.SetEngineCameraPosition(handle, 
                transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z);
            
            var quat = EulerToQuaternion(transform.LocalRotation.X, transform.LocalRotation.Y, transform.LocalRotation.Z);
            VortexAPI.SetEngineCameraRotation(handle, quat[0], quat[1], quat[2], quat[3]);
            
            VortexAPI.SetEngineCameraFOV(handle, camera.FieldOfView);
            VortexAPI.SetEngineCameraClipPlanes(handle, camera.NearClip, camera.FarClip);
            VortexAPI.SetEngineCameraProjection(handle, (CameraProjectionType)camera.Projection);
            VortexAPI.SetEngineCameraType(handle, (CameraTypeEnum)camera.CameraType);
            VortexAPI.SetEngineCameraBackgroundColor(handle, 
                camera.BackgroundR, camera.BackgroundG, camera.BackgroundB, 1f);
            VortexAPI.SetEngineCameraDepth(handle, camera.Depth);
        }

        /// <summary>
        /// Remove an entity's camera.
        /// </summary>
        public void RemoveEntityCamera(Guid entityId)
        {
            if (_entityCameras.TryGetValue(entityId, out var handle))
            {
                VortexAPI.DestroyEngineCamera(handle);
                _entityCameras.Remove(entityId);
            }
        }

        /// <summary>
        /// Get the engine camera handle for an entity.
        /// </summary>
        public CameraHandle GetEntityCamera(Guid entityId)
        {
            return _entityCameras.TryGetValue(entityId, out var handle) ? handle : CameraHandle.Invalid;
        }

        /// <summary>
        /// Render camera gizmo for a selected camera entity.
        /// </summary>
        public void RenderCameraGizmo(Guid entityId, bool isMainCamera)
        {
            if (!_entityCameras.TryGetValue(entityId, out var handle)) return;
            
            // Main camera = purple, other cameras = blue
            if (isMainCamera)
            {
                VortexAPI.RenderEngineCameraGizmo(handle, 0.608f, 0.349f, 0.714f); // Purple #9B59B6
            }
            else
            {
                VortexAPI.RenderEngineCameraGizmo(handle, 0.337f, 0.612f, 0.839f); // Blue #569CD6
            }
        }

        /// <summary>
        /// Start previewing a camera's view in the viewport.
        /// </summary>
        public void StartCameraPreview(CameraHandle camera)
        {
            _previewCamera = camera;
            _isPreviewingCamera = true;
        }

        /// <summary>
        /// Stop camera preview and return to editor camera.
        /// </summary>
        public void StopCameraPreview()
        {
            _previewCamera = CameraHandle.Invalid;
            _isPreviewingCamera = false;
        }

        /// <summary>
        /// Check if currently previewing a camera.
        /// </summary>
        public bool IsPreviewingCamera => _isPreviewingCamera;

        /// <summary>
        /// Get the camera being previewed.
        /// </summary>
        public CameraHandle PreviewCamera => _previewCamera;

        /// <summary>
        /// Apply the preview camera to the renderer (call during render loop).
        /// </summary>
        public void ApplyPreviewCameraIfActive()
        {
            if (_isPreviewingCamera && _previewCamera.IsValid)
            {
                VortexAPI.ApplyEngineCameraToRenderer(_previewCamera);
            }
        }

        /// <summary>
        /// Convert Euler angles (degrees) to quaternion.
        /// </summary>
        private float[] EulerToQuaternion(float pitch, float yaw, float roll)
        {
            // Convert to radians
            double p = pitch * Math.PI / 180.0 * 0.5;
            double y = yaw * Math.PI / 180.0 * 0.5;
            double r = roll * Math.PI / 180.0 * 0.5;

            double sinP = Math.Sin(p), cosP = Math.Cos(p);
            double sinY = Math.Sin(y), cosY = Math.Cos(y);
            double sinR = Math.Sin(r), cosR = Math.Cos(r);

            return new float[]
            {
                (float)(cosR * sinP * cosY + sinR * cosP * sinY), // X
                (float)(cosR * cosP * sinY - sinR * sinP * cosY), // Y
                (float)(sinR * cosP * cosY - cosR * sinP * sinY), // Z
                (float)(cosR * cosP * cosY + sinR * sinP * sinY)  // W
            };
        }

        #endregion

        #region Light Management

        private bool _hasSceneLights = false;
        private bool _hasDirectionalLight = false;
        private bool _hasSkybox = false;

        /// <summary>Ambient strength a game SCRIPT set via Vortex.Lighting.SetAmbient during play. While set,
        /// scene re-submits must NOT stomp it with the editor defaults below — the shipped-game loop re-submits
        /// after every scripted light change, so the default (0.35) used to win the tug-of-war every time and a
        /// pitch-black horror scene snapped back to bright. Cleared by ScriptRuntime on play begin/end.</summary>
        public static float? ScriptAmbientOverride;

        /// <summary>
        /// Submit all lights in the scene to the renderer.
        /// </summary>
        private void SubmitSceneLights(Data.Scene scene)
        {
            // Clear previous frame's lights
            VortexAPI.ClearAllLights();

            _hasSceneLights = false;
            _hasDirectionalLight = false;
            _hasSkybox = false;

            // Collect and submit all lights and skybox
            foreach (var entity in scene.Entities)
            {
                SubmitEntityLightsRecursive(entity);
            }

            // If no lights in scene, use default directional light
            if (!_hasSceneLights)
            {
                // Default sun light (like Unity/Unreal default scene)
                VortexAPI.SetDirectionalLightParams(
                    -0.5f, -0.7f, 0.5f,  // Direction
                    1.0f, 0.98f, 0.95f,   // Strong intensity for PBR
                    3.0f);
            }
            else if (!_hasDirectionalLight)
            {
                // The scene HAS lights but no directional one — force the sun OFF. The directional light is
                // PERSISTENT renderer state (ClearAllLights only clears the point/spot lists), so a default
                // sun set by an earlier lightless submit (project boot, scene switch) would otherwise burn
                // forever and drown out point/spot-only horror scenes — the flashlight looked broken because
                // a full-strength sun was still lighting the bunker.
                VortexAPI.SetDirectionalLightParams(0f, -1f, 0f, 1f, 1f, 1f, 0f);
            }
            
            // If no skybox, disable skybox rendering and set default ambient — unless a game script owns
            // the ambient right now (horror scenes crush it to ~0.01; the default would flood the dark).
            if (!_hasSkybox)
            {
                VortexAPI.EnableSkybox(false);
                VortexAPI.SetAmbientLightStrength(ScriptAmbientOverride ?? 0.35f);  // 0.35 matches the engine header default
            }
        }

        private void SubmitEntityLightsRecursive(GameEntity entity)
        {
            if (entity == null || !entity.IsActive) return;
            // Eye toggle: a hidden subtree takes its light/skybox contribution out too (edit mode only —
            // the session flag must stay inert during play/GameHost re-submits).
            if (entity.IsHiddenInEditor && !IsPlayLike) return;

            // Check for Skybox component
            var skybox = entity.GetComponent<Skybox>();
            if (skybox != null && skybox.IsEnabled)
            {
                _hasSkybox = true;
                
                // Enable skybox rendering
                VortexAPI.EnableSkybox(true);
                
                // Set skybox mode based on type
                switch (skybox.SkyboxType)
                {
                    case SkyboxType.SolidColor:
                        VortexAPI.SetSkyboxRenderMode(VortexAPI.SkyboxMode.SolidColor);
                        // Apply exposure to solid color
                        float exp = skybox.Exposure;
                        VortexAPI.SetSkyboxColor(
                            skybox.TopColorR * exp, 
                            skybox.TopColorG * exp, 
                            skybox.TopColorB * exp);
                        break;
                        
                    case SkyboxType.Gradient:
                        VortexAPI.SetSkyboxRenderMode(VortexAPI.SkyboxMode.Gradient);
                        // Set skybox colors (apply exposure)
                        float gradExp = skybox.Exposure;
                        VortexAPI.SetSkyboxGradient(
                            skybox.TopColorR * gradExp, skybox.TopColorG * gradExp, skybox.TopColorB * gradExp,
                            skybox.HorizonColorR * gradExp, skybox.HorizonColorG * gradExp, skybox.HorizonColorB * gradExp,
                            skybox.BottomColorR * gradExp, skybox.BottomColorG * gradExp, skybox.BottomColorB * gradExp);
                        break;
                        
                    case SkyboxType.Cubemap:
                    case SkyboxType.Texture:
                        // IMPORTANT: Disable the built-in gradient skybox when using texture
                        VortexAPI.EnableSkybox(false);
                        
                        // Render skybox with texture on built-in sphere or custom mesh
                        if (!string.IsNullOrEmpty(skybox.TexturePath))
                        {
                            SubmitSkyboxWithTexture(skybox);
                        }
                        else if (!string.IsNullOrEmpty(skybox.SkyboxMeshPath))
                        {
                            SubmitSkyboxMesh(skybox);
                        }
                        else
                        {
                            // No texture set - fall back to gradient
                            VortexAPI.EnableSkybox(true);
                            VortexAPI.SetSkyboxRenderMode(VortexAPI.SkyboxMode.Gradient);
                        }
                        break;
                }
                
                // Also set ambient light based on skybox colors — unless a game script owns the ambient.
                var (ambientR, ambientG, ambientB) = skybox.GetAmbientColor();
                float ambientBrightness = Math.Max(Math.Max(ambientR, ambientG), ambientB);
                ambientBrightness = Math.Max(ambientBrightness, 0.2f);
                VortexAPI.SetAmbientLightStrength(ScriptAmbientOverride ?? ambientBrightness);
            }

            var light = entity.GetComponent<ECS.Components.Lighting.Light>();
            if (light != null)
            {
                // A PRESENT light suppresses the default sun even while disabled — otherwise an
                // all-lights-off scene (horror: flashlight toggled off) snaps back to full daylight.
                _hasSceneLights = true;
            }
            if (light != null && light.IsEnabled)
            {
                var transform = entity.Transform;
                if (transform != null)
                {
                    // WORLD transform (parent chain included) — a flashlight is naturally a CHILD of the
                    // player camera, and lights used to submit LocalPosition/LocalRotation: a parented spot
                    // rendered at its local offset near the origin with an unrotated cone. Meshes already
                    // accumulate the chain; lights now match. Local forward is +Z (the old Euler math was
                    // exactly Ry·Rx·(0,0,1)), so the world forward is the matrix's third basis row.
                    float[] wm;
                    try { wm = BuildWorldMatrixWithParent(entity); }
                    catch { wm = BuildWorldMatrix(transform); }
                    float px = wm[12], py = wm[13], pz = wm[14];
                    float dirX = wm[8], dirY = wm[9], dirZ = wm[10];
                    float dl = (float)Math.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
                    if (dl > 1e-6f) { dirX /= dl; dirY /= dl; dirZ /= dl; }
                    else { dirX = 0f; dirY = 0f; dirZ = 1f; }

                    switch (light.LightType)
                    {
                        case ECS.Components.Lighting.LightType.Directional:
                            _hasDirectionalLight = true;
                            // CSM (#24): ShadowType != None turns on the sun's cascade pass. Bias mapping:
                            // the component's legacy 0.05 default scales by 0.016 to land on the tuned
                            // CSM NDC bias (0.0008); user tweaks stay proportional (same idea as spots).
                            VortexAPI.SetDirectionalLightParams(
                                dirX, dirY, dirZ,
                                light.ColorR, light.ColorG, light.ColorB,
                                light.Intensity,
                                light.ShadowType != ECS.Components.Lighting.ShadowType.None,
                                light.ShadowStrength,
                                light.ShadowBias * 0.016f);
                            break;

                        case ECS.Components.Lighting.LightType.Point:
                            // Point cube shadows (#25): same ShadowType gate + legacy bias mapping
                            // as spots (0.05 default x 0.03 = the tuned 0.0015 NDC bias).
                            VortexAPI.SubmitPointLight(
                                px, py, pz,
                                light.ColorR, light.ColorG, light.ColorB,
                                light.Intensity, light.Range,
                                light.ShadowType != ECS.Components.Lighting.ShadowType.None,
                                light.ShadowStrength,
                                light.ShadowBias * 0.03f);
                            break;

                        case ECS.Components.Lighting.LightType.Spot:
                            // Spot shadows (#23): ShadowType != None requests this spot as the frame's
                            // shadow caster (renderer takes the FIRST such spot; Soft = Hard in v1).
                            // Bias mapping: the component's ShadowBias serialized with a 0.05 default long
                            // before shadows existed — scale by 0.03 so that legacy default lands exactly
                            // on the tuned NDC-depth bias (0.0015) and user tweaks stay proportional.
                            VortexAPI.SubmitSpotLight(
                                px, py, pz,
                                dirX, dirY, dirZ,
                                light.ColorR, light.ColorG, light.ColorB,
                                light.Intensity, light.Range,
                                light.SpotAngle, light.InnerSpotAngle,
                                light.ShadowType != ECS.Components.Lighting.ShadowType.None,
                                light.ShadowStrength,
                                light.ShadowBias * 0.03f,
                                light.ShadowResolution);
                            break;
                    }
                }
            }

            // Process children
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    SubmitEntityLightsRecursive(child);
                }
            }
        }

        // Cache for built-in skybox sphere
        private long _skyboxSphereId = -1;
        private long _skyboxMaterialId = -1;
        private long _skyboxTextureId = -1;
        private string _cachedSkyboxTexturePath = null;
        private float _cachedSkyboxExposure = -1f;

        /// <summary>
        /// Submit a skybox with texture on a built-in inverted sphere.
        /// This is the simplest and most reliable way to render a textured skybox.
        /// </summary>
        private void SubmitSkyboxWithTexture(Skybox skybox)
        {
            var texturePath = skybox.TexturePath;
            var exposure = skybox.Exposure;
            var projectPath = Data.ProjectData.Current?.Path ?? "";
            
            // Build full texture path
            var fullTexturePath = System.IO.Path.IsPathRooted(texturePath)
                ? texturePath
                : System.IO.Path.Combine(projectPath, texturePath);

            // Check if texture file exists
            if (!AssetVfs.Exists(fullTexturePath))
            {
                Log($"[SceneRenderService] Skybox texture not found: {fullTexturePath}");
                return;
            }

            // Check if we need to reload (texture path or exposure changed)
            bool needsReload = _cachedSkyboxTexturePath != fullTexturePath || 
                               Math.Abs(_cachedSkyboxExposure - exposure) > 0.01f;
                               
            if (needsReload)
            {
                Log($"[SceneRenderService] Loading skybox texture: {fullTexturePath} (exposure={exposure})");
                
                // Create sphere if not exists (only once)
                if (_skyboxSphereId < 0)
                {
                    // Try to create inverted sphere, fall back to normal sphere
                    try
                    {
                        _skyboxSphereId = VortexAPI.CreateInvertedSphereMesh(1.0f);
                    }
                    catch
                    {
                        _skyboxSphereId = VortexAPI.CreateSphereMesh(1.0f);
                    }
                    
                    if (_skyboxSphereId < 0)
                    {
                        _skyboxSphereId = VortexAPI.CreateSphereMesh(1.0f);
                    }
                    Log($"[SceneRenderService] Created skybox sphere: {_skyboxSphereId}");
                }

                // Load texture
                long newTextureId = ImportTexturePath(fullTexturePath);
                if (newTextureId < 0)
                {
                    Log($"[SceneRenderService] Failed to load skybox texture");
                    return;
                }

                // Create or update material
                if (_skyboxMaterialId < 0)
                {
                    _skyboxMaterialId = VortexAPI.CreateNewMaterial();
                }

                if (_skyboxMaterialId >= 0)
                {
                    // Set material properties for skybox (UNLIT - no lighting, controlled by exposure)
                    VortexAPI.SetMaterialBaseColor(_skyboxMaterialId, 1.0f, 1.0f, 1.0f, 1.0f);
                    VortexAPI.SetMaterialAlbedoTexture(_skyboxMaterialId, newTextureId);
                    
                    // CRITICAL: Set material as unlit so it ignores all scene lighting
                    VortexAPI.SetMaterialAsUnlit(_skyboxMaterialId, true);
                    // Use exposure from skybox component (typically 0.1-4.0)
                    VortexAPI.SetMaterialEmissiveBrightness(_skyboxMaterialId, exposure);
                    
                    
                    _skyboxTextureId = newTextureId;
                    _cachedSkyboxTexturePath = fullTexturePath;
                    _cachedSkyboxExposure = exposure;
                    
                    Log($"[SceneRenderService] Skybox ready (UNLIT, exposure={exposure}): sphere={_skyboxSphereId}, material={_skyboxMaterialId}, texture={newTextureId}");
                }
            }
            else if (_skyboxMaterialId >= 0 && Math.Abs(_cachedSkyboxExposure - exposure) > 0.01f)
            {
                // Just update exposure without reloading texture (for smooth slider operation)
                VortexAPI.SetMaterialEmissiveBrightness(_skyboxMaterialId, exposure);
                _cachedSkyboxExposure = exposure;
            }

            // Render the skybox sphere
            if (_skyboxSphereId >= 0 && _skyboxMaterialId >= 0)
            {
                // Get camera position to center the skybox sphere around the camera
                // This prevents the "rectangular anomaly" artifacts when flying high
                var cameraController = EditorCameraController.Instance;
                float camX = cameraController?.PositionX ?? 0f;
                float camY = cameraController?.PositionY ?? 0f;
                float camZ = cameraController?.PositionZ ?? 0f;
                
                // Use a smaller scale that fits within the far clip plane (typically 1000)
                // The sphere should be large enough to encompass all objects but smaller than far plane
                float scale = 1000.0f;
                
                // Create world matrix with translation to camera position
                // This ensures the skybox always surrounds the camera regardless of position
                float[] worldMatrix = new float[16]
                {
                    scale, 0, 0, 0,
                    0, scale, 0, 0,
                    0, 0, scale, 0,
                    camX, camY, camZ, 1
                };

                VortexAPI.SubmitMeshForRendering(_skyboxSphereId, _skyboxMaterialId, worldMatrix);
            }
        }

        // Cache for skybox mesh
        private readonly Dictionary<string, (long meshId, long materialId, long textureId)> _skyboxMeshCache = 
            new Dictionary<string, (long, long, long)>();



        /// <summary>
        /// Submit a skybox mesh for rendering.
        /// </summary>
        private void SubmitSkyboxMesh(Skybox skybox)
        {
            var meshPath = skybox.SkyboxMeshPath;
            var texturePath = skybox.TexturePath;
            
            if (string.IsNullOrEmpty(meshPath))
            {
                Log("[SceneRenderService] Skybox mesh path is empty");
                return;
            }
            
            // Get full path
            var projectPath = Data.ProjectData.Current?.Path ?? "";
            var fullMeshPath = System.IO.Path.IsPathRooted(meshPath) 
                ? meshPath 
                : System.IO.Path.Combine(projectPath, meshPath);

            Log($"[SceneRenderService] Skybox mesh path: {fullMeshPath}");
            Log($"[SceneRenderService] Skybox texture path: {texturePath}");

            // Check if file exists
            if (!AssetVfs.Exists(fullMeshPath))
            {
                Log($"[SceneRenderService] Skybox mesh file not found: {fullMeshPath}");
                return;
            }

            // Create cache key that includes both mesh and texture
            var cacheKey = $"{fullMeshPath}|{texturePath ?? ""}";

            // Check if we have a cached mesh
            if (!_skyboxMeshCache.TryGetValue(cacheKey, out var cached))
            {
                Log($"[SceneRenderService] Importing skybox mesh...");
                
                // Import the mesh
                var submeshData = VortexAPI.ImportModelWithMaterialsFromFile(fullMeshPath);
                if (submeshData != null && submeshData.Length > 0)
                {
                    long meshId = submeshData[0].MeshId;
                    long materialId = submeshData[0].MaterialId;
                    long textureId = -1;

                    Log($"[SceneRenderService] Skybox mesh imported: meshId={meshId}, materialId={materialId}");

                    // If we have a texture, load it and apply to material
                    if (!string.IsNullOrEmpty(texturePath))
                    {
                        var fullTexturePath = System.IO.Path.IsPathRooted(texturePath)
                            ? texturePath
                            : System.IO.Path.Combine(projectPath, texturePath);

                        Log($"[SceneRenderService] Loading skybox texture: {fullTexturePath}");

                        if (AssetVfs.Exists(fullTexturePath))
                        {
                            textureId = ImportTexturePath(fullTexturePath);
                            if (textureId >= 0)
                            {
                                VortexAPI.SetMaterialAlbedoTexture(materialId, textureId);
                                Log($"[SceneRenderService] Skybox texture loaded: textureId={textureId}");
                            }
                            else
                            {
                                Log($"[SceneRenderService] Failed to load skybox texture");
                            }
                        }
                        else
                        {
                            Log($"[SceneRenderService] Skybox texture file not found: {fullTexturePath}");
                        }
                    }

                    cached = (meshId, materialId, textureId);
                    _skyboxMeshCache[cacheKey] = cached;
                    
                    Log($"[SceneRenderService] Skybox cached: mesh={meshId}, material={materialId}, texture={textureId}");
                }
                else
                {
                    Log($"[SceneRenderService] Failed to import skybox mesh: {meshPath}");
                    return;
                }
            }

            // Get camera position to center the skybox mesh around the camera
            var cameraController = EditorCameraController.Instance;
            float camX = cameraController?.PositionX ?? 0f;
            float camY = cameraController?.PositionY ?? 0f;
            float camZ = cameraController?.PositionZ ?? 0f;
            
            // Use a scale that fits within the far clip plane
            float scale = 500.0f;
            
            // Create world matrix with translation to camera position
            float[] worldMatrix = new float[16]
            {
                scale, 0, 0, 0,
                0, scale, 0, 0,
                0, 0, scale, 0,
                camX, camY, camZ, 1
            };

            // Submit skybox mesh - it will be rendered before other objects
            VortexAPI.SubmitMeshForRendering(cached.meshId, cached.materialId, worldMatrix);
        }

        /// <summary>
        /// Clear cached skybox mesh and texture (call when skybox properties change).
        /// </summary>
        public void ClearSkyboxMeshCache()
        {
            _skyboxMeshCache.Clear();
            _cachedSkyboxTexturePath = null; // Force reload of texture
            
            // Delete the old sphere to force recreation with inverted normals
            if (_skyboxSphereId >= 0)
            {
                VortexAPI.DeleteMesh(_skyboxSphereId);
                _skyboxSphereId = -1;
            }
            
            Log("[SceneRenderService] Skybox cache cleared");
        }

        /// <summary>
        /// Render light icons for all lights in the scene.
        /// </summary>
        private void RenderAllLightIcons(Data.Scene scene, GameEntity selected)
        {
            if (!VortexAPI.AreGizmosVisible) return;

            foreach (var entity in scene.Entities)
            {
                RenderLightIconRecursive(entity, selected);
            }
        }

        private void RenderLightIconRecursive(GameEntity entity, GameEntity selected)
        {
            if (entity == null || !entity.IsActive || entity.IsHiddenInEditor) return;   // eye toggle hides the icon too
            var light = entity.GetComponent<ECS.Components.Lighting.Light>();
            if (light != null)
            {
                // World position so the icon/outline sits where the light actually shines (parented lights).
                ECS.Vector3 pos;
                try { var wm = BuildWorldMatrixWithParent(entity); pos = new ECS.Vector3(wm[12], wm[13], wm[14]); }
                catch { pos = entity.Transform?.LocalPosition ?? ECS.Vector3.Zero; }
                var rot = entity.Transform?.LocalRotation ?? ECS.Vector3.Zero;
                
                // Light icon color based on type
                float iconR, iconG, iconB;
                switch (light.LightType)
                {
                    case ECS.Components.Lighting.LightType.Directional:
                        iconR = 1.0f; iconG = 0.95f; iconB = 0.5f; // Yellow-gold
                        break;
                    case ECS.Components.Lighting.LightType.Point:
                        iconR = 0.5f; iconG = 0.8f; iconB = 1.0f; // Light blue
                        break;
                    case ECS.Components.Lighting.LightType.Spot:
                        iconR = 0.5f; iconG = 1.0f; iconB = 0.5f; // Light green
                        break;
                    default:
                        iconR = 1.0f; iconG = 1.0f; iconB = 1.0f;
                        break;
                }

                // TODO: Render actual light icon using VortexAPI.RenderLightIcon when available
                // For now, we render a simple selection outline for the selected light
                if (entity == selected)
                {
                    VortexAPI.RenderSelectionOutline(
                        pos.X, pos.Y, pos.Z,
                        0.5f, 0.5f, 0.5f,
                        rot.X, rot.Y, rot.Z);
                }
            }

            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    RenderLightIconRecursive(child, selected);
                }
            }
        }

        #endregion

        public void Dispose()
        {
            Shutdown();
        }
    }
}
