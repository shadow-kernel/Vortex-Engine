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
                var compilation = CreateCompilation(files, OptimizationLevel.Debug, "GameScripts_" + Guid.NewGuid().ToString("N"));

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
                var compilation = CreateCompilation(files, OptimizationLevel.Release, "GameScripts");
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

        /// <summary>The one compilation every path uses (play, export, the tools' check): same language version,
        /// options and references, so a check that passes means Play compiles.</summary>
        private static CSharpCompilation CreateCompilation(string[] files, OptimizationLevel level, string name) =>
            CreateCompilation(files.Select(f => (f, File.ReadAllText(f))), LanguageVersion.Latest, level, name);

        private static CSharpCompilation CreateCompilation(IEnumerable<(string path, string code)> sources, LanguageVersion version, OptimizationLevel level, string name)
        {
            var parseOptions = new CSharpParseOptions(version);
            var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.code, parseOptions, path: s.path)).ToList();
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable);
            return CSharpCompilation.Create(name, trees, References(), options);
        }

        /// <summary>Errors of in-memory sources against the gameplay API (the docs' code samples). With
        /// <paramref name="csharp5"/> they must also be C# 5 — what the Windows editor's CodeDOM compiler accepts.</summary>
        public static List<ScriptDiagnostic> CheckSources(IEnumerable<(string path, string code)> sources, bool csharp5)
        {
            var compilation = CreateCompilation(sources, csharp5 ? LanguageVersion.CSharp5 : LanguageVersion.Latest, OptimizationLevel.Debug, "Samples_Check");
            return compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return new ScriptDiagnostic
                {
                    File = span.Path, Line = span.StartLinePosition.Line + 1, Column = span.StartLinePosition.Character + 1,
                    Id = d.Id, IsError = true, Message = d.GetMessage(),
                };
            }).ToList();
        }

        /// <summary>A compiler message with its place (1-based line and column).</summary>
        public sealed class ScriptDiagnostic
        {
            public string File;
            public int Line, Column;
            public string Id;
            public bool IsError;
            public string Message;
            public override string ToString() => System.IO.Path.GetFileName(File) + "(" + Line + "," + Column + "): " + (IsError ? "error " : "warning ") + Id + ": " + Message;
        }

        /// <summary>Errors (and optionally warnings) of the script files without loading anything — the same
        /// compilation Play uses (Claude's compile check, #91).</summary>
        public static List<ScriptDiagnostic> Check(string[] files, bool includeWarnings = false)
        {
            var list = new List<ScriptDiagnostic>();
            if (files == null || files.Length == 0) return list;
            var compilation = CreateCompilation(files, OptimizationLevel.Debug, "GameScripts_Check");
            foreach (var d in compilation.GetDiagnostics())
            {
                bool error = d.Severity == DiagnosticSeverity.Error;
                if (!error && !(includeWarnings && d.Severity == DiagnosticSeverity.Warning)) continue;
                var span = d.Location.GetLineSpan();
                list.Add(new ScriptDiagnostic
                {
                    File = span.Path,
                    Line = span.StartLinePosition.Line + 1,
                    Column = span.StartLinePosition.Character + 1,
                    Id = d.Id,
                    IsError = error,
                    Message = d.GetMessage(),
                });
            }
            return list;
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
