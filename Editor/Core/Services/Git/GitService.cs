using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Editor.Core.Services.Git
{
    /// <summary>
    /// Versions a Vortex PROJECT with Git, by driving the git CLI (so it uses the user's installed git +
    /// git-lfs, credentials, remotes — exactly like the command line). All calls are async / off the UI
    /// thread. A machine without git simply does nothing (IsAvailableAsync gates everything).
    /// </summary>
    public sealed class GitService
    {
        private static GitService _instance;
        public static GitService Instance => _instance ?? (_instance = new GitService());

        // ---- low-level runner ----
        private static Task<GitResult> RunAsync(string repoPath, string args)
        {
            return Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("git", args)
                    {
                        WorkingDirectory = repoPath ?? "",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8,
                    };
                    using (var p = Process.Start(psi))
                    {
                        // Read both streams concurrently so a full pipe buffer can't deadlock.
                        var outTask = p.StandardOutput.ReadToEndAsync();
                        var errTask = p.StandardError.ReadToEndAsync();
                        p.WaitForExit();
                        return new GitResult { ExitCode = p.ExitCode, StdOut = outTask.Result, StdErr = errTask.Result };
                    }
                }
                catch (Exception ex)
                {
                    return new GitResult { ExitCode = -1, StdErr = ex.Message };
                }
            });
        }

        private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "") + "\"";

        // ---- availability ----
        public async Task<bool> IsAvailableAsync()
        {
            try { return (await RunAsync(null, "--version")).Success; } catch { return false; }
        }

        public async Task<bool> IsLfsAvailableAsync()
        {
            try { return (await RunAsync(null, "lfs version")).Success; } catch { return false; }
        }

        public async Task<bool> IsRepoAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath) || !Directory.Exists(repoPath)) return false;
            var r = await RunAsync(repoPath, "rev-parse --is-inside-work-tree");
            return r.Success && r.StdOut.Trim() == "true";
        }

        // ---- repo bootstrap ----
        /// <summary>Make <paramref name="repoPath"/> a git repo if it isn't one, and ensure a sensible
        /// .gitignore + .gitattributes (Git LFS for binary assets) exist. Safe + idempotent + never throws.</summary>
        public async Task EnsureRepoAsync(string repoPath)
        {
            try
            {
                if (string.IsNullOrEmpty(repoPath) || !Directory.Exists(repoPath)) return;
                if (!await IsAvailableAsync()) return;

                bool freshInit = false;
                if (!await IsRepoAsync(repoPath))
                {
                    await RunAsync(repoPath, "init");
                    freshInit = true;
                }

                var ignore = Path.Combine(repoPath, ".gitignore");
                if (!File.Exists(ignore)) File.WriteAllText(ignore, GitIgnoreContent);
                var attrs = Path.Combine(repoPath, ".gitattributes");
                if (!File.Exists(attrs)) File.WriteAllText(attrs, GitAttributesContent);

                if (await IsLfsAvailableAsync())
                    await RunAsync(repoPath, "lfs install --local");

                if (freshInit)
                    await RunAsync(repoPath, "add .gitignore .gitattributes");
            }
            catch (Exception ex) { Debug.WriteLine("[Git] EnsureRepo failed: " + ex.Message); }
        }

        // ---- status ----
        public async Task<IReadOnlyList<GitFileChange>> StatusAsync(string repoPath)
        {
            var list = new List<GitFileChange>();
            var res = await RunAsync(repoPath, "status --porcelain=v1 -z");
            if (!res.Success) return list;

            var tokens = res.StdOut.Split('\0');
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i];
                if (t.Length < 3) continue;
                char x = t[0], y = t[1];
                string path = t.Substring(3);

                if (x == 'R' || y == 'R') { if (i + 1 < tokens.Length) i++; } // skip the rename's old path token

                var change = new GitFileChange { Path = path };
                if (x == '?' && y == '?')
                {
                    change.Kind = GitChangeKind.Untracked;
                    change.Staged = false;
                }
                else
                {
                    change.Staged = x != ' ' && x != '?';
                    char c = change.Staged ? x : y;
                    switch (c)
                    {
                        case 'A': change.Kind = GitChangeKind.Added; break;
                        case 'D': change.Kind = GitChangeKind.Deleted; break;
                        case 'R': change.Kind = GitChangeKind.Renamed; break;
                        default: change.Kind = GitChangeKind.Modified; break;
                    }
                    if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
                        change.Kind = GitChangeKind.Conflicted;
                }
                list.Add(change);
            }
            return list;
        }

        /// <summary>Unified diff for one file (staged+unstaged vs HEAD; whole-file for new/untracked).
        /// Returns "" when there is no textual diff (e.g. unchanged, or binary handled by the caller).</summary>
        public async Task<string> DiffAsync(string repoPath, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return "";
            var hasHead = (await RunAsync(repoPath, "rev-parse --verify HEAD")).Success;
            if (hasHead)
            {
                var r = await RunAsync(repoPath, "diff --no-color HEAD -- " + Q(relativePath));
                if (!string.IsNullOrWhiteSpace(r.StdOut)) return r.StdOut;
            }
            // New / untracked file: show the whole thing as added (git returns exit 1 here — that's fine).
            var ni = await RunAsync(repoPath, "diff --no-color --no-index -- \"/dev/null\" " + Q(relativePath));
            return ni.StdOut ?? "";
        }

        /// <summary>True if git considers the path a binary blob (for the diff viewer to switch to a preview).</summary>
        public async Task<bool> IsBinaryAsync(string repoPath, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;
            var r = await RunAsync(repoPath, "diff --no-color --numstat HEAD -- " + Q(relativePath));
            // numstat prints "-\t-\t<path>" for binary files; "<add>\t<del>\t<path>" for text.
            var outp = (r.StdOut ?? "").Trim();
            return outp.StartsWith("-\t-");
        }

        // ---- stage / commit ----
        public Task<GitResult> StageAllAsync(string repoPath) => RunAsync(repoPath, "add -A");

        /// <summary>Commit staged changes. The message goes through a temp file (-F) to dodge all
        /// command-line quoting hazards (multi-line, quotes, locale).</summary>
        public async Task<GitResult> CommitAsync(string repoPath, string message)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "vortex_commit_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(tmp, message ?? "");
                return await RunAsync(repoPath, "commit -F " + Q(tmp));
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>Stage everything then commit — the common one-click flow.</summary>
        public async Task<GitResult> StageAllAndCommitAsync(string repoPath, string message)
        {
            var staged = await StageAllAsync(repoPath);
            if (!staged.Success) return staged;
            return await CommitAsync(repoPath, message);
        }

        // ---- remote ----
        public Task<GitResult> PushAsync(string repoPath) => RunAsync(repoPath, "push");
        public Task<GitResult> PullAsync(string repoPath) => RunAsync(repoPath, "pull --rebase");
        public Task<GitResult> FetchAsync(string repoPath) => RunAsync(repoPath, "fetch --all");
        public async Task<bool> HasRemoteAsync(string repoPath)
        {
            var r = await RunAsync(repoPath, "remote");
            return r.Success && !string.IsNullOrWhiteSpace(r.StdOut);
        }
        public Task<GitResult> SetRemoteAsync(string repoPath, string url)
            => RunAsync(repoPath, "remote add origin " + Q(url));

        /// <summary>Current origin URL ("" if none).</summary>
        public async Task<string> GetRemoteUrlAsync(string repoPath)
        {
            var r = await RunAsync(repoPath, "remote get-url origin");
            return r.Success ? r.StdOut.Trim() : "";
        }

        /// <summary>Set origin's URL — adds origin if missing, updates it otherwise. Empty url removes it.</summary>
        public async Task<GitResult> SetRemoteUrlAsync(string repoPath, string url)
        {
            bool has = await HasRemoteAsync(repoPath);
            if (string.IsNullOrWhiteSpace(url))
                return has ? await RunAsync(repoPath, "remote remove origin") : new GitResult { ExitCode = 0 };
            return has ? await RunAsync(repoPath, "remote set-url origin " + Q(url))
                       : await RunAsync(repoPath, "remote add origin " + Q(url));
        }

        // ---- local config (per-repo identity etc.) ----
        public async Task<string> GetConfigAsync(string repoPath, string key)
        {
            var r = await RunAsync(repoPath, "config --get " + Q(key));
            return r.Success ? r.StdOut.Trim() : "";
        }

        public Task<GitResult> SetConfigAsync(string repoPath, string key, string value)
            => RunAsync(repoPath, "config " + Q(key) + " " + Q(value));

        // ---- branches ----
        public async Task<string> CurrentBranchAsync(string repoPath)
        {
            // --show-current reports the branch name even on an unborn branch (fresh repo, no commit yet),
            // where rev-parse HEAD would fail.
            var r = await RunAsync(repoPath, "branch --show-current");
            return r.Success ? r.StdOut.Trim() : "";
        }

        public async Task<IReadOnlyList<string>> ListBranchesAsync(string repoPath)
        {
            var r = await RunAsync(repoPath, "branch --format=%(refname:short)");
            return SplitLines(r.StdOut);
        }

        public Task<GitResult> CheckoutAsync(string repoPath, string branch) => RunAsync(repoPath, "checkout " + Q(branch));
        public Task<GitResult> CreateBranchAsync(string repoPath, string name) => RunAsync(repoPath, "checkout -b " + Q(name));
        public Task<GitResult> RenameBranchAsync(string repoPath, string oldName, string newName)
            => RunAsync(repoPath, "branch -m " + Q(oldName) + " " + Q(newName));

        // ---- tags ----
        public async Task<IReadOnlyList<string>> ListTagsAsync(string repoPath)
        {
            var r = await RunAsync(repoPath, "tag --list --sort=-creatordate");
            return SplitLines(r.StdOut);
        }

        public Task<GitResult> CreateTagAsync(string repoPath, string name, string message)
            => RunAsync(repoPath, "tag -a " + Q(name) + " -m " + Q(message ?? name));

        // ---- helpers ----
        private static List<string> SplitLines(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var line in s.Replace("\r", "").Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
            return list;
        }

        // ---- embedded project files ----
        private const string GitIgnoreContent =
