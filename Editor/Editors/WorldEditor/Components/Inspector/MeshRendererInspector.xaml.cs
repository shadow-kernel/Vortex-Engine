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

            // Material slot: SHOW the actually-assigned material (from MaterialPath). A path assigned in
            // code, via drag&drop or the picker (e.g. "Assets/Materials/grass.vmat") displays as "grass";
            // no assignment (or a legacy "Material:" placeholder) reads "Default" = engine default material.
            var matPath = _meshRenderer.MaterialPath;
            bool hasMat = !string.IsNullOrEmpty(matPath) &&
                          !matPath.StartsWith("Material:", StringComparison.OrdinalIgnoreCase); // legacy placeholder = unset
            MaterialNameText.Text = hasMat ? System.IO.Path.GetFileNameWithoutExtension(matPath) : "Default";
            MaterialSlot.ToolTip = hasMat
                ? matPath + "\nDouble-click: open in the Material Editor"
                : "Drop a .vmat here — double-click to open it in the Material Editor";

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

        private void ClearMaterialButton_Click(object sender, RoutedEventArgs e)
        {
            if (_meshRenderer == null) return;
            _meshRenderer.MaterialPath = null;   // back to the engine default material
            UpdateUI();
            GamePreview.GamePreviewView.RequestResubmit();
        }

        private void MaterialSlot_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = Editor.Editors.WorldEditor.DragDrop.ViewportDropHandler.GetMaterialDropPath(e.Data) != null
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void MaterialSlot_Drop(object sender, DragEventArgs e)
        {
            if (_meshRenderer == null) return;

            var vmatPath = Editor.Editors.WorldEditor.DragDrop.ViewportDropHandler.GetMaterialDropPath(e.Data);
            if (vmatPath == null) return;

            _meshRenderer.MaterialPath = ToProjectRelativePath(vmatPath);
            UpdateUI();
            GamePreview.GamePreviewView.RequestResubmit();
            e.Handled = true;
        }

        private void MaterialSlot_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2 || _meshRenderer == null) return;

            var matPath = _meshRenderer.MaterialPath;
            if (string.IsNullOrEmpty(matPath) || matPath.StartsWith("Material:", StringComparison.OrdinalIgnoreCase))
                return;

            var projectPath = Editor.Core.Data.ProjectData.Current?.Path ?? "";
            string fullPath = System.IO.Path.IsPathRooted(matPath) ? matPath : System.IO.Path.Combine(projectPath, matPath);
            if (!System.IO.File.Exists(fullPath)) return;

            try { Dialogs.MaterialEditorDialog.OpenMaterial(Window.GetWindow(this), fullPath); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MeshRendererInspector] Material Editor failed: {ex.Message}"); }
            e.Handled = true;
        }

        /// <summary>Serialized asset paths are project-relative with forward slashes — normalize a picked/dropped path.</summary>
        private static string ToProjectRelativePath(string path)
        {
            var projectPath = Editor.Core.Data.ProjectData.Current?.Path;
            string rel = path;
            if (!string.IsNullOrEmpty(projectPath) && System.IO.Path.IsPathRooted(rel) &&
                rel.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
            {
                rel = rel.Substring(projectPath.Length).TrimStart('\\', '/');
            }
            return rel.Replace('\\', '/');
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
                // "Default" carries a null Path = clear the assignment (engine default material).
                _meshRenderer.MaterialPath = string.IsNullOrEmpty(dialog.SelectedAsset.Path) ? null : dialog.SelectedAsset.Path;
                UpdateUI();   // reflect the new material in the slot immediately
                GamePreview.GamePreviewView.RequestResubmit();
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
