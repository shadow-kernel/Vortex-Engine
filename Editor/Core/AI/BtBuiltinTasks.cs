using System;
using System.Collections.Generic;
using Vortex;

// The engine's own tasks (#111): enough to build a patrol → investigate → chase tree without a line of script, and the
// glue (Wait, blackboard reads / writes, navigation, perception, patrol routes) project tasks sit next to. Gameplay
// decisions stay in the project's scripts — these only move, look, listen and wait.
namespace Vortex.Tasks
{
    /// <summary>Succeeds after <c>seconds</c> (+ up to <c>random</c> extra seconds).</summary>
    public sealed class Wait : BtTask
    {
        private static readonly Random _rng = new Random();
        private float _left;
        public override void OnEnter() { _left = Math.Max(0f, ParamFloat("seconds", 1f)) + (float)_rng.NextDouble() * Math.Max(0f, ParamFloat("random", 0f)); }
        public override BtStatus OnTick(float dt) { _left -= dt; return _left <= 0f ? BtStatus.Success : BtStatus.Running; }
        public override IEnumerable<BtParam> DescribeParams()
        {
            yield return new BtParam("seconds", "1", "How long to wait");
            yield return new BtParam("random", "0", "Up to this many extra seconds, random per run");
        }
        public override string Description { get { return "Succeeds after a delay"; } }
    }

    /// <summary>Always Success.</summary>
    public sealed class Succeed : BtTask
    {
        public override BtStatus OnTick(float dt) { return BtStatus.Success; }
        public override string Description { get { return "Always succeeds"; } }
    }

    /// <summary>Always Failure.</summary>
    public sealed class Fail : BtTask
    {
        public override BtStatus OnTick(float dt) { return BtStatus.Failure; }
        public override string Description { get { return "Always fails"; } }
    }

