using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Editor.Core.AI;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components.AI;
using Vortex;
using Scene = Editor.Core.Data.Scene;

namespace Editor.Core.Services.AI
{
    /// <summary>
    /// The behaviour trees of the running game (#111): one <see cref="BehaviorTreeRunner"/> per entity, started from the
    /// <see cref="BehaviorTreeAgent"/> components at play start (or by <c>BehaviorTree.Run</c>), ticked by
    /// <see cref="AiRuntime"/> after perception and before the navigation tick, ended with the run. Task classes are
    /// resolved by simple name: the project's scripts first (so a project can override a built-in), then the engine's
    /// <c>Vortex.Tasks</c>, then anything registered with <see cref="RegisterTaskType"/>.
    /// </summary>
    public static class BehaviorTreeService
    {
        private sealed class Entry
        {
            public BehaviorTreeRunner Runner;
            public BehaviorTreeAgent Agent;
            public float Accumulator;
        }

        private static readonly Dictionary<GameEntity, Entry> _entries = new Dictionary<GameEntity, Entry>();
        private static readonly Dictionary<string, Type> _registered = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static Dictionary<string, Type> _builtins;
        private static Scene _scene;

        public static bool IsRunning { get; private set; }
        public static int Count => _entries.Count;

        /// <summary>Where log lines go (the console in the editor and the player). Null = Debug output.</summary>
        public static Action<string> Logger;

        // ------------------------------------------------------------------------------------------ lifecycle

        /// <summary>Play start: a runner for every enabled Behavior Tree component with RunOnStart.</summary>
        public static void Begin(Scene scene)
        {
            End();
            _scene = scene;
            IsRunning = true;
            if (scene?.Entities == null) return;
            foreach (var e in scene.Entities) BeginRecursive(e);
        }

        private static void BeginRecursive(GameEntity e)
        {
            if (e == null || !e.IsActive) return;
            var agent = e.GetComponent<BehaviorTreeAgent>();
            if (agent != null && agent.IsEnabled && agent.RunOnStart && !string.IsNullOrWhiteSpace(agent.TreePath)) Start(e, agent.TreePath);
            if (e.Children != null) foreach (var c in e.Children) BeginRecursive(c);
        }

        /// <summary>One frame: every runner ticks (at its component's interval when it has one).</summary>
        public static void Tick(float dt)
        {
            if (!IsRunning || _entries.Count == 0) return;
            List<GameEntity> dead = null;
            foreach (var kv in _entries)
            {
                var en = kv.Value;
                if (en.Runner == null || en.Runner.Stopped) { (dead ??= new List<GameEntity>()).Add(kv.Key); continue; }
                if (kv.Key.Scene != null && !kv.Key.ActiveInHierarchy) continue;
                float interval = en.Agent != null ? en.Agent.TickInterval : 0f;
                if (interval > 0f)
                {
                    en.Accumulator += dt;
                    if (en.Accumulator < interval) continue;
                    float step = en.Accumulator; en.Accumulator = 0f;
                    en.Runner.Tick(step);
                }
                else en.Runner.Tick(dt);
            }
            if (dead != null) foreach (var e in dead) _entries.Remove(e);
        }

        /// <summary>Play stop / scene switch: every running task gets OnExit (Aborted), the runners go.</summary>
        public static void End()
        {
            foreach (var kv in _entries) { try { kv.Value.Runner?.Stop(); } catch { } }
            _entries.Clear();
            _scene = null;
            IsRunning = false;
        }

        // ------------------------------------------------------------------------------------------ per entity

        /// <summary>Start (or replace) a tree on an entity. <paramref name="treePath"/> is project-relative or absolute.</summary>
        public static bool Start(GameEntity e, string treePath)
        {
            if (e == null || string.IsNullOrWhiteSpace(treePath)) return false;
            string full = ResolvePath(treePath);
            var asset = BehaviorTreeAsset.Load(full);
            if (asset == null) { LogLine("[BehaviorTree] '" + treePath + "' not found or invalid (" + e.Name + ")"); return false; }
            Stop(e);
            long handle = Editor.Scripting.ScriptRuntime.Instance.HandleForEntity(e);
            var agent = e.GetComponent<BehaviorTreeAgent>();
            var runner = BehaviorTreeRunner.Create(asset, Relative(treePath), handle, ResolveTask, LogLine);
            if (runner == null) return false;
            foreach (var m in runner.MissingTasks) LogLine("[BehaviorTree] " + Path.GetFileName(treePath) + ": no task class '" + m + "' — the node fails");
            _entries[e] = new Entry { Runner = runner, Agent = agent };
            if (!IsRunning) IsRunning = true;   // a script started a tree before/without Begin — tick it anyway
            return true;
        }

        public static void Stop(GameEntity e)
        {
            if (e == null) return;
            Entry en;
            if (!_entries.TryGetValue(e, out en)) return;
            try { en.Runner?.Stop(); } catch { }
            _entries.Remove(e);
        }

