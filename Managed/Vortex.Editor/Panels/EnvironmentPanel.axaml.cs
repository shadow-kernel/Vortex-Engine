using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.DllWrapper;
using VortexEditor.Controls;

namespace VortexEditor.Panels
{
    /// <summary>
    /// Scene environment: fog + post-processing (vignette, grain, chromatic aberration, ambient occlusion, bloom,
    /// colour grading). Edits the active scene's SceneSettings, applies live and marks the scene dirty.
    /// </summary>
    public partial class EnvironmentPanel : UserControl
    {
        private bool _loading;
        private ProjectData _hooked;
        private readonly List<Action> _refreshers = new List<Action>();

        private static Scene ActiveScene => ProjectData.Current?.ActiveScene;
        private static SceneSettings Settings => ActiveScene?.Settings;

        public EnvironmentPanel()
        {
            InitializeComponent();
            Build();
            EditorSession.Instance.ProjectOpened += _ => Dispatcher.UIThread.Post(Refresh);
            EditorSession.Instance.ProjectClosed += () => Dispatcher.UIThread.Post(Refresh);
            AttachedToVisualTree += (s, e) => Refresh();
        }

        private void Build()
        {
            Sections.Children.Add(Section("Fog", s => s.FogEnabled, (s, v) => s.FogEnabled = v,
                Slider("Density", 0, 0.5, 0.001, s => s.FogDensity, (s, v) => s.FogDensity = v),
                Slider("Height", -50, 50, 0.1, s => s.FogHeightY, (s, v) => s.FogHeightY = v),
                Slider("Height falloff", 0, 2, 0.01, s => s.FogHeightFalloff, (s, v) => s.FogHeightFalloff = v),
                ColorRow("Color", s => (s.FogR, s.FogG, s.FogB), (s, c) => { s.FogR = c.r; s.FogG = c.g; s.FogB = c.b; })));
            Sections.Children.Add(Section("Vignette", s => s.VignetteEnabled, (s, v) => s.VignetteEnabled = v,
                Slider("Intensity", 0, 1, 0.01, s => s.VignetteIntensity, (s, v) => s.VignetteIntensity = v),
                Slider("Smoothness", 0, 1, 0.01, s => s.VignetteSmoothness, (s, v) => s.VignetteSmoothness = v),
                Slider("Roundness", 0, 1, 0.01, s => s.VignetteRoundness, (s, v) => s.VignetteRoundness = v),
                ColorRow("Color", s => (s.VignetteR, s.VignetteG, s.VignetteB), (s, c) => { s.VignetteR = c.r; s.VignetteG = c.g; s.VignetteB = c.b; })));
            Sections.Children.Add(Section("Film Grain", s => s.GrainEnabled, (s, v) => s.GrainEnabled = v,
                Slider("Intensity", 0, 1, 0.01, s => s.GrainIntensity, (s, v) => s.GrainIntensity = v),
                Slider("Size", 0.5, 4, 0.05, s => s.GrainSize, (s, v) => s.GrainSize = v)));
            Sections.Children.Add(Section("Chromatic Aberration", s => s.CaEnabled, (s, v) => s.CaEnabled = v,
                Slider("Strength", 0, 1, 0.01, s => s.CaStrength, (s, v) => s.CaStrength = v),
                Slider("Falloff", 0, 3, 0.01, s => s.CaFalloff, (s, v) => s.CaFalloff = v)));
            Sections.Children.Add(Section("Ambient Occlusion", s => s.AoEnabled, (s, v) => s.AoEnabled = v,
                Slider("Radius", 0.05, 3, 0.01, s => s.AoRadius, (s, v) => s.AoRadius = v),
                Slider("Intensity", 0, 3, 0.01, s => s.AoIntensity, (s, v) => s.AoIntensity = v)));
            Sections.Children.Add(Section("Bloom", s => s.BloomEnabled, (s, v) => s.BloomEnabled = v,
                Slider("Threshold", 0, 2, 0.01, s => s.BloomThreshold, (s, v) => s.BloomThreshold = v),
                Slider("Knee", 0, 1, 0.01, s => s.BloomKnee, (s, v) => s.BloomKnee = v),
                Slider("Intensity", 0, 3, 0.01, s => s.BloomIntensity, (s, v) => s.BloomIntensity = v),
                Slider("Scatter", 0, 1, 0.01, s => s.BloomScatter, (s, v) => s.BloomScatter = v)));
            Sections.Children.Add(Section("Color Grading", s => s.GradeEnabled, (s, v) => s.GradeEnabled = v,
                Slider("Exposure", -3, 3, 0.01, s => s.Exposure, (s, v) => s.Exposure = v),
                Slider("Contrast", 0, 2, 0.01, s => s.Contrast, (s, v) => s.Contrast = v),
                Slider("Saturation", 0, 2, 0.01, s => s.Saturation, (s, v) => s.Saturation = v),
                Slider("Temperature", -1, 1, 0.01, s => s.Temperature, (s, v) => s.Temperature = v),
                Slider("Tint", -1, 1, 0.01, s => s.Tint, (s, v) => s.Tint = v)));
        }

