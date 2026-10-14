using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.ECS;
using Microsoft.Extensions.AI;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>A project file a tool call wrote: before and after (for the Operations diff).</summary>
    public sealed class FileChange
    {
        public string Path { get; init; }
        public bool Created { get; init; }
        public string Before { get; init; }
        public string After { get; init; }
    }

    /// <summary>State of the tool call that is running (calls run one at a time on the UI thread).</summary>
    public static class ToolContext
    {
        /// <summary>Name of the undo step for the running call — tools set it when they can say it better than their
        /// title ("Claude: scatter 40 debris props").</summary>
        public static string UndoLabel { get; set; }

        /// <summary>Who called: "mcp" (Claude Code / Desktop over the MCP server) or "panel" (the embedded chat).</summary>
        public static string Origin { get; internal set; }

        internal static HashSet<Guid> Touched { get; private set; } = new HashSet<Guid>();
        internal static List<FileChange> Files { get; private set; } = new List<FileChange>();
        /// <summary>The scenes of the entities the call touched — whether Stop reverts them decides its runtime_only (#423).</summary>
        internal static HashSet<Scene> TouchedScenes { get; private set; } = new HashSet<Scene>();

        /// <summary>The running call is a dry run: everything it changes is taken back afterwards, so it must not reach
        /// anything the undo does not cover — the running game (#405).</summary>
        public static bool DryRun { get; internal set; }

        private static bool _running;
        private static readonly List<(string Key, Action Run)> _afterSuccess = new List<(string, Action)>();

        /// <summary>Run <paramref name="action"/> once the call has succeeded and its undo step stands — not after a failed
        /// call (rolled back) or a dry run. One action per <paramref name="key"/> and call (#405: field values reach the
        /// running behaviour once, after every field of the call was valid). Outside a call it runs at once.</summary>
        public static void AfterSuccess(string key, Action action)
        {
            if (action == null) return;
            if (!_running) { action(); return; }
            if (_afterSuccess.Exists(a => a.Key == key)) return;
            _afterSuccess.Add((key, action));
        }

        /// <summary>The running call referenced or created this entity (dependency check of Revert).</summary>
        public static void Touch(GameEntity e)
        {
            if (e == null) return;
            Touched.Add(e.Id);
            if (e.Scene != null) TouchedScenes.Add(e.Scene);
        }

        internal static void FileWritten(FileChange f) => Files.Add(f);

        internal static void Begin(string origin, bool dryRun)
        {
            Reset(origin);
            DryRun = dryRun;
            _running = true;
        }

        /// <summary>The call is over: its after-success actions run when it succeeded and was no dry run.</summary>
        internal static void End(bool succeeded)
        {
            var actions = _afterSuccess.ToList();
            bool run = succeeded && !DryRun;
            Reset(null);
            if (!run) return;
            foreach (var a in actions)
            {
                try { a.Run(); }
                catch (Exception ex) { ConsoleService.Instance.LogError("Claude tool: " + a.Key + " failed after the call: " + ex.Message); }
            }
        }

        private static void Reset(string origin)
        {
            Origin = origin;
            UndoLabel = null;
            Touched = new HashSet<Guid>();
            Files = new List<FileChange>();
            TouchedScenes = new HashSet<Scene>();
            DryRun = false;
            _running = false;
            _afterSuccess.Clear();
        }
    }

    /// <summary>One tool call, as the Operations list shows it.</summary>
    public sealed class ToolOperation
    {
        public DateTime Time { get; init; }
        public string Tool { get; init; }
        public string Origin { get; init; }
        public string Arguments { get; init; }
        public string Result { get; set; }
        public bool IsError { get; set; }
        public bool DryRun { get; set; }
        public double Milliseconds { get; set; }
        /// <summary>The undo step the call produced (null for reads, dry runs and failed calls).</summary>
        public IUndoableCommand UndoStep { get; set; }
        /// <summary>Entities the call referenced or created.</summary>
        public HashSet<Guid> Entities { get; set; } = new HashSet<Guid>();
        /// <summary>Project files the call wrote.</summary>
        public List<FileChange> Files { get; set; } = new List<FileChange>();
        /// <summary>Taken back from the Operations list.</summary>
        public bool Reverted { get; set; }

        /// <summary>Can it still be reverted (its undo step is on the undo stack)?</summary>
        public bool CanRevert => !Reverted && UndoStep != null && UndoRedoManager.Instance.IsOnUndoStack(UndoStep);
    }

    /// <summary>The recent tool calls of this editor session (newest last), for the Operations list and the status bar.</summary>
    public static class OperationLog
    {
        public const int Max = 500;
        private static readonly List<ToolOperation> _items = new List<ToolOperation>();

        public static IReadOnlyList<ToolOperation> Items => _items;
        public static event Action<ToolOperation> Added;
        public static event Action Changed;

        internal static void Add(ToolOperation op)
        {
            _items.Add(op);
            if (_items.Count > Max) _items.RemoveRange(0, _items.Count - Max);
            try { Added?.Invoke(op); } catch { }
            try { Changed?.Invoke(); } catch { }
        }

        /// <summary>Later, still-active operations that touched the same entities or files — reverting
        /// <paramref name="op"/> out of order may break them.</summary>
        public static List<ToolOperation> DependentsOf(ToolOperation op)
        {
            int i = _items.IndexOf(op);
            if (i < 0) return new List<ToolOperation>();
            var paths = new HashSet<string>(op.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            return _items.Skip(i + 1)
                .Where(o => o.CanRevert && (o.Entities.Overlaps(op.Entities) || o.Files.Any(f => paths.Contains(f.Path))))
                .ToList();
        }

        /// <summary>Take one operation back: the newest like Undo, an older one out of order.</summary>
        public static bool Revert(ToolOperation op)
        {
            if (op == null || !op.CanRevert) return false;
            if (!UndoRedoManager.Instance.Revert(op.UndoStep)) return false;
            op.Reverted = true;
            try { EditorCommands.AfterSceneEdit(); } catch { }
            try { Changed?.Invoke(); } catch { }
            return true;
        }
    }

    /// <summary>
    /// Runs Vortex tools for every client. A call:
    /// <list type="bullet">
    /// <item>waits for the previous one (one at a time — parallel tool calls would interleave their undo steps),</item>
    /// <item>runs on the UI thread (the scene, the undo stack and the viewport live there),</item>
    /// <item>is ONE undo step named "Claude: …" however many entities it touches — and a call that fails half-way is
    /// rolled back completely,</item>
    /// <item>with <c>dry_run: true</c> (every tool that changes the project) runs and is rolled back, returning what it
    /// would create, remove, change and write,</item>
    /// <item>lands in the <see cref="OperationLog"/> with the entities and files it touched.</item>
    /// </list>
    /// Expected failures (<see cref="ToolError"/>, bad arguments) come back as error results the model can read and fix.
    /// </summary>
    public static class ToolHost
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static Task<ToolResult> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> args, string origin, CancellationToken ct = default)
            => CallAsync(name, args?.ToDictionary(kv => kv.Key, kv => (object)kv.Value), origin, ct);

        public static async Task<ToolResult> CallAsync(string name, IDictionary<string, object> args, string origin, CancellationToken ct = default)
        {
            var def = ToolCatalog.Find(name);
            if (def == null)
                return ToolResult.Error("Unknown tool '" + name + "'. Available: " + string.Join(", ", ToolCatalog.All.Select(t => t.Name)));
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await Dispatcher.UIThread.InvokeAsync(() => RunAsync(def, args ?? new Dictionary<string, object>(), origin, ct));
            }
            finally { Gate.Release(); }
        }

        /// <summary>The error for argument keys the tool's schema does not know (null when all are known), with the
        /// nearest parameter as a hint and the full parameter list.</summary>
        internal static string UnknownArguments(ToolDef def, IDictionary<string, object> args)
        {
            if (def == null || args == null || args.Count == 0) return null;
            var known = new List<string>();
            try
            {
                if (def.InputSchema.ValueKind == JsonValueKind.Object && def.InputSchema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                    foreach (var p in props.EnumerateObject()) known.Add(p.Name);
            }
            catch { }
            if (known.Count == 0) return null;   // a schema that cannot be read: the function decides
            List<string> bad = null;
            foreach (var key in args.Keys)
                if (!known.Contains(key, StringComparer.Ordinal)) (bad ??= new List<string>()).Add(key);
            if (bad == null) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var b in bad)
            {
                sb.Append("Unknown parameter '").Append(b).Append("' for ").Append(def.Name);
                string hint = NearestParameter(b, known);
                if (hint != null) sb.Append(" — did you mean '").Append(hint).Append("'?");
                if (b == "dry_run") sb.Append(" (this tool has no dry run; nothing was executed)");
                sb.Append(". ");
            }
            sb.Append("Parameters: ").Append(string.Join(", ", known)).Append('.');
            return sb.ToString();
        }

        private static readonly Dictionary<string, string> ArgumentAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["primitive"] = "kind", ["type"] = "kind", ["shape"] = "kind", ["target"] = "look_at", ["lookat"] = "look_at",
            ["pos"] = "position", ["rot"] = "rotation", ["id"] = "entity", ["entities"] = "entity", ["names"] = "name",
        };

        private static string NearestParameter(string key, List<string> known)
        {
            if (ArgumentAliases.TryGetValue(key, out var alias) && known.Contains(alias, StringComparer.Ordinal)) return alias;
            string best = null; int bestD = int.MaxValue;
            foreach (var k in known)
            {
                if (k.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return k;
                int d = Levenshtein(key.ToLowerInvariant(), k.ToLowerInvariant());
                if (d < bestD) { bestD = d; best = k; }
            }
            return best != null && bestD <= Math.Max(2, key.Length / 3) ? best : null;
        }

        private static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        private static async Task<ToolResult> RunAsync(ToolDef def, IDictionary<string, object> args, string origin, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            // an unknown top-level argument is an error, not a silent default (#355): create_entity {"primitive": "Cube"}
            // used to make an empty entity, focus_camera {"target": …} kept the old yaw, and dry_run on a tool without
            // one ran for real
            string unknown = UnknownArguments(def, args);
            if (unknown != null) return ToolResult.Error(unknown);
            bool dryRun = def.GenericDryRun && args.TryGetValue("dry_run", out var dv) && IsTrue(dv);
            if (def.GenericDryRun) args.Remove("dry_run");
            var op = new ToolOperation { Time = DateTime.Now, Tool = def.Name, Origin = origin, Arguments = Shorten(ToolJson.Serialize(args), 240), DryRun = dryRun };
            var undo = UndoRedoManager.Instance;
            bool grouped = !def.ReadOnly && !def.ManagesUndo;
            bool playingAtStart = PlayModeService.Instance.IsPlaying;
            var startUtc = DateTime.UtcNow;
            ToolContext.Begin(origin, dryRun);
            HashSet<Guid> before = dryRun ? AllIds() : null;
            if (grouped) undo.BeginGroup("Claude: " + def.Title);
            ToolResult result;
            try
            {
                object raw = await def.Function.InvokeAsync(new AIFunctionArguments(args), ct);
                result = ToolResult.From(raw);
            }
            catch (ToolError e) { result = ToolResult.Error(e.Message); }
            catch (OperationCanceledException) { result = ToolResult.Error(def.Name + " was cancelled."); }
            catch (Exception e) when (e is ArgumentException || e is JsonException || e is FormatException || e is InvalidCastException)
            {
                result = ToolResult.Error("Invalid arguments for " + def.Name + ": " + e.Message);
            }
            catch (Exception e)
            {
                ConsoleService.Instance.LogError("Claude tool " + def.Name + " failed: " + e);
                result = ToolResult.Error(def.Name + " failed: " + e.Message);
            }
            if (grouped)
            {
                if (dryRun && !result.IsError)
                {
                    // describe what happened, then take all of it back
                    var plan = Plan(def, before, result, undo);
                    undo.EndGroup(commit: false);
                    result = ToolResult.Json(plan);
                }
                else
                {
                    // a successful call is one named undo step; a failed one leaves no trace
                    if (ToolContext.UndoLabel != null) undo.RenameGroup("Claude: " + ToolContext.UndoLabel);
                    op.UndoStep = undo.EndGroup(commit: !result.IsError);
                }
                try { EditorCommands.AfterSceneEdit(); } catch { }
            }
            // while the game runs every change says what Stop does with it — set here, once, for every tool (#423)
            if (playingAtStart && PlayModeService.Instance.IsPlaying && !def.ReadOnly && !dryRun && !result.IsError)
                result = PlayModeNote(result, startUtc);
            op.Entities = ToolContext.Touched;
            op.Files = dryRun || result.IsError ? new List<FileChange>() : ToolContext.Files;
            op.Result = result.Summary();
            op.IsError = result.IsError;
            op.Milliseconds = sw.Elapsed.TotalMilliseconds;
            ToolContext.End(succeeded: !result.IsError);
            OperationLog.Add(op);
            return result;
        }

        internal const string RevertedOnStop = "play mode: live only, reverted on Stop";

        /// <summary>
        /// A change made while the game runs or is paused says what Stop does with it (#423). runtime_only is true when Stop
        /// takes all of it back — the play snapshot puts every scene that was loaded at Play back, and a data file play created
        /// for an entity that goes is deleted with it — and false, with the reason, when part of it stays: files written, or a
        /// scene that was only loaded while playing. Flat fields on an object result (a tool's own note is kept in front), a
        /// second text block after any other, so a result has the shape it has in edit mode.
        /// </summary>
        internal static ToolResult PlayModeNote(ToolResult result, DateTime sinceUtc)
        {
            var project = ProjectData.Current;
            var scenes = new HashSet<Scene>(ToolContext.TouchedScenes);
            if (project?.ActiveScene != null) scenes.Add(project.ActiveScene);   // the scene the tools create in
            var reasons = new List<string>();
            foreach (var s in scenes)
                if (!Editor.Core.Viewport.EditorViewportSession.RevertsOnStop(s)) reasons.Add("'" + s.Name + "' was loaded while playing — Stop does not put it back");
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in ToolContext.Files) { try { if (f?.Path != null) files.Add(System.IO.Path.GetFullPath(f.Path)); } catch { } }
            foreach (var f in PlaySession.FilesWrittenSince(project?.Path, sinceUtc)) files.Add(f);
            var stays = files.Where(f => !PlaySession.CreatedDataFile(f)).Select(System.IO.Path.GetFileName).ToList();
            if (stays.Count > 0)
                reasons.Add(stays.Count + (stays.Count == 1 ? " file" : " files") + " written stay on disk (" + string.Join(", ", stays.Take(3)) + (stays.Count > 3 ? ", …" : "") + ")");
            bool reverted = reasons.Count == 0;
            string note = reverted ? RevertedOnStop : "play mode: Stop does not take all of this back — " + string.Join("; ", reasons);

            var first = result.Content.FirstOrDefault(c => c.Text != null);
            System.Text.Json.Nodes.JsonObject obj = null;
            if (first != null) { try { obj = System.Text.Json.Nodes.JsonNode.Parse(first.Text) as System.Text.Json.Nodes.JsonObject; } catch { } }
            var annotated = new ToolResult { IsError = result.IsError };
            foreach (var c in result.Content)
            {
                if (obj == null || !ReferenceEquals(c, first)) { annotated.Content.Add(c); continue; }
                string own = obj["note"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string text) ? text : null;
                obj["runtime_only"] = reverted;
                if (obj["note"] != null && own == null) obj["play_note"] = note;   // a tool's own structured note stays as it is
                else obj["note"] = string.IsNullOrEmpty(own) ? note : own + "; " + note;
                annotated.Content.Add(new ToolContent { Text = obj.ToJsonString(ToolJson.Options) });
            }
            if (obj == null) annotated.Content.Add(new ToolContent { Text = "runtime_only: " + (reverted ? "true" : "false") + " — " + note });
            return annotated;
        }

        private static bool IsTrue(object v) => v switch
        {
            bool b => b,
            JsonElement e => e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.String && string.Equals(e.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        private static HashSet<Guid> AllIds()
        {
            var set = new HashSet<Guid>();
            var p = ProjectData.Current;
            if (p?.Scenes == null) return set;
            foreach (var s in p.Scenes)
                if (s?.IsLoaded == true) foreach (var e in SceneModel.All(s)) set.Add(e.Id);
            return set;
        }

        /// <summary>What a call did, from the scene before/after and its undo group — the dry-run answer.</summary>
        private static object Plan(ToolDef def, HashSet<Guid> before, ToolResult result, UndoRedoManager undo)
        {
            var after = new Dictionary<Guid, GameEntity>();
            var p = ProjectData.Current;
            if (p?.Scenes != null)
                foreach (var s in p.Scenes)
                    if (s?.IsLoaded == true) foreach (var e in SceneModel.All(s)) after[e.Id] = e;
            var created = after.Values.Where(e => !before.Contains(e.Id)).ToList();
            var removed = before.Where(id => !after.ContainsKey(id)).ToList();
            var changed = ToolContext.Touched.Where(id => before.Contains(id) && after.ContainsKey(id)).Select(id => after[id]).ToList();
            var steps = new Dictionary<string, int>();
            void Count(IUndoableCommand c)
            {
                if (c is CompositeCommand cc) { foreach (var x in cc.Commands) Count(x); return; }
                if (c == null) return;
                steps[c.Name] = steps.TryGetValue(c.Name, out int n) ? n + 1 : 1;
            }
            Count(undo.CurrentGroup);
            return new
            {
                dry_run = true,
                tool = def.Name,
                nothing_changed = true,
                would_create = created.Count == 0 ? null : created.Take(40).Select(e => SceneModel.ShortId(e) + " " + SceneModel.PathOf(e)).ToArray(),
                would_create_count = created.Count > 40 ? (int?)created.Count : null,
                would_remove = removed.Count == 0 ? null : (int?)removed.Count,
                would_change = changed.Count == 0 ? null : changed.Take(40).Select(e => SceneModel.ShortId(e) + " " + SceneModel.PathOf(e)).ToArray(),
                files = ToolContext.Files.Count == 0 ? null : ToolContext.Files.Select(f => (f.Created ? "create " : "update ") + ProjectFiles.Rel(f.Path)).ToArray(),
                edits = steps.Count == 0 ? null : steps,
                would_return = Shorten(result.Summary(2000), 2000),
            };
        }

        internal static string Shorten(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
