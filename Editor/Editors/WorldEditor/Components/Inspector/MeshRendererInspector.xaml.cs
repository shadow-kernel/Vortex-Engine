using System;
using System.Windows;
using System.Windows.Controls;
using Editor.DllWrapper;
using Editor.ECS.Components.Rendering;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    public partial class MeshRendererInspector : UserControl
    {
        private MeshRenderer _meshRenderer;
        private bool _isUpdating;

        public MeshRendererInspector()
        {
            InitializeComponent();
        }

        /// <summary>Raised when the user clicks the header remove button; the host detaches the component.</summary>
        public event EventHandler RemoveRequested;
        private void Remove_Click(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this, EventArgs.Empty);

        public MeshRenderer MeshRenderer
        {
            get => _meshRenderer;
            set
            {
                _meshRenderer = value;
                DataContext = _meshRenderer;
                UpdateUI();
            }
        }

        private void UpdateUI()
        {
            if (_meshRenderer == null) return;

            _isUpdating = true;

            // Set mesh selection based on path
            var meshPath = _meshRenderer.MeshPath?.ToLower() ?? "";
            if (meshPath.Contains("cube"))
                MeshComboBox.SelectedIndex = 1;
            else if (meshPath.Contains("sphere"))
                MeshComboBox.SelectedIndex = 2;
            else if (meshPath.Contains("plane"))
                MeshComboBox.SelectedIndex = 3;
            else if (meshPath.Contains("cylinder"))
                MeshComboBox.SelectedIndex = 4;
            else if (meshPath.Contains("cone"))
                MeshComboBox.SelectedIndex = 5;
            else
                MeshComboBox.SelectedIndex = 0;

            // Material: SHOW the actually-assigned material (from MaterialPath) instead of always "Default". A path
            // assigned in code or a picker (e.g. "Assets/Materials/grass.vmat") now displays as "grass". (Fixes the
            // bug where an assigned material read as unset.)
            MaterialComboBox.Items.Clear();
            MaterialComboBox.Items.Add(new ComboBoxItem { Content = "Default" });
            var matPath = _meshRenderer.MaterialPath;
            bool hasMat = !string.IsNullOrEmpty(matPath) &&
                          !matPath.StartsWith("Material:", StringComparison.OrdinalIgnoreCase); // legacy placeholder = unset
            if (hasMat)
            {
                MaterialComboBox.Items.Add(new ComboBoxItem { Content = System.IO.Path.GetFileNameWithoutExtension(matPath) });
                MaterialComboBox.SelectedIndex = 1;
            }
            else
            {
                MaterialComboBox.SelectedIndex = 0;
            }

            _isUpdating = false;
        }

        private void MeshComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating || _meshRenderer == null) return;

            var selectedItem = MeshComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null) return;

            var meshType = selectedItem.Content.ToString();
            _meshRenderer.MeshPath = meshType == "None" ? null : $"Primitive:{meshType}";

            OnMeshChanged(meshType);
        }

        private void MaterialComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating || _meshRenderer == null) return;

            var selectedItem = MaterialComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null) return;

            // "Default" clears the assignment (engine default material). Any other item is the already-assigned
            // material's display name (informational) — changing the actual material is done via the picker button.
            if ((selectedItem.Content as string) == "Default")
                _meshRenderer.MaterialPath = null;
        }

        private void SelectMeshButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = AssetPickerDialog.CreateForMeshes();
            dialog.Owner = Window.GetWindow(this);
            
            if (dialog.ShowDialog() == true && dialog.SelectedAsset != null)
            {
                _meshRenderer.MeshPath = dialog.SelectedAsset.Path;
                UpdateUI();
                OnMeshChanged(dialog.SelectedAsset.Name);
            }
        }

        private void SelectMaterialButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = AssetPickerDialog.CreateForMaterials();
            dialog.Owner = Window.GetWindow(this);
            
            if (dialog.ShowDialog() == true && dialog.SelectedAsset != null)
            {
                _meshRenderer.MaterialPath = dialog.SelectedAsset.Path;
                UpdateUI();   // reflect the new material in the dropdown immediately
            }
        }

        private void OnMeshChanged(string meshType)
        {
            MeshChanged?.Invoke(this, meshType);
        }

        public event EventHandler<string> MeshChanged;
        public event EventHandler MeshPickerRequested;
        public event EventHandler MaterialPickerRequested;
    }
}
