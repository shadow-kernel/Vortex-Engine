using System.Runtime.Serialization;

namespace Editor.ECS.Components.Animation
{
    /// <summary>
    /// Procedural look-at: the head — optionally helped by the neck and the upper spine — turns toward a target entity
    /// or a script-supplied world point, within yaw/pitch limits measured against the torso, blended by
    /// <see cref="Weight"/> and smoothed over time. Sits on the entity that carries the Animator; works on any rig:
    /// head / neck / spine bones are detected (a "head" bone in any naming convention, the neck line between the chest
    /// and the head, the chest = where both arms hang from), and the face direction comes from the character's bind pose
    /// (heel → toes), so no axis setup is needed. Explicit bone names and a forward override cover exotic rigs.
    /// Scripts: <c>Animation.SetLookAtTarget(bot, point)</c> / <c>Animation.SetLookAtTarget(bot, playerEntity)</c> /
    /// <c>Animation.ClearLookAtTarget(bot)</c> / <c>Animation.SetLookAtWeight(bot, 0.5f)</c> — those work even without
    /// this component (defaults), the component makes it editor-authored and previewable in the viewport.
    /// </summary>
    [DataContract(Name = "LookAtIk", Namespace = "")]
    public class LookAtIk : Component
    {
        private RigPreset _rig = RigPreset.Auto;
        private string _targetEntity = "";
        private Vector3 _targetOffset;
        private float _weight = 1f;
        private float _headWeight = 0.6f;
        private float _neckWeight = 0.3f;
        private float _spineWeight = 0.1f;
        private float _maxYaw = 70f;
        private float _maxPitch = 40f;
        private float _smoothing = 0.15f;
        private string _headBone = "";
        private string _neckBones = "";
        private string _spineBones = "";
        private int _forwardAxis = 0;

        public override string DisplayName => "Look-At IK";
        public override string IconCode => "";   // MDL2 view glyph
        public override string IconColor => "#C586C0";

        /// <summary>Naming convention used to find head/neck/spine (Auto = detect on any rig).</summary>
        [DataMember(Name = "rig", Order = 10)]
        public RigPreset Rig { get => _rig; set { if (SetProperty(ref _rig, value, nameof(Rig))) Changed(); } }

        /// <summary>Entity to look at — its NAME (first match in the scene) or its id. Empty = only script targets.
        /// A target with a skeleton is looked at on its head bone, anything else at its origin (+ offset).</summary>
        [DataMember(Name = "targetEntity", Order = 11)]
        public string TargetEntity { get => _targetEntity; set { if (SetProperty(ref _targetEntity, value ?? "", nameof(TargetEntity))) Changed(); } }

        /// <summary>World-space offset added to the target point (e.g. eye height above an entity origin).</summary>
        [DataMember(Name = "targetOffset", Order = 12)]
        public Vector3 TargetOffset { get => _targetOffset; set { if (SetProperty(ref _targetOffset, value, nameof(TargetOffset))) Changed(); } }

        /// <summary>0 = animation only, 1 = look fully at the target (within the limits).</summary>
        [DataMember(Name = "weight", Order = 13)]
        public float Weight { get => _weight; set { if (SetProperty(ref _weight, Clamp01(value), nameof(Weight))) Changed(); } }

        /// <summary>Share of the turn done by the head (the weights are normalised over the bones found).</summary>
        [DataMember(Name = "headWeight", Order = 14)]
        public float HeadWeight { get => _headWeight; set { if (SetProperty(ref _headWeight, Clamp01(value), nameof(HeadWeight))) Changed(); } }

        /// <summary>Share of the turn done by the neck bone(s).</summary>
        [DataMember(Name = "neckWeight", Order = 15)]
        public float NeckWeight { get => _neckWeight; set { if (SetProperty(ref _neckWeight, Clamp01(value), nameof(NeckWeight))) Changed(); } }

        /// <summary>Share of the turn done by the upper spine (chest). 0 = head and neck only.</summary>
        [DataMember(Name = "spineWeight", Order = 16)]
        public float SpineWeight { get => _spineWeight; set { if (SetProperty(ref _spineWeight, Clamp01(value), nameof(SpineWeight))) Changed(); } }

        /// <summary>Largest sideways turn relative to the torso (degrees).</summary>
        [DataMember(Name = "maxYaw", Order = 17)]
        public float MaxYaw { get => _maxYaw; set { if (SetProperty(ref _maxYaw, Clamp(value, 0f, 180f), nameof(MaxYaw))) Changed(); } }

        /// <summary>Largest up/down turn relative to the torso (degrees).</summary>
        [DataMember(Name = "maxPitch", Order = 18)]
        public float MaxPitch { get => _maxPitch; set { if (SetProperty(ref _maxPitch, Clamp(value, 0f, 89f), nameof(MaxPitch))) Changed(); } }

        /// <summary>Time constant (seconds) the look direction follows the target with; 0 = instant.</summary>
        [DataMember(Name = "smoothing", Order = 19)]
        public float Smoothing { get => _smoothing; set { if (SetProperty(ref _smoothing, Clamp(value, 0f, 5f), nameof(Smoothing))) Changed(); } }

        /// <summary>Explicit head bone (empty = detect).</summary>
        [DataMember(Name = "headBone", Order = 20)]
        public string HeadBone { get => _headBone; set { if (SetProperty(ref _headBone, value ?? "", nameof(HeadBone))) Changed(); } }

        /// <summary>Explicit neck bones, comma separated, lowest first (empty = detect).</summary>
        [DataMember(Name = "neckBones", Order = 21)]
        public string NeckBones { get => _neckBones; set { if (SetProperty(ref _neckBones, value ?? "", nameof(NeckBones))) Changed(); } }

        /// <summary>Explicit spine bones, comma separated, lowest first (empty = detect: the chest and the bone below).</summary>
        [DataMember(Name = "spineBones", Order = 22)]
        public string SpineBones { get => _spineBones; set { if (SetProperty(ref _spineBones, value ?? "", nameof(SpineBones))) Changed(); } }

        /// <summary>Which way the character faces in its model: 0 = auto (from the bind pose), 1 = +Z, 2 = -Z, 3 = +X, 4 = -X.</summary>
        [DataMember(Name = "forwardAxis", Order = 23)]
        public int ForwardAxis { get => _forwardAxis; set { if (SetProperty(ref _forwardAxis, value < 0 ? 0 : (value > 4 ? 4 : value), nameof(ForwardAxis))) Changed(); } }

        public LookAtIk() : base() { }
        public LookAtIk(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingMethod(StreamingContext context)
        {
            _rig = RigPreset.Auto; _targetEntity = ""; _targetOffset = default(Vector3);
            _weight = 1f; _headWeight = 0.6f; _neckWeight = 0.3f; _spineWeight = 0.1f;
            _maxYaw = 70f; _maxPitch = 40f; _smoothing = 0.15f;
            _headBone = ""; _neckBones = ""; _spineBones = ""; _forwardAxis = 0;
        }

        private void Changed()
        {
            if (Entity != null) Core.Animation.AnimationService.Instance.RefreshIk(Entity);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
