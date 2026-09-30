using System.Runtime.Serialization;

namespace Editor.ECS.Components.Animation
{
    /// <summary>
    /// Foot IK: plants both feet on uneven ground (stairs, slopes, rubble). Every frame each animated foot casts a ray
    /// down through the gameplay collision world; the ground height under it (relative to the flat floor the animation
    /// assumes) moves the foot up or down with a two-bone leg solve (the same solver as <see cref="TwoBoneIk"/>), the
    /// pelvis drops so the lower foot can reach, and the feet tilt to the surface normal. Works on any biped: the feet
    /// are detected by name (Foot/foot_l/foot.L/ankle in any convention) and the knee/hip joints are the joints above
    /// them (twist helpers and split segments skipped). Active while playing (the collision world exists in play);
    /// scripts blend it with <c>Animation.SetFootIkWeight(entity, w)</c>.
    /// </summary>
    [DataContract(Name = "FootIk", Namespace = "")]
    public class FootIk : Component
    {
        private RigPreset _rig = RigPreset.Auto;
        private float _weight = 1f;
        private float _maxStep = 0.45f;
        private float _rayHeight = 0.5f;
        private float _footHeight = -1f;
        private bool _alignToGround = true;
        private float _maxFootAngle = 35f;
        private bool _adjustPelvis = true;
        private float _smoothing = 0.08f;
        private int _groundLayers = -1;
        private string _leftFoot = "";
        private string _rightFoot = "";
        private string _pelvisBone = "";

        public override string DisplayName => "Foot IK";
        public override string IconCode => "";   // MDL2 walk glyph
        public override string IconColor => "#C586C0";

        /// <summary>Naming convention used to find the legs (Auto = detect on any rig).</summary>
        [DataMember(Name = "rig", Order = 10)]
        public RigPreset Rig { get => _rig; set { if (SetProperty(ref _rig, value, nameof(Rig))) Changed(); } }

        /// <summary>0 = animation only, 1 = feet fully planted.</summary>
        [DataMember(Name = "weight", Order = 11)]
        public float Weight { get => _weight; set { if (SetProperty(ref _weight, Clamp(value, 0f, 1f), nameof(Weight))) Changed(); } }

        /// <summary>Largest ground height difference handled (meters): feet move and the pelvis drops at most this much.</summary>
        [DataMember(Name = "maxStep", Order = 12)]
        public float MaxStep { get => _maxStep; set { if (SetProperty(ref _maxStep, Clamp(value, 0f, 2f), nameof(MaxStep))) Changed(); } }

        /// <summary>Rays start this far above the foot (meters) — ground up to this height above the floor is found.</summary>
        [DataMember(Name = "rayHeight", Order = 13)]
        public float RayHeight { get => _rayHeight; set { if (SetProperty(ref _rayHeight, Clamp(value, 0.05f, 3f), nameof(RayHeight))) Changed(); } }

        /// <summary>Ankle height above the sole (meters); negative = measured from the bind pose (ankle vs. toes).</summary>
        [DataMember(Name = "footHeight", Order = 14)]
        public float FootHeight { get => _footHeight; set { if (SetProperty(ref _footHeight, value < 0f ? -1f : value, nameof(FootHeight))) Changed(); } }

        /// <summary>Tilt the feet to the ground normal (slopes, stair edges).</summary>
        [DataMember(Name = "alignToGround", Order = 15)]
        public bool AlignToGround { get => _alignToGround; set { if (SetProperty(ref _alignToGround, value, nameof(AlignToGround))) Changed(); } }

        /// <summary>Largest foot tilt (degrees).</summary>
        [DataMember(Name = "maxFootAngle", Order = 16)]
        public float MaxFootAngle { get => _maxFootAngle; set { if (SetProperty(ref _maxFootAngle, Clamp(value, 0f, 80f), nameof(MaxFootAngle))) Changed(); } }

        /// <summary>Lower the pelvis so the lower foot can reach its ground (off = only the legs adapt).</summary>
        [DataMember(Name = "adjustPelvis", Order = 17)]
        public bool AdjustPelvis { get => _adjustPelvis; set { if (SetProperty(ref _adjustPelvis, value, nameof(AdjustPelvis))) Changed(); } }

        /// <summary>Time constant (seconds) of the foot/pelvis offsets; 0 = instant.</summary>
        [DataMember(Name = "smoothing", Order = 18)]
        public float Smoothing { get => _smoothing; set { if (SetProperty(ref _smoothing, Clamp(value, 0f, 2f), nameof(Smoothing))) Changed(); } }

        /// <summary>Entity layers the feet stand on (bit mask, -1 = all).</summary>
        [DataMember(Name = "groundLayers", Order = 19)]
        public int GroundLayers { get => _groundLayers; set { if (SetProperty(ref _groundLayers, value, nameof(GroundLayers))) Changed(); } }

        /// <summary>Explicit left foot (ankle) bone (empty = detect). Knee and hip are the joints above it.</summary>
        [DataMember(Name = "leftFoot", Order = 20)]
        public string LeftFoot { get => _leftFoot; set { if (SetProperty(ref _leftFoot, value ?? "", nameof(LeftFoot))) Changed(); } }

        /// <summary>Explicit right foot (ankle) bone (empty = detect).</summary>
        [DataMember(Name = "rightFoot", Order = 21)]
        public string RightFoot { get => _rightFoot; set { if (SetProperty(ref _rightFoot, value ?? "", nameof(RightFoot))) Changed(); } }

        /// <summary>Explicit pelvis/hips bone moved down on uneven ground (empty = the bone both legs hang from).</summary>
        [DataMember(Name = "pelvisBone", Order = 22)]
        public string PelvisBone { get => _pelvisBone; set { if (SetProperty(ref _pelvisBone, value ?? "", nameof(PelvisBone))) Changed(); } }

        public FootIk() : base() { }
        public FootIk(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingMethod(StreamingContext context)
        {
            _rig = RigPreset.Auto; _weight = 1f; _maxStep = 0.45f; _rayHeight = 0.5f; _footHeight = -1f;
            _alignToGround = true; _maxFootAngle = 35f; _adjustPelvis = true; _smoothing = 0.08f; _groundLayers = -1;
            _leftFoot = ""; _rightFoot = ""; _pelvisBone = "";
        }

        private void Changed()
        {
            if (Entity != null) Core.Animation.AnimationService.Instance.RefreshIk(Entity);
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
