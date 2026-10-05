using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Controls;
using VortexEditor.Services;
using VortexEditor.Shell.AssetImport;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Import assets (port of the Windows AssetImportDialog + ImportResultDialog): pick the name (single file), the target
    /// folder inside the project, tags (quick tags + custom) and the options (copy into the project, .vmeta, bring related
    /// textures along, extract animation clips, overwrite or keep both), then import — models get their own folder with
    /// one .vmat per submesh, extracted embedded textures and .vanim clips — and read the summary: meshes, created
    /// materials, textures (copied / extracted / missing), clips, location, warnings and errors, with Show in Asset
    /// Browser / Add to Scene / Model Editor.
    /// </summary>
    public sealed class AssetImportDialog : Window
    {
        /// <summary>Import <paramref name="files"/> into <paramref name="targetFolder"/> (absolute; null = the type's default
        /// folder); returns the imported paths (models: the model file in its new folder). Empty when cancelled.</summary>
        public static async Task<string[]> Run(string[] files, string targetFolder)
        {
            var list = (files ?? Array.Empty<string>()).Where(f => !string.IsNullOrEmpty(f) && File.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (list.Length == 0) return Array.Empty<string>();
            if (string.IsNullOrEmpty(ProjectData.Current?.Path)) { EditorCommands.Toast("Open a project first"); return Array.Empty<string>(); }
            var dlg = new AssetImportDialog(list, targetFolder);
            var owner = EditorWindows.Owner;
            if (owner != null && owner.IsVisible) await dlg.ShowDialog(owner);
            else { var tcs = new TaskCompletionSource<bool>(); dlg.Closed += (s, e) => tcs.TrySetResult(true); dlg.Show(); await tcs.Task; }
            return dlg.ImportedPaths;
        }

        private readonly List<string> _files;
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _target = new TextBox();
        private readonly HashSet<string> _tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ToggleButton> _quick = new Dictionary<string, ToggleButton>(StringComparer.OrdinalIgnoreCase);
        private readonly WrapPanel _selectedTags = new WrapPanel();
        private readonly TextBlock _noTags = new TextBlock { Text = "No tags selected. Click quick tags above or add custom tags below.", FontStyle = FontStyle.Italic, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBox _customTag = new TextBox { Watermark = "Custom tag" };
        private readonly CheckBox _copy = new CheckBox { Content = "Copy files into the project folder", IsChecked = true };
        private readonly CheckBox _meta = new CheckBox { Content = "Generate metadata files (.vmeta)", IsChecked = true };
        private readonly CheckBox _related = new CheckBox { Content = "Auto-detect related textures (bring them along)", IsChecked = true };
        private readonly CheckBox _clips = new CheckBox { Content = "Extract animation clips (.vanim)", IsChecked = true };
        private readonly CheckBox _overwrite = new CheckBox { Content = "Overwrite existing files (off: keep both)", IsChecked = true };
        private readonly StackPanel _fileRows = new StackPanel { Spacing = 2 };
        private readonly TextBlock _subtitle = new TextBlock { FontSize = 12 };
        private readonly TextBlock _error = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private Control _optionsPage;
        // global asset library (#57): files whose exact bytes the library already has, and the ones imported "as new"
        private readonly Dictionary<string, LibraryEntry> _inLibrary = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _asNew = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Border _libraryBanner = new Border { IsVisible = false, Padding = new Thickness(12, 10), Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        private readonly TextBlock _libraryChecking = new TextBlock { Text = "Checking the asset library…", FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

        /// <summary>The background library lookup started when the dialog opened (tests await it).</summary>
        public Task LibraryCheck { get; private set; } = Task.CompletedTask;
        /// <summary>The library already has this file's exact content.</summary>
        internal bool KnownInLibrary(string file) => _inLibrary.ContainsKey(file);

        public string[] ImportedPaths { get; private set; } = Array.Empty<string>();
        public List<ImportReport> Reports { get; } = new List<ImportReport>();
        public bool ShowingResults { get; private set; }

        public AssetImportDialog(string[] files, string targetFolder)
        {
            _files = files.ToList();
            Title = "Import Asset" + (_files.Count > 1 ? "s" : "");
            Width = 620; Height = 700; MinWidth = 520; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            _subtitle.Foreground = Ui.Brush("VxTextSecondaryBrush");
            _noTags.Foreground = Ui.Brush("VxTextTertiaryBrush");
            _error.Foreground = Ui.Brush("VxRedBrush");

            string root = ProjectData.Current?.Path ?? "";
            string folder = targetFolder;
            if (string.IsNullOrEmpty(folder))
            {
                var kinds = _files.Select(ModelImportPipeline.DefaultFolderFor).Distinct().ToList();
                folder = Path.Combine(root, kinds.Count == 1 ? kinds[0] : "Assets");
            }
            _target.Text = Ui.ProjectRelative(folder);
            _name.Text = Path.GetFileNameWithoutExtension(_files[0]);

            _optionsPage = BuildOptionsPage();
            Content = _optionsPage;
            AutoSelectTags();
            RefreshFiles();
            LibraryCheck = CheckLibraryAsync();
            KeyDown += (s, e) => { if (e.Key == Key.Escape && !ShowingResults) Close(); };
        }

        // ================================================================== options page

        private Control BuildOptionsPage()
        {
            var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 10) };
            stack.Children.Add(new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
            stack.Children.Add(_subtitle);
            _libraryChecking.Foreground = Ui.Brush("VxTextTertiaryBrush");
            stack.Children.Add(_libraryChecking);
            stack.Children.Add(_libraryBanner);

            stack.Children.Add(Ui.Header("Files", new Thickness(0, 14, 0, 6)));
            stack.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(8), MaxHeight = 190, Child = new ScrollViewer { Content = _fileRows } });

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
            if (_files.Count == 1)
            {
                var nl = new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("VxTextSecondaryBrush") };
                grid.Children.Add(nl);
                Grid.SetColumn(_name, 1); _name.Margin = new Thickness(0, 0, 0, 8); grid.Children.Add(_name);
            }
            var tl = new TextBlock { Text = "Target folder:", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("VxTextSecondaryBrush") };
            Grid.SetRow(tl, 1); grid.Children.Add(tl);
            var tg = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            tg.Children.Add(_target);
            var browse = Ui.Button("…", () => _ = BrowseFolder(), "Choose a folder inside the project");
            browse.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(browse, 1); tg.Children.Add(browse);
            Grid.SetRow(tg, 1); Grid.SetColumn(tg, 1); grid.Children.Add(tg);
            stack.Children.Add(grid);
            if (_files.Any(ModelImportPipeline.IsModel))
                stack.Children.Add(new TextBlock { Text = "Each model gets its own folder: <target>/<name>/ with materials/, textures and animations/.", FontSize = 11, Foreground = Ui.Brush("VxTextTertiaryBrush"), Margin = new Thickness(110, 4, 0, 0), TextWrapping = TextWrapping.Wrap });

            // tags
            stack.Children.Add(new TextBlock { Text = "Tags", FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 8) });
            var quick = new WrapPanel();
            foreach (var tag in AssetTagService.PredefinedTags)
            {
                var t = tag;
                var b = new ToggleButton { Content = t, Margin = new Thickness(0, 0, 4, 4), Classes = { "accentcheck" }, BorderThickness = new Thickness(1), BorderBrush = Ui.Brush("VxControlBorderBrush") };
                b.IsCheckedChanged += (s, e) => { if (b.IsChecked == true) AddTag(t); else RemoveTag(t); };
                _quick[t] = b;
                quick.Children.Add(b);
            }
            var quickStack = new StackPanel();
            quickStack.Children.Add(new TextBlock { Text = "Quick Tags", FontSize = 11, Foreground = Ui.Brush("VxTextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 6) });
            quickStack.Children.Add(quick);
            stack.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), Child = quickStack });
            var selStack = new StackPanel();
            selStack.Children.Add(new TextBlock { Text = "Selected Tags", FontSize = 11, Foreground = Ui.Brush("VxTextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 6) });
            selStack.Children.Add(_selectedTags);
            selStack.Children.Add(_noTags);
            stack.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(10), MinHeight = 56, Child = selStack });
            var custom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
            custom.Children.Add(_customTag);
            _customTag.KeyDown += (s, e) => { if (e.Key == Key.Return) { AddCustomTag(); e.Handled = true; } };
            var addTag = Ui.Button("Add Tag", AddCustomTag); addTag.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(addTag, 1); custom.Children.Add(addTag);
            stack.Children.Add(custom);

            // options
            var opts = new StackPanel { Spacing = 4 };
            opts.Children.Add(new TextBlock { Text = "Import Options", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            opts.Children.Add(_copy); opts.Children.Add(_meta);
            bool anyModel = _files.Any(ModelImportPipeline.IsModel);
            _related.IsVisible = anyModel; _clips.IsVisible = anyModel;
            opts.Children.Add(_related); opts.Children.Add(_clips); opts.Children.Add(_overwrite);
            ToolTip.SetTip(_copy, "Off: register files that already live inside the project where they are");
            ToolTip.SetTip(_related, "Copy the textures next to the source model (and its textures/ folder) into the model's folder");
            stack.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(14), Margin = new Thickness(0, 14, 0, 0), Child = opts });
            stack.Children.Add(_error);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(Ui.Button("Cancel", Close, null, null, 84));
            var import = Ui.Button("Import", () => _ = ImportAsync(confirm: true), "Copy and import", "accent", 100);
            import.IsDefault = true;
            buttons.Children.Add(import);

            var dock = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            dock.Children.Add(new ScrollViewer { Content = stack });
            var foot = new Border { Background = Ui.Brush("VxToolbarBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(22, 10), Child = buttons };
            Grid.SetRow(foot, 1); dock.Children.Add(foot);
            return dock;
        }

        private void RefreshFiles()
        {
            _fileRows.Children.Clear();
            foreach (var f in _files.ToList())
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(0, 1) };
                g.Children.Add(new VxIcon { Icon = AssetPickerDialog.IconFor(f), Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                var name = new TextBlock { Text = Path.GetFileName(f), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(name, f);
                Grid.SetColumn(name, 1); g.Children.Add(name);
                long size = 0; try { size = new FileInfo(f).Length; } catch { }
                var meta = new TextBlock { Text = ModelImportPipeline.KindOf(f) + " · " + ModelDocument.FormatBytes(size), FontSize = 11, Foreground = Ui.Brush("VxTextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
                Grid.SetColumn(meta, 2); g.Children.Add(meta);
                if (_inLibrary.TryGetValue(f, out var known) && _files.Count > 1)
                {
                    var file = f;
                    var asNew = new CheckBox { Content = "in library — import as new", FontSize = 11, IsChecked = _asNew.Contains(f), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                    ToolTip.SetTip(asNew, "Already in your library as “" + known.Name + "”" + (known.Tags.Count > 0 ? " (" + string.Join(", ", known.Tags) + ")" : "") +
                                          ", " + known.SourceDescription +
                                          ".\nChecked: give it a separate library entry with this import's name and tags (still stored once).");
                    asNew.IsCheckedChanged += (s, e) => { if (asNew.IsChecked == true) _asNew.Add(file); else _asNew.Remove(file); };
                    Grid.SetColumn(asNew, 3); g.Children.Add(asNew);
                }
                if (_files.Count > 1)
                {
                    var file = f;
                    var rm = Ui.IconButton("Close", "Don't import this file", () => { _files.Remove(file); RefreshFiles(); });
                    Grid.SetColumn(rm, 4); g.Children.Add(rm);
                }
                _fileRows.Children.Add(g);
            }
            var groups = _files.GroupBy(ModelImportPipeline.KindOf).Select(x => x.Count() + " " + x.Key.ToLowerInvariant() + (x.Count() > 1 ? "s" : ""));
            _subtitle.Text = _files.Count + " file(s): " + string.Join(", ", groups);
        }

        // ================================================================== library duplicate check (#57)

        /// <summary>Hash the incoming files (off the UI thread) and ask the library whether it has them already.</summary>
        private async Task CheckLibraryAsync()
        {
            var files = _files.ToList();
            List<(string file, LibraryEntry entry)> found;
            try
            {
                found = await Task.Run(() =>
                {
                    var lib = GlobalAssetDatabase.Instance;
                    var list = new List<(string, LibraryEntry)>();
                    if (!lib.IsAvailable) return list;
                    foreach (var f in files) { var e = lib.FindByFile(f, out _); if (e != null) list.Add((f, e)); }
                    return list;
                });
            }
            catch { found = new List<(string, LibraryEntry)>(); }
            _libraryChecking.IsVisible = false;
            foreach (var (f, e) in found) _inLibrary[f] = e;
            if (found.Count == 0 || ShowingResults) return;
            BuildLibraryBanner();
            RefreshFiles();
        }

        private void BuildLibraryBanner()
        {
            _libraryBanner.Background = Ui.Brush("VxAccentSoftBrush");
            _libraryBanner.BorderBrush = Ui.Brush("VxAccentBrush");
            var text = new StackPanel { Spacing = 3 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new VxIcon { Icon = "Library", Width = 15, Height = 15, Foreground = Ui.Brush("VxAccentBrush"), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = "Already in your library", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            text.Children.Add(head);
            if (_files.Count == 1 && _inLibrary.TryGetValue(_files[0], out var e))
            {
                text.Children.Add(new TextBlock { Text = "“" + e.Name + "” · " + (e.Type == AssetType.Mesh ? "Model" : e.Type.ToString()) + " · " + e.SourceDescription, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                if (e.Tags.Count > 0) text.Children.Add(new TextBlock { Text = "Tags: " + string.Join(", ", e.Tags), FontSize = 11, Foreground = Ui.Brush("VxTextSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
                var useLib = new RadioButton { GroupName = "libchoice", Content = new TextBlock { Text = "Use the library entry — its name and tags (no new library entry)", TextWrapping = TextWrapping.Wrap, FontSize = 12 }, IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
                var asNew = new RadioButton { GroupName = "libchoice", Content = new TextBlock { Text = "Import as new — a separate library entry with this import's name and tags (the file is still stored once)", TextWrapping = TextWrapping.Wrap, FontSize = 12 } };
                string file = _files[0];
                void Adopt()
                {
                    _asNew.Remove(file);
                    _name.Text = e.Name;
                    foreach (var t in e.Tags) AddTag(t);
                }
                useLib.IsCheckedChanged += (s, a) => { if (useLib.IsChecked == true) Adopt(); };
                asNew.IsCheckedChanged += (s, a) => { if (asNew.IsChecked == true) _asNew.Add(file); };
                text.Children.Add(useLib);
                text.Children.Add(asNew);
                Adopt();
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                var thumb = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Ui.Brush("VxFieldBrush"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
                thumb.Child = new VxIcon { Icon = AssetPickerDialog.IconFor(file), Width = 26, Height = 26 };
                LibraryThumbs.Request(e, bmp => thumb.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill });
                row.Children.Add(thumb);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                _libraryBanner.Child = row;
            }
            else
            {
                text.Children.Add(new TextBlock { Text = _inLibrary.Count + " of " + _files.Count + " files are already in the library. They are imported into the project as usual and keep one library entry — tick “import as new” on a file to give it a separate entry.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
                _libraryBanner.Child = text;
            }
            _libraryBanner.IsVisible = true;
        }

        private void AutoSelectTags()
        {
            AddTag("Imported");
            var n = Path.GetFileNameWithoutExtension(_files[0]).ToLowerInvariant();
            if (n.Contains("character") || n.Contains("player")) AddTag("Character");
            else if (n.Contains("skybox") || n.Contains("sky")) AddTag("Skybox");
            else if (n.Contains("ui")) AddTag("UI");
            else if (n.Contains("prop")) AddTag("Prop");
            else if (n.Contains("env") || n.Contains("terrain")) AddTag("Environment");
        }

        private void AddTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return;
            tag = tag.Trim();
            if (!_tags.Add(tag)) return;
            if (_quick.TryGetValue(tag, out var b) && b.IsChecked != true) b.IsChecked = true;
            RefreshSelectedTags();
        }

        private void RemoveTag(string tag)
        {
            if (!_tags.Remove(tag)) return;
            if (_quick.TryGetValue(tag, out var b) && b.IsChecked == true) b.IsChecked = false;
            RefreshSelectedTags();
        }

        private void AddCustomTag()
        {
            var t = _customTag.Text?.Trim();
            if (string.IsNullOrWhiteSpace(t)) return;
            AddTag(t); _customTag.Text = ""; _customTag.Focus();
        }

        private void RefreshSelectedTags()
        {
            _selectedTags.Children.Clear();
            foreach (var t in _tags.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var tag = t;
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                sp.Children.Add(new TextBlock { Text = t, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
                var x = new Button { Content = new VxIcon { Icon = "Close", Width = 10, Height = 10, Foreground = Brushes.White }, Classes = { "ghost" }, Padding = new Thickness(2) };
                x.Click += (s, e) => RemoveTag(tag);
                sp.Children.Add(x);
                _selectedTags.Children.Add(new Border { Background = Ui.Brush("VxAccentBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 3, 2), Margin = new Thickness(0, 0, 5, 5), Child = sp });
            }
            _noTags.IsVisible = _tags.Count == 0;
        }

        private async Task BrowseFolder()
        {
            var root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return;
            var opts = new FolderPickerOpenOptions { Title = "Select the target folder inside the project", AllowMultiple = false };
            try { var start = ResolveTarget(); if (start != null && Directory.Exists(start)) opts.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start); } catch { }
            var picked = await StorageProvider.OpenFolderPickerAsync(opts);
            var p = picked?.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) return;
            var rel = Ui.ProjectRelative(p);
            if (Path.IsPathRooted(rel)) { ShowError("The target folder must be inside the project (" + root + ")."); return; }
            _target.Text = rel;
            ShowError(null);
        }

        private string ResolveTarget()
        {
            var root = ProjectData.Current?.Path;
            var t = (_target.Text ?? "").Trim();
            if (string.IsNullOrEmpty(root)) return null;
            if (string.IsNullOrEmpty(t)) t = "Assets";
            var full = Path.GetFullPath(Path.IsPathRooted(t) ? t : Path.Combine(root, t.Replace('\\', '/')));
            var r = Path.GetFullPath(root).TrimEnd('/', '\\');
            return full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(full, r, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        private void ShowError(string text) { _error.Text = text ?? ""; _error.IsVisible = !string.IsNullOrEmpty(text); _error.Margin = new Thickness(0, 10, 0, 0); }

        // ================================================================== import

        /// <summary>Run the import (the Import button). <paramref name="confirm"/> = ask before overwriting existing files.</summary>
        public async Task ImportAsync(bool confirm)
        {
            if (_files.Count == 0) { ShowError("Nothing to import."); return; }
            string target = ResolveTarget();
            if (target == null) { ShowError("The target folder must be inside the project."); return; }
            string newName = _files.Count == 1 ? (_name.Text ?? "").Trim() : null;
            if (_files.Count == 1 && string.IsNullOrWhiteSpace(newName)) { ShowError("Please enter an asset name."); return; }
            if (newName != null && newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { ShowError("The name contains characters that are not allowed in file names."); return; }

            var o = new ImportOptions
            {
                CopyToProject = _copy.IsChecked == true,
                GenerateMeta = _meta.IsChecked == true,
                CopyRelatedTextures = _related.IsChecked == true,
                ExtractAnimations = _clips.IsChecked == true,
                Overwrite = _overwrite.IsChecked == true
            };
            o.Tags.AddRange(_tags);

            if (confirm && o.Overwrite && o.CopyToProject)
            {
                var existing = _files.Select(f => ModelImportPipeline.TargetPathFor(f, target, newName)).Where(File.Exists).ToList();
                if (existing.Count > 0 && !await Dialogs.Confirm("Files exist", existing.Count == 1 ? "'" + Path.GetFileName(existing[0]) + "' already exists. Overwrite?" : existing.Count + " files already exist in the target folder. Overwrite them?", "Overwrite", "Cancel"))
                    return;
            }

            ShowError(null);
            IsEnabled = false;
            try
            {
                Reports.Clear();
                foreach (var f in _files)
                {
                    await Task.Yield();   // let the UI breathe between files
                    o.LibraryForceNew = _asNew.Contains(f);
                    o.LibraryName = o.LibraryForceNew ? (newName ?? Path.GetFileNameWithoutExtension(f)) : null;
                    Reports.Add(ModelImportPipeline.Import(f, target, o, newName));
                }
                try { AssetDatabase.Instance.Refresh(); } catch { }
                try { EditorCommands.Window?.AssetBrowser?.Refresh(); } catch { }
                ImportedPaths = Reports.Where(r => r.Success && r.TargetPath != null).Select(r => r.TargetPath).ToArray();
                foreach (var r in Reports.Where(r => !r.Success)) ConsoleService.Instance.LogError("Import failed: " + Path.GetFileName(r.SourcePath) + " — " + r.Error);
                ShowResults();
            }
            catch (Exception ex) { ShowError("Import failed: " + ex.Message); ConsoleService.Instance.LogError("Import failed: " + ex); }
            finally { IsEnabled = true; }
        }

        // ================================================================== result page (port of ImportResultDialog)

        private void ShowResults()
        {
            ShowingResults = true;
            int ok = Reports.Count(r => r.Success), failed = Reports.Count - ok;
            bool warnings = Reports.Any(r => r.Warnings.Count > 0 || r.MissingTextures.Count > 0);
            Title = "Import Result";

            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 10) };
            string icon = failed == 0 ? (warnings ? "Warning" : "Check") : ok == 0 ? "Error" : "Warning";
            string brush = failed == 0 ? (warnings ? "VxOrangeBrush" : "VxGreenBrush") : ok == 0 ? "VxRedBrush" : "VxOrangeBrush";
            head.Children.Add(new VxIcon { Icon = icon, Width = 30, Height = 30, Foreground = Ui.Brush(brush), VerticalAlignment = VerticalAlignment.Center });
            var ht = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            ht.Children.Add(new TextBlock { Text = failed == 0 ? "Import Successful" : ok == 0 ? "Import Failed" : "Imported with errors", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = ok == 0 ? Ui.Brush("VxRedBrush") : Ui.Brush("VxTextBrush") });
            var single = Reports.Count == 1 ? Reports[0] : null;
            string sub = single != null
                ? (single.Success ? (single.IsModel ? (single.SubmeshNames.Count > 1 ? "Model imported with " + single.SubmeshNames.Count + " submeshes (multi-material)" : "Model imported and ready to use") : single.Kind + " imported") : single.Error)
                : ok + " of " + Reports.Count + " file(s) imported" + (failed > 0 ? ", " + failed + " failed" : "");
            ht.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Ui.Brush("VxTextSecondaryBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 500 });
            head.Children.Add(ht);

            var content = new StackPanel { Spacing = 2 };
            foreach (var r in Reports) AddReport(content, r, Reports.Count > 1);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var first = Reports.FirstOrDefault(r => r.Success);
            var models = Reports.Where(r => r.Success && r.IsModel).ToList();
            var showBtn = Ui.Button("Show in Asset Browser", () => { ShowInBrowser(first); Close(); }, "Reveal the imported files in the Asset Browser", "accent");
            showBtn.IsEnabled = first != null;
            buttons.Children.Add(showBtn);
            if (models.Count > 0)
            {
                buttons.Children.Add(Ui.Button(models.Count == 1 ? "Add to Scene" : "Add " + models.Count + " to Scene", () => { AddToScene(models); Close(); }, "Place the imported model(s) in the active scene"));
                if (models.Count == 1) buttons.Children.Add(Ui.Button("Model Editor", () => { EditorWindows.ModelEditor(models[0].TargetPath); Close(); }, "Edit the imported model's materials and textures"));
            }
            buttons.Children.Add(Ui.Button("Close", Close, null, null, 80));

            var body = new StackPanel { Margin = new Thickness(22, 18, 22, 10) };
            body.Children.Add(head);
            body.Children.Add(content);
            var dock = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            dock.Children.Add(new ScrollViewer { Content = body });
            var foot = new Border { Background = Ui.Brush("VxToolbarBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(22, 10), Child = buttons };
            Grid.SetRow(foot, 1); dock.Children.Add(foot);
            Content = dock;
        }

        private static TextBlock H(string text) => new TextBlock { Text = text, Foreground = Ui.Brush("VxTealBrush"), FontWeight = FontWeight.Bold, FontSize = 13, Margin = new Thickness(0, 10, 0, 4) };
        private static TextBlock Line(string text, string brush = "VxTextBrush", double size = 12, double indent = 10)
            => new TextBlock { Text = text, Foreground = Ui.Brush(brush), FontSize = size, Margin = new Thickness(indent, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
        private static TextBlock Item(string text, string brush = "VxTextSecondaryBrush") => Line("• " + text, brush, 11, 22);

        private static void AddReport(StackPanel p, ImportReport r, bool many)
        {
            if (many) p.Children.Add(new Border { BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 10, 0, 0), Child = new TextBlock { Text = Path.GetFileName(r.SourcePath) + (r.Success ? "" : "  —  failed"), FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 0), Foreground = r.Success ? Ui.Brush("VxTextBrush") : Ui.Brush("VxRedBrush") } });
            if (!r.Success)
            {
                p.Children.Add(H("Error"));
                p.Children.Add(Line(r.Error ?? "Unknown error", "VxRedBrush"));
                p.Children.Add(Line("Source: " + r.SourcePath, "VxTextTertiaryBrush", 10));
                return;
            }
            if (r.IsModel)
            {
                p.Children.Add(H("Model"));
                p.Children.Add(Line("Name: " + r.Name));
                p.Children.Add(Line("Source: " + r.SourcePath, "VxTextTertiaryBrush", 10));

                p.Children.Add(H("Meshes"));
                p.Children.Add(Line(r.SubmeshNames.Count > 1 ? "Submeshes: " + r.SubmeshNames.Count + " (separate materials)" : r.SubmeshNames.Count == 1 ? "1 mesh imported" : "No meshes found"));
                for (int i = 0; i < r.SubmeshNames.Count && i < 60; i++) p.Children.Add(Item(r.SubmeshNames[i] + (i < r.SubmeshTextured.Count && r.SubmeshTextured[i] ? "  [textured]" : "")));
                if (r.SubmeshNames.Count > 60) p.Children.Add(Item("… " + (r.SubmeshNames.Count - 60) + " more"));

                p.Children.Add(H("Materials"));
                int textured = r.SubmeshTextured.Count(x => x);
                p.Children.Add(Line(r.MaterialFiles.Count > 0 ? "Created: " + r.MaterialFiles.Count + " material(s), " + textured + " with textures" : "No materials found (using default)"));
                foreach (var m in r.MaterialFiles.Take(60)) p.Children.Add(Item(Ui.ProjectRelative(m)));

                p.Children.Add(H("Textures"));
                var tex = r.CopiedTextures.Concat(r.ExtractedTextures).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                int total = tex.Count + r.MissingTextures.Count;
                p.Children.Add(Line(total == 0 ? "No textures referenced" : "Found: " + total + " texture reference(s)" + (r.ExtractedTextures.Count > 0 ? " — " + r.ExtractedTextures.Count + " extracted from the model" : "")));
                foreach (var t in r.CopiedTextures.Take(60)) p.Children.Add(Item("✓ " + Path.GetFileName(t), "VxGreenBrush"));
                foreach (var t in r.ExtractedTextures.Take(60)) p.Children.Add(Item("✓ " + Path.GetFileName(t) + "  (embedded)", "VxGreenBrush"));
                foreach (var t in r.MissingTextures) p.Children.Add(Item("⚠ " + Path.GetFileName(t) + " (not found)", "VxOrangeBrush"));

                if (r.AnimationClips.Count > 0 || r.Bones > 0)
                {
                    p.Children.Add(H("Animation"));
                    if (r.Bones > 0) p.Children.Add(Line("Skeleton: " + r.Bones + " bones"));
                    p.Children.Add(Line(r.AnimationClips.Count + " clip(s) extracted to animations/"));
                    foreach (var c in r.AnimationClips.Take(60)) p.Children.Add(Item(Path.GetFileName(c)));
                }
                if (r.CopiedFiles.Count > 0)
                {
                    p.Children.Add(H("Companion files"));
                    foreach (var c in r.CopiedFiles) p.Children.Add(Item(Path.GetFileName(c)));
                }
            }
            else
            {
                p.Children.Add(H(r.Kind));
                p.Children.Add(Line("Name: " + Path.GetFileName(r.TargetPath)));
                p.Children.Add(Line("Source: " + r.SourcePath, "VxTextTertiaryBrush", 10));
            }
            p.Children.Add(H("Asset Location"));
            p.Children.Add(Line(Ui.ProjectRelative(r.TargetPath) ?? "N/A"));
            if (r.AssetGuid != Guid.Empty) p.Children.Add(Line("GUID " + r.AssetGuid, "VxTextTertiaryBrush", 10));
            if (r.Warnings.Count > 0)
            {
                p.Children.Add(H("Warnings"));
                foreach (var w in r.Warnings) p.Children.Add(Item(w, "VxOrangeBrush"));
            }
        }

        private static void ShowInBrowser(ImportReport r)
        {
            if (r?.TargetPath == null) return;
            try
            {
                var ab = EditorCommands.Window?.AssetBrowser;
                if (ab == null) return;
                var dir = Path.GetDirectoryName(r.TargetPath);
                if (dir != null) ab.Navigate(dir);
                ab.SelectPath(r.TargetPath);
            }
            catch { }
        }

        internal static Editor.ECS.GameEntity AddToScene(List<ImportReport> models)
        {
            if (ProjectData.Current?.ActiveScene == null) { _ = Dialogs.Alert("Add to Scene", "No active scene. Please open a scene first."); return null; }
            Editor.ECS.GameEntity last = null;
            foreach (var m in models)
            {
                try
                {
                    // the Asset Browser's (undoable) placement when available, else the shared core placement
                    var e = VortexEditor.Services.AssetActions.AddToScene(m.TargetPath) ?? Editor.Core.Assets.AssetActions.AddModelToScene(m.TargetPath);
                    if (e != null) last = e;
                }
                catch (Exception ex) { EditorCommands.Fail("Add to Scene", ex); }
            }
            if (last != null) { SelectionService.Instance.Select(last); SceneRenderService.RuntimeDirty = true; EditorCommands.Toast("Added " + models.Count + " model(s) to the scene"); }
            return last;
        }

        // ================================================================== test hooks
        internal string TargetText { get => _target.Text; set => _target.Text = value; }
        internal IReadOnlyCollection<string> SelectedTags => _tags;
        internal void SetOption(string name, bool on)
        {
            switch (name)
            {
                case "copy": _copy.IsChecked = on; break;
                case "meta": _meta.IsChecked = on; break;
                case "related": _related.IsChecked = on; break;
                case "clips": _clips.IsChecked = on; break;
                case "overwrite": _overwrite.IsChecked = on; break;
            }
        }
    }
}
