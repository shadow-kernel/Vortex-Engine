using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using Editor.Core.Data;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.ECS;
using Editor.ECS.Components;

namespace VortexEditor.Claude
{
    /// <summary>
    /// How tools find and describe entities. A reference is an id (the 8-hex short id every result shows, or a full
    /// GUID), a path ("Level/Corridor/Lamp 2") or a unique name; ambiguous or unknown references fail with the
    /// candidates listed, so the model can pick one instead of guessing.
    /// </summary>
    public static class SceneModel
    {
        public static ProjectData Project => ProjectData.Current ?? throw new ToolError("No project is open in the Vortex editor. Ask the user to open one.");

        public static Scene ActiveScene => Project.ActiveScene ?? throw new ToolError("The project has no active scene. Use list_scenes and open_scene.");

        /// <summary>Scenes to search: the active one first, then the other loaded scenes.</summary>
        public static IEnumerable<Scene> SearchScenes()
        {
            var active = ActiveScene;
            yield return active;
            foreach (var s in Project.Scenes)
                if (s != null && !ReferenceEquals(s, active) && s.IsLoaded) yield return s;
        }

        /// <summary>Every entity of a scene, depth-first in hierarchy order.</summary>
        public static IEnumerable<GameEntity> All(Scene scene)
        {
            if (scene?.Entities == null) yield break;
            var stack = new Stack<GameEntity>();
            for (int i = scene.Entities.Count - 1; i >= 0; i--) stack.Push(scene.Entities[i]);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (e == null) continue;
                yield return e;
                if (e.Children != null)
                    for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
            }
        }

        public static string ShortId(GameEntity e) => e.Id.ToString("N").Substring(0, 8);

        public static string PathOf(GameEntity e)
        {
            var parts = new List<string>();
            for (var cur = e; cur != null; cur = cur.Parent) parts.Add(cur.Name ?? "");
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static GameEntity Resolve(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) throw new ToolError("An entity reference is required (id, path or name).");
            string r = reference.Trim();
            var scenes = SearchScenes().ToList();

            // full GUID
            if (Guid.TryParse(r, out var guid))
            {
                foreach (var s in scenes) { var hit = s.FindEntityById(guid); if (hit != null) return hit; }
                throw new ToolError("No entity has the id " + r + ".");
            }
            // short id (hex prefix)
            if (r.Length >= 6 && r.Length <= 32 && r.All(Uri.IsHexDigit))
            {
                string p = r.ToLowerInvariant();
                var hits = scenes.SelectMany(All).Where(e => e.Id.ToString("N").StartsWith(p, StringComparison.Ordinal)).Take(5).ToList();
                if (hits.Count == 1) return hits[0];
                if (hits.Count > 1) throw Ambiguous(r, hits);
                // a name that happens to be hex ("cafe01") falls through to the name lookup
            }
            // path
            if (r.Contains('/'))
            {
                var parts = r.Split('/', StringSplitOptions.RemoveEmptyEntries);
                foreach (var s in scenes)
                {
                    IEnumerable<GameEntity> level = s.Entities;
                    GameEntity cur = null;
                    foreach (var part in parts)
                    {
                        var matches = level?.Where(x => x != null && string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches == null || matches.Count == 0) { cur = null; break; }
                        cur = matches[0];
                        level = cur.Children;
                    }
                    if (cur != null) return cur;
                }
                throw new ToolError("No entity at the path '" + r + "'." + Suggest(parts.Last(), scenes));
            }
            // name
            var named = scenes.SelectMany(All).Where(e => string.Equals(e.Name, r, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
            if (named.Count == 1) return named[0];
            if (named.Count > 1) throw Ambiguous(r, named);
            throw new ToolError("No entity named '" + r + "'." + Suggest(r, scenes));
        }

        public static List<GameEntity> ResolveMany(IEnumerable<string> references)
        {
            var list = new List<GameEntity>();
            if (references == null) return list;
            foreach (var r in references)
            {
                var e = Resolve(r);
                if (!list.Contains(e)) list.Add(e);
            }
            if (list.Count == 0) throw new ToolError("No entities given.");
            return list;
        }

        private static ToolError Ambiguous(string r, List<GameEntity> hits) =>
            new ToolError("'" + r + "' matches several entities — use an id or path: " +
                          string.Join("; ", hits.Select(h => ShortId(h) + " " + PathOf(h))));

        private static string Suggest(string name, IEnumerable<Scene> scenes)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Length == 0) return "";
            var close = scenes.SelectMany(All)
                .Where(e => e.Name != null && (e.Name.ToLowerInvariant().Contains(n) || n.Contains(e.Name.ToLowerInvariant())))
                .Take(6).Select(e => PathOf(e) + " (" + ShortId(e) + ")").ToList();
            return close.Count == 0 ? " Use scene_outline or find_entities to see what exists." : " Similar: " + string.Join("; ", close);
        }

