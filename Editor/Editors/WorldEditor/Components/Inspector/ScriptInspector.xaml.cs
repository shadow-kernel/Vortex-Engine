using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Editor.Core.Services;
using Editor.ECS.Components.Scripting;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    /// <summary>Inspector for a <see cref="Script"/> component: shows the bound class + path, opens it in
    /// Visual Studio, lets you change or detach it.</summary>
    public partial class ScriptInspector : UserControl
    {
        private Script _script;

        /// <summary>Raised when the user clicks the detach (trash) button.</summary>
        public event EventHandler DetachRequested;
        /// <summary>Raised when the user wants to swap the assigned script.</summary>
        public event EventHandler ChangeRequested;

        public ScriptInspector()
        {
            InitializeComponent();
        }

        public Script ScriptComponent
        {
            get { return _script; }
            set { _script = value; Bind(); }
        }

        private void Bind()
        {
            if (_script == null) return;
            ClassText.Text = string.IsNullOrEmpty(_script.ScriptClassName) ? "(none)" : _script.ScriptClassName;
            PathText.Text = _script.ScriptPath ?? "";
            WarnPanel.Visibility = ScriptFileExists() ? Visibility.Collapsed : Visibility.Visible;
            BuildFieldRows();
        }

        // ---- public script fields (#47) ----
        // Rows are reflected from the compiled script type (compiled on demand in edit mode, cached by
        // script write time). Value shown = the stored per-instance override, else the code default.
        // Editing stores an override on the Script component (serialized into scene + prefab); setting
        // a value back to the code default REMOVES the override, so untouched fields track the code.

        private object _defaults;   // a throwaway instance of the script type, for code-default values

        private void BuildFieldRows()
        {
            FieldsPanel.Children.Clear();
            FieldsHint.Visibility = Visibility.Collapsed;
            _defaults = null;
            if (_script == null || string.IsNullOrEmpty(_script.ScriptClassName)) return;

            Type type = null;
            try { type = Editor.Scripting.ScriptRuntime.Instance.GetScriptTypeForInspector(_script.ScriptClassName); }
            catch { }
            if (type == null)
            {
                FieldsHint.Text = "Fields unavailable — the project scripts don't compile right now (see the Console after pressing Play).";
                FieldsHint.Visibility = Visibility.Visible;
                return;
            }

            try { _defaults = Activator.CreateInstance(type); } catch { _defaults = null; }

            bool any = false;
            foreach (var f in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!Editor.Scripting.ScriptRuntime.IsInspectableFieldType(f.FieldType)) continue;
                AddFieldRow(f);
                any = true;
            }
            if (any)
            {
                var header = new TextBlock
                {
                    Text = "FIELDS",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 4),
                };
                FieldsPanel.Children.Insert(0, header);
            }
        }

        private string CurrentValueString(System.Reflection.FieldInfo f)
        {
            var stored = _script.GetFieldValue(f.Name);
            if (stored != null) return stored;
            try { return Editor.Scripting.ScriptRuntime.FormatFieldValue(f.GetValue(_defaults)); }
            catch { return ""; }
        }

        private string DefaultValueString(System.Reflection.FieldInfo f)
        {
            try { return Editor.Scripting.ScriptRuntime.FormatFieldValue(f.GetValue(_defaults)); }
            catch { return null; }
        }

        private void StoreField(System.Reflection.FieldInfo f, string formatted)
        {
            // Same-as-default removes the override so untouched instances keep tracking the code.
            var def = DefaultValueString(f);
            _script.SetFieldValue(f.Name, formatted == def ? null : formatted);
            var scene = _script.Entity != null ? _script.Entity.Scene : null;
            if (scene != null) scene.IsDirty = true;
        }

        private void AddFieldRow(System.Reflection.FieldInfo f)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = NiceName(f.Name),
                FontSize = 11.5,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0xA1)),
                VerticalAlignment = f.FieldType.IsArray ? VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = f.FieldType.IsArray ? new Thickness(0, 5, 0, 0) : new Thickness(0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = f.Name + " (" + FriendlyTypeName(f.FieldType) + ")",
            };
            row.Children.Add(label);

            FrameworkElement editor = f.FieldType.IsArray
                ? MakeArrayEditor(f)
                : MakeScalarEditor(f.FieldType, () => CurrentValueString(f), v => StoreField(f, v));

            Grid.SetColumn(editor, 1);
            row.Children.Add(editor);
            FieldsPanel.Children.Add(row);
        }

        private static string FriendlyTypeName(Type t)
            => t.IsArray ? FriendlyTypeName(t.GetElementType()) + "[]" : t.Name;

        /// <summary>One editor control for a SCALAR value (bool/enum/Vector3/int/float/string) —
        /// shared by plain field rows and array elements. String editors get a browse ("…") button
        /// that stores a project-relative asset path (prefab lists, clip paths, sounds…).</summary>
        private FrameworkElement MakeScalarEditor(Type t, Func<string> current, Action<string> store)
        {
            if (t == typeof(bool))
            {
                var cb = new CheckBox
                {
                    IsChecked = string.Equals(current(), "true", StringComparison.OrdinalIgnoreCase),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                cb.Checked += (s, e) => store("true");
                cb.Unchecked += (s, e) => store("false");
                return cb;
            }
            if (t.IsEnum)
            {
                var combo = new ComboBox { FontSize = 11.5, Height = 24 };
                foreach (var n in Enum.GetNames(t)) combo.Items.Add(n);
                var cur = current();
                combo.SelectedItem = combo.Items.Contains(cur) ? cur : (combo.Items.Count > 0 ? combo.Items[0] : null);
                combo.SelectionChanged += (s, e) => { if (combo.SelectedItem != null) store((string)combo.SelectedItem); };
                return combo;
            }
            if (t == typeof(Vortex.Vector3))
            {
                var parts = (current() + ",,").Split(',');
                var panel = new Grid();
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var boxes = new TextBox[3];
                for (int i = 0; i < 3; i++)
                {
                    var tb = MakeValueBox(i < parts.Length ? parts[i] : "0");
                    tb.Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0);
                    Grid.SetColumn(tb, i);
                    panel.Children.Add(tb);
                    boxes[i] = tb;
                }
                Action commitV = () =>
                {
                    var vals = new float[3];
                    for (int i = 0; i < 3; i++)
                        if (!float.TryParse(boxes[i].Text, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out vals[i])) return;
                    store(vals[0].ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ","
                        + vals[1].ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ","
                        + vals[2].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                };
                foreach (var tb in boxes)
                {
                    tb.LostFocus += (s, e) => commitV();
                    tb.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) commitV(); };
                }
                return panel;
            }

            // int / float / string
            var box = MakeValueBox(current());
            Action commit = () =>
            {
                var text = box.Text;
                try
                {
                    // validate through the real parser; garbage keeps the old stored value
                    Editor.Scripting.ScriptRuntime.ParseFieldValue(t, text);
                    store(text);
                }
                catch { box.Text = current(); }
            };
            box.LostFocus += (s, e) => commit();
            box.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) commit(); };

            if (t != typeof(string)) return box;

            // Strings: add a browse button — pick any project asset, stored project-relative.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(box);
            var browse = new Button
            {
                Content = "…",
                FontSize = 11.5,
                Width = 24,
                Height = 24,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Pick a project asset (prefab, clip, sound…) — stored as a project-relative path",
            };
            browse.Click += (s, e) =>
            {
                // MUST use the engine's STA FilePicker (raw OpenFileDialog deadlocks the DXGI apartment).
                var root = ScriptingService.ProjectRoot;
                string picked = Editor.Core.Util.FilePicker.OpenFile(
                    "Prefab|*.ventity|Animation|*.vanim|Audio|*.wav;*.ogg;*.mp3|All Files|*.*",
                    "Pick an asset for this field", string.IsNullOrEmpty(root) ? "" : root);
                if (string.IsNullOrEmpty(picked)) return;
                if (!string.IsNullOrEmpty(root) && picked.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    picked = picked.Substring(root.Length).TrimStart('\\', '/');
                picked = picked.Replace('\\', '/');
                box.Text = picked;
                store(picked);
            };
            Grid.SetColumn(browse, 1);
            grid.Children.Add(browse);
            return grid;
        }

        /// <summary>List editor for ARRAY fields (element type = any scalar): one row per element
        /// (editor + remove), plus "+ Add". The whole array round-trips as ONE stored string joined
        /// with ScriptRuntime.ArraySeparator — e.g. a WeaponPrefabs list on a loadout script.</summary>
        private FrameworkElement MakeArrayEditor(System.Reflection.FieldInfo f)
        {
            var elemType = f.FieldType.GetElementType();
            var panel = new StackPanel();

            var raw = CurrentValueString(f);
            var elems = string.IsNullOrEmpty(raw)
                ? new System.Collections.Generic.List<string>()
                : new System.Collections.Generic.List<string>(raw.Split(Editor.Scripting.ScriptRuntime.ArraySeparator));

            Action storeAll = () =>
            {
                var joined = string.Join(Editor.Scripting.ScriptRuntime.ArraySeparator.ToString(), elems);
                try
                {
                    Editor.Scripting.ScriptRuntime.ParseFieldValue(f.FieldType, joined);   // validate as a whole
                    StoreField(f, joined);
                }
                catch { }
            };
            Action rebuild = () => Dispatcher.BeginInvoke(new Action(BuildFieldRows));

            for (int i = 0; i < elems.Count; i++)
            {
                int idx = i;
                var elemRow = new Grid { Margin = new Thickness(0, i == 0 ? 0 : 3, 0, 0) };
                elemRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                elemRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var elemEditor = MakeScalarEditor(elemType,
                    () => idx < elems.Count ? elems[idx] : "",
                    v => { if (idx < elems.Count) { elems[idx] = v; storeAll(); } });
                elemRow.Children.Add(elemEditor);

                var remove = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Width = 22,
                    Height = 22,
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = "Remove element " + i,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                remove.Click += (s, e) => { elems.RemoveAt(idx); storeAll(); rebuild(); };
                Grid.SetColumn(remove, 1);
                elemRow.Children.Add(remove);
                panel.Children.Add(elemRow);
            }

            var add = new Button
            {
                Content = "+ Add",
                FontSize = 11,
                Height = 22,
                Padding = new Thickness(8, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, elems.Count > 0 ? 4 : 0, 0, 0),
            };
            add.Click += (s, e) =>
            {
                // default element = the scalar default ("" / "0" / "false" / first enum name)
                string def = "";
                try { def = Editor.Scripting.ScriptRuntime.FormatFieldValue(
                    elemType.IsValueType ? Activator.CreateInstance(elemType) : (object)""); }
                catch { }
                elems.Add(def);
                storeAll();
                rebuild();
            };
            panel.Children.Add(add);
            return panel;
        }

        private static TextBox MakeValueBox(string text)
        {
            return new TextBox
            {
                Text = text ?? "",
                FontSize = 11.5,
                Height = 24,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x35, 0x35, 0x3C)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE9, 0xE9, 0xED)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 0, 6, 0),
            };
        }

        /// <summary>"WalkSpeed" -> "Walk Speed" for the row label.</summary>
        private static string NiceName(string n)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n.Length; i++)
            {
                if (i > 0 && char.IsUpper(n[i]) && !char.IsUpper(n[i - 1])) sb.Append(' ');
                sb.Append(n[i] == '_' ? ' ' : n[i]);
            }
            return sb.ToString();
        }

        private bool ScriptFileExists()
        {
            try
            {
                var abs = AbsolutePath();
                return !string.IsNullOrEmpty(abs) && File.Exists(abs);
            }
            catch { return true; } // don't nag on unexpected errors
        }

        private string AbsolutePath()
        {
            var root = ScriptingService.ProjectRoot;
            if (string.IsNullOrEmpty(root) || _script == null || string.IsNullOrEmpty(_script.ScriptPath)) return null;
            return Path.Combine(root, _script.ScriptPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private void OpenVs_Click(object sender, RoutedEventArgs e)
        {
            ScriptingService.OpenInVisualStudio(AbsolutePath());
        }

        private void Detach_Click(object sender, RoutedEventArgs e)
        {
            var h = DetachRequested; if (h != null) h(this, EventArgs.Empty);
        }

        private void Change_Click(object sender, RoutedEventArgs e)
        {
            var h = ChangeRequested; if (h != null) h(this, EventArgs.Empty);
        }
    }
}
