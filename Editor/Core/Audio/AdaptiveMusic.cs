using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;

namespace Editor.Core.Audio
{
    /// <summary>When a music change lands (#437): at once, or on the next beat / bar line / phrase end of the music.</summary>
    public enum MusicSync { Immediate, Beat, Bar, Phrase }

    /// <summary>
    /// Adaptive music (.vmusic, #437): stems of one tempo that play sample-locked as ONE music track; named states that
    /// give each stem a volume (stealth = pads only, combat = everything); parameters (intensity 0..1) that fade stems in
    /// and out; transition rules that say when a state change lands — at once, on the next beat, the next bar or the end
    /// of the phrase — with a crossfade and an optional stinger; and stingers. It plays on the music channel:
    /// <c>Audio.Music.Play("Assets/Audio/Music/spy.vmusic")</c>, then <c>Audio.Music.SetState("alert")</c>.
    /// Stem, stinger and parameter references keep the asset GUID (rename-proof) and the project-relative path (fallback).
    /// </summary>
    [DataContract(Name = "AdaptiveMusic", Namespace = "")]
    public class AdaptiveMusic
    {
        public const string FileExtension = ".vmusic";

        public static bool IsMusicPath(string path)
            => !string.IsNullOrEmpty(path) && path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase);

        /// <summary>Beats per minute — the beats of the time signature (6/8 at 180 = six eighth notes a bar, 180 a minute).</summary>
        [DataMember(Name = "bpm", Order = 0)] public float Bpm { get; set; } = 120f;
        /// <summary>Beats in a bar — the time signature's upper number.</summary>
        [DataMember(Name = "beatsPerBar", Order = 1)] public int BeatsPerBar { get; set; } = 4;
        /// <summary>The time signature's lower number (shown in the editor; the tempo counts BeatsPerBar beats per bar).</summary>
        [DataMember(Name = "beatUnit", Order = 2)] public int BeatUnit { get; set; } = 4;
        /// <summary>Seconds into the stems where bar 1 begins (a pickup or leading silence before it).</summary>
        [DataMember(Name = "offset", Order = 3)] public float OffsetSeconds { get; set; }
        /// <summary>Bars the music loops, from bar 1; 0 = the stems' length rounded to whole bars.</summary>
        [DataMember(Name = "loopBars", Order = 4)] public int LoopBars { get; set; }
        /// <summary>Bars in a phrase: "phrase" transitions wait for the next multiple of it.</summary>
        [DataMember(Name = "phraseBars", Order = 5)] public int PhraseBars { get; set; } = 4;
        /// <summary>Loop the music (false: it plays once and ends).</summary>
        [DataMember(Name = "loop", Order = 6)] public bool Loop { get; set; } = true;
        /// <summary>Stream long stems from disk instead of decoding them into memory. Streamed stems loop their whole
        /// files (LoopBars and Offset do not cut them), so give them one length.</summary>
        [DataMember(Name = "stream", Order = 7)] public bool Stream { get; set; }
        /// <summary>Volume of the whole music (multiplies Audio.Music.Volume).</summary>
        [DataMember(Name = "volume", Order = 8)] public float Volume { get; set; } = 1f;
        [DataMember(Name = "stems", Order = 9)] public List<Stem> Stems { get; set; } = new List<Stem>();
        [DataMember(Name = "states", Order = 10)] public List<State> States { get; set; } = new List<State>();
        /// <summary>The state the music starts in ("" = the first state).</summary>
        [DataMember(Name = "initialState", Order = 11)] public string InitialState { get; set; } = "";
        /// <summary>The rule for every state change no specific rule covers.</summary>
        [DataMember(Name = "transition", Order = 12)] public Transition DefaultTransition { get; set; } = new Transition();
        /// <summary>Rules for specific changes (from → to; "*" = any state), the most specific first.</summary>
        [DataMember(Name = "transitions", Order = 13)] public List<Transition> Transitions { get; set; } = new List<Transition>();
        [DataMember(Name = "stingers", Order = 14)] public List<Stinger> Stingers { get; set; } = new List<Stinger>();
        /// <summary>Starting values of the parameters (name → value 0..1); a parameter not listed starts at 0.</summary>
        [DataMember(Name = "parameters", Order = 15)] public Dictionary<string, float> Parameters { get; set; } = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Seconds a SetParameter glides the mapped stems to their new volumes.</summary>
        [DataMember(Name = "parameterFade", Order = 16)] public float ParameterFadeSeconds { get; set; } = 0.5f;

