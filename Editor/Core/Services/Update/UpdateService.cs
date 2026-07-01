using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Editor.Core.Services.Update
{
    public enum BumpType { None, Patch, Minor, Major }

    /// <summary>A newer release found on GitHub.</summary>
    public sealed class UpdateInfo
    {
        public Version Latest;
        public string Tag;          // e.g. "v2.3.1"
        public string Notes;        // release body (markdown)
        public string SetupUrl;     // browser_download_url of the Setup exe
        public string SetupName;    // e.g. "VortexEngine-Setup-2.3.1.exe"
        public BumpType Bump;
    }

    /// <summary>
    /// Checks the public GitHub Releases for a newer version and, when the user accepts (or automatically for a
    /// patch), downloads the installer and runs it silently so the app updates itself and relaunches. Only ever
    /// runs for INSTALLED builds (an <c>unins000.exe</c> sits next to the exe) — never a dev run or the shipped game.
    /// Every step is wrapped so a failure can never crash startup.
    /// </summary>
    public static class UpdateService
    {
        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Vortex-Engine-Updater");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>True only for a build installed by the Inno Setup installer (has an uninstaller next to it).</summary>
        public static bool IsInstalledBuild()
        {
            try
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                return File.Exists(Path.Combine(dir, "unins000.exe"));
            }
            catch { return false; }
        }

        /// <summary>Query the latest GitHub release. Returns null on error / no update / not newer.</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            try
            {
                string url = $"https://api.github.com/repos/{Editor.Core.EngineInfo.RepoOwner}/{Editor.Core.EngineInfo.RepoName}/releases/latest";
                using (var resp = await _http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using (var doc = JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
                        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                        if (string.IsNullOrEmpty(tag)) return null;
                        var latest = ParseVersion(tag);
                        if (latest == null) return null;

                        var current = Editor.Core.EngineInfo.Version;
                        if (latest <= current) return null; // up to date (or a downgrade)

                        // find the Setup exe asset
                        string setupUrl = null, setupName = null;
                        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in assets.EnumerateArray())
                            {
                                string name = a.TryGetProperty("name", out var n) ? n.GetString() : "";
                                if (!string.IsNullOrEmpty(name) && name.StartsWith("VortexEngine-Setup", StringComparison.OrdinalIgnoreCase)
                                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    setupName = name;
                                    setupUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                                    break;
                                }
                            }
                        }
                        if (string.IsNullOrEmpty(setupUrl)) return null; // no installer asset -> can't self-update
                        if (!IsGitHubUrl(setupUrl)) return null;         // only ever run an installer fetched from GitHub

                        return new UpdateInfo
                        {
                            Latest = latest,
                            Tag = tag,
                            Notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "",
                            SetupUrl = setupUrl,
                            SetupName = setupName,
                            Bump = Classify(current, latest),
                        };
                    }
                }
            }
            catch { return null; }
        }

        // Defense against a substituted download host: only trust assets served by GitHub over HTTPS.
        private static bool IsGitHubUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                if (u.Scheme != Uri.UriSchemeHttps) return false;
                var h = u.Host.ToLowerInvariant();
                return h == "github.com" || h.EndsWith(".github.com") || h.EndsWith(".githubusercontent.com");
            }
            catch { return false; }
        }

        private static BumpType Classify(Version cur, Version next)
        {
            if (next.Major > cur.Major) return BumpType.Major;
            if (next.Major == cur.Major && next.Minor > cur.Minor) return BumpType.Minor;
            return BumpType.Patch; // same major+minor, higher build -> patch
        }

        private static Version ParseVersion(string tag)
        {
            try
            {
                string s = tag.Trim();
                if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
                // keep only leading numeric.dot (strip any -beta suffix)
                int i = 0; while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                s = s.Substring(0, i);
                var parts = s.Split('.');
                int maj = parts.Length > 0 ? int.Parse(parts[0]) : 0;
                int min = parts.Length > 1 ? int.Parse(parts[1]) : 0;
                int pat = parts.Length > 2 ? int.Parse(parts[2]) : 0;
                return new Version(maj, min, pat);
            }
            catch { return null; }
        }

        /// <summary>Download the installer to %TEMP%. Reports 0..1 progress. Returns the local path or null.</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress)
        {
            try
            {
                string dest = Path.Combine(Path.GetTempPath(), info.SetupName ?? "VortexEngine-Setup.exe");
                using (var resp = await _http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long? total = resp.Content.Headers.ContentLength;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buf = new byte[81920];
                        long read = 0; int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, n).ConfigureAwait(false);
                            read += n;
                            if (total.HasValue && total.Value > 0) progress?.Report((double)read / total.Value);
                        }
                    }
                }
                if (new FileInfo(dest).Length < 100_000) { try { File.Delete(dest); } catch { } return null; } // too small = bad download
                return dest;
            }
            catch { return null; }
        }

        /// <summary>
        /// Launch the installer silently (elevated — the app installs into Program Files) so it closes this
        /// running instance, installs the new version over it, and relaunches it; then shut down so the files
        /// are unlocked for the overwrite.
        /// </summary>
        public static void InstallAndRestart(string setupPath)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = setupPath,
                    // /VERYSILENT: no UI  /CLOSEAPPLICATIONS: RM closes us if we're slow to exit (AppMutex)
                    // /SP-: skip the "This will install…" prompt  /NORESTART: never reboot.
                    // The installer relaunches the editor via its skipifnotsilent [Run] entry — NOT /RESTARTAPPLICATIONS
                    // (which, combined with our self-exit + the [Run], could launch two instances).
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL /SP-",
                    UseShellExecute = true,
                    Verb = "runas", // elevate (Program Files write) — single UAC consent
                };
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return; // couldn't start — stay on the current version
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Update] install launch failed: " + ex.Message);
                return; // user likely declined UAC — stay on the current version
            }

            // Exit NOW (skip the blocking native engine teardown in OnExit, which could hang and keep the DLLs
            // locked) so the elevated installer can overwrite our files. It relaunches us via its [Run] entry.
            Environment.Exit(0);
        }
    }
}
