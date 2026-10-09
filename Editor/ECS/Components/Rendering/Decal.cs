using System.Runtime.Serialization;

namespace Editor.ECS.Components.Rendering
{
    public enum DecalBlendMode { Lit = 0, Multiply = 1, Additive = 2 }

    /// <summary>
    /// A projected decal (#120): a box on the entity (its transform × <see cref="Size"/>) that projects a material along the
    /// entity's local +Y onto whatever opaque geometry lies inside it — blood, bullet holes, grime, posters — without new
    /// geometry. Lit like a surface, multiplied into it or added; fades on surfaces turning away and with distance.
    /// Runtime decals come from <c>Decal.Spawn</c> in scripts.
    /// </summary>
    [DataContract(Name = "Decal", Namespace = "")]
    public class Decal : Component
    {
        private string _materialPath;
        private Vector3 _size = new Vector3(1f, 0.25f, 1f);
        private float _colorR = 1f, _colorG = 1f, _colorB = 1f;
        private float _opacity = 1f;
        private int _blend;
        private float _angleFade = 0.5f;
        private float _fadeDistance;
        private int _sortOrder;

        public Decal() : base() { }
        public Decal(GameEntity entity) : base(entity) { }

        public override string DisplayName => "Decal";
        public override string IconCode => "";
        public override string IconColor => "#D070C0";

        /// <summary>The .vmat whose albedo texture and base colour are projected (project-relative). Empty = the tint alone.</summary>
        [DataMember(Name = "materialPath", Order = 10)]
        public string MaterialPath
        {
            get => _materialPath;
            set => SetProperty(ref _materialPath, value, nameof(MaterialPath));
        }

        /// <summary>Box extents in metres before the transform: X and Z across the surface, Y the projection depth.</summary>
        [DataMember(Name = "size", Order = 11)]
        public Vector3 Size
        {
            get => _size;
            set => SetProperty(ref _size, value, nameof(Size));
        }

        [DataMember(Name = "colorR", Order = 12)]
        public float ColorR { get => _colorR; set => SetProperty(ref _colorR, value, nameof(ColorR)); }
        [DataMember(Name = "colorG", Order = 13)]
        public float ColorG { get => _colorG; set => SetProperty(ref _colorG, value, nameof(ColorG)); }
        [DataMember(Name = "colorB", Order = 14)]
        public float ColorB { get => _colorB; set => SetProperty(ref _colorB, value, nameof(ColorB)); }

        [DataMember(Name = "opacity", Order = 15)]
        public float Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, value < 0f ? 0f : (value > 1f ? 1f : value), nameof(Opacity));
        }

        /// <summary>0 lit (shaded like a surface), 1 multiply (darkens what is under it), 2 additive (glows).</summary>
        [DataMember(Name = "blend", Order = 16)]
        public int Blend
        {
            get => _blend;
            set => SetProperty(ref _blend, value < 0 ? 0 : (value > 2 ? 2 : value), nameof(Blend));
        }

        /// <summary>Facing (surface normal · projection axis) at which the decal is fully opaque; it fades towards grazing
        /// surfaces below that. 0 = hard edge, no fade.</summary>
        [DataMember(Name = "angleFade", Order = 17)]
        public float AngleFade
        {
            get => _angleFade;
            set => SetProperty(ref _angleFade, value < 0f ? 0f : (value > 1f ? 1f : value), nameof(AngleFade));
        }

        /// <summary>Fully faded at this view distance in metres (0 = never).</summary>
        [DataMember(Name = "fadeDistance", Order = 18)]
        public float FadeDistance
        {
            get => _fadeDistance;
            set => SetProperty(ref _fadeDistance, value < 0f ? 0f : value, nameof(FadeDistance));
        }

        /// <summary>Higher sort orders draw on top of lower ones.</summary>
        [DataMember(Name = "sortOrder", Order = 19)]
        public int SortOrder
        {
            get => _sortOrder;
            set => SetProperty(ref _sortOrder, value, nameof(SortOrder));
        }
    }
}
