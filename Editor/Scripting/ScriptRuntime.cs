using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Scripting;

namespace Editor.Scripting
{
    /// <summary>
    /// In-engine gameplay scripting runtime — the Unity/Unreal-style "compile + run" loop. On Play it
    /// compiles every Assets/Scripts/*.cs (via the in-box C# compiler, no extra dependencies) into one
    /// assembly, instantiates the VortexBehaviour attached to each entity through its Script component,
    /// and calls Start once + Update every tick (OnDestroy on Stop). Behaviours affect the live game
    /// through this class (it is the IScriptHost: move entities, read input).
    /// </summary>
    public sealed class ScriptRuntime : Vortex.IScriptHost
    {
        private static ScriptRuntime _instance;
        public static ScriptRuntime Instance => _instance ?? (_instance = new ScriptRuntime());

        // Maps a UNIQUE per-behaviour handle -> the entity it's attached to. We assign our own handle
        // (not the engine EntityId) because engine ids can collide/be invalid for editor entities; a
        // collision previously made a script move the WRONG entity (e.g. the Ground instead of the camera).
        private readonly Dictionary<long, GameEntity> _entitiesById = new Dictionary<long, GameEntity>();
        private long _nextHandle;
        private readonly List<Vortex.VortexBehaviour> _behaviours = new List<Vortex.VortexBehaviour>();
        // Reverse maps so collision/trigger events can be dispatched to the right behaviour.
        private readonly Dictionary<long, Vortex.VortexBehaviour> _behavioursByHandle = new Dictionary<long, Vortex.VortexBehaviour>();
        private readonly Dictionary<GameEntity, Vortex.VortexBehaviour> _behavioursByEntity = new Dictionary<GameEntity, Vortex.VortexBehaviour>();
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

        internal Vortex.VortexBehaviour BehaviourOf(long id)
        {
            if (_behavioursByHandle.TryGetValue(id, out var b) && b != null) return b;
            var e = FindEntityByHandle(id);
            return e != null && _behavioursByEntity.TryGetValue(e, out var b2) ? b2 : null;
        }

        internal bool SetEntityActive(long id, bool active)
        {
            var e = FindEntityByHandle(id);
            if (e == null) return false;
            if (e.IsActive == active) return true;   // no-op guard: a re-add would duplicate collision shapes
            if (!_activeChanged.ContainsKey(e)) _activeChanged[e] = e.IsActive;   // restore on play end
            e.IsActive = active;
            try { e.SyncEngineStateRecursive(active); } catch { }
            // #51: collision + audio follow the active state (previously a documented limitation —
            // a deactivated door/monster stayed solid and kept emitting).
            try
            {
                if (active) Editor.Core.Services.Physics.CollisionService.AddEntityShapes(e);
                else Editor.Core.Services.Physics.CollisionService.RemoveEntityShapes(e);
            }
            catch { }
            if (!active)
            {
                try { StopAudioRecursive(e); } catch { }
            }
            Editor.Core.Services.SceneRenderService.RuntimeDirty = true;
            return true;
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
            Editor.Core.Services.SceneRenderService.RuntimeDirty = true;
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
            var b = BehaviourOf(targetId);
            if (b == null) return;
            try { b.OnMessage(message ?? "", arg); }
            catch (Exception ex) { LogScriptError("OnMessage", b, ex); }
        }

        // ---- coroutines + timers (#37) ----

        private sealed class Co
        {
            public Vortex.VortexBehaviour Owner;
            public System.Collections.IEnumerator Routine;
            public float Wait;
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
            try { more = c.Routine.MoveNext(); }
            catch (Exception ex) { LogScriptError("Coroutine", c.Owner, ex); c.Handle.Done = true; return; }
            if (!more) { c.Handle.Done = true; return; }
            c.Wait = (c.Routine.Current as Vortex.WaitForSeconds)?.Seconds ?? 0f;   // yield return null = one frame
        }

