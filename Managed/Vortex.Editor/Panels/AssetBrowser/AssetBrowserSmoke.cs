using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using VortexEditor.Services;
using VortexEditor.Shell;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>Smoke checks of the Asset Browser package (browser, file tree, AssetActions) — run by
    /// <c>VORTEX_SMOKE_FULL=1 … --smoke=N</c> after the built-in checks. They only touch the project copy the editor
    /// was started on and clean up after themselves.</summary>
    internal static class AssetBrowserSmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            Add("asset browser: lists the project's Assets folder", ListsAssets);
            Add("asset browser: thumbnails arrive (model, image, material, prefab)", Thumbnails);
            Add("asset actions: AddToScene model/primitive/prefab = one undo step each", AddToSceneUndo);
            Add("asset actions: multi-submesh model -> container + locked parts, parent-local placement", AddToSceneContainer);
            Add("asset browser: double-click routing (plain / Shift editor / Cmd+Ctrl preview)", DoubleClickRouting);
            Add("asset browser: create / rename / move (+.vmeta) / duplicate / delete, all undoable", FileOps);
            Add("asset browser: search + type tabs + primitives + sort + list view", FiltersAndViews);
            Add("file tree: synced with the browser both ways", TreeSync);
            Add("asset watcher: external file changes refresh the browser", Watcher);
            Add("asset browser: real Cmd+D key duplicates the asset (not the entity)", KeyboardDuplicate);
            Add("asset browser: real keys (F2 / Enter rename, Esc, Enter opens folder, Backspace up, Cmd+Backspace deletes)", RealKeys);
            Add("asset browser: real mouse double-clicks (plain / Shift / Cmd / Ctrl)", RealDoubleClicks);
            Add("asset browser: context menus carry every action (+ Edit Tags / Stress Test windows)", ContextMenus);
            Add("asset browser: create folder / material / shader / script / UI screen / clip / sound container / prefab / scene", CreateAssets);
            Add("asset browser: tag filter", TagFilter);
            Add("asset browser: drag over / drop onto a folder tile moves (undoable)", DropOnFolder);
            Add("asset browser: audition on select, back/forward, breadcrumb, tile size, reimport", Misc);
            Add("asset browser: Finder drop -> import dialog -> imported file shown + selected", OsDropImports);
            Add("asset browser: real Cmd-click / Shift-click selection, rubber band, arrow keys", RealSelection);
            Add("file tree: inline rename, drop onto a folder, drag a folder, delete + undo", TreeOps);
            Add("asset browser: screenshots with thumbnails", Screenshots);
        }

        private static int _diag;

        /// <summary>Register a check; VORTEX_AB_DIAG=1 also captures the main window after it (diag_NN.png),
        /// VORTEX_AB_ONLY=RealKeys,TreeOps runs just those checks of this package.</summary>
        private static void Add(string name, Func<Task<bool>> check)
        {
            string only = Environment.GetEnvironmentVariable("VORTEX_AB_ONLY");
            if (!string.IsNullOrWhiteSpace(only) && !only.Split(',').Any(o => string.Equals(o.Trim(), check.Method.Name, StringComparison.OrdinalIgnoreCase))) return;
            SmokeRegistry.Add(name, async () =>
            {
                bool ok = await check();
                if (Environment.GetEnvironmentVariable("VORTEX_AB_DIAG") == "1")
                {
                    await SmokeRegistry.Settle(400);
                    SmokeRegistry.Capture(EditorCommands.Window, "diag_" + (++_diag).ToString("00") + ".png");
                }
                return ok;
            });
        }

        // ------------------------------------------------------------------ helpers
        private static AssetBrowserPanel B => AssetBrowserPanel.Current;
        private static string Root => ProjectData.Current?.Path;
        private static string Assets => Root == null ? null : Path.Combine(Root, "Assets");
        private static void Log(string s) => ConsoleService.Instance.Log("  asset browser smoke: " + s);

        private static async Task<bool> WaitFor(Func<bool> cond, int timeoutMs = 5000)
        {
            for (int t = 0; t < timeoutMs; t += 50) { if (cond()) return true; await Task.Delay(50); }
            return cond();
        }

        private static List<string> AssetFiles(Func<string, bool> filter)
            => Directory.Exists(Assets) ? AssetBrowserPanel.Walk(Assets).Where(x => !x.isDir && filter(x.path)).Select(x => x.path).ToList() : new List<string>();

        private static string Ext(string p) => Path.GetExtension(p).ToLowerInvariant();
        private static string FirstModel() => AssetFiles(f => Ext(f) == ".glb" || Ext(f) == ".gltf").OrderBy(f => new FileInfo(f).Length).FirstOrDefault();
        private static string FirstImage() => AssetFiles(f => Ext(f) == ".png" || Ext(f) == ".jpg").OrderBy(f => new FileInfo(f).Length).FirstOrDefault();

        private static IReadOnlyList<Window> Windows => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? (IReadOnlyList<Window>)Array.Empty<Window>();

        /// <summary>Run <paramref name="open"/>, return the window it opened (null = none) — and close it.</summary>
        private static async Task<Window> OpenedWindow(Func<Task> open)
        {
            var before = Windows.ToList();
            await open();
            Window w = null;
            await WaitFor(() => (w = Windows.FirstOrDefault(x => !before.Contains(x))) != null, 3000);
            if (w != null) { await SmokeRegistry.Settle(250); try { w.Close(); } catch { } await SmokeRegistry.Settle(100); }
            return w;
        }

        private static string Describe(Window w) => w == null ? "(none)" : w.GetType().Name + " \"" + w.Title + "\"";

        private static AssetTile TileOf(string path) => B?.Tiles.FirstOrDefault(t => AssetFileOps.PathsEqual(t.FullPath, path));

        /// <summary>Show a file's folder in the Explorer tab and return its (selected) tile.</summary>
        private static async Task<AssetTile> RevealTile(string path)
        {
            B.ClearFilters(false);
            B.Reveal(path);
            AssetTile t = null;
            await WaitFor(() => (t = TileOf(path)) != null, 3000);
            return t;
        }

        // ------------------------------------------------------------------ checks
        private static async Task<bool> ListsAssets()
        {
            if (B == null || Assets == null || !Directory.Exists(Assets)) return false;
            B.ClearFilters(false);
            B.Navigate(Assets);
            await SmokeRegistry.Settle(300);
            var want = AssetBrowserPanel.SafeDirs(Assets).Concat(AssetBrowserPanel.SafeFiles(Assets)).Select(AssetTile.Normalize).ToList();
            var have = new HashSet<string>(B.Tiles.Select(t => t.FullPath), StringComparer.OrdinalIgnoreCase);
            var missing = want.Where(w => !have.Contains(w)).ToList();
            bool parent = B.Tiles.Any(t => t.IsParentLink && AssetFileOps.PathsEqual(t.FullPath, Root));
            bool folderFirst = B.Tiles.SkipWhile(t => t.IsParentLink).TakeWhile(t => t.IsFolder).Count() == want.Count(Directory.Exists);
            Log("Assets lists " + B.Tiles.Count + " tiles (" + want.Count + " expected), '..' = " + parent + ", folders first = " + folderFirst + (missing.Count > 0 ? ", missing: " + string.Join(", ", missing.Take(5)) : ""));
            return want.Count > 0 && missing.Count == 0 && parent && folderFirst && AssetFileOps.PathsEqual(B.CurrentFolder, Assets);
        }

        private static async Task<bool> Thumbnails()
        {
            if (B == null) return false;
            var targets = new List<(string what, string path)>
            {
                ("model", FirstModel()), ("image", FirstImage()),
                ("material", AssetFiles(f => Ext(f) == ".vmat").FirstOrDefault()),
                ("prefab", AssetFiles(f => Ext(f) == ".ventity").FirstOrDefault()),
            };
            bool ok = true;
            foreach (var (what, path) in targets)
            {
                if (path == null) { Log("no " + what + " in the project — skipped"); continue; }
                var t = await RevealTile(path);
                if (t == null) { Log(what + ": tile not listed"); ok = false; continue; }
                bool arrived = await WaitFor(() => t.Thumbnail != null, 4000);
                if (!arrived) { B.EnsureThumbnail(t, force: true); arrived = await WaitFor(() => t.Thumbnail != null, 30000); Log(what + ": thumbnail requested explicitly (tile not realized in view?)"); }
                Log(what + " " + AssetFileOps.ToRelative(path) + " -> thumbnail " + (arrived ? t.Thumbnail.PixelSize.Width + "px" : "MISSING"));
                ok &= arrived;
            }
            return ok;
        }

        private static async Task<bool> AddToSceneUndo()
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) return false;
            bool ok = true;
            foreach (var path in new[] { FirstModel(), "Primitive:Cube", AssetFiles(f => Ext(f) == ".ventity").FirstOrDefault() })
            {
                if (path == null) continue;
                int count = scene.Entities.Count;
                var top0 = UndoRedoManager.Instance.GetUndoHistory().FirstOrDefault();
                var cam = Editor.Core.Viewport.EditorViewportSession.Main?.Camera ?? EditorCameraController.Instance;
                var e = AssetActions.AddToScene(path);
                await SmokeRegistry.Settle(150);
                bool added = e != null && scene.Entities.Contains(e) && scene.Entities.Count == count + 1;
                // one new undo step on top (a single Undo below must remove the whole placement)
                var history = UndoRedoManager.Instance.GetUndoHistory();
                bool oneStep = history.Count > 0 && !ReferenceEquals(history[0], top0) && (history.Count < 2 || ReferenceEquals(history[1], top0) || top0 == null || UndoRedoManager.Instance.UndoCount >= UndoRedoManager.Instance.MaxUndoStackSize);
                bool selected = ReferenceEquals(SelectionService.Instance.SelectedEntity, e);
                var p = e?.Transform.LocalPosition ?? default;
                double yaw = cam.Yaw * Math.PI / 180, pitch = cam.Pitch * Math.PI / 180;
                double fx = Math.Sin(yaw) * Math.Cos(pitch), fy = -Math.Sin(pitch), fz = Math.Cos(yaw) * Math.Cos(pitch);
                double ahead = (p.X - cam.PositionX) * fx + (p.Y - cam.PositionY) * fy + (p.Z - cam.PositionZ) * fz;
                EditorCommands.Undo();
                bool undone = e != null && !scene.Entities.Contains(e) && scene.Entities.Count == count;
                EditorCommands.Redo();
                bool redone = e != null && scene.Entities.Contains(e);
                EditorCommands.Undo();
                bool clean = scene.Entities.Count == count;
                string extra = e == null ? "" : path.EndsWith(".ventity", StringComparison.OrdinalIgnoreCase) ? " prefabInstance=" + e.IsPrefabInstance
                    : AssetActions.IsPrimitive(path) ? " mesh=" + e.GetComponent<MeshRenderer>()?.MeshPath : " parts=" + e.Children.Count + " animator=" + (e.GetComponent<Editor.ECS.Components.Animation.Animator>() != null);
                Log("AddToScene(" + Path.GetFileName(path) + "): added=" + added + " oneUndoStep=" + oneStep + " selected=" + selected + " ahead=" + ahead.ToString("0.0") + "m undo=" + undone + " redo=" + redone + " clean=" + clean + extra);
                ok &= added && oneStep && selected && ahead > 0.5 && undone && redone && clean;
                if (path.EndsWith(".ventity", StringComparison.OrdinalIgnoreCase)) ok &= e != null && e.IsPrefabInstance;
                if (AssetActions.IsPrimitive(path)) ok &= e?.GetComponent<MeshRenderer>()?.MeshPath == "Primitive:Cube";
            }
            return ok;
        }

        private static async Task<bool> AddToSceneContainer()
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) return false;
            string multi = AssetFiles(f => Ext(f) == ".glb" || Ext(f) == ".gltf").OrderBy(f => new FileInfo(f).Length)
                .FirstOrDefault(f => { try { return VortexAPI.GetSubmeshCount(f) > 1; } catch { return false; } });
            bool ok = true;
            int count = scene.Entities.Count;
            if (multi != null)
            {
                var e = AssetActions.AddToScene(multi, new Vector3(4, 0, 2));
                bool container = e != null && e.GetComponent<MeshRenderer>() == null && e.Children.Count > 1 && e.Children.All(c => c.IsLockedToParent && (c.GetComponent<MeshRenderer>()?.MeshPath ?? "").Contains("#submesh"));
                bool atPos = e != null && Math.Abs(e.Transform.LocalPosition.X - 4) < 0.01 && Math.Abs(e.Transform.LocalPosition.Z - 2) < 0.01;
                int vmats = e?.Children.Count(c => !string.IsNullOrEmpty(c.GetComponent<MeshRenderer>()?.MaterialPath)) ?? 0;
                Log("multi-submesh " + AssetFileOps.ToRelative(multi) + ": container=" + container + " parts=" + (e?.Children.Count ?? 0) + " withVmat=" + vmats + " atPosition=" + atPos);
                EditorCommands.Undo();
                ok &= container && atPos && scene.Entities.Count == count;
            }
            else Log("no multi-submesh model in the project — container check skipped");

            // under a rotated + scaled parent: the child keeps the requested WORLD position
            var parent = new GameEntity(scene, "SmokeParent");
            parent.Transform.LocalPosition = new Vector3(10, 1, -3);
            parent.Transform.LocalRotation = new Vector3(0, 90, 0);
            parent.Transform.LocalScale = new Vector3(2, 2, 2);
            scene.AddEntity(parent);
            var child = AssetActions.AddToScene("Primitive:Sphere", new Vector3(10, 1, 1), parent);
            bool underParent = child != null && ReferenceEquals(child.Parent, parent) && parent.Children.Contains(child);
            var w = child == null ? default : WorldPos(child);
            bool worldKept = child != null && Math.Abs(w.X - 10) < 0.01 && Math.Abs(w.Y - 1) < 0.01 && Math.Abs(w.Z - 1) < 0.01;
            EditorCommands.Undo();   // the child
            bool childGone = child != null && !parent.Children.Contains(child);
            EditorCommands.Undo();   // the parent
            Log("child under rotated/scaled parent: attached=" + underParent + " world=(" + w.X.ToString("0.00") + "," + w.Y.ToString("0.00") + "," + w.Z.ToString("0.00") + ") kept=" + worldKept + " undo=" + childGone);
            await SmokeRegistry.Settle(100);
            return ok && underParent && worldKept && childGone && scene.Entities.Count == count;
        }

        private static Vector3 WorldPos(GameEntity e)
        {
            var m = System.Numerics.Matrix4x4.Identity;
            var chain = new List<GameEntity>();
            for (var x = e; x != null; x = x.Parent) chain.Add(x);
            System.Numerics.Vector3 p = System.Numerics.Vector3.Zero;
            // local point (0,0,0) of e through every ancestor: p = p * local(e) * local(parent) ...
            foreach (var x in chain)
            {
                var t = x.Transform;
                double rx = t.LocalRotation.X * Math.PI / 180, ry = t.LocalRotation.Y * Math.PI / 180, rz = t.LocalRotation.Z * Math.PI / 180;
                float cx = (float)Math.Cos(rx), sx = (float)Math.Sin(rx), cy = (float)Math.Cos(ry), sy = (float)Math.Sin(ry), cz = (float)Math.Cos(rz), sz = (float)Math.Sin(rz);
                var s = t.LocalScale;
                var local = new System.Numerics.Matrix4x4(
                    s.X * (cz * cy + sz * sx * sy), s.X * (sz * cx), s.X * (-cz * sy + sz * sx * cy), 0,
                    s.Y * (-sz * cy + cz * sx * sy), s.Y * (cz * cx), s.Y * (sz * sy + cz * sx * cy), 0,
                    s.Z * (cx * sy), s.Z * (-sx), s.Z * (cx * cy), 0,
                    t.LocalPosition.X, t.LocalPosition.Y, t.LocalPosition.Z, 1);
                p = System.Numerics.Vector3.Transform(p, local);
            }
            return new Vector3(p.X, p.Y, p.Z);
        }

        private static async Task<bool> DoubleClickRouting()
        {
            if (B == null) return false;
            bool ok = true;
            string model = FirstModel();
            if (model != null)
            {
                var t = await RevealTile(model);
                var ed = await OpenedWindow(() => B.OpenTile(t, KeyModifiers.Shift));
                var pv = await OpenedWindow(() => B.OpenTile(t, KeyModifiers.Meta));
                var pv2 = await OpenedWindow(() => B.OpenTile(t, KeyModifiers.Control));
                bool edOk = ed != null && (ed.GetType().Name.Contains("ModelEditor") || (ed.Title ?? "").Contains("Model Editor"));
                bool pvOk = pv != null && (pv.GetType().Name.Contains("ModelViewer") || (pv.Title ?? "").Contains("Viewer") || (pv.Title ?? "").Contains("Preview"));
                bool pv2Ok = pv2 != null && pv2.GetType() == pv?.GetType();
                Log("model: Shift -> " + Describe(ed) + ", Cmd -> " + Describe(pv) + ", Ctrl -> " + Describe(pv2));
                ok &= edOk && pvOk && pv2Ok;
                // plain double-click on a model adds it to the scene (undo it again)
                var scene = ProjectData.Current?.ActiveScene;
                int n = scene?.Entities.Count ?? 0;
                await B.OpenTile(t, KeyModifiers.None);
                bool placed = scene != null && scene.Entities.Count == n + 1;
                if (placed) EditorCommands.Undo();
                Log("model: plain double-click adds to scene = " + placed);
                ok &= placed;
            }
            var plain = new List<(string ext, string type)> { (".vmat", "MaterialEditor"), (".vsndc", "SoundContainerEditor"), (".vanim", "AnimationEditor"), (".png", "TextureEditor") };
            foreach (var (ext, type) in plain)
            {
                string f = AssetFiles(p => Ext(p) == ext).FirstOrDefault();
                if (f == null) { Log("no " + ext + " — skipped"); continue; }
                var t = await RevealTile(f);
                var w = await OpenedWindow(() => B.OpenTile(t, KeyModifiers.None));
                bool match = w != null && (w.GetType().Name.Contains(type) || (w.Title ?? "").Replace(" ", "").Contains(type.Replace("Editor", "")));
                Log(ext + ": plain -> " + Describe(w));
                ok &= match;
            }
            string prefab = AssetFiles(p => Ext(p) == ".ventity").FirstOrDefault();
            if (prefab != null)
            {
                var t = await RevealTile(prefab);
                var w = await OpenedWindow(() => B.OpenTile(t, KeyModifiers.Shift));
                Log("prefab: Shift -> " + Describe(w));
                ok &= w != null && (w.GetType().Name.Contains("Prefab") || (w.Title ?? "").Contains("Prefab"));
            }
            // folders navigate; ".." goes up
            B.Navigate(Assets);
            await SmokeRegistry.Settle(150);
            var folder = B.Tiles.FirstOrDefault(x => x.Kind == AssetKind.Folder);
            if (folder != null)
            {
                await B.OpenTile(folder, KeyModifiers.None);
                bool inside = AssetFileOps.PathsEqual(B.CurrentFolder, folder.FullPath);
                var up = B.Tiles.FirstOrDefault(x => x.IsParentLink);
                if (up != null) await B.OpenTile(up, KeyModifiers.None);
                bool back = AssetFileOps.PathsEqual(B.CurrentFolder, Assets);
                Log("folder double-click navigates = " + inside + ", '..' goes up = " + back);
                ok &= inside && back;
            }
            return ok;
        }

        private static async Task<bool> FileOps()
        {
            if (B == null || Assets == null) return false;
            B.ClearFilters(false);
            B.Navigate(Assets);
            await SmokeRegistry.Settle(150);
            bool ok = true;
            string created = B.CreateFolder(Assets, rename: false);
            await SmokeRegistry.Settle(150);
            ok &= created != null && Directory.Exists(created) && TileOf(created) != null;
            string temp = Path.Combine(Assets, "SmokeTemp_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string renamed = null;
            try
            {
                renamed = AssetFileOps.Rename(created, Path.GetFileName(temp));
                B.Refresh();
                bool renameOk = Directory.Exists(temp) && !Directory.Exists(created) && TileOf(temp) != null;
                EditorCommands.Undo();
                bool renameUndo = Directory.Exists(created) && !Directory.Exists(temp);
                EditorCommands.Redo();
                Log("create folder = " + (created != null) + ", rename = " + renameOk + ", undo rename = " + renameUndo + ", redo = " + Directory.Exists(temp));
                ok &= renameOk && renameUndo && Directory.Exists(temp);

                // move a file + its .vmeta sidecar into the folder (undoable)
                string file = Path.Combine(Assets, "smoke_move_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
                File.WriteAllText(file, "smoke");
                File.WriteAllText(file + ".vmeta", "{}");
                var moved = B.MoveInto(new[] { file }, temp);
                string dst = Path.Combine(temp, Path.GetFileName(file));
                bool moveOk = moved.Count == 1 && File.Exists(dst) && File.Exists(dst + ".vmeta") && !File.Exists(file) && !File.Exists(file + ".vmeta");
                EditorCommands.Undo();
                bool moveUndo = File.Exists(file) && File.Exists(file + ".vmeta") && !File.Exists(dst);
                EditorCommands.Redo();
                Log("move file+.vmeta into folder = " + moveOk + ", undo = " + moveUndo + ", redo = " + File.Exists(dst + ".vmeta"));
                ok &= moveOk && moveUndo && File.Exists(dst);

                // duplicate the folder (copies get fresh GUIDs: no .vmeta inside the copy)
                B.Refresh();
                B.SelectOnly(TileOf(temp));
                var dups = B.DuplicateSelected();
                string dup = dups.FirstOrDefault();
                string dupMeta = dup == null ? null : Path.Combine(dup, Path.GetFileName(dst)) + ".vmeta";
                // the copy must not share the original's asset GUID: its sidecar is dropped and re-created fresh
                bool freshGuid = dupMeta == null || !File.Exists(dupMeta) || File.ReadAllText(dupMeta) != File.ReadAllText(dst + ".vmeta");
                bool dupOk = dup != null && Directory.Exists(dup) && File.Exists(Path.Combine(dup, Path.GetFileName(dst))) && freshGuid && Path.GetFileName(dup).Contains(" - Copy");
                EditorCommands.Undo();
                bool dupUndo = dup != null && !Directory.Exists(dup);
                Log("duplicate folder = " + dupOk + " (" + Path.GetFileName(dup) + "), undo = " + dupUndo);
                ok &= dupOk && dupUndo;

                // delete (macOS: to the Trash) and undo — the undo takes it back out of the Trash
                var deleted = B.DeletePaths(new List<string> { temp });
                bool delOk = deleted.Count == 1 && !Directory.Exists(temp) && TileOf(temp) == null;
                EditorCommands.Undo();
                bool delUndo = Directory.Exists(temp) && File.Exists(dst) && File.Exists(dst + ".vmeta");
                Log("delete folder = " + delOk + " (" + (MacTrash.IsSupported ? "Trash" : "in-memory") + "), undo restores it = " + delUndo);
                ok &= delOk && delUndo;
            }
            finally
            {
                foreach (var d in new[] { temp, created }) { try { if (d != null && Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
                foreach (var f in Directory.GetFiles(Assets, "smoke_move_*")) { try { File.Delete(f); } catch { } }
                B.Refresh();
            }
            return ok;
        }

        private static async Task<bool> FiltersAndViews()
        {
            if (B == null) return false;
            bool ok = true;
            B.Navigate(Assets);
            // search spans sub-folders (Finder-style)
            string image = FirstImage();
            if (image != null)
            {
                string q = Path.GetFileNameWithoutExtension(image);
                B.SetSearch(q);
                await SmokeRegistry.Settle(100);
                bool found = TileOf(image) != null && B.Tiles.All(t => t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                Log("search \"" + q + "\" from Assets finds the image in a sub-folder = " + found + " (" + B.Tiles.Count + " results)");
                ok &= found;
                B.ClearFilters();
            }
            // type tabs
            var expect = new Dictionary<string, Func<string, bool>>
            {
                ["Models"] = f => AssetKinds.Is(Ext(f), AssetKinds.ModelExt),
                ["Textures"] = f => AssetKinds.Is(Ext(f), AssetKinds.TextureExt),
                ["Materials"] = f => Ext(f) == ".vmat",
                ["Audio"] = f => AssetKinds.Is(Ext(f), AssetKinds.AudioExt) || Ext(f) == ".vsndc",
                ["Prefabs"] = f => Ext(f) == ".ventity",
                ["Scenes"] = f => Ext(f) == ".vscene",
            };
            foreach (var kv in expect)
            {
                B.SetTab(kv.Key);
                int want = AssetFiles(kv.Value).Count;
                int have = B.Tiles.Count(t => t.IsFileSystemItem);
                if (want != have) { Log("tab " + kv.Key + ": " + have + " tiles, expected " + want); ok = false; }
            }
            B.SetTab("Scripts");
            int scripts = B.Tiles.Count;
            Log("tabs ok = " + ok + " (Scripts lists " + scripts + ")");
            B.SetTab("Meshes");
            var prims = B.Tiles.Where(t => t.Kind == AssetKind.Primitive).Select(t => t.Name).ToList();
            bool primOk = prims.SequenceEqual(AssetKinds.Primitives);
            var cube = B.Tiles.FirstOrDefault(t => t.Name == "Cube" && t.Kind == AssetKind.Primitive);
            bool cubeThumb = cube != null && await WaitFor(() => cube.Thumbnail != null, 10000);
            Log("Meshes tab primitives = [" + string.Join(", ", prims) + "], cube thumbnail = " + cubeThumb);
            ok &= primOk && cubeThumb;
            // sort: size descending puts the biggest model first (after primitives)
            B.SetTab("Models");
            B.SetSort("Size", true);
            var sizes = B.Tiles.Where(t => t.IsFileSystemItem).Select(t => t.Size).ToList();
            bool sorted = sizes.Zip(sizes.Skip(1), (a, b) => a >= b).All(x => x);
            B.SetSort("Name", false);
            var names = B.Tiles.Where(t => t.IsFileSystemItem).Select(t => t.Name).ToList();
            bool byName = names.Zip(names.Skip(1), (a, b) => AssetBrowserPanel.NaturalCompare(a, b) <= 0).All(x => x);
            Log("sort by size desc = " + sorted + ", by name = " + byName);
            ok &= sorted && byName;
            // list view round trip keeps the tiles
            int count = B.Tiles.Count;
            B.ApplyViewMode(true, save: false);
            await SmokeRegistry.Settle(300);
            bool list = B.IsListMode && B.Tiles.Count == count;
            await WaitFor(() => B.Tiles.Where(IsVisible).All(t => t.Thumbnail != null || !HasPreview(t)), 15000);
            SmokeRegistry.Capture(EditorCommands.Window, "assetbrowser_list.png");
            B.ApplyViewMode(false, save: false);
            Log("list view = " + list);
            ok &= list;
            B.SetTab("Explorer");
            return ok;
        }

        private static async Task<bool> TreeSync()
        {
            var tree = EditorCommands.Window?.FileTree;
            if (B == null || tree == null || Assets == null) return false;
            string models = Path.Combine(Assets, "Models"), textures = Path.Combine(Assets, "Textures");
            if (!Directory.Exists(models) || !Directory.Exists(textures))
            {
                var dirs = AssetBrowserPanel.SafeDirs(Assets).ToList();
                if (dirs.Count < 2) return true;
                models = dirs[0]; textures = dirs[1];
            }
            B.Navigate(models);
            bool treeFollows = await WaitFor(() => tree.SelectedNode != null && AssetFileOps.PathsEqual(tree.SelectedNode.FullPath, models), 2000);
            var node = tree.RootNode?.Find(textures, expandAlongTheWay: true);
            if (node != null) tree.Tree.SelectedItem = node;   // as if clicked
            bool browserFollows = await WaitFor(() => AssetFileOps.PathsEqual(B.CurrentFolder, textures) && B.CurrentTab == "Explorer", 2000);
            // a new folder appears in the tree by itself (watcher), expansion kept
            string temp = Path.Combine(textures, "SmokeTreeTemp");
            Directory.CreateDirectory(temp);
            bool appears = await WaitFor(() => node != null && node.Children.Any(c => AssetFileOps.PathsEqual(c.FullPath, temp)), 4000);
            Directory.Delete(temp);
            bool vanishes = await WaitFor(() => node != null && !node.Children.Any(c => AssetFileOps.PathsEqual(c.FullPath, temp)), 4000);
            var menu = tree.BuildContextMenu(node ?? tree.RootNode);
            Log("browser -> tree = " + treeFollows + ", tree -> browser = " + browserFollows + ", new folder appears = " + appears + ", removed folder vanishes = " + vanishes + ", menu items = " + menu.Items.Count);
            return treeFollows && browserFollows && appears && vanishes;
        }

        private static async Task<bool> Watcher()
        {
            if (B == null || Assets == null) return false;
            B.ClearFilters(false);
            B.Navigate(Assets);
            string file = Path.Combine(Assets, "SmokeWatch_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
            File.WriteAllText(file, "external change");
            bool appeared = await WaitFor(() => TileOf(file) != null, 5000);
            File.Delete(file);
            bool vanished = await WaitFor(() => TileOf(file) == null, 5000);
            Log("file written from outside appears = " + appeared + ", deleted from outside vanishes = " + vanished + " (watching " + AssetWatcher.WatchedFolder + ")");
            return appeared && vanished;
        }

        private static async Task<bool> KeyboardDuplicate()
        {
            if (B == null || Assets == null) return false;
            if (!OperatingSystem.IsMacOS()) return true;
            var scene = ProjectData.Current?.ActiveScene;
            string file = Path.Combine(Assets, "smoke_key_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
            File.WriteAllText(file, "key");
            string copy = AssetFileOps.UniqueCopy(file, false);
            var win = EditorCommands.Window;
            var keys = new List<string>();
            EventHandler<KeyEventArgs> rec = (s, e) => { if (e.Key == Key.D) keys.Add(e.KeyModifiers.ToString()); };
            win?.AddHandler(InputElement.KeyDownEvent, rec, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            try
            {
                var t = await RevealTile(file);
                if (t == null) return false;
                // an entity is selected too: if the Edit menu's ⌘D (entity Duplicate) grabbed the key, it shows
                var ent = scene?.Entities.FirstOrDefault(e => e.Parent == null);
                if (ent != null) SelectionService.Instance.Select(ent);
                B.SelectOnly(t);
                win?.Activate();
                await SmokeRegistry.Settle(200);
                (B.Items.ContainerFromItem(t) as Control)?.Focus();
                await SmokeRegistry.Settle(150);
                int entities = scene?.Entities.Count ?? 0;
                bool focus = B.HasItemFocus;
                string state = MacKeys.State(win);
                bool sent = MacKeys.Send(0x02, "d", MacKeys.Command, win);   // kVK_ANSI_D
                bool duplicated = await WaitFor(() => File.Exists(copy), 2000);
                int after = scene?.Entities.Count ?? 0;
                if (after > entities) EditorCommands.Undo();   // the menu duplicated the selected entity instead
                Log("Cmd+D sent=" + sent + (MacKeys.LastError != null ? " (" + MacKeys.LastError + ")" : "") + " (" + state + ", list focused=" + focus
                    + ", key reached the window: " + (keys.Count > 0 ? string.Join("/", keys) : "no") + "): asset duplicated = " + duplicated + ", entity count " + entities + " -> " + after
                    + (after > entities ? " — the Edit menu's ⌘D (EditorCommands.Duplicate) took the key: it must route to the asset browser while the browser has focus" : ""));
                if (duplicated) EditorCommands.Undo();
                return sent && duplicated && after == entities;
            }
            finally
            {
                win?.RemoveHandler(InputElement.KeyDownEvent, rec);
                SelectionService.Instance.ClearSelection();
                foreach (var f in new[] { file, copy, file + ".vmeta", copy + ".vmeta" }) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
                B.Refresh();
            }
        }

        /// <summary>A temp folder under Assets with a file and a sub-folder, shown in the Explorer tab.</summary>
        private static async Task<(string dir, string file, string sub)> TempFolder(string prefix)
        {
            string dir = Path.Combine(Assets, prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            string file = Path.Combine(dir, "a.txt");
            File.WriteAllText(file, "smoke");
            B.ClearFilters(false);
            B.SetTab("Explorer");
            B.Navigate(dir);
            await WaitFor(() => TileOf(file) != null && TileOf(sub) != null, 3000);
            return (dir, file, sub);
        }

        private static void Cleanup(params string[] paths)
        {
            foreach (var p in paths)
            {
                try { if (Directory.Exists(p)) Directory.Delete(p, true); else if (File.Exists(p)) File.Delete(p); } catch { }
                try { if (File.Exists(p + ".vmeta")) File.Delete(p + ".vmeta"); } catch { }
            }
            B?.Refresh();
        }

        private static async Task<bool> FocusTile(string path)
        {
            var t = TileOf(path);
            if (t == null) return false;
            B.SelectOnly(t);
            EditorCommands.Window?.Activate();
            await SmokeRegistry.Settle(150);
            (B.Items.ContainerFromItem(t) as Control)?.Focus();
            await SmokeRegistry.Settle(100);
            return B.HasItemFocus;
        }

        private static async Task<bool> RealKeys()
        {
            if (B == null || !OperatingSystem.IsMacOS()) return B != null;
            var win = EditorCommands.Window;
            var (dir, file, sub) = await TempFolder("SmokeKeys");
            bool ok = true;
            try
            {
                // F2 starts the inline rename (stem selected), Esc cancels it
                TextBox FocusedBox() => TopLevel.GetTopLevel(B)?.FocusManager?.GetFocusedElement() as TextBox;
                bool focused = await FocusTile(file);
                MacKeys.Send(0x78, "", 0, win);   // F2
                var t = TileOf(file);
                bool renaming = await WaitFor(() => t != null && t.IsRenaming, 1500);
                await WaitFor(() => FocusedBox() != null, 1500);
                var box = FocusedBox();
                bool stemSelected = box != null && box.SelectionStart == 0 && box.SelectionEnd == 1 && box.Text == "a.txt";
                MacKeys.Send(0x35, "\u001B", 0, win);   // Esc
                bool cancelled = await WaitFor(() => t != null && !t.IsRenaming, 1500) && File.Exists(file);
                // Enter on a file renames (Finder); typing replaces the selected stem, Enter commits
                await FocusTile(file);
                MacKeys.Send(0x24, "\r", 0, win);        // Return
                bool enterRenames = await WaitFor(() => TileOf(file)?.IsRenaming == true, 1500);
                await WaitFor(() => FocusedBox() != null, 1500);
                MacKeys.Send(0x0B, "b", 0, win);         // 'b'
                await SmokeRegistry.Settle(150);
                MacKeys.Send(0x24, "\r", 0, win);
                string renamed = Path.Combine(dir, "b.txt");
                bool typedRename = await WaitFor(() => File.Exists(renamed) && !File.Exists(file), 2000);
                string current = typedRename ? renamed : file;
                Log("F2 -> rename=" + renaming + " (focused=" + focused + ", stem selected=" + stemSelected + "), Esc cancels=" + cancelled + ", Enter renames=" + enterRenames + ", typed 'b'+Enter -> b.txt=" + typedRename);
                ok &= renaming && stemSelected && cancelled && enterRenames;
                // Enter on a folder opens it, Backspace goes up again
                await FocusTile(sub);
                MacKeys.Send(0x24, "\r", 0, win);
                bool opened = await WaitFor(() => AssetFileOps.PathsEqual(B.CurrentFolder, sub), 1500);
                B.Items.Focus();
                await SmokeRegistry.Settle(100);
                MacKeys.Send(0x33, "\u007F", 0, win);   // Delete key (Backspace)
                bool up = await WaitFor(() => AssetFileOps.PathsEqual(B.CurrentFolder, dir), 1500);
                Log("Enter on folder opens it=" + opened + ", Backspace goes up=" + up);
                ok &= opened && up;
                // Cmd+Backspace asks, then deletes (undoable)
                await WaitFor(() => TileOf(current) != null, 1500);
                await FocusTile(current);
                var before = Windows.ToList();
                MacKeys.Send(0x33, "\u007F", MacKeys.Command, win);
                Window dlg = null;
                await WaitFor(() => (dlg = Windows.FirstOrDefault(w => !before.Contains(w))) != null, 2000);
                var delete = dlg?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Delete");
                delete?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                bool deleted = await WaitFor(() => !File.Exists(current), 2000);
                if (deleted) EditorCommands.Undo();
                bool restored = await WaitFor(() => File.Exists(current), 2000);
                Log("Cmd+Backspace -> confirm dialog=" + (dlg != null) + ", deleted=" + deleted + ", undo restores=" + restored);
                ok &= dlg != null && deleted && restored;
            }
            finally { Cleanup(dir); }
            return ok;
        }

        private static async Task<bool> RealDoubleClicks()
        {
            if (B == null || !OperatingSystem.IsMacOS()) return B != null;
            string model = FirstModel();
            if (model == null) return true;
            var win = EditorCommands.Window;
            var scene = ProjectData.Current?.ActiveScene;
            bool ok = true;
            async Task<(Window w, int added)> DoubleClick(ulong mods)
            {
                var t = await RevealTile(model);
                await SmokeRegistry.Settle(200);
                var c = t == null ? null : B.Items.ContainerFromItem(t) as Control;
                int n = scene?.Entities.Count ?? 0;
                var before = Windows.ToList();
                bool sent = c != null && MacMouse.DoubleClick(win, c, mods);
                Window w = null;
                await WaitFor(() => (w = Windows.FirstOrDefault(x => !before.Contains(x))) != null || (scene?.Entities.Count ?? 0) > n, 2500);
                await SmokeRegistry.Settle(200);
                int added = (scene?.Entities.Count ?? 0) - n;
                if (w != null) { try { w.Close(); } catch { } await SmokeRegistry.Settle(150); }
                if (added > 0) EditorCommands.Undo();
                if (!sent) Log("double-click not sent: " + MacMouse.LastError);
                return (w, added);
            }
            var plain = await DoubleClick(0);
            var shift = await DoubleClick(MacKeys.Shift);
            var cmd = await DoubleClick(MacKeys.Command);
            var ctrl = await DoubleClick(MacKeys.Control);
            MacKeys.Send(0x35, "\u001B", 0, win);   // close a context menu a control-click may have opened
            Log("real double-click on " + Path.GetFileName(model) + ": plain -> added " + plain.added + " entity, Shift -> " + Describe(shift.w) + ", Cmd -> " + Describe(cmd.w) + ", Ctrl -> " + Describe(ctrl.w) + (ctrl.added > 0 ? " (+entity)" : ""));
            bool IsPreview(Window w) => w != null && (w.GetType().Name.Contains("ModelViewer") || (w.Title ?? "").Contains("Viewer") || (w.Title ?? "").Contains("Preview"));
            ok &= plain.added == 1 && plain.w == null;
            ok &= shift.w != null && (shift.w.GetType().Name.Contains("ModelEditor") || (shift.w.Title ?? "").Contains("Model Editor")) && shift.added == 0;
            ok &= IsPreview(cmd.w) && cmd.added == 0;
            ok &= IsPreview(ctrl.w) && ctrl.added == 0;
            return ok;
        }

        private static string HeaderText(MenuItem mi)
        {
            if (mi.Header is string s) return s;
            if (mi.Header is Panel p) return p.Children.OfType<TextBlock>().LastOrDefault()?.Text;
            return mi.Header?.ToString();
        }

        private static List<MenuItem> Flatten(IEnumerable<object> items)
        {
            var list = new List<MenuItem>();
            foreach (var mi in items.OfType<MenuItem>()) { list.Add(mi); list.AddRange(Flatten(mi.Items.Cast<object>())); }
            return list;
        }

        private static async Task<bool> ContextMenus()
        {
            if (B == null) return false;
            bool ok = true;
            var common = new[] { "Rename", "Duplicate", "Delete…", "Reveal in Finder", "Copy Path", "Copy Full Path", "Reimport", "New Folder", "New Script…", "New Material", "New Shader", "New Prefab", "New Scene…", "New UI Screen…", "New Animation Clip…", "New Sound Container", "Import…", "Refresh" };
            var expect = new List<(string path, string[] items)>
            {
                (FirstModel(), new[] { "Add to Scene", "Open in Model Editor", "Mesh Editor", "Large Preview", "Create Prefab from Model", "Extract Animations…", "Stress Test…", "Edit Tags…", "Open With Default App" }),
                (AssetFiles(f => Ext(f) == ".ventity").FirstOrDefault(), new[] { "Add to Scene (Instance)", "Open Prefab (Edit)", "Large Preview", "Edit Tags…" }),
                (AssetFiles(f => Ext(f) == ".vmat").FirstOrDefault(), new[] { "Open in Material Editor", "Large Preview", "Assign to Selected Entity" }),
                (AssetFiles(f => Ext(f) == ".cs").FirstOrDefault(), new[] { "Open in Code Editor", "Assign to Selected Entity" }),
                (AssetFiles(f => Ext(f) == ".wav").FirstOrDefault(), new[] { "Play", "Stop" }),
                (AssetFiles(f => Ext(f) == ".vscene").FirstOrDefault(), new[] { "Open Scene" }),
            };
            MenuItem editTags = null, stress = null;
            foreach (var (path, items) in expect)
            {
                if (path == null) continue;
                var t = await RevealTile(path);
                if (t == null) { ok = false; continue; }
                var all = Flatten(B.BuildContextMenu(t, new List<AssetTile> { t }).Items.Cast<object>());
                var headers = all.Select(HeaderText).ToList();
                var missing = items.Concat(common).Where(h => !headers.Contains(h)).ToList();
                if (missing.Count > 0) { Log(Path.GetFileName(path) + " menu misses: " + string.Join(", ", missing)); ok = false; }
                if (AssetActions.IsModel(path)) { editTags = all.FirstOrDefault(m => HeaderText(m) == "Edit Tags…"); stress = all.FirstOrDefault(m => HeaderText(m) == "Stress Test…"); }
            }
            B.Navigate(Assets);
            await SmokeRegistry.Settle(150);
            var folder = B.Tiles.FirstOrDefault(x => x.Kind == AssetKind.Folder);
            var folderMenu = folder == null ? new List<string>() : Flatten(B.BuildContextMenu(folder, new List<AssetTile> { folder }).Items.Cast<object>()).Select(HeaderText).ToList();
            var emptyMenu = Flatten(B.BuildContextMenu(null, new List<AssetTile>()).Items.Cast<object>()).Select(HeaderText).ToList();
            bool folderOk = folderMenu.Contains("Open Folder") && folderMenu.Contains("Rename") && !folderMenu.Contains("Edit Tags…");
            bool emptyOk = emptyMenu.Contains("New Folder") && emptyMenu.Contains("Import…") && emptyMenu.Contains("Open Folder in Finder") && !emptyMenu.Contains("Rename");
            ok &= folderOk && emptyOk;
            // the entries open the real windows
            Window tagWin = null, stressWin = null;
            if (editTags != null)
            {
                editTags.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                await WaitFor(() => AssetTagEditorDialog.Current != null, 2000);
                tagWin = AssetTagEditorDialog.Current;
                try { tagWin?.Close(); } catch { }
                await SmokeRegistry.Settle(150);
            }
            if (stress != null) stressWin = await OpenedWindow(() => { stress.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); return Task.CompletedTask; });
            Log("folder menu ok=" + folderOk + ", empty-space menu ok=" + emptyOk + ", Edit Tags… -> " + Describe(tagWin) + ", Stress Test… -> " + Describe(stressWin));
            return ok && tagWin != null && stressWin != null;
        }

        private static async Task<bool> CreateAssets()
        {
            if (B == null) return false;
            var (dir, file, sub) = await TempFolder("SmokeCreate");
            var extra = new List<string>();
            bool ok = true;
            var scene0 = ProjectData.Current?.ActiveScene;
            try
            {
                string folder = B.CreateFolder(dir, rename: false);
                string mat = await B.CreateMaterialAsync("Standard", "SmokeMat");
                string shader = await B.CreateShaderAsync("Unlit", "SmokeShader", openInEditor: false);
                string clip = await B.CreateAnimationClipAsync("SmokeClip", open: false);
                var ui = await OpenedWindow(async () => extra.Add(await B.CreateUiScreenAsync("SmokeUi", open: true)));
                string sndc = B.CreateSoundContainer(open: false);
                string prefab = B.CreatePrefab(); extra.Add(prefab);
                string script = await B.CreateScriptAsync("SmokeCreatedBehaviour", openInEditor: false); extra.Add(script);
                string uiPath = extra[0];
                bool inFolder(string p, string ext) => p != null && File.Exists(p) && AssetFileOps.PathsEqual(Path.GetDirectoryName(p), dir) && p.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
                var results = new List<(string what, bool ok)>
                {
                    ("folder", folder != null && Directory.Exists(folder) && AssetFileOps.PathsEqual(Path.GetDirectoryName(folder), dir)),
                    ("material", inFolder(mat, ".vmat")),
                    ("shader", inFolder(shader, OperatingSystem.IsWindows() ? ".hlsl" : ".metal")),
                    ("animation clip", inFolder(clip, ".vanim")),
                    ("UI screen (+editor " + Describe(ui) + ")", inFolder(uiPath, ".vui") && ui != null),
                    ("sound container", inFolder(sndc, ".vsndc")),
                    ("prefab (Assets/Prefabs)", prefab != null && File.Exists(prefab) && prefab.Contains(Path.Combine("Assets", "Prefabs"))),
                    ("script (Assets/Scripts)", script != null && File.Exists(script) && script.Contains(Path.Combine("Assets", "Scripts"))),
                };
                // the scene: created + activated, then back to the original scene
                string scenePath = await B.CreateSceneAsync("SmokeScene_" + Guid.NewGuid().ToString("N").Substring(0, 4));
                var created = ProjectData.Current?.Scenes.FirstOrDefault(s => s != null && scenePath != null && AssetFileOps.PathsEqual(s.FilePath, scenePath));
                bool sceneOk = scenePath != null && File.Exists(scenePath) && created != null && created.Entities.Count >= 3
                               && TileOf(scenePath) != null && ProjectData.Current?.ActiveScene == scene0;
                if (created != null) { try { ProjectData.Current.RemoveScene(created); } catch { } }
                if (scenePath != null) extra.Add(scenePath);
                results.Add(("scene (in project, saved, listed, active scene unchanged)", sceneOk));
                Log("created: " + string.Join(", ", results.Select(r => r.what + "=" + r.ok)));
                ok &= results.All(r => r.ok);
                B.Navigate(dir);
                await SmokeRegistry.Settle(200);
                ok &= new[] { mat, shader, clip, uiPath, sndc }.All(p => TileOf(p) != null);
            }
            finally { Cleanup(new[] { dir }.Concat(extra.Where(x => x != null)).ToArray()); }
            return ok;
        }

        private static async Task<bool> TagFilter()
        {
            if (B == null) return false;
            string target = FirstImage();
            if (target == null) return true;
            string tag = "SmokeTag" + Guid.NewGuid().ToString("N").Substring(0, 4);
            bool saved = AssetTagEditorDialog.SaveTags(target, new[] { tag });
            B.ClearFilters(false);
            B.Navigate(Assets);
            B.SetTagFilter(tag);
            await SmokeRegistry.Settle(150);
            bool only = B.Tiles.Count == 1 && AssetFileOps.PathsEqual(B.Tiles[0].FullPath, target);
            bool tip = only && (B.Tiles[0].ToolTip ?? "").Contains(tag);
            AssetTagEditorDialog.SaveTags(target, Array.Empty<string>());
            bool emptied = await WaitFor(() => B.Tiles.Count == 0, 2000);
            B.SetTagFilter(null);
            bool back = B.Tiles.Count > 0;
            Log("tag \"" + tag + "\" saved=" + saved + ": filter shows only the tagged asset=" + only + " (tooltip lists tag=" + tip + "), untag re-filters=" + emptied + ", clear filter=" + back);
            return saved && only && tip && emptied && back;
        }

        private static async Task<bool> DropOnFolder()
        {
            if (B == null) return false;
            var (dir, file, sub) = await TempFolder("SmokeDrop");
            try
            {
                var fileTile = TileOf(file); var subTile = TileOf(sub);
                var target = subTile == null ? null : B.Items.ContainerFromItem(subTile) as Control;
                if (fileTile == null || target == null) return false;
                var data = AssetDragData.Create(new[] { fileTile });
                var over = new DragEventArgs(DragDrop.DragOverEvent, data, target, new Point(10, 10), KeyModifiers.None);
                target.RaiseEvent(over);
                bool moveEffect = over.DragEffects == DragDropEffects.Move && subTile.IsDropTarget;
                // hovering the file itself (same folder) is not a move target
                var selfOver = new DragEventArgs(DragDrop.DragOverEvent, data, B.Items.ContainerFromItem(fileTile) as Control, new Point(5, 5), KeyModifiers.None);
                (B.Items.ContainerFromItem(fileTile) as Control)?.RaiseEvent(selfOver);
                bool noSelf = selfOver.DragEffects == DragDropEffects.None;
                target.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, target, new Point(10, 10), KeyModifiers.None));
                string moved = Path.Combine(sub, "a.txt");
                bool didMove = File.Exists(moved) && !File.Exists(file);
                // from inside "sub", the ".." tile takes it back up a level
                B.Navigate(sub);
                await WaitFor(() => TileOf(moved) != null && B.Tiles.Any(x => x.IsParentLink), 2000);
                var upTile = B.Tiles.FirstOrDefault(x => x.IsParentLink);
                var upBox = upTile == null ? null : B.Items.ContainerFromItem(upTile) as Control;
                var movedTile = TileOf(moved);
                bool upMoved = false;
                if (upBox != null && movedTile != null)
                {
                    upBox.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, AssetDragData.Create(new[] { movedTile }), upBox, new Point(5, 5), KeyModifiers.None));
                    upMoved = File.Exists(file) && !File.Exists(moved);
                    EditorCommands.Undo();   // back into sub
                }
                EditorCommands.Undo();       // the first move
                bool undone = File.Exists(file) && !File.Exists(moved);
                // payload contract: project-relative path
                bool contract = (data.Get(AssetDragData.AssetFormat) as string) == AssetFileOps.ToRelative(file) && !Path.IsPathRooted((string)data.Get(AssetDragData.AssetFormat));
                Log("drag over folder -> Move=" + moveEffect + ", over own folder -> none=" + noSelf + ", drop moved=" + didMove + ", drop on '..' moves up=" + upMoved + ", undo=" + undone + ", \"vortex/asset\" is project-relative=" + contract);
                return moveEffect && noSelf && didMove && upMoved && undone && contract;
            }
            finally { Cleanup(dir); }
        }

        private static async Task<bool> Misc()
        {
            if (B == null) return false;
            bool ok = true;
            // audition (Windows editor): selecting an audio clip plays it, selecting anything else stops it
            string wav = AssetFiles(f => Ext(f) == ".wav").FirstOrDefault();
            if (wav != null)
            {
                var t = await RevealTile(wav);
                B.Items.Selection.Clear();
                B.SelectOnly(t);
                bool playing = AssetActions.IsAuditioning;
                B.Items.Selection.Clear();
                bool stopped = !AssetActions.IsAuditioning;
                B.SelectOnly(t);
                var image = FirstImage();
                var it = image == null ? null : await RevealTile(image);   // selects a non-audio asset
                bool stoppedByOther = it == null || !AssetActions.IsAuditioning;
                Log("selecting " + Path.GetFileName(wav) + " auditions it=" + playing + ", deselecting stops it=" + stopped + ", selecting a non-audio asset stops it=" + stoppedByOther);
                ok &= playing && stopped && stoppedByOther;
            }
            // history + breadcrumb
            var first = AssetBrowserPanel.SafeDirs(Assets).FirstOrDefault();
            if (first != null)
            {
                B.Navigate(Assets); B.Navigate(first);
                B.GoBack(); bool back = AssetFileOps.PathsEqual(B.CurrentFolder, Assets);
                B.GoForward(); bool fwd = AssetFileOps.PathsEqual(B.CurrentFolder, first);
                var crumb = B.Breadcrumb.Children.OfType<Button>().FirstOrDefault(b => AssetFileOps.PathsEqual(b.Tag as string, Assets));
                crumb?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                bool crumbNav = crumb != null && AssetFileOps.PathsEqual(B.CurrentFolder, Assets);
                Log("back=" + back + ", forward=" + fwd + ", breadcrumb click navigates=" + crumbNav);
                ok &= back && fwd && crumbNav;
            }
            // tile size slider drives the tile templates
            double size0 = B.SizeSlider.Value;
            B.SizeSlider.Value = 150;
            bool sized = Math.Abs(B.TileSize - 150) < 0.5 && Math.Abs(B.TileWidth - 164) < 0.5;
            B.SizeSlider.Value = size0;
            // reimport: the thumbnail is invalidated (memory + disk) and the visible tile renders it again
            string mat = AssetFiles(f => Ext(f) == ".vmat").FirstOrDefault();
            bool reimported = true, caughtUp = true;
            if (mat != null)
            {
                var t = await RevealTile(mat);
                await WaitFor(() => t?.Thumbnail != null, 20000);
                var old = t?.Thumbnail;
                string seen = null;
                Action<string> h = p => { if (AssetFileOps.PathsEqual(p, mat)) seen = p; };
                ThumbnailService.Invalidated += h;
                try
                {
                    B.Reimport(new[] { mat });
                    var t2 = TileOf(mat);
                    reimported = seen != null && await WaitFor(() => t2?.Thumbnail != null && !ReferenceEquals(t2.Thumbnail, old), 20000);
                }
                finally { ThumbnailService.Invalidated -= h; }
                // the asset changes while the browser hides behind the Console tab: the tile catches up when it returns
                var tabs = B.FindAncestorOfType<TabControl>();
                if (tabs != null && tabs.ItemCount > 1)
                {
                    int idx = tabs.SelectedIndex;
                    var t3 = TileOf(mat);
                    var before = t3?.Thumbnail;
                    tabs.SelectedIndex = idx == 0 ? 1 : 0;
                    await SmokeRegistry.Settle(250);
                    bool hidden = B.GetVisualRoot() == null;
                    ThumbnailService.Invalidate(mat);
                    await SmokeRegistry.Settle(250);
                    tabs.SelectedIndex = idx;
                    caughtUp = await WaitFor(() => t3?.Thumbnail != null && !ReferenceEquals(t3.Thumbnail, before), 20000);
                    Log("changed while hidden behind another tab (detached=" + hidden + "): tile re-renders on return=" + caughtUp);
                }
            }
            Log("tile size slider -> TileSize=" + sized + ", reimport invalidates + re-renders the thumbnail=" + reimported);
            return ok && sized && reimported && caughtUp;
        }

        private static async Task<bool> OsDropImports()
        {
            if (B == null) return false;
            string image = FirstImage();
            if (image == null) return true;
            string src = Path.Combine(Path.GetTempPath(), "vortex_ab_drop_" + Guid.NewGuid().ToString("N").Substring(0, 6) + Path.GetExtension(image));
            File.Copy(image, src);
            var (dir, file, sub) = await TempFolder("SmokeImport");
            string imported = Path.Combine(dir, Path.GetFileName(src));
            try
            {
                var top = TopLevel.GetTopLevel(B);
                var item = await top.StorageProvider.TryGetFileFromPathAsync(new Uri(src));
                if (item == null) { Log("could not wrap the Finder file"); return false; }
#pragma warning disable CS0618
                var data = new DataObject();
                data.Set(DataFormats.Files, new Avalonia.Platform.Storage.IStorageItem[] { item });
#pragma warning restore CS0618
                var over = new DragEventArgs(DragDrop.DragOverEvent, data, B.Items, new Point(20, 20), KeyModifiers.None);
                B.Items.RaiseEvent(over);
                bool copyEffect = over.DragEffects == DragDropEffects.Copy;
                var before = Windows.ToList();
                B.Items.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, B.Items, new Point(20, 20), KeyModifiers.None));
                Window dlg = null;
                await WaitFor(() => (dlg = Windows.FirstOrDefault(w => !before.Contains(w))) != null, 4000);
                if (dlg is AssetImportDialog aid)
                {
                    await aid.ImportAsync(confirm: false);
                    await SmokeRegistry.Settle(200);
                    aid.Close();
                }
                bool shown = await WaitFor(() => TileOf(imported) != null && B.SelectedAssets.Any(t => AssetFileOps.PathsEqual(t.FullPath, imported)), 5000);
                Log("Finder drag over -> Copy=" + copyEffect + ", drop opened " + Describe(dlg) + ", imported into the browsed folder=" + File.Exists(imported) + ", shown + selected=" + shown);
                return copyEffect && dlg is AssetImportDialog && File.Exists(imported) && shown;
            }
            finally { Cleanup(dir, src); }
        }

        private static async Task<bool> RealSelection()
        {
            if (B == null || !OperatingSystem.IsMacOS()) return B != null;
            var win = EditorCommands.Window;
            var (dir, file, sub) = await TempFolder("SmokeSelect");
            var files = new List<string> { file };
            for (int i = 0; i < 3; i++) { string f = Path.Combine(dir, "f" + i + ".txt"); File.WriteAllText(f, "x"); files.Add(f); }
            var presses = new List<string>();
            EventHandler<PointerPressedEventArgs> rec = (s, e) => presses.Add(e.KeyModifiers + "#" + e.ClickCount + (e.Handled ? "(handled)" : ""));
            B.Items.AddHandler(InputElement.PointerPressedEvent, rec, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            try
            {
                B.Refresh();
                await WaitFor(() => files.All(f => TileOf(f) != null), 3000);
                await SmokeRegistry.Settle(400);   // let the watcher's refresh for the new files pass first
                Control Box(string p) => B.Items.ContainerFromItem(TileOf(p)) as Control;
                // plain click selects one, Cmd-click adds, Shift-click extends the range
                string state = MacKeys.State(win);
                MacMouse.Click(win, Box(files[0]), 0);
                await SmokeRegistry.Settle(250);
                int afterPlain = B.SelectedAssets.Count;
                MacMouse.Click(win, Box(files[2]), MacKeys.Command);
                await SmokeRegistry.Settle(250);
                bool cmdAdds = B.SelectedAssets.Count == 2;
                Log("clicks (" + state + "): plain -> " + afterPlain + " selected, Cmd-click -> " + B.SelectedAssets.Count + " selected; presses seen: " + string.Join(", ", presses));
                MacMouse.Click(win, Box(files[1]), 0);
                await SmokeRegistry.Settle(250);
                MacMouse.Click(win, Box(files[3]), MacKeys.Shift);
                await SmokeRegistry.Settle(250);
                int range = B.SelectedAssets.Count;
                // arrow keys move the selection
                MacMouse.Click(win, Box(files[0]), 0);
                await SmokeRegistry.Settle(250);
                var start = B.SelectedAssets.FirstOrDefault();
                MacKeys.Send(0x7C, "", 0, win);   // Right
                await SmokeRegistry.Settle(250);
                var next = B.SelectedAssets.FirstOrDefault();
                bool arrow = start != null && next != null && !ReferenceEquals(start, next) && B.SelectedAssets.Count == 1;
                // rubber band from empty space over every tile
                var items = B.Items;
                bool dragged = MacMouse.Drag(win, items, new Point(items.Bounds.Width - 40, items.Bounds.Height - 20), new Point(8, 8));
                await SmokeRegistry.Settle(300);
                int banded = B.SelectedAssets.Count;
                int expected = B.Tiles.Count(t => !t.IsParentLink);
                Log("Cmd-click adds=" + cmdAdds + ", Shift-click range selects " + range + " (3 expected), Right arrow moves selection=" + arrow + ", rubber band (sent=" + dragged + ") selects " + banded + "/" + expected);
                return cmdAdds && range == 3 && arrow && banded == expected;
            }
            finally { B.Items.RemoveHandler(InputElement.PointerPressedEvent, rec); Cleanup(dir); }
        }

        private static async Task<bool> TreeOps()
        {
            var tree = EditorCommands.Window?.FileTree;
            if (B == null || tree?.RootNode == null) return false;
            var (dir, file, sub) = await TempFolder("SmokeTree");
            string dest = Path.Combine(dir, "dest");
            Directory.CreateDirectory(dest);
            try
            {
                var node = tree.RootNode.Find(dir, expandAlongTheWay: true);
                if (node == null) return false;
                node.IsExpanded = true;
                node.Sync(recursive: false);
                await SmokeRegistry.Settle(200);
                // inline rename
                var subNode = node.Children.FirstOrDefault(c => AssetFileOps.PathsEqual(c.FullPath, sub));
                tree.BeginRename(subNode);
                bool editing = subNode != null && subNode.IsRenaming;
                if (subNode != null) subNode.EditName = "sub2";
                tree.CommitRename();
                string sub2 = Path.Combine(dir, "sub2");
                bool renamed = Directory.Exists(sub2) && !Directory.Exists(sub);
                // drop a browser tile onto a tree folder = move
                await SmokeRegistry.Settle(200);
                var sub2Node = node.Children.FirstOrDefault(c => AssetFileOps.PathsEqual(c.FullPath, sub2));
                var box = sub2Node == null ? null : tree.Tree.TreeContainerFromItem(sub2Node) as Control;
                B.Refresh();
                var fileTile = TileOf(file);
                bool dropped = false, overOk = false;
                if (box != null && fileTile != null)
                {
                    var data = AssetDragData.Create(new[] { fileTile });
                    var over = new DragEventArgs(DragDrop.DragOverEvent, data, box, new Point(5, 5), KeyModifiers.None);
                    box.RaiseEvent(over);
                    overOk = over.DragEffects == DragDropEffects.Move;
                    box.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, box, new Point(5, 5), KeyModifiers.None));
                    dropped = File.Exists(Path.Combine(sub2, "a.txt")) && !File.Exists(file);
                }
                // drag a tree folder onto another folder
                var destNode = node.Children.FirstOrDefault(c => AssetFileOps.PathsEqual(c.FullPath, dest));
                var destBox = destNode == null ? null : tree.Tree.TreeContainerFromItem(destNode) as Control;
                bool folderMoved = false;
                if (destBox != null)
                {
                    destBox.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, AssetDragData.ForPath(sub2), destBox, new Point(5, 5), KeyModifiers.None));
                    folderMoved = Directory.Exists(Path.Combine(dest, "sub2")) && !Directory.Exists(sub2);
                }
                // delete from the tree (+ undo)
                await SmokeRegistry.Settle(300);
                var destNode2 = tree.RootNode.Find(dest, expandAlongTheWay: true);
                await tree.DeleteFolderAsync(destNode2, confirm: false);
                bool deleted = !Directory.Exists(dest);
                EditorCommands.Undo();
                bool restored = File.Exists(Path.Combine(dest, "sub2", "a.txt"));
                Log("tree: rename box=" + editing + ", renamed=" + renamed + ", drag over folder=Move " + overOk + ", tile dropped on folder moved=" + dropped + ", folder dragged onto folder moved=" + folderMoved + ", delete=" + deleted + ", undo restores=" + restored);
                return editing && renamed && overOk && dropped && folderMoved && deleted && restored;
            }
            finally { Cleanup(dir); tree.Resync(); }
        }

        private static async Task<bool> Screenshots()
        {
            if (B == null || EditorCommands.Window == null) return false;
            var shots = new List<(string file, Action show)>
            {
                ("assetbrowser_models.png", () => { B.ClearFilters(false); B.SetTab("Models"); }),
                ("assetbrowser_materials.png", () => { B.SetTab("Materials"); }),
                ("assetbrowser_audio.png", () => { B.SetTab("Audio"); }),
                ("assetbrowser_meshes.png", () => { B.SetTab("Meshes"); }),
            };
            bool ok = true;
            foreach (var (file, show) in shots)
            {
                show();
                await SmokeRegistry.Settle(300);
                // wait until the visible thumbnails are in (renders are paced one per frame)
                await WaitFor(() => B.Tiles.Where(IsVisible).All(t => t.Thumbnail != null || !HasPreview(t)), 25000);
                int shown = B.Tiles.Count(t => IsVisible(t) && t.Thumbnail != null), want = B.Tiles.Count(t => IsVisible(t) && HasPreview(t));
                Log(file + ": " + shown + "/" + want + " visible previews");
                ok &= SmokeRegistry.Capture(EditorCommands.Window, file) && shown == want && want > 0;
            }
            // leave the editor on the folder with the most previewable assets for the final window capture
            string folder = AssetBrowserPanel.Walk(Assets).Where(x => x.isDir).Select(x => x.path).Prepend(Assets)
                .OrderByDescending(d => AssetBrowserPanel.SafeFiles(d).Count(f => AssetActions.IsModel(f) || AssetActions.IsPrefab(f)))
                .ThenByDescending(d => AssetBrowserPanel.SafeFiles(d).Count(f => ThumbnailService.KindOf(f) != ThumbnailService.Kind.None))
                .First();
            B.SetTab("Explorer");
            B.Navigate(folder);
            await SmokeRegistry.Settle(300);
            await WaitFor(() => B.Tiles.Where(IsVisible).All(t => t.Thumbnail != null || !HasPreview(t)), 20000);
            ok &= SmokeRegistry.Capture(EditorCommands.Window, "assetbrowser_window.png");
            return ok;
        }

        private static bool HasPreview(AssetTile t)
            => t.Kind == AssetKind.AudioClip || t.Kind == AssetKind.BuiltInMaterial || t.Kind == AssetKind.BuiltInTexture || ThumbnailService.KindOf(t.FullPath) != ThumbnailService.Kind.None;

        private static bool IsVisible(AssetTile t)
        {
            var c = B.Items.ContainerFromItem(t) as Control;
            if (c == null || !c.IsEffectivelyVisible) return false;
            var p = c.TranslatePoint(new Point(0, 0), B.Items);
            return p.HasValue && p.Value.Y + c.Bounds.Height > 0 && p.Value.Y < B.Items.Bounds.Height;
        }

        /// <summary>Real key presses through AppKit ([NSApp sendEvent:]) — the path a physical key takes, including the
        /// main menu's key equivalents — so the smoke run proves which handler a shortcut reaches.</summary>
        internal static class MacKeys
        {
            private const string ObjC = "/usr/lib/libobjc.A.dylib";
            public const ulong Command = 1UL << 20, Shift = 1UL << 17, Control = 1UL << 18;
            [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
            [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
            [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgId(IntPtr self, IntPtr sel);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgIdPtr(IntPtr self, IntPtr sel, IntPtr arg);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long MsgLong(IntPtr self, IntPtr sel);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgVoidPtr(IntPtr self, IntPtr sel, IntPtr arg);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgVoidByte(IntPtr self, IntPtr sel, byte arg);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")]
            private static extern IntPtr MsgKeyEvent(IntPtr cls, IntPtr sel, ulong type, CGPoint location, ulong modifierFlags, double timestamp,
                long windowNumber, IntPtr context, IntPtr characters, IntPtr charactersIgnoringModifiers, byte isARepeat, ushort keyCode);

            public static string LastError;

            [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool MsgBool(IntPtr self, IntPtr sel);

            /// <summary>"app active=…, key window=…" — synthetic events behave differently when the editor isn't frontmost.</summary>
            public static string State(TopLevel top)
            {
                try
                {
                    IntPtr app = MsgId(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                    IntPtr w = NSWindowOf(top);
                    return "app active=" + MsgBool(app, sel_registerName("isActive")) + ", key window=" + (w != IntPtr.Zero && MsgBool(w, sel_registerName("isKeyWindow")));
                }
                catch (Exception ex) { return "state: " + ex.Message; }
            }

            /// <summary>NSWindow of an Avalonia window (its platform handle is the content NSView).</summary>
            public static IntPtr NSWindowOf(TopLevel top)
            {
                var h = top?.TryGetPlatformHandle();
                if (h == null || h.Handle == IntPtr.Zero) return IntPtr.Zero;
                if (string.Equals(h.HandleDescriptor, "NSWindow", StringComparison.OrdinalIgnoreCase)) return h.Handle;
                return MsgId(h.Handle, sel_registerName("window"));
            }

            public static bool Send(ushort keyCode, string chars, ulong modifiers, TopLevel target = null)
            {
                IntPtr utf8 = IntPtr.Zero;
                LastError = null;
                try
                {
                    IntPtr app = MsgId(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                    MsgVoidByte(app, sel_registerName("activateIgnoringOtherApps:"), 1);
                    IntPtr window = NSWindowOf(target);
                    if (window == IntPtr.Zero) window = MsgId(app, sel_registerName("keyWindow"));
                    if (window == IntPtr.Zero) window = MsgId(app, sel_registerName("mainWindow"));
                    if (window == IntPtr.Zero) { LastError = "no NSWindow"; return false; }
                    MsgVoidPtr(window, sel_registerName("makeKeyAndOrderFront:"), IntPtr.Zero);
                    long number = MsgLong(window, sel_registerName("windowNumber"));
                    utf8 = Marshal.StringToCoTaskMemUTF8(chars);
                    IntPtr str = MsgIdPtr(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
                    IntPtr sel = sel_registerName("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:");
                    double t = Environment.TickCount64 / 1000.0;
                    IntPtr down = MsgKeyEvent(objc_getClass("NSEvent"), sel, 10, new CGPoint(), modifiers, t, number, IntPtr.Zero, str, str, 0, keyCode);
                    IntPtr up = MsgKeyEvent(objc_getClass("NSEvent"), sel, 11, new CGPoint(), modifiers, t + 0.03, number, IntPtr.Zero, str, str, 0, keyCode);
                    if (down == IntPtr.Zero || up == IntPtr.Zero) { LastError = "NSEvent keyEventWithType returned nil (window " + number + ")"; return false; }
                    MsgVoidPtr(app, sel_registerName("sendEvent:"), down);
                    MsgVoidPtr(app, sel_registerName("sendEvent:"), up);
                    return true;
                }
                catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return false; }
                finally { if (utf8 != IntPtr.Zero) Marshal.FreeCoTaskMem(utf8); }
            }
        }

        /// <summary>Real mouse clicks through AppKit ([NSApp sendEvent:]) at a control's position — exercises the
        /// toolkit's own click counting and modifier handling (Shift / Cmd / Ctrl double-clicks).</summary>
        internal static class MacMouse
        {
            private const string ObjC = "/usr/lib/libobjc.A.dylib";
            [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
            [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }
            [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
            [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgId(IntPtr self, IntPtr sel);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long MsgLong(IntPtr self, IntPtr sel);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect MsgRect(IntPtr self, IntPtr sel);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgVoidPtr(IntPtr self, IntPtr sel, IntPtr arg);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")]
            private static extern IntPtr MsgMouseEvent(IntPtr cls, IntPtr sel, ulong type, CGPoint location, ulong modifierFlags, double timestamp,
                long windowNumber, IntPtr context, long eventNumber, long clickCount, float pressure);

            public static string LastError;

            /// <summary>Window coordinates (AppKit, bottom-left origin) of a point in <paramref name="target"/>.</summary>
            private static bool Locate(TopLevel top, Control target, Point local, out IntPtr window, out CGPoint loc, out long number)
            {
                window = MacKeys.NSWindowOf(top); loc = default; number = 0;
                if (window == IntPtr.Zero || target == null) { LastError = "no NSWindow / target"; return false; }
                IntPtr content = MsgId(window, sel_registerName("contentView"));
                var frame = MsgRect(content, sel_registerName("frame"));
                var p = target.TranslatePoint(local, top);
                if (!p.HasValue) { LastError = "target not in window"; return false; }
                loc = new CGPoint { X = frame.X + p.Value.X, Y = frame.Y + frame.H - p.Value.Y };
                number = MsgLong(window, sel_registerName("windowNumber"));
                MsgVoidPtr(window, sel_registerName("makeKeyAndOrderFront:"), IntPtr.Zero);
                return true;
            }

            private static void Post(ulong type, CGPoint loc, ulong mods, double t, long number, long clicks, float pressure)
            {
                IntPtr app = MsgId(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                IntPtr sel = sel_registerName("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:");
                IntPtr ev = MsgMouseEvent(objc_getClass("NSEvent"), sel, type, loc, mods, t, number, IntPtr.Zero, 0, clicks, pressure);
                if (ev != IntPtr.Zero) MsgVoidPtr(app, sel_registerName("sendEvent:"), ev);
            }

            /// <summary>One left click (optionally with modifiers) at the centre of a control.</summary>
            public static bool Click(TopLevel top, Control target, ulong modifiers)
            {
                LastError = null;
                try
                {
                    if (!Locate(top, target, new Point(target.Bounds.Width / 2, Math.Min(target.Bounds.Height / 2, 40)), out _, out var loc, out long number)) return false;
                    double t = Environment.TickCount64 / 1000.0 + 1.0;   // well apart from any earlier click (no double-click)
                    Post(1, loc, modifiers, t, number, 1, 1f);
                    Post(2, loc, modifiers, t + 0.02, number, 1, 0f);
                    return true;
                }
                catch (Exception ex) { LastError = ex.Message; return false; }
            }

            /// <summary>Press at <paramref name="from"/>, drag in steps to <paramref name="to"/> (both in the control's
            /// coordinates) and release — a rubber band on empty space, or a drag.</summary>
            public static bool Drag(TopLevel top, Control target, Point from, Point to)
            {
                LastError = null;
                try
                {
                    if (!Locate(top, target, from, out _, out var a, out long number)) return false;
                    if (!Locate(top, target, to, out _, out var b, out _)) return false;
                    double t = Environment.TickCount64 / 1000.0 + 2.0;
                    Post(1, a, 0, t, number, 1, 1f);
                    for (int i = 1; i <= 8; i++)
                    {
                        var p = new CGPoint { X = a.X + (b.X - a.X) * i / 8.0, Y = a.Y + (b.Y - a.Y) * i / 8.0 };
                        Post(6, p, 0, t + 0.02 * i, number, 1, 1f);   // NSEventTypeLeftMouseDragged
                    }
                    Post(2, b, 0, t + 0.2, number, 1, 0f);
                    return true;
                }
                catch (Exception ex) { LastError = ex.Message; return false; }
            }

            public static bool DoubleClick(TopLevel top, Control target, ulong modifiers)
            {
                LastError = null;
                try
                {
                    IntPtr window = MacKeys.NSWindowOf(top);
                    if (window == IntPtr.Zero) { LastError = "no NSWindow"; return false; }
                    IntPtr content = MsgId(window, sel_registerName("contentView"));
                    var frame = MsgRect(content, sel_registerName("frame"));
                    var p = target.TranslatePoint(new Point(target.Bounds.Width / 2, Math.Min(target.Bounds.Height / 2, 40)), top);
                    if (!p.HasValue) { LastError = "target not in window"; return false; }
                    var loc = new CGPoint { X = frame.X + p.Value.X, Y = frame.Y + frame.H - p.Value.Y };
                    long number = MsgLong(window, sel_registerName("windowNumber"));
                    IntPtr app = MsgId(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                    MsgVoidPtr(window, sel_registerName("makeKeyAndOrderFront:"), IntPtr.Zero);
                    IntPtr sel = sel_registerName("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:");
                    double t = Environment.TickCount64 / 1000.0;
                    for (int click = 1; click <= 2; click++)
                    {
                        IntPtr down = MsgMouseEvent(objc_getClass("NSEvent"), sel, 1, loc, modifiers, t, number, IntPtr.Zero, 0, click, 1f);
                        IntPtr up = MsgMouseEvent(objc_getClass("NSEvent"), sel, 2, loc, modifiers, t + 0.02, number, IntPtr.Zero, 0, click, 0f);
                        if (down == IntPtr.Zero || up == IntPtr.Zero) { LastError = "NSEvent mouseEventWithType returned nil"; return false; }
                        MsgVoidPtr(app, sel_registerName("sendEvent:"), down);
                        MsgVoidPtr(app, sel_registerName("sendEvent:"), up);
                        t += 0.08;
                    }
                    return true;
                }
                catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return false; }
            }
        }
    }
}