        /// <summary>One line of facts about an entity: id, name, path, component types.</summary>
        public static object Brief(GameEntity e) => new
        {
            id = ShortId(e),
            name = e.Name,
            path = PathOf(e),
            components = e.Components.Where(c => !(c is Transform)).Select(c => c.GetType().Name).ToArray(),
            children = e.Children?.Count > 0 ? (int?)e.Children.Count : null,
            active = e.IsActive ? (bool?)null : false,
        };

        public static object TransformInfo(GameEntity e)
        {
            var t = e.Transform;
            if (t == null) return null;
            return new
            {
                position = ToolJson.V(t.LocalPosition),
                rotation = ToolJson.V(t.LocalRotation),
                scale = ToolJson.V(t.LocalScale),
                world_position = e.Parent != null ? ToolJson.V(TransformMath.WorldPosition(e)) : null,
            };
        }

        // ------------------------------------------------------------------ undoable edits

        /// <summary>Change a value through the undo stack (part of the running tool call's undo step).</summary>
        public static void Set<T>(object target, string name, Func<T> get, Action<T> set, T value)
        {
            T old = get();
            if (EqualityComparer<T>.Default.Equals(old, value)) return;
            UndoRedoManager.Instance.Execute(new PropertyChangeCommand<T>(target, name, set, old, value));
        }

        public static void SetTransform(GameEntity e, Vector3? pos, Vector3? rot, Vector3? scale)
        {
            var t = e.Transform ?? throw new ToolError(e.Name + " has no transform.");
            if (pos.HasValue) Set(t, "Position", () => t.LocalPosition, v => t.LocalPosition = v, pos.Value);
            if (rot.HasValue) Set(t, "Rotation", () => t.LocalRotation, v => t.LocalRotation = v, rot.Value);
            if (scale.HasValue) Set(t, "Scale", () => t.LocalScale, v => t.LocalScale = v, scale.Value);
        }

