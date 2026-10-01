using System;
using System.Runtime.InteropServices;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>Navmesh bake parameters — mirrors the C struct <c>NavBakeSettings</c> (VortexAPI/Api/NavigationApi.cpp).
    /// Metres / degrees; "cells" are voxels of <see cref="CellSize"/> (xz) and <see cref="CellHeight"/> (y).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NavBakeSettings
    {
        public float CellSize, CellHeight, AgentHeight, AgentRadius, AgentMaxClimb, AgentMaxSlope;
        public float RegionMinSize, RegionMergeSize, EdgeMaxLen, EdgeMaxError, DetailSampleDist, DetailSampleMaxError;
        public int VertsPerPoly, TileSize, Partition, FilterFlags;
        public float BoundsMinX, BoundsMinY, BoundsMinZ, BoundsMaxX, BoundsMaxY, BoundsMaxZ;
        /// <summary>Opaque to the engine, stored with the navmesh: the editor's geometry-source flags.</summary>
        public int SourceFlags;
    }

    /// <summary>What a bake produced / what the loaded navmesh contains — mirrors <c>NavBakeStats</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NavBakeStats
    {
        public int TilesX, TilesZ, TileCount, PolyCount, VertCount, DetailTriCount, InputTriangles, InputBoxes;
        public float BakeMs;
        public float BoundsMinX, BoundsMinY, BoundsMinZ, BoundsMaxX, BoundsMaxY, BoundsMaxZ;
        public int DataSize;
    }

    /// <summary>Crowd agent parameters — mirrors <c>NavAgentParams</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NavAgentParams
    {
        public float Radius, Height, MaxSpeed, MaxAcceleration, SeparationWeight, CollisionQueryRange, PathOptimizationRange;
        /// <summary>0 low .. 3 high obstacle-avoidance sampling.</summary>
        public int AvoidanceQuality;
        /// <summary>DetourCrowd update flags; 0 = the default set.</summary>
        public uint UpdateFlags;
    }

    /// <summary>A crowd agent's state — mirrors <c>NavAgentState</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NavAgentState
    {
        public float PosX, PosY, PosZ;
        public float VelX, VelY, VelZ;
        public float DesiredVelX, DesiredVelY, DesiredVelZ;
        public float TargetX, TargetY, TargetZ;
        public float CornerX, CornerY, CornerZ;
        /// <summary>Along the path to the target (m); -1 = no path.</summary>
        public float RemainingDistance;
        /// <summary>0 idle, 1 pending, 2 moving, 3 failed, 4 velocity control.</summary>
        public int MoveState;
        public int Partial, OnNavMesh, CornerCount;
    }

    /// <summary>
    /// AI &amp; Navigation native entry points (Recast/Detour, issues #108-#110) — P/Invoke declarations for every function
    /// of <c>VortexAPI/Api/NavigationApi.cpp</c>. Positions are float triples (world space, Y up), <c>extents</c> the half
    /// extents of the box that finds the closest navmesh polygon (null = (2, 4, 2)), agent handles <c>uint</c> (0 =
    /// invalid). Gameplay code goes through <see cref="Editor.Core.Services.AI.NavigationService"/>, which degrades
    /// gracefully when the engine was built without Recast (see <see cref="NavigationNative"/>).
    /// </summary>
    public static partial class VortexAPI
    {
        #region Navigation — world + bake

        /// <summary>1 = Recast/Detour compiled in, 0 = stub build. Throws EntryPointNotFound on an older library.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavInit();

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void NavGetDefaultSettings(out NavBakeSettings settings);

        /// <summary>Bake world-space geometry (triangle soup + solid boxes of 10 floats: centre, half extents, quaternion
        /// xyzw). Areas: 0 obstacle, 1..63 walkable by slope, null = all ground. Returns the .vnav size (&gt; 0, fetch it with
        /// <see cref="NavGetBakeResult"/>) or an error: -1 no geometry, -2 bad settings, -3 cancelled, -4 failed,
        /// -5 nothing walkable, -6 stub, -7 too many tiles, -8 busy. Thread-safe (never touches the loaded navmesh).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavBake(float[] verts, int vertCount, int[] tris, int triCount, byte[] triAreas,
            float[] boxes, int boxCount, byte[] boxAreas, ref NavBakeSettings settings, out NavBakeStats stats);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern float NavGetBakeProgress();

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void NavCancelBake();

        /// <summary>Copies the last bake result; <paramref name="output"/> null = returns the size.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavGetBakeResult([In, Out] byte[] output, int maxBytes);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void NavClearBakeResult();

        /// <summary>Replaces the loaded navmesh (every agent is dropped). 0 = rejected (not a .vnav / wrong version).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavLoad(byte[] data, int size);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void NavUnload();

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavIsLoaded();

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavGetInfo(out NavBakeSettings settings, out NavBakeStats stats);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavGetData([In, Out] byte[] output, int maxBytes);

        #endregion

        #region Navigation — queries

        /// <summary>Straight corner path (start and end included). Returns the point count; status 0 invalid, 1 complete,
        /// 2 partial (the end is unreachable, the path leads to the closest point).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavFindPath(float[] start, float[] end, float[] extents, [In, Out] float[] outPoints, int maxPoints, out int status);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavClosestPoint(float[] pos, float[] extents, [In, Out] float[] outPoint);

        /// <summary>1 = the navmesh boundary blocks the way from start to end (hit / normal / t describe where).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavRaycast(float[] start, float[] end, float[] extents, [In, Out] float[] outHit, [In, Out] float[] outNormal, out float t);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavRandomPoint([In, Out] float[] outPoint);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavRandomPointAround(float[] center, float radius, float[] extents, [In, Out] float[] outPoint);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void NavSetRandomSeed(uint seed);

        /// <summary>Detail triangles of the navmesh, 9 floats each; null buffer = the float count.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavGetDebugTriangles([In, Out] float[] output, int maxFloats);

        /// <summary>Polygon edges, 6 floats per segment; flags bit0 boundary, bit1 internal edges. Null buffer = count.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavGetDebugLines([In, Out] float[] output, int maxFloats, int flags);

        /// <summary>Engine mesh of the navmesh surface lifted by <paramref name="yOffset"/> (release with DestroyMesh);
        /// ID.INVALID_ID without a navmesh or renderer.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern long NavCreateDebugMesh(float yOffset);

        #endregion

        #region Navigation — crowd agents

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern uint NavAgentAdd(float[] pos, ref NavAgentParams parameters);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void NavAgentRemove(uint agent);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavAgentValid(uint agent);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavAgentSetParams(uint agent, ref NavAgentParams parameters);

        /// <summary>Asynchronous: the path is computed during the next crowd updates.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavAgentSetTarget(uint agent, float[] target);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavAgentResetTarget(uint agent);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavAgentSetVelocity(uint agent, float[] velocity);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavAgentTeleport(uint agent, float[] pos);

        /// <summary>Nudge the agent along the navmesh surface (collision correction); keeps the path.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavAgentMovePosition(uint agent, float[] pos);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavAgentGetState(uint agent, out NavAgentState state);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavAgentGetCorners(uint agent, [In, Out] float[] outPoints, int maxPoints);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int NavGetAgentCount();

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void NavCrowdUpdate(float dt);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void NavCrowdClear();

        #endregion

        #region Navigation — debug drawing

        private static readonly System.Collections.Generic.Dictionary<int, long> _navLineMaterials = new System.Collections.Generic.Dictionary<int, long>();

        /// <summary>An unlit gizmo material of the given colour (cached per colour) for navigation overlays.</summary>
        public static long NavigationGizmoMaterial(float r, float g, float b)
        {
            int key = ((int)(Math.Max(0f, Math.Min(1f, r)) * 255f) << 16) | ((int)(Math.Max(0f, Math.Min(1f, g)) * 255f) << 8) | (int)(Math.Max(0f, Math.Min(1f, b)) * 255f);
            if (_navLineMaterials.TryGetValue(key, out long mat)) return mat;
            if (!_gizmosInitialized) InitializeGizmos();
            mat = MakeUnlitMaterial(r, g, b);
            if (mat != ID.INVALID_ID) _navLineMaterials[key] = mat;
            return mat;
        }

        /// <summary>Draw line segments (6 floats each: x0 y0 z0 x1 y1 z1) as thin always-on-top boxes through the gizmo
        /// pass, in one colour — path lines, patrol routes, perception cones. The gizmo pass holds 512 items per frame
        /// (shared with every other gizmo), so keep <paramref name="maxSegments"/> small; the navmesh surface itself is one
        /// mesh item (<see cref="NavCreateDebugMesh"/>).</summary>
        public static void RenderNavigationLines(float[] segments, int floatCount, float r, float g, float b, float thickness = 0.02f, int maxSegments = 160)
        {
            if (segments == null || floatCount < 6) return;
            if (!_gizmosInitialized) InitializeGizmos();
            if (_gizmoCube == ID.INVALID_ID) return;
            long mat = NavigationGizmoMaterial(r, g, b);
            if (mat == ID.INVALID_ID) return;
            int segs = Math.Min(Math.Min(floatCount, segments.Length) / 6, maxSegments);
            var m = new float[16];
            for (int i = 0; i < segs; i++)
            {
                int o = i * 6;
                float ax = segments[o], ay = segments[o + 1], az = segments[o + 2];
                float bx = segments[o + 3], by = segments[o + 4], bz = segments[o + 5];
                float dx = bx - ax, dy = by - ay, dz = bz - az;
                float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len < 1e-5f) continue;
                float ix = dx / len, iy = dy / len, iz = dz / len;
                float ux = 0f, uy = 1f, uz = 0f;
                if (Math.Abs(iy) > 0.99f) { uy = 0f; uz = 1f; }
                float rx = uy * iz - uz * iy, ry = uz * ix - ux * iz, rz = ux * iy - uy * ix;
                float rl = (float)Math.Sqrt(rx * rx + ry * ry + rz * rz);
                if (rl < 1e-6f) continue;
                rx /= rl; ry /= rl; rz /= rl;
                float upx = iy * rz - iz * ry, upy = iz * rx - ix * rz, upz = ix * ry - iy * rx;
                m[0] = rx * thickness; m[1] = ry * thickness; m[2] = rz * thickness; m[3] = 0f;
                m[4] = upx * thickness; m[5] = upy * thickness; m[6] = upz * thickness; m[7] = 0f;
                m[8] = ix * len; m[9] = iy * len; m[10] = iz * len; m[11] = 0f;
                m[12] = (ax + bx) * 0.5f; m[13] = (ay + by) * 0.5f; m[14] = (az + bz) * 0.5f; m[15] = 1f;
                SubmitGizmoWireForRendering(_gizmoCube, mat, m);
            }
        }

        #endregion
    }

    /// <summary>
    /// Availability gate for the native navigation module. <see cref="Available"/> calls <c>NavInit()</c> once and
    /// remembers the answer: a stub build (Windows .vcxproj without Recast) returns 0, an engine library that predates the
    /// navigation API throws <see cref="EntryPointNotFoundException"/>, a missing library <see cref="DllNotFoundException"/>
    /// — all read as "not available" (bakes fail with a clear message, agents stand still). <see cref="Reason"/> says why.
    /// </summary>
    public static class NavigationNative
    {
        private static int _state;   // 0 = not probed, 1 = available, -1 = unavailable

        public static bool Available
        {
            get
            {
                if (_state == 0) Probe();
                return _state > 0;
            }
        }

        /// <summary>Why navigation is unavailable ("" when it is).</summary>
        public static string Reason { get; private set; } = "";

        private static void Probe()
        {
            try
            {
                int r = VortexAPI.NavInit();
                _state = r != 0 ? 1 : -1;
                Reason = r != 0 ? "" : "engine built without Recast/Detour (stub NavInit returned 0)";
            }
            catch (EntryPointNotFoundException)
            {
                _state = -1;
                Reason = "engine library has no navigation entry points (rebuild the native engine)";
            }
            catch (DllNotFoundException)
            {
                _state = -1;
                Reason = "engine library not found";
            }
            catch (Exception ex)
            {
                _state = -1;
                Reason = "NavInit failed: " + ex.Message;
            }
        }

        /// <summary>Forget the cached probe (tests / after swapping the native library).</summary>
        public static void ResetProbe() { _state = 0; Reason = ""; }
    }
}
