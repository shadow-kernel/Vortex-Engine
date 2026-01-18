using System.Runtime.Serialization;

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
            set => SetProperty(ref _meshPath, value, nameof(MeshPath));
        }

        /// <summary>
        /// Pfad zur Material-Datei
        /// </summary>
        [DataMember(Name = "materialPath", Order = 11)]
        public string MaterialPath
        {
            get => _materialPath;
            set => SetProperty(ref _materialPath, value, nameof(MaterialPath));
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

        public MeshRenderer() : base() { }
        public MeshRenderer(GameEntity entity) : base(entity) { }
        public MeshRenderer(GameEntity entity, string meshPath) : base(entity)
        {
            MeshPath = meshPath;
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

        public override string DisplayName => "Sprite Renderer";
        public override string IconCode => "\uE8B9";
        public override string IconColor => "#C586C0";

        [DataMember(Name = "spritePath", Order = 10)]
        public string SpritePath
        {
            get => _spritePath;
            set => SetProperty(ref _spritePath, value, nameof(SpritePath));
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
    }
}