        public static bool IsRunningOn(GameEntity e) => e != null && _entries.ContainsKey(e);

        public static BehaviorTreeRunner RunnerOf(GameEntity e)
        {
            Entry en;
            return e != null && _entries.TryGetValue(e, out en) ? en.Runner : null;
        }

        /// <summary>Every (entity, runner) of the run — the editor's live view.</summary>
        public static IEnumerable<KeyValuePair<GameEntity, BehaviorTreeRunner>> Runners()
        {
            foreach (var kv in _entries) yield return new KeyValuePair<GameEntity, BehaviorTreeRunner>(kv.Key, kv.Value.Runner);
        }

        /// <summary>The first runner of a tree asset (by project-relative path), or null — the editor highlights it.</summary>
        public static BehaviorTreeRunner FirstRunnerOf(string treePath)
        {
            string rel = Relative(treePath);
            foreach (var kv in _entries)
                if (kv.Value.Runner != null && string.Equals(Relative(kv.Value.Runner.AssetPath), rel, StringComparison.OrdinalIgnoreCase)) return kv.Value.Runner;
            return null;
        }

        /// <summary>An entity left the scene (runtime Destroy): its tree goes with it.</summary>
        public static void RemoveEntity(GameEntity root)
        {
            if (root == null || _entries.Count == 0) return;
            Stop(root);
            if (root.Children != null) foreach (var c in root.Children) RemoveEntity(c);
        }

        // ------------------------------------------------------------------------------------------ task types

        /// <summary>Make a task class available by name (engine extensions, tests). Project scripts need no registration.</summary>
        public static void RegisterTaskType(Type t)
        {
            if (t == null || !typeof(BtTask).IsAssignableFrom(t) || t.IsAbstract) return;
            _registered[t.Name] = t;
        }

        public static void UnregisterTaskType(string name) { if (name != null) _registered.Remove(name); }

        /// <summary>Scripts first, then the built-ins, then registered types. Null when nobody has the class.</summary>
        public static Type ResolveTask(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            Type t = null;
            try { t = Editor.Scripting.ScriptRuntime.Instance?.FindBtTaskType(name); } catch { }
            if (t != null) return t;
            if (Builtins.TryGetValue(name, out t)) return t;
            if (_registered.TryGetValue(name, out t)) return t;
            return null;
        }

        /// <summary>The engine's own tasks (<c>Vortex.Tasks</c>), by simple name.</summary>
        public static Dictionary<string, Type> Builtins
        {
            get
            {
                if (_builtins != null) return _builtins;
                var d = new Dictionary<string, Type>(StringComparer.Ordinal);
                try
                {
                    foreach (var t in typeof(BtTask).Assembly.GetTypes())
                        if (!t.IsAbstract && typeof(BtTask).IsAssignableFrom(t) && t.Namespace == "Vortex.Tasks") d[t.Name] = t;
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (var t in ex.Types) if (t != null && !t.IsAbstract && typeof(BtTask).IsAssignableFrom(t) && t.Namespace == "Vortex.Tasks") d[t.Name] = t;
                }
                return _builtins = d;
            }
        }

        /// <summary>Every task class the editor can offer: built-ins + the project's script tasks (compiled on demand).</summary>
        public static List<Type> AvailableTaskTypes()
        {
            var list = new List<Type>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var t in Editor.Scripting.ScriptRuntime.Instance?.BtTaskTypes() ?? Enumerable.Empty<Type>())
                    if (seen.Add(t.Name)) list.Add(t);
            }
            catch { }
            foreach (var kv in Builtins) if (seen.Add(kv.Key)) list.Add(kv.Value);
            foreach (var kv in _registered) if (seen.Add(kv.Key)) list.Add(kv.Value);
            return list;
        }

        /// <summary>A throw-away instance for the editor (parameter descriptions, the palette text).</summary>
        public static BtTask Describe(Type t)
        {
            try { return t != null ? Activator.CreateInstance(t) as BtTask : null; } catch { return null; }
        }

        // ------------------------------------------------------------------------------------------ helpers

        public static string ResolvePath(string treePath)
        {
            if (string.IsNullOrEmpty(treePath)) return treePath;
            if (Path.IsPathRooted(treePath)) return treePath;
            string root = ProjectData.Current?.Path;
            return string.IsNullOrEmpty(root) ? treePath : Path.Combine(root, treePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        }

        public static string Relative(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string root = ProjectData.Current?.Path;
            string p = path.Replace('\\', '/');
            if (!string.IsNullOrEmpty(root))
            {
                string r = root.Replace('\\', '/').TrimEnd('/') + "/";
                if (p.StartsWith(r, StringComparison.OrdinalIgnoreCase)) p = p.Substring(r.Length);
            }
            return p;
        }

        public static void LogLine(string line)
        {
            try
            {
                if (Logger != null) Logger(line);
                else ConsoleService.Instance.Log(line);
            }
            catch { System.Diagnostics.Debug.WriteLine(line); }
        }
    }
}
