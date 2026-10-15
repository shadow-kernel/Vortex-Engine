using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "mcp save slots" (#458): on the template — a project made before #458, so Shared — reset_save without
    /// game_saves is refused and the real save stays; Project Settings shows Shared and saves the switch to Sandbox into
    /// project.vortex; then, over MCP, set_save seeds the sandbox and enter_play_mode starts the
    /// game in that state; while playing set_save changes the running game's cache, snapshot_save / restore_save bring a slot
    /// back, get_save shows the game's view; reset_save removes the slot. Through all of it — a probe that writes the save
    /// every frame, Stop flushing it — the exported game's own save_slot0.dat stays byte for byte what it was. Runs only with
    /// VORTEX_APPDATA_DIR set (smoke runs set it), so no real save is ever touched.
    /// </summary>
    internal static class McpSaveSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("mcp save slots", Run);

        private const string Probe =
            "using Vortex;\n\npublic class McpSaveProbe : VortexBehaviour\n{\n" +
            "    public int Act = -1, Rank = -1, StartCredits = -1, Credits = -1, Updates;\n" +
            "    public bool HadSave;\n" +
            "    public override void Start()\n    {\n        Cursor.Locked = false;\n" +
            "        HadSave = Save.SlotExists(0); Act = Save.GetInt(\"act\", -1); Rank = Save.GetInt(\"rank\", -1); StartCredits = Save.GetInt(\"credits\", -1);\n    }\n" +
            "    public override void Update(float dt) { Updates++; Credits = Save.GetInt(\"credits\", -1); Save.SetInt(\"ticks\", Updates); }\n" +
            "    public override void LateUpdate(float dt) { Cursor.Locked = false; }\n}\n";

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("mcp save slots: " + why); return false; }
            var project = ProjectData.Current;
            if (project?.ActiveScene == null) return Fail("no scene");
            string appData = Environment.GetEnvironmentVariable("VORTEX_APPDATA_DIR");
            if (string.IsNullOrEmpty(appData) || !Vortex.Save.Folder(false).StartsWith(Path.GetFullPath(appData), StringComparison.Ordinal))
                return Fail("VORTEX_APPDATA_DIR is not set — this check writes save files and never runs against the user's own");
            if (McpHost.IsRunning) return Fail("a server is already running");
            if (PlayModeService.Instance.IsPlaying) return Fail("the editor is already playing");
            string setting = project.Settings?.PlayModeSaves;
            string real = Vortex.Save.SlotPath(0, false), sandboxDir = Vortex.Save.Folder(true);
            string scriptFile = Path.Combine(project.Path, "Assets", "Scripts", "McpSaveProbe.cs");
            int undoBefore = UndoRedoManager.Instance.UndoCount;
            int port = McpTestClient.FreePort();
            if (!await McpHost.StartAsync(port)) return Fail("server did not start: " + McpHost.LastError);
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(60) };
            async Task<JsonNode> C(string tool, JsonObject args) => McpTestClient.Json(await McpTestClient.Call(http, tool, args));
            async Task<JsonNode> Probed()
            {
                var st = await C("get_script_state", new JsonObject { ["target"] = "Mcp Save Probe" });
                return st["behaviours"]?[0]?["state"];
            }
            try
            {
                await McpTestClient.Rpc(http, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "vortex-smoke-saves", ["version"] = "1" },
                });
                // the player's own playthrough of the exported game, which play testing must never change
                Directory.CreateDirectory(Path.GetDirectoryName(real));
                File.WriteAllText(real, "credits\ti:777\n");
                byte[] realBefore = File.ReadAllBytes(real);

                // ---- a project made before #458 (the template has no key) shares the game's files: deleting the real save by default
                // is refused (#458 review)
                project.Settings.PlayModeSaves = null;   // as the template has it today (the finally puts back what it was)
                var refused = await McpTestClient.Call(http, "reset_save", new JsonObject());
                if ((bool?)refused["isError"] != true || !McpTestClient.Text(refused).Contains("game_saves: true") || !File.ReadAllBytes(real).SequenceEqual(realBefore))
                    return Fail("reset_save on a Shared project's real save must need game_saves: " + McpTestClient.Text(refused));

                // ---- Project Settings: Shared is shown for it; the switch to Sandbox is saved
                var window = new ProjectSettingsWindow();
                window.Show();
                for (int i = 0; i < 60 && window.PlaySaves.SelectedIndex < 0; i++) await Task.Delay(50);
                bool sharedShown = window.PlaySaves.SelectedIndex == (project.Settings.SandboxedPlaySaves ? 0 : 1);
                window.PlaySaves.SelectedIndex = 0;
                await window.SaveAsync();
                SmokeRegistry.Capture(window, "project_settings_play_saves.png");
                window.Close();
                if (!sharedShown) return Fail("Project Settings does not show the project's Play-mode saves");
                string onDisk = (string)JsonNode.Parse(File.ReadAllText(Path.Combine(project.Path, "project.vortex")))?["settings"]?["playModeSaves"];
                if (project.Settings.PlayModeSaves != "Sandbox" || onDisk != "Sandbox")
                    return Fail("choosing Sandbox in Project Settings was not saved: " + project.Settings.PlayModeSaves + " / project.vortex: " + onDisk);

                await C("reset_save", new JsonObject());

                // ---- seed, then Play starts in that state
                var seeded = await C("set_save", new JsonObject { ["values"] = new JsonObject { ["act"] = 4, ["rank"] = 3, ["credits"] = 5000 } });
                if ((string)seeded["saves"] != "sandbox" || !((string)seeded["file"]).StartsWith(sandboxDir, StringComparison.Ordinal) || (int?)seeded["keys"] != 3)
                    return Fail("set_save should write the sandbox: " + seeded.ToJsonString());
                var got = await C("get_save", new JsonObject());
                if ((int?)got["values"]?["credits"] != 5000 || (string)got["types"]?["credits"] != "int" || (bool?)got["exists"] != true) return Fail("get_save: " + got.ToJsonString());
                var ws = await C("write_script", new JsonObject { ["script"] = "McpSaveProbe", ["code"] = Probe });
                if ((bool)ws["compiles"] != true) return Fail("the probe does not compile: " + ws.ToJsonString());
                await C("create_entity", new JsonObject { ["name"] = "Mcp Save Probe", ["position"] = new JsonArray(410, 1, 410) });
                await C("attach_script", new JsonObject { ["entity"] = "Mcp Save Probe", ["script"] = "McpSaveProbe" });
                await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.5 });
                var p = await Probed();
                if ((int?)p?["Act"] != 4 || (int?)p["Rank"] != 3 || (int?)p["StartCredits"] != 5000 || (bool?)p["HadSave"] != true)
                    return Fail("the game should start in the seeded state: " + p?.ToJsonString());

                // ---- while playing: the running game's cache
                var live = await C("set_save", new JsonObject { ["values"] = new JsonObject { ["credits"] = 7000 } });
                if ((bool?)live["live"] != true || live["runtime_only"] == null || (bool)live["runtime_only"]) return Fail("set_save in play should be live and say the file stays: " + live.ToJsonString());
                if (!((string)live["backup"] ?? "").StartsWith("auto-", StringComparison.Ordinal)) return Fail("overwriting a save keeps it as an automatic snapshot first: " + live.ToJsonString());
                await Task.Delay(200);
                if ((int?)(await Probed())?["Credits"] != 7000) return Fail("the running game did not see credits 7000");
                var view = await C("get_save", new JsonObject());
                if ((bool?)view["live"] != true || view["values"]?["ticks"] == null) return Fail("get_save in play shows the game's view, its unsaved ticks included: " + view.ToJsonString());
                var snap = await C("snapshot_save", new JsonObject { ["name"] = "mcp-smoke" });
                if ((int?)snap["keys"] < 4) return Fail("snapshot_save: " + snap.ToJsonString());
                await C("set_save", new JsonObject { ["values"] = new JsonObject { ["credits"] = 1 }, ["replace"] = true });
                await Task.Delay(200);
                if ((int?)(await Probed())?["Credits"] != 1) return Fail("replace did not reach the game");
                var restored = await C("restore_save", new JsonObject { ["name"] = "mcp-smoke" });
                await Task.Delay(200);
                if ((bool?)restored["live"] != true || (int?)(await Probed())?["Credits"] != 7000) return Fail("restore_save should bring credits 7000 back into the game: " + restored.ToJsonString());
                // the restore replaced credits 1 — its automatic snapshot brings that back, and the named one 7000 again
                await C("restore_save", new JsonObject { ["name"] = (string)restored["backup"] });
                await Task.Delay(200);
                if ((int?)(await Probed())?["Credits"] != 1) return Fail("the automatic snapshot of a restore did not bring the replaced slot back: " + restored.ToJsonString());
                await C("restore_save", new JsonObject { ["name"] = "mcp-smoke" });
                await C("exit_play_mode", new JsonObject());   // Stop flushes the probe's ticks — into the sandbox

                if (!File.ReadAllBytes(real).SequenceEqual(realBefore)) return Fail("play testing changed the exported game's save: " + File.ReadAllText(real));
                if (!File.ReadAllText(Vortex.Save.SlotPath(0, true)).Contains("ticks\t")) return Fail("Stop should have flushed the game's save into the sandbox");
                var mine = await C("get_save", new JsonObject { ["game_saves"] = true });
                if ((int?)mine["values"]?["credits"] != 777 || (string)mine["saves"] != "game") return Fail("get_save(game_saves) reads the game's own save: " + mine.ToJsonString());
                var reset = await C("reset_save", new JsonObject());
                var empty = await C("get_save", new JsonObject());
                if ((bool?)reset["deleted"] != true || reset["backup"] == null || (bool?)empty["exists"] != false || !(empty["snapshots"] as JsonArray).Any(n => (string)n == "mcp-smoke"))
                    return Fail("reset_save: " + reset.ToJsonString() + " " + empty.ToJsonString());

                log.Log("mcp save slots: OK — on the Shared template reset_save without game_saves was refused; Project Settings saved Sandbox; set_save seeded act 4 / rank 3 / 5000 credits and Play started there; live set, snapshot, replace, restore (and back from its automatic snapshot) reached the running game; the exported game's save_slot0.dat stayed byte for byte");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                if (PlayModeService.Instance.IsPlaying) EditorCommands.Stop();
                while (UndoRedoManager.Instance.UndoCount > undoBefore && UndoRedoManager.Instance.Undo()) { }
                EditorCommands.AfterSceneEdit();
                foreach (var f in new[] { scriptFile, scriptFile + ".vmeta", real }) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
                try { if (Directory.Exists(sandboxDir)) Directory.Delete(sandboxDir, true); } catch { }
                // the project as it was: the setting back, written the way the editor writes it
                try { project.Settings.PlayModeSaves = setting; ProjectService.SaveManifestFile(project); } catch { }
                await McpHost.StopAsync();
            }
        }
    }
}
