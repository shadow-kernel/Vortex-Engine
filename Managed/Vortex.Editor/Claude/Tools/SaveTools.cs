using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor.Core.Claude;
using ModelContextProtocol.Server;

namespace VortexEditor.Claude.Tools
{
    /// <summary>Save data for play testing (#458): read, seed, reset, snapshot and restore the game's Save slots — the
    /// play-mode sandbox by default, in edit mode and while playing (then through the running game's cache).</summary>
    [McpServerToolType, DisplayName("Saves")]
    public static class SaveTools
    {
        private const string Where =
            "By default it works on the saves editor play uses, which the project's play-mode saves setting picks (Project Settings ▸ General ▸ Play testing ▸ Saves): " +
            "Sandbox — separate test saves the exported game never reads, the default of new projects — or Shared — the exported game's own files; " +
            "game_saves: true always means the exported game's own files. ";

        // the count is SaveSlots.AutoKeep (an attribute text has to be a constant)
        private const string Safety =
            "Not on the undo stack: a slot that holds a save is first copied to an automatic snapshot (backup in the answer; restore_save brings it back, the " +
            "newest 10 are kept). On a Shared project deleting or replacing a slot needs game_saves: true — it is the player's real save. ";

        private static void NeedProject() => _ = SceneModel.Project;

        [McpServerTool(Name = "get_save", ReadOnly = true, Idempotent = true)]
        [Description("The game's save data — what scripts store with Save.SetInt / SetFloat / SetString / SetBool: one slot's keys with their values " +
                     "and types (int, float, string; a bool is an int 0 / 1), plus the slots that exist and the snapshots (auto-… ones were taken before a " +
                     "tool overwrote or deleted a slot). " + Where + "In play mode the slot the game has open shows what it holds now, unsaved changes " +
                     "included (live: true).")]
        public static object GetSave(
            [Description("Save slot (Save.UseSlot's number; 0 is the default)")] int slot = 0,
            [Description("The exported game's own save files instead of the ones editor play uses")] bool game_saves = false)
        {
            NeedProject();
            try
            {
                var raw = SaveSlots.Read(slot, game_saves, out bool live);
                var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
                var types = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in raw)
                {
                    values[kv.Key] = SaveSlots.Value(kv.Value, out string type);
                    types[kv.Key] = type;
                }
                string file = SaveSlots.FileOf(slot, game_saves);
                return new
                {
                    slot,
                    saves = Saves(game_saves),
                    file,
                    exists = File.Exists(file),
                    live = live ? (bool?)true : null,
                    values,
                    types,
                    slots = SaveSlots.Slots(game_saves),
                    snapshots = SaveSlots.Snapshots(),
                };
            }
            catch (SaveSlots.Error e) { throw new ToolError(e.Message); }
        }

