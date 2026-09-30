using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;
using VortexEditor.Controls;
using VortexEditor.Shell;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>
    /// Inspector card of a <see cref="Script"/> component: the bound class + file (missing-file warning), open / change
    /// the script, and its PUBLIC FIELDS reflected from the compiled type — bool, int, float, string (with an asset
    /// browse button, stored project-relative), enum, Vector3 (colour swatch for colour-named fields) and 1-D arrays of
    /// any of them (per-element editors, add / remove). The value shown is the per-instance override, else the code
    /// default; setting a value back to the default REMOVES the override so untouched fields keep tracking the code.
    /// </summary>
    internal static class ScriptCard
    {
        private static readonly string[] AnyAsset = { "*.ventity", "*.vanim", "*.wav", "*.ogg", "*.mp3", "*.flac", "*.vsndc", "*.vmat", "*.glb", "*.gltf", "*.fbx", "*.png", "*.jpg", "*.vui", "*.vscene", "*.json", "*.txt" };

        public static IEnumerable<Control> Rows(Script sc, GameEntity entity)
        {
            var rows = new List<Control>();
            var cls = new TextBlock { Text = string.IsNullOrEmpty(sc.ScriptClassName) ? "(none)" : sc.ScriptClassName, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            rows.Add(Row("Class", cls));
            var file = new TextBlock { Text = sc.ScriptPath ?? "", Classes = { "small", "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(file, sc.ScriptPath);
            rows.Add(Row("File", file));
            string abs = ComponentEditors.ProjectRelativeToAbsolute(sc.ScriptPath);
            if (!string.IsNullOrEmpty(sc.ScriptPath) && !File.Exists(abs)) rows.Add(Warning("Script file not found in the project. Use Change… to re-link it."));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var open = new Button { Content = "Open in Editor", Classes = { "ghost" } };
            ToolTip.SetTip(open, "Open the script in the code editor (VS Code / Rider / Visual Studio)");
            open.Click += (s, e) => EditorCommands.OpenInIde(abs);
            var change = new Button { Content = "Change…", Classes = { "ghost" } };
            ToolTip.SetTip(change, "Swap this component to another script (its field overrides are dropped)");
            change.Click += (s, e) => ShowScriptMenu(change, sc, entity);
            actions.Children.Add(open); actions.Children.Add(change);
            rows.Add(Row("", actions));

            rows.AddRange(FieldRows(sc, entity));
            return rows;
        }

        // ---------------------------------------------------------------- script switching
        private static void ShowScriptMenu(Control anchor, Script sc, GameEntity entity)
        {
            var m = new MenuFlyout();
            try
            {
                foreach (var rel in ScriptingService.EnumerateScripts())
                {
                    var r = rel;
                    var mi = new MenuItem { Header = Path.GetFileNameWithoutExtension(r) };
                    if (string.Equals(r, sc.ScriptPath, StringComparison.OrdinalIgnoreCase)) mi.IsEnabled = false;
                    mi.Click += (s, e) => Swap(sc, entity, r);
                    m.Items.Add(mi);
                }
            }
            catch { }
            if (m.Items.Count > 0) m.Items.Add(new Separator());
            var browse = new MenuItem { Header = "Browse…" };
            browse.Click += async (s, e) =>
            {
                var p = await AssetPickerDialog.Pick("Scripts", new[] { "*.cs" });
                if (!string.IsNullOrEmpty(p)) Swap(sc, entity, ScriptingService.MakeRelative(ProjectData.Current?.Path ?? "", ComponentEditors.ProjectRelativeToAbsolute(p)));
            };
            var nw = new MenuItem { Header = "New Script…" };
            nw.Click += (s, e) =>
            {
                try
                {
                    string baseName = entity != null && !string.IsNullOrWhiteSpace(entity.Name) ? new string(entity.Name.Where(char.IsLetterOrDigit).ToArray()) + "Behaviour" : "NewBehaviour";
                    if (baseName.Length == 0 || char.IsDigit(baseName[0])) baseName = "NewBehaviour";
                    var path = ScriptingService.CreateScript(baseName);
                    Swap(sc, entity, ScriptingService.MakeRelative(ProjectData.Current?.Path ?? "", path));
                    EditorCommands.OpenInIde(path);
                }
                catch (Exception ex) { EditorCommands.Fail("New script", ex); }
            };
            m.Items.Add(browse); m.Items.Add(nw);
            m.ShowAt(anchor);
        }

        /// <summary>Replace the Script component with one for <paramref name="rel"/> (undoable remove + add, like the Windows editor).</summary>
        private static void Swap(Script old, GameEntity entity, string rel)
        {
            var e = entity ?? old.Entity;
            if (e == null || string.IsNullOrEmpty(rel)) return;
            var fresh = new Script(e, rel) { IsEnabled = old.IsEnabled };
            e.RemoveComponent(old);
            e.AddComponent(fresh);
            SceneRenderService.RuntimeDirty = true;
            ComponentEditors.RefreshInspectorSoon();
        }

        // ---------------------------------------------------------------- public fields (#47)
        private static IEnumerable<Control> FieldRows(Script sc, GameEntity entity)
        {
            var rows = new List<Control>();
            if (string.IsNullOrEmpty(sc.ScriptClassName)) return rows;
            Type type = null;
            try { type = ScriptRuntime.Instance.GetScriptTypeForInspector(sc.ScriptClassName); } catch { }
            if (type == null)
            {
                rows.Add(Hint("Fields unavailable — the project scripts don't compile right now (see the Console after pressing Play)."));
                return rows;
            }
            object defaults = null;
            try { defaults = Activator.CreateInstance(type); } catch { }
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => ScriptRuntime.IsInspectableFieldType(f.FieldType)).ToList();
            if (fields.Count == 0) { rows.Add(Hint("This script has no public fields.")); return rows; }

            var ctx = new FieldContext { Script = sc, Entity = entity, Defaults = defaults };
            var header = new DockPanel { Margin = new Thickness(0, 8, 0, 2) };
            var resetAll = new Button { Content = "Reset all", Classes = { "link" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(resetAll, "Drop every per-instance override — all fields show the script's code defaults");
            resetAll.Click += (s, e) => { foreach (var f in fields) sc.SetFieldValue(f.Name, null); ctx.MarkDirty(); ComponentEditors.RefreshInspectorSoon(); };
            DockPanel.SetDock(resetAll, Dock.Right);
            header.Children.Add(resetAll);
            header.Children.Add(Section("Fields"));
            rows.Add(header);
            foreach (var f in fields)
            {
                try { rows.Add(FieldRow(ctx, f)); }
                catch (Exception ex) { rows.Add(Warning(f.Name + ": " + ex.Message)); }
            }
            return rows;
        }

        private sealed class FieldContext
        {
            public Script Script;
            public GameEntity Entity;
            public object Defaults;

            public string Default(FieldInfo f)
            {
                try { return Defaults != null ? ScriptRuntime.FormatFieldValue(f.GetValue(Defaults)) : null; } catch { return null; }
            }
            public string Current(FieldInfo f) => Script.GetFieldValue(f.Name) ?? Default(f) ?? "";
            public bool IsOverridden(FieldInfo f) => Script.GetFieldValue(f.Name) != null;
            public void Store(FieldInfo f, string formatted)
            {
                // same-as-default removes the override so untouched instances keep tracking the code
                var def = Default(f);
                Script.SetFieldValue(f.Name, formatted == def ? null : formatted);
                MarkDirty();
            }
            public void MarkDirty()
            {
                var scene = Script.Entity?.Scene ?? Entity?.Scene;
                if (scene != null) scene.IsDirty = true;
            }
        }

        private static Control FieldRow(FieldContext ctx, FieldInfo f)
        {
            string label = Pretty(f.Name);
            string typeName = FriendlyTypeName(f.FieldType);
            Control editor;
            Action updateMark = null;
            if (f.FieldType.IsArray) editor = ArrayEditor(ctx, f);
            else
            {
                var ed = ScalarEditor(f.FieldType, f.Name, () => ctx.Current(f), v => { ctx.Store(f, v); updateMark?.Invoke(); });
                // a small ↺ reverts an overridden field to the code default
                var reset = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "Undo", Width = 12, Height = 12 }, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(reset, "Revert to the code default (" + (ctx.Default(f) ?? "?").Replace(ScriptRuntime.ArraySeparator, ',') + ")");
                reset.Click += (s, e) => { ctx.Script.SetFieldValue(f.Name, null); ctx.MarkDirty(); RefreshAll(); updateMark?.Invoke(); };
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                g.Children.Add(ed);
                Grid.SetColumn(reset, 1); g.Children.Add(reset);
                updateMark = () => reset.IsVisible = ctx.IsOverridden(f);
                updateMark();
                editor = g;
            }
            var row = f.FieldType.IsArray ? RowTop(label, editor, f.Name + " (" + typeName + ")") : Row(label, editor, f.Name + " (" + typeName + ")");
            return row;
        }

        private static string FriendlyTypeName(Type t) => t.IsArray ? FriendlyTypeName(t.GetElementType()) + "[]" : t.IsEnum ? t.Name : t == typeof(float) ? "float" : t == typeof(int) ? "int" : t == typeof(bool) ? "bool" : t == typeof(string) ? "string" : t.Name;

        private static bool LooksLikeColor(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            return n.Contains("color") || n.Contains("colour") || n.Contains("tint");
        }

        /// <summary>One editor for a SCALAR value (bool / enum / Vector3 / int / float / string) — shared by plain field rows
        /// and array elements. Values travel as the invariant strings the runtime parses.</summary>
        private static Control ScalarEditor(Type t, string name, Func<string> current, Action<string> store)
        {
            var ci = CultureInfo.InvariantCulture;
            if (t == typeof(bool))
            {
                var cb = new CheckBox { IsChecked = string.Equals(current(), "true", StringComparison.OrdinalIgnoreCase), MinHeight = 22 };
                cb.IsCheckedChanged += (s, e) => { string v = cb.IsChecked == true ? "true" : "false"; if (!string.Equals(v, current(), StringComparison.OrdinalIgnoreCase)) store(v); };
                Refreshers[cb] = () => cb.IsChecked = string.Equals(current(), "true", StringComparison.OrdinalIgnoreCase);
                return cb;
            }
            if (t.IsEnum)
            {
                var names = System.Enum.GetNames(t);
                var combo = new ComboBox { MinHeight = 22, HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var n in names) combo.Items.Add(Pretty(n));
                int Index() { var c = current(); int i = Array.FindIndex(names, n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase)); return i < 0 ? 0 : i; }
                combo.SelectedIndex = Index();
                combo.SelectionChanged += (s, e) => { if (combo.SelectedIndex >= 0 && !string.Equals(names[combo.SelectedIndex], current(), StringComparison.OrdinalIgnoreCase)) store(names[combo.SelectedIndex]); };
                Refreshers[combo] = () => combo.SelectedIndex = Index();
                return combo;
            }
            if (t == typeof(Vortex.Vector3))
            {
                float[] Parts()
                {
                    var p = (current() ?? "").Split(',');
                    var r = new float[3];
                    for (int i = 0; i < 3; i++) if (i < p.Length) TryParse(p[i], out r[i]);
                    return r;
                }
                void StoreParts(float[] v) => store(v[0].ToString("R", ci) + "," + v[1].ToString("R", ci) + "," + v[2].ToString("R", ci));
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto,*,Auto") };
                string[] axes = { "X", "Y", "Z" }; string[] keys = { "VxAxisXBrush", "VxAxisYBrush", "VxAxisZBrush" };
                var boxes = new TextBox[3];
                for (int i = 0; i < 3; i++)
                {
                    int axis = i;
                    var lbl = new TextBlock { Text = axes[i], FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = (IBrush)Application.Current.FindResource(keys[i]), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i == 0 ? 0 : 6, 0, 3, 0), Cursor = new Cursor(StandardCursorType.SizeWestEast) };
                    var box = new TextBox { Classes = { "number" }, MinHeight = 22, MinWidth = 36, Text = Fmt(Parts()[i]) };
                    boxes[i] = box;
                    void Commit()
                    {
                        var v = Parts();
                        if (box.Text == Fmt(v[axis])) return;   // unchanged display text: don't round the stored value
                        if (TryParse(box.Text, out var nv) && nv != v[axis]) { v[axis] = nv; StoreParts(v); }
                        box.Text = Fmt(Parts()[axis]);
                    }
                    box.LostFocus += (s, e) => Commit();
                    box.KeyDown += (s, e) => { if (e.Key == Key.Return) { Commit(); e.Handled = true; } };
                    AttachScrub(lbl, () => Parts()[axis], nv => { var v = Parts(); v[axis] = nv; StoreParts(v); box.Text = Fmt(nv); }, 0.1);
                    Grid.SetColumn(lbl, i * 2); Grid.SetColumn(box, i * 2 + 1);
                    g.Children.Add(lbl); g.Children.Add(box);
                }
                Action refreshBoxes = () => { var v = Parts(); for (int i = 0; i < 3; i++) if (!boxes[i].IsFocused) boxes[i].Text = Fmt(v[i]); };
                Refreshers[g] = refreshBoxes;
                if (LooksLikeColor(name))
                {
                    // colour-named Vector3 (0..1 RGB): a swatch next to the numbers
                    var pickerHost = Color(() => { var v = Parts(); return (v[0], v[1], v[2]); }, (r, gg, b) => { StoreParts(new[] { r, gg, b }); refreshBoxes(); });
                    pickerHost.Width = 44; pickerHost.Margin = new Thickness(6, 0, 0, 0);
                    ToolTip.SetTip(pickerHost, "Pick a colour (stored as RGB 0..1)");
                    Grid.SetColumn(pickerHost, 6);
                    g.Children.Add(pickerHost);
                }
                return g;
            }
            // int / float / string
            var tb = new TextBox { Text = current() ?? "", MinHeight = 22 };
            if (t == typeof(int) || t == typeof(float)) tb.Classes.Add("number");
            void CommitText()
            {
                string text = tb.Text ?? "";
                try
                {
                    // validate + canonicalize through the real parser; garbage keeps the old stored value
                    object parsed = ScriptRuntime.ParseFieldValue(t, t == typeof(string) ? text : text.Trim());
                    string formatted = t == typeof(string) ? text : ScriptRuntime.FormatFieldValue(parsed);
                    if (formatted != current()) store(formatted);
                }
                catch { }
                tb.Text = current() ?? "";
            }
            tb.LostFocus += (s, e) => CommitText();
            tb.KeyDown += (s, e) => { if (e.Key == Key.Return) { CommitText(); e.Handled = true; } else if (e.Key == Key.Escape) { tb.Text = current() ?? ""; e.Handled = true; } };
            if (t == typeof(int) || t == typeof(float))
            {
                tb.PointerWheelChanged += (s, e) =>
                {
                    if (!tb.IsFocused) return;
                    double step = (t == typeof(int) ? 1 : 0.1) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1) * (e.Delta.Y > 0 ? 1 : -1);
                    if (t == typeof(int) && int.TryParse(current(), NumberStyles.Integer, ci, out int iv)) store((iv + (int)step).ToString(ci));
                    else if (t == typeof(float) && TryParse(current(), out float fv)) store(((float)(fv + step)).ToString("R", ci));
                    tb.Text = current(); e.Handled = true;
                };
            }
            Refreshers[tb] = () => { if (!tb.IsFocused) tb.Text = current() ?? ""; };
            if (t != typeof(string)) return tb;

            // strings: pick / drop any project asset — stored project-relative (prefab lists, clip paths, sounds…)
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(tb);
            var browse = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Folder" }, Margin = new Thickness(4, 0, 0, 0) };
            ToolTip.SetTip(browse, "Pick a project asset (prefab, clip, sound…) — stored as a project-relative path");
            browse.Click += async (s, e) =>
            {
                var p = await AssetPickerDialog.Pick("Assets", AnyAsset);
                if (p == null) return;
                store(p); tb.Text = current() ?? "";
            };
            DragDrop.SetAllowDrop(tb, true);
            tb.AddHandler(DragDrop.DragOverEvent, (s, e) => { e.DragEffects = DroppedPath(e, "vortex/asset") != null ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; });
            tb.AddHandler(DragDrop.DropEvent, (s, e) => { var p = DroppedPath(e, "vortex/asset"); if (p != null) { store(ToProjectRelative(p)); tb.Text = current() ?? ""; e.Handled = true; } });
            Grid.SetColumn(browse, 1);
            grid.Children.Add(browse);
            return grid;
        }

        /// <summary>List editor for ARRAY fields: one row per element (editor + remove) plus "+ Add". The whole array
        /// round-trips as ONE stored string joined with ScriptRuntime.ArraySeparator.</summary>
        private static Control ArrayEditor(FieldContext ctx, FieldInfo f)
        {
            var elemType = f.FieldType.GetElementType();
            var panel = new StackPanel { Spacing = 3 };
            List<string> elems = null;

            void Load()
            {
                var raw = ctx.Current(f);
                elems = string.IsNullOrEmpty(raw) ? new List<string>() : new List<string>(raw.Split(ScriptRuntime.ArraySeparator));
            }
            void StoreAll()
            {
                var joined = string.Join(ScriptRuntime.ArraySeparator.ToString(), elems);
                try { ScriptRuntime.ParseFieldValue(f.FieldType, joined); ctx.Store(f, joined); } catch { }
            }
            void Rebuild()
            {
                panel.Children.Clear();
                for (int i = 0; i < elems.Count; i++)
                {
                    int idx = i;
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*,Auto,Auto") };
                    row.Children.Add(new TextBlock { Text = i.ToString(CultureInfo.InvariantCulture), Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
                    var ed = ScalarEditor(elemType, f.Name, () => idx < elems.Count ? elems[idx] : "", v => { if (idx < elems.Count) { elems[idx] = v; StoreAll(); } });
                    Grid.SetColumn(ed, 1); row.Children.Add(ed);
                    var up = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "ChevronDown", Width = 11, Height = 11, RenderTransform = new RotateTransform(180) }, IsEnabled = i > 0, Margin = new Thickness(2, 0, 0, 0) };
                    ToolTip.SetTip(up, "Move up");
                    up.Click += (s, e) => { if (idx <= 0) return; (elems[idx - 1], elems[idx]) = (elems[idx], elems[idx - 1]); StoreAll(); Rebuild(); };
                    Grid.SetColumn(up, 2); row.Children.Add(up);
                    var rm = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "Close", Width = 11, Height = 11 } };
                    ToolTip.SetTip(rm, "Remove element " + i);
                    rm.Click += (s, e) => { if (idx < elems.Count) { elems.RemoveAt(idx); StoreAll(); Rebuild(); } };
                    Grid.SetColumn(rm, 3); row.Children.Add(rm);
                    panel.Children.Add(row);
                }
                var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var add = new Button { Content = "+ Add", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
                add.Click += (s, e) =>
                {
                    // default element = the scalar default ("" / "0" / "false" / first enum name / "0,0,0")
                    string def = "";
                    try { def = ScriptRuntime.FormatFieldValue(elemType.IsValueType ? Activator.CreateInstance(elemType) : (object)""); } catch { }
                    elems.Add(def);
                    StoreAll(); Rebuild();
                };
                bar.Children.Add(add);
                bar.Children.Add(new TextBlock { Text = elems.Count + (elems.Count == 1 ? " element" : " elements") + (ctx.IsOverridden(f) ? "" : " (code default)"), Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
                if (ctx.IsOverridden(f))
                {
                    var reset = new Button { Content = "Reset", Classes = { "link" }, FontSize = 11 };
                    ToolTip.SetTip(reset, "Back to the script's default list");
                    reset.Click += (s, e) => { ctx.Script.SetFieldValue(f.Name, null); ctx.MarkDirty(); Load(); Rebuild(); };
                    bar.Children.Add(reset);
                }
                panel.Children.Add(bar);
            }
            Load();
            Rebuild();
            Refreshers[panel] = () => { if (panel.IsKeyboardFocusWithin) return; Load(); Rebuild(); };
            return panel;
        }
    }
}
