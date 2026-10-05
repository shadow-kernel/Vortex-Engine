using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>
    /// The wiki's C# samples compile against the gameplay API — and as C# 5, the language the Windows editor's CodeDOM
    /// compiler accepts (#98: "every code sample tested against the release build"). A complete sample (it declares a
    /// class) must compile as it is, usings included, because that is what readers paste. Member-only samples are
    /// wrapped in a VortexBehaviour, statement samples in one of its methods. A block preceded by
    /// <c>&lt;!-- no-compile --&gt;</c> is an illustration and skipped.
    /// </summary>
    public static class DocsTests
    {
        /// <summary>Pages about the engine's own code (interop, internals, workflow) — their C# is not gameplay script.</summary>
        private static readonly string[] InternalPages =
        {
            "Architecture.md", "Developer-Guide.md", "Managed-Interop-Bindings.md", "Native-DLL-API.md", "Performance-Master-Plan.md",
            "Contributing-Workflow.md", "Roadmap.md", "Release-Test-Plan-v2.7.md",
        };

        [Test]
        public static void WikiCodeSamplesCompile(TestContext t)
        {
            string wiki = FindWiki();
            t.NotNull(wiki, "docs/wiki above the test binaries");
            var samples = new List<(string path, string code)>();
            foreach (var page in Directory.GetFiles(wiki, "*.md").OrderBy(p => p, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(page);
                if (name.StartsWith("Design-", StringComparison.Ordinal) || InternalPages.Contains(name)) continue;
                foreach (var (line, code) in Blocks(File.ReadAllLines(page)))
                    samples.Add((name + ":" + line, Wrap(code, samples.Count)));
            }
            t.True(samples.Count > 10, "the wiki has C# samples (" + samples.Count + ")");
            var errors = RoslynScriptCompiler.CheckSources(samples, csharp5: true);
            var report = errors.GroupBy(e => e.File).Select(g => g.Key + " → " + string.Join(" | ", g.Take(3).Select(e => e.Id + " " + e.Message))).ToList();
            foreach (var r in report) Console.WriteLine("        " + r);
            t.True(errors.Count == 0, report.Count + " of " + samples.Count + " samples do not compile (listed above)");
        }

        /// <summary>The ```csharp blocks of a page: (1-based line of the fence, code).</summary>
        internal static IEnumerable<(int line, string code)> Blocks(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                string fence = lines[i].Trim();
                if (!(fence == "```csharp" || fence == "```cs" || fence == "```c#" || fence == "```C#")) continue;
                bool skip = i > 0 && lines[i - 1].Contains("<!-- no-compile");
                var code = new StringBuilder();
                int j = i + 1;
                for (; j < lines.Length && lines[j].Trim() != "```"; j++) code.AppendLine(lines[j]);
                if (!skip) yield return (i + 1, code.ToString());
                i = j;
            }
        }

        private static readonly Regex TypeDecl = new Regex(@"^\s*((public|internal|static|sealed|abstract|partial)\s+)*(class|struct|enum|interface)\s+\w+", RegexOptions.Multiline);
        private static readonly Regex MemberStart = new Regex(@"^\s*(\[|(public|private|protected|internal|static|override|virtual|readonly|const|void|IEnumerator)\b)");
        /// <summary>A method header anywhere in the snippet ("public override void Update(float dt)", "IEnumerator Fade()").</summary>
        private static readonly Regex MethodHeader = new Regex(@"^\s*((public|private|protected|internal|static|override|virtual)\s+)*(void|IEnumerator|bool|int|float|string|Vector3)\s+\w+\s*\([^;]*\)\s*(\{.*)?$", RegexOptions.Multiline);

        /// <summary>Each sample in its own namespace, so equal class names in two samples don't collide.</summary>
        internal static string Wrap(string code, int n)
        {
            var sb = new StringBuilder("namespace DocSamples.S" + n + "\n{\n");
            if (TypeDecl.IsMatch(code)) sb.Append(code);
            else
            {
                // the sample's own usings first, then the context it is written for
                var lines = code.Replace("\r\n", "\n").Split('\n').ToList();
                var usings = lines.TakeWhile(l => l.TrimStart().StartsWith("using ", StringComparison.Ordinal) || l.Trim().Length == 0).ToList();
                var body = lines.Skip(usings.Count).ToList();
                sb.Append("using Vortex;\nusing System.Collections;\nusing System.Collections.Generic;\n");
                foreach (var u in usings) sb.Append(u).Append('\n');
                string first = body.FirstOrDefault(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)) ?? "";
                if (MemberStart.IsMatch(first) || MethodHeader.IsMatch(string.Join("\n", body))) sb.Append("public class Sample : VortexBehaviour\n{\n").Append(string.Join("\n", body)).Append("\n}\n");
                else sb.Append("public class Sample : VortexBehaviour\n{\nvoid Run(float dt)\n{\n").Append(string.Join("\n", body)).Append("\n}\n}\n");
            }
            return sb.Append("\n}\n").ToString();
        }

        private static string FindWiki()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string wiki = Path.Combine(dir.FullName, "docs", "wiki");
                if (Directory.Exists(wiki)) return wiki;
            }
            return null;
        }
    }
}
