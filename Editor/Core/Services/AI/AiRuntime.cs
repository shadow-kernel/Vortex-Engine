using System;
using System.Collections;
using Editor.Core.Data;
using Editor.Scripting;

namespace Editor.Core.Services.AI
{
    /// <summary>
    /// The play-mode lifecycle of the AI services (navigation agents + perception): <see cref="Begin"/> on play start,
    /// <see cref="Tick"/> once per frame after the behaviours' Update, <see cref="End"/> on stop / scene switch — the same
    /// three places <c>PhysicsService</c> is wired into <c>ScriptRuntime</c>. The script runtime is owned by another work
    /// package, so until those hook lines land (see <see cref="HookLines"/>) the runtime starts itself lazily: the first
    /// call of any AI script API (<c>Vortex.Navigation</c> / <c>Vortex.Perception</c> / <c>Vortex.PatrolRoute</c>)
    /// runs <see cref="EnsureRunning"/>, which begins the run and ticks it from a coroutine of the script runtime (after
    /// LateUpdate instead of before the physics step — one frame of camera lag at most). A run change (stop / replay /
    /// scene switch / hot reload) is detected through the script event bus, which the runtime clears on every End().
    /// Once the hooks call <see cref="Begin"/>, the lazy path switches itself off.
    /// </summary>
    public static class AiRuntime
    {
        /// <summary>The exact lines for Editor/Scripting/ScriptRuntime.cs (documentation / the integration report).</summary>
        public const string HookLines =
            "Begin(): after PhysicsService.Build(scene) -> try { Editor.Core.Services.AI.AiRuntime.Begin(scene); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(\"[ScriptRuntime] AI begin failed: \" + ex.Message); }\n" +
            "Update(): before PhysicsService.Step(dt) -> try { Editor.Core.Services.AI.AiRuntime.Tick(dt); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(\"[ScriptRuntime] AI tick error: \" + ex.Message); }\n" +
            "Update(): after PhysicsService.SubmitDebugDraw() -> try { Editor.Core.Services.AI.AiRuntime.SubmitDebugDraw(); } catch { }\n" +
            "End(): next to PhysicsService.Clear() -> try { Editor.Core.Services.AI.AiRuntime.End(); } catch { }";

        private sealed class RunToken { }
        private static readonly RunToken _token = new RunToken();
        private static bool _tokenSeen;
        private static Scene _scene;
        private static Vortex.Coroutine _tick;
        private static int _generation;

        /// <summary>True between Begin and End (either path).</summary>
        public static bool IsRunning { get; private set; }
        /// <summary>True when the script runtime calls Begin / Tick / End itself (the hooks are in place).</summary>
        public static bool ExternallyDriven { get; private set; }
        /// <summary>The scene of the current run.</summary>
        public static Scene Scene => _scene;

        // ------------------------------------------------------------------------------------------ hooks

        /// <summary>Play start (ScriptRuntime.Begin, after the collision + physics worlds were built).</summary>
        public static void Begin(Scene scene)
        {
            ExternallyDriven = true;
            BeginCore(scene);
        }

        /// <summary>One frame (ScriptRuntime.Update, after the behaviours' Update and before PhysicsService.Step).</summary>
        public static void Tick(float dt)
        {
            if (!IsRunning) return;
            TickCore(dt);
        }

        /// <summary>Play stop / scene switch (ScriptRuntime.End).</summary>
        public static void End()
        {
            EndCore();
            ExternallyDriven = false;
        }

        /// <summary>Debug overlays of the running game (navmesh / agent paths / patrol routes / vision cones, each behind
        /// its own toggle). ScriptRuntime.Update, next to PhysicsService.SubmitDebugDraw.</summary>
        public static void SubmitDebugDraw()
        {
            NavigationService.SubmitDebugDraw();
            if (!IsRunning || _scene?.Entities == null) return;
            foreach (var e in _scene.Entities) SubmitPatrolRoutes(e);
        }

        private static void SubmitPatrolRoutes(Editor.ECS.GameEntity e)
        {
            if (e == null || !e.ActiveInHierarchy) return;
            var p = e.GetComponent<Editor.ECS.Components.AI.PatrolPath>();
            if (p != null && p.IsEnabled && p.ShowInGame) PatrolPathService.Submit(p, false);
            if (e.Children != null) foreach (var c in e.Children) SubmitPatrolRoutes(c);
        }

        // ------------------------------------------------------------------------------------------ lazy path

        /// <summary>Start (or restart after a run change) the AI runtime for the running game when the script runtime does
        /// not drive it yet. Called by every AI script API entry point; cheap when already running.</summary>
        public static void EnsureRunning()
        {
            if (ExternallyDriven) return;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) return;
            if (IsRunning && ReferenceEquals(scene, _scene) && TokenAlive()) return;
            BeginCore(scene);
            try
            {
                Vortex.Events.Subscribe<RunToken>(OnToken);
                _tick = ScriptRuntime.Instance.StartCoroutine(null, TickLoop());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[AI] lazy start failed: " + ex.Message); }
        }

        /// <summary>Editor housekeeping: end a lazily started run whose game already stopped (keeps stale agents / paths out
        /// of the edit-mode overlays). No-op when the hooks drive the runtime.</summary>
        public static void EndIfStale()
        {
            if (ExternallyDriven || !IsRunning) return;
            if (!TokenAlive()) EndCore();
        }

        private static bool TokenAlive()
        {
            _tokenSeen = false;
            try { Vortex.Events.Publish(_token); } catch { }
            return _tokenSeen;
        }

        private static void OnToken(RunToken t) { _tokenSeen = true; }

        private static IEnumerator TickLoop()
        {
            int generation = _generation;
            // StartCoroutine runs the first step immediately (inside a script's Start / Update): tick from the next frame.
            yield return null;
            while (IsRunning && !ExternallyDriven && generation == _generation)
            {
                TickCore(Vortex.Time.DeltaTime);
                SubmitDebugDraw();
                yield return null;
            }
        }

        // ------------------------------------------------------------------------------------------ core

        private static void BeginCore(Scene scene)
        {
            EndCore();
            _scene = scene;
            IsRunning = true;
            PerceptionService.StimulusSink = Vortex.Perception.Deliver;
            try { NavigationService.Begin(scene); } catch (Exception ex) { NavigationService.Log("[Navigation] begin failed: " + ex.Message, true); }
            try { PerceptionService.Begin(scene); } catch (Exception ex) { NavigationService.Log("[Perception] begin failed: " + ex.Message, true); }
        }

        private static void TickCore(float dt)
        {
            try { NavigationService.Tick(dt); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Navigation] tick: " + ex); }
            try { PerceptionService.Tick(dt); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Perception] tick: " + ex); }
        }

        private static void EndCore()
        {
            _generation++;   // a tick coroutine of the previous run (if the script runtime still holds it) retires itself
            _tick = null;
            try { NavigationService.End(); } catch { }
            try { PerceptionService.End(); } catch { }
            _scene = null;
            IsRunning = false;
        }
    }
}
