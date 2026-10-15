using System;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Audio;

namespace VortexTests
{
    /// <summary>The audio bridge's rules that need no audio device: where a Reverb Zone counts (#497), when an Audio
    /// Source plays and which Audio Listener hears.</summary>
    public static class AudioTests
    {
        private static GameEntity Entity(string name, GameEntity parent = null, Vector3? pos = null, Vector3? rot = null, Vector3? scale = null)
        {
            var e = new GameEntity { Name = name };
            var t = new Transform { Entity = e };
            if (pos.HasValue) t.LocalPosition = pos.Value;
            if (rot.HasValue) t.LocalRotation = rot.Value;
            if (scale.HasValue) t.LocalScale = scale.Value;
            e.Components.Add(t);
            if (parent != null) parent.AddChild(e);
            return e;
        }

        private static ReverbZone Zone(GameEntity e, int shape, Vector3 extents, float radius = 10f, float falloff = 3f)
        {
            var z = new ReverbZone(e) { Shape = shape, BoxExtents = extents, Radius = radius, Falloff = falloff };
            e.Components.Add(z);
            return z;
        }

        [Test]
        public static void ReverbZoneCountsOnlyWhileActiveInTheHierarchy(TestContext t)
        {
            // (re-activating an entity creates its engine twin, so every case gets its own hierarchy)
            ReverbZone ZoneIn(GameEntity parent, out GameEntity e) { e = Entity("Zone", parent); return Zone(e, (int)ReverbZoneShape.Box, new Vector3(4, 3, 10)); }
            var p = new Vector3(0, 0, 0);

            var zone = ZoneIn(Entity("Group"), out var e);
            t.Equal(1f, AudioPlaybackService.ZoneWeight(zone, e, p), "an active zone, listener inside");

            var act = Entity("Act 2");
            zone = ZoneIn(act, out e);
            act.IsActive = false;
            t.Equal(0f, AudioPlaybackService.ZoneWeight(zone, e, p), "the zone's parent is inactive (#497)");

            var outer = Entity("World");
            zone = ZoneIn(Entity("Act 3", outer), out e);
            outer.IsActive = false;
            t.Equal(0f, AudioPlaybackService.ZoneWeight(zone, e, p), "an ancestor two levels up is inactive");

            zone = ZoneIn(Entity("Group"), out e);
            e.IsActive = false;
            t.Equal(0f, AudioPlaybackService.ZoneWeight(zone, e, p), "the zone's own entity is inactive");

            zone = ZoneIn(Entity("Group"), out e);
            zone.IsEnabled = false;
            t.Equal(0f, AudioPlaybackService.ZoneWeight(zone, e, p), "the component is disabled");
        }

        [Test]
        public static void SourcePlaysOnlyWhileActiveInTheHierarchy(TestContext t)
        {
            AudioSource SourceIn(GameEntity parent, out GameEntity e)
            {
                e = Entity("Radio", parent);
                var s = new AudioSource(e, "Assets/Audio/radio.wav");
                e.Components.Add(s);
                return s;
            }

            var src = SourceIn(Entity("Group"), out var e);
            t.True(AudioPlaybackService.ShouldPlay(true, src, e), "an active source with a clip plays");
            t.False(AudioPlaybackService.ShouldPlay(false, src, e), "not wanted (no Play On Awake, no Play())");

            var act = Entity("Act 2");
            src = SourceIn(act, out e);
            act.IsActive = false;
            t.False(AudioPlaybackService.ShouldPlay(true, src, e), "the source's parent is inactive");

            var outer = Entity("World");
            src = SourceIn(Entity("Act 3", outer), out e);
            outer.IsActive = false;
            t.False(AudioPlaybackService.ShouldPlay(true, src, e), "an ancestor two levels up is inactive");

            src = SourceIn(Entity("Group"), out e);
            e.IsActive = false;
            t.False(AudioPlaybackService.ShouldPlay(true, src, e), "the source's own entity is inactive");

            src = SourceIn(Entity("Group"), out e);
            src.IsEnabled = false;
            t.False(AudioPlaybackService.ShouldPlay(true, src, e), "the component is disabled");

            src = SourceIn(Entity("Group"), out e);
            src.AudioClipPath = "";
            t.False(AudioPlaybackService.ShouldPlay(true, src, e), "no clip");
        }

