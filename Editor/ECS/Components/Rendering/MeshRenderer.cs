using System.Runtime.Serialization;
using Editor.DllWrapper;
using Editor.Utilities;

namespace Editor.ECS.Components.Rendering
{
    /// <summary>
    /// MeshRenderer-Komponente für 3D-Mesh-Darstellung.
    /// </summary>
    [DataContract(Name = "MeshRenderer", Namespace = "")]
    public class MeshRenderer : Component
    {
        private string _meshPath;
        private string _materialPath;
        private bool _castShadows = true;
        private bool _receiveShadows = true;
        private int _renderLayer;
        
        // Material color properties
        private float _colorR = 0.7f;
        private float _colorG = 0.7f;
        private float _colorB = 0.7f;
        private float _colorA = 1.0f;

        [IgnoreDataMember]
        private long _meshHandle = ID.INVALID_ID;

        [IgnoreDataMember]
        private long _materialHandle = ID.INVALID_ID;

        public override string DisplayName => "Mesh Renderer";
        public override string IconCode => "\uE809";
        public override string IconColor => "#4EC9B0";

        /// <summary>
        /// Pfad zur Mesh-Datei
        /// </summary>
        [DataMember(Name = "meshPath", Order = 10)]
        public string MeshPath
        {
            get => _meshPath;
            set
            {
                if (SetProperty(ref _meshPath, value, nameof(MeshPath)))
                {
                    ReloadMeshHandle();
                }
            }
        }

        /// <summary>
        /// Pfad zur Material-Datei
        /// </summary>
        [DataMember(Name = "materialPath", Order = 11)]
        public string MaterialPath
        {
            get => _materialPath;
            set
            {
                if (SetProperty(ref _materialPath, value, nameof(MaterialPath)))
                {
                    ReloadMaterialHandle();
                }
            }
        }

        /// <summary>
        /// Ob das Mesh Schatten wirft
        /// </summary>
        [DataMember(Name = "castShadows", Order = 12)]
        public bool CastShadows
        {
            get => _castShadows;
            set => SetProperty(ref _castShadows, value, nameof(CastShadows));
        }

        /// <summary>
        /// Ob das Mesh Schatten empfängt
        /// </summary>
        [DataMember(Name = "receiveShadows", Order = 13)]
        public bool ReceiveShadows
        {
            get => _receiveShadows;
            set => SetProperty(ref _receiveShadows, value, nameof(ReceiveShadows));
        }

        /// <summary>
        /// Render-Layer für Culling
        /// </summary>
        [DataMember(Name = "renderLayer", Order = 14)]
        public int RenderLayer
        {
            get => _renderLayer;
            set => SetProperty(ref _renderLayer, value, nameof(RenderLayer));
        }

        /// <summary>
        /// Material base color - Red component (0-1)
        /// </summary>
        [DataMember(Name = "colorR", Order = 15)]
        public float ColorR
        {
            get => _colorR;
            set => SetProperty(ref _colorR, Clamp01(value), nameof(ColorR));
        }

        /// <summary>
        /// Material base color - Green component (0-1)
        /// </summary>
        [DataMember(Name = "colorG", Order = 16)]
        public float ColorG
        {
            get => _colorG;
            set => SetProperty(ref _colorG, Clamp01(value), nameof(ColorG));
        }

        /// <summary>
        /// Material base color - Blue component (0-1)
        /// </summary>
        [DataMember(Name = "colorB", Order = 17)]
        public float ColorB
        {
            get => _colorB;
            set => SetProperty(ref _colorB, Clamp01(value), nameof(ColorB));
        }

        /// <summary>
        /// Material base color - Alpha component (0-1)
        /// </summary>
        [DataMember(Name = "colorA", Order = 18)]
        public float ColorA
        {
            get => _colorA;
            set => SetProperty(ref _colorA, Clamp01(value), nameof(ColorA));
        }

        public MeshRenderer() : base() { }
        public MeshRenderer(GameEntity entity) : base(entity) { }
        public MeshRenderer(GameEntity entity, string meshPath) : base(entity)
        {
            MeshPath = meshPath;
        }

