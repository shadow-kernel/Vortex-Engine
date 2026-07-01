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
        public static ExportResult Export(string outputDir, Action<double, string> progress = null, bool debug = false)
        {
            var project = ProjectData.Current;
            if (project == null) return Fail("No project is open.");
            return ExportFromPath(project.Path, project.Name, outputDir, progress, debug);
        }

        /// <summary>Export a project by path. Used by Export() for the open project, and by build harnesses.
        /// <paramref name="progress"/> (optional) reports (fraction 0..1, status text) for a build dialog.
        /// <paramref name="debug"/> = a DEBUG build: ship the project loose + as source with a debug marker so the
        /// game boots with dev tooling (script + shader hot-reload, on-screen overlay) ON. Release (default) packs
        /// everything into an opaque Assets.vpak and ships hot-reload OFF.</summary>
        public static ExportResult ExportFromPath(string projectRoot, string projectName, string outputDir, Action<double, string> progress = null, bool debug = false)
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

                // 2) Package the project. DEBUG = loose + source (dev tooling / hot-reload ON); RELEASE = packed +
                //    compiled + obfuscated (hot-reload OFF). The branded exe + README (below) are shared by both.
                bool scriptsOk = true; string scriptLog = null;
                const string exe = "Vortex Engine.exe";
                if (debug)
                {
                    // DEBUG export: DO NOT copy the project. The build REFERENCES the original project folder on disk,
                    // so hot-reload watches the SAME source files you edit in the editor / VS — there are NO stale
                    // copies in the build folder to accidentally edit. We ship only the runtime + engine shaders + a
                    // marker holding the original project path; the player loads + hot-reloads straight from there.
                    P(0.22, "Validating gameplay scripts…");
                    var tmpDllDbg = Path.Combine(Path.GetTempPath(), "GameScripts_" + Guid.NewGuid().ToString("N") + ".dll");
                    scriptsOk = CompileScripts(projectRoot, tmpDllDbg, out scriptLog);   // validate only (never shipped in debug)
                    try { if (File.Exists(tmpDllDbg)) File.Delete(tmpDllDbg); } catch { }
                    sb.AppendLine(scriptsOk ? "• Scripts validated OK" : "• SCRIPTS FAILED TO COMPILE");

                    P(0.90, "Writing debug marker…");
                    var projAbs = Path.GetFullPath(projectRoot).Replace("\\", "\\\\").Replace("\"", "'");
                    File.WriteAllText(Path.Combine(outputDir, "player.vortex"),
                        "{\"game\":\"" + (projectName ?? "Game").Replace("\"", "'") + "\",\"debug\":true,\"projectPath\":\"" + projAbs + "\"}");
                    sb.AppendLine("• Debug marker written — the build REFERENCES the ORIGINAL project (" + Path.GetFullPath(projectRoot) + ").");
                    sb.AppendLine("  Edit your scripts/shaders there and they hot-reload live in this build (no copies to edit).");
                    P(0.93, "Finalizing…");
                }
                else
                {
                    // RELEASE export: compile scripts -> DLL (ships COMPILED, never as source).
                    P(0.22, "Compiling gameplay scripts…");
                    var tmpDll = Path.Combine(Path.GetTempPath(), "GameScripts_" + Guid.NewGuid().ToString("N") + ".dll");
                    scriptsOk = CompileScripts(projectRoot, tmpDll, out scriptLog);
                    sb.AppendLine(scriptsOk ? "• Scripts compiled OK -> GameScripts.dll" : "• SCRIPTS FAILED TO COMPILE");

                    // BINARY ASSET PAKS (streaming-friendly, like a real engine's asset bundles): the shared/core
                    // assets + manifest + compiled scripts go into ONE opaque, compressed+obfuscated core Assets.vpak,
                    // and EACH scene ships in its OWN Scenes/<name>.vpak. The player mounts the core pak + only the
                    // START scene's pack at boot, and streams the others on demand — so a 100-scene game never loads
                    // every scene (or one giant file) up front. Nothing is readable/editable in the shipped game.
                    var entries = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, byte[]>>();
                    var sceneEntries = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, byte[]>>();
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
                            // Scene files go into their own per-scene pack (streamed on demand); everything else is shared.
                            if (Path.GetExtension(f).Equals(".vscene", StringComparison.OrdinalIgnoreCase) &&
                                rel.StartsWith("Assets/Scenes/", StringComparison.OrdinalIgnoreCase))
                                sceneEntries.Add(new System.Collections.Generic.KeyValuePair<string, byte[]>(rel, File.ReadAllBytes(f)));
                            else
                                entries.Add(new System.Collections.Generic.KeyValuePair<string, byte[]>(rel, File.ReadAllBytes(f)));
                            assetCount++;
                            if ((i & 7) == 0 && files.Length > 0)
                                P(0.38 + 0.45 * ((double)i / files.Length), "Packing assets… (" + assetCount + " files)");
                        }
                    }
                    if (scriptsOk && File.Exists(tmpDll))
                        entries.Add(new System.Collections.Generic.KeyValuePair<string, byte[]>("GameScripts.dll", File.ReadAllBytes(tmpDll)));
                    try { if (File.Exists(tmpDll)) File.Delete(tmpDll); } catch { }

                    P(0.86, "Writing asset paks…");
                    VortexPak.Write(Path.Combine(outputDir, "Assets.vpak"), entries);
                    // One pak per scene: Scenes/<sceneFileName>.vpak
                    if (sceneEntries.Count > 0)
                    {
                        var scenesOut = Path.Combine(outputDir, "Scenes");
                        Directory.CreateDirectory(scenesOut);
                        foreach (var se in sceneEntries)
                        {
                            var baseName = Path.GetFileNameWithoutExtension(se.Key);
                            var one = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, byte[]>> { se };
                            VortexPak.Write(Path.Combine(scenesOut, baseName + ".vpak"), one);
                        }
                    }
                    sb.AppendLine("• Packed " + (assetCount - sceneEntries.Count) + " shared assets + manifest + scripts -> Assets.vpak; "
                        + sceneEntries.Count + " scene(s) -> Scenes/*.vpak (streamed on demand)");
                    P(0.93, "Finalizing…");

                    // Player marker: its presence makes the runtime boot straight into the game (no editor UI).
                    File.WriteAllText(Path.Combine(outputDir, "player.vortex"),
                        "{\"game\":\"" + (projectName ?? "Game").Replace("\"", "'") + "\",\"pak\":\"Assets.vpak\",\"scenePaks\":\"Scenes\",\"scriptsDll\":\"GameScripts.dll\"}");
                    sb.AppendLine("• Player marker written (player.vortex)");
                }

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
                    "Vortex Engine — exported game: " + projectName + (debug ? "  [DEBUG BUILD]" : "  [RELEASE BUILD]") + "\r\n" +
                    "Created " + DateTime.Now + "\r\n\r\n" +
                    "Double-click '" + name + ".exe' to play (boots straight into the game — no editor).\r\n\r\n" +
                    (debug
                        ? "DEBUG build: this folder holds ONLY the runtime — it REFERENCES your original project on disk\r\n" +
                          "(" + Path.GetFullPath(projectRoot) + ").\r\n" +
                          "Edit your scripts/shaders THERE (the same files the editor uses — no copies), save, then Alt-Tab\r\n" +
                          "back to the game and your change hot-reloads live. Needs the project present; don't distribute.\r\n"
                        : "RELEASE build: all assets + the compiled gameplay code are packed into Assets.vpak (one opaque\r\n" +
                          "binary, decompressed into RAM at startup). No readable/editable source files, and no dev\r\n" +
                          "hot-reload tooling — this is the build to ship.\r\n"));

                // RELEASE: also wrap the game into a clean Windows INSTALLER (the way real games ship — most people
                // never see a raw exe folder). Uses Inno Setup if it's installed; the portable folder stays either way.
                if (!debug)
                {
                    P(0.97, "Building installer…");
                    try
                    {
                        var setupExe = TryBuildGameInstaller(outputDir, name, projectName);
                        if (setupExe != null) sb.AppendLine("• Installer built -> " + setupExe);
                        else sb.AppendLine("• No installer (Inno Setup 6 not found). Install it from jrsoftware.org to ship a one-click <game>-Setup.exe; the folder above is the portable build.");
                    }
                    catch (Exception ix) { sb.AppendLine("• Installer step skipped: " + ix.Message); }
                }

                P(1.0, "Done");
                if (!scriptsOk)
                    return new ExportResult { Success = false, OutputDir = outputDir, Message = "Export done, but SCRIPTS FAILED TO COMPILE:\n\n" + scriptLog };
                return new ExportResult { Success = true, OutputDir = outputDir, Message = sb.ToString() };
            }
            catch (Exception ex) { return Fail("Export failed: " + ex.Message); }
        }

        /// <summary>Wrap a finished release export folder into a Windows installer via Inno Setup (ISCC). Returns the
        /// path to &lt;game&gt;-Setup.exe, or null if Inno Setup isn't installed / the build failed. The installer output
        /// goes in a SIBLING folder so it's never packed into itself; the portable folder is left intact.</summary>
        private static string TryBuildGameInstaller(string gameDir, string exeBaseName, string productName)
        {
            string iscc = null;
            foreach (var c in new[] { @"C:\Program Files (x86)\Inno Setup 6\ISCC.exe", @"C:\Program Files\Inno Setup 6\ISCC.exe" })
                if (File.Exists(c)) { iscc = c; break; }
            if (iscc == null) return null;

            var outDir = gameDir.TrimEnd('\\', '/') + "_Installer";
            Directory.CreateDirectory(outDir);
            var appId = "{{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            var title = (productName ?? exeBaseName).Replace("\"", "'");
            var iss =
                "#define MyName \"" + title + "\"\r\n" +
                "#define MyExe \"" + exeBaseName + ".exe\"\r\n" +
                "[Setup]\r\n" +
                "AppId=" + appId + "\r\n" +
                "AppName={#MyName}\r\nAppVersion=1.0.0\r\nAppVerName={#MyName} 1.0.0\r\n" +
                "DefaultDirName={autopf}\\{#MyName}\r\nDefaultGroupName={#MyName}\r\nAllowNoIcons=yes\r\n" +
                "OutputDir=" + outDir + "\r\nOutputBaseFilename=" + exeBaseName + "-Setup\r\n" +
                "Compression=lzma2/ultra64\r\nSolidCompression=yes\r\nWizardStyle=modern\r\n" +
                "MinVersion=10.0\r\nArchitecturesAllowed=x64compatible\r\nArchitecturesInstallIn64BitMode=x64compatible\r\n" +
                "PrivilegesRequired=admin\r\nPrivilegesRequiredOverridesAllowed=dialog\r\n" +
                "UninstallDisplayIcon={app}\\{#MyExe}\r\nUninstallDisplayName={#MyName}\r\n" +
                "[Tasks]\r\n" +
                "Name: \"desktopicon\"; Description: \"{cm:CreateDesktopIcon}\"; GroupDescription: \"{cm:AdditionalIcons}\"; Flags: unchecked\r\n" +
                "[Files]\r\n" +
                "Source: \"" + gameDir + "\\*\"; DestDir: \"{app}\"; Flags: recursesubdirs createallsubdirs ignoreversion\r\n" +
                "[Icons]\r\n" +
                "Name: \"{group}\\{#MyName}\"; Filename: \"{app}\\{#MyExe}\"\r\n" +
                "Name: \"{group}\\{cm:UninstallProgram,{#MyName}}\"; Filename: \"{uninstallexe}\"\r\n" +
                "Name: \"{autodesktop}\\{#MyName}\"; Filename: \"{app}\\{#MyExe}\"; Tasks: desktopicon\r\n" +
                "[Run]\r\n" +
                "Filename: \"{app}\\{#MyExe}\"; Description: \"{cm:LaunchProgram,{#MyName}}\"; Flags: nowait postinstall skipifsilent\r\n";

            var issPath = Path.Combine(Path.GetTempPath(), exeBaseName + "_" + Guid.NewGuid().ToString("N") + ".iss");
            File.WriteAllText(issPath, iss);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(iscc, "\"" + issPath + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    proc.StandardOutput.ReadToEnd(); proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(240000) || proc.ExitCode != 0) return null;
                }
            }
            finally { try { File.Delete(issPath); } catch { } }

            var setup = Path.Combine(outDir, exeBaseName + "-Setup.exe");
            return File.Exists(setup) ? setup : null;
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

        /// <summary>Recursively copy a directory tree (skipping build caches), returning the file count. Used by the
        /// DEBUG export to ship the project loose + editable.</summary>
        private static int CopyTree(string src, string dst)
        {
            int n = 0;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); n++; } catch { }
            }
            foreach (var d in Directory.GetDirectories(src))
            {
                var nm = Path.GetFileName(d);
                if (nm == "obj" || nm == "bin" || nm == ".vs") continue;
                n += CopyTree(d, Path.Combine(dst, nm));
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
