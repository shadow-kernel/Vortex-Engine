using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Core.Data;
using Editor.Core.Services.Git;

namespace Editor.Editors.WorldEditor.Components.Git
{
    /// <summary>
    /// Source-control panel for the current project. Drives <see cref="GitService"/> (the git CLI):
    /// branch switch/create/rename, see changes with a LIVE diff/preview, commit, push/pull/fetch, tags.
    /// </summary>
    public partial class GitWindow : Window
    {
        private readonly string _repo;
        private bool _busy;

        // diff line styling
        private static readonly Brush AddBack = Frozen("#142A1C"), AddFore = Frozen("#7CE0A3");
        private static readonly Brush DelBack = Frozen("#2A1618"), DelFore = Frozen("#E58A8A");
        private static readonly Brush HunkFore = Frozen("#9C8CFF"), MetaFore = Frozen("#73737A"), CtxFore = Frozen("#C8C8CE");
        private static readonly Brush Transparent = Brushes.Transparent;

        public GitWindow()
        {
            InitializeComponent();
            var project = ProjectData.Current;
            _repo = project != null ? project.Path : null;
            RepoLabel.Text = project != null ? "— " + project.Name : "— (no project)";
            Loaded += async (s, e) => await InitAsync();
        }

        private async Task InitAsync()
        {
            if (string.IsNullOrEmpty(_repo)) { SetStatus("No project is open."); return; }
            SetBusy(true, "Loading…");
            await GitService.Instance.EnsureRepoAsync(_repo);
            await RefreshAll();
            SetBusy(false, HistoryList.Items.Count == 0
                ? "New repo — make your first commit to start the history."
                : "Ready");
        }

        // ---- refresh ----
        private async Task RefreshAll()
        {
            await RefreshBranches();
            await RefreshChanges();
            await RefreshTags();
            await RefreshHistory();
            await RefreshStash();
        }

        private const int HistPageSize = 200;
        private int _histLoaded = 0;
        private readonly List<GitGraphCommit> _histAll = new List<GitGraphCommit>();

        private async Task RefreshHistory()
        {
            _histAll.Clear();
            var commits = await GitService.Instance.LogGraphAsync(_repo, HistPageSize, 0);
            _histAll.AddRange(commits);
            _histLoaded = _histAll.Count;
            BindHistory(commits.Count);
        }

        private void BindHistory(int lastBatch)
        {
            BuildLanes(_histAll);
            HistoryList.ItemsSource = null;
            HistoryList.ItemsSource = _histAll;
            LoadMoreHistoryBtn.Visibility = lastBatch >= HistPageSize ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void LoadMoreHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, "Loading more history…");
            var more = await GitService.Instance.LogGraphAsync(_repo, HistPageSize, _histLoaded);
            _histAll.AddRange(more);
            _histLoaded += more.Count;
            BindHistory(more.Count);
            SetBusy(false, "Ready");
        }

        // IntelliJ-style lane assignment: one top-to-bottom pass over the (date-ordered) commits.
        private static readonly int LanePalette = 5;
        private void BuildLanes(List<GitGraphCommit> commits)
        {
            var active = new List<string>(); // each lane holds the hash it's waiting to draw, or null
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
                    if (incoming[i] == c.Hash) c.TopLines.Add(new GLine(i, dot, i));   // merge up into the dot
                    else c.TopLines.Add(new GLine(i, i, i));                            // passing (top half)
                }
                for (int i = 0; i < incoming.Count; i++)
                {
                    if (incoming[i] == null || incoming[i] == c.Hash) continue;
                    c.BottomLines.Add(new GLine(i, i, i));                              // passing (bottom half)
                }
                if (c.Parents.Length >= 1) c.BottomLines.Add(new GLine(dot, dot, dot)); // first parent continues
                foreach (var ml in mergeLanes) c.BottomLines.Add(new GLine(dot, ml, ml)); // merge diagonal

