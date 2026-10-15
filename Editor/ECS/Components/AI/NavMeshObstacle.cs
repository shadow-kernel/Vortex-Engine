using System;
using System.Runtime.Serialization;

namespace Editor.ECS.Components.AI
{
    /// <summary>The shape of a <see cref="NavMeshObstacle"/>.</summary>
    public enum NavObstacleShape
    {
        /// <summary>An oriented box (<see cref="NavMeshObstacle.Size"/>), turned with the entity.</summary>
        Box,
        /// <summary>An upright cylinder (<see cref="NavMeshObstacle.Radius"/>, <see cref="NavMeshObstacle.Height"/>).</summary>
        Cylinder
    }

    /// <summary>
    /// A dynamic navmesh obstacle (GitHub #435) — doors, parked cars, rubble, a barricade. In play mode it changes where Nav
    /// Agents can go without a rebake. Carving (the default) cuts its footprint, grown by the agent radius, out of the
    /// navmesh: paths go around it and agents re-plan when it appears in their way. The tiles it covers are rebuilt in the
    /// background from the scene geometry, so the first carve lands a few milliseconds later; switching it off and on again
    /// (the component, or <c>Navigation.SetObstacleEnabled</c>) is instant — a door. Not carving, the agents only steer
    /// around it like around another agent (a moving cart, a car that just parked). The obstacle's own entity (and its
    /// children) are left out of the navmesh bake, the component switched on or off: bake with the door open or closed, it
    /// is the obstacle that decides — switched off (a door that starts open) it blocks nothing.
    /// </summary>
    [DataContract(Name = "NavMeshObstacle", Namespace = "")]
    public class NavMeshObstacle : Component
    {
        private NavObstacleShape _shape = NavObstacleShape.Box;
        private Vector3 _center = new Vector3(0f, 0f, 0f);
        private Vector3 _size = new Vector3(1f, 1f, 1f);
        private float _radius = 0.5f;
        private float _height = 2f;
        private bool _carve = true;
        private bool _carveOnlyStationary = true;
        private float _moveThreshold = 0.1f;
        private float _timeToStationary = 0.5f;

        public override string DisplayName => "Nav Mesh Obstacle";
        public override string IconCode => "";   // MDL2 "Blocked"
        public override string IconColor => "#C586C0";

        /// <summary>Box (oriented with the entity) or upright cylinder.</summary>
        [DataMember(Name = "shape", Order = 10)]
        public NavObstacleShape Shape { get => _shape; set => SetProperty(ref _shape, value, nameof(Shape)); }

        /// <summary>The shape's centre in the entity's local space (scaled and turned with it).</summary>
        [DataMember(Name = "center", Order = 11)]
        public Vector3 Center { get => _center; set => SetProperty(ref _center, value, nameof(Center)); }

        /// <summary>Box: its size in the entity's local space (scaled with it), metres.</summary>
        [DataMember(Name = "size", Order = 12)]
        public Vector3 Size
        {
            get => _size;
            set => SetProperty(ref _size, new Vector3(Clamp(value.X, 0.01f, 10000f), Clamp(value.Y, 0.01f, 10000f), Clamp(value.Z, 0.01f, 10000f)), nameof(Size));
        }

        /// <summary>Cylinder: its radius, metres (scaled by the entity's larger horizontal scale).</summary>
        [DataMember(Name = "radius", Order = 13)]
        public float Radius { get => _radius; set => SetProperty(ref _radius, Clamp(value, 0.01f, 10000f), nameof(Radius)); }

        /// <summary>Cylinder: its height, metres (scaled by the entity's vertical scale).</summary>
        [DataMember(Name = "height", Order = 14)]
        public float Height { get => _height; set => SetProperty(ref _height, Clamp(value, 0.01f, 10000f), nameof(Height)); }

        /// <summary>Cut the obstacle out of the navmesh (paths go around it). Off = agents only steer around it, the
        /// navmesh stays as baked.</summary>
        [DataMember(Name = "carve", Order = 15)]
        public bool Carve { get => _carve; set => SetProperty(ref _carve, value, nameof(Carve)); }

        /// <summary>While it moves it is only steered around; it carves once it has stood still for
        /// <see cref="TimeToStationary"/> seconds (no tile rebuilds every frame for a pushed cart). Off = re-carved whenever it
        /// moved more than <see cref="MoveThreshold"/>.</summary>
        [DataMember(Name = "carveOnlyStationary", Order = 16)]
        public bool CarveOnlyStationary { get => _carveOnlyStationary; set => SetProperty(ref _carveOnlyStationary, value, nameof(CarveOnlyStationary)); }

        /// <summary>How far (m) it has to move — or how far a box corner, turning — before it counts as moving.</summary>
        [DataMember(Name = "moveThreshold", Order = 17)]
        public float MoveThreshold { get => _moveThreshold; set => SetProperty(ref _moveThreshold, Clamp(value, 0.001f, 100f), nameof(MoveThreshold)); }

        /// <summary>Seconds it has to stand still before it carves again (with Carve Only Stationary).</summary>
        [DataMember(Name = "timeToStationary", Order = 18)]
        public float TimeToStationary { get => _timeToStationary; set => SetProperty(ref _timeToStationary, Clamp(value, 0f, 100f), nameof(TimeToStationary)); }

        public NavMeshObstacle() : base() { }
        public NavMeshObstacle(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingNavMeshObstacle(StreamingContext context)
        {
            // DataContract skips constructors: fields missing from a file keep these defaults.
            _shape = NavObstacleShape.Box; _center = new Vector3(0f, 0f, 0f); _size = new Vector3(1f, 1f, 1f);
            _radius = 0.5f; _height = 2f; _carve = true; _carveOnlyStationary = true; _moveThreshold = 0.1f; _timeToStationary = 0.5f;
        }

        private static float Clamp(float v, float lo, float hi) => float.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }
}
