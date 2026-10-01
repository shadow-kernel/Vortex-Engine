using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.AI;
using SysVec = System.Numerics.Vector3;

namespace Editor.Core.Services.AI
{
    /// <summary>Which sense produced a stimulus.</summary>
    public enum PerceptionSense { Sight = 0, Hearing = 1 }

    /// <summary>What an agent perceived (raised through <see cref="PerceptionService.StimulusSink"/>).</summary>
    public struct PerceptionStimulus
    {
        /// <summary>The perceiving agent (its entity has the AIPerception component).</summary>
        public GameEntity Agent;
        /// <summary>The seen target / the noise's instigator (may be null for anonymous noises).</summary>
        public GameEntity Source;
        public PerceptionSense Sense;
        /// <summary>"PerceptionSeen", "PerceptionLost" or "PerceptionHeard".</summary>
        public string Kind;
        /// <summary>Where the target was seen / the noise was made.</summary>
        public Vector3 Position;
        /// <summary>Loudness of a noise (0 for sight).</summary>
        public float Loudness;
        public float Time;
    }

    /// <summary>
    /// AI perception (GitHub #112): sight and hearing for every entity with an <see cref="AIPerception"/> component, plus a
    /// per-agent memory of the last seen / heard position. Sight = inside the vision cone (or the proximity radius) and
    /// within the view distance, confirmed by line-of-sight raycasts (the Jolt world when it is built, else the static
    /// collision world) to three points on the target (feet + 25 / 60 / 95 % of its height) — hiding behind a crate,
    /// crouched, breaks it. Checks run every <see cref="AIPerception.UpdateInterval"/> seconds, staggered across agents.
    /// Hearing = <see cref="MakeNoise"/>: heard when the loudness reaches the agent's threshold and the distance is within
    /// hearing range × loudness (halved when geometry is in between). Lifecycle owned by <see cref="AiRuntime"/>.
    /// </summary>
    public static class PerceptionService
    {
        /// <summary>Receives every stimulus (the script API forwards them to the agent's behaviour as OnMessage).</summary>
        public static Action<PerceptionStimulus> StimulusSink;

        /// <summary>Draw the vision cones / hearing rings of the agents in play (debug).</summary>
        public static bool ShowDebug;

        private sealed class TargetMem
        {
            public GameEntity Target;
            public bool Visible;          // raw: in cone + line of sight at the last check
            public float VisibleTime;     // continuous visible time (confirmation)
            public bool Seen;             // confirmed sighting in progress
        }

        private sealed class Agent
        {
            public GameEntity Entity;
            public AIPerception Sensor;
            public float NextCheck;
            public readonly Dictionary<GameEntity, TargetMem> Targets = new Dictionary<GameEntity, TargetMem>();
            public readonly List<GameEntity> ExtraTargets = new List<GameEntity>();
            public bool HasSeen; public Vector3 LastSeenPosition; public float LastSeenTime = float.NegativeInfinity; public GameEntity LastSeenTarget;
            public bool HasHeard; public Vector3 LastHeardPosition; public float LastHeardTime = float.NegativeInfinity; public float LastHeardLoudness; public GameEntity LastHeardSource;
        }

        private static readonly Dictionary<GameEntity, Agent> _agents = new Dictionary<GameEntity, Agent>();
        private static readonly List<Agent> _agentList = new List<Agent>();
        private static readonly List<GameEntity> _taggedTargets = new List<GameEntity>();
        private static readonly HashSet<GameEntity> _hidden = new HashSet<GameEntity>();
        private static readonly Dictionary<GameEntity, float> _targetHeights = new Dictionary<GameEntity, float>();
        private static Scene _scene;
        private static float _time;
        private static float _rescan;

        public static bool IsRunning { get; private set; }
        /// <summary>Seconds since <see cref="Begin"/> (the clock of LastSeenTime / LastHeardTime).</summary>
        public static float Time => _time;
        public const float DefaultTargetHeight = 1.7f;

        public static void Begin(Scene scene)
        {
            End();
            _scene = scene;
            IsRunning = true;
            _time = 0f;
            Rescan();
        }

        public static void End()
        {
            _agents.Clear(); _agentList.Clear(); _taggedTargets.Clear(); _hidden.Clear(); _targetHeights.Clear();
            _scene = null;
            IsRunning = false;
        }

