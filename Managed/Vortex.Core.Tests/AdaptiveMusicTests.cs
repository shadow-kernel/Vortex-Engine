using System;
using System.Collections.Generic;
using Editor.Core.Audio;

namespace VortexTests
{
    /// <summary>Adaptive music (#437) without an audio device: the bar and beat maths, where transitions land, the rules,
    /// the stem volumes of states and parameters, which beats a game tick delivers, and the .vmusic format.</summary>
    public static class AdaptiveMusicTests
    {
        private const double Rate = 48000;

        [Test]
        public static void BarAndBeatMathsFollowTheTempo(TestContext t)
        {
            var m = new MusicTiming(120, 4, 4, 0, Rate);
            t.Equal(24000.0, m.FramesPerBeat, "120 BPM at 48 kHz: half a second a beat");
            t.Equal(96000.0, m.FramesPerBar, "4/4: four beats a bar");
            t.Equal(0L, m.BeatAt(0), "bar 1 begins with beat 0");
            t.Equal(0L, m.BeatAt(23999), "the last frame of beat 0");
            t.Equal(1L, m.BeatAt(24000), "beat 1 begins exactly on its line");
            t.Equal(-1L, m.BeatAt(-1), "a pickup before bar 1 is beat -1");
            t.Equal(2L, m.BarAt(2 * 96000 + 5), "bar index");

            // an odd tempo and metre: the grid never drifts, however many beats in
            var odd = new MusicTiming(133, 7, 4, 0, Rate);
            t.True(Math.Abs(odd.FramesPerBeat - 48000.0 * 60 / 133) < 1e-9, "133 BPM: fractional frames per beat");
            t.Equal(100000L, odd.BeatAt(odd.FrameOfBeat(100000)), "beat 100000 starts where its line says");
            t.Equal(99999L, odd.BeatAt(odd.FrameOfBeat(100000) - 1), "one frame before it is still beat 99999");
            t.Equal(5000L, odd.BarAt(odd.FrameOfBar(5000)), "bar 5000 of 7/8");

            // a half-second pickup: bar 1 begins half a second into the stems
            var pickup = new MusicTiming(120, 4, 4, 0.5, Rate);
            t.Equal(0.0, pickup.MusicalFrame(24000, 0, 24000, 24000 + 8 * 96000), "bar 1 at 0.5 s into the stems is musical frame 0");
            t.Equal(-1L, pickup.BeatAt(pickup.MusicalFrame(1000, 0, 24000, 24000 + 8 * 96000)), "the pickup is before beat 0");
        }

        [Test]
        public static void MusicalPositionCountsThroughLoops(TestContext t)
        {
            var m = new MusicTiming(120, 4, 4, 0.5, Rate);
            var (start, end) = m.LoopRegion(8, 0);
            t.Equal(24000UL, start, "the loop begins at bar 1 (after the pickup)");
            t.Equal(24000UL + 8 * 96000, end, "eight bars later");
            t.Equal(3 * 8 * 96000.0 + 100, m.MusicalFrame(start + 100, 3, start, end), "three passes in: beats keep counting up");

            // continuous across the wrap: the frame before it and the loop start one pass later are neighbours
            double before = m.MusicalFrame(end - 1, 0, start, end), after = m.MusicalFrame(start, 1, start, end);
            t.Equal(1.0, after - before, "the wrap is seamless on the beat grid");

            // LoopBars 0: the stems' length rounded to whole bars, a few samples either way
            var whole = new MusicTiming(120, 4, 4, 0, Rate);
            t.Equal((0UL, 8UL * 96000), whole.LoopRegion(0, 8 * 96000 + 30), "30 samples of tail: still eight bars");
            t.Equal((0UL, 8UL * 96000), whole.LoopRegion(0, 8 * 96000 - 20), "20 samples short: eight bars (the stem is silent for them)");
            t.Equal((0UL, 0UL), whole.LoopRegion(0, 1000), "shorter than a bar: let the stems decide");

            // a streamed music loops its whole file: a pass counts as its length rounded to bars
            t.Equal(8 * 96000.0, whole.PassLength(0, 8 * 96000 + 12), "a stream 12 samples longer than eight bars counts eight bars a pass");
        }

