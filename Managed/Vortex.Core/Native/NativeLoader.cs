using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Editor.Core.Native
{
    /// <summary>
    /// Locates the native engine library (libVortexAPI.dylib / libVortexAPI.so / VortexAPI.dll) for the P/Invoke
    /// layer: next to the managed binaries, inside an .app bundle, in VORTEX_NATIVE_DIR, or — for development —
    /// in the repository's CMake build tree. Call Register() once before the first engine call.
    /// </summary>
    public static class NativeLoader
    {
        private static bool _registered;
        public static string ResolvedLibraryPath { get; private set; }
        public static string ResolvedShaderDirectory { get; private set; }

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, Resolve);
        }

        /// <summary>Shader directory for the loaded library (resolved on first use, after the library is loaded).</summary>
        public static string ShaderDirectory => ResolvedShaderDirectory ?? (ResolvedShaderDirectory = FindShaderDirectory());

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, "VortexAPI", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(libraryName, "VortexAPI.dll", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            string[] fileNames = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? new[] { "libVortexAPI.dylib" }
                : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { "VortexAPI.dll" } : new[] { "libVortexAPI.so" };
            foreach (string dir in CandidateDirectories())
            {
                foreach (string file in fileNames)
                {
                    string path = Path.Combine(dir, file);
                    if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
                    {
                        ResolvedLibraryPath = path;
                        return handle;
                    }
                }
            }
            return IntPtr.Zero;   // fall back to the default probing (and its error message)
        }

        /// <summary>Where the engine's shader sources live for this process (used by SetShaderDirectory).</summary>
        public static string FindShaderDirectory()
        {
            string env = Environment.GetEnvironmentVariable("VORTEX_SHADER_DIR");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            // The shaders that belong to the library we actually loaded come first.
            if (!string.IsNullOrEmpty(ResolvedLibraryPath))
            {
                string beside = Path.Combine(Path.GetDirectoryName(ResolvedLibraryPath) ?? "", "Shaders", "msl");
                if (Directory.Exists(beside)) return beside;
            }
            foreach (string dir in CandidateDirectories())
            {
                string a = Path.Combine(dir, "Shaders", "msl");
                if (Directory.Exists(a)) return a;
            }
            string repo = FindRepositoryRoot();
            if (repo != null)
            {
                string src = Path.Combine(repo, "Engine", "Shaders", "msl");
                if (Directory.Exists(src)) return src;
            }
            return null;
        }

        public static IEnumerable<string> CandidateDirectories()
        {
            string env = Environment.GetEnvironmentVariable("VORTEX_NATIVE_DIR");
            if (!string.IsNullOrEmpty(env)) yield return env;
            string baseDir = AppContext.BaseDirectory;
            yield return baseDir;
            yield return Path.Combine(baseDir, "native");
            yield return Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks"));   // .app bundle: Contents/MacOS -> Contents/Frameworks
            string repo = FindRepositoryRoot();
            if (repo != null)
            {
                // Development: the repo's CMake build trees, newest build first (a stale sibling tree must not win).
                var trees = new List<string>();
                foreach (string preset in new[] { "macos-release", "macos-debug", "linux-release", "linux-debug", "windows-x64" })
                    trees.Add(Path.Combine(repo, "build", preset, "bin"));
                trees.Add(Path.Combine(repo, "x64", "Release"));
                trees.Add(Path.Combine(repo, "x64", "Debug"));
                trees.Sort((a, b) => NewestLibraryTime(b).CompareTo(NewestLibraryTime(a)));
                foreach (string t in trees) yield return t;
            }
        }

        private static DateTime NewestLibraryTime(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return DateTime.MinValue;
                DateTime best = DateTime.MinValue;
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    string n = Path.GetFileName(f);
                    if (n.IndexOf("VortexAPI", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > best) best = t;
                }
                return best;
            }
            catch { return DateTime.MinValue; }
        }

        /// <summary>Walks up from the managed binaries to the repository root (identified by Vortex.slnx).</summary>
        public static string FindRepositoryRoot()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 8 && dir != null; ++i, dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "Vortex.slnx"))) return dir.FullName;
            }
            catch { }
            return null;
        }
    }
}
