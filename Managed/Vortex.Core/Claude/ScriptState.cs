using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Editor.Core.Claude
{
    /// <summary>
    /// The live state of the running game's scripts for the MCP tools get_script_state / set_script_state (#457): the
    /// public fields and properties of a running behaviour — those its own classes declare, not VortexBehaviour's engine
    /// members (Position, EntityId …, which get_entity shows) — or the public static fields and properties of a script
    /// class ("static:MissionManager"). Values come out as JSON values: numbers, strings, bools, enums by name, Vector3
    /// as [x, y, z], collections as lists (64 items at most), other objects as their members down to a depth, a reference
    /// to another behaviour as "Health on 'Npc'". A dotted path ("Instance.Credits") reaches into objects. Writes convert
    /// JSON to the member's type and are all checked before any is set. Reading runs property getters — game code — so a
    /// getter that throws shows its error as the value.
    /// </summary>
    public static class ScriptState
    {
        /// <summary>A reference or value the tools cannot use — the message is worded for the model.</summary>
        public sealed class Error : Exception { public Error(string message) : base(message) { } }

        /// <summary>Items listed per collection.</summary>
        public const int MaxItems = 64;
        /// <summary>Characters shown per string.</summary>
        public const int MaxText = 2000;
        /// <summary>The deepest a tool may ask for.</summary>
        public const int MaxDepth = 6;

        // ================================================================== members

        /// <summary>The members the tools see, in declaration order: an instance's public fields and readable properties
        /// without VortexBehaviour's own, or a class's public static ones (base classes included, constants not).</summary>
        public static List<MemberInfo> Members(Type type, bool statics)
        {
            var list = new List<MemberInfo>();
            if (type == null) return list;
            var flags = BindingFlags.Public | (statics ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);
            foreach (var f in type.GetFields(flags))
                if (!f.IsLiteral && !IsEngineMember(f) && !IsCompilerGenerated(f)) list.Add(f);
            foreach (var p in type.GetProperties(flags))
                if (p.GetIndexParameters().Length == 0 && p.GetMethod != null && p.GetMethod.IsPublic && !IsEngineMember(p)) list.Add(p);
            return list;
        }

        /// <summary>Declared by VortexBehaviour (or object): the engine's members, not the game's state.</summary>
        private static bool IsEngineMember(MemberInfo m) =>
            m.DeclaringType == typeof(Vortex.VortexBehaviour) || m.DeclaringType == typeof(object);

        private static bool IsCompilerGenerated(MemberInfo m) => m.Name.IndexOf('<') >= 0;

        /// <summary>A member by name — "Credits", "credits" and "credits_left" ≈ "CreditsLeft" are the same — or null.</summary>
        public static MemberInfo Find(Type type, bool statics, string name)
        {
            var members = Members(type, statics);
            return members.FirstOrDefault(m => m.Name == name)
                   ?? members.FirstOrDefault(m => Norm(m.Name) == Norm(name));
        }

        /// <summary>"Credits, Act, Instance" — for an error that names what exists.</summary>
        public static string Names(Type type, bool statics)
        {
            var n = Members(type, statics).Select(m => m.Name).ToList();
            return n.Count == 0 ? "(none)" : string.Join(", ", n);
        }

        private static string Norm(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private static Type MemberType(MemberInfo m) => m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;

        /// <summary>A member's value; a throwing getter becomes the text of its error.</summary>
        private static object Get(MemberInfo m, object owner)
        {
            try { return m is FieldInfo f ? f.GetValue(owner) : ((PropertyInfo)m).GetValue(owner); }
            catch (TargetInvocationException ex) { return new GetterError(ex.InnerException ?? ex); }
            catch (Exception ex) { return new GetterError(ex); }
        }

        private sealed class GetterError
        {
            public readonly string Text;
            public GetterError(Exception ex) { Text = "<error: " + ex.GetType().Name + ": " + ex.Message + ">"; }
        }

        // ================================================================== reading

        /// <summary>The state of <paramref name="instance"/> (or of <paramref name="type"/>'s statics): every member, or the
        /// members / dotted paths in <paramref name="fields"/>, as JSON values expanded <paramref name="depth"/> levels deep.</summary>
        public static Dictionary<string, object> Read(object instance, Type type, bool statics, IList<string> fields, int depth)
        {
            depth = Math.Clamp(depth, 0, MaxDepth);
            var result = new Dictionary<string, object>();
            if (fields == null || fields.Count == 0)
            {
                foreach (var m in Members(type, statics)) result[m.Name] = Json(Get(m, statics ? null : instance), depth);
                return result;
            }
            foreach (var path in fields)
            {
                var (owner, member) = Resolve(instance, type, statics, path, forWrite: false);
                result[path] = Json(Get(member, owner), depth);
            }
            return result;
        }

        /// <summary>A value as JSON: scalars always, objects and collections <paramref name="depth"/> levels deep (below that a
        /// summary such as "&lt;Inventory&gt;"; a collection of scalars is listed at any depth).</summary>
        public static object Json(object v, int depth)
        {
            switch (v)
            {
                case null: return null;
                case GetterError ge: return ge.Text;
                case string s: return s.Length > MaxText ? s.Substring(0, MaxText) + "…" : s;
                case bool b: return b;
                case char c: return c.ToString();
                case float f: return float.IsFinite(f) ? (object)f : f.ToString(CultureInfo.InvariantCulture);
                case double d: return double.IsFinite(d) ? (object)d : d.ToString(CultureInfo.InvariantCulture);
                case decimal m: return m;
                case Enum e: return e.ToString();
                case Vortex.Vector3 v3: return new object[] { Json(v3.X, 0), Json(v3.Y, 0), Json(v3.Z, 0) };
                case Vortex.VortexBehaviour vb: return Describe(vb);
                case Delegate del: return "<delegate " + TypeName(del.GetType()) + ">";
                case Type t: return "<type " + t.Name + ">";
            }
            var type = v.GetType();
            if (type.IsPrimitive) return v;   // the integers (int, long, short, byte …)
            if (v is IDictionary dict)
            {
                if (depth <= 0 && !AllScalar(dict.Values)) return "<" + TypeName(type) + " (" + dict.Count + " entries)>";
                var o = new Dictionary<string, object>();
                int n = 0;
                foreach (DictionaryEntry kv in dict)
                {
                    if (n++ == MaxItems) { o["…"] = (dict.Count - MaxItems) + " more"; break; }
                    o[System.Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? "null"] = Json(kv.Value, depth - 1);
                }
                return o;
            }
            if (v is IEnumerable seq)
            {
                var items = new List<object>();
                int count = 0;
                bool more = false;
                foreach (var item in seq)
                {
                    if (count == MaxItems) { more = true; break; }
                    items.Add(item);
                    count++;
                }
                if (depth <= 0 && !AllScalar(items)) return "<" + TypeName(type) + (v is ICollection col ? " (" + col.Count + " items)" : "") + ">";
                var list = items.Select(x => Json(x, depth - 1)).ToList();
                if (more) list.Add("… " + (v is ICollection c2 ? (c2.Count - MaxItems) + " more" : "more"));
                return list;
            }
            // .NET's own types (DateTime, TimeSpan, Guid, Random …) as their text, not their internals
            if (type.Namespace != null && type.Namespace.StartsWith("System", StringComparison.Ordinal))
                return System.Convert.ToString(v, CultureInfo.InvariantCulture);
            // any other object or struct: its public fields and properties
            if (depth <= 0) return "<" + TypeName(type) + ">";
            var obj = new Dictionary<string, object>();
            foreach (var m in Members(type, statics: false)) obj[m.Name] = Json(Get(m, v), depth - 1);
            return obj;
        }

        private static bool IsScalar(object v) =>
            v == null || v is string || v is bool || v is char || v is Enum || v is Vortex.Vector3 || v.GetType().IsPrimitive || v is decimal;

        private static bool AllScalar(IEnumerable items)
        {
            foreach (var x in items) if (!IsScalar(x)) return false;
            return true;
        }

        /// <summary>"Health on 'Npc'" — a behaviour another one points to (it is not expanded: it has its own entity).</summary>
        private static string Describe(Vortex.VortexBehaviour b)
        {
            string entity = "";
            try { entity = Editor.Scripting.ScriptRuntime.Instance.EntityNameOf(b.EntityId); } catch { }
            return b.GetType().Name + (b.Destroyed ? " (destroyed)" : string.IsNullOrEmpty(entity) ? "" : " on '" + entity + "'");
        }

        private static string TypeName(Type t)
        {
            if (!t.IsGenericType) return t.Name;
            string n = t.Name.Substring(0, Math.Max(0, t.Name.IndexOf('`')));
            return n + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
        }

        // ================================================================== paths

        /// <summary>"Instance.Credits": the object that holds the last member, and that member. Every step before the last
        /// must be an object (a class) that is not null; writing needs a field or a property with a public setter.</summary>
        private static (object owner, MemberInfo member) Resolve(object instance, Type type, bool statics, string path, bool forWrite)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new Error("An empty member name.");
            var parts = path.Split('.');
            object owner = statics ? null : instance;
            Type t = type;
            bool st = statics;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                string sofar = string.Join(".", parts.Take(i + 1));
                var m = Find(t, st, part) ?? throw new Error((i == 0 ? (st ? "The class " + t.Name + " has no public static member '" : t.Name + " has no public field or property '")
                                                                     : sofar.Substring(0, sofar.LastIndexOf('.')) + " (" + t.Name + ") has no public member '") + part + "'. Members: " + Names(t, st) + ".");
                if (i == parts.Length - 1)
                {
                    if (forWrite) CheckWritable(m, path);
                    return (owner, m);
                }
                var next = Get(m, st ? null : owner);
                if (next is GetterError ge) throw new Error(sofar + " could not be read: " + ge.Text);
                if (next == null) throw new Error(sofar + " is null — there is nothing to read or set inside it.");
                if (next.GetType().IsValueType)
                {
                    if (forWrite) throw new Error(sofar + " is a " + next.GetType().Name + " (a value type) — set " + sofar + " as a whole" + (next is Vortex.Vector3 ? ": [x, y, z]" : "") + ".");
                    if (next is Vortex.Vector3) throw new Error(sofar + " is a Vector3 — read it as a whole ([x, y, z]).");
                }
                owner = next;
                t = next.GetType();
                st = false;
            }
            throw new Error("An empty member name.");
        }

        private static void CheckWritable(MemberInfo m, string path)
        {
            if (m is FieldInfo f)
            {
                if (f.IsInitOnly) throw new Error(path + " is readonly.");
                return;
            }
            var p = (PropertyInfo)m;
            var set = p.SetMethod;
            if (set == null || !set.IsPublic) throw new Error(path + " has no public setter.");
            if (set.ReturnParameter.GetRequiredCustomModifiers().Any(x => x.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
                throw new Error(path + " can only be set when the object is created (init).");
        }

        // ================================================================== writing

        /// <summary>Set the members / dotted paths of <paramref name="values"/> (a JSON object) — every one is resolved and
        /// converted first, so a call with one bad value sets none. Returns the values read back after setting.</summary>
        public static Dictionary<string, object> Write(object instance, Type type, bool statics, JsonElement values)
        {
            if (values.ValueKind != JsonValueKind.Object) throw new Error("values must be an object {\"Member\": value, \"Instance.Credits\": 5000}.");
            var plan = new List<(string path, object owner, MemberInfo member, object value)>();
            foreach (var p in values.EnumerateObject())
            {
                var (owner, member) = Resolve(instance, type, statics, p.Name, forWrite: true);
                plan.Add((p.Name, owner, member, Convert(p.Value, MemberType(member), p.Name)));
            }
            if (plan.Count == 0) throw new Error("values is empty — name at least one member.");
            var done = new List<string>();
            foreach (var (path, owner, member, value) in plan)
            {
                try
                {
                    if (member is FieldInfo f) f.SetValue(owner, value);
                    else ((PropertyInfo)member).SetValue(owner, value);
                    done.Add(path);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    throw new Error("Setting " + path + " threw " + inner.GetType().Name + ": " + inner.Message + (done.Count > 0 ? " (already set: " + string.Join(", ", done) + ")" : " (nothing was set)"));
                }
            }
            var result = new Dictionary<string, object>();
            foreach (var (path, owner, member, _) in plan) result[path] = Json(Get(member, owner), 1);
            return result;
        }

        /// <summary>A JSON value as a member's type: strings, bools, every number type (whole numbers for the integer
        /// types), enums by name or number, Vector3 as [x, y, z] or {"x", "y", "z"}, arrays and List&lt;T&gt; element-wise,
        /// null for references and nullables.</summary>
        public static object Convert(JsonElement v, Type t, string name)
        {
            var nullable = Nullable.GetUnderlyingType(t);
            if (v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined)
            {
                if (!t.IsValueType || nullable != null) return null;
                throw new Error(name + " (" + TypeName(t) + ") cannot be null.");
            }
            if (nullable != null) t = nullable;
            if (t == typeof(string))
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number || v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False
                    ? v.GetRawText() : throw new Error(name + " expects a string.");
            if (t == typeof(bool))
            {
                if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) return v.GetBoolean();
                if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b)) return b;
                throw new Error(name + " expects true or false.");
            }
            if (t == typeof(char))
            {
                if (v.ValueKind == JsonValueKind.String && v.GetString().Length == 1) return v.GetString()[0];
                throw new Error(name + " expects a one-character string.");
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
                else if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long iv) && Enum.IsDefined(t, System.Convert.ChangeType(iv, Enum.GetUnderlyingType(t), CultureInfo.InvariantCulture)))
                    return Enum.ToObject(t, iv);
                throw new Error(name + " must be one of: " + string.Join(", ", names) + ".");
            }
            if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
            {
                double d = Number(v, name);
                if (t == typeof(float)) return (float)d;
                if (t == typeof(decimal)) return (decimal)d;
                return d;
            }
            if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort))
            {
                double d = Number(v, name);
                if (d != Math.Floor(d)) throw new Error(name + " is a whole number (" + t.Name + ") — " + v.GetRawText() + " is not.");
                try { return System.Convert.ChangeType(d, t, CultureInfo.InvariantCulture); }
                catch (OverflowException) { throw new Error(name + " (" + t.Name + ") cannot hold " + v.GetRawText() + "."); }
            }
            if (t == typeof(Vortex.Vector3))
            {
                if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 3)
                    return new Vortex.Vector3((float)Number(v[0], name), (float)Number(v[1], name), (float)Number(v[2], name));
                if (v.ValueKind == JsonValueKind.Object)
                {
                    float x = 0, y = 0, z = 0;
                    foreach (var p in v.EnumerateObject())
                    {
                        string k = p.Name.ToLowerInvariant();
                        if (k == "x") x = (float)Number(p.Value, name); else if (k == "y") y = (float)Number(p.Value, name); else if (k == "z") z = (float)Number(p.Value, name);
                    }
                    return new Vortex.Vector3(x, y, z);
                }
                throw new Error(name + " expects [x, y, z].");
            }
            if (t.IsArray)
            {
                if (v.ValueKind != JsonValueKind.Array) throw new Error(name + " expects an array.");
                var et = t.GetElementType();
                var items = v.EnumerateArray().Select((x, i) => Convert(x, et, name + "[" + i + "]")).ToList();
                var arr = Array.CreateInstance(et, items.Count);
                for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                return arr;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                if (v.ValueKind != JsonValueKind.Array) throw new Error(name + " expects an array.");
                var et = t.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(t);
                int i = 0;
                foreach (var x in v.EnumerateArray()) list.Add(Convert(x, et, name + "[" + i++ + "]"));
                return list;
            }
            throw new Error(name + " (" + TypeName(t) + ") cannot be set as a whole — set its members with a dotted path (" + name + ".Member).");
        }

        private static double Number(JsonElement v, string name)
        {
            if (v.ValueKind == JsonValueKind.Number) { double d = v.GetDouble(); if (double.IsFinite(d)) return d; }
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double p) && double.IsFinite(p)) return p;
            throw new Error(name + " expects a number (got " + v.GetRawText() + ").");
        }

        // ================================================================== classes

        /// <summary>A script class of the running game by name ("MissionManager", or with its namespace). Several of that name
        /// (in different namespaces) are listed, not picked; a name that matches nothing lists similar classes.</summary>
        public static Type FindClass(Assembly asm, string name)
        {
            if (asm == null) throw new Error("No game scripts are running.");
            string n = (name ?? "").Trim();
            if (n.Length == 0) throw new Error("Name the class: \"static:ClassName\".");
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }
            types = types.Where(x => !x.Name.StartsWith("<", StringComparison.Ordinal) && !x.IsGenericTypeDefinition).ToArray();
            var hits = types.Where(x => x.Name == n || x.FullName == n || x.FullName?.Replace('+', '.') == n).ToList();
            if (hits.Count == 0) hits = types.Where(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) throw new Error("Several classes are named " + n + ": " + string.Join(", ", hits.Select(h => h.FullName)) + " — use the full name.");
            var near = types.Where(x => x.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(x => x.Name).Distinct().Take(8).ToList();
            throw new Error("The running scripts have no class " + n + "." + (near.Count > 0 ? " Similar: " + string.Join(", ", near) + "." : ""));
        }
    }
}
