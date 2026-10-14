using System.Linq;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>The scripting API's XML doc summaries are readable at runtime (#404): get_scripting_api prints them under
    /// the signatures it lists by reflection — they ship as Vortex.Core.xml next to the assembly.</summary>
    public static class ScriptApiDocsTests
    {
        [Test]
        public static void SummariesOfTypesAndMembersAreFound(TestContext t)
        {
            t.True((ScriptApiDocs.Summary(typeof(Vortex.Audio)) ?? "").Contains("Game audio for scripts"), "a type's summary");
            t.True((ScriptApiDocs.Summary(typeof(Vortex.Audio.Music)) ?? "").Contains("music channel"), "a nested type's summary");
            var oneShot = typeof(Vortex.Audio).GetMethod("PlayOneShot", new[] { typeof(string), typeof(Vortex.Vector3), typeof(float), typeof(float) });
            var withMix = typeof(Vortex.Audio).GetMethod("PlayOneShot", new[] { typeof(string), typeof(Vortex.Vector3), typeof(float), typeof(float), typeof(float) });
            t.True((ScriptApiDocs.Summary(oneShot) ?? "").Contains("positional (3D) one-shot"), "an overload by its parameters");
            t.True((ScriptApiDocs.Summary(withMix) ?? "").Contains("reverbMix"), "the other overload: its own summary, <paramref> as the name");
            var raycast = typeof(Vortex.Physics).GetMethods().First(m => m.Name == "Raycast" && m.GetParameters().Any(p => p.ParameterType.IsByRef));
            t.NotNull(ScriptApiDocs.Summary(raycast), "a method with an out parameter (doc id with @)");
            t.True((ScriptApiDocs.Summary(typeof(Vortex.AudioSource).GetProperty("Volume")) ?? "").Contains("Live volume"), "a property");
            string audio = ScriptApiDocs.Summary(typeof(Vortex.Audio)) ?? "";
            t.True(audio.Contains("\n    ") && audio.Contains("Audio.PlayOneShot("), "a <code> sample keeps its own, indented lines");
            t.False(audio.Contains("<"), "no XML tags in the text");
        }

        [Test]
        public static void DocIdsFollowTheCompilersFormat(TestContext t)
        {
            t.Equal("T:Vortex.Audio.Music", ScriptApiDocs.IdOf(typeof(Vortex.Audio.Music)), "nested type: '.'");
            var fadeTo = typeof(Vortex.AudioSource).GetMethod("FadeTo");
            t.Equal("M:Vortex.AudioSource.FadeTo(System.Single,System.Single)", ScriptApiDocs.IdOf(fadeTo), "method with parameters");
            t.Equal("M:Vortex.AudioSource.Play", ScriptApiDocs.IdOf(typeof(Vortex.AudioSource).GetMethod("Play")), "method without parameters: no parentheses");
            t.Equal("P:Vortex.AudioSource.Volume", ScriptApiDocs.IdOf(typeof(Vortex.AudioSource).GetProperty("Volume")), "property");
            var tryGet = typeof(Vortex.Blackboard).GetMethods().First(m => m.Name == "TryGet" && m.IsGenericMethodDefinition);
            t.Equal("M:Vortex.Blackboard.TryGet``1(System.String,``0@)", ScriptApiDocs.IdOf(tryGet), "generic method with an out T parameter");
        }
    }
}
