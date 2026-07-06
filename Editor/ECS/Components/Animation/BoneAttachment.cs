using System.Runtime.Serialization;

namespace Editor.ECS.Components.Animation
{
    /// <summary>
    /// Attaches this entity to a BONE of an animated (skinned) entity: every frame after animation
    /// evaluation, this entity's transform is driven by boneWorld x offset — a weapon in a hand, a
    /// lantern on a belt, a hat on a head. The socket pass writes the entity's Transform (world ->
    /// local against its real parent), so children, colliders and scripts all follow for free.
    /// Target resolution: explicit entity id when set, else the nearest ANCESTOR with an Animator —
    /// so "weapon as child of character" works with zero wiring and survives prefab instantiation.
    /// Transform math lives in Core.Animation.BoneSocketService.
    /// </summary>
    [DataContract(Name = "BoneAttachment", Namespace = "")]
    public class BoneAttachment : Component
    {
        private string _targetEntityId = "";
        private string _boneName = "";
        private Vector3 _offsetPosition;
        private Vector3 _offsetRotation;
        private Vector3 _offsetScale = Vector3.One;

        public override string DisplayName => "Bone Attachment";
        public override string IconCode => "";   // MDL2 link glyph
        public override string IconColor => "#C586C0";

        /// <summary>Guid (as string) of the skeletal target entity. Empty = nearest ancestor with an Animator.</summary>
        [DataMember(Name = "targetEntityId", Order = 10)]
        public string TargetEntityId
        {
            get => _targetEntityId;
            set => SetProperty(ref _targetEntityId, value ?? "", nameof(TargetEntityId));
        }

        /// <summary>Skeleton bone this entity follows (e.g. "mixamorig:RightHand").</summary>
        [DataMember(Name = "boneName", Order = 11)]
        public string BoneName
        {
            get => _boneName;
            set => SetProperty(ref _boneName, value ?? "", nameof(BoneName));
        }

        /// <summary>Local offset from the bone, in bone space.</summary>
        [DataMember(Name = "offsetPosition", Order = 12)]
        public Vector3 OffsetPosition
        {
            get => _offsetPosition;
            set => SetProperty(ref _offsetPosition, value, nameof(OffsetPosition));
        }

        /// <summary>Local rotation offset from the bone (Euler degrees, engine ZXY order).</summary>
        [DataMember(Name = "offsetRotation", Order = 13)]
        public Vector3 OffsetRotation
        {
            get => _offsetRotation;
            set => SetProperty(ref _offsetRotation, value, nameof(OffsetRotation));
        }

        /// <summary>Local scale relative to the bone (bones usually carry unit scale).</summary>
        [DataMember(Name = "offsetScale", Order = 14)]
        public Vector3 OffsetScale
        {
            get => _offsetScale;
            set => SetProperty(ref _offsetScale, value, nameof(OffsetScale));
        }

        public BoneAttachment() : base() { }
        public BoneAttachment(GameEntity entity) : base(entity) { }

        /// <summary>DataContractSerializer creates this object UNINITIALIZED — restore non-trivial defaults
        /// (OffsetScale=0 would collapse the attached entity invisibly small).</summary>
        [OnDeserializing]
        private void OnDeserializingMethod(StreamingContext context)
        {
            _targetEntityId = "";
            _boneName = "";
            _offsetPosition = default(Vector3);
            _offsetRotation = default(Vector3);
            _offsetScale = Vector3.One;
        }
    }
}
