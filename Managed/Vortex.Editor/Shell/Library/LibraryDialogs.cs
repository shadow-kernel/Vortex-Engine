using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Controls;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Services;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell.Library
{
    /// <summary>Shared bits of the asset-library windows.</summary>
    internal static class LibraryUi
    {
        public static GlobalAssetDatabase Lib => GlobalAssetDatabase.Instance;

        public static async Task ShowModal(Window w)
        {
            var owner = EditorKit.ActiveWindow();
            if (owner != null && !ReferenceEquals(owner, w) && owner.IsVisible) await w.ShowDialog(owner);
            else { var tcs = new TaskCompletionSource<bool>(); w.Closed += (s, e) => tcs.TrySetResult(true); w.Show(); await tcs.Task; }
        }

        public static Control Footer(params Control[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var b in buttons) if (b != null) row.Children.Add(b);
            return new Border { Background = EditorKit.Brush("VxToolbarBrush"), BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 10), Child = row };
        }

        /// <summary>Content + footer, the content scrolling.</summary>
        public static Control Layout(Control content, Control footer)
        {
            var g = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            g.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            Grid.SetRow(footer, 1);
            g.Children.Add(footer);
            return g;
        }

        public static TextBlock Title(string text) => new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        public static TextBlock Para(string text) => new TextBlock { Text = text, Classes = { "secondary" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        public static TextBlock Small(string text) => new TextBlock { Text = text, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap };
        public static string Bytes(long b) => GlobalAssetDatabase.FormatBytes(b);

        /// <summary>The Library tab and its thumbnails after the library itself changed place or content wholesale.</summary>
        public static void RefreshViews()
        {
            LibraryThumbs.Clear();
            LibraryView.Current?.Refresh();
        }
    }

    // ====================================================================== index existing projects (#64)

    /// <summary>
    /// Index existing projects into the library: pick projects (the Project Hub's list + any folder), then every asset is
    /// hashed, stored once and registered — duplicates across projects collapse into one entry, .vmeta tags come
    /// along, thumbnails render in the background. Cancellable; re-running skips what is already known.
    /// </summary>
    public sealed class LibraryIndexDialog : Window
    {
        public static LibraryIndexDialog Current { get; private set; }

        public static async Task Run()
        {
            var d = new LibraryIndexDialog();
            Current = d;
            try { await LibraryUi.ShowModal(d); } finally { if (ReferenceEquals(Current, d)) Current = null; }
        }

        private sealed class Row { public CheckBox Box; public string Path; public string Name; }
        private readonly List<Row> _rows = new List<Row>();
        private readonly StackPanel _list = new StackPanel { Spacing = 2 };
        private readonly CheckBox _thumbs = new CheckBox { Content = "Render thumbnails in the background", IsChecked = true };
        private readonly ProgressBar _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
        private readonly TextBlock _status = new TextBlock { Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
        private readonly Button _start, _close, _addFolder;
        private CancellationTokenSource _cts;

        /// <summary>The report of the last run (tests).</summary>
        public IndexReport Report { get; private set; }

        public LibraryIndexDialog()
        {
            Title = "Index Existing Projects";
            Width = 600; Height = 600; MinWidth = 480; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

            var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 12), Spacing = 6 };
            stack.Children.Add(LibraryUi.Title("Index existing projects"));
            stack.Children.Add(LibraryUi.Para("Adds every asset of the selected projects to the machine-wide library. Each file is stored once — the same texture " +
                                              "or sound in three projects becomes one entry used by three projects. Tags from .vmeta files come along and existing " +
                                              ".vmeta files get their content hash; nothing else in the projects changes."));
            var excluded = LibraryUi.Lib.Settings.ExcludedTypes.Where(t => t != (int)AssetType.Unknown && t != (int)AssetType.Folder).Select(t => ((AssetType)t).ToString()).ToList();
            if (excluded.Count > 0) stack.Children.Add(LibraryUi.Small("Skipped by the library settings: " + string.Join(", ", excluded) + "."));

            stack.Children.Add(Ui.Header("Projects", new Thickness(0, 10, 0, 4)));
            stack.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(8), Child = _list });
            _addFolder = Ui.Button("Add Folder…", () => _ = AddFolder(), "Index a project that is not in the Project Hub list");
            _addFolder.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(_addFolder);
            stack.Children.Add(_thumbs);
            stack.Children.Add(_bar);
            stack.Children.Add(_status);
            stack.Children.Add(_summary);

            _close = Ui.Button("Cancel", () => { if (_cts != null) _cts.Cancel(); else Close(); }, null, null, 84);
            _start = Ui.Button("Index", () => _ = Start(), "Start indexing", "accent", 100);
            _start.IsDefault = true;
            Content = LibraryUi.Layout(stack, LibraryUi.Footer(_close, _start));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = ProjectData.Current;
            if (current?.Path != null) AddRow(current.Path, current.Name, seen);
            try { foreach (var p in ProjectService.Instance.GetAllProjects().Values.OrderBy(p => p.Name)) AddRow(p.Path, p.Name, seen); } catch { }
            if (_rows.Count == 0) _list.Children.Add(LibraryUi.Small("No projects known yet — add a project folder."));
        }

        private void AddRow(string path, string name, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(path)) return;
            string full; try { full = Path.GetFullPath(path).TrimEnd('/', '\\'); } catch { return; }
            if (!seen.Add(full)) return;
            bool ok = Directory.Exists(Path.Combine(full, "Assets"));
            var box = new CheckBox { IsChecked = ok, IsEnabled = ok, VerticalAlignment = VerticalAlignment.Center };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(name) ? Path.GetFileName(full) : name, FontWeight = FontWeight.SemiBold });
            text.Children.Add(new TextBlock { Text = ok ? full : full + " — not found", Classes = { "small", "tertiary" }, TextTrimming = TextTrimming.CharacterEllipsis });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { box, text } };
            if (_list.Children.Count == 1 && _list.Children[0] is TextBlock) _list.Children.Clear();
            _list.Children.Add(row);
            _rows.Add(new Row { Box = box, Path = full, Name = name });
        }

        private async Task AddFolder()
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Project folder (contains Assets/)", AllowMultiple = true });
            foreach (var f in picked ?? Array.Empty<IStorageFolder>())
            {
                var p = f.TryGetLocalPath();
                if (string.IsNullOrEmpty(p)) continue;
                if (!Directory.Exists(Path.Combine(p, "Assets"))) { await Dialogs.Alert("Index Projects", "“" + Path.GetFileName(p) + "” has no Assets folder — pick a Vortex project folder.", owner: this); continue; }
                AddRow(p, null, new HashSet<string>(_rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase));
            }
        }

        /// <summary>Run the scan (the Index button; tests call it directly).</summary>
        public async Task<IndexReport> Start(IEnumerable<(string path, string name)> only = null)
        {
            var projects = only?.ToList() ?? _rows.Where(r => r.Box.IsChecked == true).Select(r => (r.Path, r.Name)).ToList();
            if (projects.Count == 0) { _status.Text = "Select at least one project."; return null; }
            var lib = LibraryUi.Lib;
            if (!lib.IsAvailable) { _status.Text = lib.LastError; return null; }
            _cts = new CancellationTokenSource();
            _start.IsEnabled = false; _addFolder.IsEnabled = false; _thumbs.IsEnabled = false;
            foreach (var r in _rows) r.Box.IsEnabled = false;
            _close.Content = "Stop";
            _bar.IsVisible = true; _summary.IsVisible = false;
            bool thumbs = _thumbs.IsChecked == true;
            var progress = new Progress<LibraryProgress>(p =>
            {
                _bar.Value = p.Fraction;
                _status.Text = p.Phase + " — " + p.Done + " / " + p.Total + (string.IsNullOrEmpty(p.Current) ? "" : " · " + p.Current);
            });
            var token = _cts.Token;
            IndexReport rep;
            try
            {
                rep = await Task.Run(() => LibraryProjects.IndexProjects(lib, projects,
                    thumbs ? (Action<string, string, AssetType>)((h, f, t) => LibraryThumbs.Ensure(h, f, t)) : null, progress, token));
            }
            finally { _cts = null; }
            Report = rep;
            try { lib.Settings.IndexPromptShown = true; lib.Settings.Save(); } catch { }
            _bar.Value = 1;
            _status.Text = rep.Cancelled ? "Stopped — run it again to continue where it stopped." : "Done in " + rep.Duration.TotalSeconds.ToString("0.0") + " s.";
            _summary.Text = rep.Files + " file(s) in " + rep.Projects + " project(s): " + rep.Registered + " new asset(s), " + rep.DuplicatesCollapsed + " duplicate(s) collapsed, " +
                            rep.AlreadyKnown + " already in the library, " + rep.Skipped + " skipped by the type rules. Stored " + LibraryUi.Bytes(rep.BytesStored) +
                            "; " + rep.VmetaUpdated + " .vmeta file(s) got their content hash." +
                            (rep.Errors.Count > 0 ? "\n\nProblems:\n" + string.Join("\n", rep.Errors.Take(12)) + (rep.Errors.Count > 12 ? "\n…" : "") : "");
            _summary.IsVisible = true;
            _close.Content = "Close";
            _start.IsVisible = false;
            LibraryView.Current?.Refresh();
            return rep;
        }
    }

    // ====================================================================== tag manager (#62)

    /// <summary>All library tags with their usage counts: rename (renaming onto an existing tag merges), merge, delete.</summary>
    public sealed class LibraryTagManager : Window
    {
        public static async Task Run() => await LibraryUi.ShowModal(new LibraryTagManager());

        private readonly ListBox _list = new ListBox { MinHeight = 260 };
        private readonly TextBox _filter = new TextBox { Watermark = "Filter tags" };

        public LibraryTagManager()
        {
            Title = "Library Tags";
            Width = 460; Height = 560; MinWidth = 380; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var stack = new DockPanel { Margin = new Thickness(20, 16, 20, 12) };
            var head = new StackPanel { Spacing = 6 };
            head.Children.Add(LibraryUi.Title("Library tags"));
            head.Children.Add(LibraryUi.Para("Rename a tag everywhere (renaming onto an existing tag merges the two), merge tags or delete one. Project .vmeta tags are not changed."));
            head.Children.Add(_filter);
            DockPanel.SetDock(head, Dock.Top);
            stack.Children.Add(head);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(Ui.Button("Rename…", () => _ = RenameSelected()));
            buttons.Children.Add(Ui.Button("Merge Into…", () => _ = MergeSelected()));
            var del = Ui.Button("Delete", () => _ = DeleteSelected());
            del.Foreground = EditorKit.Brush("VxRedBrush");
            buttons.Children.Add(del);
            DockPanel.SetDock(buttons, Dock.Bottom);
            stack.Children.Add(buttons);
            _list.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(_list);
            Content = LibraryUi.Layout(stack, LibraryUi.Footer(Ui.Button("Close", Close, null, null, 84)));
            _filter.TextChanged += (s, e) => Reload();
            Reload();
        }

        private void Reload()
        {
            string f = (_filter.Text ?? "").Trim();
            _list.ItemsSource = LibraryUi.Lib.TagCounts().Where(t => f.Length == 0 || t.tag.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(t => new ListBoxItem { Content = t.tag + "   (" + t.count + ")", Tag = t.tag }).ToList();
        }

        private string Selected => (_list.SelectedItem as ListBoxItem)?.Tag as string;

        private async Task RenameSelected()
        {
            var tag = Selected; if (tag == null) return;
            var to = await Dialogs.Prompt("Rename Tag", "New name for “" + tag + "” (an existing tag name merges both):", tag, "Rename", this);
            if (string.IsNullOrWhiteSpace(to) || to.Trim() == tag) return;
            LibraryUi.Lib.RenameTag(tag, to.Trim());
            Reload();
        }

        private async Task MergeSelected()
        {
            var tag = Selected; if (tag == null) return;
            var others = LibraryUi.Lib.AllTags().Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).Take(15);
            var to = await Dialogs.Prompt("Merge Tag", "Merge “" + tag + "” into which tag?\n" + string.Join(", ", others), "", "Merge", this);
            if (string.IsNullOrWhiteSpace(to)) return;
            LibraryUi.Lib.MergeTags(tag, to.Trim());
            Reload();
        }

        private async Task DeleteSelected()
        {
            var tag = Selected; if (tag == null) return;
            if (!await Dialogs.Confirm("Delete Tag", "Remove the tag “" + tag + "” from every library asset?", "Delete", "Cancel", destructive: true, owner: this)) return;
            LibraryUi.Lib.DeleteTag(tag);
            Reload();
        }
    }

    // ====================================================================== maintenance (#63)

    /// <summary>
    /// Library maintenance: size by type / source, the biggest assets, verify every stored file against its hash, find
    /// and fix catalog↔blob mismatches (dry run first), collect unreferenced blobs, open the library folder.
    /// </summary>
    public sealed class LibraryMaintenanceWindow : Window
    {
        private static LibraryMaintenanceWindow _open;

        public static void Open()
        {
            if (_open != null) { _open.Activate(); return; }
            _open = new LibraryMaintenanceWindow();
            _open.Closed += (s, e) => _open = null;
            var owner = EditorKit.ActiveWindow();
            if (owner != null) _open.Show(owner); else _open.Show();
        }

        private readonly StackPanel _stats = new StackPanel { Spacing = 3 };
        private readonly TextBlock _output = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly ProgressBar _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
        private readonly WrapPanel _actions = new WrapPanel();
        private Button _fix;
        private OrphanReport _lastScan;

        public LibraryMaintenanceWindow()
        {
            Title = "Asset Library — Maintenance";
            Width = 640; Height = 640; MinWidth = 520; MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 12), Spacing = 6 };
            stack.Children.Add(LibraryUi.Title("Library maintenance"));
            stack.Children.Add(LibraryUi.Small(LibraryUi.Lib.Root));
            stack.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(12), Margin = new Thickness(0, 8, 0, 0), Child = _stats });
            void Act(string text, Func<Task> run, string tip) { var b = Ui.Button(text, () => _ = run(), tip); b.Margin = new Thickness(0, 0, 6, 6); _actions.Children.Add(b); }
            Act("Verify Files", Verify, "Re-hash every stored file and report missing or damaged ones");
            Act("Find Problems", Scan, "Dry run: files without catalog rows, rows without files, unreferenced blobs, stale thumbnails");
            Act("Collect Garbage", Collect, "Delete stored files nothing refers to any more");
            Act("Open Library Folder", () => { EditorCommands.RevealInFinder(LibraryUi.Lib.CatalogPath); return Task.CompletedTask; }, null);
            _fix = Ui.Button("Fix Problems", () => _ = Fix(), "Apply the fixes the last scan found", "accent");
            _fix.IsVisible = false; _fix.Margin = new Thickness(0, 0, 6, 6);
            _actions.Children.Add(_fix);
            stack.Children.Add(new Border { Margin = new Thickness(0, 10, 0, 0), Child = _actions });
            stack.Children.Add(_bar);
            stack.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(12), Child = _output });
            Content = LibraryUi.Layout(stack, LibraryUi.Footer(Ui.Button("Close", Close, null, null, 84)));
            _output.Text = "Pick an action above.";
            ReloadStats();
        }

        private void ReloadStats()
        {
            _stats.Children.Clear();
            var lib = LibraryUi.Lib;
            if (!lib.IsAvailable) { _stats.Children.Add(LibraryUi.Para(lib.LastError ?? "The library is not available.")); return; }
            var st = lib.Stats(10);
            _stats.Children.Add(new TextBlock { Text = st.Entries + " assets · " + LibraryUi.Bytes(st.StoredBytes) + " in " + st.Blobs + " stored files" + (st.SizeCapBytes > 0 ? " (cap " + LibraryUi.Bytes(st.SizeCapBytes) + ")" : ""), FontWeight = FontWeight.SemiBold });
            if (st.OverCap) _stats.Children.Add(new TextBlock { Text = "Over the size cap — delete assets you no longer need, then Collect Garbage.", Foreground = EditorKit.Brush("VxOrangeBrush"), FontSize = 12 });
            _stats.Children.Add(LibraryUi.Small("Thumbnails " + LibraryUi.Bytes(st.ThumbnailBytes) + " · " + st.Tags + " tags · used by " + st.Projects + " project(s)"));
            _stats.Children.Add(Ui.Header("By type", new Thickness(0, 8, 0, 2)));
            foreach (var (type, count, bytes) in st.ByType) _stats.Children.Add(LibraryUi.Small(LibraryView.TypeLabel(type) + ": " + count + " · " + LibraryUi.Bytes(bytes)));
            _stats.Children.Add(Ui.Header("By source", new Thickness(0, 8, 0, 2)));
            foreach (var (source, count, bytes) in st.BySource.Take(12)) _stats.Children.Add(LibraryUi.Small((source ?? "?") + ": " + count + " · " + LibraryUi.Bytes(bytes)));
            if (st.Largest.Count > 0)
            {
                _stats.Children.Add(Ui.Header("Largest", new Thickness(0, 8, 0, 2)));
                foreach (var e in st.Largest) _stats.Children.Add(LibraryUi.Small(e.Name + " (" + LibraryView.TypeLabel(e.Type) + ") · " + LibraryUi.Bytes(e.Size)));
            }
        }

        private void Busy(bool on) { _bar.IsVisible = on; if (on) _bar.Value = 0; foreach (var c in _actions.Children) c.IsEnabled = !on; }

        private async Task Verify()
        {
            Busy(true);
            _output.Text = "Verifying…";
            var progress = new Progress<LibraryProgress>(p => { _bar.Value = p.Fraction; _output.Text = "Verifying " + p.Done + " / " + p.Total + "…"; });
            VerifyReport rep;
            try { rep = await Task.Run(() => LibraryUi.Lib.Verify(progress)); }
            finally { Busy(false); }
            var names = new Func<IEnumerable<string>, string>(hashes => string.Join("\n", hashes.Take(20).Select(h =>
            {
                var e = LibraryUi.Lib.FindByHash(h).FirstOrDefault();
                return "• " + (e != null ? e.Name + " (" + h.Substring(0, 12) + "…)" : h.Substring(0, 12) + "… (companion file)");
            })));
            _output.Text = rep.Ok
                ? "All " + rep.Checked + " stored files match their hashes (" + rep.Duration.TotalSeconds.ToString("0.0") + " s)."
                : "Checked " + rep.Checked + " files." +
                  (rep.Missing.Count > 0 ? "\n\nMissing (" + rep.Missing.Count + "):\n" + names(rep.Missing) : "") +
                  (rep.Corrupt.Count > 0 ? "\n\nDamaged (" + rep.Corrupt.Count + "):\n" + names(rep.Corrupt) : "") +
                  "\n\nRe-import the original files to restore them; Find Problems → Fix marks them as missing.";
        }

        private async Task Scan()
        {
            Busy(true);
            _output.Text = "Scanning…";
            try { _lastScan = await Task.Run(() => LibraryUi.Lib.ScanOrphans(false)); }
            finally { Busy(false); }
            var r = _lastScan;
            _output.Text = r.IsClean ? "No problems found." :
                "Found:\n" +
                (r.UntrackedFiles.Count > 0 ? "• " + r.UntrackedFiles.Count + " stored file(s) without a catalog entry\n" : "") +
                (r.UnreferencedBlobs.Count > 0 ? "• " + r.UnreferencedBlobs.Count + " stored file(s) nothing refers to\n" : "") +
                (r.MissingFiles.Count > 0 ? "• " + r.MissingFiles.Count + " catalog row(s) whose file is missing\n" : "") +
                (r.StaleThumbnails.Count > 0 ? "• " + r.StaleThumbnails.Count + " thumbnail(s) of deleted assets\n" : "") +
                (r.BrokenEntries.Count > 0 ? "• " + r.BrokenEntries.Count + " asset(s) without their bytes: " + string.Join(", ", r.BrokenEntries.Take(8).Select(e => e.Name)) + "\n" : "") +
                "\nFix frees about " + LibraryUi.Bytes(r.ReclaimableBytes) + ". Assets without bytes are kept — delete them in the Library tab or re-import the originals.";
            _fix.IsVisible = !r.IsClean;
        }

        private async Task Fix()
        {
            if (!await Dialogs.Confirm("Fix Library Problems", "Delete stray and unreferenced stored files and stale thumbnails, and mark missing files in the catalog?", "Fix", "Cancel", owner: this)) return;
            Busy(true);
            OrphanReport r;
            try { r = await Task.Run(() => LibraryUi.Lib.ScanOrphans(true)); }
            finally { Busy(false); }
            _fix.IsVisible = false;
            _output.Text = "Fixed. Freed about " + LibraryUi.Bytes(r.ReclaimableBytes) + ".";
            ReloadStats();
            LibraryUi.RefreshViews();
        }

        private async Task Collect()
        {
            Busy(true);
            long freed;
            try { freed = await Task.Run(() => LibraryUi.Lib.CollectGarbage()); }
            finally { Busy(false); }
            _output.Text = freed > 0 ? "Freed " + LibraryUi.Bytes(freed) + "." : "Nothing to collect — every stored file is in use.";
            ReloadStats();
        }
    }

    // ====================================================================== settings (#65)

    /// <summary>Library settings: location (move with hash verification), size cap, automatic registration, type rules.</summary>
    public sealed class LibrarySettingsDialog : Window
    {
        public static async Task Run() => await LibraryUi.ShowModal(new LibrarySettingsDialog());

        private readonly TextBlock _location = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly TextBlock _info = new TextBlock { Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap };
        private readonly NumericUpDown _cap = new NumericUpDown { Minimum = 0, Maximum = 100000, Increment = 5, FormatString = "0", Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly CheckBox _auto = new CheckBox { Content = "Add every import to the library automatically" };
        private readonly Dictionary<AssetType, CheckBox> _types = new Dictionary<AssetType, CheckBox>();
        private readonly ProgressBar _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
        private readonly TextBlock _moveStatus = new TextBlock { Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap };
        private Button _move;

        public LibrarySettingsDialog()
        {
            Title = "Asset Library Settings";
            Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var lib = LibraryUi.Lib;
            var s = lib.Settings;
            var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 14), Spacing = 6 };
            stack.Children.Add(LibraryUi.Title("Asset library"));

            stack.Children.Add(Ui.Header("Location", new Thickness(0, 8, 0, 2)));
            _location.Text = lib.Root;
            stack.Children.Add(_location);
            stack.Children.Add(_info);
            _move = Ui.Button("Move Library…", () => _ = Move(), "Copy the library to another folder or drive, verify every file, then remove the old copy");
            _move.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(_move);
            stack.Children.Add(_bar);
            stack.Children.Add(_moveStatus);

            stack.Children.Add(Ui.Header("Size cap", new Thickness(0, 12, 0, 2)));
            _cap.Value = (decimal)s.SizeCapGB;
            var capRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _cap, new TextBlock { Text = "GB (0 = no cap)", VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } } } };
            stack.Children.Add(capRow);
            stack.Children.Add(LibraryUi.Small("Over the cap the library keeps working but warns you — it never deletes assets on its own."));

            stack.Children.Add(Ui.Header("Automatic registration", new Thickness(0, 12, 0, 2)));
            _auto.IsChecked = s.AutoRegisterImports;
            stack.Children.Add(_auto);
            stack.Children.Add(LibraryUi.Small("Types the library takes (imports and project indexing). \"Add Files to Library\" always works."));
            var wrap = new WrapPanel();
            foreach (AssetType t in Enum.GetValues(typeof(AssetType)))
            {
                if (t == AssetType.Folder) continue;
                var cb = new CheckBox { Content = LibraryView.TypeLabel(t), IsChecked = s.Includes(t), MinWidth = 110 };
                _types[t] = cb;
                wrap.Children.Add(cb);
            }
            stack.Children.Add(wrap);

            var cancel = Ui.Button("Cancel", Close, null, null, 84);
            var save = Ui.Button("Save", Save, null, "accent", 90);
            save.IsDefault = true;
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { cancel, save } });
            Content = stack;
            UpdateInfo();
        }

        private void UpdateInfo()
        {
            var lib = LibraryUi.Lib;
            _location.Text = lib.Root;
            if (!lib.IsAvailable) { _info.Text = lib.LastError; return; }
            var st = lib.Stats(0);
            _info.Text = st.Entries + " assets · " + LibraryUi.Bytes(st.StoredBytes + st.ThumbnailBytes) + " on disk" + (string.IsNullOrEmpty(lib.Settings.Root) ? " · default location" : "");
        }

        private void Save()
        {
            var s = LibraryUi.Lib.Settings;
            s.SizeCapGB = (double)(_cap.Value ?? 0);
            s.AutoRegisterImports = _auto.IsChecked == true;
            foreach (var kv in _types) s.SetIncluded(kv.Key, kv.Value.IsChecked == true);
            LibraryUi.Lib.SaveSettings();
            Close();
        }

        private async Task Move()
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "New library folder (empty)" });
            var p = picked?.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) return;
            string target = Path.GetFileName(p.TrimEnd('/', '\\')) == "AssetDB" ? p : Path.Combine(p, "AssetDB");
            if (!await Dialogs.Confirm("Move Library", "Move the asset library to\n" + target + "?\n\nClose other Vortex editors first. Every file is verified after copying; the old copy is removed afterwards.", "Move", "Cancel", owner: this))
                return;
            _move.IsEnabled = false; _bar.IsVisible = true;
            var progress = new Progress<LibraryProgress>(pr => { _bar.Value = pr.Fraction; _moveStatus.Text = pr.Phase + " " + pr.Done + " / " + pr.Total; });
            string error = null;
            bool ok = await Task.Run(() => LibraryUi.Lib.MoveLibrary(target, progress, CancellationToken.None, out error));
            _bar.IsVisible = false; _move.IsEnabled = true;
            _moveStatus.Text = ok ? "Moved." : "Not moved: " + error;
            UpdateInfo();
            LibraryUi.RefreshViews();
        }
    }
}
