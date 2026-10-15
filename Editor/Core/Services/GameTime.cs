using System;

namespace Editor.Core.Services
{
    /// <summary>
    /// The play session's clock (#425, #481) — what <c>Vortex.Time</c> shows the scripts, and what the engine's simulations
    /// step with. Every tick the host hands the script runtime the real (unscaled) frame time; <see cref="BeginTick"/> turns it
    /// into game time with the time scale: Update / LateUpdate, the physics accumulator, the animators, the particles and decals,
    /// the AI crowd, coroutines and Invoke timers all advance by the scaled seconds, while the UI (the .vui screens and their
    /// action classes, the dev console, the hosts' overlays) and the audio engine's own clock keep real time.
    ///
    /// The fixed step (<see cref="FixedDeltaTime"/>) is game time: at a time scale of 0.5 the physics and FixedUpdate run half as
    /// often per real second, each step still simulating FixedDeltaTime. <see cref="AdvanceFixed"/> is the accumulator maths
    /// (pure, unit-tested); <c>PhysicsService.Step</c> runs it.
    /// </summary>
    public static class GameTime
    {
        /// <summary>The fixed step when the project sets none: 60 steps per second, the rate physics always ran at.</summary>
        public const float DefaultFixedDeltaTime = 1f / 60f;
        /// <summary>Bounds of <see cref="FixedDeltaTime"/>: 1000 down to 10 steps per second.</summary>
        public const float MinFixedDeltaTime = 0.001f, MaxFixedDeltaTime = 0.1f;
        /// <summary>The largest time scale (#425: 0..100).</summary>
        public const float MaxTimeScale = 100f;
        /// <summary>How much game time one frame may simulate at time scale 1 before the rest is dropped — 4 steps of 1/60 s, the
        /// limit physics always had (MaxSubsteps 4). A slow frame then slows the simulation down instead of making the next frame
        /// slower still (the spiral of death).</summary>
        public const float MaxCatchUpSeconds = 4f / 60f;
        /// <summary>Hard cap of fixed steps in one frame, whatever the time scale (fast-forward) asks for.</summary>
        public const int MaxStepsPerFrame = 32;

        private static float _scale = 1f, _editorScale = 1f, _fixed = DefaultFixedDeltaTime;

        /// <summary>The game's time scale (Vortex.Time.TimeScale): 1 = normal speed, 0.2 = slow motion, 0 = paused, 2 = fast
        /// forward. Clamped to 0..<see cref="MaxTimeScale"/>; a NaN is ignored.</summary>
        public static float Scale
        {
            get => _scale;
            set { if (!float.IsNaN(value)) _scale = value < 0f ? 0f : (value > MaxTimeScale ? MaxTimeScale : value); }
        }

        /// <summary>The editor's test multiplier (Play options ▸ Time Scale): the whole game runs this much slower or faster on
        /// top of what the game itself sets, without the game seeing it in Time.TimeScale. 1 in the standalone player.</summary>
        public static float EditorScale
        {
            get => _editorScale;
            set { if (!float.IsNaN(value)) _editorScale = value < 0f ? 0f : (value > MaxTimeScale ? MaxTimeScale : value); }
        }

        /// <summary>What the engine's simulations step with: <see cref="Scale"/> × <see cref="EditorScale"/>.</summary>
        public static float EffectiveScale => Math.Min(MaxTimeScale, _scale * _editorScale);

        /// <summary>Seconds of game time per fixed step — FixedUpdate's dt and the physics step (Vortex.Time.FixedDeltaTime).
        /// Clamped to <see cref="MinFixedDeltaTime"/>..<see cref="MaxFixedDeltaTime"/>; a NaN or 0 is ignored.</summary>
        public static float FixedDeltaTime
        {
            get => _fixed;
            set { if (value > 0f && !float.IsNaN(value)) _fixed = value < MinFixedDeltaTime ? MinFixedDeltaTime : (value > MaxFixedDeltaTime ? MaxFixedDeltaTime : value); }
        }