        public static void Tick(float dt)
        {
            if (!IsRunning) return;
            if (dt < 0f) dt = 0f;
            if (dt > 0.25f) dt = 0.25f;
            _time += dt;
            _rescan -= dt;
            if (_rescan <= 0f) { _rescan = 1f; Rescan(); }
            for (int i = 0; i < _agentList.Count; i++)
            {
                var a = _agentList[i];
                if (a.Sensor == null || !a.Sensor.IsEnabled || a.Entity == null || !a.Entity.ActiveInHierarchy) continue;
                if (_time < a.NextCheck) continue;
                float interval = Math.Max(0f, a.Sensor.UpdateInterval);
                a.NextCheck = _time + interval;
                UpdateSight(a, Math.Max(interval, dt));
            }
            if (ShowDebug) SubmitDebug();
        }

        // ------------------------------------------------------------------------------------------ sight

        private static void UpdateSight(Agent a, float elapsed)
        {
            var s = a.Sensor;
            GetEye(a, out var eye, out var forward);
            foreach (var t in CandidateTargets(a))
            {
                if (!a.Targets.TryGetValue(t, out var mem)) { mem = new TargetMem { Target = t }; a.Targets[t] = mem; }
                bool visible = !_hidden.Contains(t) && t.ActiveInHierarchy && CheckVisible(a, eye, forward, t, out _);
                mem.Visible = visible;
                if (visible)
                {
                    mem.VisibleTime += elapsed;
                    if (!mem.Seen && mem.VisibleTime >= s.SightConfirmTime - 1e-4f)
                    {
                        mem.Seen = true;
                        Remember(a, t);
                        Raise(a, t, PerceptionSense.Sight, "PerceptionSeen", a.LastSeenPosition, 0f);
                    }
                    else if (mem.Seen) Remember(a, t);
                }
                else
                {
                    mem.VisibleTime = 0f;
                    if (mem.Seen)
                    {
                        mem.Seen = false;
                        Raise(a, t, PerceptionSense.Sight, "PerceptionLost", a.LastSeenPosition, 0f);
                    }
                }
            }
        }

        private static void Remember(Agent a, GameEntity t)
        {
            var p = Animation.BoneSocketService.EntityWorld(t).Translation;
            a.HasSeen = true;
            a.LastSeenPosition = new Vector3(p.X, p.Y, p.Z);
            a.LastSeenTime = _time;
            a.LastSeenTarget = t;
        }

        private static IEnumerable<GameEntity> CandidateTargets(Agent a)
        {
            string tag = a.Sensor.TargetTag;
            if (!string.IsNullOrEmpty(tag))
                foreach (var t in _taggedTargets)
                    if (!ReferenceEquals(t, a.Entity) && string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase)) yield return t;
            foreach (var t in a.ExtraTargets) if (t != null && !ReferenceEquals(t, a.Entity)) yield return t;
        }

        // Points on a target the sight rays aim at (fractions of its height): chest first, then head, then knees.
        private static readonly float[] SampleFractions = { 0.6f, 0.95f, 0.25f };

        /// <summary>Cone / distance / line-of-sight test from an agent's eye to a target.</summary>
        private static bool CheckVisible(Agent a, SysVec eye, SysVec forward, GameEntity target, out SysVec seenPoint)
        {
            var s = a.Sensor;
            var feet = Animation.BoneSocketService.EntityWorld(target).Translation;
            float h = _targetHeights.TryGetValue(target, out var th) ? th : DefaultTargetHeight;
            float cosHalf = (float)Math.Cos(Math.Min(180f, s.FieldOfView * 0.5f) * Math.PI / 180.0);
            seenPoint = feet;
            foreach (var f in SampleFractions)
            {
                var p = feet + new SysVec(0f, h * f, 0f);
                var d = p - eye;
                float dist = d.Length();
                if (dist > s.ViewDistance) continue;
                bool close = dist <= s.ProximityRadius;
                if (!close && s.FieldOfView < 359.9f)
                {
                    if (dist < 1e-4f) { seenPoint = p; return true; }
                    if (SysVec.Dot(d / dist, forward) < cosHalf) continue;
                }
                if (HasLineOfSight(ToEcs(eye), ToEcs(p), a.Entity, target)) { seenPoint = p; return true; }
            }
            return false;
        }

        private static void GetEye(Agent a, out SysVec eye, out SysVec forward)
        {
            var m = Animation.BoneSocketService.EntityWorld(a.Entity);
            var nav = a.Entity.GetComponent<NavAgent>();
            float baseOffset = nav != null ? nav.BaseOffset : 0f;
            eye = m.Translation + new SysVec(0f, a.Sensor.EyeHeight - baseOffset, 0f);
            forward = SysVec.TransformNormal(SysVec.UnitZ, m);
            forward.Y = 0f;
            if (forward.LengthSquared() < 1e-8f) forward = SysVec.UnitZ;
            forward = SysVec.Normalize(forward);
        }

