using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Editor.Core.Assets.Store;

namespace Editor.Core.Claude
{
    /// <summary>Where the Claude panel's credentials come from.</summary>
    public enum ClaudeCredential
    {
        None,
        /// <summary>An API key the user added in the editor (kept in the editor's key store).</summary>
        ApiKey,
        /// <summary><c>ANTHROPIC_API_KEY</c> or <c>ANTHROPIC_AUTH_TOKEN</c> in the environment.</summary>
        Environment,
        /// <summary>The Anthropic account the user signed in to with the Anthropic CLI (<c>ant auth login</c>).</summary>
        AnthropicAccount,
    }

    /// <summary>
    /// The Claude panel's credentials — always the user's own, never a key shipped with the editor:
    /// <list type="bullet">
    /// <item>an API key the user adds in the editor (key store, encrypted per user);</item>
    /// <item><c>ANTHROPIC_API_KEY</c> / <c>ANTHROPIC_AUTH_TOKEN</c> in the environment;</item>
    /// <item>the Anthropic account the user signed in to with the official Anthropic CLI: <c>ant auth login</c> is a
    /// browser OAuth sign-in against the Claude Console that stores a profile the official SDK reads by itself
    /// (and refreshes) — usage is billed to that Console account like an API key.</item>
    /// </list>
    /// A Claude subscription (Pro / Max) is not offered here: Anthropic does not allow third-party apps to offer
    /// claude.ai login. Subscribers use Claude Code itself, connected to the editor's MCP server
    /// (<see cref="ClaudeCode"/>).
    /// </summary>
    public static class ClaudeAccount
    {
        public const string KeyName = "anthropic";

