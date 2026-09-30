using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Editor.Scripting
{
    /// <summary>
    /// In-process C# compiler for gameplay scripts on modern .NET (the .NET Framework editor uses CodeDOM).
    /// Compiles the project's script files against the running framework + Vortex.Core and loads the result into
    /// a collectible AssemblyLoadContext, so hot reload can drop the previous generation.
    /// </summary>
    public static class RoslynScriptCompiler
    {
        private static AssemblyLoadContext _current;
        private static List<MetadataReference> _references;

        public static Assembly Compile(string[] files, out string log)
        {
            log = null;
            if (files == null || files.Length == 0) return null;
            try
            {
                var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
                var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOptions, path: f)).ToList();
                var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable);
                var compilation = CSharpCompilation.Create("GameScripts_" + Guid.NewGuid().ToString("N"), trees, References(), options);

                using var ms = new MemoryStream();
                var result = compilation.Emit(ms);
                if (!result.Success)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        var span = d.Location.GetLineSpan();
                        sb.AppendLine($"{Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1}): {d.GetMessage()}");
                    }
                    log = "Script compile failed:\n" + sb;
                    return null;
                }
                ms.Position = 0;

                // A fresh collectible context per generation: the previous scripts become collectable once nothing
                // references them any more (the runtime re-instantiates behaviours on reload).
                var previous = _current;
                _current = new AssemblyLoadContext("VortexScripts", isCollectible: true);
                var asm = _current.LoadFromStream(ms);
                try { previous?.Unload(); } catch { }
                return asm;
            }
            catch (Exception ex)
            {
                log = "Script compile exception: " + ex.Message;
                return null;
            }
        }

        /// <summary>Compile the script files to a DLL on disk (game export). Returns false with a log on errors.</summary>
        public static bool EmitToFile(string[] files, string outDll, out string log)
        {
            log = null;
            try
            {
                var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
                var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOptions, path: f)).ToList();
                var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable);
                var compilation = CSharpCompilation.Create("GameScripts", trees, References(), options);
                var result = compilation.Emit(outDll);
                if (result.Success) return true;
                var sb = new System.Text.StringBuilder();
                foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                {
                    var span = d.Location.GetLineSpan();
                    sb.AppendLine($"{Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1}): {d.GetMessage()}");
                }
                log = sb.ToString();
                return false;
            }
            catch (Exception ex) { log = ex.Message; return false; }
        }

        private static IEnumerable<MetadataReference> References()
        {
            if (_references != null) return _references;
            var refs = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The shared framework as the runtime sees it, plus this core assembly (the Vortex.* gameplay API).
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            {
                foreach (string path in tpa.Split(Path.PathSeparator))
                {
                    string name = Path.GetFileName(path);
                    if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!(name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "netstandard.dll", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "mscorlib.dll", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (seen.Add(path)) { try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { } }
                }
            }
            string core = typeof(Vortex.VortexBehaviour).Assembly.Location;
            if (!string.IsNullOrEmpty(core) && seen.Add(core)) refs.Add(MetadataReference.CreateFromFile(core));
            _references = refs;
            return refs;
        }
    }
}
