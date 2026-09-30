using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.VisualTree;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using VortexEditor.Panels;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Smoke checks of the editor shell package: menus, keyboard shortcuts (real AppKit key events), hierarchy
    /// (reparent / reorder / multi-select clipboard / rename / prefab / asset drop), viewport (drop, material drop,
    /// camera preview, view options, layouts), History, Console, Environment, every Window-menu editor and the shell
    /// windows (Git, Project Settings, About, Hub, shortcuts sheet). Screenshots go to the smoke capture folder.
    /// </summary>
    internal static class ShellSmoke
    {
        private static MainWindow Main => EditorCommands.Window;
        private static Scene ActiveScene => ProjectData.Current?.ActiveScene;
        private static void Log(string m) => ConsoleService.Instance.Log(m);
        private static void Note(string m) => ConsoleService.Instance.LogWarning("SMOKE NOTE " + m);

        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("shell: menu bar has every menu, the ⌘ gestures and every editor", () => Task.FromResult(CheckMenus()));
            SmokeRegistry.Add("shell: keyboard shortcuts (real key events: W/E/R, G, ⌘Z/⇧⌘Z, ⌘D, ⌫, typing guard)", CheckShortcuts);
            SmokeRegistry.Add("shell: panel toggles (Window ▸ ⌘1…⌘6) + reset layout", () => Task.FromResult(CheckPanels()));
            SmokeRegistry.Add("hierarchy: drag-reparent, reorder, move-to-root as one undo step each", () => Task.FromResult(CheckReparent()));
            SmokeRegistry.Add("hierarchy: multi-select copy / paste / duplicate / delete + undo", () => Task.FromResult(CheckClipboard()));
            SmokeRegistry.Add("hierarchy: inline rename + undo", CheckRename);
            SmokeRegistry.Add("hierarchy: create prefab from selection", CheckPrefab);
            SmokeRegistry.Add("hierarchy: asset drop (primitive under the row's entity)", CheckHierarchyDrop);
            SmokeRegistry.Add("viewport: drop a primitive at the pointer ray hit", CheckViewportDrop);
            SmokeRegistry.Add("viewport: drop a material onto the object under the pointer + undo", CheckMaterialDrop);
            SmokeRegistry.Add("viewport: camera preview (PIP) renders a scene camera", CheckPip);
            SmokeRegistry.Add("viewport: toolbar view options (wireframe, snap size, camera speed, layouts)", CheckViewOptions);
            SmokeRegistry.Add("history window: lists the undo stack, click jumps undo / redo", CheckHistory);
            SmokeRegistry.Add("console: level filters, search, collapse, copy, source links", CheckConsole);
            SmokeRegistry.Add("environment panel: sections + preview toggle", CheckEnvironment);
            SmokeRegistry.Add("window menu: every editor opens a window (open + capture + close)", CheckEditorWindows);
            SmokeRegistry.Add("shell windows: git, project settings, about, hub, shortcuts (open + capture + close)", CheckShellWindows);
        }

        // ================================================================== helpers

        private static IReadOnlyList<Window> AllWindows() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? (IReadOnlyList<Window>)Array.Empty<Window>();

        private static IntPtr NSWindowOf(Window w)
        {
            try { var h = w?.TryGetPlatformHandle(); if (h != null && h.Handle != IntPtr.Zero) return h.Handle; } catch { }
            return IntPtr.Zero;
        }

        private static string F(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        private static string Slug(string s) => new string((s ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_').Replace("__", "_");

        private static GameEntity NewCube(string name)
        {
            var e = ActiveScene.CreatePrimitive(PrimitiveType.Cube);
            e.Name = name;
            return e;
        }

        private static void Cleanup(params GameEntity[] ents)
        {
            foreach (var e in ents)
            {
                if (e == null) continue;
                if (e.Parent != null) e.Parent.Children.Remove(e);
                else e.Scene?.Entities.Remove(e);
            }
            SelectionService.Instance.ClearSelection();
            EditorSession.Instance.Hierarchy.ClearSelection();
        }

        private static async Task<List<Window>> NewWindowsAfter(Func<Task> open, HashSet<Window> before, int settleMs = 900)
        {
            await open();
            await SmokeRegistry.Settle(settleMs);
            return AllWindows().Where(w => !before.Contains(w) && w.IsVisible).ToList();
        }

        private static async Task CloseAll(IEnumerable<Window> windows)
        {
            foreach (var w in windows.ToList()) { try { w.Close(); } catch { } }
            await SmokeRegistry.Settle(300);
        }

        // ================================================================== menus

        private static bool CheckMenus()
        {
            var menu = NativeMenu.GetMenu(Main);
            if (menu == null) { Log("no native menu attached"); return false; }
            var tops = menu.Items.OfType<NativeMenuItem>().ToList();
            string[] expected = { "File", "Edit", "View", "GameObject", "Component", "Assets", "Window", "Tools", "Help" };
            var missing = expected.Where(x => tops.All(t => t.Header != x)).ToList();
            var all = new List<(string path, NativeMenuItem item)>();
            void Walk(NativeMenu m, string p) { foreach (var i in m.Items.OfType<NativeMenuItem>()) { all.Add((p + i.Header, i)); if (i.Menu != null) Walk(i.Menu, p + i.Header + " ▸ "); } }
            Walk(menu, "");
            string G(string path) => all.FirstOrDefault(a => a.path == path).item?.Gesture?.ToString();
            var gestures = new Dictionary<string, string>
            {
                ["File ▸ Save"] = "S", ["File ▸ Save All"] = "S", ["File ▸ Project Settings…"] = "OemComma", ["File ▸ Build…"] = "B",
                ["Edit ▸ Undo"] = "Z", ["Edit ▸ Redo"] = "Z", ["Edit ▸ Copy"] = "C", ["Edit ▸ Paste"] = "V", ["Edit ▸ Cut"] = "X",
                ["Edit ▸ Duplicate"] = "D", ["Edit ▸ Delete"] = "Back", ["Edit ▸ Select All"] = "A", ["Edit ▸ Find…"] = "F", ["Tools ▸ Play"] = "P",
                ["GameObject ▸ Create Empty"] = "N", ["Window ▸ Console"] = "D5",
            };
            var badGestures = new List<string>();
            foreach (var kv in gestures)
            {
                var item = all.FirstOrDefault(a => a.path == kv.Key || (kv.Key == "Edit ▸ Undo" && a.path.StartsWith("Edit ▸ Undo")) || (kv.Key == "Edit ▸ Redo" && a.path.StartsWith("Edit ▸ Redo"))).item;
                var g = item?.Gesture;
                bool ok = g != null && g.Key.ToString() == kv.Value && (OperatingSystem.IsMacOS() ? g.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta) : g.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control));
                if (!ok) badGestures.Add(kv.Key + "=" + (g?.ToString() ?? "none"));
            }
            var window = tops.FirstOrDefault(t => t.Header == "Window")?.Menu?.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList() ?? new List<string>();
            var missingEditors = EditorMenus.Editors.Where(e => e != null && !window.Contains(e.Title)).Select(e => e.Title).ToList();
            var physics = all.Where(a => a.path.StartsWith("Component ▸ Physics ▸ ")).Select(a => a.item.Header).ToList();
            bool joints = new[] { "Hinge Joint", "Ball Joint", "Slider Joint", "Fixed Joint", "Distance Joint" }.All(physics.Contains);
            Log($"menus: {tops.Count} top-level, {all.Count} items; missing={string.Join(",", missing)} badGestures={string.Join(",", badGestures)} missingEditors={string.Join(",", missingEditors)} joints={joints}");
            // the real macOS menu bar (only installed while the editor is the active app — not the case in a background smoke run)
            if (OperatingSystem.IsMacOS())
            {
                if (MacKeys.IsAppActive())
                {
                    var bar = MacKeys.DumpMainMenu(1).Where(d => !d.Path.Contains(" ▸ ")).Select(d => d.Title).ToList();
                    Log("macOS menu bar: " + string.Join(" | ", bar));
                }
                else Note("menus: the editor is not the active app in this run, so AppKit's menu bar is not installed — checked the NativeMenu model instead");
            }
            return missing.Count == 0 && badGestures.Count == 0 && missingEditors.Count == 0 && joints;
        }

        // ================================================================== keyboard

        private static async Task<bool> CheckShortcuts()
        {
            if (!OperatingSystem.IsMacOS()) { Note("shortcut check posts AppKit events — macOS only"); return true; }
            var scene = ActiveScene; if (scene == null) return false;
            var nsw = NSWindowOf(Main);
            MacKeys.MakeKey(nsw);
            await SmokeRegistry.Settle(300);
            bool menuActive = MacKeys.IsAppActive();
            var results = new List<string>();
            bool ok = true;
            void Expect(string what, bool cond) { results.Add(what + (cond ? " ok" : " FAIL")); if (!cond) ok = false; }

            // plain keys, no text focus
            VortexEditor.Viewport.EngineViewport.Current?.ClearFocus();
            Main.FocusManager?.ClearFocus();
            await SmokeRegistry.Settle(150);
            MacKeys.Press(nsw, 'e', 0); await SmokeRegistry.Settle(150);
            Expect("E=rotate", TransformGizmoService.Instance.CurrentMode == TransformGizmoService.GizmoMode.Rotate);
            MacKeys.Press(nsw, 'r', 0); await SmokeRegistry.Settle(150);
            Expect("R=scale", TransformGizmoService.Instance.CurrentMode == TransformGizmoService.GizmoMode.Scale);
            MacKeys.Press(nsw, 'w', 0); await SmokeRegistry.Settle(150);
            Expect("W=move", TransformGizmoService.Instance.CurrentMode == TransformGizmoService.GizmoMode.Translate);
            bool grid0 = EditorViewportService.Instance.IsGridVisible;
            MacKeys.Press(nsw, 'g', 0); await SmokeRegistry.Settle(150);
            Expect("G=grid once", EditorViewportService.Instance.IsGridVisible == !grid0);
            if (EditorViewportService.Instance.IsGridVisible != grid0) EditorViewportService.Instance.IsGridVisible = grid0;

            // ⌘Z / ⇧⌘Z / ⌘D / ⌫ on entities
            var a = NewCube("KeyTestA");
            EditorCommands.SelectMany(new[] { a });
            await SmokeRegistry.Settle(200);
            int n = scene.Entities.Count;
            MacKeys.Press(nsw, 'z', MacKeys.Cmd); await SmokeRegistry.Settle(250);
            Expect("⌘Z undo", scene.Entities.Count == n - 1);
            MacKeys.Press(nsw, 'z', MacKeys.Cmd | MacKeys.Shift); await SmokeRegistry.Settle(250);
            Expect("⇧⌘Z redo", scene.Entities.Count == n);
            EditorCommands.SelectMany(new[] { a });
            await SmokeRegistry.Settle(150);
            MacKeys.Press(nsw, 'd', MacKeys.Cmd); await SmokeRegistry.Settle(250);
            Expect("⌘D duplicate", scene.Entities.Count == n + 1);
            MacKeys.Press(nsw, 'z', MacKeys.Cmd); await SmokeRegistry.Settle(250);
            EditorCommands.SelectMany(new[] { a });
            Main.Hierarchy.FocusTree();
            await SmokeRegistry.Settle(200);
            MacKeys.Press(nsw, '\b', 0); await SmokeRegistry.Settle(250);
            Expect("⌫ delete (hierarchy focus)", !scene.Entities.Contains(a));
            MacKeys.Press(nsw, 'z', MacKeys.Cmd); await SmokeRegistry.Settle(250);
            Expect("⌘Z restores", scene.Entities.Contains(a));

            // typing guard: letters and ⌘A go to the text field, not to the scene
            var search = Main.Hierarchy.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("search"));
            if (search != null)
            {
                search.Text = "";
                search.Focus();
                await SmokeRegistry.Settle(200);
                var mode = TransformGizmoService.Instance.CurrentMode;
                MacKeys.Press(nsw, 'e', 0); await SmokeRegistry.Settle(150);
                Expect("typing 'e' stays in the text field", search.Text == "e" && TransformGizmoService.Instance.CurrentMode == mode);
                search.Text = "abc"; search.CaretIndex = 3;
                int before = scene.Entities.Count;
                MacKeys.Press(nsw, 'a', MacKeys.Cmd); await SmokeRegistry.Settle(150);
                MacKeys.Press(nsw, 'd', MacKeys.Cmd); await SmokeRegistry.Settle(200);
                Expect("⌘A/⌘D in a text field don't touch entities", scene.Entities.Count == before && EditorSession.Instance.Hierarchy.SelectedEntities.Count <= 1);
                search.Text = "";
                Main.Hierarchy.FocusTree();
            }
            Cleanup(a);
            Log("shortcuts (" + (menuActive ? "menu bar active" : "window key path; the menu bar is inactive in this run") + "): " + string.Join(", ", results));
            return ok;
        }

        // ================================================================== panels

        private static bool CheckPanels()
        {
            var w = Main; bool ok = true;
            foreach (var p in new[] { MainWindow.PanelHierarchy, MainWindow.PanelFiles, MainWindow.PanelInspector, MainWindow.PanelEnvironment, MainWindow.PanelProject, MainWindow.PanelConsole })
            {
                w.ShowPanel(p);
                if (!w.IsPanelVisible(p)) ok = false;
                w.TogglePanel(p);   // in front -> hide
                if (w.IsPanelVisible(p)) { Log("panel " + p + " did not hide"); ok = false; }
                w.TogglePanel(p);   // hidden -> show
                if (!w.IsPanelVisible(p)) { Log("panel " + p + " did not show"); ok = false; }
            }
            w.TogglePanel(MainWindow.PanelHierarchy); w.TogglePanel(MainWindow.PanelFiles);
            bool leftCollapsed = w.Workspace.ColumnDefinitions[0].Width.Value == 0;
            w.ResetLayout();
            bool reset = w.Workspace.ColumnDefinitions[0].Width.Value > 100 && w.IsPanelVisible(MainWindow.PanelHierarchy);
            w.BottomTabs.SelectedIndex = 0; w.RightTabs.SelectedIndex = 0;
            return ok && leftCollapsed && reset;
        }

        // ================================================================== hierarchy

        private static bool CheckReparent()
        {
            var scene = ActiveScene; if (scene == null) return false;
            var h = Main.Hierarchy; var um = UndoRedoManager.Instance;
            var a = NewCube("ReparentA"); var b = NewCube("ReparentB"); var c = NewCube("ReparentC");
            int ib = scene.Entities.IndexOf(b), ic = scene.Entities.IndexOf(c);
            int undo0 = um.UndoCount;
            bool r1 = h.MoveEntities(new[] { b }, a, HierarchyPanel.DropPosition.Inside) && b.Parent == a && a.Children.Contains(b) && !scene.Entities.Contains(b);
            bool oneStep = um.UndoCount == undo0 + 1;
            um.Undo();
            bool u1 = b.Parent == null && scene.Entities.IndexOf(b) == ib && !a.Children.Contains(b);
            // multi-select drop: both become children in one step, undo restores the original order
            bool r2 = h.MoveEntities(new[] { b, c }, a, HierarchyPanel.DropPosition.Inside) && b.Parent == a && c.Parent == a && um.UndoCount == undo0 + 1;
            um.Undo();
            bool u2 = b.Parent == null && c.Parent == null && scene.Entities.IndexOf(b) == ib && scene.Entities.IndexOf(c) == ic;
            // reorder: C before A
            bool r3 = h.MoveEntities(new[] { c }, a, HierarchyPanel.DropPosition.Before) && scene.Entities.IndexOf(c) == scene.Entities.IndexOf(a) - 1;
            um.Undo();
            bool u3 = scene.Entities.IndexOf(c) == ic;
            // child back to the top level (drop on empty space)
            h.MoveEntities(new[] { b }, a, HierarchyPanel.DropPosition.Inside);
            bool r4 = h.MoveEntities(new[] { b }, null, HierarchyPanel.DropPosition.Inside) && b.Parent == null && scene.Entities.Contains(b);
            um.Undo(); um.Undo();
            // refused: an entity into its own child
            h.MoveEntities(new[] { b }, a, HierarchyPanel.DropPosition.Inside);
            bool refused = !h.MoveEntities(new[] { a }, b, HierarchyPanel.DropPosition.Inside);
            um.Undo();
            // another scene (when the project has one)
            bool r5 = true;
            var other = ProjectData.Current.Scenes.FirstOrDefault(s => !ReferenceEquals(s, scene));
            if (other != null)
            {
                r5 = h.MoveEntities(new[] { c }, other, HierarchyPanel.DropPosition.Inside) && c.Scene == other && other.Entities.Contains(c);
                um.Undo();
                r5 &= c.Scene == scene && scene.Entities.IndexOf(c) == ic;
            }
            Log($"reparent: inside={r1}/{oneStep} undo={u1} multi={r2} undo={u2} before={r3} undo={u3} toRoot={r4} cycleRefused={refused} otherScene={r5}");
            Cleanup(a, b, c);
            return r1 && oneStep && u1 && r2 && u2 && r3 && u3 && r4 && refused && r5;
        }

        private static bool CheckClipboard()
        {
            var scene = ActiveScene; if (scene == null) return false;
            var um = UndoRedoManager.Instance;
            var a = NewCube("ClipA"); var b = NewCube("ClipB");
            EditorCommands.SelectMany(new[] { a, b });
            bool multi = EditorCommands.SelectedEntities().Count == 2;
            int n = scene.Entities.Count;
            EditorCommands.Copy();
            var pasted = EditorCommands.Paste();
            bool paste = pasted.Count == 2 && scene.Entities.Count == n + 2 && pasted.All(p => p.Id != a.Id && p.Id != b.Id);
            um.Undo();
            bool undoPaste = scene.Entities.Count == n;
            EditorCommands.SelectMany(new[] { a });
            var dup = EditorCommands.Duplicate();
            bool duplicate = dup.Count == 1 && scene.Entities.IndexOf(dup[0]) == scene.Entities.IndexOf(a) + 1 && dup[0].Name == a.Name;
            um.Undo();
            bool undoDup = scene.Entities.Count == n;
            EditorCommands.SelectMany(new[] { a, b });
            EditorCommands.Delete();
            bool del = !scene.Entities.Contains(a) && !scene.Entities.Contains(b) && SelectionService.Instance.SelectedEntity == null;
            um.Undo();
            bool undoDel = scene.Entities.Contains(a) && scene.Entities.Contains(b);
            // stale multi-selection: a viewport pick replaces it (⌘⌫ must not delete the old set)
            EditorCommands.SelectMany(new[] { a, b });
            SelectionService.Instance.Select(a);
            SelectionService.Instance.Select(b);
            bool sync = EditorCommands.SelectedEntities().Count == 2;   // b was part of the set -> kept
            var other = scene.Entities.FirstOrDefault(e => e != a && e != b);
            if (other != null) { SelectionService.Instance.Select(other); sync &= EditorCommands.SelectedEntities().SequenceEqual(new[] { SelectionService.Instance.SelectedEntity }); }
            Log($"clipboard: multi={multi} paste={paste} undo={undoPaste} duplicate(sibling)={duplicate} undo={undoDup} delete={del} undo={undoDel} selectionSync={sync}");
            Cleanup(a, b);
            return multi && paste && undoPaste && duplicate && undoDup && del && undoDel && sync;
        }

        private static async Task<bool> CheckRename()
        {
            var scene = ActiveScene; if (scene == null) return false;
            var a = NewCube("RenameMe");
            EditorCommands.SelectMany(new[] { a });
            await SmokeRegistry.Settle(300);
            Main.Hierarchy.BeginRename(a);
            for (int i = 0; i < 20 && !Main.Hierarchy.IsRenaming; i++) await SmokeRegistry.Settle(100);
            bool open = Main.Hierarchy.IsRenaming;
            var box = Main.Hierarchy.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("renamebox") && t.IsVisible);
            if (box != null) box.Text = "Renamed Cube";
            SmokeRegistry.Capture(Main.Hierarchy, "panel_hierarchy_rename.png");
            Main.Hierarchy.CommitRename();
            bool renamed = a.Name == "Renamed Cube";
            UndoRedoManager.Instance.Undo();
            bool undone = a.Name == "RenameMe";
            Log($"rename: inline editor={open} (box={(box != null)}) renamed={renamed} undo={undone}");
            Cleanup(a);
            return open && box != null && renamed && undone;
        }

        private static async Task<bool> CheckPrefab()
        {
            var scene = ActiveScene; if (scene == null) return false;
            var a = NewCube("SmokePrefabSource");
            var child = new GameEntity(scene, "Child"); a.AddChild(child);
            var paths = await EditorCommands.CreatePrefabFromSelection(new[] { a }, ask: false);
            bool ok = paths.Count == 1 && File.Exists(paths[0]) && a.IsPrefabInstance && paths[0].EndsWith("SmokePrefabSource.ventity");
            Log("prefab: " + string.Join(", ", paths) + " instance=" + a.IsPrefabInstance);
            foreach (var p in paths) { try { File.Delete(p); File.Delete(p + ".vmeta"); } catch { } }   // leave no asset behind for later checks
            Cleanup(a);
            return ok;
        }

        /// <summary>AssetActions.AddToScene belongs to the Asset Browser package; until it lands it returns null.</summary>
        private static bool AddToSceneAvailable(out GameEntity probe)
        {
            probe = VortexEditor.Services.AssetActions.AddToScene("Primitive:Cube");
            return probe != null;
        }

        private static async Task<bool> CheckHierarchyDrop()
        {
            var scene = ActiveScene; if (scene == null) return false;
            if (!AddToSceneAvailable(out var probe)) { Note("AssetActions.AddToScene is not implemented yet (Asset Browser package) — hierarchy drop verified up to the call"); return await HierarchyDropStub(); }
            UndoRedoManager.Instance.Undo(); Cleanup(probe);
            var parent = NewCube("DropParent");
            var e = await Main.Hierarchy.DropAsset("Primitive:Sphere", parent, HierarchyPanel.DropPosition.Inside);
            bool ok = e != null && e.Parent == parent;
            Log("hierarchy drop: created=" + (e?.Name ?? "null") + " parent=" + (e?.Parent?.Name ?? "none"));
            Cleanup(e, parent);
            return ok;
        }

        private static async Task<bool> HierarchyDropStub()
        {
            // script + material drops do not depend on AddToScene
            var a = NewCube("DropTarget");
            string script = EditorMenus.FindAssets(new[] { "*.cs" }).FirstOrDefault(f => !f.EndsWith("VortexScripting.cs"));
            bool scriptOk = true;
            if (script != null)
            {
                await Main.Hierarchy.DropAsset(script, a, HierarchyPanel.DropPosition.Inside);
                scriptOk = a.GetComponent<Editor.ECS.Components.Scripting.Script>() != null;
            }
            Log("hierarchy drop (script onto entity): " + scriptOk);
            Cleanup(a);
            return scriptOk;
        }

        // ================================================================== viewport

        private static async Task<bool> CheckViewportDrop()
        {
            var vp = Main.ViewportPanel; var scene = ActiveScene; if (scene == null) return false;
            vp.SetLayout(1);
            await SmokeRegistry.Settle(300);
            var ev = vp.EngineView;
            var center = new Point(ev.Bounds.Width / 2, ev.Bounds.Height * 0.75);
            var hit = vp.DropPoint(center.X, center.Y);
            bool finite = !float.IsNaN(hit.X) && !float.IsNaN(hit.Y) && !float.IsNaN(hit.Z) && !float.IsInfinity(hit.X);
            if (!AddToSceneAvailable(out var probe))
            {
                var none = await vp.DropAssetAt("Primitive:Cube", center);
                Note("AssetActions.AddToScene is not implemented yet (Asset Browser package) — viewport drop computed the hit point (" + F(hit.X) + ", " + F(hit.Y) + ", " + F(hit.Z) + ") and called it");
                return finite && none == null && vp.LastDropPoint.HasValue;
            }
            UndoRedoManager.Instance.Undo(); Cleanup(probe);
            int n = scene.Entities.Count;
            var e = await vp.DropAssetAt("Primitive:Cube", center);
            bool created = e != null && scene.Entities.Count == n + 1;
            var p = e?.Transform?.LocalPosition;
            bool placed = p.HasValue && Math.Abs(p.Value.X - hit.X) < 0.5f && Math.Abs(p.Value.Z - hit.Z) < 0.5f;
            Log("viewport drop: created=" + created + " at (" + F(p?.X ?? float.NaN) + ", " + F(p?.Y ?? float.NaN) + ", " + F(p?.Z ?? float.NaN) + "), ray hit (" + F(hit.X) + ", " + F(hit.Y) + ", " + F(hit.Z) + ")");
            if (e != null) { UndoRedoManager.Instance.Undo(); Cleanup(e); }
            return finite && created && placed;
        }

        private static async Task<bool> CheckMaterialDrop()
        {
            var vp = Main.ViewportPanel; var scene = ActiveScene; if (scene == null) return false;
            var ev = vp.EngineView;
            // find a point over an object with a mesh (scan the view)
            GameEntity target = null; Point at = default;
            for (int yi = 1; yi < 10 && target == null; yi++)
                for (int xi = 1; xi < 10 && target == null; xi++)
                {
                    var p = new Point(ev.Bounds.Width * xi / 10, ev.Bounds.Height * yi / 10);
                    var t = vp.Session.PickEntityAt(p.X, p.Y);
                    var mr = t?.GetComponent<MeshRenderer>();
                    if (mr != null && !string.IsNullOrEmpty(mr.MeshPath) && mr.MeshPath.IndexOf('#') < 0) { target = t; at = p; }
                }
            if (target == null)
            {
                // nothing under the camera: put a cube in front of it and frame it
                target = NewCube("MaterialDropTarget");
                SelectionService.Instance.Select(target);
                EditorCommands.FocusSelected();
                await SmokeRegistry.Settle(500);
                at = new Point(ev.Bounds.Width / 2, ev.Bounds.Height * 0.72);
                var picked = vp.Session.PickEntityAt(at.X, at.Y);
                if (picked != target) { Log("material drop: could not place a target under the pointer (picked " + (picked?.Name ?? "nothing") + ")"); Cleanup(target); return false; }
            }
            var mrT = target.GetComponent<MeshRenderer>();
            string before = mrT.MaterialPath;
            string vmat = Editor.Core.Assets.AssetActions.CreateMaterial(Path.Combine(ProjectData.Current.Path, "Assets", "Materials", "SmokeDrop.vmat"), "Standard");
            var r = await vp.DropAssetAt(vmat, at);
            string rel = Editor.Core.Assets.AssetActions.Relative(vmat);
            bool assigned = r == target && mrT.MaterialPath == rel;
            UndoRedoManager.Instance.Undo();
            bool undone = mrT.MaterialPath == before;
            Log($"material drop: target={target.Name} assigned={assigned} ({mrT.MaterialPath}) undo={undone}");
            if (target.Name == "MaterialDropTarget") Cleanup(target);
            try { File.Delete(vmat); File.Delete(vmat + ".vmeta"); } catch { }
            return assigned && undone;
        }

        private static async Task<bool> CheckPip()
        {
            var vp = Main.ViewportPanel;
            GameEntity cam = null;
            void Find(GameEntity e) { if (cam != null) return; if (e.GetComponent<Camera>() != null) cam = e; if (e.Children != null) foreach (var c in e.Children) Find(c); }
            foreach (var e in ActiveScene?.Entities ?? new System.Collections.ObjectModel.ObservableCollection<GameEntity>()) Find(e);
            GameEntity made = null;
            if (cam == null) { made = cam = EditorCommands.CreateCamera(); }
            CameraPreviewService.Instance.ShowPreview(cam);
            for (int i = 0; i < 30 && vp.CameraPreview.FramesRendered < 3; i++) await SmokeRegistry.Settle(100);
            bool ok = vp.CameraPreview.IsVisible && vp.CameraPreview.FramesRendered > 0;
            SmokeRegistry.Capture(vp.CameraPreview.Frame, "viewport_pip.png");
            CameraPreviewService.Instance.ClosePreview();
            await SmokeRegistry.Settle(200);
            ok &= !vp.CameraPreview.IsVisible;
            Log("camera preview: camera=" + cam?.Name + " frames=" + vp.CameraPreview.FramesRendered);
            if (made != null) { UndoRedoManager.Instance.Undo(); Cleanup(made); }
            return ok;
        }

        private static async Task<bool> CheckViewOptions()
        {
            var vp = Main.ViewportPanel;
            vp.SetWireframe(true);
            await SmokeRegistry.Settle(300);
            bool wire = Editor.DllWrapper.VortexAPI.IsWireframeMode;
            SmokeRegistry.Capture(vp.Toolbar, "viewport_toolbar.png");
            vp.SetWireframe(false);
            bool shaded = !Editor.DllWrapper.VortexAPI.IsWireframeMode;
            float spacing = EditorViewportService.Instance.GridSpacing;
            EditorCommands.SetSnapSize(0.25f);
            bool snap = Math.Abs(EditorViewportService.Instance.GridSpacing - 0.25f) < 1e-4f;
            EditorCommands.SetSnapSize(spacing);
            float speed = EditorCameraController.Instance.MoveSpeed;
            vp.SetCameraSpeed(12f);
            bool sp = Math.Abs(EditorCameraController.Instance.MoveSpeed - 12f) < 1e-3f;
            EditorCameraController.Instance.MoveSpeed = speed;
            bool layouts = true;
            foreach (int l in new[] { 2, 3, 4 })
            {
                vp.SetLayout(l);
                await SmokeRegistry.Settle(700);
                var views = vp.GetVisualDescendants().OfType<SecondaryViewportView>().ToList();
                int expected = l == 4 ? 3 : 1;
                if (views.Count != expected || views.Any(v => v.FramesRendered == 0)) { layouts = false; Log("layout " + l + ": " + views.Count + " views, frames " + string.Join("/", views.Select(v => v.FramesRendered))); }
                if (l == 4) SmokeRegistry.Capture(vp, "viewport_quad.png");
            }
            vp.SetLayout(1);
            Log($"view options: wireframe={wire}/{shaded} snap={snap} speed={sp} layouts={layouts}");
            return wire && shaded && snap && sp && layouts;
        }

        // ================================================================== history

        private static async Task<bool> CheckHistory()
        {
            var scene = ActiveScene; if (scene == null) return false;
            var um = UndoRedoManager.Instance;
            int start = um.UndoCount;
            var a = NewCube("HistoryA"); var b = NewCube("HistoryB");
            int steps = um.UndoCount - start;   // a primitive = add component + add entity
            EditorWindows.History();
            await SmokeRegistry.Settle(600);
            var w = HistoryWindow.Current;
            if (w == null) { Log("history window did not open"); Cleanup(a, b); return false; }
            var rows = w.Rows;
            int undoRows = rows.Count(r => r.Steps < 0);
            bool lists = undoRows == um.UndoCount && rows.Any(r => r.IsCurrent) && rows.Count(r => r.Steps < 0 && r.Name.Contains("Add")) >= 2;
            SmokeRegistry.Capture(w, "window_history.png");
            int undoCount = um.UndoCount;
            var back = w.Rows.FirstOrDefault(r => r.Steps == -steps);
            w.Jump(back);
            await SmokeRegistry.Settle(200);
            bool jumpedBack = um.UndoCount == undoCount - steps && !scene.Entities.Contains(a) && !scene.Entities.Contains(b);
            SmokeRegistry.Capture(w, "window_history_after_jump.png");
            var fwd = w.Rows.FirstOrDefault(r => r.Steps == steps);
            w.Jump(fwd);
            await SmokeRegistry.Settle(200);
            bool jumpedFwd = um.UndoCount == undoCount && scene.Entities.Contains(a) && scene.Entities.Contains(b);
            Log($"history: rows={rows.Count} undoRows={undoRows} listed={lists} back{steps}={jumpedBack} fwd{steps}={jumpedFwd}");
            w.Close();
            await SmokeRegistry.Settle(200);
            Cleanup(a, b);
            return lists && jumpedBack && jumpedFwd && HistoryWindow.Current == null;
        }

        // ================================================================== console

        private static async Task<bool> CheckConsole()
        {
            var c = Main.ConsoleView;
            Main.ShowPanel(MainWindow.PanelConsole);
            string token = "smk" + Guid.NewGuid().ToString("N").Substring(0, 6);
            for (int i = 0; i < 3; i++) ConsoleService.Instance.LogWarning(token + " repeated warning");
            ConsoleService.Instance.Log(token + " info line");
            ConsoleService.Instance.LogError(token + " Assets/Scripts/Nowhere.cs(12,5): error CS1002: ; expected");
            await SmokeRegistry.Settle(300);
            c.Flush();
            c.SearchText = token;
            await SmokeRegistry.Settle(200);
            bool search = c.VisibleRows.Count == 5 && c.VisibleRows.All(r => r.Message.Contains(token));
            c.SetLevelVisible(LogLevel.Warning, false);
            bool levelFilter = c.VisibleRows.Count == 2 && c.VisibleRows.All(r => r.Level != LogLevel.Warning);
            c.SetLevelVisible(LogLevel.Warning, true);
            c.Collapse = true;
            var grp = c.VisibleRows.FirstOrDefault(r => r.Level == LogLevel.Warning);
            bool collapse = c.VisibleRows.Count == 3 && grp != null && grp.Count == 3 && grp.HasCount;
            SmokeRegistry.Capture(c, "panel_console.png");
            string copied = await c.CopyAsync();
            bool copy = copied.Contains(token) && copied.Contains("(x3)");
            c.Collapse = false;
            c.SearchText = "";
            // source links: a real script of the project resolves to its file + line
            string script = EditorMenus.FindAssets(new[] { "*.cs" }).FirstOrDefault(f => !f.EndsWith("VortexScripting.cs"));
            bool link = true;
            if (script != null)
            {
                string rel = Editor.Core.Assets.AssetActions.Relative(script);
                link = ConsolePanel.TryParseSource(rel + "(42,7): error CS0103: The name 'x' does not exist", out var f, out int line) && Path.GetFullPath(f) == Path.GetFullPath(script) && line == 42
                    && ConsolePanel.TryParseSource("   at Player.Update() in " + script + ":line 17", out var f2, out int line2) && line2 == 17;
            }
            Log($"console: search={search} levelFilter={levelFilter} collapse={collapse} copy={copy} sourceLink={link}");
            Main.BottomTabs.SelectedIndex = 0;
            return search && levelFilter && collapse && copy && link;
        }

        // ================================================================== environment

        private static async Task<bool> CheckEnvironment()
        {
            var env = Main.Environment;
            Main.ShowPanel(MainWindow.PanelEnvironment);
            env.Refresh();
            var sections = env.Sections.Children.Count;
            bool preview0 = EnvironmentPanel.PreviewPostEffects;
            EnvironmentPanel.SetPreviewPostEffects(true);
            bool on = EnvironmentPanel.PreviewPostEffects;
            EnvironmentPanel.SetPreviewPostEffects(preview0);
            var st = ActiveScene?.Settings;
            bool apply = true;
            if (st != null)
            {
                // drive the first slider of the Fog card (Density) like a user would
                var slider = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(env.Sections).OfType<Slider>().FirstOrDefault();
                float d = st.FogDensity;
                if (slider == null) apply = false;
                else
                {
                    slider.Value = 0.123;
                    apply = Math.Abs(st.FogDensity - 0.123f) < 1e-4f && ActiveScene.IsDirty;
                    st.FogDensity = d; st.Apply(); env.Refresh();
                }
            }
            await SmokeRegistry.Settle(400);
            SmokeRegistry.Capture(env, "panel_environment.png");
            Main.RightTabs.SelectedIndex = 0;
            Log($"environment: sections={sections} previewToggle={on} settings={apply}");
            return sections == 7 && on && apply;
        }

        private static void Dispatch(Action a) { try { a(); } catch { } }

        // ================================================================== Window menu editors

        private static async Task<bool> CheckEditorWindows()
        {
            var scene = ActiveScene; if (scene == null) return false;
            var results = new List<string>();
            bool ok = true;
            GameEntity entity = scene.Entities.FirstOrDefault(e => e.GetComponent<MeshRenderer>() != null) ?? NewCube("EditorTarget");
            SelectionService.Instance.Select(entity);
            foreach (var entry in EditorMenus.Editors)
            {
                if (entry == null) continue;
                var before = new HashSet<Window>(AllWindows());
                string label = entry.Title.TrimEnd('…');
                Func<Task> open;
                if (entry.IsAsset)
                {
                    string asset = EditorMenus.FindAssets(entry.Patterns).FirstOrDefault();
                    // no screen in the project: use a throw-away one outside it (never leave files for other checks)
                    if (asset == null && entry.Patterns.Contains("*.vui")) asset = Editor.Core.Assets.AssetActions.CreateUiScreen(Path.Combine(Path.GetTempPath(), "vortex-shell-smoke", "SmokeScreen.vui"));
                    if (asset == null) { results.Add(label + ": no asset of that type in the project (skipped)"); continue; }
                    open = () => { entry.OpenAsset(asset); return Task.CompletedTask; };
                }
                else if (entry.IsEntity) open = () => { entry.OpenEntity(entity); return Task.CompletedTask; };
                else if (entry.Title.StartsWith("Import Assets"))
                {
                    string sample = EditorMenus.FindAssets(new[] { "*.png" }).FirstOrDefault();
                    Task<string[]> pending = null;
                    open = () => { pending = EditorWindows.ImportAssets(sample != null ? new[] { sample } : new string[0], null); return Task.CompletedTask; };
                    var w1 = await NewWindowsAfter(open, before);
                    if (w1.Count == 0 && pending != null && pending.IsCompleted) { results.Add(label + ": foundation stub (no dialog yet)"); Note("Import Assets dialog is still the foundation stub"); continue; }
                    foreach (var w in w1) SmokeRegistry.Capture(w, "menu_" + Slug(label) + ".png");
                    results.Add(label + (w1.Count > 0 ? ": " + w1[0].Title : ": NO WINDOW"));
                    if (w1.Count == 0) ok = false;
                    await CloseAll(w1);
                    continue;
                }
                else if (entry.Title.StartsWith("Color Picker"))
                {
                    Task<Color?> pending = null;
                    open = () => { pending = EditorWindows.PickColor(Colors.Orange, "Color Picker"); return Task.CompletedTask; };
                    var w2 = await NewWindowsAfter(open, before);
                    if (w2.Count == 0 && pending != null && pending.IsCompleted) { results.Add(label + ": foundation stub (no dialog yet)"); continue; }
                    foreach (var w in w2) SmokeRegistry.Capture(w, "menu_" + Slug(label) + ".png");
                    results.Add(label + (w2.Count > 0 ? ": " + w2[0].Title : ": NO WINDOW"));
                    if (w2.Count == 0) ok = false;
                    await CloseAll(w2);
                    continue;
                }
                else open = () => EditorMenus.Run(entry);

                var opened = await NewWindowsAfter(open, before, 1200);
                if (opened.Count == 0) { ok = false; results.Add(label + ": NO WINDOW"); continue; }
                foreach (var w in opened) SmokeRegistry.Capture(w, "menu_" + Slug(label) + ".png");
                results.Add(label + ": " + string.Join("+", opened.Select(w => w.Title)));
                await CloseAll(opened);
                var still = opened.Where(w => w.IsVisible).ToList();
                if (still.Count > 0) { ok = false; results.Add(label + ": did not close"); }
            }
            if (entity.Name == "EditorTarget") Cleanup(entity);
            Log("window menu editors:\n  " + string.Join("\n  ", results));
            return ok;
        }

        // ================================================================== shell windows

        private static async Task<bool> CheckShellWindows()
        {
            var results = new List<string>();
            bool ok = true;
            async Task One(string name, Func<Task> open, Func<Window, Task<bool>> verify = null, int settle = 900)
            {
                var before = new HashSet<Window>(AllWindows());
                var opened = await NewWindowsAfter(open, before, settle);
                if (opened.Count == 0) { ok = false; results.Add(name + ": NO WINDOW"); return; }
                bool v = true;
                if (verify != null) { try { v = await verify(opened[0]); } catch (Exception ex) { v = false; results.Add(name + ": " + ex.Message); } }
                SmokeRegistry.Capture(opened[0], "window_" + Slug(name) + ".png");
                results.Add(name + ": " + opened[0].Title + (v ? "" : " (verify FAILED)"));
                if (!v) ok = false;
                await CloseAll(opened);
            }
            await One("git", () => { EditorCommands.GitWindow(); return Task.CompletedTask; }, async w =>
            {
                var g = w as GitWindow;
                for (int i = 0; i < 100 && g != null && (g.IsBusy || g.StatusText == "" || g.StatusText == "Loading…"); i++) await SmokeRegistry.Settle(100);
                await SmokeRegistry.Settle(300);
                results.Add("git status: " + g?.StatusText + " · changes " + g?.ChangeCount + " · commits " + g?.CommitCount);
                return g != null && !g.IsBusy && !string.IsNullOrEmpty(g.StatusText) && !g.StatusText.StartsWith("Git:");
            });
            await One("project settings", () => { EditorCommands.ProjectSettings(); return Task.CompletedTask; }, async w =>
            {
                var ps = w as ProjectSettingsWindow;
                await SmokeRegistry.Settle(600);
                if (ps == null) return false;
                for (int t = 1; t < ps.Tabs.ItemCount; t++) { ps.Tabs.SelectedIndex = t; await SmokeRegistry.Settle(250); SmokeRegistry.Capture(ps, "window_project_settings_tab" + t + ".png"); }
                ps.Tabs.SelectedIndex = 0;
                await SmokeRegistry.Settle(200);
                return ps.Tabs.ItemCount == 4;
            });
            await One("about", () => { EditorCommands.About(); return Task.CompletedTask; });
            await One("project hub", () => { EditorCommands.OpenProject(); return Task.CompletedTask; }, w => Task.FromResult(w is ProjectHubWindow hub && !hub.IsCreatePage && hub.ProjectCount >= 0));
            await One("keyboard shortcuts", () => { _ = EditorCommands.KeyboardShortcuts(); return Task.CompletedTask; }, settle: 600);
            await One("history", () => { EditorCommands.History(); return Task.CompletedTask; }, settle: 500);
            Log("shell windows:\n  " + string.Join("\n  ", results));
            // the main window with the menus' target panels (the macOS menu bar itself is outside the window)
            SmokeRegistry.Capture(Main, "shell_main_window.png");
            SmokeRegistry.Capture(Main.Hierarchy, "panel_hierarchy.png");
            return ok;
        }
    }
}