        [McpServerTool(Name = "set_save"), NoDryRun]
        [Description("Writes save data for a test, e.g. {\"act\": 4, \"rank\": 3, \"credits\": 5000} — then enter_play_mode starts the game in that " +
                     "state (every play session reads the saves from disk). Values: numbers, text, true / false; null removes a key. A number keeps the " +
                     "type its key has (a new whole number is an int, a fraction a float); types: {\"speed\": \"float\"} chooses one (int, float, string, " +
                     "bool). replace: true leaves only these keys in the slot. " + Where + "In play mode the slot the game has open changes in the running " +
                     "game at once (and its unsaved changes are written with it). " + Safety)]
        public static object SetSave(
            [Description("Keys and values {\"credits\": 5000, \"name\": \"Ada\", \"old_key\": null}")] JsonElement values,
            [Description("Save slot (Save.UseSlot's number; 0 is the default)")] int slot = 0,
            [Description("Leave only these keys in the slot")] bool replace = false,
            [Description("Storage type per key {\"speed\": \"float\"}: int, float, string or bool")] JsonElement? types = null,
            [Description("The exported game's own save files instead of the ones editor play uses")] bool game_saves = false)
        {
            NeedProject();
            if (values.ValueKind != JsonValueKind.Object) throw new ToolError("values must be an object {\"key\": value}.");
            var typeOf = new Dictionary<string, string>(StringComparer.Ordinal);
            if (types.HasValue && types.Value.ValueKind == JsonValueKind.Object)
                foreach (var p in types.Value.EnumerateObject()) typeOf[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
            else if (types.HasValue && types.Value.ValueKind != JsonValueKind.Null) throw new ToolError("types must be an object {\"key\": \"float\"}.");
            try
            {
                if (replace) SaveSlots.GuardDestructive(slot, game_saves, "set_save with replace");
                var current = SaveSlots.Read(slot, game_saves, out _);
                var raw = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in values.EnumerateObject())
                {
                    if (p.Name.Length == 0) throw new ToolError("An empty key.");
                    current.TryGetValue(p.Name, out string existing);
                    raw[p.Name] = SaveSlots.Raw(p.Name, p.Value, typeOf.TryGetValue(p.Name, out string t) ? t : null, replace ? null : existing);
                }
                foreach (var k in typeOf.Keys) if (!raw.ContainsKey(k)) throw new ToolError("types names '" + k + "', which values does not set.");
                if (raw.Count == 0 && !replace) throw new ToolError("values is empty — name at least one key (replace: true with {} empties the slot).");
                string file = SaveSlots.FileOf(slot, game_saves);
                string before = SaveSlots.Write(slot, game_saves, raw, replace, out bool live, out string backup);
                Logged(file, before);
                var after = SaveSlots.Read(slot, game_saves, out _);
                return new
                {
                    slot,
                    saves = Saves(game_saves),
                    file,
                    set = raw.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => SaveSlots.Value(kv.Value, out _)),
                    removed = raw.Where(kv => kv.Value == null).Select(kv => kv.Key).ToArray() is { Length: > 0 } gone ? gone : null,
                    keys = after.Count,
                    live = live ? (bool?)true : null,
                    backup,
                    note = live ? "the running game holds these values now" : SceneModel.Playing ? "the running game does not have this slot open — it reads it on Save.UseSlot(" + slot + ") or at the next Play" : null,
                };
            }
            catch (SaveSlots.Error e) { throw new ToolError(e.Message); }
        }

        [McpServerTool(Name = "reset_save"), NoDryRun]
        [Description("Deletes a save slot — the game sees no save there, as on a first launch (Save.SlotExists is false). " + Where +
                     "In play mode the slot the game has open is emptied in the running game too. " + Safety)]
        public static object ResetSave(
            [Description("Save slot (Save.UseSlot's number; 0 is the default)")] int slot = 0,
            [Description("The exported game's own save files instead of the ones editor play uses")] bool game_saves = false)
        {
            NeedProject();
            try
            {
                string file = SaveSlots.FileOf(slot, game_saves);
                string before = File.Exists(file) ? SafeRead(file) : null;
                bool existed = SaveSlots.Reset(slot, game_saves, out bool live, out string backup);
                if (existed) Logged(file, before, deleted: true);
                return new { slot, saves = Saves(game_saves), file, deleted = existed, live = live ? (bool?)true : null, backup, note = existed ? null : "the slot had no save" };
            }
            catch (SaveSlots.Error e) { throw new ToolError(e.Message); }
        }

        [McpServerTool(Name = "snapshot_save"), NoDryRun]
        [Description("Keeps a copy of a save slot under a name (replacing that snapshot's copy of the slot), to bring back with restore_save — e.g. " +
                     "\"act4-start\" before a test run. A slot without a save is recorded as such: restoring it removes the slot again. In play mode the " +
                     "slot the game has open is copied as the game holds it, unsaved changes included. Snapshots live with the sandbox saves; the exported " +
                     "game never reads them.")]
        public static object SnapshotSave(
            [Description("Snapshot name: letters, digits, spaces, '-', '_', '.'")] string name,
            [Description("Save slot (Save.UseSlot's number; 0 is the default)")] int slot = 0,
            [Description("The exported game's own save files instead of the ones editor play uses")] bool game_saves = false)
        {
            NeedProject();
            try
            {
                int keys = SaveSlots.Snapshot(slot, game_saves, name, out bool live);
                return new { snapshot = SaveSlots.SnapshotName(name), slot, saves = Saves(game_saves), keys = keys >= 0 ? (int?)keys : null, no_save = keys < 0 ? (bool?)true : null, live = live ? (bool?)true : null, snapshots = SaveSlots.Snapshots() };
            }
            catch (SaveSlots.Error e) { throw new ToolError(e.Message); }
        }

        [McpServerTool(Name = "restore_save"), NoDryRun]
        [Description("Puts a snapshot back over a save slot — one of snapshot_save, or an automatic one (auto-…) — or removes the slot when it had " +
                     "no save when the snapshot was taken. In play mode the game reads the restored slot at once when it has it open, dropping its " +
                     "unsaved changes. " + Safety)]
        public static object RestoreSave(
            [Description("The snapshot's name")] string name,
            [Description("Save slot (Save.UseSlot's number; 0 is the default)")] int slot = 0,
            [Description("The exported game's own save files instead of the ones editor play uses")] bool game_saves = false)
        {
            NeedProject();
            try
            {
                string file = SaveSlots.FileOf(slot, game_saves);
                string before = File.Exists(file) ? SafeRead(file) : null;
                int keys = SaveSlots.Restore(slot, game_saves, name, out bool live, out string backup);
                Logged(file, before, deleted: keys < 0);
                return new { snapshot = SaveSlots.SnapshotName(name), slot, saves = Saves(game_saves), file, keys = keys >= 0 ? (int?)keys : null, removed = keys < 0 ? (bool?)true : null, live = live ? (bool?)true : null, backup };
            }
            catch (SaveSlots.Error e) { throw new ToolError(e.Message); }
        }

        /// <summary>"sandbox" or "game" — which folder a call used.</summary>
        private static string Saves(bool gameSaves) => SaveSlots.UsesSandbox(gameSaves) ? "sandbox" : "game";

        private static string SafeRead(string file) { try { return File.ReadAllText(file); } catch { return null; } }

        /// <summary>The save file the call wrote, for the Operations list — and, in play mode, so the answer says it stays after
        /// Stop (runtime_only: false): save files are not part of the scene Stop puts back.</summary>
        private static void Logged(string file, string before, bool deleted = false)
        {
            string after = deleted ? null : SafeRead(file);
            if (before == after) return;
            ToolContext.FileWritten(new FileChange { Path = file, Created = before == null, Before = before ?? "", After = after ?? "" });
        }
    }
}
