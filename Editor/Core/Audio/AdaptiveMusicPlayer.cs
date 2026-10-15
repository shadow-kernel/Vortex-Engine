using System;
using System.Collections.Generic;
using Editor.DllWrapper;

namespace Editor.Core.Audio
{
    /// <summary>
    /// Plays one .vmusic (#437) through a native music voice — its stems mixed sample-locked in one voice — and drives it:
    /// <see cref="SetState"/> schedules the stems' state volumes on the boundary the transition rule names (the music's own
    /// frame clock, so the change lands exactly on the bar line), <see cref="SetParameter"/> glides the stems mapped to a
    /// parameter, <see cref="PlayStinger"/> mixes a stinger in on its beat, and <see cref="Tick"/> turns the music clock
    /// into OnBeat / OnBar deliveries. Shared by play mode (Audio.Music) and the editor's preview.
    /// </summary>
    public sealed class AdaptiveMusicPlayer
    {
        /// <summary>A change asked for now lands on the first boundary at least this far ahead: the clock the game reads is
        /// the end of the last mix, and a boundary the mixer passes before the schedule reaches it would be missed.</summary>
        public const double LeadSeconds = 0.03;
        /// <summary>Between two mixes the clock is carried on by wall time — at most this far, so a paused or stalled mixer
        /// does not run the beats ahead.</summary>
        public const double MaxExtrapolationSeconds = 0.05;

        public AdaptiveMusic Music { get; }
        public ulong Handle { get; }
        public MusicTiming Timing { get; }

        private readonly Func<string, string> _resolveClip;
        private readonly Dictionary<string, float> _params;
        private readonly BeatTracker _tracker;
        private readonly List<long> _beats = new List<long>();
        private string _target;        // the state asked for last (the state the music is in or heading to)
        private string _audible;       // the state whose volumes have begun
        private ulong _pendingAt = ulong.MaxValue;

        /// <summary>The state the music is in or heading to — the one SetState chose last, or the start state.</summary>
        public string State => _target ?? "";

        /// <summary>Diagnostics (smoke check): where the last state change was scheduled — the music clock frame and the
        /// musical frame (frames since bar 1) of its boundary; ulong.MaxValue / NaN before the first.</summary>
        internal ulong LastSwitchPlayedFrame { get; private set; } = ulong.MaxValue;
        internal double LastSwitchMusicalFrame { get; private set; } = double.NaN;
        internal MusicSync LastSwitchSync { get; private set; }

        /// <summary>Diagnostics: how far from its bar line each of the last bars was delivered (seconds of the music clock;
        /// negative = up to half a frame early).</summary>
        internal readonly List<double> BarErrors = new List<double>();
        /// <summary>Diagnostics: the average time between two ticks (seconds) — half of it bounds the delivery error.</summary>
        internal double AverageTickSeconds { get; private set; }

        private AdaptiveMusicPlayer(AdaptiveMusic music, ulong handle, MusicTiming timing, string state, Dictionary<string, float> pars, Func<string, string> resolveClip)
        {
            Music = music;
            Handle = handle;
            Timing = timing;
            _target = _audible = state;
            _params = pars;
            _resolveClip = resolveClip;
            _tracker = new BeatTracker(timing);
        }

