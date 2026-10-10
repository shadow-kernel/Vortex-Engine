using System.Runtime.Serialization;

namespace Editor.ECS.Components.Rendering
{
    /// <summary>
    /// A body of still water (#200): a lake, pond or pool whose surface lies at the entity's height and covers a square
    /// of <see cref="Size"/> metres around the entity. The surface mesh follows the terrain underneath — every vertex knows
    /// how deep the ground is below it — so the water shader fades out at the shore, darkens with depth, foams along the
    /// bank, reflects the sky with Fresnel, catches the sun and takes the sun's shadows. Scripts read it through
    /// <c>Water.Height</c> / <c>Water.IsUnderwater</c>.
    /// </summary>
    [DataContract(Name = "Water", Namespace = "")]
    public class Water : Component
    {
        private float _size = 64f;
        private float _cellSize = 1f;
        private float _deepR = 0.015f, _deepG = 0.10f, _deepB = 0.12f;
        private float _shallowR = 0.16f, _shallowG = 0.45f, _shallowB = 0.40f;
        private float _absorption = 3.5f;
        private float _reflection = 0.85f;
        private float _roughness = 0.08f;
        private float _waveScale = 6f;
        private float _waveSpeed = 1f;
        private float _waveHeight = 0.35f;
        private float _foamWidth = 0.9f;
        private float _defaultDepth = 4f;

        public Water() : base() { }
        public Water(GameEntity entity) : base(entity) { }

        public override string DisplayName => "Water";
        public override string IconCode => "";
        public override string IconColor => "#29B6F6";

        /// <summary>Edge length (m) of the square surface centred on the entity.</summary>
        [DataMember(Name = "size", Order = 10)]
        public float Size { get => _size; set => SetProperty(ref _size, value < 1f ? 1f : (value > 8192f ? 8192f : value), nameof(Size)); }

        /// <summary>Metres between surface vertices (the shore fade follows the terrain this finely).</summary>
        [DataMember(Name = "cellSize", Order = 11)]
        public float CellSize { get => _cellSize; set => SetProperty(ref _cellSize, value < 0.25f ? 0.25f : (value > 64f ? 64f : value), nameof(CellSize)); }

        /// <summary>Colour of deep water (linear).</summary>
        [DataMember(Name = "deepR", Order = 12)] public float DeepR { get => _deepR; set => SetProperty(ref _deepR, value, nameof(DeepR)); }
        [DataMember(Name = "deepG", Order = 13)] public float DeepG { get => _deepG; set => SetProperty(ref _deepG, value, nameof(DeepG)); }
        [DataMember(Name = "deepB", Order = 14)] public float DeepB { get => _deepB; set => SetProperty(ref _deepB, value, nameof(DeepB)); }

        /// <summary>Colour of shallow water over the bank (linear).</summary>
        [DataMember(Name = "shallowR", Order = 15)] public float ShallowR { get => _shallowR; set => SetProperty(ref _shallowR, value, nameof(ShallowR)); }
        [DataMember(Name = "shallowG", Order = 16)] public float ShallowG { get => _shallowG; set => SetProperty(ref _shallowG, value, nameof(ShallowG)); }
        [DataMember(Name = "shallowB", Order = 17)] public float ShallowB { get => _shallowB; set => SetProperty(ref _shallowB, value, nameof(ShallowB)); }

        /// <summary>Depth (m) at which the water has turned to its deep colour.</summary>
        [DataMember(Name = "absorption", Order = 18)]
        public float Absorption { get => _absorption; set => SetProperty(ref _absorption, value < 0.1f ? 0.1f : value, nameof(Absorption)); }

        /// <summary>0..1: how much of the sky the surface reflects at grazing angles.</summary>
        [DataMember(Name = "reflection", Order = 19)]
        public float Reflection { get => _reflection; set => SetProperty(ref _reflection, value < 0f ? 0f : (value > 1f ? 1f : value), nameof(Reflection)); }

        /// <summary>Surface roughness: 0.02 glassy, 0.3 choppy — the size of the sun's glitter.</summary>
        [DataMember(Name = "roughness", Order = 20)]
        public float Roughness { get => _roughness; set => SetProperty(ref _roughness, value < 0.01f ? 0.01f : (value > 1f ? 1f : value), nameof(Roughness)); }

        /// <summary>Wavelength (m) of the ripples.</summary>
        [DataMember(Name = "waveScale", Order = 21)]
        public float WaveScale { get => _waveScale; set => SetProperty(ref _waveScale, value < 0.1f ? 0.1f : value, nameof(WaveScale)); }

        /// <summary>Ripple speed multiplier.</summary>
        [DataMember(Name = "waveSpeed", Order = 22)]
        public float WaveSpeed { get => _waveSpeed; set => SetProperty(ref _waveSpeed, value < 0f ? 0f : value, nameof(WaveSpeed)); }

        /// <summary>Ripple strength (normal tilt): 0 a mirror, 1 a windy lake.</summary>
        [DataMember(Name = "waveHeight", Order = 23)]
        public float WaveHeight { get => _waveHeight; set => SetProperty(ref _waveHeight, value < 0f ? 0f : (value > 4f ? 4f : value), nameof(WaveHeight)); }

        /// <summary>Width (m) of the foam band along the bank (0 = none).</summary>
        [DataMember(Name = "foamWidth", Order = 24)]
        public float FoamWidth { get => _foamWidth; set => SetProperty(ref _foamWidth, value < 0f ? 0f : value, nameof(FoamWidth)); }

        /// <summary>Depth (m) assumed where no terrain lies under the surface (a pool over a mesh floor).</summary>
        [DataMember(Name = "defaultDepth", Order = 25)]
        public float DefaultDepth { get => _defaultDepth; set => SetProperty(ref _defaultDepth, value < 0f ? 0f : value, nameof(DefaultDepth)); }
    }
}