        private void TickCoroutinesAndInvokes(float dt)
        {
            // Snapshot the counts: coroutines/invokes STARTED during this tick already ran their first
            // step (StartCoroutine) or wait a full delay — they must not double-advance this frame.
            int nc = _coroutines.Count;
            for (int i = 0; i < nc; i++)
            {
                var c = _coroutines[i];
                if (c.Handle.Stopped || c.Handle.Done) continue;
                c.Wait -= dt;
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
                try { v.Action(); }
                catch (Exception ex) { LogScriptError("Invoke", v.Owner, ex); }
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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] Instantiate failed: " + ex.Message); }
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
            Editor.Core.Services.SceneRenderService.RuntimeDirty = true;
            try { Editor.Core.Services.Physics.CollisionService.AddEntityShapes(ent); } catch { }

            // Its Script components come alive immediately — a spawned monster thinks from THIS frame.
            if (_scriptAsm != null)
            {
                int before = _behaviours.Count;
                InstantiateRecursive(ent, _scriptAsm);
                for (int i = before; i < _behaviours.Count; i++)
                {
                    try { _behaviours[i].Start(); }
                    catch (Exception ex) { LogScriptError("Start", _behaviours[i], ex); }
                }
            }
            return handle;
        }

        internal bool DestroyEntity(long id)
        {
            var e = FindEntityByHandle(id);
            if (e == null || _currentScene == null) return false;

            // OnDestroy + unregister every behaviour in the subtree (and their coroutines/timers).
            var doomed = new List<Vortex.VortexBehaviour>();
            void Collect(GameEntity x)
            {
                if (x == null) return;
                if (_behavioursByEntity.TryGetValue(x, out var b) && b != null) doomed.Add(b);
                if (x.Children != null) foreach (var c in x.Children) Collect(c);
            }
            Collect(e);
            foreach (var b in doomed)
            {
                try { b.OnDestroy(); } catch (Exception ex) { LogScriptError("OnDestroy", b, ex); }
                _behaviours.Remove(b);
                _behavioursByHandle.Remove(b.EntityId);
                var be = FindEntityByHandle(b.EntityId);
                if (be != null) _behavioursByEntity.Remove(be);
                StopAllCoroutines(b);
                CancelInvokes(b);
            }

            try { Editor.Core.Services.Physics.CollisionService.RemoveEntityShapes(e); } catch { }
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

            // Release the subtree's script handles — a stale id must resolve to nothing, not a ghost.
            void Release(GameEntity x)
            {
                if (x == null) return;
                if (_handlesByEntity.TryGetValue(x, out var h)) { _handlesByEntity.Remove(x); _entitiesById.Remove(h); }
                if (x.Children != null) foreach (var c in x.Children) Release(c);
            }
            Release(e);

            Editor.Core.Services.SceneRenderService.RuntimeDirty = true;
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
                    r.Entity.SyncEngineStateRecursive(r.Entity.IsActive);
                }
                catch { }
            }
            _removed.Clear();

            foreach (var kv in _activeChanged)
            {
                try { kv.Key.IsActive = kv.Value; kv.Key.SyncEngineStateRecursive(kv.Value); } catch { }
            }
            _activeChanged.Clear();

            Editor.Core.Services.SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>Last compile diagnostics (empty on success) — surfaced to the user.</summary>
        public string LastBuildLog { get; private set; } = "";

        /// <summary>Compile the project's scripts and start every attached behaviour.</summary>
        /// <summary>When set (by the standalone player), gameplay runs from this PRE-COMPILED assembly
        /// instead of compiling source at startup — fast boot + no .cs source shipped with the game.</summary>
        public Assembly PrecompiledAssembly { get; set; }

        public void Begin(Scene scene) { Begin(scene, null); }

        /// <summary>(Re)start the scripts on <paramref name="scene"/>. <paramref name="overrideAsm"/> lets the dev
        /// hot-reload inject a freshly-compiled assembly instead of re-reading disk.</summary>
        public void Begin(Scene scene, System.Reflection.Assembly overrideAsm)
        {
            End();
            if (scene?.Entities == null) return;
            _currentScene = scene;   // remembered so hot-reload can re-begin the same scene

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

            // Scripted scene-atmosphere state starts clean each run (a previous run's ambient/fog must not leak).
            Editor.Core.Services.SceneRenderService.ScriptAmbientOverride = null;
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
            try { Editor.Core.Services.Physics.CollisionService.Build(scene); } catch { } // build the collision world for this scene

            _entitiesById.Clear();
            _behavioursByHandle.Clear();
            _behavioursByEntity.Clear();
            _nextHandle = 0;

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
            if (asm == null) { _active = true; return; } // no scripts / compile failed -> nothing to run

            foreach (var e in scene.Entities) InstantiateRecursive(e, asm);

            _active = true;
            // Snapshot the count: a Start() that calls Scene.Instantiate appends new behaviours which
            // are ALREADY Start()ed by InstantiatePrefabAt — walking the live count would start them twice.
            int startCount = _behaviours.Count;
            for (int i = 0; i < startCount; i++)
            {
                try { _behaviours[i].Start(); }
                catch (Exception ex) { LogScriptError("Start", _behaviours[i], ex); }
            }
        }