    /// <summary>Writes a line to the console. <c>message</c> may read the blackboard with <c>{key}</c>.</summary>
    public sealed class Log : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            Editor.Core.Services.AI.BehaviorTreeService.LogLine("[BT " + NodeName + "] " + Resolve("message", ""));
            return BtStatus.Success;
        }
        public override IEnumerable<BtParam> DescribeParams() { yield return new BtParam("message", "", "Text to log; {key} inserts a blackboard value"); }
        public override string Description { get { return "Logs a message"; } }
    }

    /// <summary>Writes <c>value</c> (a literal, or <c>{other}</c> to copy a key) into blackboard <c>key</c>.</summary>
    public sealed class SetValue : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            string key = Param("key", "");
            if (string.IsNullOrEmpty(key)) return BtStatus.Failure;
            string raw = Param("value", "").Trim();
            if (raw.Length > 2 && raw[0] == '{' && raw[raw.Length - 1] == '}') Blackboard.Set(key, Blackboard.Get(raw.Substring(1, raw.Length - 2)));
            else Blackboard.Set(key, raw);
            return BtStatus.Success;
        }
        public override IEnumerable<BtParam> DescribeParams()
        {
            yield return new BtParam("key", "", "Blackboard key to write");
            yield return new BtParam("value", "", "The value (number, true/false, x,y,z, text) or {key} to copy");
        }
        public override string Description { get { return "Writes a blackboard value"; } }
    }

    /// <summary>Condition: Success when blackboard <c>key</c> is set (and, for strings, not empty).</summary>
    public sealed class HasValue : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            string key = Param("key", "");
            object v = Blackboard.Get(key);
            if (v == null) return BtStatus.Failure;
            if (v is string && string.IsNullOrEmpty((string)v)) return BtStatus.Failure;
            if (v is long && (long)v == 0) return BtStatus.Failure;
            return BtStatus.Success;
        }
        public override IEnumerable<BtParam> DescribeParams() { yield return new BtParam("key", "", "Blackboard key to test"); }
        public override string Description { get { return "Condition: a blackboard key is set"; } }
    }

    /// <summary>Removes blackboard <c>key</c>.</summary>
    public sealed class ClearValue : BtTask
    {
        public override BtStatus OnTick(float dt) { Blackboard.Remove(Param("key", "")); return BtStatus.Success; }
        public override IEnumerable<BtParam> DescribeParams() { yield return new BtParam("key", "", "Blackboard key to clear"); }
        public override string Description { get { return "Clears a blackboard value"; } }
    }

    /// <summary>Condition: the agent is within <c>distance</c> of <c>target</c> (a blackboard key holding a position or an entity).</summary>
    public sealed class IsNear : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            Vector3 p;
            if (!Targets.Resolve(Blackboard, Param("target", "Target"), out p)) return BtStatus.Failure;
            var me = Scene.WorldPositionOf(Agent);
            float d = ParamFloat("distance", 1.5f);
            float dx = p.X - me.X, dy = p.Y - me.Y, dz = p.Z - me.Z;
            return dx * dx + dy * dy + dz * dz <= d * d ? BtStatus.Success : BtStatus.Failure;
        }
        public override IEnumerable<BtParam> DescribeParams()
        {
            yield return new BtParam("target", "Target", "Blackboard key: a position or an entity");
            yield return new BtParam("distance", "1.5", "Metres");
        }
        public override string Description { get { return "Condition: within a distance of the target"; } }
    }

    /// <summary>Walks the Nav Agent to <c>target</c> (a blackboard key holding a position or an entity; a moving entity is
    /// followed). Success on arrival, Failure when there is no agent / no path.</summary>
    public sealed class MoveTo : BtTask
    {
        private Vector3 _sent;
        private float _resend, _sinceSend;
        private bool _any;

        public override void OnEnter()
        {
            float speed = ParamFloat("speed", 0f);
            if (speed > 0f) Navigation.SetSpeed(Agent, speed);
            _any = false; _resend = 0f; _sinceSend = 0f;
        }

        public override BtStatus OnTick(float dt)
        {
            Vector3 p;
            if (!Targets.Resolve(Blackboard, Param("target", "Target"), out p)) return BtStatus.Failure;
            _resend -= dt; _sinceSend += dt;
            if (!_any || (_resend <= 0f && Dist2(p, _sent) > 0.25f))
            {
                if (!Navigation.SetDestination(Agent, p)) return BtStatus.Failure;
                _sent = p; _any = true; _resend = 0.25f; _sinceSend = 0f;
            }
            // the crowd plans the path over the next ticks: "no path" only counts once the request had its chance and
            // the agent dropped the destination (unreachable target, agent off the navmesh)
            var status = Navigation.PathStatus(Agent);
            if (status == NavPathStatus.Invalid && _sinceSend > 0.75f && !Navigation.HasDestination(Agent)) return BtStatus.Failure;
            float acceptance = ParamFloat("acceptance", 0f);
            if (acceptance > 0f)
            {
                var me = Scene.WorldPositionOf(Agent);
                if (Dist2(me, p) <= acceptance * acceptance) return BtStatus.Success;
            }
            return Navigation.HasArrived(Agent) ? BtStatus.Success : BtStatus.Running;
        }

        public override void OnExit() { if (Aborted) Navigation.Stop(Agent); }

        private static float Dist2(Vector3 a, Vector3 b) { float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z; return x * x + y * y + z * z; }

        public override IEnumerable<BtParam> DescribeParams()
        {
            yield return new BtParam("target", "Target", "Blackboard key: a position (x,y,z) or an entity");
            yield return new BtParam("acceptance", "0", "Done within this distance (0 = the agent's stopping distance)");
            yield return new BtParam("speed", "0", "Agent speed while moving (0 = unchanged)");
        }
        public override string Description { get { return "Walks the Nav Agent to a target"; } }
    }

    /// <summary>Stops the Nav Agent.</summary>
    public sealed class StopMoving : BtTask
    {
        public override BtStatus OnTick(float dt) { Navigation.Stop(Agent); return BtStatus.Success; }
        public override string Description { get { return "Stops the Nav Agent"; } }
    }

    /// <summary>Condition: the agent's perception sees a target. Writes the seen entity into blackboard <c>key</c>.</summary>
    public sealed class CanSee : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            if (!Perception.CanSee(Agent)) return BtStatus.Failure;
            string key = Param("key", "Target");
            long seen = Perception.VisibleTarget(Agent);
            if (!string.IsNullOrEmpty(key) && seen != 0) Blackboard.Set(key, seen);
            return BtStatus.Success;
        }
        public override IEnumerable<BtParam> DescribeParams() { yield return new BtParam("key", "Target", "Blackboard key that receives the seen entity"); }
        public override string Description { get { return "Condition: the agent sees a target (writes it to the blackboard)"; } }
    }

    /// <summary>Condition: the agent heard something within <c>maxAge</c> seconds. Writes the position into blackboard <c>key</c>.</summary>
    public sealed class HasHeard : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            if (!Perception.HasHeard(Agent, ParamFloat("maxAge", -1f))) return BtStatus.Failure;
            Vector3 p;
            if (Perception.LastHeardPosition(Agent, out p)) Blackboard.Set(Param("key", "HeardAt"), p);
            return BtStatus.Success;
        }
        public override IEnumerable<BtParam> DescribeParams()
        {
            yield return new BtParam("maxAge", "-1", "Only noises younger than this many seconds (-1 = any)");
            yield return new BtParam("key", "HeardAt", "Blackboard key that receives the noise position");
        }
        public override string Description { get { return "Condition: the agent heard a noise (writes where)"; } }
    }

    /// <summary>Writes the last known position of the agent's target (seen or heard) into blackboard <c>key</c>; Failure when none.</summary>
    public sealed class LastKnownPosition : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            Vector3 p;
            if (!Perception.LastKnownPosition(Agent, out p)) return BtStatus.Failure;
            Blackboard.Set(Param("key", "LastKnown"), p);
            return BtStatus.Success;
        }
        public override IEnumerable<BtParam> DescribeParams() { yield return new BtParam("key", "LastKnown", "Blackboard key that receives the position"); }
        public override string Description { get { return "Remembers where the target was last seen or heard"; } }
    }

    /// <summary>Forgets what the agent saw and heard.</summary>
    public sealed class ForgetTarget : BtTask
    {
        public override BtStatus OnTick(float dt) { Perception.Forget(Agent); Blackboard.Remove(Param("key", "Target")); return BtStatus.Success; }
        public override IEnumerable<BtParam> DescribeParams() { yield return new BtParam("key", "Target", "Blackboard key to clear as well"); }
        public override string Description { get { return "Clears the perception memory"; } }
    }

    /// <summary>Writes a random navmesh point within <c>radius</c> of the agent (or of blackboard <c>around</c>) into blackboard <c>key</c>.</summary>
    public sealed class RandomPoint : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            Vector3 centre;
            string around = Param("around", "");
            if (string.IsNullOrEmpty(around) || !Targets.Resolve(Blackboard, around, out centre)) centre = Scene.WorldPositionOf(Agent);
            Vector3 p;
            if (!Navigation.RandomPoint(centre, ParamFloat("radius", 10f), out p)) return BtStatus.Failure;
            Blackboard.Set(Param("key", "Wander"), p);
            return BtStatus.Success;
        }
        public override IEnumerable<BtParam> DescribeParams()
        {
            yield return new BtParam("key", "Wander", "Blackboard key that receives the point");
            yield return new BtParam("radius", "10", "Metres around the centre");
            yield return new BtParam("around", "", "Blackboard key of the centre (empty = the agent)");
        }
        public override string Description { get { return "Picks a random reachable point"; } }
    }

    /// <summary>Advances along a Patrol Path: writes the next waypoint into blackboard <c>key</c> (and the path's wait time
    /// into <c>waitKey</c>). The path is the entity named <c>path</c>, or the entity in blackboard <c>pathKey</c>; without
    /// either, the agent's own Patrol Path.</summary>
    public sealed class PatrolNext : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            long path = 0;
            string name = Param("path", "");
            if (!string.IsNullOrEmpty(name)) path = Scene.Find(name);
            if (path == 0) path = Blackboard.GetEntity(Param("pathKey", "PatrolPath"));
            if (path == 0) path = Agent;
            int count = PatrolPath.Count(path);
            if (count == 0) return BtStatus.Failure;
            int index = Blackboard.GetInt("__patrolIndex", -1);
            int dir = Blackboard.GetInt("__patrolDir", 1);
            if (index < 0) index = PatrolPath.Closest(path, Scene.WorldPositionOf(Agent));
            else index = PatrolPath.Next(path, index, ref dir);
            if (index < 0) return BtStatus.Failure;   // a "once" route has ended
            Blackboard.Set("__patrolIndex", index);
            Blackboard.Set("__patrolDir", dir);
            Blackboard.Set(Param("key", "PatrolPoint"), PatrolPath.GetWaypoint(path, index));
            Blackboard.Set(Param("waitKey", "PatrolWait"), PatrolPath.WaitTime(path));
            return BtStatus.Success;
        }
        public override IEnumerable<BtParam> DescribeParams()
        {
            yield return new BtParam("key", "PatrolPoint", "Blackboard key that receives the next waypoint");
            yield return new BtParam("waitKey", "PatrolWait", "Blackboard key that receives the path's wait time");
            yield return new BtParam("path", "", "Name of the Patrol Path entity (empty = pathKey / the agent's own)");
            yield return new BtParam("pathKey", "PatrolPath", "Blackboard key holding the Patrol Path entity");
        }
        public override string Description { get { return "Next waypoint of a Patrol Path"; } }
    }

    /// <summary>Plays an animation clip on the agent (Success at once; the clip keeps playing).</summary>
    public sealed class PlayAnimation : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            return Animation.Play(Agent, Resolve("clip", ""), ParamFloat("fade", 0.15f)) ? BtStatus.Success : BtStatus.Failure;
        }
        public override IEnumerable<BtParam> DescribeParams()
        {
            yield return new BtParam("clip", "", "Clip name or path");
            yield return new BtParam("fade", "0.15", "Crossfade seconds");
        }
        public override string Description { get { return "Plays an animation clip"; } }
    }

    /// <summary>Sends a message to the agent's script (<c>OnMessage(name, arg)</c>) — the hand-over to project gameplay code.</summary>
    public sealed class SendMessage : BtTask
    {
        public override BtStatus OnTick(float dt)
        {
            string target = Param("target", "");
            long to = string.IsNullOrEmpty(target) ? Agent : Targets.Entity(Blackboard, target, Agent);
            Editor.Scripting.ScriptRuntime.Instance.SendEntityMessage(to, Resolve("message", "bt"), Resolve("arg", null));
            return BtStatus.Success;
        }
        public override IEnumerable<BtParam> DescribeParams()
        {
            yield return new BtParam("message", "", "Message name for OnMessage");
            yield return new BtParam("arg", "", "Argument (text; {key} inserts a blackboard value)");
            yield return new BtParam("target", "", "Blackboard key of the receiving entity (empty = the agent)");
        }
        public override string Description { get { return "Sends OnMessage to a script"; } }
    }

    /// <summary>Shared target parsing: a blackboard key holding a position (x,y,z) or an entity handle.</summary>
    internal static class Targets
    {
        public static bool Resolve(Blackboard bb, string key, out Vector3 position)
        {
            position = default(Vector3);
            if (bb == null || string.IsNullOrEmpty(key)) return false;
            object v = bb.Get(key);
            if (v == null) return false;
            if (v is Vector3) { position = (Vector3)v; return true; }
            if (v is long) { long h = (long)v; if (h == 0) return false; position = Scene.WorldPositionOf(h); return true; }
            if (v is int) { position = Scene.WorldPositionOf((int)v); return true; }
            object parsed;
            if (Blackboard.TryConvert(v, typeof(Vector3), out parsed)) { position = (Vector3)parsed; return true; }
            if (Blackboard.TryConvert(v, typeof(long), out parsed) && (long)parsed != 0) { position = Scene.WorldPositionOf((long)parsed); return true; }
            return false;
        }

        public static long Entity(Blackboard bb, string key, long fallback)
        {
            if (bb == null) return fallback;
            long h = bb.GetEntity(key);
            return h != 0 ? h : fallback;
        }
    }
}
