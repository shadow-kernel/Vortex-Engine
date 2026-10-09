using System;
using System.Collections.Generic;
using Vortex;

namespace Editor.Core.AI
{
    /// <summary>What a runner hands the nodes each tick.</summary>
    public sealed class BtContext
    {
        public BehaviorTreeRunner Runner;
        public long Agent;
        public Blackboard Blackboard;
        public float Dt;
        public float Time;
        public Action<string> Log;
    }

    /// <summary>A node's state for the editor's live view.</summary>
    public struct BtDebugNode
    {
        public string Id;
        public BtStatus Status;
        public bool Running;
        public int LastTick;
    }

    /// <summary>
    /// A runtime node (#111). Composites and decorators are the engine's; <see cref="TaskNode"/> wraps a
    /// <see cref="BtTask"/>. Ticks are re-entrant per frame: a node that returned Running is ticked again next frame
    /// from where it was, a finished node restarts from the top the next time its parent reaches it.
    /// </summary>
    public abstract class BtNode
    {
        public BtNodeData Data;
        public readonly List<BtNode> Children = new List<BtNode>();
        public BtStatus LastStatus = BtStatus.Failure;
        public bool IsRunning;
        public int LastTick = -1;

        public string Id { get { return Data != null ? Data.Id : ""; } }
        public string Label { get { return Data != null ? Data.Label : GetType().Name; } }

        public BtStatus Tick(BtContext c)
        {
            BtStatus s;
            try { s = OnTick(c); }
            catch (Exception ex)
            {
                if (c.Log != null) c.Log("[BehaviorTree] " + Label + ": " + ex.GetType().Name + ": " + ex.Message);
                s = BtStatus.Failure;
            }
            LastStatus = s;
            LastTick = c.Runner != null ? c.Runner.TickIndex : 0;
            IsRunning = s == BtStatus.Running;
            return s;
        }

        protected abstract BtStatus OnTick(BtContext c);

        /// <summary>Stop a running subtree now (conditional abort, tree stop): running tasks get OnExit with Aborted.</summary>
        public virtual void Abort(BtContext c)
        {
            if (!IsRunning) return;
            foreach (var ch in Children) ch.Abort(c);
            IsRunning = false;
            LastStatus = BtStatus.Failure;
            OnAborted(c);
        }

        protected virtual void OnAborted(BtContext c) { }

        /// <summary>Forget the progress of a FINISHED subtree so it starts from the top next time (Repeat, tree restart).</summary>
        public virtual void Reset()
        {
            foreach (var ch in Children) ch.Reset();
            IsRunning = false;
        }

        public void Snapshot(List<BtDebugNode> into)
        {
            into.Add(new BtDebugNode { Id = Id, Status = LastStatus, Running = IsRunning, LastTick = LastTick });
            foreach (var ch in Children) ch.Snapshot(into);
        }