        private Control Section(string title, Func<SceneSettings, bool> get, Action<SceneSettings, bool> set, params Control[] rows)
        {
            var card = new Border { Classes = { "card" }, Padding = new Thickness(10, 6, 10, 8) };
            var stack = new StackPanel { Spacing = 2 };
            var header = new DockPanel { Height = 26 };
            var body = new StackPanel { Spacing = 2 };
            var sw = new ToggleSwitch { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            DockPanel.SetDock(sw, Dock.Right);
            sw.IsCheckedChanged += (s, e) => { if (_loading) return; var st = Settings; if (st == null) return; set(st, sw.IsChecked == true); body.IsEnabled = sw.IsChecked == true; Apply(); };
            header.Children.Add(sw);
            header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            stack.Children.Add(header);
            foreach (var r in rows) body.Children.Add(r);
            stack.Children.Add(body);
            card.Child = stack;
            _refreshers.Add(() => { var st = Settings; if (st == null) return; sw.IsChecked = get(st); body.IsEnabled = get(st); });
            return card;
        }

        private Control Slider(string label, double min, double max, double step, Func<SceneSettings, float> get, Action<SceneSettings, float> set)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*,58"), Classes = { "proprow" } };
            row.Children.Add(new TextBlock { Text = label, Classes = { "label", "small" } });
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = false, Margin = new Thickness(4, 0) };
            Grid.SetColumn(slider, 1);
            var box = new TextBox { Classes = { "number" }, MinHeight = 22, FontSize = 11 };
            Grid.SetColumn(box, 2);
            slider.ValueChanged += (s, e) => { if (_loading) return; var st = Settings; if (st == null) return; set(st, (float)slider.Value); box.Text = Fmt(slider.Value); Apply(); };
            box.LostFocus += (s, e) => Commit();
            box.KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Return) { Commit(); e.Handled = true; } };
            void Commit() { if (float.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) slider.Value = Math.Max(min, Math.Min(max, v)); else box.Text = Fmt(slider.Value); }
            row.Children.Add(slider); row.Children.Add(box);
            _refreshers.Add(() => { var st = Settings; if (st == null) return; slider.Value = get(st); box.Text = Fmt(slider.Value); });
            return row;
        }

        private Control ColorRow(string label, Func<SceneSettings, (float, float, float)> get, Action<SceneSettings, (float r, float g, float b)> set)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*"), Classes = { "proprow" } };
            row.Children.Add(new TextBlock { Text = label, Classes = { "label", "small" } });
            var picker = new ColorPicker { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Width = 90, Height = 22, IsAlphaEnabled = false, IsAlphaVisible = false };
            Grid.SetColumn(picker, 1);
            picker.ColorChanged += (s, e) => { if (_loading) return; var st = Settings; if (st == null) return; set(st, (e.NewColor.R / 255f, e.NewColor.G / 255f, e.NewColor.B / 255f)); Apply(); };
            row.Children.Add(picker);
            _refreshers.Add(() => { var st = Settings; if (st == null) return; var (r, g, b) = get(st); picker.Color = Color.FromRgb(B(r), B(g), B(b)); });
            return row;
        }

        private static byte B(float v) => (byte)Math.Max(0, Math.Min(255, (int)(v * 255f + 0.5f)));
        private static string Fmt(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private void Apply()
        {
            var st = Settings; if (st == null) return;
            try { st.Apply(); } catch { }
            var scene = ActiveScene; if (scene != null) scene.IsDirty = true;
        }

        public void Refresh()
        {
            HookProject();
            _loading = true;
            try
            {
                IsEnabled = Settings != null;
                foreach (var r in _refreshers) r();
            }
            finally { _loading = false; }
        }

        private void HookProject()
        {
            var p = ProjectData.Current;
            if (ReferenceEquals(p, _hooked)) return;
            if (_hooked != null) _hooked.PropertyChanged -= OnProjectChanged;
            _hooked = p;
            if (p != null) p.PropertyChanged += OnProjectChanged;
        }

        private void OnProjectChanged(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProjectData.ActiveScene)) Dispatcher.UIThread.Post(Refresh);
        }

        private void OnPreviewFx(object sender, RoutedEventArgs e) => VortexAPI.SetPostMainView(PreviewFx.IsChecked == true);
    }
}