        /// <summary>This tick's game seconds (real seconds × the time scale).</summary>
        public static float DeltaTime { get; private set; }
        /// <summary>This tick's real seconds.</summary>
        public static float UnscaledDeltaTime { get; private set; }
        /// <summary>Game seconds since the play session started (scaled).</summary>
        public static double Elapsed { get; private set; }
        /// <summary>Real seconds since the play session started.</summary>
        public static double UnscaledElapsed { get; private set; }
        /// <summary>Ticks since the play session started (the first tick is 1).</summary>
        public static int FrameCount { get; private set; }
        /// <summary>True while FixedUpdate runs (between the physics steps).</summary>
        public static bool InFixedStep { get; internal set; }

        /// <summary>The fixed rate a project asks for (Project Settings ▸ Physics, #481): 0 or a value out of range = 60 Hz.</summary>
        public static float FixedDeltaTimeForRate(int stepsPerSecond)
            => stepsPerSecond > 0 ? Math.Max(MinFixedDeltaTime, Math.Min(MaxFixedDeltaTime, 1f / stepsPerSecond)) : DefaultFixedDeltaTime;

        /// <summary>A new play session: time scale 1, the clocks at 0, the project's fixed step.</summary>
        public static void ResetSession(float fixedDeltaTime)
        {
            _scale = 1f;
            _fixed = DefaultFixedDeltaTime;
            FixedDeltaTime = fixedDeltaTime;
            DeltaTime = UnscaledDeltaTime = 0f;
            Elapsed = UnscaledElapsed = 0d;
            FrameCount = 0;
            InFixedStep = false;
        }

        /// <summary>One tick: <paramref name="realSeconds"/> (the host's frame time; negative or NaN counts as 0) becomes this
        /// tick's unscaled and scaled seconds, and the clocks move on. Returns the scaled seconds.</summary>
        public static float BeginTick(float realSeconds)
        {
            if (!(realSeconds > 0f) || float.IsInfinity(realSeconds)) realSeconds = 0f;
            UnscaledDeltaTime = realSeconds;
            DeltaTime = realSeconds * EffectiveScale;
            UnscaledElapsed += realSeconds;
            Elapsed += DeltaTime;
            FrameCount++;
            return DeltaTime;
        }

        /// <summary>The most fixed steps one frame may run: <see cref="MaxCatchUpSeconds"/> of game time (more while fast-
        /// forwarding — a time scale of 4 needs 4× the steps per real second), never more than <see cref="MaxStepsPerFrame"/>.
        /// 4 at the default 60 Hz and time scale 1, as before #481.</summary>
        public static int MaxStepsFor(float fixedDeltaTime, float timeScale)
        {
            if (!(fixedDeltaTime > 0f)) fixedDeltaTime = DefaultFixedDeltaTime;
            float budget = MaxCatchUpSeconds * Math.Max(1f, float.IsNaN(timeScale) ? 1f : timeScale);
            int n = (int)Math.Ceiling(budget / fixedDeltaTime - 1e-3f);
            return n < 1 ? 1 : (n > MaxStepsPerFrame ? MaxStepsPerFrame : n);
        }

        /// <summary>The fixed-step accumulator (#481): adds this frame's game seconds to <paramref name="accumulator"/> and returns
        /// how many fixed steps are due now (0 on a frame shorter than a step), at most <paramref name="maxSteps"/>; the steps'
        /// time is taken out of the accumulator, and what could not be caught up with is dropped, so the next frame owes at most
        /// one step. Afterwards accumulator / step is where the rendered frame sits between the last two steps (interpolation).
        /// A frame of more than 0.25 s (× the time scale while fast-forwarding) counts as 0.25 s — a breakpoint or a hitch.</summary>
        public static int AdvanceFixed(ref float accumulator, float frameSeconds, float step, int maxSteps, float timeScale = 1f)
        {
            if (!(step > 0f)) step = DefaultFixedDeltaTime;
            if (!(frameSeconds > 0f) || float.IsInfinity(frameSeconds)) frameSeconds = 0f;
            float cap = 0.25f * Math.Max(1f, float.IsNaN(timeScale) ? 1f : timeScale);
            if (frameSeconds > cap) frameSeconds = cap;
            if (!(accumulator >= 0f)) accumulator = 0f;
            accumulator += frameSeconds;
            int steps = 0;
            while (accumulator >= step && steps < maxSteps)
            {
                accumulator -= step;
                steps++;
            }
            if (accumulator > step) accumulator = step;   // drop time we can't catch up with
            return steps;
        }
    }
}
