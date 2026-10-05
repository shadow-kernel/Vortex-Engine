using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Editor.Core.Data;
using Editor.Core.Services;

namespace Editor.Core.Services.Build
{
    public sealed class PackageResult
    {
        public bool Success;
        public string OutputPath;
        public string Message;
    }

    /// <summary>What to export: which project, for which platform, with which branding.</summary>
    public sealed class ExportRequest
    {
        public string ProjectRoot;
        public string ProjectName;
        public ProjectSettings Settings;              // product/company/version/bundle id (null = defaults)
        public ExportPlatform Platform = ExportPlatforms.Host;
        public string OutputDir;                      // the folder the .app / game folder is created in
        public bool Debug;                            // host platform only: references the live project (hot reload)
        public bool CreateArchive;                    // macOS: .dmg next to the .app; Windows/Linux: .zip
        /// <summary>Icon files prepared by the editor shell (any may be null): macOS .icns, Windows .ico, a PNG (Linux / fallback).</summary>
        public string IcnsPath, IcoPath, PngIconPath;

        public string ProductName => !string.IsNullOrWhiteSpace(Settings?.ProductName) && Settings.ProductName != "My Game" ? Settings.ProductName.Trim() : (ProjectName ?? "Game");
        public string CompanyName => !string.IsNullOrWhiteSpace(Settings?.CompanyName) ? Settings.CompanyName.Trim() : "DefaultCompany";
        public string Version => !string.IsNullOrWhiteSpace(Settings?.ProductVersion) ? Settings.ProductVersion.Trim() : "1.0.0";
        public string BundleIdentifier
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Settings?.BundleIdentifier)) return Settings.BundleIdentifier.Trim();
                return "com." + Slug(CompanyName) + "." + Slug(ProductName);
            }
        }
        private static string Slug(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "game";
        }
    }

    /// <summary>
    /// Cross-platform game export. Produces, for the chosen platform, a self-contained build of the project:
    /// the standalone player published for that platform + the native engine + shaders (from a runtime pack),
    /// the project packed as Assets.vpak + Scenes/*.vpak + compiled GameScripts.dll (Release) or a live reference
    /// to the project folder (Debug, host platform only), branded with the game's name, version and icon:
    ///   macOS   -> "&lt;Name&gt;.app" (Info.plist, AppIcon.icns, ad-hoc signed), optional .dmg
    ///   Windows -> "&lt;Name&gt;/&lt;Name&gt;.exe" (icon + version info embedded in the executable), optional .zip
    ///   Linux   -> "&lt;Name&gt;/&lt;Name&gt;" + .png icon + .desktop entry, optional .zip
    /// Same pak format as the Windows editor's exporter.
    /// </summary>
    public static class GamePackager
    {
        /// <summary>Compatibility entry: host platform, default branding.</summary>
        public static PackageResult Export(string projectRoot, string projectName, string outputDir, bool debug, Action<double, string> progress = null)
        {
            var project = ProjectData.Current;
            return Export(new ExportRequest { ProjectRoot = projectRoot, ProjectName = projectName, Settings = project?.Settings, OutputDir = outputDir, Debug = debug }, progress);
        }

        public static PackageResult Export(ExportRequest req, Action<double, string> progress = null)
        {
            var sb = new StringBuilder();
            void P(double f, string s) { try { progress?.Invoke(f, s); } catch { } }
            PackageResult Fail(string why) { sb.AppendLine("ERROR: " + why); return new PackageResult { Success = false, OutputPath = req.OutputDir, Message = sb.ToString() }; }
            try
            {
                if (req == null || string.IsNullOrEmpty(req.ProjectRoot) || !Directory.Exists(req.ProjectRoot)) return Fail("Project folder not found.");
                if (string.IsNullOrEmpty(req.OutputDir)) return Fail("No output folder.");
                var platform = req.Platform;
                bool mac = ExportPlatforms.IsMac(platform), win = ExportPlatforms.IsWindows(platform), linux = ExportPlatforms.IsLinux(platform);
                if (req.Debug && platform != ExportPlatforms.Host) return Fail("Debug builds reference this machine's project folder and can only target " + ExportPlatforms.DisplayName(ExportPlatforms.Host) + ".");
                string name = SanitizeName(req.ProductName);

                P(0.02, "Locating the " + ExportPlatforms.DisplayName(platform) + " runtime…");
                var pack = RuntimePacks.Locate(platform);
                if (!pack.IsComplete) return Fail("Runtime for " + ExportPlatforms.DisplayName(platform) + " is not available:\n  • " + string.Join("\n  • ", pack.Problems));
                sb.AppendLine("• Runtime pack: " + pack.Source);

                // Layout: macOS -> <name>.app/Contents/{MacOS,Resources}; Windows/Linux -> <name>/ folder.
                P(0.05, "Preparing output folder…");
                Directory.CreateDirectory(req.OutputDir);
                string appRoot = Path.Combine(req.OutputDir, mac ? name + ".app" : name);
                if (Directory.Exists(appRoot)) Directory.Delete(appRoot, true);
                string binDir = mac ? Path.Combine(appRoot, "Contents", "MacOS") : appRoot;
                string resDir = mac ? Path.Combine(appRoot, "Contents", "Resources") : appRoot;
                Directory.CreateDirectory(binDir); Directory.CreateDirectory(resDir);

                // 1) The player runtime (managed, self-contained).
                P(0.10, "Copying the player runtime…");
                int copied = CopyTree(pack.ManagedDir, binDir, f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && !IsRuntimeJunk(f, pack));
                sb.AppendLine("• Player runtime: " + copied + " files");

                // 2) The native engine + its sibling libraries + shaders.
                P(0.22, "Copying the native engine…");
                string nativeName = ExportPlatforms.NativeLibraryName(platform);
                File.Copy(Path.Combine(pack.NativeDir, nativeName), Path.Combine(binDir, nativeName), true);
                int extras = 0;
                foreach (var extra in Directory.GetFiles(pack.NativeDir))
                {
                    string fn = Path.GetFileName(extra);
                    if (fn.Equals(nativeName, StringComparison.OrdinalIgnoreCase)) continue;
                    bool isLib = win ? fn.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !IsManagedAssembly(extra)
                                     : fn.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && (fn.EndsWith(".dylib") || fn.Contains(".so"));
                    if (isLib && !File.Exists(Path.Combine(binDir, fn))) { File.Copy(extra, Path.Combine(binDir, fn), true); extras++; }
                }
                if (pack.ShaderDir != null && Directory.Exists(pack.ShaderDir))
                {
                    int sh = CopyTree(pack.ShaderDir, Path.Combine(binDir, ExportPlatforms.ShaderRelativeDir(platform)), null);
                    sb.AppendLine("• Native engine + " + extras + " libraries, " + sh + " shader files");
                }
                else sb.AppendLine("• Native engine + " + extras + " libraries");

                // 3) Scripts.
                P(0.35, req.Debug ? "Validating gameplay scripts…" : "Compiling gameplay scripts…");
                string tmpDll = Path.Combine(Path.GetTempPath(), "GameScripts_" + Guid.NewGuid().ToString("N") + ".dll");
                bool scriptsOk = CompileScripts(req.ProjectRoot, tmpDll, out string scriptLog);
                sb.AppendLine(scriptsOk ? "• Scripts compiled OK" : "• SCRIPTS FAILED TO COMPILE:\n" + scriptLog);

                // 4) The project: Debug = live reference, Release = packed.
                if (req.Debug)
                {
                    P(0.80, "Writing debug marker…");
                    string projAbs = Path.GetFullPath(req.ProjectRoot).Replace("\\", "\\\\").Replace("\"", "'");
                    File.WriteAllText(Path.Combine(binDir, "player.vortex"), "{\"game\":\"" + name.Replace("\"", "'") + "\",\"debug\":true,\"projectPath\":\"" + projAbs + "\"}");
                    sb.AppendLine("• Debug build references the live project at " + Path.GetFullPath(req.ProjectRoot) + " (scripts and shaders hot-reload).");
                }
                else
                {
                    PackProject(req.ProjectRoot, binDir, name, scriptsOk ? tmpDll : null, sb, P);
                }
                try { if (File.Exists(tmpDll)) File.Delete(tmpDll); } catch { }

                // 4b) CREDITS.md for the third-party assets the asset library recorded (#77)
                try
                {
                    string credits = Editor.Core.Assets.Library.LicenseAudit.WriteCredits(req.ProjectRoot, mac ? resDir : appRoot, req.ProductName, out int credited);
                    if (credits != null) sb.AppendLine("• CREDITS.md: " + credited + " attribution-required asset(s) credited");
                }
                catch (Exception ex) { sb.AppendLine("• CREDITS.md could not be written: " + ex.Message); }

                // 5) Branding: executable name, icon, version metadata, launcher.
                P(0.90, "Branding…");
                string playerExe = Path.Combine(binDir, ExportPlatforms.PlayerExecutableName(platform));
                string gameExe = Path.Combine(binDir, win ? name + ".exe" : name);
                if (File.Exists(playerExe) && !string.Equals(playerExe, gameExe, StringComparison.Ordinal))
                {
                    File.Move(playerExe, gameExe, true);   // the apphost finds Vortex.Player.dll by its embedded name; the exe may carry any name
                }
                if (mac)
                {
                    string iconFile = null;
                    if (!string.IsNullOrEmpty(req.IcnsPath) && File.Exists(req.IcnsPath)) { File.Copy(req.IcnsPath, Path.Combine(resDir, "AppIcon.icns"), true); iconFile = "AppIcon"; }
                    File.WriteAllText(Path.Combine(appRoot, "Contents", "Info.plist"), InfoPlist(req.ProductName, name, req.BundleIdentifier, req.Version, iconFile));
                    try { Chmod(gameExe); } catch { }
                    string signLog = RunTool("codesign", "--force --deep --sign - \"" + appRoot + "\"");
                    RunTool("xattr", "-dr com.apple.quarantine \"" + appRoot + "\"");
                    sb.AppendLine("• " + name + ".app — " + req.ProductName + " " + req.Version + " (" + req.BundleIdentifier + ")" + (iconFile != null ? ", icon" : ", no icon") + (signLog == null ? ", signed ad-hoc" : ", not signed: " + signLog));
                }
                else if (win)
                {
                    string iconNote = "no icon";
                    try
                    {
                        byte[] ico = !string.IsNullOrEmpty(req.IcoPath) && File.Exists(req.IcoPath) ? File.ReadAllBytes(req.IcoPath) : null;
                        PeResourceWriter.SetIconAndVersion(gameExe, ico, req.ProductName, req.CompanyName, req.Version, req.ProductName);
                        iconNote = ico != null ? "icon + version info embedded" : "version info embedded";
                    }
                    catch (Exception ex) { iconNote = "icon/version not embedded: " + ex.Message; }
                    File.WriteAllText(Path.Combine(binDir, "README.txt"), req.ProductName + " " + req.Version + "\r\n\r\nDouble-click " + name + ".exe to play.\r\nMade with Vortex Engine.\r\n");
                    sb.AppendLine("• " + name + ".exe — " + req.ProductName + " " + req.Version + " (" + iconNote + ")");
                }
                else if (linux)
                {
                    string iconLine = "";
                    if (!string.IsNullOrEmpty(req.PngIconPath) && File.Exists(req.PngIconPath)) { File.Copy(req.PngIconPath, Path.Combine(binDir, name + ".png"), true); iconLine = "Icon=" + name + ".png\n"; }
                    File.WriteAllText(Path.Combine(binDir, name + ".desktop"), "[Desktop Entry]\nType=Application\nName=" + req.ProductName + "\nExec=./" + name + "\n" + iconLine + "Terminal=false\nCategories=Game;\n");
                    File.WriteAllText(Path.Combine(binDir, "run.sh"), "#!/bin/sh\ncd \"$(dirname \"$0\")\" && ./" + name + " \"$@\"\n");
                    try { Chmod(gameExe); Chmod(Path.Combine(binDir, "run.sh")); } catch { }
                    sb.AppendLine("• " + name + " — " + req.ProductName + " " + req.Version + " (desktop entry" + (iconLine != "" ? " + icon" : "") + ")");
                }

                // 6) Archive.
                string archive = null;
                if (req.CreateArchive && scriptsOk)
                {
                    P(0.96, mac ? "Creating disk image…" : "Creating archive…");
                    if (mac)
                    {
                        archive = Path.Combine(req.OutputDir, name + "-" + req.Version + ".dmg");
                        try { File.Delete(archive); } catch { }
                        string err = RunTool("hdiutil", "create -volname \"" + req.ProductName + "\" -srcfolder \"" + appRoot + "\" -ov -format UDZO \"" + archive + "\"");
                        if (err != null) { sb.AppendLine("• Disk image failed: " + err); archive = null; }
                    }
                    else
                    {
                        archive = Path.Combine(req.OutputDir, name + "-" + req.Version + "-" + pack.Rid + ".zip");
                        try { File.Delete(archive); } catch { }
                        System.IO.Compression.ZipFile.CreateFromDirectory(appRoot, archive, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: true);
                    }
                    if (archive != null) sb.AppendLine("• Archive: " + archive);
                }

                P(1.0, "Done.");
                return new PackageResult { Success = scriptsOk, OutputPath = appRoot, Message = sb.ToString() };
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex.Message);
                return new PackageResult { Success = false, OutputPath = req?.OutputDir, Message = sb.ToString() };
            }
        }

        private static void PackProject(string projectRoot, string binDir, string name, string scriptsDll, StringBuilder sb, Action<double, string> P)
        {
            var entries = new List<KeyValuePair<string, byte[]>>();
            var sceneEntries = new List<KeyValuePair<string, byte[]>>();
            var manifest = Path.Combine(projectRoot, "project.vortex");
            if (File.Exists(manifest)) entries.Add(new KeyValuePair<string, byte[]>("project.vortex", File.ReadAllBytes(manifest)));
            P(0.45, "Packing assets…");
            var assetsSrc = Path.Combine(projectRoot, "Assets");
            int assetCount = 0;
            if (Directory.Exists(assetsSrc))
            {
                var files = Directory.GetFiles(assetsSrc, "*", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    var f = files[i];
                    if (Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = "Assets/" + f.Substring(assetsSrc.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (Path.GetExtension(f).Equals(".vscene", StringComparison.OrdinalIgnoreCase) && rel.StartsWith("Assets/Scenes/", StringComparison.OrdinalIgnoreCase))
                        sceneEntries.Add(new KeyValuePair<string, byte[]>(rel, File.ReadAllBytes(f)));
                    else entries.Add(new KeyValuePair<string, byte[]>(rel, File.ReadAllBytes(f)));
                    assetCount++;
                    if ((i & 15) == 0) P(0.45 + 0.30 * ((double)i / Math.Max(1, files.Length)), "Packing assets… (" + assetCount + ")");
                }
            }
            if (scriptsDll != null && File.Exists(scriptsDll)) entries.Add(new KeyValuePair<string, byte[]>("GameScripts.dll", File.ReadAllBytes(scriptsDll)));
            var mixerCfg = Path.Combine(projectRoot, AudioMixerConfig.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(mixerCfg)) entries.Add(new KeyValuePair<string, byte[]>(AudioMixerConfig.RelativePath, File.ReadAllBytes(mixerCfg)));
            P(0.80, "Writing asset paks…");
            VortexPak.Write(Path.Combine(binDir, "Assets.vpak"), entries);
            if (sceneEntries.Count > 0)
            {
                var scenesOut = Path.Combine(binDir, "Scenes");
                Directory.CreateDirectory(scenesOut);
                foreach (var se in sceneEntries)
                    VortexPak.Write(Path.Combine(scenesOut, Path.GetFileNameWithoutExtension(se.Key) + ".vpak"), new List<KeyValuePair<string, byte[]>> { se });
            }
            sb.AppendLine("• Packed " + (assetCount - sceneEntries.Count) + " shared assets + manifest + scripts -> Assets.vpak; " + sceneEntries.Count + " scene(s) -> Scenes/*.vpak");
            File.WriteAllText(Path.Combine(binDir, "player.vortex"), "{\"game\":\"" + name.Replace("\"", "'") + "\",\"pak\":\"Assets.vpak\",\"scenePaks\":\"Scenes\",\"scriptsDll\":\"GameScripts.dll\"}");
        }

        /// <summary>Files of a runtime folder that must not ship: logs and crash dumps left behind by development runs.</summary>
        private static bool IsRuntimeJunk(string file, RuntimePack pack)
        {
            string n = Path.GetFileName(file);
            return n.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                || n.Equals("player.vortex", StringComparison.OrdinalIgnoreCase) || n.Equals("Assets.vpak", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsManagedAssembly(string path)
        {
            try { using var fs = File.OpenRead(path); using var pe = new System.Reflection.PortableExecutable.PEReader(fs); return pe.HasMetadata; }
            catch { return false; }
        }

        public static string SanitizeName(string n)
        {
            if (string.IsNullOrWhiteSpace(n)) n = "Game";
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Replace('/', '_').Replace('\\', '_').Trim().TrimEnd('.');
            return n.Length > 0 ? n : "Game";
        }

        private static int CopyTree(string src, string dst, Func<string, bool> filter)
        {
            int n = 0;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                if (filter != null && !filter(f)) continue;
                string rel = f.Substring(src.Length).TrimStart('\\', '/');
                string target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(f, target, true);
                n++;
            }
            return n;
        }

        private static void Chmod(string path)
        {
            if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        /// <summary>Run a command-line tool; null on success, otherwise the error text (tool missing or non-zero exit).</summary>
        private static string RunTool(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
                using var p = Process.Start(psi);
                string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
                p.WaitForExit(120000);
                return p.ExitCode == 0 ? null : (err ?? "").Trim();
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static string InfoPlist(string displayName, string exeName, string bundleId, string version, string iconFile) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n" +
            "  <key>CFBundleName</key><string>" + Esc(displayName) + "</string>\n  <key>CFBundleDisplayName</key><string>" + Esc(displayName) + "</string>\n" +
            "  <key>CFBundleIdentifier</key><string>" + Esc(bundleId) + "</string>\n  <key>CFBundleExecutable</key><string>" + Esc(exeName) + "</string>\n" +
            (iconFile != null ? "  <key>CFBundleIconFile</key><string>" + Esc(iconFile) + "</string>\n" : "") +
            "  <key>CFBundlePackageType</key><string>APPL</string>\n  <key>CFBundleVersion</key><string>" + Esc(version) + "</string>\n  <key>CFBundleShortVersionString</key><string>" + Esc(version) + "</string>\n" +
            "  <key>LSMinimumSystemVersion</key><string>13.0</string>\n  <key>NSHighResolutionCapable</key><true/>\n  <key>NSSupportsAutomaticGraphicsSwitching</key><true/>\n  <key>LSApplicationCategoryType</key><string>public.app-category.games</string>\n</dict>\n</plist>\n";
        private static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static bool CompileScripts(string projectRoot, string outDll, out string log)
        {
            log = null;
            var dir = Path.Combine(projectRoot, "Assets", "Scripts");
            if (!Directory.Exists(dir)) return true;
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("VortexScripting.cs", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length == 0) return true;
            return Editor.Scripting.RoslynScriptCompiler.EmitToFile(files, outDll, out log);
        }
    }
}
