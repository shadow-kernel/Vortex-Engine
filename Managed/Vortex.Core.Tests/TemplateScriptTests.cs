using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>The project templates' scripts compile against the gameplay API — the same Roslyn compile the editor
    /// and the player run on a project's Assets/Scripts. Skipped when the Templates folder is not checked out (the
    /// CI's macOS job, a packaged test run); the Windows job checks out Default3D.</summary>
    public static class TemplateScriptTests
    {
        [Test]
        public static void TemplateScriptsCompile(TestContext t)
        {
            string root = FindRepoRoot();
            string templates = root != null ? Path.Combine(root, "Templates") : null;
            if (templates == null || !Directory.Exists(templates)) { Console.WriteLine("        skipped: no Templates folder"); return; }
            int templatesChecked = 0;
            foreach (var dir in Directory.GetDirectories(templates).OrderBy(d => d, StringComparer.Ordinal))
            {
                string scripts = Path.Combine(dir, "Assets", "Scripts");
                if (!Directory.Exists(scripts)) continue;
                var files = Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories);
                if (files.Length == 0) continue;
                var sources = files.Select(f => (path: Path.GetRelativePath(dir, f), code: File.ReadAllText(f))).ToList();
                var errors = RoslynScriptCompiler.CheckSources(sources);
                var report = errors.GroupBy(e => e.File).Select(g => g.Key + " → " + string.Join(" | ", g.Take(3).Select(e => e.Id + " " + e.Message))).ToList();
                foreach (var r in report) Console.WriteLine("        " + Path.GetFileName(dir) + ": " + r);
                t.True(errors.Count == 0, Path.GetFileName(dir) + ": " + errors.Count + " error(s) in " + files.Length + " scripts (listed above)");
                templatesChecked++;
            }
            Console.WriteLine("        " + templatesChecked + " template(s) compiled");
        }

        private static string FindRepoRoot()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
                    if (Directory.Exists(Path.Combine(dir.FullName, "Templates")) && Directory.Exists(Path.Combine(dir.FullName, "Managed"))) return dir.FullName;
            }
            catch { }
            return null;
        }
    }
}
