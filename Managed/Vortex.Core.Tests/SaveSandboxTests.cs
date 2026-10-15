using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Editor.Core.Claude;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>#458: play-testing saves. A project's Play-mode saves setting (Sandbox for new projects, Shared — today's
    /// behaviour — for every project made before) decides where Save keeps its slots while the game is played from its
    /// project; an exported game always uses its own folder. Save.Reload / Keys / DeleteSlot, a backslash that survives the
    /// file, and the MCP save tools' logic (SaveSlots): typed values, seeding before Play, the running game's cache in play,
    /// reset, snapshot and restore.</summary>
    public static class SaveSandboxTests
    {
        /// <summary>A project that plays from the editor with the given setting, in the test's folder (the app data folder
        /// is the test run's own VORTEX_APPDATA_DIR).</summary>
        private sealed class Host : IDisposable
        {
            private readonly ProjectData _before = ProjectData.Current;
            private readonly bool _plays = Vortex.Save.PlaysFromProject;
            public readonly ProjectData Project;

            public Host(TestContext t, string name, string setting, bool playsFromProject = true)
            {
                Project = new ProjectData(t.Path(name), name);
                Project.Settings.PlayModeSaves = setting;
                ProjectData.Current = Project;
                Vortex.Save.PlaysFromProject = playsFromProject;
                foreach (var dir in new[] { Vortex.Save.Folder(false), Vortex.Save.Folder(true) })
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Vortex.Save.BeginSession();
            }

            public void Dispose()
            {
                if (PlayModeService.Instance.State != PlayState.Editing) PlayModeService.Instance.Stop();
                try { foreach (var dir in new[] { Vortex.Save.Folder(false), Vortex.Save.Folder(true) }) if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                Vortex.Save.PlaysFromProject = _plays;
                ProjectData.Current = _before;
                Vortex.Save.BeginSession();
            }
        }

        private static JsonElement J(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

        [Test]
        public static void ASandboxedPlayNeverChangesTheGamesSave(TestContext t)
        {
            using var host = new Host(t, "Spectre", ProjectSettings.SandboxSaves);
            string real = Vortex.Save.SlotPath(0, sandbox: false), sandbox = Vortex.Save.SlotPath(0, sandbox: true);
            string appData = Environment.GetEnvironmentVariable("VORTEX_APPDATA_DIR");
            t.True(real.StartsWith(Path.Combine(appData, "VortexGames", "Spectre"), StringComparison.Ordinal), "the game's saves: VortexGames/<project>: " + real);
            t.Equal(Path.Combine(Path.GetDirectoryName(real), "editor", "save_slot0.dat"), sandbox, "the sandbox: its editor/ folder");
            Directory.CreateDirectory(Path.GetDirectoryName(real));
            File.WriteAllText(real, "credits\ti:777\n");   // the developer's own playthrough of the exported game

            Vortex.Save.BeginSession();                     // Play
            t.True(Vortex.Save.SessionSandboxed, "the session plays in the sandbox");
            t.Equal(0, Vortex.Save.GetInt("credits"), "it does not see the game's save");
            Vortex.Save.SetInt("credits", 5);
            Vortex.Save.UseSlot(2);
            Vortex.Save.SetString("name", "Ada");
            Vortex.Save.Flush();                            // Stop
            t.Equal("credits\ti:777\n", File.ReadAllText(real), "the game's save is untouched");
            t.False(File.Exists(Vortex.Save.SlotPath(2, false)), "no slot of the game's appeared");
            t.Equal("credits\ti:5\n", File.ReadAllText(sandbox), "the play session wrote the sandbox");

            // a setting changed while playing applies at the next Play — one session never reads one folder and writes the other
            host.Project.Settings.PlayModeSaves = ProjectSettings.SharedSaves;
            Vortex.Save.SetString("name", "Bea");
            Vortex.Save.Flush();
            t.Equal("name\ts:Bea\n", File.ReadAllText(Vortex.Save.SlotPath(2, true)), "still the sandbox until the session ends");
            Vortex.Save.BeginSession();
            t.False(Vortex.Save.SessionSandboxed, "Shared at the next Play");
            t.Equal(777, Vortex.Save.GetInt("credits"), "… which reads the game's own save, as before #458");
        }

        [Test]
        public static void AnExportedGameAndOldProjectsUseTheGamesOwnFolder(TestContext t)
        {
            using (var exported = new Host(t, "Shipped", ProjectSettings.SandboxSaves, playsFromProject: false))
            {
                Vortex.Save.BeginSession();
                t.False(Vortex.Save.SessionSandboxed, "an exported game never uses the sandbox, whatever the project says");
                t.Equal(Vortex.Save.SlotPath(0, false), Vortex.Save.SlotPath(0), "its slots are the game's own");
            }

            // project.vortex: a project made before #458 has no key — it reads as Shared and a save does not add one
            string dir = t.Path("old");
            t.Write("old/project.vortex", "{\n  \"id\": \"a1cb32ad-19e2-4796-aa3f-1515eff8926d\",\n  \"name\": \"Old\",\n  \"scenes\": [ ],\n" +
                                          "  \"settings\": { \"companyName\": \"Paddi\", \"vSync\": true }\n}\n");
            var manifest = DataSerializer.FromJson<ProjectManifest>(File.ReadAllText(Path.Combine(dir, "project.vortex"), Encoding.UTF8));
            var old = ProjectService.FromManifest(manifest, dir);
            t.False(old.Settings.SandboxedPlaySaves, "an old project keeps sharing the game's saves");
            ProjectService.RememberManifest(old);
            ProjectService.SaveManifestFile(old);
            t.False(File.ReadAllText(Path.Combine(dir, "project.vortex")).Contains("playModeSaves"), "saving it adds no key");
            var noSettings = ProjectService.FromManifest(new ProjectManifest("Older") { Settings = null }, dir);
            t.False(noSettings.Settings.SandboxedPlaySaves, "a manifest without settings too");

            // a new project uses the sandbox, and the choice round-trips
            string fresh = t.Path("fresh");
            Directory.CreateDirectory(fresh);
            var project = new ProjectData(fresh, "Fresh");
            t.True(project.Settings.SandboxedPlaySaves, "a new project plays in the sandbox");
            ProjectService.SaveManifestFile(project);
            var back = DataSerializer.FromJson<ProjectManifest>(File.ReadAllText(Path.Combine(fresh, "project.vortex"), Encoding.UTF8));
            t.Equal("Sandbox", back.Settings.PlayModeSaves, "written as \"playModeSaves\": \"Sandbox\"");
        }

        [Test]
        public static void ReloadKeysAndDeleteSlot(TestContext t)
        {
            using var host = new Host(t, "Api", ProjectSettings.SandboxSaves);
            Vortex.Save.BeginSession();
            Vortex.Save.SetInt("b", 2);
            Vortex.Save.SetFloat("a", 1.5f);
            Vortex.Save.SetString("path", "C:\\new\\tab\tline\r\nend");
            t.True(Vortex.Save.Keys().SequenceEqual(new[] { "a", "b", "path" }), "Keys: sorted, unsaved ones included");
            Vortex.Save.Flush();
            Vortex.Save.SetInt("b", 99);                    // not flushed …
            Vortex.Save.Reload();                           // … and dropped: the slot as it is on disk
            t.Equal(2, Vortex.Save.GetInt("b"), "Reload reads the file again");
            t.Equal("C:\\new\\tab\tline\r\nend", Vortex.Save.GetString("path"), "backslashes, tabs and line breaks survive the file");
            File.WriteAllText(Vortex.Save.SlotPath(0), "b\ti:3\n");   // a tool changed the file meanwhile
            Vortex.Save.Reload();
            t.True(Vortex.Save.Keys().SequenceEqual(new[] { "b" }) && Vortex.Save.GetInt("b") == 3, "… and sees what a tool wrote");
            t.True(Vortex.Save.SlotExists(0), "the slot exists");
            Vortex.Save.SetInt("c", 1);
            Vortex.Save.DeleteSlot(0);
            t.False(Vortex.Save.SlotExists(0), "DeleteSlot removes the file");
            t.Equal(0, Vortex.Save.Keys().Length, "and empties the active slot, unsaved keys too");
        }

        [Test]
        public static void SaveToolsSeedResetSnapshotAndRestore(TestContext t)
        {
            using var host = new Host(t, "Tools", ProjectSettings.SandboxSaves);
            // typed values: what Save.SetInt / SetFloat / SetString / SetBool write
            t.Equal("i:5000", SaveSlots.Raw("credits", J("5000"), null, null), "a new whole number is an int");
            t.Equal("f:2.5", SaveSlots.Raw("speed", J("2.5"), null, null), "a fraction a float");
            t.Equal("f:3", SaveSlots.Raw("speed", J("3"), null, "f:1.5"), "a number keeps its key's float type");
            t.Equal("i:1", SaveSlots.Raw("done", J("true"), null, null), "a bool as SetBool stores it");
            t.Equal("s:Ada", SaveSlots.Raw("name", J("\"Ada\""), null, null), "text");
            t.Equal("f:4", SaveSlots.Raw("x", J("4"), "float", null), "a type chosen");
            t.Equal(null, SaveSlots.Raw("gone", J("null"), null, "i:1"), "null removes the key");
            string err = null;
            try { SaveSlots.Raw("credits", J("2.5"), null, "i:100"); } catch (SaveSlots.Error e) { err = e.Message; }
            t.True(err != null && err.Contains("whole number") && err.Contains("\"float\""), "a fraction for an int key is refused, with the way out: " + err);
            t.Equal(5000L, SaveSlots.Value("i:5000", out string type), "read back");
            t.Equal("int", type, "with its type");

            // edit mode: set_save writes the sandbox file, and the next Play starts from it
            var seeded = new Dictionary<string, string> { ["act"] = "i:4", ["rank"] = "i:3", ["credits"] = "i:5000" };
            SaveSlots.Write(0, false, seeded, replace: false, out bool live, out string backup);
            t.False(live, "not playing: the file");
            t.Equal(null, backup, "a slot without a save has nothing to keep");
            t.True(File.Exists(Vortex.Save.SlotPath(0, true)) && !File.Exists(Vortex.Save.SlotPath(0, false)), "the sandbox, not the game's save");
            SaveSlots.Write(0, false, new Dictionary<string, string> { ["credits"] = "i:4000", ["rank"] = null }, replace: false, out _, out backup);
            t.True(backup != null && backup.StartsWith(SaveSlots.AutoPrefix, StringComparison.Ordinal), "overwriting a save keeps it first: " + backup);
            var read = SaveSlots.Read(0, false, out _);
            t.True(read.Count == 2 && read["credits"] == "i:4000" && read["act"] == "i:4", "merged, rank removed");
            Vortex.Save.BeginSession();                     // Play
            t.Equal(4, Vortex.Save.GetInt("act"), "the game starts in the seeded state");

            // play mode: the slot the game has open goes through its cache
            PlayModeService.Instance.Play();
            Vortex.Save.SetInt("xp", 10);                   // the game's own change, not flushed yet
            SaveSlots.Write(0, false, new Dictionary<string, string> { ["credits"] = "i:9999" }, replace: false, out live, out _);
            t.True(live, "live");
            t.Equal(9999, Vortex.Save.GetInt("credits"), "the running game sees it at once");
            t.Equal(10, Vortex.Save.GetInt("xp"), "its unsaved change is kept");
            t.True(File.ReadAllText(Vortex.Save.SlotPath(0, true)).Contains("xp\ti:10"), "and written with it — file and game agree");
            var gameView = SaveSlots.Read(0, false, out live);
            t.True(live && gameView["credits"] == "i:9999", "get_save shows the game's view");
            SaveSlots.Write(3, false, new Dictionary<string, string> { ["other"] = "i:1" }, replace: false, out live, out _);
            t.False(live, "a slot the game has not open is a file");

            // snapshot, change, restore — the running game reads the restored slot
            int keys = SaveSlots.Snapshot(0, false, "act4-start", out _);
            t.Equal(3, keys, "act, credits, xp");
            SaveSlots.Write(0, false, new Dictionary<string, string> { ["credits"] = "i:1" }, replace: true, out _, out _);
            t.Equal(1, Vortex.Save.GetInt("credits"), "replaced in the game");
            t.False(Vortex.Save.HasKey("act"), "replace left only the given keys");
            SaveSlots.Restore(0, false, "act4-start", out live, out backup);
            t.True(live, "restored into the running game");
            t.Equal(9999, Vortex.Save.GetInt("credits"), "the snapshot is back");
            t.Equal(4, Vortex.Save.GetInt("act"), "all of it");
            t.True(backup != null, "what the restore replaced was kept too");
            SaveSlots.Restore(0, false, backup, out _, out _);
            t.Equal(1, Vortex.Save.GetInt("credits"), "… and comes back from its automatic snapshot");
            SaveSlots.Restore(0, false, "act4-start", out _, out _);

            // a slot without a save: the snapshot remembers that, restore removes the slot again
            t.Equal(-1, SaveSlots.Snapshot(5, false, "act4-start", out _), "slot 5 has no save");
            SaveSlots.Write(5, false, new Dictionary<string, string> { ["junk"] = "i:1" }, replace: false, out _, out _);
            SaveSlots.Restore(5, false, "act4-start", out _, out _);
            t.False(File.Exists(Vortex.Save.SlotPath(5, true)), "restored to no save");
            t.True(SaveSlots.Snapshots().Contains("act4-start"), "the snapshot is listed");
            err = null;
            try { SaveSlots.Restore(0, false, "nope", out _, out _); } catch (SaveSlots.Error e) { err = e.Message; }
            t.True(err != null && err.Contains("act4-start"), "an unknown snapshot lists the real ones: " + err);
            err = null;
            try { SaveSlots.Snapshot(0, false, "../escape", out _); } catch (SaveSlots.Error e) { err = e.Message; }
            t.True(err != null, "a snapshot name cannot leave its folder");

            // reset: the game sees no save
            t.True(SaveSlots.Reset(0, false, out live, out backup) && live, "reset the open slot");
            t.False(Vortex.Save.SlotExists(0) || Vortex.Save.HasKey("credits"), "gone from disk and from the game");
            t.True(backup != null, "the reset slot was kept as an automatic snapshot");
            t.True(SaveSlots.Slots(false).SequenceEqual(new[] { 3 }), "slot 3 is left");
            PlayModeService.Instance.Stop();

            // game_saves: the exported game's own folder, explicitly
            SaveSlots.Write(0, true, new Dictionary<string, string> { ["real"] = "i:1" }, replace: false, out _, out _);
            t.True(File.Exists(Vortex.Save.SlotPath(0, false)), "game_saves writes the game's own slot");
            t.False(SaveSlots.UsesSandbox(true), "and says so");
        }

        [Test]
        public static void ASharedProjectsRealSaveIsNeverDeletedByDefault(TestContext t)
        {
            // #458 review: an old project (no key) plays on the exported game's own files — the save tools' default folder
            using var host = new Host(t, "Spectre", setting: null);
            string real = Vortex.Save.SlotPath(0, false);
            Directory.CreateDirectory(Path.GetDirectoryName(real));
            File.WriteAllText(real, "act\ti:6\ncredits\ti:91234\n");   // the developer's own playthrough
            t.False(SaveSlots.UsesSandbox(false), "Shared: the default folder is the game's own");
            foreach (var (what, call) in new (string, Action)[]
            {
                ("reset_save", () => SaveSlots.Reset(0, false, out _, out _)),
                ("restore_save", () => SaveSlots.Restore(0, false, "anything", out _, out _)),
                ("set_save with replace", () => SaveSlots.Write(0, false, new Dictionary<string, string> { ["x"] = "i:1" }, replace: true, out _, out _)),
            })
            {
                string err = null;
                try { call(); } catch (SaveSlots.Error e) { err = e.Message; }
                t.True(err != null && err.Contains("game_saves: true") && err.Contains(what), what + " is refused on the real save without game_saves: " + err);
                t.Equal("act\ti:6\ncredits\ti:91234\n", File.ReadAllText(real), "the real save is untouched after " + what);
            }

            // a merge is allowed — and keeps the save first; game_saves: true deletes on purpose, also kept first
            SaveSlots.Write(0, false, new Dictionary<string, string> { ["credits"] = "i:5" }, replace: false, out _, out string backup);
            t.True(backup != null, "set_save on the real save keeps it as an automatic snapshot first");
            t.True(SaveSlots.Reset(0, true, out _, out string resetBackup), "game_saves: true resets on purpose");
            t.False(File.Exists(real), "deleted");
            SaveSlots.Restore(0, true, backup, out _, out _);
            t.Equal("act\ti:6\ncredits\ti:91234\n", File.ReadAllText(real), "and the original playthrough comes back from the first automatic snapshot");
            t.True(resetBackup != null && resetBackup != backup, "each overwrite has its own snapshot");

            // only the newest automatic snapshots are kept — a named one never goes
            SaveSlots.Snapshot(0, true, "mine", out _);
            var names = new List<string>();
            for (int i = 0; i < SaveSlots.AutoKeep + 5; i++)   // as fast as it goes: many land in one millisecond
            {
                SaveSlots.Write(0, true, new Dictionary<string, string> { ["n"] = "i:" + i }, replace: false, out _, out string b);
                names.Add(b);
            }
            t.Equal(names.Count, names.Distinct().Count(), "every automatic snapshot has a name of its own, also within one millisecond");
            var snaps = SaveSlots.Snapshots();
            t.Equal(SaveSlots.AutoKeep, snaps.Count(s => s.StartsWith(SaveSlots.AutoPrefix, StringComparison.Ordinal)), "the newest " + SaveSlots.AutoKeep + " automatic snapshots: " + string.Join(", ", snaps));
            t.True(snaps.Contains(names[names.Count - 1]) && !snaps.Contains(names[0]), "the oldest went, the newest stayed");
            t.True(snaps.Contains("mine"), "a named snapshot is not pruned");

            // restoring an automatic snapshot backs the slot up first — into a snapshot of its own, never the one being read
            SaveSlots.Write(0, true, new Dictionary<string, string> { ["n"] = "i:100" }, replace: false, out _, out _);
            SaveSlots.Restore(0, true, names[names.Count - 1], out _, out string first);
            SaveSlots.Restore(0, true, first, out _, out string second);
            t.True(first != second && SaveSlots.Read(0, true, out _)["n"] == "i:100", "the restore's own backup brings n = 100 back: " + first + " / " + second);
        }
    }
}
