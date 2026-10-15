using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Scripting;

namespace Editor.Scripting
{
    /// <summary>
    /// In-engine gameplay scripting runtime — the Unity/Unreal-style "compile + run" loop. On Play it
    /// compiles every Assets/Scripts/*.cs (via the in-box C# compiler, no extra dependencies) into one
    /// assembly, instantiates a VortexBehaviour for every Script component of every entity (#408: an entity
    /// can run several), and calls Start once + Update every tick (OnDestroy on Stop). Behaviours affect the live game
    /// through this class (it is the IScriptHost: move entities, read input).
    /// </summary>
    public sealed class ScriptRuntime : Vortex.IScriptHost
    {
        private static ScriptRuntime _instance;
        public static ScriptRuntime Instance => _instance ?? (_instance = new ScriptRuntime());

        // Maps a UNIQUE per-entity handle -> the entity. We assign our own handle (not the engine EntityId)
        // because engine ids can collide/be invalid for editor entities; a collision previously made a script
        // move the WRONG entity (e.g. the Ground instead of the camera).
        private readonly Dictionary<long, GameEntity> _entitiesById = new Dictionary<long, GameEntity>();
        private long _nextHandle;
        private readonly List<Vortex.VortexBehaviour> _behaviours = new List<Vortex.VortexBehaviour>();
        // Every behaviour an entity runs, in the order of its Script components (#408): each Script component gets its
        // own instance (own fields, Start/Update, callbacks) and they all share the entity's one handle, so EntityId,
        // TriggerHit ids and MoveCharacter's selfId keep meaning the entity. Collision/trigger/area/message dispatch
        // fans out over the list.
        private readonly Dictionary<GameEntity, List<Vortex.VortexBehaviour>> _behavioursByEntity = new Dictionary<GameEntity, List<Vortex.VortexBehaviour>>();
        // The Script component each running behaviour was made from, so field overrides reach the right instance (#408).
        private readonly Dictionary<Vortex.VortexBehaviour, Script> _scriptOf = new Dictionary<Vortex.VortexBehaviour, Script>(ReferenceEqualityComparer.Instance);
        // Reusable buffers drained each tick from CollisionService (avoid per-frame allocation).
        private readonly List<Editor.Core.Services.Physics.CollisionService.Contact> _evEnter = new List<Editor.Core.Services.Physics.CollisionService.Contact>();
        private readonly List<Editor.Core.Services.Physics.CollisionService.Contact> _evStay = new List<Editor.Core.Services.Physics.CollisionService.Contact>();
        private readonly List<Editor.Core.Services.Physics.CollisionService.Contact> _evExit = new List<Editor.Core.Services.Physics.CollisionService.Contact>();
        private readonly List<Editor.Core.Services.Physics.CollisionService.Contact> _evCollide = new List<Editor.Core.Services.Physics.CollisionService.Contact>();

        // The loaded gameplay assembly (precompiled .dll for the shipped game, or the runtime-compiled scripts).
        // Kept so a UI button can spin up its screen's "<Screen>Actions" class on demand even if the user never
        // attached it to a scene entity — the "one class per UI, no wiring" model.
        private Assembly _scriptAsm;
        // On-demand-instantiated UI action controllers, keyed by class name (a cached null means "looked up, none").
        // These have NO scene entity (EntityId 0); they exist purely to receive .vui button clicks + tick.
        private readonly Dictionary<string, Vortex.VortexBehaviour> _uiActions = new Dictionary<string, Vortex.VortexBehaviour>();

