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
    /// The documentation's C# samples compile against the gameplay API — and as C# 5, the language the Windows classic
    /// editor's CodeDOM compiler accepts (#98: "every code sample tested against the release build"). The documentation
    /// is the docs website (repository shadow-kernel/Vortex-Engine-Homepage, pages in <c>docs/content</c>); CI checks it
    /// out and points <c>VORTEX_DOCS_DIR</c> at it, and on a developer machine a clone next to this repository
    /// (<c>../Vortex-Engine-Homepage</c>) is found too. A complete sample (it declares a class) must compile as it is,
    /// usings included, because that is what readers paste. Member-only samples are wrapped in a VortexBehaviour,
    /// statement samples in one of its methods. Lines starting with <c>//~</c> are context the website hides (the fields
    /// a fragment uses, say): the test compiles them without the marker. A block fenced <c>```csharp nocompile</c> is an
    /// illustration (an API listing) and skipped.
    /// </summary>
    public static class DocsTests
    {
        /// <summary>Pages about the engine's own code and process — their C# is not gameplay script.</summary>
        private static readonly string[] InternalPages =
        {
            "architecture.md", "vortexapi.md", "developer-guide.md", "contributing.md", "feature-status.md", "release-process.md",
            "claude-tools.md", "changelog.md",
        };

        [Test]
        public static void DocsCodeSamplesCompile(TestContext t)
        {
            string docs = FindDocs();
            if (docs == null)
            {
                // CI always has the docs; a developer machine without a clone of the docs repository skips the check
                t.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")), "the docs (VORTEX_DOCS_DIR) are missing in CI");
                Console.WriteLine("        skipped: no docs — clone shadow-kernel/Vortex-Engine-Homepage next to this repository or set VORTEX_DOCS_DIR");
                return;
            }
            var samples = new List<(string path, string code)>();
            foreach (var page in Directory.GetFiles(docs, "*.md").OrderBy(p => p, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(page);
                if (name.StartsWith("design-", StringComparison.Ordinal) || InternalPages.Contains(name)) continue;
                foreach (var (line, code) in Blocks(File.ReadAllLines(page)))
                    samples.Add((name + ":" + line, Wrap(code, samples.Count)));
            }
            t.True(samples.Count > 10, "the docs have C# samples (" + samples.Count + " in " + docs + ")");
            var errors = RoslynScriptCompiler.CheckSources(samples, csharp5: true);
            var report = errors.GroupBy(e => e.File).Select(g => g.Key + " → " + string.Join(" | ", g.Take(3).Select(e => e.Id + " " + e.Message))).ToList();
            foreach (var r in report) Console.WriteLine("        " + r);
            t.True(errors.Count == 0, report.Count + " of " + samples.Count + " samples do not compile (listed above)");
        }

        /// <summary>The ```csharp blocks of a page that are meant to compile: (1-based line of the fence, code).</summary>
        internal static IEnumerable<(int line, string code)> Blocks(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var info = lines[i].Trim();
                if (!info.StartsWith("```", StringComparison.Ordinal)) continue;
                var words = info.Substring(3).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string lang = words.Length > 0 ? words[0].ToLowerInvariant() : "";
                if (!(lang == "csharp" || lang == "cs" || lang == "c#"))
                {
                    // another language's block (or one without a language): skip to its closing fence
                    for (i++; i < lines.Length && lines[i].Trim() != "```"; i++) { }
                    continue;
                }
                bool skip = words.Skip(1).Any(w => w.Equals("nocompile", StringComparison.OrdinalIgnoreCase));
                var code = new StringBuilder();
                int j = i + 1;
                for (; j < lines.Length && lines[j].Trim() != "```"; j++)
                {
                    string l = lines[j];
                    int k = l.IndexOf("//~", StringComparison.Ordinal);
                    // hidden context: "//~ float _yaw;" compiles as "float _yaw;"
                    if (k >= 0 && l.Substring(0, k).Trim().Length == 0) l = l.Substring(0, k) + l.Substring(k + 3).TrimStart();
                    code.AppendLine(l);
                }
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

        /// <summary>The docs website's pages: <c>VORTEX_DOCS_DIR</c> (the docs repository or its <c>docs/content</c>), else a
        /// clone of the docs repository next to this one.</summary>
        internal static string FindDocs()
        {
            string env = Environment.GetEnvironmentVariable("VORTEX_DOCS_DIR");
            if (!string.IsNullOrWhiteSpace(env))
            {
                string content = Path.Combine(env, "docs", "content");
                return Directory.Exists(content) ? content : Directory.Exists(env) ? env : null;
            }
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string sibling = Path.Combine(dir.FullName, "Vortex-Engine-Homepage", "docs", "content");
                if (Directory.Exists(sibling)) return sibling;
            }
            return null;
        }
    }
}
