using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Editor.ECS.Components.AI
{
    /// <summary>How a patrol continues after its last waypoint.</summary>
    public enum PatrolMode
    {
        /// <summary>0, 1, 2, …, last, 0, 1, … (a closed round).</summary>
        Loop,
        /// <summary>0, 1, …, last, last-1, …, 0, 1, … (back and forth).</summary>
        PingPong,
        /// <summary>0, 1, …, last, then stay at the last waypoint.</summary>
        Once
    }

    /// <summary>
    /// A patrol route (GitHub #114): an ordered list of waypoints a NavAgent walks (driven by a project script —
    /// <c>PatrolPath.Next</c> / <c>Navigation.SetDestination</c>, see the Horror Monster sample). Waypoints are either the
    /// entity's CHILD entities in hierarchy order (<see cref="UseChildren"/>: drag them in the viewport with the move
    /// gizmo) or the <see cref="Points"/> list, stored as offsets in the path entity's LOCAL space so the whole route
    /// moves / rotates with it. <see cref="WaitTime"/> is the pause a patrol script makes at each waypoint.
    /// </summary>
    [DataContract(Name = "PatrolPath", Namespace = "")]
    public class PatrolPath : Component
    {
        private PatrolMode _mode = PatrolMode.Loop;
        private bool _useChildren = true;
        private List<Vector3> _points = new List<Vector3>();
        private float _waitTime = 1f;
        private bool _showInGame;

        public override string DisplayName => "Patrol Path";
        public override string IconCode => "";   // MDL2 "MapPin"
        public override string IconColor => "#C586C0";

        [DataMember(Name = "mode", Order = 10)]
        public PatrolMode Mode { get => _mode; set => SetProperty(ref _mode, value, nameof(Mode)); }

        /// <summary>True: the child entities (in hierarchy order) are the waypoints when there are any; the point list
        /// is used otherwise.</summary>
        [DataMember(Name = "useChildren", Order = 11)]
        public bool UseChildren { get => _useChildren; set => SetProperty(ref _useChildren, value, nameof(UseChildren)); }

        /// <summary>Waypoints as local-space offsets from the path entity (scaled / rotated / moved with it). Assign a new
        /// list to change it (edits through the setter are undoable).</summary>
        [DataMember(Name = "points", Order = 12)]
        public List<Vector3> Points
        {
            get => _points ?? (_points = new List<Vector3>());
            set => SetProperty(ref _points, value != null ? new List<Vector3>(value) : new List<Vector3>(), nameof(Points));
        }

        /// <summary>Seconds a patrolling agent waits at each waypoint (read by patrol scripts).</summary>
        [DataMember(Name = "waitTime", Order = 13)]
        public float WaitTime { get => _waitTime; set => SetProperty(ref _waitTime, float.IsNaN(value) ? 0f : Math.Max(0f, value), nameof(WaitTime)); }

        /// <summary>Draw the route in play mode too (debug); in the editor it is drawn while the entity is selected.</summary>
        [DataMember(Name = "showInGame", Order = 14)]
        public bool ShowInGame { get => _showInGame; set => SetProperty(ref _showInGame, value, nameof(ShowInGame)); }

        public PatrolPath() : base() { }
        public PatrolPath(GameEntity entity) : base(entity) { }

        [OnDeserializing]
        private void OnDeserializingPatrolPath(StreamingContext context)
        {
            _mode = PatrolMode.Loop; _useChildren = true; _points = new List<Vector3>(); _waitTime = 1f; _showInGame = false;
        }

        /// <summary>True when the waypoints come from the child entities right now.</summary>
        public bool UsesChildWaypoints => _useChildren && Entity?.Children != null && Entity.Children.Count > 0;

        /// <summary>Number of waypoints (children or points).</summary>
        public int Count => UsesChildWaypoints ? Entity.Children.Count : Points.Count;

        /// <summary>The waypoint after <paramref name="index"/> for this path's <see cref="Mode"/>. <paramref name="direction"/>
        /// (+1 / -1) carries the ping-pong direction between calls; start with 1. Returns -1 for an empty path; for
        /// <see cref="PatrolMode.Once"/> the last index repeats at the end.</summary>
        public int Next(int index, ref int direction) => NextIndex(Mode, Count, index, ref direction);

        /// <summary>The stepping rule on its own (shared with the script API).</summary>
        public static int NextIndex(PatrolMode mode, int count, int index, ref int direction)
        {
            if (count <= 0) return -1;
            if (count == 1) return 0;
            if (direction == 0) direction = 1;
            if (index < 0 || index >= count) return 0;
            switch (mode)
            {
                case PatrolMode.PingPong:
                    {
                        int n = index + direction;
                        if (n >= count) { direction = -1; n = count - 2; }
                        else if (n < 0) { direction = 1; n = 1; }
                        return n;
                    }
                case PatrolMode.Once:
                    return Math.Min(index + 1, count - 1);
                default:
                    return (index + 1) % count;
            }
        }
    }
}
