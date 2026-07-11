using System.Runtime.Serialization;

namespace Editor.ECS.Components.Animation
{
    /// <summary>
    /// Runtime two-bone IK (#179): pulls a 3-joint limb chain (e.g. LeftArm → LeftForeArm → LeftHand)
    /// so the TIP bone reaches a target that is rigidly coupled to ANOTHER bone of the SAME skeleton —
    /// the classic "support hand grips the weapon" setup: the weapon follows the right hand via a bone
    /// socket, and this component keeps the LEFT hand glued to a grip point relative to the right hand
    /// through every animation. Sits on the entity that carries the Animator. Solved entirely in model
    /// space inside the animation palette evaluation (after clips/layers/#178 overrides, before
    /// skinning), so ALL submeshes, bone sockets and bone queries see the IK'd pose.
    /// The chain is derived from the tip: mid = tip.Parent, root = mid.Parent.
    /// </summary>
    [DataContract(Name = "TwoBoneIk", Namespace = "")]
    public class TwoBoneIk : Component
    {
        private string _tipBone = "";
        private string _targetBone = "";
        private Vector3 _targetOffsetPosition;
        private Vector3 _targetOffsetRotation;
        private float _weight = 1f;
        private float _poleAngle;
        private bool _applyTipRotation = true;
        private bool _autoGrip = true;

        public override string DisplayName => "Two-Bone IK";
        public override string IconCode => "";   // MDL2 glyph
        public override string IconColor => "#C586C0";

        /// <summary>The END of the chain — the bone that reaches for the target (e.g. "mixamorig:LeftHand").
        /// Mid and root joints are its parent and grandparent (LeftForeArm, LeftArm).</summary>
        [DataMember(Name = "tipBone", Order = 10)]
        public string TipBone
        {
            get => _tipBone;
            set { if (SetProperty(ref _tipBone, value ?? "", nameof(TipBone))) NotifyIkChanged(); }
        }

        /// <summary>The bone the target is expressed against (e.g. "mixamorig:RightHand" — the weapon hand).
        /// The tip follows TargetOffset in THIS bone's local frame, so it tracks it through every animation.</summary>
        [DataMember(Name = "targetBone", Order = 11)]
        public string TargetBone
        {
            get => _targetBone;
            set { if (SetProperty(ref _targetBone, value ?? "", nameof(TargetBone))) NotifyIkChanged(); }
        }

        /// <summary>Grip position in the target bone's local frame (MODEL units — cm on a Mixamo rig).
        /// (0,0,0) = directly on the target bone. Tune live in the editor viewport (bind-pose preview).</summary>
        [DataMember(Name = "targetOffsetPosition", Order = 12)]
        public Vector3 TargetOffsetPosition
        {
            get => _targetOffsetPosition;
            set { if (SetProperty(ref _targetOffsetPosition, value, nameof(TargetOffsetPosition))) NotifyIkChanged(); }
        }

        /// <summary>Grip orientation relative to the target bone (Euler degrees, engine ZXY order) —
        /// the tip bone takes this orientation when <see cref="ApplyTipRotation"/> is on.</summary>
        [DataMember(Name = "targetOffsetRotation", Order = 13)]
        public Vector3 TargetOffsetRotation
        {
            get => _targetOffsetRotation;
            set { if (SetProperty(ref _targetOffsetRotation, value, nameof(TargetOffsetRotation))) NotifyIkChanged(); }
        }

        /// <summary>0 = animation only, 1 = full IK. Blend at runtime via Animation.SetIkWeight
        /// (e.g. release the support hand during reload).</summary>
        [DataMember(Name = "weight", Order = 14)]
        public float Weight
        {
            get => _weight;
            set
            {
                if (value < 0f) value = 0f; else if (value > 1f) value = 1f;
                if (SetProperty(ref _weight, value, nameof(Weight))) NotifyIkChanged();
            }
        }

        /// <summary>Rotates the elbow/knee around the shoulder→target axis (degrees). 0 keeps the
        /// animation's natural bend plane — usually correct; nudge if the elbow flips.</summary>
        [DataMember(Name = "poleAngle", Order = 15)]
        public float PoleAngle
        {
            get => _poleAngle;
            set { if (SetProperty(ref _poleAngle, value, nameof(PoleAngle))) NotifyIkChanged(); }
        }

        /// <summary>Also orient the tip bone (wrist) to the grip rotation — on for weapon grips so the
        /// palm wraps the handguard; off to keep the animated wrist orientation.</summary>
        [DataMember(Name = "applyTipRotation", Order = 16)]
        public bool ApplyTipRotation
        {
            get => _applyTipRotation;
            set { if (SetProperty(ref _applyTipRotation, value, nameof(ApplyTipRotation))) NotifyIkChanged(); }
        }

        /// <summary>Auto-grip (default on): CAPTURE the natural tip-relative-to-target grip from the
        /// animation on the first frame and hold it through every clip — so the support hand just stays
        /// where the idle/hold animation put it relative to the weapon hand, no manual offset needed.
        /// The grip offset fields then FINE-TUNE on top. Turn off to use only the explicit offset.</summary>
        [DataMember(Name = "autoGrip", Order = 17)]
        public bool AutoGrip
        {
            get => _autoGrip;
            set { if (SetProperty(ref _autoGrip, value, nameof(AutoGrip))) NotifyIkChanged(); }
        }

        public TwoBoneIk() : base() { }
        public TwoBoneIk(GameEntity entity) : base(entity) { }

        /// <summary>DataContractSerializer creates this object UNINITIALIZED — restore non-trivial defaults.</summary>
        [OnDeserializing]
        private void OnDeserializingMethod(StreamingContext context)
        {
            _tipBone = "";
            _targetBone = "";
            _targetOffsetPosition = default(Vector3);
            _targetOffsetRotation = default(Vector3);
            _weight = 1f;
            _poleAngle = 0f;
            _applyTipRotation = true;
            _autoGrip = true;
        }

        /// <summary>Live edit-mode preview: any field edit re-poses the animator so the editor viewport
        /// shows the IK'd pose immediately (null-safe during deserialization — Entity not wired yet).</summary>
        private void NotifyIkChanged()
        {
            if (Entity != null)
                Core.Animation.AnimationService.Instance.RefreshIk(Entity);
        }
    }
}
