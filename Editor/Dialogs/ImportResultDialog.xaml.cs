using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Core.Assets;

namespace Editor.Dialogs
{
    /// <summary>
    /// Data for a single submesh import
    /// </summary>
    public class SubmeshImportData
    {
        public long MeshId { get; set; } = -1;
        public long MaterialId { get; set; } = -1;
        public long TextureId { get; set; } = -1;
        public string Name { get; set; }
        public string TexturePath { get; set; }
    }

    /// <summary>
    /// Model import result data.
    /// </summary>
    public class ModelImportResult
    {
        public string ModelName { get; set; }
        public string SourcePath { get; set; }
        public string AssetPath { get; set; }
        public string RelativePath { get; set; }
        public long MeshId { get; set; } = -1;
        public long MaterialId { get; set; } = -1;
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        
        // Import statistics
        public int SubmeshCount { get; set; }
        public List<string> SubmeshNames { get; set; } = new List<string>();
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        
        // Materials
        public List<string> MaterialNames { get; set; } = new List<string>();
        
        // Textures found/imported
        public List<string> TexturePaths { get; set; } = new List<string>();
        public List<string> ImportedTextures { get; set; } = new List<string>();
        public List<string> MissingTextures { get; set; } = new List<string>();
        
        // Asset GUID for AssetDatabase integration
        public Guid AssetGuid { get; set; }

        // New structured asset graph built from import (for editor/engine integration)
        public ModelAsset BuiltModel { get; set; }

        // Multi-material import data (one per submesh)
        public List<SubmeshImportData> Submeshes { get; set; } = new List<SubmeshImportData>();
        
        /// <summary>
        /// True if this is a multi-material import with separate submeshes
        /// </summary>
        public bool IsMultiMaterial => Submeshes.Count > 1;
    }

    /// <summary>
    /// Dialog to display model import results.
    /// </summary>
    public partial class ImportResultDialog : Window
    {
        private ModelImportResult _result;
        private HashSet<string> _selectedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        public bool AddToSceneRequested { get; private set; }
        public bool OpenAssetBrowserRequested { get; private set; }

        public ImportResultDialog(ModelImportResult result)
        {
            InitializeComponent();
            _result = result;
            PopulateDialog();
            PopulateTags();
        }

        private void PopulateTags()
        {
            // Find the TagsPanel in the visual tree
            var tagsPanel = FindName("TagsPanel") as WrapPanel;
            if (tagsPanel == null) return;
            
            tagsPanel.Children.Clear();
            
            // Add predefined tags as checkboxes
            foreach (var tag in AssetTagService.PredefinedTags)
            {
                var checkbox = new CheckBox
                {
                    Content = tag,
                    Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                    Margin = new Thickness(0, 0, 15, 8),
                    FontSize = 12
                };
                checkbox.Checked += (s, e) => _selectedTags.Add(tag);
                checkbox.Unchecked += (s, e) => _selectedTags.Remove(tag);
                tagsPanel.Children.Add(checkbox);
            }
        }

        private void SaveTags()
        {
            if (_result == null || !_result.Success || _selectedTags.Count == 0)
                return;

            // Save tags for the imported asset
            if (_result.AssetGuid != Guid.Empty)
            {
                AssetTagService.Instance.SetTags(_result.AssetGuid, _selectedTags);
                
                // Also save to vmeta file if asset path is known
                if (!string.IsNullOrEmpty(_result.AssetPath))
                {
                    var metaPath = _result.AssetPath + ".vmeta";
                    try
                    {
                        var meta = AssetMetadataService.Instance.LoadMetadata(metaPath);
                        if (meta == null)
                        {
                            meta = new AssetMetadata
                            {
                                Guid = _result.AssetGuid,
                                FileName = _result.ModelName,
                                RelativePath = _result.RelativePath,
                                Type = AssetType.Mesh
                            };
                        }
                        meta.Tags = _selectedTags.ToList();
                        AssetMetadataService.Instance.SaveMetadata(metaPath, meta);
                        
                        System.Diagnostics.Debug.WriteLine($"[ImportResultDialog] Saved tags for {_result.ModelName}: {string.Join(", ", _selectedTags)}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ImportResultDialog] Error saving tags: {ex.Message}");
                    }
                }
            }
        }