        /// <summary>Report a script exception to BOTH the VS debug output and the editor's Console panel (so the user
        /// sees which script threw, in which phase, and why — the game's errors surface where the game runs).</summary>
        private static string _lastErrKey;
        private static DateTime _lastErrTime;
        private static void LogScriptError(string phase, object behaviour, Exception ex)
        {
            var who = behaviour?.GetType().Name ?? "Script";
            var msg = who + "." + phase + "(): " + (ex?.InnerException?.Message ?? ex?.Message ?? "error");
            // Throttle a per-frame-repeating exception (a script throwing every Update) so it can't flood the Console
            // ~60×/sec and freeze the UI. The Debug.WriteLine sits BEHIND the throttle too: with a VS debugger
            // attached every write is a ~1ms cross-process round-trip — unthrottled it alone tanked F5 FPS.
            var now = DateTime.UtcNow;
            if (msg == _lastErrKey && (now - _lastErrTime).TotalSeconds < 1.0) return;
            _lastErrKey = msg; _lastErrTime = now;
            System.Diagnostics.Debug.WriteLine("[ScriptRuntime] " + msg);
            try { Editor.Core.Services.ConsoleService.Instance.LogError(msg); } catch { }
        }

        /// <summary>Tick every running behaviour.</summary>
        public void Update(float dt)
        {
            if (!_active) return;
            Vortex.Time.DeltaTime = dt;
            Vortex.Input.PollGamepad();   // refresh controller state once per tick (scripts read Input.LeftStickX etc.)
            for (int i = 0; i < _behaviours.Count; i++)
            {
                try { _behaviours[i].Update(dt); }
                catch (Exception ex) { LogScriptError("Update", _behaviours[i], ex); }
            }
            // Auto-instantiated UI action controllers tick too, so a screen's class can drive its own widgets.
            foreach (var b in _uiActions.Values)
            {
                if (b == null) continue;
                try { b.Update(dt); }
                catch (Exception ex) { LogScriptError("UI Update", b, ex); }
            }

            // Coroutines + Invoke timers (#37) advance after behaviours, so a coroutine resumed this
            // frame sees the world the behaviours just built (and PlayAnimation from it applies below).
            TickCoroutinesAndInvokes(dt);

            // Debug draw + dev console (#42): re-submit live wire shapes, then the console overlay on top.
            SubmitDebugShapes(dt);
            RenderDebugConsole();

            // Skeletal animation: advance every Animator AFTER behaviours ran, so a same-frame
            // PlayAnimation() takes effect immediately. This is the one tick all three play drivers share.
            // Bone sockets apply right after — animation -> sockets -> (submit reads final transforms).
            try
            {
                Editor.Core.Services.CameraFXService.Instance.Step(dt);   // springs/noise BEFORE sockets read them
                Editor.Core.Animation.AnimationService.Instance.Step(_currentScene, dt);
                Editor.Core.Animation.BoneSocketService.Instance.Apply(_currentScene);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] Animation step error: " + ex.Message); }

            // Collision/trigger events fire AFTER everyone moved this tick, so overlaps are tested at final positions.
            DispatchCollisionEvents();
        }

        private bool _animEventsHooked;

