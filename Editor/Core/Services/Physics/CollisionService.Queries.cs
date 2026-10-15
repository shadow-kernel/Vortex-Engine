using System;
using System.Collections.Generic;
using Editor.ECS;
using SysVec = System.Numerics.Vector3;

namespace Editor.Core.Services.Physics
{
    /// <summary>
    /// Shape casts, all-hit rays and shape overlaps on the collision world (#480) — the answers Physics.SphereCast /
    /// CapsuleCast / BoxCast / RaycastAll / OverlapBox … give without a Jolt world or with an explicit entity-layer mask.
    /// They follow the Jolt queries' contract (PhysicsService): the narrow phase is a port of Jolt's GJK (closest points and
    /// the convex cast with convex radii), so both paths meet a shape at the same distance. A sphere is a point and a
    /// capsule a segment with a radius; boxes and triangles are sharp (Jolt rounds a box's edges by up to 5 cm, so a sweep
    /// that grazes an edge may differ by a centimetre or two).
    /// </summary>
    public static partial class CollisionService
    {
        // ------------------------------------------------------------------------------------------ public queries

        /// <summary>Sweep <paramref name="shape"/> along <paramref name="direction"/> for <paramref name="maxDist"/>; the
        /// first hit. <paramref name="layerMask"/> filters by the owning entity's Layer bit (like <see cref="Raycast"/>).
        /// What a sweep that STARTS in contact reports — the rule of the Jolt query: a collider the shape already overlaps
        /// (or touches) is hit at distance 0 when the sweep moves deeper into it (point = on the collider, normal = the way
        /// out), and not at all when the sweep moves out of it or along it. Mesh colliders and terrain are surfaces, hit
        /// from either side. Normals face the cast. <paramref name="ignore"/> and everything under it are never hit;
        /// <paramref name="triggers"/>: trigger colliders count too. Characters are no part of this world.</summary>
        public static bool ShapeCast(QueryShape shape, Vector3 direction, float maxDist, int layerMask, out PhysicsQueryHit hit,
            GameEntity ignore = null, bool triggers = false)
        {
            hit = default(PhysicsQueryHit);
            _castHits.Clear();
            if (Sweep(shape, direction, maxDist, layerMask, ignore, triggers, false, _castHits) == 0) return false;
            hit = _castHits[0];
            return true;
        }

        /// <summary>Every entity the sweep meets — the first hit on each — sorted by distance, appended to
        /// <paramref name="results"/>; returns how many. The rules of <see cref="ShapeCast"/>.</summary>
        public static int ShapeCastAll(QueryShape shape, Vector3 direction, float maxDist, int layerMask, List<PhysicsQueryHit> results,
            GameEntity ignore = null, bool triggers = false)
            => Sweep(shape, direction, maxDist, layerMask, ignore, triggers, true, results);

        /// <summary>Every entity along the ray — the first hit on each — sorted by distance, appended to
        /// <paramref name="results"/>; returns how many. The rules of <see cref="Raycast"/> (#418): a box, sphere or capsule
        /// that contains the origin is not hit, a mesh is hit from either side (once: where the ray first meets it), a ray
        /// that starts on a surface pointing into it hits it at distance 0, normals face the origin.</summary>
        public static int RaycastAll(Vector3 origin, Vector3 direction, float maxDist, int layerMask, List<PhysicsQueryHit> results,
            GameEntity ignore = null, bool triggers = false)
        {
            if (results == null || !IsBuilt) return 0;
            V3 d = From(direction).Norm();
            if (d.Len() < 0.5f || !(maxDist > 0f)) return 0;
            V3 o = From(origin) - d * RaySurfaceSkin;
            float reach = maxDist + RaySurfaceSkin;
            float maxT = reach + reach * 1e-6f + 1e-6f;
            _found.Clear();
            for (int list = 0; list < (triggers ? 3 : 2); list++)
            {
                var shapes = list == 0 ? _world : list == 1 ? _dynamic : _triggers;
                for (int i = 0; i < shapes.Count; i++)
                {
                    var s = shapes[i];
                    if (s == null || !Admit(s, layerMask, ignore)) continue;
                    if (!RayShape(o, d, s, maxT, out float t, out V3 n)) continue;
                    Keep(s, t, o + d * t, n);
                }
            }
            return Emit(results, 1f, RaySurfaceSkin, maxDist);
        }

        /// <summary>Every entity whose solid collider overlaps or touches <paramref name="shape"/> (entity-layer mask,
        /// deduplicated; mesh colliders count from either side, like the Jolt query); <paramref name="ignore"/> and everything
        /// under it are left out; <paramref name="triggers"/>: trigger colliders count too.</summary>
        public static List<GameEntity> OverlapShape(QueryShape shape, int layerMask = ~0, GameEntity ignore = null, bool triggers = false)
        {
            var result = new List<GameEntity>();
            if (!IsBuilt || !ToConvex(shape, out var q)) return result;
            Bounds(q, new V3(0, 0, 0), out V3 qmin, out V3 qmax);
            for (int list = 0; list < (triggers ? 3 : 2); list++)
            {
                var shapes = list == 0 ? _world : list == 1 ? _dynamic : _triggers;
                for (int i = 0; i < shapes.Count; i++)
                {
                    var s = shapes[i];
                    if (s == null || s.Owner == null || result.Contains(s.Owner) || !AabbOverlap(qmin, qmax, s.Min, s.Max)) continue;
                    if (!Admit(s, layerMask, ignore) || !Overlaps(q, s, qmin, qmax)) continue;
                    result.Add(s.Owner);
                }
            }
            return result;
        }