        /// <summary>Starts the music: every stem resolved to a playable path by <paramref name="resolveClip"/> (project-
        /// relative in, a file path or registered pak name out; null = missing), in its start state, at
        /// <paramref name="volume"/> × the music's own volume, on mixer bus <paramref name="bus"/>. Null with
        /// <paramref name="error"/> when it cannot play.</summary>
        public static AdaptiveMusicPlayer Start(AdaptiveMusic music, Func<string, string> resolveClip, int bus, float volume, out string error)
        {
            error = null;
            if (music == null || music.Stems.Count == 0) { error = "it has no stems"; return null; }
            if (music.Stems.Count > 16) { error = "it has " + music.Stems.Count + " stems — 16 is the most one music plays"; return null; }
            var paths = new string[music.Stems.Count];
            for (int i = 0; i < paths.Length; i++)
            {
                var stem = music.Stems[i];
                string rel = AdaptiveMusic.ResolveClip(stem.Guid, stem.ClipPath);
                paths[i] = rel != null ? resolveClip(rel) : null;
                if (paths[i] == null) { error = "stem '" + (string.IsNullOrEmpty(stem.Name) ? "#" + (i + 1) : stem.Name) + "' " + (rel == null ? "has no clip" : "was not found: " + rel); return null; }
            }
            VortexAudio.GetDspTime(out int rate);
            if (rate <= 0) { error = "there is no audio engine"; return null; }
            var timing = MusicTiming.For(music, rate);

            // the stems loop whole bars from bar 1, streamed or decoded: LoopBars, or the longest stem rounded to bars (it is
            // never cut short by a stem a few samples shorter — that one is silent for those samples)
            ulong loopStart = 0, loopEnd = 0;
            if (music.Loop)
            {
                double longest = 0;
                if (music.LoopBars <= 0)
                    foreach (var p in paths) longest = Math.Max(longest, StemSeconds(p) * rate);
                (loopStart, loopEnd) = timing.LoopRegion(music.LoopBars, longest);
            }

            var start = music.StartState();
            var pars = new Dictionary<string, float>(music.Parameters, StringComparer.OrdinalIgnoreCase);
            ulong h = VortexAudio.PlayMusic(paths, music.States.Count > 0 ? music.StateGains(start) : null, music.ParameterGains(pars),
                loopStart, loopEnd, 0, music.Loop, music.Decode, bus, Math.Max(0f, volume) * music.Volume);
            if (h == VortexAudio.InvalidVoice) { error = "a stem could not be opened (unreadable file?)"; return null; }
            return new AdaptiveMusicPlayer(music, h, timing, start?.Name, pars, resolveClip);
        }

        /// <summary>Stem lengths, read once per file version: an MP3 stem's length is a scan of the whole file, which a
        /// second Play (a scene switch, a retry) must not pay again.</summary>
        private static readonly Dictionary<string, (long Stamp, double Seconds)> _stemSeconds = new Dictionary<string, (long, double)>(StringComparer.OrdinalIgnoreCase);

        private static double StemSeconds(string path)
        {
            long stamp = 0;
            try { var fi = new System.IO.FileInfo(path); if (fi.Exists) stamp = fi.LastWriteTimeUtc.Ticks ^ fi.Length; } catch { }   // a pak name: no file
            lock (_stemSeconds)
                if (_stemSeconds.TryGetValue(path, out var known) && known.Stamp == stamp) return known.Seconds;
            double seconds = VortexAudio.GetClipInfo(path, out float s, out _, out _) ? s : 0;
            lock (_stemSeconds) _stemSeconds[path] = (stamp, seconds);
            return seconds;
        }

        /// <summary>The musical position now (frames since bar 1, rising through loops): the mixer's position at its last
        /// mix carried on by the wall time since, at most <see cref="MaxExtrapolationSeconds"/>.</summary>
        public bool TryGetMusical(out double musicalNow, out VortexAudio.MusicClock clock)
        {
            musicalNow = 0;
            if (!VortexAudio.GetMusicClock(Handle, out clock)) return false;
            double extra = clock.Stalled != 0 ? 0 : Math.Clamp(clock.SecondsSinceMix, 0, MaxExtrapolationSeconds);
            musicalNow = Timing.MusicalFrame(clock.Position, clock.Loops, clock.LoopStart, clock.LoopEnd) + extra * Timing.SampleRate;
            return true;
        }

        /// <summary>The beat / bar the mixer is in (0 = the first beat of bar 1; -1 before bar 1 or without a clock).</summary>
        public int CurrentBeat => TryGetMusical(out double m, out _) ? (int)Math.Max(-1, Timing.BeatAt(m)) : -1;
        public int CurrentBar => TryGetMusical(out double m, out _) ? (int)Math.Max(-1, Timing.BarAt(m)) : -1;

        /// <summary>The frame of the music clock a change asked for now lands on (see MusicTiming.ScheduleFrame); false
        /// without a clock.</summary>
        private bool Schedule(MusicSync sync, out ulong playedAt, out double musicalAt)
        {
            playedAt = 0; musicalAt = double.NaN;
            if (!VortexAudio.GetMusicClock(Handle, out var c)) return false;
            // the end of the last mix: the next mix starts there, and the schedule refers to that clock
            double now = Timing.MusicalFrame(c.Position, c.Loops, c.LoopStart, c.LoopEnd);
            musicalAt = Timing.ScheduleFrame(now, sync, LeadSeconds * Timing.SampleRate);
            playedAt = MusicTiming.PlayedFrameAt(musicalAt, now, c.Played);
            return true;
        }

