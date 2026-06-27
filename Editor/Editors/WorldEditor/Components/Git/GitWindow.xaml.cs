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
        }

        private async Task RefreshHistory()
        {
            var commits = await GitService.Instance.LogAsync(_repo);
            HistoryList.Items.Clear();
            foreach (var c in commits) HistoryList.Items.Add(c);
        }

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

            int adds = 0, dels = 0;
            var lines = new List<DiffLine>();
            foreach (var raw in diff.Replace("\r", "").Split('\n'))
            {
                var dl = new DiffLine { Text = raw, Back = Transparent, Fore = CtxFore };
                if (raw.StartsWith("@@")) { dl.Fore = HunkFore; }
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
}
