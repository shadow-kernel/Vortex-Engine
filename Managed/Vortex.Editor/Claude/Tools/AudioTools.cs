using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Assets.Store;
using Editor.Core.Audio;
using Editor.Core.Services;
using Editor.Core.Audio.SoundStudio;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.DllWrapper;
using Editor.ECS.Components.Audio;
using ModelContextProtocol.Server;

namespace VortexEditor.Claude.Tools
{
    /// <summary>Audio (#99): find and audition sounds, set up AudioSources, read and set the mixer, generate sounds with the Sound Studio.</summary>
    [McpServerToolType, DisplayName("Audio")]
    public static class AudioTools
    {
        private static readonly string[] AudioExt = { ".wav", ".mp3", ".ogg", ".flac", ".vsndc" };
        private static readonly string[] ClipExt = { ".wav", ".mp3", ".ogg", ".flac" };

        [McpServerTool(Name = "search_audio", ReadOnly = true, Idempotent = true)]
        [Description("Finds sounds by name/tag text: the project's audio files and the user's asset library (library entries can be brought " +
                     "in with add_library_asset). Returns duration, channels and source/license.")]
        public static object SearchAudio(
            [Description("Search text, e.g. footstep, door creak, ambience")] string query = null,
            [Description("Maximum results per source")] int limit = 20)
        {
            limit = Math.Clamp(limit, 1, 100);
            var project = Directory.Exists(ProjectFiles.AssetsDir)
                ? Directory.EnumerateFiles(ProjectFiles.AssetsDir, "*", SearchOption.AllDirectories)
                    .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(ProjectFiles.Rel)
                    .Where(r => string.IsNullOrEmpty(query) || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => r.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(r => r, StringComparer.OrdinalIgnoreCase).Take(limit).ToList()
                : new List<string>();
            object library = null;
            var lib = GlobalAssetDatabase.Instance;
            if (lib.EnsureOpen())
                library = lib.Query(new LibraryQuery { Search = query, Types = new List<AssetType> { AssetType.Audio }, Limit = limit }).Select(e => new
                {
                    id = e.Id, name = e.Name, duration_s = e.Duration.HasValue ? (double?)Math.Round(e.Duration.Value, 2) : null, channels = e.Channels,
                    source = e.SourceName, license = e.License, tags = e.Tags.Count > 0 ? e.Tags.ToArray() : null,
                }).ToArray();
            return new
            {
                project = project.Select(r =>
                {
                    bool ok = VortexAudio.GetClipInfo(Path.Combine(ProjectFiles.Root, r), out float dur, out int rate, out int ch);
                    return new { path = r, duration_s = ok ? (double?)Math.Round(dur, 2) : null, channels = ok ? (int?)ch : null };
                }).ToArray(),
                library,
            };
        }

        private static ulong _voice = VortexAudio.InvalidVoice;

        [McpServerTool(Name = "audition_clip", ReadOnly = true)]
        [Description("Plays a sound in the editor so the user can hear it (a project path or a library entry id). One at a time; stop_audition stops it.")]
        public static object AuditionClip(
            [Description("Project path of the sound")] string path = null,
            [Description("Or: asset library entry id")] long? library_id = null,
            [Description("Volume 0–1")] float volume = 0.8f,
            [Description("Loop until stop_audition")] bool loop = false)
        {
            string full;
            if (!string.IsNullOrEmpty(path)) full = ProjectFiles.Resolve(path, AudioExt, mustExist: true);
            else if (library_id.HasValue)
            {
                var lib = GlobalAssetDatabase.Instance;
                var e = lib.EnsureOpen() ? lib.Get(library_id.Value) : null;
                if (e == null || e.Type != AssetType.Audio) throw new ToolError("No audio library entry " + library_id + ".");
                full = LibraryPreview.Materialize(lib, e) ?? throw new ToolError("The library has no bytes for entry " + library_id + ".");
            }
            else throw new ToolError("Give path or library_id.");
            StopVoice();
            _voice = VortexAudio.PlayVoice(full, Math.Clamp(volume, 0f, 1f), 1f, 0f, loop, 0, false, 4);
            if (_voice == VortexAudio.InvalidVoice) throw new ToolError("The sound could not be played (unreadable file, or the audio device is unavailable).");
            bool info = VortexAudio.GetClipInfo(full, out float dur, out int rate, out int ch);
            return new { playing = Path.GetFileName(full), duration_s = info ? (double?)Math.Round(dur, 2) : null, channels = info ? (int?)ch : null, loop };
        }

        [McpServerTool(Name = "stop_audition", ReadOnly = true, Idempotent = true)]
        [Description("Stops the sound audition_clip started.")]
        public static object StopAudition() { StopVoice(); return new { stopped = true }; }

        private static void StopVoice()
        {
            if (_voice != VortexAudio.InvalidVoice) { try { VortexAudio.StopVoice(_voice); } catch { } }
            _voice = VortexAudio.InvalidVoice;
        }

        [McpServerTool(Name = "configure_audio_source")]
        [Description("Sets up an entity's AudioSource (added when missing): clip (project path), volume, pitch, loop, play_on_awake, spatial " +
                     "(true = 3D), min_distance, max_distance, rolloff_mode, bus (Master|Music|SFX|Ambience|UI), priority, streaming, " +
                     "enable_occlusion, enable_hrtf … any AudioSource property. One undo step.")]
        public static object ConfigureAudioSource(
            [Description("Entity id, path or name")] string entity,
            [Description("Properties {\"clip\": \"Assets/Audio/hum.wav\", \"loop\": true, \"spatial\": true, \"max_distance\": 12, \"bus\": \"Ambience\"}")] JsonElement properties)
        {
            var e = SceneModel.Resolve(entity);
            if (properties.ValueKind != JsonValueKind.Object) throw new ToolError("properties must be an object.");
            var src = e.GetComponent<AudioSource>();
            bool added = src == null;
            if (added) { src = new AudioSource(e); e.AddComponent(src); }
            var done = new List<string>();
            foreach (var p in properties.EnumerateObject())
            {
                string n = ComponentProps.Norm(p.Name);
                var v = p.Value;
                switch (n)
                {
                    case "clip": case "audioclip": case "audioclippath": case "sound":
                    {
                        string full = ProjectFiles.Resolve(v.GetString(), AudioExt, mustExist: true);
                        string rel = ProjectFiles.Rel(full);
                        SceneModel.Set(src, "AudioClipPath", () => src.AudioClipPath, x => src.AudioClipPath = x, rel);
                        done.Add("clip");
                        break;
                    }
                    case "spatial":
                    {
                        bool on = v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0.5;
                        SceneModel.Set(src, "SpatialBlend", () => src.SpatialBlend, x => src.SpatialBlend = x, on ? 1f : 0f);
                        done.Add("spatial_blend");
                        break;
                    }
                    case "bus": case "outputbus":
                    {
                        int bus = v.ValueKind == JsonValueKind.Number ? v.GetInt32() : VortexAudio.BusIndexFromName(v.GetString());
                        if (bus < 0 || bus >= VortexAudio.BusNames.Length) throw new ToolError("bus must be one of: " + string.Join(", ", VortexAudio.BusNames) + ".");
                        SceneModel.Set(src, "OutputBus", () => src.OutputBus, x => src.OutputBus = x, bus);
                        done.Add("bus");
                        break;
                    }
                    default: done.Add(ComponentProps.Set(src, p.Name, v)); break;
                }
            }
            ToolContext.UndoLabel = (added ? "add audio to " : "configure audio of ") + e.Name;
            return new { entity = SceneModel.ShortId(e), added, set = done, properties = ComponentProps.Values(src) };
        }

        [McpServerTool(Name = "create_sound_container", Destructive = false)]
        [Description("Creates a random sound container (.vsndc) from clips — footstep, impact or creak variations — by default in " +
                     "Assets/Audio. Each play picks one clip (every clip once before any repeats, never the same twice in a row) and rolls " +
                     "pitch and volume within the ranges. It goes wherever a clip goes: configure_audio_source clip, Audio.PlayOneShot in " +
                     "scripts. One undo step (Undo deletes the file).")]
        public static object CreateSoundContainer(
            [Description("Container name (also the file name), e.g. footsteps_concrete")] string name,
            [Description("Project paths of the clips, e.g. [\"Assets/Audio/step_01.wav\", \"Assets/Audio/step_02.wav\"]")] string[] clips,
            [Description("Relative pick weights, one per clip (1 = normal)")] float[] weights = null,
            [Description("Pitch range [min, max] as multipliers, default [0.95, 1.05]")] float[] pitch = null,
            [Description("Volume range [min, max] as multipliers, default [0.9, 1]")] float[] volume = null,
            [Description("Folder relative to the project")] string folder = "Assets/Audio")
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ToolError("A container name is required.");
            if (clips == null || clips.Length == 0) throw new ToolError("clips needs at least one audio file (.wav, .mp3, .ogg, .flac).");
            if (weights != null && weights.Length != clips.Length) throw new ToolError("weights needs one value per clip (" + clips.Length + ").");
            var c = new SoundContainer();
            for (int i = 0; i < clips.Length; i++)
            {
                string rel = ProjectFiles.Rel(ProjectFiles.Resolve(clips[i], ClipExt, mustExist: true));
                float w = weights != null ? weights[i] : 1f;
                if (!(w > 0)) throw new ToolError("Every weight must be greater than 0.");
                // the GUID keeps the entry working when the clip is renamed or moved; the path is the fallback
                var meta = AssetDatabase.Instance.GetAssetByPath(rel);
                c.Entries.Add(new SoundContainer.Entry { Guid = meta != null ? meta.Guid.ToString() : "", ClipPath = rel, Weight = w });
            }
            if (pitch != null) { var r = Range(pitch, "pitch", 0.1f, 4f); c.PitchMin = r.min; c.PitchMax = r.max; }
            if (volume != null) { var r = Range(volume, "volume", 0f, 2f); c.VolumeMin = r.min; c.VolumeMax = r.max; }
            string dir = Path.GetDirectoryName(ProjectFiles.Resolve(Path.Combine(folder ?? "Assets/Audio", "x" + SoundContainer.FileExtension).Replace('\\', '/')));
            string target = ProjectFiles.Unique(dir, name.Trim(), SoundContainer.FileExtension);
            ProjectFiles.Write(target, Editor.Core.Serialization.DataSerializer.ToJson(c));
            ToolContext.UndoLabel = "create sound container " + Path.GetFileNameWithoutExtension(target);
            return new
            {
                path = ProjectFiles.Rel(target),
                clips = c.Entries.Select(x => x.ClipPath).ToArray(),
                pitch = new[] { c.PitchMin, c.PitchMax },
                volume = new[] { c.VolumeMin, c.VolumeMax },
            };
        }

