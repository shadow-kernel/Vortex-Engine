using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.ECS.Components.Lighting;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    public partial class LightInspector : UserControl
    {
        private Light _light;
        private bool _isUpdating;

        public Light Light
        {
            get => _light;
            set
            {
                _light = value;
                UpdateUI();
            }
        }

        public LightInspector()
        {
            InitializeComponent();
        }

        /// <summary>Raised when the user clicks the header remove button; the host detaches the component.</summary>
        public event EventHandler RemoveRequested;
        private void Remove_Click(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this, EventArgs.Empty);

        private void UpdateUI()
        {
            if (_light == null) return;
            
            _isUpdating = true;
            
            HeaderText.Text = _light.DisplayName;
            EnabledCheckBox.IsChecked = _light.IsEnabled;
            LightTypeCombo.SelectedIndex = (int)_light.LightType;
            
            // Color
            var color = Color.FromScRgb(1f, _light.ColorR, _light.ColorG, _light.ColorB);
            ColorPreview.Background = new SolidColorBrush(color);
            
            // Intensity
            IntensitySlider.Value = _light.Intensity;
            IntensityValue.Text = _light.Intensity.ToString("F1");
            
            // Range
            RangeSlider.Value = _light.Range;
            RangeValue.Text = _light.Range.ToString("F1");
            
            // Spot angles
            SpotAngleSlider.Value = _light.SpotAngle;
            SpotAngleValue.Text = _light.SpotAngle.ToString("F0");
            InnerSpotAngleSlider.Value = _light.InnerSpotAngle;
            InnerSpotAngleValue.Text = _light.InnerSpotAngle.ToString("F0");
            
            // Shadows
            ShadowTypeCombo.SelectedIndex = (int)_light.ShadowType;
            ShadowStrengthSlider.Value = _light.ShadowStrength;
            ShadowStrengthValue.Text = _light.ShadowStrength.ToString("F2");
            ShadowBiasSlider.Value = Math.Min(Math.Max(_light.ShadowBias, 0.0), 0.2);
            ShadowBiasValue.Text = _light.ShadowBias.ToString("F3");
            ShadowResolutionCombo.SelectedIndex = ResolutionToIndex(_light.ShadowResolution);

            UpdateVisibility();

            _isUpdating = false;
        }

        private static int ResolutionToIndex(int res) => res <= 512 ? 0 : res <= 1024 ? 1 : res <= 2048 ? 2 : 3;
        private static int IndexToResolution(int idx) => idx == 0 ? 512 : idx == 1 ? 1024 : idx == 3 ? 4096 : 2048;

        private void UpdateVisibility()
        {
            if (_light == null) return;

            bool isDirectional = _light.LightType == LightType.Directional;
            bool isSpot = _light.LightType == LightType.Spot;

            RangeRow.Visibility = isDirectional ? Visibility.Collapsed : Visibility.Visible;
            SpotAngleRow.Visibility = isSpot ? Visibility.Visible : Visibility.Collapsed;
            InnerSpotAngleRow.Visibility = isSpot ? Visibility.Visible : Visibility.Collapsed;

            // Shadow detail rows only make sense while shadows are enabled; the hint explains that only
            // spot lights render a real shadow map today (#23 — directional/point are #24/#25).
            bool shadowsOn = _light.ShadowType != ShadowType.None;
            var rows = shadowsOn ? Visibility.Visible : Visibility.Collapsed;
            ShadowStrengthRow.Visibility = rows;
            ShadowBiasRow.Visibility = rows;
            ShadowResolutionRow.Visibility = rows;
            ShadowHint.Visibility = shadowsOn && !isSpot ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_light != null && !_isUpdating)
            {
                _light.IsEnabled = EnabledCheckBox.IsChecked ?? true;
            }
        }

        private void LightTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_light != null && !_isUpdating && LightTypeCombo.SelectedIndex >= 0)
            {
                _light.LightType = (LightType)LightTypeCombo.SelectedIndex;
                HeaderText.Text = _light.DisplayName;
                UpdateVisibility();
            }
        }

        private void ColorPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_light == null) return;
            
            // Open color picker dialog
            var dialog = new Dialogs.ColorPickerDialog();
            dialog.SelectedColor = Color.FromScRgb(1f, _light.ColorR, _light.ColorG, _light.ColorB);
            dialog.Owner = Window.GetWindow(this);
            
            if (dialog.ShowDialog() == true)
            {
                var color = dialog.SelectedColor;
                _light.ColorR = color.ScR;
                _light.ColorG = color.ScG;
                _light.ColorB = color.ScB;
                ColorPreview.Background = new SolidColorBrush(color);
            }
        }

        private void IntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_light != null && !_isUpdating)
            {
                _light.Intensity = (float)IntensitySlider.Value;
                IntensityValue.Text = _light.Intensity.ToString("F1");
            }
        }

        private void IntensityValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_light != null && !_isUpdating && float.TryParse(IntensityValue.Text, out float value))
            {
                _light.Intensity = Math.Max(0, value);
                _isUpdating = true;
                IntensitySlider.Value = _light.Intensity;
                _isUpdating = false;
            }
        }

        private void RangeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_light != null && !_isUpdating)
            {
                _light.Range = (float)RangeSlider.Value;
                RangeValue.Text = _light.Range.ToString("F1");
            }
        }

        private void RangeValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_light != null && !_isUpdating && float.TryParse(RangeValue.Text, out float value))
            {
                _light.Range = Math.Max(0.1f, value);
                _isUpdating = true;
                RangeSlider.Value = _light.Range;
                _isUpdating = false;
            }
        }

        private void SpotAngleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_light != null && !_isUpdating)
            {
                _light.SpotAngle = (float)SpotAngleSlider.Value;
                SpotAngleValue.Text = _light.SpotAngle.ToString("F0");
            }
        }

        private void InnerSpotAngleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_light != null && !_isUpdating)
            {
                _light.InnerSpotAngle = (float)InnerSpotAngleSlider.Value;
                InnerSpotAngleValue.Text = _light.InnerSpotAngle.ToString("F0");
            }
        }

        // ---- Shadows (#23) ----

        private void ShadowTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_light != null && !_isUpdating && ShadowTypeCombo.SelectedIndex >= 0)
            {
                _light.ShadowType = (ShadowType)ShadowTypeCombo.SelectedIndex;
                UpdateVisibility();
            }
        }

        private void ShadowStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_light != null && !_isUpdating)
            {
                _light.ShadowStrength = (float)ShadowStrengthSlider.Value;
                ShadowStrengthValue.Text = _light.ShadowStrength.ToString("F2");
            }
        }

        private void ShadowStrengthValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_light != null && !_isUpdating && float.TryParse(ShadowStrengthValue.Text, out float value))
            {
                _light.ShadowStrength = Math.Min(Math.Max(value, 0f), 1f);
                _isUpdating = true;
                ShadowStrengthSlider.Value = _light.ShadowStrength;
                _isUpdating = false;
            }
        }

        private void ShadowBiasSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_light != null && !_isUpdating)
            {
                _light.ShadowBias = (float)ShadowBiasSlider.Value;
                ShadowBiasValue.Text = _light.ShadowBias.ToString("F3");
            }
        }

        private void ShadowBiasValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_light != null && !_isUpdating && float.TryParse(ShadowBiasValue.Text, out float value))
            {
                _light.ShadowBias = Math.Max(0f, value);
                _isUpdating = true;
                ShadowBiasSlider.Value = Math.Min(_light.ShadowBias, 0.2f);
                _isUpdating = false;
            }
        }

        private void ShadowResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_light != null && !_isUpdating && ShadowResolutionCombo.SelectedIndex >= 0)
            {
                _light.ShadowResolution = IndexToResolution(ShadowResolutionCombo.SelectedIndex);
            }
        }
    }
}
