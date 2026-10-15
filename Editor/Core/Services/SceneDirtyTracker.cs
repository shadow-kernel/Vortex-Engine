using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Editor.Core.Data;
using Editor.Core.UndoRedo;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Keeps <see cref="Scene.IsDirty"/> close to what is unsaved (#411 review). Every undo step executed, undone or redone marks
    /// the scenes it touches — found with the undo walk over its entities, components and scenes: hierarchy edits, gizmo drags
    /// (one step per drag), the inspector header, tool calls. Inspector rows write values without an undo step, so the inspector
    /// marks its entity's scene itself; a tool call marks the scenes of the entities it touched. Reading, saving and reverting a
    /// scene clear the flag; Stop puts back the flags of Play (and marks a scene saved while playing). The flag is a fast hint:
    /// whether a scene really differs from its file is <see cref="Scene.MatchesBaseline"/>, which the tools and Revert ask.
    /// The editor installs it; the player has nothing to save.
    /// </summary>
    public static class SceneDirtyTracker
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            UndoRedoManager.Instance.CommandExecuted += (s, e) =>
            {
                try { MarkScenesOf(e?.Command); } catch { }
            };
        }

        /// <summary>Mark every loaded scene <paramref name="step"/> touches as unsaved; when the walk cannot finish, the active
        /// scene at least.</summary>
        internal static void MarkScenesOf(object step)
        {
            if (step == null) return;
            var scenes = new HashSet<Scene>(ReferenceEqualityComparer.Instance);
            var r = UndoReferences.Walk(step, o =>
            {
                switch (o)
                {
                    case Scene sc: scenes.Add(sc); return UndoRef.Skip;
                    case GameEntity e: if (e.Scene != null) scenes.Add(e.Scene); return UndoRef.Skip;
                    case Editor.ECS.Component c: if (c.Entity?.Scene != null) scenes.Add(c.Entity.Scene); return UndoRef.Skip;
                    case ProjectData _: return UndoRef.Skip;
                    case IEnumerable<Scene> _: return UndoRef.Skip;   // the project's scene list is not an edit of each scene
                    default: return UndoRef.LookInto;
                }
            }, 200_000);
            foreach (var sc in scenes) if (sc.IsLoaded) sc.IsDirty = true;
            if (r.OutOfBudget && ProjectData.Current?.ActiveScene is Scene active) active.IsDirty = true;
        }

        private static readonly ConcurrentDictionary<(Type, string), bool> _persisted = new ConcurrentDictionary<(Type, string), bool>();

        /// <summary>
        /// Whether a change of <paramref name="property"/> on <paramref name="owner"/> is one the scene file holds: a [DataMember]
        /// property, or the property of a [DataMember] field (<c>_size</c> / <c>size</c> for <c>Size</c>) — not the selection, an
        /// expanded node or another runtime state ([IgnoreDataMember] or not serialized at all).
        /// </summary>
        public static bool IsPersisted(object owner, string property)
        {
            if (owner == null || string.IsNullOrEmpty(property)) return false;
            return _persisted.GetOrAdd((owner.GetType(), property), k => Find(k.Item1, k.Item2));
        }

        private static bool Find(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var p = t.GetProperty(name, flags);
                if (p != null)
                {
                    if (p.IsDefined(typeof(IgnoreDataMemberAttribute), true)) return false;
                    if (p.IsDefined(typeof(DataMemberAttribute), true)) return true;
                }
                foreach (var f in t.GetFields(flags))
                {
                    var dm = f.GetCustomAttribute<DataMemberAttribute>();
                    if (dm == null) continue;
                    if (string.Equals(f.Name.TrimStart('_'), name, StringComparison.OrdinalIgnoreCase)) return true;
                    if (dm.Name != null && string.Equals(dm.Name.TrimStart('_'), name, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
    }
}
