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
            
            // Shadow type
            ShadowTypeCombo.SelectedIndex = (int)_light.ShadowType;
            
            UpdateVisibility();
            
            _isUpdating = false;
        }

        private void UpdateVisibility()
        {
            if (_light == null) return;
            
            bool isDirectional = _light.LightType == LightType.Directional;
            bool isSpot = _light.LightType == LightType.Spot;
            
            RangeRow.Visibility = isDirectional ? Visibility.Collapsed : Visibility.Visible;
            SpotAngleRow.Visibility = isSpot ? Visibility.Visible : Visibility.Collapsed;
            InnerSpotAngleRow.Visibility = isSpot ? Visibility.Visible : Visibility.Collapsed;
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
    }
}