        [Test]
        public static void TransitionsLandOnTheNextBoundary(TestContext t)
        {
            var m = new MusicTiming(120, 4, 4, 0, Rate);
            t.Equal(24000.0, m.NextBoundary(1000, MusicSync.Beat), "the next beat line");
            t.Equal(24000.0, m.NextBoundary(24000, MusicSync.Beat), "a frame on a line is that line");
            t.Equal(192000.0, m.NextBoundary(96001, MusicSync.Bar), "one frame past bar 1: bar 2");
            t.Equal(8 * 96000.0, m.NextBoundary(5 * 96000 + 1, MusicSync.Phrase), "four-bar phrases: from bar 5 to bar 8");
            t.Equal(1234.0, m.NextBoundary(1234, MusicSync.Immediate), "immediate: now");

            double lead = AdaptiveMusicPlayer.LeadSeconds * Rate;
            t.Equal(96000.0, m.ScheduleFrame(90000, MusicSync.Bar, lead), "125 ms before the bar line: that line");
            t.Equal(192000.0, m.ScheduleFrame(95500, MusicSync.Bar, lead), "10 ms before it — closer than the lead: the following bar");
            t.Equal(90000 + lead, m.ScheduleFrame(90000, MusicSync.Immediate, lead), "immediate: just past the mixer's block");

            // the music clock frame of a musical frame, from one reading of both
            t.Equal(1006000UL, MusicTiming.PlayedFrameAt(96000, 90000, 1000000), "6000 frames after now");
            t.Equal(1000000UL, MusicTiming.PlayedFrameAt(80000, 90000, 1000000), "a boundary already passed: now");
        }

        [Test]
        public static void TransitionRulesPickTheMostSpecific(TestContext t)
        {
            var music = new AdaptiveMusic();
            var exact = new AdaptiveMusic.Transition { From = "stealth", To = "combat", Sync = "beat" };
            var toCombat = new AdaptiveMusic.Transition { From = "*", To = "combat", Sync = "immediate" };
            var fromStealth = new AdaptiveMusic.Transition { From = "stealth", To = "", Sync = "phrase" };
            music.Transitions.AddRange(new[] { toCombat, fromStealth, exact });
            music.DefaultTransition = new AdaptiveMusic.Transition { Sync = "bar" };
            t.True(ReferenceEquals(exact, music.TransitionFor("stealth", "combat")), "from→to wins");
            t.True(ReferenceEquals(toCombat, music.TransitionFor("alert", "combat")), "*→to next");
            t.True(ReferenceEquals(fromStealth, music.TransitionFor("stealth", "alert")), "from→* next");
            t.True(ReferenceEquals(music.DefaultTransition, music.TransitionFor("alert", "stealth")), "else the default rule");
            t.True(ReferenceEquals(exact, music.TransitionFor("STEALTH", "Combat")), "state names ignore case");
            t.Equal(MusicSync.Beat, exact.SyncMode, "sync parsed");
            foreach (var (text, sync) in new[] { ("next bar", MusicSync.Bar), ("End of Phrase", MusicSync.Phrase), ("now", MusicSync.Immediate), ("next_beat", MusicSync.Beat) })
                t.Equal(sync, AdaptiveMusic.ParseSync(text, MusicSync.Beat), "'" + text + "'");
            t.Equal(MusicSync.Bar, AdaptiveMusic.ParseSync("whenever", MusicSync.Bar), "unknown: the fallback");
        }

        [Test]
        public static void StatesAndParametersSetStemVolumes(TestContext t)
        {
            var music = new AdaptiveMusic();
            foreach (var n in new[] { "pads", "drums", "brass" }) music.Stems.Add(new AdaptiveMusic.Stem { Name = n });
            var alert = new AdaptiveMusic.State { Name = "alert" };
            alert.Volumes["PADS"] = 0.5f; alert.Volumes["drums"] = 1f;   // brass not listed: silent
            music.States.Add(alert);
            music.Normalize();
            var g = music.StateGains(music.FindState("Alert"));
            t.True(g[0] == 0.5f && g[1] == 1f && g[2] == 0f, "listed stems at their volume (names ignore case), the rest silent: " + string.Join(",", g));
            t.True(Array.TrueForAll(music.StateGains(null), x => x == 1f), "music without states plays every stem");

            t.True(Math.Abs(AdaptiveMusic.ParameterGain(0.45f, 0.3f, 0.6f) - 0.5f) < 1e-6f, "halfway up the ramp");
            t.Equal(0f, AdaptiveMusic.ParameterGain(0.1f, 0.3f, 0.6f), "below From: silent");
            t.Equal(1f, AdaptiveMusic.ParameterGain(0.9f, 0.3f, 0.6f), "above To: full");
            t.True(Math.Abs(AdaptiveMusic.ParameterGain(0.6f, 0.8f, 0.5f) - 2f / 3f) < 1e-6f, "From above To fades out as it rises");
            t.Equal(1f, AdaptiveMusic.ParameterGain(0.7f, 0.7f, 0.7f), "From == To: a switch at that value");
            music.Stems[1].Parameter = "intensity"; music.Stems[1].From = 0.3f; music.Stems[1].To = 0.6f;
            var p = music.ParameterGains(new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["Intensity"] = 0.45f });
            t.True(p[0] == 1f && Math.Abs(p[1] - 0.5f) < 1e-6f && p[2] == 1f, "only the mapped stem follows the parameter: " + string.Join(",", p));
            t.True(music.ParameterGains(new Dictionary<string, float>())[1] == 0f, "a parameter never set counts as 0");
        }

