using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vortex
{
    /// <summary>What a behaviour tree node reports after a tick (#111).</summary>
    public enum BtStatus
    {
        /// <summary>Not finished — tick again next frame.</summary>
        Running,
        Success,
        Failure,
    }

    /// <summary>
    /// The shared memory of one behaviour tree run: typed values by name, read and written by the tree's tasks and by
    /// the agent's scripts (<see cref="BehaviorTree.BlackboardOf"/>). Values written as strings (the asset's defaults,
    /// the editor) are parsed on a typed read: <c>"1.5"</c> → float, <c>"true"</c> → bool, <c>"1,2,3"</c> → Vector3.
    /// </summary>
    public sealed class Blackboard
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);
        private int _version;

        /// <summary>Bumps on every write — tasks can notice a change cheaply.</summary>
        public int Version { get { return _version; } }

        public IEnumerable<string> Keys { get { return _values.Keys; } }
        public int Count { get { return _values.Count; } }

        public bool Has(string key) { return !string.IsNullOrEmpty(key) && _values.ContainsKey(key); }

        public void Set(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) return;
            _values[key] = value;
            _version++;
        }

        public bool Remove(string key)
        {
            if (string.IsNullOrEmpty(key) || !_values.Remove(key)) return false;
            _version++;
            return true;
        }

        public void Clear() { _values.Clear(); _version++; }

        public object Get(string key) { object v; return !string.IsNullOrEmpty(key) && _values.TryGetValue(key, out v) ? v : null; }

        public bool TryGet<T>(string key, out T value)
        {
            value = default(T);
            object raw = Get(key);
            if (raw == null) return false;
            if (raw is T) { value = (T)raw; return true; }
            object converted;
            if (TryConvert(raw, typeof(T), out converted)) { value = (T)converted; return true; }
            return false;
        }

        public T Get<T>(string key, T fallback = default(T)) { T v; return TryGet(key, out v) ? v : fallback; }

        public string GetString(string key, string fallback = "") { object v = Get(key); return v == null ? fallback : (v is string ? (string)v : Format(v)); }
        public float GetFloat(string key, float fallback = 0f) { return Get(key, fallback); }
        public int GetInt(string key, int fallback = 0) { return Get(key, fallback); }
        public bool GetBool(string key, bool fallback = false) { return Get(key, fallback); }
        public Vector3 GetVector3(string key, Vector3 fallback = default(Vector3)) { return Get(key, fallback); }
        /// <summary>An entity handle stored under the key (0 when none).</summary>
        public long GetEntity(string key) { return Get<long>(key, 0L); }

        /// <summary>Everything as display strings (the editor's live blackboard view).</summary>
        public IEnumerable<KeyValuePair<string, string>> Dump()
        {
            foreach (var kv in _values) yield return new KeyValuePair<string, string>(kv.Key, Format(kv.Value));
        }

        internal static string Format(object v)
        {
            if (v == null) return "";
            if (v is float) return ((float)v).ToString("0.###", CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString("0.###", CultureInfo.InvariantCulture);
            if (v is Vector3) { var p = (Vector3)v; return p.X.ToString("0.##", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.##", CultureInfo.InvariantCulture) + "," + p.Z.ToString("0.##", CultureInfo.InvariantCulture); }
            if (v is bool) return (bool)v ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>Parse a string the way typed reads do (the asset's defaults, editor edits, task parameters).</summary>
        public static bool TryConvert(object raw, Type target, out object result)
        {
            result = null;
            if (raw == null) return false;
            if (target.IsInstanceOfType(raw)) { result = raw; return true; }
            try
            {
                if (target == typeof(float)) { float f; if (raw is string ? float.TryParse(((string)raw).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) : TryNum(raw, out f)) { result = f; return true; } return false; }
                if (target == typeof(double)) { float f; if (TryConvert(raw, typeof(float), out var o)) { result = (double)(float)o; return true; } return false; }
                if (target == typeof(int)) { if (raw is string) { int i; if (int.TryParse(((string)raw).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) { result = i; return true; } float f; if (float.TryParse(((string)raw).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f)) { result = (int)f; return true; } return false; } result = Convert.ToInt32(raw, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(long)) { if (raw is string) { long l; if (long.TryParse(((string)raw).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) { result = l; return true; } return false; } result = Convert.ToInt64(raw, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(bool))
                {
                    if (raw is string) { string s = ((string)raw).Trim().ToLowerInvariant(); if (s == "true" || s == "1" || s == "yes" || s == "on") { result = true; return true; } if (s == "false" || s == "0" || s == "no" || s == "off" || s == "") { result = false; return true; } return false; }
                    result = Convert.ToBoolean(raw, CultureInfo.InvariantCulture); return true;
                }
                if (target == typeof(string)) { result = Format(raw); return true; }
                if (target == typeof(Vector3))
                {
                    if (raw is string)
                    {
                        var parts = ((string)raw).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length != 3) return false;
                        float x, y, z;
                        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                        { result = new Vector3(x, y, z); return true; }
                    }
                    return false;
                }
            }
            catch { }
            return false;
        }

        private static bool TryNum(object raw, out float f)
        {
            try { f = Convert.ToSingle(raw, CultureInfo.InvariantCulture); return true; } catch { f = 0f; return false; }
        }
    }

    /// <summary>A parameter a task understands — the editor shows one row per entry (name, default, help).</summary>
    public struct BtParam
    {
        public string Name;
        public string Default;
        public string Help;
        public BtParam(string name, string def, string help) { Name = name; Default = def; Help = help; }
    }

    /// <summary>
    /// A leaf of a behaviour tree (#111): a task (runs over frames, reports <see cref="BtStatus"/>) or a condition (the
    /// same class, used by a Condition node: answers Success / Failure at once). Write your own in the project's
    /// scripts — any public class deriving from <c>BtTask</c> is picked up by name — and build trees from them in the
    /// Behavior Tree editor. Gameplay stays in the project: the engine only ticks the tree.
    /// <code>
    /// public class ChasePlayer : BtTask
    /// {
    ///     public override void OnEnter() { Navigation.SetSpeed(Agent, ParamFloat("speed", 4f)); }
    ///     public override BtStatus OnTick(float dt)
    ///     {
    ///         long target = Blackboard.GetEntity("Target");
    ///         if (target == 0) return BtStatus.Failure;
    ///         Navigation.SetDestination(Agent, Scene.WorldPositionOf(target));
    ///         return Navigation.HasArrived(Agent) ? BtStatus.Success : BtStatus.Running;
    ///     }
    ///     public override void OnExit() { Navigation.Stop(Agent); }
    /// }
    /// </code>
    /// </summary>
    public abstract class BtTask
    {
        /// <summary>The entity the tree runs on (its handle — what <c>Navigation.*</c>, <c>Perception.*</c>, <c>Scene.*</c> take).</summary>
        public long Agent { get; internal set; }
        /// <summary>The run's shared memory.</summary>
        public Blackboard Blackboard { get; internal set; }
        /// <summary>The node's label in the tree (for log lines).</summary>
        public string NodeName { get; internal set; }
        /// <summary>Seconds since the tree started.</summary>
        public float TreeTime { get; internal set; }
        /// <summary>True while this task is the one being aborted by a conditional abort or a tree stop (read in OnExit).</summary>
        public bool Aborted { get; internal set; }

        internal IDictionary<string, string> Parameters;

        /// <summary>A raw parameter as saved in the tree (empty = <paramref name="fallback"/>).</summary>
        public string Param(string name, string fallback = "")
        {
            string v;
            return Parameters != null && name != null && Parameters.TryGetValue(name, out v) && !string.IsNullOrEmpty(v) ? v : fallback;
        }

        /// <summary>A parameter with blackboard substitution: every <c>{key}</c> in the value is replaced by the key's
        /// value as text (<c>"{Target}"</c> alone reads one key; <c>"saw {Target} at {LastKnown}"</c> builds a line).
        /// A key that is not set becomes <paramref name="fallback"/> when it is the whole value, empty text inside one.</summary>
        public string Resolve(string name, string fallback = "")
        {
            string v = Param(name, null);
            if (v == null) return fallback;
            v = v.Trim();
            if (v.IndexOf('{') < 0) return v;
            if (v.Length > 2 && v[0] == '{' && v[v.Length - 1] == '}' && v.IndexOf('{', 1) < 0)
            {
                string key = v.Substring(1, v.Length - 2);
                return Blackboard != null && Blackboard.Has(key) ? Blackboard.GetString(key, fallback) : fallback;
            }
            var sb = new System.Text.StringBuilder(v.Length + 16);
            int i = 0;
            while (i < v.Length)
            {
                int open = v.IndexOf('{', i);
                if (open < 0) { sb.Append(v, i, v.Length - i); break; }
                int close = v.IndexOf('}', open + 1);
                if (close < 0) { sb.Append(v, i, v.Length - i); break; }
                sb.Append(v, i, open - i);
                string key = v.Substring(open + 1, close - open - 1);
                if (Blackboard != null && Blackboard.Has(key)) sb.Append(Blackboard.GetString(key, ""));
                i = close + 1;
            }
            return sb.ToString();
        }

        public float ParamFloat(string name, float fallback = 0f) { object o; return Blackboard.TryConvert(Resolve(name, null), typeof(float), out o) ? (float)o : fallback; }
        public int ParamInt(string name, int fallback = 0) { object o; return Blackboard.TryConvert(Resolve(name, null), typeof(int), out o) ? (int)o : fallback; }
        public bool ParamBool(string name, bool fallback = false) { object o; return Blackboard.TryConvert(Resolve(name, null), typeof(bool), out o) ? (bool)o : fallback; }
        public Vector3 ParamVector3(string name, Vector3 fallback = default(Vector3))
        {
            string v = Param(name, null);
            if (v == null) return fallback;
            v = v.Trim();
            if (v.Length > 2 && v[0] == '{' && v[v.Length - 1] == '}') return Blackboard != null ? Blackboard.GetVector3(v.Substring(1, v.Length - 2), fallback) : fallback;
            object o; return Blackboard.TryConvert(v, typeof(Vector3), out o) ? (Vector3)o : fallback;
        }

        /// <summary>Called once when the task starts running (again after it finished and the tree re-enters it).</summary>
        public virtual void OnEnter() { }
        /// <summary>Called every tick while the task runs; Success / Failure ends it.</summary>
        public abstract BtStatus OnTick(float dt);
        /// <summary>Called when the task finished or was aborted (<see cref="Aborted"/>).</summary>
        public virtual void OnExit() { }

        /// <summary>The parameters the task reads — the editor offers a row for each (override to document yours).</summary>
        public virtual IEnumerable<BtParam> DescribeParams() { yield break; }

        /// <summary>One line for the editor's node palette.</summary>
        public virtual string Description { get { return ""; } }
    }

    /// <summary>
    /// Behaviour trees on entities (#111). A <c>Behavior Tree</c> component runs its <c>.vbt</c> from play start; scripts
    /// can also start, stop and inspect trees at runtime, and share state with them through the blackboard:
    /// <code>
    /// BehaviorTree.Run(EntityId, "Assets/AI/Monster.vbt");
    /// BehaviorTree.BlackboardOf(EntityId).Set("Target", playerId);
    /// </code>
    /// </summary>
    public static class BehaviorTree
    {
        /// <summary>Start (or replace) the tree on an entity. The path is project-relative (<c>Assets/AI/Monster.vbt</c>).
        /// False when the file is missing or invalid.</summary>
        public static bool Run(long entity, string treePath)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            return e != null && Editor.Core.Services.AI.BehaviorTreeService.Start(e, treePath);
        }

        /// <summary>Stop the entity's tree (running tasks get OnExit with Aborted = true).</summary>
        public static void Stop(long entity)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            if (e != null) Editor.Core.Services.AI.BehaviorTreeService.Stop(e);
        }

        public static bool IsRunning(long entity)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            return e != null && Editor.Core.Services.AI.BehaviorTreeService.IsRunningOn(e);
        }

        /// <summary>Pause / resume the ticks (running tasks keep their state).</summary>
        public static void SetPaused(long entity, bool paused)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            var r = e != null ? Editor.Core.Services.AI.BehaviorTreeService.RunnerOf(e) : null;
            if (r != null) r.Paused = paused;
        }

        /// <summary>The run's blackboard — null when no tree runs on the entity.</summary>
        public static Blackboard BlackboardOf(long entity)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            var r = e != null ? Editor.Core.Services.AI.BehaviorTreeService.RunnerOf(e) : null;
            return r != null ? r.Blackboard : null;
        }

        /// <summary>The project-relative path of the running tree ("" when none).</summary>
        public static string CurrentTree(long entity)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            var r = e != null ? Editor.Core.Services.AI.BehaviorTreeService.RunnerOf(e) : null;
            return r != null ? (r.AssetPath ?? "") : "";
        }

        /// <summary>The label of the task that ran last tick, or "" (debugging / HUDs).</summary>
        public static string ActiveTask(long entity)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            var r = e != null ? Editor.Core.Services.AI.BehaviorTreeService.RunnerOf(e) : null;
            return r != null ? r.ActiveTaskLabel : "";
        }
    }
}
