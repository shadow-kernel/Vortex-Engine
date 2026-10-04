using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Editor.Core.Services.Build
{
    /// <summary>The platforms a game can be exported for. Every platform needs a runtime pack (the standalone
    /// player published for that platform + the native engine built for it + its shaders).</summary>
    public enum ExportPlatform
    {
        MacOSArm64,
        MacOSX64,
        WindowsX64,
        LinuxX64,
    }

    public static class ExportPlatforms
    {
        public static string Rid(ExportPlatform p)
        {
            switch (p)
            {
                case ExportPlatform.MacOSArm64: return "osx-arm64";
                case ExportPlatform.MacOSX64: return "osx-x64";
                case ExportPlatform.WindowsX64: return "win-x64";
                default: return "linux-x64";
            }
        }

        public static string DisplayName(ExportPlatform p)
        {
            switch (p)
            {
                case ExportPlatform.MacOSArm64: return "macOS (Apple Silicon)";
                case ExportPlatform.MacOSX64: return "macOS (Intel)";
                case ExportPlatform.WindowsX64: return "Windows (x64)";
                default: return "Linux (x64)";
            }
        }

        public static bool IsMac(ExportPlatform p) => p == ExportPlatform.MacOSArm64 || p == ExportPlatform.MacOSX64;
        public static bool IsWindows(ExportPlatform p) => p == ExportPlatform.WindowsX64;
        public static bool IsLinux(ExportPlatform p) => p == ExportPlatform.LinuxX64;

        /// <summary>The platform this editor runs on (the only one that can build Debug exports and run the result).</summary>
        public static ExportPlatform Host
        {
            get
            {
                if (OperatingSystem.IsMacOS()) return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? ExportPlatform.MacOSArm64 : ExportPlatform.MacOSX64;
                if (OperatingSystem.IsWindows()) return ExportPlatform.WindowsX64;
                return ExportPlatform.LinuxX64;
            }
        }

        public static string PlayerExecutableName(ExportPlatform p) => IsWindows(p) ? "Vortex.Player.exe" : "Vortex.Player";
        public static string NativeLibraryName(ExportPlatform p) => IsWindows(p) ? "VortexAPI.dll" : IsMac(p) ? "libVortexAPI.dylib" : "libVortexAPI.so";
        /// <summary>Shader folder inside a runtime pack / shipped game, relative to the executable, matching what
        /// that platform's backend loads: Metal sources in Shaders/msl on macOS, compiled SPIR-V modules in
        /// Shaders/spirv on Linux (SDL GPU / Vulkan), and the flat Shaders/*.hlsl folder the DirectX engine reads
        /// on Windows. Mirrors Engine/Graphics/SdlGpu/SdlGpuShaderFormat.h.</summary>
        public static string ShaderRelativeDir(ExportPlatform p) =>
            IsMac(p) ? Path.Combine("Shaders", "msl") : IsLinux(p) ? Path.Combine("Shaders", "spirv") : "Shaders";

        public static IEnumerable<ExportPlatform> All()
        {
            yield return ExportPlatform.MacOSArm64;
            yield return ExportPlatform.MacOSX64;
            yield return ExportPlatform.WindowsX64;
            yield return ExportPlatform.LinuxX64;
        }
    }

    /// <summary>
    /// A runtime pack = everything the exported game needs besides the project itself, for ONE platform:
    /// the published standalone player (managed, self-contained), the native engine library (+ sibling
    /// libraries) and the engine shaders. The host platform's pack is normally the editor's own installation;
    /// packs for other platforms are built on that platform (tools/make-runtime-pack.*) and installed under
    /// the user's application data (Runtimes/&lt;rid&gt;), or found in the repository's dist/runtimes folder.
    /// </summary>
    public sealed class RuntimePack
    {
        public ExportPlatform Platform;
        public string Rid => ExportPlatforms.Rid(Platform);
        /// <summary>Folder holding the published player (Vortex.Player + Vortex.Player.dll + runtime).</summary>
        public string ManagedDir;
        /// <summary>Folder holding the native engine library and its sibling libraries.</summary>
        public string NativeDir;
        /// <summary>Folder holding the shaders this platform loads (…/Shaders/msl, …/Shaders/spirv or …/Shaders).</summary>
        public string ShaderDir;
        public string Source;   // where it was found (for the UI)
        public readonly List<string> Problems = new List<string>();
        public bool IsComplete => Problems.Count == 0;

        public override string ToString() => Rid + (IsComplete ? " ready" : " incomplete: " + string.Join("; ", Problems));
    }

    public static class RuntimePacks
    {
        /// <summary>Where installed packs live: &lt;app data&gt;/VortexEngine/Runtimes/&lt;rid&gt;.</summary>
        public static string InstallRoot => Path.Combine(EditorPaths.VortexAppData, "Runtimes");

        /// <summary>Locate the best pack for a platform. The result is never null; check IsComplete / Problems.</summary>
        public static RuntimePack Locate(ExportPlatform platform)
        {
            string rid = ExportPlatforms.Rid(platform);
            foreach (var root in CandidateRoots(rid))
            {
                var pack = FromRoot(platform, root);
                if (pack != null && pack.IsComplete) return pack;
            }
            // Host platform: compose the editor's own runtime (dev tree or the .app bundle).
            if (platform == ExportPlatforms.Host)
            {
                var host = HostPack(platform);
                if (host.IsComplete) return host;
                return host;
            }
            var missing = new RuntimePack { Platform = platform, Source = "(not installed)" };
            missing.Problems.Add("No runtime pack for " + ExportPlatforms.DisplayName(platform) + " (" + rid + "). Build it on that platform with tools/make-runtime-pack (see Managed/README.md) and install it into " + Path.Combine(InstallRoot, rid) + ".");
            return missing;
        }

        public static IEnumerable<string> CandidateRoots(string rid)
        {
            string env = Environment.GetEnvironmentVariable("VORTEX_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(env)) yield return Path.Combine(env, rid);
            string baseDir = AppContext.BaseDirectory;
            yield return Path.Combine(baseDir, "Runtimes", rid);
            yield return Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", "Runtimes", rid));   // .app bundle
            yield return Path.Combine(InstallRoot, rid);
            string repo = Editor.Core.Native.NativeLoader.FindRepositoryRoot();
            if (repo != null) yield return Path.Combine(repo, "dist", "runtimes", rid);
        }

        /// <summary>A pack rooted in one folder: player files at the root, native library at the root, Shaders/&lt;api&gt; below.</summary>
        public static RuntimePack FromRoot(ExportPlatform platform, string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            var pack = new RuntimePack { Platform = platform, ManagedDir = root, NativeDir = root, Source = root };
            string exe = Path.Combine(root, ExportPlatforms.PlayerExecutableName(platform));
            if (!File.Exists(exe) || !File.Exists(Path.Combine(root, "Vortex.Player.dll"))) pack.Problems.Add("player missing (" + ExportPlatforms.PlayerExecutableName(platform) + " + Vortex.Player.dll)");
            if (!File.Exists(Path.Combine(root, ExportPlatforms.NativeLibraryName(platform)))) pack.Problems.Add("native engine missing (" + ExportPlatforms.NativeLibraryName(platform) + ")");
            string sh = Path.Combine(root, ExportPlatforms.ShaderRelativeDir(platform));
            if (Directory.Exists(sh) && Directory.GetFiles(sh).Length > 0) pack.ShaderDir = sh;
            else pack.Problems.Add("shaders missing (" + ExportPlatforms.ShaderRelativeDir(platform) + ")");
            return pack;
        }

        /// <summary>The editor's own runtime for the platform it runs on: the player next to the editor / in the
        /// bundle / in the dev build tree, the native library that is loaded right now, and its shaders.</summary>
        public static RuntimePack HostPack(ExportPlatform platform)
        {
            var pack = new RuntimePack { Platform = platform, Source = "this installation" };
            pack.ManagedDir = FindHostPlayerDirectory(platform);
            if (pack.ManagedDir == null) pack.Problems.Add("Vortex.Player was not found next to the editor (build Managed/Vortex.Player or run make-app.sh)");
            string nativeLib = Editor.Core.Native.NativeLoader.ResolvedLibraryPath;
            if (nativeLib != null && File.Exists(nativeLib)) pack.NativeDir = Path.GetDirectoryName(nativeLib);
            else pack.Problems.Add("the native engine library is not loaded");
            string shaders = Editor.Core.Native.NativeLoader.ShaderDirectory;
            if (shaders != null && Directory.Exists(shaders)) pack.ShaderDir = shaders;
            else if (!ExportPlatforms.IsWindows(platform)) pack.Problems.Add("shader directory not found");
            return pack;
        }

        private static string FindHostPlayerDirectory(ExportPlatform platform)
        {
            string exeName = ExportPlatforms.PlayerExecutableName(platform);
            string baseDir = AppContext.BaseDirectory;
            var candidates = new List<string> { baseDir, Path.Combine(baseDir, "player"), Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", "Player")) };
            foreach (var cfg in new[] { "Release", "Debug" })
                candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Vortex.Player", "bin", cfg, "net10.0")));
            string repo = Editor.Core.Native.NativeLoader.FindRepositoryRoot();
            if (repo != null) foreach (var cfg in new[] { "Release", "Debug" }) candidates.Add(Path.Combine(repo, "Managed", "Vortex.Player", "bin", cfg, "net10.0"));
            foreach (var c in candidates)
                if (File.Exists(Path.Combine(c, "Vortex.Player.dll")) && File.Exists(Path.Combine(c, exeName))) return c;
            return null;
        }

        /// <summary>Install a pack from a folder (or a .zip) into the user's Runtimes/&lt;rid&gt; folder. Returns the pack.</summary>
        public static RuntimePack Install(ExportPlatform platform, string folderOrZip)
        {
            string target = Path.Combine(InstallRoot, ExportPlatforms.Rid(platform));
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            if (File.Exists(folderOrZip) && folderOrZip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                System.IO.Compression.ZipFile.ExtractToDirectory(folderOrZip, target);
            else CopyTree(folderOrZip, target);
            // a zip may wrap everything in one top-level folder: unwrap it
            var entries = Directory.GetFileSystemEntries(target);
            if (entries.Length == 1 && Directory.Exists(entries[0]))
            {
                string inner = entries[0];
                foreach (var e in Directory.GetFileSystemEntries(inner)) Directory.Move(e, Path.Combine(target, Path.GetFileName(e)));
                Directory.Delete(inner);
            }
            return FromRoot(platform, target);
        }

        internal static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.Length).TrimStart('\\', '/');
                string t = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(t));
                File.Copy(f, t, true);
            }
        }
    }
}
