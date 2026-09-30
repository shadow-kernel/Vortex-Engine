using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor.Core.Serialization;
using Editor.ECS;

namespace VortexEditor.Shell.Prefab
{
    /// <summary>
    /// What a prefab instance changed compared with its asset ("overrides"): the instance is serialized exactly like
    /// the template and both JSON trees are compared — entity by entity (children matched by name + order),
    /// component by component (matched by type + order). Ignored, like the prefab service treats them: ids, the
    /// prefab link, the ROOT transform (per-instance placement), the root name, and activeSelf of template entities
    /// that instantiation forces on. The result feeds the inspector's prefab bar ("3 overrides").
    /// </summary>
    public static class PrefabOverrides
    {
        public sealed class Result
        {
            public int Count;
            public bool TemplateMissing;
            public bool Failed;
            public readonly List<string> Items = new List<string>();
            public string Summary => TemplateMissing ? "asset missing" : Failed ? "" : Count == 0 ? "no overrides" : Count + " override" + (Count == 1 ? "" : "s");
        }

        private static readonly Dictionary<string, (DateTime stamp, string json)> _templates = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
        private const int MaxItems = 40;

        public static Result Compute(GameEntity instance)
        {
            var r = new Result();
            var root = PrefabWorkflow.FindInstanceRoot(instance);
            if (root == null) return r;
            string full = PrefabWorkflow.Resolve(root.PrefabPath);
            if (string.IsNullOrEmpty(full) || !File.Exists(full)) { r.TemplateMissing = true; return r; }
            try
            {
                string templateJson = Template(full);
                string instanceJson = DataSerializer.ToJson(root);
                using (var t = JsonDocument.Parse(templateJson, new JsonDocumentOptions { AllowTrailingCommas = true }))
                using (var i = JsonDocument.Parse(instanceJson))
                    CompareEntity(t.RootElement, i.RootElement, "", true, r);
            }
            catch { r.Failed = true; }
            return r;
        }

        private static string Template(string full)
        {
            var stamp = File.GetLastWriteTimeUtc(full);
            lock (_templates)
            {
                if (_templates.TryGetValue(full, out var hit) && hit.stamp == stamp) return hit.json;
                string text = File.ReadAllText(full);
                _templates[full] = (stamp, text);
                return text;
            }
        }

        private static void Add(Result r, string item)
        {
            r.Count++;
            if (r.Items.Count < MaxItems) r.Items.Add(item);
        }

        private static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static string Name(JsonElement e) => Str(e, "name") ?? "(entity)";
        private static string TypeOf(JsonElement c) { var t = Str(c, "__type") ?? "Component"; int i = t.IndexOf(':'); return i > 0 ? t.Substring(0, i) : t; }