        [Test]
        public static void EarsFollowTheFirstListenerActiveInTheHierarchy(TestContext t)
        {
            GameEntity ListenerIn(string name, GameEntity parent)
            {
                var e = Entity(name, parent);
                e.Components.Add(new AudioListener(e));
                return e;
            }

            var cutsceneRig = Entity("Cutscene Rig");
            var cutscene = ListenerIn("Cutscene Camera", cutsceneRig);
            var player = ListenerIn("Player Camera", Entity("Player"));
            var listeners = new[] { cutscene, player };

            cutsceneRig.IsActive = false;
            t.Equal(player, AudioPlaybackService.ActiveListener(listeners, out int n), "the cutscene rig is off: the player's camera hears");
            t.Equal(1, n, "one listener active");

            var rig2 = Entity("Cutscene Rig 2");
            var cutscene2 = ListenerIn("Cutscene Camera 2", rig2);
            t.Equal(cutscene2, AudioPlaybackService.ActiveListener(new[] { cutscene2, player }, out n), "both active: the first in hierarchy order");
            t.Equal(2, n, "two listeners active at once");

            cutscene2.GetComponent<AudioListener>().IsEnabled = false;
            t.Equal(player, AudioPlaybackService.ActiveListener(new[] { cutscene2, player }, out n), "a disabled listener does not hear");

            var lone = ListenerIn("Lone", Entity("Off"));
            lone.Parent.IsActive = false;
            t.Equal(null, AudioPlaybackService.ActiveListener(new[] { lone }, out n), "no active listener: the main camera hears");
            t.Equal(0, n, "none active");
        }

        [Test]
        public static void BoxZoneIsOrientedAndScaledByItsEntity(TestContext t)
        {
            // a corridor 2 x 2 x 20 m along its local z, turned 90 degrees in yaw: it now runs along world x
            var corridor = Entity("Corridor", rot: new Vector3(0, 90, 0));
            var zone = Zone(corridor, (int)ReverbZoneShape.Box, new Vector3(1, 1, 10));
            t.Equal(1f, AudioPlaybackService.ZoneWeight(zone, corridor, new Vector3(8, 0, 0)), "8 m along the turned corridor: inside");
            t.Equal(0f, AudioPlaybackService.ZoneWeight(zone, corridor, new Vector3(0, 0, 8)), "8 m along world z (the unturned box): outside");

            // a 4 x 4 x 4 m box scaled (3, 1, 1): 12 m wide; the falloff (3 m) stays world metres
            var hall = Entity("Hall", scale: new Vector3(3, 1, 1));
            var hz = Zone(hall, (int)ReverbZoneShape.Box, new Vector3(2, 2, 2));
            t.Equal(1f, AudioPlaybackService.ZoneWeight(hz, hall, new Vector3(5.5f, 0, 0)), "inside the scaled width");
            float w = AudioPlaybackService.ZoneWeight(hz, hall, new Vector3(7, 0, 0));
            t.True(w > 0.6f && w < 0.85f, "1 m outside the scaled box: inside the 3 m falloff (" + w + ")");
            t.Equal(0f, AudioPlaybackService.ZoneWeight(hz, hall, new Vector3(0, 0, 5.5f)), "z is not scaled: 3.5 m outside, past the falloff");

            // parents count: a zone 10 m in front of a turned, moved parent
            var root = Entity("Act", pos: new Vector3(100, 0, 0), rot: new Vector3(0, 90, 0));
            var room = Entity("Room", root, pos: new Vector3(0, 0, 10));
            var rz = Zone(room, (int)ReverbZoneShape.Box, new Vector3(1, 1, 3));
            t.Equal(1f, AudioPlaybackService.ZoneWeight(rz, room, new Vector3(110, 0, 0)), "the child zone sits where its parent put it");
            t.Equal(1f, AudioPlaybackService.ZoneWeight(rz, room, new Vector3(112.5f, 0, 0)), "and is turned with it (its long axis along world x)");
            t.Equal(0f, AudioPlaybackService.ZoneWeight(rz, room, new Vector3(110, 0, 5)), "4 m across the turned room: past the falloff");
        }

