using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;

namespace Editor.Core.Audio
{
    /// <summary>
    /// A random sound container (.vsndc, issue #16): a list of clips with randomized
    /// pitch/volume ranges and a no-repeat shuffle-bag policy. Playable anywhere a
    /// single clip is accepted (AudioSource clip slot, Audio.PlayOneShot) — the
    /// resolution happens in C# before the native play call. Clip references keep
    /// BOTH the asset GUID (rename-proof, like material->texture deps) and the
    /// relative path (fallback / readability).
    /// </summary>
    [DataContract(Name = "SoundContainer", Namespace = "")]
    public class SoundContainer
    {
        public const string FileExtension = ".vsndc";

        [DataContract(Name = "SoundEntry", Namespace = "")]
        public class Entry
        {
            [DataMember(Name = "guid", Order = 0)] public string Guid { get; set; } = "";
            [DataMember(Name = "clipPath", Order = 1)] public string ClipPath { get; set; } = "";
            /// <summary>Relative pick probability (1 = normal).</summary>
            [DataMember(Name = "weight", Order = 2)] public float Weight { get; set; } = 1f;
        }

        [DataMember(Name = "entries", Order = 0)]
        public List<Entry> Entries { get; set; } = new List<Entry>();

        [DataMember(Name = "pitchMin", Order = 1)] public float PitchMin { get; set; } = 0.95f;
        [DataMember(Name = "pitchMax", Order = 2)] public float PitchMax { get; set; } = 1.05f;
        [DataMember(Name = "volumeMin", Order = 3)] public float VolumeMin { get; set; } = 0.9f;
        [DataMember(Name = "volumeMax", Order = 4)] public float VolumeMax { get; set; } = 1f;

        public static SoundContainer Load(string absolutePath)
        {
            try
            {
                if (File.Exists(absolutePath))
                    return Serialization.DataSerializer.FromJson<SoundContainer>(File.ReadAllText(absolutePath)) ?? new SoundContainer();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SoundContainer] load failed: " + ex.Message);
            }
            return new SoundContainer();
        }

        public void Save(string absolutePath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
                File.WriteAllText(absolutePath, Serialization.DataSerializer.ToJson(this));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SoundContainer] save failed: " + ex.Message);
            }
        }

        /// <summary>Resolves an entry's clip path: GUID first (rename-proof), stored
        /// path as fallback.</summary>
        public static string ResolveEntryPath(Entry entry)
        {
            if (entry == null) return null;
            if (!string.IsNullOrEmpty(entry.Guid) && System.Guid.TryParse(entry.Guid, out var g))
            {
                var meta = Assets.AssetDatabase.Instance.GetAsset(g);
                if (meta != null && !string.IsNullOrEmpty(meta.RelativePath)) return meta.RelativePath;
            }
            return string.IsNullOrEmpty(entry.ClipPath) ? null : entry.ClipPath;
        }
    }

    /// <summary>
    /// Runtime resolution for containers: per-container shuffle bags (each clip plays
    /// once before any repeats; the same clip never plays twice in a row across bag
    /// refills) and the pitch/volume rolls.
    /// </summary>
    public static class SoundContainerService
    {
        public struct Resolved
        {
            public string ClipPath;    // project-relative
            public float VolumeScale;  // multiply onto the caller's volume
            public float PitchScale;   // multiply onto the caller's pitch
        }

        private static readonly Random _rng = new Random();
        private sealed class BagState
        {
            public List<int> Bag = new List<int>();
            public int LastIndex = -1;
            public DateTime LoadedAt;
            public SoundContainer Container;
        }
        private static readonly Dictionary<string, BagState> _bags =
            new Dictionary<string, BagState>(StringComparer.OrdinalIgnoreCase);

        public static bool IsContainerPath(string path)
            => !string.IsNullOrEmpty(path) && path.EndsWith(SoundContainer.FileExtension, StringComparison.OrdinalIgnoreCase);

        /// <summary>Pick the next clip + rolls from a container. False when the file is
        /// missing/empty. absolutePath = resolved container file on disk.</summary>
        public static bool Resolve(string absolutePath, out Resolved result)
        {
            result = default;
            try
            {
                if (!_bags.TryGetValue(absolutePath, out var state)
                    || (DateTime.UtcNow - state.LoadedAt).TotalSeconds > 2.0) // cheap hot-reload
                {
                    state = new BagState { Container = SoundContainer.Load(absolutePath), LoadedAt = DateTime.UtcNow, LastIndex = state?.LastIndex ?? -1 };
                    _bags[absolutePath] = state;
                }

                var c = state.Container;
                if (c?.Entries == null || c.Entries.Count == 0) return false;

                int pick = NextFromBag(state, c);
                var entry = c.Entries[pick];
                var clip = SoundContainer.ResolveEntryPath(entry);
                if (string.IsNullOrEmpty(clip)) return false;

                result.ClipPath = clip;
                result.VolumeScale = Lerp(c.VolumeMin, c.VolumeMax, (float)_rng.NextDouble());
                result.PitchScale = Lerp(c.PitchMin, c.PitchMax, (float)_rng.NextDouble());
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SoundContainer] resolve failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Weighted shuffle bag: refill with every index (weight expands into
        /// duplicate tickets), draw randomly without replacement; the same entry never
        /// plays twice consecutively while there are at least 2 distinct entries.</summary>
        private static int NextFromBag(BagState state, SoundContainer c)
        {
            if (c.Entries.Count == 1) { state.LastIndex = 0; return 0; }

            if (state.Bag.Count == 0)
            {
                for (int i = 0; i < c.Entries.Count; i++)
                {
                    int tickets = Math.Max(1, (int)Math.Round(Math.Max(0.01f, c.Entries[i].Weight) * 4f));
                    for (int t = 0; t < tickets; t++) state.Bag.Add(i);
                }
            }

            // Draw, avoiding an immediate repeat of the previous pick.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int slot = _rng.Next(state.Bag.Count);
                int candidate = state.Bag[slot];
                if (candidate != state.LastIndex || state.Bag.TrueForAll(x => x == candidate))
                {
                    state.Bag.RemoveAt(slot);
                    state.LastIndex = candidate;
                    return candidate;
                }
            }
            // Bag only holds the previous clip's tickets — take it anyway.
            int last = state.Bag[state.Bag.Count - 1];
            state.Bag.RemoveAt(state.Bag.Count - 1);
            state.LastIndex = last;
            return last;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
