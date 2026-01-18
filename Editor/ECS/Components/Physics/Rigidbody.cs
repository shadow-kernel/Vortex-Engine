using System.Runtime.Serialization;

namespace Editor.ECS.Components.Physics
{
    /// <summary>
    /// Rigidbody-Typ
    /// </summary>
    public enum RigidbodyType
    {
        Dynamic,
        Kinematic,
        Static
    }

    /// <summary>
    /// Interpolation für Rigidbody
    /// </summary>
    public enum RigidbodyInterpolation
    {
        None,
        Interpolate,
        Extrapolate
    }

    /// <summary>
    /// Kollisionserkennung
    /// </summary>
    public enum CollisionDetectionMode
    {
        Discrete,
        Continuous,
        ContinuousDynamic,
        ContinuousSpeculative
    }

    /// <summary>
    /// Rigidbody-Komponente für Physik-Simulation.
    /// </summary>
    [DataContract(Name = "Rigidbody", Namespace = "")]
    public class Rigidbody : Component
    {
        private float _mass = 1f;
        private float _drag;
        private float _angularDrag = 0.05f;
        private bool _useGravity = true;
        private RigidbodyType _bodyType = RigidbodyType.Dynamic;
        private RigidbodyInterpolation _interpolation = RigidbodyInterpolation.None;
        private CollisionDetectionMode _collisionDetection = CollisionDetectionMode.Discrete;
        
        // Constraints
        private bool _freezePositionX;
        private bool _freezePositionY;
        private bool _freezePositionZ;
        private bool _freezeRotationX;
        private bool _freezeRotationY;
        private bool _freezeRotationZ;

        public override string DisplayName => "Rigidbody";
        public override string IconCode => "\uE7AD";
        public override string IconColor => "#CE9178";

        /// <summary>
        /// Masse in Kilogramm
        /// </summary>
        [DataMember(Name = "mass", Order = 10)]
        public float Mass
        {
            get => _mass;
            set => SetProperty(ref _mass, value, nameof(Mass));
        }

        /// <summary>
        /// Linearer Widerstand
        /// </summary>
        [DataMember(Name = "drag", Order = 11)]
        public float Drag
        {
            get => _drag;
            set => SetProperty(ref _drag, value, nameof(Drag));
        }

        /// <summary>
        /// Winkelwiderstand
        /// </summary>
        [DataMember(Name = "angularDrag", Order = 12)]
        public float AngularDrag
        {
            get => _angularDrag;
            set => SetProperty(ref _angularDrag, value, nameof(AngularDrag));
        }

        /// <summary>
        /// Ob Gravitation verwendet wird
        /// </summary>
        [DataMember(Name = "useGravity", Order = 13)]
        public bool UseGravity
        {
            get => _useGravity;
            set => SetProperty(ref _useGravity, value, nameof(UseGravity));
        }

        /// <summary>
        /// Typ des Rigidbody
        /// </summary>
        [DataMember(Name = "bodyType", Order = 14)]
        public RigidbodyType BodyType
        {
            get => _bodyType;
            set => SetProperty(ref _bodyType, value, nameof(BodyType));
        }

        /// <summary>
        /// Interpolationsmodus
        /// </summary>
        [DataMember(Name = "interpolation", Order = 15)]
        public RigidbodyInterpolation Interpolation
        {
            get => _interpolation;
            set => SetProperty(ref _interpolation, value, nameof(Interpolation));
        }

        /// <summary>
        /// Kollisionserkennungsmodus
        /// </summary>
        [DataMember(Name = "collisionDetection", Order = 16)]
        public CollisionDetectionMode CollisionDetection
        {
            get => _collisionDetection;
            set => SetProperty(ref _collisionDetection, value, nameof(CollisionDetection));
        }

        #region Constraints

        [DataMember(Name = "freezePosX", Order = 20)]
        public bool FreezePositionX
        {
            get => _freezePositionX;
            set => SetProperty(ref _freezePositionX, value, nameof(FreezePositionX));
        }

        [DataMember(Name = "freezePosY", Order = 21)]
        public bool FreezePositionY
        {
            get => _freezePositionY;
            set => SetProperty(ref _freezePositionY, value, nameof(FreezePositionY));
        }

        [DataMember(Name = "freezePosZ", Order = 22)]
        public bool FreezePositionZ
        {
            get => _freezePositionZ;
            set => SetProperty(ref _freezePositionZ, value, nameof(FreezePositionZ));
        }

        [DataMember(Name = "freezeRotX", Order = 23)]
        public bool FreezeRotationX
        {
            get => _freezeRotationX;
            set => SetProperty(ref _freezeRotationX, value, nameof(FreezeRotationX));
        }

        [DataMember(Name = "freezeRotY", Order = 24)]
        public bool FreezeRotationY
        {
            get => _freezeRotationY;
            set => SetProperty(ref _freezeRotationY, value, nameof(FreezeRotationY));
        }

        [DataMember(Name = "freezeRotZ", Order = 25)]
        public bool FreezeRotationZ
        {
            get => _freezeRotationZ;
            set => SetProperty(ref _freezeRotationZ, value, nameof(FreezeRotationZ));
        }

        #endregion

        public Rigidbody() : base() { }
        public Rigidbody(GameEntity entity) : base(entity) { }
    }
}