        [Test]
        public static void ScaledZonesAreNamedInANote(TestContext t)
        {
            // #497 changes the size of zones in scaled transforms in existing scenes: play names each of them once
            var plain = Entity("Plain Room", pos: new Vector3(5, 0, 0), rot: new Vector3(0, 30, 0));
            t.True(AudioPlaybackService.ScaledZoneNote(Zone(plain, (int)ReverbZoneShape.Box, new Vector3(4, 3, 4)), plain) == null,
                "a moved and turned zone at scale 1 needs no note");
            var act = Entity("Act 1", scale: new Vector3(2, 1, 2));
            var hall = Entity("Hall", act);
            string note = AudioPlaybackService.ScaledZoneNote(Zone(hall, (int)ReverbZoneShape.Box, new Vector3(4, 3, 4)), hall);
            t.True(note != null && note.Contains("'Hall'") && note.Contains("world scale 2, 1, 2") && note.Contains("box"), "a zone under a scaled parent: " + note);
            var dome = Entity("Dome", scale: new Vector3(0.5f, 0.5f, 0.5f));
            string sphere = AudioPlaybackService.ScaledZoneNote(Zone(dome, (int)ReverbZoneShape.Sphere, new Vector3(1, 1, 1)), dome);
            t.True(sphere != null && sphere.Contains("'Dome'") && sphere.Contains("sphere"), "a scaled sphere zone: " + sphere);
        }

        [Test]
        public static void SphereZoneScalesWithTheLargestAxis(TestContext t)
        {
            var e = Entity("Dome", pos: new Vector3(0, 10, 0), scale: new Vector3(1, 3, 1));
            var zone = Zone(e, (int)ReverbZoneShape.Sphere, new Vector3(1, 1, 1), radius: 2f, falloff: 1f);
            t.Equal(1f, AudioPlaybackService.ZoneWeight(zone, e, new Vector3(5, 10, 0)), "radius 2 x scale 3 = 6 m: 5 m from the centre is inside");
            t.Equal(0f, AudioPlaybackService.ZoneWeight(zone, e, new Vector3(7.5f, 10, 0)), "7.5 m: past radius + falloff");
            var flat = Entity("Flat", scale: new Vector3(1, 0, 1));
            var fz = Zone(flat, (int)ReverbZoneShape.Box, new Vector3(5, 5, 5));
            t.Equal(0f, AudioPlaybackService.ZoneWeight(fz, flat, new Vector3(0, 0, 0)), "a box scaled to zero height has no volume");
        }

        [Test]
        public static void OnlyStartingSilentThenFadingInTakesTheLevelOver(TestContext t)
        {
            // #404: the envelope multiplies Volume, so "Volume = 0; Play(); FadeTo(0.95f, 4f)" stayed silent forever
            var silent = new AudioSource { Volume = 0f };
            t.Equal(AudioPlaybackService.FadeStart.TakesOverLevel, AudioPlaybackService.PlanFade(silent, 0.95f, freshStart: true),
                "Play() and the fade in the same frame at Volume 0: the envelope carries the level");
            t.Equal(AudioPlaybackService.FadeStart.TakesOverLevel, AudioPlaybackService.PlanFade(silent, 1f, freshStart: true), "FadeIn (always a fresh start) on Volume 0");
            // a radio muted with Volume = 0 while it plays stays silent when a dialogue system ducks everything
            t.Equal(AudioPlaybackService.FadeStart.SilentVolume, AudioPlaybackService.PlanFade(silent, 0.3f, freshStart: false),
                "a source already playing at Volume 0: the fade multiplies 0 (and warns)");
            t.Equal(AudioPlaybackService.FadeStart.Composed, AudioPlaybackService.PlanFade(silent, 0f, freshStart: true), "fading to 0: nothing to take over or warn about");
            foreach (bool fresh in new[] { true, false })
            {
                t.Equal(AudioPlaybackService.FadeStart.Composed, AudioPlaybackService.PlanFade(new AudioSource { Volume = 0.001f }, 0.95f, fresh),
                    "any non-zero Volume composes exactly as before (fresh=" + fresh + ")");
                t.Equal(AudioPlaybackService.FadeStart.Muted, AudioPlaybackService.PlanFade(new AudioSource { Volume = 1f, Mute = true }, 0.95f, fresh),
                    "Mute stays muted and warns (fresh=" + fresh + ")");
            }
            t.Equal(AudioPlaybackService.FadeStart.Composed, AudioPlaybackService.PlanFade(silent, float.NaN, true), "a NaN target changes nothing");
        }