        private void PopulateDialog()
        {
            if (_result == null) return;

            // Header
            if (_result.Success)
            {
                TitleText.Text = "Import Successful";
                if (_result.IsMultiMaterial)
                {
                    SubtitleText.Text = $"Model imported with {_result.Submeshes.Count} submeshes (multi-material)";
                }
                else
                {
                    SubtitleText.Text = $"Model imported and ready to use";
                }
            }
            else
            {
                TitleText.Text = "Import Failed";
                TitleText.Foreground = System.Windows.Media.Brushes.Red;
                SubtitleText.Text = _result.ErrorMessage ?? "Unknown error occurred";
                AddToSceneButton.IsEnabled = false;
                OpenAssetBrowserButton.IsEnabled = false;
            }

            // Model info
            ModelNameText.Text = $"Name: {_result.ModelName}";
            ModelPathText.Text = $"Source: {_result.SourcePath}";

            // Mesh info - prioritize Submeshes list for multi-material
            if (_result.Submeshes.Count > 0)
            {
                MeshCountText.Text = $"Submeshes: {_result.Submeshes.Count} (separate materials)";
                foreach (var submesh in _result.Submeshes)
                {
                    var info = $"� {submesh.Name}";
                    if (submesh.TextureId >= 0)
                        info += " [textured]";
                    MeshListPanel.Children.Add(CreateListItem(info));
                }
            }
            else if (_result.SubmeshCount > 0)
            {
                MeshCountText.Text = $"Submeshes: {_result.SubmeshCount}";
                if (_result.VertexCount > 0)
                    MeshCountText.Text += $" | Vertices: {_result.VertexCount:N0} | Triangles: {_result.TriangleCount:N0}";
                
                foreach (var meshName in _result.SubmeshNames)
                {
                    MeshListPanel.Children.Add(CreateListItem($"� {meshName}"));
                }
            }
            else
            {
                MeshCountText.Text = _result.Success ? "1 mesh imported" : "No meshes found";
            }

            // Materials info
            if (_result.Submeshes.Count > 0)
            {
                int materialsWithTextures = 0;
                foreach (var sub in _result.Submeshes)
                {
                    if (sub.TextureId >= 0) materialsWithTextures++;
                }
                MaterialCountText.Text = $"Created: {_result.Submeshes.Count} material(s), {materialsWithTextures} with textures";
            }
            else if (_result.MaterialNames.Count > 0)
            {
                MaterialCountText.Text = $"Found: {_result.MaterialNames.Count} material(s)";
                foreach (var matName in _result.MaterialNames)
                {
                    MaterialListPanel.Children.Add(CreateListItem($"� {matName}"));
                }
            }
            else
            {
                MaterialCountText.Text = "No materials found (using default)";
            }

            // Textures info
            if (_result.ImportedTextures.Count > 0 || _result.MissingTextures.Count > 0)
            {
                var imported = _result.ImportedTextures.Count;
                var missing = _result.MissingTextures.Count;
                var total = imported + missing;
                
                TextureCountText.Text = $"Found: {total} texture reference(s)";
                
                foreach (var tex in _result.ImportedTextures)
                {
                    var item = CreateListItem($"? {Path.GetFileName(tex)}");
                    item.Foreground = System.Windows.Media.Brushes.LightGreen;
                    TextureListPanel.Children.Add(item);
                }
                
                foreach (var tex in _result.MissingTextures)
                {
                    var item = CreateListItem($"? {Path.GetFileName(tex)} (not found)");
                    item.Foreground = System.Windows.Media.Brushes.Orange;
                    TextureListPanel.Children.Add(item);
                }
            }
            else if (_result.TexturePaths.Count > 0)
            {
                TextureCountText.Text = $"Referenced: {_result.TexturePaths.Count} texture(s)";
                foreach (var tex in _result.TexturePaths)
                {
                    TextureListPanel.Children.Add(CreateListItem($"� {Path.GetFileName(tex)}"));
                }
            }
            else
            {
                TextureCountText.Text = "No textures referenced";
            }

            // Asset location
            AssetLocationText.Text = !string.IsNullOrEmpty(_result.AssetPath) 
                ? _result.AssetPath 
                : _result.RelativePath ?? "N/A";
        }

        private TextBlock CreateListItem(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(20, 1, 0, 1)
            };
        }

        private void OpenAssetBrowser_Click(object sender, RoutedEventArgs e)
        {
            SaveTags(); // Save any selected tags
            OpenAssetBrowserRequested = true;
            DialogResult = true;
            Close();
        }

