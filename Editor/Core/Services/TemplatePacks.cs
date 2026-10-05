using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Core.Services
{
    /// <summary>
    /// Template content that is not installed with the editor (#299). The big templates keep their models, textures
    /// and sounds in Git LFS; release builds don't download LFS (bandwidth), so an installed editor finds pointer files
    /// in Templates/. The full template is a release asset instead — <c>Template-&lt;Id&gt;.zip</c> on the GitHub
    /// release of this engine version — downloaded once when the user creates a project from it, and cached per
    /// engine version under &lt;VortexAppData&gt;/Templates/&lt;version&gt;/&lt;Id&gt;.
    /// </summary>
    public static class TemplatePacks
    {
        public const string AssetPrefix = "Template-";
        private const string CompleteMarker = ".vortex-template-pack";
        private static readonly string[] LfsTracked =
        {
            ".glb", ".gltf", ".bin", ".fbx", ".obj", ".png", ".jpg", ".jpeg", ".tga", ".hdr", ".exr", ".dds", ".wav", ".ogg", ".mp3", ".flac", ".vsndc", ".ttf",
        };

        /// <summary>Override for tests (null = the GitHub release of this engine version).</summary>
        public static Func<string, string> ReleaseApiUrl = version =>
            "https://api.github.com/repos/" + EngineInfo.RepoOwner + "/" + EngineInfo.RepoName + "/releases/tags/v" + version;

        /// <summary>Override for tests.</summary>
        public static HttpMessageHandler Handler;

        public static string CacheRoot => Path.Combine(EditorPaths.VortexAppData, "Templates", EngineInfo.VersionString);

        /// <summary>True when the folder holds Git LFS pointer files instead of real content (samples the tracked file types).</summary>
        public static bool HasLfsPointers(string dir)
        {
            try
            {
                int checkedFiles = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (Array.IndexOf(LfsTracked, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                    if (IsLfsPointer(f)) return true;
                    if (++checkedFiles >= 40) break;
                }
            }
            catch { }
            return false;
        }

        public static bool IsLfsPointer(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 1024) return false;   // pointers are ~130 bytes
                using (var fs = File.OpenRead(file))
                {
                    var buf = new byte[40];
                    int n = fs.Read(buf, 0, buf.Length);
                    return Encoding.ASCII.GetString(buf, 0, n).StartsWith("version https://git-lfs.github.com/spec/", StringComparison.Ordinal);
                }
            }
            catch { return false; }
        }

        /// <summary>The cached full template (project folder), or null when it has not been downloaded for this version.</summary>
        public static string CachedProjectDir(string templateId)
        {
            if (string.IsNullOrEmpty(templateId)) return null;
            var dir = Path.Combine(CacheRoot, templateId);
            if (!File.Exists(Path.Combine(dir, CompleteMarker))) return null;
            if (File.Exists(Path.Combine(dir, "project.vortex"))) return dir;
            var nested = Directory.Exists(dir) ? Directory.GetDirectories(dir).FirstOrDefault(d => File.Exists(Path.Combine(d, "project.vortex"))) : null;
            return nested;
        }

        public sealed class PackInfo
        {
            public string Name { get; set; }
            public string Url { get; set; }
            public long Size { get; set; }
        }

        private static HttpClient NewClient()
        {
            var c = Handler != null ? new HttpClient(Handler, false) : new HttpClient();
            c.Timeout = TimeSpan.FromHours(2);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("VortexEngine/" + EngineInfo.VersionString);
            return c;
        }

        /// <summary>The pack of a template on this engine version's release (name, download URL, size), or null.</summary>
        public static async Task<PackInfo> FindAsync(string templateId, CancellationToken ct = default(CancellationToken))
        {
            string wanted = AssetPrefix + templateId + ".zip";
            using (var client = NewClient())
            using (var resp = await client.GetAsync(ReleaseApiUrl(EngineInfo.VersionString), ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using (var doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                        return new PackInfo
                        {
                            Name = name,
                            Url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null,
                            Size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long size) ? size : 0,
                        };
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// The folder to create projects from: the template's own folder when it has its content, else the cached
        /// pack — downloaded and unpacked first when needed (progress 0..1). Throws with a readable message when the
        /// pack cannot be found or downloaded.
        /// </summary>
        public static async Task<string> EnsureAsync(ProjectTemplate template, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (template == null || template.IsEmpty || string.IsNullOrEmpty(template.ProjectDir)) return template?.ProjectDir;
            if (!HasLfsPointers(template.ProjectDir)) return template.ProjectDir;
            var cached = CachedProjectDir(template.Id);
            if (cached != null) return cached;

            var pack = await FindAsync(template.Id, ct).ConfigureAwait(false);
            if (pack == null || string.IsNullOrEmpty(pack.Url))
                throw new InvalidOperationException("The " + template.Name + " template content is not installed, and the release of Vortex " + EngineInfo.VersionString +
                                                    " has no " + AssetPrefix + template.Id + ".zip to download it from.");
            string target = Path.Combine(CacheRoot, template.Id);
            string tmpZip = Path.Combine(Path.GetTempPath(), "vortex-template-" + Guid.NewGuid().ToString("N") + ".zip");
            string tmpDir = target + ".partial";
            try
            {
                using (var client = NewClient())
                using (var resp = await client.GetAsync(pack.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Downloading " + pack.Name + " failed (HTTP " + (int)resp.StatusCode + ").");
                    long total = resp.Content.Headers.ContentLength ?? pack.Size;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = File.Create(tmpZip))
                    {
                        var buf = new byte[1 << 16];
                        long done = 0;
                        int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                            done += n;
                            if (total > 0) progress?.Report(Math.Min(0.95, 0.95 * done / total));
                        }
                    }
                }
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                Directory.CreateDirectory(tmpDir);
                ExtractSafely(tmpZip, tmpDir);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(tmpDir, target);
                File.WriteAllText(Path.Combine(target, CompleteMarker), pack.Name + " " + pack.Size);
                progress?.Report(1.0);
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
            return CachedProjectDir(template.Id) ?? throw new InvalidOperationException(pack.Name + " does not contain a Vortex project (project.vortex).");
        }

        /// <summary>Unzip, refusing entries that would land outside the folder ("zip slip").</summary>
        private static void ExtractSafely(string zip, string dir)
        {
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var entry in archive.Entries)
                {
                    string dest = Path.GetFullPath(Path.Combine(dir, entry.FullName));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The template pack has an entry outside its folder: " + entry.FullName);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal)) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                }
            }
        }

        /// <summary>Human-readable size ("430 MB").</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            double mb = bytes / (1024.0 * 1024.0);
            return mb >= 1024 ? (mb / 1024).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB" : Math.Round(mb).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }
    }
}
