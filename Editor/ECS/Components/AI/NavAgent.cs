using System;
using System.Runtime.Serialization;

namespace Editor.ECS.Components.AI
{
    /// <summary>
    /// Navigation agent (GitHub #110): moves its entity over the scene's baked navmesh (Recast/Detour) in play mode.
    /// A script gives it somewhere to go — <c>Navigation.SetDestination(EntityId, target)</c> — and the agent plans a
    /// path around the level geometry, steers along it with local avoidance of other agents (DetourCrowd), resolves its
    /// capsule against the colliders (the same collide-and-slide as the player) and turns to face where it walks.
    /// The entity's position is its FEET (plus <see cref="BaseOffset"/>). Runtime changes made by scripts (speed
    /// overrides, stop / resume) live in <c>NavigationService</c>, never in this component, so play stays
    /// non-destructive. Bake the navmesh first: Window ▸ Navigation.
    /// </summary>
    [DataContract(Name = "NavAgent", Namespace = "")]
    public class NavAgent : Component
    {
        private float _speed = 3.5f;
        private float _acceleration = 8f;
        private float _angularSpeed = 360f;
        private float _radius = 0.4f;
        private float _height = 1.8f;
        private float _stoppingDistance = 0.1f;
        private bool _autoBraking = true;
        private int _avoidancePriority = 50;
        private float _baseOffset;
        private bool _updateRotation = true;
        private bool _resolveCollisions = true;

        public override string DisplayName => "Nav Agent";
        public override string IconCode => "";   // MDL2 "Walk"
        public override string IconColor => "#C586C0";

        /// <summary>Top speed in m/s (a walking monster ~1.5, running ~4.5).</summary>
        [DataMember(Name = "speed", Order = 10)]
        public float Speed { get => _speed; set => SetProperty(ref _speed, Clamp(value, 0f, 100f), nameof(Speed)); }

        /// <summary>How fast it reaches its speed / turns its velocity, m/s².</summary>
        [DataMember(Name = "acceleration", Order = 11)]
        public float Acceleration { get => _acceleration; set => SetProperty(ref _acceleration, Clamp(value, 0f, 1000f), nameof(Acceleration)); }

        /// <summary>How fast the entity turns to face its walking direction, degrees per second (0 = never turns).</summary>
        [DataMember(Name = "angularSpeed", Order = 12)]
        public float AngularSpeed { get => _angularSpeed; set => SetProperty(ref _angularSpeed, Clamp(value, 0f, 100000f), nameof(AngularSpeed)); }

        /// <summary>Radius of the agent's capsule (avoidance + collision). The navmesh keeps its baked radius off walls.</summary>
        [DataMember(Name = "radius", Order = 13)]
        public float Radius { get => _radius; set => SetProperty(ref _radius, Clamp(value, 0.05f, 10f), nameof(Radius)); }

        /// <summary>Height of the agent's capsule (feet to head).</summary>
        [DataMember(Name = "height", Order = 14)]
        public float Height { get => _height; set => SetProperty(ref _height, Clamp(value, 0.1f, 20f), nameof(Height)); }

        /// <summary>The agent stops this far from its destination (m).</summary>
        [DataMember(Name = "stoppingDistance", Order = 15)]
        public float StoppingDistance { get => _stoppingDistance; set => SetProperty(ref _stoppingDistance, Clamp(value, 0f, 1000f), nameof(StoppingDistance)); }

        /// <summary>Slow down when approaching the destination. Off = full speed until the stop (patrol waypoints).</summary>
        [DataMember(Name = "autoBraking", Order = 16)]
        public bool AutoBraking { get => _autoBraking; set => SetProperty(ref _autoBraking, value, nameof(AutoBraking)); }

        /// <summary>0 (most important) .. 99 (least): lower-priority agents give way more (separation / avoidance effort).</summary>
        [DataMember(Name = "avoidancePriority", Order = 17)]
        public int AvoidancePriority { get => _avoidancePriority; set => SetProperty(ref _avoidancePriority, Math.Max(0, Math.Min(99, value)), nameof(AvoidancePriority)); }

        /// <summary>Height of the entity's pivot above its feet (m) — e.g. 1 for a 2 m capsule primitive pivoted at its centre.</summary>
        [DataMember(Name = "baseOffset", Order = 18)]
        public float BaseOffset { get => _baseOffset; set => SetProperty(ref _baseOffset, Clamp(value, -100f, 100f), nameof(BaseOffset)); }

        /// <summary>Turn the entity (yaw) to face its movement. Off = scripts / animation control the rotation.</summary>
        [DataMember(Name = "updateRotation", Order = 19)]
        public bool UpdateRotation { get => _updateRotation; set => SetProperty(ref _updateRotation, value, nameof(UpdateRotation)); }

        /// <summary>Resolve the agent's capsule against colliders (props, doors, the player) with the character controller;
        /// it also blocks the player like another character. Off = glide on the navmesh only.</summary>
        [DataMember(Name = "resolveCollisions", Order = 20)]
        public bool ResolveCollisions { get => _resolveCollisions; set => SetProperty(ref _resolveCollisions, value, nameof(ResolveCollisions)); }

        public NavAgent() : base() { }
        public NavAgent(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingNavAgent(StreamingContext context)
        {
            // DataContract skips constructors: fields missing from older files keep these defaults.
            _speed = 3.5f; _acceleration = 8f; _angularSpeed = 360f; _radius = 0.4f; _height = 1.8f;
            _stoppingDistance = 0.1f; _autoBraking = true; _avoidancePriority = 50; _baseOffset = 0f;
            _updateRotation = true; _resolveCollisions = true;
        }

        private static float Clamp(float v, float lo, float hi) => float.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }
}