        private static IEnumerable<JsonElement> Array(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

        private static void CompareEntity(JsonElement t, JsonElement i, string path, bool isRoot, Result r)
        {
            string here = isRoot ? "" : (string.IsNullOrEmpty(path) ? Name(i) : path + " › " + Name(i));
            // entity-level fields
            foreach (var prop in i.EnumerateObject())
            {
                string k = prop.Name;
                if (k == "id" || k == "prefabPath" || k == "children" || k == "components" || k == "__type") continue;
                if (isRoot && (k == "name" || k == "isActive")) continue;
                bool hasT = t.TryGetProperty(k, out var tv);
                if (k == "isActive")
                {
                    // instantiation forces every template entity active: only an instance that is OFF is an override
                    if (prop.Value.ValueKind == JsonValueKind.False) Add(r, Label(here, "Active"));
                    continue;
                }
                // a key the (older) template file lacks is a newer serializer field, not an edit
                if (hasT && !Same(tv, prop.Value)) Add(r, Label(here, k));
            }
            // components: match by type in order
            var tc = Array(t, "components").ToList();
            var ic = Array(i, "components").ToList();
            var used = new bool[tc.Count];
            foreach (var c in ic)
            {
                string type = TypeOf(c);
                int match = -1;
                for (int k = 0; k < tc.Count; k++) if (!used[k] && TypeOf(tc[k]) == type) { match = k; break; }
                if (match < 0) { Add(r, Label(here, "+ " + Pretty(type))); continue; }
                used[match] = true;
                CompareComponent(tc[match], c, here, type, isRoot, r);
            }
            for (int k = 0; k < tc.Count; k++) if (!used[k]) Add(r, Label(here, "− " + Pretty(TypeOf(tc[k]))));
            // children: match by name in order
            var tch = Array(t, "children").ToList();
            var ich = Array(i, "children").ToList();
            var usedC = new bool[tch.Count];
            foreach (var ch in ich)
            {
                string n = Name(ch);
                int match = -1;
                for (int k = 0; k < tch.Count; k++) if (!usedC[k] && Name(tch[k]) == n) { match = k; break; }
                if (match < 0) { Add(r, Label(here, "+ child " + n)); continue; }
                usedC[match] = true;
                CompareEntity(tch[match], ch, here, false, r);
            }
            for (int k = 0; k < tch.Count; k++) if (!usedC[k]) Add(r, Label(here, "− child " + Name(tch[k])));
        }

        private static void CompareComponent(JsonElement t, JsonElement i, string here, string type, bool isRootEntity, Result r)
        {
            foreach (var prop in i.EnumerateObject())
            {
                string k = prop.Name;
                if (k == "id" || k == "__type") continue;
                if (isRootEntity && type == "Transform" && (k == "localPosition" || k == "localRotation" || k == "localScale")) continue;
                if (t.TryGetProperty(k, out var tv) && !Same(tv, prop.Value)) Add(r, Label(here, Pretty(type) + "." + k));
            }
        }

        private static string Label(string here, string what) => string.IsNullOrEmpty(here) ? what : here + " › " + what;

        private static string Pretty(string type)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in type) { if (char.IsUpper(c) && sb.Length > 0) sb.Append(' '); sb.Append(c); }
            return sb.ToString();
        }

        private static bool Same(JsonElement a, JsonElement b)
        {
            if (a.ValueKind == JsonValueKind.Null && b.ValueKind == JsonValueKind.String && b.GetString() == "") return true;
            if (b.ValueKind == JsonValueKind.Null && a.ValueKind == JsonValueKind.String && a.GetString() == "") return true;
            if ((a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False) && (b.ValueKind == JsonValueKind.True || b.ValueKind == JsonValueKind.False))
                return a.ValueKind == b.ValueKind;
            if (a.ValueKind != b.ValueKind) return false;
            switch (a.ValueKind)
            {
                case JsonValueKind.Number:
                    return Math.Abs(a.GetDouble() - b.GetDouble()) <= 1e-5 * Math.Max(1.0, Math.Abs(a.GetDouble()));
                case JsonValueKind.String:
                    return string.Equals((a.GetString() ?? "").Replace('\\', '/'), (b.GetString() ?? "").Replace('\\', '/'), StringComparison.Ordinal);
                case JsonValueKind.Array:
                {
                    var x = a.EnumerateArray().ToList(); var y = b.EnumerateArray().ToList();
                    if (x.Count != y.Count) return false;
                    for (int k = 0; k < x.Count; k++) if (!Same(x[k], y[k])) return false;
                    return true;
                }
                case JsonValueKind.Object:
                {
                    var names = new HashSet<string>(a.EnumerateObject().Select(p => p.Name).Concat(b.EnumerateObject().Select(p => p.Name)));
                    names.Remove("id");
                    foreach (var n in names)
                    {
                        bool ha = a.TryGetProperty(n, out var va), hb = b.TryGetProperty(n, out var vb);
                        if (ha != hb) { if ((ha ? va : vb).ValueKind == JsonValueKind.Null) continue; return false; }
                        if (!Same(va, vb)) return false;
                    }
                    return true;
                }
                default:
                    return true;
            }
        }
    }
}