        private static (float min, float max) Range(float[] r, string what, float lo, float hi)
        {
            if (r.Length != 2 || r[0] > r[1] || r[0] < lo || r[1] > hi)
                throw new ToolError(what + " must be [min, max] with " + lo + " <= min <= max <= " + hi + ".");
            return (r[0], r[1]);
        }

        // ================================================================== mixer

        [McpServerTool(Name = "get_mixer_state", ReadOnly = true)]
        [Description("The audio mixer: each bus (Master, Music, SFX, Ambience, UI) with volume in dB, mute and the current level.")]
        public static object GetMixerState() => VortexAudio.BusNames.Select((name, i) =>
        {
            float v = VortexAudio.GetBusVolume(i);
            VortexAudio.GetBusLevels(i, out float peak, out float rms);
            return new { bus = name, volume_db = Db(v), muted = VortexAudio.GetBusMute(i) ? (bool?)true : null, peak_db = Db(peak) };
        }).ToArray();

        [McpServerTool(Name = "set_bus_volume", Idempotent = true)]
        [Description("Sets a mixer bus volume in dB (0 = unchanged level, −6 = half, −80 = silent) and/or mute — saved in the project's " +
                     "mixer settings (what the game starts with). One undo step.")]
        public static object SetBusVolume(
            [Description("Master, Music, SFX, Ambience or UI")] string bus,
            [Description("Volume in dB (−80 … +6)")] float? db = null,
            [Description("Mute the bus")] bool? mute = null)
        {
            int i = VortexAudio.BusIndexFromName(bus);
            if (i < 0) throw new ToolError("bus must be one of: " + string.Join(", ", VortexAudio.BusNames) + ".");
            if (!db.HasValue && !mute.HasValue) throw new ToolError("Give db and/or mute.");
            string root = SceneModel.Project.Path;
            var before = AudioMixerConfig.Load(root);
            float oldV = before.BusVolumes[i]; bool oldM = before.BusMutes[i];
            float newV = db.HasValue ? (db.Value <= -80f ? 0f : (float)Math.Pow(10, Math.Clamp(db.Value, -80f, 6f) / 20.0)) : oldV;
            bool newM = mute ?? oldM;
            void SetTo(float v, bool m)
            {
                var c = AudioMixerConfig.Load(root);
                c.BusVolumes[i] = v; c.BusMutes[i] = m;
                c.Save(root);
                c.Apply();
            }
            UndoRedoManager.Instance.Execute(new ActionCommand("Mixer " + VortexAudio.BusNames[i], () => SetTo(newV, newM), () => SetTo(oldV, oldM)));
            ToolContext.UndoLabel = "mixer " + VortexAudio.BusNames[i] + (db.HasValue ? " " + db.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " dB" : "") + (mute == true ? " muted" : "");
            return new { bus = VortexAudio.BusNames[i], volume_db = Db(newV), muted = newM };
        }

