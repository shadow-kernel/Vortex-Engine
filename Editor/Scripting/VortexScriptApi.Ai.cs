// AI & Navigation scripting API (GitHub #108 epic: #110 NavAgent + pathfinding, #112 perception, #114 patrol paths).
// Gameplay stays in PROJECT scripts: the engine provides the primitives below, the game writes the brain (see the
// Horror Monster sample). Entities are addressed by their script handle — EntityId, Scene.Find("Monster"),
// TriggerHit.EntityId, RaycastHit.EntityId. Every call is safe without a baked navmesh / without the component: it
// returns false / 0 / an empty result instead of throwing. Examples are C# 5 (they compile in every editor).
using System;
using System.Collections.Generic;
using Editor.Core.Services.AI;
using Editor.Scripting;
using EcsVec = Editor.ECS.Vector3;

namespace Vortex
{
    /// <summary>State of a path (query result or an agent's current path).</summary>
    public enum NavPathStatus
    {
        /// <summary>No navmesh, or the start / end is not on (near) it.</summary>
        Invalid = 0,
        /// <summary>The path reaches the destination.</summary>
        Complete = 1,
        /// <summary>The destination is unreachable — the path ends at the closest reachable point.</summary>
        Partial = 2,
        /// <summary>The agent's path is still being computed (SetDestination returns immediately).</summary>
        Pending = 3
    }

    /// <summary>
    /// Navmesh pathfinding + Nav Agent control. Give the monster entity a <b>Nav Agent</b> component, bake the navmesh
    /// (Window ▸ Navigation ▸ Bake), then drive it from its script:
    /// <code>
    /// public class Hunter : VortexBehaviour
    /// {
    ///     private long _player;
    ///     public override void Start() { _player = Scene.Find("Player"); }
    ///     public override void Update(float dt)
    ///     {
    ///         Navigation.SetDestination(EntityId, Scene.WorldPositionOf(_player));   // re-plans cheaply every frame
    ///         if (Navigation.HasArrived(EntityId)) { /* attack */ }
    ///     }
    /// }
    /// </code>
    /// Queries without an agent: <c>Vector3[] corners; if (Navigation.CalculatePath(a, b, out corners) == NavPathStatus.Complete) { … }</c>
    /// </summary>
    public static class Navigation
    {
        private static Editor.ECS.GameEntity E(long entity)
        {
            AiRuntime.EnsureRunning();
            return ScriptRuntime.Instance.FindEntityByHandle(entity);
        }

        private static EcsVec V(Vector3 v) { return new EcsVec(v.X, v.Y, v.Z); }
        private static Vector3 V(EcsVec v) { return new Vector3(v.X, v.Y, v.Z); }

        // ---- agents ----

        /// <summary>Send the entity's Nav Agent to <paramref name="point"/>. Returns immediately (the path is computed over
        /// the next frames); false when the entity has no enabled Nav Agent, no navmesh is baked, or the point is nowhere
        /// near the navmesh. Calling it again with a new point (a moving player) re-plans.</summary>
        public static bool SetDestination(long entity, Vector3 point) { return NavigationService.SetDestination(E(entity), V(point)); }

        /// <summary>Stop the agent (the destination is cleared; it slows to a halt).</summary>
        public static void Stop(long entity) { NavigationService.Stop(E(entity)); }

        /// <summary>Freeze the agent in place without losing its destination (true) / let it continue (false).</summary>
        public static void SetPaused(long entity, bool paused) { NavigationService.SetPaused(E(entity), paused); }

        /// <summary>True while the agent has somewhere to go or is still moving.</summary>
        public static bool IsMoving(long entity) { return NavigationService.IsMoving(E(entity)); }

        /// <summary>True once the agent got within its Stopping Distance of the last destination.</summary>
        public static bool HasArrived(long entity) { return NavigationService.HasArrived(E(entity)); }

        /// <summary>True while a destination is set (arrival or Stop clears it).</summary>
        public static bool HasDestination(long entity) { return NavigationService.HasDestination(E(entity)); }
        /// <summary>Draw the navmesh and / or the agents' paths as overlays in the game view (the Navigation window's
        /// toggles, from a script — a feature tour key, a debug menu). Off again with (false, false).</summary>
        public static void DebugDraw(bool navmesh, bool paths) { NavigationService.ShowNavMesh = navmesh; NavigationService.ShowAgentPaths = paths; }

