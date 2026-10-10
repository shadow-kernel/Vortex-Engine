// Vortex scripting API — painted foliage (#125). Read how much grows where, paint or clear instances at runtime (a
// burnt patch, a cleared camp), and drive the wind every foliage layer sways with (the weather does). Runtime:
// Editor.Core.Services.Foliage.FoliageService.
using System;
using Editor.Core.Data;
using Editor.Core.Services.Foliage;

namespace Vortex
{
    /// <summary>
    /// The scene's foliage layers (the <c>Foliage</c> component): instances of trees, bushes, grass and rocks painted onto
    /// the ground. Positions are world space.
    /// <code>
    /// Foliage.SetWind(0.3f + storm * 1.7f);                       // every layer sways with the weather
    /// Foliage.Erase(Foliage.Find(), blast.Point, 6f);              // a blast clears the undergrowth
    /// Foliage.Paint(Foliage.Find(), "Grass", spot, 4f, 0.5f);      // and it grows back
    /// </code>
    /// </summary>
    public static class Foliage
    {
        /// <summary>Foliage layers in the active scene.</summary>
        public static int Count { get { return FoliageService.Count; } }

        /// <summary>The first Foliage entity of the active scene (0 when there is none).</summary>
        public static long Find()
        {
            foreach (var e in Layers()) return e.EntityId;
            return 0;
        }

        /// <summary>Instances of every type (or of <paramref name="typeName"/>) on a layer.</summary>
        public static int InstanceCount(long foliageEntity, string typeName = null)
        {
            var e = Resolve(foliageEntity);
            var f = e?.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (f == null) return 0;
            int idx = string.IsNullOrEmpty(typeName) ? -1 : f.IndexOfType(typeName);
            if (!string.IsNullOrEmpty(typeName) && idx < 0) return 0;
            return FoliageService.InstanceCount(e, idx);
        }

        /// <summary>Paint instances of a type inside a disc (<paramref name="strength"/> × the type's density); the number placed.</summary>
        public static int Paint(long foliageEntity, string typeName, Vector3 centre, float radius, float strength = 1f, int seed = 0)
        {
            var e = Resolve(foliageEntity);
            var f = e?.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (f == null) return 0;
            int idx = f.IndexOfType(typeName);
            if (idx < 0) return 0;
            int n = FoliageService.Paint(e, idx, Vfx.Sys(centre), radius, strength, seed != 0 ? seed : Environment.TickCount);
            return n;
        }

        /// <summary>Remove the instances inside a disc — one type, or every type when <paramref name="typeName"/> is null.</summary>
        public static int Erase(long foliageEntity, Vector3 centre, float radius, string typeName = null)
        {
            var e = Resolve(foliageEntity);
            var f = e?.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (f == null) return 0;
            int idx = string.IsNullOrEmpty(typeName) ? -1 : f.IndexOfType(typeName);
            if (!string.IsNullOrEmpty(typeName) && idx < 0) return 0;
            return FoliageService.Erase(e, idx, Vfx.Sys(centre), radius);
        }

        /// <summary>The wind every foliage layer sways with: 0 calm, 1 a breeze, 2 and above a storm.</summary>
        public static void SetWind(float strength)
        {
            foreach (var e in Layers())
            {
                var f = e.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
                if (f != null) f.Wind = strength;
            }
        }

        /// <summary>The wind of the first foliage layer (0 when there is none).</summary>
        public static float Wind
        {
            get
            {
                foreach (var e in Layers()) { var f = e.GetComponent<Editor.ECS.Components.Rendering.Foliage>(); if (f != null) return f.Wind; }
                return 0f;
            }
        }

        /// <summary>Write every layer's instances to its data file.</summary>
        public static int Save() { return FoliageService.SaveAll(ProjectData.Current?.ActiveScene); }

        /// <summary>An entity by handle: the script runtime's table while playing, else the scene itself (editor tools, smokes).</summary>
        internal static Editor.ECS.GameEntity Resolve(long handle)
        {
            if (handle == 0) return null;
            Editor.ECS.GameEntity e = null;
            try { e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(handle); } catch { }
            if (e != null) return e;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene?.Entities == null) return null;
            var stack = new System.Collections.Generic.Stack<Editor.ECS.GameEntity>();
            foreach (var r in scene.Entities) stack.Push(r);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                if (x == null) continue;
                if (x.EntityId == handle) return x;
                if (x.Children != null) foreach (var c in x.Children) stack.Push(c);
            }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<Editor.ECS.GameEntity> Layers()
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene?.Entities == null) yield break;
            var stack = new System.Collections.Generic.Stack<Editor.ECS.GameEntity>();
            foreach (var e in scene.Entities) stack.Push(e);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (e == null) continue;
                if (e.GetComponent<Editor.ECS.Components.Rendering.Foliage>() != null) yield return e;
                if (e.Children != null) foreach (var c in e.Children) stack.Push(c);
            }
        }
    }
}
