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

        /// <summary>
        /// Query the GitHub releases. Returns null on error / no update / not newer. Notes aggregate the
        /// FULL changelogs of EVERY release newer than the running version (a user updating 2.2 -> 2.4
        /// sees what 2.3 AND 2.4 brought), newest first.
        /// </summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            try
            {
                string url = $"https://api.github.com/repos/{Editor.Core.EngineInfo.RepoOwner}/{Editor.Core.EngineInfo.RepoName}/releases?per_page=20";
                using (var resp = await _http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
                        var current = Editor.Core.EngineInfo.Version;

                        UpdateInfo best = null;
                        var notes = new System.Text.StringBuilder();

                        foreach (var rel in doc.RootElement.EnumerateArray())
                        {
                            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                            if (rel.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) continue;
                            string tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                            var ver = ParseVersion(tag);
                            if (ver == null || ver <= current) continue;

                            string body = rel.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
                            notes.AppendLine("# " + (tag ?? ver.ToString(3)));
                            notes.AppendLine(body.Trim());
                            notes.AppendLine();

                            if (best != null && ver <= best.Latest) continue; // keep collecting notes, not the target

                            // find this release's Setup exe asset
                            string setupUrl = null, setupName = null;
                            if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
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
                            if (string.IsNullOrEmpty(setupUrl) || !IsGitHubUrl(setupUrl)) continue; // can't self-update to this one

                            best = new UpdateInfo
                            {
                                Latest = ver,
                                Tag = tag,
                                SetupUrl = setupUrl,
                                SetupName = setupName,
                            };
                        }

                        if (best == null) return null;
                        best.Notes = notes.ToString().Trim();
                        best.Bump = Classify(current, best.Latest);
                        return best;
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
        /// Install the downloaded setup and restart the app — via a detached RELAY script, because launching
        /// the installer directly from the running app is a guaranteed-lost race: the app still holds the
        /// AppMutex ("VortexEngineSingleInstance") when the silent installer checks it, and /SUPPRESSMSGBOXES
        /// answers the "application is running" prompt with CANCEL — the install silently never happened
        /// (the pre-2.4.1 bug: download, exit, still the old version). The relay:
        ///   1. waits for THIS process to fully exit (mutex released, files unlocked — deterministic, no race),
        ///   2. runs the installer /VERYSILENT (elevated via UAC),
        ///   3. the installer's [Run] skipifnotsilent entry relaunches the new version de-elevated.
        /// </summary>
        public static void InstallAndRestart(string setupPath)
        {
            try
            {
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                string log = Path.Combine(Path.GetTempPath(), "VortexUpdate.log");
                string script = Path.Combine(Path.GetTempPath(), "VortexUpdateRelay.ps1");

                // The relay waits up to 30s for us to die, then installs. -Verb RunAs = the single UAC consent.
                // If the setup's [Run] relaunch ever failed (defensive), the relay starts the app itself — but
                // only when the install log shows no fresh launch happened is hard to detect, so instead the
                // relay checks whether the app came back within 15s and starts it de-elevated via explorer if not.
                File.WriteAllText(script,
                    "$ErrorActionPreference = 'SilentlyContinue'\n" +
                    "Wait-Process -Id " + pid + " -Timeout 30\n" +
                    "Start-Sleep -Milliseconds 500\n" +   // let the OS release file handles/mutex fully
                    "$p = Start-Process -FilePath '" + setupPath.Replace("'", "''") + "' " +
                        "-ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS','/FORCECLOSEAPPLICATIONS','/NOCANCEL','/SP-','/LOG=\"" + log.Replace("'", "''") + "\"' " +
                        "-Verb RunAs -PassThru -Wait\n" +
                    "Start-Sleep -Seconds 3\n" +
                    "if (-not (Get-Process -Name 'Vortex Engine' -ErrorAction SilentlyContinue)) {\n" +
                    "  $exe = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Vortex Engine\\Vortex Engine.exe'\n" +
                    "  if (Test-Path $exe) { explorer.exe \"$exe\" }\n" +   // explorer relaunch = de-elevated
                    "}\n");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return; // couldn't start the relay — stay on the current version
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Update] relay launch failed: " + ex.Message);
                return; // stay on the current version
            }

            // Release the single-instance mutex explicitly, then exit NOW (skip the blocking native engine
            // teardown in OnExit, which could hang and keep DLLs locked). The relay takes over from here.
            Editor.App.ReleaseSingleInstanceMutex();
            Environment.Exit(0);
        }
    }
}