        /// <summary>Change to a state: its stem volumes begin on the boundary its transition rule names (from the state
        /// that is audible), gliding over the rule's fade, with the rule's stinger on the same frame. The same state again
        /// (or the one already on its way) changes nothing. False for an unknown state.</summary>
        public bool SetState(string name)
        {
            var state = Music.FindState(name);
            if (state == null) return false;
            if (string.Equals(state.Name, _target, StringComparison.OrdinalIgnoreCase)) return true;
            var rule = Music.TransitionFor(_audible ?? "", state.Name);
            var sync = rule.SyncMode;
            if (!Schedule(sync, out ulong at, out double musical)) return false;
            VortexAudio.MusicSchedule(Handle, VortexAudio.MusicStageState, Music.StateGains(state), at, (ulong)Math.Round(Math.Max(0, rule.FadeSeconds) * Timing.SampleRate));
            if (!string.IsNullOrEmpty(rule.Stinger)) PlayStingerAt(Music.FindStinger(rule.Stinger), at);
            _target = state.Name;
            _pendingAt = at;
            LastSwitchPlayedFrame = at;
            LastSwitchMusicalFrame = musical;
            LastSwitchSync = sync;
            return true;
        }

        /// <summary>Set a parameter (0..1): every stem mapped to it glides to its new volume over the music's parameter
        /// fade — at once, not on a boundary (it follows gameplay smoothly; call it every frame if you like).</summary>
        public void SetParameter(string name, float value)
        {
            if (string.IsNullOrEmpty(name) || float.IsNaN(value)) return;
            value = AdaptiveMusic.Clamp01(value);
            if (_params.TryGetValue(name, out float old) && old == value) return;
            _params[name] = value;
            if (!Music.Stems.Exists(s => string.Equals(s.Parameter, name, StringComparison.OrdinalIgnoreCase))) return;   // nothing follows it
            VortexAudio.MusicSchedule(Handle, VortexAudio.MusicStageParameter, Music.ParameterGains(_params), 0,
                (ulong)Math.Round(Math.Max(0, Music.ParameterFadeSeconds) * Timing.SampleRate));
        }

        public float GetParameter(string name) => name != null && _params.TryGetValue(name, out float v) ? v : 0f;

        /// <summary>Play one of the music's stingers on its sync boundary (by default the next beat). False for an unknown
        /// stinger, a clip that cannot be opened, or four already sounding.</summary>
        public bool PlayStinger(string name)
        {
            var st = Music.FindStinger(name);
            if (st == null || !Schedule(st.SyncMode, out ulong at, out _)) return false;
            return PlayStingerAt(st, at);
        }

        private bool PlayStingerAt(AdaptiveMusic.Stinger st, ulong at)
        {
            if (st == null) return false;
            string rel = AdaptiveMusic.ResolveClip(st.Guid, st.ClipPath);
            string path = rel != null ? _resolveClip(rel) : null;
            return path != null && VortexAudio.MusicPlayStinger(Handle, path, Math.Max(0f, st.Volume), at);
        }

        /// <summary>Per game tick: book the state change that began, then deliver the beats the tick is nearest to —
        /// <paramref name="onBeat"/>(beat, bar, isBarLine) in order — with the next tick expected <paramref name="dtSeconds"/>
        /// later.</summary>
        public void Tick(double dtSeconds, Action<int, int, bool> onBeat)
        {
            if (!TryGetMusical(out double now, out var c)) return;
            if (dtSeconds > 0 && dtSeconds < 0.25) AverageTickSeconds = AverageTickSeconds <= 0 ? dtSeconds : AverageTickSeconds * 0.95 + dtSeconds * 0.05;
            if (_pendingAt != ulong.MaxValue && c.Played >= _pendingAt) { _audible = _target; _pendingAt = ulong.MaxValue; }
            _beats.Clear();
            _tracker.Advance(now, Math.Clamp(dtSeconds, 0, 0.25) * Timing.SampleRate, _beats);
            foreach (long b in _beats)
            {
                bool isBar = b % Timing.BeatsPerBar == 0;
                long bar = b / Timing.BeatsPerBar;
                if (isBar)
                {
                    BarErrors.Add((now - Timing.FrameOfBar(bar)) / Timing.SampleRate);
                    if (BarErrors.Count > 32) BarErrors.RemoveAt(0);
                }
                onBeat?.Invoke((int)Math.Min(int.MaxValue, b), (int)Math.Min(int.MaxValue, bar), isBar);
            }
        }
    }
}
