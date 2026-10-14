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
    /// path, an IK weight, a light's flicker, a script's field values — whatever the scene file would have saved, as
    /// deep copies (#478). The hierarchy comes back exactly (#423): an entity added while playing goes again, wherever
    /// it was put and whoever added it (a script, the hierarchy, the Claude tools); a re-parented one returns to its old
    /// parent and slot; a destroyed one comes back.
    /// </summary>
    public sealed class PlaySnapshot
    {
        private const int MaxDepth = 8;   // nesting a deep copy / comparison follows (a guard against cycles)

        private Scene _scene;
        private readonly List<Action> _restore = new List<Action>();   // per entity: its components and properties
        private static readonly Dictionary<Type, PropertyInfo[]> _props = new Dictionary<Type, PropertyInfo[]>();
        private static readonly Dictionary<Type, Member[]> _plain = new Dictionary<Type, Member[]>();
        private static readonly object _propsLock = new object();

        // The hierarchy at Take (#423): every list — the scene's roots and each entity's children — with its members in
        // order, and each entity's parent and scene.
        private sealed class Slot { public GameEntity Owner; public GameEntity[] Members; }
        private sealed class Home { public GameEntity Parent; public Scene Scene; }
        private readonly List<Slot> _slots = new List<Slot>();
        private readonly Dictionary<GameEntity, Home> _homes = new Dictionary<GameEntity, Home>(ReferenceEqualityComparer.Instance);
        private readonly List<GameEntity> _removed = new List<GameEntity>();

        /// <summary>The entities the last <see cref="Restore"/> removed because they were added while playing (the top of
        /// each removed subtree) — e.g. so the editor can drop a selection that points into one.</summary>
        public IReadOnlyList<GameEntity> Removed => _removed;

        /// <summary>Remember the scene's state (call before scripts start).</summary>
        public static PlaySnapshot Take(Scene scene)
        {
            var snap = new PlaySnapshot { _scene = scene };
            if (scene?.Entities != null)
            {
                snap.TakeList(null, scene.Entities);
            }
            return snap;
        }

        /// <summary>Put everything back (call after the scripts' OnDestroy ran). Values that did not change are
        /// not written, so nothing is re-sent needlessly.</summary>
        public void Restore()
        {
            _removed.Clear();
            List<GameEntity> putBack = null;
            try { putBack = RestoreHierarchy(); } catch { /* a broken tree: the values below still come back */ }
            foreach (var r in _restore)
            {
                try { r(); } catch { /* a component torn down with its entity */ }
            }
            _restore.Clear();
            // an entity that had been destroyed or moved away is in the engine again — with its active flag already back
            if (putBack != null)
                foreach (var e in putBack)
                {
                    try { e.SyncEngineStateRecursive(e.Parent?.ActiveInHierarchy ?? (e.Scene?.IsActive ?? true)); } catch { }
                }
        }

        private void TakeList(GameEntity owner, IList<GameEntity> list)
        {
            var slot = new Slot { Owner = owner, Members = new GameEntity[list.Count] };
            list.CopyTo(slot.Members, 0);
            _slots.Add(slot);
            foreach (var e in slot.Members)
            {
                if (e == null || _homes.ContainsKey(e)) continue;
                _homes[e] = new Home { Parent = e.Parent, Scene = e.Scene };
                Walk(e);
                if (e.Children != null) TakeList(e, e.Children);
            }
        }

        private void Walk(GameEntity e)
        {
            var entityProps = Capture(e, typeof(GameEntity));
            var authored = new List<Component>(e.Components);
            var componentProps = new List<Action>(authored.Count);
            foreach (var c in authored) componentProps.Add(c == null ? null : Capture(c, c.GetType()));
            bool wasActive = e.IsActive;

            _restore.Add(() =>
            {
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
        }

        /// <summary>Every list of the hierarchy gets exactly the members it had at Take, in their order (#423). An entity
        /// that is in no list of the snapshot was added while playing and goes — wherever it was put; its engine and render
        /// resources are released. An authored entity found in another list (re-parented, or moved into another scene) is
        /// taken out there and put back into its own; a destroyed one is put back. Only what differs is touched, so an
        /// unchanged hierarchy sees no collection change. Returns the authored entities that had to be put back.</summary>
        private List<GameEntity> RestoreHierarchy()
        {
            var putBack = new List<GameEntity>();
            if (_scene?.Entities == null || _slots.Count == 0) return putBack;

            // 1) what does not belong where it is now
            var strays = new List<(IList<GameEntity> list, int index, GameEntity e)>();
            var removed = new HashSet<GameEntity>(ReferenceEqualityComparer.Instance);
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

            void Examine(IList<GameEntity> list, Slot slot, bool insideAdded)
            {
                if (list == null || !seen.Add(list)) return;
                if (slot != null && list.Count == slot.Members.Length)
                {
                    bool same = true;
                    for (int i = 0; i < list.Count && same; i++) same = ReferenceEquals(list[i], slot.Members[i]);
                    if (same) return;   // as at Take — the usual case
                }
                Dictionary<GameEntity, int> room = null;   // how often each entity belongs in this list
                if (slot != null)
                {
                    room = new Dictionary<GameEntity, int>(ReferenceEqualityComparer.Instance);
                    foreach (var m in slot.Members) if (m != null) room[m] = room.TryGetValue(m, out int n) ? n + 1 : 1;
                }
                var now = new GameEntity[list.Count];
                list.CopyTo(now, 0);
                for (int i = 0; i < now.Length; i++)
                {
                    var e = now[i];
                    if (e == null) continue;
                    bool authored = _homes.ContainsKey(e);
                    if (room != null && room.TryGetValue(e, out int left) && left > 0) room[e] = left - 1;   // its place
                    else if (authored) strays.Add((list, i, e));               // moved here while playing: back home below
                    else if (!insideAdded)                                     // added while playing: goes
                    {
                        strays.Add((list, i, e));
                        if (removed.Add(e)) _removed.Add(e);
                    }
                    // (an entity added inside an added one goes with it)
                    if (!authored) Examine(e.Children, null, true);   // an authored entity may have been put under it
                }
            }

            // an authored entity moved into another loaded scene: out of there again (that scene's own entities stay)
            void Foreign(IList<GameEntity> list)
            {
                if (list == null || !seen.Add(list)) return;
                var now = new GameEntity[list.Count];
                list.CopyTo(now, 0);
                for (int i = 0; i < now.Length; i++)
                {
                    var e = now[i];
                    if (e == null) continue;
                    if (_homes.ContainsKey(e)) strays.Add((list, i, e));
                    else Foreign(e.Children);
                }
            }

            foreach (var slot in _slots) Examine(slot.Owner == null ? _scene.Entities : slot.Owner.Children, slot, false);
            var foreignScenes = new HashSet<Scene>(ReferenceEqualityComparer.Instance);
            foreach (var kv in _homes)
            {
                var now = kv.Key.Scene;
                if (now != null && !ReferenceEquals(now, kv.Value.Scene) && !ReferenceEquals(now, _scene) && foreignScenes.Add(now)) Foreign(now.Entities);
            }

            // 2) out with them — the highest index of a list first, so the other recorded indices stay right
            strays.Sort((a, b) => b.index.CompareTo(a.index));
            foreach (var s in strays)
            {
                if (s.index < s.list.Count && ReferenceEquals(s.list[s.index], s.e)) s.list.RemoveAt(s.index);
                else s.list.Remove(s.e);
            }

            // 3) every list of the snapshot back to its members in their order
            foreach (var slot in _slots)
            {
                var list = slot.Owner == null ? _scene.Entities : slot.Owner.Children;
                if (list == null) continue;
                var want = slot.Members;
                for (int i = 0; i < want.Length; i++)
                {
                    var e = want[i];
                    if (i < list.Count && ReferenceEquals(list[i], e)) continue;
                    int at = -1;
                    for (int k = i + 1; k < list.Count; k++) if (ReferenceEquals(list[k], e)) { at = k; break; }
                    if (at >= 0) list.Move(at, i);
                    else
                    {
                        list.Insert(Math.Min(i, list.Count), e);
                        if (e != null) putBack.Add(e);
                    }
                }
                while (list.Count > want.Length) list.RemoveAt(list.Count - 1);   // nothing but a stray null can be left here
            }
            foreach (var kv in _homes)
            {
                if (!ReferenceEquals(kv.Key.Parent, kv.Value.Parent)) kv.Key.Parent = kv.Value.Parent;
                if (!ReferenceEquals(kv.Key.Scene, kv.Value.Scene)) kv.Key.Scene = kv.Value.Scene;
            }

            // 4) what was added while playing leaves the engine and the renderer too
            foreach (var e in _removed)
            {
                try { e.SyncEngineStateRecursive(false); } catch { }
                try { SceneRenderService.Instance.RemoveEntityTree(e); } catch { }
            }
            if (_removed.Count > 0 || putBack.Count > 0 || strays.Count > 0)
                SceneRenderService.RuntimeDirty = SceneRenderService.StaticDirty = true;   // structural (#364 A)
            return putBack;
        }

        /// <summary>An action that writes the object's serialized properties back to what they are now — deep copies, so
        /// a later in-place edit of a value cannot reach the snapshot (#478).</summary>
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

        private sealed class Member { public Func<object, object> Get; public Action<object, object> Set; }

        /// <summary>The [DataMember] properties and fields of a plain [DataContract] data class — a script's field value,
        /// an Animator clip slot, a physics material, a foliage type — or null for any other type (entities, components
        /// and scenes are references, never copies).</summary>
        private static Member[] PlainData(Type t)
        {
            lock (_propsLock)
            {
                if (_plain.TryGetValue(t, out var cached)) return cached;
                Member[] result = null;
                if (t.IsClass && !t.IsAbstract && t.GetCustomAttribute<DataContractAttribute>(false) != null
                    && !typeof(GameEntity).IsAssignableFrom(t) && !typeof(Component).IsAssignableFrom(t) && !typeof(Scene).IsAssignableFrom(t))
                {
                    var list = new List<Member>();
                    const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    for (var x = t; x != null && x != typeof(object); x = x.BaseType)
                    {
                        foreach (var p in x.GetProperties(declared))
                        {
                            if (p.GetCustomAttribute<DataMemberAttribute>() == null || !p.CanRead || !p.CanWrite || p.GetIndexParameters().Length != 0) continue;
                            var prop = p;
                            list.Add(new Member { Get = o => prop.GetValue(o), Set = (o, v) => prop.SetValue(o, v) });
                        }
                        foreach (var f in x.GetFields(declared))
                        {
                            if (f.GetCustomAttribute<DataMemberAttribute>() == null || f.IsInitOnly) continue;
                            var field = f;
                            list.Add(new Member { Get = o => field.GetValue(o), Set = (o, v) => field.SetValue(o, v) });
                        }
                    }
                    result = list.ToArray();
                }
                _plain[t] = result;
                return result;
            }
        }

        /// <summary>A copy the live value cannot reach into (#478): value types and strings as they are; arrays, lists and
        /// dictionaries element by element; a plain data object member by member. A script field edited in place
        /// (Script.SetFieldValue changes the existing entry) used to change the snapshot's shallow copy as well, so Stop
        /// saw nothing to restore.</summary>
        private static object Clone(object v, int depth = 0)
        {
            if (v == null || v is string) return v;
            var t = v.GetType();
            if (t.IsValueType || depth > MaxDepth) return v;
            if (v is Array a)
            {
                var copy = (Array)a.Clone();
                var element = t.GetElementType();
                if (!element.IsValueType && element != typeof(string) && a.Rank == 1 && a.GetLowerBound(0) == 0)
                    for (int i = 0; i < copy.Length; i++) copy.SetValue(Clone(a.GetValue(i), depth + 1), i);
                return copy;
            }
            if (v is IList list)
            {
                try
                {
                    var copy = (IList)Activator.CreateInstance(t);
                    foreach (var item in list) copy.Add(Clone(item, depth + 1));
                    return copy;
                }
                catch { return v; }
            }
            if (v is IDictionary dict)
            {
                try
                {
                    var copy = (IDictionary)Activator.CreateInstance(t);
                    foreach (DictionaryEntry kv in dict) copy[kv.Key] = Clone(kv.Value, depth + 1);
                    return copy;
                }
                catch { return v; }
            }
            var members = PlainData(t);
            if (members != null)
            {
                try
                {
                    var copy = Activator.CreateInstance(t, nonPublic: true);
                    foreach (var m in members) m.Set(copy, Clone(m.Get(v), depth + 1));
                    return copy;
                }
                catch { return v; }
            }
            return v;
        }

        /// <summary>Equal by value — through arrays, lists, dictionaries and plain data objects, so an element changed in
        /// place is a change (#478).</summary>
        private static bool Same(object a, object b, int depth = 0)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a is string || a.GetType().IsValueType || depth > MaxDepth) return Equals(a, b);
            if (a.GetType() != b.GetType()) return false;
            if (a is Array aa)
            {
                var ba = (Array)b;
                if (aa.Length != ba.Length || aa.Rank != ba.Rank) return false;
                for (int d = 0; d < aa.Rank; d++) if (aa.GetLength(d) != ba.GetLength(d)) return false;
                var ea = aa.GetEnumerator();
                var eb = ba.GetEnumerator();
                while (ea.MoveNext()) { if (!eb.MoveNext() || !Same(ea.Current, eb.Current, depth + 1)) return false; }
                return true;
            }
            if (a is IList la)
            {
                var lb = (IList)b;
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!Same(la[i], lb[i], depth + 1)) return false;
                return true;
            }
            if (a is IDictionary da)
            {
                var db = (IDictionary)b;
                if (da.Count != db.Count) return false;
                foreach (DictionaryEntry kv in da) { if (!db.Contains(kv.Key) || !Same(db[kv.Key], kv.Value, depth + 1)) return false; }
                return true;
            }
            var members = PlainData(a.GetType());
            if (members != null)
            {
                foreach (var m in members) if (!Same(m.Get(a), m.Get(b), depth + 1)) return false;
                return true;
            }
            return Equals(a, b);
        }
    }
}
