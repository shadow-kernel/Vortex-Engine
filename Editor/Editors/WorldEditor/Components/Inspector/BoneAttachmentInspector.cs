using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.ECS.Components.Animation;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    /// <summary>
    /// Inspector card for the BoneAttachment component (#172). Built programmatically (no XAML page
    /// to register in the non-SDK csproj): resolved-target readout, searchable bone dropdown filled
    /// from the target's skeleton, bone-space offset fields, and the two authoring actions —
    /// "Snap to Bone" (apply the socket now, see it in the viewport) and "Capture Offset" (place the
    /// entity visually with the normal gizmo first, then bake its current pose into the offsets).
    /// </summary>
    public sealed class BoneAttachmentInspector : UserControl
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

        private readonly BoneAttachment _attachment;
        private readonly TextBlock _targetInfo;

        public event EventHandler RemoveRequested;

        public BoneAttachmentInspector(BoneAttachment attachment)
        {
            _attachment = attachment;

            var root = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(new TextBlock
            {
                Text = "\uE71B",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = HeaderFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = "Bone Attachment",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = HeaderFg,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(titleRow);
            var remove = new Button
            {
                Content = "\uE711",
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

            // Target: resolved readout + optional explicit id
            root.Children.Add(SectionLabel("TARGET"));
            _targetInfo = new TextBlock { Foreground = LabelFg, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            RefreshTargetInfo();
            root.Children.Add(_targetInfo);
            root.Children.Add(TextRow("Target Id", _attachment.TargetEntityId, v =>
            {
                _attachment.TargetEntityId = v;
                RefreshTargetInfo();
            }, "Entity GUID of the skeletal target. Leave EMPTY to use the nearest parent with an Animator (the usual case: weapon as child of the character)."));

            // Bone: editable (searchable) dropdown from the resolved target's skeleton
            root.Children.Add(SectionLabel("BONE"));
            var boneRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            boneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            boneRow.ColumnDefinitions.Add(new ColumnDefinition());
            boneRow.Children.Add(new TextBlock { Text = "Bone", Foreground = LabelFg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var boneBox = new ComboBox
            {
                Background = FieldBg,
                Foreground = FieldFg,
                BorderBrush = FieldBorder,
                FontSize = 11.5,
                ToolTip = "Skeleton bone this entity follows (from the target's skeleton). With the list open, type to jump to a bone."
            };
            Action fillBones = () =>
            {
                string current = _attachment.BoneName;
                boneBox.Items.Clear();
                var scene = Editor.Core.Services.SceneService.Instance.CurrentScene;
                foreach (var n in Editor.Core.Animation.BoneSocketService.Instance.GetBoneNamesFor(scene, _attachment.Entity))
                    boneBox.Items.Add(n);
                // Keep the authored bone visible even when the skeleton can't resolve right now.
                if (!string.IsNullOrEmpty(current) && !boneBox.Items.Contains(current))
                    boneBox.Items.Insert(0, current);
                boneBox.SelectedItem = string.IsNullOrEmpty(current) ? null : current;
            };
            boneBox.Loaded += (s, e) => fillBones();
            boneBox.DropDownOpened += (s, e) => fillBones();
            boneBox.SelectionChanged += (s, e) =>
            {
                if (boneBox.SelectedItem is string sel && !string.Equals(sel, _attachment.BoneName, StringComparison.Ordinal))
                {
                    _attachment.BoneName = sel;
                    RefreshTargetInfo();
                }
            };
            Grid.SetColumn(boneBox, 1);
            boneRow.Children.Add(boneBox);
            root.Children.Add(boneRow);

            // Offsets (bone space)
            root.Children.Add(SectionLabel("OFFSET (BONE SPACE)"));
            root.Children.Add(Vec3Row("Position", () => _attachment.OffsetPosition, v => _attachment.OffsetPosition = v));
            root.Children.Add(Vec3Row("Rotation", () => _attachment.OffsetRotation, v => _attachment.OffsetRotation = v));
            root.Children.Add(Vec3Row("Scale", () => _attachment.OffsetScale, v => _attachment.OffsetScale = v));

            // Authoring actions
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var snap = SmallButton("Snap to Bone");
            snap.ToolTip = "Apply the socket now: move this entity onto the bone (bind pose in edit mode) so you see the result in the viewport.";
            snap.Click += (s, e) =>
            {
                var scene = Editor.Core.Services.SceneService.Instance.CurrentScene;
                if (!Editor.Core.Animation.BoneSocketService.Instance.ApplyOne(scene, _attachment.Entity))
                    MessageBox.Show("Could not resolve the bone — check the target and bone name.", "Bone Attachment",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            };
            actions.Children.Add(snap);
            var capture = SmallButton("Capture Offset");
            capture.Margin = new Thickness(6, 0, 0, 0);
            capture.ToolTip = "Bake this entity's CURRENT transform into the offsets: drag it into place with the normal gizmo first, then click here.";
            capture.Click += (s, e) =>
            {
                var scene = Editor.Core.Services.SceneService.Instance.CurrentScene;
                if (Editor.Core.Animation.BoneSocketService.Instance.CaptureOffsetFromCurrentPose(scene, _attachment.Entity))
                    RefreshOffsets();
                else
                    MessageBox.Show("Could not resolve the bone — check the target and bone name.", "Bone Attachment",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            };
            actions.Children.Add(capture);
            root.Children.Add(actions);

            root.Children.Add(new TextBlock
            {
                Text = "In play mode the socket drives this entity every frame. In edit mode use Snap/Capture; the normal transform gizmo stays free for placing.",
                Foreground = LabelFg,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });

            Content = new Border
            {
                Background = CardBg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 5, 0, 0),
                Child = root
            };
        }

        private readonly System.Collections.Generic.List<Action> _offsetRefreshers = new System.Collections.Generic.List<Action>();

        private void RefreshOffsets()
        {
            foreach (var r in _offsetRefreshers) r();
        }

        private void RefreshTargetInfo()
        {
            var scene = Editor.Core.Services.SceneService.Instance.CurrentScene;
            var target = Editor.Core.Animation.BoneSocketService.Instance.ResolveTargetOf(scene, _attachment.Entity);
            _targetInfo.Text = target != null
                ? "Resolves to: " + target.Name
                : "No target resolves — set an id or parent this entity under one with an Animator.";
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

        private UIElement TextRow(string label, string value, Action<string> commit, string tooltip = null)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = label, Foreground = LabelFg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var field = Field(value);
            if (tooltip != null) field.ToolTip = tooltip;
            field.LostFocus += (s, e) => commit(field.Text.Trim());
            field.KeyDown += (s, e) => { if (e.Key == Key.Enter) commit(field.Text.Trim()); };
            Grid.SetColumn(field, 1);
            row.Children.Add(field);
            return row;
        }

        private UIElement Vec3Row(string label, Func<Editor.ECS.Vector3> get, Action<Editor.ECS.Vector3> set)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = label, Foreground = LabelFg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });

            var boxes = new TextBox[3];
            for (int i = 0; i < 3; i++)
            {
                int axis = i;
                var box = Field("");
                box.Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0);
                Action apply = () =>
                {
                    if (!float.TryParse(box.Text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float f)) return;
                    var v = get();
                    if (axis == 0) v.X = f; else if (axis == 1) v.Y = f; else v.Z = f;
                    set(v);
                };
                box.LostFocus += (s, e) => apply();
                box.KeyDown += (s, e) => { if (e.Key == Key.Enter) apply(); };
                Grid.SetColumn(box, i + 1);
                row.Children.Add(box);
                boxes[i] = box;
            }
            Action refresh = () =>
            {
                var v = get();
                boxes[0].Text = v.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                boxes[1].Text = v.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                boxes[2].Text = v.Z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            };
            refresh();
            _offsetRefreshers.Add(refresh);
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
