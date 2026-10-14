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
    /// path, an IK weight, a light's flicker — whatever the scene file would have saved. The hierarchy comes back
    /// exactly (#423): an entity added while playing goes again, wherever it was put and whoever added it (a script,
    /// the hierarchy, the Claude tools); a re-parented one returns to its old parent and slot; a destroyed one comes back.
    /// </summary>
    public sealed class PlaySnapshot
    {
        private Scene _scene;
        private readonly List<Action> _restore = new List<Action>();   // per entity: its components and properties
        private static readonly Dictionary<Type, PropertyInfo[]> _props = new Dictionary<Type, PropertyInfo[]>();
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