        /// <summary>True when nothing solid is between the two points. Hits on <paramref name="ignoreA"/> /
        /// <paramref name="ignoreB"/> (and their children) are skipped — a hit on the target itself counts as clear.</summary>
        public static bool HasLineOfSight(Vector3 from, Vector3 to, GameEntity ignoreA, GameEntity ignoreB)
        {
            var dir = to - from;
            float dist = dir.Magnitude;
            if (dist < 1e-3f) return true;
            dir = dir / dist;
            var origin = from;
            float remaining = dist;
            const uint mask = (1u << PhysicsService.LayerStatic) | (1u << PhysicsService.LayerDynamic) | (1u << PhysicsService.LayerCharacter);
            for (int i = 0; i < 6; i++)
            {
                bool hit;
                GameEntity he;
                float hd;
                Vector3 hp;
                if (PhysicsService.IsBuilt) hit = PhysicsService.Raycast(origin, dir, remaining, out hp, out _, out he, out hd, mask);
                else if (CollisionService.IsBuilt) hit = CollisionService.Raycast(origin, dir, remaining, ~0, out hp, out _, out he, out hd);
                else return true;   // no collision world: the cone alone decides
                if (!hit) return true;
                if (IsSelfOrChild(he, ignoreB)) return true;
                if (!IsSelfOrChild(he, ignoreA)) return false;
                float step = hd + 0.02f;
                origin = origin + dir * step;
                remaining -= step;
                if (remaining <= 0f) return true;
            }
            return true;
        }

        private static bool IsSelfOrChild(GameEntity e, GameEntity root)
        {
            if (e == null || root == null) return false;
            for (var p = e; p != null; p = p.Parent) if (ReferenceEquals(p, root)) return true;
            return false;
        }

        // ------------------------------------------------------------------------------------------ hearing

        /// <summary>A noise at <paramref name="position"/> (footstep 0.3, running 0.7, door slam 1, gunshot 3 …). Every
        /// perceiving agent within hearing range × loudness hears it (half range through walls) — its memory keeps the
        /// position and its behaviour gets <c>OnMessage("PerceptionHeard", …)</c>. Returns how many agents heard it.</summary>
        public static int MakeNoise(Vector3 position, float loudness, GameEntity instigator)
        {
            if (!IsRunning || loudness <= 0f || float.IsNaN(loudness)) return 0;
            int heard = 0;
            for (int i = 0; i < _agentList.Count; i++)
            {
                var a = _agentList[i];
                var s = a.Sensor;
                if (s == null || !s.IsEnabled || a.Entity == null || !a.Entity.ActiveInHierarchy) continue;
                if (ReferenceEquals(a.Entity, instigator)) continue;
                if (loudness < s.HearingThreshold) continue;
                GetEye(a, out var eye, out _);
                float range = s.HearingRange * loudness;
                float dist = (position - ToEcs(eye)).Magnitude;
                if (dist > range) continue;
                if (dist > range * 0.5f && !HasLineOfSight(ToEcs(eye), position, a.Entity, instigator)) continue;   // muffled by walls
                a.HasHeard = true;
                a.LastHeardPosition = position;
                a.LastHeardTime = _time;
                a.LastHeardLoudness = loudness;
                a.LastHeardSource = instigator;
                heard++;
                Raise(a, instigator, PerceptionSense.Hearing, "PerceptionHeard", position, loudness);
            }
            return heard;
        }

        // ------------------------------------------------------------------------------------------ queries

        private static Agent Get(GameEntity e)
        {
            if (e == null || !IsRunning) return null;
            if (_agents.TryGetValue(e, out var a)) return a;
            var s = e.GetComponent<AIPerception>();
            if (s == null) return null;
            a = new Agent { Entity = e, Sensor = s, NextCheck = _time };
            _agents[e] = a;
            _agentList.Add(a);
            return a;
        }

        /// <summary>True while the agent has a confirmed, current sighting of <paramref name="target"/> (null = any target).</summary>
        public static bool CanSee(GameEntity agent, GameEntity target)
        {
            var a = Get(agent);
            if (a == null) return false;
            foreach (var m in a.Targets.Values)
                if (m.Seen && m.Visible && (target == null || ReferenceEquals(m.Target, target))) return true;
            return false;
        }