        private static float Clamp01(float value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        [OnDeserialized]
        internal void OnDeserializedMeshRenderer(StreamingContext context)
        {
            ReloadMeshHandle();
            ReloadMaterialHandle();
        }

        private void ReloadMeshHandle()
        {
            if (_meshHandle != ID.INVALID_ID)
            {
                VortexAPI.UnloadResourceHandle(_meshHandle);
                _meshHandle = ID.INVALID_ID;
            }

            if (!string.IsNullOrEmpty(_meshPath))
            {
                _meshHandle = VortexAPI.LoadMeshResource(_meshPath);
            }
        }

        private void ReloadMaterialHandle()
        {
            if (_materialHandle != ID.INVALID_ID)
            {
                VortexAPI.UnloadResourceHandle(_materialHandle);
                _materialHandle = ID.INVALID_ID;
            }

            if (!string.IsNullOrEmpty(_materialPath))
            {
                _materialHandle = VortexAPI.LoadMaterialResource(_materialPath);
            }
        }
    }

    /// <summary>
    /// SpriteRenderer-Komponente für 2D-Sprite-Darstellung.
    /// </summary>
    [DataContract(Name = "SpriteRenderer", Namespace = "")]
    public class SpriteRenderer : Component
    {
        private string _spritePath;
        private float _colorR = 1f;
        private float _colorG = 1f;
        private float _colorB = 1f;
        private float _colorA = 1f;
        private int _sortingOrder;
        private bool _flipX;
        private bool _flipY;

        [IgnoreDataMember]
        private long _spriteHandle = ID.INVALID_ID;

        public override string DisplayName => "Sprite Renderer";
        public override string IconCode => "\uE8B9";
        public override string IconColor => "#C586C0";

        [DataMember(Name = "spritePath", Order = 10)]
        public string SpritePath
        {
            get => _spritePath;
            set
            {
                if (SetProperty(ref _spritePath, value, nameof(SpritePath)))
                {
                    ReloadSpriteHandle();
                }
            }
        }

        [DataMember(Name = "colorR", Order = 11)]
        public float ColorR
        {
            get => _colorR;
            set => SetProperty(ref _colorR, value, nameof(ColorR));
        }

        [DataMember(Name = "colorG", Order = 12)]
        public float ColorG
        {
            get => _colorG;
            set => SetProperty(ref _colorG, value, nameof(ColorG));
        }

        [DataMember(Name = "colorB", Order = 13)]
        public float ColorB
        {
            get => _colorB;
            set => SetProperty(ref _colorB, value, nameof(ColorB));
        }

        [DataMember(Name = "colorA", Order = 14)]
        public float ColorA
        {
            get => _colorA;
            set => SetProperty(ref _colorA, value, nameof(ColorA));
        }

        [DataMember(Name = "sortingOrder", Order = 15)]
        public int SortingOrder
        {
            get => _sortingOrder;
            set => SetProperty(ref _sortingOrder, value, nameof(SortingOrder));
        }

        [DataMember(Name = "flipX", Order = 16)]
        public bool FlipX
        {
            get => _flipX;
            set => SetProperty(ref _flipX, value, nameof(FlipX));
        }

        [DataMember(Name = "flipY", Order = 17)]
        public bool FlipY
        {
            get => _flipY;
            set => SetProperty(ref _flipY, value, nameof(FlipY));
        }

        public SpriteRenderer() : base() { }
        public SpriteRenderer(GameEntity entity) : base(entity) { }

        [OnDeserialized]
        internal void OnDeserializedSpriteRenderer(StreamingContext context)
        {
            ReloadSpriteHandle();
        }

        private void ReloadSpriteHandle()
        {
            if (_spriteHandle != ID.INVALID_ID)
            {
                VortexAPI.UnloadResourceHandle(_spriteHandle);
                _spriteHandle = ID.INVALID_ID;
            }

            if (!string.IsNullOrEmpty(_spritePath))
            {
                _spriteHandle = VortexAPI.LoadTextureResource(_spritePath);
            }
        }
    }
}
