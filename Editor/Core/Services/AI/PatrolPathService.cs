using System;
using System.Collections.Generic;
using Editor.ECS;
using Editor.ECS.Components.AI;
using SysVec = System.Numerics.Vector3;

namespace Editor.Core.Services.AI
{
    /// <summary>
    /// Patrol routes (GitHub #114): world-space waypoints of a <see cref="PatrolPath"/> (child entities in hierarchy order,
    /// or the local-space point list transformed by the path entity) and the debug-line feed for the viewport gizmo layer
    /// (<see cref="GetDebugLines"/>: route segments, a cross per waypoint, a tick marking waypoint 0 and arrows for the
    /// walking direction). Pure data + math: usable in the editor (gizmos) and at runtime (scripts, samples).
    /// </summary>
    public static class PatrolPathService
    {
        /// <summary>The route's waypoints in world space (empty when it has none).</summary>
        public static List<Vector3> GetWorldWaypoints(PatrolPath path)
        {
            var list = new List<Vector3>();
            var e = path?.Entity;
            if (e == null) return list;
            if (path.UsesChildWaypoints)
            {
                foreach (var c in e.Children)
                {
                    if (c == null) continue;
                    var w = Animation.BoneSocketService.EntityWorld(c).Translation;
                    list.Add(new Vector3(w.X, w.Y, w.Z));
                }
                return list;
            }
            var world = Animation.BoneSocketService.EntityWorld(e);
            foreach (var p in path.Points)
            {
                var w = SysVec.Transform(new SysVec(p.X, p.Y, p.Z), world);
                list.Add(new Vector3(w.X, w.Y, w.Z));
            }
            return list;
        }

        /// <summary>World position of waypoint <paramref name="index"/> (wrapped into range); false for an empty route.</summary>
        public static bool TryGetWaypoint(PatrolPath path, int index, out Vector3 point)
        {
            point = Vector3.Zero;
            var pts = GetWorldWaypoints(path);
            if (pts.Count == 0) return false;
            index %= pts.Count;
            if (index < 0) index += pts.Count;
            point = pts[index];
            return true;
        }

        /// <summary>Index of the waypoint closest to <paramref name="position"/> (-1 for an empty route) — where a patrol
        /// resumes after a chase.</summary>
        public static int ClosestWaypoint(PatrolPath path, Vector3 position)
        {
            var pts = GetWorldWaypoints(path);
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                float d = (pts[i] - position).SqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Convert a world position into the path entity's local space (for authoring the point list).</summary>
        public static Vector3 WorldToLocal(PatrolPath path, Vector3 world)
        {
            var e = path?.Entity;
            if (e == null) return world;
            var m = Animation.BoneSocketService.EntityWorld(e);
            if (!System.Numerics.Matrix4x4.Invert(m, out var inv)) return world;
            var l = SysVec.Transform(new SysVec(world.X, world.Y, world.Z), inv);
            return new Vector3(l.X, l.Y, l.Z);
        }

        /// <summary>Line segments (6 floats each: x0 y0 z0 x1 y1 z1) that draw the route: the path (closed for Loop),
        /// a cross at every waypoint (bigger at waypoint 0), and a small arrow head at each segment's middle pointing the
        /// walking direction. <paramref name="lift"/> raises everything off the floor. The feed for the gizmo package;
        /// <c>VortexAPI.RenderNavigationLines</c> draws it through the gizmo pass.</summary>
        public static float[] GetDebugLines(PatrolPath path, float lift = 0.1f)
        {
            var pts = GetWorldWaypoints(path);
            var s = new List<float>();
            if (pts.Count == 0) return s.ToArray();
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i] + new Vector3(0f, lift, 0f);
                float k = i == 0 ? 0.35f : 0.2f;
                Seg(s, p + new Vector3(-k, 0f, 0f), p + new Vector3(k, 0f, 0f));
                Seg(s, p + new Vector3(0f, 0f, -k), p + new Vector3(0f, 0f, k));
                if (i == 0) Seg(s, p, p + new Vector3(0f, 0.6f, 0f));   // start marker
            }
            int segCount = path.Mode == PatrolMode.Loop && pts.Count > 2 ? pts.Count : pts.Count - 1;
            for (int i = 0; i < segCount; i++)
            {
                var a = pts[i] + new Vector3(0f, lift, 0f);
                var b = pts[(i + 1) % pts.Count] + new Vector3(0f, lift, 0f);
                Seg(s, a, b);
                var d = b - a;
                float len = d.Magnitude;
                if (len < 0.3f) continue;
                var dir = d / len;
                var side = Vector3.Cross(dir, Vector3.Up);
                if (side.Magnitude < 1e-4f) side = new Vector3(1f, 0f, 0f);
                side = side.Normalized;
                var mid = a + d * 0.5f;
                float h = Math.Min(0.3f, len * 0.2f);
                Seg(s, mid, mid - dir * h + side * (h * 0.6f));
                Seg(s, mid, mid - dir * h - side * (h * 0.6f));
            }
            return s.ToArray();
        }

        private static void Seg(List<float> s, Vector3 a, Vector3 b)
        {
            s.Add(a.X); s.Add(a.Y); s.Add(a.Z); s.Add(b.X); s.Add(b.Y); s.Add(b.Z);
        }

        /// <summary>Draw a route now (one frame) through the gizmo pass — editor overlay / in-game debug.</summary>
        public static void Submit(PatrolPath path, bool highlighted)
        {
            var lines = GetDebugLines(path);
            if (lines.Length < 6) return;
            if (highlighted) Editor.DllWrapper.VortexAPI.RenderNavigationLines(lines, lines.Length, 1f, 0.55f, 0.1f, 0.035f, 120);
            else Editor.DllWrapper.VortexAPI.RenderNavigationLines(lines, lines.Length, 0.85f, 0.45f, 0.1f, 0.02f, 120);
        }
    }
}
