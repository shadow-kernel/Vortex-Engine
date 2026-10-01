using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Editor.Core.Data;
using Editor.Core.Services.Git;
using VortexEditor.Controls;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Source control for the open project (port of the WPF GitWindow, driving the git CLI through GitService):
    /// branch switch / new / rename, changes with a live diff or image preview, commit, push / pull / fetch / remote,
    /// tags, the commit history as a branch graph (click = show the commit; right-click = checkout, revert, reset
    /// soft / mixed / hard, copy hash; paged "load more"), and the shelf (stash save / apply / pop / drop).
    /// </summary>
    public sealed class GitWindow : Window
    {
        private static GitWindow _open;
        /// <summary>Open this window (owned by the main window) — one instance; a second call brings it to the front.</summary>
        public static void Open()
        {
            if (_open != null) { _open.Activate(); return; }
            _open = new GitWindow();
            _open.Closed += (s, e) => _open = null;
            EditorWindows.Show(_open);
        }
        public static GitWindow Current => _open;

        private sealed class DiffLine { public string Text { get; set; } public IBrush Back { get; set; } public IBrush Fore { get; set; } }

        private static readonly IBrush AddBack = Br("#142A1C"), AddFore = Br("#7CE0A3"), DelBack = Br("#2A1618"), DelFore = Br("#E58A8A");
        private static readonly IBrush HunkFore = Br("#9C8CFF"), MetaFore = Br("#8A8A93");
        private const int HistPageSize = 200;

        private readonly string _repo = ProjectData.Current?.Path;
        private readonly GitService _git = GitService.Instance;
        private readonly ComboBox _branches = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ListBox _changes = new ListBox { Height = 190 }, _tags = new ListBox { Height = 84 }, _history = new ListBox { Height = 240 }, _stash = new ListBox { Height = 84 };
        private readonly ListBox _diff = new ListBox();
        private readonly TextBox _commit = new TextBox { Watermark = "Commit message", AcceptsReturn = true, Height = 64, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _status = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _diffTitle = new TextBlock { Text = "Select a change to see the diff", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _diffStat = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        private readonly TextBlock _diffNote = new TextBlock { Classes = { "tertiary" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private readonly Image _preview = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(20) };
        private readonly Border _previewHost;
        private readonly Button _loadMore;
        private readonly List<GitGraphCommit> _histAll = new List<GitGraphCommit>();
        private int _histLoaded;
        private bool _busy;

        /// <summary>Last status line (smoke checks).</summary>
        public string StatusText => _status.Text;
        public int ChangeCount => _changes.ItemCount;
        public int CommitCount => _histAll.Count;
        public bool IsBusy => _busy;

        public GitWindow()
        {
            Title = "Source Control"; Width = 1100; Height = 720; MinWidth = 820; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

            // ---- header
            var header = new DockPanel { Margin = new Thickness(14, 10, 14, 8) };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            titleRow.Children.Add(new VxIcon { Icon = "Git", Foreground = Res("VxAccentBrush") });
            titleRow.Children.Add(new TextBlock { Text = "Source Control", FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            titleRow.Children.Add(new TextBlock { Text = ProjectData.Current != null ? "— " + ProjectData.Current.Name : "— (no project)", Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(titleRow);

            // ---- left column (sections)
            var left = new StackPanel { Spacing = 6, Margin = new Thickness(14, 4, 14, 14) };
            left.Children.Add(Section("BRANCH"));
            left.Children.Add(_branches);
            left.Children.Add(Row(Btn("Switch", SwitchBranch), Btn("New…", NewBranch), Btn("Rename…", RenameBranch)));

            left.Children.Add(SectionWith("CHANGES", Btn("Refresh", async () => { SetBusy(true, "Refreshing…"); await RefreshAll(); SetBusy(false, "Ready"); })));
            _changes.ItemTemplate = new FuncDataTemplate<GitFileChange>((c, _) =>
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                sp.Children.Add(new TextBlock { Text = c?.Badge, Width = 18, FontWeight = FontWeight.Bold, Foreground = HunkFore });
                sp.Children.Add(new TextBlock { Text = c?.Path, TextTrimming = TextTrimming.CharacterEllipsis });
                return sp;
            });
            _changes.SelectionChanged += async (s, e) => { if (_changes.SelectedItem is GitFileChange c) await ShowDiff(c); };
            left.Children.Add(Framed(_changes));
            left.Children.Add(_commit);
            var commitBtn = Btn("Commit all changes", Commit, accent: true); commitBtn.HorizontalAlignment = HorizontalAlignment.Stretch; commitBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
            left.Children.Add(commitBtn);

            left.Children.Add(Section("REMOTE"));
            left.Children.Add(Row(Btn("Push", Push), Btn("Pull", Pull), Btn("Fetch", Fetch, accent: true), Btn("Set remote…", SetRemote)));

            left.Children.Add(SectionWith("TAGS", Btn("New tag…", NewTag)));
            left.Children.Add(Framed(_tags));

            left.Children.Add(Section("HISTORY"));
            _history.ItemTemplate = new FuncDataTemplate<GitGraphCommit>((c, _) =>
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), MinHeight = 38 };
                g.Children.Add(new CommitGraphCell { Commit = c });
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = c?.Display, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = c?.Meta, Classes = { "small", "tertiary" }, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(text, 1); g.Children.Add(text);
                return g;
            });
            _history.SelectionChanged += async (s, e) => { if (_history.SelectedItem is GitGraphCommit c) await ShowCommit(c); };
            _history.ContextMenu = HistoryMenu();
            left.Children.Add(Framed(_history));
            _loadMore = Btn("Load more history", LoadMoreHistory); _loadMore.HorizontalAlignment = HorizontalAlignment.Center; _loadMore.IsVisible = false;
            left.Children.Add(_loadMore);

            left.Children.Add(SectionWith("SHELF / STASH", Btn("Stash changes…", StashSave)));
            _stash.ItemTemplate = new FuncDataTemplate<GitStash>((st, _) => new TextBlock { Text = st?.Display, TextTrimming = TextTrimming.CharacterEllipsis });
            _stash.ContextMenu = StashMenu();
            left.Children.Add(Framed(_stash));
            var leftScroll = new ScrollViewer { Content = left, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

            // ---- right column (diff / preview)
            var diffHead = new DockPanel { Margin = new Thickness(14, 8, 14, 8) };
            DockPanel.SetDock(_diffStat, Dock.Right);
            diffHead.Children.Add(_diffStat); diffHead.Children.Add(_diffTitle);
            _diff.ItemTemplate = new FuncDataTemplate<DiffLine>((d, _) => new Border
            {
                Background = d?.Back ?? Brushes.Transparent,
                Padding = new Thickness(8, 0),
                Child = new TextBlock { Text = d?.Text, Foreground = d?.Fore, FontFamily = (FontFamily)Application.Current.FindResource("VxMono"), FontSize = 12, TextWrapping = TextWrapping.NoWrap }
            });
            _diff.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x)) { Setters = { new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 17.0) } });
            _previewHost = new Border { Background = Res("VxViewportBgBrush"), Child = _preview, IsVisible = false };
            var diffBody = new Grid();
            diffBody.Children.Add(_diff); diffBody.Children.Add(_previewHost); diffBody.Children.Add(_diffNote);
            var right = new DockPanel();
            DockPanel.SetDock(diffHead, Dock.Top);
            right.Children.Add(diffHead);
            right.Children.Add(new Border { Classes = { "hairline-top" }, Background = Res("VxFieldBrush"), Child = diffBody });

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("400,5,*") };
            grid.Children.Add(new Border { Classes = { "hairline-right" }, Child = leftScroll });
            var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(split, 1); grid.Children.Add(split);
            Grid.SetColumn(right, 2); grid.Children.Add(right);

            var statusBar = new Border { Classes = { "hairline-top" }, Padding = new Thickness(12, 0), Height = 26, Child = _status };
            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(header); root.Children.Add(statusBar);
            root.Children.Add(new Border { Classes = { "hairline-top" }, Child = grid });
            Content = root;
            KeyDown += (s, e) => { bool cmd = OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Meta) : e.KeyModifiers.HasFlag(KeyModifiers.Control); if (cmd && e.Key == Key.W) { Close(); e.Handled = true; } else if (cmd && e.Key == Key.Return) { _ = Commit(); e.Handled = true; } };
            Opened += async (s, e) => await Init();
        }

        // ------------------------------------------------------------------ UI helpers
        private static IBrush Br(string hex) => new SolidColorBrush(Color.Parse(hex));
        private static IBrush Res(string key) => (IBrush)Application.Current.FindResource(key);
        private static TextBlock Section(string text) => new TextBlock { Text = text, Classes = { "section" }, Margin = new Thickness(0, 10, 0, 2) };
        private static Control SectionWith(string text, Control right)
        {
            var d = new DockPanel { Margin = new Thickness(0, 10, 0, 2) };
            DockPanel.SetDock(right, Dock.Right); right.VerticalAlignment = VerticalAlignment.Center;
            d.Children.Add(right);
            d.Children.Add(new TextBlock { Text = text, Classes = { "section" }, Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
            return d;
        }
        private static Control Row(params Control[] items) { var w = new WrapPanel { Orientation = Orientation.Horizontal }; foreach (var i in items) { i.Margin = new Thickness(0, 0, 6, 6); w.Children.Add(i); } return w; }
        private static Border Framed(Control c) => new Border { Classes = { "card" }, Padding = new Thickness(2), Child = c };

        private Button Btn(string text, Func<Task> a, bool accent = false)
        {
            var b = new Button { Content = text }; if (accent) b.Classes.Add("accent");
            b.Click += async (s, e) => { try { await a(); } catch (Exception ex) { SetBusy(false, "Git: " + ex.Message); } };
            return b;
        }

        private ContextMenu HistoryMenu()
        {
            var m = new ContextMenu();
            MenuItem I(string h, Func<Task> a) { var mi = new MenuItem { Header = h }; mi.Click += async (s, e) => await a(); return mi; }
            m.Items.Add(I("Checkout this commit", () => RunHist(c => _git.CheckoutCommitAsync(_repo, c.Hash), "Checked out ", "Checkout this commit (detached HEAD)?")));
            m.Items.Add(I("Revert this commit", () => RunHist(c => _git.RevertAsync(_repo, c.Hash), "Reverted ", "Create a commit that undoes this commit?")));
            m.Items.Add(new Separator());
            m.Items.Add(I("Reset to here — keep changes staged (soft)", () => RunHist(c => _git.ResetAsync(_repo, c.Hash, "soft"), "Reset (soft) to ", "Reset the branch to here and put the later changes back into the staged changes?")));
            m.Items.Add(I("Reset to here — keep changes unstaged (mixed)", () => RunHist(c => _git.ResetAsync(_repo, c.Hash, "mixed"), "Reset (mixed) to ", "Reset the branch to here and keep the later changes unstaged?")));
            m.Items.Add(I("Reset to here — discard changes (hard)", () => RunHist(c => _git.ResetAsync(_repo, c.Hash, "hard"), "Reset (hard) to ", "DISCARD all commits and changes after this one? This cannot be undone.", destructive: true)));
            m.Items.Add(new Separator());
            m.Items.Add(I("Copy commit hash", async () => { if (_history.SelectedItem is GitGraphCommit c) { try { await Clipboard.SetTextAsync(c.Hash); SetStatus("Copied " + c.Hash); } catch { } } }));
            return m;
        }

        private ContextMenu StashMenu()
        {
            var m = new ContextMenu();
            MenuItem I(string h, Func<Task> a) { var mi = new MenuItem { Header = h }; mi.Click += async (s, e) => await a(); return mi; }
            m.Items.Add(I("Apply (keep the stash)", () => RunStash(i => _git.StashApplyAsync(_repo, i), "Stash applied.")));
            m.Items.Add(I("Pop (apply + remove)", () => RunStash(i => _git.StashPopAsync(_repo, i), "Stash popped.")));
            m.Items.Add(I("Drop (delete)", () => RunStash(i => _git.StashDropAsync(_repo, i), "Stash dropped.")));
            return m;
        }

        private void SetStatus(string s) { if (s != null) _status.Text = s; }
        private void SetBusy(bool busy, string status) { _busy = busy; Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default; SetStatus(status); }
        private static string FirstLine(string s) { if (string.IsNullOrEmpty(s)) return ""; int i = s.IndexOf('\n'); return (i >= 0 ? s.Substring(0, i) : s).Trim(); }
        private Task<string> Ask(string title, string message, string initial = "", string ok = "OK") => Dialogs.Prompt(title, message, initial, ok, this);

        // ------------------------------------------------------------------ load / refresh
        private async Task Init()
        {
            if (string.IsNullOrEmpty(_repo)) { SetStatus("No project is open."); return; }
            if (!await _git.IsAvailableAsync()) { SetStatus("git is not installed — install the Xcode Command Line Tools (xcode-select --install)."); return; }
            SetBusy(true, "Loading…");
            await _git.EnsureRepoAsync(_repo);
            if (!await _git.IsRepoAsync(_repo))
            {
                // e.g. a copied submodule whose ".git" file points to a folder that does not exist here
                bool gitFile = File.Exists(Path.Combine(_repo, ".git"));
                SetBusy(false, gitFile ? "Not a working git repository: the project's .git file points to a missing folder (a copied submodule?). Remove that .git file to start a fresh repository."
                                       : "Not a working git repository — git could not initialise one in " + _repo);
                ShowPane(note: true, noteText: _status.Text);
                return;
            }
            await RefreshAll();
            SetBusy(false, _histAll.Count == 0 ? "New repo — make your first commit to start the history." : "Ready");
        }

        public async Task RefreshAll()
        {
            if (string.IsNullOrEmpty(_repo)) return;
            await RefreshBranches();
            await RefreshChanges();
            await RefreshTags();
            await RefreshHistory();
            await RefreshStash();
        }

        private async Task RefreshBranches()
        {
            var cur = await _git.CurrentBranchAsync(_repo);
            var branches = new List<string>(await _git.ListBranchesAsync(_repo));
            if (!string.IsNullOrEmpty(cur) && !branches.Contains(cur)) branches.Insert(0, cur);
            _branches.ItemsSource = branches;
            _branches.SelectedItem = cur;
        }

        private async Task RefreshChanges()
        {
            var selected = (_changes.SelectedItem as GitFileChange)?.Path;
            var changes = await _git.StatusAsync(_repo);
            _changes.ItemsSource = changes;
            if (changes.Count == 0) { ClearDiff(); _diffTitle.Text = "No changes"; _diffStat.Text = ""; ShowPane(note: true, noteText: "Working tree clean — no changes."); }
            else if (selected != null) _changes.SelectedItem = changes.FirstOrDefault(c => string.Equals(c.Path, selected, StringComparison.OrdinalIgnoreCase));
        }

        private async Task RefreshTags() => _tags.ItemsSource = await _git.ListTagsAsync(_repo);
        private async Task RefreshStash() => _stash.ItemsSource = await _git.StashListAsync(_repo);

        private async Task RefreshHistory()
        {
            _histAll.Clear();
            var commits = await _git.LogGraphAsync(_repo, HistPageSize, 0);
            _histAll.AddRange(commits);
            _histLoaded = _histAll.Count;
            BindHistory(commits.Count);
        }

        private void BindHistory(int lastBatch)
        {
            BuildLanes(_histAll);
            _history.ItemsSource = null;
            _history.ItemsSource = _histAll.ToList();
            _loadMore.IsVisible = lastBatch >= HistPageSize;
        }

        private async Task LoadMoreHistory()
        {
            if (_busy) return;
            SetBusy(true, "Loading more history…");
            var more = await _git.LogGraphAsync(_repo, HistPageSize, _histLoaded);
            _histAll.AddRange(more);
            _histLoaded += more.Count;
            BindHistory(more.Count);
            SetBusy(false, "Ready");
        }

        /// <summary>IntelliJ-style lane assignment: one top-to-bottom pass over the (date-ordered) commits.</summary>
        private static void BuildLanes(List<GitGraphCommit> commits)
        {
            var active = new List<string>();
            int maxLane = 0;
            foreach (var c in commits)
            {
                int dot = active.IndexOf(c.Hash);
                if (dot < 0) { dot = active.IndexOf(null); if (dot < 0) { dot = active.Count; active.Add(null); } }
                c.Lane = dot;
                var incoming = new List<string>(active);
                for (int i = 0; i < active.Count; i++) if (active[i] == c.Hash) active[i] = null;
                var mergeLanes = new List<int>();
                if (c.Parents.Length >= 1)
                {
                    active[dot] = c.Parents[0];
                    for (int k = 1; k < c.Parents.Length; k++)
                    {
                        int pl = active.IndexOf(c.Parents[k]);
                        if (pl < 0) { pl = active.IndexOf(null); if (pl < 0) { pl = active.Count; active.Add(null); } active[pl] = c.Parents[k]; }
                        mergeLanes.Add(pl);
                    }
                }
                else active[dot] = null;
                c.TopLines.Clear(); c.BottomLines.Clear();
                for (int i = 0; i < incoming.Count; i++)
                {
                    if (incoming[i] == null) continue;
                    if (incoming[i] == c.Hash) c.TopLines.Add(new GLine(i, dot, i));
                    else c.TopLines.Add(new GLine(i, i, i));
                }
                for (int i = 0; i < incoming.Count; i++)
                {
                    if (incoming[i] == null || incoming[i] == c.Hash) continue;
                    c.BottomLines.Add(new GLine(i, i, i));
                }
                if (c.Parents.Length >= 1) c.BottomLines.Add(new GLine(dot, dot, dot));
                foreach (var ml in mergeLanes) c.BottomLines.Add(new GLine(dot, ml, ml));
                int rowMax = dot;
                foreach (var l in c.TopLines) rowMax = Math.Max(rowMax, Math.Max(l.From, l.To));
                foreach (var l in c.BottomLines) rowMax = Math.Max(rowMax, Math.Max(l.From, l.To));
                maxLane = Math.Max(maxLane, rowMax);
            }
            int width = Math.Min(maxLane, 9) + 1;
            foreach (var c in commits) c.Width = width;
        }

        // ------------------------------------------------------------------ diff / preview
        private async Task ShowDiff(GitFileChange change)
        {
            _diffTitle.Text = change.Path;
            _diffStat.Text = "";
            string abs = null;
            try { abs = Path.Combine(_repo, change.Path.Replace('/', Path.DirectorySeparatorChar)); } catch { }
            if (IsImage(change.Path) && abs != null && File.Exists(abs))
            {
                try { _preview.Source = new Bitmap(abs); ShowPane(preview: true); _diffStat.Text = "image preview"; return; } catch { }
            }
            SetBusy(true, "Loading diff…");
            var diff = await _git.DiffAsync(_repo, change.Path);
            SetBusy(false, "Ready");
            if (string.IsNullOrWhiteSpace(diff) || diff.Contains("Binary files"))
            {
                bool binary = (diff ?? "").Contains("Binary files") || await _git.IsBinaryAsync(_repo, change.Path);
                ClearDiff();
                ShowPane(note: true, noteText: binary ? "Binary file — changed (no text diff)." : "No textual differences to show.");
                return;
            }
            RenderDiff(diff);
        }

        private async Task ShowCommit(GitGraphCommit c)
        {
            _diffTitle.Text = c.Hash + "  " + c.Subject;
            _diffStat.Text = "";
            SetBusy(true, "Loading commit…");
            var diff = await _git.ShowCommitAsync(_repo, c.Hash);
            SetBusy(false, "Ready");
            if (string.IsNullOrWhiteSpace(diff)) { ClearDiff(); ShowPane(note: true, noteText: "(no diff)"); return; }
            RenderDiff(diff);
        }

        private void RenderDiff(string diff)
        {
            int adds = 0, dels = 0;
            var lines = new List<DiffLine>();
            foreach (var raw in (diff ?? "").Replace("\r", "").Split('\n'))
            {
                var dl = new DiffLine { Text = raw.Length == 0 ? " " : raw, Back = Brushes.Transparent, Fore = Res("VxTextBrush") };
                if (raw.StartsWith("@@") || raw.StartsWith("commit ") || raw.StartsWith("Author:") || raw.StartsWith("Date:") || raw.StartsWith("Merge:")) dl.Fore = HunkFore;
                else if (raw.StartsWith("+++") || raw.StartsWith("---") || raw.StartsWith("diff ") || raw.StartsWith("index ") || raw.StartsWith("new file") || raw.StartsWith("deleted file") || raw.StartsWith("similarity") || raw.StartsWith("rename ")) dl.Fore = MetaFore;
                else if (raw.StartsWith("+")) { dl.Back = AddBack; dl.Fore = AddFore; adds++; }
                else if (raw.StartsWith("-")) { dl.Back = DelBack; dl.Fore = DelFore; dels++; }
                lines.Add(dl);
            }
            _diff.ItemsSource = lines;
            _diffStat.Text = "+" + adds + "  −" + dels;
            ShowPane(diff: true);
        }

        private void ShowPane(bool diff = false, bool preview = false, bool note = false, string noteText = null)
        {
            _diff.IsVisible = diff; _previewHost.IsVisible = preview; _diffNote.IsVisible = note;
            if (noteText != null) _diffNote.Text = noteText;
        }

        private void ClearDiff() { _diff.ItemsSource = null; _preview.Source = null; }
        private static bool IsImage(string p) { var e = (Path.GetExtension(p ?? "") ?? "").ToLowerInvariant(); return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".gif"; }

        // ------------------------------------------------------------------ operations
        private async Task Run(Func<Task<GitResult>> op, string working, Func<GitResult, string> done, bool refreshAll = true)
        {
            if (_busy || string.IsNullOrEmpty(_repo)) return;
            SetBusy(true, working);
            GitResult r = null;
            try { r = await op(); }
            catch (Exception ex) { SetBusy(false, "Git: " + ex.Message); return; }
            if (refreshAll) await RefreshAll();
            SetBusy(false, done(r));
            if (r != null && r.Success) EditorCommands.Toast(FirstLine(done(r)));
        }

        private async Task SwitchBranch()
        {
            var b = _branches.SelectedItem as string;
            if (string.IsNullOrEmpty(b)) { SetStatus("Pick a branch first."); return; }
            await Run(() => _git.CheckoutAsync(_repo, b), "Switching to " + b + "…", r => r.Success ? "On branch " + b : "Switch failed: " + FirstLine(r.Message));
        }

        private async Task NewBranch()
        {
            var name = await Ask("New branch", "Branch name, e.g. feature/zone", "feature/", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            await Run(() => _git.CreateBranchAsync(_repo, name.Trim()), "Creating branch…", r => r.Success ? "Created + switched to " + name.Trim() : "Failed: " + FirstLine(r.Message));
        }

        private async Task RenameBranch()
        {
            var cur = await _git.CurrentBranchAsync(_repo);
            var name = await Ask("Rename current branch", "New name for \"" + cur + "\"", cur, "Rename");
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == cur) return;
            await Run(() => _git.RenameBranchAsync(_repo, cur, name.Trim()), "Renaming…", r => r.Success ? "Renamed to " + name.Trim() : "Failed: " + FirstLine(r.Message));
        }

        private async Task Commit()
        {
            var msg = (_commit.Text ?? "").Trim();
            if (msg.Length == 0) { SetStatus("Enter a commit message first."); _commit.Focus(); return; }
            await Run(() => _git.StageAllAndCommitAsync(_repo, msg), "Committing…", r => { if (r.Success) _commit.Text = ""; return r.Success ? "Committed." : "Commit failed: " + FirstLine(r.Message); });
        }

        private Task Push() => Run(() => _git.PushAsync(_repo), "Pushing…", r => r.Success ? "Pushed." : "Push failed: " + FirstLine(r.Message), refreshAll: false);
        private Task Pull() => Run(() => _git.PullAsync(_repo), "Pulling…", r => r.Success ? "Pulled." : "Pull failed: " + FirstLine(r.Message));
        private Task Fetch() => Run(() => _git.FetchAsync(_repo), "Fetching…", r => r.Success ? "Fetched." : "Fetch failed: " + FirstLine(r.Message), refreshAll: false);

        private async Task SetRemote()
        {
            string current = null;
            try { current = await _git.GetRemoteUrlAsync(_repo); } catch { }
            var url = await Ask("Set remote 'origin'", "Repository URL, e.g. https://github.com/you/repo.git", current ?? "", "Set");
            if (string.IsNullOrWhiteSpace(url)) return;
            await Run(() => _git.SetRemoteUrlAsync(_repo, url.Trim()), "Setting remote…", r => r.Success ? "Remote 'origin' set." : "Failed: " + FirstLine(r.Message), refreshAll: false);
        }

        private async Task NewTag()
        {
            var name = await Ask("New tag", "Tag name, e.g. v1.0", "v1.0", "Next");
            if (string.IsNullOrWhiteSpace(name)) return;
            var msg = await Ask("Tag message", "Message for " + name.Trim(), "Release " + name.Trim(), "Create");
            if (msg == null) return;
            await Run(() => _git.CreateTagAsync(_repo, name.Trim(), msg), "Creating tag…", r => r.Success ? "Tagged " + name.Trim() : "Failed: " + FirstLine(r.Message));
        }

        private async Task RunHist(Func<GitGraphCommit, Task<GitResult>> op, string okPrefix, string confirm, bool destructive = false)
        {
            if (!(_history.SelectedItem is GitGraphCommit c)) { SetStatus("Select a commit in the history first."); return; }
            if (!await Dialogs.Confirm(confirm, c.Hash + "  " + c.Subject, "Continue", "Cancel", destructive, this)) return;
            await Run(() => op(c), "Working…", r => r.Success ? okPrefix + c.Hash : "Failed: " + FirstLine(r.Message));
        }

        private async Task StashSave()
        {
            var msg = await Ask("Stash (shelve) current changes", "Description", "WIP", "Stash");
            if (msg == null) return;
            await Run(() => _git.StashSaveAsync(_repo, msg), "Stashing…", r => r.Success ? "Changes stashed." : "Stash failed: " + FirstLine(r.Message));
        }

        private async Task RunStash(Func<int, Task<GitResult>> op, string okMsg)
        {
            if (!(_stash.SelectedItem is GitStash st)) { SetStatus("Select a stash first."); return; }
            await Run(() => op(st.Index), "Working…", r => r.Success ? okMsg : "Failed: " + FirstLine(r.Message));
        }
    }

    /// <summary>One commit row of the branch graph: the lanes passing through, merges and the commit dot.</summary>
    public sealed class CommitGraphCell : Control
    {
        public static readonly StyledProperty<GitGraphCommit> CommitProperty = AvaloniaProperty.Register<CommitGraphCell, GitGraphCommit>(nameof(Commit));
        public GitGraphCommit Commit { get => GetValue(CommitProperty); set => SetValue(CommitProperty, value); }
        private static readonly string[] Palette = { "#6C5CE7", "#7CE0A3", "#E58A8A", "#E0C57C", "#6CB8E7" };
        private static readonly IPen[] Pens = Palette.Select(h => (IPen)new Pen(new SolidColorBrush(Color.Parse(h)), 2.0)).ToArray();
        private static readonly IBrush[] Fills = Palette.Select(h => (IBrush)new SolidColorBrush(Color.Parse(h))).ToArray();
        private const double LaneW = 16, R = 4.5;

        static CommitGraphCell() { AffectsMeasure<CommitGraphCell>(CommitProperty); AffectsRender<CommitGraphCell>(CommitProperty); }
        private static double X(int lane) => 8 + lane * LaneW + LaneW / 2;

        protected override Size MeasureOverride(Size availableSize) => new Size(8 + (Commit?.Width ?? 1) * LaneW + 6, 38);

        public override void Render(DrawingContext dc)
        {
            var c = Commit; if (c == null) return;
            double h = Bounds.Height > 0 ? Bounds.Height : 38, mid = h / 2;
            foreach (var l in c.TopLines) dc.DrawLine(Pens[Math.Abs(l.Color) % Pens.Length], new Point(X(l.From), 0), new Point(X(l.To), mid));
            foreach (var l in c.BottomLines) dc.DrawLine(Pens[Math.Abs(l.Color) % Pens.Length], new Point(X(l.From), mid), new Point(X(l.To), h));
            dc.DrawEllipse(Fills[Math.Abs(c.Lane) % Fills.Length], new Pen(new SolidColorBrush(Color.Parse("#161618")), 2), new Point(X(c.Lane), mid), R, R);
        }
    }
}