                int rowMax = dot;
                foreach (var l in c.TopLines) rowMax = Math.Max(rowMax, Math.Max(l.From, l.To));
                foreach (var l in c.BottomLines) rowMax = Math.Max(rowMax, Math.Max(l.From, l.To));
                maxLane = Math.Max(maxLane, rowMax);
            }
            int width = Math.Min(maxLane, 9) + 1; // cap the graph column width
            foreach (var c in commits) c.Width = width;
        }

        private async Task RefreshStash()
        {
            var stashes = await GitService.Instance.StashListAsync(_repo);
            StashList.Items.Clear();
            foreach (var s in stashes) StashList.Items.Add(s);
        }

        // ---- history operations ----
        private async Task RunHist(Func<GitGraphCommit, Task<GitResult>> op, string okPrefix, bool confirm, string confirmText)
        {
            if (_busy) return;
            var c = HistoryList.SelectedItem as GitGraphCommit;
            if (c == null) { SetStatus("Select a commit in the history first."); return; }
            if (confirm && MessageBox.Show(confirmText + "\n\n" + c.Hash + "  " + c.Subject, "Git",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true, "Working…");
            var r = await op(c);
            await RefreshAll();
            SetBusy(false, r.Success ? okPrefix + c.Hash : "Failed: " + FirstLine(r.Message));
        }

        private async void HistCheckout_Click(object s, RoutedEventArgs e)
            => await RunHist(c => GitService.Instance.CheckoutCommitAsync(_repo, c.Hash), "Checked out ", true, "Checkout this commit (detached HEAD)?");
        private async void HistRevert_Click(object s, RoutedEventArgs e)
            => await RunHist(c => GitService.Instance.RevertAsync(_repo, c.Hash), "Reverted ", true, "Create a commit that undoes this commit?");
        private async void HistResetSoft_Click(object s, RoutedEventArgs e)
            => await RunHist(c => GitService.Instance.ResetAsync(_repo, c.Hash, "soft"), "Reset (soft) to ", true, "Reset branch to here and put later changes back into staged Changes?");
        private async void HistResetMixed_Click(object s, RoutedEventArgs e)
            => await RunHist(c => GitService.Instance.ResetAsync(_repo, c.Hash, "mixed"), "Reset (mixed) to ", true, "Reset branch to here and keep later changes unstaged?");
        private async void HistResetHard_Click(object s, RoutedEventArgs e)
            => await RunHist(c => GitService.Instance.ResetAsync(_repo, c.Hash, "hard"), "Reset (hard) to ", true, "DISCARD all commits and changes after this one? This cannot be undone.");
        private void HistCopyHash_Click(object s, RoutedEventArgs e)
        {
            var c = HistoryList.SelectedItem as GitGraphCommit;
            if (c != null) { try { Clipboard.SetText(c.Hash); SetStatus("Copied " + c.Hash); } catch { } }
        }

        // ---- stash (shelve) ----
        private async void StashSave_Click(object s, RoutedEventArgs e)
        {
            if (_busy) return;
            var msg = Prompt("Stash (shelve) current changes", "description", "WIP");
            if (msg == null) return;
            SetBusy(true, "Stashing…");
            var r = await GitService.Instance.StashSaveAsync(_repo, msg);
            await RefreshChanges(); await RefreshStash();
            SetBusy(false, r.Success ? "Changes stashed." : "Stash failed: " + FirstLine(r.Message));
        }
        private async Task RunStash(Func<int, Task<GitResult>> op, string okMsg)
        {
            if (_busy) return;
            var st = StashList.SelectedItem as GitStash;
            if (st == null) { SetStatus("Select a stash first."); return; }
            SetBusy(true, "Working…");
            var r = await op(st.Index);
            await RefreshChanges(); await RefreshStash();
            SetBusy(false, r.Success ? okMsg : "Failed: " + FirstLine(r.Message));
        }
        private async void StashApply_Click(object s, RoutedEventArgs e) => await RunStash(i => GitService.Instance.StashApplyAsync(_repo, i), "Stash applied.");
        private async void StashPop_Click(object s, RoutedEventArgs e) => await RunStash(i => GitService.Instance.StashPopAsync(_repo, i), "Stash popped.");
        private async void StashDrop_Click(object s, RoutedEventArgs e) => await RunStash(i => GitService.Instance.StashDropAsync(_repo, i), "Stash dropped.");

        private async Task RefreshBranches()
        {
            var cur = await GitService.Instance.CurrentBranchAsync(_repo);
            var branches = new List<string>(await GitService.Instance.ListBranchesAsync(_repo));
            if (!string.IsNullOrEmpty(cur) && !branches.Contains(cur)) branches.Insert(0, cur);
            BranchCombo.ItemsSource = branches;
            BranchCombo.SelectedItem = cur;
        }

        private async Task RefreshChanges()
        {
            var selectedPath = (ChangesList.SelectedItem as GitFileChange)?.Path;
            var changes = await GitService.Instance.StatusAsync(_repo);
            ChangesList.Items.Clear();
            foreach (var c in changes) ChangesList.Items.Add(c);
            if (changes.Count == 0)
            {
                ClearDiff();
                DiffNote.Text = "Working tree clean — no changes.";
                DiffNote.Visibility = Visibility.Visible;
                DiffTitle.Text = "No changes";
                DiffStat.Text = "";
            }
            else if (selectedPath != null)
            {
                foreach (var c in changes)
                    if (string.Equals(c.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) { ChangesList.SelectedItem = c; break; }
            }
        }

        private async Task RefreshTags()
        {
            var tags = await GitService.Instance.ListTagsAsync(_repo);
            TagsList.Items.Clear();
            foreach (var t in tags) TagsList.Items.Add(t);
        }

        // ---- diff / preview ----
        private async void ChangesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var change = ChangesList.SelectedItem as GitFileChange;
            if (change == null) return;
            await ShowDiff(change);
        }

        private async Task ShowDiff(GitFileChange change)
        {
            DiffTitle.Text = change.Path;
            DiffStat.Text = "";
            string abs = null;
            try { if (!string.IsNullOrEmpty(_repo)) abs = Path.Combine(_repo, change.Path.Replace('/', Path.DirectorySeparatorChar)); } catch { }

            // Image preview
            if (IsImage(change.Path) && abs != null && File.Exists(abs))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(abs);
                    bmp.EndInit();
                    PreviewImage.Source = bmp;
                    ShowPane(preview: true);
                    DiffStat.Text = "image preview";
                    return;
                }
                catch { /* fall through to text/binary */ }
            }

            SetBusy(true, "Loading diff…");
            var diff = await GitService.Instance.DiffAsync(_repo, change.Path);
            SetBusy(false, "Ready");

            if (string.IsNullOrWhiteSpace(diff) || diff.Contains("Binary files"))
            {
                bool binary = diff.Contains("Binary files") || await GitService.Instance.IsBinaryAsync(_repo, change.Path);
                ClearDiff();
                DiffNote.Text = binary
                    ? "Binary file — changed (no text diff)."
                    : "No textual differences to show.";
                DiffNote.Visibility = Visibility.Visible;
                ShowPane(note: true);
                return;
            }

            RenderDiffText(diff);
        }

        /// <summary>Parse a unified diff (file diff or `git show`) into colored lines + show it.</summary>
        private void RenderDiffText(string diff)
        {
            int adds = 0, dels = 0;
            var lines = new List<DiffLine>();
            foreach (var raw in (diff ?? "").Replace("\r", "").Split('\n'))
            {
                var dl = new DiffLine { Text = raw, Back = Transparent, Fore = CtxFore };
                if (raw.StartsWith("@@")) { dl.Fore = HunkFore; }
                else if (raw.StartsWith("commit ") || raw.StartsWith("Author:") || raw.StartsWith("Date:") ||
                         raw.StartsWith("Merge:")) { dl.Fore = HunkFore; }
                else if (raw.StartsWith("+++") || raw.StartsWith("---") || raw.StartsWith("diff ") ||
                         raw.StartsWith("index ") || raw.StartsWith("new file") || raw.StartsWith("deleted file") ||
                         raw.StartsWith("similarity") || raw.StartsWith("rename ")) { dl.Fore = MetaFore; }
                else if (raw.StartsWith("+")) { dl.Back = AddBack; dl.Fore = AddFore; adds++; }
                else if (raw.StartsWith("-")) { dl.Back = DelBack; dl.Fore = DelFore; dels++; }
                lines.Add(dl);
            }
            DiffList.ItemsSource = lines;
            DiffStat.Text = "+" + adds + "  −" + dels;
            ShowPane(diff: true);
        }

        // ---- history: clicking a commit shows what it changed ----
        private async void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var c = HistoryList.SelectedItem as GitGraphCommit;
            if (c == null) return;
            DiffTitle.Text = c.Hash + "  " + c.Subject;
            DiffStat.Text = "";
            SetBusy(true, "Loading commit…");
            var diff = await GitService.Instance.ShowCommitAsync(_repo, c.Hash);
            SetBusy(false, "Ready");
            if (string.IsNullOrWhiteSpace(diff)) { ClearDiff(); DiffNote.Text = "(no diff)"; ShowPane(note: true); return; }
            RenderDiffText(diff);
        }

        private void HistoryList_RightDown(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && !(dep is ListBoxItem)) dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
            if (dep is ListBoxItem lbi) { lbi.IsSelected = true; HistoryList.SelectedItem = lbi.DataContext; }
        }

        // ---- let the wheel scroll the whole left column even over a list ----
        private void InnerList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (LeftScroll == null) return;
            e.Handled = true;
            LeftScroll.ScrollToVerticalOffset(LeftScroll.VerticalOffset - e.Delta);
        }

        private void ShowPane(bool diff = false, bool preview = false, bool note = false)
        {
            DiffList.Visibility = diff ? Visibility.Visible : Visibility.Collapsed;
            PreviewHost.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
            DiffNote.Visibility = note ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearDiff()
        {
            DiffList.ItemsSource = null;
            PreviewImage.Source = null;
        }

        private static bool IsImage(string path)
        {
            var e = (Path.GetExtension(path ?? "") ?? "").ToLowerInvariant();
            return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".gif";
        }

        // ---- branch actions ----
        private async void Switch_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var b = BranchCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(b)) { SetStatus("Pick a branch first."); return; }
            SetBusy(true, "Switching to " + b + "…");
            var r = await GitService.Instance.CheckoutAsync(_repo, b);
            await RefreshAll();
            SetBusy(false, r.Success ? "On branch " + b : "Switch failed: " + FirstLine(r.Message));
        }

        private async void NewBranch_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var name = Prompt("New branch", "branch name, e.g. feature/zone");
            if (string.IsNullOrWhiteSpace(name)) return;
            SetBusy(true, "Creating branch…");
            var r = await GitService.Instance.CreateBranchAsync(_repo, name.Trim());
            await RefreshAll();
            SetBusy(false, r.Success ? "Created + switched to " + name : "Failed: " + FirstLine(r.Message));
        }

        private async void RenameBranch_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var cur = await GitService.Instance.CurrentBranchAsync(_repo);
            var name = Prompt("Rename current branch", "new name", cur);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == cur) return;
            SetBusy(true, "Renaming…");
            var r = await GitService.Instance.RenameBranchAsync(_repo, cur, name.Trim());
            await RefreshBranches();
            SetBusy(false, r.Success ? "Renamed to " + name : "Failed: " + FirstLine(r.Message));
        }

        // ---- changes / commit ----
        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, "Refreshing…");
            await RefreshAll();
            SetBusy(false, "Ready");
        }

        private async void Commit_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var msg = CommitBox.Text != null ? CommitBox.Text.Trim() : "";
            if (string.IsNullOrEmpty(msg)) { SetStatus("Enter a commit message first."); CommitBox.Focus(); return; }
            SetBusy(true, "Committing…");
            var r = await GitService.Instance.StageAllAndCommitAsync(_repo, msg);
            if (r.Success) CommitBox.Clear();
            await RefreshChanges();
            await RefreshHistory();
            SetBusy(false, r.Success ? "Committed." : "Commit failed: " + FirstLine(r.Message));
        }

        // ---- remote ----
        private async void Push_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, "Pushing…");
            var r = await GitService.Instance.PushAsync(_repo);
            SetBusy(false, r.Success ? "Pushed." : "Push failed: " + FirstLine(r.Message));
        }

        private async void Pull_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, "Pulling…");
            var r = await GitService.Instance.PullAsync(_repo);
            await RefreshAll();
            SetBusy(false, r.Success ? "Pulled." : "Pull failed: " + FirstLine(r.Message));
        }

        private async void Fetch_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, "Fetching…");
            var r = await GitService.Instance.FetchAsync(_repo);
            SetBusy(false, r.Success ? "Fetched." : "Fetch failed: " + FirstLine(r.Message));
        }

        private async void SetRemote_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var url = Prompt("Set remote 'origin'", "https://github.com/you/repo.git");
            if (string.IsNullOrWhiteSpace(url)) return;
            SetBusy(true, "Setting remote…");
            var r = await GitService.Instance.SetRemoteAsync(_repo, url.Trim());
            SetBusy(false, r.Success ? "Remote 'origin' set." : "Failed: " + FirstLine(r.Message));
        }

        // ---- tags ----
        private async void NewTag_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var name = Prompt("New tag", "tag name, e.g. v1.0");
            if (string.IsNullOrWhiteSpace(name)) return;
            var msg = Prompt("Tag message", "message", name.Trim());
            SetBusy(true, "Creating tag…");
            var r = await GitService.Instance.CreateTagAsync(_repo, name.Trim(), msg);
            await RefreshTags();
            SetBusy(false, r.Success ? "Tagged " + name : "Failed: " + FirstLine(r.Message));
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ---- helpers ----
        private void SetStatus(string s) { if (s != null) StatusText.Text = s; }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
            SetStatus(status);
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            return (i >= 0 ? s.Substring(0, i) : s).Trim();
        }

        private static Brush Frozen(string hex)
        {
            var b = (Brush)new BrushConverter().ConvertFromString(hex);
            if (b.CanFreeze) b.Freeze();
            return b;
        }

        private sealed class DiffLine
        {
            public string Text { get; set; }
            public Brush Back { get; set; }
            public Brush Fore { get; set; }
        }

        /// <summary>Minimal modal text prompt in the Vortex dark style. Returns null on cancel.</summary>
        private string Prompt(string title, string watermark, string initial = "")
        {
            string result = null;
            var dlg = new Window
            {
                Width = 400, Height = 168, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ShowInTaskbar = false,
                Background = Br("#1A1A1C")
            };
            var outer = new Border { BorderBrush = Br("#3A3A42"), BorderThickness = new Thickness(1), Padding = new Thickness(18) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, Foreground = Br("#F5F5F7"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            var tb = new TextBox
            {
                Text = initial ?? "", Background = Br("#202023"), Foreground = Br("#F5F5F7"),
                BorderBrush = Br("#3A3A42"), Padding = new Thickness(8, 6, 8, 6), FontSize = 13, Height = 32,
                CaretBrush = Br("#F5F5F7"), ToolTip = watermark
            };
            sp.Children.Add(tb);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = MakeButton("Cancel", "#26262B", "#E9E9ED");
            var ok = MakeButton("OK", "#6C5CE7", "#FFFFFF");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (s, e) => { dlg.DialogResult = false; };
            ok.Click += (s, e) => { result = tb.Text; dlg.DialogResult = true; };
            row.Children.Add(cancel); row.Children.Add(ok);
            sp.Children.Add(row);
            outer.Child = sp; dlg.Content = outer;
            tb.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) { result = tb.Text; dlg.DialogResult = true; } else if (e.Key == Key.Escape) { dlg.DialogResult = false; } };
            var shown = dlg.ShowDialog();
            return shown == true ? result : null;
        }

        private static Button MakeButton(string text, string bg, string fg)
        {
            return new Button
            {
                Content = text, Width = 84, Height = 30, Background = Br(bg), Foreground = Br(fg),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12.5
            };
        }

        private static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
    }

    /// <summary>Draws one commit row of the branch graph: lane dots + the lines passing through / branching.</summary>
    public sealed class CommitGraphCell : FrameworkElement
    {
        private static readonly Pen[] _pens;
        private static readonly Brush[] _brushes;
        private static readonly Pen _dotPen;
        private const double LANE_W = 16, R = 4.5;

        static CommitGraphCell()
        {
            string[] hex = { "#6C5CE7", "#7CE0A3", "#E58A8A", "#E0C57C", "#6CB8E7" };
            _pens = new Pen[hex.Length];
            _brushes = new Brush[hex.Length];
            for (int i = 0; i < hex.Length; i++)
            {
                var b = (Brush)new BrushConverter().ConvertFromString(hex[i]); b.Freeze();
                _brushes[i] = b;
                var p = new Pen(b, 2.0); p.Freeze(); _pens[i] = p;
            }
            _dotPen = new Pen((Brush)new BrushConverter().ConvertFromString("#161618"), 2.0); _dotPen.Freeze();
        }

        public CommitGraphCell()
        {
            DataContextChanged += (s, e) => { InvalidateMeasure(); InvalidateVisual(); };
        }

        private static double X(int lane) { return 8 + lane * LANE_W + LANE_W / 2; }

        protected override Size MeasureOverride(Size availableSize)
        {
            var c = DataContext as GitGraphCommit;
            int w = c != null ? c.Width : 1;
            return new Size(8 + w * LANE_W + 6, 0); // height comes from the row
        }

        protected override void OnRender(DrawingContext dc)
        {
            var c = DataContext as GitGraphCommit;
            if (c == null) return;
            double h = ActualHeight > 0 ? ActualHeight : 38;
            double yTop = 0, yMid = h / 2, yBot = h;
            foreach (var l in c.TopLines)
                dc.DrawLine(_pens[Math.Abs(l.Color) % _pens.Length], new Point(X(l.From), yTop), new Point(X(l.To), yMid));
            foreach (var l in c.BottomLines)
                dc.DrawLine(_pens[Math.Abs(l.Color) % _pens.Length], new Point(X(l.From), yMid), new Point(X(l.To), yBot));
            dc.DrawEllipse(_brushes[Math.Abs(c.Lane) % _brushes.Length], _dotPen, new Point(X(c.Lane), yMid), R, R);
        }
    }
}
