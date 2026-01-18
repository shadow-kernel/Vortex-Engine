using System.Runtime.Serialization;

namespace Editor.ECS.Components.Physics
{
    /// <summary>
    /// Collider-Typen
    /// </summary>
    public enum ColliderType
    {
        Box,
        Sphere,
        Capsule,
        Mesh,
        Convex
    }

    /// <summary>
    /// Physik-Material für Collider
    /// </summary>
    [DataContract(Name = "PhysicsMaterial", Namespace = "")]
    public class PhysicsMaterial
    {
        [DataMember(Name = "friction")]
        public float Friction { get; set; } = 0.5f;

        [DataMember(Name = "bounciness")]
        public float Bounciness { get; set; } = 0f;

        [DataMember(Name = "frictionCombine")]
        public int FrictionCombine { get; set; } = 0; // Average

        [DataMember(Name = "bounceCombine")]
        public int BounceCombine { get; set; } = 0; // Average
    }

    /// <summary>
    /// Basis-Collider-Komponente für Physik-Kollisionen.
    /// </summary>
    [DataContract(Name = "Collider", Namespace = "")]
    public class Collider : Component
    {
        private ColliderType _colliderType = ColliderType.Box;
        private bool _isTrigger;
        private Vector3 _center;
        private PhysicsMaterial _material;

        public override string DisplayName => $"{_colliderType} Collider";
        public override string IconCode => "\uE73C";
        public override string IconColor => "#4FC14F";

        /// <summary>
        /// Typ des Colliders
        /// </summary>
        [DataMember(Name = "colliderType", Order = 10)]
        public ColliderType ColliderType
        {
            get => _colliderType;
            set
            {
                if (SetProperty(ref _colliderType, value, nameof(ColliderType)))
                    OnPropertyChanged(nameof(DisplayName));
            }
        }

        /// <summary>
        /// Ob der Collider ein Trigger ist
        /// </summary>
        [DataMember(Name = "isTrigger", Order = 11)]
        public bool IsTrigger
        {
            get => _isTrigger;
            set => SetProperty(ref _isTrigger, value, nameof(IsTrigger));
        }

        /// <summary>
        /// Zentrum des Colliders relativ zur Entity
        /// </summary>
        [DataMember(Name = "center", Order = 12)]
        public Vector3 Center
        {
            get => _center;
            set => SetProperty(ref _center, value, nameof(Center));
        }

        /// <summary>
        /// Physik-Material
        /// </summary>
        [DataMember(Name = "material", Order = 13)]
        public PhysicsMaterial Material
        {
            get => _material;
            set => SetProperty(ref _material, value, nameof(Material));
        }

        public Collider() : base() { }
        public Collider(GameEntity entity) : base(entity) { }
        public Collider(GameEntity entity, ColliderType type) : base(entity)
        {
            ColliderType = type;
        }
    }

    /// <summary>
    /// Box-Collider
    /// </summary>
    [DataContract(Name = "BoxCollider", Namespace = "")]
    public class BoxCollider : Collider
    {
        private Vector3 _size = Vector3.One;

        public override string DisplayName => "Box Collider";

        /// <summary>
        /// Größe der Box
        /// </summary>
        [DataMember(Name = "size", Order = 20)]
        public Vector3 Size
        {
            get => _size;
            set => SetProperty(ref _size, value, nameof(Size));
        }

        public BoxCollider() : base() { ColliderType = ColliderType.Box; }
        public BoxCollider(GameEntity entity) : base(entity) { ColliderType = ColliderType.Box; }
    }

    /// <summary>
    /// Sphere-Collider
    /// </summary>
    [DataContract(Name = "SphereCollider", Namespace = "")]
    public class SphereCollider : Collider
    {
        private float _radius = 0.5f;

        public override string DisplayName => "Sphere Collider";

        /// <summary>
        /// Radius der Kugel
        /// </summary>
        [DataMember(Name = "radius", Order = 20)]
        public float Radius
        {
            get => _radius;
            set => SetProperty(ref _radius, value, nameof(Radius));
        }

        public SphereCollider() : base() { ColliderType = ColliderType.Sphere; }
        public SphereCollider(GameEntity entity) : base(entity) { ColliderType = ColliderType.Sphere; }
    }

    /// <summary>
    /// Capsule-Collider
    /// </summary>
    [DataContract(Name = "CapsuleCollider", Namespace = "")]
    public class CapsuleCollider : Collider
    {
        private float _radius = 0.5f;
        private float _height = 2f;
        private int _direction = 1; // 0=X, 1=Y, 2=Z

        public override string DisplayName => "Capsule Collider";

        /// <summary>
        /// Radius der Kapsel
        /// </summary>
        [DataMember(Name = "radius", Order = 20)]
        public float Radius
        {
            get => _radius;
            set => SetProperty(ref _radius, value, nameof(Radius));
        }

        /// <summary>
        /// Höhe der Kapsel
        /// </summary>
        [DataMember(Name = "height", Order = 21)]
        public float Height
        {
            get => _height;
            set => SetProperty(ref _height, value, nameof(Height));
        }

        /// <summary>
        /// Ausrichtung der Kapsel (0=X, 1=Y, 2=Z)
        /// </summary>
        [DataMember(Name = "direction", Order = 22)]
        public int Direction
        {
            get => _direction;
            set => SetProperty(ref _direction, value, nameof(Direction));
        }

        public CapsuleCollider() : base() { ColliderType = ColliderType.Capsule; }
        public CapsuleCollider(GameEntity entity) : base(entity) { ColliderType = ColliderType.Capsule; }
    }

    /// <summary>
    /// Mesh-Collider
    /// </summary>
    [DataContract(Name = "MeshCollider", Namespace = "")]
    public class MeshCollider : Collider
    {
        private string _meshPath;
        private bool _convex;

        public override string DisplayName => "Mesh Collider";

        /// <summary>
        /// Pfad zur Mesh-Datei
        /// </summary>
        [DataMember(Name = "meshPath", Order = 20)]
        public string MeshPath
        {
            get => _meshPath;
            set => SetProperty(ref _meshPath, value, nameof(MeshPath));
        }

        /// <summary>
        /// Ob das Mesh konvex sein soll
        /// </summary>
        [DataMember(Name = "convex", Order = 21)]
        public bool Convex
        {
            get => _convex;
            set => SetProperty(ref _convex, value, nameof(Convex));
        }

        public MeshCollider() : base() { ColliderType = ColliderType.Mesh; }
        public MeshCollider(GameEntity entity) : base(entity) { ColliderType = ColliderType.Mesh; }
    }
}
