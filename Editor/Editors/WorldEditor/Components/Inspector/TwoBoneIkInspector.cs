using System;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.ECS.Components.Animation;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    /// <summary>
    /// Inspector card for the TwoBoneIk component (#179) — the "support hand grips the weapon" IK.
    /// Built programmatically (no XAML page to register in the non-SDK csproj): tip/target bone
    /// dropdowns from the entity's own skeleton, grip offset fields (model units), weight/pole, and
    /// "Capture From Current Pose" which bakes the current tip↔target relation into the offsets.
    /// Every edit re-poses the animator live (bind pose + IK in edit mode) via AnimationService.RefreshIk.
    /// </summary>
    public sealed class TwoBoneIkInspector : UserControl
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

        private readonly TwoBoneIk _ik;
        private readonly TextBlock _chainInfo;
        private readonly System.Collections.Generic.List<Action> _refreshers = new System.Collections.Generic.List<Action>();

        public event EventHandler RemoveRequested;

        public TwoBoneIkInspector(TwoBoneIk ik)
        {
            _ik = ik;

            var root = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = HeaderFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = "Two-Bone IK",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = HeaderFg,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(titleRow);
            var remove = new Button
            {
                Content = "",
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

            // Chain readout
            _chainInfo = new TextBlock { Foreground = LabelFg, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            RefreshChainInfo();
            root.Children.Add(_chainInfo);

            // Bones
            root.Children.Add(SectionLabel("CHAIN"));
            root.Children.Add(BoneRow("Tip Bone", () => _ik.TipBone, v => { _ik.TipBone = v; AfterEdit(); },
                "The reaching bone (e.g. mixamorig:LeftHand). Mid/root joints = its parent and grandparent."));
            root.Children.Add(BoneRow("Target Bone", () => _ik.TargetBone, v => { _ik.TargetBone = v; AfterEdit(); },
                "The bone the grip is expressed against (e.g. mixamorig:RightHand — the weapon hand)."));

            // Grip offset
            root.Children.Add(SectionLabel("GRIP OFFSET (target-bone space, MODEL units)"));
            root.Children.Add(Vec3Row("Position", () => _ik.TargetOffsetPosition, v => { _ik.TargetOffsetPosition = v; AfterEdit(); }));
            root.Children.Add(Vec3Row("Rotation", () => _ik.TargetOffsetRotation, v => { _ik.TargetOffsetRotation = v; AfterEdit(); }));

            // Weight / pole / tip rotation
            root.Children.Add(SectionLabel("SOLVE"));
            root.Children.Add(FloatRow("Weight", () => _ik.Weight, v => { _ik.Weight = v; AfterEdit(); },
                "0 = animation only, 1 = full IK. Scripts blend it at runtime via Animation.SetIkWeight."));
            root.Children.Add(FloatRow("Pole Angle", () => _ik.PoleAngle, v => { _ik.PoleAngle = v; AfterEdit(); },
                "Rotates the elbow around the shoulder-to-target axis (degrees). 0 keeps the animation's natural bend."));
            var tipRot = new CheckBox
            {
                Content = "Apply tip rotation (orient the wrist to the grip)",
                Foreground = LabelFg,
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 0),
                IsChecked = _ik.ApplyTipRotation
            };
            tipRot.Checked += (s, e) => { _ik.ApplyTipRotation = true; AfterEdit(); };
            tipRot.Unchecked += (s, e) => { _ik.ApplyTipRotation = false; AfterEdit(); };
            root.Children.Add(tipRot);

            var autoGrip = new CheckBox
            {
                Content = "Auto-grip (capture the natural hold from the animation)",
                Foreground = LabelFg,
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = "On: the support hand locks to wherever the idle/hold animation puts it relative to the weapon hand, and holds it through every clip — no manual offset needed (the offset fields fine-tune on top). Off: use only the explicit grip offset below.",
                IsChecked = _ik.AutoGrip
            };
            autoGrip.Checked += (s, e) => { _ik.AutoGrip = true; AfterEdit(); };
            autoGrip.Unchecked += (s, e) => { _ik.AutoGrip = false; AfterEdit(); };
            root.Children.Add(autoGrip);

            // Authoring actions
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var capture = SmallButton("Capture From Current Pose");
            capture.ToolTip = "Bake the CURRENT tip-to-target relation into the grip offset — pose the character first " +
                              "(e.g. a rifle-hold frame in the Keyframe Editor, or nudge the numbers), then click. " +
                              "Captured from the un-IK'd pose (weight is temporarily zeroed).";
            capture.Click += (s, e) => CaptureFromCurrentPose();
            actions.Children.Add(capture);
            root.Children.Add(actions);

            root.Children.Add(new TextBlock
            {
                Text = "The left hand follows the grip point through EVERY animation once the weapon is socketed to " +
                       "the target bone. Offsets are in model units (cm on a Mixamo rig). The viewport previews the " +
                       "IK'd pose live while you tune.",
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

        /// <summary>Component setters already re-pose via AnimationService.RefreshIk; the retained
        /// render queue additionally needs a viewport resubmit to show it.</summary>
        private void AfterEdit()
        {
            RefreshChainInfo();
            GamePreview.GamePreviewView.RequestResubmit();
        }

        private void CaptureFromCurrentPose()
        {
            if (string.IsNullOrEmpty(_ik.TipBone) || string.IsNullOrEmpty(_ik.TargetBone))
            {
                MessageBox.Show("Set Tip Bone and Target Bone first.", "Two-Bone IK", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Capture from the UN-IK'd pose: zero the weight, re-pose, read, restore.
            float savedWeight = _ik.Weight;
            _ik.Weight = 0f;
            var svc = Editor.Core.Animation.AnimationService.Instance;
            svc.RefreshIk(_ik.Entity);

            bool ok = false;
            if (svc.TryGetNodeWorlds(_ik.Entity, out var skel, out var worlds) && worlds != null)
            {
                int tip = skel.FindNode(_ik.TipBone);
                int tgt = skel.FindNode(_ik.TargetBone);
                if (tip >= 0 && tgt >= 0 && tip < worlds.Length && tgt < worlds.Length &&
                    Matrix4x4.Invert(worlds[tgt], out var inv))
                {
                    var off = worlds[tip] * inv;
                    Vector3 s, tr; Quaternion q;
                    if (!Matrix4x4.Decompose(off, out s, out q, out tr))
                    {
                        tr = off.Translation;
                        q = Quaternion.Identity;
                    }
                    var euler = Editor.Core.Animation.BoneSocketService.ToEulerZXY(Matrix4x4.CreateFromQuaternion(q));
                    _ik.TargetOffsetPosition = new Editor.ECS.Vector3(tr.X, tr.Y, tr.Z);
                    _ik.TargetOffsetRotation = new Editor.ECS.Vector3(euler.X, euler.Y, euler.Z);
                    ok = true;
                }
            }

            _ik.Weight = savedWeight;
            foreach (var r in _refreshers) r();
            AfterEdit();
            if (!ok)
                MessageBox.Show("Could not resolve the bones — check Tip/Target names against the skeleton.",
                    "Two-Bone IK", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RefreshChainInfo()
        {
            string info = "No skeleton resolves — put this component on the entity that carries the Animator.";
            if (Editor.Core.Animation.AnimationService.Instance.TryGetNodeWorlds(_ik.Entity, out var skel, out _))
            {
                int tip = string.IsNullOrEmpty(_ik.TipBone) ? -1 : skel.FindNode(_ik.TipBone);
                if (tip >= 0)
                {
                    int mid = skel.Nodes[tip].Parent;
                    int rootN = mid >= 0 ? skel.Nodes[mid].Parent : -1;
                    info = rootN >= 0
                        ? "Chain: " + skel.Nodes[rootN].Name + " → " + skel.Nodes[mid].Name + " → " + skel.Nodes[tip].Name
                        : "Tip bone has no grandparent — a two-bone chain needs 3 joints.";
                }
                else
                {
                    info = "Pick the tip bone (the hand that should grip).";
                }
            }
            _chainInfo.Text = info;
        }

        private string[] GetOwnBoneNames()
        {
            if (Editor.Core.Animation.AnimationService.Instance.TryGetNodeWorlds(_ik.Entity, out var skel, out _))
            {
                var names = new string[skel.Nodes.Length];
                for (int i = 0; i < skel.Nodes.Length; i++) names[i] = skel.Nodes[i].Name;
                return names;
            }
            return new string[0];
        }

        private UIElement BoneRow(string label, Func<string> get, Action<string> commit, string tooltip)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = label, Foreground = LabelFg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var box = new ComboBox
            {
                Background = FieldBg,
                Foreground = FieldFg,
                BorderBrush = FieldBorder,
                FontSize = 11.5,
                ToolTip = tooltip
            };
            Action fill = () =>
            {
                string current = get();
                box.Items.Clear();
                foreach (var n in GetOwnBoneNames()) box.Items.Add(n);
                if (!string.IsNullOrEmpty(current) && !box.Items.Contains(current))
                    box.Items.Insert(0, current);
                box.SelectedItem = string.IsNullOrEmpty(current) ? null : current;
            };
            box.Loaded += (s, e) => fill();
            box.DropDownOpened += (s, e) => fill();
            box.SelectionChanged += (s, e) =>
            {
                if (box.SelectedItem is string sel && !string.Equals(sel, get(), StringComparison.Ordinal))
                    commit(sel);
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            return row;
        }

        private UIElement FloatRow(string label, Func<float> get, Action<float> commit, string tooltip)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = label, Foreground = LabelFg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var box = Field(get().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            box.ToolTip = tooltip;
            Action apply = () =>
            {
                if (float.TryParse(box.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float f))
                    commit(f);
            };
            box.LostFocus += (s, e) => apply();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) apply(); };
            _refreshers.Add(() => box.Text = get().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
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
            _refreshers.Add(refresh);
            return row;
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
