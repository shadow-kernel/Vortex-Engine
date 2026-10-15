using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>The game's clock (#481, #425): the fixed-step accumulator, FixedUpdate once per physics step, the time scale
    /// on Update / LateUpdate, coroutines, Invoke timers, FixedUpdate and the animators, real time for the realtime waits, and
    /// the sound's pitch factor. Physics runs with or without the Jolt world (without it the fixed clock ticks alone).</summary>
    public static class TimeTests
    {
        private const string Scripts = @"
using Vortex;

public class FixedCounter : VortexBehaviour
{
    public int FixedCalls, Updates;
    public float LastFixedDt, DeltaInFixed, DeltaInUpdate;
    public bool InFixed, InFixedDuringUpdate = true;
    public override void FixedUpdate(float fixedDt) { FixedCalls++; LastFixedDt = fixedDt; DeltaInFixed = Time.DeltaTime; InFixed = Time.InFixedStep; }
    public override void Update(float dt) { Updates++; DeltaInUpdate = Time.DeltaTime; InFixedDuringUpdate = Time.InFixedStep; }
}

public class FixedThrower : VortexBehaviour
{
    public override void FixedUpdate(float dt) { throw new System.InvalidOperationException(""fixed boom""); }
}

public class Plain : VortexBehaviour
{
    public int Updates;
    public override void Update(float dt) { Updates++; }
}

public class Clocks : VortexBehaviour
{
    public float LastDt = -1f, LastLateDt = -1f, LastUnscaled = -1f;
    public int GameWaits, RealWaits, Invoked, NullSteps;
    public override void Start()
    {
        StartCoroutine(Game());
        StartCoroutine(Real());
        StartCoroutine(Frames());
        Invoke(() => Invoked++, 0.5f);
    }
    private System.Collections.IEnumerator Game() { yield return new WaitForSeconds(0.5f); GameWaits++; }
    private System.Collections.IEnumerator Real() { yield return new WaitForSecondsRealtime(0.5f); RealWaits++; }
    private System.Collections.IEnumerator Frames() { while (true) { NullSteps++; yield return null; } }
    public override void Update(float dt) { LastDt = dt; LastUnscaled = Time.UnscaledDeltaTime; }
    public override void LateUpdate(float dt) { LastLateDt = dt; }
}
";

        private static Assembly Compile(TestContext t)
        {
            var file = t.Write("Scripts/TimeProbe.cs", Scripts);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the test scripts do not compile: " + log);
            return asm;
        }

        private static GameEntity Scripted(Scene s, string name, string cls)
        {
            var e = new GameEntity(s, name);
            e.AddComponentDirect(new Script(e, "Assets/Scripts/" + cls + ".cs"));
            s.Entities.Add(e);
            return e;
        }

        private static object Field(object o, string name) => o.GetType().GetField(name).GetValue(o);
        private static bool Near(float a, float b, float eps = 1e-5f) => Math.Abs(a - b) <= eps;

        // ------------------------------------------------------------------ the clock maths (pure)

        [Test]
        public static void TheAccumulatorStepsAtTheFixedRate(TestContext t)
        {
            const float step = 1f / 60f;
            float acc = 0f; int steps = 0, frames0 = 0;
            for (int i = 0; i < 144; i++)
            {
                int n = GameTime.AdvanceFixed(ref acc, 1f / 144f, step, 4);
                if (n == 0) frames0++;
                t.True(n <= 1, "at 144 fps a frame runs at most one 60 Hz step");
                steps += n;
            }
            t.True(steps >= 59 && steps <= 60, "one second at 144 fps runs 60 steps (" + steps + ")");
            t.True(frames0 >= 84, "most 144 fps frames run none (" + frames0 + ")");

            acc = 0f;
            for (int i = 0; i < 30; i++) t.Equal(2, GameTime.AdvanceFixed(ref acc, 1f / 30f, step, 4), "a 30 fps frame runs two steps (frame " + i + ")");

            acc = 0f;
            t.Equal(4, GameTime.AdvanceFixed(ref acc, 0.5f, step, 4), "a half-second hitch runs the cap of 4 steps");
            t.True(acc <= step + 1e-6f, "and drops the rest: the next frame owes at most one step (" + acc + ")");
            t.Equal(1, GameTime.AdvanceFixed(ref acc, 0f, step, 4), "… which it runs");

            acc = 0f;
            t.Equal(0, GameTime.AdvanceFixed(ref acc, 0f, step, 4), "time standing still runs no step");
            t.Equal(0, GameTime.AdvanceFixed(ref acc, float.NaN, step, 4), "a NaN frame counts as 0");
            t.Equal(0, GameTime.AdvanceFixed(ref acc, -1f, step, 4), "a negative frame counts as 0");
            t.True(acc == 0f, "nothing accumulated");
        }

        [Test]
        public static void TheCatchUpCapScalesWithTheRateAndFastForward(TestContext t)
        {
            t.Equal(4, GameTime.MaxStepsFor(1f / 60f, 1f), "60 Hz: 4 steps, as before #481");
            t.Equal(8, GameTime.MaxStepsFor(1f / 120f, 1f), "120 Hz: the same 66 ms of game time = 8 steps");
            t.Equal(4, GameTime.MaxStepsFor(1f / 50f, 1f), "50 Hz: 4 steps");
            t.Equal(2, GameTime.MaxStepsFor(1f / 30f, 1f), "30 Hz: 2 steps");
            t.Equal(4, GameTime.MaxStepsFor(1f / 60f, 0.2f), "slow motion keeps the cap");
            t.Equal(16, GameTime.MaxStepsFor(1f / 60f, 4f), "4× fast forward needs 4× the steps");
            t.Equal(GameTime.MaxStepsPerFrame, GameTime.MaxStepsFor(1f / 60f, 100f), "never more than the hard cap");
            // fast forward is not dropped as a hitch: 4× at 60 fps feeds 1/15 s per frame, all of it simulated
            float acc = 0f;
            t.Equal(4, GameTime.AdvanceFixed(ref acc, 4f / 60f, 1f / 60f, GameTime.MaxStepsFor(1f / 60f, 4f), 4f), "4× fast forward at 60 fps: 4 steps a frame");
        }

        [Test]
        public static void TheTickScalesTimeAndCountsBothClocks(TestContext t)
        {
            float oldEditor = GameTime.EditorScale;
            try
            {
                GameTime.EditorScale = 1f;
                GameTime.ResetSession(1f / 60f);
                t.Equal(1f, GameTime.Scale, "a session starts at time scale 1");
                GameTime.Scale = 0.25f;
                float dt = GameTime.BeginTick(0.02f);
                t.True(Near(dt, 0.005f) && Near(GameTime.DeltaTime, 0.005f), "game seconds = real × 0.25 (" + dt + ")");
                t.True(Near(GameTime.UnscaledDeltaTime, 0.02f), "real seconds stay real");
                GameTime.BeginTick(0.02f);
                t.True(Math.Abs(GameTime.Elapsed - 0.01) < 1e-6 && Math.Abs(GameTime.UnscaledElapsed - 0.04) < 1e-6, "both clocks add up (" + GameTime.Elapsed + " / " + GameTime.UnscaledElapsed + ")");
                t.Equal(2, GameTime.FrameCount, "two ticks");
                t.Equal(0f, GameTime.BeginTick(float.NaN), "a NaN frame is no time");

                GameTime.Scale = -3f; t.Equal(0f, GameTime.Scale, "negative clamps to 0");
                GameTime.Scale = 1000f; t.Equal(GameTime.MaxTimeScale, GameTime.Scale, "the scale clamps to 100");
                GameTime.Scale = float.NaN; t.Equal(GameTime.MaxTimeScale, GameTime.Scale, "a NaN is ignored");
                GameTime.Scale = 0.5f; GameTime.EditorScale = 0.5f;
                t.True(Near(GameTime.EffectiveScale, 0.25f), "the editor's test multiplier multiplies");
                t.Equal(0.5f, Vortex.Time.TimeScale, "… and the game does not see it");

                GameTime.FixedDeltaTime = 0f; t.True(Near(GameTime.FixedDeltaTime, 1f / 60f), "a 0 fixed step is ignored");
                GameTime.FixedDeltaTime = 1e-6f; t.Equal(GameTime.MinFixedDeltaTime, GameTime.FixedDeltaTime, "the fixed step clamps low");
                GameTime.FixedDeltaTime = 5f; t.Equal(GameTime.MaxFixedDeltaTime, GameTime.FixedDeltaTime, "… and high");
                t.True(Near(GameTime.FixedDeltaTimeForRate(0), 1f / 60f), "no project rate = 60 Hz");
                t.True(Near(GameTime.FixedDeltaTimeForRate(120), 1f / 120f), "a project rate of 120 = 1/120 s");

                GameTime.ResetSession(1f / 50f);
                t.True(GameTime.Scale == 1f && GameTime.Elapsed == 0d && GameTime.FrameCount == 0 && Near(GameTime.FixedDeltaTime, 1f / 50f),
                       "a new session: scale 1, clocks at 0, the project's step");
            }
            finally { GameTime.EditorScale = oldEditor; GameTime.ResetSession(1f / 60f); }
        }

        [Test]
        public static void TheProjectRateIsOptionalInTheFile(TestContext t)
        {
            var st = new ProjectSettings();
            t.Equal(0, st.FixedUpdateRate, "a new project asks for no rate (60 Hz)");
            string json = Editor.Core.Serialization.DataSerializer.ToJson(st);
            t.False(json.Contains("fixedUpdateRate"), "… and writes no key for it: " + json);
            st.FixedUpdateRate = 120;
            json = Editor.Core.Serialization.DataSerializer.ToJson(st);
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
                t.True(doc.RootElement.TryGetProperty("fixedUpdateRate", out var rate) && rate.GetInt32() == 120, "another rate is written: " + json);
            var back = Editor.Core.Serialization.DataSerializer.FromJson<ProjectSettings>(json);
            t.Equal(120, back.FixedUpdateRate, "and read back");
        }

        // ------------------------------------------------------------------ FixedUpdate in the running game (#481)

        [Test]
        public static void FixedUpdateRunsOncePerPhysicsStep(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Fixed" };
            var e = Scripted(scene, "Counter", "FixedCounter");
            var rt = ScriptRuntime.Instance;
            try
            {
                GameTime.ResetSession(1f / 60f);
                rt.Begin(scene, asm);
                var b = rt.BehavioursOf(e)[0];
                int expected = 0, framesWithout = 0;
                for (int i = 0; i < 144; i++)
                {
                    int before = (int)Field(b, "FixedCalls");
                    rt.Update(1f / 144f);
                    int calls = (int)Field(b, "FixedCalls") - before;
                    t.Equal(PhysicsService.LastStepCount, calls, "FixedUpdate ran once per physics step (frame " + i + ")");
                    expected += PhysicsService.LastStepCount;
                    if (calls == 0) framesWithout++;
                }
                t.True(expected >= 59 && expected <= 60, "a second at 144 fps: 60 fixed steps (" + expected + ")");
                t.True(framesWithout > 0, "frames without a step get no FixedUpdate");
                t.Equal(144, (int)Field(b, "Updates"), "Update still runs every frame");
                t.True(Near((float)Field(b, "LastFixedDt"), 1f / 60f), "FixedUpdate gets the fixed step");
                t.True(Near((float)Field(b, "DeltaInFixed"), 1f / 60f), "Time.DeltaTime reads the fixed step inside FixedUpdate");
                t.True((bool)Field(b, "InFixed"), "Time.InFixedStep is true inside");
                t.False((bool)Field(b, "InFixedDuringUpdate"), "… and false in Update");
                t.True(Near((float)Field(b, "DeltaInUpdate"), 1f / 144f), "Update's Time.DeltaTime is the frame's");

                int c0 = (int)Field(b, "FixedCalls");
                for (int i = 0; i < 10; i++) { rt.Update(1f / 30f); t.Equal(2, PhysicsService.LastStepCount, "a 30 fps frame steps twice"); }
                t.Equal(c0 + 20, (int)Field(b, "FixedCalls"), "… with two FixedUpdates each");

                // a script changes the rate: 120 Hz from the next frame on
                Vortex.Time.FixedDeltaTime = 1f / 120f;
                c0 = (int)Field(b, "FixedCalls");
                for (int i = 0; i < 6; i++) rt.Update(1f / 60f);
                t.True((int)Field(b, "FixedCalls") - c0 >= 11, "Time.FixedDeltaTime = 1/120: two steps per 60 fps frame (" + ((int)Field(b, "FixedCalls") - c0) + ")");
                t.True(Near((float)Field(b, "LastFixedDt"), 1f / 120f), "FixedUpdate gets the new step");
            }
            finally { rt.End(); GameTime.ResetSession(1f / 60f); }
        }

        [Test]
        public static void NoFixedUpdateNoHookAndExceptionsReachTheConsole(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Hook" };
            Scripted(scene, "Plain", "Plain");
            var rt = ScriptRuntime.Instance;
            try
            {
                GameTime.ResetSession(1f / 60f);
                rt.Begin(scene, asm);
                t.True(PhysicsService.BeforeStep == null, "no behaviour overrides FixedUpdate: no per-step hook (no cost)");
                rt.End();

                var thrower = Scripted(scene, "Thrower", "FixedThrower");
                rt.Begin(scene, asm);
                t.NotNull(PhysicsService.BeforeStep, "a FixedUpdate behaviour installs the hook");
                rt.Update(1f / 30f);
                bool logged = ConsoleService.Instance.Entries.Any(x => x.Level == LogLevel.Error && x.Message.Contains("FixedThrower.FixedUpdate(): fixed boom"));
                t.True(logged, "a FixedUpdate exception reaches the Console like every callback's (#322)");
                Vortex.Scene.Destroy(rt.BehavioursOf(thrower)[0].EntityId);
                rt.Update(1f / 60f);
                t.True(PhysicsService.BeforeStep == null, "the last FixedUpdate behaviour destroyed: the hook goes again");
            }
            finally { rt.End(); GameTime.ResetSession(1f / 60f); }
            t.True(PhysicsService.BeforeStep == null, "End removes the hook");
        }

        // ------------------------------------------------------------------ the time scale in the running game (#425)

        [Test]
        public static void TheTimeScaleSlowsScriptsTimersAndFixedSteps(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Scale" };
            var clocks = Scripted(scene, "Clocks", "Clocks");
            var counter = Scripted(scene, "Counter", "FixedCounter");
            var rt = ScriptRuntime.Instance;
            try
            {
                GameTime.ResetSession(1f / 60f);
                rt.Begin(scene, asm);
                var c = rt.BehavioursOf(clocks)[0];
                var f = rt.BehavioursOf(counter)[0];

                Vortex.Time.TimeScale = 0.25f;
                rt.Update(0.02f);
                t.True(Near((float)Field(c, "LastDt"), 0.005f), "Update gets game seconds (" + Field(c, "LastDt") + ")");
                t.True(Near((float)Field(c, "LastLateDt"), 0.005f), "LateUpdate too");
                t.True(Near((float)Field(c, "LastUnscaled"), 0.02f), "Time.UnscaledDeltaTime stays real");
                t.True(Near(Vortex.Time.DeltaTime, 0.005f), "Time.DeltaTime is scaled");

                // TimeScale 0: Update runs with dt 0, FixedUpdate does not, game-time waits hold, realtime waits and null yields go on
                Vortex.Time.TimeScale = 0f;
                int fixedBefore = (int)Field(f, "FixedCalls"), nullBefore = (int)Field(c, "NullSteps");
                for (int i = 0; i < 60; i++) rt.Update(1f / 60f);   // one real second
                t.Equal(0f, (float)Field(c, "LastDt"), "paused: Update's dt is 0");
                t.Equal(fixedBefore, (int)Field(f, "FixedCalls"), "paused: no FixedUpdate");
                t.Equal(0, PhysicsService.LastStepCount, "paused: no physics step");
                t.Equal(0, (int)Field(c, "GameWaits"), "WaitForSeconds holds while paused");
                t.Equal(0, (int)Field(c, "Invoked"), "Invoke holds while paused");
                t.Equal(1, (int)Field(c, "RealWaits"), "WaitForSecondsRealtime counts real seconds");
                t.Equal(nullBefore + 60, (int)Field(c, "NullSteps"), "yield return null resumes every frame, also while paused");
                t.True(Vortex.Time.Elapsed < 0.01f && Vortex.Time.UnscaledElapsed > 1f, "game time stood still, real time ran (" + Vortex.Time.Elapsed + " / " + Vortex.Time.UnscaledElapsed + ")");

                Vortex.Time.TimeScale = 1f;
                for (int i = 0; i < 31; i++) rt.Update(1f / 60f);
                t.Equal(1, (int)Field(c, "GameWaits"), "time runs again: the game-time wait ends after its half second");
                t.Equal(1, (int)Field(c, "Invoked"), "… and so does the Invoke");

                // slow motion: half a second of real time at 0.5 runs a quarter second of steps
                Vortex.Time.TimeScale = 0.5f;
                fixedBefore = (int)Field(f, "FixedCalls");
                for (int i = 0; i < 30; i++) rt.Update(1f / 60f);
                int slowSteps = (int)Field(f, "FixedCalls") - fixedBefore;
                t.True(slowSteps >= 14 && slowSteps <= 16, "0.5 s real at time scale 0.5 = 15 fixed steps (" + slowSteps + ")");
            }
            finally { rt.End(); GameTime.ResetSession(1f / 60f); }
        }

        [Test]
        public static void APlaySessionStartsAtTimeScaleOne(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Session" };
            Scripted(scene, "Plain", "Plain");
            var rt = ScriptRuntime.Instance;
            try
            {
                Vortex.Time.TimeScale = 0.1f;
                Vortex.Time.FixedDeltaTime = 1f / 30f;
                rt.BeginPlaySession(scene, asm);
                t.Equal(1f, Vortex.Time.TimeScale, "a new play session starts at time scale 1");
                t.True(Near(Vortex.Time.FixedDeltaTime, 1f / 60f), "… with the project's fixed step (60 Hz by default)");
                Vortex.Time.TimeScale = 0.3f;
                Vortex.Time.FixedDeltaTime = 1f / 120f;
                rt.Begin(scene, asm);   // a scene load / hot reload inside the session
                t.True(Near(Vortex.Time.TimeScale, 0.3f), "a scene load keeps the session's time scale");
                t.True(Near(Vortex.Time.FixedDeltaTime, 1f / 120f), "… and its fixed step");
                Vortex.Time.TimeScale = 0f;
                rt.End();   // play stops
                t.Equal(1f, GameTime.Scale, "play stopped: time scale 1 again (a paused game does not hold the editor)");
                t.True(Near(GameTime.FixedDeltaTime, 1f / 60f), "… and the project's fixed step");
            }
            finally { rt.End(); GameTime.ResetSession(1f / 60f); }
        }

        [Test]
        public static void AnimatorsFollowTheTimeScaleUnlessTheyIgnoreIt(TestContext t)
        {
            var scene = new Scene { Name = "Anim" };
            var slowed = Rigged(scene, "Slowed", ignore: false);
            var real = Rigged(scene, "Real", ignore: true);
            var svc = AnimationService.Instance;
            try
            {
                svc.ResetStates();
                var clip = new VortexAnimClip { Name = "idle", DurationSec = 10f, Loop = true };
                t.True(svc.PlayWithRig(slowed, TestRig(), clip) && svc.PlayWithRig(real, TestRig(), clip), "both play");
                for (int i = 0; i < 10; i++) svc.Step(scene, 0.25f * 0.1f, 0.1f);   // one real second at time scale 0.25
                t.True(Math.Abs(svc.GetTime(slowed) - 0.25f) < 1e-3f, "the animator plays a quarter second (" + svc.GetTime(slowed) + ")");
                t.True(Math.Abs(svc.GetTime(real) - 1f) < 1e-3f, "Ignore Time Scale plays the real second (" + svc.GetTime(real) + ")");
            }
            finally { svc.ResetStates(); }
        }

        internal static GameEntity Rigged(Scene s, string name, bool ignore = false)
        {
            var e = new GameEntity(s, name);
            e.AddComponentDirect(new Editor.ECS.Components.Animation.Animator(e) { IgnoreTimeScale = ignore, PlayOnStart = false });
            s.Entities.Add(e);
            return e;
        }

        internal static SkeletonDef TestRig()
        {
            var root = new SkeletonNode { Name = "Root", Parent = -1, BindRotation = System.Numerics.Quaternion.Identity, BindTranslation = System.Numerics.Vector3.Zero, BindScale = System.Numerics.Vector3.One, LocalBind = System.Numerics.Matrix4x4.Identity };
            var hips = new SkeletonNode { Name = "Hips", Parent = 0, BindRotation = System.Numerics.Quaternion.Identity, BindTranslation = new System.Numerics.Vector3(0, 1f, 0), BindScale = System.Numerics.Vector3.One, LocalBind = System.Numerics.Matrix4x4.CreateTranslation(0, 1f, 0) };
            return new SkeletonDef { Nodes = new[] { root, hips }, Bones = new[] { new SkeletonBone { NodeIndex = 1 } } };
        }

        // ------------------------------------------------------------------ the sound (#425)

        [Test]
        public static void SoundsFollowTheTimeScaleOnlyWhenAsked(TestContext t)
        {
            t.Equal(1f, AudioPlaybackService.TimePitch(false, 0.2f, 0.5f, out bool stopped), "off: sounds keep their pitch");
            t.False(stopped, "off: nothing holds");
            t.Equal(1f, AudioPlaybackService.TimePitch(false, 0f, 0.5f, out stopped), "off: not even at time scale 0");
            t.False(stopped, "off: time scale 0 holds nothing");
            t.Equal(0.8f, AudioPlaybackService.TimePitch(true, 0.8f, 0.5f, out stopped), "on: pitch × the time scale");
            t.Equal(0.5f, AudioPlaybackService.TimePitch(true, 0.1f, 0.5f, out stopped), "on: never below the floor");
            t.Equal(4f, AudioPlaybackService.TimePitch(true, 10f, 0.5f, out stopped), "on: at most 4×");
            AudioPlaybackService.TimePitch(true, 0f, 0.5f, out stopped);
            t.True(stopped, "on: time scale 0 holds the following voices");
            t.False(AudioPlaybackService.FollowsTimeScale(Editor.DllWrapper.VortexAudio.BusMusic), "the Music bus never follows");
            t.False(AudioPlaybackService.FollowsTimeScale(Editor.DllWrapper.VortexAudio.BusUi), "the UI bus never follows");
            t.True(AudioPlaybackService.FollowsTimeScale(Editor.DllWrapper.VortexAudio.BusSfx), "SFX follows");
            t.True(AudioPlaybackService.FollowsTimeScale(Editor.DllWrapper.VortexAudio.BusAmbience), "Ambience follows");
            var svc = AudioPlaybackService.Instance;
            bool on = svc.PitchFollowsTimeScale; float min = svc.MinTimeScalePitch;
            try
            {
                Vortex.Audio.SetPitchFollowsTimeScale(true, 7f);
                t.True(Vortex.Audio.PitchFollowsTimeScale && svc.MinTimeScalePitch == 1f, "the floor clamps to 1");
                Vortex.Audio.SetPitchFollowsTimeScale(true, 0f);
                t.True(svc.MinTimeScalePitch >= 0.05f, "… and to 0.05");
            }
            finally { svc.SetPitchFollowsTimeScale(on, min); }
        }
    }
}