        [DataContract(Name = "MusicStem", Namespace = "")]
        public class Stem
        {
            [DataMember(Name = "name", Order = 0)] public string Name { get; set; } = "";
            [DataMember(Name = "guid", Order = 1)] public string Guid { get; set; } = "";
            [DataMember(Name = "clipPath", Order = 2)] public string ClipPath { get; set; } = "";
            /// <summary>The parameter this stem's volume follows ("" = none): silent at <see cref="From"/>, full at
            /// <see cref="To"/>, linear between — From above To fades it OUT as the parameter rises.</summary>
            [DataMember(Name = "parameter", Order = 3)] public string Parameter { get; set; } = "";
            [DataMember(Name = "from", Order = 4)] public float From { get; set; }
            [DataMember(Name = "to", Order = 5)] public float To { get; set; } = 1f;

            [OnDeserializing] private void Defaults(StreamingContext c) { Name = ""; Guid = ""; ClipPath = ""; Parameter = ""; From = 0f; To = 1f; }
        }

        [DataContract(Name = "MusicState", Namespace = "")]
        public class State
        {
            [DataMember(Name = "name", Order = 0)] public string Name { get; set; } = "";
            /// <summary>Stem name → volume 0..1 in this state; a stem not listed is silent in it.</summary>
            [DataMember(Name = "volumes", Order = 1)] public Dictionary<string, float> Volumes { get; set; } = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            [OnDeserializing] private void Defaults(StreamingContext c) { Name = ""; Volumes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase); }
        }

        [DataContract(Name = "MusicTransition", Namespace = "")]
        public class Transition
        {
            /// <summary>The state changed from ("*" or "" = any).</summary>
            [DataMember(Name = "from", Order = 0)] public string From { get; set; } = "*";
            /// <summary>The state changed to ("*" or "" = any).</summary>
            [DataMember(Name = "to", Order = 1)] public string To { get; set; } = "*";
            /// <summary>When it lands: "immediate", "beat", "bar" or "phrase".</summary>
            [DataMember(Name = "sync", Order = 2)] public string Sync { get; set; } = "bar";
            /// <summary>Seconds the stems glide to the new volumes from that moment (0 = a ~1 ms declick).</summary>
            [DataMember(Name = "fade", Order = 3)] public float FadeSeconds { get; set; } = 0.1f;
            /// <summary>A stinger (by name) played on the change ("" = none).</summary>
            [DataMember(Name = "stinger", Order = 4)] public string Stinger { get; set; } = "";

            [OnDeserializing] private void Defaults(StreamingContext c) { From = "*"; To = "*"; Sync = "bar"; FadeSeconds = 0.1f; Stinger = ""; }

            public MusicSync SyncMode => ParseSync(Sync, MusicSync.Bar);
        }

        [DataContract(Name = "MusicStinger", Namespace = "")]
        public class Stinger
        {
            [DataMember(Name = "name", Order = 0)] public string Name { get; set; } = "";
            [DataMember(Name = "guid", Order = 1)] public string Guid { get; set; } = "";
            [DataMember(Name = "clipPath", Order = 2)] public string ClipPath { get; set; } = "";
            [DataMember(Name = "volume", Order = 3)] public float Volume { get; set; } = 1f;
            /// <summary>When Audio.Music.PlayStinger plays it: "immediate", "beat", "bar" or "phrase".</summary>
            [DataMember(Name = "sync", Order = 4)] public string Sync { get; set; } = "beat";

            [OnDeserializing] private void Defaults(StreamingContext c) { Name = ""; Guid = ""; ClipPath = ""; Volume = 1f; Sync = "beat"; }

            public MusicSync SyncMode => ParseSync(Sync, MusicSync.Beat);
        }

        // DataContract deserialization skips field initializers: missing fields would read 0 BPM, 0 volume …
        [OnDeserializing]
        private void Defaults(StreamingContext c)
        {
            Bpm = 120f; BeatsPerBar = 4; BeatUnit = 4; OffsetSeconds = 0f; LoopBars = 0; PhraseBars = 4; Loop = true; Stream = false; Volume = 1f;
            Stems = new List<Stem>(); States = new List<State>(); InitialState = ""; DefaultTransition = new Transition();
            Transitions = new List<Transition>(); Stingers = new List<Stinger>();
            Parameters = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase); ParameterFadeSeconds = 0.5f;
        }

        [OnDeserialized]
        private void AfterLoad(StreamingContext c) => Normalize();

        /// <summary>Clamp what a hand-edited file or a tool could get wrong to values the player can use.</summary>
        public void Normalize()
        {
            if (!(Bpm >= 20f && Bpm <= 400f)) Bpm = float.IsNaN(Bpm) ? 120f : Math.Clamp(Bpm, 20f, 400f);
            BeatsPerBar = Math.Clamp(BeatsPerBar, 1, 32);
            BeatUnit = BeatUnit is 1 or 2 or 4 or 8 or 16 or 32 ? BeatUnit : 4;
            if (!(OffsetSeconds >= 0f)) OffsetSeconds = 0f;
            LoopBars = Math.Max(0, LoopBars);
            PhraseBars = Math.Max(1, PhraseBars);
            if (!(Volume >= 0f)) Volume = 1f;
            if (!(ParameterFadeSeconds >= 0f)) ParameterFadeSeconds = 0.5f;
            Stems ??= new List<Stem>(); States ??= new List<State>(); Transitions ??= new List<Transition>(); Stingers ??= new List<Stinger>();
            DefaultTransition ??= new Transition();
            InitialState ??= "";
            var pars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (Parameters != null) foreach (var kv in Parameters) if (!string.IsNullOrEmpty(kv.Key)) pars[kv.Key] = Clamp01(kv.Value);
            Parameters = pars;
            foreach (var s in States)
            {
                var v = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                if (s.Volumes != null) foreach (var kv in s.Volumes) if (!string.IsNullOrEmpty(kv.Key)) v[kv.Key] = Math.Clamp(float.IsNaN(kv.Value) ? 0f : kv.Value, 0f, 4f);
                s.Volumes = v;
            }
        }

        public static float Clamp01(float v) => v > 0f ? (v < 1f ? v : 1f) : 0f;   // NaN -> 0

        /// <summary>"bar", "nextbar", "next bar", "Bar" … → the sync mode; <paramref name="fallback"/> for anything else.</summary>
        public static MusicSync ParseSync(string s, MusicSync fallback)
        {
            string k = (s ?? "").Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
            switch (k)
            {
                case "immediate": case "now": case "instant": return MusicSync.Immediate;
                case "beat": case "nextbeat": return MusicSync.Beat;
                case "bar": case "nextbar": return MusicSync.Bar;
                case "phrase": case "endofphrase": case "nextphrase": return MusicSync.Phrase;
                default: return fallback;
            }
        }

        public static string SyncName(MusicSync s) => s switch { MusicSync.Immediate => "immediate", MusicSync.Beat => "beat", MusicSync.Phrase => "phrase", _ => "bar" };

        public State FindState(string name)
            => string.IsNullOrEmpty(name) ? null : States.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        public Stinger FindStinger(string name)
            => string.IsNullOrEmpty(name) ? null : Stingers.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The state the music starts in: <see cref="InitialState"/>, else the first state; null without states.</summary>
        public State StartState() => FindState(InitialState) ?? (States.Count > 0 ? States[0] : null);

        /// <summary>The rule for a change from <paramref name="from"/> to <paramref name="to"/>: from→to, then *→to, then
        /// from→*, then the default rule.</summary>
        public Transition TransitionFor(string from, string to)
        {
            static bool Any(string s) => string.IsNullOrEmpty(s) || s == "*";
            static bool Is(string rule, string state) => string.Equals(rule, state, StringComparison.OrdinalIgnoreCase);
            Transition anyFrom = null, anyTo = null;
            foreach (var t in Transitions)
            {
                if (t == null) continue;
                bool fromHit = Is(t.From, from), toHit = Is(t.To, to);
                if (fromHit && toHit) return t;
                if (Any(t.From) && toHit) anyFrom ??= t;
                else if (fromHit && Any(t.To)) anyTo ??= t;
            }
            return anyFrom ?? anyTo ?? DefaultTransition ?? new Transition();
        }

        /// <summary>Each stem's volume in a state (null state = every stem at 1: music without states).</summary>
        public float[] StateGains(State state)
        {
            var g = new float[Stems.Count];
            for (int i = 0; i < g.Length; i++)
                g[i] = state == null ? 1f : (state.Volumes.TryGetValue(Stems[i].Name ?? "", out float v) ? v : 0f);
            return g;
        }

        /// <summary>Each stem's volume from the parameters (a stem not mapped to one: 1).</summary>
        public float[] ParameterGains(IReadOnlyDictionary<string, float> values)
        {
            var g = new float[Stems.Count];
            for (int i = 0; i < g.Length; i++)
            {
                var s = Stems[i];
                if (string.IsNullOrEmpty(s.Parameter)) { g[i] = 1f; continue; }
                float x = values != null && values.TryGetValue(s.Parameter, out float v) ? v : 0f;
                g[i] = ParameterGain(x, s.From, s.To);
            }
            return g;
        }

        /// <summary>A mapped stem's volume at parameter value <paramref name="x"/>: 0 at <paramref name="from"/>, 1 at
        /// <paramref name="to"/>, linear between and clamped outside; from == to is a switch at that value.</summary>
        public static float ParameterGain(float x, float from, float to)
        {
            if (from == to) return x >= to ? 1f : 0f;
            return Clamp01((x - from) / (to - from));
        }

        /// <summary>A stem's / stinger's clip: the GUID first (rename-proof), the stored path as fallback.</summary>
        public static string ResolveClip(string guid, string clipPath)
        {
            if (!string.IsNullOrEmpty(guid) && System.Guid.TryParse(guid, out var g))
            {
                try
                {
                    var meta = Assets.AssetDatabase.Instance.GetAsset(g);
                    if (meta != null && !string.IsNullOrEmpty(meta.RelativePath)) return meta.RelativePath;
                }
                catch { /* no asset database (shipped game, tests): the path decides */ }
            }
            return string.IsNullOrEmpty(clipPath) ? null : clipPath;
        }

        public static AdaptiveMusic FromJson(string json)
        {
            var m = Serialization.DataSerializer.FromJson<AdaptiveMusic>(json);
            m?.Normalize();
            return m;
        }

        public string ToJson() => Serialization.DataSerializer.ToJson(this);

        public static AdaptiveMusic Load(string absolutePath)
        {
            try
            {
                if (File.Exists(absolutePath)) return FromJson(File.ReadAllText(absolutePath)) ?? new AdaptiveMusic();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[AdaptiveMusic] load failed: " + ex.Message); }
            return new AdaptiveMusic();
        }

        public void Save(string absolutePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            File.WriteAllText(absolutePath, ToJson());
        }

        /// <summary>A short summary for tooltips: "120 BPM 4/4 · 3 stems · 2 states".</summary>
        public string Describe()
            => Bpm.ToString("0.##", CultureInfo.InvariantCulture) + " BPM " + BeatsPerBar + "/" + BeatUnit + " · " + Stems.Count + (Stems.Count == 1 ? " stem" : " stems")
               + " · " + States.Count + (States.Count == 1 ? " state" : " states");
    }
}