        /// <summary>Metres left along the path; 0 after arriving, -1 without a path (infinity while the path is pending).</summary>
        public static float RemainingDistance(long entity) { return NavigationService.RemainingDistance(E(entity)); }

        /// <summary>Complete / Partial (unreachable, heading to the closest point) / Pending / Invalid.</summary>
        public static NavPathStatus PathStatus(long entity) { return (NavPathStatus)(int)NavigationService.GetPathStatus(E(entity)); }

        /// <summary>True while the agent's path is still being computed.</summary>
        public static bool PathPending(long entity) { return PathStatus(entity) == NavPathStatus.Pending; }

        /// <summary>The agent's current velocity (m/s).</summary>
        public static Vector3 Velocity(long entity) { return V(NavigationService.GetVelocity(E(entity))); }

        /// <summary>The destination set by SetDestination (Zero without one).</summary>
        public static Vector3 Destination(long entity) { return V(NavigationService.GetDestination(E(entity))); }

        /// <summary>The next path corner the agent walks towards — aim the head / flashlight there.</summary>
        public static Vector3 SteeringTarget(long entity) { return V(NavigationService.GetSteeringTarget(E(entity))); }

        /// <summary>The corners of the agent's remaining path (empty without one).</summary>
        public static Vector3[] GetPath(long entity)
        {
            var list = NavigationService.GetAgentPath(E(entity));
            var result = new Vector3[list.Count];
            for (int i = 0; i < list.Count; i++) result[i] = V(list[i]);
            return result;
        }

        /// <summary>Teleport the agent (its path is cleared). False when the point is not on / near the navmesh.</summary>
        public static bool Warp(long entity, Vector3 position) { return NavigationService.Warp(E(entity), V(position)); }

        /// <summary>Override the agent's speed at runtime (m/s) — walk 1.5 on patrol, run 5 in a chase. Negative = back to the
        /// component's Speed. The component itself is not changed.</summary>
        public static void SetSpeed(long entity, float speed) { NavigationService.SetSpeedOverride(E(entity), speed); }

        /// <summary>The agent's current top speed (override or component).</summary>
        public static float GetSpeed(long entity) { return NavigationService.GetSpeed(E(entity)); }

        // ---- queries ----

        /// <summary>True when a navmesh is loaded for the running scene.</summary>
        public static bool IsAvailable { get { AiRuntime.EnsureRunning(); return NavigationService.IsLoaded; } }

        /// <summary>A path from <paramref name="from"/> to <paramref name="to"/> as corner points (start and end included).
        /// <c>Vector3[] corners; NavPathStatus s = Navigation.CalculatePath(a, b, out corners);</c></summary>
        public static NavPathStatus CalculatePath(Vector3 from, Vector3 to, out Vector3[] corners)
        {
            AiRuntime.EnsureRunning();
            var list = new List<EcsVec>();
            var status = NavigationService.CalculatePath(V(from), V(to), list);
            corners = new Vector3[list.Count];
            for (int i = 0; i < list.Count; i++) corners[i] = V(list[i]);
            return (NavPathStatus)(int)status;
        }

        /// <summary>Walking distance from <paramref name="from"/> to <paramref name="to"/> along the navmesh; -1 when there is
        /// no complete path. (The straight-line distance is Vector3.Distance.)</summary>
        public static float PathLength(Vector3 from, Vector3 to)
        {
            Vector3[] c;
            if (CalculatePath(from, to, out c) != NavPathStatus.Complete) return -1f;
            float len = 0f;
            for (int i = 1; i < c.Length; i++) len += Vector3.Distance(c[i - 1], c[i]);
            return len;
        }

        /// <summary>The closest navmesh point within <paramref name="maxDistance"/> — snap a spawn / target onto walkable ground.</summary>
        public static bool SamplePosition(Vector3 position, float maxDistance, out Vector3 hit)
        {
            AiRuntime.EnsureRunning();
            EcsVec h;
            bool ok = NavigationService.SamplePosition(V(position), maxDistance, out h);
            hit = V(h);
            return ok;
        }

        /// <summary>A random point reachable from <paramref name="center"/> within <paramref name="radius"/> (wander / search).</summary>
        public static bool RandomPoint(Vector3 center, float radius, out Vector3 point)
        {
            AiRuntime.EnsureRunning();
            EcsVec p;
            bool ok = NavigationService.RandomPointAround(V(center), radius, out p);
            point = V(p);
            return ok;
        }

