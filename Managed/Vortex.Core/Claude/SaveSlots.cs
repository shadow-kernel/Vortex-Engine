using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor.Core.Services;

namespace Editor.Core.Claude
{
    /// <summary>
    /// The game's save slots as the editor's MCP save tools see them (#458): get_save, set_save, reset_save,
    /// snapshot_save and restore_save. They work on the folder editor play uses — the play-mode sandbox
    /// (VortexGames/&lt;project&gt;/editor) or, for a project whose Play-mode saves setting is Shared, the exported game's own
    /// folder — or, asked for explicitly, on the exported game's folder. In edit mode they read and write the slot files,
    /// so the next Play starts from them (every session reads the saves from disk, #452). While the game runs, the slot it
    /// has open in that folder goes through the running game's cache instead: it sees a change at once, and a read shows
    /// its unsaved changes too. Snapshots are copies of a slot under the sandbox's snapshots/ folder, which the exported
    /// game never reads; a snapshot of a slot without a file brings that "no save yet" back on restore.
    /// <para>Nothing here is on the undo stack, so (#458 review): before a call overwrites or deletes a slot that holds a save,
    /// the slot is copied to an automatic snapshot ("auto-…", the newest <see cref="AutoKeep"/> are kept) that restore_save
    /// brings back; and on a project whose play-mode saves are Shared — the default folder is the exported game's own —
    /// deleting or replacing a slot needs game_saves: true, so it never happens to the player's real save by default.</para>
    /// </summary>
    public static class SaveSlots
    {
        /// <summary>A request the tools refuse — the message is worded for the model.</summary>
        public sealed class Error : Exception { public Error(string message) : base(message) { } }

        private static bool Playing => PlayModeService.Instance.IsPlaying;

        /// <summary>The tools use the sandbox: not <paramref name="gameSaves"/>, and play uses it — the running session's
        /// folder while the game runs, the project's setting otherwise.</summary>
        public static bool UsesSandbox(bool gameSaves) => !gameSaves && (Playing ? Vortex.Save.SessionSandboxed : Vortex.Save.SandboxForPlay());

        /// <summary>The folder the tools read and write.</summary>
        public static string Folder(bool gameSaves) => Vortex.Save.Folder(UsesSandbox(gameSaves));

        /// <summary>A slot's file in that folder.</summary>
        public static string FileOf(int slot, bool gameSaves) => Vortex.Save.SlotPath(CheckSlot(slot), UsesSandbox(gameSaves));

        /// <summary>The running game has this slot open in this folder: changes go through its cache.</summary>
        public static bool IsLive(int slot, bool gameSaves) =>
            Playing && Vortex.Save.CurrentSlot == slot && Vortex.Save.SessionSandboxed == UsesSandbox(gameSaves);

        private static int CheckSlot(int slot)
        {
            if (slot < 0 || slot > 9999) throw new Error("slot must be 0 or more (Save.UseSlot's number; 0 is the default slot).");
            return slot;
        }

        // ================================================================== slots

        /// <summary>A slot's raw values ("i:5", "f:1.5", "s:text") — the running game's view when the slot is live.</summary>
        public static Dictionary<string, string> Read(int slot, bool gameSaves, out bool live)
        {
            live = IsLive(CheckSlot(slot), gameSaves);
            return live ? Vortex.Save.LiveValues() : Vortex.Save.ReadFile(FileOf(slot, gameSaves));
        }

        /// <summary>Set raw values (null removes the key); <paramref name="replace"/> leaves only them in the slot (on a Shared
        /// project's default folder only with <paramref name="gameSaves"/>). A slot that holds a save is kept first as an
        /// automatic snapshot (<paramref name="backup"/>, null when there was nothing to keep). Returns the file's text before
        /// (null when there was none) for the operation log.</summary>
        public static string Write(int slot, bool gameSaves, IDictionary<string, string> raw, bool replace, out bool live, out string backup)
        {
            if (replace) GuardDestructive(slot, gameSaves, "set_save with replace");
            string file = FileOf(slot, gameSaves);
            string before = ReadText(file);
            backup = Backup(slot, gameSaves, "set_save");
            live = IsLive(slot, gameSaves);
            string error;
            if (live) error = Vortex.Save.ApplyLive(raw, replace);
            else
            {
                var data = replace ? new Dictionary<string, string>() : Vortex.Save.ReadFile(file);
                foreach (var kv in raw)
                {
                    if (kv.Value == null) data.Remove(kv.Key);
                    else data[kv.Key] = kv.Value;
                }
                error = Vortex.Save.Write(file, data);
            }
            if (error != null) throw new Error("The save file could not be written: " + error);
            return before;
        }

