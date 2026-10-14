using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Editor.Core.Services
{
    /// <summary>
    /// How a save writes project.vortex (#414): the manifest the editor builds — <c>mine</c>, as DataContractJsonSerializer
    /// has always written it — merged into the file on disk at the JSON level, so the file keeps what the editor does not
    /// own or did not change:
    /// <list type="bullet">
    /// <item>keys the engine does not know — at the top, in the settings, in a scene entry — stay where they are with
    /// their text as it is, whatever their name ("$schema", "release notes", "steam:appId");</item>
    /// <item>a value the editor did not change since it read or last wrote the file (<c>baseline</c>) keeps the file's
    /// current text: a version a release script bumped meanwhile, createdAt with its original offset and escaping;</item>
    /// <item>a value the editor changed is written (a rename, a setting edited in Project Settings, a new project's
    /// identity), and lastModified, engineVersion and formatVersion are stamped on every save;</item>
    /// <item>the scene list is the editor's; an entry keeps the file's keys besides id, name and path (loadOnStart …).</item>
    /// </list>
    /// What the merge changes is laid out like DataContractJsonSerializer's output (two-space indent, "[ ]" for an empty
    /// list) with the file's line endings; an object or list it does not change keeps the file's text, layout included.
    /// So saving an unchanged project rewrites nothing but the stamps.
    /// </summary>
    internal static class ProjectManifestJson
    {
        private static readonly HashSet<string> Stamps = new HashSet<string>(StringComparer.Ordinal) { "lastModified", "engineVersion", "formatVersion" };
        private static readonly HashSet<string> SceneKeys = new HashSet<string>(StringComparer.Ordinal) { "id", "name", "path" };

        /// <param name="mine">The editor's manifest as JSON (<c>DataSerializer.ToJson</c>).</param>
        /// <param name="disk">The file's current text (without a byte order mark), or null when there is none.</param>
        /// <param name="baseline">The editor's manifest JSON from when it read or last wrote the file, or null when unknown —
        /// then every value of <paramref name="mine"/> counts as changed.</param>
        /// <returns>The text to write; <paramref name="mine"/> itself when the file is missing or not a JSON object.</returns>
        internal static string Merge(string mine, string disk, string baseline)
        {
            using var m = JsonDocument.Parse(mine);
            using var d = TryParse(disk);
            if (d == null || d.RootElement.ValueKind != JsonValueKind.Object || m.RootElement.ValueKind != JsonValueKind.Object) return mine;
            using var b = TryParse(baseline);
            JsonElement? bRoot = b != null && b.RootElement.ValueKind == JsonValueKind.Object ? b.RootElement : (JsonElement?)null;
            // the file's line endings; a one-line file gets the serializer's
            var w = new Writer(disk.Contains("\r\n") ? "\r\n" : disk.Contains('\n') ? "\n" : mine.Contains("\r\n") ? "\r\n" : "\n");
            string text = w.Object(m.RootElement, d.RootElement, bRoot, 0, root: true);
            return disk.EndsWith("\n", StringComparison.Ordinal) ? text + w.NewLine : text;
        }

        private static JsonDocument TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonDocument.Parse(json); }
            catch (JsonException) { return null; }
        }

        /// <summary>One member or list item: its text, and whether that is the file's own text for it.</summary>
        private readonly struct Part
        {
            public readonly string Name, Text;
            public readonly bool FromFile;
            public Part(string name, string text, bool fromFile) { Name = name; Text = text; FromFile = fromFile; }
        }

        private sealed class Writer
        {
            public readonly string NewLine;

            public Writer(string newLine) { NewLine = newLine; }

            /// <summary>A value from the file: its text exactly as the file has it.</summary>
            private static string Raw(JsonElement e) => e.GetRawText();

            /// <summary>A value of the editor's: scalars as the serializer wrote them, objects and lists laid out again with
            /// the file's line endings.</summary>
            private string Value(JsonElement v, int depth)
            {
                if (v.ValueKind == JsonValueKind.Object)
                    return Layout(v.EnumerateObject().Select(p => new Part(p.Name, Value(p.Value, depth + 1), false)).ToList(), depth, '{', '}');
                if (v.ValueKind == JsonValueKind.Array)
                    return Layout(v.EnumerateArray().Select(e => new Part(null, Value(e, depth + 1), false)).ToList(), depth, '[', ']');
                return Raw(v);
            }

            /// <summary>An editor's value for a member the file has: whether it reads the same as the file is kept, so an
            /// object of unchanged members can be written as the file has it.</summary>
            private Part Mine(string name, JsonElement mine, bool hasDisk, JsonElement disk, int depth)
            {
                string text = Value(mine, depth);
                return new Part(name, text, hasDisk && text == Raw(disk));
            }

            /// <summary>The members in the file's order; a member only the editor has goes after the member it follows in
            /// <paramref name="mine"/>.</summary>
            private static List<string> Order(JsonElement mine, JsonElement disk)
            {
                var order = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in disk.EnumerateObject()) if (seen.Add(p.Name)) order.Add(p.Name);
                string previous = null;
                foreach (var p in mine.EnumerateObject())
                {
                    if (seen.Add(p.Name)) order.Insert(previous == null ? 0 : order.IndexOf(previous) + 1, p.Name);
                    previous = p.Name;
                }
                return order;
            }

            /// <summary>The top level and the settings: three-way per member (the editor's change wins, otherwise the file).</summary>
            public string Object(JsonElement mine, JsonElement disk, JsonElement? baseline, int depth, bool root)
            {
                var parts = new List<Part>();
                foreach (var name in Order(mine, disk))
                {
                    bool hasMine = mine.TryGetProperty(name, out var mv);
                    bool hasDisk = disk.TryGetProperty(name, out var dv);   // the last one of duplicates, as JSON readers take it
                    JsonElement bv = default;
                    bool hasBase = baseline.HasValue && baseline.Value.TryGetProperty(name, out bv);
                    if (!hasMine) parts.Add(new Part(name, Raw(dv), true));                      // a key only the file has
                    else if (root && name == "scenes")
                    {
                        string text = Scenes(mv, hasDisk ? dv : (JsonElement?)null, depth + 1, out bool same);
                        parts.Add(new Part(name, text, same));
                    }
                    else if (root && Stamps.Contains(name)) parts.Add(Mine(name, mv, hasDisk, dv, depth + 1));
                    else if (mv.ValueKind == JsonValueKind.Object && hasDisk && dv.ValueKind == JsonValueKind.Object)
                    {
                        string text = Object(mv, dv, hasBase && bv.ValueKind == JsonValueKind.Object ? bv : (JsonElement?)null, depth + 1, root: false);
                        parts.Add(new Part(name, text, text == Raw(dv)));
                    }
                    else if (hasBase && mv.GetRawText() == bv.GetRawText())
                    {
                        if (hasDisk) parts.Add(new Part(name, Raw(dv), true));                 // unchanged by the editor: the file's value
                    }                                                                          // (and a key the file dropped stays dropped)
                    else parts.Add(Mine(name, mv, hasDisk, dv, depth + 1));                   // changed by the editor
                }
                return parts.All(p => p.FromFile) ? Raw(disk) : Layout(parts, depth, '{', '}');
            }

            /// <summary>The editor's scene list; each entry keeps the file's keys besides id, name and path.</summary>
            private string Scenes(JsonElement mine, JsonElement? disk, int depth, out bool same)
            {
                same = false;
                if (mine.ValueKind != JsonValueKind.Array) return Value(mine, depth);
                var entries = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                if (disk?.ValueKind == JsonValueKind.Array)
                    foreach (var e in disk.Value.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            entries[id.GetString()] = e;
                var parts = new List<Part>();
                foreach (var item in mine.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        && entries.TryGetValue(id.GetString(), out var onDisk))
                    {
                        string text = Entry(item, onDisk, depth + 1);
                        parts.Add(new Part(null, text, text == Raw(onDisk)));
                    }
                    else parts.Add(new Part(null, Value(item, depth + 1), false));   // a scene the file does not list yet
                }
                // the same entries in the same order: the list as the file has it
                if (disk?.ValueKind == JsonValueKind.Array && parts.All(p => p.FromFile)
                    && disk.Value.EnumerateArray().Select(Raw).SequenceEqual(parts.Select(p => p.Text)))
                {
                    same = true;
                    return Raw(disk.Value);
                }
                return Layout(parts, depth, '[', ']');
            }

            /// <summary>A scene the file lists: id, name and path are the editor's, the rest (loadOnStart, keys the engine
            /// does not know) stays as the file has it.</summary>
            private string Entry(JsonElement mine, JsonElement disk, int depth)
            {
                var parts = new List<Part>();
                foreach (var name in Order(mine, disk))
                {
                    bool hasMine = mine.TryGetProperty(name, out var mv);
                    bool hasDisk = disk.TryGetProperty(name, out var dv);
                    if (hasMine && SceneKeys.Contains(name)) parts.Add(Mine(name, mv, hasDisk, dv, depth + 1));
                    else if (hasDisk) parts.Add(new Part(name, Raw(dv), true));
                }
                return parts.All(p => p.FromFile) ? Raw(disk) : Layout(parts, depth, '{', '}');
            }

            /// <summary>Members or items the way DataContractJsonSerializer lays them out ("{ }" / "[ ]" when empty).</summary>
            private string Layout(List<Part> parts, int depth, char open, char close)
            {
                if (parts.Count == 0) return open + " " + close;
                var sb = new StringBuilder().Append(open);
                for (int i = 0; i < parts.Count; i++)
                {
                    sb.Append(NewLine).Append(' ', 2 * (depth + 1));
                    if (parts[i].Name != null) sb.Append(Quote(parts[i].Name)).Append(": ");
                    sb.Append(parts[i].Text);
                    if (i < parts.Count - 1) sb.Append(',');
                }
                return sb.Append(NewLine).Append(' ', 2 * depth).Append(close).ToString();
            }

            /// <summary>A member name as JSON: only what JSON requires is escaped, so a name keeps the file's spelling.</summary>
            private static string Quote(string name)
            {
                var sb = new StringBuilder(name.Length + 2).Append('"');
                foreach (char c in name)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
                return sb.Append('"').ToString();
            }
        }
    }
}
