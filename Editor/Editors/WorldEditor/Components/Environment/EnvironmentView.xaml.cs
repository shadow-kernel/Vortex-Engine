using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Data;

namespace Editor.Editors.WorldEditor.Components.Environment
{
    /// <summary>
    /// Scene Environment panel (fog #27 + post-FX #28/#29): edits the active scene's SceneSettings,
    /// pushes every change to the renderer the SAME frame (live viewport preview) and marks the scene
    /// dirty so Ctrl+S persists it into the .vscene. Authored values survive play mode: ScriptRuntime
    /// clears scripted overrides on stop and re-applies the scene's settings.
    /// </summary>
    public partial class EnvironmentView : UserControl
    {
        private bool _loading;                 // suppress apply while pulling model -> UI
        private ProjectData _hookedProject;    // ActiveScene change subscription

        public EnvironmentView()
        {
            InitializeComponent();
            Loaded += (s, e) => Refresh();
            IsVisibleChanged += (s, e) => { if (IsVisible) Refresh(); };
        }

        private static Scene ActiveScene => ProjectData.Current?.ActiveScene;
        private static SceneSettings Settings => ActiveScene?.Settings;

        // ---------- model -> UI ----------

        private void Refresh()
        {
            HookProject();
            var s = Settings;
            if (s == null) { IsEnabled = false; return; }
            IsEnabled = true;

            _loading = true;
            try
            {
                FogOn.IsChecked = s.FogEnabled;
                SetPair(FogDensity, FogDensityBox, s.FogDensity);
                SetPair(FogHeightY, FogHeightYBox, s.FogHeightY);
                SetPair(FogFalloff, FogFalloffBox, s.FogHeightFalloff);
                FogColorSwatch.Background = new SolidColorBrush(ToColor(s.FogR, s.FogG, s.FogB));

                VigOn.IsChecked = s.VignetteEnabled;
                SetPair(VigIntensity, VigIntensityBox, s.VignetteIntensity);
                SetPair(VigSmoothness, VigSmoothnessBox, s.VignetteSmoothness);
                SetPair(VigRoundness, VigRoundnessBox, s.VignetteRoundness);
                VigColorSwatch.Background = new SolidColorBrush(ToColor(s.VignetteR, s.VignetteG, s.VignetteB));

                GrainOn.IsChecked = s.GrainEnabled;
                SetPair(GrainIntensity, GrainIntensityBox, s.GrainIntensity);
                SetPair(GrainSize, GrainSizeBox, s.GrainSize);

                CaOn.IsChecked = s.CaEnabled;
                SetPair(CaStrength, CaStrengthBox, s.CaStrength);
                SetPair(CaFalloff, CaFalloffBox, s.CaFalloff);

                AoOn.IsChecked = s.AoEnabled;
                SetPair(AoRadius, AoRadiusBox, s.AoRadius);
                SetPair(AoIntensity, AoIntensityBox, s.AoIntensity);

                BloomOn.IsChecked = s.BloomEnabled;
                SetPair(BloomThreshold, BloomThresholdBox, s.BloomThreshold);
                SetPair(BloomKnee, BloomKneeBox, s.BloomKnee);
                SetPair(BloomIntensity, BloomIntensityBox, s.BloomIntensity);
                SetPair(BloomScatter, BloomScatterBox, s.BloomScatter);

                GradeOn.IsChecked = s.GradeEnabled;
                SetPair(Exposure, ExposureBox, s.Exposure);
                SetPair(Contrast, ContrastBox, s.Contrast);
                SetPair(Saturation, SaturationBox, s.Saturation);
                SetPair(Temperature, TemperatureBox, s.Temperature);
                SetPair(Tint, TintBox, s.Tint);
            }
            finally { _loading = false; }
        }

        private void HookProject()
        {
            var p = ProjectData.Current;
            if (ReferenceEquals(p, _hookedProject)) return;
            if (_hookedProject != null) _hookedProject.PropertyChanged -= OnProjectPropertyChanged;
            _hookedProject = p;
            if (p != null) p.PropertyChanged += OnProjectPropertyChanged;
        }

