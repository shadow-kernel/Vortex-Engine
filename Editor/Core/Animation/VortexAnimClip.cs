using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Editor.Core.Animation
{
    /// <summary>One position/scale key (time in seconds).</summary>
    public class AnimKeyVec3
    {
        public float T { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    /// <summary>One rotation key (quaternion, time in seconds).</summary>
    public class AnimKeyQuat
    {
        public float T { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; } = 1f;
    }

    /// <summary>
    /// All keys for one bone. Bones are referenced by NAME (not index) so a clip survives
    /// mesh re-import and can be shared between models with compatible skeletons.
    /// Empty key lists fall back to the bone's bind-pose component.
    /// </summary>
    public class AnimTrack
    {
        public string Bone { get; set; } = "";
        public List<AnimKeyVec3> Pos { get; set; } = new List<AnimKeyVec3>();
        public List<AnimKeyQuat> Rot { get; set; } = new List<AnimKeyQuat>();
        public List<AnimKeyVec3> Scale { get; set; } = new List<AnimKeyVec3>();
    }

    /// <summary>Named marker on the timeline (footsteps, attack hits, ...). Fired into gameplay scripts.</summary>
    public class AnimEvent
    {
        public float T { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// A .vanim animation clip — standalone JSON asset (System.Text.Json, camelCase — the .vui family).
    /// Generated from FBX-embedded clips at import and authored/edited in the Keyframe Editor.
    /// Ships in Assets.vpak automatically; Load is VFS-aware for exported games.
    /// </summary>
    public class VortexAnimClip
    {
        /// <summary>Format version marker.</summary>
        public int Vanim { get; set; } = 1;

        public string Name { get; set; } = "New Clip";

        /// <summary>Project-relative model path this clip was authored against (preview + skeleton binding).</summary>
        public string Model { get; set; } = "";

        public float DurationSec { get; set; } = 1f;

        /// <summary>Authoring frame rate (timeline snapping only — keys store raw seconds).</summary>
        public float FrameRate { get; set; } = 30f;

        public bool Loop { get; set; } = true;

        public List<AnimTrack> Tracks { get; set; } = new List<AnimTrack>();

        public List<AnimEvent> Events { get; set; } = new List<AnimEvent>();

        private static JsonSerializerOptions Options => new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public bool Save(string filePath)
        {
            try
            {
                File.WriteAllText(filePath, JsonSerializer.Serialize(this, Options));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VortexAnimClip] save failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Loads a clip — from the in-RAM pak in shipped games, else from disk. Null on failure.</summary>
        public static VortexAnimClip Load(string filePath)
        {
            try
            {
                string json;
                if (Editor.Core.Services.AssetVfs.IsMounted && Editor.Core.Services.AssetVfs.Contains(filePath))
                    json = Editor.Core.Services.AssetVfs.GetText(filePath);
                else if (File.Exists(filePath))
                    json = File.ReadAllText(filePath);
                else
                    return null;

                return JsonSerializer.Deserialize<VortexAnimClip>(json, Options);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VortexAnimClip] load failed: " + ex.Message);
                return null;
            }
        }

        public AnimTrack GetOrAddTrack(string bone)
        {
            foreach (var t in Tracks)
                if (string.Equals(t.Bone, bone, StringComparison.Ordinal)) return t;
            var track = new AnimTrack { Bone = bone };
            Tracks.Add(track);
            return track;
        }

        public AnimTrack FindTrack(string bone)
        {
            foreach (var t in Tracks)
                if (string.Equals(t.Bone, bone, StringComparison.Ordinal)) return t;
            return null;
        }
    }
}