        /// <summary>A random point anywhere on the navmesh.</summary>
        public static bool RandomPoint(out Vector3 point)
        {
            AiRuntime.EnsureRunning();
            EcsVec p;
            bool ok = NavigationService.RandomPoint(out p);
            point = V(p);
            return ok;
        }

        /// <summary>Walk the navmesh from <paramref name="from"/> towards <paramref name="to"/>: true when an edge of the
        /// walkable area (a wall, a drop) is in the way; <paramref name="hit"/> = where. "Can I walk straight there?"</summary>
        public static bool Raycast(Vector3 from, Vector3 to, out Vector3 hit)
        {
            AiRuntime.EnsureRunning();
            EcsVec h, n;
            bool blocked = NavigationService.Raycast(V(from), V(to), out h, out n);
            hit = V(h);
            return blocked;
        }

        /// <summary>Draw the navmesh + the agents' paths over the game (debug).</summary>
        public static bool ShowDebug
        {
            get { return NavigationService.ShowNavMesh; }
            set { NavigationService.ShowNavMesh = value; NavigationService.ShowAgentPaths = value; AiRuntime.EnsureRunning(); }
        }
    }

    /// <summary>What a perceiving agent's behaviour receives in <c>OnMessage(message, arg)</c> for the messages
    /// <see cref="Perception.SeenMessage"/>, <see cref="Perception.LostMessage"/> and <see cref="Perception.HeardMessage"/>
    /// (also published on the event bus: <c>Events.Subscribe&lt;PerceptionEvent&gt;(OnPerceived)</c>).</summary>
    public sealed class PerceptionEvent
    {
        /// <summary>The perceiving agent.</summary>
        public long Agent;
        /// <summary>The seen target / the noise's instigator (0 = anonymous noise).</summary>
        public long Source;
        public string SourceName = "";
        public string SourceTag = "";
        /// <summary>"PerceptionSeen", "PerceptionLost" or "PerceptionHeard".</summary>
        public string Message = "";
        /// <summary>"Sight" or "Hearing".</summary>
        public string Sense = "";
        /// <summary>Where the target was seen / the noise was made.</summary>
        public Vector3 Position;
        /// <summary>Loudness of a noise (0 for sight).</summary>
        public float Loudness;
    }

    /// <summary>
    /// AI senses. Give the monster an <b>AI Perception</b> component (field of view, view distance, eye height, hearing
    /// range, target tag "Player"); it then sees targets inside its vision cone when nothing blocks the line of sight
    /// (walls, doors, crates — crouch behind cover!) and hears <see cref="MakeNoise"/>. React in the monster's script:
    /// <code>
    /// public override void OnMessage(string message, object arg)
    /// {
    ///     PerceptionEvent e = arg as PerceptionEvent;
    ///     if (message == Perception.SeenMessage) _state = "Chase";
    ///     else if (message == Perception.HeardMessage) { _investigate = e.Position; _state = "Investigate"; }
    /// }
    /// </code>
    /// or poll: <c>if (Perception.CanSee(EntityId)) { … }</c>, <c>Vector3 p; if (Perception.LastKnownPosition(EntityId, out p)) { … }</c>.
    /// The player's scripts make the noise: <c>Perception.MakeNoise(Position, running ? 0.8f : 0.3f, EntityId);</c>
    /// </summary>
    public static class Perception
    {
        /// <summary>Draw every AI Perception's vision cone and its last seen / heard markers as overlays in the game view.</summary>
        public static void DebugDraw(bool on) { PerceptionService.ShowDebug = on; }
        public const string SeenMessage = "PerceptionSeen";
        public const string LostMessage = "PerceptionLost";
        public const string HeardMessage = "PerceptionHeard";

        private static Editor.ECS.GameEntity E(long entity)
        {
            AiRuntime.EnsureRunning();
            return entity != 0 ? ScriptRuntime.Instance.FindEntityByHandle(entity) : null;
        }

        private static EcsVec V(Vector3 v) { return new EcsVec(v.X, v.Y, v.Z); }
        private static Vector3 V(EcsVec v) { return new Vector3(v.X, v.Y, v.Z); }