        private void OnProjectPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProjectData.ActiveScene))
                Dispatcher.BeginInvoke(new Action(Refresh));
        }

        // ---------- UI -> model (live) ----------

        private void AnyChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var s = Settings;
            if (s == null) return;

            s.FogEnabled = FogOn.IsChecked == true;
            s.FogDensity = (float)FogDensity.Value;
            s.FogHeightY = (float)FogHeightY.Value;
            s.FogHeightFalloff = (float)FogFalloff.Value;

            s.VignetteEnabled = VigOn.IsChecked == true;
            s.VignetteIntensity = (float)VigIntensity.Value;
            s.VignetteSmoothness = (float)VigSmoothness.Value;
            s.VignetteRoundness = (float)VigRoundness.Value;

            s.GrainEnabled = GrainOn.IsChecked == true;
            s.GrainIntensity = (float)GrainIntensity.Value;
            s.GrainSize = (float)GrainSize.Value;

            s.CaEnabled = CaOn.IsChecked == true;
            s.CaStrength = (float)CaStrength.Value;
            s.CaFalloff = (float)CaFalloff.Value;

            s.AoEnabled = AoOn.IsChecked == true;
            s.AoRadius = (float)AoRadius.Value;
            s.AoIntensity = (float)AoIntensity.Value;

            s.BloomEnabled = BloomOn.IsChecked == true;
            s.BloomThreshold = (float)BloomThreshold.Value;
            s.BloomKnee = (float)BloomKnee.Value;
            s.BloomIntensity = (float)BloomIntensity.Value;
            s.BloomScatter = (float)BloomScatter.Value;

            s.GradeEnabled = GradeOn.IsChecked == true;
            s.Exposure = (float)Exposure.Value;
            s.Contrast = (float)Contrast.Value;
            s.Saturation = (float)Saturation.Value;
            s.Temperature = (float)Temperature.Value;
            s.Tint = (float)Tint.Value;

            SyncBoxes();
            s.Apply();
            var scene = ActiveScene;
            if (scene != null) scene.IsDirty = true;
        }

        /// <summary>Editor-only preview: post-FX normally renders in the GAME camera only (play window +
        /// exported game). This toggle lets the author see it in the build viewport while tuning.</summary>
        private void PreviewFx_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            DllWrapper.VortexAPI.SetPostMainView(PreviewFx.IsChecked == true);
        }

        private void FogColor_Click(object sender, MouseButtonEventArgs e)
        {
            var s = Settings; if (s == null) return;
            if (PickColor(ToColor(s.FogR, s.FogG, s.FogB), out var c))
            {
                s.FogR = c.R / 255f; s.FogG = c.G / 255f; s.FogB = c.B / 255f;
                FogColorSwatch.Background = new SolidColorBrush(c);
                AnyChanged(sender, null);
            }
        }

        private void VigColor_Click(object sender, MouseButtonEventArgs e)
        {
            var s = Settings; if (s == null) return;
            if (PickColor(ToColor(s.VignetteR, s.VignetteG, s.VignetteB), out var c))
            {
                s.VignetteR = c.R / 255f; s.VignetteG = c.G / 255f; s.VignetteB = c.B / 255f;
                VigColorSwatch.Background = new SolidColorBrush(c);
                AnyChanged(sender, null);
            }
        }

        // ---------- numeric boxes (commit on Enter / focus loss, like the Transform inspector) ----------

        private void AnyBoxCommit(object sender, RoutedEventArgs e) => CommitBox(sender as TextBox);

        private void AnyBoxKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitBox(sender as TextBox); e.Handled = true; }
        }

        private void CommitBox(TextBox box)
        {
            if (box == null || _loading) return;
            if (!float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { SyncBoxes(); return; }
            var slider = SliderFor(box);
            if (slider == null) return;
            slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, v));   // triggers AnyChanged
        }

        private Slider SliderFor(TextBox box)
        {
            if (box == FogDensityBox) return FogDensity;
            if (box == FogHeightYBox) return FogHeightY;
            if (box == FogFalloffBox) return FogFalloff;
            if (box == VigIntensityBox) return VigIntensity;
            if (box == VigSmoothnessBox) return VigSmoothness;
            if (box == VigRoundnessBox) return VigRoundness;
            if (box == GrainIntensityBox) return GrainIntensity;
            if (box == GrainSizeBox) return GrainSize;
            if (box == CaStrengthBox) return CaStrength;
            if (box == CaFalloffBox) return CaFalloff;
            if (box == AoRadiusBox) return AoRadius;
            if (box == AoIntensityBox) return AoIntensity;
            if (box == BloomThresholdBox) return BloomThreshold;
            if (box == BloomKneeBox) return BloomKnee;
            if (box == BloomIntensityBox) return BloomIntensity;
            if (box == BloomScatterBox) return BloomScatter;
            if (box == ExposureBox) return Exposure;
            if (box == ContrastBox) return Contrast;
            if (box == SaturationBox) return Saturation;
            if (box == TemperatureBox) return Temperature;
            if (box == TintBox) return Tint;
            return null;
        }

        private void SyncBoxes()
        {
            FogDensityBox.Text = Fmt(FogDensity.Value);
            FogHeightYBox.Text = Fmt(FogHeightY.Value);
            FogFalloffBox.Text = Fmt(FogFalloff.Value);
            VigIntensityBox.Text = Fmt(VigIntensity.Value);
            VigSmoothnessBox.Text = Fmt(VigSmoothness.Value);
            VigRoundnessBox.Text = Fmt(VigRoundness.Value);
            GrainIntensityBox.Text = Fmt(GrainIntensity.Value);
            GrainSizeBox.Text = Fmt(GrainSize.Value);
            CaStrengthBox.Text = Fmt(CaStrength.Value);
            CaFalloffBox.Text = Fmt(CaFalloff.Value);
            AoRadiusBox.Text = Fmt(AoRadius.Value);
            AoIntensityBox.Text = Fmt(AoIntensity.Value);
            BloomThresholdBox.Text = Fmt(BloomThreshold.Value);
            BloomKneeBox.Text = Fmt(BloomKnee.Value);
            BloomIntensityBox.Text = Fmt(BloomIntensity.Value);
            BloomScatterBox.Text = Fmt(BloomScatter.Value);
            ExposureBox.Text = Fmt(Exposure.Value);
            ContrastBox.Text = Fmt(Contrast.Value);
            SaturationBox.Text = Fmt(Saturation.Value);
            TemperatureBox.Text = Fmt(Temperature.Value);
            TintBox.Text = Fmt(Tint.Value);
        }

        // ---------- helpers ----------

        private static void SetPair(Slider slider, TextBox box, float value)
        {
            slider.Value = value;
            box.Text = Fmt(value);
        }

        private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static Color ToColor(float r, float g, float b) => Color.FromRgb(
            (byte)Math.Max(0, Math.Min(255, (int)(r * 255f + 0.5f))),
            (byte)Math.Max(0, Math.Min(255, (int)(g * 255f + 0.5f))),
            (byte)Math.Max(0, Math.Min(255, (int)(b * 255f + 0.5f))));

        private static bool PickColor(Color initial, out Color chosen)
        {
            var dlg = new Editor.Dialogs.ColorPickerDialog(initial) { Owner = Application.Current?.MainWindow };
            if (dlg.ShowDialog() == true) { chosen = dlg.SelectedColor; return true; }
            chosen = initial;
            return false;
        }
    }
}