        /// <summary>The Anthropic CLI's config folder: <c>ANTHROPIC_CONFIG_DIR</c>, else <c>%APPDATA%\Anthropic</c> on
        /// Windows and <c>~/.config/anthropic</c> elsewhere.</summary>
        public static string ConfigDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("ANTHROPIC_CONFIG_DIR");
                if (!string.IsNullOrWhiteSpace(env)) return env;
                if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anthropic");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "anthropic");
            }
        }

        /// <summary>The profile the SDK uses: <c>ANTHROPIC_PROFILE</c>, else the CLI's active profile, else "default".</summary>
        public static string ActiveProfile
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("ANTHROPIC_PROFILE");
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
                try
                {
                    string f = Path.Combine(ConfigDir, "active_config");
                    if (File.Exists(f))
                    {
                        string text = File.ReadAllText(f).Trim();
                        if (text.StartsWith("{"))
                        {
                            using var doc = JsonDocument.Parse(text);
                            foreach (var name in new[] { "profile", "name", "active" })
                                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
                        }
                        else if (text.Length > 0) return text;
                    }
                }
                catch { }
                return "default";
            }
        }

        /// <summary>True when the Anthropic CLI has signed in the active profile (<c>configs/&lt;profile&gt;.json</c> with its
        /// <c>authentication</c> section is what the SDK resolves; the tokens sit in <c>credentials/&lt;profile&gt;.json</c>).</summary>
        public static bool HasAccountProfile
        {
            get
            {
                try
                {
                    string profile = ActiveProfile;
                    return File.Exists(Path.Combine(ConfigDir, "configs", profile + ".json"))
                        && File.Exists(Path.Combine(ConfigDir, "credentials", profile + ".json"));
                }
                catch { return false; }
            }
        }

        /// <summary>An <c>ANTHROPIC_API_KEY</c> in the environment wins over a signed-in account (the SDK's order).</summary>
        public static bool EnvironmentShadowsAccount =>
            HasAccountProfile && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        /// <summary>Which credentials the next request uses — in the order the panel and the SDK resolve them.</summary>
        public static ClaudeCredential Current
        {
            get
            {
                if (StoreKeys.Has(KeyName)) return ClaudeCredential.ApiKey;
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"))) return ClaudeCredential.Environment;
                return HasAccountProfile ? ClaudeCredential.AnthropicAccount : ClaudeCredential.None;
            }
        }

        public static string Describe(ClaudeCredential c) => c switch
        {
            ClaudeCredential.ApiKey => "API key",
            ClaudeCredential.Environment => "API key from the environment",
            ClaudeCredential.AnthropicAccount => "Anthropic account (" + ActiveProfile + ")",
            _ => "Not signed in",
        };

        /// <summary>A turn at high effort can think for many minutes; the SDK's default (10 min) would cut it off.</summary>
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);

        /// <summary>An SDK client with the user's credentials. <paramref name="http"/> carries the editor's HTTP stack
        /// (tests' scripted responses).</summary>
        public static AnthropicClient CreateClient(HttpClient http)
        {
            string key = StoreKeys.Get(KeyName);
            if (!string.IsNullOrWhiteSpace(key))
                return new AnthropicClient { ApiKey = key, HttpClient = http, Timeout = RequestTimeout };
            // the SDK resolves ANTHROPIC_API_KEY, ANTHROPIC_AUTH_TOKEN and the Anthropic CLI's profile itself (and
            // refreshes the profile's token)
            return new AnthropicClient { HttpClient = http, Timeout = RequestTimeout };
        }

        // ------------------------------------------------------------------ the Anthropic CLI (ant)

        /// <summary>The Anthropic CLI, or null. Apps started from the Finder or the Start menu do not see the shell's
        /// PATH, so the usual install folders are tried too.</summary>
        public static string FindCli() => ProcessTools.FindExecutable("ant", new[]
        {
            "/opt/homebrew/bin/ant", "/usr/local/bin/ant", "/usr/bin/ant",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "go", "bin", "ant"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "ant"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ant", "ant.exe"),
        });

        public const string CliInstallMac = "brew install anthropics/tap/ant && xattr -d com.apple.quarantine \"$(brew --prefix)/bin/ant\"";
        public const string CliReleases = "https://github.com/anthropics/anthropic-cli/releases";

        /// <summary>Sign in with the Anthropic CLI: it opens the browser, the user picks the organization and workspace,
        /// and the CLI stores the profile the SDK then uses. Returns false with the CLI's output when it failed.</summary>
        /// <param name="onLine">Each output line as it comes (the panel shows the sign-in link in case no browser opened).</param>
        public static Task<(bool ok, string output)> SignInAsync(CancellationToken ct, Action<string> onLine = null) =>
            RunCliAsync(new[] { "auth", "login" }, TimeSpan.FromMinutes(10), ct, onLine);

        public static Task<(bool ok, string output)> SignOutAsync(CancellationToken ct) => RunCliAsync(new[] { "auth", "logout" }, TimeSpan.FromSeconds(30), ct);

        public static Task<(bool ok, string output)> StatusAsync(CancellationToken ct) => RunCliAsync(new[] { "auth", "status" }, TimeSpan.FromSeconds(20), ct);

        private static Task<(bool ok, string output)> RunCliAsync(string[] args, TimeSpan timeout, CancellationToken ct, Action<string> onLine = null)
        {
            string cli = FindCli();
            if (cli == null) return Task.FromResult((false, "The Anthropic CLI (ant) is not installed."));
            return ProcessTools.RunAsync(cli, args, timeout, ct, onLine);
        }

        /// <summary>The Anthropic CLI's sign-in in a terminal window (when the background sign-in does not finish, e.g.
        /// because the CLI wants a terminal). The panel notices the new profile when the user comes back.</summary>
        public static bool SignInInTerminal()
        {
            string cli = FindCli();
            return cli != null && ProcessTools.OpenInTerminal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cli, "auth", "login");
        }

        /// <summary>The first https link in a line of CLI output, or null.</summary>
        public static string LinkIn(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int i = line.IndexOf("https://", StringComparison.Ordinal);
            if (i < 0) return null;
            int end = i;
            while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != '"' && line[end] != '\'' && line[end] != '>') end++;
            return line.Substring(i, end - i).TrimEnd('.', ',', ')');
        }
    }

    /// <summary>Claude Code with the user's own Claude subscription, in their own terminal: it opens on the project
    /// folder, whose <c>.mcp.json</c> points at the editor's MCP server.</summary>
    public static class ClaudeCode
    {
        public const string InstallUrl = "https://code.claude.com/docs/en/setup";

        public static string FindCli() => ProcessTools.FindExecutable("claude", new[]
        {
            "/opt/homebrew/bin/claude", "/usr/local/bin/claude",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "local", "claude"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
        });

        /// <summary>Open a terminal on <paramref name="folder"/> running Claude Code. False when no terminal could be
        /// started (the caller shows the command instead).</summary>
        public static bool OpenInTerminal(string folder, string claude) => ProcessTools.OpenInTerminal(folder, claude);

    }

    /// <summary>Finding and running the command-line tools the panel works with.</summary>
    public static class ProcessTools
    {
        /// <summary>Open a terminal window on <paramref name="folder"/> that runs <paramref name="exe"/> with
        /// <paramref name="args"/> (Terminal on macOS, Windows Terminal or cmd on Windows, the desktop's terminal on Linux).
        /// False when no terminal could be started.</summary>
        public static bool OpenInTerminal(string folder, string exe, params string[] args)
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    string cmd = "cd " + ShellQuote(folder) + " && " + string.Join(" ", new[] { exe }.Concat(args).Select(ShellQuote));
                    string script = "tell application \"Terminal\"\n  activate\n  do script \"" + cmd.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\nend tell";
                    var psi = new ProcessStartInfo("osascript") { UseShellExecute = false, CreateNoWindow = true };
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add(script);
                    using var p = Process.Start(psi);
                    return p != null;
                }
                if (OperatingSystem.IsWindows())
                {
                    string wt = FindExecutable("wt.exe", new string[0]);
                    var psi = wt != null
                        ? new ProcessStartInfo(wt) { UseShellExecute = true }
                        : new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = folder };
                    if (wt != null) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(folder); psi.ArgumentList.Add("cmd"); }
                    psi.ArgumentList.Add("/k");
                    psi.ArgumentList.Add(exe);
                    foreach (var a in args) psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi);
                    return p != null;
                }
                foreach (var term in new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xterm" })
                {
                    string t = FindExecutable(term, new string[0]);
                    if (t == null) continue;
                    var psi = new ProcessStartInfo(t) { UseShellExecute = false, WorkingDirectory = folder };
                    psi.ArgumentList.Add(term == "gnome-terminal" ? "--" : "-e");
                    psi.ArgumentList.Add(exe);
                    foreach (var a in args) psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi);
                    return p != null;
                }
            }
            catch { }
            return false;
        }

        internal static string ShellQuote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

        public static string FindExecutable(string name, IEnumerable<string> knownPaths)
        {
            try
            {
                var exts = OperatingSystem.IsWindows() && Path.GetExtension(name).Length == 0 ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    foreach (var ext in exts)
                    {
                        string p = Path.Combine(dir.Trim('"'), name + ext);
                        if (File.Exists(p)) return p;
                    }
                return knownPaths.FirstOrDefault(File.Exists);
            }
            catch { return null; }
        }

        /// <summary>Run a tool to the end (no window, no input); returns its exit status and combined output.</summary>
        public static async Task<(bool ok, string output)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onLine = null)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var output = new StringBuilder();
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void Line(string line)
            {
                if (line == null) return;
                lock (output) output.AppendLine(line);
                try { onLine?.Invoke(line); } catch { }
            }
            p.OutputDataReceived += (s, e) => Line(e.Data);
            p.ErrorDataReceived += (s, e) => Line(e.Data);
            try { p.Start(); }
            catch (Exception ex) { return (false, ex.Message); }
            try { p.StandardInput.Close(); } catch { }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try { await p.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                lock (output) return (false, output.ToString().Trim() + (ct.IsCancellationRequested ? "\n(cancelled)" : "\n(timed out)"));
            }
            lock (output) return (p.ExitCode == 0, output.ToString().Trim());
        }
    }
}
