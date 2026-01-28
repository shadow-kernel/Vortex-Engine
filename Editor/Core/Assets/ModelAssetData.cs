using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Universal model asset data structure.
    /// Works with any 3D format (OBJ, FBX, GLTF, etc.)
    /// </summary>
    public class ModelAssetData : INotifyPropertyChanged
    {
        public string FilePath { get; set; }
        public string FileName => Path.GetFileName(FilePath);
        public string Directory => Path.GetDirectoryName(FilePath);
        public string Format => Path.GetExtension(FilePath)?.ToUpperInvariant().TrimStart('.');
        
        public ObservableCollection<MaterialData> Materials { get; } = new ObservableCollection<MaterialData>();
        public ObservableCollection<MeshData> Meshes { get; } = new ObservableCollection<MeshData>();
        public ObservableCollection<TextureData> AvailableTextures { get; } = new ObservableCollection<TextureData>();
        
        public int TotalVertices => Meshes.Sum(m => m.VertexCount);
        public int TotalTriangles => Meshes.Sum(m => m.TriangleCount);
        
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        
        public void RefreshStats()
        {
            OnPropertyChanged(nameof(TotalVertices));
            OnPropertyChanged(nameof(TotalTriangles));
        }
    }

    /// <summary>
    /// Represents a single mesh/submesh in a model.
    /// </summary>
    public class MeshData : INotifyPropertyChanged
    {
        private string _name;
        private string _materialName;
        private bool _isVisible = true;
        
        public int Index { get; set; }
        public string Name { get => _name; set { _name = value; OnPropertyChanged(nameof(Name)); } }
        public string MaterialName { get => _materialName; set { _materialName = value; OnPropertyChanged(nameof(MaterialName)); } }
        
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        
        public bool IsVisible { get => _isVisible; set { _isVisible = value; OnPropertyChanged(nameof(IsVisible)); } }
        
        // Engine IDs (set after import)
        public long EngineId { get; set; } = -1;
        
        public string Info => $"{VertexCount:N0} verts, {TriangleCount:N0} tris";
        
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Represents a material with PBR properties and texture slots.
    /// </summary>
    public class MaterialData : INotifyPropertyChanged
    {
        private string _name;
        private Color _baseColor = Colors.White;
        private float _metallic = 0f;
        private float _roughness = 0.5f;
        private float _normalStrength = 1f;
        
        public int Index { get; set; }
        public string Name { get => _name; set { _name = value; OnPropertyChanged(nameof(Name)); } }
        
        // PBR Properties
        public Color BaseColor { get => _baseColor; set { _baseColor = value; OnPropertyChanged(nameof(BaseColor)); OnPropertyChanged(nameof(BaseColorBrush)); } }
        public SolidColorBrush BaseColorBrush => new SolidColorBrush(BaseColor);
        public float Metallic { get => _metallic; set { _metallic = value; OnPropertyChanged(nameof(Metallic)); } }
        public float Roughness { get => _roughness; set { _roughness = value; OnPropertyChanged(nameof(Roughness)); } }
        public float NormalStrength { get => _normalStrength; set { _normalStrength = value; OnPropertyChanged(nameof(NormalStrength)); } }
        
        // Texture Slots
        public ObservableCollection<TextureSlotData> TextureSlots { get; } = new ObservableCollection<TextureSlotData>();
        
        // Engine IDs
        public long EngineId { get; set; } = -1;
        
        public int AssignedTextureCount => TextureSlots.Count(t => t.IsAssigned);
        public string TextureInfo => $"{AssignedTextureCount} texture(s)";
        
        public MaterialData()
        {
            // Initialize standard PBR slots
            TextureSlots.Add(new TextureSlotData { SlotType = TextureSlotType.Albedo, DisplayName = "Albedo" });
            TextureSlots.Add(new TextureSlotData { SlotType = TextureSlotType.Normal, DisplayName = "Normal" });
            TextureSlots.Add(new TextureSlotData { SlotType = TextureSlotType.Metallic, DisplayName = "Metallic" });
            TextureSlots.Add(new TextureSlotData { SlotType = TextureSlotType.Roughness, DisplayName = "Roughness" });
            TextureSlots.Add(new TextureSlotData { SlotType = TextureSlotType.AO, DisplayName = "AO" });
        }
        
        public TextureSlotData GetSlot(TextureSlotType type) => TextureSlots.FirstOrDefault(s => s.SlotType == type);
        
        public void RefreshStats()
        {
            OnPropertyChanged(nameof(AssignedTextureCount));
            OnPropertyChanged(nameof(TextureInfo));
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Types of texture slots in a PBR material.
    /// </summary>
    public enum TextureSlotType
    {
        Albedo,
        Normal,
        Metallic,
        Roughness,
        AO,
        Emissive,
        Height,
        Opacity,
        Custom
    }

    /// <summary>
    /// Represents a texture slot in a material.
    /// </summary>
    public class TextureSlotData : INotifyPropertyChanged
    {
        private string _texturePath;
        private BitmapSource _preview;
        
        public TextureSlotType SlotType { get; set; }
        public string DisplayName { get; set; }
        
        public string TexturePath
        {
            get => _texturePath;
            set
            {
                _texturePath = value;
                OnPropertyChanged(nameof(TexturePath));
                OnPropertyChanged(nameof(TextureFileName));
                OnPropertyChanged(nameof(IsAssigned));
                LoadPreview();
            }
        }
        
        public string TextureFileName => string.IsNullOrEmpty(_texturePath) ? "None" : Path.GetFileName(_texturePath);
        public bool IsAssigned => !string.IsNullOrEmpty(_texturePath) && File.Exists(_texturePath);
        
        public BitmapSource Preview
        {
            get => _preview;
            private set { _preview = value; OnPropertyChanged(nameof(Preview)); }
        }
        
        private void LoadPreview()
        {
            if (!IsAssigned)
            {
                Preview = null;
                return;
            }
            
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_texturePath);
                bitmap.DecodePixelWidth = 64;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                Preview = bitmap;
            }
            catch
            {
                Preview = null;
            }
        }
        
        public void Clear()
        {
            TexturePath = null;
            Preview = null;
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Represents a texture file found in the model's directory.
    /// </summary>
    public class TextureData : INotifyPropertyChanged
    {
        private BitmapSource _preview;
        
        public string FilePath { get; set; }
        public string FileName => Path.GetFileName(FilePath);
        public long FileSize { get; set; }
        public string FileSizeText => FormatFileSize(FileSize);
        
        public BitmapSource Preview
        {
            get => _preview;
            set { _preview = value; OnPropertyChanged(nameof(Preview)); }
        }
        
        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
