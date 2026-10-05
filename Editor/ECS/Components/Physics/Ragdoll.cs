using System.Runtime.Serialization;

namespace Editor.ECS.Components.Physics
{
    /// <summary>
    /// Ragdoll (GitHub #104): turns an animated character into a physics ragdoll. At activation the ragdoll is built
    /// from the character's skeleton and its CURRENT animated pose — capsules on pelvis, spine, chest, head, upper and
    /// lower arms and legs, joined by swing-twist joints with hinges at the elbows and knees — so the switch never
    /// pops; from then on the Jolt simulation drives the skinned mesh. The bones are detected on any humanoid rig
    /// (Mixamo, Unreal, Rigify, Unity, …), no per-model setup. Activate it from a script, usually on death
    /// (<c>Ragdoll.Activate(EntityId)</c>), or with <see cref="ActivateOnStart"/>. Needs an Animator on the same
    /// entity and a physics-enabled engine build; shots that hit a ragdoll push the body part they hit.
    /// </summary>
    [DataContract(Name = "Ragdoll", Namespace = "")]
    public class Ragdoll : Component
    {
        private bool _activateOnStart;
        private float _mass = 70f;
        private float _thickness = 1f;
        private float _friction = 0.8f;
        private float _damping = 0.25f;
        private float _jointFriction = 0.5f;
        private float _blendTime;
        private bool _disableColliders = true;

        public override string DisplayName => "Ragdoll";
        public override string IconCode => "\uE716";   // MDL2 "People"
        public override string IconColor => "#4FC14F";

        /// <summary>Fall as a ragdoll as soon as play starts (dead bodies, testing the setup).</summary>
        [DataMember(Name = "activateOnStart", Order = 10)]
        public bool ActivateOnStart { get => _activateOnStart; set => SetProperty(ref _activateOnStart, value, nameof(ActivateOnStart)); }

        /// <summary>Total mass of the body parts in kg (split realistically: torso heavy, hands light).</summary>
        [DataMember(Name = "mass", Order = 11)]
        public float Mass { get => _mass; set => SetProperty(ref _mass, Clamp(value, 5f, 500f), nameof(Mass)); }

        /// <summary>Scales the radius of every capsule (1 = average build; above 1 for armour or bulky creatures).</summary>
        [DataMember(Name = "thickness", Order = 12)]
        public float Thickness { get => _thickness; set => SetProperty(ref _thickness, Clamp(value, 0.3f, 3f), nameof(Thickness)); }

        /// <summary>Friction of the body parts against the world (0..1+): higher = the body slides less on slopes.</summary>
        [DataMember(Name = "friction", Order = 13)]
        public float Friction { get => _friction; set => SetProperty(ref _friction, Clamp(value, 0f, 2f), nameof(Friction)); }

        /// <summary>Angular damping of the body parts: 0 = floppy, higher = limbs settle faster.</summary>
        [DataMember(Name = "damping", Order = 14)]
        public float Damping { get => _damping; set => SetProperty(ref _damping, Clamp(value, 0f, 5f), nameof(Damping)); }

        /// <summary>Friction of the elbow and knee hinges in N·m (0 = loose, a few N·m = stiff, muscle-like).</summary>
        [DataMember(Name = "jointFriction", Order = 15)]
        public float JointFriction { get => _jointFriction; set => SetProperty(ref _jointFriction, Clamp(value, 0f, 50f), nameof(JointFriction)); }

        /// <summary>Seconds over which the animation hands over to the simulation (0 = at once). While it blends, the
        /// clip that is playing (a death animation, say) still shows through.</summary>
        [DataMember(Name = "blendTime", Order = 16)]
        public float BlendTime { get => _blendTime; set => SetProperty(ref _blendTime, Clamp(value, 0f, 3f), nameof(BlendTime)); }

        /// <summary>Remove the entity's own colliders (a hit capsule, say) while it is a ragdoll, so shots and props
        /// meet the limbs, not the standing shape.</summary>
        [DataMember(Name = "disableColliders", Order = 17)]
        public bool DisableColliders { get => _disableColliders; set => SetProperty(ref _disableColliders, value, nameof(DisableColliders)); }

        public Ragdoll() : base() { }
        public Ragdoll(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingMethod(StreamingContext context)
        {
            _activateOnStart = false; _mass = 70f; _thickness = 1f; _friction = 0.8f; _damping = 0.25f;
            _jointFriction = 0.5f; _blendTime = 0f; _disableColliders = true;
        }

        private static float Clamp(float v, float lo, float hi) => float.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }
}
