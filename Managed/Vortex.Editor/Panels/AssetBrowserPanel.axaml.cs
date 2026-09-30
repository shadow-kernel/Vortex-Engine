using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;
using VortexEditor.Controls;
using VortexEditor.Shell;

namespace VortexEditor.Panels
{
    public sealed class AssetTile
    {
        public string Name { get; set; }
        public string Path { get; set; }          // absolute
        public bool IsFolder { get; set; }
        public bool IsParentLink { get; set; }
        public string Icon { get; set; }
        public IBrush IconBrush { get; set; }
        public Bitmap Thumbnail { get; set; }
        public bool HasThumbnail => Thumbnail != null;
        public string Tooltip { get; set; }
        public string Kind { get; set; }
    }

    /// <summary>
    /// Project asset browser: folder navigation (Explorer) plus type views (Models, Textures, …), import,
    /// create, open-in-editor, drag to the viewport / hierarchy / inspector, rename / delete / reveal.
    /// </summary>
    public partial class AssetBrowserPanel : UserControl
    {
        private readonly ObservableCollection<AssetTile> _tiles = new ObservableCollection<AssetTile>();
        private string _tab = "Explorer";
        private string _folder;
        private Point _pressPos; private AssetTile _pressTile; private bool _dragging;
        private ulong _auditionVoice = Editor.DllWrapper.VortexAudio.InvalidVoice;
        private static readonly Dictionary<string, string[]> TabPatterns = new Dictionary<string, string[]>
        {
            ["Models"] = new[] { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.dae", "*.3ds", "*.blend", "*.vmesh" },
            ["Textures"] = new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.hdr", "*.dds" },
            ["Materials"] = new[] { "*.vmat", "*.hlsl", "*.metal" },
            ["Scripts"] = new[] { "*.cs" },
            ["Audio"] = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac", "*.vsndc" },
            ["Prefabs"] = new[] { "*.ventity" },
            ["Scenes"] = new[] { "*.vscene" },
        };
        private static readonly HashSet<string> TextureExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

        public AssetBrowserPanel()
        {
            InitializeComponent();
            Grid.ItemsSource = _tiles;
            FileExplorerService.Instance.CurrentFolderChanged += (s, f) => Dispatcher.UIThread.Post(() => { if (_tab == "Explorer" && f != null) { _folder = f.FullPath; Refresh(); } });
            FileExplorerService.Instance.FolderContentsChanged += (s, e) => Dispatcher.UIThread.Post(Refresh);
            EditorSession.Instance.ProjectOpened += _ => Dispatcher.UIThread.Post(Reload);
            EditorSession.Instance.ProjectClosed += () => Dispatcher.UIThread.Post(Reload);
            DragDrop.SetAllowDrop(Grid, true);
            Grid.AddHandler(DragDrop.DragOverEvent, (s, e) => { e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None; });
            Grid.AddHandler(DragDrop.DropEvent, OnFilesDropped);
        }

        public string ProjectRoot => ProjectData.Current?.Path;

        public void Reload()
        {
            var root = ProjectRoot;
            if (root != null)
            {
                try { FileExplorerService.Instance.Initialize(root); } catch (Exception ex) { ConsoleService.Instance.LogWarning("File explorer: " + ex.Message); }
                _folder = Directory.Exists(Path.Combine(root, "Assets")) ? Path.Combine(root, "Assets") : root;
            }
            else _folder = null;
            Refresh();
        }

        public void Refresh()
        {
            _tiles.Clear();
            Breadcrumb.Children.Clear();
            var root = ProjectRoot;
            if (root == null) { EmptyHint.Text = "No project open"; EmptyHint.IsVisible = true; return; }
            string q = SearchBox.Text?.Trim();
            var list = new List<AssetTile>();
            if (_tab == "Explorer")
            {
                if (_folder == null || !Directory.Exists(_folder)) _folder = root;
                BuildBreadcrumb(root, _folder);
                if (!PathsEqual(_folder, root)) list.Add(new AssetTile { Name = "..", Path = Path.GetDirectoryName(_folder), IsFolder = true, IsParentLink = true, Icon = "ChevronLeft", IconBrush = Brush("VxTextTertiaryBrush") });
                try
                {
                    foreach (var d in Directory.GetDirectories(_folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        if (!Path.GetFileName(d).StartsWith(".") && Path.GetFileName(d) != "obj" && Path.GetFileName(d) != "bin") list.Add(Tile(d, true));
                    foreach (var f in Directory.GetFiles(_folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        if (!Path.GetFileName(f).StartsWith(".") && !f.EndsWith(".vmeta", StringComparison.OrdinalIgnoreCase)) list.Add(Tile(f, false));
                }
                catch (Exception ex) { ConsoleService.Instance.LogWarning("Browse: " + ex.Message); }
            }
            else
            {
                BuildBreadcrumb(root, null);
                var patterns = TabPatterns.TryGetValue(_tab, out var pp) ? pp : new[] { "*" };
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (f.Contains(Path.DirectorySeparatorChar + ".") || f.Contains("/obj/") || f.Contains("/bin/")) continue;
                        if (Inspector.PropertyRows.Matches(f, patterns)) list.Add(Tile(f, false));
                    }
                }
                catch { }
                list = list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (_tab == "Models") foreach (var prim in new[] { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" }) list.Insert(0, new AssetTile { Name = prim, Path = "Primitive:" + prim, Icon = prim == "Sphere" ? "Sphere" : "Cube", IconBrush = Brush("VxAccentBrush"), Kind = "Primitive", Tooltip = "Built-in primitive" });
            }
            if (!string.IsNullOrEmpty(q)) list = list.Where(t => t.IsParentLink || t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            foreach (var t in list) _tiles.Add(t);
            EmptyHint.Text = "Nothing here yet"; EmptyHint.IsVisible = _tiles.Count == 0;
            _ = LoadThumbnailsAsync(list);
        }

        private static bool PathsEqual(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
        private static IBrush Brush(string key) => Application.Current != null && Application.Current.TryFindResource(key, out var b) ? (IBrush)b : Brushes.Gray;

        private AssetTile Tile(string path, bool folder)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            return new AssetTile
            {
                Name = folder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path),
                Path = path, IsFolder = folder,
                Icon = folder ? "FolderFill" : AssetPickerDialog.IconFor(path),
                IconBrush = Brush(FileIconBrushConverter.BrushKeyFor(path, folder)),
                Kind = folder ? "Folder" : ext.TrimStart('.').ToUpperInvariant(),
                Tooltip = folder ? path : Path.GetFileName(path) + "\n" + path
            };
        }

        private async Task LoadThumbnailsAsync(List<AssetTile> tiles)
        {
            foreach (var t in tiles)
            {
                if (t.IsFolder || t.Path == null || !TextureExt.Contains(Path.GetExtension(t.Path))) continue;
                Bitmap bmp = null;
                try { bmp = await Task.Run(() => { using var fs = File.OpenRead(t.Path); return Bitmap.DecodeToWidth(fs, 120); }); } catch { }
                if (bmp == null) continue;
                t.Thumbnail = bmp;
                int i = _tiles.IndexOf(t);
                if (i >= 0) { _tiles[i] = t; }   // re-render the tile
            }
        }

        private void BuildBreadcrumb(string root, string folder)
        {
            void Add(string label, string target, bool last)
            {
                var b = new Button { Content = label, Classes = { "ghost" }, Padding = new Thickness(6, 1), FontWeight = last ? FontWeight.SemiBold : FontWeight.Normal };
                b.Click += (s, e) => { if (target != null) { _tab = "Explorer"; SetTab("Explorer"); Navigate(target); } };
                Breadcrumb.Children.Add(b);
                if (!last) Breadcrumb.Children.Add(new VxIcon { Icon = "ChevronRight", Width = 10, Height = 10, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Foreground = Brush("VxTextTertiaryBrush") });
            }
            if (folder == null) { Add(Path.GetFileName(root.TrimEnd('/', '\\')), root, false); Add(_tab, null, true); return; }
            string rel = Path.GetRelativePath(root, folder);
            var parts = rel == "." ? Array.Empty<string>() : rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Add(Path.GetFileName(root.TrimEnd('/', '\\')), root, parts.Length == 0);
            string acc = root;
            for (int i = 0; i < parts.Length; i++) { acc = Path.Combine(acc, parts[i]); Add(parts[i], acc, i == parts.Length - 1); }
        }

        private void SetTab(string tab)
        {
            _tab = tab;
            foreach (var child in Tabs.Children) if (child is RadioButton rb && (rb.Tag as string) == tab) rb.IsChecked = true;
        }

        public void Navigate(string folder)
        {
            _folder = folder;
            try { FileExplorerService.Instance.NavigateToPath(folder); } catch { }
            Refresh();
        }

        public void SelectPath(string path)
        {
            var t = _tiles.FirstOrDefault(x => PathsEqual(x.Path ?? "", path));
            if (t != null) { Grid.SelectedItem = t; Grid.ScrollIntoView(t); }
        }

        // ---------------------------------------------------------------- toolbar
        private void OnTabClick(object s, RoutedEventArgs e) { _tab = (s as RadioButton)?.Tag as string ?? "Explorer"; Refresh(); }
        private void OnSearchChanged(object s, TextChangedEventArgs e) => Refresh();
        private void OnRefreshClick(object s, RoutedEventArgs e) { try { AssetDatabase.Instance.Refresh(); } catch { } Refresh(); }
        private async void OnImportClick(object s, RoutedEventArgs e) => await EditorCommands.ImportAsset();

        private void OnCreateClick(object s, RoutedEventArgs e)
        {
            var m = new MenuFlyout();
            m.Items.Add(Item("Folder", () => { FileExplorerService.Instance.NavigateToPath(_folder ?? ProjectRoot); FileExplorerService.Instance.CreateFolder(); Refresh(); }));
            m.Items.Add(Item("Scene", async () => await CreateScene()));
            m.Items.Add(Item("Script", () => { var p = ScriptingService.CreateScript("NewBehaviour"); Refresh(); EditorCommands.OpenInIde(p); }));
            m.Items.Add(new Separator());
            var mat = new MenuItem { Header = "Material" };
            foreach (var t in new[] { "Standard", "Unlit", "Transparent" }) { var tt = t; mat.Items.Add(Item(t + " Material", async () => await CreateMaterialAsync(tt))); }
            m.Items.Add(mat);
            var sh = new MenuItem { Header = "Shader" };
            foreach (var t in new[] { "Standard", "Unlit", "Transparent" }) { var tt = t; sh.Items.Add(Item(t + " Shader", async () => await CreateShaderAsync(tt))); }
            m.Items.Add(sh);
            m.Items.Add(Item("Prefab (empty)", () => Run(() => AssetActions.CreateEmptyPrefab(), "Prefab created")));
            m.Items.Add(Item("UI Screen (.vui)", async () => await CreateNamed("UI Screen", "NewScreen", ".vui", Path.Combine("Assets", "UI"), p => { var r = AssetActions.CreateUiScreen(p); EditorCommands.Window?.OpenUiEditor(r); return r; })));
            m.Items.Add(Item("Animation Clip (.vanim)", async () => await CreateNamed("Animation Clip", "NewClip", ".vanim", Path.Combine("Assets", "Animations"), p => { var r = AssetActions.CreateAnimationClip(p); EditorCommands.Window?.OpenAnimationEditor(r); return r; })));
            m.Items.Add(Item("Sound Container (.vsndc)", () => Run(() => { var p = AssetActions.CreateSoundContainer(); EditorCommands.Window?.OpenSoundContainerEditor(p); return p; }, "Sound container created")));
            m.ShowAt(CreateButton);
        }

        private static MenuItem Item(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (s, e) => a(); return mi; }
        private static MenuItem Item(string h, Func<Task> a) { var mi = new MenuItem { Header = h }; mi.Click += (s, e) => _ = a(); return mi; }

        private void Run(Func<string> action, string toast)
        {
            try { var p = action(); if (!string.IsNullOrEmpty(p)) { EditorCommands.Toast(toast); if (Path.GetDirectoryName(p) is string d) Navigate(d); SelectPath(p); } }
            catch (Exception ex) { EditorCommands.Fail("Create", ex); }
        }

        private async Task CreateNamed(string what, string defaultName, string ext, string relDir, Func<string, string> create)
        {
            var root = ProjectRoot; if (root == null) return;
            var name = await Dialogs.Prompt("New " + what, "Name", defaultName, "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            string dir = _tab == "Explorer" && _folder != null ? _folder : Path.Combine(root, relDir);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name.Trim().EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name.Trim() : name.Trim() + ext);
            Run(() => create(path), what + " created");
        }

        public Task CreateMaterialAsync(string type) => CreateNamed(type + " Material", "New" + type + "Material", ".vmat", "Materials", p => AssetActions.CreateMaterial(p, type));
        public void CreateMaterial(string type) => _ = CreateMaterialAsync(type);
        public Task CreateShaderAsync(string type) => CreateNamed(type + " Shader", type == "Unlit" ? "NewUnlitShader" : "NewShader", OperatingSystem.IsWindows() ? ".hlsl" : ".metal", Path.Combine("Assets", "Shaders"), p => { var r = AssetActions.CreateShader(p, type); EditorCommands.OpenInIde(r); return r; });
        public void CreateShader(string type) => _ = CreateShaderAsync(type);

        private async Task CreateScene()
        {
            var project = ProjectData.Current; if (project == null) return;
            var name = await Dialogs.Prompt("New Scene", "Scene name", "New Scene", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            try { var scene = SceneService.Instance.CreateDefaultScene(project, name.Trim()); if (scene != null) { EditorSession.Instance.ActivateScene(scene); EditorCommands.Toast("Scene created"); } Refresh(); }
            catch (Exception ex) { EditorCommands.Fail("Create scene", ex); }
        }

        public void ImportFile(string sourcePath)
        {
            var root = ProjectRoot; if (root == null) return;
            try
            {
                string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (new[] { ".fbx", ".obj", ".gltf", ".glb", ".dae", ".3ds", ".blend" }.Contains(ext))
                {
                    string target = _tab == "Explorer" && _folder != null ? Path.GetRelativePath(Path.Combine(root, "Assets"), _folder) : "Models";
                    if (target.StartsWith("..")) target = "Models";
                    var imported = AssetActions.ImportModel(sourcePath, target);
                    EditorCommands.Toast("Imported " + Path.GetFileName(imported));
                    if (Path.GetDirectoryName(imported) is string d) Navigate(d);
                }
                else
                {
                    string targetDir = _tab == "Explorer" && _folder != null ? _folder : Path.Combine(root, "Assets", AssetActions.DefaultFolderFor(ext));
                    Directory.CreateDirectory(targetDir);
                    string dest = Path.Combine(targetDir, Path.GetFileName(sourcePath));
                    int n = 1; while (File.Exists(dest)) dest = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(sourcePath) + "_" + (n++) + ext);
                    File.Copy(sourcePath, dest);
                    EditorCommands.Toast("Imported " + Path.GetFileName(dest));
                }
                try { AssetDatabase.Instance.Refresh(); } catch { }
                Refresh();
            }
            catch (Exception ex) { EditorCommands.Fail("Import", ex); }
        }

        public void ExportSelected()
        {
            if (!(Grid.SelectedItem is AssetTile t) || t.IsFolder || t.Path == null || !File.Exists(t.Path)) { EditorCommands.Toast("Select an asset to export"); return; }
            _ = ExportAsync(t.Path);
        }
        private async Task ExportAsync(string path)
        {
            var top = TopLevel.GetTopLevel(this); if (top == null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions { Title = "Export Asset", SuggestedFileName = Path.GetFileName(path) });
            var dest = file?.TryGetLocalPath(); if (string.IsNullOrEmpty(dest)) return;
            try { File.Copy(path, dest, true); EditorCommands.Toast("Exported to " + dest); } catch (Exception ex) { EditorCommands.Fail("Export", ex); }
        }

        private void OnFilesDropped(object s, DragEventArgs e)
        {
            foreach (var f in e.Data.GetFiles() ?? Array.Empty<Avalonia.Platform.Storage.IStorageItem>()) { var p = f.TryGetLocalPath(); if (p != null && File.Exists(p)) ImportFile(p); }
        }

        // ---------------------------------------------------------------- tiles: open / context / drag
        private void OnDoubleTapped(object s, TappedEventArgs e) { if (Grid.SelectedItem is AssetTile t) Open(t); }

        private void Open(AssetTile t)
        {
            if (t.IsFolder) { Navigate(t.Path); return; }
            if (t.Kind == "Primitive") { EditorCommands.CreatePrimitive(Enum.Parse<PrimitiveType>(t.Path.Substring(10))); return; }
            string ext = Path.GetExtension(t.Path).ToLowerInvariant();
            switch (ext)
            {
                case ".vscene": OpenScene(t.Path); break;
                case ".vmat": EditorCommands.Window?.OpenMaterialEditor(t.Path); break;
                case ".cs": case ".hlsl": case ".metal": case ".json": case ".txt": case ".md": EditorCommands.OpenInIde(t.Path); break;
                case ".vui": EditorCommands.Window?.OpenUiEditor(t.Path); break;
                case ".vanim": EditorCommands.Window?.OpenAnimationEditor(t.Path); break;
                case ".vsndc": EditorCommands.Window?.OpenSoundContainerEditor(t.Path); break;
                case ".ventity": AddToScene(t); break;
                case ".wav": case ".mp3": case ".ogg": case ".flac": Audition(t.Path); break;
                default:
                    if (TabPatterns["Models"].Any(p => p.EndsWith(ext))) AddToScene(t);
                    else if (TabPatterns["Textures"].Any(p => p.EndsWith(ext))) EditorCommands.OpenUrl(t.Path);
                    else EditorCommands.RevealInFinder(t.Path);
                    break;
            }
        }

        private void OpenScene(string path)
        {
            var project = ProjectData.Current; if (project == null) return;
            var existing = project.Scenes?.FirstOrDefault(sc => sc != null && !string.IsNullOrEmpty(sc.FilePath) && PathsEqual(sc.FilePath, path));
            if (existing != null) { EditorSession.Instance.ActivateScene(existing); return; }
            try { var scene = SceneService.Instance.LoadScene(path); if (scene != null) { if (!project.Scenes.Contains(scene)) project.AddScene(scene); EditorSession.Instance.ActivateScene(scene); } }
            catch (Exception ex) { EditorCommands.Fail("Open scene", ex); }
        }

        private void AddToScene(AssetTile t)
        {
            try
            {
                var ent = t.Kind == "Primitive" ? ProjectData.Current?.ActiveScene?.CreatePrimitive(Enum.Parse<PrimitiveType>(t.Path.Substring(10)))
                        : Path.GetExtension(t.Path).Equals(".ventity", StringComparison.OrdinalIgnoreCase) ? AssetActions.PlacePrefab(t.Path)
                        : AssetActions.AddModelToScene(t.Path);
                if (ent != null) { SelectionService.Instance.Select(ent); SelectionService.Instance.RequestFocus(ent); SceneRenderService.RuntimeDirty = true; EditorCommands.Toast("Added " + ent.Name + " to the scene"); }
            }
            catch (Exception ex) { EditorCommands.Fail("Add to scene", ex); }
        }

        private void Audition(string path)
        {
            try
            {
                if (_auditionVoice != Editor.DllWrapper.VortexAudio.InvalidVoice) Editor.DllWrapper.VortexAudio.StopVoice(_auditionVoice);
                _auditionVoice = Editor.DllWrapper.VortexAudio.PlayVoice(path, 1f, 1f, 0f, false, 128);
                EditorCommands.Toast("Playing " + Path.GetFileName(path));
            }
            catch (Exception ex) { EditorCommands.Fail("Audition", ex); }
        }

        private void OnTilePressed(object s, PointerPressedEventArgs e)
        {
            var t = (s as Control)?.DataContext as AssetTile; if (t == null) return;
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsLeftButtonPressed) { _pressTile = t; _pressPos = e.GetPosition(this); _dragging = false; }
            if (props.IsRightButtonPressed)
            {
                Grid.SelectedItem = t;
                var m = new MenuFlyout();
                if (t.IsFolder) m.Items.Add(Item("Open", () => Navigate(t.Path)));
                else
                {
                    m.Items.Add(Item("Open", () => Open(t)));
                    string ext = Path.GetExtension(t.Path ?? "").ToLowerInvariant();
                    if (t.Kind == "Primitive" || ext == ".ventity" || TabPatterns["Models"].Any(p => p.EndsWith(ext))) m.Items.Add(Item("Add to Scene", () => AddToScene(t)));
                    if (TabPatterns["Models"].Any(p => p.EndsWith(ext)) && t.Kind != "Primitive") m.Items.Add(Item("Create Prefab from Model", () => Run(() => PrefabService.Instance.CreatePrefabFromModel(t.Path), "Prefab created")));
                    if (ext == ".cs") m.Items.Add(Item("Assign to Selected Entity", () => { var en = SelectionService.Instance.SelectedEntity; if (en != null) { en.AddComponent(new Editor.ECS.Components.Scripting.Script(en, ScriptingService.MakeRelative(ProjectRoot, t.Path))); EditorCommands.Window?.Inspector?.Refresh(); } }));
                    if (ext == ".vmat") m.Items.Add(Item("Assign to Selected Entity", () => { var en = SelectionService.Instance.SelectedEntity; var mr = en?.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>(); if (mr != null) { mr.MaterialPath = Path.GetRelativePath(ProjectRoot, t.Path).Replace('\\', '/'); SceneRenderService.RuntimeDirty = true; EditorCommands.Window?.Inspector?.Refresh(); } }));
                    if (ext == ".wav" || ext == ".mp3" || ext == ".ogg" || ext == ".flac") { m.Items.Add(Item("Play", () => Audition(t.Path))); m.Items.Add(Item("Stop", () => { try { Editor.DllWrapper.VortexAudio.StopVoice(_auditionVoice); _auditionVoice = Editor.DllWrapper.VortexAudio.InvalidVoice; } catch { } })); }
                }
                if (t.Kind != "Primitive" && !t.IsParentLink)
                {
                    m.Items.Add(new Separator());
                    m.Items.Add(Item("Rename…", async () => { var n = await Dialogs.Prompt("Rename", "New name", Path.GetFileName(t.Path), "Rename"); if (!string.IsNullOrWhiteSpace(n) && n != Path.GetFileName(t.Path)) { try { string dest = Path.Combine(Path.GetDirectoryName(t.Path), n.Trim()); if (t.IsFolder) Directory.Move(t.Path, dest); else File.Move(t.Path, dest); Refresh(); } catch (Exception ex) { EditorCommands.Fail("Rename", ex); } } }));
                    m.Items.Add(Item("Delete", async () => { if (await Dialogs.Confirm("Delete \"" + Path.GetFileName(t.Path) + "\"?", "This cannot be undone.", "Delete", "Cancel", destructive: true)) { try { if (t.IsFolder) Directory.Delete(t.Path, true); else File.Delete(t.Path); if (!t.IsFolder && Path.GetExtension(t.Path).Equals(".ventity", StringComparison.OrdinalIgnoreCase)) PrefabService.Instance.OnPrefabDeleted(t.Path); Refresh(); } catch (Exception ex) { EditorCommands.Fail("Delete", ex); } } }));
                    m.Items.Add(Item("Reveal in Finder", () => EditorCommands.RevealInFinder(t.Path)));
                }
                m.ShowAt(s as Control, true);
                e.Handled = true;
            }
        }

        private async void OnTileMoved(object s, PointerEventArgs e)
        {
            if (_pressTile == null || _dragging || _pressTile.IsFolder) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressTile = null; return; }
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPos.X) < 6 && Math.Abs(p.Y - _pressPos.Y) < 6) return;
            _dragging = true;
            var data = new DataObject();
            data.Set("vortex/asset", _pressTile.Path);
            try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Link); } catch { }
            _dragging = false; _pressTile = null;
        }

        private void OnKeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Return && Grid.SelectedItem is AssetTile t) { Open(t); e.Handled = true; }
            else if (e.Key == Key.Back && _tab == "Explorer" && _folder != null && ProjectRoot != null && !PathsEqual(_folder, ProjectRoot)) { Navigate(Path.GetDirectoryName(_folder)); e.Handled = true; }
        }
    }
}