        private static double Db(float linear) => linear <= 0.0001f ? -80 : Math.Round(20 * Math.Log10(linear), 1);

        // ================================================================== generation

        [McpServerTool(Name = "generate_sound"), OpenWorld]
        [Description("Generates a sound effect from a text prompt with the Sound Studio and saves it to the asset library and the project " +
                     "(returns the path for configure_audio_source). backend: procedural (free, offline synth), elevenlabs, fal-cassette-sfx, " +
                     "fal-stable-audio, stability-audio-2.5, stability-audio-3 — the AI backends use the user's own API key and may cost credits. " +
                     "Default: the first AI backend that has a key, else procedural. Describe the sound concretely: source, material, space, length. " +
                     "The procedural backend only knows a fixed set of recipes (click, beep, impact, whoosh, laser, gunshot, reload, explosion, heartbeat, riser, " +
                     "stinger, siren/alarm/horn, glass, fire, rain, water, creak, rustle, whisper, crowd, engine, wind, drone/ambience); the result says which " +
                     "one it used (family) and carries a warning when the prompt matched none.")]
        public static async Task<object> GenerateSound(
            [Description("What it sounds like, e.g. \"heavy wooden door creaking open slowly in a stone corridor\"")] string prompt,
            [Description("Length in seconds (backend limits apply)")] double? duration = null,
            [Description("Make it loop seamlessly (ambiences)")] bool loop = false,
            [Description("Backend id (see description)")] string backend = null,
            [Description("Name of the asset (default: from the prompt)")] string name = null,
            [Description("Also copy it into the project")] bool add_to_project = true,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(prompt)) throw new ToolError("prompt is empty.");
            var project = SceneModel.Project;
            ISoundBackend b;
            if (!string.IsNullOrEmpty(backend))
            {
                b = SoundBackends.All.FirstOrDefault(x => string.Equals(x.Id, backend.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolError("Unknown backend '" + backend + "'. Backends: " + string.Join(", ", SoundBackends.All.Select(x => x.Id)) + ".");
                if (b.KeyId != null && !StoreKeys.Has(b.KeyId))
                    throw new ToolError(b.Name + " needs the user's own API key — ask them to add it (Asset Store ▸ API Keys…), or use backend procedural.");
            }
            else b = SoundBackends.All.FirstOrDefault(x => x.KeyId != null && StoreKeys.Has(x.KeyId)) ?? SoundBackends.Get("procedural");
            if (duration.HasValue && (duration.Value <= 0 || duration.Value > b.MaxSeconds)) throw new ToolError(b.Name + " makes 0–" + b.MaxSeconds + " s.");
            if (loop && !b.SupportsLoop) loop = false;
            var req = new SoundRequest { Prompt = prompt.Trim(), DurationSeconds = duration, Loop = loop, Label = string.IsNullOrWhiteSpace(name) ? null : name.Trim(), Seed = Environment.TickCount & 0xffff };
            string outDir = Path.Combine(Path.GetTempPath(), "vortex-sound-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(outDir);
            GeneratedSound g;
            try { g = await b.GenerateAsync(req, outDir, ct); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new ToolError(b.Name + " could not generate the sound: " + ex.Message); }
            if (g == null || string.IsNullOrEmpty(g.FilePath) || !File.Exists(g.FilePath)) throw new ToolError(b.Name + " returned no audio.");
            var recipe = SoundRecipe.From(g, prompt, "claude", null, null);
            var saved = await Task.Run(() => SoundStudioLibrary.Save(g, recipe, add_to_project, project.Path, project.Name, new[] { "Claude" }));
            try { Directory.Delete(outDir, true); } catch { }
            if (!saved.Success) throw new ToolError("The sound was generated but could not be saved: " + saved.Error);
            try { if (saved.ProjectPath != null) AssetDatabase.Instance.RegisterFile(saved.ProjectPath); } catch { }
            var entry = saved.Entry;
            // the procedural synthesizer says what it understood (#353): it used to hand out a broadband noise burst for
            // every prompt outside its recipes without a word
            string family = null, keyword = null, warning = null;
            if (b.Id == "procedural")
            {
                family = ProceduralBackend.Family(req.Prompt, out keyword);
                if (family == null)
                    warning = "The procedural synthesizer has no recipe for this prompt and made a generic whoosh. It understands: " +
                              string.Join(", ", ProceduralBackend.KnownFamilies) + ". Use an AI backend (the user's API key) for anything else.";
            }
            return new
            {
                path = saved.ProjectPath != null ? ProjectFiles.Rel(saved.ProjectPath) : null,
                library_id = entry?.Id,
                name = entry?.Name,
                backend = b.Name,
                family,
                understood = b.Id == "procedural" ? (family != null ? "as " + family + " (\"" + keyword + "\")" : "no") : null,
                warning,
                duration_s = entry?.Duration.HasValue == true ? (double?)Math.Round(entry.Duration.Value, 2) : null,
                channels = entry?.Channels,
                license = b.LicenseId ?? "generated (yours)",
                cost = g.CostNote,
            };
        }
    }
}
