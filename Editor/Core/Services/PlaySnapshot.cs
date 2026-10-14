using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Serialization;
using Editor.Core.Data;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Play is non-destructive: everything scripts (or the Claude tools, #345) change while playing is put back on
    /// Stop. The snapshot is generic (#318): for every entity of every loaded scene it keeps its place in the hierarchy,
    /// its serialized properties (active, name, tag, layer, …), the set of components it had, and every [DataMember]
    /// property of every component — an AudioSource's volume, a renderer's or collider's enabled flag, a render layer, a
    /// material path, an IK weight, a light's flicker, a script's field values — whatever the scene file would have saved.
    /// Values go back into the very objects that held them, contents included, so the viewport and an open inspector see
    /// the restored values (#478). The hierarchy comes back exactly (#423): an entity added while playing goes again,
    /// wherever it was put and whoever added it (a script, the hierarchy, the Claude tools); a re-parented one returns to
    /// its old parent and slot; a destroyed one comes back. So do the scenes' environment settings (#479).
    /// </summary>
    public sealed class PlaySnapshot
    {
        private const int MaxDepth = 8;   // nesting the remembered contents follow (a guard against cycles)

        private readonly HashSet<Scene> _scenes = new HashSet<Scene>(ReferenceEqualityComparer.Instance);
        private readonly List<Action> _restore = new List<Action>();   // per entity: its components and properties
        private static readonly Dictionary<Type, PropertyInfo[]> _props = new Dictionary<Type, PropertyInfo[]>();
        private static readonly Dictionary<Type, Member[]> _plain = new Dictionary<Type, Member[]>();
        private static readonly object _propsLock = new object();

        // The hierarchy at Take (#423): every list — each scene's roots and each entity's children — with its members in
        // order, and each entity's parent and scene.
        private sealed class Slot
        {
            public Scene Scene;
            public GameEntity Owner;   // null: the scene's roots
            public GameEntity[] Members;
            public ObservableCollection<GameEntity> List => Owner == null ? Scene?.Entities : Owner.Children;
        }
        private sealed class Home { public GameEntity Parent; public Scene Scene; }
        private readonly List<Slot> _slots = new List<Slot>();
        private readonly Dictionary<GameEntity, Home> _homes = new Dictionary<GameEntity, Home>(ReferenceEqualityComparer.Instance);
        private readonly List<(Scene scene, SceneSettings settings, Func<bool> back)> _settings = new List<(Scene, SceneSettings, Func<bool>)>();

        private readonly List<GameEntity> _removed = new List<GameEntity>();
        private readonly List<Scene> _settingsReverted = new List<Scene>();

        /// <summary>The entities the last <see cref="Restore"/> removed because they were added while playing — the top of each
        /// removed subtree. They are out of every scene: Parent and Scene are cleared.</summary>
        public IReadOnlyList<GameEntity> Removed => _removed;

        /// <summary>How many authored entities the last <see cref="Restore"/> had to put back (destroyed or moved while playing).</summary>
        public int PutBack { get; private set; }

        /// <summary>The scenes whose environment settings the last <see cref="Restore"/> changed back.</summary>
        public IReadOnlyList<Scene> SettingsReverted => _settingsReverted;

        /// <summary>Remember one scene's state (call before scripts start).</summary>
        public static PlaySnapshot Take(Scene scene) => Take(new[] { scene });

        /// <summary>Remember the state of every given scene — the editor passes every loaded one, so a scene edited while
        /// playing comes back whichever it is (#423).</summary>
        public static PlaySnapshot Take(IEnumerable<Scene> scenes)
        {
            var snap = new PlaySnapshot();
            if (scenes == null) return snap;
            foreach (var scene in scenes)
            {
                if (scene?.Entities == null || !snap._scenes.Add(scene)) continue;
                snap.TakeList(scene, null, scene.Entities);
                var settings = scene.Settings;
                snap._settings.Add((scene, settings, Capture(settings, typeof(SceneSettings))));
            }
            return snap;
        }

        /// <summary>Whether Stop puts this scene back — it was loaded at Play.</summary>
        public bool Covers(Scene scene) => scene != null && _scenes.Contains(scene);

        /// <summary>Put everything back (call after the scripts' OnDestroy ran). Values that did not change are
        /// not written, so nothing is re-sent needlessly.</summary>
        public void Restore()
        {
            _removed.Clear();
            _settingsReverted.Clear();
            PutBack = 0;
            List<GameEntity> putBack = null;
            try { putBack = RestoreHierarchy(); }
            catch (Exception ex) { Log("Stop could not put the hierarchy back completely: " + ex.Message); }   // the values below still come back
            foreach (var r in _restore)
            {
                try { r(); } catch { /* a component torn down with its entity */ }
            }
            _restore.Clear();
            // an entity that had been destroyed or moved away is in the engine again — with its active flag already back, while
            // its parent and its scene are active (SyncEngineState, like every other place an entity is put; one without a
            // scene stays out instead of being registered outside any scene)
            if (putBack != null)
            {
                PutBack = putBack.Count;
                foreach (var e in putBack)
                {
                    try { e.SyncEngineState(); } catch { }
                }
            }
            RestoreSettings();
        }

        private static void Log(string message)
        {
            try { ConsoleService.Instance.LogError(message); } catch { }
        }

        private void TakeList(Scene scene, GameEntity owner, IList<GameEntity> list)
        {
            var slot = new Slot { Scene = scene, Owner = owner, Members = new GameEntity[list.Count] };
            list.CopyTo(slot.Members, 0);
            _slots.Add(slot);
            foreach (var e in slot.Members)
            {
                if (e == null || _homes.ContainsKey(e)) continue;
                _homes[e] = new Home { Parent = e.Parent, Scene = e.Scene };
                Walk(e);
                if (e.Children != null) TakeList(scene, e, e.Children);
            }
        }

        private void Walk(GameEntity e)
        {
            var entityProps = Capture(e, typeof(GameEntity));
            var authored = new List<Component>(e.Components);
            var componentProps = new List<Func<bool>>(authored.Count);
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
                for (int i = 0; i < authored.Count; i++)
                {
                    var back = componentProps[i];
                    if (back == null || !back()) continue;
                    // the foliage renderer keys its rebuild on the layer's version, not on the type objects it holds (#478)
                    if (authored[i] is ECS.Components.Rendering.Foliage foliage) foliage.Touch();
                }
                if (e.IsActive != wasActive) e.IsActive = wasActive;
            });
        }

        /// <summary>Every list of the hierarchy gets exactly the members it had at Take, in their order (#423). An entity
        /// that is in no list of the snapshot was added while playing and goes — wherever it was put; its engine and render
        /// resources are released and it leaves the scene for good (Parent and Scene cleared). An authored entity found in
        /// another list (re-parented, moved into another scene) is taken out there and put back into its own; a destroyed
        /// one is put back. Only what differs is touched, so an unchanged hierarchy sees no collection change. Returns the
        /// authored entities that had to be put back.</summary>
        private List<GameEntity> RestoreHierarchy()
        {
            var putBack = new List<GameEntity>();
            if (_slots.Count == 0) return putBack;

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

            // an authored entity moved into a scene loaded while playing: out of there again (that scene's own entities stay)
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

            foreach (var slot in _slots) Examine(slot.List, slot, false);
            var foreignScenes = new HashSet<Scene>(ReferenceEqualityComparer.Instance);
            foreach (var kv in _homes)
            {
                var now = kv.Key.Scene;
                if (now != null && !ReferenceEquals(now, kv.Value.Scene) && !_scenes.Contains(now) && foreignScenes.Add(now)) Foreign(now.Entities);
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
                var list = slot.List;
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

            // 4) what was added while playing leaves the engine and the renderer too — and every scene: a stale selection
            // of it must not act on a scene any more (a Duplicate added a copy next to it)
            foreach (var e in _removed)
            {
                try { e.SyncEngineStateRecursive(false); } catch { }
                try { SceneRenderService.Instance.RemoveEntityTree(e); } catch { }
                e.Parent = null;
                Unplace(e, 0);
            }
            if (_removed.Count > 0 || putBack.Count > 0 || strays.Count > 0)
                SceneRenderService.RuntimeDirty = SceneRenderService.StaticDirty = true;   // structural (#364 A)
            return putBack;
        }

        private static void Unplace(GameEntity e, int depth)
        {
            if (e == null || depth > 256) return;
            e.Scene = null;
            if (e.Children != null) foreach (var c in e.Children) Unplace(c, depth + 1);
        }

        /// <summary>Each scene's environment (fog, post effects, exposure, anti-aliasing, GI …) as it was at Take (#479) —
        /// set_environment, the Environment panel or a script may have changed it while playing. The live scene's renderer
        /// gets the authored values again: ScriptRuntime.End re-applied the play-time ones just before.</summary>
        private void RestoreSettings()
        {
            foreach (var (scene, settings, back) in _settings)
            {
                bool changed = false;
                try
                {
                    if (!ReferenceEquals(scene.Settings, settings)) { scene.Settings = settings; changed = true; }
                    if (back()) changed = true;
                }
                catch { }
                if (!changed) continue;
                _settingsReverted.Add(scene);
                if (scene.IsActive) { try { settings.Apply(); } catch { } }
            }
        }

        /// <summary>A function that puts the object's serialized properties back as they are now — the same objects, with what
        /// they hold now (#478) — and tells whether it changed anything.</summary>
        private static Func<bool> Capture(object target, Type type)
        {
            var props = Props(type);
            var memos = new Memo[props.Length];
            for (int i = 0; i < props.Length; i++)
            {
                try { memos[i] = Remember(props[i].GetValue(target), 0); } catch { memos[i] = null; }
            }
            return () =>
            {
                bool changed = false;
                for (int i = 0; i < props.Length; i++)
                {
                    var m = memos[i];
                    if (m == null) continue;
                    object current;
                    try { current = props[i].GetValue(target); } catch { continue; }
                    if (!Same(current, m.Value))
                    {
                        try { props[i].SetValue(target, m.Value); changed = true; } catch { }
                    }
                    if (PutContentsBack(m)) changed = true;   // the object may have been edited before it was replaced
                }
                return changed;
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
        /// and scenes are kept as references).</summary>
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

        /// <summary>A value as it was at Take: the value or the very object, and — for a list, an array, a dictionary or a plain
        /// data object — what it held then, each item with a memo of its own. A script field edited in place
        /// (Script.SetFieldValue changes the stored entry) is seen and undone in that same entry (#478).</summary>
        private sealed class Memo
        {
            public object Value;
            public object[] Items;    // list / array elements, dictionary values, a plain data object's member values
            public object[] Keys;     // dictionary keys
            public Member[] Members;  // a plain data object's members
            public Memo[] Inner;      // per item: what it held (null for plain values)
        }

        private static Memo Remember(object v, int depth)
        {
            var m = new Memo { Value = v };
            if (v == null || v is string || depth > MaxDepth || v.GetType().IsValueType) return m;
            try
            {
                if (v is Array a)
                {
                    if (a.Rank != 1 || a.GetLowerBound(0) != 0) return m;
                    m.Items = new object[a.Length];
                    for (int i = 0; i < a.Length; i++) m.Items[i] = a.GetValue(i);
                }
                else if (v is IList list)
                {
                    m.Items = new object[list.Count];
                    list.CopyTo(m.Items, 0);
                }
                else if (v is IDictionary dict)
                {
                    m.Keys = new object[dict.Count];
                    m.Items = new object[dict.Count];
                    int i = 0;
                    foreach (DictionaryEntry kv in dict) { m.Keys[i] = kv.Key; m.Items[i] = kv.Value; i++; }
                }
                else
                {
                    m.Members = PlainData(v.GetType());
                    if (m.Members == null) return m;
                    m.Items = new object[m.Members.Length];
                    for (int i = 0; i < m.Members.Length; i++) m.Items[i] = m.Members[i].Get(v);
                }
                m.Inner = new Memo[m.Items.Length];
                for (int i = 0; i < m.Items.Length; i++)
                {
                    var item = m.Items[i];
                    if (item != null && !(item is string) && !item.GetType().IsValueType) m.Inner[i] = Remember(item, depth + 1);
                }
            }
            catch { m.Items = null; m.Keys = null; m.Members = null; m.Inner = null; }
            return m;
        }

        /// <summary>Put what the remembered object held at Take back into that object; true when something had changed.</summary>
        private static bool PutContentsBack(Memo m)
        {
            if (m?.Items == null) return false;
            bool changed = false;
            try
            {
                if (m.Value is Array a)
                {
                    for (int i = 0; i < m.Items.Length && i < a.Length; i++)
                        if (!Same(a.GetValue(i), m.Items[i])) { a.SetValue(m.Items[i], i); changed = true; }
                }
                else if (m.Value is IList list)
                {
                    bool same = list.Count == m.Items.Length;
                    for (int i = 0; same && i < m.Items.Length; i++) same = Same(list[i], m.Items[i]);
                    if (!same) { list.Clear(); foreach (var item in m.Items) list.Add(item); changed = true; }
                }
                else if (m.Value is IDictionary dict)
                {
                    bool same = dict.Count == m.Keys.Length;
                    for (int i = 0; same && i < m.Keys.Length; i++) same = dict.Contains(m.Keys[i]) && Same(dict[m.Keys[i]], m.Items[i]);
                    if (!same) { dict.Clear(); for (int i = 0; i < m.Keys.Length; i++) dict[m.Keys[i]] = m.Items[i]; changed = true; }
                }
                else if (m.Members != null)
                {
                    for (int i = 0; i < m.Members.Length; i++)
                        if (!Same(m.Members[i].Get(m.Value), m.Items[i])) { m.Members[i].Set(m.Value, m.Items[i]); changed = true; }
                }
            }
            catch { }
            if (m.Inner != null)
                foreach (var inner in m.Inner) if (PutContentsBack(inner)) changed = true;
            return changed;
        }

        /// <summary>The same value: equal for value types and strings, the very same object for everything else.</summary>
        private static bool Same(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return (a is string || a.GetType().IsValueType) && Equals(a, b);
        }
    }
}
