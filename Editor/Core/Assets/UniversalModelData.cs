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
    #region Enums

    /// <summary>
    /// Supported texture slot types for PBR materials.
    /// </summary>
    public enum TextureMapType
    {
        Albedo,         // Diffuse/Base Color
        Normal,         // Normal Map
        Metallic,       // Metallic Map
        Roughness,      // Roughness Map
        AmbientOcclusion, // AO Map
        Emissive,       // Emissive/Emission Map
        Height,         // Height/Displacement Map
        Opacity,        // Alpha/Opacity Map
        Specular,       // Specular Map (legacy)
        MetallicRoughness, // Combined Metallic-Roughness (GLTF style)
        OcclusionRoughnessMetallic, // Combined ORM Map
        Custom          // User-defined slot
    }

    /// <summary>
    /// Supported 3D model formats.
    /// </summary>
    public enum ModelFormat
    {
        Unknown,
        OBJ,
        FBX,
        GLTF,
        GLB,
        DAE,
        Blend,
        ThreeDS,
        VMesh // Native format
    }

    #endregion

    #region Texture Classes

    /// <summary>
    /// Represents a single texture map with preview and metadata.
    /// </summary>
    public class TextureMapData : INotifyPropertyChanged
    {
        private string _filePath;
        private BitmapSource _preview;
        private bool _isLoading;

        public TextureMapType MapType { get; set; }
        public string CustomSlotName { get; set; } // For Custom type

        public string DisplayName => MapType == TextureMapType.Custom 
            ? (CustomSlotName ?? "Custom") 
            : MapType.ToString();

        public string FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    OnPropertyChanged(nameof(FilePath));
                    OnPropertyChanged(nameof(FileName));
                    OnPropertyChanged(nameof(IsAssigned));
                    OnPropertyChanged(nameof(StatusText));
                    LoadPreviewAsync();
                }
            }
        }

        public string FileName => string.IsNullOrEmpty(_filePath) ? "None" : Path.GetFileName(_filePath);
        
        public bool IsAssigned => !string.IsNullOrEmpty(_filePath);
        
        public bool FileExists => IsAssigned && File.Exists(_filePath);

        public string StatusText
        {
            get
            {
                if (!IsAssigned) return "Not assigned";
                if (!FileExists) return "File not found";
                return FileName;
            }
        }

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

        public long EngineTextureId { get; set; } = -1;

        private void LoadPreviewAsync()
        {
            if (!FileExists)
            {
                Preview = null;
                return;
            }

            IsLoading = true;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_filePath);
                bitmap.DecodePixelWidth = 80;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                Preview = bitmap;
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

        public void Clear()
        {
            FilePath = null;
            Preview = null;
            EngineTextureId = -1;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Represents a discovered texture file in the model's directory.
    /// </summary>
    public class DiscoveredTexture : INotifyPropertyChanged
    {
        private BitmapSource _preview;

        public string FilePath { get; set; }
        public string FileName => Path.GetFileName(FilePath);
        public long FileSize { get; set; }
        public TextureMapType DetectedType { get; set; } = TextureMapType.Custom;

        public string FileSizeText
        {
            get
            {
                if (FileSize >= 1024 * 1024) return $"{FileSize / (1024.0 * 1024.0):F1} MB";
                if (FileSize >= 1024) return $"{FileSize / 1024.0:F1} KB";
                return $"{FileSize} B";
            }
        }

        public BitmapSource Preview
        {
            get => _preview;
            set { _preview = value; OnPropertyChanged(nameof(Preview)); }
        }

        public void LoadPreview()
        {
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                Preview = null;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(FilePath);
                bitmap.DecodePixelWidth = 80;
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

        /// <summary>
        /// Auto-detect texture type from filename.
        /// </summary>
        public static TextureMapType DetectTypeFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return TextureMapType.Custom;
            
            var lower = fileName.ToLowerInvariant();
            var nameWithoutExt = Path.GetFileNameWithoutExtension(lower);

            // Check for combined maps first
            if (ContainsAny(lower, "_orm", "occlusionroughnessmetallic", "_arm"))
                return TextureMapType.OcclusionRoughnessMetallic;
            
            if (ContainsAny(lower, "metallicroughness", "_mr.", "_rm."))
                return TextureMapType.MetallicRoughness;

            // Normal map - check before albedo because "normal" contains "n"
            if (ContainsAny(lower, "normal", "_n.", "_nrm", "_nor", "normalmap", "_normal.", "_nmap", "nmap.", 
                "_norm.", "_norm_", "norm_"))
                return TextureMapType.Normal;

            // Roughness - check before albedo because some patterns overlap
            if (ContainsAny(lower, "roughness", "_r.", "_rough.", "_rgh", "_rough_", "rough.", "rough_"))
                return TextureMapType.Roughness;

            // Albedo/Diffuse
            if (ContainsAny(lower, "albedo", "diffuse", "basecolor", "_col.", "_color.", "_d.", "_diff", "_alb",
                "color.", "color_", "_col_", "_bc.", "_bc_", "_base."))
                return TextureMapType.Albedo;

            // Metallic
            if (ContainsAny(lower, "metallic", "_m.", "_met.", "metalness", "_metal", "metal.", "metal_"))
                return TextureMapType.Metallic;

            // AO
            if (ContainsAny(lower, "_ao.", "occlusion", "ambient", "ambientocclusion", "_ao_", "_ao"))
                return TextureMapType.AmbientOcclusion;

            // Emissive
            if (ContainsAny(lower, "emissive", "emission", "_e.", "_emit", "glow", "selfillum"))
                return TextureMapType.Emissive;

            // Height
            if (ContainsAny(lower, "height", "displacement", "_h.", "disp", "heightmap"))
                return TextureMapType.Height;

            // Opacity
            if (ContainsAny(lower, "opacity", "alpha", "transparency", "_a.", "_opacity", "mask"))
                return TextureMapType.Opacity;

            // Specular (legacy) - check after roughness
            if (ContainsAny(lower, "specular", "_s.", "_spec", "spec.", "spec_"))
                return TextureMapType.Specular;

            return TextureMapType.Custom;
        }

        private static bool ContainsAny(string source, params string[] values)
        {
            return values.Any(v => source.Contains(v));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    #endregion

    #region Material Classes

    /// <summary>
    /// Universal PBR material definition.
    /// Works with any 3D format and supports all standard PBR texture slots.
    /// </summary>
    public class UniversalMaterial : INotifyPropertyChanged
    {
        private string _name = "Material";
        private Color _baseColor = Colors.White;
        private float _metallic = 0f;
        private float _roughness = 0.5f;
        private float _normalStrength = 1f;
        private float _aoStrength = 1f;
        private float _emissiveStrength = 0f;
        private Color _emissiveColor = Colors.Black;
        private bool _twoSided = false;
        private bool _isExpanded = false;

        public int Index { get; set; }
        
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        #region PBR Properties

        public Color BaseColor
        {
            get => _baseColor;
            set { _baseColor = value; OnPropertyChanged(nameof(BaseColor)); OnPropertyChanged(nameof(BaseColorBrush)); }
        }
        
        public SolidColorBrush BaseColorBrush => new SolidColorBrush(BaseColor);

        public float Metallic
        {
            get => _metallic;
            set { _metallic = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(nameof(Metallic)); }
        }

        public float Roughness
        {
            get => _roughness;
            set { _roughness = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(nameof(Roughness)); }
        }

        public float NormalStrength
        {
            get => _normalStrength;
            set { _normalStrength = Math.Max(0, Math.Min(2, value)); OnPropertyChanged(nameof(NormalStrength)); }
        }

        public float AOStrength
        {
            get => _aoStrength;
            set { _aoStrength = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(nameof(AOStrength)); }
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

        #endregion

        #region Texture Maps

        public ObservableCollection<TextureMapData> TextureMaps { get; } = new ObservableCollection<TextureMapData>();

        /// <summary>
        /// Texture slots are now built DYNAMICALLY from the import — a material shows only the maps it actually
        /// has, never a fixed Albedo/Normal/Metallic/Roughness/AO/Emissive placeholder set. This is intentionally
        /// a NO-OP (kept so existing callers compile); the import calls SetTexture per found map, and the user
        /// adds extra slots via AddStandardSlot from the "Add Map" UI.
        /// </summary>
        public void InitializeStandardSlots() { /* dynamic slots — no static placeholders */ }

        /// <summary>The standard PBR map types, for the "Add Map" picker.</summary>
        public static readonly TextureMapType[] StandardMapTypes =
        {
            TextureMapType.Albedo, TextureMapType.Normal, TextureMapType.Metallic, TextureMapType.Roughness,
            TextureMapType.AmbientOcclusion, TextureMapType.Emissive, TextureMapType.Height, TextureMapType.Opacity,
            TextureMapType.MetallicRoughness, TextureMapType.OcclusionRoughnessMetallic
        };

        /// <summary>Adds an empty slot of the given type if the material doesn't already have one (for "Add Map").</summary>
        public TextureMapData AddStandardSlot(TextureMapType type)
        {
            var existing = GetTextureSlot(type);
            if (existing != null) return existing;
            var slot = new TextureMapData { MapType = type };
            TextureMaps.Add(slot);
            RefreshStats();
            return slot;
        }

        public TextureMapData GetTextureSlot(TextureMapType type)
        {
            return TextureMaps.FirstOrDefault(t => t.MapType == type);
        }

        public void SetTexture(TextureMapType type, string filePath)
        {
            var slot = GetTextureSlot(type);
            if (slot != null)
            {
                slot.FilePath = filePath;
            }
            else
            {
                TextureMaps.Add(new TextureMapData { MapType = type, FilePath = filePath });
            }
            RefreshStats();
        }

        public void AddCustomSlot(string name)
        {
            TextureMaps.Add(new TextureMapData 
            { 
                MapType = TextureMapType.Custom, 
                CustomSlotName = name 
            });
        }

        #endregion

        #region Engine IDs

        public long EngineMaterialId { get; set; } = -1;

        #endregion

        #region Stats

        public int AssignedTextureCount => TextureMaps.Count(t => t.IsAssigned);
        
        public string TextureSummary => AssignedTextureCount == 0 
            ? "No textures" 
            : $"{AssignedTextureCount} texture(s)";

        public void RefreshStats()
        {
            OnPropertyChanged(nameof(AssignedTextureCount));
            OnPropertyChanged(nameof(TextureSummary));
        }

        #endregion

        #region UI State

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
        }

        #endregion

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    #endregion

    #region Mesh/Submesh Classes

    /// <summary>
    /// Represents a single submesh within a model.
    /// </summary>
    public class SubmeshData : INotifyPropertyChanged
    {
        private string _name;
        private bool _isVisible = true;

        public int Index { get; set; }
        
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string DisplayName => string.IsNullOrEmpty(_name) ? $"Submesh {Index}" : _name;

        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public int IndexCount { get; set; }

        public string GeometryInfo => $"{VertexCount:N0} verts, {TriangleCount:N0} tris";

        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(nameof(IsVisible)); }
        }

        /// <summary>
        /// Index of the material used by this submesh.
        /// </summary>
        public int MaterialIndex { get; set; } = 0;

        /// <summary>
        /// Engine mesh ID after import.
        /// </summary>
        public long EngineMeshId { get; set; } = -1;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    #endregion

    #region Model Data

    /// <summary>
    /// Universal 3D model data container.
    /// Supports any format (OBJ, FBX, GLTF, DAE, etc.) with unified structure.
    /// </summary>
    public class UniversalModelData : INotifyPropertyChanged
    {
        private string _filePath;
        private bool _isLoaded;

        #region File Properties

        public string FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                OnPropertyChanged(nameof(FilePath));
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(Directory));
                OnPropertyChanged(nameof(Format));
                OnPropertyChanged(nameof(FormatName));
            }
        }

        public string FileName => Path.GetFileName(FilePath);
        public string FileNameWithoutExtension => Path.GetFileNameWithoutExtension(FilePath);
        public string Directory => Path.GetDirectoryName(FilePath);
        public string Extension => Path.GetExtension(FilePath)?.ToLowerInvariant() ?? "";

        public ModelFormat Format => DetectFormat(Extension);

        public string FormatName => Format switch
        {
            ModelFormat.OBJ => "Wavefront OBJ",
            ModelFormat.FBX => "Autodesk FBX",
            ModelFormat.GLTF => "glTF 2.0",
            ModelFormat.GLB => "glTF Binary",
            ModelFormat.DAE => "Collada",
            ModelFormat.Blend => "Blender",
            ModelFormat.ThreeDS => "3D Studio",
            ModelFormat.VMesh => "Vortex Mesh",
            _ => "Unknown Format"
        };

        public bool IsLoaded
        {
            get => _isLoaded;
            set { _isLoaded = value; OnPropertyChanged(nameof(IsLoaded)); }
        }

        #endregion

        #region Collections

        public ObservableCollection<SubmeshData> Submeshes { get; } = new ObservableCollection<SubmeshData>();
        public ObservableCollection<UniversalMaterial> Materials { get; } = new ObservableCollection<UniversalMaterial>();
        public ObservableCollection<DiscoveredTexture> DiscoveredTextures { get; } = new ObservableCollection<DiscoveredTexture>();

        #endregion

        #region Statistics

        public int TotalVertices => Submeshes.Sum(s => s.VertexCount);
        public int TotalTriangles => Submeshes.Sum(s => s.TriangleCount);
        public int SubmeshCount => Submeshes.Count;
        public int MaterialCount => Materials.Count;
        public int TextureCount => DiscoveredTextures.Count;

        public string StatsSummary => $"{SubmeshCount} submesh(es), {MaterialCount} material(s), {TextureCount} texture(s)";
        public string GeometrySummary => $"{TotalVertices:N0} vertices, {TotalTriangles:N0} triangles";

        public void RefreshStats()
        {
            OnPropertyChanged(nameof(TotalVertices));
            OnPropertyChanged(nameof(TotalTriangles));
            OnPropertyChanged(nameof(SubmeshCount));
            OnPropertyChanged(nameof(MaterialCount));
            OnPropertyChanged(nameof(TextureCount));
            OnPropertyChanged(nameof(StatsSummary));
            OnPropertyChanged(nameof(GeometrySummary));
        }

        #endregion

        #region Material Lookup

        /// <summary>
        /// Gets the material for a given submesh.
        /// </summary>
        public UniversalMaterial GetMaterialForSubmesh(SubmeshData submesh)
        {
            if (submesh == null) return null;
            if (submesh.MaterialIndex >= 0 && submesh.MaterialIndex < Materials.Count)
                return Materials[submesh.MaterialIndex];
            return Materials.FirstOrDefault();
        }

        /// <summary>
        /// Gets the material by name.
        /// </summary>
        public UniversalMaterial GetMaterialByName(string name)
        {
            return Materials.FirstOrDefault(m => 
                string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region Format Detection

        public static ModelFormat DetectFormat(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return ModelFormat.Unknown;
            
            return extension.ToLowerInvariant().TrimStart('.') switch
            {
                "obj" => ModelFormat.OBJ,
                "fbx" => ModelFormat.FBX,
                "gltf" => ModelFormat.GLTF,
                "glb" => ModelFormat.GLB,
                "dae" => ModelFormat.DAE,
                "blend" => ModelFormat.Blend,
                "3ds" => ModelFormat.ThreeDS,
                "vmesh" => ModelFormat.VMesh,
                _ => ModelFormat.Unknown
            };
        }

        public static bool IsSupportedFormat(string extension)
        {
            return DetectFormat(extension) != ModelFormat.Unknown;
        }

        #endregion

        #region Clear

        public void Clear()
        {
            Submeshes.Clear();
            Materials.Clear();
            DiscoveredTextures.Clear();
            IsLoaded = false;
            RefreshStats();
        }

        #endregion

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    #endregion
}
