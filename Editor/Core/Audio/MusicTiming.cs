using System;

namespace Editor.Core.Audio
{
    /// <summary>
    /// Bar and beat maths of adaptive music (#437) on the music's own frame clock. The native stem group reports where it
    /// is in the stems (position, wrapping in the loop region) and how many passes it completed; this turns that into the
    /// musical position — frames since bar 1 began, counting every pass, so beats and bars keep counting up across loops
    /// — and finds the boundaries a transition, a stinger or OnBeat/OnBar waits for. Pure arithmetic, no audio needed.
    /// </summary>
    public sealed class MusicTiming
    {
        public double SampleRate { get; }
        public double FramesPerBeat { get; }
        public double FramesPerBar { get; }
        public int BeatsPerBar { get; }
        public int PhraseBars { get; }
        /// <summary>The frame of the stems where bar 1 begins.</summary>
        public double Offset { get; }

        public MusicTiming(double bpm, int beatsPerBar, int phraseBars, double offsetSeconds, double sampleRate)
        {
            SampleRate = sampleRate > 0 ? sampleRate : 48000;
            BeatsPerBar = Math.Max(1, beatsPerBar);
            PhraseBars = Math.Max(1, phraseBars);
            FramesPerBeat = SampleRate * 60.0 / (bpm > 0 ? bpm : 120.0);
            FramesPerBar = FramesPerBeat * BeatsPerBar;
            Offset = offsetSeconds > 0 ? offsetSeconds * SampleRate : 0;
        }

        public static MusicTiming For(AdaptiveMusic m, double sampleRate)
            => new MusicTiming(m.Bpm, m.BeatsPerBar, m.PhraseBars, m.OffsetSeconds, sampleRate);

        /// <summary>The loop region the decoded stems play (frames of the stems): bar 1 up to <paramref name="loopBars"/>
        /// bars later, or — 0 bars — the stems' length rounded to whole bars, so every pass is whole bars and the beat grid
        /// never drifts (a stem a few samples short is silent for those samples). End 0 = let the stems decide.</summary>
        public (ulong Start, ulong End) LoopRegion(int loopBars, double stemFrames)
        {
            double start = Offset;
            double bars = loopBars > 0 ? loopBars : Math.Round((stemFrames - Offset) / FramesPerBar);
            if (!(bars >= 1)) return (0, 0);
            return ((ulong)Math.Round(start), (ulong)Math.Round(start + bars * FramesPerBar));
        }

        /// <summary>The musical length of one pass through the loop region: its length rounded to whole bars (exact for a
        /// region <see cref="LoopRegion"/> made), the region itself when it is shorter than a bar.</summary>
        public double PassLength(ulong loopStart, ulong loopEnd)
        {
            double len = loopEnd > loopStart ? loopEnd - loopStart : 0;
            double bars = Math.Round(len / FramesPerBar);
            return bars >= 1 ? bars * FramesPerBar : len;
        }

        /// <summary>Frames since bar 1 began — negative in a pickup before it — counting every completed pass through the
        /// loop region, so it keeps rising while the music loops.</summary>
        public double MusicalFrame(ulong position, ulong loops, ulong loopStart, ulong loopEnd)
            => loops * PassLength(loopStart, loopEnd) + (position - Offset);

        /// <summary>The beat a musical frame lies in (0 = the first beat of bar 1; negative in a pickup).</summary>
        public long BeatAt(double musicalFrame) => (long)Math.Floor(musicalFrame / FramesPerBeat + 1e-9);
        public long BarAt(double musicalFrame) => (long)Math.Floor(musicalFrame / FramesPerBar + 1e-9);
        public double FrameOfBeat(long beat) => beat * FramesPerBeat;
        public double FrameOfBar(long bar) => bar * FramesPerBar;

        /// <summary>Frames between two boundaries of a sync mode (0 for Immediate).</summary>
        public double Unit(MusicSync sync) => sync switch
        {
            MusicSync.Beat => FramesPerBeat,
            MusicSync.Bar => FramesPerBar,
            MusicSync.Phrase => FramesPerBar * PhraseBars,
            _ => 0,
        };

        /// <summary>The first boundary of <paramref name="sync"/> at or after <paramref name="musicalFrame"/> — a frame
        /// exactly on a line counts — or the frame itself for Immediate. Phrases count from bar 1 (bars 0, 4, 8 … at four bars
        /// a phrase).</summary>
        public double NextBoundary(double musicalFrame, MusicSync sync)
        {
            double unit = Unit(sync);
            if (unit <= 0) return musicalFrame;
            return Math.Ceiling(musicalFrame / unit - 1e-9) * unit;
        }

        /// <summary>When a change asked for now lands: the first boundary at or after now + <paramref name="leadFrames"/> —
        /// the lead keeps a boundary the mixer is about to pass from being promised and then missed (the change waits
        /// for the next one instead).</summary>
        public double ScheduleFrame(double musicalNow, MusicSync sync, double leadFrames)
            => sync == MusicSync.Immediate ? musicalNow + leadFrames : NextBoundary(musicalNow + leadFrames, sync);

        /// <summary>The music's own clock (frames it has played) at a musical frame, from one reading of both — exact while
        /// nothing seeks in between (decoded stems loop whole bars, so passes add no gap).</summary>
        public static ulong PlayedFrameAt(double targetMusical, double musicalNow, ulong playedNow)
        {
            double d = Math.Round(targetMusical - musicalNow);
            if (d <= 0) return playedNow;
            return playedNow + (ulong)d;
        }
    }

    /// <summary>
    /// Which beats OnBeat / OnBar deliver on a game tick (#437): every beat whose line lies before the midpoint between this
    /// tick and the next one expected (<c>dt</c> later) fires now — the tick nearest to the moment the mixer reaches it,
    /// within half a frame. A few missed beats (a hitch) are delivered late but in order; after a jump (a seek, a restart,
    /// a long stall) the skipped beats are dropped rather than fired in a burst, and the count resumes from there.
    /// </summary>
    public sealed class BeatTracker
    {
        /// <summary>More pending beats than this at once = a jump: they are skipped, not delivered.</summary>
        public const int MaxCatchUp = 4;

        private readonly MusicTiming _t;
        private long _last;
        private bool _started;

        public BeatTracker(MusicTiming timing) { _t = timing; }

        /// <summary>The last beat delivered (or skipped); long.MinValue before the first.</summary>
        public long LastBeat => _started ? _last : long.MinValue;

        /// <summary>The beats to deliver on a tick at musical frame <paramref name="musicalNow"/> with the next tick
        /// expected <paramref name="dtFrames"/> later; appended to <paramref name="beats"/> in order. Beats before bar 1
        /// (a pickup) are not delivered.</summary>
        public void Advance(double musicalNow, double dtFrames, System.Collections.Generic.List<long> beats)
        {
            double dt = Math.Max(0, dtFrames);
            long upTo = _t.BeatAt(musicalNow + dt * 0.5);
            if (!_started || upTo < _last || upTo - _last > MaxCatchUp)
            {
                // the first tick, or a jump (back, or forward past more than a hitch): the beat at hand fires only when
                // its line was crossed within the last frame — one that began half a beat ago is history
                _started = true;
                bool fresh = _t.FrameOfBeat(upTo) > musicalNow - Math.Max(dt, 1.0);
                _last = fresh ? upTo - 1 : upTo;
            }
            for (long b = _last + 1; b <= upTo; b++)
                if (b >= 0) beats.Add(b);
            if (upTo > _last) _last = upTo;
        }

        public void Reset() { _started = false; }
    }
}
