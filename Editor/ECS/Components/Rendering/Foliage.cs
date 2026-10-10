using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Editor.ECS.Components.Rendering
{
    /// <summary>One kind of painted vegetation or prop (a tree, a bush, a grass clump, a rock): the model, how it is
    /// placed, how far it draws, whether it collides and how it sways. Instances are stored in the Foliage component's
    /// <c>.vfoliage</c> data, not as entities.</summary>
    [DataContract(Name = "FoliageType", Namespace = "")]
    public class FoliageType
    {
        [DataMember(Name = "name", Order = 0)] public string Name { get; set; } = "Type";
        /// <summary>The model (.glb / .gltf / .fbx / .vmesh) or a primitive ("Primitive:Sphere"), project-relative.</summary>
        [DataMember(Name = "meshPath", Order = 1)] public string MeshPath { get; set; }
        /// <summary>Random uniform scale range per instance.</summary>
        [DataMember(Name = "minScale", Order = 2)] public float MinScale { get; set; } = 0.8f;
        [DataMember(Name = "maxScale", Order = 3)] public float MaxScale { get; set; } = 1.25f;
        /// <summary>Tilt the instance onto the surface normal (grass, ground cover) instead of standing it upright (trees).</summary>
        [DataMember(Name = "alignToNormal", Order = 4)] public bool AlignToNormal { get; set; }
        /// <summary>Random tilt in degrees around the up axis's perpendiculars (a little lean for naturalness).</summary>
        [DataMember(Name = "maxTilt", Order = 5)] public float MaxTilt { get; set; } = 4f;
        /// <summary>Instances per square metre at full brush strength.</summary>
        [DataMember(Name = "density", Order = 6)] public float Density { get; set; } = 0.15f;
        /// <summary>Minimum distance (m) between two instances of this type.</summary>
        [DataMember(Name = "minSpacing", Order = 7)] public float MinSpacing { get; set; } = 1.5f;
        /// <summary>Steepest surface (degrees from flat) the type grows on.</summary>
        [DataMember(Name = "maxSlope", Order = 8)] public float MaxSlope { get; set; } = 45f;
        /// <summary>Instances beyond this camera distance (m) are not drawn.</summary>
        [DataMember(Name = "cullDistance", Order = 9)] public float CullDistance { get; set; } = 180f;
        /// <summary>Beyond this distance (m) only every second instance draws, beyond twice it every fourth (grass, ground cover); 0 = never thin.</summary>
        [DataMember(Name = "thinDistance", Order = 10)] public float ThinDistance { get; set; }
        /// <summary>0 none, 1 a vertical capsule (trunks), 2 a box of the model's bounds.</summary>
        [DataMember(Name = "collision", Order = 11)] public int Collision { get; set; }
        [DataMember(Name = "collisionRadius", Order = 12)] public float CollisionRadius { get; set; } = 0.35f;
        [DataMember(Name = "collisionHeight", Order = 13)] public float CollisionHeight { get; set; } = 5f;
        /// <summary>Sway amplitude at the top of the model in metres (0 = rigid); scaled by the Foliage component's wind.</summary>
        [DataMember(Name = "windStrength", Order = 14)] public float WindStrength { get; set; } = 0.15f;
        /// <summary>Sway speed multiplier.</summary>
        [DataMember(Name = "windSpeed", Order = 15)] public float WindSpeed { get; set; } = 1f;
        /// <summary>Height (m) of the model's sway reference: the top of the model moves by WindStrength, the roots stay.</summary>
        [DataMember(Name = "windHeight", Order = 16)] public float WindHeight { get; set; } = 6f;
        /// <summary>Alpha-tested leaves and blades: the model's materials cut out below 50 % alpha and draw both sides.</summary>
        [DataMember(Name = "cutout", Order = 17)] public bool Cutout { get; set; } = true;
        /// <summary>Relative probability when a brush paints several types at once.</summary>
        [DataMember(Name = "weight", Order = 18)] public float Weight { get; set; } = 1f;
        /// <summary>Sink the instance into the ground by this much (m) so roots never float.</summary>
        [DataMember(Name = "sink", Order = 19)] public float Sink { get; set; } = 0.05f;

        public FoliageType Clone() => (FoliageType)MemberwiseClone();
    }

    /// <summary>
    /// Painted vegetation and props (#125): thousands of instances of a few models (trees, bushes, grass, rocks) placed with
    /// a brush on terrains and meshes, stored as compact instance lists in a <c>.vfoliage</c> asset (world space; the entity
    /// is only the container) and drawn through the renderer's instancing path with a per-type cull distance, distance
    /// thinning and wind sway. Trees can collide (capsules) and block the navmesh.
    /// </summary>
    [DataContract(Name = "Foliage", Namespace = "")]
    public class Foliage : Component
    {
        private string _dataPath;
        private List<FoliageType> _types = new List<FoliageType>();
        private float _wind = 1f;
        private float _viewDistanceScale = 1f;

        public Foliage() : base() { }
        public Foliage(GameEntity entity) : base(entity) { }

        public override string DisplayName => "Foliage";
        public override string IconCode => "";
        public override string IconColor => "#43A047";

        /// <summary>The <c>.vfoliage</c> with the instances (project-relative); assigned on the first save (Assets/Foliage/&lt;entity&gt;.vfoliage).</summary>
        [DataMember(Name = "dataPath", Order = 10)]
        public string DataPath
        {
            get => _dataPath;
            set => SetProperty(ref _dataPath, value, nameof(DataPath));
        }

        /// <summary>The types this layer paints (edited in the inspector; instances bind to a type by name).</summary>
        [DataMember(Name = "types", Order = 11)]
        public List<FoliageType> Types
        {
            get => _types ?? (_types = new List<FoliageType>());
            set => SetProperty(ref _types, value ?? new List<FoliageType>(), nameof(Types));
        }

        /// <summary>Wind strength multiplier for every type's sway (0 = calm, 1 = a breeze, 2 = a storm). Scripts drive it through Weather.</summary>
        [DataMember(Name = "wind", Order = 12)]
        public float Wind
        {
            get => _wind;
            set => SetProperty(ref _wind, value < 0f ? 0f : (value > 4f ? 4f : value), nameof(Wind));
        }

        /// <summary>Scales every type's cull distance (quality setting).</summary>
        [DataMember(Name = "viewDistanceScale", Order = 13)]
        public float ViewDistanceScale
        {
            get => _viewDistanceScale;
            set => SetProperty(ref _viewDistanceScale, value < 0.1f ? 0.1f : (value > 4f ? 4f : value), nameof(ViewDistanceScale));
        }

        /// <summary>Bumped by the inspector / tools whenever a type's settings change (the service rebuilds what it must).</summary>
        public int Version { get; private set; }
        public void Touch() { Version++; }

        public int IndexOfType(string name)
        {
            for (int i = 0; i < Types.Count; i++) if (string.Equals(Types[i].Name, name, System.StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>A sensible default type for a model path (trees stand upright and collide; small plants align and thin out).</summary>
        public static FoliageType DefaultType(string name, string meshPath, bool small)
        {
            return small
                ? new FoliageType { Name = name, MeshPath = meshPath, AlignToNormal = true, MaxTilt = 8f, Density = 1.2f, MinSpacing = 0.6f, MaxSlope = 55f, CullDistance = 70f, ThinDistance = 25f, Collision = 0, WindStrength = 0.08f, WindHeight = 1f, MinScale = 0.7f, MaxScale = 1.3f, Sink = 0.03f }
                : new FoliageType { Name = name, MeshPath = meshPath, AlignToNormal = false, MaxTilt = 3f, Density = 0.02f, MinSpacing = 4f, MaxSlope = 40f, CullDistance = 400f, ThinDistance = 0f, Collision = 1, CollisionRadius = 0.35f, CollisionHeight = 6f, WindStrength = 0.25f, WindHeight = 10f, MinScale = 0.85f, MaxScale = 1.3f, Sink = 0.08f };
        }
    }
}