        /// <summary>Delete a slot (a new game) — on a Shared project's default folder only with <paramref name="gameSaves"/>. A save
        /// it held is kept first as an automatic snapshot (<paramref name="backup"/>). True when it had a file.</summary>
        public static bool Reset(int slot, bool gameSaves, out bool live, out string backup)
        {
            GuardDestructive(slot, gameSaves, "reset_save");
            string file = FileOf(slot, gameSaves);
            bool existed = File.Exists(file);
            backup = Backup(slot, gameSaves, "reset_save");
            live = IsLive(slot, gameSaves);
            if (live) Vortex.Save.DeleteSlot(slot);   // the session's own file, and its cache with it
            else if (existed)
            {
                try { File.Delete(file); }
                catch (Exception ex) { throw new Error("The save file could not be deleted: " + ex.Message); }
            }
            return existed;
        }

        /// <summary>A call that deletes or replaces a slot, on the default folder of a project whose play-mode saves are Shared:
        /// that folder is the exported game's own, the developer's real playthrough — refused unless asked for with
        /// game_saves: true (#458 review).</summary>
        public static void GuardDestructive(int slot, bool gameSaves, string what)
        {
            if (gameSaves || UsesSandbox(false)) return;
            throw new Error("This project's play-mode saves are Shared, so slot " + slot + " is the exported game's own save file — " + what +
                            " would overwrite or delete the real save. Pass game_saves: true to do that on purpose, or switch the project to Sandbox " +
                            "(Project Settings ▸ General ▸ Play testing ▸ Saves) for separate test saves.");
        }

        // ================================================================== automatic snapshots

        /// <summary>The prefix of the snapshots taken before a call overwrites or deletes a slot.</summary>
        public const string AutoPrefix = "auto-";
        /// <summary>How many automatic snapshots are kept (the newest).</summary>
        public const int AutoKeep = 10;

        /// <summary>Before a call overwrites or deletes a slot: copy what it holds — the running game's view when it is live — to a
        /// new automatic snapshot "auto-&lt;time&gt;-&lt;tool&gt;", and drop the oldest beyond <see cref="AutoKeep"/>. Returns the
        /// snapshot's name, or null when the slot holds no save (nothing to lose). <paramref name="keep"/> (the snapshot a
        /// restore is about to read) is never the one dropped.</summary>
        public static string Backup(int slot, bool gameSaves, string tool, string keep = null)
        {
            string file = FileOf(slot, gameSaves);
            var values = IsLive(slot, gameSaves) ? Vortex.Save.LiveValues() : Vortex.Save.ReadFile(file);
            if (!File.Exists(file) && values.Count == 0) return null;
            string name = AutoPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + tool;
            Snapshot(slot, gameSaves, name, out _);
            var autos = Snapshots().Where(n => n.StartsWith(AutoPrefix, StringComparison.Ordinal) && n != keep).OrderBy(n => n, StringComparer.Ordinal).ToList();
            for (int i = 0; i < autos.Count - AutoKeep; i++)
                try { Directory.Delete(Path.Combine(SnapshotsFolder, autos[i]), true); } catch { }
            return name;
        }