        /// <summary>Simulates a game at <paramref name="fps"/> over <paramref name="seconds"/> of 120 BPM music; returns each
        /// delivered beat with how far from its line the delivering tick was (seconds).</summary>
        private static List<(long Beat, double Error)> Deliver(double fps, double seconds, Func<double, double> musicAt = null)
        {
            var m = new MusicTiming(120, 4, 4, 0, Rate);
            var tracker = new BeatTracker(m);
            var got = new List<(long, double)>();
            var beats = new List<long>();
            double dt = 1.0 / fps;
            for (double wall = 0; wall < seconds; wall += dt)
            {
                double music = (musicAt ?? (w => w))(wall) * Rate;
                beats.Clear();
                tracker.Advance(music, dt * Rate, beats);
                foreach (var b in beats) got.Add((b, (music - m.FrameOfBeat(b)) / Rate));
            }
            return got;
        }

        [Test]
        public static void BeatsArriveOnTheNearestTick(TestContext t)
        {
            // the music starts on the first tick (as Audio.Music.Play in an Update does) 3.7 ms of frame time late, so the
            // later lines fall between ticks; 59.94 fps does not divide the beat either
            foreach (double fps in new[] { 59.94, 144.0 })
            {
                var got = Deliver(fps, 10.0, w => Math.Max(0, w - 0.0037));
                t.True(got.Count == 20 || got.Count == 21, fps + " fps: every beat of 10 s at 120 BPM once (" + got.Count + ")");
                for (int i = 0; i < got.Count; i++) t.Equal((long)i, got[i].Beat, fps + " fps: in order");
                double worst = 0;
                foreach (var g in got) worst = Math.Max(worst, Math.Abs(g.Error));
                t.True(worst <= 0.5 / fps + 1e-6, fps + " fps: within half a frame of the line (" + (worst * 1000).ToString("0.00") + " ms)");
                if (fps >= 100) t.True(worst <= 0.005, "at " + fps + " fps within 5 ms (" + (worst * 1000).ToString("0.00") + " ms)");
            }

            // a 400 ms hitch over the 2.5 s line: that beat comes late, in order — nothing is lost
            var hitch = Deliver(60, 4.0, w => w < 2.4 ? w : (w < 2.8 ? 2.4 : w));
            var order = hitch.ConvertAll(g => g.Beat);
            t.True(order.Count >= 8 && order.TrueForAll(b => b == order.IndexOf(b)), "a hitch delays a beat but loses none: " + string.Join(",", order));
            var late = hitch.Find(g => g.Beat == 5);
            t.True(late.Error > 0.25 && late.Error < 0.32, "the swallowed beat (2.5 s) arrives when the game resumes at 2.8 s (" + late.Error + ")");

            // a seek 30 s ahead at 1.2 s: no burst of the 60 skipped beats, the count resumes at the next line
            var seek = Deliver(60, 3.0, w => w < 1.2 ? w : w + 30.0);
            var s = seek.ConvertAll(g => g.Beat);
            t.True(s.Contains(2) && s.Contains(63) && s.TrueForAll(b => b <= 2 || b >= 63) && s.Count <= 7, "a jump forward fires no burst: " + string.Join(",", s));

            // a seek back from 3.1 s to 1.25 s: nothing until the next line (beat 3 at 1.5 s), the beat it landed in stays quiet
            var back = Deliver(60, 4.0, w => w < 3.1 ? w : w - 1.85).ConvertAll(g => g.Beat);
            int jump = back.IndexOf(6) + 1;
            var after = jump > 0 ? back.GetRange(jump, back.Count - jump) : new List<long>();
            t.True(jump > 0 && after.Count == 2 && after[0] == 3 && after[1] == 4, "after a jump back: beats 3, 4 (" + string.Join(",", back) + ")");
        }

