using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Editor.Core.Areas
{
    /// <summary>The shape of an area: a box (SizeX / Y / Z), a sphere (SizeX = diameter) or a vertical cylinder (SizeX =
    /// diameter, SizeY = height). All around the entity's position, turned and scaled by its transform.</summary>
    public enum AreaShape { Box = 0, Sphere = 1, Cylinder = 2 }

    /// <summary>
    /// A <c>.varea</c> file: a reusable area definition — its shape and size, the tags a script asks for ("cave",
    /// "no-fly", "safe"), free properties ("flightAllowed": false, "ambient": "cave_drips") and the outline colour the
    /// editor draws it in. An Area component references one (or carries its own values); the entity's transform places it.
    /// Nothing here is a hard-coded meaning: the engine tracks who is inside, the scripts decide what that means.
    /// </summary>
    public sealed class AreaDefinition
    {
        public const string Extension = ".varea";
        public string Name { get; set; } = "Area";
        public AreaShape Shape { get; set; } = AreaShape.Box;
        public float SizeX { get; set; } = 20f;
        public float SizeY { get; set; } = 10f;
        public float SizeZ { get; set; } = 20f;
        /// <summary>Tags, lower-case, as scripts query them.</summary>
        public List<string> Tags { get; } = new List<string>();
        /// <summary>Free key / value properties a script reads (strings; "true" / "false" and numbers parse on request).</summary>
        public Dictionary<string, string> Properties { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public float ColorR { get; set; } = 0.35f;
        public float ColorG { get; set; } = 0.85f;
        public float ColorB { get; set; } = 0.45f;
        /// <summary>Higher wins when a script asks for THE area at a point and several overlap.</summary>
        public int Priority { get; set; }

        public static AreaDefinition Load(string path)
        {
            string json = Services.AssetVfs.GetText(path) ?? (File.Exists(path) ? File.ReadAllText(path) : null);
            if (json == null) return null;
            return FromJson(json);
        }

        public static AreaDefinition FromJson(string json)
        {
            var d = new AreaDefinition();
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null) return d;
            d.Name = root["name"]?.GetValue<string>() ?? d.Name;
            string shape = root["shape"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(shape) && Enum.TryParse<AreaShape>(shape, true, out var sh)) d.Shape = sh;
            if (root["size"] is JsonArray size && size.Count >= 3)
            {
                d.SizeX = (float)(size[0]?.GetValue<double>() ?? d.SizeX); d.SizeY = (float)(size[1]?.GetValue<double>() ?? d.SizeY); d.SizeZ = (float)(size[2]?.GetValue<double>() ?? d.SizeZ);
            }
            if (root["tags"] is JsonArray tags) foreach (var t in tags) { var s = t?.GetValue<string>(); if (!string.IsNullOrWhiteSpace(s)) d.Tags.Add(s.Trim().ToLowerInvariant()); }
            if (root["properties"] is JsonObject props) foreach (var kv in props) d.Properties[kv.Key] = kv.Value?.ToJsonString().Trim('"') ?? "";
            if (root["color"] is JsonArray col && col.Count >= 3)
            {
                d.ColorR = (float)(col[0]?.GetValue<double>() ?? d.ColorR); d.ColorG = (float)(col[1]?.GetValue<double>() ?? d.ColorG); d.ColorB = (float)(col[2]?.GetValue<double>() ?? d.ColorB);
            }
            d.Priority = root["priority"]?.GetValue<int>() ?? 0;
            return d;
        }

        public string ToJson()
        {
            var root = new JsonObject
            {
                ["varea"] = 1,
                ["name"] = Name,
                ["shape"] = Shape.ToString(),
                ["size"] = new JsonArray(Math.Round(SizeX, 3), Math.Round(SizeY, 3), Math.Round(SizeZ, 3)),
                ["tags"] = new JsonArray(),
                ["properties"] = new JsonObject(),
                ["color"] = new JsonArray(Math.Round(ColorR, 3), Math.Round(ColorG, 3), Math.Round(ColorB, 3)),
                ["priority"] = Priority,
            };
            foreach (var t in Tags) ((JsonArray)root["tags"]).Add(t);
            foreach (var kv in Properties) ((JsonObject)root["properties"])[kv.Key] = kv.Value;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            bool created = !File.Exists(path);
            File.WriteAllText(path, ToJson());
            Services.PlaySession.NoteWrite(path, created);   // a file saved in play for an area Stop reverts goes with it (#423)
        }

        /// <summary>"cave, no-fly" ↔ the tag list.</summary>
        public static List<string> ParseTags(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim().ToLowerInvariant();
                if (t.Length > 0 && !list.Contains(t)) list.Add(t);
            }
            return list;
        }

        /// <summary>"flightAllowed=false\nambient=cave_drips" ↔ the property map (one key=value per line).</summary>
        public static Dictionary<string, string> ParseProperties(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return map;
            foreach (var raw in text.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                var k = raw.Substring(0, eq).Trim(); var v = raw.Substring(eq + 1).Trim();
                if (k.Length > 0) map[k] = v;
            }
            return map;
        }
    }
}
