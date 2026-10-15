using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Editor.Core.UndoRedo
{
    /// <summary>
    /// Whether an undo step still refers to certain objects (#411: the steps of a scene that was read again from its file).
    /// Steps are opaque — a property change holds its target, a collection command its list and item, an ActionCommand two
    /// closures — so the step's object graph is walked: its fields, the elements of the collections it holds, the closures
    /// of its delegates. Only the engine's and the editor's own types are looked into (the Vortex assemblies, which hold the
    /// compiler-made closures too) besides collections, delegates and tuples: a window or a task a closure captured is not,
    /// so the walk stays small. A walk that runs out of its budget counts as a hit — dropping one step too many is safe,
    /// keeping one that acts on discarded objects is not.
    /// </summary>
    internal static class UndoReferences
    {
        /// <param name="step">The undo step (or any object) to look through.</param>
        /// <param name="classify">For each object met: true = a target (the step refers to it), false = no target and not
        /// looked into, null = look into it.</param>
        /// <param name="budget">Objects to visit at most before the answer is "yes".</param>
        public static bool Uses(object step, Func<object, bool?> classify, int budget = 100_000)
        {
            if (step == null || classify == null) return false;
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var todo = new Stack<object>();
            todo.Push(step);
            while (todo.Count > 0)
            {
                var o = todo.Pop();
                if (o == null || !seen.Add(o)) continue;
                if (--budget < 0) return true;
                bool? verdict = classify(o);
                if (verdict == true) return true;
                if (verdict == false) continue;
                var type = o.GetType();
                if (IsLeaf(type)) continue;
                if (o is Delegate d)
                {
                    foreach (var one in d.GetInvocationList()) if (one.Target != null) todo.Push(one.Target);
                    continue;
                }
                if (o is Array array)
                {
                    if (type.GetElementType() is Type et && (et.IsPrimitive || et.IsEnum)) continue;
                    foreach (var x in array) if (x != null) todo.Push(x);
                    continue;
                }
                // only collections that hold their elements: enumerating a lazy query would run it
                if (IsCollection(type))
                {
                    foreach (var x in (IEnumerable)o) if (x != null) todo.Push(x);
                    continue;
                }
                for (var t = type; t != null && LooksInto(t); t = t.BaseType)
                {
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        var ft = f.FieldType;
                        if (ft.IsPrimitive || ft.IsEnum || ft.IsPointer || ft == typeof(string)) continue;
                        object v;
                        try { v = f.GetValue(o); } catch { continue; }
                        if (v != null) todo.Push(v);
                    }
                }
            }
            return false;
        }

        private static bool IsLeaf(Type t)
            => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || typeof(MemberInfo).IsAssignableFrom(t) || typeof(Assembly).IsAssignableFrom(t);

        private static readonly ConcurrentDictionary<Type, bool> _collections = new ConcurrentDictionary<Type, bool>();
        private static readonly ConcurrentDictionary<Type, bool> _lookInto = new ConcurrentDictionary<Type, bool>();

        /// <summary>A collection that holds its elements — never a LINQ query, which some collection interfaces cover too.</summary>
        private static bool IsCollection(Type t) => _collections.GetOrAdd(t, type =>
        {
            if (type.Assembly == typeof(Enumerable).Assembly) return false;
            if (typeof(ICollection).IsAssignableFrom(type)) return true;
            foreach (var i in type.GetInterfaces())
            {
                if (!i.IsGenericType) continue;
                var g = i.GetGenericTypeDefinition();
                if (g == typeof(ICollection<>) || g == typeof(IReadOnlyCollection<>)) return true;
            }
            return false;
        });

        /// <summary>Types whose fields are walked: the engine's and the editor's own (and their closures), tuples, pairs.</summary>
        private static bool LooksInto(Type t) => _lookInto.GetOrAdd(t, LooksIntoUncached);

        private static bool LooksIntoUncached(Type t)
        {
            if (t.IsGenericType)
            {
                var g = t.GetGenericTypeDefinition();
                if (g == typeof(KeyValuePair<,>)) return true;
                string n = g.FullName ?? "";
                if (n.StartsWith("System.ValueTuple`", StringComparison.Ordinal) || n.StartsWith("System.Tuple`", StringComparison.Ordinal)) return true;
            }
            string asm = t.Assembly.GetName().Name ?? "";
            return asm.StartsWith("Vortex", StringComparison.Ordinal);
        }
    }
}
