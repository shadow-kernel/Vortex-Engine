using System.Runtime.Serialization;

namespace Editor.ECS.Components.Audio
{
    /// <summary>
    /// Reverb zone shape.
    /// </summary>
    public enum ReverbZoneShape
    {
        Sphere,
        Box
    }

    /// <summary>
    /// A reverb zone (issue #15): while the LISTENER is inside the zone, the global
    /// reverb takes on this zone's character (decay/wet/pre-delay). Crossing the
    /// boundary blends over the falloff distance; overlapping zones blend by weight.
    /// A source's contribution to the reverb is scaled by its own ReverbZoneMix.
    /// The zone sits in its entity's world transform like an Area or a collider and
    /// counts only while the entity is active in the hierarchy (#497).
    /// </summary>
    [DataContract(Name = "ReverbZone", Namespace = "")]
    public class ReverbZone : Component
    {
        private int _shape = (int)ReverbZoneShape.Sphere;
        private float _radius = 10f;
        private Vector3 _boxExtents = new Vector3(10f, 5f, 10f);
        private float _falloff = 3f;
        private float _decayTime = 1.8f;
        private float _wetLevel = 0.6f;
        private float _preDelayMs = 20f;

        public override string DisplayName => "Reverb Zone";
        public override string IconCode => "\uE9A1";
        public override string IconColor => "#CE9178";

        /// <summary>0 = Sphere (Radius), 1 = Box (BoxExtents = half sizes).</summary>
        [DataMember(Name = "shape", Order = 10)]
        public int Shape
        {
            get => _shape;
            set => SetProperty(ref _shape, value, nameof(Shape));
        }

        /// <summary>Sphere radius (local units: scaled by the entity's largest world scale axis, as a sphere collider is).</summary>
        [DataMember(Name = "radius", Order = 11)]
        public float Radius
        {
            get => _radius;
            set => SetProperty(ref _radius, value, nameof(Radius));
        }

        /// <summary>Box HALF extents in the entity's local units: the entity's (and its parents') scale and rotation
        /// apply, so the box is the oriented box the gizmo draws (#497).</summary>
        [DataMember(Name = "boxExtents", Order = 12)]
        public Vector3 BoxExtents
        {
            get => _boxExtents;
            set => SetProperty(ref _boxExtents, value, nameof(BoxExtents));
        }

        /// <summary>Blend distance (world metres) beyond the boundary: full effect inside, silence
        /// at boundary + falloff. Prevents clicks when walking through the door.</summary>
        [DataMember(Name = "falloff", Order = 13)]
        public float Falloff
        {
            get => _falloff;
            set => SetProperty(ref _falloff, value, nameof(Falloff));
        }

        /// <summary>Tail length in seconds (0.1 dry closet .. 20 cathedral).</summary>
        [DataMember(Name = "decayTime", Order = 14)]
        public float DecayTime
        {
            get => _decayTime;
            set => SetProperty(ref _decayTime, value, nameof(DecayTime));
        }

        /// <summary>Wet level 0..1 — how loud the reverb tail is.</summary>
        [DataMember(Name = "wetLevel", Order = 15)]
        public float WetLevel
        {
            get => _wetLevel;
            set => SetProperty(ref _wetLevel, value, nameof(WetLevel));
        }

        /// <summary>Pre-delay in ms (0..200) — bigger rooms start their tail later.</summary>
        [DataMember(Name = "preDelayMs", Order = 16)]
        public float PreDelayMs
        {
            get => _preDelayMs;
            set => SetProperty(ref _preDelayMs, value, nameof(PreDelayMs));
        }

        public ReverbZone() : base() { }
        public ReverbZone(GameEntity entity) : base(entity) { }
    }
}