        private void AddToScene_Click(object sender, RoutedEventArgs e)
        {
            // Prevent double-click
            if (AddToSceneRequested)
                return;
            
            SaveTags(); // Save any selected tags
            
            // Mark as requested immediately to prevent double-clicks
            AddToSceneRequested = true;
            
            // Disable the button visually
            if (sender is System.Windows.Controls.Button btn)
            {
                btn.IsEnabled = false;
            }
                
            try
            {
                // Get the active scene
                var scene = Core.Data.ProjectData.Current?.ActiveScene;
                if (scene == null)
                {
                    MessageBox.Show("No active scene. Please open a scene first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    AddToSceneRequested = false;
                    return;
                }

                var projectPath = Core.Data.ProjectData.Current?.Path ?? "";

                // Multi-material import: Create parent entity with child entities
                if (_result.IsMultiMaterial && _result.Submeshes.Count > 1)
                {
                    System.Diagnostics.Debug.WriteLine($"[ImportResultDialog] Creating multi-material entity with {_result.Submeshes.Count} submeshes");

                    // Create parent container entity
                    var parentEntity = new ECS.GameEntity(scene, _result.ModelName);
                    parentEntity.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);
                    scene.AddEntity(parentEntity);

                    // Create child entity for each submesh
                    for (int i = 0; i < _result.Submeshes.Count; i++)
                    {
                        var submesh = _result.Submeshes[i];
                        string childName = !string.IsNullOrEmpty(submesh.Name) ? submesh.Name : $"Part_{i}";
                        
                        var childEntity = new ECS.GameEntity(scene, childName);
                        
                        // Mark child as locked to parent (can't be moved individually)
                        childEntity.IsLockedToParent = true;
                        
                        // Use submesh-specific mesh path
                        string submeshPath = $"{_result.RelativePath}#submesh{i}";
                        var meshRenderer = new ECS.Components.Rendering.MeshRenderer(childEntity)
                        {
                            MeshPath = submeshPath,
                            MaterialHandle = submesh.MaterialId
                        };

                        // Bind the generated per-submesh .vmat (the single source of truth, mirroring
                        // ViewportDropHandler): the engine renders FROM it, later Material-Editor edits
                        // apply, and the assignment survives a restart. Without it these entities kept
                        // only the session-local MaterialHandle and reverted to the embedded materials.
                        string vmatRel = FindSubmeshVmat(projectPath, _result.RelativePath, i);
                        if (vmatRel != null)
                            meshRenderer.MaterialPath = vmatRel;

                        // Fallback only when no .vmat exists (older imports): persist the raw texture path.
                        if (vmatRel == null && !string.IsNullOrEmpty(submesh.TexturePath))
                        {
                            string relTexPath = submesh.TexturePath;
                            if (!string.IsNullOrEmpty(projectPath) && submesh.TexturePath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                            {
                                relTexPath = submesh.TexturePath.Substring(projectPath.Length)
                                    .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                            }
                            meshRenderer.TexturePath = relTexPath;
                        }

                        childEntity.AddComponent(meshRenderer);
                        childEntity.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);
                        parentEntity.AddChild(childEntity);

                        System.Diagnostics.Debug.WriteLine($"[ImportResultDialog] Created child '{childName}': mesh={submeshPath}, material={submesh.MaterialId}");
                    }

                    System.Diagnostics.Debug.WriteLine($"[ImportResultDialog] Multi-material entity created with {parentEntity.Children.Count} children");
                }
                else
                {
                    // Single mesh: Create single entity
                    var entity = new ECS.GameEntity(scene, _result.ModelName);
                    
                    string texturePath = null;
                    if (_result.Submeshes.Count > 0 && !string.IsNullOrEmpty(_result.Submeshes[0].TexturePath))
                    {
                        texturePath = _result.Submeshes[0].TexturePath;
                    }
                    else if (_result.ImportedTextures.Count > 0)
                    {
                        texturePath = _result.ImportedTextures.FirstOrDefault(t =>
                            t.ToLowerInvariant().Contains("col") ||
                            t.ToLowerInvariant().Contains("diffuse") ||
                            t.ToLowerInvariant().Contains("albedo")) ?? _result.ImportedTextures[0];
                    }

                    var meshRenderer = new ECS.Components.Rendering.MeshRenderer(entity)
                    {
                        MeshPath = _result.RelativePath,
                        MaterialHandle = _result.MaterialId
                    };

                    // Bind the model's sidecar .vmat (single-submesh imports write materials/submesh_0.vmat)
                    // so Material-Editor edits apply and persist across a restart — mirrors ViewportDropHandler.
                    string vmatRel = FindSubmeshVmat(projectPath, _result.RelativePath, 0);
                    if (vmatRel != null)
                        meshRenderer.MaterialPath = vmatRel;

                    // Fallback only when no .vmat exists (older imports): persist the raw texture path.
                    if (vmatRel == null && !string.IsNullOrEmpty(texturePath))
                    {
                        string relTexPath = texturePath;
                        if (!string.IsNullOrEmpty(projectPath) && texturePath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            relTexPath = texturePath.Substring(projectPath.Length)
                                .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                        }
                        meshRenderer.TexturePath = relTexPath;
                    }

                    entity.AddComponent(meshRenderer);
                    entity.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);
                    scene.AddEntity(entity);

                    System.Diagnostics.Debug.WriteLine($"[ImportResultDialog] Created single entity '{_result.ModelName}'");
                }
                
                AddToSceneRequested = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ImportResultDialog] EXCEPTION: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Error creating entity: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }


            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Project-relative path (forward slashes) of the importer-generated
        /// &lt;modelDir&gt;/materials/submesh_{index}.vmat, or null when none exists.
        /// </summary>
        private static string FindSubmeshVmat(string projectPath, string modelRelativePath, int index)
        {
            try
            {
                if (string.IsNullOrEmpty(modelRelativePath))
                    return null;

                string modelDir = Path.GetDirectoryName(modelRelativePath) ?? "";
                string vmatRel = Path.Combine(modelDir, "materials", $"submesh_{index}.vmat").Replace('\\', '/');
                if (File.Exists(Path.Combine(projectPath ?? "", vmatRel)))
                    return vmatRel;
            }
            catch { }
            return null;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            SaveTags(); // Save any selected tags even when just closing
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Shows the import result dialog.
        /// </summary>
        public static ImportResultDialog Show(Window owner, ModelImportResult result)
        {
            var dialog = new ImportResultDialog(result);
            if (owner != null)
            {
                dialog.Owner = owner;
            }
            dialog.ShowDialog();
            return dialog;
        }
    }
}