        /// <summary>Report a noise at <paramref name="position"/>: loudness 1 is heard at an agent's full Hearing Range
        /// (sneaking 0.1-0.2, walking 0.3, running 0.7, a slammed door 1, a gunshot 3). <paramref name="instigator"/> = who
        /// made it (0 = unknown). Returns how many agents heard it.</summary>
        public static int MakeNoise(Vector3 position, float loudness, long instigator = 0)
        {
            return PerceptionService.MakeNoise(V(position), loudness, E(instigator));
        }

        /// <summary>True while the agent sees any of its targets (confirmed sighting).</summary>
        public static bool CanSee(long agent) { return PerceptionService.CanSee(E(agent), null); }

        /// <summary>True while the agent sees <paramref name="target"/>.</summary>
        public static bool CanSee(long agent, long target) { var t = E(target); return t != null && PerceptionService.CanSee(E(agent), t); }

        /// <summary>The target the agent sees right now (0 = none).</summary>
        public static long VisibleTarget(long agent) { return Handle(PerceptionService.VisibleTarget(E(agent))); }

        /// <summary>Did the agent see a target within the last <paramref name="maxAge"/> seconds (default: its memory)?</summary>
        public static bool HasSeen(long agent, float maxAge = -1f)
        {
            EcsVec p; float age; Editor.ECS.GameEntity t;
            return PerceptionService.TryGetLastSeen(E(agent), maxAge, out p, out age, out t);
        }

        /// <summary>Where the agent last saw a target (within its memory).</summary>
        public static bool LastSeenPosition(long agent, out Vector3 position)
        {
            EcsVec p; float age; Editor.ECS.GameEntity t;
            bool ok = PerceptionService.TryGetLastSeen(E(agent), -1f, out p, out age, out t);
            position = V(p);
            return ok;
        }

        /// <summary>Did the agent hear a noise within the last <paramref name="maxAge"/> seconds (default: its memory)?</summary>
        public static bool HasHeard(long agent, float maxAge = -1f)
        {
            EcsVec p; float age, loud; Editor.ECS.GameEntity s;
            return PerceptionService.TryGetLastHeard(E(agent), maxAge, out p, out age, out loud, out s);
        }

        /// <summary>Where the agent last heard a noise (within its memory).</summary>
        public static bool LastHeardPosition(long agent, out Vector3 position)
        {
            EcsVec p; float age, loud; Editor.ECS.GameEntity s;
            bool ok = PerceptionService.TryGetLastHeard(E(agent), -1f, out p, out age, out loud, out s);
            position = V(p);
            return ok;
        }

        /// <summary>The freshest seen-or-heard position in the agent's memory — where a search starts.</summary>
        public static bool LastKnownPosition(long agent, out Vector3 position)
        {
            EcsVec p;
            bool ok = PerceptionService.TryGetLastKnownPosition(E(agent), out p);
            position = V(p);
            return ok;
        }

        /// <summary>Seconds since the agent last saw a target (infinity when never).</summary>
        public static float TimeSinceSeen(long agent)
        {
            EcsVec p; float age; Editor.ECS.GameEntity t;
            PerceptionService.TryGetLastSeen(E(agent), float.PositiveInfinity, out p, out age, out t);
            return age;
        }

        /// <summary>Seconds since the agent last heard a noise (infinity when never).</summary>
        public static float TimeSinceHeard(long agent)
        {
            EcsVec p; float age, loud; Editor.ECS.GameEntity s;
            PerceptionService.TryGetLastHeard(E(agent), float.PositiveInfinity, out p, out age, out loud, out s);
            return age;
        }

        /// <summary>Clear the agent's memory (a search gave up — back to patrol).</summary>
        public static void Forget(long agent) { PerceptionService.Forget(E(agent)); }

        /// <summary>Hide a target from every agent (the player climbs into a locker) / reveal it again.</summary>
        public static void SetHidden(long target, bool hidden) { PerceptionService.SetHidden(E(target), hidden); }

        /// <summary>The height the sight rays aim at on a target: 1.7 standing (default), ~1.0 crouched.</summary>
        public static void SetTargetHeight(long target, float height) { PerceptionService.SetTargetHeight(E(target), height); }

        /// <summary>Let an agent also look for <paramref name="target"/> (besides its Target Tag).</summary>
        public static void AddTarget(long agent, long target) { PerceptionService.AddTarget(E(agent), E(target)); }

        /// <summary>Geometry only (no line of sight): is the point inside the agent's vision cone and view distance?</summary>
        public static bool IsInViewCone(long agent, Vector3 point) { return PerceptionService.IsInViewCone(E(agent), V(point)); }