        /// <summary>The target the agent currently sees (null = none).</summary>
        public static GameEntity VisibleTarget(GameEntity agent)
        {
            var a = Get(agent);
            if (a == null) return null;
            foreach (var m in a.Targets.Values) if (m.Seen && m.Visible) return m.Target;
            return null;
        }

        /// <summary>Last confirmed sighting within <paramref name="maxAge"/> seconds (negative = the component's memory).</summary>
        public static bool TryGetLastSeen(GameEntity agent, float maxAge, out Vector3 position, out float age, out GameEntity target)
        {
            position = Vector3.Zero; age = float.PositiveInfinity; target = null;
            var a = Get(agent);
            if (a == null || !a.HasSeen) return false;
            age = _time - a.LastSeenTime;
            if (maxAge < 0f) maxAge = a.Sensor.MemorySeconds;
            position = a.LastSeenPosition; target = a.LastSeenTarget;
            return age <= maxAge;
        }

        /// <summary>Last heard noise within <paramref name="maxAge"/> seconds (negative = the component's memory).</summary>
        public static bool TryGetLastHeard(GameEntity agent, float maxAge, out Vector3 position, out float age, out float loudness, out GameEntity source)
        {
            position = Vector3.Zero; age = float.PositiveInfinity; loudness = 0f; source = null;
            var a = Get(agent);
            if (a == null || !a.HasHeard) return false;
            age = _time - a.LastHeardTime;
            if (maxAge < 0f) maxAge = a.Sensor.MemorySeconds;
            position = a.LastHeardPosition; loudness = a.LastHeardLoudness; source = a.LastHeardSource;
            return age <= maxAge;
        }

        /// <summary>The most recent seen-or-heard position still in memory (what a search behaviour walks to).</summary>
        public static bool TryGetLastKnownPosition(GameEntity agent, out Vector3 position)
        {
            position = Vector3.Zero;
            bool seen = TryGetLastSeen(agent, -1f, out var sp, out var sAge, out _);
            bool heard = TryGetLastHeard(agent, -1f, out var hp, out var hAge, out _, out _);
            if (!seen && !heard) return false;
            position = seen && (!heard || sAge <= hAge) ? sp : hp;
            return true;
        }

        /// <summary>Wipe the agent's memory (after a search gave up).</summary>
        public static void Forget(GameEntity agent)
        {
            var a = Get(agent);
            if (a == null) return;
            a.HasSeen = false; a.HasHeard = false; a.LastSeenTarget = null; a.LastHeardSource = null;
            a.LastSeenTime = float.NegativeInfinity; a.LastHeardTime = float.NegativeInfinity;
            foreach (var m in a.Targets.Values) { m.Seen = false; m.VisibleTime = 0f; }
        }

        /// <summary>Hide a target from every agent (the player entering a locker) — sightings of it end at the next check.</summary>
        public static void SetHidden(GameEntity target, bool hidden)
        {
            if (target == null) return;
            if (hidden) _hidden.Add(target); else _hidden.Remove(target);
        }

        public static bool IsHidden(GameEntity target) => target != null && _hidden.Contains(target);

        /// <summary>The height (m) the sight rays aim at on a target: 1.7 standing, ~1.0 crouched.</summary>
        public static void SetTargetHeight(GameEntity target, float height)
        {
            if (target == null) return;
            _targetHeights[target] = Math.Max(0.1f, height);
        }

        /// <summary>Let an agent look for an extra target (besides the Target Tag entities).</summary>
        public static void AddTarget(GameEntity agent, GameEntity target)
        {
            var a = Get(agent);
            if (a != null && target != null && !a.ExtraTargets.Contains(target)) a.ExtraTargets.Add(target);
        }

        public static void RemoveTarget(GameEntity agent, GameEntity target)
        {
            var a = Get(agent);
            if (a == null || target == null) return;
            a.ExtraTargets.Remove(target);
            a.Targets.Remove(target);
        }

        /// <summary>Geometry only: is <paramref name="point"/> inside the agent's vision cone and view distance?</summary>
        public static bool IsInViewCone(GameEntity agent, Vector3 point)
        {
            var a = Get(agent);
            if (a == null) return false;
            GetEye(a, out var eye, out var forward);
            var d = new SysVec(point.X, point.Y, point.Z) - eye;
            float dist = d.Length();
            if (dist > a.Sensor.ViewDistance) return false;
            if (dist < 1e-4f || a.Sensor.FieldOfView >= 359.9f) return true;
            float cosHalf = (float)Math.Cos(Math.Min(180f, a.Sensor.FieldOfView * 0.5f) * Math.PI / 180.0);
            return SysVec.Dot(d / dist, forward) >= cosHalf;
        }

