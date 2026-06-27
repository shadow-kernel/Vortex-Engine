using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Text;
using Editor.Core.Data;

namespace Editor.Core.Services.Build
{
    public sealed class ExportResult
    {
        public bool Success;
        public string OutputDir;
        public string Message;
    }

    /// <summary>
    /// Exports the current project into a self-contained "play build" folder: the engine runtime
    /// (the editor's own Release binaries — the game runs in-process), the project's Assets, the
    /// project manifest, the gameplay scripts COMPILED to a DLL (validates them), plus a launcher.
    /// A fully chrome-less standalone Player.exe needs the play runtime factored out of the editor
    /// assembly — that's the documented next step; this v1 packages + compiles + launches.
    /// </summary>
    public static class GameExporter
    {
        public static ExportResult Export(string outputDir)
        {
            var sb = new StringBuilder();
            try
            {
                var project = ProjectData.Current;
                if (project == null) return Fail("No project is open.");
                if (string.IsNullOrEmpty(outputDir)) return Fail("No output folder selected.");
                var projectRoot = project.Path;
                var name = Sanitize(project.Name);

                Directory.CreateDirectory(outputDir);

                // 1) Engine runtime = the editor's own Release output (exe + native + managed DLLs).
                var runtimeDir = Path.GetDirectoryName(typeof(GameExporter).Assembly.Location);
                int n = CopyRuntime(runtimeDir, outputDir);
                sb.AppendLine("• Runtime: " + n + " files copied");

                // 2) Project assets + manifest (loose files — the engine imports at runtime).
                var assetsSrc = Path.Combine(projectRoot, "Assets");
                if (Directory.Exists(assetsSrc)) { CopyDir(assetsSrc, Path.Combine(outputDir, "Assets")); sb.AppendLine("• Assets copied"); }
                var manifest = Path.Combine(projectRoot, "project.vortex");
                if (File.Exists(manifest)) File.Copy(manifest, Path.Combine(outputDir, "project.vortex"), true);

                // 3) Compile gameplay scripts -> <Project>Scripts.dll ("richtig compilen" + validation).
                string scriptLog;
                bool scriptsOk = CompileScripts(projectRoot, Path.Combine(outputDir, name + "Scripts.dll"), out scriptLog);
                sb.AppendLine(scriptsOk ? "• Scripts compiled OK" : "• SCRIPTS FAILED TO COMPILE");

                // 4) Player marker + a branded <Game>.exe entry point + README.
                // The marker's presence makes the runtime boot straight into the game (no editor UI).
                const string exe = "Vortex Engine.exe";
                File.WriteAllText(Path.Combine(outputDir, "player.vortex"),
                    "{\"game\":\"" + (project.Name ?? "Game").Replace("\"", "'") + "\",\"scriptsDll\":\"" + name + "Scripts.dll\"}");
                sb.AppendLine("• Player marker written (player.vortex)");

                try
                {
                    var srcExe = Path.Combine(outputDir, exe);
                    if (File.Exists(srcExe))
                    {
                        File.Copy(srcExe, Path.Combine(outputDir, name + ".exe"), true);     // double-click this
                        var cfg = srcExe + ".config";
                        if (File.Exists(cfg)) File.Copy(cfg, Path.Combine(outputDir, name + ".exe.config"), true);
                        sb.AppendLine("• " + name + ".exe created");
                    }
                }
                catch { }

                File.WriteAllText(Path.Combine(outputDir, "README.txt"),
                    "Vortex Engine — exported game: " + project.Name + "\r\n" +
                    "Created " + DateTime.Now + "\r\n\r\n" +
                    "Double-click '" + name + ".exe' to play (boots straight into the game — no editor).\r\n" +
                    "Assets are loose files + their .vmeta; gameplay scripts are compiled into " + name + "Scripts.dll.\r\n" +
                    "(A binary asset pak/VFS is a planned optimization.)\r\n");

                if (!scriptsOk)
                    return new ExportResult { Success = false, OutputDir = outputDir, Message = "Export done, but SCRIPTS FAILED TO COMPILE:\n\n" + scriptLog };
                return new ExportResult { Success = true, OutputDir = outputDir, Message = sb.ToString() };
            }
            catch (Exception ex) { return Fail("Export failed: " + ex.Message); }
        }

        private static bool CompileScripts(string projectRoot, string outDll, out string log)
        {
            log = null;
            var dir = Path.Combine(projectRoot, "Assets", "Scripts");
            if (!Directory.Exists(dir)) return true; // nothing to compile
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("VortexScripting.cs", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length == 0) return true;
            try
            {
                using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
                {
                    var p = new CompilerParameters
                    {
                        GenerateInMemory = false,
                        GenerateExecutable = false,
                        OutputAssembly = outDll,
                        TreatWarningsAsErrors = false
                    };
                    p.ReferencedAssemblies.Add("mscorlib.dll");
                    p.ReferencedAssemblies.Add("System.dll");
                    p.ReferencedAssemblies.Add("System.Core.dll");
                    p.ReferencedAssemblies.Add(typeof(Vortex.VortexBehaviour).Assembly.Location);
                    var results = provider.CompileAssemblyFromFile(p, files);
                    if (results.Errors.HasErrors)
                    {
                        var sb = new StringBuilder();
                        foreach (CompilerError e in results.Errors)
                            if (!e.IsWarning) sb.AppendLine(Path.GetFileName(e.FileName) + "(" + e.Line + "): " + e.ErrorText);
                        log = sb.ToString();
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex) { log = ex.Message; return false; }
        }

        private static int CopyRuntime(string runtimeDir, string outDir)
        {
            int n = 0;
            if (string.IsNullOrEmpty(runtimeDir) || !Directory.Exists(runtimeDir)) return 0;
            foreach (var f in Directory.GetFiles(runtimeDir))
            {
                var ext = (Path.GetExtension(f) ?? "").ToLowerInvariant();
                if (ext == ".dll" || ext == ".exe" || ext == ".json" || ext == ".config")
                {
                    try { File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)), true); n++; } catch { }
                }
            }
            return n;
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); } catch { }
            foreach (var d in Directory.GetDirectories(src))
            {
                var nm = Path.GetFileName(d);
                if (nm == "obj" || nm == "bin" || nm == ".vs") continue; // skip build cache
                CopyDir(d, Path.Combine(dst, nm));
            }
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in (s ?? "Game")) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.Length == 0 ? "Game" : sb.ToString();
        }

        private static ExportResult Fail(string m) => new ExportResult { Success = false, Message = m };
    }
}
