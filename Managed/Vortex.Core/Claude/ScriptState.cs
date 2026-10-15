using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;

namespace Editor.Core.Claude
{
    /// <summary>
    /// The live state of the running game's scripts for the MCP tools get_script_state / set_script_state (#457): the
    /// public fields and properties of a running behaviour — those its own classes declare, not VortexBehaviour's engine
    /// members (Position, EntityId …, which get_entity shows) — or the public static fields and properties of a script
    /// class ("static:MissionManager").
    /// <para>Reading runs no game code unless asked to: fields and auto-properties are read directly (an auto-property
    /// through its backing field); a property with a getter of its own is only listed (<see cref="Snapshot.Computed"/>)
    /// and runs only when a path names it — a getter that calls itself is refused, since its stack overflow would take the
    /// editor down with it. Values come out as JSON values: numbers, strings, bools, enums by name, Vector3 / Quaternion /
    /// Color as arrays, the game's own objects as their members down to a depth, the BCL's collections as lists (64 items
    /// at most), a reference to another behaviour as "Health on 'Npc'"; an object already shown (a shared node, a cycle) is
    /// not expanded twice, and one read stops at <see cref="MaxValues"/> values / <see cref="MaxChars"/> characters.</para>
    /// <para>A dotted path ("Instance.Credits", "Tint.R") goes only into the game's own classes and structs and the engine's
    /// structs; writes convert JSON to the member's type, are all checked before any is set, and a write inside a struct is
    /// written back to the member that holds it.</para>
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
        /// <summary>Values one read returns at most (then <see cref="Snapshot.Truncated"/>).</summary>
        public const int MaxValues = 5000;
        /// <summary>Characters (roughly) one read returns at most.</summary>
        public const long MaxChars = 400_000;

        // ================================================================== members

        /// <summary>A field or property as the tools see it.</summary>
        public sealed class Member
        {
            public MemberInfo Info;
            /// <summary>The compiler's backing field of an auto-property — read instead of running the getter.</summary>
            public FieldInfo Backing;
            public string Name => Info.Name;
            public Type Type => Info is FieldInfo f ? f.FieldType : ((PropertyInfo)Info).PropertyType;
            /// <summary>A property with a getter of its own: game code, run only when a path names it.</summary>
            public bool Computed => Info is PropertyInfo && Backing == null;
        }

        /// <summary>The members the tools see, in declaration order: an instance's public fields and readable properties
        /// without VortexBehaviour's own, or a class's public static ones (base classes included, constants not).</summary>
        public static List<Member> Members(Type type, bool statics)
        {
            var list = new List<Member>();
            if (type == null) return list;
            var flags = BindingFlags.Public | (statics ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);
            foreach (var f in type.GetFields(flags))
                if (!f.IsLiteral && !IsEngineMember(f) && f.Name.IndexOf('<') < 0) list.Add(new Member { Info = f });
            foreach (var p in type.GetProperties(flags))
                if (p.GetIndexParameters().Length == 0 && p.GetMethod != null && p.GetMethod.IsPublic && !IsEngineMember(p))
                    list.Add(new Member { Info = p, Backing = BackingField(p, statics) });
            return list;
        }

        private static FieldInfo BackingField(PropertyInfo p, bool statics) =>
            p.DeclaringType?.GetField("<" + p.Name + ">k__BackingField",
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly | (statics ? BindingFlags.Static : BindingFlags.Instance));

        /// <summary>Declared by VortexBehaviour (or object): the engine's members, not the game's state.</summary>
        private static bool IsEngineMember(MemberInfo m) =>
            m.DeclaringType == typeof(Vortex.VortexBehaviour) || m.DeclaringType == typeof(object);

        /// <summary>A member by name — "Credits", "credits" and "credits_left" ≈ "CreditsLeft" are the same — or null.</summary>
        public static Member Find(Type type, bool statics, string name)
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

