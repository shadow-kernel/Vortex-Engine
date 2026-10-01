using System;
using System.Runtime.Serialization;

namespace Editor.ECS.Components.AI
{
    /// <summary>
    /// AI senses (GitHub #112) for its entity in play mode, evaluated by <c>PerceptionService</c>:
    /// <b>sight</b> — targets (entities tagged <see cref="TargetTag"/>) inside the <see cref="FieldOfView"/> cone and
    /// <see cref="ViewDistance"/>, confirmed by a line-of-sight raycast from the eye (<see cref="EyeHeight"/> above the
    /// feet) against the colliders, so walls, doors and crates hide the player; <b>hearing</b> — noises reported with
    /// <c>Perception.MakeNoise(position, loudness, instigator)</c> are heard up to <see cref="HearingRange"/> × loudness
    /// (half through walls). Each agent remembers the last seen / heard position for <see cref="MemorySeconds"/>; project
    /// scripts query it (<c>Perception.CanSee</c>, <c>Perception.LastKnownPosition</c> …) and receive
    /// <c>OnMessage("PerceptionSeen" | "PerceptionLost" | "PerceptionHeard", PerceptionEvent)</c>.
    /// Checks are staggered (<see cref="UpdateInterval"/>) so many agents don't all raycast in the same frame.
    /// </summary>
    [DataContract(Name = "AIPerception", Namespace = "")]
    public class AIPerception : Component
    {
        private float _fieldOfView = 110f;
        private float _viewDistance = 15f;
        private float _eyeHeight = 1.6f;
        private float _hearingRange = 12f;
        private float _hearingThreshold = 0.1f;
        private string _targetTag = "Player";
        private float _memorySeconds = 10f;
        private float _updateInterval = 0.1f;
        private float _sightConfirmTime = 0.25f;
        private float _proximityRadius = 1.5f;

        public override string DisplayName => "AI Perception";
        public override string IconCode => "";   // MDL2 "View"
        public override string IconColor => "#C586C0";

        /// <summary>Full opening angle of the vision cone, degrees (360 = all around).</summary>
        [DataMember(Name = "fieldOfView", Order = 10)]
        public float FieldOfView { get => _fieldOfView; set => SetProperty(ref _fieldOfView, Clamp(value, 1f, 360f), nameof(FieldOfView)); }

        /// <summary>How far the agent sees (m).</summary>
        [DataMember(Name = "viewDistance", Order = 11)]
        public float ViewDistance { get => _viewDistance; set => SetProperty(ref _viewDistance, Clamp(value, 0f, 1000f), nameof(ViewDistance)); }

        /// <summary>Eye height above the entity's feet (m) — the origin of the vision cone and the sight rays.</summary>
        [DataMember(Name = "eyeHeight", Order = 12)]
        public float EyeHeight { get => _eyeHeight; set => SetProperty(ref _eyeHeight, Clamp(value, -100f, 100f), nameof(EyeHeight)); }

        /// <summary>Distance (m) at which a noise of loudness 1 is still heard; louder noises carry further.</summary>
        [DataMember(Name = "hearingRange", Order = 13)]
        public float HearingRange { get => _hearingRange; set => SetProperty(ref _hearingRange, Clamp(value, 0f, 1000f), nameof(HearingRange)); }

        /// <summary>Quieter noises are ignored entirely (loudness below this, e.g. 0.1 = sneaking footsteps).</summary>
        [DataMember(Name = "hearingThreshold", Order = 14)]
        public float HearingThreshold { get => _hearingThreshold; set => SetProperty(ref _hearingThreshold, Clamp(value, 0f, 100f), nameof(HearingThreshold)); }

        /// <summary>Tag of the entities this agent looks for ("Player"). Scripts can add more targets at runtime.</summary>
        [DataMember(Name = "targetTag", Order = 15)]
        public string TargetTag { get => _targetTag ?? ""; set => SetProperty(ref _targetTag, value ?? "", nameof(TargetTag)); }

        /// <summary>How long a sighting / noise stays "fresh" (s) — the window a search behaviour works with.</summary>
        [DataMember(Name = "memorySeconds", Order = 16)]
        public float MemorySeconds { get => _memorySeconds; set => SetProperty(ref _memorySeconds, Clamp(value, 0f, 3600f), nameof(MemorySeconds)); }

        /// <summary>Seconds between two sight checks of this agent (staggered across agents).</summary>
        [DataMember(Name = "updateInterval", Order = 17)]
        public float UpdateInterval { get => _updateInterval; set => SetProperty(ref _updateInterval, Clamp(value, 0f, 10f), nameof(UpdateInterval)); }

        /// <summary>A target must stay visible this long (s) before it counts as seen — a glimpse around a corner is not
        /// enough. 0 = instantly.</summary>
        [DataMember(Name = "sightConfirmTime", Order = 18)]
        public float SightConfirmTime { get => _sightConfirmTime; set => SetProperty(ref _sightConfirmTime, Clamp(value, 0f, 60f), nameof(SightConfirmTime)); }

        /// <summary>Targets closer than this (m) are sensed all around, cone or not (bumping into the monster), still
        /// needing line of sight.</summary>
        [DataMember(Name = "proximityRadius", Order = 19)]
        public float ProximityRadius { get => _proximityRadius; set => SetProperty(ref _proximityRadius, Clamp(value, 0f, 100f), nameof(ProximityRadius)); }

        public AIPerception() : base() { }
        public AIPerception(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingPerception(StreamingContext context)
        {
            _fieldOfView = 110f; _viewDistance = 15f; _eyeHeight = 1.6f; _hearingRange = 12f; _hearingThreshold = 0.1f;
            _targetTag = "Player"; _memorySeconds = 10f; _updateInterval = 0.1f; _sightConfirmTime = 0.25f; _proximityRadius = 1.5f;
        }

        private static float Clamp(float v, float lo, float hi) => float.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }
}