        /// <summary>The slots that have a file, in order.</summary>
        public static List<int> Slots(bool gameSaves)
        {
            var list = new List<int>();
            string dir = Folder(gameSaves);
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "save_slot*.dat"))
            {
                string n = Path.GetFileNameWithoutExtension(f).Substring("save_slot".Length);
                if (int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out int s)) list.Add(s);
            }
            list.Sort();
            return list;
        }

        private static string ReadText(string file)
        {
            try { return File.Exists(file) ? File.ReadAllText(file) : null; } catch { return null; }
        }

        // ================================================================== snapshots

        /// <summary>Where snapshots live: the sandbox's snapshots/ folder (the exported game never reads it).</summary>
        public static string SnapshotsFolder => Path.Combine(Vortex.Save.Folder(true), "snapshots");

        /// <summary>The snapshots that exist, by name.</summary>
        public static List<string> Snapshots()
        {
            var dir = SnapshotsFolder;
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetDirectories(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A snapshot's name as a folder: letters, digits, spaces, '-', '_' and '.', not starting with a dot.</summary>
        public static string SnapshotName(string name)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) throw new Error("name the snapshot, e.g. \"act4-start\".");
            if (n.Length > 64 || n.StartsWith(".", StringComparison.Ordinal) || n.Any(c => !(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.')))
                throw new Error("A snapshot name uses letters, digits, spaces, '-', '_' and '.' (64 at most, not starting with '.'): \"" + name + "\" does not.");
            return n;
        }

        private static string SnapshotFile(string name, int slot) => Path.Combine(SnapshotsFolder, SnapshotName(name), "save_slot" + slot + ".dat");
        private static string AbsentMarker(string name, int slot) => Path.Combine(SnapshotsFolder, SnapshotName(name), "save_slot" + slot + ".absent");

        /// <summary>Copy a slot into the snapshot <paramref name="name"/> (replacing that snapshot's copy of the slot) — a live
        /// slot as the running game holds it, unsaved changes included. Returns the number of keys, or -1 when the slot has
        /// no save (restore then removes it again).</summary>
        public static int Snapshot(int slot, bool gameSaves, string name, out bool live)
        {
            string file = FileOf(slot, gameSaves), copy = SnapshotFile(name, slot), absent = AbsentMarker(name, slot);
            live = IsLive(slot, gameSaves);
            var values = live ? Vortex.Save.LiveValues() : Vortex.Save.ReadFile(file);
            bool none = !File.Exists(file) && values.Count == 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy));
                if (none)
                {
                    if (File.Exists(copy)) File.Delete(copy);
                    File.WriteAllText(absent, "");
                    return -1;
                }
                if (File.Exists(absent)) File.Delete(absent);
            }
            catch (Exception ex) { throw new Error("The snapshot could not be written: " + ex.Message); }
            string error = Vortex.Save.Write(copy, values);
            if (error != null) throw new Error("The snapshot could not be written: " + error);
            return values.Count;
        }

        /// <summary>Put the snapshot's copy of a slot back — the slot's file is replaced, or removed when the slot had no save
        /// when the snapshot was taken; a live slot is read again by the running game. On a Shared project's default folder only
        /// with <paramref name="gameSaves"/>; what the slot held is kept first as an automatic snapshot (<paramref name="backup"/>).
        /// Returns the number of keys, or -1.</summary>
        public static int Restore(int slot, bool gameSaves, string name, out bool live, out string backup)
        {
            GuardDestructive(slot, gameSaves, "restore_save");
            string file = FileOf(slot, gameSaves), copy = SnapshotFile(name, slot), absent = AbsentMarker(name, slot);
            bool hasCopy = File.Exists(copy), wasAbsent = File.Exists(absent);
            if (!hasCopy && !wasAbsent)
            {
                var names = Snapshots();
                if (!names.Contains(SnapshotName(name), StringComparer.Ordinal))
                    throw new Error("There is no snapshot \"" + name + "\"." + (names.Count > 0 ? " Snapshots: " + string.Join(", ", names) + "." : " snapshot_save makes one."));
                var slots = Directory.GetFiles(Path.Combine(SnapshotsFolder, SnapshotName(name)), "save_slot*.*")
                    .Select(f => Path.GetFileNameWithoutExtension(f).Substring("save_slot".Length)).Distinct().ToList();
                throw new Error("The snapshot \"" + name + "\" has no copy of slot " + slot + " (it has slot " + string.Join(", ", slots) + ").");
            }
            backup = Backup(slot, gameSaves, "restore_save", keep: SnapshotName(name));
            live = IsLive(slot, gameSaves);
            int keys;
            try
            {
                if (wasAbsent)
                {
                    if (File.Exists(file)) File.Delete(file);
                    keys = -1;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.Copy(copy, file, overwrite: true);
                    keys = Vortex.Save.ReadFile(file).Count;
                }
            }
            catch (Exception ex) { throw new Error("The save could not be restored: " + ex.Message); }
            if (live) Vortex.Save.Reload();   // the running game reads the restored slot, its unsaved changes dropped
            return keys;
        }

        // ================================================================== typed values

        /// <summary>A raw value as JSON: "i:5" → 5 ("int"), "f:1.5" → 1.5 ("float"), "s:Ada" → "Ada" ("string"); anything
        /// else as its text ("raw").</summary>
        public static object Value(string raw, out string type)
        {
            raw ??= "";
            if (raw.StartsWith("i:", StringComparison.Ordinal) && long.TryParse(raw.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out long i)) { type = "int"; return i; }
            if (raw.StartsWith("f:", StringComparison.Ordinal) && double.TryParse(raw.Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
            {
                type = "float";
                return double.IsFinite(f) ? (object)f : raw.Substring(2);
            }
            if (raw.StartsWith("s:", StringComparison.Ordinal)) { type = "string"; return raw.Substring(2); }
            type = "raw";
            return raw;
        }

        /// <summary>The types a value may be stored as: what Save.SetInt / SetFloat / SetString / SetBool write.</summary>
        public const string TypeNames = "int, float, string, bool";

        /// <summary>A JSON value as the raw text Save stores (null = remove the key). Without <paramref name="type"/>: a bool
        /// is stored as SetBool does (int 0 / 1), text as a string, a number in the type the key already has — a whole number
        /// as int and a fraction as float for a new key. A number that does not fit the key's int is refused rather than
        /// stored as a float the game's GetInt would not read.</summary>
        public static string Raw(string key, JsonElement v, string type, string existingRaw)
        {
            if (v.ValueKind == JsonValueKind.Null) return null;
            string t = (type ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0)
            {
                if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) t = "bool";
                else if (v.ValueKind == JsonValueKind.String) t = "string";
                else if (v.ValueKind == JsonValueKind.Number)
                {
                    if (existingRaw != null && existingRaw.StartsWith("f:", StringComparison.Ordinal)) t = "float";
                    else if (existingRaw != null && existingRaw.StartsWith("i:", StringComparison.Ordinal))
                    {
                        if (!IsWhole(v.GetDouble())) throw new Error(key + " holds a whole number (int) — " + v.GetRawText() + " is not one; pass types: {\"" + key + "\": \"float\"} to store it as a float (the game then reads it with GetFloat).");
                        t = "int";
                    }
                    else t = IsWhole(v.GetDouble()) ? "int" : "float";
                }
                else throw new Error(key + ": save values are numbers, text or true / false — not " + (v.ValueKind == JsonValueKind.Array ? "an array" : "an object") + ".");
            }
            switch (t)
            {
                case "int":
                {
                    double d = v.ValueKind == JsonValueKind.True ? 1 : v.ValueKind == JsonValueKind.False ? 0 : Number(key, v);
                    if (!IsWhole(d)) throw new Error(key + " is stored as an int — " + v.GetRawText() + " is not a whole number.");
                    if (d < int.MinValue || d > int.MaxValue) throw new Error(key + ": " + v.GetRawText() + " is too large for an int (Save.GetInt); store it as a float or a string.");
                    return "i:" + ((int)d).ToString(CultureInfo.InvariantCulture);
                }
                case "float":
                    return "f:" + ((float)Number(key, v)).ToString("R", CultureInfo.InvariantCulture);
                case "bool":
                    if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) return v.GetBoolean() ? "i:1" : "i:0";
                    if (v.ValueKind == JsonValueKind.Number && (v.GetDouble() == 0 || v.GetDouble() == 1)) return v.GetDouble() == 1 ? "i:1" : "i:0";
                    if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b)) return b ? "i:1" : "i:0";
                    throw new Error(key + " is stored as a bool — give true or false.");
                case "string":
                    if (v.ValueKind == JsonValueKind.String) return "s:" + v.GetString();
                    if (v.ValueKind == JsonValueKind.Number || v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) return "s:" + v.GetRawText();
                    throw new Error(key + " is stored as a string — give text.");
                default:
                    throw new Error("The type of " + key + " must be one of: " + TypeNames + " (not '" + type + "').");
            }
        }

        private static bool IsWhole(double d) => double.IsFinite(d) && d == Math.Floor(d);

        private static double Number(string key, JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Number) { double d = v.GetDouble(); if (double.IsFinite(d)) return d; }
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double p) && double.IsFinite(p)) return p;
            throw new Error(key + " expects a number (got " + v.GetRawText() + ").");
        }
    }
}
