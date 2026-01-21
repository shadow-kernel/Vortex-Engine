using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Editor.Core.Services;
using Editor.DllWrapper;

namespace Editor.Editors.WorldEditor.Components.CameraViewport
{
    /// <summary>
    /// Input settings dialog for configuring camera controls and keybindings.
    /// </summary>
    public partial class InputSettingsDialog : Window
    {
        private Button _waitingForKeyButton;
        private bool _isWaitingForKey;

        public InputSettingsDialog()
        {
            InitializeComponent();
            LoadCurrentSettings();
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void LoadCurrentSettings()
        {
            var controller = EditorCameraController.Instance;
            
            MoveSpeedSlider.Value = controller.MoveSpeed;
            LookSpeedSlider.Value = controller.LookSpeed;
            SprintMultSlider.Value = controller.SprintMultiplier;
            
            UpdateSliderLabels();
        }

        private void UpdateSliderLabels()
        {
            MoveSpeedValue.Text = MoveSpeedSlider.Value.ToString("F1");
            LookSpeedValue.Text = LookSpeedSlider.Value.ToString("F2");
            SprintMultValue.Text = SprintMultSlider.Value.ToString("F1");
        }

        #region Key Binding

        private void KeyBind_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                // Cancel previous waiting
                if (_waitingForKeyButton != null)
                {
                    _waitingForKeyButton.Content = _waitingForKeyButton.Tag.ToString();
                }

                _waitingForKeyButton = btn;
                _isWaitingForKey = true;
                btn.Content = "Press key...";
                btn.Focus();
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isWaitingForKey || _waitingForKeyButton == null) return;

            // Convert WPF key to display string
            string keyName = GetKeyDisplayName(e.Key);
            
            if (keyName != null)
            {
                _waitingForKeyButton.Content = keyName;
                SaveKeyBinding(_waitingForKeyButton.Tag.ToString(), e.Key);
            }

            _isWaitingForKey = false;
            _waitingForKeyButton = null;
            e.Handled = true;
        }

        private string GetKeyDisplayName(Key key)
        {
            switch (key)
            {
                case Key.Escape:
                    return null; // Cancel
                case Key.LeftShift:
                case Key.RightShift:
                    return "Shift";
                case Key.LeftCtrl:
                case Key.RightCtrl:
                    return "Ctrl";
                case Key.LeftAlt:
                case Key.RightAlt:
                    return "Alt";
                case Key.Space:
                    return "Space";
                default:
                    return key.ToString();
            }
        }

        private void SaveKeyBinding(string action, Key key)
        {
            // Save to InputBindings service
            InputBindingsService.Instance.SetBinding(action, key);
        }

        #endregion

        #region Slider Events

        private void MoveSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MoveSpeedValue != null)
            {
                MoveSpeedValue.Text = e.NewValue.ToString("F1");
            }
        }

        private void LookSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LookSpeedValue != null)
            {
                LookSpeedValue.Text = e.NewValue.ToString("F2");
            }
        }

        private void SprintMult_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SprintMultValue != null)
            {
                SprintMultValue.Text = e.NewValue.ToString("F1");
            }
        }

        #endregion

        #region Button Events

        private void ResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            // Reset all to defaults
            ForwardKeyBtn.Content = "W";
            BackwardKeyBtn.Content = "S";
            LeftKeyBtn.Content = "A";
            RightKeyBtn.Content = "D";
            UpKeyBtn.Content = "E";
            DownKeyBtn.Content = "Q";
            SprintKeyBtn.Content = "Shift";

            MoveSpeedSlider.Value = 5.0;
            LookSpeedSlider.Value = 0.2;
            SprintMultSlider.Value = 2.5;

            EnableGameInputCheck.IsChecked = true;
            LockCursorInPlayCheck.IsChecked = true;
            InvertYCheck.IsChecked = false;
            InvertXCheck.IsChecked = false;
            InputTargetCombo.SelectedIndex = 0;
            FlyModeButtonCombo.SelectedIndex = 0;

            // Reset bindings
            InputBindingsService.Instance.ResetToDefaults();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            ApplySettings();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ApplySettings()
        {
            var controller = EditorCameraController.Instance;
            
            controller.MoveSpeed = (float)MoveSpeedSlider.Value;
            controller.LookSpeed = (float)LookSpeedSlider.Value;
            controller.SprintMultiplier = (float)SprintMultSlider.Value;

            // Apply input settings
            var inputSettings = InputBindingsService.Instance;
            inputSettings.EnableGameInputForwarding = EnableGameInputCheck.IsChecked ?? true;
            inputSettings.LockCursorInPlayMode = LockCursorInPlayCheck.IsChecked ?? true;
            inputSettings.InvertY = InvertYCheck.IsChecked ?? false;
            inputSettings.InvertX = InvertXCheck.IsChecked ?? false;
            inputSettings.InputTarget = (InputTarget)InputTargetCombo.SelectedIndex;
            inputSettings.FlyModeButton = (FlyModeMouseButton)FlyModeButtonCombo.SelectedIndex;
        }

        #endregion
    }
}