        public string DebugBehaviourNames()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _behaviours.Count; i++) { if (i > 0) sb.Append(","); sb.Append(_behaviours[i].GetType().Name); }
            return _behaviours.Count + "[" + sb + "]";
        }

        /// <summary>The scene entity a behaviour handle is attached to (null for UI action
        /// controllers / after End). Used by the Vortex.Audio script surface to reach the
        /// entity's AudioSource component.</summary>
        internal GameEntity FindEntityByHandle(long handle)
        {
            return _entitiesById.TryGetValue(handle, out var e) ? e : null;
        }
        private bool _active;

        // Whose code runs right now (#406) — null between callbacks: the behaviour whose callback (Start, Update, a coroutine
        // step, a message, a UI action...) runs, or, when none does, the entity whose behaviour tree or area event runs.
        private Vortex.VortexBehaviour _dispatching;
        private GameEntity _dispatchingEntity;

        /// <summary>The entity a script character moves (#406), from whose code is running: a characterId equal to the
        /// EntityId of the behaviour whose callback runs names that behaviour's entity (all of an entity's behaviours share
        /// one, #408); while an entity's behaviour tree ticks or its area event is raised, a characterId that is one of its
        /// handles (the tree's Agent) names it. Any other id is a custom one and names no entity, even when it happens to
        /// equal some other entity's handle (a characterId 1 used to resolve to whatever entity had handle 1). An id the
        /// collision world already knows an owner for keeps that owner (CollisionService.KnownOwner).</summary>
        internal GameEntity CharacterOwner(long characterId)
        {
            if (characterId == 0) return null;
            if (_dispatching != null) return _dispatching.EntityId == characterId ? FindEntityByHandle(characterId) : null;
            return _dispatchingEntity != null && ReferenceEquals(FindEntityByHandle(characterId), _dispatchingEntity) ? _dispatchingEntity : null;
        }

        /// <summary>Who ran before <see cref="EnterEntityCode"/> — handed back to <see cref="ExitEntityCode"/>.</summary>
        internal struct CodeContext { internal Vortex.VortexBehaviour Behaviour; internal GameEntity Entity; }

        /// <summary>Code that runs on behalf of <paramref name="e"/> without being one of its behaviours' callbacks — its
        /// behaviour tree's tasks, the handlers of its area events (#406): until <see cref="ExitEntityCode"/>, a characterId
        /// that is one of its handles names it.</summary>
        internal CodeContext EnterEntityCode(GameEntity e)
        {
            var prev = new CodeContext { Behaviour = _dispatching, Entity = _dispatchingEntity };
            _dispatching = null;
            _dispatchingEntity = e;
            return prev;
        }

        internal void ExitEntityCode(CodeContext prev) { _dispatching = prev.Behaviour; _dispatchingEntity = prev.Entity; }

        // ================= Scripting wave (#35-#40): handles, queries, coroutines, spawn/destroy =================

        // Handles for ARBITRARY entities (not just scripted ones), assigned on demand — the ids TriggerHit,
        // RaycastHit and Scene.Find hand to scripts. One canonical handle per entity for the whole run.
        private readonly Dictionary<GameEntity, long> _handlesByEntity = new Dictionary<GameEntity, long>();

        internal long HandleForEntity(GameEntity e)
        {
            if (e == null) return 0;
            if (_handlesByEntity.TryGetValue(e, out var h)) return h;
            long handle = ++_nextHandle;
            _entitiesById[handle] = e;
            _handlesByEntity[e] = handle;
            return handle;
        }

        // ---- entity queries (#39) ----

        internal long FindEntityHandle(string name)
        {
            if (string.IsNullOrEmpty(name) || _currentScene?.Entities == null) return 0;
            GameEntity found = null;
            void Walk(GameEntity e)
            {
                if (found != null || e == null) return;
                if (e.Name == name) { found = e; return; }
                if (e.Children != null) foreach (var c in e.Children) Walk(c);
            }
            foreach (var e in _currentScene.Entities) { Walk(e); if (found != null) break; }
            return found != null ? HandleForEntity(found) : 0;
        }

        internal long[] FindEntityHandlesByTag(string tag)
        {
            var result = new List<long>();
            if (string.IsNullOrEmpty(tag) || _currentScene?.Entities == null) return result.ToArray();
            void Walk(GameEntity e)
            {
                if (e == null) return;
                if (e.Tag == tag) result.Add(HandleForEntity(e));
                if (e.Children != null) foreach (var c in e.Children) Walk(c);
            }
            foreach (var e in _currentScene.Entities) Walk(e);
            return result.ToArray();
        }

        internal long GetParentHandle(long id)
        {
            var e = FindEntityByHandle(id);
            return e?.Parent != null ? HandleForEntity(e.Parent) : 0;
        }

        internal long[] GetChildHandles(long id)
        {
            var e = FindEntityByHandle(id);
            if (e?.Children == null || e.Children.Count == 0) return new long[0];
            var result = new long[e.Children.Count];
            for (int i = 0; i < e.Children.Count; i++) result[i] = HandleForEntity(e.Children[i]);
            return result;
        }

        internal string EntityNameOf(long id) { return FindEntityByHandle(id)?.Name ?? ""; }
        internal string EntityTagOf(long id) { return FindEntityByHandle(id)?.Tag ?? ""; }

        /// <summary>The behaviours running on an entity, in Script-component order (#408) — empty for none.</summary>
        internal IReadOnlyList<Vortex.VortexBehaviour> BehavioursOf(GameEntity e)
        {
            return e != null && _behavioursByEntity.TryGetValue(e, out var list) ? list : (IReadOnlyList<Vortex.VortexBehaviour>)Array.Empty<Vortex.VortexBehaviour>();
        }

        internal IReadOnlyList<Vortex.VortexBehaviour> BehavioursOf(long id) => BehavioursOf(FindEntityByHandle(id));

        /// <summary>The first behaviour of type T on an entity (#408: an entity can run several, so pick by type —
        /// the old single lookup returned the entity's one behaviour and null when that was another class). One whose
        /// OnDestroy already ran (a sibling destroyed first, play ending) is skipped.</summary>
        internal T BehaviourOf<T>(long id) where T : class
        {
            var list = BehavioursOf(id);
            for (int i = 0; i < list.Count; i++)
                if (list[i] is T t && IsLive(list[i])) return t;
            return null;
        }

        internal bool SetEntityActive(long id, bool active)
        {
            var e = FindEntityByHandle(id);
            if (e == null) return false;
            // an OnEnable / OnDisable of this very switch turning it again, either way (Unity refuses that too, #422)
            if (_activating.Contains(e)) { WarnReentrantSetActive(e); return false; }
            if (e.IsActive == active) return true;   // no-op guard: a re-add would duplicate collision shapes
            _activating.Add(e);
            try
            {
                // #422: OnDisable for the subtree's active scripts first — their colliders and bodies are still there
                if (!active && ActiveInHierarchy(e))
                {
                    NotifySubtree(e, enable: false);
                    if (!ReferenceEquals(FindEntityByHandle(id), e) || e.IsActive == active) return true;   // an OnDisable destroyed it
                }
                if (!_activeChanged.ContainsKey(e)) _activeChanged[e] = e.IsActive;   // restore on play end
                e.IsActive = active;
                try { e.SyncEngineState(); } catch { }   // under an inactive parent it stays out of the engine
                // #51: collision + audio follow the active state (previously a documented limitation —
                // a deactivated door/monster stayed solid and kept emitting).
                try
                {
                    if (active) Editor.Core.Services.Physics.CollisionService.AddEntityShapes(e);
                    else Editor.Core.Services.Physics.CollisionService.RemoveEntityShapes(e);
                }
                catch { }
                try
                {
                    if (active) Editor.Core.Services.Physics.PhysicsService.AddEntity(e);
                    else Editor.Core.Services.Physics.PhysicsService.RemoveEntity(e);
                }
                catch { }
                if (!active)
                {
                    try { StopAudioRecursive(e); } catch { }
                    // #407: the subtree's characters stop blocking (managed capsule + Jolt character); MoveCharacter after
                    // re-activation registers them again
                    try { Editor.Core.Services.Physics.PhysicsService.RemoveCharactersIn(e); } catch { }
                }
                Editor.Core.Services.SceneRenderService.RuntimeDirty = Editor.Core.Services.SceneRenderService.StaticDirty = true;   // structural (#364 A)
                // #422: OnEnable once the colliders, bodies and audio are back — for the scripts whose entity is now active in
                // the hierarchy (a child switched off on its own stays out)
                if (active && ActiveInHierarchy(e)) NotifySubtree(e, enable: true);
                return true;
            }
            finally { _activating.Remove(e); }
        }

        // ---- the script lifecycle (#422): OnEnable / Start / OnDisable, each delivered once ----

        private readonly HashSet<GameEntity> _activating = new HashSet<GameEntity>();   // a SetActive in progress
        private readonly HashSet<GameEntity> _reentryWarned = new HashSet<GameEntity>();

        private void WarnReentrantSetActive(GameEntity e)
        {
            if (!_reentryWarned.Add(e)) return;
            try
            {
                Editor.Core.Services.ConsoleService.Instance.LogWarning("Scene.SetActive: '" + e.Name + "' is being switched on or off right now — "
                    + "a SetActive on it from its own OnEnable / OnDisable is ignored. (Shown once per entity.)");
            }
            catch { }
        }

        /// <summary>The entity and every ancestor are active (the play world's rule; the scene's own flag is not consulted).</summary>
        private static bool ActiveInHierarchy(GameEntity e)
        {
            if (e == null) return false;
            for (var p = e; p != null; p = p.Parent) if (!p.IsActive) return false;
            return true;
        }

        /// <summary>The scene entity a behaviour runs on — null for a UI action class (EntityId 0) or after its entity left.</summary>
        private GameEntity EntityOf(Vortex.VortexBehaviour b)
            => b != null && b.EntityId != 0 && _entitiesById.TryGetValue(b.EntityId, out var e) ? e : null;

        /// <summary>Would the behaviour be active now: live, its Script switched on, its entity active in the hierarchy (a UI
        /// action class, which has no entity, always is).</summary>
        private bool ShouldBeEnabled(Vortex.VortexBehaviour b)
            => IsLive(b) && !b.ScriptDisabled && (b.EntityId == 0 || ActiveInHierarchy(EntityOf(b)));

        private void CallOnEnable(Vortex.VortexBehaviour b)
        {
            if (b.LifecycleEnabled || !ShouldBeEnabled(b)) return;
            b.LifecycleEnabled = true;   // first: a re-entrant path sees it delivered
            var prev = _dispatching; _dispatching = b;   // its character moves are its entity's (#406)
            try { b.OnEnable(); }
            catch (Exception ex) { LogScriptError("OnEnable", b, ex); }
            finally { _dispatching = prev; }
        }

        private void CallOnDisable(Vortex.VortexBehaviour b)
        {
            if (b == null || b.Destroyed || !b.LifecycleEnabled) return;
            b.LifecycleEnabled = false;
            var prev = _dispatching; _dispatching = b;
            try { b.OnDisable(); }
            catch (Exception ex) { LogScriptError("OnDisable", b, ex); }
            finally { _dispatching = prev; }
        }

        private void CallStart(Vortex.VortexBehaviour b)
        {
            if (!IsLive(b) || b.Started) return;
            b.Started = true;
            var prev = _dispatching; _dispatching = b;
            try { b.Start(); }
            catch (Exception ex) { LogScriptError("Start", b, ex); }
            finally { _dispatching = prev; }
        }

        /// <summary>New behaviours come alive: OnEnable for the ones that are active (all of them first), then Start for each —
        /// the order of play start, a spawn and a script attached while playing (#422). <paramref name="list"/> is a snapshot:
        /// what a callback spawns is started by its own spawn.</summary>
        private void EnableAndStart(IReadOnlyList<Vortex.VortexBehaviour> list)
        {
            for (int i = 0; i < list.Count && _active; i++) CallOnEnable(list[i]);
            for (int i = 0; i < list.Count && _active; i++) CallStart(list[i]);
        }

        /// <summary>OnEnable or OnDisable for the scripts of <paramref name="root"/>'s subtree that are reached through active
        /// entities (#422) — the ones whose activeInHierarchy follows the root's; a child switched off on its own, and what
        /// is under it, keeps its state. Each behaviour is asked again right before its call, so one switched off or
        /// destroyed by an earlier callback of the same pass is skipped.</summary>
        private void NotifySubtree(GameEntity root, bool enable)
        {
            List<Vortex.VortexBehaviour> list = null;
            void Walk(GameEntity x)
            {
                if (x == null || !x.IsActive) return;
                if (_behavioursByEntity.TryGetValue(x, out var own) && own.Count > 0) (list ??= new List<Vortex.VortexBehaviour>()).AddRange(own);
                if (x.Children != null) foreach (var c in x.Children) Walk(c);
            }
            Walk(root);
            if (list == null) return;
            foreach (var b in list)
            {
                if (enable) CallOnEnable(b);
                else CallOnDisable(b);
            }
        }

        private static void StopAudioRecursive(Editor.ECS.GameEntity e)
        {
            var src = e.GetComponent<Editor.ECS.Components.Audio.AudioSource>();
            if (src != null) Editor.Core.Services.AudioPlaybackService.Instance.ScriptStop(src);
            if (e.Children != null)
                foreach (var c in e.Children) StopAudioRecursive(c);
        }

        // ---- component-level enable toggles (#51) ----

        internal bool SetRendererEnabled(long id, bool enabled)
        {
            var e = FindEntityByHandle(id);
            var mr = e != null ? e.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>() : null;
            if (mr == null) return false;
            mr.IsEnabled = enabled;   // SubmitScene skips disabled renderers
            Editor.Core.Services.SceneRenderService.RuntimeDirty = Editor.Core.Services.SceneRenderService.StaticDirty = true;   // structural (#364 A)
            return true;
        }

        internal bool SetColliderEnabled(long id, bool enabled)
        {
            var e = FindEntityByHandle(id);
            if (e == null) return false;
            // Entity-level shape rebuild: remove this subtree's shapes, then re-add — AddRecursive
            // honors each collider component's IsEnabled, so flip the flags first.
            try
            {
                SetColliderFlagRecursive(e, enabled);
                Editor.Core.Services.Physics.CollisionService.RemoveEntityShapes(e);
                if (enabled && e.IsActive) Editor.Core.Services.Physics.CollisionService.AddEntityShapes(e);
                // Rigid bodies follow the collider flags too (a disabled collider = no body) — a live ragdoll's part
                // bodies included (#341): a hidden passenger must not be hit by the car's own probes.
                if (!enabled && Editor.Core.Services.Physics.RagdollService.IsActive(e)) Editor.Core.Services.Physics.RagdollService.Deactivate(e);
                Editor.Core.Services.Physics.PhysicsService.RemoveEntity(e);
                if (enabled && e.IsActive) Editor.Core.Services.Physics.PhysicsService.AddEntity(e);
            }
            catch { return false; }
            return true;
        }

        private static void SetColliderFlagRecursive(Editor.ECS.GameEntity e, bool enabled)
        {
            foreach (var comp in e.Components)
                if (comp is Editor.ECS.Components.Physics.Collider col) col.IsEnabled = enabled;
            if (e.Children != null)
                foreach (var c in e.Children) SetColliderFlagRecursive(c, enabled);
        }

        internal bool IsEntityActive(long id)
        {
            var e = FindEntityByHandle(id);
            return e != null && e.IsActive;
        }

        internal bool IsEntityActiveInHierarchy(long id)
        {
            var e = FindEntityByHandle(id);
            if (e == null || !e.IsActive) return false;
            for (var p = e.Parent; p != null; p = p.Parent)
                if (!p.IsActive) return false;
            return true;
        }

        // ---- entity messaging (#38) ----

        internal void SendEntityMessage(long targetId, string message, object arg)
        {
            // every behaviour of the target hears it (#408); one destroyed by an earlier handler is skipped
            var list = BehavioursOf(targetId);
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                if (!IsLive(b)) continue;
                var prev = _dispatching; _dispatching = b;
                try { b.OnMessage(message ?? "", arg); }
                catch (Exception ex) { LogScriptError("OnMessage", b, ex); }
                finally { _dispatching = prev; }
            }
        }

        /// <summary>Still running (its OnDestroy has not run: entity not destroyed, play not ended) — every loop and dispatch
        /// over behaviours checks it, since a handler earlier in the same pass may destroy an entity (#408).</summary>
        private static bool IsLive(Vortex.VortexBehaviour b) => b != null && !b.Destroyed;

        // Destroyed behaviours stay in _behaviours until the next safe point: removing them while the Start / Update /
        // LateUpdate loops walk the list by index shifted it, and every behaviour after them skipped the frame (with
        // several scripts per entity a destroy removes several at once).
        private bool _compactPending;

        private void CompactBehaviours()
        {
            if (!_compactPending) return;
            _compactPending = false;
            _behaviours.RemoveAll(b => !IsLive(b));
            if (_fixedBehaviours.Count > 0)
            {
                _fixedBehaviours.RemoveAll(b => !IsLive(b));
                if (_fixedBehaviours.Count == 0) Editor.Core.Services.Physics.PhysicsService.BeforeStep = null;   // #481: no cost again
            }
        }

        // ---- coroutines + timers (#37) ----

        private sealed class Co
        {
            public Vortex.VortexBehaviour Owner;
            public System.Collections.IEnumerator Routine;
            public float Wait;
            public bool Realtime;   // the wait counts real seconds (WaitForSecondsRealtime), not game time (#425)
            public Vortex.Coroutine Handle;
        }
        private sealed class Inv
        {
            public Vortex.VortexBehaviour Owner;
            public Action Action;
            public float Due;
            public float Interval;   // 0 = one-shot
            public bool Cancelled;
        }
        private readonly List<Co> _coroutines = new List<Co>();
        private readonly List<Inv> _invokes = new List<Inv>();

        internal Vortex.Coroutine StartCoroutine(Vortex.VortexBehaviour owner, System.Collections.IEnumerator routine)
        {
            var handle = new Vortex.Coroutine();
            if (routine == null || !_active) { handle.Done = true; return handle; }
            var co = new Co { Owner = owner, Routine = routine, Wait = 0f, Handle = handle };
            _coroutines.Add(co);
            AdvanceCoroutine(co);   // Unity semantics: the first MoveNext runs immediately
            return handle;
        }

        internal void StopAllCoroutines(Vortex.VortexBehaviour owner)
        {
            for (int i = 0; i < _coroutines.Count; i++)
                if (ReferenceEquals(_coroutines[i].Owner, owner)) _coroutines[i].Handle.Stopped = true;
        }

        internal void ScheduleInvoke(Vortex.VortexBehaviour owner, Action action, float delay, float interval)
        {
            if (action == null || !_active) return;
            _invokes.Add(new Inv { Owner = owner, Action = action, Due = delay > 0f ? delay : 0f, Interval = interval });
        }

        internal void CancelInvokes(Vortex.VortexBehaviour owner)
        {
            for (int i = 0; i < _invokes.Count; i++)
                if (ReferenceEquals(_invokes[i].Owner, owner)) _invokes[i].Cancelled = true;
        }

        private void AdvanceCoroutine(Co c)
        {
            bool more;
            var prev = _dispatching; _dispatching = c.Owner;
            try { more = c.Routine.MoveNext(); }
            catch (Exception ex) { LogScriptError("Coroutine", c.Owner, ex); c.Handle.Done = true; return; }
            finally { _dispatching = prev; }
            if (!more) { c.Handle.Done = true; return; }
            // yield return null = one frame; WaitForSeconds counts game time, WaitForSecondsRealtime real time (#425)
            var current = c.Routine.Current;
            if (current is Vortex.WaitForSecondsRealtime real) { c.Wait = real.Seconds; c.Realtime = true; }
            else { c.Wait = (current as Vortex.WaitForSeconds)?.Seconds ?? 0f; c.Realtime = false; }
        }

        /// <summary>Coroutines and Invoke timers advance by <paramref name="dt"/> (game seconds — slowed or held by the time
        /// scale, #425); a coroutine waiting on WaitForSecondsRealtime by <paramref name="realDt"/>.</summary>
        private void TickCoroutinesAndInvokes(float dt, float realDt)
        {
            // Snapshot the counts: coroutines/invokes STARTED during this tick already ran their first
            // step (StartCoroutine) or wait a full delay — they must not double-advance this frame.
            int nc = _coroutines.Count;
            for (int i = 0; i < nc; i++)
            {
                var c = _coroutines[i];
                if (c.Handle.Stopped || c.Handle.Done) continue;
                c.Wait -= c.Realtime ? realDt : dt;
                if (c.Wait > 0f) continue;
                AdvanceCoroutine(c);
            }
            _coroutines.RemoveAll(c => c.Handle.Stopped || c.Handle.Done);

            int ni = _invokes.Count;
            for (int i = 0; i < ni; i++)
            {
                var v = _invokes[i];
                if (v.Cancelled) continue;
                v.Due -= dt;
                if (v.Due > 0f) continue;
                var prev = _dispatching; _dispatching = v.Owner;
                try { v.Action(); }
                catch (Exception ex) { LogScriptError("Invoke", v.Owner, ex); }
                finally { _dispatching = prev; }
                if (v.Interval > 0f) v.Due = v.Interval;   // fixed re-arm (no catch-up burst after a hitch)
                else v.Cancelled = true;
            }
            _invokes.RemoveAll(v => v.Cancelled);
        }

        // ---- debug draw + on-screen console (#42) ----

        private sealed class DebugShape
        {
            public bool IsSphere;
            public Vortex.Vector3 A, B;   // line endpoints; A = center for spheres
            public float Radius;
            public int ColorKey;
            public float Until;           // debug-clock deadline (duration 0 = one frame)
        }
        private readonly List<DebugShape> _debugShapes = new List<DebugShape>();
        private float _debugClock;
        private long _dbgCubeMesh = -1, _dbgSphereMesh = -1;                      // lazy, cached for the process
        private readonly Dictionary<int, long> _dbgMaterials = new Dictionary<int, long>();
        private bool _f9Held;

        internal void AddDebugLine(Vortex.Vector3 a, Vortex.Vector3 b, float r, float g, float bl, float duration)
        {
            if (!_active) return;
            _debugShapes.Add(new DebugShape { IsSphere = false, A = a, B = b, ColorKey = PackColor(r, g, bl), Until = _debugClock + duration });
        }

        internal void AddDebugSphere(Vortex.Vector3 center, float radius, float r, float g, float bl, float duration)
        {
            if (!_active) return;
            _debugShapes.Add(new DebugShape { IsSphere = true, A = center, Radius = radius > 0f ? radius : 0.01f, ColorKey = PackColor(r, g, bl), Until = _debugClock + duration });
        }

        private static int PackColor(float r, float g, float b)
        {
            int q(float v) { return (int)(Math.Max(0f, Math.Min(1f, v)) * 31f + 0.5f); }
            return (q(r) << 10) | (q(g) << 5) | q(b);
        }

        private long MaterialForColor(int key)
        {
            if (_dbgMaterials.TryGetValue(key, out var id)) return id;
            id = Editor.DllWrapper.VortexAPI.CreateNewMaterial();
            Editor.DllWrapper.VortexAPI.SetMaterialBaseColor(id,
                ((key >> 10) & 31) / 31f, ((key >> 5) & 31) / 31f, (key & 31) / 31f, 1f);
            _dbgMaterials[key] = id;
            return id;
        }

        /// <summary>Re-submit every live debug shape into the wire-gizmo queue (cleared by the renderer
        /// each frame) and age them out. Wire gizmos draw always-on-top in the viewport, the play window
        /// AND the shipped player.</summary>
        private void SubmitDebugShapes(float dt)
        {
            _debugClock += dt;
            if (_debugShapes.Count == 0) return;
            if (_dbgCubeMesh < 0) _dbgCubeMesh = Editor.DllWrapper.VortexAPI.CreateCubeMesh(1.0f);
            if (_dbgSphereMesh < 0) _dbgSphereMesh = Editor.DllWrapper.VortexAPI.CreateSphereMesh(0.5f);

            var m = new float[16];
            for (int i = 0; i < _debugShapes.Count; i++)
            {
                var s = _debugShapes[i];
                long mat = MaterialForColor(s.ColorKey);
                if (s.IsSphere)
                {
                    float sc = s.Radius * 2f;   // mesh radius is 0.5
                    m[0] = sc; m[1] = 0; m[2] = 0; m[3] = 0;
                    m[4] = 0; m[5] = sc; m[6] = 0; m[7] = 0;
                    m[8] = 0; m[9] = 0; m[10] = sc; m[11] = 0;
                    m[12] = s.A.X; m[13] = s.A.Y; m[14] = s.A.Z; m[15] = 1;
                    Editor.DllWrapper.VortexAPI.SubmitGizmoWireForRendering(_dbgSphereMesh, mat, m);
                }
                else
                {
                    // Thin box spanning A -> B: rows = right*T | up*T | dir*len | midpoint.
                    float dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y, dz = s.B.Z - s.A.Z;
                    float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (len < 1e-5f) continue;
                    float ix = dx / len, iy = dy / len, iz = dz / len;
                    float ux = 0f, uy = 1f, uz = 0f;
                    if (Math.Abs(iy) > 0.99f) { uy = 0f; uz = 1f; }
                    float rx = uy * iz - uz * iy, ry = uz * ix - ux * iz, rz = ux * iy - uy * ix;
                    float rl = (float)Math.Sqrt(rx * rx + ry * ry + rz * rz);
                    if (rl < 1e-6f) continue;
                    rx /= rl; ry /= rl; rz /= rl;
                    float upx = iy * rz - iz * ry, upy = iz * rx - ix * rz, upz = ix * ry - iy * rx;
                    const float T = 0.02f;
                    m[0] = rx * T; m[1] = ry * T; m[2] = rz * T; m[3] = 0;
                    m[4] = upx * T; m[5] = upy * T; m[6] = upz * T; m[7] = 0;
                    m[8] = ix * len; m[9] = iy * len; m[10] = iz * len; m[11] = 0;
                    m[12] = (s.A.X + s.B.X) * 0.5f; m[13] = (s.A.Y + s.B.Y) * 0.5f; m[14] = (s.A.Z + s.B.Z) * 0.5f; m[15] = 1;
                    Editor.DllWrapper.VortexAPI.SubmitGizmoWireForRendering(_dbgCubeMesh, mat, m);
                }
            }
            _debugShapes.RemoveAll(sh => sh.Until <= _debugClock);
        }

        /// <summary>The in-game dev console overlay (#42): F9 toggles; draws the newest log lines over the
        /// game via the same immediate-mode UI the scripts use. Works in play mode AND shipped builds.</summary>
        private void RenderDebugConsole()
        {
            bool f9 = ((Vortex.IScriptHost)this).GetKey("F9");
            if (f9 && !_f9Held) Vortex.Debug.ConsoleVisible = !Vortex.Debug.ConsoleVisible;
            _f9Held = f9;
            if (!Vortex.Debug.ConsoleVisible || _uiW < 10f) return;

            const int maxLines = 14;
            float w = Math.Min(_uiW * 0.62f, 680f);
            float lineH = 17f, pad = 8f;
            int count;
            Vortex.Debug.ConsoleLine[] lines;
            lock (Vortex.Debug.Lines)
            {
                count = Math.Min(maxLines, Vortex.Debug.Lines.Count);
                lines = new Vortex.Debug.ConsoleLine[count];
                for (int i = 0; i < count; i++) lines[i] = Vortex.Debug.Lines[Vortex.Debug.Lines.Count - count + i];
            }
            float h = pad * 2f + 18f + count * lineH;
            var host = (Vortex.IScriptHost)this;
            host.UIRect(10f, 10f, w, h, 0.05f, 0.05f, 0.07f, 0.82f, 8f);
            host.UIText(10f + pad, 10f + pad - 2f, w - pad * 2f, 16f, "DEV CONSOLE  [F9]", 11f, 0.55f, 0.55f, 0.6f, 1f, 0, 700);
            for (int i = 0; i < count; i++)
            {
                float r = 0.86f, g = 0.86f, b = 0.88f;
                if (lines[i].Level == 1) { r = 1f; g = 0.78f; b = 0.35f; }
                else if (lines[i].Level == 2) { r = 1f; g = 0.42f; b = 0.42f; }
                host.UIText(10f + pad, 10f + pad + 16f + i * lineH, w - pad * 2f, lineH,
                    lines[i].Text, 12f, r, g, b, 1f, 0, 400);
            }
        }

        // ---- runtime instantiate/destroy (#36) + the play-mode restore ledger ----
        // Editor play runs the AUTHORED scene in place — runtime spawns/destroys/SetActive must not leak
        // into the asset. Every mutation is recorded and rolled back in End().

        private readonly List<GameEntity> _spawned = new List<GameEntity>();
        private sealed class RemovedRec { public GameEntity Entity; public GameEntity Parent; public int Index; }
        private readonly List<RemovedRec> _removed = new List<RemovedRec>();
        private readonly Dictionary<GameEntity, bool> _activeChanged = new Dictionary<GameEntity, bool>();

        internal long InstantiatePrefabAt(string prefabPath, Vortex.Vector3 pos, float yawDeg)
        {
            if (!_active || _currentScene == null || string.IsNullOrEmpty(prefabPath)) return 0;
            GameEntity ent = null;
            try { ent = Editor.Core.Services.PrefabService.Instance.InstantiatePrefab(prefabPath, _currentScene, null, false); }
            catch (Exception ex) { LogScriptError("Scene", "Instantiate", ex); }
            if (ent == null)
            {
                try { Editor.Core.Services.ConsoleService.Instance.LogError("Instantiate: prefab not found: " + prefabPath); } catch { }
                return 0;
            }
            _spawned.Add(ent);

            long handle = HandleForEntity(ent);
            ((Vortex.IScriptHost)this).SetPosition(handle, pos);
            var rot = ((Vortex.IScriptHost)this).GetRotation(handle); rot.Y = yawDeg;
            ((Vortex.IScriptHost)this).SetRotation(handle, rot);

            try { ent.SyncEngineStateRecursive(true); } catch { }
            Editor.Core.Services.SceneRenderService.RuntimeDirty = Editor.Core.Services.SceneRenderService.StaticDirty = true;   // structural (#364 A)
            try { Editor.Core.Services.Physics.CollisionService.AddEntityShapes(ent); } catch { }
            try { Editor.Core.Services.Physics.PhysicsService.AddEntity(ent); } catch { }   // spawned crates/barrels simulate at once
            try { Editor.Core.Services.AudioPlaybackService.Instance.AddEntity(ent); } catch { }   // its sounds play (Play On Awake next tick)

            // Its Script components come alive immediately — a spawned monster thinks from THIS frame: OnEnable (its colliders
            // and bodies exist already, #422), then Start.
            if (_scriptAsm != null)
            {
                int before = _behaviours.Count;
                InstantiateRecursive(ent, _scriptAsm);
                // just this spawn's behaviours: one whose Start() spawns again starts its own, and one that destroys an
                // entity only marks it (the list never shrinks under this loop)
                EnableAndStart(_behaviours.GetRange(before, _behaviours.Count - before));
            }
            return handle;
        }

        /// <summary>Big-engine socket system: at play start, every BoneAttachment that REGISTERS a prefab
        /// (SocketPrefabPath) auto-instantiates that prefab (the weapon) and attaches it to the bone — no per-weapon
        /// script. Spawns are ledgered in _spawned so they roll back cleanly on play end. Runs BEFORE the behaviour
        /// pass so the spawned prefab's own scripts (if any) come alive with everything else.</summary>
        private void ExpandSocketPrefabs(Editor.Core.Data.Scene scene)
        {
            if (scene?.Entities == null) return;
            var sockets = new List<GameEntity>();
            void Collect(GameEntity e)
            {
                if (e == null) return;
                var att = e.GetComponent<Editor.ECS.Components.Animation.BoneAttachment>();
                if (att != null && att.IsEnabled && !string.IsNullOrEmpty(att.SocketPrefabPath) && !string.IsNullOrEmpty(att.BoneName))
                    sockets.Add(e);
                if (e.Children != null) foreach (var c in e.Children) Collect(c);
            }
            foreach (var root in new List<GameEntity>(scene.Entities)) Collect(root);

            foreach (var socketEnt in sockets)
            {
                var comp = socketEnt.GetComponent<Editor.ECS.Components.Animation.BoneAttachment>();
                if (comp == null) continue;
                GameEntity weapon = null;
                try { weapon = Editor.Core.Services.PrefabService.Instance.InstantiatePrefab(comp.SocketPrefabPath, scene, socketEnt, false); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[socket] spawn failed: " + ex.Message); }
                if (weapon == null) { try { Editor.Core.Services.ConsoleService.Instance.LogError("Socket: prefab not found: " + comp.SocketPrefabPath); } catch { } continue; }
                _spawned.Add(weapon);
                // Socket render-layer override: force the spawned prefab's meshes onto the socket's layer
                // (e.g. 2 = Third-Person only, so the gun hides together with the body it hangs on).
                if (comp.SocketRenderLayer >= 0)
                {
                    try { ApplyRenderLayerRecursive(weapon, comp.SocketRenderLayer); } catch { }
                }
                try { weapon.SyncEngineState(); } catch { }
                try { Editor.Core.Services.Physics.CollisionService.AddEntityShapes(weapon); } catch { }
                try { Editor.Core.Services.Physics.PhysicsService.AddEntity(weapon); } catch { }
                try { Editor.Core.Services.AudioPlaybackService.Instance.AddEntity(weapon); } catch { }   // audio began before the sockets
                // Attach the spawned weapon to the bone (target = the socket's resolved skeletal owner). It keeps
                // its own scale; the socket pass drives it every frame after animation.
                // Resolve the skeletal target the weapon attaches to. Normally the nearest ancestor with an
                // Animator (weapon socket parented under the character) or an explicit TargetEntityId. If neither
                // resolves (e.g. the socket entity isn't under the character), fall back to the ONLY animated
                // character in the scene so a mis-parented socket still works, and warn — otherwise the weapon
                // would silently stay at the spawn point (the "weapon doesn't follow the hand" trap).
                var target = Editor.Core.Animation.BoneSocketService.Instance.ResolveTargetOf(scene, socketEnt);
                if (target == null)
                {
                    target = FindLoneAnimatedEntity(scene);
                    try
                    {
                        if (target != null)
                            Editor.Core.Services.ConsoleService.Instance.LogWarning("Socket '" + socketEnt.Name +
                                "' has no animated ancestor — falling back to '" + target.Name + "'. Parent the socket under the character or set its Target for a stable bind.");
                        else
                            Editor.Core.Services.ConsoleService.Instance.LogError("Socket '" + socketEnt.Name +
                                "' can't find a character to attach to: parent it under the animated character, or set the Bone Attachment's Target.");
                    }
                    catch { }
                }
                try
                {
                    Editor.Core.Animation.BoneSocketService.Instance.Attach(weapon, target, comp.BoneName,
                        new System.Numerics.Vector3(comp.OffsetPosition.X, comp.OffsetPosition.Y, comp.OffsetPosition.Z),
                        new System.Numerics.Vector3(comp.OffsetRotation.X, comp.OffsetRotation.Y, comp.OffsetRotation.Z));
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[socket] attach failed: " + ex.Message); }
            }
        }

        /// <summary>The scene's single animated character (exactly one entity with an enabled Animator),
        /// or null when there are zero or several — used as a socket target fallback for a mis-parented
        /// socket so the weapon still binds instead of silently staying at the spawn point.</summary>
        private static GameEntity FindLoneAnimatedEntity(Editor.Core.Data.Scene scene)
        {
            if (scene?.Entities == null) return null;
            GameEntity found = null; int count = 0;
            void Scan(GameEntity e)
            {
                if (e == null) return;
                var a = e.GetComponent<Editor.ECS.Components.Animation.Animator>();
                if (a != null && a.IsEnabled) { found = e; count++; }
                if (e.Children != null) foreach (var c in e.Children) Scan(c);
            }
            foreach (var root in scene.Entities) Scan(root);
            return count == 1 ? found : null;
        }

        /// <summary>Force a render layer onto every MeshRenderer in a spawned prefab subtree
        /// (socket render-layer override — runtime instances only, authored prefabs stay untouched).</summary>
        private static void ApplyRenderLayerRecursive(GameEntity e, int layer)
        {
            if (e == null) return;
            var comps = e.Components;
            for (int i = 0; i < comps.Count; i++)
            {
                if (comps[i] is Editor.ECS.Components.Rendering.MeshRenderer mr)
                    mr.RenderLayer = layer;
            }
            if (e.Children != null)
                foreach (var c in e.Children) ApplyRenderLayerRecursive(c, layer);
        }

        internal bool DestroyEntity(long id)
        {
            var e = FindEntityByHandle(id);
            if (e == null || _currentScene == null) return false;

            // OnDestroy + unregister every behaviour in the subtree — all of an entity's behaviours (#408) — and their
            // coroutines/timers.
            var doomed = new List<KeyValuePair<GameEntity, Vortex.VortexBehaviour[]>>();
            void Collect(GameEntity x)
            {
                if (x == null) return;
                if (_behavioursByEntity.TryGetValue(x, out var list) && list.Count > 0)
                    doomed.Add(new KeyValuePair<GameEntity, Vortex.VortexBehaviour[]>(x, list.ToArray()));
                if (x.Children != null) foreach (var c in x.Children) Collect(c);
            }
            Collect(e);
            // #422: OnDisable first, for every script of the subtree that is active — the colliders and bodies are still there
            foreach (var kv in doomed)
                foreach (var b in kv.Value) CallOnDisable(b);
            if (!ReferenceEquals(FindEntityByHandle(id), e)) return true;   // an OnDisable destroyed it already
            foreach (var kv in doomed)
            {
                foreach (var b in kv.Value)
                {
                    // marked BEFORE its OnDestroy: an OnDestroy that destroys the entity again, a sibling's lookup and the
                    // rest of this tick skip it. It leaves _behaviours between ticks (CompactBehaviours), never under a loop.
                    if (!IsLive(b)) continue;
                    b.Destroyed = true;
                    _scriptOf.Remove(b);
                    _compactPending = true;
                    var prev = _dispatching; _dispatching = b;
                    try { b.OnDestroy(); } catch (Exception ex) { LogScriptError("OnDestroy", b, ex); }
                    finally { _dispatching = prev; }
                    StopAllCoroutines(b);
                    CancelInvokes(b);
                }
                _behavioursByEntity.Remove(kv.Key);   // the list object stays intact for a dispatch in progress
            }
            // an OnDestroy above destroyed this very entity (Scene.Destroy(EntityId)) — that inner call finished the job
            if (!ReferenceEquals(FindEntityByHandle(id), e)) return true;

            try { Editor.Core.Services.Physics.CollisionService.RemoveEntityShapes(e); } catch { }
            try { Editor.Core.Services.Physics.PhysicsService.RemoveEntity(e); } catch { }
            try { Editor.Core.Services.Physics.PhysicsService.RemoveCharactersIn(e); } catch { }   // #407: no capsule left behind
            try { Editor.Core.Services.AudioPlaybackService.Instance.RemoveEntity(e); } catch { }   // its sounds stop with it
            try { e.SyncEngineStateRecursive(false); } catch { }

            // Detach from the live tree. Authored entities are LEDGERED and restored on play end;
            // runtime-spawned ones are gone for good.
            if (_spawned.Remove(e))
            {
                if (e.Parent != null) e.Parent.Children.Remove(e); else _currentScene.Entities.Remove(e);
            }
            else
            {
                var rec = new RemovedRec { Entity = e, Parent = e.Parent };
                if (e.Parent != null) { rec.Index = e.Parent.Children.IndexOf(e); e.Parent.Children.Remove(e); }
                else { rec.Index = _currentScene.Entities.IndexOf(e); _currentScene.Entities.Remove(e); }
                _removed.Add(rec);
            }
            // its own render resources go now (#358); a ledgered entity gets them back on its next submit after Stop
            try { Editor.Core.Services.SceneRenderService.Instance.RemoveEntityTree(e); } catch { }
            try { Editor.Core.Services.AI.BehaviorTreeService.RemoveEntity(e); } catch { }   // #111: its tree stops with it

            // Release the subtree's script handles — a stale id must resolve to nothing, not a ghost.
            void Release(GameEntity x)
            {
                if (x == null) return;
                if (_handlesByEntity.TryGetValue(x, out var h)) { _handlesByEntity.Remove(x); _entitiesById.Remove(h); }
                if (x.Children != null) foreach (var c in x.Children) Release(c);
            }
            Release(e);

            Editor.Core.Services.SceneRenderService.RuntimeDirty = Editor.Core.Services.SceneRenderService.StaticDirty = true;   // structural (#364 A)
            return true;
        }

        /// <summary>Roll every runtime scene mutation back (spawns out, destroys back in, SetActive
        /// restored) — play mode must never alter the authored scene.</summary>
        private void RollbackRuntimeSceneChanges()
        {
            foreach (var e in _spawned)
            {
                try
                {
                    e.SyncEngineStateRecursive(false);
                    if (e.Parent != null) e.Parent.Children.Remove(e); else _currentScene?.Entities.Remove(e);
                    Editor.Core.Services.SceneRenderService.Instance.RemoveEntityTree(e);   // spawned-and-kept: free it (#358)
                }
                catch { }
            }
            _spawned.Clear();

            for (int i = _removed.Count - 1; i >= 0; i--)
            {
                var r = _removed[i];
                try
                {
                    var list = r.Parent != null ? r.Parent.Children : _currentScene?.Entities;
                    if (list != null && !list.Contains(r.Entity))
                        list.Insert(Math.Min(Math.Max(r.Index, 0), list.Count), r.Entity);
                    r.Entity.SyncEngineState();
                }
                catch { }
            }
            _removed.Clear();

            foreach (var kv in _activeChanged)
            {
                // the setter re-registers the entity with the engine — only for one still in the scene: a runtime spawn a
                // script switched off is gone by now (removed above, or destroyed), and re-registering it put a native
                // entity into the engine outside any scene, one leak per such spawn and play session
                try { if (InLiveHierarchy(kv.Key)) kv.Key.IsActive = kv.Value; else kv.Key.SetActiveFlagOnly(kv.Value); } catch { }
            }
            _activeChanged.Clear();

            Editor.Core.Services.SceneRenderService.RuntimeDirty = Editor.Core.Services.SceneRenderService.StaticDirty = true;   // structural (#364 A)
        }

        /// <summary>True when the entity hangs in its scene: every level of it is in its parent's or the scene's list.</summary>
        private static bool InLiveHierarchy(GameEntity e)
        {
            for (var x = e; x != null; x = x.Parent)
            {
                var list = x.Parent != null ? x.Parent.Children : x.Scene?.Entities;
                if (list == null || !list.Contains(x)) return false;
            }
            return true;
        }

        /// <summary>Last compile diagnostics (empty on success) — surfaced to the user.</summary>
        public string LastBuildLog { get; private set; } = "";

        /// <summary>Compile the project's scripts and start every attached behaviour.</summary>
        /// <summary>When set (by the standalone player), gameplay runs from this PRE-COMPILED assembly
        /// instead of compiling source at startup — fast boot + no .cs source shipped with the game.</summary>
        public Assembly PrecompiledAssembly { get; set; }

        /// <summary>The script assembly the running behaviours come from (null outside play) — diagnostics/tests.</summary>
        public Assembly ScriptAssembly => _scriptAsm;

        public void Begin(Scene scene) { Begin(scene, null); }

        /// <summary>Start a PLAY SESSION on <paramref name="scene"/> — the editor's Play, the player's boot: what a freshly
        /// launched game starts with is reset first (the save cache and slot are read from disk again, #452), then the
        /// scripts begin. A scene switch or a hot reload inside the session calls <see cref="Begin(Scene)"/> and keeps it.</summary>
        public void BeginPlaySession(Scene scene) { BeginPlaySession(scene, null); }

        public void BeginPlaySession(Scene scene, System.Reflection.Assembly overrideAsm)
        {
            try { Vortex.Save.BeginSession(); } catch { }   // forgets the last session's cache — the file on disk is the truth
            ResetNonFiniteWarnings();                       // a NaN written again in this session is reported again (#455)
            _duplicateWarned.Clear();
            // a session starts at time scale 1 with the project's fixed step (#425, #481); a scene load inside it keeps both
            int rate = 0;
            try { rate = Editor.Core.Data.ProjectData.Current?.Settings?.FixedUpdateRate ?? 0; } catch { }
            Editor.Core.Services.GameTime.ResetSession(Editor.Core.Services.GameTime.FixedDeltaTimeForRate(rate));
            try { Editor.Core.Services.AudioPlaybackService.Instance.SetPitchFollowsTimeScale(false); } catch { }   // #425: off until a script turns it on
            Begin(scene, overrideAsm);
        }

        internal static void ResetNonFiniteWarnings()
        {
            Editor.ECS.Components.Transform.NonFiniteWarningEpoch++;
            _nonFiniteShown = _nonFiniteSuppressed = 0;
        }

        // ---- non-finite value warnings (#455): one per entity and property, and at most NonFiniteWarningCap per play
        // session — a spawner producing a NaN per spawn must not flood the Console; End() reports the rest as one line ----
        internal const int NonFiniteWarningCap = 8;
        private static int _nonFiniteShown, _nonFiniteSuppressed;

        /// <summary>May one more non-finite warning be shown this session? False counts it for End's "…and N more" line —
        /// the caller then skips building the message (and its stack walk).</summary>
        internal static bool TakeNonFiniteWarning()
        {
            if (_nonFiniteShown < NonFiniteWarningCap) { _nonFiniteShown++; return true; }
            _nonFiniteSuppressed++;
            return false;
        }

        private static void ReportSuppressedNonFinite()
        {
            if (_nonFiniteSuppressed <= 0) return;
            int n = _nonFiniteSuppressed;
            _nonFiniteSuppressed = 0;
            try
            {
                Editor.Core.Services.ConsoleService.Instance.LogWarning("…and " + n + " more non-finite (NaN / infinite) value" + (n == 1 ? " was" : "s were")
                    + " ignored — the first " + NonFiniteWarningCap + " of this play session are listed above.");
            }
            catch { }
        }

        /// <summary>"PlayerController.Update" — the innermost frame of the running game scripts on the current stack, or
        /// null when the engine, a tool or the inspector is the caller. Lets a warning about a bad value name the script
        /// that produced it (#455). Walks the stack: for rare warnings only.</summary>
        internal static string ScriptFrameOfCaller()
        {
            var asm = _instance?._scriptAsm;
            if (asm == null) return null;
            try
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                for (int i = 0; i < st.FrameCount; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    var t = m?.DeclaringType;
                    if (t == null || t.Assembly != asm) continue;
                    string name = m.Name;
                    // a coroutine or lambda body sits in a compiler-made nested type ("<Scare>d__3") or method ("<Update>b__0")
                    for (; t.DeclaringType != null && t.Name.StartsWith("<", StringComparison.Ordinal); t = t.DeclaringType)
                        if (t.Name.IndexOf('>') > 1) name = t.Name.Substring(1, t.Name.IndexOf('>') - 1);
                    if (name.StartsWith("<", StringComparison.Ordinal) && name.IndexOf('>') > 1) name = name.Substring(1, name.IndexOf('>') - 1);
                    return t.Name + "." + name;
                }
            }
            catch { }
            return null;
        }

        /// <summary>(Re)start the scripts on <paramref name="scene"/>. <paramref name="overrideAsm"/> lets the dev
        /// hot-reload inject a freshly-compiled assembly instead of re-reading disk.</summary>
        public void Begin(Scene scene, System.Reflection.Assembly overrideAsm)
        {
            End();
            try { Editor.Core.Services.Areas.AreaService.Reset(); Vortex.Areas.ClearHandlers(); } catch { }
            if (scene?.Entities == null) return;
            _currentScene = scene;   // remembered so hot-reload can re-begin the same scene
            try { var srs = Editor.Core.Services.SceneRenderService.Instance; Editor.Core.Services.Areas.AreaService.Refresh(scene, e => srs?.WorldMatrixOf(e)); } catch { }   // Areas.Count is right in Start

            Vortex.VortexBehaviour.Host = this;
            Vortex.Input.Host = this;
            Vortex.UI.Host = this;
            Vortex.Scene.Host = this;
            Vortex.Cursor.Host = this;
            Vortex.Application.Host = this;
            Vortex.Camera.Host = this;
            Vortex.Physics.Host = this;
            Vortex.Animation.Host = this;
            Vortex.CameraFX.Host = this;
            Vortex.Assets.Host = this;

            // Scripted scene-atmosphere state starts clean each run (a previous run's ambient/fog/sky must not leak).
            Editor.Core.Services.SceneRenderService.ScriptAmbientOverride = null;
            Editor.Core.Services.SceneRenderService.ScriptSky = null;
            // The authored per-scene environment (fog + post-FX) is the baseline every run starts from —
            // the End() above wiped ALL post-FX, including what ActivateEntities just applied on boot.
            // Scripts may override from Start() onwards.
            try { scene.Settings.Apply(); } catch { }

            // Fresh Animator playback states for this run; animation-event markers route to OnAnimationEvent.
            Editor.Core.Animation.AnimationService.Instance.ResetStates();
            Editor.Core.Animation.BoneSocketService.Instance.ResetRuntime();
            Editor.Core.Services.CameraFXService.Instance.Reset();
            if (!_animEventsHooked)
            {
                Editor.Core.Animation.AnimationService.Instance.AnimationEvent += OnAnimationServiceEvent;
                _animEventsHooked = true;
            }
            if (Editor.Core.Services.Physics.CollisionService.MeshTriangleProvider == null)
                Editor.Core.Services.Physics.CollisionService.MeshTriangleProvider = ResolveMeshTriangles;

            // Socket prefabs: spawn + attach the weapon(s) referenced by any BoneAttachment sockets BEFORE the
            // behaviour pass, so their scripts (and collision) come alive with the rest of the scene.
            try { ExpandSocketPrefabs(scene); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[socket] expand failed: " + ex); }

            try { Editor.Core.Services.Physics.CollisionService.Build(scene); } catch { } // build the collision world for this scene
            // Physics v2 (#100): rigid bodies for every collider/Rigidbody, built AFTER the static world so dynamic
            // props leave it again; contacts come back through OnPhysicsContact -> the usual script callbacks.
            Editor.Core.Services.Physics.PhysicsService.ContactHandler = OnPhysicsContact;
            try { Editor.Core.Services.Physics.PhysicsService.Build(scene); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] physics build failed: " + ex.Message); }
            _entitiesById.Clear();
            _scriptOf.Clear();
            _behavioursByEntity.Clear();
            _nextHandle = 0;
            // the previous run's particles go NOW, before any Start() spawns new ones (#347)
            try { Editor.Core.Services.Particles.ParticleService.BeginPlay(scene); } catch { }

            string log = null;
            Assembly asm = overrideAsm ?? PrecompiledAssembly;
            if (asm != null) log = overrideAsm != null ? "Hot-reloaded gameplay assembly" : ("Using precompiled gameplay assembly: " + asm.GetName().Name);
            else asm = Compile(out log);
            LastBuildLog = log ?? "";
            _lastScriptWrite = LatestScriptWrite();   // baseline for hot-reload change detection
            if (!string.IsNullOrEmpty(LastBuildLog))
                System.Diagnostics.Debug.WriteLine("[ScriptRuntime] build:\n" + LastBuildLog);
            // Surface a compile FAILURE in the editor Console so the user sees WHY their scripts didn't run.
            if (asm == null && !string.IsNullOrEmpty(LastBuildLog))
                Editor.Core.Services.ConsoleService.Instance.LogError("Script build failed:\n" + LastBuildLog);
            _scriptAsm = asm;
            // AI & Navigation (#108): the navmesh + crowd, the perception and the behaviour trees start with the worlds
            // they query — AFTER the entity tables were reset (their agent handles live in _entitiesById) and with the
            // compiled script assembly in place (behaviour-tree tasks of the project resolve from it), BEFORE Start().
            try { Editor.Core.Services.AI.AiRuntime.Begin(scene); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] AI begin failed: " + ex.Message); }
            if (asm == null) { _active = true; return; } // no scripts / compile failed -> nothing to run

            foreach (var e in scene.Entities) InstantiateRecursive(e, asm);
            ReportMultiScriptEntities();

            _active = true;
            // A snapshot: a Start() that calls Scene.Instantiate appends new behaviours which are ALREADY Start()ed by
            // InstantiatePrefabAt, and one that destroys an entity (or quits play) must not shift the walk — the old index
            // loop then re-read _behaviours[i] past the end in its catch and the exception escaped Begin.
            // #422: OnEnable first, for every script whose entity is active (the physics world is built: bodies exist), then
            // Start for every script — under an inactive entity too, as before; its OnEnable comes when it is switched on.
            // One destroyed by an earlier callback gets neither (no Start after its OnDestroy); a Start() that stopped play
            // (Application.Quit in the editor) ends the walk.
            EnableAndStart(_behaviours.ToArray());
            CompactBehaviours();
        }

        /// <summary>Several scripts on one entity all run now (#408) — say so once per play start, and warn about two Script
        /// components of the same class: older editors could add a duplicate (a hierarchy drop only checked the first
        /// script), which used to sit there silently and now runs twice.</summary>
        private void ReportMultiScriptEntities()
        {
            foreach (var kv in _behavioursByEntity)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                var names = new string[list.Count];
                for (int i = 0; i < list.Count; i++) names[i] = list[i].GetType().Name;
                try
                {
                    Editor.Core.Services.ConsoleService.Instance.Log("Scripts: '" + kv.Key.Name + "' runs " + list.Count + " scripts — " + string.Join(", ", names));
                    var twice = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key + " ×" + g.Count()).ToArray();
                    if (twice.Length > 0 && _duplicateWarned.Add(kv.Key))
                        Editor.Core.Services.ConsoleService.Instance.LogWarning("Scripts: '" + kv.Key.Name + "' carries the same script more than once ("
                            + string.Join(", ", twice) + ") and every copy runs — remove the extra Script component in the Inspector unless that is intended.");
                }
                catch { }
            }
        }

        private readonly HashSet<GameEntity> _duplicateWarned = new HashSet<GameEntity>();   // per play session (BeginPlaySession)

        /// <summary>Report a script exception to BOTH the VS debug output and the editor's Console panel (so the user
        /// sees which script threw, in which phase, and why — the game's errors surface where the game runs).</summary>
        private static string _lastErrKey;
        private static DateTime _lastErrTime;
        private static void LogScriptError(string phase, object behaviour, Exception ex)
            => LogScriptError(behaviour?.GetType().Name ?? "Script", phase, ex);

        private static void LogScriptError(string who, string phase, Exception ex)
        {
            var inner = ex?.InnerException ?? ex;
            var msg = who + "." + phase + "(): " + (inner?.Message ?? "error");
            // the first frame of the script's own code (file and line) — a trigger script that throws is otherwise a
            // message without a place (#322)
            try
            {
                var st = new System.Diagnostics.StackTrace(inner, true);
                for (int i = 0; i < st.FrameCount; i++)
                {
                    var f = st.GetFrame(i);
                    var file = f?.GetFileName();
                    if (string.IsNullOrEmpty(file)) continue;
                    msg += " — " + System.IO.Path.GetFileName(file) + ":" + f.GetFileLineNumber();
                    break;
                }
            }
            catch { }
            // Throttle a per-frame-repeating exception (a script throwing every Update) so it can't flood the Console
            // ~60×/sec and freeze the UI. The Debug.WriteLine sits BEHIND the throttle too: with a VS debugger
            // attached every write is a ~1ms cross-process round-trip — unthrottled it alone tanked F5 FPS.
            var now = DateTime.UtcNow;
            if (msg == _lastErrKey && (now - _lastErrTime).TotalSeconds < 1.0) return;
            _lastErrKey = msg; _lastErrTime = now;
            System.Diagnostics.Debug.WriteLine("[ScriptRuntime] " + msg);
            try { Editor.Core.Services.ConsoleService.Instance.LogError(msg); } catch { }
        }

        /// <summary>Tick every running behaviour. <paramref name="realDt"/> is the host's frame time in real seconds; the game
        /// runs on it scaled by the time scale (#425): Update / LateUpdate, the AI, the physics accumulator (and with it
        /// FixedUpdate, #481), coroutines and timers, camera effects and the animators get game seconds — the UI action classes
        /// and the debug draw clock keep real ones.</summary>
        public void Update(float realDt)
        {
            // a new input tick: key edges (GetKeyDown / GetKeyUp) and the sub-frame press latch roll over (#337)
            _keyTick++;
            Editor.Core.Input.HostInput.BeginTick();
            if (!_active) return;
            float dt = Editor.Core.Services.GameTime.BeginTick(realDt);   // game seconds (#425)
            realDt = Editor.Core.Services.GameTime.UnscaledDeltaTime;     // sanitised (negative / NaN = 0)
            Vortex.Time.DeltaTime = dt;
            Vortex.Input.PollGamepad();   // refresh controller state once per tick (scripts read Input.LeftStickX etc.)
            CompactBehaviours();          // drop what was destroyed since the last tick — never inside the loops below
            ApplyPendingEnableChanges();  // Script components switched on / off in the Inspector or by a tool (#422)
            if (_startPending.Count > 0)  // scripts attached while paused start before their first Update (#423)
            {
                var starting = _startPending.ToArray();
                _startPending.Clear();
                EnableAndStart(starting);   // one destroyed before it ever ran gets neither (#422: OnEnable before Start)
            }
            for (int i = 0; i < _behaviours.Count; i++)
            {
                var b = _behaviours[i];
                if (!IsLive(b) || b.ScriptDisabled) continue;   // destroyed earlier this tick, or switched off (#422)
                var prev = _dispatching; _dispatching = b;
                try { b.Update(dt); }
                catch (Exception ex) { LogScriptError("Update", b, ex); }
                finally { _dispatching = prev; }
            }
            // Auto-instantiated UI action controllers tick too, so a screen's class can drive its own widgets — in real time:
            // a pause menu (time scale 0) keeps animating (#425).
            foreach (var b in _uiActions.Values)
            {
                if (b == null || b.ScriptDisabled) continue;
                try { b.Update(realDt); }
                catch (Exception ex) { LogScriptError("UI Update", b, ex); }
            }

            // Rigid-body physics (#100) steps AFTER the behaviours' Update (forces/impulses/kinematic moves of this
            // frame are in) and BEFORE LateUpdate/animation, so a viewmodel or camera in LateUpdate reads final poses.
            // AI agents + perception tick after the behaviours' Update (destinations of this frame are in) and before the
            // physics step, so a NavAgent's move of this frame is what the physics world and LateUpdate see (#108).
            try { Editor.Core.Services.AI.AiRuntime.Tick(dt); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] AI tick error: " + ex.Message); }
            try { TickAreas(dt); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] areas tick error: " + ex.Message); }
            try { Editor.Core.Services.Physics.PhysicsService.Step(dt); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] physics step error: " + ex.Message); }

            // LateUpdate pass — runs AFTER every behaviour's Update() this frame. A first-person VIEWMODEL that
            // follows the camera MUST position itself here, not in Update: otherwise, depending on script
            // registration order, it can read last-frame's camera state and lag a frame behind → visible jitter
            // (worst at uncapped FPS where dt varies wildly). Here the camera script has always run first.
            for (int i = 0; i < _behaviours.Count; i++)
            {
                var b = _behaviours[i];
                if (!IsLive(b) || b.ScriptDisabled) continue;
                var prev = _dispatching; _dispatching = b;
                try { b.LateUpdate(dt); }
                catch (Exception ex) { LogScriptError("LateUpdate", b, ex); }
                finally { _dispatching = prev; }
            }
            foreach (var b in _uiActions.Values)
            {
                if (b == null || b.ScriptDisabled) continue;
                try { b.LateUpdate(realDt); }
                catch (Exception ex) { LogScriptError("UI LateUpdate", b, ex); }
            }

            // Coroutines + Invoke timers (#37) advance after behaviours, so a coroutine resumed this
            // frame sees the world the behaviours just built (and PlayAnimation from it applies below).
            TickCoroutinesAndInvokes(dt, realDt);

            // Debug draw + dev console (#42): re-submit live wire shapes, then the console overlay on top. A shape's duration
            // is real time — a debug line stays readable while the game is paused.
            SubmitDebugShapes(realDt);
            try { Editor.Core.Services.Physics.PhysicsService.SubmitDebugDraw(); } catch { }   // View ▸ Physics Debug (#106)
            try { Editor.Core.Services.AI.AiRuntime.SubmitDebugDraw(); } catch { }           // navmesh / agent paths / patrol routes / vision cones
            RenderDebugConsole();

            // play stopped during this tick: no pose may land in the restored edit scene (#410)
            if (!_active) return;

            // Skeletal animation: advance every Animator AFTER behaviours ran, so a same-frame
            // PlayAnimation() takes effect immediately. This is the one tick all three play drivers share.
            // Bone sockets apply right after — animation -> sockets -> (submit reads final transforms).
            try
            {
                Editor.Core.Services.CameraFXService.Instance.Step(dt);   // springs/noise BEFORE sockets read them
                Editor.Core.Animation.AnimationService.Instance.Step(_currentScene, dt, realDt);   // Ignore Time Scale animators: real time (#425)
                Editor.Core.Animation.BoneSocketService.Instance.Apply(_currentScene);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] Animation step error: " + ex.Message); }

            // Collision/trigger events fire AFTER everyone moved this tick, so overlaps are tested at final positions.
            DispatchCollisionEvents();
        }

        // ---- FixedUpdate (#481): the behaviours whose class overrides it, in _behaviours order. PhysicsService calls
        // RunFixedUpdate before each fixed step; while the list is empty its hook stays null — no cost per step.
        private readonly List<Vortex.VortexBehaviour> _fixedBehaviours = new List<Vortex.VortexBehaviour>();
        private static readonly Dictionary<Type, bool> _overridesFixedUpdate = new Dictionary<Type, bool>();
        private Action<float> _fixedHook;

        /// <summary>True when the behaviour class (or a base of it) overrides VortexBehaviour.FixedUpdate.</summary>
        internal static bool OverridesFixedUpdate(Type t)
        {
            if (t == null) return false;
            lock (_overridesFixedUpdate)
            {
                if (_overridesFixedUpdate.TryGetValue(t, out bool yes)) return yes;
                var m = t.GetMethod("FixedUpdate", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float) }, null);
                yes = m != null && m.DeclaringType != typeof(Vortex.VortexBehaviour);
                _overridesFixedUpdate[t] = yes;
                return yes;
            }
        }

        private void TrackFixedUpdate(Vortex.VortexBehaviour b)
        {
            if (b == null || !OverridesFixedUpdate(b.GetType())) return;
            _fixedBehaviours.Add(b);
            Editor.Core.Services.Physics.PhysicsService.BeforeStep = _fixedHook ??= RunFixedUpdate;
        }

        /// <summary>One fixed step's FixedUpdate on every live behaviour that has one (#481): Time.DeltaTime reads the step
        /// and Time.InFixedStep is true meanwhile. A behaviour spawned by a FixedUpdate runs from the next step on.</summary>
        private void RunFixedUpdate(float step)
        {
            if (!_active) return;
            Editor.Core.Services.GameTime.InFixedStep = true;
            try
            {
                int n = _fixedBehaviours.Count;
                for (int i = 0; i < n && i < _fixedBehaviours.Count; i++)
                {
                    var b = _fixedBehaviours[i];
                    if (!IsLive(b) || b.ScriptDisabled) continue;   // switched off (#422): no FixedUpdate
                    var prev = _dispatching; _dispatching = b;   // its character moves are its entity's (#406)
                    try { b.FixedUpdate(step); }
                    catch (Exception ex) { LogScriptError("FixedUpdate", b, ex); }
                    finally { _dispatching = prev; }
                    if (!_active) break;   // a FixedUpdate stopped play
                }
            }
            finally { Editor.Core.Services.GameTime.InFixedStep = false; }
        }

        private bool _animEventsHooked;

        /// <summary>Route an AnimEvent marker (footstep, attack hit, ...) to the entity's behaviours (#408: every one).</summary>
        private void OnAnimationServiceEvent(GameEntity entity, string name)
        {
            if (entity == null || !_behavioursByEntity.TryGetValue(entity, out var list)) return;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                if (!IsLive(b)) continue;
                var prev = _dispatching; _dispatching = b;
                try { b.OnAnimationEvent(name); }
                catch (Exception ex) { LogScriptError("OnAnimationEvent", b, ex); }   // #322
                finally { _dispatching = prev; }
            }
        }

        /// <summary>The behaviours whose EntityId is <paramref name="handle"/> — all of an entity's share one (#408). An
        /// entity's other handle (one handed out before its scripts started, e.g. a NavAgent's) resolves to none, exactly
        /// like the per-behaviour handle map this replaced.</summary>
        private List<Vortex.VortexBehaviour> BehavioursWithHandle(long handle)
        {
            if (!_entitiesById.TryGetValue(handle, out var e) || e == null) return null;
            return _behavioursByEntity.TryGetValue(e, out var list) && list.Count > 0 && list[0].EntityId == handle ? list : null;
        }

        private void InvokeAll(List<Vortex.VortexBehaviour> list, EvKind kind, Vortex.TriggerHit hit)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!IsLive(list[i])) continue;
                var prev = _dispatching; _dispatching = list[i];
                try { Invoke(list[i], kind, hit); }
                finally { _dispatching = prev; }
            }
        }

        private enum EvKind { Enter, Stay, Exit, Collision }

        /// <summary>Drain this tick's trigger/collision overlaps from CollisionService and deliver them as
        /// OnTriggerEnter/Stay/Exit / OnCollisionEnter to the relevant behaviours (both the collider owner and the
        /// touching character), so a collider can act as a no-fly zone / "react when touched".</summary>
        private void DispatchCollisionEvents()
        {
            try { Editor.Core.Services.Physics.CollisionService.StepEvents(_evEnter, _evStay, _evExit, _evCollide); }
            catch { return; }
            for (int i = 0; i < _evEnter.Count; i++) DispatchContact(_evEnter[i], EvKind.Enter);
            for (int i = 0; i < _evStay.Count; i++) DispatchContact(_evStay[i], EvKind.Stay);
            for (int i = 0; i < _evExit.Count; i++) DispatchContact(_evExit[i], EvKind.Exit);
            for (int i = 0; i < _evCollide.Count; i++) DispatchContact(_evCollide[i], EvKind.Collision);
        }

        private void DispatchContact(Editor.Core.Services.Physics.CollisionService.Contact c, EvKind kind)
        {
            var owner = c.Other;
            if (owner == null) return;
            _entitiesById.TryGetValue(c.CharacterId, out var charEnt);
            if (charEnt == null) return;                                    // stale/unknown character — don't fire a phantom contact
            if (ReferenceEquals(charEnt, owner)) return;                    // never fire on self

            var charBehs = BehavioursWithHandle(c.CharacterId);
            _behavioursByEntity.TryGetValue(owner, out var ownerBehs);
            if (ownerBehs != null && ownerBehs.Count == 0) ownerBehs = null;
            long ownerId = ownerBehs != null ? ownerBehs[0].EntityId : 0;   // "0 if it has no script" (TriggerHit.EntityId)

            // Fire on the collider OWNER's behaviours (other = the character that touched it) — every one of them (#408).
            if (ownerBehs != null) InvokeAll(ownerBehs, kind, new Vortex.TriggerHit(c.CharacterId, charEnt?.Name, EntityTag(charEnt)));
            // And on the touching CHARACTER's behaviours (other = the collider owner) so either side can react.
            if (charBehs != null) InvokeAll(charBehs, kind, new Vortex.TriggerHit(ownerId, owner.Name, EntityTag(owner)));
        }

        /// <summary>Physics v2 (#100): a contact between two simulated bodies. Delivered through the SAME callbacks
        /// the character contacts use — OnCollisionEnter for a solid first touch, OnTriggerEnter/Stay/Exit for
        /// sensor overlaps — on BOTH entities' behaviours, each seeing the other as the TriggerHit. The player
        /// character is not a physics body, so nothing here duplicates what CollisionService.StepEvents reports.</summary>
        // ---- areas (zones): the Area components tracked against the scripted entities every tick ----
        private void TickAreas(float dt)
        {
            var scene = Editor.Core.Data.ProjectData.Current?.ActiveScene;
            if (scene == null) return;
            var srs = Editor.Core.Services.SceneRenderService.Instance;
            Editor.Core.Services.Areas.AreaService.Dispatch = DispatchArea;
            Editor.Core.Services.Areas.AreaService.Refresh(scene, e => srs?.WorldMatrixOf(e));
            Editor.Core.Services.Areas.AreaService.Tick(dt, _behavioursByEntity.Keys, e =>
            {
                float[] m = null;
                try { m = srs?.WorldMatrixOf(e); } catch { }
                if (m != null && m.Length >= 16) return new System.Numerics.Vector3(m[12], m[13], m[14]);
                if (e.Transform == null) return null;
                var p = e.Transform.LocalPosition; return new System.Numerics.Vector3(p.X, p.Y, p.Z);
            });
        }

        private void DispatchArea(GameEntity e, Editor.Core.Services.Areas.AreaInstance a, int kind, float dt)
        {
            var info = AreaInfoFor(a);
            if (_behavioursByEntity.TryGetValue(e, out var list))
            {
                for (int i = 0; i < list.Count; i++)   // every behaviour of the entity (#408)
                {
                    var b = list[i];
                    if (!IsLive(b)) continue;
                    var prev = _dispatching; _dispatching = b;
                    try
                    {
                        if (kind == 0) b.OnAreaEnter(info); else if (kind == 1) b.OnAreaStay(info, dt); else b.OnAreaExit(info);
                    }
                    catch (Exception ex) { LogScriptError(kind == 0 ? "OnAreaEnter" : (kind == 1 ? "OnAreaStay" : "OnAreaExit"), b, ex); }
                    finally { _dispatching = prev; }
                }
            }
            RaiseAreaEvent(e, kind, info);
        }

        /// <summary>Areas.Entered / Exited for <paramref name="e"/>: the handlers run on its behalf (#406) — one that moves
        /// the entity's character with the id it is handed names the entity.</summary>
        internal void RaiseAreaEvent(GameEntity e, int kind, Vortex.AreaInfo info)
        {
            long id = HandleForEntity(e);
            var ctx = EnterEntityCode(e);
            try { if (kind == 0) Vortex.Areas.RaiseEntered(id, info); else if (kind == 2) Vortex.Areas.RaiseExited(id, info); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] area event handler: " + ex.Message); }
            finally { ExitEntityCode(ctx); }
        }

        private Vortex.AreaInfo AreaInfoFor(Editor.Core.Services.Areas.AreaInstance a)
        {
            var d = a.Definition;
            return new Vortex.AreaInfo(HandleForEntity(a.Entity), string.IsNullOrEmpty(a.Entity?.Name) ? d.Name : a.Entity.Name, d.Tags.ToArray(), d.Priority,
                new Vortex.Vector3(a.WorldPosition.X, a.WorldPosition.Y, a.WorldPosition.Z), new System.Collections.Generic.Dictionary<string, string>(d.Properties, StringComparer.OrdinalIgnoreCase));
        }
        private Vortex.AreaInfo[] ToInfos(System.Collections.Generic.List<Editor.Core.Services.Areas.AreaInstance> list)
        {
            var r = new Vortex.AreaInfo[list.Count];
            for (int i = 0; i < list.Count; i++) r[i] = AreaInfoFor(list[i]);
            return r;
        }
        internal bool AreaIsInside(long entity, string tag)
        {
            if (!_entitiesById.TryGetValue(entity, out var e)) return false;
            foreach (var a in Editor.Core.Services.Areas.AreaService.Of(e)) if (string.IsNullOrEmpty(tag) || a.HasTag(tag)) return true;
            return false;
        }
        internal Vortex.AreaInfo[] AreasOf(long entity)
        {
            if (!_entitiesById.TryGetValue(entity, out var e)) return new Vortex.AreaInfo[0];
            return ToInfos(Editor.Core.Services.Areas.AreaService.Of(e));
        }
        internal Vortex.AreaInfo[] AreasAt(Vortex.Vector3 p) => ToInfos(Editor.Core.Services.Areas.AreaService.At(new System.Numerics.Vector3(p.X, p.Y, p.Z)));
        internal bool AreaPointInside(Vortex.Vector3 p, string tag) => Editor.Core.Services.Areas.AreaService.IsInside(new System.Numerics.Vector3(p.X, p.Y, p.Z), tag);
        internal Vortex.AreaInfo AreaFind(string nameOrTag) { var a = Editor.Core.Services.Areas.AreaService.Find(nameOrTag); return a != null ? AreaInfoFor(a) : null; }
        internal void AreaTrack(long entity, bool on)
        {
            if (!_entitiesById.TryGetValue(entity, out var e)) return;
            if (on) Editor.Core.Services.Areas.AreaService.Track(e); else Editor.Core.Services.Areas.AreaService.Untrack(e);
        }

        private void OnPhysicsContact(Editor.Core.Services.Physics.PhysicsContactEvent c)
        {
            if (!_active || c.EntityA == null || c.EntityB == null) return;
            EvKind kind;
            if (c.IsTrigger)
            {
                switch (c.Kind)
                {
                    case Editor.Core.Services.Physics.PhysicsContactKind.Added: kind = EvKind.Enter; break;
                    case Editor.Core.Services.Physics.PhysicsContactKind.Removed: kind = EvKind.Exit; break;
                    default: kind = EvKind.Stay; break;
                }
            }
            else
            {
                if (c.Kind != Editor.Core.Services.Physics.PhysicsContactKind.Added) return;
                kind = EvKind.Collision;
            }
            // both entities' behaviours, every one of them (#408)
            _behavioursByEntity.TryGetValue(c.EntityA, out var behA);
            _behavioursByEntity.TryGetValue(c.EntityB, out var behB);
            if (behA != null && behA.Count > 0) InvokeAll(behA, kind, new Vortex.TriggerHit(HandleForEntity(c.EntityB), c.EntityB.Name, EntityTag(c.EntityB)));
            if (behB != null && behB.Count > 0) InvokeAll(behB, kind, new Vortex.TriggerHit(HandleForEntity(c.EntityA), c.EntityA.Name, EntityTag(c.EntityA)));
        }

        private static string EntityTag(GameEntity e)
        {
            if (e == null) return "";
            try { var p = e.GetType().GetProperty("Tag"); var v = p?.GetValue(e) as string; return v ?? ""; } catch { return ""; }
        }

        private static void Invoke(Vortex.VortexBehaviour b, EvKind kind, Vortex.TriggerHit hit)
        {
            try
            {
                switch (kind)
                {
                    case EvKind.Enter: b.OnTriggerEnter(hit); break;
                    case EvKind.Stay: b.OnTriggerStay(hit); break;
                    case EvKind.Exit: b.OnTriggerExit(hit); break;
                    case EvKind.Collision: b.OnCollisionEnter(hit); break;
                }
            }
            catch (Exception ex) { LogScriptError(kind == EvKind.Collision ? "OnCollisionEnter" : "OnTrigger" + kind, b, ex); }   // #322
        }

        /// <summary>Invoke the C# method bound to each fired UI button action (the button↔code link). Routing
        /// (the "one class per UI" model): a button on <c>Foo.vui</c> prefers a <c>FooActions</c> class — found
        /// among attached behaviours, the auto-instantiated cache, or freshly spun up from the gameplay assembly
        /// (no scene wiring needed). If no such class exists it falls back to ANY running behaviour with the method,
        /// so hand-written controllers (LobbyController / PlayerController) keep working unchanged.</summary>
        public void InvokeUiActions(System.Collections.Generic.List<Editor.UI.Vui.UiAction> actions)
        {
            if (actions == null || !_active) return;
            foreach (var a in actions)
            {
                if (string.IsNullOrEmpty(a.Action)) continue;
                var target = ResolveActionTarget(a.Screen, a.Action);
                if (target == null)
                {
                    System.Diagnostics.Debug.WriteLine("[UIAction] no handler for '" + a.Action + "' (screen '" + a.Screen + "')");
                    continue;
                }
                var m = GetParamlessMethod(target.GetType(), a.Action);
                var prev = _dispatching; _dispatching = target;   // the action is that behaviour's code (#406)
                try { m.Invoke(target, null); }
                catch (Exception ex) { LogScriptError(target.GetType().Name, a.Action, ex); }   // #322
                finally { _dispatching = prev; }
            }
        }

        /// <summary>Find the behaviour that should handle <paramref name="action"/> fired from screen
        /// <paramref name="screen"/> — see <see cref="InvokeUiActions"/> for the routing order.</summary>
        private Vortex.VortexBehaviour ResolveActionTarget(string screen, string action)
        {
            string cls = ScreenActionClass(screen);   // e.g. "PauseMenu.vui" -> "PauseMenuActions" (or null)
            if (cls != null)
            {
                // 1) the screen's own actions class, attached to a scene entity
                foreach (var b in _behaviours)
                    if (IsLive(b) && string.Equals(b.GetType().Name, cls, StringComparison.Ordinal) && GetParamlessMethod(b.GetType(), action) != null)
                        return b;
                // 2) already auto-instantiated this screen's class
                if (_uiActions.TryGetValue(cls, out var cached) && cached != null && GetParamlessMethod(cached.GetType(), action) != null)
                    return cached;
                // 3) spin it up from the gameplay assembly on first use (no scene wiring needed)
                var inst = TryCreateUiAction(cls);
                if (inst != null && GetParamlessMethod(inst.GetType(), action) != null)
                    return inst;
            }
            // 4) fallback (back-compat): any attached behaviour with the method
            foreach (var b in _behaviours)
                if (IsLive(b) && GetParamlessMethod(b.GetType(), action) != null) return b;
            // 5) fallback: any already-instantiated UI action with the method
            foreach (var b in _uiActions.Values)
                if (b != null && GetParamlessMethod(b.GetType(), action) != null) return b;
            return null;
        }

        /// <summary>Create + cache the screen's actions class if it exists in the gameplay assembly and isn't
        /// already attached to a scene entity (which would double-tick it). A cached null means "none found".</summary>
        private Vortex.VortexBehaviour TryCreateUiAction(string cls)
        {
            if (_uiActions.TryGetValue(cls, out var existing)) return existing; // hit OR remembered miss
            Vortex.VortexBehaviour inst = null;
            try
            {
                bool attached = false;
                foreach (var b in _behaviours)
                    if (IsLive(b) && string.Equals(b.GetType().Name, cls, StringComparison.Ordinal)) { attached = true; break; }
                if (!attached && _scriptAsm != null)
                {
                    var type = FindBehaviourType(_scriptAsm, cls);
                    if (type != null)
                    {
                        inst = (Vortex.VortexBehaviour)Activator.CreateInstance(type);
                        inst.EntityId = 0; // no scene entity -> Position/Rotation read as zero; UI action classes don't use them
                        CallOnEnable(inst);   // #422: OnEnable, then Start (exceptions reach the Console)
                        CallStart(inst);
                    }
                }
            }
            catch (Exception ex) { LogScriptError(cls, "constructor", ex); }
            _uiActions[cls] = inst; // cache the result (incl. null) so we don't rescan the assembly every click
            return inst;
        }

        private static System.Reflection.MethodInfo GetParamlessMethod(Type t, string name)
            => t.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                           null, System.Type.EmptyTypes, null);

        /// <summary>".../PauseMenu.vui" -> "PauseMenuActions" (a valid C# identifier), or null if empty.</summary>
        private static string ScreenActionClass(string screen)
        {
            if (string.IsNullOrEmpty(screen)) return null;
            string name = screen;
            int slash = name.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) name = name.Substring(slash + 1);
            if (name.EndsWith(".vui", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            var sb = new System.Text.StringBuilder();
            foreach (char c in name) if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            if (sb.Length == 0) return null;
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            sb.Append("Actions");
            return sb.ToString();
        }

        /// <summary>Stop all behaviours.</summary>
        public void End()
        {
            try { Editor.Core.Services.Areas.AreaService.Reset(); Vortex.Areas.ClearHandlers(); } catch { }
            // #422: OnDisable for every active script first (the world is still there), then OnDestroy for every script.
            for (int i = 0; i < _behaviours.Count; i++) CallOnDisable(_behaviours[i]);
            foreach (var b in _uiActions.Values) if (b != null) CallOnDisable(b);
            // By index over the live list (an OnDestroy may spawn — appended — or destroy — only marked); each behaviour is
            // marked first, so one destroyed by another's OnDestroy gets exactly one OnDestroy and lookups skip it.
            for (int i = 0; i < _behaviours.Count; i++)
            {
                var b = _behaviours[i];
                if (!IsLive(b)) continue;
                CallOnDisable(b);   // spawned by an OnDisable / OnDestroy of this pass
                b.Destroyed = true;
                var prev = _dispatching; _dispatching = b;
                try { b.OnDestroy(); } catch (Exception ex) { LogScriptError("OnDestroy", b, ex); }   // #322
                finally { _dispatching = prev; }
            }
            foreach (var b in _uiActions.Values) { if (b != null) try { b.OnDestroy(); } catch (Exception ex) { LogScriptError("OnDestroy", b, ex); } }
            UnwatchScripts();
            _activating.Clear();
            _reentryWarned.Clear();
            // Scripting-wave teardown (#36-#40): undo runtime scene mutations, stop timers/coroutines,
            // drop event subscriptions, persist pending save data.
            try { RollbackRuntimeSceneChanges(); } catch { }
            try { Editor.Core.Services.Particles.ParticleService.EndPlay(); } catch { }   // #347
            _triCache.Clear();   // whole-model triangle arrays, one per collider key — rebuilt from the disk cache next play (#358)
            _coroutines.Clear();
            _invokes.Clear();
            _debugShapes.Clear();
            _nonFiniteMoveWarned.Clear();
            ReportSuppressedNonFinite();
            Vortex.Debug.ConsoleVisible = false;
            try { Vortex.Debug.ClearLines(); } catch { }
            try { Vortex.Events.Clear(); } catch { }
            try { Vortex.Scene.ResetHooks(); } catch { }
            try { Vortex.Save.Flush(); } catch { }
            _uiActions.Clear();
            _behaviours.Clear();
            _fixedBehaviours.Clear();
            lock (_overridesFixedUpdate) _overridesFixedUpdate.Clear();   // holds script types: a hot reload's old assembly must unload
            Editor.Core.Services.Physics.PhysicsService.BeforeStep = null;   // #481
            Editor.Core.Services.GameTime.InFixedStep = false;
            _compactPending = false;
            _startPending.Clear();
            _entitiesById.Clear();
            _handlesByEntity.Clear();
            _scriptOf.Clear();
            _behavioursByEntity.Clear();
            try { Editor.Core.Services.Physics.CollisionService.ResetEvents(); Editor.Core.Services.Physics.CollisionService.ClearCharacters(); } catch { }
            _dispatching = null; _dispatchingEntity = null;
            try { Editor.Core.Services.Physics.PhysicsService.Clear(); Editor.Core.Services.Physics.PhysicsService.ContactHandler = null; } catch { }
            try { Editor.Core.Services.AI.AiRuntime.End(); } catch { }
            try { Editor.Core.Animation.AnimationService.Instance.ResetStates(); } catch { }
            try { Editor.Core.Animation.BoneSocketService.Instance.ResetRuntime(); } catch { }
            try { Editor.Core.Services.CameraFXService.Instance.Reset(); } catch { }
            // Return the scene atmosphere to editor defaults: scripted ambient stops overriding and any
            // scripted fog is switched off — otherwise the horror scene's darkness/fog sticks to the
            // EDITOR viewport after leaving play mode (the fog CB is persistent frame state).
            Editor.Core.Services.SceneRenderService.ScriptAmbientOverride = null;
            Editor.Core.Services.SceneRenderService.ScriptSky = null;   // #349
            try { Editor.DllWrapper.VortexAPI.SetFog(0f, 0f, 0f, 0f, 0f, 0f); } catch { }
            try { Editor.DllWrapper.VortexAPI.RenderDistance(0f); Editor.DllWrapper.VortexAPI.GeometricLod(true, 40f, 120f); } catch { }   // #360 defaults
            // Post-FX is persistent renderer state too — scripted grain/vignette must not stick to the
            // editor viewport after play. The scene's AUTHORED environment then re-applies on top.
            try { Vortex.PostFx.ClearAll(); } catch { }
            try { _currentScene?.Settings?.Apply(); } catch { }
            _scriptAsm = null;
            _active = false;
        }

        private Scene _currentScene;                       // last Begin's scene (for hot-reload)
        private DateTime _lastScriptWrite = DateTime.MinValue;

        /// <summary>Dev script HOT-RELOAD: if any script changed on disk, recompile and — only if it compiles — re-run
        /// the scripts on the current scene with fresh state. A compile error keeps the running scripts + logs it, so
        /// a typo never kills the game. No-op for a shipped game (scripts ship precompiled, not on disk) or if nothing
        /// changed. Returns true when a reload actually happened. Wired to game-window re-focus (alt-tab back).</summary>
        public enum ReloadOutcome { Unchanged, Reloaded, CompileError }
        /// <summary>Result of the last ReloadScripts() call (for the on-screen hot-reload overlay).</summary>
        public ReloadOutcome LastReloadOutcome { get; private set; } = ReloadOutcome.Unchanged;
        /// <summary>First compiler error line when LastReloadOutcome == CompileError (shown in the overlay).</summary>
        public string LastReloadError { get; private set; }

        /// <summary>Cheap (no-compile) check: has any script been saved since the last (re)build? Used so the
        /// hot-reload overlay only appears when there's a REAL change to apply.</summary>
        public bool ScriptsChanged()
        {
            if (PrecompiledAssembly != null || _currentScene == null) return false;
            return LatestScriptWrite() > _lastScriptWrite;
        }

        public bool ReloadScripts()
        {
            LastReloadOutcome = ReloadOutcome.Unchanged;
            if (PrecompiledAssembly != null || _currentScene == null) return false;
            DateTime now = LatestScriptWrite();
            if (now <= _lastScriptWrite) return false;      // nothing changed since the last build -> no hitch
            var asm = Compile(out string log);
            LastBuildLog = log ?? "";
            if (asm == null)
            {
                _lastScriptWrite = now; // don't retry the same broken source every focus; wait for the next save
                LastReloadOutcome = ReloadOutcome.CompileError;
                LastReloadError = FirstErrorLine(log);
                System.Diagnostics.Debug.WriteLine("[hot-reload] compile FAILED — keeping running scripts:\n" + log);
                return false;
            }
            LastReloadSummary = NewestScriptName();
            LastReloadOutcome = ReloadOutcome.Reloaded;
            Begin(_currentScene, asm);   // tear down + re-instantiate + Start with the new code (also resets _lastScriptWrite)
            System.Diagnostics.Debug.WriteLine("[hot-reload] scripts reloaded from disk");
            return true;
        }

        private static string FirstErrorLine(string log)
        {
            if (string.IsNullOrEmpty(log)) return "compile error";
            foreach (var line in log.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0 && !t.StartsWith("Script compile", StringComparison.Ordinal))
                    return t.Length > 90 ? t.Substring(0, 90) + "…" : t;
            }
            return "compile error";
        }

        /// <summary>Short description of the last successful hot-reload (the freshly-saved script) for the on-screen overlay.</summary>
        public string LastReloadSummary { get; private set; } = "";

        private static string NewestScriptName()
        {
            try
            {
                string dir = ScriptingService.ScriptsDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "scripts";
                string best = "scripts"; DateTime max = DateTime.MinValue;
                foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > max) { max = t; best = Path.GetFileName(f); }
                }
                return best;
            }
            catch { return "scripts"; }
        }

        private static DateTime LatestScriptWrite()
        {
            try
            {
                string dir = ScriptingService.ScriptsDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return DateTime.MinValue;
                DateTime max = DateTime.MinValue;
                foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > max) max = t;
                }
                return max;
            }
            catch { return DateTime.MinValue; }
        }

        private void InstantiateRecursive(GameEntity e, Assembly asm)
        {
            if (e == null) return;
            InstantiateOne(e, asm);
            if (e.Children != null)
                foreach (var c in e.Children) InstantiateRecursive(c, asm);
        }

        /// <summary>The live behaviours of an entity re-read their Script components' field overrides (#345): what
        /// attach_script / the inspector set while playing reaches the running instances — each one its OWN Script's
        /// values (#408). False when the entity has no running behaviour.</summary>
        public bool ApplyFieldsToLiveBehaviour(GameEntity e)
        {
            if (e == null || !_behavioursByEntity.TryGetValue(e, out var list) || list.Count == 0) return false;
            foreach (var b in list)
                if (IsLive(b) && _scriptOf.TryGetValue(b, out var sc)) ApplySerializedFields(b, sc);
            return true;
        }

        /// <summary>The behaviour made from <paramref name="script"/> re-reads that component's field overrides (#345) —
        /// never another Script's behaviour on the same entity (#408). False when this Script has no running behaviour.</summary>
        public bool ApplyFieldsToLiveBehaviour(GameEntity e, Script script)
        {
            var b = LiveBehaviourOf(e, script);
            if (b == null) return false;
            ApplySerializedFields(b, script);
            return true;
        }

        /// <summary>The running behaviour made from this Script component of <paramref name="e"/>, or null (not playing,
        /// disabled, the class does not compile, destroyed). Each Script component of an entity runs its own behaviour
        /// (#408). When the component was REPLACED while playing (attach_script after an undo, a prefab revert), the
        /// behaviour of the same class whose own component is gone keeps running — it is linked to this one instead of
        /// a second copy being started.</summary>
        public Vortex.VortexBehaviour LiveBehaviourOf(GameEntity e, Script script)
        {
            if (e == null || script == null || !_behavioursByEntity.TryGetValue(e, out var list)) return null;
            foreach (var b in list)
                if (IsLive(b) && _scriptOf.TryGetValue(b, out var sc) && ReferenceEquals(sc, script)) return b;
            if (!script.IsEnabled) return null;
            foreach (var b in list)
            {
                if (!IsLive(b) || !_scriptOf.TryGetValue(b, out var sc) || e.Components.Contains(sc)) continue;
                if (!string.Equals(b.GetType().Name, script.ScriptClassName, StringComparison.Ordinal)) continue;
                _scriptOf[b] = script;   // re-linked: its fields now come from the new component
                return b;
            }
            return null;
        }

        /// <summary>Scripts added to an entity WHILE playing come alive now (#345): every enabled Script component without
        /// a running behaviour is instantiated from the running assembly and started, the way a spawned entity's scripts
        /// are — next to the behaviours the entity already runs (#408). False when nothing new could start (not playing,
        /// no assembly, every Script already runs or is disabled, or the class does not compile).</summary>
        public bool AttachAtRuntime(GameEntity e) => AttachAtRuntime(e, null);

        /// <summary>As <see cref="AttachAtRuntime(GameEntity)"/> for one Script component of the entity (null = every
        /// one): true when it was started now — while paused, it starts on the next tick that plays (#423).</summary>
        public bool AttachAtRuntime(GameEntity e, Script script)
        {
            if (e == null || _scriptAsm == null) return false;
            var started = new List<Vortex.VortexBehaviour>();
            foreach (var sc in e.GetComponents<Script>())
            {
                if (script != null && !ReferenceEquals(sc, script)) continue;
                WatchScript(sc);                                 // its enable switch counts from now on (#422)
                if (LiveBehaviourOf(e, sc) != null) continue;   // already running — its fields go through ApplyFieldsToLiveBehaviour
                var b = InstantiateScript(e, sc, _scriptAsm);
                if (b != null) started.Add(b);
            }
            // paused: nothing of the game may run now — OnEnable and Start wait for the next tick that plays (#423)
            bool paused = Editor.Core.Services.PlayModeService.Instance.State == Editor.Core.Services.PlayState.Paused;
            if (paused) _startPending.AddRange(started);
            else EnableAndStart(started);   // one an earlier Start() destroyed gets neither
            return started.Count > 0;
        }

        private readonly List<Vortex.VortexBehaviour> _startPending = new List<Vortex.VortexBehaviour>();

        /// <summary>Every enabled Script component of the entity gets its own behaviour (#408) — the runtime used to take only
        /// the first one (GetComponent) and silently drop the rest. A switched-off one gets its behaviour when it is switched
        /// on (#422), so every Script component is watched.</summary>
        private void InstantiateOne(GameEntity e, Assembly asm)
        {
            foreach (var script in e.GetComponents<Script>())   // a copy: a constructor can't upset the iteration
            {
                WatchScript(script);
                InstantiateScript(e, script, asm);
            }
        }

        // ---- the Script component's enable switch while playing (#422) ----
        // The Inspector's checkbox, set_component_properties and a script's own Enabled all end in Script.IsEnabled. A change
        // that does not come through Enabled (another thread, the UI) is queued and applied at the start of the next tick.

        private readonly HashSet<Script> _watched = new HashSet<Script>(ReferenceEqualityComparer.Instance);
        private readonly List<Script> _enableQueue = new List<Script>();

        private void WatchScript(Script sc)
        {
            if (sc == null || !_watched.Add(sc)) return;
            sc.PropertyChanged += OnScriptPropertyChanged;
        }

        private void UnwatchScripts()
        {
            foreach (var sc in _watched) sc.PropertyChanged -= OnScriptPropertyChanged;
            _watched.Clear();
            lock (_enableQueue) _enableQueue.Clear();
        }

        private void OnScriptPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Script.IsEnabled) || !(sender is Script sc)) return;
            lock (_enableQueue) if (!_enableQueue.Contains(sc)) _enableQueue.Add(sc);
        }

        private void ApplyPendingEnableChanges()
        {
            Script[] pending;
            lock (_enableQueue)
            {
                if (_enableQueue.Count == 0) return;
                pending = _enableQueue.ToArray();
                _enableQueue.Clear();
            }
            foreach (var sc in pending) ApplyScriptEnabled(sc);
        }

        /// <summary>Bring the behaviour of <paramref name="sc"/> in line with the component's switch: on — OnEnable (when its
        /// entity is active), and for a script that never ran its behaviour is made and started now (paused: on the next tick
        /// that plays, #423); off — OnDisable, no more Update / LateUpdate / FixedUpdate. Nothing happens when it already
        /// matches, so a queued change that a script's Enabled applied already is a no-op.</summary>
        private void ApplyScriptEnabled(Script sc)
        {
            var e = sc?.Entity;
            if (e == null || !_active || !e.Components.Contains(sc)) return;
            var b = LiveBehaviourOf(e, sc);
            if (sc.IsEnabled)
            {
                if (b == null) { AttachAtRuntime(e, sc); return; }   // switched on for the first time: made, OnEnable, Start
                b.ScriptDisabled = false;
                CallOnEnable(b);
                if (!b.Started && !_startPending.Contains(b)) CallStart(b);
            }
            else if (b != null)
            {
                b.ScriptDisabled = true;
                CallOnDisable(b);
            }
        }

        /// <summary>VortexBehaviour.Enabled (#422): switch the behaviour's Script component and apply it at once.</summary>
        internal void SetBehaviourEnabled(Vortex.VortexBehaviour b, bool enabled)
        {
            if (b == null || b.Destroyed) return;
            if (_scriptOf.TryGetValue(b, out var sc) && sc != null)
            {
                if (sc.IsEnabled != enabled) sc.IsEnabled = enabled;   // the Inspector shows it; play end puts it back (#318)
                if (_active) ApplyScriptEnabled(sc);
                else b.ScriptDisabled = !enabled;
                return;
            }
            // a UI action class has no component: the switch is the behaviour's own
            b.ScriptDisabled = !enabled;
            if (enabled) { CallOnEnable(b); CallStart(b); } else CallOnDisable(b);
        }

        private Vortex.VortexBehaviour InstantiateScript(GameEntity e, Script script, Assembly asm)
        {
            if (script == null || string.IsNullOrEmpty(script.ScriptClassName)) return null;
            // the component's enable toggle: a script switched off in the Inspector does not run (it used to — with one
            // script per entity nobody noticed the toggle did nothing)
            if (!script.IsEnabled) return null;
            var type = FindBehaviourType(asm, script.ScriptClassName);
            if (type == null)
            {
                // Silent-death guard: the class is abstract, renamed, or not a VortexBehaviour —
                // without this the component just never runs and nothing tells the user why.
                try
                {
                    Editor.Core.Services.ConsoleService.Instance.LogError(
                        "Script '" + script.ScriptClassName + "' on entity '" + e.Name + "' resolves to no runnable class. " +
                        "The class must be CONCRETE (not abstract, e.g. not a Weapon base class), derive from VortexBehaviour, " +
                        "and match the file name '" + script.ScriptClassName + ".cs'.");
                }
                catch { }
                return null;
            }
            try
            {
                var behaviour = (Vortex.VortexBehaviour)Activator.CreateInstance(type);
                ApplySerializedFields(behaviour, script);   // #47: per-instance field overrides, before Start()
                long handle;
                if (_behavioursByEntity.TryGetValue(e, out var list) && list.Count > 0)
                    handle = list[0].EntityId;   // a further Script of the entity shares its handle (#408)
                else
                {
                    // Assign a UNIQUE handle and map it to THIS entity — guarantees the behaviour can
                    // only ever read/move its own entity (no engine-EntityId collisions).
                    handle = ++_nextHandle;
                    _entitiesById[handle] = e;
                    _handlesByEntity[e] = handle;   // the behaviour handle IS the entity's canonical handle
                    if (list == null) { list = new List<Vortex.VortexBehaviour>(1); _behavioursByEntity[e] = list; }
                }
                behaviour.EntityId = handle;
                _behaviours.Add(behaviour);
                list.Add(behaviour);
                _scriptOf[behaviour] = script;
                TrackFixedUpdate(behaviour);   // #481
                return behaviour;
            }
            catch (Exception ex) { LogScriptError(script.ScriptClassName, "constructor", ex); return null; }
        }

        /// <summary>#111: a behaviour tree task class (<c>Vortex.BtTask</c> subclass) of the project's scripts by simple
        /// name — the loaded script assembly during play, the on-demand reflection compile in the editor. Null = none.</summary>
        public Type FindBtTaskType(string className)
        {
            if (string.IsNullOrEmpty(className)) return null;
            try
            {
                var asm = _scriptAsm ?? ReflectAssembly();
                if (asm == null) return null;
                foreach (var t in asm.GetTypes())
                    if (!t.IsAbstract && typeof(Vortex.BtTask).IsAssignableFrom(t) && string.Equals(t.Name, className, StringComparison.Ordinal)) return t;
            }
            catch { }
            return null;
        }

        /// <summary>#111: every behaviour tree task class the project's scripts define (the editor's node palette).</summary>
        public IEnumerable<Type> BtTaskTypes()
        {
            var list = new List<Type>();
            try
            {
                var asm = _scriptAsm ?? ReflectAssembly();
                if (asm != null)
                    foreach (var t in asm.GetTypes())
                        if (!t.IsAbstract && typeof(Vortex.BtTask).IsAssignableFrom(t)) list.Add(t);
            }
            catch { }
            return list;
        }

        private Assembly ReflectAssembly()
        {
            // the same script files as last time: the same assembly — or the same failure, not compiled again (#405)
            string key = ScriptSetFingerprint();
            if (key == null || key != _reflectKey)
            {
                _reflectAsm = Compile(out _);
                _reflectKey = key;
                ReflectCompiles++;
            }
            return _reflectAsm;
        }

        /// <summary>Every script's path, size and write time. An added, removed or renamed file changes it too (the newest
        /// write time alone misses a file copied in with an older date, or a deletion), and a set of files that does not
        /// compile is remembered as such: the Inspector and get_entity ask on every refresh, and a project whose scripts
        /// do not compile was compiled again on each ask (#405: 0.7 s per call with 120 scripts, the UI frozen).
        /// Null when the folder cannot be read (then it compiles, as before).</summary>
        private static string ScriptSetFingerprint()
        {
            try
            {
                string dir = ScriptingService.ScriptsDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "";
                var sb = new System.Text.StringBuilder(dir);
                foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var fi = new FileInfo(f);
                    sb.Append('\n').Append(f).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks);
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        private static Type FindBehaviourType(Assembly asm, string className)
        {
            // Match by simple name, ignoring namespace; must derive from VortexBehaviour.
            foreach (var t in asm.GetTypes())
            {
                if (!typeof(Vortex.VortexBehaviour).IsAssignableFrom(t) || t.IsAbstract) continue;
                if (string.Equals(t.Name, className, StringComparison.Ordinal)) return t;
            }
            return null;
        }

        // ---- serialized script fields (#47) ----

        /// <summary>Field types the inspector edits + the scene serializes: primitives, enums, Vector3.</summary>
        /// <summary>Separator for ARRAY field elements inside the single stored value string — the
        /// ASCII unit separator never occurs in paths/names, so string elements round-trip safely.</summary>
        public const char ArraySeparator = '\u001F';

        public static bool IsInspectableFieldType(Type t)
            => t == typeof(int) || t == typeof(float) || t == typeof(bool) || t == typeof(string)
               || t.IsEnum || t == typeof(Vortex.Vector3)
               || (t.IsArray && t.GetArrayRank() == 1 && t.GetElementType() != null
                   && !t.GetElementType().IsArray && IsInspectableFieldType(t.GetElementType()));

        /// <summary>Invariant-culture round-trip of a field value ("1,2,3" for Vector3; enum by name;
        /// arrays as elements joined with <see cref="ArraySeparator"/> — a WeaponPrefabs list is one string).</summary>
        public static string FormatFieldValue(object v)
        {
            if (v == null) return "";
            if (v is float f) return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (v is int i) return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "true" : "false";
            if (v is Vortex.Vector3 v3)
                return v3.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ","
                     + v3.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ","
                     + v3.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (v is Array arr)
            {
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < arr.Length; k++)
                {
                    if (k > 0) sb.Append(ArraySeparator);
                    sb.Append(FormatFieldValue(arr.GetValue(k)));
                }
                return sb.ToString();
            }
            return v.ToString();
        }

        /// <summary>Parse a stored string back into the REFLECTED field type. Throws on garbage — the
        /// callers skip-and-log so one bad value can't take a scene down. Arrays: "" = empty array,
        /// else elements split on <see cref="ArraySeparator"/> and parsed per element type.</summary>
        public static object ParseFieldValue(Type ft, string v)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (ft == typeof(int)) return int.Parse(v, ci);
            if (ft == typeof(float)) return float.Parse(v, System.Globalization.NumberStyles.Float, ci);
            if (ft == typeof(bool)) return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            if (ft == typeof(string)) return v ?? "";
            if (ft.IsEnum) return Enum.Parse(ft, v, true);
            if (ft == typeof(Vortex.Vector3))
            {
                var p = (v ?? "").Split(',');
                return new Vortex.Vector3(
                    float.Parse(p[0], System.Globalization.NumberStyles.Float, ci),
                    float.Parse(p[1], System.Globalization.NumberStyles.Float, ci),
                    float.Parse(p[2], System.Globalization.NumberStyles.Float, ci));
            }
            if (ft.IsArray && ft.GetArrayRank() == 1)
            {
                var et = ft.GetElementType();
                if (string.IsNullOrEmpty(v)) return Array.CreateInstance(et, 0);
                var parts = v.Split(ArraySeparator);
                var result = Array.CreateInstance(et, parts.Length);
                for (int k = 0; k < parts.Length; k++) result.SetValue(ParseFieldValue(et, parts[k]), k);
                return result;
            }
            throw new NotSupportedException(ft.Name);
        }

        /// <summary>Apply the component's stored field overrides onto a fresh behaviour instance —
        /// runs BEFORE Start() (and again after every hot-reload, which re-instantiates through the
        /// same path). Unknown/renamed fields and unparsable values are skipped, never fatal.</summary>
        private static void ApplySerializedFields(Vortex.VortexBehaviour b, Script script)
        {
            if (b == null || script == null || script.FieldValues == null) return;
            var t = b.GetType();
            foreach (var fv in script.FieldValues)
            {
                if (fv == null || string.IsNullOrEmpty(fv.Name)) continue;
                var f = t.GetField(fv.Name, BindingFlags.Public | BindingFlags.Instance);
                if (f == null || !IsInspectableFieldType(f.FieldType)) continue;
                try { f.SetValue(b, ParseFieldValue(f.FieldType, fv.Value)); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ScriptRuntime] field '" + fv.Name + "' on "
                        + t.Name + ": " + ex.Message);
                }
            }
        }

        // Edit-mode reflection assembly for the inspector (#47): compiled on demand, kept while the script files
        // are the same (ScriptSetFingerprint). A running play session's assembly is the freshest truth.
        private Assembly _reflectAsm;
        private string _reflectKey;

        /// <summary>How often the edit-mode assembly was compiled (the cache test counts with it).</summary>
        internal int ReflectCompiles { get; private set; }

        /// <summary>True when the behaviour class reacts to contact — it overrides OnTriggerEnter / OnTriggerStay /
        /// OnTriggerExit or OnCollisionEnter. The Collision Editor's script section is about such a script (#408: an entity
        /// can carry several, and replacing "the" script must not hit an unrelated one like a Health script).</summary>
        public static bool HandlesContact(Type behaviourType)
        {
            if (behaviourType == null || !typeof(Vortex.VortexBehaviour).IsAssignableFrom(behaviourType)) return false;
            foreach (var name in new[] { "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit", "OnCollisionEnter" })
            {
                var m = behaviourType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Vortex.TriggerHit) }, null);
                if (m != null && m.DeclaringType != typeof(Vortex.VortexBehaviour)) return true;
            }
            return false;
        }

        /// <summary>The behaviour TYPE for a script class, usable in EDIT mode (the inspector reflects
        /// its public fields). Compiles the project scripts on first use / after a script save; returns
        /// null while the scripts don't compile (the inspector shows a hint instead of rows).</summary>
        public Type GetScriptTypeForInspector(string className)
        {
            if (string.IsNullOrEmpty(className)) return null;
            try
            {
                if (_scriptAsm != null) return FindBehaviourType(_scriptAsm, className);
                var asm = ReflectAssembly();
                return asm != null ? FindBehaviourType(asm, className) : null;
            }
            catch { return null; }
        }

        private Assembly Compile(out string log)
        {
            log = null;
            string dir = ScriptingService.ScriptsDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            // Compile every script EXCEPT the VS-only API stub (the real Vortex API lives in this assembly).
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("VortexScripting.cs", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (files.Length == 0) return null;

            // Shared core (modern .NET): Roslyn compiles the scripts in-process into a collectible load context.
            return Editor.Scripting.RoslynScriptCompiler.Compile(files, out log);
        }

        // ---- IScriptHost (behaviours act on the live game through these; transforms go through the C#
        // model so the viewport reflects them immediately) ----

        Vortex.Vector3 Vortex.IScriptHost.GetPosition(long entityId)
        {
            if (_entitiesById.TryGetValue(entityId, out var e) && e.Transform != null)
            {
                var p = e.Transform.LocalPosition;
                return new Vortex.Vector3(p.X, p.Y, p.Z);
            }
            return Vortex.Vector3.Zero;
        }

        void Vortex.IScriptHost.SetPosition(long entityId, Vortex.Vector3 position)
        {
            if (_entitiesById.TryGetValue(entityId, out var e) && e.Transform != null)
                e.Transform.LocalPosition = new ECS.Vector3(position.X, position.Y, position.Z);
        }

        Vortex.Vector3 Vortex.IScriptHost.GetRotation(long entityId)
        {
            if (_entitiesById.TryGetValue(entityId, out var e) && e.Transform != null)
            {
                var r = e.Transform.LocalRotation; // Euler degrees
                return new Vortex.Vector3(r.X, r.Y, r.Z);
            }
            return Vortex.Vector3.Zero;
        }

        void Vortex.IScriptHost.SetRotation(long entityId, Vortex.Vector3 eulerDegrees)
        {
            if (_entitiesById.TryGetValue(entityId, out var e) && e.Transform != null)
                e.Transform.LocalRotation = new ECS.Vector3(eulerDegrees.X, eulerDegrees.Y, eulerDegrees.Z);
        }

        Vortex.Vector3 Vortex.IScriptHost.GetScale(long entityId)
        {
            if (_entitiesById.TryGetValue(entityId, out var e) && e.Transform != null)
            {
                var s = e.Transform.LocalScale;
                return new Vortex.Vector3(s.X, s.Y, s.Z);
            }
            return Vortex.Vector3.One;
        }

        void Vortex.IScriptHost.SetScale(long entityId, Vortex.Vector3 scale)
        {
            if (_entitiesById.TryGetValue(entityId, out var e) && e.Transform != null)
                e.Transform.LocalScale = new ECS.Vector3(scale.X, scale.Y, scale.Z);
        }

        /// <summary>WORLD-space pose write, parent-safe: desired = R(euler) + T(pos), converted into the
        /// entity's local frame against its real parent chain (same math as the bone-socket pass); the
        /// entity's own LOCAL scale is untouched. This is what makes a camera-locked viewmodel work when
        /// it lives as a CHILD of the Player entity — SetPosition/SetRotation write LOCAL values there.</summary>
        void Vortex.IScriptHost.SetEntityWorldPose(long entityId, Vortex.Vector3 position, Vortex.Vector3 rotationEulerDeg)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e) || e.Transform == null) return;

            var desired = Editor.Core.Animation.BoneSocketService.EulerZXY(
                new System.Numerics.Vector3(rotationEulerDeg.X, rotationEulerDeg.Y, rotationEulerDeg.Z));
            desired.Translation = new System.Numerics.Vector3(position.X, position.Y, position.Z);

            var local = desired;
            var parentWorld = Editor.Core.Animation.BoneSocketService.EntityWorld(e.Parent);
            System.Numerics.Matrix4x4 inv;
            if (System.Numerics.Matrix4x4.Invert(parentWorld, out inv)) local = desired * inv;

            // Rotation from the orthonormalized basis (a scaled parent bakes 1/s into the local matrix).
            var euler = Editor.Core.Animation.BoneSocketService.ToEulerZXY(
                Editor.Core.Animation.BoneSocketService.NormalizeBasis(local));
            var t = e.Transform;
            t.LocalPosition = new ECS.Vector3(local.Translation.X, local.Translation.Y, local.Translation.Z);
            t.LocalRotation = new ECS.Vector3(euler.X, euler.Y, euler.Z);
        }

        void Vortex.IScriptHost.SetEntityColor(long entityId, float r, float g, float b)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Services.SceneRenderService.Instance.SetEntityColor(e, r, g, b, 1f);
        }

        // Runtime material swap (weapon camos, damage states): the render loop resolves MeshRenderer.MaterialPath per
        // frame (cached per .vmat path), so assigning the path is all it takes; null/"" falls back to the imported material.
        void Vortex.IScriptHost.SetEntityMaterial(long entityId, string materialPath)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e)) return;
            var mr = e.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>();
            if (mr == null) return;
            mr.MaterialPath = string.IsNullOrEmpty(materialPath) ? null : materialPath.Replace('\\', '/');
        }

        string Vortex.IScriptHost.GetEntityMaterial(long entityId)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e)) return "";
            var mr = e.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>();
            return mr?.MaterialPath ?? "";
        }

        // --- skeletal animation (Vortex.Animation / VortexBehaviour sugar -> AnimationService) ---

        bool Vortex.IScriptHost.PlayAnimation(long entityId, string clip, float fade)
        {
            return _entitiesById.TryGetValue(entityId, out var e)
                && Editor.Core.Animation.AnimationService.Instance.Play(e, clip, fade);
        }

        void Vortex.IScriptHost.StopAnimation(long entityId)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.Stop(e);
        }

        void Vortex.IScriptHost.SetAnimationSpeed(long entityId, float speed)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.SetSpeed(e, speed);
        }

        bool Vortex.IScriptHost.IsAnimationPlaying(long entityId, string clip)
        {
            return _entitiesById.TryGetValue(entityId, out var e)
                && Editor.Core.Animation.AnimationService.Instance.IsPlaying(e, clip);
        }

        float Vortex.IScriptHost.GetAnimationTime(long entityId)
        {
            return _entitiesById.TryGetValue(entityId, out var e)
                ? Editor.Core.Animation.AnimationService.Instance.GetTime(e) : 0f;
        }

        // --- camera/attachment feel primitives (#176 -> CameraFXService) ---

        void Vortex.IScriptHost.CameraFxKick(Vortex.Vector3 rotationDegrees, Vortex.Vector3 position)
            => Editor.Core.Services.CameraFXService.Instance.KickCamera(
                new System.Numerics.Vector3(rotationDegrees.X, rotationDegrees.Y, rotationDegrees.Z),
                new System.Numerics.Vector3(position.X, position.Y, position.Z));

        void Vortex.IScriptHost.CameraFxKickEntity(long entityId, Vortex.Vector3 rotationDegrees, Vortex.Vector3 position)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Services.CameraFXService.Instance.KickEntity(e,
                    new System.Numerics.Vector3(rotationDegrees.X, rotationDegrees.Y, rotationDegrees.Z),
                    new System.Numerics.Vector3(position.X, position.Y, position.Z));
        }

        void Vortex.IScriptHost.CameraFxSway(int slot, float posAmp, float rotAmpDeg, float freq)
            => Editor.Core.Services.CameraFXService.Instance.SwayCamera(slot, posAmp, rotAmpDeg, freq);

        void Vortex.IScriptHost.CameraFxSwayEntity(long entityId, int slot, float posAmp, float rotAmpDeg, float freq)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Services.CameraFXService.Instance.SwayEntity(e, slot, posAmp, rotAmpDeg, freq);
        }

        void Vortex.IScriptHost.CameraFxSpring(float stiffness, float damping)
            => Editor.Core.Services.CameraFXService.Instance.SetSpring(stiffness, damping);

        void Vortex.IScriptHost.CameraFxSeed(int seed)
            => Editor.Core.Services.CameraFXService.Instance.SetSeed(seed);

        // --- synced playback groups (#174) ---

        int Vortex.IScriptHost.PlaySyncedAnimation(long[] entities, string[] clips, float speed, float fade)
        {
            if (entities == null || clips == null) return 0;
            var resolved = new ECS.GameEntity[entities.Length];
            for (int i = 0; i < entities.Length; i++)
                _entitiesById.TryGetValue(entities[i], out resolved[i]);
            return Editor.Core.Animation.AnimationService.Instance.PlaySynced(resolved, clips, speed, fade);
        }

        void Vortex.IScriptHost.PauseSyncedAnimation(int groupId, bool paused)
            => Editor.Core.Animation.AnimationService.Instance.PauseSynced(groupId, paused);

        void Vortex.IScriptHost.SetSyncedAnimationSpeed(int groupId, float speed)
            => Editor.Core.Animation.AnimationService.Instance.SetSyncedSpeed(groupId, speed);

        void Vortex.IScriptHost.StopSyncedAnimation(int groupId)
            => Editor.Core.Animation.AnimationService.Instance.StopSynced(groupId);

        // --- bone-masked layers (#173) ---

        bool Vortex.IScriptHost.PlayLayeredAnimation(long entityId, string clip, int layer, string mask, float weight, float fade)
        {
            return _entitiesById.TryGetValue(entityId, out var e)
                && Editor.Core.Animation.AnimationService.Instance.PlayLayered(e, clip, layer, mask, weight, fade);
        }

        void Vortex.IScriptHost.SetAnimationLayerWeight(long entityId, int layer, float weight)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.SetLayerWeight(e, layer, weight);
        }

        void Vortex.IScriptHost.StopLayeredAnimation(long entityId, int layer)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.StopLayer(e, layer);
        }

        // --- runtime procedural bone control (#178: additive aim-offset / lean / recoil on real bones) ---

        void Vortex.IScriptHost.SetBoneAdditiveRotation(long entityId, string bone, Vortex.Vector3 eulerDeg)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.SetBoneAdditiveRotation(e, bone,
                    new System.Numerics.Vector3(eulerDeg.X, eulerDeg.Y, eulerDeg.Z));
        }

        void Vortex.IScriptHost.SetBoneScaleOverride(long entityId, string bone, float scale)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.SetBoneScaleOverride(e, bone, scale);
        }

        void Vortex.IScriptHost.ClearBoneOverrides(long entityId)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.ClearBoneOverrides(e);
        }

        void Vortex.IScriptHost.SetBoneHidden(long entityId, string bone, bool hidden, bool includeDescendants)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.SetBoneHidden(e, bone, hidden, includeDescendants);
        }

        void Vortex.IScriptHost.SetIkWorldTarget(long entityId, string tipBone, Vortex.Vector3 worldPos, Vortex.Vector3 worldRotEuler, bool hasRotation)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e)) return;
            System.Numerics.Quaternion? rot = null;
            if (hasRotation)
            {
                var m = Editor.Core.Animation.BoneSocketService.EulerZXY(new System.Numerics.Vector3(worldRotEuler.X, worldRotEuler.Y, worldRotEuler.Z));
                rot = System.Numerics.Quaternion.CreateFromRotationMatrix(m);
            }
            Editor.Core.Animation.AnimationService.Instance.SetIkWorldTarget(e, tipBone,
                new System.Numerics.Vector3(worldPos.X, worldPos.Y, worldPos.Z), rot);
        }

        void Vortex.IScriptHost.ClearIkWorldTarget(long entityId, string tipBone)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.ClearIkWorldTarget(e, tipBone);
        }

        void Vortex.IScriptHost.SetIkPoleAngle(long entityId, string tipBone, float degrees)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e)) return;
            var comps = e.Components;
            for (int i = 0; i < comps.Count; i++)
                if (comps[i] is Editor.ECS.Components.Animation.TwoBoneIk ik &&
                    (string.IsNullOrEmpty(tipBone) || string.Equals(ik.TipBone, tipBone, StringComparison.OrdinalIgnoreCase)))
                    ik.PoleAngle = degrees;   // setter re-syncs the runtime chain via AnimationService.RefreshIk
        }

        bool Vortex.IScriptHost.TryGetEntityWorldPose(long entityId, out Vortex.Vector3 position, out Vortex.Vector3 rotationEulerDeg)
        {
            position = Vortex.Vector3.Zero; rotationEulerDeg = Vortex.Vector3.Zero;
            if (!_entitiesById.TryGetValue(entityId, out var e) || e.Transform == null) return false;
            var w = Editor.Core.Animation.BoneSocketService.EntityWorld(e);
            var euler = Editor.Core.Animation.BoneSocketService.ToEulerZXY(Editor.Core.Animation.BoneSocketService.NormalizeBasis(w));
            position = new Vortex.Vector3(w.Translation.X, w.Translation.Y, w.Translation.Z);
            rotationEulerDeg = new Vortex.Vector3(euler.X, euler.Y, euler.Z);
            return true;
        }

        void Vortex.IScriptHost.SetRenderLayer(long entityId, int layer)
        {
            if (layer < 0 || layer > 2) return;
            if (!_entitiesById.TryGetValue(entityId, out var e)) return;
            ApplyRenderLayerRecursive(e, layer);
            Editor.Core.Services.SceneRenderService.RuntimeDirty = Editor.Core.Services.SceneRenderService.StaticDirty = true;   // structural (#364 A)   // retained queue -> re-submit
        }

        void Vortex.IScriptHost.SetIkWeight(long entityId, string tipBone, float weight)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e)) return;
            var comps = e.Components;
            for (int i = 0; i < comps.Count; i++)
            {
                if (comps[i] is Editor.ECS.Components.Animation.TwoBoneIk ik &&
                    (string.IsNullOrEmpty(tipBone) || string.Equals(ik.TipBone, tipBone, StringComparison.OrdinalIgnoreCase)))
                {
                    ik.Weight = weight;   // setter clamps 0..1 + re-poses via AnimationService.RefreshIk
                }
            }
            // In-editor play mutates the authored component, but play end reverts the scene state, so
            // authored weights survive; the standalone GameHost ends with the process. No ledger needed.
        }

        // --- rig-aware procedural layers (#147: hand poses / look-at / foot IK -> AnimationService) ---
        // Runtime overrides live in the animation state, never in the authored components.

        /// <summary>"Left"/"L", "Right"/"R" or "Both" (both = null side). False for anything else.</summary>
        private static bool ParseHandSide(string side, out Editor.ECS.Components.Animation.HandSide? result)
        {
            result = null;
            string s = (side ?? "").Trim().ToLowerInvariant();
            if (s == "left" || s == "l") { result = Editor.ECS.Components.Animation.HandSide.Left; return true; }
            if (s == "right" || s == "r") { result = Editor.ECS.Components.Animation.HandSide.Right; return true; }
            return s == "both" || s == "";
        }

        private bool _handPoseWarned;

        bool Vortex.IScriptHost.SetHandPose(long entityId, string side, string preset, float weight, float blendSeconds)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e)) return false;
            Editor.ECS.Components.Animation.HandSide? s;
            Editor.ECS.Components.Animation.HandPosePreset p;
            if (!ParseHandSide(side, out s) || !Editor.Core.Animation.AnimationService.TryParsePreset(preset, out p))
            {
                if (!_handPoseWarned)
                {
                    _handPoseWarned = true;
                    Editor.Core.Services.ConsoleService.Instance?.LogWarning("Animation.SetHandPose: side must be Left/Right/Both and preset one of " +
                        string.Join(", ", Enum.GetNames(typeof(Editor.ECS.Components.Animation.HandPosePreset))) + " (got '" + side + "', '" + preset + "').");
                }
                return false;
            }
            var svc = Editor.Core.Animation.AnimationService.Instance;
            if (s.HasValue) return svc.SetHandPosePreset(e, s.Value, preset, weight, blendSeconds);
            bool l = svc.SetHandPosePreset(e, Editor.ECS.Components.Animation.HandSide.Left, preset, weight, blendSeconds);
            bool r = svc.SetHandPosePreset(e, Editor.ECS.Components.Animation.HandSide.Right, preset, weight, blendSeconds);
            return l || r;
        }

        bool Vortex.IScriptHost.SetHandPoseCurls(long entityId, string side, Vortex.Vector3 index, Vortex.Vector3 middle, Vortex.Vector3 ring,
            Vortex.Vector3 pinky, Vortex.Vector3 thumb, float spread, float weight, float blendSeconds)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e)) return false;
            Editor.ECS.Components.Animation.HandSide? s;
            if (!ParseHandSide(side, out s)) return false;
            var curls = new[]
            {
                new ECS.Vector3(index.X, index.Y, index.Z), new ECS.Vector3(middle.X, middle.Y, middle.Z), new ECS.Vector3(ring.X, ring.Y, ring.Z),
                new ECS.Vector3(pinky.X, pinky.Y, pinky.Z), new ECS.Vector3(thumb.X, thumb.Y, thumb.Z)
            };
            var svc = Editor.Core.Animation.AnimationService.Instance;
            if (s.HasValue) return svc.SetHandPoseCurls(e, s.Value, curls, spread, weight, blendSeconds);
            bool l = svc.SetHandPoseCurls(e, Editor.ECS.Components.Animation.HandSide.Left, curls, spread, weight, blendSeconds);
            bool r = svc.SetHandPoseCurls(e, Editor.ECS.Components.Animation.HandSide.Right, curls, spread, weight, blendSeconds);
            return l || r;
        }

        void Vortex.IScriptHost.ClearHandPose(long entityId, string side)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e)) return;
            Editor.ECS.Components.Animation.HandSide? s;
            if (!ParseHandSide(side, out s)) return;
            Editor.Core.Animation.AnimationService.Instance.ClearHandPose(e, s);
        }

        void Vortex.IScriptHost.SetLookAtPoint(long entityId, Vortex.Vector3 worldPoint)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.SetLookAtPoint(e, new System.Numerics.Vector3(worldPoint.X, worldPoint.Y, worldPoint.Z));
        }

        void Vortex.IScriptHost.SetLookAtEntity(long entityId, long targetEntityId)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e)) return;
            _entitiesById.TryGetValue(targetEntityId, out var target);
            if (target == null) { Editor.Core.Animation.AnimationService.Instance.ClearLookAtTarget(e); return; }
            Editor.Core.Animation.AnimationService.Instance.SetLookAtEntity(e, target);
        }

        void Vortex.IScriptHost.ClearLookAtTarget(long entityId)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.ClearLookAtTarget(e);
        }

        void Vortex.IScriptHost.SetLookAtWeight(long entityId, float weight)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.SetLookAtWeight(e, weight);
        }

        void Vortex.IScriptHost.SetFootIkWeight(long entityId, float weight)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Animation.AnimationService.Instance.SetFootIkWeight(e, weight);
        }

        // --- bone sockets (#170/#171: Attach/Detach/GetBoneTransform -> BoneSocketService) ---

        bool Vortex.IScriptHost.AttachEntityToBone(long entityId, long targetId, string bone,
            Vortex.Vector3 offsetPos, Vortex.Vector3 offsetRotEuler)
        {
            if (!_entitiesById.TryGetValue(entityId, out var e)) return false;
            _entitiesById.TryGetValue(targetId, out var target);   // 0/unknown = nearest ancestor Animator
            return Editor.Core.Animation.BoneSocketService.Instance.Attach(e, target, bone,
                new System.Numerics.Vector3(offsetPos.X, offsetPos.Y, offsetPos.Z),
                new System.Numerics.Vector3(offsetRotEuler.X, offsetRotEuler.Y, offsetRotEuler.Z));
        }

        bool Vortex.IScriptHost.DetachEntityFromBone(long entityId, bool keepWorldPosition)
        {
            return _entitiesById.TryGetValue(entityId, out var e)
                && Editor.Core.Animation.BoneSocketService.Instance.Detach(e, keepWorldPosition);
        }

        bool Vortex.IScriptHost.TryGetBoneTransform(long targetId, string bone,
            out Vortex.Vector3 position, out Vortex.Vector3 rotationEuler)
        {
            position = default(Vortex.Vector3); rotationEuler = default(Vortex.Vector3);
            if (!_entitiesById.TryGetValue(targetId, out var e)) return false;
            if (!Editor.Core.Animation.BoneSocketService.Instance.TryGetBoneTransform(e, bone, out var p, out var r))
                return false;
            position = new Vortex.Vector3(p.X, p.Y, p.Z);
            rotationEuler = new Vortex.Vector3(r.X, r.Y, r.Z);
            return true;
        }

        void Vortex.IScriptHost.ComposeBoneAttach(Vortex.Vector3 bonePos, Vortex.Vector3 boneEuler,
            Vortex.Vector3 offsetPos, Vortex.Vector3 offsetEuler, out Vortex.Vector3 worldPos, out Vortex.Vector3 worldEuler)
        {
            // Bone frame: rotation from the euler + world position (unit scale). Shared composition with the editor.
            var boneWorld = Editor.Core.Animation.BoneSocketService.EulerZXY(new System.Numerics.Vector3(boneEuler.X, boneEuler.Y, boneEuler.Z));
            boneWorld.Translation = new System.Numerics.Vector3(bonePos.X, bonePos.Y, bonePos.Z);
            var world = Editor.Core.Animation.BoneSocketService.ComposeBoneLocalMatrix(
                boneWorld,
                new System.Numerics.Vector3(offsetPos.X, offsetPos.Y, offsetPos.Z),
                new System.Numerics.Vector3(offsetEuler.X, offsetEuler.Y, offsetEuler.Z), 1f);
            var wp = world.Translation;
            var we = Editor.Core.Animation.BoneSocketService.ToEulerZXY(world);
            worldPos = new Vortex.Vector3(wp.X, wp.Y, wp.Z);
            worldEuler = new Vortex.Vector3(we.X, we.Y, we.Z);
        }

        string Vortex.IScriptHost.AssetReadText(string relPath)
        {
            try
            {
                if (string.IsNullOrEmpty(relPath)) return null;
                var proj = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.Path : null;
                var abs = System.IO.Path.IsPathRooted(relPath) ? relPath : (proj != null ? System.IO.Path.Combine(proj, relPath) : relPath);
                byte[] bytes;
                if (Editor.Core.Services.AssetVfs.IsMounted && Editor.Core.Services.AssetVfs.TryGetBytes(abs, out bytes) && bytes != null)
                    return System.Text.Encoding.UTF8.GetString(bytes);
                if (System.IO.File.Exists(abs)) return System.IO.File.ReadAllText(abs);
            }
            catch { }
            return null;
        }

        bool Vortex.IScriptHost.AssetWriteText(string relPath, string text)
        {
            try
            {
                if (string.IsNullOrEmpty(relPath)) return false;
                if (Editor.Core.Services.AssetVfs.IsMounted) return false;   // shipped pak is read-only
                var proj = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.Path : null;
                var abs = System.IO.Path.IsPathRooted(relPath) ? relPath : (proj != null ? System.IO.Path.Combine(proj, relPath) : relPath);
                var dir = System.IO.Path.GetDirectoryName(abs);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(abs, text ?? "");
                return true;
            }
            catch { return false; }
        }

        long[] Vortex.IScriptHost.GetAttachedEntities(long targetId)
        {
            if (!_entitiesById.TryGetValue(targetId, out var e)) return new long[0];
            var list = Editor.Core.Animation.BoneSocketService.Instance.GetAttachedTo(e);
            var result = new long[list.Count];
            for (int i = 0; i < list.Count; i++) result[i] = HandleForEntity(list[i]);
            return result;
        }


        // --- deferred scene-switch request (set by a script via Vortex.Scene.Load; applied by the driver) ---
        private string _pendingScene;
        void Vortex.IScriptHost.LoadScene(string name) { _pendingScene = name; }
        /// <summary>Returns the requested scene name (and clears it), or null. The runtime driver calls this
        /// AFTER Update() and performs the switch (so it never happens mid-tick).</summary>
        public string ConsumePendingScene() { var s = _pendingScene; _pendingScene = null; return s; }
        public string PendingScene { get { return _pendingScene; } } // diagnostic peek

        // --- deferred quit of in-editor play (#410): Application.Quit() used to stop play right away, from inside the
        // behaviour's Update — End() and the play-snapshot restore ran in the middle of the tick, and the rest of the tick
        // (physics, LateUpdate, the animation and bone-socket pass) then wrote play-time poses into the restored edit
        // scene. Now it only asks; the editor's viewport stops play after the whole tick, through the toolbar's Stop.
        private bool _quitRequested;
        /// <summary>True once after a script asked to quit in-editor play (Application.Quit); the caller stops play.</summary>
        public bool ConsumeQuitRequest() { bool q = _quitRequested; _quitRequested = false; return q; }

        // --- mouse mode (game-controlled via Vortex.Cursor; the GameWindow enforces capture) ---
        private bool _cursorLocked; // default false: free cursor (menus/lobby) until a script locks it
        void Vortex.IScriptHost.SetCursorLocked(bool locked) { _cursorLocked = locked; }
        bool Vortex.IScriptHost.GetCursorLocked() { return _cursorLocked; }
        public bool CursorLocked { get { return _cursorLocked; } }

        // --- UI overlay frame state (fed by the runtime driver each frame: the editor viewport / the player) ---
        private float _uiW, _uiH, _mouseX, _mouseY, _uiScale = 1f;
        private bool _mouseDown, _mousePressed;

        /// <summary>Called once per frame before Update() so scripts can draw UI + hit-test the mouse. Size and mouse are in
        /// UI POINTS, the host window's logical units — the editor viewport's layout size, the player window's client size —
        /// the same unit UIBegin gets, on every host (#454); <paramref name="pixelsPerPoint"/> is how many device pixels one
        /// point covers (2 on a Retina display).</summary>
        public void SetUIFrame(float width, float height, float mouseX, float mouseY, bool mouseDown, bool mousePressed, float pixelsPerPoint)
        {
            _uiW = width; _uiH = height; _mouseX = mouseX; _mouseY = mouseY;
            _mouseDown = mouseDown; _mousePressed = mousePressed;
            _uiScale = pixelsPerPoint > 0f && float.IsFinite(pixelsPerPoint) ? pixelsPerPoint : 1f;
        }

        public void SetUIFrame(float width, float height, float mouseX, float mouseY, bool mouseDown, bool mousePressed)
            => SetUIFrame(width, height, mouseX, mouseY, mouseDown, mousePressed, _uiScale);

        void Vortex.IScriptHost.UIRect(float x, float y, float w, float h, float r, float g, float b, float a, float radius)
            => Editor.DllWrapper.VortexAPI.UIRect(x, y, w, h, r, g, b, a, radius);
        void Vortex.IScriptHost.UIText(float x, float y, float w, float h, string text, float size, float r, float g, float b, float a, int align, int weight)
            => Editor.DllWrapper.VortexAPI.UIText(x, y, w, h, text, size, r, g, b, a, align, weight);
        void Vortex.IScriptHost.UILine(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick)
            => Editor.DllWrapper.VortexAPI.UILine(x1, y1, x2, y2, r, g, b, a, thick);
        void Vortex.IScriptHost.UIImage(float x, float y, float w, float h, string path, float r, float g, float b, float a)
            => Editor.DllWrapper.VortexAPI.UIImage(x, y, w, h, Editor.UI.Vui.VuiCanvas.ResolveImage(path), r, g, b, a);
        float Vortex.IScriptHost.UIWidth() => _uiW;
        float Vortex.IScriptHost.UIHeight() => _uiH;
        float Vortex.IScriptHost.UIMouseX() => _mouseX;
        float Vortex.IScriptHost.UIMouseY() => _mouseY;
        float Vortex.IScriptHost.UIScale() => _uiScale;
        bool Vortex.IScriptHost.UIMouseDown() => _mouseDown;
        bool Vortex.IScriptHost.UIMousePressed() => _mousePressed;

        void Vortex.IScriptHost.QuitGame()
        {
            try
            {
                // The standalone player and --project dev play run INSIDE the blocking native GameHost loop
                // (App.BootPlayer -> RunGameHost), which owns this thread and pumps its own Win32 messages — the
                // WPF Dispatcher is NOT pumping. So Application.Shutdown() here would only be queued and never run,
                // and the window would stay open (the old "Leave / Quit Game does nothing" bug). Instead break the
                // native loop (g_running=false); RunGameHost then returns and BootPlayer calls Shutdown() to exit.
                if (Editor.Core.Services.PlayModeService.Instance.NativeGameHostRunning)
                    Editor.DllWrapper.VortexAPI.RequestGameHostExit();
                else if (Editor.Core.Services.PlayModeService.Instance.IsPlaying)
                    _quitRequested = true;   // in-editor play: stop after this tick (#410), don't kill the editor
            }
            catch { }
        }

        void Vortex.IScriptHost.SetCameraFov(float fovDegrees)
        {
            try { Editor.DllWrapper.VortexAPI.SetViewFOV(fovDegrees); }
            catch { }
        }

        void Vortex.IScriptHost.SetViewmodelFov(float fovDegrees)
        {
            try { Editor.DllWrapper.VortexAPI.SetViewmodelFov(fovDegrees); }
            catch { }
        }

        // Edge-accurate mesh colliders: resolve an imported model's real triangles (native export), VFS-aware + cached.
        private static readonly System.Collections.Generic.Dictionary<string, float[]> _triCache =
            new System.Collections.Generic.Dictionary<string, float[]>(System.StringComparer.OrdinalIgnoreCase);
        /// <summary>A model's local collision triangles (flat x, y, z …) — the same source the collision world uses, also
        /// outside play mode (the world lighting cache opens walk-in meshes with it).</summary>
        public static float[] MeshTriangles(string meshPath) => ResolveMeshTriangles(meshPath);

        /// <summary>A model changed on disk (SceneRenderService.InvalidateModel): forget its cached triangles, so collision and
        /// the world lighting cache read the new mesh (in edit mode the cache was only cleared when play stopped).</summary>
        public static void ForgetMeshTriangles() { _triCache.Clear(); }

        private static float[] ResolveMeshTriangles(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath) || meshPath.StartsWith("Primitive:", System.StringComparison.OrdinalIgnoreCase)) return null;
            float[] cached; if (_triCache.TryGetValue(meshPath, out cached)) return cached;
            float[] tris = null;
            try
            {
                // "model.glb#submeshN" collides with submesh N only (#362) — the whole model used to be loaded N times
                var actual = meshPath; int h = actual.LastIndexOf('#'); int submesh = -1;
                if (h > 0)
                {
                    var tag = actual.Substring(h + 1);
                    if (tag.StartsWith("submesh", System.StringComparison.OrdinalIgnoreCase)) int.TryParse(tag.Substring(7), out submesh);
                    actual = actual.Substring(0, h);
                    if (submesh < 0) submesh = -1;
                }
                var proj = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.Path : null;
                var abs = System.IO.Path.IsPathRooted(actual) ? actual : (proj != null ? System.IO.Path.Combine(proj, actual) : actual);
                var ext = System.IO.Path.GetExtension(actual); if (ext != null) ext = ext.TrimStart('.');
                byte[] bytes;
                if (Editor.Core.Services.AssetVfs.IsMounted && Editor.Core.Services.AssetVfs.TryGetBytes(abs, out bytes) && bytes != null)
                    tris = Editor.DllWrapper.VortexAPI.GetModelTrianglesFromMemory(bytes, ext, submesh, Editor.Core.Services.ModelImportSettings.LoadLeftHanded(abs));
                else if (System.IO.File.Exists(abs))
                {
                    // the on-disk cache saves the second Assimp pass per model on every later start (#364 C)
                    string cacheFile = Editor.Core.Services.Physics.CollisionTriangleCache.FileFor(proj, abs, submesh);
                    tris = Editor.Core.Services.Physics.CollisionTriangleCache.TryRead(cacheFile);
                    if (tris == null)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        tris = Editor.DllWrapper.VortexAPI.GetModelTriangles(abs, submesh);
                        sw.Stop();
                        if (tris != null) Editor.Core.Services.Physics.CollisionTriangleCache.Write(cacheFile, tris);
                        if (sw.ElapsedMilliseconds > 50)
                            try { Editor.Core.Services.ConsoleService.Instance.LogSystem("Collision mesh " + System.IO.Path.GetFileName(abs) + ": " + (tris != null ? tris.Length / 9 : 0) + " triangles parsed in " + sw.ElapsedMilliseconds + " ms — cached for the next start"); } catch { }
                    }
                }
            }
            catch { }
            _triCache[meshPath] = tris;
            return tris;
        }

        Vortex.Vector3 Vortex.IScriptHost.MoveCharacter(Vortex.Vector3 feet, float radius, float height, Vortex.Vector3 move, out bool grounded, long selfId)
            => MoveCharacter(feet, radius, height, move, out grounded, selfId, null);

        /// <summary>Physics.MoveCharacter: <paramref name="explicitOwner"/> is the entity a script named as the owner (#406, a
        /// squad manager moving its guards' capsules); without one, the code that runs decides (<see cref="CharacterOwner"/>),
        /// and an id the collision world already knows keeps its owner.</summary>
        internal Vortex.Vector3 MoveCharacter(Vortex.Vector3 feet, float radius, float height, Vortex.Vector3 move, out bool grounded, long selfId,
            GameEntity explicitOwner)
        {
            // a NaN or an infinity from the game's math must not reach the collision world or Jolt's native character (#455):
            // a non-finite move is no move this frame; a non-finite position or capsule skips the step (feet come back as given)
            if (!IsFinite(move)) { WarnNonFiniteMove(selfId, "move " + move); move = Vortex.Vector3.Zero; }
            if (!IsFinite(feet) || !float.IsFinite(radius) || !float.IsFinite(height))
            {
                WarnNonFiniteMove(selfId, IsFinite(feet) ? "capsule (radius " + radius.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", height " + height.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : "feet position " + feet);
                grounded = false;
                return feet;
            }
            var f = new Editor.ECS.Vector3(feet.X, feet.Y, feet.Z);
            var m = new Editor.ECS.Vector3(move.X, move.Y, move.Z);
            var owner = explicitOwner ?? CharacterOwner(selfId);   // null: the services keep the owner the id already has
            Editor.ECS.Vector3 r;
            if (Editor.Core.Services.Physics.PhysicsService.JoltCharacters)   // #187: the scene opted into Jolt's CharacterVirtual
                r = Editor.Core.Services.Physics.PhysicsService.MoveCharacterJolt(f, radius, height, m, Vortex.Time.DeltaTime, out grounded, selfId,
                    Editor.Core.Services.Physics.CollisionService.CharacterStepHeight, Editor.Core.Services.Physics.CollisionService.CharacterMaxSlopeDeg, owner);
            else
                r = Editor.Core.Services.Physics.CollisionService.MoveCharacter(f, radius, height, m, out grounded, selfId, owner: owner);
            return new Vortex.Vector3(r.X, r.Y, r.Z);
        }

        private static bool IsFinite(Vortex.Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private readonly HashSet<long> _nonFiniteMoveWarned = new HashSet<long>();   // characters warned about this run (#455)

        private void WarnNonFiniteMove(long selfId, string what)
        {
            if (!_nonFiniteMoveWarned.Add(selfId) || !TakeNonFiniteWarning()) return;
            try
            {
                var e = FindEntityByHandle(selfId);
                string writer = ScriptFrameOfCaller();
                Editor.Core.Services.ConsoleService.Instance.LogWarning("Physics.MoveCharacter: ignored a non-finite " + what
                    + (e != null ? " for '" + e.Name + "'" : selfId != 0 ? " for character " + selfId : "") + (writer != null ? ", called by " + writer : "")
                    + " — the character does not move this frame. (Shown once per character.)");
            }
            catch { }
        }

        string Vortex.IScriptHost.GroundTag(Vortex.Vector3 origin, float maxDist)
        {
            var o = new Editor.ECS.Vector3(origin.X, origin.Y, origin.Z);
            Editor.ECS.Vector3 hit; string tag;
            return Editor.Core.Services.Physics.CollisionService.RaycastDown(o, maxDist, out hit, out tag) ? (tag ?? "") : "";
        }

        string Vortex.IScriptHost.GroundMaterial(Vortex.Vector3 origin, float maxDist)
        {
            var o = new Editor.ECS.Vector3(origin.X, origin.Y, origin.Z);
            Editor.ECS.Vector3 hit; string mat;
            return Editor.Core.Services.Physics.CollisionService.RaycastDownMaterial(o, maxDist, out hit, out mat) ? (mat ?? "") : "";
        }

        string Vortex.IScriptHost.GroundStepSound(Vortex.Vector3 origin, float maxDist)
        {
            var o = new Editor.ECS.Vector3(origin.X, origin.Y, origin.Z);
            Editor.ECS.Vector3 hit; string step;
            return Editor.Core.Services.Physics.CollisionService.RaycastDownStepSound(o, maxDist, out hit, out step) ? (step ?? "") : "";
        }

        // ---- Rigid-body physics (#100): script handle -> entity -> PhysicsService (no-ops without Jolt) ----

        private static Editor.ECS.Vector3 E(Vortex.Vector3 v) => new Editor.ECS.Vector3(v.X, v.Y, v.Z);
        private static Vortex.Vector3 S(Editor.ECS.Vector3 v) => new Vortex.Vector3(v.X, v.Y, v.Z);

        bool Vortex.IScriptHost.PhysicsAddForce(long entityId, Vortex.Vector3 force)
            => Editor.Core.Services.Physics.PhysicsService.AddForce(FindEntityByHandle(entityId), E(force));
        bool Vortex.IScriptHost.PhysicsAddForceAtPoint(long entityId, Vortex.Vector3 force, Vortex.Vector3 worldPoint)
            => Editor.Core.Services.Physics.PhysicsService.AddForceAtPoint(FindEntityByHandle(entityId), E(force), E(worldPoint));
        bool Vortex.IScriptHost.PhysicsAddImpulse(long entityId, Vortex.Vector3 impulse)
            => Editor.Core.Services.Physics.PhysicsService.AddImpulse(FindEntityByHandle(entityId), E(impulse));
        bool Vortex.IScriptHost.PhysicsAddImpulseAtPoint(long entityId, Vortex.Vector3 impulse, Vortex.Vector3 worldPoint)
            => Editor.Core.Services.Physics.PhysicsService.AddImpulseAtPoint(FindEntityByHandle(entityId), E(impulse), E(worldPoint));
        bool Vortex.IScriptHost.PhysicsAddTorque(long entityId, Vortex.Vector3 torque)
            => Editor.Core.Services.Physics.PhysicsService.AddTorque(FindEntityByHandle(entityId), E(torque));
        bool Vortex.IScriptHost.PhysicsSetVelocity(long entityId, Vortex.Vector3 velocity)
            => Editor.Core.Services.Physics.PhysicsService.SetVelocity(FindEntityByHandle(entityId), E(velocity));
        Vortex.Vector3 Vortex.IScriptHost.PhysicsGetVelocity(long entityId)
            => S(Editor.Core.Services.Physics.PhysicsService.GetVelocity(FindEntityByHandle(entityId)));
        bool Vortex.IScriptHost.PhysicsSetAngularVelocity(long entityId, Vortex.Vector3 velocity)
            => Editor.Core.Services.Physics.PhysicsService.SetAngularVelocity(FindEntityByHandle(entityId), E(velocity));
        Vortex.Vector3 Vortex.IScriptHost.PhysicsGetAngularVelocity(long entityId)
            => S(Editor.Core.Services.Physics.PhysicsService.GetAngularVelocity(FindEntityByHandle(entityId)));
        bool Vortex.IScriptHost.PhysicsSetKinematic(long entityId, bool kinematic)
            => Editor.Core.Services.Physics.PhysicsService.SetKinematic(FindEntityByHandle(entityId), kinematic);
        bool Vortex.IScriptHost.PhysicsWakeUp(long entityId)
            => Editor.Core.Services.Physics.PhysicsService.WakeUp(FindEntityByHandle(entityId));
        bool Vortex.IScriptHost.PhysicsIsSleeping(long entityId)
            => Editor.Core.Services.Physics.PhysicsService.IsSleeping(FindEntityByHandle(entityId));
        void Vortex.IScriptHost.PhysicsSetGravity(Vortex.Vector3 gravity)
            => Editor.Core.Services.Physics.PhysicsService.Gravity = E(gravity);
        long[] Vortex.IScriptHost.PhysicsOverlapSphere(Vortex.Vector3 center, float radius)
        {
            var hits = Editor.Core.Services.Physics.PhysicsService.OverlapSphere(E(center), radius);
            var result = new long[hits.Count];
            for (int i = 0; i < hits.Count; i++) result[i] = HandleForEntity(hits[i]);
            return result;
        }
        bool Vortex.IScriptHost.PhysicsHasRigidbody(long entityId)
        {
            var e = FindEntityByHandle(entityId);
            if (e == null) return false;
            if (Editor.Core.Services.Physics.PhysicsService.IsBuilt) return Editor.Core.Services.Physics.PhysicsService.HasRigidbody(e);
            var rb = e.GetComponent<Editor.ECS.Components.Physics.Rigidbody>();   // stub build: answer from the component
            return rb != null && rb.IsEnabled && rb.BodyType != Editor.ECS.Components.Physics.RigidbodyType.Static;
        }
        float Vortex.IScriptHost.PhysicsGetMass(long entityId)
            => Editor.Core.Services.Physics.PhysicsService.GetMass(FindEntityByHandle(entityId));

        /// <summary>Debug freecam owns the input: when the engine's debug free-fly camera is flying (editor play,
        /// debug builds only), gameplay keys are suppressed so WASD flies the camera instead of the player. Holding
        /// RMB clears it so control passes back to the player while the freecam watches.</summary>
        public static bool SuppressGameplayInput;

        bool Vortex.IScriptHost.GetKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (SuppressGameplayInput) return false;
            // Freeze gameplay movement keys while a screen that OPTED IN (BlocksGameplay checkbox in the UI editor)
            // is up — e.g. a chest/inventory. A hotbar/HUD leaves it off, so the player keeps moving. (Mouse-look is
            // gated the same way in Vortex.Input.MouseDeltaX/Y.)
            if (Editor.UI.Vui.VuiStack.Instance.GameplayInputBlocked) return false;
            // Mouse buttons are NOT in the WPF Key enum, so Enum.TryParse("RButton") fails and Input.GetKey("RButton")
            // always returned false (ADS/right-click never worked). Map the mouse-button names to Win32 VK codes.
            int mvk = 0;
            if (key == "LButton" || key == "Mouse0" || key == "LeftMouse") mvk = 0x01;
            else if (key == "RButton" || key == "Mouse1" || key == "RightMouse") mvk = 0x02;
            else if (key == "MButton" || key == "Mouse2" || key == "MiddleMouse") mvk = 0x04;
            // Shared core: the host (native GameHost / editor shell) answers physical key state; key names are
            // the WPF Key enum names the scripts were written against, mapped to virtual-key codes.
            int vk = mvk != 0 ? mvk : Editor.Core.Input.KeyNames.VirtualKeyFromName(key);
            if (vk == 0) { WarnUnknownKey(key); return false; }
            // GetAsyncKeyState reads the GLOBAL key state (needed because focus is on a native swapchain HWND), so it
            // would fire even when our game is in the background — gate it on our window actually being focused. Simulated
            // input (send_input, --input) is not physical: it drives the game with another app in front (#457).
            if (!Vortex.Input.WindowFocused) return Editor.Core.Input.HostInput.Simulated.IsDown(vk);
            if (mvk != 0) return Editor.Core.Input.HostInput.IsKeyDown(mvk);
            return Editor.Core.Input.HostInput.IsKeyDownThisTick(vk);   // incl. a tap shorter than one frame (#337)
        }

        // ---- key edges (#337): GetKeyDown / GetKeyUp without hand-rolled edge detection. Per-tick cache so every
        // call in one tick agrees; the tick advances at the start of Update.
        private struct KeyEdgeState { public int Tick; public bool Now, Prev; }
        private readonly Dictionary<string, KeyEdgeState> _keyEdges = new Dictionary<string, KeyEdgeState>(StringComparer.OrdinalIgnoreCase);
        private int _keyTick = 1;

        private KeyEdgeState KeyEdge(string key)
        {
            KeyEdgeState s;
            if (string.IsNullOrEmpty(key)) return new KeyEdgeState();
            if (_keyEdges.TryGetValue(key, out s) && s.Tick == _keyTick) return s;
            bool prev = s.Tick != 0 && s.Now;   // the last tick this key was looked at
            s = new KeyEdgeState { Tick = _keyTick, Now = ((Vortex.IScriptHost)this).GetKey(key), Prev = prev };
            _keyEdges[key] = s;
            return s;
        }

        bool Vortex.IScriptHost.GetKeyDown(string key) { var e = KeyEdge(key); return e.Now && !e.Prev; }
        bool Vortex.IScriptHost.GetKeyUp(string key) { var e = KeyEdge(key); return !e.Now && e.Prev; }

        /// <summary>An unknown key name used to fail silently (#336) — say so once per name.</summary>
        private static readonly HashSet<string> _warnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static void WarnUnknownKey(string key)
        {
            lock (_warnedKeys) { if (!_warnedKeys.Add(key)) return; }
            try
            {
                Editor.Core.Services.ConsoleService.Instance.LogWarning("Input.GetKey: unknown key name \"" + key +
                    "\" — use W, Space, LeftShift, Num1 / Alpha1 / \"1\", Keypad1, F5, Left, LButton, … (the KeyCode names)");
            }
            catch { }
        }
    }
}
