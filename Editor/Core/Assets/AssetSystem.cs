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
    /// Strongly typed asset identifiers used across model/material/mesh/texture assets.
    /// </summary>
    public sealed class AssetId : IEquatable<AssetId>
    {
        public Guid Guid { get; }

        public AssetId()
        {
            Guid = Guid.NewGuid();
        }

        public AssetId(Guid guid)
        {
            Guid = guid;
        }

        public override string ToString() => Guid.ToString("N");
        public bool Equals(AssetId other) => other != null && Guid.Equals(other.Guid);
        public override bool Equals(object obj) => obj is AssetId other && Equals(other);
        public override int GetHashCode() => Guid.GetHashCode();
        public static implicit operator Guid(AssetId id) => id.Guid;
    }

    /// <summary>
    /// Base information common to all asset types.
    /// </summary>
    public abstract class AssetDescriptor : INotifyPropertyChanged
    {
        private string _name;
        private string _sourcePath;
        private string _assetPath;

        public AssetId Id { get; } = new AssetId();

        /// <summary>Display name.</summary>
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        /// <summary>Absolute source path at import time.</summary>
        public string SourcePath
        {
            get => _sourcePath;
            set { _sourcePath = value; OnPropertyChanged(nameof(SourcePath)); }
        }

        /// <summary>Path inside the project (e.g. Assets/Models/Car/Car.fbx).</summary>
        public string AssetPath
        {
            get => _assetPath;
            set { _assetPath = value; OnPropertyChanged(nameof(AssetPath)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    #region Texture

    public enum ColorSpace
    {
        Linear,
        SRgb
    }

    public class TextureAsset : AssetDescriptor
    {
        private BitmapSource _preview;
        private bool _isLoading;

        public TextureMapType MapType { get; set; } = TextureMapType.Custom;
        public ColorSpace ColorSpace { get; set; } = ColorSpace.SRgb;
        public long FileSize { get; set; } = 0;
        public long EngineTextureId { get; set; } = -1;

        public string FileName => string.IsNullOrEmpty(AssetPath) ? "" : Path.GetFileName(AssetPath);
        public bool FileExists => !string.IsNullOrEmpty(AssetPath) && File.Exists(AssetPath);

        public bool IsLoading
        {
            get => _isLoading;
            private set { _isLoading = value; OnPropertyChanged(nameof(IsLoading)); }
        }

        public BitmapSource Preview
        {
            get => _preview;
            private set { _preview = value; OnPropertyChanged(nameof(Preview)); }
        }

        public void LoadPreview()
        {
            if (!FileExists)
            {
                Preview = null;
                return;
            }

            IsLoading = true;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(AssetPath);
                bmp.DecodePixelWidth = 96;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Preview = bmp;
            }
            catch
            {
                Preview = null;
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    #endregion

    #region Material

    public class MaterialTextureSlot : INotifyPropertyChanged
    {
        private string _boundTexturePath;
        private long _engineTextureId = -1;
        private BitmapSource _preview;

        public TextureMapType SlotType { get; set; }
        public string DisplayName { get; set; }

        public string BoundTexturePath
        {
            get => _boundTexturePath;
            set
            {
                if (_boundTexturePath == value) return;
                _boundTexturePath = value;
                OnPropertyChanged(nameof(BoundTexturePath));
                OnPropertyChanged(nameof(IsAssigned));
                OnPropertyChanged(nameof(BoundTextureFileName));
                LoadPreview();
            }
        }

        public string BoundTextureFileName => string.IsNullOrEmpty(BoundTexturePath) ? "None" : Path.GetFileName(BoundTexturePath);
        public bool IsAssigned => !string.IsNullOrEmpty(BoundTexturePath) && File.Exists(BoundTexturePath);

        public long EngineTextureId
        {
            get => _engineTextureId;
            set { _engineTextureId = value; OnPropertyChanged(nameof(EngineTextureId)); }
        }

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
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(BoundTexturePath);
                bmp.DecodePixelWidth = 80;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Preview = bmp;
            }
            catch
            {
                Preview = null;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class MaterialAsset : AssetDescriptor
    {
        private Color _baseColor = Colors.White;
        private float _metallic = 0f;
        private float _roughness = 0.5f;
        private float _normalStrength = 1f;
        private float _aoStrength = 1f;
        private float _emissiveStrength = 0f;
        private Color _emissiveColor = Colors.Black;
        private bool _twoSided;

        public ObservableCollection<MaterialTextureSlot> TextureSlots { get; } = new ObservableCollection<MaterialTextureSlot>();
        public long EngineMaterialId { get; set; } = -1;

        public Color BaseColor
        {
            get => _baseColor;
            set { _baseColor = value; OnPropertyChanged(nameof(BaseColor)); OnPropertyChanged(nameof(BaseColorBrush)); }
        }

        public SolidColorBrush BaseColorBrush => new SolidColorBrush(BaseColor);

        public float Metallic
        {
            get => _metallic;
            set { _metallic = Clamp01(value); OnPropertyChanged(nameof(Metallic)); }
        }

        public float Roughness
        {
            get => _roughness;
            set { _roughness = Clamp01(value); OnPropertyChanged(nameof(Roughness)); }
        }

        public float NormalStrength
        {
            get => _normalStrength;
            set { _normalStrength = Math.Max(0, Math.Min(2, value)); OnPropertyChanged(nameof(NormalStrength)); }
        }

        public float AOStrength
        {
            get => _aoStrength;
            set { _aoStrength = Clamp01(value); OnPropertyChanged(nameof(AOStrength)); }
        }

        public float EmissiveStrength
        {
            get => _emissiveStrength;
            set { _emissiveStrength = Math.Max(0, value); OnPropertyChanged(nameof(EmissiveStrength)); }
        }

        public Color EmissiveColor
        {
            get => _emissiveColor;
            set { _emissiveColor = value; OnPropertyChanged(nameof(EmissiveColor)); }
        }

        public bool TwoSided
        {
            get => _twoSided;
            set { _twoSided = value; OnPropertyChanged(nameof(TwoSided)); }
        }

        public void InitializeStandardSlots()
        {
            TextureSlots.Clear();
            TextureSlots.Add(new MaterialTextureSlot { SlotType = TextureMapType.Albedo, DisplayName = "Albedo" });
            TextureSlots.Add(new MaterialTextureSlot { SlotType = TextureMapType.Normal, DisplayName = "Normal" });
            TextureSlots.Add(new MaterialTextureSlot { SlotType = TextureMapType.Metallic, DisplayName = "Metallic" });
            TextureSlots.Add(new MaterialTextureSlot { SlotType = TextureMapType.Roughness, DisplayName = "Roughness" });
            TextureSlots.Add(new MaterialTextureSlot { SlotType = TextureMapType.AmbientOcclusion, DisplayName = "AO" });
            TextureSlots.Add(new MaterialTextureSlot { SlotType = TextureMapType.Emissive, DisplayName = "Emissive" });
            TextureSlots.Add(new MaterialTextureSlot { SlotType = TextureMapType.Opacity, DisplayName = "Opacity" });
            TextureSlots.Add(new MaterialTextureSlot { SlotType = TextureMapType.Height, DisplayName = "Height" });
        }

        public MaterialTextureSlot GetSlot(TextureMapType type) => TextureSlots.FirstOrDefault(s => s.SlotType == type);

        private static float Clamp01(float v) => Math.Max(0, Math.Min(1, v));
    }

    #endregion

    #region Mesh/Model

    public class MeshAsset : AssetDescriptor
    {
        public ObservableCollection<SubmeshAsset> Submeshes { get; } = new ObservableCollection<SubmeshAsset>();
        public long EngineMeshId { get; set; } = -1;
    }

    public class SubmeshAsset : INotifyPropertyChanged
    {
        private string _name;
        private bool _visible = true;

        public int Index { get; set; }
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string DisplayName => string.IsNullOrEmpty(_name) ? $"Submesh {Index}" : _name;
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public long EngineMeshId { get; set; } = -1;
        public AssetId MaterialId { get; set; }

        public bool IsVisible
        {
            get => _visible;
            set { _visible = value; OnPropertyChanged(nameof(IsVisible)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ModelAsset : AssetDescriptor
    {
        public ObservableCollection<MeshAsset> Meshes { get; } = new ObservableCollection<MeshAsset>();
        public ObservableCollection<MaterialAsset> Materials { get; } = new ObservableCollection<MaterialAsset>();
        public ObservableCollection<TextureAsset> Textures { get; } = new ObservableCollection<TextureAsset>();

        public int TotalSubmeshCount => Meshes.Sum(m => m.Submeshes.Count);
        public int TotalMaterialCount => Materials.Count;
        public int TotalTextureCount => Textures.Count;
    }

    #endregion

    #region Preview

    /// <summary>
    /// Central place to request previews for assets. Texture previews are local bitmaps;
    /// model/material previews can be routed through a dedicated viewport if available.
    /// </summary>
    public class AssetPreviewService
    {
        private static AssetPreviewService _instance;
        public static AssetPreviewService Instance => _instance ?? (_instance = new AssetPreviewService());

        private AssetPreviewService() { }

        public void EnsurePreview(TextureAsset texture)
        {
            if (texture == null) return;
            if (texture.Preview == null)
                texture.LoadPreview();
        }

        public void EnsurePreview(MaterialAsset material)
        {
            if (material == null) return;
            foreach (var slot in material.TextureSlots)
            {
                if (slot.Preview == null && slot.IsAssigned)
                    slot.GetType(); // force property access; preview loads on set
            }
        }

        public void EnsurePreview(ModelAsset model)
        {
            if (model == null) return;
            foreach (var tex in model.Textures)
            {
                EnsurePreview(tex);
            }
        }
    }

    #endregion
}
