using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Animation;
using Editor.ECS;
using Editor.ECS.Components.Animation;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    /// <summary>
    /// WPF inspector cards for the rig-aware animation components (#147): Hand Pose, Look-At IK, Foot IK. Built in code
    /// (no XAML page to register in the non-SDK csproj), dark style like <see cref="TwoBoneIkInspector"/>. Every card
    /// shows what the component resolved on the entity's skeleton; bone names are only needed when detection is wrong.
    /// Hook-up in DynamicInspectorView: <c>{ typeof(HandPose), c => RigInspectorCards.Create(c, RemoveComponentAndRefresh) }</c>
    /// (same for LookAtIk / FootIk) plus the Add-Component menu entries.
    /// </summary>
    public static class RigInspectorCards
    {
        /// <summary>The card for a HandPose / LookAtIk / FootIk (null for other components).</summary>
        public static UserControl Create(Component component, Action<Component> remove)
        {
            if (component is HandPose || component is LookAtIk || component is FootIk)
                return new RigComponentCard(component, remove);
            return null;
        }
    }

    internal sealed class RigComponentCard : UserControl
    {
        private static readonly Brush CardBg = Freeze("#2D2D30");
        private static readonly Brush HeaderFg = Freeze("#C5C5C5");
        private static readonly Brush LabelFg = Freeze("#98989F");
        private static readonly Brush FieldBg = Freeze("#202023");
        private static readonly Brush FieldFg = Freeze("#F0F0F3");
        private static readonly Brush FieldBorder = Freeze("#34343C");
        private static readonly Brush Good = Freeze("#5AC878");
        private static readonly Brush Warn = Freeze("#E8A33D");
        private static readonly Brush Danger = Freeze("#FFB76B7E");
        private static readonly string[] RigLabels = { "Auto-detect", "Mixamo", "Unreal", "Unity / Generic", "Rigify", "Custom (bone lists)" };

        private static Brush Freeze(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private readonly Component _c;
        private readonly StackPanel _body = new StackPanel();
        private readonly StackPanel _report = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        private readonly List<Action> _refreshers = new List<Action>();

        public RigComponentCard(Component c, Action<Component> remove)
        {
            _c = c;
            var root = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock { Text = c.DisplayName, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = HeaderFg, VerticalAlignment = VerticalAlignment.Center });
            var rm = new Button
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
            rm.Click += (s, e) => { if (remove != null) remove(c); };
            header.Children.Add(rm);
            root.Children.Add(header);
            root.Children.Add(_body);

            var hp = c as HandPose;
            var la = c as LookAtIk;
            var fi = c as FootIk;
            if (hp != null) BuildHandPose(hp);
            else if (la != null) BuildLookAt(la);
            else if (fi != null) BuildFootIk(fi);

            Content = new Border { Background = CardBg, CornerRadius = new CornerRadius(4), Padding = new Thickness(10), Margin = new Thickness(0, 5, 0, 0), Child = root };
            UpdateReport();
            if (c.Entity != null) { try { AnimationService.Instance.RefreshPreview(c.Entity); } catch { } }
        }

        // ------------------------------------------------------------------ cards

        private void BuildHandPose(HandPose hp)
        {
            _body.Children.Add(ComboRow("Hand", new[] { "Left", "Right" }, () => (int)hp.Side, v => hp.Side = (HandSide)v));
            _body.Children.Add(ComboRow("Rig", RigLabels, () => (int)hp.Rig, v => hp.Rig = (RigPreset)v));
            var presets = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
            foreach (HandPosePreset p in Enum.GetValues(typeof(HandPosePreset)))
            {
                var preset = p;
                var b = SmallButton(p.ToString());
                b.Click += (s, e) => { hp.ApplyPreset(preset); RefreshAll(); };
                presets.Children.Add(b);
            }
            _body.Children.Add(Labeled("Pose", presets));
            _body.Children.Add(FloatRow("Weight", () => hp.Weight, v => hp.Weight = v));
            _body.Children.Add(Vec3Row("Index", () => hp.Index, v => hp.Index = v));
            _body.Children.Add(Vec3Row("Middle", () => hp.Middle, v => hp.Middle = v));
            _body.Children.Add(Vec3Row("Ring", () => hp.Ring, v => hp.Ring = v));
            _body.Children.Add(Vec3Row("Pinky", () => hp.Pinky, v => hp.Pinky = v));
            _body.Children.Add(Vec3Row("Thumb", () => hp.Thumb, v => hp.Thumb = v));
            _body.Children.Add(FloatRow("Spread", () => hp.Spread, v => hp.Spread = v));
            var mirror = SmallButton("Mirror to other hand");
            mirror.Click += (s, e) => { hp.MirrorToOtherHand(); GamePreview.GamePreviewView.RequestResubmit(); };
            _body.Children.Add(Labeled("", mirror));
            _body.Children.Add(Section("DETECTED"));
            _body.Children.Add(_report);
            _body.Children.Add(Section("BONES & AXIS (only when detection is wrong)"));
            _body.Children.Add(TextRow("Hand bone", () => hp.HandBone, v => hp.HandBone = v));
            _body.Children.Add(TextRow("Index bones", () => hp.IndexBones, v => hp.IndexBones = v));
            _body.Children.Add(TextRow("Middle bones", () => hp.MiddleBones, v => hp.MiddleBones = v));
            _body.Children.Add(TextRow("Ring bones", () => hp.RingBones, v => hp.RingBones = v));
            _body.Children.Add(TextRow("Pinky bones", () => hp.PinkyBones, v => hp.PinkyBones = v));
            _body.Children.Add(TextRow("Thumb bones", () => hp.ThumbBones, v => hp.ThumbBones = v));
            _body.Children.Add(ComboRow("Curl axis", new[] { "Auto (bind pose)", "Local X", "Local Y", "Local Z" }, () => hp.CurlAxis + 1, v => hp.CurlAxis = v - 1));
            _body.Children.Add(ComboRow("Curl sign", new[] { "+1", "-1" }, () => hp.CurlSign < 0f ? 1 : 0, v => hp.CurlSign = v == 1 ? -1f : 1f));
            _body.Children.Add(TextRow("Mixamo prefix", () => hp.BonePrefix, v => hp.BonePrefix = v));
        }

        private void BuildLookAt(LookAtIk la)
        {
            _body.Children.Add(ComboRow("Rig", RigLabels, () => (int)la.Rig, v => la.Rig = (RigPreset)v));
            _body.Children.Add(TextRow("Look at", () => la.TargetEntity, v => la.TargetEntity = v));
            _body.Children.Add(Vec3Row("Target offset", () => la.TargetOffset, v => la.TargetOffset = v));
            _body.Children.Add(FloatRow("Weight", () => la.Weight, v => la.Weight = v));
            _body.Children.Add(FloatRow("Head share", () => la.HeadWeight, v => la.HeadWeight = v));
            _body.Children.Add(FloatRow("Neck share", () => la.NeckWeight, v => la.NeckWeight = v));
            _body.Children.Add(FloatRow("Spine share", () => la.SpineWeight, v => la.SpineWeight = v));
            _body.Children.Add(FloatRow("Max yaw", () => la.MaxYaw, v => la.MaxYaw = v));
            _body.Children.Add(FloatRow("Max pitch", () => la.MaxPitch, v => la.MaxPitch = v));
            _body.Children.Add(FloatRow("Smoothing (s)", () => la.Smoothing, v => la.Smoothing = v));
            _body.Children.Add(ComboRow("Face forward", new[] { "Auto (bind pose)", "+Z", "-Z", "+X", "-X" }, () => la.ForwardAxis, v => la.ForwardAxis = v));
            _body.Children.Add(Section("DETECTED"));
            _body.Children.Add(_report);
            _body.Children.Add(Section("BONES (only when detection is wrong)"));
            _body.Children.Add(TextRow("Head bone", () => la.HeadBone, v => la.HeadBone = v));
            _body.Children.Add(TextRow("Neck bones", () => la.NeckBones, v => la.NeckBones = v));
            _body.Children.Add(TextRow("Spine bones", () => la.SpineBones, v => la.SpineBones = v));
        }

        private void BuildFootIk(FootIk fi)
        {
            _body.Children.Add(new TextBlock { Text = "Active in Play: the feet are planted on the gameplay colliders.", Foreground = LabelFg, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            _body.Children.Add(ComboRow("Rig", RigLabels, () => (int)fi.Rig, v => fi.Rig = (RigPreset)v));
            _body.Children.Add(FloatRow("Weight", () => fi.Weight, v => fi.Weight = v));
            _body.Children.Add(FloatRow("Max step (m)", () => fi.MaxStep, v => fi.MaxStep = v));
            _body.Children.Add(FloatRow("Ray height (m)", () => fi.RayHeight, v => fi.RayHeight = v));
            _body.Children.Add(FloatRow("Ankle height (m, -1 auto)", () => fi.FootHeight, v => fi.FootHeight = v));
            _body.Children.Add(CheckRow("Align to ground", () => fi.AlignToGround, v => fi.AlignToGround = v));
            _body.Children.Add(FloatRow("Max foot angle", () => fi.MaxFootAngle, v => fi.MaxFootAngle = v));
            _body.Children.Add(CheckRow("Adjust pelvis", () => fi.AdjustPelvis, v => fi.AdjustPelvis = v));
            _body.Children.Add(FloatRow("Smoothing (s)", () => fi.Smoothing, v => fi.Smoothing = v));
            _body.Children.Add(FloatRow("Ground layers", () => fi.GroundLayers, v => fi.GroundLayers = (int)Math.Round(v)));
            _body.Children.Add(Section("DETECTED"));
            _body.Children.Add(_report);
            _body.Children.Add(Section("BONES (only when detection is wrong)"));
            _body.Children.Add(TextRow("Left foot", () => fi.LeftFoot, v => fi.LeftFoot = v));
            _body.Children.Add(TextRow("Right foot", () => fi.RightFoot, v => fi.RightFoot = v));
            _body.Children.Add(TextRow("Pelvis", () => fi.PelvisBone, v => fi.PelvisBone = v));
        }

        // ------------------------------------------------------------------ report

        private void UpdateReport()
        {
            _report.Children.Clear();
            AnimationService.RigReport rep = null;
            try
            {
                var svc = AnimationService.Instance;
                var e = _c.Entity;
                if (_c is HandPose) rep = svc.DescribeHandPose(e, (HandPose)_c);
                else if (_c is LookAtIk) rep = svc.DescribeLookAt(e, (LookAtIk)_c);
                else if (_c is FootIk) rep = svc.DescribeFootIk(e, (FootIk)_c);
            }
            catch { }
            if (rep == null) return;
            _report.Children.Add(new TextBlock { Text = rep.Ok ? "Resolved on this skeleton" : "Not resolved", Foreground = rep.Ok ? Good : Warn, FontSize = 11, FontWeight = FontWeights.SemiBold });
            foreach (var l in rep.Lines) _report.Children.Add(new TextBlock { Text = l, Foreground = LabelFg, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            foreach (var n in rep.Notes) _report.Children.Add(new TextBlock { Text = "- " + n, Foreground = Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        }

        private void AfterEdit()
        {
            UpdateReport();
            GamePreview.GamePreviewView.RequestResubmit();
        }

        private void RefreshAll()
        {
            foreach (var r in _refreshers) { try { r(); } catch { } }
            AfterEdit();
        }

        // ------------------------------------------------------------------ rows

        private static TextBlock Section(string text)
            => new TextBlock { Text = text, Foreground = LabelFg, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3) };

        private static Grid Labeled(string label, UIElement editor)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock { Text = label, Foreground = LabelFg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(l);
            Grid.SetColumn(editor, 1);
            g.Children.Add(editor);
            return g;
        }

        private static Button SmallButton(string text)
            => new Button { Content = text, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 2, 8, 2), Background = FieldBg, Foreground = FieldFg, BorderBrush = FieldBorder, Cursor = Cursors.Hand };

        private TextBox Field(Func<string> get, Action<string> commit)
        {
            var box = new TextBox { Text = get() ?? "", Background = FieldBg, Foreground = FieldFg, BorderBrush = FieldBorder, CaretBrush = FieldFg, Padding = new Thickness(4, 2, 4, 2), FontSize = 11.5 };
            Action apply = () => { string v = box.Text ?? ""; if (v != (get() ?? "")) { commit(v); AfterEdit(); } };
            box.LostFocus += (s, e) => apply();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { apply(); e.Handled = true; } };
            _refreshers.Add(() => { if (!box.IsKeyboardFocused) box.Text = get() ?? ""; });
            return box;
        }

        private Grid TextRow(string label, Func<string> get, Action<string> set) => Labeled(label, Field(get, set));

        private Grid FloatRow(string label, Func<float> get, Action<float> set)
        {
            var box = Field(() => get().ToString("0.###", CultureInfo.InvariantCulture), s =>
            {
                float v;
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) set(v);
            });
            return Labeled(label, box);
        }

        private Grid Vec3Row(string label, Func<Vector3> get, Action<Vector3> set)
        {
            var g = new Grid();
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 3; i++)
            {
                int axis = i;
                var box = Field(() => Comp(get(), axis).ToString("0.###", CultureInfo.InvariantCulture), s =>
                {
                    float v;
                    if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return;
                    var c = get();
                    set(axis == 0 ? new Vector3(v, c.Y, c.Z) : axis == 1 ? new Vector3(c.X, v, c.Z) : new Vector3(c.X, c.Y, v));
                });
                box.Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0);
                Grid.SetColumn(box, i);
                g.Children.Add(box);
            }
            return Labeled(label, g);
        }

        private static float Comp(Vector3 v, int i) => i == 0 ? v.X : (i == 1 ? v.Y : v.Z);

        private Grid ComboRow(string label, string[] items, Func<int> get, Action<int> set)
        {
            var combo = new ComboBox { FontSize = 11.5, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var it in items) combo.Items.Add(it);
            combo.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, get()));
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedIndex >= 0 && combo.SelectedIndex != get()) { set(combo.SelectedIndex); AfterEdit(); }
            };
            _refreshers.Add(() => combo.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, get())));
            return Labeled(label, combo);
        }

        private Grid CheckRow(string label, Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { IsChecked = get(), VerticalAlignment = VerticalAlignment.Center };
            cb.Checked += (s, e) => { if (!get()) { set(true); AfterEdit(); } };
            cb.Unchecked += (s, e) => { if (get()) { set(false); AfterEdit(); } };
            _refreshers.Add(() => cb.IsChecked = get());
            return Labeled(label, cb);
        }
    }
}