        [Test]
        public static void FadeRuleIsInTheScriptingReference(TestContext t)
        {
            // what get_scripting_api prints under the AudioSource members (#404)
            string type = Editor.Scripting.ScriptApiDocs.Summary(typeof(Vortex.AudioSource)) ?? "";
            string fadeTo = Editor.Scripting.ScriptApiDocs.Summary(typeof(Vortex.AudioSource).GetMethod("FadeTo")) ?? "";
            string fadeIn = Editor.Scripting.ScriptApiDocs.Summary(typeof(Vortex.AudioSource).GetMethod("FadeIn")) ?? "";
            string volume = Editor.Scripting.ScriptApiDocs.Summary(typeof(Vortex.AudioSource).GetProperty("Volume")) ?? "";
            t.True(type.Contains("Volume × the fade envelope") && type.Contains("FadeTo(0.95f, 4f)") && type.Contains("same frame")
                   && type.Contains("logs one warning"), "the class states the rule, the silent-start idiom and its limit: " + type);
            t.True(fadeTo.Contains("Volume × envelope") && fadeTo.Contains("absolute level") && fadeTo.Contains("already playing at Volume 0"), "FadeTo: " + fadeTo);
            t.True(fadeIn.Contains("Volume 0"), "FadeIn: " + fadeIn);
            t.True(volume.Contains("fade envelope") && volume.Contains("right after Play()"), "Volume: " + volume);
        }

        [Test]
        public static void OneShotReverbMixDefaults(TestContext t)
        {
            // #496: a 3D one-shot reverberates like an Audio Source left at its default Reverb Zone Mix; 2D stays dry
            t.Equal(new AudioSource().ReverbZoneMix, AudioPlaybackService.DefaultOneShotReverbMix, "3D one-shot mix = an Audio Source's default");
            t.Equal(0f, AudioPlaybackService.DefaultOneShot2DReverbMix, "2D one-shots stay dry by default");
        }

        [Test]
        public static void OneShotCallsOfEveryShapeStillCompile(TestContext t)
        {
            // the reverbMix overloads (#496) must leave every call scripts already make binding as before — no ambiguity
            const string code = @"using Vortex;
public class Shots : VortexBehaviour
{
    public override void Update(float dt)
    {
        Audio.PlayOneShot(""a.wav"", Position);
        Audio.PlayOneShot(""a.wav"", Position, 0.5f);
        Audio.PlayOneShot(""a.wav"", Position, 0.5f, 1.2f);
        Audio.PlayOneShot(""a.wav"", Position, volume: 0.5f);
        Audio.PlayOneShot(""a.wav"", Position, pitch: 0.9f);
        Audio.PlayOneShot(""a.wav"", Position, 1f, 1f, 0.3f);
        Audio.PlayOneShot(""a.wav"", Position, 1f, 1f, reverbMix: 0f);
        Audio.PlayOneShot2D(""a.wav"");
        Audio.PlayOneShot2D(""a.wav"", 0.5f);
        Audio.PlayOneShot2D(""a.wav"", 0.5f, 1.2f);
        Audio.PlayOneShot2D(""a.wav"", pitch: 0.9f);
        Audio.PlayOneShot2D(""a.wav"", 1f, 1f, 0.8f);
    }
}";
            var errors = Editor.Scripting.RoslynScriptCompiler.CheckSources(new[] { ("Shots.cs", code) });
            t.True(errors.Count == 0, string.Join(" | ", errors.ConvertAll(e => e.Id + " " + e.Message)));
        }

