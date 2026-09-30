using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.ECS;
using Editor.ECS.Components.Animation;
using VortexEditor.Services;

namespace VortexEditor.Shell.Prefab
{
    /// <summary>
    /// The editor-side prefab workflow on top of <see cref="PrefabService"/> (port of the Windows editor's scene
    /// hierarchy / asset browser / inspector prefab actions): save an entity as a prefab, place / apply / revert /
    /// unpack instances, select the asset, keep thumbnails, the asset browser and open Prefab Editors in sync.
    /// Every panel (hierarchy, asset browser, inspector, prefab editor) calls these so the behaviour is identical
    /// everywhere. Interactive variants ask / confirm with sheets and report with toasts; the plain variants don't.
    /// </summary>
    public static class PrefabWorkflow
    {
        public const string Extension = PrefabService.PrefabExtension;
        private static bool _helpShown;

        public static string ProjectRoot => ProjectData.Current?.Path;

        // ------------------------------------------------------------------ paths
        /// <summary>Absolute path of a prefab reference (project-relative or absolute).</summary>
        public static string Resolve(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath)) return prefabPath;
            string p = prefabPath.Replace('\\', '/');
            if (Path.IsPathRooted(p)) return Path.GetFullPath(p);
            var root = ProjectRoot;
            return string.IsNullOrEmpty(root) ? p : Path.GetFullPath(Path.Combine(root, p));
        }

        /// <summary>Project-relative, '/'-separated form of an absolute path (unchanged when outside the project).</summary>
        public static string Relative(string path)
        {
            var root = ProjectRoot;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root) || !Path.IsPathRooted(path)) return path?.Replace('\\', '/');
            return ScriptingService.MakeRelative(root, path);
        }

        public static bool IsPrefabFile(string path) => !string.IsNullOrEmpty(path) && (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vprefab", StringComparison.OrdinalIgnoreCase));

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Resolve(a).TrimEnd('/', '\\'), Resolve(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>The nearest entity (self or ancestor) that is linked to a prefab asset.</summary>
        public static GameEntity FindInstanceRoot(GameEntity e)
        {
            for (var p = e; p != null; p = p.Parent) if (p.IsPrefabInstance) return p;
            return null;
        }

        /// <summary>Every instance of the prefab across all open scenes (nested instances included).</summary>
        public static List<GameEntity> InstancesOf(string prefabPath)
        {
            var list = new List<GameEntity>();
            var project = ProjectData.Current;
            if (project?.Scenes == null || string.IsNullOrEmpty(prefabPath)) return list;
            string full = Resolve(prefabPath);
            void Walk(IEnumerable<GameEntity> es)
            {
                if (es == null) return;
                foreach (var e in es)
                {
                    if (e == null) continue;
                    if (e.IsPrefabInstance && SamePath(e.PrefabPath, full)) list.Add(e);
                    Walk(e.Children);
                }
            }
            foreach (var s in project.Scenes) Walk(s?.Entities);
            return list;
        }

        /// <summary>True when the entity still lives in a scene (not removed by a revert / delete).</summary>
        public static bool IsInScene(GameEntity e)
        {
            if (e == null) return false;
            var top = e; while (top.Parent != null) { if (!top.Parent.Children.Contains(top)) return false; top = top.Parent; }
            return top.Scene?.Entities != null && top.Scene.Entities.Contains(top);
        }

        // ------------------------------------------------------------------ save as prefab
        /// <summary>Save the entity (with its children) as Assets/Prefabs/&lt;name&gt;.ventity and link it (it becomes an
        /// instance). Non-interactive; refreshes thumbnails, the asset browser and open prefab editors.</summary>
        public static string SaveAsPrefab(GameEntity entity, string name = null)
        {
            if (entity == null || ProjectData.Current == null) return null;
            var path = PrefabService.Instance.SaveAsPrefab(entity, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
            if (path != null) AfterPrefabWritten(path);
            return path;
        }

        /// <summary>"Save as Prefab…": ask for the name (default = entity name), confirm overwriting an existing prefab,
        /// save + link, toast, and explain the workflow the first time.</summary>
        public static async Task<string> SaveAsPrefabInteractive(GameEntity entity, Window owner = null)
        {
            if (entity == null) return null;
            if (ProjectData.Current == null) { EditorCommands.Toast("Open a project first"); return null; }
            var name = await Prompt(owner, "Save as Prefab", "Name of the new prefab (saved in Assets/Prefabs). \"" + entity.Name + "\" becomes a linked instance of it.", entity.Name, "Save");
            if (string.IsNullOrWhiteSpace(name)) return null;
            string target = Path.Combine(ProjectRoot, "Assets", "Prefabs", SanitizeFileName(name.Trim()) + Extension);
            if (File.Exists(target) && !await Confirm(owner, "Replace \"" + Path.GetFileName(target) + "\"?", "A prefab with this name already exists. Its instances keep their link and are reloaded with the new content.", "Replace", "Cancel", destructive: true))
                return null;
            try
            {
                bool existed = File.Exists(target);
                var path = SaveAsPrefab(entity, name);
                if (path == null) { EditorCommands.Toast("Could not save the prefab"); return null; }
                if (existed)
                {
                    // other instances of the replaced prefab pick up the new template (this entity keeps its state)
                    foreach (var inst in InstancesOf(path)) if (!ReferenceEquals(inst, entity)) { try { PrefabService.Instance.RevertInstance(inst); } catch { } }
                    SceneRenderService.RuntimeDirty = true;
                }
                EditorCommands.Toast("Prefab saved — '" + entity.Name + "' is now a linked instance");
                if (!_helpShown)
                {
                    _helpShown = true;
                    await Sheet(owner, "Prefab saved — how prefabs work", PrefabService.WorkflowHelp + "\n\nSaved to:  " + Relative(path), new[] { "Got it" });
                }
                return path;
            }
            catch (Exception ex) { EditorCommands.Fail("Save as prefab", ex); return null; }
        }

        // ------------------------------------------------------------------ place
        /// <summary>Drop a fresh linked instance into the active scene (undoable), select it and toast. Returns it.</summary>
        public static GameEntity PlaceInScene(string prefabPath, GameEntity parent = null, bool select = true)
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { EditorCommands.Toast("No active scene — open or create a scene first"); return null; }
            string full = Resolve(prefabPath);
            if (string.IsNullOrEmpty(full) || !File.Exists(full)) { EditorCommands.Toast("Prefab file not found"); return null; }
            GameEntity inst = null;
            try { inst = PrefabService.Instance.InstantiatePrefab(full, scene, parent); }
            catch (Exception ex) { EditorCommands.Fail("Place prefab", ex); return null; }
            if (inst == null) { EditorCommands.Toast("Could not instantiate the prefab (empty or unreadable)"); return null; }
            SceneRenderService.RuntimeDirty = true;
            if (select) SelectionService.Instance.Select(inst);
            EditorCommands.Toast("Added '" + inst.Name + "' to the scene");
            return inst;
        }

        // ------------------------------------------------------------------ apply / revert / unpack
        /// <summary>Write the instance's state into its prefab asset and update every other instance.</summary>
        public static bool Apply(GameEntity instance)
        {
            var root = FindInstanceRoot(instance);
            if (root == null) return false;
            string full = Resolve(root.PrefabPath);
            bool ok;
            try { ok = PrefabService.Instance.ApplyToPrefab(root); }
            catch (Exception ex) { EditorCommands.Fail("Apply to prefab", ex); return false; }
            if (ok) AfterPrefabWritten(full);
            SceneRenderService.RuntimeDirty = true;
            return ok;
        }

        public static async Task<bool> ApplyInteractive(GameEntity instance, Window owner = null)
        {
            var root = FindInstanceRoot(instance);
            if (root == null) return false;
            string full = Resolve(root.PrefabPath);
            if (!File.Exists(full)) { await Sheet(owner, "Prefab asset missing", "\"" + root.PrefabPath + "\" does not exist any more. Use Save as Prefab to create it again.", new[] { "OK" }); return false; }
            int others = InstancesOf(full).Count(e => !ReferenceEquals(e, root));
            string msg = "Overwrites " + Path.GetFileName(full) + " with the current state of \"" + root.Name + "\"" +
                         (others > 0 ? " and updates " + others + " other instance" + (others == 1 ? "" : "s") + " (each keeps its own transform)." : ".");
            if (!await Confirm(owner, "Apply overrides to prefab?", msg, "Apply", "Cancel")) return false;
            bool ok = Apply(root);
            EditorCommands.Toast(ok ? "Applied to prefab — " + Path.GetFileName(full) : "Could not apply to the prefab");
            return ok;
        }

        /// <summary>Replace the instance with a fresh copy of its prefab (keeps the transform) and select the new copy.</summary>
        public static GameEntity Revert(GameEntity instance, bool select = true)
        {
            var root = FindInstanceRoot(instance);
            if (root == null) return null;
            bool wasSelected = ReferenceEquals(SelectionService.Instance.SelectedEntity, instance) || ReferenceEquals(SelectionService.Instance.SelectedEntity, root);
            GameEntity fresh = null;
            try { fresh = PrefabService.Instance.RevertInstance(root); }
            catch (Exception ex) { EditorCommands.Fail("Revert to prefab", ex); return null; }
            SceneRenderService.RuntimeDirty = true;
            if (fresh != null && (select || wasSelected)) SelectionService.Instance.Select(fresh);
            return fresh;
        }

        public static async Task<GameEntity> RevertInteractive(GameEntity instance, Window owner = null)
        {
            var root = FindInstanceRoot(instance);
            if (root == null) return null;
            if (!File.Exists(Resolve(root.PrefabPath))) { await Sheet(owner, "Prefab asset missing", "\"" + root.PrefabPath + "\" does not exist any more — there is nothing to revert to.", new[] { "OK" }); return null; }
            if (!await Confirm(owner, "Revert to prefab?", "Discards every local change of \"" + root.Name + "\" and reloads it from " + Path.GetFileName(root.PrefabPath) + " (its position, rotation and scale are kept). This cannot be undone.", "Revert", "Cancel", destructive: true))
                return null;
            var fresh = Revert(root);
            EditorCommands.Toast(fresh != null ? "Reverted to prefab" : "Could not revert the instance");
            return fresh;
        }

        /// <summary>Break the link to the prefab (the entity becomes a plain, unique entity). <paramref name="completely"/>
        /// also unlinks nested prefab instances below it. Undoable unless <paramref name="undoable"/> is false.</summary>
        public static void Unpack(GameEntity instance, bool completely = false, bool undoable = true)
        {
            var root = FindInstanceRoot(instance);
            if (root == null) return;
            var targets = new List<(GameEntity e, string path)>();
            void Collect(GameEntity e, bool top)
            {
                if (e == null) return;
                if (e.IsPrefabInstance && (top || completely)) targets.Add((e, e.PrefabPath));
                if (completely) foreach (var c in e.Children) Collect(c, false);
            }
            Collect(root, true);
            if (targets.Count == 0) return;
            void Do() { foreach (var t in targets) t.e.PrefabPath = null; }
            void Undo() { foreach (var t in targets) t.e.PrefabPath = t.path; }
            if (undoable) UndoRedoManager.Instance.Execute(new ActionCommand("Unpack prefab " + root.Name, Do, Undo));
            else Do();
        }

        // ------------------------------------------------------------------ asset
        /// <summary>Show the prefab asset in the Project panel (navigate to its folder and select it).</summary>
        public static void SelectAsset(string prefabPath)
        {
            string full = Resolve(prefabPath);
            var w = EditorCommands.Window;
            if (w == null) return;
            if (string.IsNullOrEmpty(full) || !File.Exists(full)) { EditorCommands.Toast("Prefab file not found: " + prefabPath); return; }
            try { w.BottomTabs.SelectedIndex = 0; } catch { }
            var ab = w.AssetBrowser;
            if (ab == null) return;
            try { ab.Navigate(Path.GetDirectoryName(full)); ab.SelectPath(full); } catch { }
            Dispatcher.UIThread.Post(() => { try { ab.SelectPath(full); } catch { } }, DispatcherPriority.Background);
        }

        public static void OpenInEditor(string prefabPath)
        {
            string full = Resolve(prefabPath);
            if (string.IsNullOrEmpty(full) || !File.Exists(full)) { EditorCommands.Toast("Prefab file not found: " + prefabPath); return; }
            EditorWindows.PrefabEditor(full);
        }

        /// <summary>After a .ventity was (re)written: fresh thumbnail, asset browser, open prefab editors.</summary>
        public static void AfterPrefabWritten(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            try { ThumbnailService.Invalidate(fullPath); } catch { }
            try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
            try { EditorCommands.Window?.AssetBrowser?.Refresh(); } catch { }
            try { PrefabEditorWindow.NotifyChangedOnDisk(fullPath); } catch { }
        }

        // ------------------------------------------------------------------ template building
        public static readonly string[] ModelExtensions = { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".3ds", ".blend" };

        public static bool IsModelFile(string path) => Array.IndexOf(ModelExtensions, Path.GetExtension(path ?? "").ToLowerInvariant()) >= 0;

        /// <summary>An in-memory entity (no scene, no engine sync, no undo) that draws a model the way placing it does:
        /// one MeshRenderer, or a container with one locked child per submesh, each bound to its sidecar
        /// materials/submesh_N.vmat (the structure <see cref="PrefabService.CreatePrefabFromModel"/> writes).</summary>
        public static GameEntity BuildModelEntity(string modelPath, string name = null)
        {
            string full = Resolve(modelPath), rel = Relative(full);
            var root = new GameEntity(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(full) : name);
            int count = 1;
            try { count = Editor.DllWrapper.VortexAPI.GetSubmeshCount(full); } catch { count = 1; }
            string Vmat(int i)
            {
                try
                {
                    string v = Path.Combine(Path.GetDirectoryName(rel) ?? "", "materials", "submesh_" + i + ".vmat").Replace('\\', '/');
                    return File.Exists(Path.Combine(ProjectRoot ?? "", v)) ? v : null;
                }
                catch { return null; }
            }
            if (count > 1)
            {
                string[] names = null;
                try { names = Editor.DllWrapper.VortexAPI.GetSubmeshNames(full, count); } catch { }
                for (int i = 0; i < count; i++)
                {
                    var child = new GameEntity(names != null && i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : "Submesh_" + i) { IsLockedToParent = true };
                    child.AddComponentDirect(new Editor.ECS.Components.Rendering.MeshRenderer(child) { MeshPath = rel + "#submesh" + i, MaterialPath = Vmat(i) });
                    child.Parent = root;
                    root.Children.Add(child);
                }
            }
            else root.AddComponentDirect(new Editor.ECS.Components.Rendering.MeshRenderer(root) { MeshPath = rel, MaterialPath = Vmat(0) });
            return root;
        }

        /// <summary>A linked copy of a prefab for nesting inside another template (new ids, PrefabPath set).</summary>
        public static GameEntity LoadNestedInstance(string prefabPath)
        {
            string full = Resolve(prefabPath);
            var e = Editor.Core.Serialization.DataSerializer.LoadFromJson<GameEntity>(full);
            if (e == null) return null;
            e.RegenerateIds();
            e.PrefabPath = Relative(full);
            return e;
        }

        // ------------------------------------------------------------------ template hygiene
        /// <summary>Rewrite absolute under-project asset paths on the subtree to project-relative (the portable form a
        /// prefab must store). Uses the private backing fields where they exist so no engine handle is reloaded.</summary>
        public static void NormalizeAssetPaths(GameEntity e)
        {
            var root = ProjectRoot;
            if (e == null || string.IsNullOrEmpty(root)) return;
            foreach (var comp in e.Components)
            {
                if (comp == null) continue;
                foreach (var p in comp.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.PropertyType != typeof(string) || !p.CanRead || !p.CanWrite || !p.Name.EndsWith("Path", StringComparison.Ordinal)) continue;
                    if (p.GetCustomAttribute<DataMemberAttribute>() == null) continue;
                    string v;
                    try { v = p.GetValue(comp) as string; } catch { continue; }
                    if (string.IsNullOrEmpty(v) || !Path.IsPathRooted(v)) continue;
                    string rel = Relative(v);
                    if (string.Equals(rel, v, StringComparison.Ordinal)) continue;
                    var field = FindField(comp.GetType(), "_" + char.ToLowerInvariant(p.Name[0]) + p.Name.Substring(1));
                    try { if (field != null && field.FieldType == typeof(string)) field.SetValue(comp, rel); else p.SetValue(comp, rel); } catch { }
                }
                if (comp is Animator an && an.Clips != null)
                    foreach (var clip in an.Clips) if (clip != null && !string.IsNullOrEmpty(clip.Path) && Path.IsPathRooted(clip.Path)) clip.Path = Relative(clip.Path);
            }
            foreach (var c in e.Children) NormalizeAssetPaths(c);
        }

        private static FieldInfo FindField(Type t, string name)
        {
            for (var x = t; x != null; x = x.BaseType)
            {
                var f = x.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null) return f;
            }
            return null;
        }

        private static readonly FieldInfo ActiveField = typeof(GameEntity).GetField("_isActive", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo RaiseChanged = typeof(Editor.Core.ViewModelBase).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);

        /// <summary>Set activeSelf on an entity that is NOT in a scene (a prefab template being edited) without the
        /// engine sync the IsActive setter performs — that sync would create a stray engine entity in the default scene.</summary>
        public static void SetActiveDetached(GameEntity e, bool active)
        {
            if (e == null || e.IsActive == active) return;
            if (ActiveField == null) { e.IsActive = active; return; }
            ActiveField.SetValue(e, active);
            try { RaiseChanged?.Invoke(e, new object[] { nameof(GameEntity.IsActive) }); } catch { }
        }

        public static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder();
            foreach (var c in name ?? "Prefab") sb.Append(Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\' ? '_' : c);
            return sb.Length == 0 ? "Prefab" : sb.ToString();
        }

        // ------------------------------------------------------------------ sheets (owner-aware)
        /// <summary>A sheet-style dialog (the editor's alert look) owned by <paramref name="owner"/> (default: the main
        /// window). Returns the index of the pressed button (the last button on Escape / close) and the prompt text.</summary>
        public static async Task<(int button, string text)> SheetWithText(Window owner, string title, string message, string[] buttons, string promptInitial = null, int destructiveIndex = -1)
        {
            owner = owner ?? (Window)EditorCommands.Window ?? Dialogs.Owner;
            var win = new Window
            {
                Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                SystemDecorations = SystemDecorations.BorderOnly, ShowInTaskbar = false,
                Background = Brushes.Transparent, TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }
            };
            int result = buttons.Length - 1;
            TextBox box = null;
            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(22, 18, 22, 16) };
            if (Application.Current.TryFindResource("IconVortex", out var g) && g is Geometry geo)
                panel.Children.Add(new PathIcon { Data = geo, Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Res("VxAccentBrush") });
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
            if (!string.IsNullOrEmpty(message))
                panel.Children.Add(new TextBlock { Text = message, Classes = { "secondary" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 380 });
            if (promptInitial != null)
            {
                box = new TextBox { Text = promptInitial, Margin = new Thickness(0, 6, 0, 0) };
                panel.Children.Add(box);
                win.Opened += (s, e) => { box.Focus(); box.SelectAll(); };
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = new Button { Content = buttons[i], MinWidth = 84 };
                if (i == 0) b.Classes.Add(destructiveIndex == 0 ? "danger" : "accent");
                else if (i == destructiveIndex) b.Classes.Add("danger");
                b.Click += (s, e) => { result = idx; win.Close(); };
                if (i == 0) b.IsDefault = true; else if (i == buttons.Length - 1) b.IsCancel = true;
                row.Children.Add(b);
            }
            panel.Children.Add(row);
            win.Content = new Border { Background = Res("VxPanelRaisedBrush"), BorderBrush = Res("VxSeparatorBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = panel };
            if (owner != null && owner.IsVisible) await win.ShowDialog(owner);
            else { var tcs = new TaskCompletionSource<bool>(); win.Closed += (s, e) => tcs.TrySetResult(true); win.Show(); await tcs.Task; }
            return (result, box?.Text ?? "");
        }

        public static async Task<int> Sheet(Window owner, string title, string message, string[] buttons, int destructiveIndex = -1)
            => (await SheetWithText(owner, title, message, buttons, null, destructiveIndex)).button;

        public static async Task<bool> Confirm(Window owner, string title, string message, string yes = "OK", string no = "Cancel", bool destructive = false)
            => await Sheet(owner, title, message, new[] { yes, no }, destructive ? 0 : -1) == 0;

        public static async Task<string> Prompt(Window owner, string title, string message, string initial, string ok = "OK")
        {
            var r = await SheetWithText(owner, title, message, new[] { ok, "Cancel" }, initial ?? "");
            return r.button == 0 ? r.text : null;
        }

        internal static IBrush Res(string key)
            => Application.Current != null && Application.Current.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Gray;

        /// <summary>Make a tool window the owner of the shared pickers / dialogs while it is the active window, so
        /// asset pickers opened from it (inspector rows) center on and block it rather than the main window.</summary>
        internal static void AdoptDialogOwnership(Window w)
        {
            w.Activated += (s, e) => Dialogs.Owner = w;
            void Restore() { if (ReferenceEquals(Dialogs.Owner, w)) Dialogs.Owner = EditorCommands.Window; }
            w.Deactivated += (s, e) => Restore();
            w.Closed += (s, e) => Restore();
        }
    }
}