        /// <summary>#465 review: Time set while the source is stopped reads back where the next Play starts.</summary>
        [Test]
        public static void TimeSetWhileStoppedLandsInTheClip(TestContext t)
        {
            double P(double s, double len, bool loop) => Editor.Core.Services.AudioPlaybackService.PositionInClip(s, len, loop);
            t.Equal(1.5, P(1.5, 4, false), "inside the clip: as set");
            t.Equal(4.0, P(9, 4, false), "past a one-shot's end: its end (Play ends at once)");
            t.Equal(1.0, P(9, 4, true), "past a looping clip's end: wrapped");
            t.Equal(0.0, P(4, 4, true), "exactly a looping clip's length: its start");
            t.Equal(9.0, P(9, 0, false), "unknown length (a sound container): as set — the clip it rolls decides at Play");
            t.Equal(0.0, P(double.NaN, 4, true), "NaN: the start");
            t.Equal(0.0, P(-2, 4, true), "negative: the start");
        }

        [Test]
        public static void VmusicKeepsItsDefaultsAndRoundTrips(TestContext t)
        {
            // a hand-written minimal file: everything it leaves out takes the documented default
            var m = AdaptiveMusic.FromJson("{ \"stems\": [ { \"name\": \"pads\", \"clipPath\": \"Assets/Audio/pads.wav\" } ] }");
            t.Equal(120f, m.Bpm, "BPM default");
            t.Equal(4, m.BeatsPerBar, "4/4");
            t.True(m.Loop && m.Volume == 1f && m.PhraseBars == 4 && m.ParameterFadeSeconds == 0.5f, "loop, volume, phrase, parameter fade defaults");
            t.True(m.DefaultTransition != null && m.DefaultTransition.SyncMode == MusicSync.Bar && Math.Abs(m.DefaultTransition.FadeSeconds - 0.1f) < 1e-6f, "the default rule: next bar, 0.1 s");
            t.True(m.Stems[0].To == 1f && m.Stems[0].Parameter == "", "a stem's defaults");

            var full = new AdaptiveMusic { Bpm = 96, BeatsPerBar = 3, OffsetSeconds = 0.25f, LoopBars = 16, InitialState = "calm", Stream = true };
            full.Stems.Add(new AdaptiveMusic.Stem { Name = "drums", ClipPath = "Assets/Audio/drums.ogg", Parameter = "intensity", From = 0.2f, To = 0.7f });
            var calm = new AdaptiveMusic.State { Name = "calm" }; calm.Volumes["drums"] = 0.25f; full.States.Add(calm);
            full.Transitions.Add(new AdaptiveMusic.Transition { From = "calm", To = "fight", Sync = "beat", FadeSeconds = 0.05f, Stinger = "hit" });
            full.Stingers.Add(new AdaptiveMusic.Stinger { Name = "hit", ClipPath = "Assets/Audio/hit.wav", Volume = 0.8f, Sync = "bar" });
            full.Parameters["intensity"] = 0.3f;
            string json = full.ToJson();
            var back = AdaptiveMusic.FromJson(json);
            t.True(back.Bpm == 96 && back.BeatsPerBar == 3 && back.OffsetSeconds == 0.25f && back.LoopBars == 16 && back.Stream && back.InitialState == "calm", "settings survive: " + json);
            t.True(back.Stems[0].Parameter == "intensity" && back.Stems[0].From == 0.2f && back.Stems[0].To == 0.7f, "the stem's mapping survives");
            t.Equal(0.25f, back.FindState("CALM").Volumes["Drums"], "state volumes survive, names ignore case");
            t.True(back.Transitions[0].Stinger == "hit" && back.Transitions[0].SyncMode == MusicSync.Beat, "the rule survives");
            t.True(back.FindStinger("hit").SyncMode == MusicSync.Bar && back.FindStinger("hit").Volume == 0.8f, "the stinger survives");
            t.Equal(0.3f, back.Parameters["Intensity"], "start values of parameters survive");
            t.True(json.Contains("\"intensity\": 0.3") || json.Contains("\"intensity\":0.3"), "parameters are a plain JSON object: " + json);

            var bad = AdaptiveMusic.FromJson("{ \"bpm\": 0, \"beatsPerBar\": 0, \"phraseBars\": -2, \"volume\": -1 }");
            t.True(bad.Bpm == 20f && bad.BeatsPerBar == 1 && bad.PhraseBars == 1 && bad.Volume == 1f, "nonsense is clamped to something playable");
            t.Equal("calm", full.StartState().Name, "the initial state");
            t.True(AdaptiveMusic.IsMusicPath("Assets/Audio/Music/Spy.VMUSIC") && !AdaptiveMusic.IsMusicPath("a.wav"), "the file type");
        }

