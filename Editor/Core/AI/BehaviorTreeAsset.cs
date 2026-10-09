using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Editor.Core.AI
{
    /// <summary>The node kinds a behaviour tree is built from (#111). Composites and decorators are the engine's; leaf
    /// tasks and conditions name a <c>Vortex.BtTask</c> class — one of the built-ins or a class in the project's scripts.</summary>
    public enum BtNodeKind
    {
        /// <summary>Ticks children in order until one succeeds (or runs). Fails when every child failed.</summary>
        Selector,
        /// <summary>Ticks children in order until one fails (or runs). Succeeds when every child succeeded.</summary>
        Sequence,
        /// <summary>Ticks every child each tick: succeeds when all succeeded, fails as soon as one fails.</summary>
        Parallel,
        /// <summary>One child; swaps Success and Failure.</summary>
        Inverter,
        /// <summary>One child; always Success once the child finished.</summary>
        Succeeder,
        /// <summary>One child; restarts it when it finished — <c>count</c> times (0 = forever), stops early on Failure
        /// when <c>untilFailure</c>.</summary>
        Repeat,
        /// <summary>One child; after the child finished, Failure for <c>seconds</c> before it may run again.</summary>
        Cooldown,
        /// <summary>One child guarded by a condition task: the child only runs while the condition succeeds. With an
        /// <see cref="BtNodeData.Abort"/> mode the condition is observed every tick (conditional aborts).</summary>
        Condition,
        /// <summary>A leaf: the named task runs until it reports Success or Failure.</summary>
        Task,
    }

    /// <summary>Which running branch a <see cref="BtNodeKind.Condition"/> node may abort when its condition changes.</summary>
    public enum BtAbortMode
    {
        /// <summary>The condition is checked once, when the node is entered.</summary>
        None,
        /// <summary>Re-checked while its OWN child runs; a failing condition aborts the child.</summary>
        Self,
        /// <summary>Re-checked while a LATER sibling runs; a passing condition aborts that sibling and the parent
        /// resumes at this node (Selector) — or a failing one fails the parent (Sequence).</summary>
        LowerPriority,
        /// <summary>Both of the above.</summary>
        Both,
    }

    /// <summary>One node of a saved tree. <see cref="Params"/> are the task's parameters as strings (the task reads
    /// them typed); <see cref="Children"/> is the ordered child list (one child for decorators, any for composites).</summary>
    public class BtNodeData
    {
        public string Id { get; set; } = "";
        public BtNodeKind Kind { get; set; } = BtNodeKind.Task;
        /// <summary>A label shown in the editor; empty = the kind / task name.</summary>
        public string Name { get; set; } = "";
        /// <summary>Task and Condition nodes: the <c>Vortex.BtTask</c> class name (simple name, no namespace).</summary>
        public string Task { get; set; } = "";
        public Dictionary<string, string> Params { get; set; } = new Dictionary<string, string>();
        public BtAbortMode Abort { get; set; } = BtAbortMode.None;
        public List<BtNodeData> Children { get; set; } = new List<BtNodeData>();
        /// <summary>Editor note (free text).</summary>
        public string Comment { get; set; }

        public string Param(string key, string fallback = "")
            => Params != null && Params.TryGetValue(key, out var v) && v != null ? v : fallback;

        public BtNodeData Clone() => BehaviorTreeAsset.FromJson(JsonSerializer.Serialize(this, BehaviorTreeAsset.JsonOptions), typeof(BtNodeData)) as BtNodeData;

        /// <summary>The label the editor and the debug view show.</summary>
        public string Label
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name)) return Name;
                return Kind == BtNodeKind.Task || Kind == BtNodeKind.Condition ? (string.IsNullOrEmpty(Task) ? Kind.ToString() : Task) : Kind.ToString();
            }
        }
    }

    /// <summary>
    /// A behaviour tree asset (<c>.vbt</c>, JSON next to the other project assets): a root node, default blackboard
    /// values and a name. Loaded by the editor (loose file) and by games (from the pak) through <see cref="Load"/>.
    /// The runtime (<see cref="BehaviorTreeRunner"/>) instantiates nodes from it; the editor edits it in place.
    /// </summary>
    public class BehaviorTreeAsset
    {
        public const string Extension = ".vbt";

        public string Name { get; set; } = "New Behavior Tree";
        public int Version { get; set; } = 1;
        public string Description { get; set; }
        /// <summary>Default blackboard values (strings; the runtime parses numbers / bools / vectors on read).</summary>
        public Dictionary<string, string> Blackboard { get; set; } = new Dictionary<string, string>();
        public BtNodeData Root { get; set; }

        private static JsonSerializerOptions _options;
        /// <summary>camelCase, enums by name, nulls omitted, indented (diff-friendly .vbt files).</summary>
        public static JsonSerializerOptions JsonOptions
        {
            get
            {
                if (_options != null) return _options;
                var o = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true,
                };
                o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                return _options = o;
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
        public static BehaviorTreeAsset FromJson(string json)
            => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<BehaviorTreeAsset>(json, JsonOptions);
        internal static object FromJson(string json, Type t) => JsonSerializer.Deserialize(json, t, JsonOptions);
        public BehaviorTreeAsset Clone() => FromJson(ToJson());

        /// <summary>Load a .vbt (shipped game: from the mounted pak; editor: loose file). Null when missing or invalid.</summary>
        public static BehaviorTreeAsset Load(string filePath)
        {
            try
            {
                string json = ReadText(filePath);
                if (json == null) return null;
                var a = FromJson(json);
                a?.Normalize();
                return a;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BehaviorTree] load failed " + filePath + ": " + ex.Message);
                return null;
            }
        }

        public bool Save(string filePath)
        {
            try
            {
                Normalize();
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, ToJson());
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BehaviorTree] save failed " + filePath + ": " + ex.Message);
                return false;
            }
        }

        public static string ReadText(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            if (Editor.Core.Services.AssetVfs.IsMounted && Editor.Core.Services.AssetVfs.Contains(filePath))
                return Editor.Core.Services.AssetVfs.GetText(filePath);
            return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }

        /// <summary>A root for a new tree: a Selector with nothing under it.</summary>
        public static BehaviorTreeAsset NewDefault(string name)
        {
            var a = new BehaviorTreeAsset { Name = name, Root = new BtNodeData { Kind = BtNodeKind.Selector, Name = "Root" } };
            a.Normalize();
            return a;
        }

        /// <summary>Every node gets a stable id; null lists become empty ones; decorators keep at most one child.</summary>
        public void Normalize()
        {
            if (Blackboard == null) Blackboard = new Dictionary<string, string>();
            if (Root == null) Root = new BtNodeData { Kind = BtNodeKind.Selector, Name = "Root" };
            var seen = new HashSet<string>();
            NormalizeNode(Root, seen);
        }

        private static void NormalizeNode(BtNodeData n, HashSet<string> seen)
        {
            if (n.Params == null) n.Params = new Dictionary<string, string>();
            if (n.Children == null) n.Children = new List<BtNodeData>();
            if (string.IsNullOrEmpty(n.Id) || !seen.Add(n.Id)) { n.Id = NewId(); seen.Add(n.Id); }
            if (IsDecorator(n.Kind) && n.Children.Count > 1) n.Children.RemoveRange(1, n.Children.Count - 1);
            if (n.Kind == BtNodeKind.Task && n.Children.Count > 0) n.Children.Clear();
            foreach (var c in n.Children) if (c != null) NormalizeNode(c, seen);
            n.Children.RemoveAll(c => c == null);
        }

        public static bool IsDecorator(BtNodeKind k)
            => k == BtNodeKind.Inverter || k == BtNodeKind.Succeeder || k == BtNodeKind.Repeat || k == BtNodeKind.Cooldown || k == BtNodeKind.Condition;

        public static bool IsComposite(BtNodeKind k)
            => k == BtNodeKind.Selector || k == BtNodeKind.Sequence || k == BtNodeKind.Parallel;

        public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>Depth-first enumeration of every node.</summary>
        public IEnumerable<BtNodeData> AllNodes()
        {
            var stack = new Stack<BtNodeData>();
            if (Root != null) stack.Push(Root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                if (n.Children == null) continue;
                for (int i = n.Children.Count - 1; i >= 0; i--) if (n.Children[i] != null) stack.Push(n.Children[i]);
            }
        }

        /// <summary>The node with this id, or null.</summary>
        public BtNodeData Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var n in AllNodes()) if (n.Id == id) return n;
            return null;
        }

        /// <summary>The parent of the node with this id (null for the root / unknown).</summary>
        public BtNodeData ParentOf(string id)
        {
            foreach (var n in AllNodes())
                if (n.Children != null) foreach (var c in n.Children) if (c != null && c.Id == id) return n;
            return null;
        }
    }
}
