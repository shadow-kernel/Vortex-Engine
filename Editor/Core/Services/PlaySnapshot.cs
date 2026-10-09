using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Editor.Core.Data;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Play is non-destructive: everything scripts (or the Claude tools, #345) change while playing is put back on
    /// Stop. The snapshot is generic (#318): for every entity it keeps its place in the hierarchy, its serialized
    /// properties (active, name, tag, layer, …), the set of components it had, and every [DataMember] property of
    /// every component — an AudioSource's volume, a renderer's or collider's enabled flag, a render layer, a material
    /// path, an IK weight, a light's flicker — whatever the scene file would have saved. Entities the runtime spawned
    /// are rolled back by the ScriptRuntime itself; entities destroyed during play come back here.
    /// </summary>
    public sealed class PlaySnapshot
    {
        private readonly List<Action> _restore = new List<Action>();
        private static readonly Dictionary<Type, PropertyInfo[]> _props = new Dictionary<Type, PropertyInfo[]>();
        private static readonly object _propsLock = new object();

        /// <summary>Remember the scene's state (call before scripts start).</summary>
        public static PlaySnapshot Take(Scene scene)
        {
            var snap = new PlaySnapshot();
            if (scene?.Entities != null)
            {
                var roots = new List<GameEntity>(scene.Entities);
                for (int i = 0; i < roots.Count; i++) snap.Walk(roots[i], scene.Entities, i);
            }
            return snap;
        }

        /// <summary>Put everything back (call after the scripts' OnDestroy ran). Values that did not change are
        /// not written, so nothing is re-sent needlessly.</summary>
        public void Restore()
        {
            foreach (var r in _restore)
            {
                try { r(); } catch { /* a component torn down with its entity */ }
            }
            _restore.Clear();
        }

        private void Walk(GameEntity e, IList<GameEntity> siblings, int index)
        {
            if (e == null) return;
            var entityProps = Capture(e, typeof(GameEntity));
            var authored = new List<Component>(e.Components);
            var componentProps = new List<Action>(authored.Count);
            foreach (var c in authored) componentProps.Add(c == null ? null : Capture(c, c.GetType()));
            bool wasActive = e.IsActive;

            _restore.Add(() =>
            {
                // destroyed during play: back to where it was
                if (!siblings.Contains(e)) siblings.Insert(Math.Min(Math.Max(index, 0), siblings.Count), e);
                // components added during play go, removed ones come back in their authored slot
                for (int i = e.Components.Count - 1; i >= 0; i--)
                    if (!authored.Contains(e.Components[i])) e.Components.RemoveAt(i);
                for (int i = 0; i < authored.Count; i++)
                {
                    var c = authored[i];
                    if (c == null || e.Components.Contains(c)) continue;
                    c.Entity = e;
                    e.Components.Insert(Math.Min(i, e.Components.Count), c);
                }
                entityProps();
                foreach (var r in componentProps) r?.Invoke();
                if (e.IsActive != wasActive) e.IsActive = wasActive;
            });

            if (e.Children != null)
            {
                var children = new List<GameEntity>(e.Children);
                for (int i = 0; i < children.Count; i++) Walk(children[i], e.Children, i);
            }
        }

        /// <summary>An action that writes the object's serialized properties back to what they are now.</summary>
        private static Action Capture(object target, Type type)
        {
            var props = Props(type);
            var values = new object[props.Length];
            for (int i = 0; i < props.Length; i++)
            {
                try { values[i] = Clone(props[i].GetValue(target)); } catch { values[i] = null; }
            }
            return () =>
            {
                for (int i = 0; i < props.Length; i++)
                {
                    object current;
                    try { current = props[i].GetValue(target); } catch { continue; }
                    if (Same(current, values[i])) continue;
                    try { props[i].SetValue(target, Clone(values[i])); } catch { }
                }
            };
        }

        /// <summary>The [DataMember] properties with a public getter and setter — what the scene file saves — minus
        /// the identity and the collections the hierarchy restore handles itself.</summary>
        internal static PropertyInfo[] Props(Type type)
        {
            lock (_propsLock)
            {
                if (_props.TryGetValue(type, out var cached)) return cached;
                var list = new List<PropertyInfo>();
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length != 0) continue;
                    if (p.GetSetMethod(false) == null || p.GetGetMethod(false) == null) continue;
                    if (p.GetCustomAttribute<DataMemberAttribute>() == null) continue;
                    if (p.Name == "Id") continue;
                    var t = p.PropertyType;
                    if (typeof(IEnumerable<GameEntity>).IsAssignableFrom(t) || typeof(IEnumerable<Component>).IsAssignableFrom(t)) continue;
                    list.Add(p);
                }
                var arr = list.ToArray();
                _props[type] = arr;
                return arr;
            }
        }

        /// <summary>Value types, strings and references as they are; arrays, lists and dictionaries as shallow copies,
        /// so a script that fills or clears a collection does not reach into the snapshot.</summary>
        private static object Clone(object v)
        {
            if (v == null) return null;
            if (v is string || v.GetType().IsValueType) return v;
            if (v is Array a) return a.Clone();
            if (v is IList list && !(v is Array))
            {
                try
                {
                    var copy = (IList)Activator.CreateInstance(v.GetType());
                    foreach (var item in list) copy.Add(item);
                    return copy;
                }
                catch { return v; }
            }
            if (v is IDictionary dict)
            {
                try
                {
                    var copy = (IDictionary)Activator.CreateInstance(v.GetType());
                    foreach (DictionaryEntry kv in dict) copy[kv.Key] = kv.Value;
                    return copy;
                }
                catch { return v; }
            }
            return v;
        }

        private static bool Same(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a is Array aa && b is Array ba)
            {
                if (aa.Length != ba.Length) return false;
                for (int i = 0; i < aa.Length; i++) if (!Equals(aa.GetValue(i), ba.GetValue(i))) return false;
                return true;
            }
            if (a is IList la && b is IList lb && !(a is string))
            {
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!Equals(la[i], lb[i])) return false;
                return true;
            }
            if (a is IDictionary da && b is IDictionary db)
            {
                if (da.Count != db.Count) return false;
                foreach (DictionaryEntry kv in da) { if (!db.Contains(kv.Key) || !Equals(db[kv.Key], kv.Value)) return false; }
                return true;
            }
            return Equals(a, b);
        }
    }
}
