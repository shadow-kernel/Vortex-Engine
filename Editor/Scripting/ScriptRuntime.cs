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

            // Fresh Animator playback states for this run; animation-event markers route to OnAnimationEvent.
            Editor.Core.Animation.AnimationService.Instance.ResetStates();
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
            _scriptAsm = asm;
            if (asm == null) { _active = true; return; } // no scripts / compile failed -> nothing to run

            foreach (var e in scene.Entities) InstantiateRecursive(e, asm);

            _active = true;
            for (int i = 0; i < _behaviours.Count; i++)
            {
                try { _behaviours[i].Start(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] Start error: " + ex.Message); }
            }
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
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] Update error: " + ex.Message); }
            }
            // Auto-instantiated UI action controllers tick too, so a screen's class can drive its own widgets.
            foreach (var b in _uiActions.Values)
            {
                if (b == null) continue;
                try { b.Update(dt); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] UI Update error: " + ex.Message); }
            }

            // Skeletal animation: advance every Animator AFTER behaviours ran, so a same-frame
            // PlayAnimation() takes effect immediately. This is the one tick all three play drivers share.
            try { Editor.Core.Animation.AnimationService.Instance.Step(_currentScene, dt); }
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
            _uiActions.Clear();
            _behaviours.Clear();
            _entitiesById.Clear();
            _behavioursByHandle.Clear();
            _behavioursByEntity.Clear();
            try { Editor.Core.Services.Physics.CollisionService.ResetEvents(); Editor.Core.Services.Physics.CollisionService.ClearCharacters(); } catch { }
            try { Editor.Core.Animation.AnimationService.Instance.ResetStates(); } catch { }
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
                        // Assign a UNIQUE handle and map it to THIS entity — guarantees the behaviour can
                        // only ever read/move its own entity (no engine-EntityId collisions).
                        long handle = ++_nextHandle;
                        behaviour.EntityId = handle;
                        _entitiesById[handle] = e;
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
