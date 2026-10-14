using System;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Audio;

namespace VortexTests
{
    /// <summary>The audio bridge's rules that need no audio device: where a Reverb Zone counts (#497).</summary>
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
        public static void FadeOnASilentSourceTakesOverItsLevel(TestContext t)
        {
            // #404: the envelope multiplies Volume, so "Volume = 0; Play(); FadeTo(0.95f, 4f)" stayed silent forever
            var silent = new AudioSource { Volume = 0f };
            t.Equal(AudioPlaybackService.FadeStart.TakesOverLevel, AudioPlaybackService.PlanFade(silent, 0.95f), "Volume 0, fade up: the envelope carries the level");
            t.Equal(AudioPlaybackService.FadeStart.TakesOverLevel, AudioPlaybackService.PlanFade(silent, 1f), "FadeIn on Volume 0");
            t.Equal(AudioPlaybackService.FadeStart.Composed, AudioPlaybackService.PlanFade(silent, 0f), "fading to 0: nothing to take over");
            t.Equal(AudioPlaybackService.FadeStart.Composed, AudioPlaybackService.PlanFade(new AudioSource { Volume = 0.4f }, 0.95f),
                "any other Volume: the fade composes with it exactly as before (0.4 x 0.95)");
            t.Equal(AudioPlaybackService.FadeStart.Muted, AudioPlaybackService.PlanFade(new AudioSource { Volume = 1f, Mute = true }, 0.95f), "Mute stays muted (warned)");
            t.Equal(AudioPlaybackService.FadeStart.Composed, AudioPlaybackService.PlanFade(silent, float.NaN), "a NaN target changes nothing");
        }

        [Test]
        public static void FadeRuleIsInTheScriptingReference(TestContext t)
        {
            // what get_scripting_api prints under the AudioSource members (#404)
            string type = Editor.Scripting.ScriptApiDocs.Summary(typeof(Vortex.AudioSource)) ?? "";
            string fadeTo = Editor.Scripting.ScriptApiDocs.Summary(typeof(Vortex.AudioSource).GetMethod("FadeTo")) ?? "";
            string fadeIn = Editor.Scripting.ScriptApiDocs.Summary(typeof(Vortex.AudioSource).GetMethod("FadeIn")) ?? "";
            string volume = Editor.Scripting.ScriptApiDocs.Summary(typeof(Vortex.AudioSource).GetProperty("Volume")) ?? "";
            t.True(type.Contains("Volume × the fade envelope") && type.Contains("FadeTo(0.95f, 4f)"), "the class states the rule and the silent-start pattern: " + type);
            t.True(fadeTo.Contains("Volume × envelope") && fadeTo.Contains("absolute level"), "FadeTo: " + fadeTo);
            t.True(fadeIn.Contains("Volume 0"), "FadeIn: " + fadeIn);
            t.True(volume.Contains("fade envelope"), "Volume: " + volume);
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
    }
}