        /// <summary>A member's value. Fields and auto-properties are read directly; a computed property's getter runs only
        /// with <paramref name="runGetter"/> (its value is <see cref="NotRun"/> otherwise) and never when it calls itself.
        /// A getter that throws becomes the text of its error.</summary>
        private static object Value(Member m, object owner, bool runGetter)
        {
            try
            {
                if (m.Info is FieldInfo f) return f.GetValue(owner);
                if (m.Backing != null) return m.Backing.GetValue(owner);
                if (!runGetter) return NotRun;
                var getter = ((PropertyInfo)m.Info).GetMethod;
                if (CallsItself(getter)) return new GetterError("the getter of " + m.Name + " calls itself — reading it would never end");
                return getter.Invoke(owner, null);
            }
            catch (TargetInvocationException ex) { return new GetterError(ex.InnerException ?? ex); }
            catch (Exception ex) { return new GetterError(ex); }
        }

        private static readonly object NotRun = new object();

        private sealed class GetterError
        {
            public readonly string Text;
            public GetterError(Exception ex) { Text = "<error: " + ex.GetType().Name + ": " + ex.Message + ">"; }
            public GetterError(string text) { Text = "<error: " + text + ">"; }
        }

        // ================================================================== a getter that calls itself

        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => o.Value);

        /// <summary>Does the method call itself? <c>get { return Speed; }</c> instead of <c>_speed</c> — the game never calls
        /// the getter, but reading it would overflow the stack, which no catch survives. (Recursion through another method or
        /// a loop that never ends are not detected; the getters of members named in a path are the caller's choice.)</summary>
        internal static bool CallsItself(MethodInfo m)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { return false; }
            if (il == null) return false;
            for (int i = 0; i < il.Length;)
            {
                short code = il[i++];
                if (code == 0xFE && i < il.Length) code = (short)(0xFE00 | il[i++]);
                if (!OpCodesByValue.TryGetValue(code, out var op)) return false;   // not IL we can walk: no verdict
                int size = op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => i + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, i) : 4,
                    _ => 4,
                };
                if ((op == OpCodes.Call || op == OpCodes.Callvirt) && i + 4 <= il.Length)
                {
                    int token = BitConverter.ToInt32(il, i);
                    if (token == m.MetadataToken) return true;
                    try { if (m.Module.ResolveMethod(token) == m) return true; } catch { }
                }
                i += size;
            }
            return false;
        }

        // ================================================================== reading

        /// <summary>What a read returns: the values, the computed properties that were not run, whether it was cut.</summary>
        public sealed class Snapshot
        {
            public Dictionary<string, object> State = new Dictionary<string, object>();
            /// <summary>Properties with a getter of their own, not run — name one in fields to read it.</summary>
            public List<string> Computed = new List<string>();
            /// <summary>The read reached <see cref="MaxValues"/> / <see cref="MaxChars"/>; the rest shows as "…".</summary>
            public bool Truncated;
        }

        /// <summary>One read's walk: the game's assembly (its types are expanded), what was shown already, the budget.</summary>
        private sealed class Walk
        {
            public Assembly Game;
            public int Values;
            public long Chars;
            public bool Truncated;
            public readonly HashSet<object> Shown = new HashSet<object>(ReferenceEqualityComparer.Instance);
            public readonly HashSet<object> Path = new HashSet<object>(ReferenceEqualityComparer.Instance);

            /// <summary>Count one value of about <paramref name="chars"/> characters; false once the budget is spent.</summary>
            public bool Spend(long chars)
            {
                if (Truncated) return false;
                Values++;
                Chars += chars + 4;
                if (Values > MaxValues || Chars > MaxChars) Truncated = true;
                return !Truncated;
            }
        }

        private const string Cut = "…";

        /// <summary>The state of <paramref name="instance"/> (or of <paramref name="type"/>'s statics): every field and
        /// auto-property (computed properties are listed, not run), or the members / dotted paths in <paramref name="fields"/>
        /// (whose getters do run), as JSON values expanded <paramref name="depth"/> levels deep.</summary>
        public static Snapshot Read(object instance, Type type, bool statics, IList<string> fields, int depth)
        {
            depth = Math.Clamp(depth, 0, MaxDepth);
            var walk = new Walk { Game = type?.Assembly };
            var snap = new Snapshot();
            if (!statics && instance != null) walk.Path.Add(instance);   // a member pointing back at its owner is a cycle
            if (fields == null || fields.Count == 0)
            {
                foreach (var m in Members(type, statics))
                {
                    if (m.Computed) { snap.Computed.Add(m.Name); continue; }
                    snap.State[m.Name] = Json(Value(m, statics ? null : instance, false), depth, walk);
                }
            }
            else
            {
                foreach (var path in fields)
                {
                    var steps = Resolve(instance, type, statics, path, forWrite: false, walk.Game);
                    var last = steps[steps.Count - 1];
                    snap.State[path] = Json(Value(last.Member, last.Owner, true), depth, walk);
                }
            }
            snap.Truncated = walk.Truncated;
            return snap;
        }

        /// <summary>A value as JSON, outside a read (an answer reading back what was set).</summary>
        public static object Json(object v, int depth) => Json(v, depth, new Walk { Game = v?.GetType().Assembly });

        private static object Json(object v, int depth, Walk walk)
        {
            if (walk.Truncated) return Cut;
            switch (v)
            {
                case null: walk.Spend(4); return null;
                case GetterError ge: walk.Spend(ge.Text.Length); return ge.Text;
                case string s: walk.Spend(Math.Min(s.Length, MaxText)); return s.Length > MaxText ? s.Substring(0, MaxText) + "…" : s;
                case bool b: walk.Spend(5); return b;
                case char c: walk.Spend(1); return c.ToString();
                case float f: walk.Spend(10); return float.IsFinite(f) ? (object)f : f.ToString(CultureInfo.InvariantCulture);
                case double d: walk.Spend(18); return double.IsFinite(d) ? (object)d : d.ToString(CultureInfo.InvariantCulture);
                case decimal m: walk.Spend(18); return m;
                case Enum e: { string n = e.ToString(); walk.Spend(n.Length); return n; }
                case Vortex.VortexBehaviour vb: { string n = Describe(vb); walk.Spend(n.Length); return n; }
                case Delegate del: walk.Spend(16); return "<delegate " + TypeName(del.GetType()) + ">";
                case Type t: walk.Spend(16); return "<type " + t.Name + ">";
            }
            var type = v.GetType();
            if (type.IsPrimitive) { walk.Spend(12); return v; }   // the integers (int, long, short, byte …)
            if (IsEngineVector(type))
            {
                walk.Spend(40);
                return EngineVectorFields(type).Select(f => (object)(float)f.GetValue(v)).Select(x => float.IsFinite((float)x) ? x : ((float)x).ToString(CultureInfo.InvariantCulture)).ToArray();
            }
            if (IsListable(type)) return List(v, type, depth, walk);
            // .NET's own value types (DateTime, TimeSpan, Guid …) as their text; any other type that is not the game's or an
            // engine struct is not opened — its getters are not game state, and a path never goes into it either
            if (type.Namespace != null && type.Namespace.StartsWith("System", StringComparison.Ordinal) && type.IsValueType)
            {
                string text = System.Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                walk.Spend(text.Length);
                return text;
            }
            walk.Spend(12);
            if (!CanEnter(type, walk.Game)) return "<" + TypeName(type) + ">";
            if (!type.IsValueType)
            {
                if (walk.Path.Contains(v)) return "<cycle: " + TypeName(type) + ">";
                if (walk.Shown.Contains(v)) return "<" + TypeName(type) + ", shown above>";
            }
            if (depth <= 0) return "<" + TypeName(type) + ">";
            if (!type.IsValueType)
            {
                walk.Shown.Add(v);
                walk.Path.Add(v);
            }
            var obj = new Dictionary<string, object>();
            foreach (var m in Members(type, statics: false))
            {
                if (m.Computed) continue;   // a nested object's getters are not run either
                if (walk.Truncated) { obj["…"] = "truncated"; break; }
                obj[m.Name] = Json(Value(m, v, false), depth - 1, walk);
            }
            if (!type.IsValueType) walk.Path.Remove(v);
            return obj;
        }

        /// <summary>Arrays and the BCL's collections (List, Dictionary, HashSet, Queue …) — their enumerators are not game
        /// code. A lazy sequence (a LINQ query, a generator) is not run.</summary>
        private static bool IsListable(Type t) =>
            t.IsArray || (t.Namespace != null && t.Namespace.StartsWith("System.Collections", StringComparison.Ordinal) && typeof(IEnumerable).IsAssignableFrom(t));

        private static object List(object v, Type type, int depth, Walk walk)
        {
            walk.Spend(8);
            int count = v is ICollection col ? col.Count : -1;
            if (v is IDictionary dict)
            {
                if (depth <= 0 && !AllScalar(dict.Values)) return "<" + TypeName(type) + " (" + dict.Count + " entries)>";
                var o = new Dictionary<string, object>();
                int n = 0;
                foreach (DictionaryEntry kv in dict)
                {
                    if (n++ == MaxItems) { o["…"] = (dict.Count - MaxItems) + " more"; break; }
                    if (walk.Truncated) { o["…"] = "truncated"; break; }
                    o[KeyText(kv.Key)] = Json(kv.Value, depth - 1, walk);
                }
                return o;
            }
            var items = new List<object>();
            bool more = false;
            foreach (var item in (IEnumerable)v)
            {
                if (items.Count == MaxItems) { more = true; break; }
                items.Add(item);
            }
            if (depth <= 0 && !AllScalar(items)) return "<" + TypeName(type) + (count >= 0 ? " (" + count + " items)" : "") + ">";
            var list = new List<object>();
            foreach (var x in items)
            {
                if (walk.Truncated) { list.Add(Cut); break; }
                list.Add(Json(x, depth - 1, walk));
            }
            if (more) list.Add("… " + (count >= 0 ? (count - MaxItems) + " more" : "more"));
            return list;
        }

        /// <summary>A dictionary key as text: scalars as they are, anything else by type (its ToString would be game code).</summary>
        private static string KeyText(object k) =>
            k == null ? "null" : IsScalar(k) ? System.Convert.ToString(k, CultureInfo.InvariantCulture) : "<" + TypeName(k.GetType()) + " " + RuntimeHelpersHash(k) + ">";

        private static int RuntimeHelpersHash(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);

        private static bool IsScalar(object v) =>
            v == null || v is string || v is bool || v is char || v is Enum || v is decimal || v.GetType().IsPrimitive || IsEngineVector(v.GetType());

        private static bool AllScalar(IEnumerable items)
        {
            foreach (var x in items) if (!IsScalar(x)) return false;
            return true;
        }

        /// <summary>The engine's float vectors — Vector3, Quaternion, Color: a struct of the scripting API whose fields are all
        /// floats — shown and set as an array ([x, y, z], [x, y, z, w], [r, g, b, a]).</summary>
        private static bool IsEngineVector(Type t)
        {
            if (!IsEngineStruct(t)) return false;
            var fields = EngineVectorFields(t);
            return fields.Length >= 2 && fields.Length <= 4 && fields.All(f => f.FieldType == typeof(float));
        }

        private static FieldInfo[] EngineVectorFields(Type t) => t.GetFields(BindingFlags.Public | BindingFlags.Instance);

        /// <summary>A struct of the scripting API (Vector3, Color, Quaternion, RaycastHit, the handles …).</summary>
        private static bool IsEngineStruct(Type t) =>
            t.IsValueType && !t.IsPrimitive && !t.IsEnum && t.Assembly == typeof(Vortex.Vector3).Assembly && t.Namespace == "Vortex";

        /// <summary>A path goes into the game's own classes and structs and the engine's structs — nothing else: .NET's and
        /// the engine's other objects have public setters that are no game state (a FileInfo's IsReadOnly writes the disk).</summary>
        private static bool CanEnter(Type t, Assembly game) => t != null && ((game != null && t.Assembly == game) || IsEngineStruct(t));

        /// <summary>"Health on 'Npc'" — a behaviour another one points to (it is not expanded: it has its own entity).</summary>
        private static string Describe(Vortex.VortexBehaviour b)
        {
            string entity = "";
            try { entity = Editor.Scripting.ScriptRuntime.Instance.EntityNameOf(b.EntityId); } catch { }
            return b.GetType().Name + (b.Destroyed ? " (destroyed)" : string.IsNullOrEmpty(entity) ? "" : " on '" + entity + "'");
        }

        private static string TypeName(Type t)
        {
            if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
            if (!t.IsGenericType) return t.Name;
            string n = t.Name.Substring(0, Math.Max(0, t.Name.IndexOf('`')));
            return n + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
        }

        // ================================================================== paths

        /// <summary>One step of a dotted path: the object that holds the member (a boxed copy for a struct), and the member.</summary>
        private sealed class Step { public object Owner; public Member Member; }

        /// <summary>"Instance.Credits": every step's owner and member. A step before the last runs its getter when it has one
        /// (it was named) and must lead to an object the game owns — the game's own classes and structs, or an engine struct.
        /// Writing needs a field or a property with a public setter that does not call itself.</summary>
        private static List<Step> Resolve(object instance, Type type, bool statics, string path, bool forWrite, Assembly game)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new Error("An empty member name.");
            var parts = path.Split('.');
            var steps = new List<Step>();
            object owner = statics ? null : instance;
            Type t = type;
            bool st = statics;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                string sofar = string.Join(".", parts.Take(i + 1));
                string parent = i == 0 ? null : string.Join(".", parts.Take(i));
                var m = Find(t, st, part) ?? throw new Error((i == 0 ? (st ? "The class " + t.Name + " has no public static member '" : t.Name + " has no public field or property '")
                                                                     : parent + " (" + TypeName(t) + ") has no public member '") + part + "'. Members: " + Names(t, st) + ".");
                steps.Add(new Step { Owner = st ? null : owner, Member = m });
                if (i == parts.Length - 1)
                {
                    if (forWrite) CheckWritable(m, path);
                    return steps;
                }
                // a struct on the way is a copy: writing inside it means writing it back to this member
                if (forWrite && m.Type.IsValueType) CheckWritable(m, sofar);
                var next = Value(m, st ? null : owner, true);
                if (next is GetterError ge) throw new Error(sofar + " could not be read: " + ge.Text);
                if (next == null) throw new Error(sofar + " is null — there is nothing to read or set inside it.");
                var nt = next.GetType();
                if (!CanEnter(nt, game))
                    throw new Error(sofar + " is a " + TypeName(nt) + " — a path only goes into the game's own classes and structs and the engine's structs (Vector3, Color, Quaternion …), not into " +
                                    (IsListable(nt) ? "a collection; read or set " + sofar + " as a whole" : ".NET's or the engine's other objects") + ".");
                owner = next;
                t = nt;
                st = false;
            }
            throw new Error("An empty member name.");
        }

        private static void CheckWritable(Member m, string path)
        {
            if (m.Info is FieldInfo f)
            {
                if (f.IsInitOnly) throw new Error(path + " is readonly.");
                return;
            }
            var p = (PropertyInfo)m.Info;
            var set = p.SetMethod;
            if (set == null || !set.IsPublic) throw new Error(path + " has no public setter.");
            if (set.ReturnParameter.GetRequiredCustomModifiers().Any(x => x.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
                throw new Error(path + " can only be set when the object is created (init).");
            if (CallsItself(set)) throw new Error("The setter of " + path + " calls itself — setting it would never end.");
        }

        // ================================================================== writing

        /// <summary>Set the members / dotted paths of <paramref name="values"/> (a JSON object) — every one is resolved and
        /// converted first, so a call with one bad value sets none. A member inside a struct ("Tint.R") is written back to the
        /// member that holds the struct. Returns the values read back after setting.</summary>
        public static Dictionary<string, object> Write(object instance, Type type, bool statics, JsonElement values)
        {
            if (values.ValueKind != JsonValueKind.Object) throw new Error("values must be an object {\"Member\": value, \"Instance.Credits\": 5000}.");
            var game = type?.Assembly;
            var entries = values.EnumerateObject().ToList();
            if (entries.Count == 0) throw new Error("values is empty — name at least one member.");
            // every path and value is checked first: one that fails sets nothing
            foreach (var p in entries) Prepare(instance, type, statics, p, game);
            // then each is set against the state as the ones before it left it — a whole struct and a path into the same struct
            // in one call compose in order
            var done = new List<string>();
            foreach (var p in entries)
            {
                try
                {
                    var (steps, value) = Prepare(instance, type, statics, p, game);
                    Set(steps[steps.Count - 1], value);
                    // a struct on the way holds a copy: write each one back to the member that holds it, outwards
                    for (int k = steps.Count - 2; k >= 0 && steps[k].Member.Type.IsValueType; k--) Set(steps[k], steps[k + 1].Owner);
                    done.Add(p.Name);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    throw new Error((inner is Error ? inner.Message : "Setting " + p.Name + " threw " + inner.GetType().Name + ": " + inner.Message) +
                                    (done.Count > 0 ? " (already set: " + string.Join(", ", done) + ")" : " (nothing was set)"));
                }
            }
            var result = new Dictionary<string, object>();
            var walk = new Walk { Game = game };
            foreach (var p in entries)
            {
                var steps = Resolve(instance, type, statics, p.Name, forWrite: false, game);
                var last = steps[steps.Count - 1];
                result[p.Name] = Json(Value(last.Member, last.Owner, true), 1, walk);
            }
            return result;
        }

        /// <summary>A path's steps and its value converted to the member's type (a struct from its current value).</summary>
        private static (List<Step> steps, object value) Prepare(object instance, Type type, bool statics, JsonProperty p, Assembly game)
        {
            var steps = Resolve(instance, type, statics, p.Name, forWrite: true, game);
            var last = steps[steps.Count - 1];
            object current = last.Member.Type.IsValueType ? Value(last.Member, last.Owner, true) : null;   // named: its getter may run
            return (steps, Convert(p.Value, last.Member.Type, p.Name, current is GetterError || current == NotRun ? null : current, game));
        }

        private static void Set(Step s, object value)
        {
            if (s.Member.Info is FieldInfo f) f.SetValue(s.Owner, value);
            else ((PropertyInfo)s.Member.Info).SetValue(s.Owner, value);
        }

        /// <summary>A JSON value as a member's type: strings, bools, every number type (whole numbers for the integer types,
        /// in range), enums by name or number ([Flags] combinations too: "Fast, Strong" or 3), the engine's float vectors as
        /// arrays ([x, y, z], [r, g, b, a]) or objects ({"r": 1}, starting from <paramref name="current"/>), the game's and the
        /// engine's other structs as objects, arrays and List&lt;T&gt; element-wise, null for references and nullables. An
        /// object of a class is not created from JSON: only null, or its members through a dotted path.</summary>
        public static object Convert(JsonElement v, Type t, string name, object current = null, Assembly game = null)
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
            if (t.IsEnum) return EnumValue(v, t, name);
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
            if (t.IsArray)
            {
                if (v.ValueKind != JsonValueKind.Array) throw new Error(name + " expects an array.");
                var et = t.GetElementType();
                var items = v.EnumerateArray().Select((x, i) => Convert(x, et, name + "[" + i + "]", null, game)).ToList();
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
                foreach (var x in v.EnumerateArray()) list.Add(Convert(x, et, name + "[" + i++ + "]", null, game));
                return list;
            }
            if (t.IsValueType && !t.IsPrimitive && (IsEngineStruct(t) || (game != null && t.Assembly == game)))
                return StructValue(v, t, name, current, game);
            if (!t.IsValueType && game != null && t.Assembly == game)
                throw new Error(name + " is a " + TypeName(t) + " (an object) — set its members with a dotted path (" + name + ".Member), or null.");
            throw new Error(name + " is a " + TypeName(t) + " — tools set the game's own values (numbers, text, enums, the engine's vectors and structs, arrays and lists of them), not this type.");
        }

        /// <summary>A struct from JSON: an engine vector from [x, y, z(, w)] (its fields in order), any struct from an object of
        /// its fields by name — starting from the current value, so {"a": 0.5} changes the alpha only.</summary>
        private static object StructValue(JsonElement v, Type t, string name, object current, Assembly game)
        {
            object box = current != null && current.GetType() == t ? current : Activator.CreateInstance(t);
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (v.ValueKind == JsonValueKind.Array)
            {
                if (!IsEngineVector(t)) throw new Error(name + " is a " + TypeName(t) + " — give an object of its fields {" + string.Join(", ", fields.Select(f => "\"" + f.Name + "\": …")) + "}.");
                int n = v.GetArrayLength();
                if (n < 2 || n > fields.Length) throw new Error(name + " expects [" + string.Join(", ", fields.Select(f => f.Name.ToLowerInvariant())) + "].");
                for (int i = 0; i < n; i++) fields[i].SetValue(box, (float)Number(v[i], name));
                return box;
            }
            if (v.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in v.EnumerateObject())
                {
                    var f = fields.FirstOrDefault(x => x.Name == p.Name) ?? fields.FirstOrDefault(x => Norm(x.Name) == Norm(p.Name))
                            ?? throw new Error(name + " (" + TypeName(t) + ") has no field '" + p.Name + "'. Fields: " + string.Join(", ", fields.Select(x => x.Name)) + ".");
                    f.SetValue(box, Convert(p.Value, f.FieldType, name + "." + f.Name, f.FieldType.IsValueType ? f.GetValue(box) : null, game));
                }
                return box;
            }
            throw new Error(name + " is a " + TypeName(t) + " — give " + (IsEngineVector(t) ? "[" + string.Join(", ", fields.Select(f => f.Name.ToLowerInvariant())) + "] or " : "") +
                            "an object of its fields {" + string.Join(", ", fields.Select(f => "\"" + f.Name + "\": …")) + "}.");
        }

        /// <summary>An enum by name (any case, snake_case too) or by number in range; a [Flags] enum takes combinations —
        /// "Fast, Strong", "Fast|Strong" or the number of its bits.</summary>
        private static object EnumValue(JsonElement v, Type t, string name)
        {
            string[] names = Enum.GetNames(t);
            bool flags = t.IsDefined(typeof(FlagsAttribute), false);
            ulong allBits = 0;
            foreach (var x in Enum.GetValues(t)) allBits |= ToBits(x);
            string must = name + " must be one of: " + string.Join(", ", names) + (flags ? " — or several: \"" + string.Join(", ", names.Where(n => ToBits(Enum.Parse(t, n)) != 0).Take(2)) + "\"" : "") + ".";
            if (v.ValueKind == JsonValueKind.String)
            {
                var parts = v.GetString().Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                if (parts.Count == 0 || (parts.Count > 1 && !flags)) throw new Error(must);
                ulong bits = 0;
                foreach (var part in parts)
                {
                    var hit = names.FirstOrDefault(n => Norm(n) == Norm(part)) ?? throw new Error(must);
                    bits |= ToBits(Enum.Parse(t, hit));
                }
                return Enum.ToObject(t, bits);
            }
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n))
            {
                object raw;
                try { raw = System.Convert.ChangeType(n, Enum.GetUnderlyingType(t), CultureInfo.InvariantCulture); }
                catch (OverflowException) { throw new Error(must); }
                var value = Enum.ToObject(t, raw);
                if (flags ? (ToBits(value) & ~allBits) == 0 : Enum.IsDefined(t, value)) return value;
            }
            throw new Error(must);
        }

        private static ulong ToBits(object enumValue)
        {
            switch (Type.GetTypeCode(Enum.GetUnderlyingType(enumValue.GetType())))
            {
                case TypeCode.Byte: case TypeCode.UInt16: case TypeCode.UInt32: case TypeCode.UInt64:
                    return System.Convert.ToUInt64(enumValue, CultureInfo.InvariantCulture);
                default:
                    return unchecked((ulong)System.Convert.ToInt64(enumValue, CultureInfo.InvariantCulture));
            }
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
