using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Microsoft.Extensions.AI;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>State of the tool call that is running (calls run one at a time on the UI thread).</summary>
    public static class ToolContext
    {
        /// <summary>Name of the undo step for the running call — tools set it when they can say it better than their
        /// title ("Claude: scatter 40 debris props").</summary>
        public static string UndoLabel { get; set; }

        /// <summary>Who called: "mcp" (Claude Code / Desktop over the MCP server) or "panel" (the embedded chat).</summary>
        public static string Origin { get; internal set; }
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
        public double Milliseconds { get; set; }
        /// <summary>The undo step the call produced (null for reads, dry runs and failed calls).</summary>
        public IUndoableCommand UndoStep { get; set; }
    }

    /// <summary>The recent tool calls of this editor session (newest last), for the Operations list and the status bar.</summary>
    public static class OperationLog
    {
        public const int Max = 500;
        private static readonly List<ToolOperation> _items = new List<ToolOperation>();

        public static IReadOnlyList<ToolOperation> Items => _items;
        public static event Action<ToolOperation> Added;

        internal static void Add(ToolOperation op)
        {
            _items.Add(op);
            if (_items.Count > Max) _items.RemoveRange(0, _items.Count - Max);
            try { Added?.Invoke(op); } catch { }
        }
    }

    /// <summary>
    /// Runs Vortex tools for every client. A call:
    /// <list type="bullet">
    /// <item>waits for the previous one (one at a time — parallel tool calls would interleave their undo steps),</item>
    /// <item>runs on the UI thread (the scene, the undo stack and the viewport live there),</item>
    /// <item>is ONE undo step named "Claude: …" however many entities it touches — and a call that fails half-way is
    /// rolled back completely,</item>
    /// <item>lands in the <see cref="OperationLog"/>.</item>
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

        private static async Task<ToolResult> RunAsync(ToolDef def, IDictionary<string, object> args, string origin, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var op = new ToolOperation { Time = DateTime.Now, Tool = def.Name, Origin = origin, Arguments = Shorten(ToolJson.Serialize(args), 240) };
            var undo = UndoRedoManager.Instance;
            bool grouped = !def.ReadOnly && !def.ManagesUndo;
            ToolContext.Origin = origin;
            ToolContext.UndoLabel = null;
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
                // a successful call is one named undo step; a failed one leaves no trace
                if (ToolContext.UndoLabel != null) undo.RenameGroup("Claude: " + ToolContext.UndoLabel);
                op.UndoStep = undo.EndGroup(commit: !result.IsError);
                try { EditorCommands.AfterSceneEdit(); } catch { }
            }
            op.Result = result.Summary();
            op.IsError = result.IsError;
            op.Milliseconds = sw.Elapsed.TotalMilliseconds;
            ToolContext.Origin = null;
            OperationLog.Add(op);
            return result;
        }

        internal static string Shorten(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