        /// <summary>Route an AnimEvent marker (footstep, attack hit, ...) to the entity's behaviour.</summary>
        private void OnAnimationServiceEvent(GameEntity entity, string name)
        {
            if (entity == null) return;
            if (_behavioursByEntity.TryGetValue(entity, out var b) && b != null)
            {
                try { b.OnAnimationEvent(name); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] OnAnimationEvent error: " + ex.Message); }
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

            _behavioursByHandle.TryGetValue(c.CharacterId, out var charBeh);
            _behavioursByEntity.TryGetValue(owner, out var ownerBeh);

            // Fire on the collider OWNER's behaviour (other = the character that touched it).
            if (ownerBeh != null)
            {
                var hit = new Vortex.TriggerHit(c.CharacterId, charEnt?.Name, EntityTag(charEnt));
                Invoke(ownerBeh, kind, hit);
            }
            // And on the touching CHARACTER's behaviour (other = the collider owner) so either side can react.
            if (charBeh != null)
            {
                var hit = new Vortex.TriggerHit(ownerBeh?.EntityId ?? 0, owner.Name, EntityTag(owner));
                Invoke(charBeh, kind, hit);
            }
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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] " + kind + " error: " + ex.Message); }
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
                try { m.Invoke(target, null); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[UIAction] " + a.Action + ": " + ex.Message); }
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
                    if (string.Equals(b.GetType().Name, cls, StringComparison.Ordinal) && GetParamlessMethod(b.GetType(), action) != null)
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
                if (GetParamlessMethod(b.GetType(), action) != null) return b;
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
                    if (string.Equals(b.GetType().Name, cls, StringComparison.Ordinal)) { attached = true; break; }
                if (!attached && _scriptAsm != null)
                {
                    var type = FindBehaviourType(_scriptAsm, cls);
                    if (type != null)
                    {
                        inst = (Vortex.VortexBehaviour)Activator.CreateInstance(type);
                        inst.EntityId = 0; // no scene entity -> Position/Rotation read as zero; UI action classes don't use them
                        try { inst.Start(); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[UIAction] Start " + cls + ": " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[UIAction] create " + cls + ": " + ex.Message); }
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
            for (int i = 0; i < _behaviours.Count; i++)
            {
                try { _behaviours[i].OnDestroy(); } catch { }
            }
            foreach (var b in _uiActions.Values) { if (b != null) try { b.OnDestroy(); } catch { } }
            // Scripting-wave teardown (#36-#40): undo runtime scene mutations, stop timers/coroutines,
            // drop event subscriptions, persist pending save data.
            try { RollbackRuntimeSceneChanges(); } catch { }
            _coroutines.Clear();
            _invokes.Clear();
            _debugShapes.Clear();
            Vortex.Debug.ConsoleVisible = false;
            try { Vortex.Debug.ClearLines(); } catch { }
            try { Vortex.Events.Clear(); } catch { }
            try { Vortex.Scene.ResetHooks(); } catch { }
            try { Vortex.Save.Flush(); } catch { }
            _uiActions.Clear();
            _behaviours.Clear();
            _entitiesById.Clear();
            _handlesByEntity.Clear();
            _behavioursByHandle.Clear();
            _behavioursByEntity.Clear();
            try { Editor.Core.Services.Physics.CollisionService.ResetEvents(); Editor.Core.Services.Physics.CollisionService.ClearCharacters(); } catch { }
            try { Editor.Core.Animation.AnimationService.Instance.ResetStates(); } catch { }
            try { Editor.Core.Animation.BoneSocketService.Instance.ResetRuntime(); } catch { }
            try { Editor.Core.Services.CameraFXService.Instance.Reset(); } catch { }
            // Return the scene atmosphere to editor defaults: scripted ambient stops overriding and any
            // scripted fog is switched off — otherwise the horror scene's darkness/fog sticks to the
            // EDITOR viewport after leaving play mode (the fog CB is persistent frame state).
            Editor.Core.Services.SceneRenderService.ScriptAmbientOverride = null;
            try { Editor.DllWrapper.VortexAPI.SetFog(0f, 0f, 0f, 0f, 0f, 0f); } catch { }
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
            var script = e.GetComponent<Script>();
            if (script != null && !string.IsNullOrEmpty(script.ScriptClassName))
            {
                var type = FindBehaviourType(asm, script.ScriptClassName);
                if (type != null)
                {
                    try
                    {
                        var behaviour = (Vortex.VortexBehaviour)Activator.CreateInstance(type);
                        ApplySerializedFields(behaviour, script);   // #47: per-instance field overrides, before Start()
                        // Assign a UNIQUE handle and map it to THIS entity — guarantees the behaviour can
                        // only ever read/move its own entity (no engine-EntityId collisions).
                        long handle = ++_nextHandle;
                        behaviour.EntityId = handle;
                        _entitiesById[handle] = e;
                        _handlesByEntity[e] = handle;   // the behaviour handle IS the entity's canonical handle
                        _behaviours.Add(behaviour);
                        _behavioursByHandle[handle] = behaviour;
                        _behavioursByEntity[e] = behaviour;
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] instantiate '" + script.ScriptClassName + "' failed: " + ex.Message); }
                }
            }
            if (e.Children != null)
                foreach (var c in e.Children) InstantiateRecursive(c, asm);
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
        public static bool IsInspectableFieldType(Type t)
            => t == typeof(int) || t == typeof(float) || t == typeof(bool) || t == typeof(string)
               || t.IsEnum || t == typeof(Vortex.Vector3);

        /// <summary>Invariant-culture round-trip of a field value ("1,2,3" for Vector3; enum by name).</summary>
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
            return v.ToString();
        }

        /// <summary>Parse a stored string back into the REFLECTED field type. Throws on garbage — the
        /// callers skip-and-log so one bad value can't take a scene down.</summary>
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

        // Edit-mode reflection assembly for the inspector (#47): compiled on demand, cached by the
        // newest script write time. A running play session's assembly is the freshest truth.
        private Assembly _reflectAsm;
        private DateTime _reflectAsmTime = DateTime.MinValue;

        /// <summary>The behaviour TYPE for a script class, usable in EDIT mode (the inspector reflects
        /// its public fields). Compiles the project scripts on first use / after a script save; returns
        /// null while the scripts don't compile (the inspector shows a hint instead of rows).</summary>
        public Type GetScriptTypeForInspector(string className)
        {
            if (string.IsNullOrEmpty(className)) return null;
            try
            {
                if (_scriptAsm != null) return FindBehaviourType(_scriptAsm, className);
                var newest = LatestScriptWrite();
                if (_reflectAsm == null || newest > _reflectAsmTime)
                {
                    _reflectAsm = Compile(out _);
                    _reflectAsmTime = newest;
                }
                return _reflectAsm != null ? FindBehaviourType(_reflectAsm, className) : null;
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

            try
            {
                using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
                {
                    var p = new CompilerParameters
                    {
                        GenerateInMemory = true,
                        GenerateExecutable = false,
                        TreatWarningsAsErrors = false
                    };
                    p.ReferencedAssemblies.Add("mscorlib.dll");
                    p.ReferencedAssemblies.Add("System.dll");
                    p.ReferencedAssemblies.Add("System.Core.dll");
                    // Reference this editor assembly so scripts get the real Vortex.* API + shared types.
                    p.ReferencedAssemblies.Add(typeof(Vortex.VortexBehaviour).Assembly.Location);

                    CompilerResults results = provider.CompileAssemblyFromFile(p, files);
                    if (results.Errors.HasErrors)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (CompilerError err in results.Errors)
                            if (!err.IsWarning)
                                sb.AppendLine($"{Path.GetFileName(err.FileName)}({err.Line}): {err.ErrorText}");
                        log = "Script compile failed:\n" + sb;
                        return null;
                    }
                    return results.CompiledAssembly;
                }
            }
            catch (Exception ex)
            {
                log = "Script compile exception: " + ex.Message;
                return null;
            }
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

        void Vortex.IScriptHost.SetEntityColor(long entityId, float r, float g, float b)
        {
            if (_entitiesById.TryGetValue(entityId, out var e))
                Editor.Core.Services.SceneRenderService.Instance.SetEntityColor(e, r, g, b, 1f);
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

        long[] Vortex.IScriptHost.GetAttachedEntities(long targetId)
        {
            if (!_entitiesById.TryGetValue(targetId, out var e)) return new long[0];
            var list = Editor.Core.Animation.BoneSocketService.Instance.GetAttachedTo(e);
            var result = new long[list.Count];
            for (int i = 0; i < list.Count; i++) result[i] = HandleForEntity(list[i]);
            return result;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // --- deferred scene-switch request (set by a script via Vortex.Scene.Load; applied by the driver) ---
        private string _pendingScene;
        void Vortex.IScriptHost.LoadScene(string name) { _pendingScene = name; }
        /// <summary>Returns the requested scene name (and clears it), or null. The runtime driver calls this
        /// AFTER Update() and performs the switch (so it never happens mid-tick).</summary>
        public string ConsumePendingScene() { var s = _pendingScene; _pendingScene = null; return s; }
        public string PendingScene { get { return _pendingScene; } } // diagnostic peek

        // --- mouse mode (game-controlled via Vortex.Cursor; the GameWindow enforces capture) ---
        private bool _cursorLocked; // default false: free cursor (menus/lobby) until a script locks it
        void Vortex.IScriptHost.SetCursorLocked(bool locked) { _cursorLocked = locked; }
        bool Vortex.IScriptHost.GetCursorLocked() { return _cursorLocked; }
        public bool CursorLocked { get { return _cursorLocked; } }

        // --- UI overlay frame state (fed by the runtime driver each frame: GameWindow / GamePreview) ---
        private float _uiW, _uiH, _mouseX, _mouseY;
        private bool _mouseDown, _mousePressed;

        /// <summary>Called once per frame before Update() so scripts can draw UI + hit-test the mouse.</summary>
        public void SetUIFrame(float width, float height, float mouseX, float mouseY, bool mouseDown, bool mousePressed)
        {
            _uiW = width; _uiH = height; _mouseX = mouseX; _mouseY = mouseY;
            _mouseDown = mouseDown; _mousePressed = mousePressed;
        }

        void Vortex.IScriptHost.UIRect(float x, float y, float w, float h, float r, float g, float b, float a, float radius)
            => Editor.DllWrapper.VortexAPI.UIRect(x, y, w, h, r, g, b, a, radius);
        void Vortex.IScriptHost.UIText(float x, float y, float w, float h, string text, float size, float r, float g, float b, float a, int align, int weight)
            => Editor.DllWrapper.VortexAPI.UIText(x, y, w, h, text, size, r, g, b, a, align, weight);
        void Vortex.IScriptHost.UILine(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick)
            => Editor.DllWrapper.VortexAPI.UILine(x1, y1, x2, y2, r, g, b, a, thick);
        void Vortex.IScriptHost.UIImage(float x, float y, float w, float h, string path, float r, float g, float b, float a)
            => Editor.DllWrapper.VortexAPI.UIImage(x, y, w, h, path, r, g, b, a);
        float Vortex.IScriptHost.UIWidth() => _uiW;
        float Vortex.IScriptHost.UIHeight() => _uiH;
        float Vortex.IScriptHost.UIMouseX() => _mouseX;
        float Vortex.IScriptHost.UIMouseY() => _mouseY;
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
                else
                    Editor.Core.Services.PlayModeService.Instance.Stop(); // in-editor play: just stop, don't kill the editor
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
        private static float[] ResolveMeshTriangles(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath) || meshPath.StartsWith("Primitive:", System.StringComparison.OrdinalIgnoreCase)) return null;
            float[] cached; if (_triCache.TryGetValue(meshPath, out cached)) return cached;
            float[] tris = null;
            try
            {
                var actual = meshPath; int h = actual.LastIndexOf('#'); if (h > 0) actual = actual.Substring(0, h);
                var proj = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.Path : null;
                var abs = System.IO.Path.IsPathRooted(actual) ? actual : (proj != null ? System.IO.Path.Combine(proj, actual) : actual);
                var ext = System.IO.Path.GetExtension(actual); if (ext != null) ext = ext.TrimStart('.');
                byte[] bytes;
                if (Editor.Core.Services.AssetVfs.IsMounted && Editor.Core.Services.AssetVfs.TryGetBytes(abs, out bytes) && bytes != null)
                    tris = Editor.DllWrapper.VortexAPI.GetModelTrianglesFromMemory(bytes, ext);
                else if (System.IO.File.Exists(abs))
                    tris = Editor.DllWrapper.VortexAPI.GetModelTriangles(abs);
            }
            catch { }
            _triCache[meshPath] = tris;
            return tris;
        }

        Vortex.Vector3 Vortex.IScriptHost.MoveCharacter(Vortex.Vector3 feet, float radius, float height, Vortex.Vector3 move, out bool grounded, long selfId)
        {
            var f = new Editor.ECS.Vector3(feet.X, feet.Y, feet.Z);
            var m = new Editor.ECS.Vector3(move.X, move.Y, move.Z);
            var r = Editor.Core.Services.Physics.CollisionService.MoveCharacter(f, radius, height, m, out grounded, selfId);
            return new Vortex.Vector3(r.X, r.Y, r.Z);
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

        bool Vortex.IScriptHost.GetKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            // Freeze gameplay movement keys while a screen that OPTED IN (BlocksGameplay checkbox in the UI editor)
            // is up — e.g. a chest/inventory. A hotbar/HUD leaves it off, so the player keeps moving. (Mouse-look is
            // gated the same way in Vortex.Input.MouseDeltaX/Y.)
            if (Editor.UI.Vui.VuiStack.Instance.GameplayInputBlocked) return false;
            // GetAsyncKeyState reads the GLOBAL key state (needed because focus is on a native swapchain HWND), so it
            // would fire even when our game is in the background — gate it on our window actually being focused.
            if (!Vortex.Input.WindowFocused) return false;
            if (!Enum.TryParse(key, true, out Key k)) return false;
            // Use the global physical key state (not WPF Keyboard.IsKeyDown): while playing, focus is on
            // a native swapchain HWND (editor viewport or the standalone game window), where the WPF
            // keyboard device reports nothing — so WASD/jump would do nothing. GetAsyncKeyState works
            // regardless of which window/HWND has focus.
            int vk = KeyInterop.VirtualKeyFromKey(k);
            if (vk == 0) return false;
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }
    }
}
