using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Editor.Core.UndoRedo
{
    /// <summary>What the undo walk does with an object it meets (<see cref="UndoReferences"/>).</summary>
    internal enum UndoRef
    {
        /// <summary>Look into its fields, or a collection's elements.</summary>
        LookInto,
        /// <summary>A target: the step refers to it.</summary>
        Target,
        /// <summary>No target, and not looked into.</summary>
        Skip,
        /// <summary>No target and not looked into — it belongs somewhere else (another scene). A step whose walk runs out of
        /// budget after meeting such an object is that place's step, not counted as a hit.</summary>
        Elsewhere,
    }

    /// <summary>
    /// Which objects an undo step refers to (#411: the steps of a scene that was read again from its file, and the scenes an
    /// executed step marks unsaved). Steps are opaque — a property change holds its target, a collection command its list and
    /// item, an ActionCommand two closures — so the step's object graph is walked: its fields, the elements of the collections it
    /// holds, the closures of its delegates. Only the engine's and the editor's own types are looked into (the Vortex assemblies,
    /// which hold the compiler-made closures too) besides collections, delegates and tuples: a window or a task a closure captured
    /// is not, and neither are collections of plain values (foliage instances, heights), so the walk stays small.
    /// </summary>
    internal static class UndoReferences
    {
        /// <summary>The outcome of one walk.</summary>
        internal struct Result
        {
            /// <summary>A target was found (the walk stopped there).</summary>
            public bool Hit;
            /// <summary>An object classified <see cref="UndoRef.Elsewhere"/> was met.</summary>
            public bool Elsewhere;
            /// <summary>The walk stopped at its budget before seeing everything.</summary>
            public bool OutOfBudget;
        }

        /// <summary>Whether <paramref name="step"/> refers to a target. A walk out of budget counts as a hit — dropping a step too
        /// many is safe, keeping one that acts on discarded objects is not — unless it met objects of another place: then it is
        /// theirs.</summary>
        public static bool Uses(object step, Func<object, UndoRef> classify, int budget = 100_000)
        {
            var r = Walk(step, classify, budget);
            return r.Hit || (r.OutOfBudget && !r.Elsewhere);
        }

        /// <param name="root">The undo step (or any object) to look through.</param>
        /// <param name="classify">What to do with each object met.</param>
        /// <param name="budget">Objects to visit at most.</param>
        public static Result Walk(object root, Func<object, UndoRef> classify, int budget = 100_000)
        {
            var result = new Result();
            if (root == null || classify == null) return result;
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            // breadth first: what the step holds itself (its entity, its scene) is met before the contents of a big list beside it
            var todo = new Queue<object>();
            todo.Enqueue(root);
            while (todo.Count > 0)
            {
                var o = todo.Dequeue();
                if (o == null || !seen.Add(o)) continue;
                if (--budget < 0) { result.OutOfBudget = true; return result; }
                switch (classify(o))
                {
                    case UndoRef.Target: result.Hit = true; return result;
                    case UndoRef.Skip: continue;
                    case UndoRef.Elsewhere: result.Elsewhere = true; continue;
                }
                var type = o.GetType();
                if (IsLeaf(type)) continue;
                if (o is Delegate d)
                {
                    foreach (var one in d.GetInvocationList()) if (one.Target != null) todo.Enqueue(one.Target);
                    continue;
                }
                if (o is Array array)
                {
                    if (IsPlain(type.GetElementType())) continue;
                    foreach (var x in array)
                    {
                        if (todo.Count > budget) { result.OutOfBudget = true; break; }   // more than the walk can ever visit
                        if (x != null) todo.Enqueue(x);
                    }
                    continue;
                }
                // only collections that hold their elements (enumerating a lazy query would run it), and not those of plain values
                if (IsCollection(type, out var element))
                {
                    if (element != null && IsPlain(element)) continue;
                    foreach (var x in (IEnumerable)o)
                    {
                        if (todo.Count > budget) { result.OutOfBudget = true; break; }
                        if (x != null) todo.Enqueue(x);
                    }
                    continue;
                }
                for (var t = type; t != null && LooksInto(t); t = t.BaseType)
                {
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (IsPlain(f.FieldType)) continue;
                        object v;
                        try { v = f.GetValue(o); } catch { continue; }
                        if (v != null) todo.Enqueue(v);
                    }
                }
            }
            return result;
        }

        private static bool IsLeaf(Type t)
            => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || typeof(MemberInfo).IsAssignableFrom(t) || typeof(Assembly).IsAssignableFrom(t);

        private static readonly ConcurrentDictionary<Type, bool> _plain = new ConcurrentDictionary<Type, bool>();
        private static readonly ConcurrentDictionary<Type, (bool, Type)> _collections = new ConcurrentDictionary<Type, (bool, Type)>();
        private static readonly ConcurrentDictionary<Type, bool> _lookInto = new ConcurrentDictionary<Type, bool>();

        /// <summary>A value that holds no object: a primitive, an enum, a string, a struct of such (a foliage instance, a vector).</summary>
        private static bool IsPlain(Type t) => t == null || _plain.GetOrAdd(t, PlainUncached);

        // a struct cannot hold itself by value, so the recursion over value-type fields ends
        private static bool PlainUncached(Type t)
        {
            if (t.IsPrimitive || t.IsEnum || t.IsPointer || t == typeof(string) || t == typeof(decimal)) return true;
            if (!t.IsValueType || t.IsGenericTypeDefinition) return false;
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (!IsPlain(f.FieldType)) return false;
            return true;
        }

        /// <summary>A collection that holds its elements — never a LINQ query, which some collection interfaces cover too — and the
        /// type of its elements when it says.</summary>
        private static bool IsCollection(Type t, out Type element)
        {
            var (yes, el) = _collections.GetOrAdd(t, type =>
            {
                if (type.Assembly == typeof(Enumerable).Assembly) return (false, null);
                bool any = typeof(ICollection).IsAssignableFrom(type);
                Type found = null;
                foreach (var i in type.GetInterfaces())
                {
                    if (!i.IsGenericType) continue;
                    var g = i.GetGenericTypeDefinition();
                    if (g == typeof(ICollection<>) || g == typeof(IReadOnlyCollection<>)) { any = true; found = i.GetGenericArguments()[0]; }
                }
                return (any, found);
            });
            element = el;
            return yes;
        }

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
