using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Editor.Core.Audio;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using VortexEditor.Panels;

namespace VortexEditor.Shell.Audio
{
    /// <summary>
    /// Adaptive music (#437) in the running editor, with generated stems at 240 BPM (a bar per second): in play mode
    /// Audio.Music.Play starts a .vmusic, Audio.Music.SetState("alert") schedules the switch exactly on a bar line and the
    /// native stem group begins it on that very frame, the rule's stinger and PlayStinger are taken, SetParameter fades the
    /// mapped stem in, and a script's OnBar / OnBeat are called — on the game tick nearest the bar line (the delivery error
    /// is logged). The editor path: the Asset Browser creates a .vmusic, the editor adds stems and previews a state change.
    /// Without an audio device the silent mixer runs on the game clock, so all of it holds there too.
    /// </summary>
    internal static class AdaptiveMusicSmoke
    {
        private const string Folder = "Assets/Audio/SmokeMusic";
        private const string Music = Folder + "/smoke.vmusic";
        private const string Script = "Assets/Scripts/SmokeConductor.cs";
        private const int Rate = 44100;

        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("audio music states: SetState lands on the bar line, OnBar / OnBeat reach scripts, stingers and parameters play", Run);
            SmokeRegistry.Add("audio music editor: the Asset Browser creates adaptive music, the editor adds stems and previews a state change, create_adaptive_music writes one", EditorPath);
        }

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            string root = ProjectData.Current?.Path;
            var scene = ProjectData.Current?.ActiveScene;
            if (root == null || scene == null) return false;
            string folder = Path.Combine(root, Folder.Replace('/', Path.DirectorySeparatorChar));
            bool audioExisted = Directory.Exists(Path.GetDirectoryName(folder));
            string script = Path.Combine(root, Script.Replace('/', Path.DirectorySeparatorChar));
            GameEntity conductor = null;
            bool started = false;
            var fails = new List<string>();
            void Expect(bool ok, string what) { if (!ok) fails.Add(what); }
            try
            {
                WriteStems(folder);
                // a 1 s parameter glide: sampled at any frame rate (WARP's 10-15 fps included) it shows values in between
                var def = new AdaptiveMusic { Bpm = 240, BeatsPerBar = 4, LoopBars = 4, InitialState = "calm", ParameterFadeSeconds = 1f };
                def.Stems.Add(new AdaptiveMusic.Stem { Name = "pads", ClipPath = Folder + "/pads.wav" });
                def.Stems.Add(new AdaptiveMusic.Stem { Name = "drums", ClipPath = Folder + "/drums.wav" });
                def.Stems.Add(new AdaptiveMusic.Stem { Name = "strings", ClipPath = Folder + "/strings.wav", Parameter = "intensity", From = 0f, To = 1f });
                var calm = new AdaptiveMusic.State { Name = "calm" }; calm.Volumes["pads"] = 1f; calm.Volumes["strings"] = 1f;
                var alert = new AdaptiveMusic.State { Name = "alert" }; alert.Volumes["pads"] = 1f; alert.Volumes["drums"] = 1f; alert.Volumes["strings"] = 1f;
                def.States.Add(calm); def.States.Add(alert);
                def.Stingers.Add(new AdaptiveMusic.Stinger { Name = "hit", ClipPath = Folder + "/hit.wav", Sync = "beat" });
                def.Transitions.Add(new AdaptiveMusic.Transition { From = "calm", To = "alert", Sync = "bar", FadeSeconds = 0.02f, Stinger = "hit" });
                def.Save(Path.Combine(root, Music.Replace('/', Path.DirectorySeparatorChar)));

                Directory.CreateDirectory(Path.GetDirectoryName(script));
                File.WriteAllText(script, "using System.Collections.Generic;\nusing Vortex;\npublic class SmokeConductor : VortexBehaviour\n{\n" +
                                          "    public static List<int> BarList = new List<int>(), BeatList = new List<int>();\n" +
                                          "    public override void OnBar(int bar) { BarList.Add(bar); }\n    public override void OnBeat(int beat) { BeatList.Add(beat); }\n}\n");
                conductor = scene.CreateEntity("SmokeConductor");
                conductor.AddComponentDirect(new Script(conductor, Script));

                EditorCommands.Play();
                started = PlayModeService.Instance.State == PlayState.Playing;
                if (!started) { log.LogError("audio music states: play mode did not start"); return false; }
                await Task.Delay(300);

                Vortex.Audio.Music.Play(Music);
                var svc = AudioPlaybackService.Instance;
                var player = svc.AdaptiveMusicForDiagnostics;
                if (player == null) { log.LogError("audio music states: Audio.Music.Play did not start the .vmusic"); return false; }
                Expect(Vortex.Audio.Music.State == "calm" && Vortex.Audio.Music.Bpm == 240f && Vortex.Audio.Music.BeatsPerBar == 4, "state calm, 240 BPM 4/4 (" + Vortex.Audio.Music.State + ", " + Vortex.Audio.Music.Bpm + ")");
                await Task.Delay(600);
                Expect(VortexAudio.GetMusicStemGain(player.Handle, 1) == 0f, "drums are silent in calm");

                // the switch: scheduled on a bar line of the music's own clock, begun natively on exactly that frame
                Expect(Vortex.Audio.Music.SetState("alert"), "SetState(alert) is taken");
                ulong at = player.LastSwitchPlayedFrame;
                double musical = player.LastSwitchMusicalFrame;
                double bars = musical / player.Timing.FramesPerBar;
                Expect(Math.Abs(bars - Math.Round(bars)) < 1e-9, "the switch is scheduled on a bar line (bar " + bars + ")");
                Expect(Vortex.Audio.Music.SetState("alert") && player.LastSwitchPlayedFrame == at, "asking again for the same state changes nothing");
                VortexAudio.MusicClock c = default;
                var waited = System.Diagnostics.Stopwatch.StartNew();   // a deadline, not a count of delays: frames may be slow
                while (waited.Elapsed.TotalSeconds < 5)
                {
                    await Task.Delay(50);
                    if (VortexAudio.GetMusicClock(player.Handle, out c) && c.Played > at + (ulong)(0.1 * c.SampleRate)) break;
                }
                Expect(c.LastStateFrame == at, "the stems began the switch on exactly that frame (scheduled " + at + ", began " + c.LastStateFrame + ")");
                Expect(Math.Abs(VortexAudio.GetMusicStemGain(player.Handle, 1) - 1f) < 1e-4f, "drums are at full volume in alert");

                // a stinger on the next beat, and a parameter crossfade
                Expect(Vortex.Audio.Music.PlayStinger("hit"), "PlayStinger(hit) is taken");
                Expect(!Vortex.Audio.Music.PlayStinger("nope") && !Vortex.Audio.Music.SetState("nope"), "unknown names are refused");
                // the 1 s glide: sampled until it settles (whatever the frame rate), some value lies between silent and full
                float stringsBefore = VortexAudio.GetMusicStemGain(player.Handle, 2);
                Vortex.Audio.Music.SetParameter("intensity", 1f);
                var glide = new List<float>();
                var gliding = System.Diagnostics.Stopwatch.StartNew();
                do { await Task.Delay(30); glide.Add(VortexAudio.GetMusicStemGain(player.Handle, 2)); }
                while (Math.Abs(glide[glide.Count - 1] - 1f) >= 1e-4f && gliding.Elapsed.TotalSeconds < 5);
                float stringsAfter = VortexAudio.GetMusicStemGain(player.Handle, 2);
                bool between = glide.Exists(g => g > 0.02f && g < 0.98f);
                Expect(stringsBefore == 0f && between && Math.Abs(stringsAfter - 1f) < 1e-4f,
                    "SetParameter crossfades the mapped stem (" + stringsBefore + " → " + string.Join(" ", glide.Distinct().Take(8).Select(g => g.ToString("0.00"))) + " → " + stringsAfter + ")");
                Expect(Vortex.Audio.Music.Time > 0f && Math.Abs(Vortex.Audio.Music.Length - 4.0f) < 0.05f, "Music.Time runs, Length is the stems' (" + Vortex.Audio.Music.Time + " / " + Vortex.Audio.Music.Length + ")");

                // OnBar / OnBeat reached the script: every beat once and in order — a stall longer than BeatTracker.MaxCatchUp
                // beats is skipped as a whole, never a beat lost in between — and OnBar on exactly the bar lines among them
                await Task.Delay(2200);
                var type = Editor.Scripting.ScriptRuntime.Instance.ScriptAssembly?.GetType("SmokeConductor");
                var barList = type?.GetField("BarList")?.GetValue(null) as List<int> ?? new List<int>();
                var beatList = type?.GetField("BeatList")?.GetValue(null) as List<int> ?? new List<int>();
                bool inOrder = beatList.Zip(beatList.Skip(1), (a, b) => b - a).All(gap => gap == 1 || gap > BeatTracker.MaxCatchUp);
                bool barLines = barList.SequenceEqual(beatList.Where(b => b % 4 == 0).Select(b => b / 4));
                Expect(barList.Count >= 3 && inOrder && barLines, "OnBar / OnBeat reached the script, every beat in order, OnBar on each bar line (" +
                    barList.Count + " bars, " + beatList.Count + " beats: " + string.Join(",", beatList) + ")");
                // each bar on its nearest tick, whatever the frame rate: never more than half a predicted tick early, and a late
                // one within the music its tick played since the tick before — one frame, however long that frame was
                var bars1 = player.BarDeliveries.Skip(1).ToList();   // the first bar may be the music's own start
                bool nearest = bars1.Count >= 2 && bars1.All(d => d.Error >= -d.Predicted / 2 - 0.002 && d.Error <= d.Window + 0.002);
                double worst = bars1.Count > 0 ? bars1.Max(d => Math.Abs(d.Error)) : double.NaN;
                double median = bars1.Count > 0 ? bars1.Select(d => Math.Abs(d.Error)).OrderBy(e => e).ElementAt(bars1.Count / 2) : double.NaN;
                string perBar = string.Join(" ", bars1.Select(d => (d.Error * 1000).ToString("+0.0;-0.0") + "/" + (d.Window * 1000).ToString("0")));
                Expect(nearest, "every bar is delivered on its nearest tick (error/frame ms: " + perBar + ")");

                log.Log("audio music states: device=" + VortexAudio.HasDevice() + ", switch on bar " + Math.Round(bars) + " began on its frame " + (c.LastStateFrame == at) +
                        ", bars delivered " + barList.Count + " / beats " + beatList.Count + ", OnBar error median " + (median * 1000).ToString("0.0") + " ms, worst " +
                        (worst * 1000).ToString("0.0") + " ms over " + bars1.Count + " bars (error/frame ms: " + perBar + "; a tick every " +
                        (player.AverageTickSeconds * 1000).ToString("0.0") + " ms)" + (fails.Count == 0 ? ", all as expected" : ", FAILED: " + string.Join("; ", fails)));
                return fails.Count == 0;
            }
            catch (Exception ex) { log.LogError("audio music states: " + ex); return false; }
            finally
            {
                if (started) EditorCommands.Stop();
                if (conductor != null) EditorCommands.DeleteEntities(new List<GameEntity> { conductor });
                foreach (var f in new[] { script, script + ".vmeta" }) try { if (File.Exists(f)) File.Delete(f); } catch { }
                RemoveFolder(folder, audioExisted);
            }
        }

        private static async Task<bool> EditorPath()
        {
            var log = ConsoleService.Instance;
            string root = ProjectData.Current?.Path;
            var browser = AssetBrowserPanel.Current;
            if (root == null || browser == null) { log.LogError("audio music editor: no project / Asset Browser"); return false; }
            string folder = Path.Combine(root, Folder.Replace('/', Path.DirectorySeparatorChar));
            bool audioExisted = Directory.Exists(Path.GetDirectoryName(folder));
            string created = null;
            AdaptiveMusicEditorWindow window = null;
            try
            {
                WriteStems(folder);
                created = browser.CreateAdaptiveMusic(open: true);
                await SmokeRegistry.Settle(500);
                window = Material.EditorKit.OpenWindows<AdaptiveMusicEditorWindow>().FirstOrDefault(w => created != null && Material.EditorKit.SamePath(w.FilePath, created));
                if (created == null || !File.Exists(created) || window == null) { log.LogError("audio music editor: the Asset Browser did not create and open a .vmusic (" + created + ")"); return false; }
                window.AddStems(new List<string> { Folder + "/pads.wav", Folder + "/drums.wav" });
                await SmokeRegistry.Settle(300);
                SmokeRegistry.Capture(window, "adaptive-music-editor.png");
                var saved = AdaptiveMusic.Load(created);
                bool stems = saved.Stems.Count == 2 && saved.States.Count == 2 && saved.States.All(s => s.Volumes.Count == 2);
                bool preview = window.PlayPreview();
                await Task.Delay(300);
                bool switched = preview && window.Preview != null && window.Preview.SetState("intense") && window.Preview.State == "intense";
                await Task.Delay(300);
                window.StopPreview();

                // the MCP tool: a valid definition is written (one undo step), a wrong stem name is refused with the stem list
                JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
                var made = JsonSerializer.SerializeToElement(VortexEditor.Claude.Tools.AudioTools.CreateAdaptiveMusic(name: "smoke_tool",
                    stems: J("[\"" + Folder + "/pads.wav\", {\"clip\": \"" + Folder + "/drums.wav\", \"name\": \"drums\", \"parameter\": \"intensity\", \"from\": 0.2, \"to\": 0.8}]"),
                    bpm: 100, states: J("{\"calm\": {\"pads\": 1}, \"combat\": {\"pads\": 1, \"drums\": 1}}"),
                    transitions: J("[{\"from\": \"calm\", \"to\": \"combat\", \"sync\": \"beat\", \"fade\": 0.05, \"stinger\": \"hit\"}]"),
                    stingers: J("[{\"name\": \"hit\", \"clip\": \"" + Folder + "/hit.wav\"}]"), folder: Folder));
                string toolRel = made.GetProperty("path").GetString();
                string toolFull = Path.Combine(root, toolRel.Replace('/', Path.DirectorySeparatorChar));
                var toolMusic = AdaptiveMusic.Load(toolFull);
                bool tool = File.Exists(toolFull) && toolMusic.Bpm == 100 && toolMusic.Stems.Count == 2 && toolMusic.Stems[1].Parameter == "intensity"
                            && toolMusic.TransitionFor("calm", "combat").Stinger == "hit" && toolMusic.StartState()?.Name == "calm";
                string refusal = null;
                try { VortexEditor.Claude.Tools.AudioTools.CreateAdaptiveMusic(name: "bad", stems: J("[\"" + Folder + "/pads.wav\"]"), states: J("{\"x\": {\"bass\": 1}}"), folder: Folder); }
                catch (Exception ex) { refusal = ex.Message; }
                bool refused = refusal != null && refusal.Contains("bass") && refusal.Contains("pads");
                Editor.Core.UndoRedo.UndoRedoManager.Instance.Undo();
                bool undone = !File.Exists(toolFull);
                string api = VortexEditor.Claude.Tools.ScriptTools.GetScriptingApi("Music");
                bool documented = api.Contains("SetState") && api.Contains("SetParameter") && api.Contains("PlayStinger") && api.Contains("next bar");

                // a stem audition (the stem row's Play) stops when the window closes — a 3-minute stem used to play on
                ulong audition = window.AuditionForSmoke(Folder + "/pads.wav");
                await Task.Delay(150);
                bool auditioned = VortexAudio.IsVoicePlaying(audition);
                window.Close();
                window = null;
                await SmokeRegistry.Settle(200);
                bool auditionStopped = auditioned && !VortexAudio.IsVoicePlaying(audition);

                log.Log("audio music editor: created " + Path.GetFileName(created) + ", stems saved=" + stems + " (" + saved.Describe() + "), preview=" + preview + ", state change=" + switched +
                        "; create_adaptive_music wrote " + toolRel + "=" + tool + ", refused a wrong stem=" + refused + " (" + refusal + "), undo removed it=" + undone + ", get_scripting_api Music documented=" + documented +
                        "; a stem audition played=" + auditioned + " and stopped with the window=" + auditionStopped);
                return stems && preview && switched && tool && refused && undone && documented && auditionStopped;
            }
            catch (Exception ex) { log.LogError("audio music editor: " + ex); return false; }
            finally
            {
                try { window?.Close(); } catch { }
                foreach (var f in new[] { created, created + ".vmeta" }) try { if (f != null && File.Exists(f)) File.Delete(f); } catch { }
                RemoveFolder(folder, audioExisted);
            }
        }

        /// <summary>Removes the smoke's folder — and Assets/Audio when the smoke created it and left it empty.</summary>
        private static void RemoveFolder(string folder, bool parentExisted)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
            try { if (File.Exists(folder + ".vmeta")) File.Delete(folder + ".vmeta"); } catch { }
            SmokeRegistry.RemoveIfCreated(Path.GetDirectoryName(folder), parentExisted);
        }

        /// <summary>Four bars at 240 BPM (4 s): a pad tone, a click on every beat, a high tone for the parameter stem, and
        /// a short stinger — mono 16-bit at 44.1 kHz.</summary>
        private static void WriteStems(string folder)
        {
            Directory.CreateDirectory(folder);
            WriteWav(Path.Combine(folder, "pads.wav"), 4.0, (i, t) => 0.2 * Math.Sin(2 * Math.PI * 220 * t));
            WriteWav(Path.Combine(folder, "drums.wav"), 4.0, (i, t) => (i % (Rate / 4)) < 400 ? 0.5 * Math.Sin(2 * Math.PI * 90 * t) : 0.0);
            WriteWav(Path.Combine(folder, "strings.wav"), 4.0, (i, t) => 0.15 * Math.Sin(2 * Math.PI * 660 * t));
            WriteWav(Path.Combine(folder, "hit.wav"), 0.3, (i, t) => 0.4 * Math.Sin(2 * Math.PI * 880 * t) * (1 - t / 0.3));
        }

        private static void WriteWav(string path, double seconds, Func<int, double, double> sample)
        {
            int n = (int)(Rate * seconds);
            using var w = new BinaryWriter(File.Create(path));
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
            for (int i = 0; i < n; i++) w.Write((short)Math.Round(Math.Clamp(sample(i, i / (double)Rate), -1, 1) * 32767));
        }
    }
}
