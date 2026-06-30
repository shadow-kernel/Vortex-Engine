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
        public static ExportResult Export(string outputDir, Action<double, string> progress = null)
        {
            var project = ProjectData.Current;
            if (project == null) return Fail("No project is open.");
            return ExportFromPath(project.Path, project.Name, outputDir, progress);
        }

        /// <summary>Export a project by path. Used by Export() for the open project, and by build harnesses.
        /// <paramref name="progress"/> (optional) reports (fraction 0..1, status text) for a build dialog.</summary>
        public static ExportResult ExportFromPath(string projectRoot, string projectName, string outputDir, Action<double, string> progress = null)
        {
            Action<double, string> P = (f, s) => { try { progress?.Invoke(f, s); } catch { } };
            var sb = new StringBuilder();
            try
            {
                P(0.02, "Preparing…");
                if (string.IsNullOrEmpty(projectRoot)) return Fail("No project path.");
                if (string.IsNullOrEmpty(outputDir)) return Fail("No output folder selected.");
                var name = Sanitize(projectName);

                // 0) CLEAN: wipe the previous build so nothing stale lingers, then rebuild from scratch.
                try { if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true); }
                catch (Exception ex) { sb.AppendLine("• (could not fully clean — a previous game may be running: " + ex.Message + ")"); }
                Directory.CreateDirectory(outputDir);
                sb.AppendLine("• Cleaned output folder");
                P(0.10, "Copying engine runtime…");

                // 1) Engine runtime = the editor's own Release output (exe + native + managed DLLs).
                var runtimeDir = Path.GetDirectoryName(typeof(GameExporter).Assembly.Location);
                int n = CopyRuntime(runtimeDir, outputDir);
                sb.AppendLine("• Runtime: " + n + " files copied");

                // Engine shaders ship as loose files next to the exe (the native engine is a static lib in the exe and
                // can't read the C#-only .vpak). The runtime resolves <exe>/Shaders first; without this a shipped game
                // would have NO shaders. Copies .hlsl (compiled at runtime) + any precompiled bin/*.cso.
                int shaderCount = CopyShaders(outputDir);
                sb.AppendLine("• Shaders: " + shaderCount + " files copied -> Shaders/");

                // 2) Compile gameplay scripts -> a temp DLL (ships COMPILED, never as source).
                P(0.22, "Compiling gameplay scripts…");
                var tmpDll = Path.Combine(Path.GetTempPath(), "GameScripts_" + Guid.NewGuid().ToString("N") + ".dll");
                string scriptLog;
                bool scriptsOk = CompileScripts(projectRoot, tmpDll, out scriptLog);
                sb.AppendLine(scriptsOk ? "• Scripts compiled OK -> GameScripts.dll" : "• SCRIPTS FAILED TO COMPILE");

                // 3) BINARY ASSET PAK: pack the manifest + every asset (NOT .cs source) + the compiled DLL
                //    into ONE opaque, compressed+obfuscated Assets.vpak. The shipped game has no readable or
                //    editable asset files — the player decompresses it all into RAM at startup.
                var entries = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, byte[]>>();
                var manifest = Path.Combine(projectRoot, "project.vortex");
                if (File.Exists(manifest))
                    entries.Add(new System.Collections.Generic.KeyValuePair<string, byte[]>("project.vortex", File.ReadAllBytes(manifest)));

                P(0.38, "Packing assets…");
                var assetsSrc = Path.Combine(projectRoot, "Assets");
                int assetCount = 0;
                if (Directory.Exists(assetsSrc))
                {
                    var files = Directory.GetFiles(assetsSrc, "*", SearchOption.AllDirectories);
                    for (int i = 0; i < files.Length; i++)
                    {
                        var f = files[i];
                        if (Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase)) continue; // source -> compiled DLL
                        var rel = "Assets/" + f.Substring(assetsSrc.Length).TrimStart('\\', '/').Replace('\\', '/');
                        entries.Add(new System.Collections.Generic.KeyValuePair<string, byte[]>(rel, File.ReadAllBytes(f)));
                        assetCount++;
                        if ((i & 7) == 0 && files.Length > 0)
                            P(0.38 + 0.45 * ((double)i / files.Length), "Packing assets… (" + assetCount + " files)");
                    }
                }
                if (scriptsOk && File.Exists(tmpDll))
                    entries.Add(new System.Collections.Generic.KeyValuePair<string, byte[]>("GameScripts.dll", File.ReadAllBytes(tmpDll)));
                try { if (File.Exists(tmpDll)) File.Delete(tmpDll); } catch { }

                P(0.86, "Writing Assets.vpak…");
                VortexPak.Write(Path.Combine(outputDir, "Assets.vpak"), entries);
                sb.AppendLine("• Packed " + assetCount + " assets + manifest + scripts -> Assets.vpak (compressed + obfuscated)");
                P(0.93, "Finalizing…");

                // 4) Player marker + a branded <Game>.exe entry point + README.
                // The marker's presence makes the runtime boot straight into the game (no editor UI).
                const string exe = "Vortex Engine.exe";
                File.WriteAllText(Path.Combine(outputDir, "player.vortex"),
                    "{\"game\":\"" + (projectName ?? "Game").Replace("\"", "'") + "\",\"pak\":\"Assets.vpak\",\"scriptsDll\":\"GameScripts.dll\"}");
                sb.AppendLine("• Player marker written (player.vortex)");

                try
                {
                    var srcExe = Path.Combine(outputDir, exe);
                    if (File.Exists(srcExe))
                    {
                        File.Copy(srcExe, Path.Combine(outputDir, name + ".exe"), true);     // double-click this
                        var cfg = srcExe + ".config";
                        if (File.Exists(cfg)) File.Copy(cfg, Path.Combine(outputDir, name + ".exe.config"), true);
                        // Ship ONLY the branded game exe — drop the engine exe (.NET resolves the sibling DLLs
                        // by the app base directory regardless of the exe's name).
                        try { File.Delete(srcExe); } catch { }
                        try { if (File.Exists(cfg)) File.Delete(cfg); } catch { }
                        sb.AppendLine("• " + name + ".exe created (engine exe removed)");
                    }
                }
                catch { }

                File.WriteAllText(Path.Combine(outputDir, "README.txt"),
                    "Vortex Engine — exported game: " + projectName + "\r\n" +
                    "Created " + DateTime.Now + "\r\n\r\n" +
                    "Double-click '" + name + ".exe' to play (boots straight into the game — no editor).\r\n" +
                    "All assets + the compiled gameplay code are packed into Assets.vpak (one opaque binary,\r\n" +
                    "decompressed into RAM at startup). There are no readable/editable source files in this folder.\r\n");

                P(1.0, "Done");
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

        // Copy Engine/Shaders/** (the .hlsl source + any precompiled bin/*.cso) to <output>/Shaders/. The shipped
        // game's native renderer resolves shaders from <exe>/Shaders. Source is found by walking up from the editor
        // runtime dir to the repo's Engine/Shaders (mirrors the native DX12ShaderCompiler::shaders_dir resolution).
        private static int CopyShaders(string outDir)
        {
            var src = FindEngineShaders();
            if (src == null || !Directory.Exists(src)) return 0;
            var dst = Path.Combine(outDir, "Shaders");
            int n = 0;
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = f.Substring(src.Length).TrimStart('\\', '/');
                var target = Path.Combine(dst, rel);
                try { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(f, target, true); n++; } catch { }
            }
            return n;
        }

        private static string FindEngineShaders()
        {
            var p = Path.GetDirectoryName(typeof(GameExporter).Assembly.Location);
            for (int i = 0; i < 7 && !string.IsNullOrEmpty(p); i++)
            {
                var c = Path.Combine(p, "Engine", "Shaders");
                if (Directory.Exists(c)) return c;
                p = Path.GetDirectoryName(p);
            }
            return null;
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
