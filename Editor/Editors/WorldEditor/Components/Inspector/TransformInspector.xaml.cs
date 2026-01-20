using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    public partial class TransformInspector : UserControl
    {
        private Transform _transform;
        private bool _isUpdating;

        public TransformInspector()
        {
            InitializeComponent();
            
            // Subscribe to transform changes from gizmo dragging
            SelectionService.Instance.TransformChanged += OnExternalTransformChanged;
            
            Unloaded += (s, e) =>
            {
                SelectionService.Instance.TransformChanged -= OnExternalTransformChanged;
            };
        }

        private void OnExternalTransformChanged(object sender, TransformChangedEventArgs e)
        {
            // Update UI when transform is changed externally (e.g., gizmo drag)
            if (_transform != null && e.Entity?.Transform == _transform)
            {
                Dispatcher.Invoke(() => UpdateUI());
            }
        }

        public Transform Transform
        {
            get => _transform;
            set
            {
                _transform = value;
                UpdateUI();
            }
        }

        private void UpdateUI()
        {
            if (_transform == null) return;

            _isUpdating = true;

            var pos = _transform.LocalPosition;
            var rot = _transform.LocalRotation;
            var scale = _transform.LocalScale;

            PosX.Text = pos.X.ToString("F2", CultureInfo.InvariantCulture);
            PosY.Text = pos.Y.ToString("F2", CultureInfo.InvariantCulture);
            PosZ.Text = pos.Z.ToString("F2", CultureInfo.InvariantCulture);

            RotX.Text = rot.X.ToString("F2", CultureInfo.InvariantCulture);
            RotY.Text = rot.Y.ToString("F2", CultureInfo.InvariantCulture);
            RotZ.Text = rot.Z.ToString("F2", CultureInfo.InvariantCulture);

            ScaleX.Text = scale.X.ToString("F2", CultureInfo.InvariantCulture);
            ScaleY.Text = scale.Y.ToString("F2", CultureInfo.InvariantCulture);
            ScaleZ.Text = scale.Z.ToString("F2", CultureInfo.InvariantCulture);

            _isUpdating = false;
        }

        private void Position_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || _transform == null) return;

            if (TryParseFloat(PosX.Text, out float x) &&
                TryParseFloat(PosY.Text, out float y) &&
                TryParseFloat(PosZ.Text, out float z))
            {
                _transform.LocalPosition = new Vector3(x, y, z);
                TransformChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Rotation_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || _transform == null) return;

            if (TryParseFloat(RotX.Text, out float x) &&
                TryParseFloat(RotY.Text, out float y) &&
                TryParseFloat(RotZ.Text, out float z))
            {
                _transform.LocalRotation = new Vector3(x, y, z);
                TransformChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Scale_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || _transform == null) return;

            if (TryParseFloat(ScaleX.Text, out float x) &&
                TryParseFloat(ScaleY.Text, out float y) &&
                TryParseFloat(ScaleZ.Text, out float z))
            {
                _transform.LocalScale = new Vector3(x, y, z);
                TransformChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private bool TryParseFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public event EventHandler TransformChanged;
    }
}
