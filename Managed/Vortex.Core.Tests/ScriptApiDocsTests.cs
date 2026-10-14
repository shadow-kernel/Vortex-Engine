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
        public static void EachAssemblyReadsItsOwnDocFile(TestContext t)
        {
            // the first assembly asked about used to answer for every other one: a type of an assembly without docs, asked
            // first, left the engine's summaries unread for the rest of the session
            ScriptApiDocs.Forget();
            t.True(ScriptApiDocs.Summary(typeof(ScriptApiDocsTests)) == null, "the test assembly has no doc file");
            t.True((ScriptApiDocs.Summary(typeof(Vortex.Audio)) ?? "").Contains("Game audio for scripts"), "the engine's docs are still found afterwards");
        }

        [Test]
        public static void DocFilesStayOutOfGameExports(TestContext t)
        {
            // Vortex.Core.xml (1 MB) next to the player's Vortex.Core.dll in a dev tree is editor-only: exports skip it
            string Make(string name) { string p = t.Path("runtime", name); System.IO.Directory.CreateDirectory(t.Path("runtime")); System.IO.File.WriteAllText(p, "x"); return p; }
            var dll = Make("Vortex.Core.dll");
            var docs = Make("Vortex.Core.xml");
            var other = Make("settings.xml");
            t.True(Editor.Core.Services.Build.GamePackager.IsRuntimeJunk(docs, null), "an assembly's XML docs are not exported");
            t.False(Editor.Core.Services.Build.GamePackager.IsRuntimeJunk(other, null), "an XML file that belongs to no assembly is");
            t.False(Editor.Core.Services.Build.GamePackager.IsRuntimeJunk(dll, null), "the assembly itself is");
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