        private sealed class Clapper : Vortex.VortexBehaviour { public override void OnBeat(int beat) { } }
        private sealed class Conductor : Vortex.VortexBehaviour { public override void OnBar(int bar) { } public override void OnBeat(int beat) { } }
        private sealed class Walker : Vortex.VortexBehaviour { public override void Update(float dt) { } }

        [Test]
        public static void OnlyScriptsThatOverrideThemGetBeats(TestContext t)
        {
            t.Equal(1, Editor.Scripting.ScriptRuntime.MusicCallbacks(typeof(Clapper)), "OnBeat only");
            t.Equal(3, Editor.Scripting.ScriptRuntime.MusicCallbacks(typeof(Conductor)), "OnBar and OnBeat");
            t.Equal(0, Editor.Scripting.ScriptRuntime.MusicCallbacks(typeof(Walker)), "neither: never called");
        }

        [Test]
        public static void AdaptiveMusicApiCompiles(TestContext t)
        {
            const string code = @"using Vortex;
public class Band : VortexBehaviour
{
    float danger;
    public override void Start() { Audio.Music.Play(""Assets/Audio/Music/spy.vmusic"", 1f); }
    public override void Update(float dt)
    {
        danger += dt * 0.1f;
        Audio.Music.SetParameter(""intensity"", danger);
        if (danger > 0.5f && Audio.Music.State != ""combat"") Audio.Music.SetState(""combat"");
        bool hit = Audio.Music.PlayStinger(""spotted"");
        float bpm = Audio.Music.Bpm + Audio.Music.GetParameter(""intensity"");
        int where = Audio.Music.Bar * Audio.Music.BeatsPerBar + Audio.Music.Beat;
    }
    public override void OnBeat(int beat) { if (beat % Audio.Music.BeatsPerBar == 0) PlayAnimation(""Clap""); }
    public override void OnBar(int bar) { if (bar % 4 == 0) Debug.Log(""phrase "" + bar / 4); }
}";
            var errors = Editor.Scripting.RoslynScriptCompiler.CheckSources(new[] { ("Band.cs", code) });
            t.True(errors.Count == 0, string.Join(" | ", errors.ConvertAll(e => e.Id + " " + e.Message)));
        }

        [Test]
        public static void AdaptiveMusicIsInTheScriptingReference(TestContext t)
        {
            string Doc(System.Reflection.MemberInfo m) => Editor.Scripting.ScriptApiDocs.Summary(m) ?? "";
            var music = typeof(Vortex.Audio.Music);
            string setState = Doc(music.GetMethod("SetState")), param = Doc(music.GetMethod("SetParameter"));
            string onBeat = Doc(typeof(Vortex.VortexBehaviour).GetMethod("OnBeat")), cls = Doc(music);
            t.True(setState.Contains("next bar") && setState.Contains("crossfade") && setState.Contains("every frame"), "SetState: " + setState);
            t.True(param.Contains("0..1") && param.Contains("From"), "SetParameter: " + param);
            t.True(onBeat.Contains("half a frame") && onBeat.Contains("BeatsPerBar"), "OnBeat: " + onBeat);
            t.True(cls.Contains(".vmusic"), "the Music class names the asset: " + cls);
            foreach (var name in new[] { "PlayStinger", "GetParameter" }) t.True(Doc(music.GetMethod(name)).Length > 0, name + " has a summary");
            foreach (var name in new[] { "State", "Bpm", "BeatsPerBar", "Beat", "Bar" }) t.True(Doc(music.GetProperty(name)).Length > 0, name + " has a summary");
            t.True(Doc(typeof(Vortex.VortexBehaviour).GetMethod("OnBar")).Length > 0, "OnBar has a summary");
        }
    }
}
