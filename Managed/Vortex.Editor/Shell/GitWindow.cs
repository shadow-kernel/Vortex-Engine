using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Data;
using Editor.Core.Services.Git;
using VortexEditor.Controls;

namespace VortexEditor.Shell
{
    /// <summary>Source control: changes, commit, push/pull/fetch, branches, tags, history.</summary>
    public sealed class GitWindow : Window
    {
        /// <summary>Open this window (owned by the main window).</summary>
        public static void Open() => EditorWindows.Show(new GitWindow());

        private readonly string _repo = ProjectData.Current?.Path;
        private readonly GitService _git = GitService.Instance;
        private readonly ListBox _changes = new ListBox(), _history = new ListBox(), _tags = new ListBox();
        private readonly ComboBox _branches = new ComboBox { MinWidth = 160 };
        private readonly TextBox _commit = new TextBox { Watermark = "Commit message", AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _status = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center };
        private bool _busy;

        public GitWindow()
        {
            Title = "Source Control"; Width = 900; Height = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var root = new DockPanel();
            var toolbar = new DockPanel { Height = 40, Margin = new Thickness(12, 8, 12, 0) };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            left.Children.Add(new TextBlock { Text = "Branch", Classes = { "label" } });
            left.Children.Add(_branches);
            left.Children.Add(Btn("New…", async () => { var n = await Dialogs.Prompt("New branch", "Branch name", "feature/", "Create"); if (!string.IsNullOrWhiteSpace(n)) await RunOp(() => _git.CreateBranchAsync(_repo, n.Trim()), "Branch created"); }));
            left.Children.Add(Btn("Switch", async () => { if (_branches.SelectedItem is string b) await RunOp(() => _git.CheckoutCommitAsync(_repo, b), "Switched to " + b); }));
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(Btn("Fetch", () => RunOp(() => _git.FetchAsync(_repo), "Fetched")));
            right.Children.Add(Btn("Pull", () => RunOp(() => _git.PullAsync(_repo), "Pulled")));
            right.Children.Add(Btn("Push", () => RunOp(() => _git.PushAsync(_repo), "Pushed"), accent: true));
            right.Children.Add(Btn("Remote…", async () => { var url = await Dialogs.Prompt("Remote origin", "Repository URL", await _git.GetRemoteUrlAsync(_repo) ?? "", "Set"); if (url != null) await RunOp(() => _git.SetRemoteUrlAsync(_repo, url.Trim()), "Remote set"); }));
            DockPanel.SetDock(right, Dock.Right); toolbar.Children.Add(right); toolbar.Children.Add(left);
            DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
            var statusBar = new Border { Classes = { "hairline-top" }, Padding = new Thickness(12, 4), Child = _status, Height = 26 };
            DockPanel.SetDock(statusBar, Dock.Bottom); root.Children.Add(statusBar);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,*"), Margin = new Thickness(12, 8, 12, 8) };
            // changes + commit
            var changesCard = new Border { Classes = { "card" }, Padding = new Thickness(10) };
            var cs = new DockPanel();
            var ch = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            ch.Children.Add(new TextBlock { Text = "Changes", FontWeight = FontWeight.SemiBold });
            DockPanel.SetDock(ch, Dock.Top);
            var commitBox = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            commitBox.Children.Add(_commit);
            var cb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            cb.Children.Add(Btn("Refresh", Refresh));
            cb.Children.Add(Btn("Commit all changes", async () => { if (string.IsNullOrWhiteSpace(_commit.Text)) { await Dialogs.Alert("Commit message", "Describe the change first."); return; } await RunOp(() => _git.StageAllAndCommitAsync(_repo, _commit.Text.Trim()), "Committed"); _commit.Text = ""; }, accent: true));
            commitBox.Children.Add(cb);
            DockPanel.SetDock(commitBox, Dock.Bottom);
            cs.Children.Add(ch); cs.Children.Add(commitBox); cs.Children.Add(_changes);
            _changes.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GitFileChange>((c, _) =>
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                sp.Children.Add(new Border { Classes = { "badge" }, Child = new TextBlock { Text = c.Kind.ToString(), FontSize = 10 } });
                sp.Children.Add(new TextBlock { Text = c.Path, VerticalAlignment = VerticalAlignment.Center });
                return sp;
            });
            changesCard.Child = cs;
            grid.Children.Add(changesCard);
            // history + tags
            var rightCol = new Grid { RowDefinitions = new RowDefinitions("*,5,Auto") };
            var histCard = new Border { Classes = { "card" }, Padding = new Thickness(10) };
            var hs = new DockPanel();
            var hh = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            hh.Children.Add(Btn("Checkout selected", async () => { if (_history.SelectedItem is GitCommit c && await Dialogs.Confirm("Checkout " + c.Hash + "?", "Your working copy moves to this commit (detached).", "Checkout", "Cancel")) await RunOp(() => _git.CheckoutCommitAsync(_repo, c.Hash), "Checked out"); }));
            hh.Children.Add(new TextBlock { Text = "History", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(hh, Dock.Top);
            _history.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GitCommit>((c, _) =>
            {
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = c.Subject, FontWeight = FontWeight.Medium });
                sp.Children.Add(new TextBlock { Text = c.Hash.Substring(0, Math.Min(7, c.Hash.Length)) + "  ·  " + c.Meta, Classes = { "small", "tertiary" } });
                return sp;
            });
            hs.Children.Add(hh); hs.Children.Add(_history);
            histCard.Child = hs;
            Grid.SetRow(histCard, 0);
            var tagCard = new Border { Classes = { "card" }, Padding = new Thickness(10), MaxHeight = 170 };
            var ts = new DockPanel();
            var th = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            th.Children.Add(Btn("New tag…", async () => { var n = await Dialogs.Prompt("New tag", "Tag name (e.g. v1.0)", "v1.0", "Create"); if (!string.IsNullOrWhiteSpace(n)) await RunOp(() => _git.CreateTagAsync(_repo, n.Trim(), "Release " + n.Trim()), "Tag created"); }));
            th.Children.Add(new TextBlock { Text = "Tags", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(th, Dock.Top);
            ts.Children.Add(th); ts.Children.Add(_tags);
            tagCard.Child = ts;
            Grid.SetRow(tagCard, 2);
            rightCol.Children.Add(histCard); rightCol.Children.Add(new GridSplitter { ResizeDirection = GridResizeDirection.Rows }); rightCol.Children.Add(tagCard);
            Grid.SetColumn(rightCol, 2);
            grid.Children.Add(new GridSplitter { ResizeDirection = GridResizeDirection.Columns });
            Grid.SetColumn(grid.Children[1], 1);
            grid.Children.Add(rightCol);
            root.Children.Add(grid);
            Content = root;
            Opened += async (s, e) => await Refresh();
        }

        private static Button Btn(string text, Func<Task> a, bool accent = false)
        {
            var b = new Button { Content = text }; if (accent) b.Classes.Add("accent");
            b.Click += async (s, e) => { try { await a(); } catch (Exception ex) { EditorCommands.Fail("Git", ex); } };
            return b;
        }

        private async Task RunOp(Func<Task<GitResult>> op, string okMessage)
        {
            if (_busy || _repo == null) return;
            _busy = true; _status.Text = "Working…";
            try
            {
                var r = await op();
                _status.Text = r.Success ? okMessage : "Failed: " + r.Message;
                if (r.Success) EditorCommands.Toast(okMessage); else await Dialogs.Alert("Git", r.Message);
            }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { _busy = false; }
            await Refresh();
        }

        private async Task Refresh()
        {
            if (_repo == null) return;
            try
            {
                if (!await _git.IsAvailableAsync()) { _status.Text = "git is not installed (install Xcode Command Line Tools)."; return; }
                await _git.EnsureRepoAsync(_repo);
                _changes.ItemsSource = await _git.StatusAsync(_repo);
                _history.ItemsSource = await _git.LogAsync(_repo, 200);
                _tags.ItemsSource = await _git.ListTagsAsync(_repo);
                var branches = await _git.ListBranchesAsync(_repo);
                var current = await _git.CurrentBranchAsync(_repo);
                _branches.ItemsSource = branches;
                _branches.SelectedItem = branches.FirstOrDefault(b => b == current);
                bool remote = await _git.HasRemoteAsync(_repo);
                _status.Text = "On " + current + (remote ? "" : "  ·  no remote configured") + "  ·  " + (_changes.ItemCount) + " change(s)";
            }
            catch (Exception ex) { _status.Text = "Git: " + ex.Message; }
        }
    }
}