@"# Vortex Engine project — build output & editor scratch
obj/
bin/
.vs/
x64/
*.user
*.suo
*.tmp

# OS
Thumbs.db
Desktop.ini
.DS_Store
";

        private const string GitAttributesContent =
@"# Git LFS — store large binary assets out of the normal git history
*.png  filter=lfs diff=lfs merge=lfs -text
*.jpg  filter=lfs diff=lfs merge=lfs -text
*.jpeg filter=lfs diff=lfs merge=lfs -text
*.tga  filter=lfs diff=lfs merge=lfs -text
*.dds  filter=lfs diff=lfs merge=lfs -text
*.psd  filter=lfs diff=lfs merge=lfs -text
*.bmp  filter=lfs diff=lfs merge=lfs -text
*.fbx  filter=lfs diff=lfs merge=lfs -text
*.obj  filter=lfs diff=lfs merge=lfs -text
*.gltf filter=lfs diff=lfs merge=lfs -text
*.glb  filter=lfs diff=lfs merge=lfs -text
*.wav  filter=lfs diff=lfs merge=lfs -text
*.mp3  filter=lfs diff=lfs merge=lfs -text
*.ogg  filter=lfs diff=lfs merge=lfs -text
*.ttf  filter=lfs diff=lfs merge=lfs -text
*.otf  filter=lfs diff=lfs merge=lfs -text
*.vmesh filter=lfs diff=lfs merge=lfs -text
";
    }
}