        /// <summary>Does any solid collider overlap or touch <paramref name="shape"/>? <see cref="OverlapShape"/> without the
        /// list (a collider without an entity counts too).</summary>
        public static bool CheckShape(QueryShape shape, int layerMask = ~0, GameEntity ignore = null, bool triggers = false)
        {
            if (!IsBuilt || !ToConvex(shape, out var q)) return false;
            Bounds(q, new V3(0, 0, 0), out V3 qmin, out V3 qmax);
            for (int list = 0; list < (triggers ? 3 : 2); list++)
            {
                var shapes = list == 0 ? _world : list == 1 ? _dynamic : _triggers;
                for (int i = 0; i < shapes.Count; i++)
                {
                    var s = shapes[i];
                    if (s == null || !AabbOverlap(qmin, qmax, s.Min, s.Max) || !Admit(s, layerMask, ignore)) continue;
                    if (Overlaps(q, s, qmin, qmax)) return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------ sweep

        private static readonly List<PhysicsQueryHit> _castHits = new List<PhysicsQueryHit>(1);

        /// <summary>The query's best hit per entity (per shape for shapes without an owner): fraction / distance key,
        /// point, normal.</summary>
        private struct Found { public object Key; public GameEntity Owner; public float T; public V3 Point, Normal; }
        private static readonly List<Found> _found = new List<Found>();
        private static readonly Comparison<Found> _byT = (a, b) => a.T.CompareTo(b.T);

        private static bool Admit(Shape s, int layerMask, GameEntity ignore)
        {
            if (s.Owner != null && (layerMask & (1 << (s.Owner.Layer & 31))) == 0) return false;
            return ignore == null || !IsUnder(s.Owner, ignore);
        }

        private static void Keep(Shape s, float t, V3 point, V3 normal)
        {
            object key = (object)s.Owner ?? s;
            for (int i = 0; i < _found.Count; i++)
            {
                if (!ReferenceEquals(_found[i].Key, key)) continue;
                if (t < _found[i].T) _found[i] = new Found { Key = key, Owner = s.Owner, T = t, Point = point, Normal = normal };
                return;
            }
            _found.Add(new Found { Key = key, Owner = s.Owner, T = t, Point = point, Normal = normal });
        }

        /// <summary>The kept hits, sorted, into <paramref name="results"/>: distance = T × scale − offset, in [0, max].</summary>
        private static int Emit(List<PhysicsQueryHit> results, float scale, float offset, float max)
        {
            if (_found.Count > 1) _found.Sort(_byT);
            for (int i = 0; i < _found.Count; i++)
                results.Add(new PhysicsQueryHit { Point = To(_found[i].Point), Normal = To(_found[i].Normal),
                    Distance = Math.Min(max, Math.Max(0f, _found[i].T * scale - offset)), Entity = _found[i].Owner });
            int n = _found.Count;
            _found.Clear();
            return n;
        }

        private static int Sweep(QueryShape shape, Vector3 direction, float maxDist, int layerMask, GameEntity ignore, bool triggers, bool all,
            List<PhysicsQueryHit> results)
        {
            if (results == null || !IsBuilt || !ToConvex(shape, out var q)) return 0;
            V3 d = From(direction).Norm();
            if (d.Len() < 0.5f || !(maxDist > 0f) || float.IsInfinity(maxDist)) return 0;
            V3 disp = d * maxDist;
            Bounds(q, disp, out V3 smin, out V3 smax);
            _found.Clear();
            float best = float.MaxValue;   // the closest hit so far (first-hit mode skips what cannot beat it)
            for (int list = 0; list < (triggers ? 3 : 2); list++)
            {
                var shapes = list == 0 ? _world : list == 1 ? _dynamic : _triggers;
                for (int i = 0; i < shapes.Count; i++)
                {
                    var s = shapes[i];
                    if (s == null || !AabbOverlap(smin, smax, s.Min, s.Max) || !Admit(s, layerMask, ignore)) continue;
                    if (!SweepShape(q, disp, s, smin, smax, all ? 1f + FltEps : Math.Min(best, 1f + FltEps), out float t, out V3 p, out V3 n)) continue;
                    if (!all && t >= best) continue;
                    if (!all) { _found.Clear(); best = t; }
                    Keep(s, t, p, n);
                }
            }
            return Emit(results, maxDist, 0f, maxDist);
        }

        /// <summary>The first hit of the swept query on one collider shape (a mesh: its first triangle along the sweep).</summary>
        private static bool SweepShape(in Convex q, V3 disp, Shape s, V3 smin, V3 smax, float maxLambda, out float t, out V3 point, out V3 normal)
        {
            t = 0f; point = default(V3); normal = default(V3);
            if (s.Kind != Kind.Tris) return CastAgainst(q, disp, ShapeConvex(s), maxLambda, out t, out point, out normal);
            if (s.Tris == null) return false;
            bool any = false;
            float limit = maxLambda;
            var tris = s.Tris;
            if (s.Grid != null)
            {
                var buf = s.QueryBox(smin, smax, out int cnt);
                // (the grid's buffer is shared: copy the candidates before CastAgainst could query anything else)
                if (_triCandidates.Length < cnt) _triCandidates = new int[Math.Max(cnt, _triCandidates.Length * 2)];
                Array.Copy(buf, _triCandidates, cnt);
                for (int j = 0; j < cnt; j++) TryTriangle(q, disp, tris, _triCandidates[j] * 3, ref limit, ref any, ref t, ref point, ref normal);
            }
            else
                for (int k = 0; k + 2 < tris.Length; k += 3) TryTriangle(q, disp, tris, k, ref limit, ref any, ref t, ref point, ref normal);
            return any;
        }

        private static int[] _triCandidates = new int[64];

        private static void TryTriangle(in Convex q, V3 disp, V3[] tris, int k, ref float limit, ref bool any, ref float t, ref V3 point, ref V3 normal)
        {
            var tri = new Convex { Kind = 3, A = tris[k], B = tris[k + 1], C = tris[k + 2] };
            if (!CastAgainst(q, disp, tri, limit, out float tt, out V3 p, out V3 n)) return;
            if (any && tt >= t) return;
            any = true; t = tt; point = p; normal = n; limit = Math.Min(limit, tt + FltEps);
        }

        /// <summary>The swept query against one convex target: Jolt's convex cast (GJK ray cast against the Minkowski
        /// difference, convex radii kept apart). A hit at the start (fraction 0) counts only when the sweep moves deeper
        /// into the target along the penetration axis — Jolt's sweep_collector rule.</summary>
        private static bool CastAgainst(in Convex q, V3 disp, in Convex target, float maxLambda, out float t, out V3 point, out V3 normal)
        {
            t = maxLambda; point = default(V3); normal = default(V3);
            if (!GjkCast(q, target, disp, GjkTolerance, ref t, out V3 pa, out V3 pb, out V3 axis)) return false;
            if (t <= 0f)
            {
                t = 0f;
                // the start overlaps / touches: the penetration axis (query -> target) decides in or out
                V3 v = target.Center - q.Center;
                if (v.Dot(v) < 1e-12f) v = new V3(0, 1, 0);
                float d2 = GjkClosestPoints(q, target, GjkTolerance, float.MaxValue, ref v, out V3 ca, out V3 cb);
                if (d2 > GjkTolerance * GjkTolerance && d2 < float.MaxValue)
                {
                    // the cores are apart (the overlap is within the radii): the axis is exact
                    axis = (cb - ca).Norm();
                    point = cb + axis * -target.R;
                }
                else
                {
                    // the cores overlap: the axis of least penetration (a separating-axis test)
                    float depth = SatAxis(q, target, disp.Norm(), out axis);
                    point = q.Support(axis) + axis * (q.R - depth);
                }
            }
            else
            {
                point = pb;
                if (axis.Dot(axis) < GjkTolerance * GjkTolerance) axis = disp;   // no usable separating axis: the cast direction
                axis = axis.Norm();
            }
            // moving out of it or along it (within SweepIntoMargin, the Jolt query's margin: sliding along a touched surface
            // is free, and the noise of an overlap's axis must not decide)
            if (!(axis.Dot(disp) > SweepIntoMargin * disp.Len())) return false;
            normal = axis * -1f;
            return true;
        }

        /// <summary>The cosine of 89.94°: how far a sweep must point into what it touches to count as moving into it.</summary>
        private const float SweepIntoMargin = 1.0e-3f;

        /// <summary>Does the query shape overlap / touch the collider shape?</summary>
        private static bool Overlaps(in Convex q, Shape s, V3 qmin, V3 qmax)
        {
            if (s.Kind != Kind.Tris) return Touches(q, ShapeConvex(s));
            if (s.Tris == null) return false;
            var tris = s.Tris;
            if (s.Grid != null)
            {
                var buf = s.QueryBox(qmin, qmax, out int cnt);
                for (int j = 0; j < cnt; j++)
                {
                    int k = buf[j] * 3;
                    if (Touches(q, new Convex { Kind = 3, A = tris[k], B = tris[k + 1], C = tris[k + 2] })) return true;
                }
                return false;
            }
            for (int k = 0; k + 2 < tris.Length; k += 3)
                if (Touches(q, new Convex { Kind = 3, A = tris[k], B = tris[k + 1], C = tris[k + 2] })) return true;
            return false;
        }

        private static bool Touches(in Convex a, in Convex b)
        {
            float r = a.R + b.R;
            V3 v = b.Center - a.Center;
            if (v.Dot(v) < 1e-12f) v = new V3(0, 1, 0);
            float d2 = GjkClosestPoints(a, b, GjkTolerance, (r + GjkTolerance) * (r + GjkTolerance), ref v, out _, out _);
            return d2 <= (r + GjkTolerance) * (r + GjkTolerance);
        }

        // ------------------------------------------------------------------------------------------ convex shapes

        /// <summary>A convex core and its radius (Jolt's "convex radius"): a sphere is a point, a capsule a segment.</summary>
        private struct Convex
        {
            public int Kind;      // 0 point, 1 segment, 2 box, 3 triangle
            public V3 A, B, C;    // point: A; segment: A..B; triangle: A, B, C; box: A = its centre
            public V3 X, Y, Z;    // box axes (unit)
            public V3 H;          // box half extents
            public float R;       // radius around the core (spheres, capsules)

            public V3 Center => Kind == 1 ? (A + B) * 0.5f : Kind == 3 ? (A + B + C) * (1f / 3f) : A;

            public V3 Support(V3 d)
            {
                switch (Kind)
                {
                    case 0: return A;
                    case 1: return d.Dot(B) > d.Dot(A) ? B : A;
                    case 2: return A + X * (d.Dot(X) >= 0f ? H.X : -H.X) + Y * (d.Dot(Y) >= 0f ? H.Y : -H.Y) + Z * (d.Dot(Z) >= 0f ? H.Z : -H.Z);
                    default:
                    {
                        float a = d.Dot(A), b = d.Dot(B), c = d.Dot(C);
                        return a >= b ? (a >= c ? A : C) : (b >= c ? B : C);
                    }
                }
            }
        }

        private static Convex ShapeConvex(Shape s)
        {
            switch (s.Kind)
            {
                case Kind.Box: return new Convex { Kind = 2, A = s.Center, X = s.AxX, Y = s.AxY, Z = s.AxZ, H = s.Half };
                case Kind.Sphere: return new Convex { Kind = 0, A = s.Center, R = s.Radius };
                default: return new Convex { Kind = 1, A = s.A, B = s.B, R = s.Radius };   // capsule
            }
        }

        private static bool ToConvex(QueryShape s, out Convex c)
        {
            c = default(Convex);
            var center = From(s.Center);
            var rot = s.Rotation;
            if (rot.X == 0f && rot.Y == 0f && rot.Z == 0f && rot.W == 0f) rot = System.Numerics.Quaternion.Identity;
            if (float.IsNaN(center.X) || float.IsNaN(center.Y) || float.IsNaN(center.Z)) return false;
            switch (s.Type)
            {
                case PhysicsService.ShapeSphere:
                    if (!(s.Radius > 0f) || float.IsInfinity(s.Radius)) return false;
                    c = new Convex { Kind = 0, A = center, R = s.Radius };
                    return true;
                case PhysicsService.ShapeCapsule:
                {
                    if (!(s.Radius > 0f) || float.IsInfinity(s.Radius)) return false;
                    var up = Rotate(rot, new V3(0, 1, 0)) * Math.Max(0f, s.HalfHeight);
                    c = new Convex { Kind = 1, A = center - up, B = center + up, R = s.Radius };
                    return true;
                }
                case PhysicsService.ShapeBox:
                {
                    var h = new V3(Math.Abs(s.HalfExtents.X), Math.Abs(s.HalfExtents.Y), Math.Abs(s.HalfExtents.Z));
                    if (!(Math.Min(h.X, Math.Min(h.Y, h.Z)) > 0f) || float.IsInfinity(h.X + h.Y + h.Z)) return false;
                    c = new Convex { Kind = 2, A = center, X = Rotate(rot, new V3(1, 0, 0)), Y = Rotate(rot, new V3(0, 1, 0)), Z = Rotate(rot, new V3(0, 0, 1)), H = h };
                    return true;
                }
                default: return false;
            }
        }

        private static V3 Rotate(System.Numerics.Quaternion q, V3 v)
        {
            var r = SysVec.Transform(new SysVec(v.X, v.Y, v.Z), System.Numerics.Quaternion.Normalize(q));
            return new V3(r.X, r.Y, r.Z);
        }

        /// <summary>World AABB of the query at its start and moved by <paramref name="disp"/>.</summary>
        private static void Bounds(in Convex c, V3 disp, out V3 min, out V3 max)
        {
            V3 lo, hi;
            switch (c.Kind)
            {
                case 0: lo = hi = c.A; break;
                case 1: lo = new V3(Math.Min(c.A.X, c.B.X), Math.Min(c.A.Y, c.B.Y), Math.Min(c.A.Z, c.B.Z)); hi = new V3(Math.Max(c.A.X, c.B.X), Math.Max(c.A.Y, c.B.Y), Math.Max(c.A.Z, c.B.Z)); break;
                default:
                {
                    var e = new V3(Math.Abs(c.X.X) * c.H.X + Math.Abs(c.Y.X) * c.H.Y + Math.Abs(c.Z.X) * c.H.Z,
                                   Math.Abs(c.X.Y) * c.H.X + Math.Abs(c.Y.Y) * c.H.Y + Math.Abs(c.Z.Y) * c.H.Z,
                                   Math.Abs(c.X.Z) * c.H.X + Math.Abs(c.Y.Z) * c.H.Y + Math.Abs(c.Z.Z) * c.H.Z);
                    lo = c.A - e; hi = c.A + e;
                    break;
                }
            }
            var r = new V3(c.R + 1e-3f, c.R + 1e-3f, c.R + 1e-3f);
            lo = lo - r; hi = hi + r;
            min = new V3(Math.Min(lo.X, lo.X + disp.X), Math.Min(lo.Y, lo.Y + disp.Y), Math.Min(lo.Z, lo.Z + disp.Z));
            max = new V3(Math.Max(hi.X, hi.X + disp.X), Math.Max(hi.Y, hi.Y + disp.Y), Math.Max(hi.Z, hi.Z + disp.Z));
        }

        /// <summary>The axis of least overlap between two overlapping cores (with their radii), pointing from
        /// <paramref name="a"/> to <paramref name="b"/>; returns the overlap along it. Candidates: the face normals of
        /// boxes and triangles and the cross products of their edges / segments (exact for boxes and triangles, close for
        /// the round shapes). <paramref name="fallback"/> when no candidate exists (a point in a point or on a segment).</summary>
        private static float SatAxis(in Convex a, in Convex b, V3 fallback, out V3 axis)
        {
            axis = fallback;
            float best = float.MaxValue;
            _satAxes.Clear();
            Faces(a, _satAxes); Faces(b, _satAxes);
            _satEdgesA.Clear(); _satEdgesB.Clear();
            Edges(a, _satEdgesA); Edges(b, _satEdgesB);
            foreach (var ea in _satEdgesA) foreach (var eb in _satEdgesB) _satAxes.Add(Cross(ea, eb));
            if (a.Kind == 1 || b.Kind == 1)
            {
                // a segment against a point / another segment: the perpendicular between their closest points
                V3 v = b.Center - a.Center;
                if (v.Dot(v) < 1e-12f) v = new V3(0, 1, 0);
                GjkClosestPoints(a, b, 0f, float.MaxValue, ref v, out V3 pa, out V3 pb);
                _satAxes.Add(pb - pa);
            }
            foreach (var cand in _satAxes)
            {
                float len = cand.Len();
                if (len < 1e-6f) continue;
                V3 n = cand * (1f / len);
                float aMax = a.Support(n).Dot(n) + a.R, aMin = a.Support(n * -1f).Dot(n) - a.R;
                float bMax = b.Support(n).Dot(n) + b.R, bMin = b.Support(n * -1f).Dot(n) - b.R;
                float push = aMax - bMin, pull = bMax - aMin;   // B moved along +n / -n to clear A
                if (push <= pull) { if (push < best) { best = push; axis = n; } }
                else if (pull < best) { best = pull; axis = n * -1f; }
            }
            return best < float.MaxValue ? Math.Max(0f, best) : 0f;
        }

        private static readonly List<V3> _satAxes = new List<V3>(16), _satEdgesA = new List<V3>(3), _satEdgesB = new List<V3>(3);

        private static void Faces(in Convex c, List<V3> into)
        {
            if (c.Kind == 2) { into.Add(c.X); into.Add(c.Y); into.Add(c.Z); }
            else if (c.Kind == 3) into.Add(Cross(c.B - c.A, c.C - c.A));
        }

        private static void Edges(in Convex c, List<V3> into)
        {
            if (c.Kind == 1) into.Add(c.B - c.A);
            else if (c.Kind == 2) { into.Add(c.X); into.Add(c.Y); into.Add(c.Z); }
            else if (c.Kind == 3) { into.Add(c.B - c.A); into.Add(c.C - c.B); into.Add(c.A - c.C); }
        }

        // ------------------------------------------------------------------------------------------ GJK (Jolt's GJKClosestPoint)
        // A port of Jolt's GJKClosestPoint / ClosestPoint (MIT, Jorrit Rouwe — THIRD-PARTY-NOTICES.md lists Jolt) so the
        // collision world meets shapes exactly where the Jolt world does. Single-threaded, like the rest of this class.

        private const float FltEps = 1.1920929e-7f;
        /// <summary>Jolt's cDefaultCollisionTolerance.</summary>
        private const float GjkTolerance = 1.0e-4f;

        private static readonly V3[] _gY = new V3[4], _gP = new V3[4], _gQ = new V3[4];
        private static int _gN;

        /// <summary>Closest points of two convex cores (radii ignored). Returns the squared distance (0 = they intersect, then
        /// <paramref name="v"/> is zero), or float.MaxValue when farther apart than sqrt(<paramref name="maxDistSq"/>).
        /// <paramref name="v"/> in: a first guess of the separating axis; out: points from A to B.</summary>
        private static float GjkClosestPoints(in Convex a, in Convex b, float tolerance, float maxDistSq, ref V3 v, out V3 pointA, out V3 pointB)
        {
            float toleranceSq = tolerance * tolerance;
            _gN = 0;
            float vLenSq = v.Dot(v);
            float prevVLenSq = float.MaxValue;
            for (int iter = 0; iter < 64; iter++)
            {
                V3 p = a.Support(v), q = b.Support(v * -1f);
                V3 w = p - q;
                float dot = v.Dot(w);
                if (dot < 0f && dot * dot > vLenSq * maxDistSq) { pointA = pointB = default(V3); return float.MaxValue; }
                _gY[_gN] = w; _gP[_gN] = p; _gQ[_gN] = q; _gN++;
                if (!GetClosest(prevVLenSq, true, ref v, ref vLenSq, out uint set)) { _gN--; break; }
                if (set == 0xf) { v = new V3(0, 0, 0); vLenSq = 0f; break; }
                UpdatePointSet(set, true, true, true);
                if (vLenSq <= toleranceSq) { v = new V3(0, 0, 0); vLenSq = 0f; break; }
                if (vLenSq <= FltEps * MaxYLengthSq()) { v = new V3(0, 0, 0); vLenSq = 0f; break; }
                v = v * -1f;
                if (prevVLenSq - vLenSq <= FltEps * prevVLenSq) break;
                prevVLenSq = vLenSq;
            }
            PointsAAndB(out pointA, out pointB);
            return vLenSq;
        }

        /// <summary>Jolt's GJKClosestPoint::CastShape with convex radii: does core <paramref name="a"/> (radius a.R) moving
        /// along <paramref name="direction"/> × λ (λ in [0, <paramref name="lambda"/>)) meet core <paramref name="b"/>
        /// (radius b.R)? On a hit: λ, the contact points and the separating axis (A → B; near zero when they overlap at λ 0).</summary>
        private static bool GjkCast(in Convex a, in Convex b, V3 direction, float tolerance, ref float lambda,
            out V3 pointA, out V3 pointB, out V3 separatingAxis)
        {
            pointA = pointB = separatingAxis = default(V3);
            float toleranceSq = tolerance * tolerance;
            float sumRadius = a.R + b.R;
            _gN = 0;
            float l = 0f;
            V3 x = new V3(0, 0, 0);
            V3 v = b.Support(new V3(0, 0, 0)) * -1f + a.Support(new V3(0, 0, 0));
            float vLenSq = float.MaxValue;
            bool allowRestart = false;
            V3 prevV = new V3(0, 0, 0);
            for (int iter = 0; iter < 64; iter++)
            {
                V3 p = a.Support(v * -1f);
                V3 q = b.Support(v);
                V3 w = x - (q - p);
                float vDotW = v.Dot(w) - sumRadius * v.Len();
                if (vDotW > 0f)
                {
                    float vDotR = v.Dot(direction);
                    if (vDotR >= 0f) return false;
                    float oldL = l;
                    l -= vDotW / vDotR;
                    if (oldL == l) break;
                    if (l >= lambda) return false;
                    x = direction * l;
                    vLenSq = float.MaxValue;
                    toleranceSq = (tolerance + sumRadius) * (tolerance + sumRadius);
                    allowRestart = true;
                }
                _gP[_gN] = p; _gQ[_gN] = q; _gN++;
                for (int i = 0; i < _gN; i++) _gY[i] = x - (_gQ[i] - _gP[i]);
                if (!GetClosest(vLenSq, false, ref v, ref vLenSq, out uint set))
                {
                    if (!allowRestart) break;
                    allowRestart = false;
                    _gP[0] = p; _gQ[0] = q; _gN = 1;
                    v = x - q;
                    vLenSq = float.MaxValue;
                    continue;
                }
                if (set == 0xf) break;
                UpdatePointSet(set, false, true, true);
                if (vLenSq <= toleranceSq) break;
                prevV = v;
            }
            for (int i = 0; i < _gN; i++) _gY[i] = x - (_gQ[i] - _gP[i]);
            V3 nv = v.Norm();
            V3 ra = nv * a.R, rb = nv * b.R;
            switch (_gN)
            {
                case 1:
                    pointB = _gQ[0] + rb;
                    pointA = l > 0f ? pointB : _gP[0] - ra;
                    break;
                case 2:
                {
                    Bary(_gY[0], _gY[1], out float bu, out float bv);
                    pointB = _gQ[0] * bu + _gQ[1] * bv + rb;
                    pointA = l > 0f ? pointB : _gP[0] * bu + _gP[1] * bv - ra;
                    break;
                }
                default:
                {
                    Bary(_gY[0], _gY[1], _gY[2], out float bu, out float bv, out float bw);
                    pointB = _gQ[0] * bu + _gQ[1] * bv + _gQ[2] * bw + rb;
                    pointA = l > 0f ? pointB : _gP[0] * bu + _gP[1] * bv + _gP[2] * bw - ra;
                    break;
                }
            }
            separatingAxis = sumRadius > 0f ? v * -1f : prevV * -1f;
            lambda = l;
            return true;
        }

        private static float MaxYLengthSq()
        {
            float m = _gY[0].Dot(_gY[0]);
            for (int i = 1; i < _gN; i++) m = Math.Max(m, _gY[i].Dot(_gY[i]));
            return m;
        }

        private static void UpdatePointSet(uint set, bool y, bool p, bool q)
        {
            int n = 0;
            for (int i = 0; i < _gN; i++)
            {
                if ((set & (1u << i)) == 0) continue;
                if (y) _gY[n] = _gY[i];
                if (p) _gP[n] = _gP[i];
                if (q) _gQ[n] = _gQ[i];
                n++;
            }
            _gN = n;
        }

        private static void PointsAAndB(out V3 pa, out V3 pb)
        {
            switch (_gN)
            {
                case 1: pa = _gP[0]; pb = _gQ[0]; break;
                case 2:
                {
                    Bary(_gY[0], _gY[1], out float u, out float v);
                    pa = _gP[0] * u + _gP[1] * v; pb = _gQ[0] * u + _gQ[1] * v;
                    break;
                }
                case 3:
                {
                    Bary(_gY[0], _gY[1], _gY[2], out float u, out float v, out float w);
                    pa = _gP[0] * u + _gP[1] * v + _gP[2] * w; pb = _gQ[0] * u + _gQ[1] * v + _gQ[2] * w;
                    break;
                }
                default: pa = pb = default(V3); break;   // a full simplex: they intersect
            }
        }

        private static bool GetClosest(float prevVLenSq, bool lastPointPartOfClosestFeature, ref V3 v, ref float vLenSq, out uint set)
        {
            V3 r;
            switch (_gN)
            {
                case 1: set = 1; r = _gY[0]; break;
                case 2: r = ClosestOnLine(_gY[0], _gY[1], out set); break;
                case 3: r = ClosestOnTriangle(_gY[0], _gY[1], _gY[2], lastPointPartOfClosestFeature, out set); break;
                case 4: r = ClosestOnTetrahedron(_gY[0], _gY[1], _gY[2], _gY[3], lastPointPartOfClosestFeature, out set); break;
                default: set = 0; return false;
            }
            float l = r.Dot(r);
            if (l < prevVLenSq) { v = r; vLenSq = l; return true; }   // (false for NaN as well)
            return false;
        }

        private static bool Bary(V3 a, V3 b, out float u, out float v)
        {
            V3 ab = b - a;
            float den = ab.Dot(ab);
            if (den < FltEps * FltEps)
            {
                if (a.Dot(a) < b.Dot(b)) { u = 1f; v = 0f; } else { u = 0f; v = 1f; }
                return false;
            }
            v = -a.Dot(ab) / den;
            u = 1f - v;
            return true;
        }

        private static bool Bary(V3 a, V3 b, V3 c, out float u, out float v, out float w)
        {
            V3 v0 = b - a, v1 = c - a, v2 = c - b;
            float d00 = v0.Dot(v0), d11 = v1.Dot(v1), d22 = v2.Dot(v2);
            if (d00 <= d22)
            {
                float d01 = v0.Dot(v1);
                float den = d00 * d11 - d01 * d01;
                if (den < 1.0e-12f)
                {
                    if (d00 > d11) { Bary(a, b, out u, out v); w = 0f; }
                    else { Bary(a, c, out u, out w); v = 0f; }
                    return false;
                }
                float a0 = a.Dot(v0), a1 = a.Dot(v1);
                v = (d01 * a1 - d11 * a0) / den;
                w = (d01 * a0 - d00 * a1) / den;
                u = 1f - v - w;
            }
            else
            {
                float d12 = v1.Dot(v2);
                float den = d11 * d22 - d12 * d12;
                if (den < 1.0e-12f)
                {
                    if (d11 > d22) { Bary(a, c, out u, out w); v = 0f; }
                    else { Bary(b, c, out v, out w); u = 0f; }
                    return false;
                }
                float c1 = c.Dot(v1), c2 = c.Dot(v2);
                u = (d22 * c1 - d12 * c2) / den;
                v = (d11 * c2 - d12 * c1) / den;
                w = 1f - u - v;
            }
            return true;
        }

        private static V3 ClosestOnLine(V3 a, V3 b, out uint set)
        {
            Bary(a, b, out float u, out float v);
            if (v <= 0f) { set = 1; return a; }
            if (u <= 0f) { set = 2; return b; }
            set = 3;
            return a * u + b * v;
        }

        private static V3 ClosestOnTriangle(V3 inA, V3 inB, V3 inC, bool mustIncludeC, out uint set)
        {
            // a on the shortest edge (Jolt swaps a and c when bc is shorter than ac)
            bool swap = (inC - inB).Dot(inC - inB) < (inC - inA).Dot(inC - inA);
            V3 a = swap ? inC : inA, c = swap ? inA : inC;
            V3 ab = inB - a, ac = c - a;
            V3 n = Cross(ab, ac);
            float nLenSq = n.Dot(n);
            if (nLenSq < 1.0e-10f)
            {
                // degenerate: vertices and edges
                uint closestSet = 4; V3 closest = inC; float best = inC.Dot(inC);
                if (!mustIncludeC)
                {
                    float la = inA.Dot(inA); if (la < best) { closestSet = 1; closest = inA; best = la; }
                    float lb = inB.Dot(inB); if (lb < best) { closestSet = 2; closest = inB; best = lb; }
                }
                float acLen = ac.Dot(ac);
                if (acLen > FltEps * FltEps)
                {
                    float t = Clamp(-a.Dot(ac) / acLen, 0f, 1f); V3 qq = a + ac * t; float dd = qq.Dot(qq);
                    if (dd < best) { closestSet = 5; closest = qq; best = dd; }
                }
                V3 bc = inC - inB; float bcLen = bc.Dot(bc);
                if (bcLen > FltEps * FltEps)
                {
                    float t = Clamp(-inB.Dot(bc) / bcLen, 0f, 1f); V3 qq = inB + bc * t; float dd = qq.Dot(qq);
                    if (dd < best) { closestSet = 6; closest = qq; best = dd; }
                }
                if (!mustIncludeC)
                {
                    V3 ab2 = inB - inA; float abLen = ab2.Dot(ab2);
                    if (abLen > FltEps * FltEps)
                    {
                        float t = Clamp(-inA.Dot(ab2) / abLen, 0f, 1f); V3 qq = inA + ab2 * t; float dd = qq.Dot(qq);
                        if (dd < best) { closestSet = 3; closest = qq; best = dd; }
                    }
                }
                set = closestSet;
                return closest;
            }
            V3 ap = a * -1f;
            float d1 = ab.Dot(ap), d2 = ac.Dot(ap);
            if (d1 <= 0f && d2 <= 0f) { set = swap ? 4u : 1u; return a; }
            V3 bp = inB * -1f;
            float d3 = ab.Dot(bp), d4 = ac.Dot(bp);
            if (d3 >= 0f && d4 <= d3) { set = 2; return inB; }
            if (d1 * d4 <= d3 * d2 && d1 >= 0f && d3 <= 0f)
            {
                float vv = d1 / (d1 - d3);
                set = swap ? 6u : 3u;
                return a + ab * vv;
            }
            V3 cp = c * -1f;
            float d5 = ab.Dot(cp), d6 = ac.Dot(cp);
            if (d6 >= 0f && d5 <= d6) { set = swap ? 1u : 4u; return c; }
            if (d5 * d2 <= d1 * d6 && d2 >= 0f && d6 <= 0f)
            {
                float ww = d2 / (d2 - d6);
                set = 5;
                return a + ac * ww;
            }
            float d4d3 = d4 - d3, d5d6 = d5 - d6;
            if (d3 * d6 <= d5 * d4 && d4d3 >= 0f && d5d6 >= 0f)
            {
                float ww = d4d3 / (d4d3 + d5d6);
                set = swap ? 3u : 6u;
                return inB + (c - inB) * ww;
            }
            set = 7;
            return n * ((a + inB + c).Dot(n) / (3f * nLenSq));
        }

        private static void OutsideOfTetrahedronPlanes(V3 a, V3 b, V3 c, V3 d, out bool p0, out bool p1, out bool p2, out bool p3)
        {
            V3 ab = b - a, ac = c - a, ad = d - a, bd = d - b, bc = c - b;
            V3 abXac = Cross(ab, ac), acXad = Cross(ac, ad), adXab = Cross(ad, ab), bdXbc = Cross(bd, bc);
            float s0 = a.Dot(abXac), s1 = a.Dot(acXad), s2 = a.Dot(adXab), s3 = b.Dot(bdXbc);
            float e0 = ad.Dot(abXac), e1 = ab.Dot(acXad), e2 = ac.Dot(adXab), e3 = -ab.Dot(bdXbc);
            int signs = (e0 < 0f ? 1 : 0) | (e1 < 0f ? 2 : 0) | (e2 < 0f ? 4 : 0) | (e3 < 0f ? 8 : 0);
            if (signs == 0) { p0 = s0 >= -FltEps; p1 = s1 >= -FltEps; p2 = s2 >= -FltEps; p3 = s3 >= -FltEps; }
            else if (signs == 0xf) { p0 = s0 <= FltEps; p1 = s1 <= FltEps; p2 = s2 <= FltEps; p3 = s3 <= FltEps; }
            else { p0 = p1 = p2 = p3 = true; }   // degenerate tetrahedron
        }

        private static V3 ClosestOnTetrahedron(V3 a, V3 b, V3 c, V3 d, bool mustIncludeD, out uint set)
        {
            uint closestSet = 0xf;
            V3 closest = new V3(0, 0, 0);
            float best = float.MaxValue;
            OutsideOfTetrahedronPlanes(a, b, c, d, out bool o0, out bool o1, out bool o2, out bool o3);
            if (o0)
            {
                if (mustIncludeD) { closestSet = 1; closest = a; }
                else closest = ClosestOnTriangle(a, b, c, false, out closestSet);
                best = closest.Dot(closest);
            }
            if (o1)
            {
                V3 q = ClosestOnTriangle(a, c, d, mustIncludeD, out uint s);
                float dd = q.Dot(q);
                if (dd < best) { best = dd; closest = q; closestSet = (s & 1u) + ((s & 6u) << 1); }
            }
            if (o2)
            {
                V3 q = ClosestOnTriangle(a, b, d, mustIncludeD, out uint s);
                float dd = q.Dot(q);
                if (dd < best) { best = dd; closest = q; closestSet = (s & 3u) + ((s & 4u) << 1); }
            }
            if (o3)
            {
                V3 q = ClosestOnTriangle(b, c, d, mustIncludeD, out uint s);
                float dd = q.Dot(q);
                if (dd < best) { closest = q; closestSet = s << 1; }
            }
            set = closestSet;
            return closest;
        }
    }
}
