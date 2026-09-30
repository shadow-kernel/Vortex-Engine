using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Editor.ECS.Components.Physics
{
    /// <summary>Drive of a hinge or slider joint.</summary>
    public enum JointMotorMode
    {
        /// <summary>No motor (a Friction value &gt; 0 still resists motion).</summary>
        Off,
        /// <summary>Drive to a target speed (deg/s or m/s) with a torque / force limit — fans, turntables, lifts.</summary>
        Velocity,
        /// <summary>Spring to a target angle / position (deg or m) — self-closing doors, spring-loaded platforms.</summary>
        Position
    }

    /// <summary>
    /// Physics joint (GitHub #103): connects this entity's rigid body to the body of <see cref="ConnectedEntity"/> —
    /// or to the static world when that is empty — and is simulated in play mode as a Jolt constraint by
    /// <c>PhysicsService</c>. The entity needs a Collider and (to be moved by the joint) a Dynamic Rigidbody.
    /// Anchor and axis are in the entity's LOCAL space (scaled with it, like Collider.Center); the pose at play
    /// start is the joint's rest pose (hinge angle 0, slider position 0). The two connected bodies never collide
    /// with each other.
    /// </summary>
    [DataContract(Name = "PhysicsJoint", Namespace = "")]
    public abstract class PhysicsJoint : Component
    {
        private string _connectedEntity = "";
        private Vector3 _anchor;
        private float _breakForce;

        public override string IconCode => "";   // MDL2 "Link"
        public override string IconColor => "#4FC14F";

        /// <summary>The entity this joint connects to: its name (nearest match wins, so references inside a prefab
        /// keep working per instance), a hierarchy path "Parent/Child", or its id (Guid). Empty = the world.</summary>
        [DataMember(Name = "connectedEntity", Order = 10)]
        public string ConnectedEntity
        {
            get => _connectedEntity ?? "";
            set => SetProperty(ref _connectedEntity, value ?? "", nameof(ConnectedEntity));
        }

        /// <summary>Joint position in this entity's local space (e.g. (-0.5, 0, 0) = the left edge of a scaled cube).</summary>
        [DataMember(Name = "anchor", Order = 11)]
        public Vector3 Anchor
        {
            get => _anchor;
            set => SetProperty(ref _anchor, value, nameof(Anchor));
        }

        /// <summary>Force in N at which the joint breaks (it is disabled and PhysicsService.JointBroken fires).
        /// 0 = unbreakable.</summary>
        [DataMember(Name = "breakForce", Order = 12)]
        public float BreakForce
        {
            get => _breakForce;
            set => SetProperty(ref _breakForce, Math.Max(0f, value), nameof(BreakForce));
        }

        protected PhysicsJoint() : base() { }
        protected PhysicsJoint(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingJoint(StreamingContext context)
        {
            _connectedEntity = "";
            _anchor = default(Vector3);
            _breakForce = 0f;
        }

        // ------------------------------------------------------------------------------------------ references

        /// <summary>The entity <see cref="ConnectedEntity"/> names, or null for the world / when nothing matches
        /// (<paramref name="notFound"/> tells the two apart). <paramref name="sceneRoots"/> = the scene's root
        /// entities (null = the owner's own hierarchy).</summary>
        public GameEntity ResolveConnectedEntity(IEnumerable<GameEntity> sceneRoots, out bool notFound)
        {
            var hit = ResolveReference(Entity, ConnectedEntity, sceneRoots);
            notFound = hit == null && !string.IsNullOrWhiteSpace(ConnectedEntity);
            return hit;
        }

        /// <summary>Resolve an entity reference as the joints store it: a Guid; a path "A/B/C" from a scene root or
        /// relative to the owner's parent; otherwise a name (case-insensitive), nearest first — the owner's siblings
        /// and their subtrees, then each ancestor's subtree, then the whole scene. Null when empty or unmatched.</summary>
        public static GameEntity ResolveReference(GameEntity owner, string reference, IEnumerable<GameEntity> sceneRoots)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            string r = reference.Trim();
            var roots = new List<GameEntity>();
            if (sceneRoots != null) foreach (var e in sceneRoots) if (e != null) roots.Add(e);
            if (roots.Count == 0 && owner != null)
            {
                var top = owner;
                while (top.Parent != null) top = top.Parent;
                roots.Add(top);
            }

            if (Guid.TryParse(r, out var id))
            {
                var byId = FindFirst(roots, e => e.Id == id);
                if (byId != null) return byId;
            }

            if (r.IndexOf('/') >= 0)
            {
                var parts = r.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var byPath = FollowPath(roots, parts, 0);
                if (byPath == null && owner?.Parent?.Children != null) byPath = FollowPath(owner.Parent.Children, parts, 0);
                return byPath;
            }

            for (var scope = owner?.Parent; scope != null; scope = scope.Parent)
            {
                var near = FindFirst(scope.Children, e => !ReferenceEquals(e, owner) && NameIs(e, r));
                if (near != null) return near;
            }
            return FindFirst(roots, e => !ReferenceEquals(e, owner) && NameIs(e, r));
        }

        /// <summary>The reference to store for <paramref name="target"/> (the scene picker): its name when that
        /// resolves back to it from <paramref name="owner"/>, else its Guid. "" for the world (null).</summary>
        public static string ReferenceTo(GameEntity owner, GameEntity target, IEnumerable<GameEntity> sceneRoots)
        {
            if (target == null) return "";
            string name = target.Name ?? "";
            if (name.Length > 0 && name.IndexOf('/') < 0 && !Guid.TryParse(name, out _)
                && ReferenceEquals(ResolveReference(owner, name, sceneRoots), target))
                return name;
            return target.Id.ToString();
        }

        protected static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static bool NameIs(GameEntity e, string name) => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase);

        private static GameEntity FindFirst(IEnumerable<GameEntity> list, Func<GameEntity, bool> match)
        {
            if (list == null) return null;
            foreach (var e in list)
            {
                if (e == null) continue;
                if (match(e)) return e;
                var inner = FindFirst(e.Children, match);
                if (inner != null) return inner;
            }
            return null;
        }

        private static GameEntity FollowPath(IEnumerable<GameEntity> level, string[] parts, int index)
        {
            if (level == null || index >= parts.Length) return null;
            foreach (var e in level)
            {
                if (e == null || !NameIs(e, parts[index].Trim())) continue;
                if (index == parts.Length - 1) return e;
                var deeper = FollowPath(e.Children, parts, index + 1);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }

    /// <summary>Hinge joint: rotation about one axis through the anchor — doors, lids, wheels, levers. Optional
    /// angle limits, a motor (target speed) or a spring (target angle, e.g. a self-closing door) and friction.</summary>
    [DataContract(Name = "HingeJoint", Namespace = "")]
    public class HingeJoint : PhysicsJoint
    {
        private Vector3 _axis = Vector3.Up;
        private bool _useLimits;
        private float _minAngle = -90f;
        private float _maxAngle = 90f;
        private JointMotorMode _motorMode = JointMotorMode.Off;
        private float _targetVelocity = 90f;
        private float _targetAngle;
        private float _maxTorque = 100f;
        private float _springFrequency = 2f;
        private float _springDamping = 1f;
        private float _friction;

        public override string DisplayName => "Hinge Joint";

        /// <summary>Rotation axis in local space (default: up = a door's vertical hinge).</summary>
        [DataMember(Name = "axis", Order = 20)]
        public Vector3 Axis { get => _axis; set => SetProperty(ref _axis, value, nameof(Axis)); }

        [DataMember(Name = "useLimits", Order = 21)]
        public bool UseLimits { get => _useLimits; set => SetProperty(ref _useLimits, value, nameof(UseLimits)); }

        /// <summary>Lower limit in degrees relative to the start pose, -180..0.</summary>
        [DataMember(Name = "minAngle", Order = 22)]
        public float MinAngle { get => _minAngle; set => SetProperty(ref _minAngle, Clamp(value, -180f, 0f), nameof(MinAngle)); }

        /// <summary>Upper limit in degrees relative to the start pose, 0..180.</summary>
        [DataMember(Name = "maxAngle", Order = 23)]
        public float MaxAngle { get => _maxAngle; set => SetProperty(ref _maxAngle, Clamp(value, 0f, 180f), nameof(MaxAngle)); }

        [DataMember(Name = "motorMode", Order = 24)]
        public JointMotorMode MotorMode { get => _motorMode; set => SetProperty(ref _motorMode, value, nameof(MotorMode)); }

        /// <summary>Motor speed in degrees per second (Velocity mode).</summary>
        [DataMember(Name = "targetVelocity", Order = 25)]
        public float TargetVelocity { get => _targetVelocity; set => SetProperty(ref _targetVelocity, value, nameof(TargetVelocity)); }

        /// <summary>Spring target in degrees (Position mode), clamped to the limits.</summary>
        [DataMember(Name = "targetAngle", Order = 26)]
        public float TargetAngle { get => _targetAngle; set => SetProperty(ref _targetAngle, value, nameof(TargetAngle)); }

        /// <summary>Maximum motor / spring torque in N·m; 0 = unlimited.</summary>
        [DataMember(Name = "maxTorque", Order = 27)]
        public float MaxTorque { get => _maxTorque; set => SetProperty(ref _maxTorque, Math.Max(0f, value), nameof(MaxTorque)); }

        /// <summary>Spring stiffness as an oscillation frequency in Hz (Position mode).</summary>
        [DataMember(Name = "springFrequency", Order = 28)]
        public float SpringFrequency { get => _springFrequency; set => SetProperty(ref _springFrequency, Math.Max(0.01f, value), nameof(SpringFrequency)); }

        /// <summary>Spring damping ratio (Position mode): 0 = bouncy, 1 = critically damped.</summary>
        [DataMember(Name = "springDamping", Order = 29)]
        public float SpringDamping { get => _springDamping; set => SetProperty(ref _springDamping, Math.Max(0f, value), nameof(SpringDamping)); }

        /// <summary>Friction torque in N·m while the motor is off (a door that doesn't swing forever).</summary>
        [DataMember(Name = "friction", Order = 30)]
        public float Friction { get => _friction; set => SetProperty(ref _friction, Math.Max(0f, value), nameof(Friction)); }

        public HingeJoint() : base() { }
        public HingeJoint(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingHinge(StreamingContext context)
        {
            _axis = Vector3.Up; _useLimits = false; _minAngle = -90f; _maxAngle = 90f;
            _motorMode = JointMotorMode.Off; _targetVelocity = 90f; _targetAngle = 0f; _maxTorque = 100f;
            _springFrequency = 2f; _springDamping = 1f; _friction = 0f;
        }
    }

    /// <summary>Ball (socket) joint: free rotation about the anchor — pendulums, hanging lamps, chains. Optional
    /// swing cone around <see cref="Axis"/> and twist range.</summary>
    [DataContract(Name = "BallJoint", Namespace = "")]
    public class BallJoint : PhysicsJoint
    {
        private Vector3 _axis = Vector3.Down;
        private bool _useLimits;
        private float _swingLimit = 45f;
        private float _twistMin = -45f;
        private float _twistMax = 45f;

        public override string DisplayName => "Ball Joint";

        /// <summary>Centre of the swing cone / twist axis in local space (default: down, a hanging lamp).</summary>
        [DataMember(Name = "axis", Order = 20)]
        public Vector3 Axis { get => _axis; set => SetProperty(ref _axis, value, nameof(Axis)); }

        [DataMember(Name = "useLimits", Order = 21)]
        public bool UseLimits { get => _useLimits; set => SetProperty(ref _useLimits, value, nameof(UseLimits)); }

        /// <summary>Half angle of the swing cone in degrees, 0..180.</summary>
        [DataMember(Name = "swingLimit", Order = 22)]
        public float SwingLimit { get => _swingLimit; set => SetProperty(ref _swingLimit, Clamp(value, 0f, 180f), nameof(SwingLimit)); }

        /// <summary>Twist range about the axis in degrees, -180..180.</summary>
        [DataMember(Name = "twistMin", Order = 23)]
        public float TwistMin { get => _twistMin; set => SetProperty(ref _twistMin, Clamp(value, -180f, 180f), nameof(TwistMin)); }

        [DataMember(Name = "twistMax", Order = 24)]
        public float TwistMax { get => _twistMax; set => SetProperty(ref _twistMax, Clamp(value, -180f, 180f), nameof(TwistMax)); }

        public BallJoint() : base() { }
        public BallJoint(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingBall(StreamingContext context)
        {
            _axis = Vector3.Down; _useLimits = false; _swingLimit = 45f; _twistMin = -45f; _twistMax = 45f;
        }
    }

    /// <summary>Slider (prismatic) joint: movement along one axis only, no rotation — lifts, drawers, sliding doors,
    /// pistons. Travel limits relative to the start pose, a motor (target speed) or spring (target position).</summary>
    [DataContract(Name = "SliderJoint", Namespace = "")]
    public class SliderJoint : PhysicsJoint
    {
        private Vector3 _axis = Vector3.Right;
        private bool _useLimits = true;
        private float _minPosition = -1f;
        private float _maxPosition = 1f;
        private JointMotorMode _motorMode = JointMotorMode.Off;
        private float _targetVelocity = 1f;
        private float _targetPosition;
        private float _maxForce = 1000f;
        private float _springFrequency = 2f;
        private float _springDamping = 1f;
        private float _friction;

        public override string DisplayName => "Slider Joint";

        /// <summary>Movement axis in local space.</summary>
        [DataMember(Name = "axis", Order = 20)]
        public Vector3 Axis { get => _axis; set => SetProperty(ref _axis, value, nameof(Axis)); }

        [DataMember(Name = "useLimits", Order = 21)]
        public bool UseLimits { get => _useLimits; set => SetProperty(ref _useLimits, value, nameof(UseLimits)); }

        /// <summary>Lower travel limit in metres from the start pose (&lt;= 0).</summary>
        [DataMember(Name = "minPosition", Order = 22)]
        public float MinPosition { get => _minPosition; set => SetProperty(ref _minPosition, Math.Min(0f, value), nameof(MinPosition)); }

        /// <summary>Upper travel limit in metres from the start pose (&gt;= 0).</summary>
        [DataMember(Name = "maxPosition", Order = 23)]
        public float MaxPosition { get => _maxPosition; set => SetProperty(ref _maxPosition, Math.Max(0f, value), nameof(MaxPosition)); }

        [DataMember(Name = "motorMode", Order = 24)]
        public JointMotorMode MotorMode { get => _motorMode; set => SetProperty(ref _motorMode, value, nameof(MotorMode)); }

        /// <summary>Motor speed in m/s (Velocity mode).</summary>
        [DataMember(Name = "targetVelocity", Order = 25)]
        public float TargetVelocity { get => _targetVelocity; set => SetProperty(ref _targetVelocity, value, nameof(TargetVelocity)); }

        /// <summary>Spring target in metres from the start pose (Position mode), clamped to the limits.</summary>
        [DataMember(Name = "targetPosition", Order = 26)]
        public float TargetPosition { get => _targetPosition; set => SetProperty(ref _targetPosition, value, nameof(TargetPosition)); }

        /// <summary>Maximum motor / spring force in N; 0 = unlimited. A lift needs more than its load's weight.</summary>
        [DataMember(Name = "maxForce", Order = 27)]
        public float MaxForce { get => _maxForce; set => SetProperty(ref _maxForce, Math.Max(0f, value), nameof(MaxForce)); }

        [DataMember(Name = "springFrequency", Order = 28)]
        public float SpringFrequency { get => _springFrequency; set => SetProperty(ref _springFrequency, Math.Max(0.01f, value), nameof(SpringFrequency)); }

        [DataMember(Name = "springDamping", Order = 29)]
        public float SpringDamping { get => _springDamping; set => SetProperty(ref _springDamping, Math.Max(0f, value), nameof(SpringDamping)); }

        /// <summary>Friction force in N while the motor is off.</summary>
        [DataMember(Name = "friction", Order = 30)]
        public float Friction { get => _friction; set => SetProperty(ref _friction, Math.Max(0f, value), nameof(Friction)); }

        public SliderJoint() : base() { }
        public SliderJoint(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingSlider(StreamingContext context)
        {
            _axis = Vector3.Right; _useLimits = true; _minPosition = -1f; _maxPosition = 1f;
            _motorMode = JointMotorMode.Off; _targetVelocity = 1f; _targetPosition = 0f; _maxForce = 1000f;
            _springFrequency = 2f; _springDamping = 1f; _friction = 0f;
        }
    }

    /// <summary>Fixed joint: welds this body to the connected body (or pins it to the world) — breakable planks,
    /// attachments that snap off under load (set a Break Force).</summary>
    [DataContract(Name = "FixedJoint", Namespace = "")]
    public class FixedJoint : PhysicsJoint
    {
        public override string DisplayName => "Fixed Joint";

        public FixedJoint() : base() { }
        public FixedJoint(GameEntity entity) : base(entity) { }
    }

    /// <summary>Distance joint: keeps the anchor within [Min, Max] distance of the connected anchor — ropes (min 0),
    /// rods (min = max), bungees (spring).</summary>
    [DataContract(Name = "DistanceJoint", Namespace = "")]
    public class DistanceJoint : PhysicsJoint
    {
        private Vector3 _connectedAnchor = Vector3.Up;
        private float _minDistance;
        private float _maxDistance = -1f;
        private float _springFrequency;
        private float _springDamping = 0.5f;

        public override string DisplayName => "Distance Joint";

        /// <summary>The other end: a local point on the connected entity; with no connected entity (the world) the
        /// offset from this entity's anchor in world space at play start (default: 1 m straight up).</summary>
        [DataMember(Name = "connectedAnchor", Order = 20)]
        public Vector3 ConnectedAnchor { get => _connectedAnchor; set => SetProperty(ref _connectedAnchor, value, nameof(ConnectedAnchor)); }

        /// <summary>Minimum distance in m: 0 = a rope that can go slack; -1 = the distance at play start (with Max -1
        /// too: a rigid rod).</summary>
        [DataMember(Name = "minDistance", Order = 21)]
        public float MinDistance { get => _minDistance; set => SetProperty(ref _minDistance, value < 0f ? -1f : value, nameof(MinDistance)); }

        /// <summary>Maximum distance in m (the rope length); -1 = the distance at play start.</summary>
        [DataMember(Name = "maxDistance", Order = 22)]
        public float MaxDistance { get => _maxDistance; set => SetProperty(ref _maxDistance, value < 0f ? -1f : value, nameof(MaxDistance)); }

        /// <summary>&gt; 0 makes the limits a spring of this frequency in Hz (a bungee); 0 = rigid.</summary>
        [DataMember(Name = "springFrequency", Order = 23)]
        public float SpringFrequency { get => _springFrequency; set => SetProperty(ref _springFrequency, Math.Max(0f, value), nameof(SpringFrequency)); }

        [DataMember(Name = "springDamping", Order = 24)]
        public float SpringDamping { get => _springDamping; set => SetProperty(ref _springDamping, Math.Max(0f, value), nameof(SpringDamping)); }

        public DistanceJoint() : base() { }
        public DistanceJoint(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingDistance(StreamingContext context)
        {
            _connectedAnchor = Vector3.Up; _minDistance = 0f; _maxDistance = -1f; _springFrequency = 0f; _springDamping = 0.5f;
        }
    }
}
