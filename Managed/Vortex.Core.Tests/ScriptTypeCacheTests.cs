using System;
using System.IO;
using Editor.Core.Data;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>#405 review: the Inspector and get_entity ask for a behaviour's type on every refresh. A project whose scripts
    /// do not compile was compiled again on each ask (0.7 s per call with 120 scripts, the UI frozen); the edit-mode
    /// assembly is now kept — the failure too — while the script files stay the same, and every change to the set of
    /// files is seen, also one the newest write time misses.</summary>
    public static class ScriptTypeCacheTests
    {
        [Test]
        public static void ABrokenProjectIsCompiledOnceNotOnEveryAsk(TestContext t)
        {
            var before = ProjectData.Current;
            try
            {
                t.Write("proj/Assets/Scripts/Good.cs", "public class Good : Vortex.VortexBehaviour { public float Speed = 1f; }\n");
                string broken = t.Write("proj/Assets/Scripts/Broken.cs", "public class Broken : Vortex.VortexBehaviour { int x = \"s\"; }\n");
                ProjectData.Current = new ProjectData(t.Path("proj"), "Cache");
                var rt = ScriptRuntime.Instance;

                int n = rt.ReflectCompiles;
                t.True(rt.GetScriptTypeForInspector("Good") == null, "the scripts do not compile");
                t.Equal(n + 1, rt.ReflectCompiles, "compiled once");
                for (int i = 0; i < 5; i++) rt.GetScriptTypeForInspector("Good");
                t.Equal(n + 1, rt.ReflectCompiles, "the failure is remembered, not compiled again on every ask");

                File.WriteAllText(broken, "public class Broken : Vortex.VortexBehaviour { }\n");
                File.SetLastWriteTimeUtc(broken, DateTime.UtcNow.AddSeconds(5));
                t.NotNull(rt.GetScriptTypeForInspector("Good"), "the fix is seen");
                t.NotNull(rt.GetScriptTypeForInspector("Broken"), "and the fixed class found");
                t.Equal(n + 2, rt.ReflectCompiles, "compiled once more, then kept");

                // a script copied in with an old date: the newest write time does not move, the set of files does
                string old = t.Write("proj/Assets/Scripts/Sub/Old.cs", "public class Old : Vortex.VortexBehaviour { }\n");
                File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                t.NotNull(rt.GetScriptTypeForInspector("Old"), "a script added with an old date");
                File.Delete(old);
                t.True(rt.GetScriptTypeForInspector("Old") == null, "a deleted script is gone");
                t.Equal(n + 4, rt.ReflectCompiles, "one compile per change");
            }
            finally { ProjectData.Current = before; }
        }
    }
}
