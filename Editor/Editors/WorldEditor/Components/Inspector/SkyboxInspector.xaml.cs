using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Core.Services;
using Editor.ECS.Components.Rendering;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    public partial class SkyboxInspector : UserControl
    {
        private Skybox _skybox;
        private bool _isUpdating;

        public Skybox Skybox
        {
            get => _skybox;
            set
            {
                _skybox = value;
                UpdateUI();
            }
        }

        public SkyboxInspector()
        {
            InitializeComponent();
        }

        private void UpdateUI()
        {
            if (_skybox == null) return;
            
            _isUpdating = true;
            
            EnabledCheckBox.IsChecked = _skybox.IsEnabled;
            SkyboxTypeCombo.SelectedIndex = (int)_skybox.SkyboxType;
            
            // Ambient
            AmbientSlider.Value = _skybox.AmbientIntensity;
            AmbientValue.Text = _skybox.AmbientIntensity.ToString("F2");
            
            // Exposure
            ExposureSlider.Value = _skybox.Exposure;
            ExposureValue.Text = _skybox.Exposure.ToString("F1");
            
            // Colors
            UpdateColorPreviews();
            UpdateVisibility();
            
            _isUpdating = false;
        }

        private void UpdateColorPreviews()
        {
            if (_skybox == null) return;
            
            var topColor = Color.FromScRgb(1f, _skybox.TopColorR, _skybox.TopColorG, _skybox.TopColorB);
            TopColorPreview.Background = new SolidColorBrush(topColor);
            
            var horizonColor = Color.FromScRgb(1f, _skybox.HorizonColorR, _skybox.HorizonColorG, _skybox.HorizonColorB);
            HorizonColorPreview.Background = new SolidColorBrush(horizonColor);
            
            var bottomColor = Color.FromScRgb(1f, _skybox.BottomColorR, _skybox.BottomColorG, _skybox.BottomColorB);
            BottomColorPreview.Background = new SolidColorBrush(bottomColor);
        }

        private void UpdateVisibility()
        {
            if (_skybox == null) return;
            
            bool isSolidColor = _skybox.SkyboxType == SkyboxType.SolidColor;
            bool isGradient = _skybox.SkyboxType == SkyboxType.Gradient;
            bool isCubemap = _skybox.SkyboxType == SkyboxType.Cubemap;
            bool isTexture = _skybox.SkyboxType == SkyboxType.Texture;
            
            // Colors visible for SolidColor and Gradient
            // For SolidColor, only show "Sky Color" as the single color
            HorizonColorRow.Visibility = isGradient ? Visibility.Visible : Visibility.Collapsed;
            BottomColorRow.Visibility = isGradient ? Visibility.Visible : Visibility.Collapsed;
            
            // Asset paths
            CubemapRow.Visibility = isCubemap ? Visibility.Visible : Visibility.Collapsed;
            TextureRow.Visibility = isTexture ? Visibility.Visible : Visibility.Collapsed;
            MeshRow.Visibility = (isCubemap || isTexture) ? Visibility.Visible : Visibility.Collapsed;
            
            // Update path text boxes
            if (isCubemap && !string.IsNullOrEmpty(_skybox.CubemapPath))
                CubemapPathText.Text = System.IO.Path.GetFileName(_skybox.CubemapPath);
            
            if (isTexture)
            {
                if (!string.IsNullOrEmpty(_skybox.TexturePath))
                    TexturePathText.Text = System.IO.Path.GetFileName(_skybox.TexturePath);
                else
                    TexturePathText.Text = "(Not set - click ... to select)";
            }
            
            if (!string.IsNullOrEmpty(_skybox.SkyboxMeshPath))
                MeshPathText.Text = System.IO.Path.GetFileName(_skybox.SkyboxMeshPath);
            else
                MeshPathText.Text = "(Built-in Sphere)";
        }

        private void EnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_skybox != null && !_isUpdating)
            {
                _skybox.IsEnabled = EnabledCheckBox.IsChecked ?? true;
            }
        }

        private void SkyboxTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_skybox != null && !_isUpdating && SkyboxTypeCombo.SelectedIndex >= 0)
            {
                _skybox.SkyboxType = (SkyboxType)SkyboxTypeCombo.SelectedIndex;
                UpdateVisibility();
            }
        }

        private void AmbientSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_skybox != null && !_isUpdating)
            {
                _isUpdating = true;
                _skybox.AmbientIntensity = (float)AmbientSlider.Value;
                AmbientValue.Text = _skybox.AmbientIntensity.ToString("F2");
                _isUpdating = false;
            }
        }

        private void AmbientValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_skybox != null && !_isUpdating && float.TryParse(AmbientValue.Text, out float value))
            {
                _isUpdating = true;
                _skybox.AmbientIntensity = Math.Max(0, Math.Min(2, value));
                AmbientSlider.Value = _skybox.AmbientIntensity;
                _isUpdating = false;
            }
        }

        private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_skybox != null && !_isUpdating)
            {
                _isUpdating = true;
                _skybox.Exposure = (float)ExposureSlider.Value;
                ExposureValue.Text = _skybox.Exposure.ToString("F1");
                _isUpdating = false;
            }
        }

        private void ExposureValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_skybox != null && !_isUpdating && float.TryParse(ExposureValue.Text, out float value))
            {
                _isUpdating = true;
                _skybox.Exposure = Math.Max(0.1f, Math.Min(4f, value));
                ExposureSlider.Value = _skybox.Exposure;
                _isUpdating = false;
            }
        }

        private void TopColorPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_skybox == null) return;
            
            var dialog = new Dialogs.ColorPickerDialog();
            dialog.SelectedColor = Color.FromScRgb(1f, _skybox.TopColorR, _skybox.TopColorG, _skybox.TopColorB);
            
            if (dialog.ShowDialog() == true)
            {
                var color = dialog.SelectedColor;
                _skybox.TopColorR = color.ScR;
                _skybox.TopColorG = color.ScG;
                _skybox.TopColorB = color.ScB;
                TopColorPreview.Background = new SolidColorBrush(color);
            }
        }

        private void HorizonColorPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_skybox == null) return;
            
            var dialog = new Dialogs.ColorPickerDialog();
            dialog.SelectedColor = Color.FromScRgb(1f, _skybox.HorizonColorR, _skybox.HorizonColorG, _skybox.HorizonColorB);
            
            if (dialog.ShowDialog() == true)
            {
                var color = dialog.SelectedColor;
                _skybox.HorizonColorR = color.ScR;
                _skybox.HorizonColorG = color.ScG;
                _skybox.HorizonColorB = color.ScB;
                HorizonColorPreview.Background = new SolidColorBrush(color);
            }
        }

        private void BottomColorPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_skybox == null) return;
            
            var dialog = new Dialogs.ColorPickerDialog();
            dialog.SelectedColor = Color.FromScRgb(1f, _skybox.BottomColorR, _skybox.BottomColorG, _skybox.BottomColorB);
            
            if (dialog.ShowDialog() == true)
            {
                var color = dialog.SelectedColor;
                _skybox.BottomColorR = color.ScR;
                _skybox.BottomColorG = color.ScG;
                _skybox.BottomColorB = color.ScB;
                BottomColorPreview.Background = new SolidColorBrush(color);
            }
        }

        private void BrowseCubemap_Click(object sender, RoutedEventArgs e)
        {
            if (_skybox == null) return;
            
            // Use AssetPicker to select from project assets
            var dialog = new Dialogs.AssetPickerDialog("Textures");
            dialog.Owner = Window.GetWindow(this);
            dialog.Title = "Select Cubemap Texture";
            
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.SelectedAssetPath))
            {
                _skybox.CubemapPath = dialog.SelectedAssetPath;
                CubemapPathText.Text = System.IO.Path.GetFileName(dialog.SelectedAssetPath);
                
                // Clear cache so changes take effect
                SceneRenderService.Instance.ClearSkyboxMeshCache();
            }
        }

        private void BrowseTexture_Click(object sender, RoutedEventArgs e)
        {
            if (_skybox == null) return;
            
            // Use AssetPicker to select from project assets
            var dialog = new Dialogs.AssetPickerDialog("Textures");
            dialog.Owner = Window.GetWindow(this);
            dialog.Title = "Select Skybox Texture";
            
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.SelectedAssetPath))
            {
                _skybox.TexturePath = dialog.SelectedAssetPath;
                TexturePathText.Text = System.IO.Path.GetFileName(dialog.SelectedAssetPath);
                
                // Clear cache so changes take effect
                SceneRenderService.Instance.ClearSkyboxMeshCache();
            }
        }

        private void BrowseMesh_Click(object sender, RoutedEventArgs e)
        {
            if (_skybox == null) return;
            
            // Use AssetPicker to select from project assets
            var dialog = new Dialogs.AssetPickerDialog("Models");
            dialog.Owner = Window.GetWindow(this);
            dialog.Title = "Select Skybox Mesh";
            
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.SelectedAssetPath))
            {
                _skybox.SkyboxMeshPath = dialog.SelectedAssetPath;
                MeshPathText.Text = System.IO.Path.GetFileName(dialog.SelectedAssetPath);
                
                // Clear cache so changes take effect
                SceneRenderService.Instance.ClearSkyboxMeshCache();
            }
        }
    }
}
