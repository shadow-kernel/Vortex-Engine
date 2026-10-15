using System.Collections.Generic;
using Editor.Core.Data;
using Editor.Core.UndoRedo;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Keeps <see cref="Scene.IsDirty"/> true to what is unsaved (#411 review): every undo step executed, undone or redone marks
    /// the scenes it touches — found with the undo walk over its entities, components and scenes — so inspector rows, gizmo
    /// drags, hierarchy moves and tool edits all count, not only the few edits that set the flag themselves. Reading, saving
    /// and reverting a scene clear it; Stop puts back the flags of Play. The editor installs it; the player has nothing to save.
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
    }
}