        [Test]
        public static void TimeSamplesCountTheClipsOwnSamples(TestContext t)
        {
            // #465: a 44.1 kHz clip decoded for a 48 kHz mixer has its cursor in mixer frames; TimeSamples counts the clip's
            t.Equal(44100L, AudioPlaybackService.SamplesAtClipRate(48000, 48000, 44100), "one second");
            t.Equal(22050L, AudioPlaybackService.SamplesAtClipRate(24000, 48000, 44100), "half a second");
            t.Equal(26460000L, AudioPlaybackService.SamplesAtClipRate(28800000, 48000, 44100), "ten minutes in, still sample-exact");
            t.Equal(12345L, AudioPlaybackService.SamplesAtClipRate(12345, 44100, 44100), "same rate (a streamed pak clip): the cursor itself");
            t.Equal(777L, AudioPlaybackService.SamplesAtClipRate(777, 48000, 0), "unknown clip rate: the cursor");
        }

        [Test]
        public static void PlaybackPositionApiCompilesAndOldCallsStillBind(TestContext t)
        {
            // #465: the position members read like Unity's, and Time.DeltaTime still means the frame time in a behaviour
            const string code = @"using Vortex;
public class Fiesta : VortexBehaviour
{
    AudioSource band;
    double next;
    public override void Start() { band = GetAudioSource(); band.Time = 12.5f; band.Play(); next = Audio.DspTime + 0.5; }
    public override void Update(float dt)
    {
        float beatPhase = band.Time % 0.5f;
        float progress = band.NormalizedTime;
        int samples = band.TimeSamples;
        float len = band.Length;
        if (Audio.DspTime > next - 0.1) { band.PlayScheduled(next); next += 2.0; }
        band.NormalizedTime = 0.5f;
        band.TimeSamples = samples + 441;
        float music = Audio.Music.Time + Audio.Music.Length + Audio.Music.NormalizedTime + Audio.Music.TimeSamples;
        Audio.Music.Time = 3f;
        float frame = Time.DeltaTime;
        band.Play(); band.Stop(); band.Pause(); band.Resume(); band.FadeTo(0.5f, 1f);
        bool on = band.IsPlaying; band.Volume = 0.5f; band.Pitch = 1.1f; band.Loop = true; band.Clip = ""a.wav"";
    }
}";
            var errors = Editor.Scripting.RoslynScriptCompiler.CheckSources(new[] { ("Fiesta.cs", code) });
            t.True(errors.Count == 0, string.Join(" | ", errors.ConvertAll(e => e.Id + " " + e.Message)));
        }

        [Test]
        public static void PlaybackPositionIsInTheScriptingReference(TestContext t)
        {
            // what get_scripting_api prints under the new members (#465)
            string Doc(System.Reflection.MemberInfo m) => Editor.Scripting.ScriptApiDocs.Summary(m) ?? "";
            var src = typeof(Vortex.AudioSource);
            string time = Doc(src.GetProperty("Time")), scheduled = Doc(src.GetMethod("PlayScheduled"));
            string samples = Doc(src.GetProperty("TimeSamples")), dsp = Doc(typeof(Vortex.Audio).GetProperty("DspTime"));
            string music = Doc(typeof(Vortex.Audio.Music).GetProperty("Time"));
            t.True(time.Contains("mixer") && time.Contains("Pause") && time.Contains("Streaming") && time.Contains("while stopped"), "Time: " + time);
            t.True(scheduled.Contains("Sample-accurate") && scheduled.Contains("Audio.DspTime") && scheduled.Contains("IsPlaying"), "PlayScheduled: " + scheduled);
            t.True(samples.Contains("sample rate") && samples.Contains("timeSamples"), "TimeSamples: " + samples);
            t.True(dsp.Contains("dspTime") && dsp.Contains("PlayScheduled"), "DspTime: " + dsp);
            t.True(music.Contains("seek"), "Music.Time: " + music);
            foreach (var name in new[] { "Length", "NormalizedTime" })
                t.True(Doc(src.GetProperty(name)).Length > 0, "AudioSource." + name + " has a summary");
        }
    }
}