        /// <summary>Nothing solid between the two points (hits on <paramref name="ignore"/> and its children are skipped).</summary>
        public static bool HasLineOfSight(Vector3 from, Vector3 to, long ignore = 0)
        {
            return PerceptionService.HasLineOfSight(V(from), V(to), E(ignore), null);
        }

        /// <summary>Draw the agents' vision cones over the game (debug).</summary>
        public static bool ShowDebug
        {
            get { return PerceptionService.ShowDebug; }
            set { PerceptionService.ShowDebug = value; AiRuntime.EnsureRunning(); }
        }

        private static long Handle(Editor.ECS.GameEntity e) { return e != null ? ScriptRuntime.Instance.HandleForEntity(e) : 0; }

        /// <summary>The runtime forwards stimuli here: OnMessage on the agent's behaviour + the event bus.</summary>
        internal static void Deliver(PerceptionStimulus s)
        {
            var sr = ScriptRuntime.Instance;
            var evt = new PerceptionEvent
            {
                Agent = Handle(s.Agent),
                Source = Handle(s.Source),
                SourceName = s.Source != null ? (s.Source.Name ?? "") : "",
                SourceTag = s.Source != null ? (s.Source.Tag ?? "") : "",
                Message = s.Kind ?? "",
                Sense = s.Sense == PerceptionSense.Hearing ? "Hearing" : "Sight",
                Position = V(s.Position),
                Loudness = s.Loudness
            };
            if (evt.Agent != 0) sr.SendEntityMessage(evt.Agent, evt.Message, evt);
            try { Events.Publish(evt); } catch { }
        }
    }

    /// <summary>
    /// Patrol routes: an entity with a <b>Patrol Path</b> component (child entities = waypoints, in hierarchy order, or its
    /// point list) is addressed by its handle. Walk it with a Nav Agent:
    /// <code>
    /// long _route; int _wp; int _dir = 1;
    /// public override void Start() { _route = Scene.Find("PatrolRoute"); Navigation.SetDestination(EntityId, PatrolPath.GetWaypoint(_route, 0)); }
    /// public override void Update(float dt)
    /// {
    ///     if (Navigation.HasArrived(EntityId))
    ///     {
    ///         _wp = PatrolPath.Next(_route, _wp, ref _dir);
    ///         Navigation.SetDestination(EntityId, PatrolPath.GetWaypoint(_route, _wp));
    ///     }
    /// }
    /// </code>
    /// </summary>
    public static class PatrolPath
    {
        private static Editor.ECS.Components.AI.PatrolPath P(long path)
        {
            AiRuntime.EnsureRunning();
            var e = ScriptRuntime.Instance.FindEntityByHandle(path);
            return e != null ? e.GetComponent<Editor.ECS.Components.AI.PatrolPath>() : null;
        }

        /// <summary>Number of waypoints (0 = no Patrol Path on that entity).</summary>
        public static int Count(long path) { var p = P(path); return p != null ? p.Count : 0; }

        /// <summary>World position of waypoint <paramref name="index"/> (wrapped into range; Zero for an empty route).</summary>
        public static Vector3 GetWaypoint(long path, int index)
        {
            EcsVec v;
            return PatrolPathService.TryGetWaypoint(P(path), index, out v) ? new Vector3(v.X, v.Y, v.Z) : Vector3.Zero;
        }

        /// <summary>The waypoint after <paramref name="index"/> by the route's mode (Loop / PingPong / Once);
        /// <paramref name="direction"/> carries the ping-pong direction between calls (start with 1). -1 for an empty route.</summary>
        public static int Next(long path, int index, ref int direction)
        {
            var p = P(path);
            return p != null ? p.Next(index, ref direction) : -1;
        }

        /// <summary>The waypoint closest to <paramref name="position"/> — where to resume after a chase (-1 = empty route).</summary>
        public static int Closest(long path, Vector3 position)
        {
            return PatrolPathService.ClosestWaypoint(P(path), new EcsVec(position.X, position.Y, position.Z));
        }

        /// <summary>The route's pause at each waypoint (seconds, from the component).</summary>
        public static float WaitTime(long path) { var p = P(path); return p != null ? p.WaitTime : 0f; }

        /// <summary>"Loop", "PingPong" or "Once".</summary>
        public static string Mode(long path) { var p = P(path); return p != null ? p.Mode.ToString() : ""; }
    }
}