        public static Vector3? Vec(float[] v, string what)
        {
            if (v == null) return null;
            if (v.Length == 1) return new Vector3(v[0]);
            if (v.Length != 3) throw new ToolError(what + " needs 3 numbers [x, y, z] (got " + v.Length + ").");
            if (v.Any(f => !float.IsFinite(f))) throw new ToolError(what + " contains a non-finite number.");
            return new Vector3(v[0], v[1], v[2]);
        }
    }

    /// <summary>
    /// Components by name and their properties by reflection — the same public, serialized properties the
    /// Inspector edits. Values convert from JSON with clear errors (enums list their valid names).
    /// </summary>
    public static class ComponentProps
    {
        private static List<Type> _types;

        /// <summary>Component types a tool may add (not Transform, not abstract bases).</summary>
        public static IReadOnlyList<Type> Types => _types ??= typeof(Component).Assembly.GetTypes()
            .Where(t => typeof(Component).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(Transform) && t.Name != "Collider"
                        && t.GetConstructor(new[] { typeof(GameEntity) }) != null)
            .OrderBy(t => t.Name).ToList();

        public static Type FindType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ToolError("A component type is required. Valid: " + string.Join(", ", Types.Select(t => t.Name)));
            string n = Norm(name);
            var t = Types.FirstOrDefault(x => Norm(x.Name) == n);
            if (t == null && n == "transform") return typeof(Transform);
            return t ?? throw new ToolError("Unknown component type '" + name + "'. Valid: " + string.Join(", ", Types.Select(x => x.Name)));
        }

        public static Component Create(Type t, GameEntity e) => (Component)Activator.CreateInstance(t, e);

        /// <summary>The component of that type on the entity (the first one, or the index-th one).</summary>
        public static Component On(GameEntity e, string type, int index = 0)
        {
            var t = FindType(type);
            var list = e.Components.Where(c => t.IsInstanceOfType(c)).ToList();
            if (list.Count == 0)
                throw new ToolError(SceneModel.PathOf(e) + " has no " + t.Name + ". It has: " + string.Join(", ", e.Components.Select(c => c.GetType().Name)) + ".");
            if (index < 0 || index >= list.Count) throw new ToolError(SceneModel.PathOf(e) + " has " + list.Count + " " + t.Name + " component(s); index " + index + " is out of range.");
            return list[index];
        }

        /// <summary>Editable properties: public, read/write, serialized (what the Inspector shows), plus "Enabled".</summary>
        public static IEnumerable<PropertyInfo> Editable(Type t)
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0 || p.GetSetMethod() == null) continue;
                if (p.Name == "Id" || p.Name == "Entity") continue;
                if (p.Name != "IsEnabled" && p.GetCustomAttributes(typeof(IgnoreDataMemberAttribute), true).Length > 0) continue;
                if (!IsSupported(p.PropertyType)) continue;
                yield return p;
            }
        }

        private static bool IsSupported(Type t) =>
            t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(long) || t == typeof(bool) || t == typeof(string) ||
            t == typeof(Vector3) || t.IsEnum || t == typeof(float[]) || t == typeof(List<string>);

        public static PropertyInfo FindProperty(Component c, string name)
        {
            string n = Norm(name);
            if (n == "enabled") n = "isenabled";
            var props = Editable(c.GetType()).ToList();
            return props.FirstOrDefault(p => Norm(p.Name) == n)
                ?? throw new ToolError(c.GetType().Name + " has no editable property '" + name + "'. Properties: " + string.Join(", ", props.Select(p => SnakeName(p.Name))));
        }

        /// <summary>All editable values of a component, for get_entity.</summary>
        public static Dictionary<string, object> Values(Component c)
        {
            var d = new Dictionary<string, object>();
            foreach (var p in Editable(c.GetType()))
            {
                try { d[SnakeName(p.Name)] = ToJson(p.GetValue(c)); } catch { }
            }
            return d;
        }

        public static object ToJson(object v) => v switch
        {
            null => null,
            float f => ToolJson.R(f),
            double d => ToolJson.R(d),
            Vector3 v3 => ToolJson.V(v3),
            Enum e => e.ToString(),
            float[] a => a.Select(x => ToolJson.R(x)).ToArray(),
            _ => v,
        };

        /// <summary>Set one property (undoable). Also understands "color" on components with ColorR/G/B.</summary>
        public static string Set(Component c, string name, JsonElement value)
        {
            if (Norm(name) == "color" && c.GetType().GetProperty("ColorR") != null)
            {
                var rgb = ParseColor(value);
                foreach (var (ch, v) in new[] { ("ColorR", rgb[0]), ("ColorG", rgb[1]), ("ColorB", rgb[2]) })
                {
                    var cp = c.GetType().GetProperty(ch);
                    SceneModel.Set<object>(c, ch, () => cp.GetValue(c), x => cp.SetValue(c, x), v);
                }
                return "color";
            }
            var p = FindProperty(c, name);
            object converted = Convert(value, p.PropertyType, p.Name);
            SceneModel.Set<object>(c, p.Name, () => p.GetValue(c), x => p.SetValue(c, x), converted);
            return SnakeName(p.Name);
        }

        public static object Convert(JsonElement v, Type t, string what)
        {
            try
            {
                if (t == typeof(string)) return v.ValueKind == JsonValueKind.Null ? null : v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                if (t == typeof(bool))
                {
                    if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) return v.GetBoolean();
                    if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
                    throw new ToolError(what + " expects true or false.");
                }
                if (t == typeof(float)) return (float)Number(v, what);
                if (t == typeof(double)) return Number(v, what);
                if (t == typeof(int)) return (int)Math.Round(Number(v, what));
                if (t == typeof(long)) return (long)Math.Round(Number(v, what));
                if (t == typeof(Vector3))
                {
                    if (v.ValueKind == JsonValueKind.Number) { float f = (float)v.GetDouble(); return new Vector3(f); }
                    if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 3)
                        return new Vector3((float)Number(v[0], what), (float)Number(v[1], what), (float)Number(v[2], what));
                    if (v.ValueKind == JsonValueKind.Object)
                        return new Vector3(Comp(v, "x", what), Comp(v, "y", what), Comp(v, "z", what));
                    throw new ToolError(what + " expects [x, y, z].");
                }
                if (t == typeof(float[]))
                {
                    if (v.ValueKind != JsonValueKind.Array) throw new ToolError(what + " expects an array of numbers.");
                    return v.EnumerateArray().Select(x => (float)Number(x, what)).ToArray();
                }
                if (t == typeof(List<string>))
                {
                    if (v.ValueKind != JsonValueKind.Array) throw new ToolError(what + " expects an array of strings.");
                    return v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).ToList();
                }
                if (t.IsEnum)
                {
                    string[] names = Enum.GetNames(t);
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        string s = Norm(v.GetString());
                        var hit = names.FirstOrDefault(n => Norm(n) == s);
                        if (hit != null) return Enum.Parse(t, hit);
                    }
                    else if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int iv) && Enum.IsDefined(t, iv)) return Enum.ToObject(t, iv);
                    throw new ToolError(what + " must be one of: " + string.Join(", ", names) + ".");
                }
            }
            catch (ToolError) { throw; }
            catch (Exception ex) { throw new ToolError(what + ": " + ex.Message); }
            throw new ToolError(what + " (" + t.Name + ") cannot be set by tools.");
        }

        private static double Number(JsonElement v, string what)
        {
            if (v.ValueKind == JsonValueKind.Number) { double d = v.GetDouble(); if (double.IsFinite(d)) return d; }
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && double.IsFinite(p)) return p;
            throw new ToolError(what + " expects a number (got " + v.GetRawText() + ").");
        }

        private static float Comp(JsonElement o, string key, string what)
        {
            foreach (var prop in o.EnumerateObject())
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)) return (float)Number(prop.Value, what);
            return 0f;
        }

        /// <summary>"#ff8800", "#f80", [1, 0.5, 0] (0–1) or [255, 128, 0].</summary>
        public static float[] ParseColor(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString().Trim().TrimStart('#');
                if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
                if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                    return new[] { ((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f };
            }
            else if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() >= 3)
            {
                var a = v.EnumerateArray().Take(3).Select(x => (float)Number(x, "color")).ToArray();
                if (a.Any(x => x > 1f)) a = a.Select(x => x / 255f).ToArray();
                return a.Select(x => Math.Max(0f, x)).ToArray();
            }
            throw new ToolError("color expects \"#rrggbb\" or [r, g, b] (0–1 or 0–255).");
        }

        /// <summary>"Spot Angle", "spot_angle", "spotAngle" and "SpotAngle" are the same name.</summary>
        public static string Norm(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        public static string SnakeName(string pascal) => pascal == "IsEnabled" ? "enabled" : ToolCatalog.Snake(pascal);

        /// <summary>The settable properties of a type, for list_component_types.</summary>
        public static object Describe(Type t) => new
        {
            type = t.Name,
            properties = Editable(t).Select(p => p.PropertyType.IsEnum
                    ? SnakeName(p.Name) + ": " + string.Join("|", Enum.GetNames(p.PropertyType))
                    : SnakeName(p.Name) + ": " + TypeName(p.PropertyType)).ToArray(),
        };

        private static string TypeName(Type t) =>
            t == typeof(float) || t == typeof(double) ? "number" :
            t == typeof(int) || t == typeof(long) ? "integer" :
            t == typeof(bool) ? "bool" :
            t == typeof(Vector3) ? "[x,y,z]" :
            t == typeof(float[]) ? "number[]" :
            t == typeof(List<string>) ? "string[]" : "string";
    }
}