        /// <summary>Number of perceiving agents in the running game.</summary>
        public static int AgentCount => _agentList.Count;

        // ------------------------------------------------------------------------------------------ internals

        private static void Raise(Agent a, GameEntity source, PerceptionSense sense, string kind, Vector3 position, float loudness)
        {
            var sink = StimulusSink;
            if (sink == null) return;
            try
            {
                sink(new PerceptionStimulus { Agent = a.Entity, Source = source, Sense = sense, Kind = kind, Position = position, Loudness = loudness, Time = _time });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Perception] " + kind + " handler: " + ex.Message); }
        }

        private static void Rescan()
        {
            if (_scene?.Entities == null) return;
            _taggedTargets.Clear();
            var seen = new HashSet<GameEntity>();
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in _scene.Entities) CollectAgents(root, seen);
            foreach (var a in _agentList) if (!string.IsNullOrEmpty(a.Sensor?.TargetTag)) tags.Add(a.Sensor.TargetTag);
            if (tags.Count > 0) foreach (var root in _scene.Entities) CollectTargets(root, tags);
            for (int i = _agentList.Count - 1; i >= 0; i--)
            {
                if (seen.Contains(_agentList[i].Entity)) continue;
                _agents.Remove(_agentList[i].Entity);
                _agentList.RemoveAt(i);
            }
        }

        private static void CollectAgents(GameEntity e, HashSet<GameEntity> seen)
        {
            if (e == null) return;
            var s = e.GetComponent<AIPerception>();
            if (s != null)
            {
                seen.Add(e);
                if (!_agents.ContainsKey(e))
                {
                    // Stagger the first checks so many agents don't raycast in the same frame.
                    var a = new Agent { Entity = e, Sensor = s, NextCheck = _time + (_agentList.Count % 8) * Math.Max(0.01f, s.UpdateInterval) / 8f };
                    _agents[e] = a;
                    _agentList.Add(a);
                }
            }
            if (e.Children != null) foreach (var c in e.Children) CollectAgents(c, seen);
        }

        private static void CollectTargets(GameEntity e, HashSet<string> tags)
        {
            if (e == null) return;
            if (!string.IsNullOrEmpty(e.Tag) && tags.Contains(e.Tag)) _taggedTargets.Add(e);
            if (e.Children != null) foreach (var c in e.Children) CollectTargets(c, tags);
        }

        private static void SubmitDebug()
        {
            var seg = new List<float>();
            foreach (var a in _agentList)
            {
                if (a.Sensor == null || !a.Sensor.IsEnabled || a.Entity == null || !a.Entity.ActiveInHierarchy) continue;
                AppendCone(a, seg);
            }
            if (seg.Count >= 6) Editor.DllWrapper.VortexAPI.RenderNavigationLines(seg.ToArray(), seg.Count, 0.95f, 0.25f, 0.3f, 0.015f, 120);
        }

        /// <summary>Line segments of an agent's vision cone (two edges + the arc) at eye height — the gizmo feed.</summary>
        public static float[] GetConeDebugLines(GameEntity agent)
        {
            var s = agent?.GetComponent<AIPerception>();
            if (s == null) return new float[0];
            var seg = new List<float>();
            AppendCone(new Agent { Entity = agent, Sensor = s }, seg);
            return seg.ToArray();
        }

        private static void AppendCone(Agent a, List<float> seg)
        {
            GetEye(a, out var eye, out var forward);
            float half = Math.Min(180f, a.Sensor.FieldOfView * 0.5f) * (float)Math.PI / 180f;
            float r = a.Sensor.ViewDistance;
            float baseYaw = (float)Math.Atan2(forward.X, forward.Z);
            const int n = 12;
            SysVec prev = default(SysVec);
            for (int i = 0; i <= n; i++)
            {
                float ang = baseYaw - half + 2f * half * i / n;
                var p = eye + new SysVec((float)Math.Sin(ang) * r, 0f, (float)Math.Cos(ang) * r);
                if (i > 0) Add(seg, prev, p);
                if ((i == 0 || i == n) && a.Sensor.FieldOfView < 359.9f) Add(seg, eye, p);
                prev = p;
            }
        }

        private static void Add(List<float> s, SysVec a, SysVec b) { s.Add(a.X); s.Add(a.Y); s.Add(a.Z); s.Add(b.X); s.Add(b.Y); s.Add(b.Z); }
        private static Vector3 ToEcs(SysVec v) => new Vector3(v.X, v.Y, v.Z);
    }
}
