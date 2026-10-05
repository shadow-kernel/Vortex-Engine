using System;
using System.Collections.Generic;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Claude
{
    /// <summary>World-space bounding boxes of entities (their meshes, children included) for framing and spatial tools.</summary>
    public static class Bounds
    {
        /// <summary>The mesh's box in the entity's local space; false for entities without a mesh.</summary>
        public static bool LocalBox(GameEntity e, out Vector3 center, out Vector3 size)
        {
            center = Vector3.Zero; size = Vector3.Zero;
            var mr = e?.GetComponent<MeshRenderer>();
            string mesh = mr?.MeshPath;
            if (string.IsNullOrEmpty(mesh)) return false;
            if (mesh.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                switch (mesh.Substring(10).ToLowerInvariant())
                {
                    case "plane": case "quad": size = new Vector3(1, 0, 1); return true;
                    default: size = Vector3.One; return true;
                }
            }
            try { SceneRenderService.Instance.TryGetMeshBounds(mesh, out size, out center); return true; }
            catch { size = Vector3.One; return true; }
        }

        /// <summary>World AABB of the entity's own mesh (children not included); false without a mesh.</summary>
        public static bool OwnWorldBox(GameEntity e, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.Zero;
            if (!LocalBox(e, out var c, out var s)) return false;
            var m = TransformMath.World(e);
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = TransformMath.TransformPoint(m, new Vector3(c.X + ((i & 1) == 0 ? -0.5f : 0.5f) * s.X, c.Y + ((i & 2) == 0 ? -0.5f : 0.5f) * s.Y, c.Z + ((i & 4) == 0 ? -0.5f : 0.5f) * s.Z));
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            min = new Vector3(x0, y0, z0);
            max = new Vector3(x1, y1, z1);
            return true;
        }

        /// <summary>World AABB of the entities and all their descendants; meshless leaves (lights, empties) count as their
        /// position, meshless groups only through their children.</summary>
        public static void Of(IEnumerable<GameEntity> entities, out Vector3 min, out Vector3 max)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            bool any = false;
            void Add(Vector3 p)
            {
                any = true;
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            void Walk(GameEntity e)
            {
                if (e == null) return;
                var m = TransformMath.World(e);
                if (LocalBox(e, out var c, out var s))
                {
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new Vector3(c.X + ((i & 1) == 0 ? -0.5f : 0.5f) * s.X, c.Y + ((i & 2) == 0 ? -0.5f : 0.5f) * s.Y, c.Z + ((i & 4) == 0 ? -0.5f : 0.5f) * s.Z);
                        Add(TransformMath.TransformPoint(m, corner));
                    }
                }
                // a light / camera / empty counts as its position; a group (no mesh, children) only through its children
                else if (e.Children == null || e.Children.Count == 0) Add(new Vector3(m[12], m[13], m[14]));
                if (e.Children != null) foreach (var ch in e.Children) Walk(ch);
            }
            foreach (var e in entities) Walk(e);
            if (!any) { min = Vector3.Zero; max = Vector3.Zero; return; }
            min = new Vector3(x0, y0, z0);
            max = new Vector3(x1, y1, z1);
        }
    }
}
