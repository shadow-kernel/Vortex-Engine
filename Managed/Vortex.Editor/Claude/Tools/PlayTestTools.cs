using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Claude;
using Editor.Core.Input;
using Editor.Core.Services;
using Editor.ECS;
using Editor.Scripting;
using ModelContextProtocol.Server;

namespace VortexEditor.Claude.Tools
{
    /// <summary>Play testing in editor play mode (#457): input played into the running game, and the live state of its
    /// scripts read and set — together with enter_play_mode, capture_viewport and read_console(since_id) a game is tested
    /// end to end without debug hooks or OS-level clicks.</summary>
    [McpServerToolType, DisplayName("Play testing")]
    public static class PlayTestTools
    {
        private const string StaticPrefix = "static:";

        private static void NeedPlay(string tool)
        {
            _ = SceneModel.Project;
            if (!PlayModeService.Instance.IsPlaying)
                throw new ToolError(tool + " works while the game runs — enter_play_mode first" + (tool == "set_script_state" ? " (attach_script / set_component_properties change the stored values in edit mode)." : "."));
        }

        // ================================================================== input

        [McpServerTool(Name = "send_input", Destructive = false), ManagesUndo]
        [Description("Plays keyboard, mouse and wheel input into the running game (play mode): what its scripts read through Input.GetKey / " +
                     "GetKeyDown / GetKeyUp, Input.MouseDeltaX / Y, Input.ScrollDelta and UI.MouseX / Y, added to the real keyboard and mouse — no OS " +
                     "events, the real cursor never moves, and it works while another app is in front. actions, t = seconds of game time from now: " +
                     "[{\"t\": 0, \"press\": \"W\"}, {\"t\": 2, \"release\": \"W\"}, {\"t\": 2.1, \"tap\": \"LButton\"}, {\"t\": 2.5, \"look\": [40, -10, 0.5]}, " +
                     "{\"t\": 3, \"hold\": [\"LeftShift\", 0.8]}, {\"t\": 4, \"scroll\": -1}, {\"t\": 4.5, \"cursor\": [640, 360]}] — press holds until " +
                     "release, tap is a 0.12 s press (GetKeyDown sees it once; a press and release in the same frame still count), hold presses for the " +
                     "seconds given, look moves the mouse by [dx, dy] pixels spread over the seconds (0 = one frame), scroll turns the wheel by notches " +
                     "(+1 up), cursor puts the UI cursor at [x, y] points (ui_size in the answer is the view in points) so a tap of LButton clicks the " +
                     "game's UI there. Key names as Input.GetKey takes them (W, Space, LeftShift, Escape, Return, Up, F5, Num1, LButton, RButton, " +
                     "MButton …). Waits until the last action ran — other tools can run meanwhile — and returns the console errors meanwhile; Stop, " +
                     "and cancelling the call, release everything.")]
        public static async Task<object> SendInput(
            [Description("Timed actions [{\"t\": 0, \"press\": \"W\"}, …] — see the description")] JsonElement? actions = null,
            [Description("The same commands as lines of the standalone player's --input file instead of actions: \"0 press W\\n2 release W\\n2.5 look 40 -10 0.5\\n4 scroll -1\"")] string script = null,
            [Description("Wait until the last action ran (false: return at once, the input keeps playing)")] bool wait = true,
            [Description("First release every key earlier calls still hold and drop their actions not run yet")] bool cancel = false,
            CancellationToken ct = default)
        {
            NeedPlay("send_input");
            bool hasActions = actions.HasValue && actions.Value.ValueKind != JsonValueKind.Null && actions.Value.ValueKind != JsonValueKind.Undefined;
            if (hasActions && !string.IsNullOrWhiteSpace(script)) throw new ToolError("Give actions or script, not both.");
            var problems = new List<string>();
            var warnings = new List<string>();
            var ignored = new List<string>();
            List<InputScript.Command> commands;
            if (hasActions) commands = InputScript.FromJson(actions.Value, problems);
            else if (!string.IsNullOrWhiteSpace(script))
            {
                commands = InputScript.Parse(script.Replace("\\n", "\n").Split('\n'), problems, warnings);
                // a player --input file pasted as it is: its captures and exit belong to the player
                ignored.AddRange(commands.Where(c => c.Op == "capture" || c.Op == "exit").Select(c => c.Op));
                commands.RemoveAll(c => c.Op == "capture" || c.Op == "exit");
            }
            else if (cancel) commands = new List<InputScript.Command>();
            else throw new ToolError("Give actions, e.g. [{\"t\": 0, \"press\": \"W\"}, {\"t\": 2, \"release\": \"W\"}] (or script, or cancel: true).");
            if (problems.Count > 0) throw new ToolError("Nothing was sent — " + string.Join("; ", problems.Take(8)) + (problems.Count > 8 ? "; …" : "") + ".");
            double span = commands.Count == 0 ? 0 : commands.Max(c => c.End);
            if (span > 600) throw new ToolError("The actions span " + span.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s — at most 600 s per call.");

            var sim = HostInput.Simulated;
            if (cancel) sim.Cancel();
            long consoleBefore = ConsoleService.Instance.LastId;
            double start = sim.Now;
            long frames0 = sim.Frames;
            double end = sim.Enqueue(commands);
            var ui = UiSize();
            string ignoredNote = ignored.Count > 0 ? "ignored " + string.Join(", ", ignored.Distinct()) + " (player only — capture_viewport takes pictures in the editor)" : null;
            if (warnings.Count > 0) ignoredNote = (ignoredNote == null ? "" : ignoredNote + "; ") + string.Join("; ", warnings);
            if (!wait || commands.Count == 0)
            {
                return new
                {
                    queued = commands.Count,
                    runs_for = R(end - start),
                    cancelled = cancel ? (bool?)true : null,
                    keys_held = Keys(sim.HeldKeys()),
                    ui_size = ui,
                    note = ignoredNote,
                };
            }

            // the sequence runs on the play clock: wait while the game plays it, but never longer than its length in real time plus
            // a margin (a slow frame rate stretches game time), and not at all while paused. The wait does not hold the tools' lock —
            // exit_play_mode or get_script_state can come in meanwhile — and a cancelled call stops the simulated input (#457 review).
            var sw = Stopwatch.StartNew();
            double limit = Math.Min(120, (end - start) * 2 + 10);
            string stopped = null;
            try
            {
                await ToolHost.WaitOutsideLock(async () =>
                {
                    while (sim.Now <= end)
                    {
                        var state = PlayModeService.Instance.State;
                        if (state == PlayState.Editing) { stopped = "play stopped before the last action ran"; break; }
                        if (state == PlayState.Paused) { stopped = "the game is paused — the remaining actions run when it plays on"; break; }
                        if (sw.Elapsed.TotalSeconds > limit) { stopped = "still playing after " + (int)limit + " s real time (a slow frame rate stretches game time) — the rest runs on"; break; }
                        await Task.Delay(25, ct);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                sim.Cancel();   // the client gave up on the call: nothing of it may stay held
                throw;
            }
            var notes = new List<string>();
            if (stopped != null) notes.Add(stopped);
            if (ignoredNote != null) notes.Add(ignoredNote);
            return new
            {
                ran = commands.Count,
                game_seconds = R(Math.Min(sim.Now, end) - start),
                frames = sim.Frames - frames0,
                keys_held = Keys(sim.HeldKeys()),
                ui_size = ui,
                state = PlayModeService.Instance.State.ToString(),
                console_errors = ViewTools.NewEntries(consoleBefore, LogLevel.Error, 20),
                console_warnings = ViewTools.NewEntries(consoleBefore, LogLevel.Warning, 10),
                note = notes.Count > 0 ? string.Join("; ", notes) : null,
            };
        }

        /// <summary>The key names of virtual keys still held (the first name KeyNames knows for each).</summary>
        private static string[] Keys(int[] vks)
        {
            if (vks.Length == 0) return null;
            return vks.Select(vk => vk == 0x01 ? "LButton" : vk == 0x02 ? "RButton" : vk == 0x04 ? "MButton" : vk >= 'A' && vk <= 'Z' || vk >= '0' && vk <= '9' ? ((char)vk).ToString()
                : vk == 0x20 ? "Space" : vk == 0xA0 ? "LeftShift" : vk == 0xA2 ? "LeftCtrl" : vk == 0x1B ? "Escape" : "0x" + vk.ToString("X2")).ToArray();
        }

        /// <summary>The play view in UI points (UI.Width × UI.Height) — the space of cursor's [x, y].</summary>
        private static double[] UiSize()
        {
            var host = (Vortex.IScriptHost)ScriptRuntime.Instance;
            float w = host.UIWidth(), h = host.UIHeight();
            return w > 0 && h > 0 ? new[] { R(w), R(h) } : null;
        }

        // ================================================================== script state

        [McpServerTool(Name = "get_script_state", ReadOnly = true)]
        [Description("Reads the running game's script state (play mode): the public fields and properties of the behaviours an entity runs — " +
                     "the game's own members, not VortexBehaviour's Position / EntityId (get_entity shows the transform) — or with target " +
                     "\"static:ClassName\" the public static fields and properties of a script class (game state, managers: \"static:MissionManager\"). " +
                     "Runs no game code by default: fields and auto-properties are read directly, a property with a getter of its own is listed under " +
                     "computed and runs only when fields names it. Values as JSON: numbers, text, bools, enums by name, Vector3 / Quaternion / Color as " +
                     "arrays, lists (64 items at most), the game's own objects as their members down to depth (one already shown, or a cycle, is not " +
                     "expanded again); a reference to another behaviour shows as \"Health on 'Npc'\" (read that entity, or a dotted path). fields picks " +
                     "members or dotted paths into the game's objects (\"Instance.Credits\") — on an entity with several scripts, from the one that has " +
                     "it. A large state is cut (truncated: true). get_entity shows a Script's stored Inspector values instead.")]
        public static object GetScriptState(
            [Description("Entity id, path or name — or \"static:ClassName\" for a script class's static state")] string target,
            [Description("Only these members (their getters run); a dotted path reads inside the game's objects: [\"Hp\", \"Inventory.Ammo\"]")] string[] fields = null,
            [Description("When the entity runs several scripts: only this one (class name)")] string script = null,
            [Description("How many levels of nested objects and lists are expanded (0–6)")] int depth = 2)
        {
            NeedPlay("get_script_state");
            try
            {
                if (IsStatic(target, out string cls))
                {
                    var type = ScriptState.FindClass(ScriptRuntime.Instance.ScriptAssembly, cls);
                    var snap = ScriptState.Read(null, type, true, fields, depth);
                    return new
                    {
                        @class = type.FullName,
                        state = snap.State,
                        computed = snap.Computed.Count > 0 ? snap.Computed : null,
                        truncated = snap.Truncated ? (bool?)true : null,
                        note = snap.Truncated ? Cut : null,
                    };
                }
                var e = SceneModel.Resolve(target);
                var all = Behaviours(e, script);
                var reads = new List<(Vortex.VortexBehaviour b, ScriptState.Snapshot snap)>();
                if (fields == null || fields.Length == 0)
                    foreach (var b in all) reads.Add((b, ScriptState.Read(b, b.GetType(), false, null, depth)));
                else
                    // each field from the behaviour that has it (#457 review): several scripts of one entity rarely share a name
                    foreach (var group in fields.GroupBy(f => Owner(e, all, f, "read")))
                        reads.Add((group.Key, ScriptState.Read(group.Key, group.Key.GetType(), false, group.ToList(), depth)));
                bool cut = reads.Any(r => r.snap.Truncated);
                return new
                {
                    entity = SceneModel.ShortId(e) + " " + SceneModel.PathOf(e),
                    behaviours = reads.Select(r => new
                    {
                        script = r.b.GetType().Name,
                        state = r.snap.State,
                        computed = r.snap.Computed.Count > 0 ? r.snap.Computed : null,
                    }).ToArray(),
                    truncated = cut ? (bool?)true : null,
                    note = cut ? Cut : null,
                };
            }
            catch (ScriptState.Error ex) { throw new ToolError(ex.Message); }
        }

        private static readonly string Cut = "the state is larger than one answer holds (" + ScriptState.MaxValues + " values) — the rest shows as …; name members in fields, or lower depth";

        [McpServerTool(Name = "set_script_state"), ManagesUndo]
        [Description("Sets public fields or properties of the running game's behaviours — or, with target \"static:ClassName\", a script class's " +
                     "static ones — while playing: test setup such as {\"Credits\": 5000, \"Instance.Act\": 4, \"Tint.A\": 0.5} (a dotted path sets inside " +
                     "the game's objects and structs). Values as JSON in the member's type: numbers, text, bools, enums by name ([Flags]: \"Fast, Strong\"), " +
                     "Vector3 / Quaternion / Color as [x, y, z(, w)] or {\"r\": 1}, structs as objects of their fields, arrays / lists; null for a reference. " +
                     "Every value is checked before any is set; the answer reads them back. Live only: Stop discards it — the behaviours end, and the next " +
                     "Play compiles the scripts afresh, statics included (attach_script / set_component_properties change the stored values).")]
        public static object SetScriptState(
            [Description("Entity id, path or name — or \"static:ClassName\"")] string target,
            [Description("Members and values {\"Hp\": 25, \"Spawn\": [0, 1, 4], \"Instance.Credits\": 5000}")] JsonElement values,
            [Description("When the entity runs several scripts: the one to set (class name); without it the members must belong to exactly one")] string script = null)
        {
            NeedPlay("set_script_state");
            try
            {
                if (IsStatic(target, out string cls))
                {
                    var type = ScriptState.FindClass(ScriptRuntime.Instance.ScriptAssembly, cls);
                    return new { @class = type.FullName, set = ScriptState.Write(null, type, true, values) };
                }
                var e = SceneModel.Resolve(target);
                var list = Behaviours(e, script);
                Vortex.VortexBehaviour b = list[0];
                if (list.Count > 1)
                {
                    // the one behaviour that has the members named — several scripts of the entity can have one of that name
                    var owners = values.ValueKind == JsonValueKind.Object ? values.EnumerateObject().Select(p => Owner(e, list, p.Name, "set")).Distinct().ToList() : list;
                    if (owners.Count != 1)
                        throw new ToolError(SceneModel.PathOf(e) + " runs " + string.Join(", ", list.Select(x => x.GetType().Name)) + " — the members belong to " +
                                            string.Join(" and ", owners.Select(x => x.GetType().Name)) + "; set each script's in its own call (script).");
                    b = owners[0];
                }
                return new { entity = SceneModel.ShortId(e) + " " + SceneModel.PathOf(e), script = b.GetType().Name, set = ScriptState.Write(b, b.GetType(), false, values) };
            }
            catch (ScriptState.Error ex) { throw new ToolError(ex.Message); }
        }

        /// <summary>The behaviour of the entity that has the path's first member — an error naming the scripts when none or
        /// several have it.</summary>
        private static Vortex.VortexBehaviour Owner(GameEntity e, List<Vortex.VortexBehaviour> list, string path, string verb)
        {
            if (list.Count == 1) return list[0];
            string first = (path ?? "").Split('.')[0].Trim();
            var owners = list.Where(x => ScriptState.Find(x.GetType(), false, first) != null).ToList();
            if (owners.Count == 1) return owners[0];
            throw new ToolError(SceneModel.PathOf(e) + " runs " + string.Join(", ", list.Select(x => x.GetType().Name)) + " — " +
                                (owners.Count == 0 ? "none has a member '" + first + "'. Members: " + string.Join("; ", list.Select(x => x.GetType().Name + ": " + ScriptState.Names(x.GetType(), false)))
                                                   : string.Join(" and ", owners.Select(x => x.GetType().Name)) + " have '" + first + "'") + "; name the one to " + verb + " with script.");
        }

        private static bool IsStatic(string target, out string cls)
        {
            cls = null;
            if (target == null || !target.TrimStart().StartsWith(StaticPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            cls = target.TrimStart().Substring(StaticPrefix.Length).Trim();
            return true;
        }

        /// <summary>The live behaviours of an entity (one class with <paramref name="script"/>) — an error that says why there are
        /// none: no Script component, a disabled one, a class that does not compile.</summary>
        private static List<Vortex.VortexBehaviour> Behaviours(GameEntity e, string script)
        {
            var all = ScriptRuntime.Instance.BehavioursOf(e).Where(b => b != null && !b.Destroyed).ToList();
            if (all.Count == 0)
            {
                var comps = e.Components.OfType<Editor.ECS.Components.Scripting.Script>().ToList();
                throw new ToolError(SceneModel.PathOf(e) + " runs no script" + (comps.Count == 0 ? " (it has no Script component)."
                    : " — its Script components (" + string.Join(", ", comps.Select(c => c.ScriptClassName + (c.IsEnabled ? "" : " disabled"))) + ") did not start: disabled, or the class does not compile (read_console shows why)."));
            }
            if (string.IsNullOrWhiteSpace(script)) return all;
            string name = script.Trim();
            if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 3);
            name = name.Substring(name.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            var hits = all.Where(b => string.Equals(b.GetType().Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) throw new ToolError(SceneModel.PathOf(e) + " runs no " + script + " — it runs " + string.Join(", ", all.Select(b => b.GetType().Name)) + ".");
            return hits;
        }

        private static double R(double v) => ToolJson.R(v);
    }
}
