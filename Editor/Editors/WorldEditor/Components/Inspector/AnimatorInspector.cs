using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.ECS.Components.Animation;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    /// <summary>
    /// Inspector card for the Animator component. Built programmatically (no XAML page to register in
    /// the non-SDK csproj): clip table (name -> .vanim path) with add/remove/browse, default clip,
    /// play-on-start, speed, and an "Open Keyframe Editor" shortcut.
    /// </summary>
    public sealed class AnimatorInspector : UserControl
    {
        private static readonly Brush CardBg = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")));
        private static readonly Brush HeaderFg = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C5C5C5")));
        private static readonly Brush LabelFg = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98989F")));
        private static readonly Brush FieldBg = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202023")));
        private static readonly Brush FieldFg = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0F0F3")));
        private static readonly Brush FieldBorder = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34343C")));
        private static readonly Brush Accent = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C5CE7")));
        private static readonly Brush Danger = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFB76B7E")));
        private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        private readonly Animator _animator;
        private readonly StackPanel _clipRows;

        public event EventHandler RemoveRequested;

        public AnimatorInspector(Animator animator)
        {
            _animator = animator;

            var root = new StackPanel();

            // Header row: icon + title + remove button (matches the generic component card chrome).
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock
            {
                Text = "  Animator",
                FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe UI Variable Text, Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = HeaderFg,
                VerticalAlignment = VerticalAlignment.Center
            });
            var remove = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = Danger,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "Remove component"
            };
            remove.Click += (s, e) => RemoveRequested?.Invoke(this, EventArgs.Empty);
            header.Children.Add(remove);
            root.Children.Add(header);

            // Clip table
            root.Children.Add(SectionLabel("CLIPS"));
            _clipRows = new StackPanel();
            root.Children.Add(_clipRows);
            RebuildClipRows();

            var addClip = SmallButton("+  Add Clip");
            addClip.Margin = new Thickness(0, 2, 0, 8);
            addClip.Click += (s, e) =>
            {
                _animator.Clips.Add(new AnimatorClipEntry { Name = "Clip" + _animator.Clips.Count, Path = "" });
                RebuildClipRows();
            };
            root.Children.Add(addClip);

            // Default clip + playback settings
            root.Children.Add(SectionLabel("PLAYBACK"));
            root.Children.Add(TextRow("Default Clip", _animator.DefaultClip, v => _animator.DefaultClip = v));
            var playRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            playRow.ColumnDefinitions.Add(new ColumnDefinition());
            playRow.Children.Add(new TextBlock { Text = "Play On Start", Foreground = LabelFg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var chk = new CheckBox { IsChecked = _animator.PlayOnStart, VerticalAlignment = VerticalAlignment.Center };
            chk.Checked += (s, e) => _animator.PlayOnStart = true;
            chk.Unchecked += (s, e) => _animator.PlayOnStart = false;
            Grid.SetColumn(chk, 1);
            playRow.Children.Add(chk);
            root.Children.Add(playRow);
            root.Children.Add(FloatRow("Speed", _animator.Speed, v => _animator.Speed = v));

            // Open the Keyframe Editor on the default clip (or the first table entry).
            var open = SmallButton("Open Keyframe Editor…");
            open.Margin = new Thickness(0, 8, 0, 0);
            open.Click += (s, e) =>
            {
                string path = _animator.ResolveClipPath(_animator.DefaultClip);
                if (path == null && _animator.Clips.Count > 0) path = _animator.Clips[0].Path;
                if (string.IsNullOrEmpty(path))
                {
                    MessageBox.Show("Add a clip (.vanim) to this Animator first.", "Keyframe Editor",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                string full = System.IO.Path.IsPathRooted(path) ? path
                    : System.IO.Path.Combine(Editor.Core.Data.ProjectData.Current?.Path ?? "", path);
                Editor.Editors.AnimationEditor.AnimationEditorWindow.Open(Window.GetWindow(this), full);
            };
            root.Children.Add(open);

            Content = new Border
            {
                Background = CardBg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 5, 0, 0),
                Child = root
            };
        }

        private void RebuildClipRows()
        {
            _clipRows.Children.Clear();
            for (int i = 0; i < _animator.Clips.Count; i++)
            {
                var entry = _animator.Clips[i];
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = Field(entry.Name);
                name.LostFocus += (s, e) => entry.Name = name.Text.Trim();
                row.Children.Add(name);

                var path = Field(entry.Path);
                path.ToolTip = "Project-relative .vanim path";
                path.Margin = new Thickness(4, 0, 0, 0);
                path.LostFocus += (s, e) => entry.Path = path.Text.Trim().Replace('\\', '/');
                Grid.SetColumn(path, 1);
                row.Children.Add(path);

                var browse = SmallButton("…");
                browse.Margin = new Thickness(4, 0, 0, 0);
                browse.ToolTip = "Browse for a .vanim clip";
                browse.Click += (s, e) =>
                {
                    var proj = Editor.Core.Data.ProjectData.Current?.Path;
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Filter = "Vortex Animation (*.vanim)|*.vanim",
                        InitialDirectory = proj != null ? System.IO.Path.Combine(proj, "Assets") : null
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        string rel = dlg.FileName;
                        if (proj != null && rel.StartsWith(proj, StringComparison.OrdinalIgnoreCase))
                            rel = rel.Substring(proj.Length).TrimStart('\\', '/');
                        entry.Path = rel.Replace('\\', '/');
                        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.StartsWith("Clip"))
                            entry.Name = System.IO.Path.GetFileNameWithoutExtension(rel);
                        RebuildClipRows();
                    }
                };
                Grid.SetColumn(browse, 2);
                row.Children.Add(browse);

                var del = new Button
                {
                    Content = "",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10,
                    Foreground = Danger,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = "Remove clip"
                };
                var captured = entry;
                del.Click += (s, e) => { _animator.Clips.Remove(captured); RebuildClipRows(); };
                Grid.SetColumn(del, 3);
                row.Children.Add(del);

                _clipRows.Children.Add(row);
            }
        }

        private static TextBlock SectionLabel(string text) => new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6E6E77"))),
            Margin = new Thickness(0, 6, 0, 4)
        };

        private static TextBox Field(string value) => new TextBox
        {
            Text = value ?? "",
            Background = FieldBg,
            Foreground = FieldFg,
            BorderBrush = FieldBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 3, 5, 3),
            FontSize = 11.5,
            CaretBrush = Accent,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        private UIElement TextRow(string label, string value, Action<string> commit)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = label, Foreground = LabelFg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var box = Field(value);
            box.LostFocus += (s, e) => commit(box.Text.Trim());
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) commit(box.Text.Trim()); };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            return row;
        }

        private UIElement FloatRow(string label, float value, Action<float> commit)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = label, Foreground = LabelFg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var box = Field(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Action apply = () =>
            {
                if (float.TryParse(box.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float f)) commit(f);
            };
            box.LostFocus += (s, e) => apply();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) apply(); };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            return row;
        }

        private static Button SmallButton(string content) => new Button
        {
            Content = content,
            Background = FieldBg,
            Foreground = FieldFg,
            BorderBrush = FieldBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 4, 9, 4),
            FontSize = 11.5,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left
        };
    }
}