        /// <summary>The deepest running task's label (for HUDs / the status bar).</summary>
        public string ActiveTaskLabel()
        {
            if (this is TaskNode && IsRunning) return Label;
            foreach (var ch in Children) { if (!ch.IsRunning) continue; string l = ch.ActiveTaskLabel(); if (l != null) return l; }
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------- composites

    public sealed class SelectorNode : BtNode
    {
        private int _current = -1;

        protected override BtStatus OnTick(BtContext c)
        {
            int from = 0;
            if (_current >= 0)
            {
                // observed guards of HIGHER-priority children: one that passes now takes over from the running child
                for (int i = 0; i < _current; i++)
                {
                    var cn = Children[i] as ConditionNode;
                    if (cn != null && cn.ObservesLower && cn.Evaluate(c)) { Children[_current].Abort(c); _current = -1; from = i; break; }
                }
                if (_current >= 0) from = _current;
            }
            for (int i = from; i < Children.Count; i++)
            {
                var s = Children[i].Tick(c);
                if (s == BtStatus.Running) { _current = i; return BtStatus.Running; }
                if (s == BtStatus.Success) { _current = -1; return BtStatus.Success; }
            }
            _current = -1;
            return BtStatus.Failure;
        }

        protected override void OnAborted(BtContext c) { _current = -1; }
        public override void Reset() { base.Reset(); _current = -1; }
    }

    public sealed class SequenceNode : BtNode
    {
        private int _current = -1;

        protected override BtStatus OnTick(BtContext c)
        {
            int from = 0;
            if (_current >= 0)
            {
                // observed guards of EARLIER steps: one that fails now ends the sequence
                for (int i = 0; i < _current; i++)
                {
                    var cn = Children[i] as ConditionNode;
                    if (cn != null && cn.ObservesLower && !cn.Evaluate(c)) { Children[_current].Abort(c); _current = -1; return BtStatus.Failure; }
                }
                from = _current;
            }
            for (int i = from; i < Children.Count; i++)
            {
                var s = Children[i].Tick(c);
                if (s == BtStatus.Running) { _current = i; return BtStatus.Running; }
                if (s == BtStatus.Failure) { _current = -1; return BtStatus.Failure; }
            }
            _current = -1;
            return BtStatus.Success;
        }

        protected override void OnAborted(BtContext c) { _current = -1; }
        public override void Reset() { base.Reset(); _current = -1; }
    }

    /// <summary>Every child ticks each frame. Fails as soon as one child fails (the others are aborted), succeeds when
    /// all have succeeded; with <c>any</c> = true it succeeds as soon as one child succeeds.</summary>
    public sealed class ParallelNode : BtNode
    {
        private bool[] _done;
        public bool AnySucceeds;

        protected override BtStatus OnTick(BtContext c)
        {
            if (_done == null || _done.Length != Children.Count) _done = new bool[Children.Count];
            bool allDone = true;
            for (int i = 0; i < Children.Count; i++)
            {
                if (_done[i]) continue;
                var s = Children[i].Tick(c);
                if (s == BtStatus.Running) { allDone = false; continue; }
                _done[i] = true;
                if (s == BtStatus.Failure) { AbortOthers(c, i); Clear(); return BtStatus.Failure; }
                if (AnySucceeds) { AbortOthers(c, i); Clear(); return BtStatus.Success; }
            }
            if (!allDone) return BtStatus.Running;
            Clear();
            return BtStatus.Success;
        }

        private void AbortOthers(BtContext c, int except) { for (int j = 0; j < Children.Count; j++) if (j != except) Children[j].Abort(c); }
        private void Clear() { if (_done != null) Array.Clear(_done, 0, _done.Length); }
        protected override void OnAborted(BtContext c) { Clear(); }
        public override void Reset() { base.Reset(); Clear(); }
    }

    // ------------------------------------------------------------------------------------------------- decorators

    public abstract class DecoratorNode : BtNode
    {
        protected BtNode Child { get { return Children.Count > 0 ? Children[0] : null; } }
    }

    public sealed class InverterNode : DecoratorNode
    {
        protected override BtStatus OnTick(BtContext c)
        {
            if (Child == null) return BtStatus.Failure;
            var s = Child.Tick(c);
            return s == BtStatus.Running ? s : (s == BtStatus.Success ? BtStatus.Failure : BtStatus.Success);
        }
    }

    public sealed class SucceederNode : DecoratorNode
    {
        protected override BtStatus OnTick(BtContext c)
        {
            if (Child == null) return BtStatus.Success;
            return Child.Tick(c) == BtStatus.Running ? BtStatus.Running : BtStatus.Success;
        }
    }

    /// <summary>Params: <c>count</c> (0 = forever), <c>untilFailure</c> (true = Success when the child first fails).</summary>
    public sealed class RepeatNode : DecoratorNode
    {
        public int Count;
        public bool UntilFailure;
        private int _iterations;

        protected override BtStatus OnTick(BtContext c)
        {
            if (Child == null) return BtStatus.Failure;
            var s = Child.Tick(c);
            if (s == BtStatus.Running) return BtStatus.Running;
            _iterations++;
            if (UntilFailure)
            {
                if (s == BtStatus.Failure) { _iterations = 0; return BtStatus.Success; }
            }
            else if (Count > 0 && _iterations >= Count) { _iterations = 0; return s; }
            Child.Reset();   // runs again on the next tick (never twice in one tick: a Running-less loop would spin)
            return BtStatus.Running;
        }

        protected override void OnAborted(BtContext c) { _iterations = 0; }
        public override void Reset() { base.Reset(); _iterations = 0; }
    }

    /// <summary>Params: <c>seconds</c>. After the child finished, Failure for that long.</summary>
    public sealed class CooldownNode : DecoratorNode
    {
        public float Seconds;
        private float _readyAt = float.NegativeInfinity;

        protected override BtStatus OnTick(BtContext c)
        {
            if (Child == null) return BtStatus.Failure;
            if (!IsRunning && c.Time < _readyAt) return BtStatus.Failure;
            var s = Child.Tick(c);
            if (s != BtStatus.Running) _readyAt = c.Time + Math.Max(0f, Seconds);
            return s;
        }

        public override void Reset() { base.Reset(); }
    }

    /// <summary>A condition task guarding an optional child. Without a child it is a leaf condition.</summary>
    public sealed class ConditionNode : DecoratorNode
    {
        public BtTask Task;
        public BtAbortMode Abort;
        public bool ObservesSelf { get { return Abort == BtAbortMode.Self || Abort == BtAbortMode.Both; } }
        public bool ObservesLower { get { return Abort == BtAbortMode.LowerPriority || Abort == BtAbortMode.Both; } }
        public bool Invert;

        /// <summary>Run the condition once (enter / tick / exit) — no tree state changes.</summary>
        public bool Evaluate(BtContext c)
        {
            if (Task == null) return false;
            bool ok;
            try
            {
                Prepare(Task, c);
                Task.Aborted = false;
                Task.OnEnter();
                ok = Task.OnTick(c.Dt) == BtStatus.Success;
                Task.OnExit();
            }
            catch (Exception ex)
            {
                if (c.Log != null) c.Log("[BehaviorTree] condition " + Label + ": " + ex.GetType().Name + ": " + ex.Message);
                ok = false;
            }
            return Invert ? !ok : ok;
        }

        protected override BtStatus OnTick(BtContext c)
        {
            if (!IsRunning || ObservesSelf)
            {
                if (!Evaluate(c))
                {
                    if (IsRunning && Child != null) Child.Abort(c);
                    return BtStatus.Failure;
                }
            }
            if (Child == null) return BtStatus.Success;
            return Child.Tick(c);
        }

        internal static void Prepare(BtTask t, BtContext c)
        {
            t.Agent = c.Agent;
            t.Blackboard = c.Blackboard;
            t.TreeTime = c.Time;
        }
    }

    // ------------------------------------------------------------------------------------------------- leaves

    public sealed class TaskNode : BtNode
    {
        public BtTask Task;

        protected override BtStatus OnTick(BtContext c)
        {
            if (Task == null) return BtStatus.Failure;
            ConditionNode.Prepare(Task, c);
            if (!IsRunning) { Task.Aborted = false; Task.OnEnter(); }
            var s = Task.OnTick(c.Dt);
            if (s != BtStatus.Running) { Task.Aborted = false; Task.OnExit(); }
            return s;
        }

        protected override void OnAborted(BtContext c)
        {
            if (Task == null) return;
            try { Task.Aborted = true; Task.OnExit(); }
            catch (Exception ex) { if (c.Log != null) c.Log("[BehaviorTree] " + Label + " OnExit: " + ex.Message); }
            finally { Task.Aborted = false; }
        }
    }

    /// <summary>A task class the tree names but nobody provides: fails, and says so once.</summary>
    public sealed class MissingTaskNode : BtNode
    {
        public string TaskName;
        private bool _said;

        protected override BtStatus OnTick(BtContext c)
        {
            if (!_said) { _said = true; if (c.Log != null) c.Log("[BehaviorTree] no task class '" + TaskName + "' (node '" + Label + "') — define it in the project's scripts or pick a built-in"); }
            return BtStatus.Failure;
        }
    }

    // ------------------------------------------------------------------------------------------------- runner

    /// <summary>
    /// One running tree: the node instances built from a <see cref="BehaviorTreeAsset"/>, the blackboard and the agent.
    /// The tree loops — when the root finishes, it restarts from the top on the next tick.
    /// </summary>
    public sealed class BehaviorTreeRunner
    {
        public BehaviorTreeAsset Asset { get; private set; }
        public string AssetPath { get; private set; }
        public long Agent { get; private set; }
        public Blackboard Blackboard { get; private set; }
        public BtNode Root { get; private set; }
        public int TickIndex { get; private set; }
        public float Time { get; private set; }
        public BtStatus LastStatus { get; private set; }
        public bool Paused;
        public bool Stopped { get; private set; }
        /// <summary>Tasks the resolver could not find (for the editor's warnings).</summary>
        public readonly List<string> MissingTasks = new List<string>();

        private readonly BtContext _ctx = new BtContext();

        public string ActiveTaskLabel { get { return Root != null ? (Root.ActiveTaskLabel() ?? "") : ""; } }

        /// <summary>Build a runner. <paramref name="resolveTask"/> maps a task class name to its type (null = missing).</summary>
        public static BehaviorTreeRunner Create(BehaviorTreeAsset asset, string assetPath, long agent, Func<string, Type> resolveTask, Action<string> log)
        {
            if (asset == null) return null;
            asset.Normalize();
            var r = new BehaviorTreeRunner { Asset = asset, AssetPath = assetPath ?? "", Agent = agent, Blackboard = new Blackboard() };
            foreach (var kv in asset.Blackboard) r.Blackboard.Set(kv.Key, kv.Value);
            r._ctx.Runner = r; r._ctx.Agent = agent; r._ctx.Blackboard = r.Blackboard; r._ctx.Log = log;
            r.Root = r.Build(asset.Root, resolveTask);
            return r;
        }

        private BtNode Build(BtNodeData d, Func<string, Type> resolveTask)
        {
            BtNode n;
            switch (d.Kind)
            {
                case BtNodeKind.Selector: n = new SelectorNode(); break;
                case BtNodeKind.Sequence: n = new SequenceNode(); break;
                case BtNodeKind.Parallel: n = new ParallelNode { AnySucceeds = ParseBool(d.Param("any", "false")) }; break;
                case BtNodeKind.Inverter: n = new InverterNode(); break;
                case BtNodeKind.Succeeder: n = new SucceederNode(); break;
                case BtNodeKind.Repeat: n = new RepeatNode { Count = ParseInt(d.Param("count", "0")), UntilFailure = ParseBool(d.Param("untilFailure", "false")) }; break;
                case BtNodeKind.Cooldown: n = new CooldownNode { Seconds = ParseFloat(d.Param("seconds", "1")) }; break;
                case BtNodeKind.Condition:
                    {
                        var task = Instantiate(d, resolveTask);
                        if (task == null) { n = new MissingTaskNode { TaskName = d.Task }; break; }
                        n = new ConditionNode { Task = task, Abort = d.Abort, Invert = ParseBool(d.Param("invert", "false")) };
                        break;
                    }
                default:
                    {
                        var task = Instantiate(d, resolveTask);
                        n = task != null ? (BtNode)new TaskNode { Task = task } : new MissingTaskNode { TaskName = d.Task };
                        break;
                    }
            }
            n.Data = d;
            if (d.Kind != BtNodeKind.Task)
                foreach (var cd in d.Children) if (cd != null) n.Children.Add(Build(cd, resolveTask));
            return n;
        }

        private BtTask Instantiate(BtNodeData d, Func<string, Type> resolveTask)
        {
            Type t = null;
            try { t = !string.IsNullOrWhiteSpace(d.Task) && resolveTask != null ? resolveTask(d.Task.Trim()) : null; } catch { }
            if (t == null) { if (!MissingTasks.Contains(d.Task ?? "")) MissingTasks.Add(d.Task ?? ""); return null; }
            try
            {
                var task = Activator.CreateInstance(t) as BtTask;
                if (task == null) return null;
                task.Parameters = d.Params;
                task.NodeName = d.Label;
                return task;
            }
            catch (Exception ex)
            {
                if (_ctx.Log != null) _ctx.Log("[BehaviorTree] cannot create task '" + d.Task + "': " + ex.Message);
                return null;
            }
        }

        /// <summary>One tick. Call once per frame (or per interval) with the elapsed seconds.</summary>
        public void Tick(float dt)
        {
            if (Stopped || Paused || Root == null) return;
            TickIndex++;
            Time += dt;
            _ctx.Dt = dt;
            _ctx.Time = Time;
            LastStatus = Root.Tick(_ctx);
            if (LastStatus != BtStatus.Running) Root.Reset();   // the tree loops
        }

        /// <summary>Abort everything that runs (OnExit with Aborted on the running tasks) and stop ticking.</summary>
        public void Stop()
        {
            if (Stopped) return;
            Stopped = true;
            try { Root?.Abort(_ctx); } catch { }
        }

        /// <summary>Restart from the top (the blackboard keeps its values).</summary>
        public void Restart()
        {
            try { Root?.Abort(_ctx); } catch { }
            Root?.Reset();
            Stopped = false;
        }

        public void Snapshot(List<BtDebugNode> into)
        {
            into.Clear();
            Root?.Snapshot(into);
        }

        private static bool ParseBool(string s) { object o; return Blackboard.TryConvert(s, typeof(bool), out o) && (bool)o; }
        private static int ParseInt(string s) { object o; return Blackboard.TryConvert(s, typeof(int), out o) ? (int)o : 0; }
        private static float ParseFloat(string s) { object o; return Blackboard.TryConvert(s, typeof(float), out o) ? (float)o : 0f; }
    }
}
