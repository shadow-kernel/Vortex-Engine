using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services
{
    // ---- retained instances (#361) ------------------------------------------------------------------------------------------
    // The play-mode submit used to send the scene again every frame something moved — a moving camera or a scripted door was
    // enough — and the renderer merged and re-sorted all of it: frame cost grew with the entity count, not with what changed.
    // Now every rigid mesh of the scene is a persistent instance in the renderer (Engine/Graphics/Instancing/RenderQueue.h),
    // owned by its entity's RenderNode, and a submit sends only what changed since the last one — instances created, moved,
    // re-coloured or removed — in ONE call. What changed comes from the ECS (RenderChanges): entities whose transform changed
    // (their subtree's instances take the new matrices from the transforms' cached world matrices), renderers that changed
    // (resolved again), and structure changes — an entity added, removed, re-parented, (de)activated, a component added or
    // removed — which make the submit walk the scene once and compare every node with what it sent. The renderer re-sorts only
    // when the set of (mesh, material, layer) changes. Entities under an enabled Animator keep the per-frame submit: their
    // skinned meshes carry this frame's bone palette. Edit mode walks the scene on each of its submits (it submits only when
    // something changed) and sends just the differences as well.
    public partial class SceneRenderService
    {
        private static bool _retainedEnabled = Environment.GetEnvironmentVariable("VORTEX_RETAINED_SUBMIT") != "0";

        /// <summary>The retained submit (#361); off (VORTEX_RETAINED_SUBMIT=0, or set by a check that compares both) the whole
        /// scene is submitted as before — every frame in edit mode, the static split in play. An engine library without the
        /// retained instances switches it off by itself.</summary>
        internal static bool RetainedSubmit
        {
            get => _retainedEnabled;
            set
            {
                if (_retainedEnabled == value) return;
                _retainedEnabled = value;
                var inst = _instance;
                if (inst != null) inst.DropRetained(clearNative: true);
                RenderChanges.StructureChanged();
            }
        }

        /// <summary>An entity's part of the retained scene: its rigid instances in the renderer and what they were made of.</summary>
        internal sealed class RenderNode
        {
            public GameEntity Entity;
            public int Index = -1;           // in _nodes
            public int Seen;                 // the scene walk that reached it last
            public MeshRenderer Renderer;    // what the instances were resolved from …
            public int RendererVersion = -1;
            public int Epoch = -1;           // … at this _resolveEpoch
            public int WorldVersion = -1;    // the Transform.WorldVersion its instances carry
            public int Count;                // its instances (Handles)
            public int[] Handles = Array.Empty<int>();
            public long[] Meshes = Array.Empty<long>();
            public long[] Materials = Array.Empty<long>();
            public int[] Layers = Array.Empty<int>();
            public float[] Tints = Array.Empty<float>();   // r, g, b, a per instance
            // Under an enabled Animator the entity is submitted every frame instead (its skinned meshes carry this frame's bone
            // palette) and owns no instances: AnimCount items in the arrays above, Skinned[i] = drawn with the palette.
            public bool Animated;
            public int AnimCount;
            public bool[] Skinned = Array.Empty<bool>();
            public GameEntity AnimatorOwner;
            public Animator Animator;
            // Its resolution could not load what the renderer names (a mesh or .vmat file missing or broken): it is tried
            // again — on every walk, and in play every UnresolvedRetryMs — as the whole-scene submit tried on every submit.
            public bool Unresolved;
        }

        /// <summary>Where the ops go: the renderer, or a test's recorder. Returns the renderer's instance epoch; false when the
        /// engine library has no retained instances.</summary>
        internal delegate bool InstanceSink(VortexAPI.RenderInstanceOp[] ops, int count, out uint epoch);
        internal InstanceSink Sink = VortexAPI.TryApplyRenderInstances;

        /// <summary>Resolves an entity's rigid instances into the scratch (<see cref="AddResolved"/>); a test replaces it, the
        /// real one (<see cref="ResolveFromAssets"/>) needs the engine (meshes, materials).</summary>
        internal Action<GameEntity, MeshRenderer> Resolver;

        private readonly List<RenderNode> _nodes = new List<RenderNode>();
        private readonly List<RenderNode> _animatedNodes = new List<RenderNode>();
        private int _walk;
        private uint _nativeEpoch;
        private Data.Scene _retainedScene;
        private int _modeKey = -1;
        private bool _retainedFullPending = true;
        private ObservableCollection<GameEntity> _hookedRoots;

        /// <summary>Counts up whenever something a resolution read changed — the layer presentation, a model's meshes or
        /// materials, the .vmat cache: every node resolves again on the next submit.</summary>
        internal static int ResolveEpoch;

        private VortexAPI.RenderInstanceOp[] _ops = new VortexAPI.RenderInstanceOp[256];
        private int _opCount;
        private int[] _freeHandles = new int[64];
        private int _freeCount, _nextHandle, _liveInstances;

        // the nodes whose resolution failed (RenderNode.Unresolved), and when play tried them last
        private readonly List<RenderNode> _unresolved = new List<RenderNode>();
        private long _unresolvedTriedAt;
        private bool _unresolvedNow;
        internal const int UnresolvedRetryMs = 500;
        private static bool _failureLogged;

        // one entity's resolved instances (scratch); _resolveFailed: the resolution could not load a mesh or material it names
        private bool _resolveFailed;
        private string _failedPath;   // which of its paths, when the real resolution failed
        private readonly HashSet<string> _failedThisRound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _resCount;
        private long[] _resMesh = new long[8], _resMat = new long[8];
        private int[] _resLayer = new int[8];
        private float[] _resTint = new float[32];

        /// <summary>Retained instances the renderer holds for the scene, and the instance ops the last submit sent.</summary>
        public static int LastRetainedInstances, LastInstanceOps;
        /// <summary>Submits that walked the whole scene (structure changes, edit mode) — the rest touched only what changed.</summary>
        public static long SceneWalks;

        // ---------------------------------------------------------------------------------------------------- the submit

        /// <summary>The first half of a retained submit, before the lights and the other per-frame submits: walk the scene (a
        /// structure change, edit mode) or take only what the ECS reported, and with a walk rebuild the index those submits
        /// iterate. False when the engine library has no retained instances (then the whole-scene submit runs).</summary>
        internal bool PrepareRetained(Data.Scene scene)
        {
            if (_splitWasActive)
            {
                // coming from the static split: its retained set would draw a second time (VortexRendering: an empty pass drops it)
                if (VortexAPI.BeginStaticSubmit()) VortexAPI.EndStaticSubmit();
                _splitWasActive = false;
                _splitScene = null;
            }
            if (!Sink(_ops, 0, out uint epoch)) { _retainedEnabled = false; return false; }   // older native library
            VortexAPI.MarkFrameSubmit();   // the per-frame part (animated characters …) replaces last frame's even when empty
            if (epoch != _nativeEpoch) { DropRetained(clearNative: false); _nativeEpoch = epoch; }   // the renderer dropped them all
            HookRoots(scene);
            int mode = ModeKey();
            if (mode != _modeKey) { _modeKey = mode; ResolveEpoch++; _retainedFullPending = true; }

            _opCount = 0;
            _submitN = 0;
            bool full = _retainedFullPending || RenderChanges.Structure || !IsPlayLike || !ReferenceEquals(scene, _retainedScene);
            if (full) Reconcile(scene);
            else Incremental();
            if (_unresolved.Count > 0) RetryUnresolved(full);
            RenderChanges.Clear();
            return true;
        }

        /// <summary>The second half: the entities under an enabled Animator every frame (their skinned meshes carry this frame's
        /// palette), then every instance change of this submit in one call.</summary>
        internal void FinishRetained()
        {
            try { for (int i = 0; i < _animatedNodes.Count; i++) SubmitAnimated(_animatedNodes[i]); }
            finally { _batcher.Flush(FlushBatch); }

            LastInstanceOps = _opCount;
            FlushOps();
            LastRetainedInstances = _liveInstances;
            _submitN += LastInstanceOps;
        }

        private void FlushOps()
        {
            if (_opCount == 0) return;
            if (Sink(_ops, _opCount, out uint epoch) && epoch != _nativeEpoch) _retainedFullPending = true;
            _opCount = 0;
        }

        /// <summary>A submit failed half-way: the ops it gathered never reached the renderer while the nodes already count them,
        /// so start over — the renderer drops every instance and the next submit walks the scene and sends them all again.
        /// The exception goes on to the caller, as before; this says so once per session.</summary>
        internal void RetainedFailed(Exception ex)
        {
            try { DropRetained(clearNative: true); } catch { }
            RenderChanges.StructureChanged();
            if (_failureLogged) return;
            _failureLogged = true;
            try { ConsoleService.Instance.LogWarning("Scene submit failed (" + ex.GetType().Name + ": " + ex.Message + ") — the next frame sends the whole scene to the renderer again."); } catch { }
        }

        // ---------------------------------------------------------------------------------------------------- reloads

        /// <summary>
        /// Meshes are about to be freed — a model file changed on disk or is reimported (InvalidateModel), or no scene uses it
        /// any more (#339, #358). The instances that draw them leave the renderer first, the rest of the scene stays on screen,
        /// and their entities resolve the model again on the next submit, which is requested now: edit mode submits only on a
        /// change, and the renderer used to drop the whole retained scene when it found an instance of a destroyed mesh.
        /// </summary>
        internal void ReleaseMeshes(HashSet<long> ids)
        {
            if (ids == null || ids.Count == 0) return;
            bool any = false;
            for (int i = 0; i < _nodes.Count; i++)
            {
                var node = _nodes[i];
                int n = node.Animated ? node.AnimCount : node.Count;
                for (int k = 0; k < n; k++)
                {
                    if (!ids.Contains(node.Meshes[k])) continue;
                    if (node.Animated) node.AnimCount = 0;   // submitted per frame: nothing until it is resolved again
                    else ReleaseInstances(node);
                    node.Epoch = -1;
                    any = true;
                    break;
                }
            }
            if (!any) return;
            FlushOps();
            _retainedFullPending = true;   // play too: the next submit walks the scene and resolves them
            RequestResubmit();
        }

        /// <summary>A model or mesh file arrived or changed (the asset watcher, an import): the entities whose resolution failed
        /// try again on the next submit, which is requested now, instead of at the next retry.</summary>
        internal void RetryUnresolvedNow()
        {
            if (_unresolved.Count == 0) return;
            _unresolvedNow = true;
            RequestResubmit();
        }

        private static void RequestResubmit()
        {
            RuntimeDirty = true;   // the player's submit-once loop
            try { Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }

        /// <summary>The nodes whose resolution failed: a walk (edit mode, a structure change) has just tried them; otherwise play
        /// tries them every <see cref="UnresolvedRetryMs"/> — the whole-scene submit tried on every submit.</summary>
        private void RetryUnresolved(bool walked)
        {
            long now = Environment.TickCount64;
            bool due = !walked && (_unresolvedNow || now - _unresolvedTriedAt >= UnresolvedRetryMs);
            // a path that failed again this round is not looked up again for the next entity that names it: a missing file
            // shared by thousands of entities costs one look per round, not one per entity
            _failedThisRound.Clear();
            for (int i = _unresolved.Count - 1; i >= 0; i--)
            {
                var node = _unresolved[i];
                if (node.Index < 0 || !node.Unresolved) { node.Unresolved = false; _unresolved.RemoveAt(i); continue; }   // gone, or resolved
                if (!due) continue;
                var mr = node.Renderer;
                if (mr == null || !ReferenceEquals(mr.Entity, node.Entity) || node.Animated != NearestAnimatorEnabled(node.Entity)) { _retainedFullPending = true; continue; }
                if (_failedThisRound.Count > 0 && (_failedThisRound.Contains(mr.MeshPath ?? "") || _failedThisRound.Contains(mr.MaterialPath ?? ""))) continue;
                Update(node, mr, node.Animated);
                if (!node.Unresolved) _unresolved.RemoveAt(i);
                else if (_failedPath != null) _failedThisRound.Add(_failedPath);
            }
            if (walked || due) { _unresolvedTriedAt = now; _unresolvedNow = false; }
        }

        /// <summary>The render-relevant modes of the layer presentation (#175): a change resolves every node again.</summary>
        private static int ModeKey() =>
            (IsPlayLike ? 1 : 0) | ((int)EditorViewmodelPreview << 1) | (DebugThirdPersonView ? 16 : 0) | (DebugShowViewmodel ? 32 : 0);

        /// <summary>The rendered scene's root list is watched: an entity added to or removed from it is a structure change.</summary>
        private void HookRoots(Data.Scene scene)
        {
            var roots = scene.Entities;
            if (ReferenceEquals(roots, _hookedRoots)) return;
            if (_hookedRoots != null) _hookedRoots.CollectionChanged -= OnRootsChanged;
            _hookedRoots = roots;
            roots.CollectionChanged += OnRootsChanged;
            RenderChanges.StructureChanged();
        }

        private static void OnRootsChanged(object sender, NotifyCollectionChangedEventArgs e) => RenderChanges.StructureChanged();

        // ---------------------------------------------------------------------------------------------------- the walk

        /// <summary>
        /// The entities the other per-frame submits need, in the scene walk's depth-first order and by its visibility rules
        /// (activeSelf cascades, the editor's eye toggle in edit mode) — the lights, decals, terrains, foliage and water each
        /// walked the whole scene every frame for a handful of components. Rebuilt by every walk; between walks nothing they
        /// depend on changed (a component added or removed, an entity activated … is a structure change).
        /// </summary>
        internal sealed class SceneIndex
        {
            public readonly List<GameEntity> Lights = new List<GameEntity>();     // a Light or a Skybox
            public readonly List<GameEntity> Decals = new List<GameEntity>();     // a Decal component (DecalService visits 64 levels)
            public readonly List<GameEntity> Terrains = new List<GameEntity>();
            public readonly List<GameEntity> Foliage = new List<GameEntity>();
            public readonly List<GameEntity> Water = new List<GameEntity>();

            public void Clear() { Lights.Clear(); Decals.Clear(); Terrains.Clear(); Foliage.Clear(); Water.Clear(); }
        }

        private readonly SceneIndex _index = new SceneIndex();

        /// <summary>Walk the scene like SubmitEntityRecursive and bring every node in line with its entity; nodes the walk no
        /// longer reaches (removed, deactivated, hidden, renderer gone) lose their instances.</summary>
        private void Reconcile(Data.Scene scene)
        {
            _walk++;
            SceneWalks++;
            _animatedNodes.Clear();
            _index.Clear();
            var roots = scene.Entities;
            for (int i = 0; i < roots.Count; i++) Visit(roots[i], null, null, 0);
            for (int i = _nodes.Count - 1; i >= 0; i--)
            {
                var node = _nodes[i];
                if (node.Seen != _walk) RemoveNode(node);
            }
            _retainedScene = scene;
            _retainedFullPending = false;
        }

        /// <param name="animator">The nearest Animator above (SubmitEntity looks up the chain to the first one, enabled or not),
        /// on <paramref name="animatorOwner"/>.</param>
        private void Visit(GameEntity entity, GameEntity animatorOwner, Animator animator, int depth)
        {
            if (entity == null) return;
            if (entity.IsHiddenInEditor && !IsPlayLike) return;   // the editor's eye toggle, edit mode only
            if (!entity.IsActive) return;                         // activeSelf cascades to the children
            // one pass over the components: the first renderer and Animator (GetComponent semantics), the index entries
            MeshRenderer mr = null;
            Animator own = null;
            bool light = false, decal = false, terrain = false, foliage = false, water = false;
            var comps = entity.Components;
            for (int i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                if (c is MeshRenderer m) { if (mr == null) mr = m; }
                else if (c is Animator a) { if (own == null) own = a; }
                else if (c is ECS.Components.Lighting.Light || c is Skybox) light = true;
                else if (c is ECS.Components.Rendering.Decal) decal = true;
                else if (c is ECS.Components.Rendering.Terrain) terrain = true;
                else if (c is ECS.Components.Rendering.Foliage) foliage = true;
                else if (c is ECS.Components.Rendering.Water) water = true;
            }
            if (light) _index.Lights.Add(entity);
            if (decal && depth <= 64) _index.Decals.Add(entity);
            if (terrain) _index.Terrains.Add(entity);
            if (foliage) _index.Foliage.Add(entity);
            if (water) _index.Water.Add(entity);
            if (own != null) { animator = own; animatorOwner = entity; }
            if (mr != null)
            {
                var node = entity.RenderNode;
                if (node == null) { node = new RenderNode { Entity = entity }; entity.RenderNode = node; }
                if (node.Index < 0) { node.Index = _nodes.Count; _nodes.Add(node); }
                node.Seen = _walk;
                node.AnimatorOwner = animatorOwner;
                node.Animator = animator;
                Update(node, mr, animator != null && animator.IsEnabled);
                if (node.Animated) _animatedNodes.Add(node);
            }
            var children = entity.Children;
            for (int i = 0; i < children.Count; i++) Visit(children[i], animatorOwner, animator, depth + 1);
        }

        /// <summary>Only what the ECS reported: renderers that changed resolve again, moved subtrees send their new matrices.</summary>
        private void Incremental()
        {
            var restyled = RenderChanges.Restyled;
            for (int i = 0; i < restyled.Count; i++)
            {
                var e = restyled[i];
                var node = e.RenderNode;
                var mr = e.GetComponent<MeshRenderer>();
                // a renderer the walk never placed, or one that would switch between the per-frame and the retained submit:
                // where it belongs is the walk's decision — walk once
                if (node == null || node.Index < 0 || mr == null || node.Animated != NearestAnimatorEnabled(e)) { _retainedFullPending = true; continue; }
                Update(node, mr, node.Animated);
            }
            var moved = RenderChanges.Moved;
            for (int i = 0; i < moved.Count; i++) MoveSubtree(moved[i]);
            if (_retainedFullPending && _retainedScene != null) Reconcile(_retainedScene);
        }

        private void MoveSubtree(GameEntity e)
        {
            var node = e.RenderNode;
            if (node != null && node.Index >= 0 && !node.Animated && node.Count > 0) SendMoves(node);
            var children = e.Children;
            for (int i = 0; i < children.Count; i++) MoveSubtree(children[i]);
        }

        private static bool NearestAnimatorEnabled(GameEntity e)
        {
            for (var x = e; x != null; x = x.Parent)
            {
                var a = x.GetComponent<Animator>();
                if (a != null) return a.IsEnabled;
            }
            return false;
        }

        // ---------------------------------------------------------------------------------------------------- one node

        private void Update(RenderNode node, MeshRenderer mr, bool animated)
        {
            bool stale = node.Renderer != mr || node.RendererVersion != mr.RenderVersion || node.Epoch != ResolveEpoch;
            if (animated)
            {
                if (!node.Animated) { ReleaseInstances(node); node.Animated = true; stale = true; }
                if (!stale) return;
                // resolved once, submitted every frame by SubmitAnimated
                Resolve(node.Entity, mr);
                int n = _resCount;
                EnsureNodeCapacity(node, n);
                if (node.Skinned.Length < n) node.Skinned = new bool[Math.Max(n, node.Handles.Length)];
                for (int i = 0; i < n; i++)
                {
                    node.Meshes[i] = _resMesh[i]; node.Materials[i] = _resMat[i]; node.Layers[i] = _resLayer[i];
                    Array.Copy(_resTint, i * 4, node.Tints, i * 4, 4);
                    node.Skinned[i] = Resolver == null && Core.Animation.AnimationService.Instance.IsMeshSkinned(_resMesh[i]);
                }
                node.AnimCount = n;
                Stamp(node, mr);
                return;
            }
            if (node.Animated) { node.Animated = false; node.AnimCount = 0; stale = true; }   // it owned no instances: Count is 0
            if (stale)
            {
                Resolve(node.Entity, mr);
                Stamp(node, mr);
                Apply(node);
            }
            else if (node.Count > 0) SendMoves(node);
        }

        /// <summary>The node was resolved from this renderer. One whose resolution could not load a mesh or material it names
        /// stays stale and is listed, so it is tried again (its file may arrive later — an import, a copy, a download).</summary>
        private void Stamp(RenderNode node, MeshRenderer mr)
        {
            node.Renderer = mr; node.RendererVersion = mr.RenderVersion;
            if (!_resolveFailed) { node.Epoch = ResolveEpoch; node.Unresolved = false; return; }
            node.Epoch = -1;
            if (!node.Unresolved) { node.Unresolved = true; _unresolved.Add(node); }
        }

        /// <summary>An entity under an enabled Animator, every frame — SubmitEntity's work with its resolution cached on the node:
        /// what remains per frame is the bone palette (the animated pose while playing, the bind pose in edit mode), the cached
        /// world matrix and the submits. A skinned submesh carries the palette, the rest are rigid (batched per mesh / material).</summary>
        private void SubmitAnimated(RenderNode node)
        {
            int n = node.AnimCount;
            var t = node.Entity.Transform;
            if (n == 0 || t == null || node.Renderer == null) return;
            float[] world = t.WorldMatrix;
            float[] palette = null; int bones = 0;
            if (node.Animator != null && node.Animator.IsEnabled)
                Core.Animation.AnimationService.Instance.TryGetPalette(node.AnimatorOwner, node.Renderer.MeshPath, out palette, out bones);
            for (int i = 0; i < n; i++)
            {
                int k = i * 4;
                if (palette != null && node.Skinned[i])
                    VortexAPI.SubmitSkinnedMeshTinted(node.Meshes[i], node.Materials[i], world, palette, bones, node.Layers[i], node.Tints[k], node.Tints[k + 1], node.Tints[k + 2], node.Tints[k + 3]);
                else
                    _batcher.Add(node.Meshes[i], node.Materials[i], node.Layers[i], world, node.Tints[k], node.Tints[k + 1], node.Tints[k + 2], node.Tints[k + 3]);
                _submitN++;
            }
        }

        /// <summary>Send the node's matrix to its instances when its world matrix changed since it was sent last.</summary>
        private void SendMoves(RenderNode node)
        {
            var t = node.Entity.Transform;
            if (t == null) return;
            int wv = t.WorldVersion;
            if (wv == node.WorldVersion) return;
            node.WorldVersion = wv;
            var world = t.WorldMatrix;
            for (int i = 0; i < node.Count; i++)
                Emit(VortexAPI.InstanceMove, node.Handles[i], 0, 0, 0, world, node.Tints, i * 4);
        }

        /// <summary>Make the node's instances what the scratch says: same run = a move (matrix + tint), another mesh, material or
        /// layer = a set, extra ones removed.</summary>
        private void Apply(RenderNode node)
        {
            var t = node.Entity.Transform;
            float[] world = t?.WorldMatrix;
            int wv = t != null ? t.WorldVersion : -1;
            if (world == null) _resCount = 0;
            int n = _resCount;
            EnsureNodeCapacity(node, n);
            for (int i = 0; i < n; i++)
            {
                bool had = i < node.Count;
                bool sameRun = had && node.Meshes[i] == _resMesh[i] && node.Materials[i] == _resMat[i] && node.Layers[i] == _resLayer[i];
                bool sameTint = had && node.Tints[i * 4] == _resTint[i * 4] && node.Tints[i * 4 + 1] == _resTint[i * 4 + 1]
                    && node.Tints[i * 4 + 2] == _resTint[i * 4 + 2] && node.Tints[i * 4 + 3] == _resTint[i * 4 + 3];
                node.Meshes[i] = _resMesh[i]; node.Materials[i] = _resMat[i]; node.Layers[i] = _resLayer[i];
                Array.Copy(_resTint, i * 4, node.Tints, i * 4, 4);
                if (!had) node.Handles[i] = AllocHandle();
                if (!sameRun) Emit(VortexAPI.InstanceSet, node.Handles[i], _resMesh[i], _resMat[i], _resLayer[i], world, node.Tints, i * 4);
                else if (!sameTint || wv != node.WorldVersion) Emit(VortexAPI.InstanceMove, node.Handles[i], 0, 0, 0, world, node.Tints, i * 4);
            }
            for (int i = n; i < node.Count; i++) { Emit(VortexAPI.InstanceRemove, node.Handles[i], 0, 0, 0, null, null, 0); FreeHandle(node.Handles[i]); }
            _liveInstances += n - node.Count;
            node.Count = n;
            node.WorldVersion = wv;
        }

        private static void EnsureNodeCapacity(RenderNode node, int n)
        {
            if (node.Handles.Length >= n) return;
            int cap = Math.Max(n, 1);
            Array.Resize(ref node.Handles, cap);
            Array.Resize(ref node.Meshes, cap);
            Array.Resize(ref node.Materials, cap);
            Array.Resize(ref node.Layers, cap);
            Array.Resize(ref node.Tints, cap * 4);
        }

        private void ReleaseInstances(RenderNode node)
        {
            for (int i = 0; i < node.Count; i++) { Emit(VortexAPI.InstanceRemove, node.Handles[i], 0, 0, 0, null, null, 0); FreeHandle(node.Handles[i]); }
            _liveInstances -= node.Count;
            node.Count = 0;
            node.WorldVersion = -1;
        }

        private void RemoveNode(RenderNode node)
        {
            ReleaseInstances(node);
            int i = node.Index;
            if (i >= 0 && i < _nodes.Count && ReferenceEquals(_nodes[i], node))
            {
                var last = _nodes[_nodes.Count - 1];
                _nodes[i] = last; last.Index = i;
                _nodes.RemoveAt(_nodes.Count - 1);
            }
            node.Index = -1;
            node.Renderer = null;
            node.Epoch = -1;
            node.Animated = false;
        }

        /// <summary>Forget every node and handle; the renderer's instances go too when <paramref name="clearNative"/> (else it
        /// dropped them itself). The next submit walks the scene and sends everything again.</summary>
        internal void DropRetained(bool clearNative)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                var node = _nodes[i];
                node.Count = 0; node.Index = -1; node.Renderer = null; node.Epoch = -1; node.WorldVersion = -1; node.Animated = false;
            }
            _nodes.Clear();
            _animatedNodes.Clear();
            for (int i = 0; i < _unresolved.Count; i++) _unresolved[i].Unresolved = false;
            _unresolved.Clear();
            _freeCount = 0; _nextHandle = 0; _opCount = 0; _liveInstances = 0;
            _retainedFullPending = true;
            _retainedScene = null;
            if (clearNative) { try { VortexAPI.ClearRenderInstances(); } catch { } }
        }

        /// <summary>The instances of an entity tree leave the renderer now — before its meshes may be freed (#358).</summary>
        private void ReleaseTree(GameEntity e)
        {
            if (e == null) return;
            var node = e.RenderNode;
            if (node != null && node.Index >= 0) RemoveNode(node);
            var children = e.Children;
            for (int i = 0; i < children.Count; i++) ReleaseTree(children[i]);
        }

        // ---------------------------------------------------------------------------------------------------- resolution

        /// <summary>The rigid instances SubmitEntity would submit for this entity — mesh, material, layer and tint each — into the
        /// scratch, by the same rules in the same order (the layer presentation #175, a model's submeshes, the tint #331).</summary>
        private void Resolve(GameEntity entity, MeshRenderer mr)
        {
            _resCount = 0;
            _resolveFailed = false;
            _failedPath = null;
            if (Resolver != null) { Resolver(entity, mr); return; }
            ResolveFromAssets(entity, mr);
        }

        /// <summary>The real resolution: the engine's meshes and materials for the renderer's paths (see <see cref="Resolve"/>).</summary>
        internal void ResolveFromAssets(GameEntity entity, MeshRenderer mr)
        {
            if (!mr.IsEnabled || string.IsNullOrEmpty(mr.MeshPath) || entity.Transform == null) return;
            long meshId = GetOrCreateMesh(entity.Id, mr);
            if (meshId < 0) { _resolveFailed = true; _failedPath = mr.MeshPath; return; }   // missing or broken: tried again (Stamp)
            int layer = mr.RenderLayer;
            if (!PresentLayer(ref layer)) return;
            float r, g, b, a;
            if (string.IsNullOrEmpty(mr.MaterialPath) && IsModelPath(mr.MeshPath))
            {
                var set = ResolveSubmeshes(mr.MeshPath);
                if (set != null)
                {
                    for (int i = 0; i < set.MeshIds.Length; i++)
                    {
                        long subMat = GetMaterialForMeshPath(set.Keys[i]);
                        TintFor(mr, subMat, out r, out g, out b, out a);
                        AddResolved(set.MeshIds[i], subMat, layer, r, g, b, a);
                    }
                    return;
                }
            }
            long materialId = GetOrCreateMaterial(entity.Id, mr);
            // a .vmat that did not load draws with the fallback look and is tried again, as the whole-scene submit did
            if (!string.IsNullOrEmpty(mr.MaterialPath) && !_vmatPathCache.ContainsKey(mr.MaterialPath)) { _resolveFailed = true; _failedPath = mr.MaterialPath; }
            TintFor(mr, materialId, out r, out g, out b, out a);
            AddResolved(meshId, materialId, layer, r, g, b, a);
        }

        /// <summary>For a test's <see cref="Resolver"/>: the resolution could not load what the renderer names.</summary>
        internal void ResolveFailed() => _resolveFailed = true;

        internal void AddResolved(long mesh, long material, int layer, float r, float g, float b, float a)
        {
            int i = _resCount;
            if (i == _resMesh.Length)
            {
                Array.Resize(ref _resMesh, i * 2); Array.Resize(ref _resMat, i * 2);
                Array.Resize(ref _resLayer, i * 2); Array.Resize(ref _resTint, i * 8);
            }
            _resMesh[i] = mesh; _resMat[i] = material; _resLayer[i] = layer;
            _resTint[i * 4] = r; _resTint[i * 4 + 1] = g; _resTint[i * 4 + 2] = b; _resTint[i * 4 + 3] = a;
            _resCount = i + 1;
        }

        // ---------------------------------------------------------------------------------------------------- ops, handles

        private unsafe void Emit(int kind, int handle, long mesh, long material, int layer, float[] world, float[] tints, int tint)
        {
            if (_opCount == _ops.Length) Array.Resize(ref _ops, _ops.Length * 2);
            fixed (VortexAPI.RenderInstanceOp* op = &_ops[_opCount])
            {
                op->Handle = handle; op->Kind = kind; op->Mesh = mesh; op->Material = material; op->Layer = layer; op->Reserved = 0;
                if (world != null) for (int k = 0; k < 16; k++) op->World[k] = world[k];
                if (tints != null) for (int k = 0; k < 4; k++) op->Color[k] = tints[tint + k];
            }
            _opCount++;
        }

        private int AllocHandle()
        {
            if (_freeCount > 0) return _freeHandles[--_freeCount];
            return _nextHandle++;
        }

        private void FreeHandle(int h)
        {
            if (_freeCount == _freeHandles.Length) Array.Resize(ref _freeHandles, _freeHandles.Length * 2);
            _freeHandles[_freeCount++] = h;
        }
    }
}
