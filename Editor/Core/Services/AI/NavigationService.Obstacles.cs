using System;
using System.Collections.Generic;
using System.Diagnostics;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.AI;
using SysVec = System.Numerics.Vector3;
using SysQuat = System.Numerics.Quaternion;
using SysMat = System.Numerics.Matrix4x4;

namespace Editor.Core.Services.AI
{
    // Dynamic navmesh obstacles (GitHub #435): doors, parked cars, rubble, destroyed walls change where agents can go in play.
    //
    // Why tile re-bakes and not Detour's TileCache: the TileCache needs a second bake pipeline and its own file of compressed
    // heightfield layers next to the .vnav (every scene rebaked once more after #517), drops the bake's detail mesh and
    // watershed regions, cannot keep the per-tile height windows of tall levels (#451) and only cuts boxes / cylinders. Here a
    // tile is rebuilt with the bake's own code (byte for byte the baked tile when nothing carves it), from the geometry the
    // navmesh was baked from, so any obstacle shape and destruction (Navigation.Rebake) take the same path, and a door's
    // open / shut is a flag flip on its own polygons with no rebuild at all. The cost: the scene geometry is gathered once
    // (when the first carving obstacle appears) and kept natively while the scene runs; the builds run on a worker thread
    // and the game thread only swaps finished tiles in, within RebuildBudgetMs a frame.
    public static partial class NavigationService
    {
        // NavigationWorld.h: obstacle_carve / obstacle_avoid / obstacle_active
        private const uint ObstacleCarveFlag = 1u, ObstacleAvoidFlag = 2u, ObstacleActiveFlag = 4u;

        /// <summary>Ids of script obstacles (<c>Navigation.AddObstacle</c>) start here — above every entity handle.</summary>
        public const long ScriptObstacleBase = 1L << 40;

        // A script obstacle follows the rules of a Nav Mesh Obstacle with its defaults: Carve Only Stationary, Move Threshold
        // 0.1 m, Time To Stationary 0.5 s (moved every frame, it is steered around until it stands still).
        private const float ScriptMoveThreshold = 0.1f, ScriptTimeToStationary = 0.5f;
        // Milliseconds a frame for gathering the scene geometry the rebuilds start from (the first carving obstacle, Rebake).
        private const double GatherBudgetMs = 3.0;

        private sealed class ObstacleRec
        {
            public GameEntity Entity;            // null for a script obstacle
            public NavMeshObstacle Component;
            public long Id;                      // a script obstacle's id
            public uint Handle;
            public int Shape;                    // 0 box, 1 cylinder
            public SysVec Center, Half;          // world space, now
            public SysQuat Rotation = SysQuat.Identity;
            public uint SentFlags;
            public SysVec SentCenter, SentHalf;
            public SysQuat SentRotation = SysQuat.Identity;
            public bool Sent, SentMoving;
            public SysVec CarvedCenter, CarvedHalf;   // where it carves (Move Threshold is measured from there)
            public SysQuat CarvedRotation = SysQuat.Identity;
            public bool Carving;
            public SysVec RestCenter, RestHalf;  // where it last came to rest: moving = more than Move Threshold away from it
            public SysQuat RestRotation = SysQuat.Identity;
            public bool HasRest;
            public float Still = float.MaxValue; // seconds it has been standing; it starts out standing
            public SysVec LastCenter;            // at the last frame (its velocity, for the agents that steer around it)
            public bool HasLast;
            public bool? Enabled;                // Navigation.SetObstacleEnabled — null: the component decides
            public bool EnabledSeen;             // the component's own switch when that was set (switching it drops the override)
            public bool ScriptCarve = true;
        }

        private static readonly Dictionary<GameEntity, ObstacleRec> _obstacles = new Dictionary<GameEntity, ObstacleRec>();
        private static readonly Dictionary<long, ObstacleRec> _scriptObstacles = new Dictionary<long, ObstacleRec>();
        private static readonly List<ObstacleRec> _obstacleList = new List<ObstacleRec>();
        private static long _nextScriptObstacle = ScriptObstacleBase;
        private static bool _sourceSet, _sourceFailed, _obstaclesUnavailable, _sceneChangedWarned;
        private static readonly float[] _oc = new float[3], _oh = new float[3], _oq = new float[4], _ov = new float[3];
        private static float _rebuildBudgetMs = DefaultRebuildBudgetMs;
        private const float DefaultRebuildBudgetMs = 2f;
        // the scene geometry being gathered for the rebuilds (null = none), a few ms a frame
        private static GeometryBuilder _gather;
        private static bool _gatherRebake;
        private static double _gatherMs;
        private static readonly List<(SysVec min, SysVec max)> _rebakeBoxes = new List<(SysVec, SysVec)>();

        /// <summary>Game-thread milliseconds a frame may spend swapping rebuilt navmesh tiles in (one tile a frame at least;
        /// the builds themselves run on a worker thread). Default 2; back to it when play stops.</summary>
        public static float RebuildBudgetMs
        {
            get => _rebuildBudgetMs;
            set => _rebuildBudgetMs = float.IsNaN(value) ? DefaultRebuildBudgetMs : Math.Max(0f, Math.Min(100f, value));
        }

        /// <summary>Obstacles the running game knows (Nav Mesh Obstacle components and script obstacles).</summary>
        public static int ObstacleCount => _obstacleList.Count;

        /// <summary>Game-thread milliseconds the last gathering of the scene geometry took in all (spread over frames,
        /// <c>GatherBudgetMs</c> each); 0 before the first carving obstacle.</summary>
        public static float SourceGatherMs { get; private set; }

        /// <summary>True while the scene geometry for the rebuilds is being gathered (a few milliseconds a frame).</summary>
        public static bool GatheringSource => _gather != null;

        /// <summary>Navmesh tiles waiting for a rebuild, being rebuilt or waiting for the swap (and 1 while the scene geometry
        /// they start from is being gathered).</summary>
        public static int PendingRebuilds
        {
            get
            {
                if (!Available || _obstaclesUnavailable) return 0;
                int n = _gather != null ? 1 : 0;
                try { return n + VortexAPI.NavRebuildPending(); } catch { return n; }
            }
        }

        /// <summary>What the runtime rebuilds did so far (tiles built / swapped, worker and game-thread times, source size).</summary>
        public static NavRebuildStats GetRebuildStats()
        {
            var st = default(NavRebuildStats);
            if (!Available || _obstaclesUnavailable) return st;
            try { VortexAPI.NavGetRebuildStats(out st); } catch { }
            return st;
        }

        // ---------------------------------------------------------------- script obstacles

        /// <summary>A script obstacle: a box (<paramref name="halfExtents"/>, turned by <paramref name="rotation"/>) or, with
        /// <paramref name="cylinder"/>, an upright cylinder (halfExtents.X = radius, .Y = half height). Carving cuts it out of the
        /// navmesh — while it is moved it is only steered around, and it is carved where it stands still for half a second
        /// (moved less than 0.1 m); otherwise agents steer around it. Returns its id (0 when there is no running navigation).</summary>
        public static long AddObstacle(SysVec center, SysVec halfExtents, SysQuat rotation, bool cylinder, bool carve)
        {
            if (!IsRunning || !Available || _obstaclesUnavailable) return 0;
            if (!Finite(center) || !Finite(halfExtents)) return 0;
            var r = new ObstacleRec
            {
                Id = ++_nextScriptObstacle,
                Shape = cylinder ? 1 : 0,
                Center = center,
                Half = new SysVec(Math.Max(0.001f, Math.Abs(halfExtents.X)), Math.Max(0.001f, Math.Abs(halfExtents.Y)), Math.Max(0.001f, Math.Abs(halfExtents.Z))),
                Rotation = cylinder ? SysQuat.Identity : SafeRotation(rotation),
                ScriptCarve = carve
            };
            _scriptObstacles[r.Id] = r;
            _obstacleList.Add(r);
            SyncObstacle(r, 0f);
            return r.Id;
        }

        /// <summary>Move a script obstacle. Moved more than 0.1 m from where it stood, it is steered around until it stands
        /// still again for half a second; then it is carved where it is (its tiles rebuilt).</summary>
        public static bool MoveObstacle(long id, SysVec center, SysQuat rotation)
        {
            if (!_scriptObstacles.TryGetValue(id, out var r) || !Finite(center)) return false;
            r.Center = center;
            if (r.Shape == 0) r.Rotation = SafeRotation(rotation);
            SyncObstacle(r, 0f);
            return true;
        }

        /// <summary>Remove a script obstacle; its tiles are rebuilt without it (its polygons open at once).</summary>
        public static void RemoveObstacle(long id)
        {
            if (!_scriptObstacles.TryGetValue(id, out var r)) return;
            _scriptObstacles.Remove(id);
            _obstacleList.Remove(r);
            Release(r);
        }

        /// <summary>Switch an obstacle on or off for this play session — an entity's Nav Mesh Obstacle (the component is not
        /// changed; switching the component later takes over again) or a script obstacle. A carving one blocks / frees its
        /// polygons at once (a door).</summary>
        public static void SetObstacleEnabled(GameEntity entity, long scriptId, bool enabled)
        {
            var r = FindObstacle(entity, scriptId, true);
            if (r == null) return;
            r.Enabled = enabled;
            r.EnabledSeen = r.Component != null && r.Component.IsEnabled;
            SyncObstacle(r, 0f);
        }

        /// <summary>Is the obstacle switched on (its runtime setting, else the component and its entity)?</summary>
        public static bool IsObstacleEnabled(GameEntity entity, long scriptId)
        {
            var r = FindObstacle(entity, scriptId, false);
            return r != null && Active(r);
        }

        /// <summary>Is the obstacle in the navmesh as it is now: carved where it stands (its tiles rebuilt) or, not carving or
        /// while it moves, steered around?</summary>
        public static bool IsObstacleReady(GameEntity entity, long scriptId)
        {
            var r = FindObstacle(entity, scriptId, false);
            if (r == null || r.Handle == 0) return false;
            if (r.Carving && !_sourceSet) return false;   // its tiles wait for the scene geometry
            try { return VortexAPI.NavObstacleReady(r.Handle) != 0; } catch { return false; }
        }

        /// <summary>Re-bake the navmesh inside a box from the scene as it is now — after a wall was blown away or rubble fell.
        /// The scene geometry is gathered again over the next frames (a few milliseconds each), then the tiles over the box
        /// are rebuilt on the worker thread. False without a running navmesh.</summary>
        public static bool Rebake(SysVec center, SysVec halfExtents)
        {
            if (!IsRunning || !IsLoaded || _obstaclesUnavailable || !Finite(center) || !Finite(halfExtents)) return false;
            var h = new SysVec(Math.Abs(halfExtents.X), Math.Abs(halfExtents.Y), Math.Abs(halfExtents.Z));
            _rebakeBoxes.Add((center - h, center + h));
            StartGather(true);   // (a gather under way starts over: the scene changed)
            return true;
        }

        /// <summary>Tests and tools: finish gathering the scene geometry now and wait (up to <paramref name="timeoutMs"/>)
        /// until every rebuild is in the navmesh.</summary>
        public static bool FlushRebuilds(float timeoutMs = 2000f)
        {
            if (!Available || _obstaclesUnavailable) return true;
            if (_gather != null) StepGather(double.MaxValue);
            try { return VortexAPI.NavRebuildFlush(timeoutMs) != 0; } catch { return true; }
        }

        // ---------------------------------------------------------------- per frame

        /// <summary>Find every Nav Mesh Obstacle of the run scene (runtime spawns too) and forget removed ones.</summary>
        private static void RescanObstacles()
        {
            if (_runScene?.Entities == null || _obstaclesUnavailable) return;
            var seen = new HashSet<GameEntity>();
            void Walk(GameEntity e)
            {
                if (e == null) return;
                var o = e.GetComponent<NavMeshObstacle>();
                if (o != null)
                {
                    seen.Add(e);
                    if (!_obstacles.ContainsKey(e))
                    {
                        var r = new ObstacleRec { Entity = e, Component = o };
                        _obstacles[e] = r;
                        _obstacleList.Add(r);
                    }
                }
                if (e.Children != null) foreach (var c in e.Children) Walk(c);
            }
            foreach (var root in _runScene.Entities) Walk(root);
            for (int i = _obstacleList.Count - 1; i >= 0; i--)
            {
                var r = _obstacleList[i];
                if (r.Entity == null || seen.Contains(r.Entity)) continue;
                _obstacles.Remove(r.Entity);
                _obstacleList.RemoveAt(i);
                Release(r);
            }
        }

        /// <summary>One frame, before the crowd: the scene geometry gathered on (a few ms), obstacle poses and switches to
        /// the navmesh, finished tile rebuilds in.</summary>
        private static void UpdateObstacles(float dt)
        {
            if (_obstaclesUnavailable || !IsLoaded) return;
            if (_gather != null) StepGather(GatherBudgetMs);
            for (int i = 0; i < _obstacleList.Count; i++) SyncObstacle(_obstacleList[i], dt);
            if (_obstacleList.Count == 0 && !_sourceSet && _gather == null) return;   // nothing changes the navmesh
            try { VortexAPI.NavRebuildUpdate(_rebuildBudgetMs); }
            catch (EntryPointNotFoundException) { ObstaclesUnavailable(); }
        }

        private static void SyncObstacle(ObstacleRec r, float dt)
        {
            if (_obstaclesUnavailable || !IsLoaded) return;
            bool component = r.Entity != null;
            if (component)
            {
                if (r.Component == null || r.Entity == null) return;
                PoseOf(r);
            }
            bool active = Active(r);
            float threshold = component ? r.Component.MoveThreshold : ScriptMoveThreshold;
            float stationaryAfter = component ? r.Component.TimeToStationary : ScriptTimeToStationary;
            bool stationaryOnly = !component || r.Component.CarveOnlyStationary;

            // standing or moving: measured from where it last came to rest — a jitter under Move Threshold (a physics prop
            // settling) keeps it standing, more starts it moving (it stands again Time To Stationary after its last such move)
            if (!r.HasRest || Displacement(r, r.RestCenter, r.RestRotation, r.RestHalf) > threshold)
            {
                if (r.HasRest) r.Still = 0f;   // (the first pose: it starts out standing)
                r.RestCenter = r.Center; r.RestRotation = r.Rotation; r.RestHalf = r.Half; r.HasRest = true;
            }
            else if (dt > 0f && r.Still < float.MaxValue) r.Still += dt;
            var velocity = SysVec.Zero;
            if (dt > 0f)
            {
                if (r.HasLast && dt > 1e-5f) velocity = (r.Center - r.LastCenter) / dt;
                r.LastCenter = r.Center;
                r.HasLast = true;
            }

            uint flags;
            bool wantsCarve = component ? r.Component.Carve : r.ScriptCarve;
            if (!wantsCarve) flags = active ? ObstacleAvoidFlag | ObstacleActiveFlag : 0u;
            else
            {
                // A carving obstacle is cut out where it stands even while switched off (its polygons free then): switching it
                // on is a flag flip, no rebuild — a door that starts open shuts at once. Moving more than Move Threshold from
                // there carves it again where it is now, or — Carve Only Stationary — makes it an avoided one until it stood
                // still for Time To Stationary (no tile rebuilds every frame for a pushed cart).
                bool movedAway = !r.Carving || Displacement(r, r.CarvedCenter, r.CarvedRotation, r.CarvedHalf) > threshold
                    || (r.Half - r.CarvedHalf).Length() > threshold;
                if (movedAway && (!stationaryOnly || r.Still >= stationaryAfter))
                {
                    r.CarvedCenter = r.Center; r.CarvedRotation = r.Rotation; r.CarvedHalf = r.Half;
                    r.Carving = true;
                }
                else if (movedAway) r.Carving = false;
                if (r.Carving)
                {
                    flags = ObstacleCarveFlag | (active ? ObstacleActiveFlag : 0u);
                    if (!_sourceSet && _gather == null && !_sourceFailed) StartGather(false);   // the first carving obstacle
                }
                else flags = active ? ObstacleAvoidFlag | ObstacleActiveFlag : 0u;
            }

            // the pose the native side gets: the carve pose while carving (a door nudged by less than its threshold
            // rebuilds nothing), the current pose otherwise
            SysVec c = r.Center, h = r.Half;
            SysQuat q = r.Rotation;
            if ((flags & ObstacleCarveFlag) != 0) { c = r.CarvedCenter; h = r.CarvedHalf; q = r.CarvedRotation; }
            bool poseChanged = !r.Sent || (c - r.SentCenter).LengthSquared() > 1e-10f || (h - r.SentHalf).LengthSquared() > 1e-10f
                || Math.Abs(SysQuat.Dot(q, r.SentRotation)) < 1f - 1e-7f;
            bool avoiding = (flags & ObstacleAvoidFlag) != 0;
            bool moving = avoiding && velocity.LengthSquared() > 1e-8f;
            if (r.Handle != 0 && !poseChanged && flags == r.SentFlags && !moving && !r.SentMoving) return;

            Fill(_oc, c); Fill(_oh, h);
            _oq[0] = q.X; _oq[1] = q.Y; _oq[2] = q.Z; _oq[3] = q.W;
            Fill(_ov, avoiding ? velocity : SysVec.Zero);
            try
            {
                if (r.Handle == 0 || VortexAPI.NavObstacleValid(r.Handle) == 0)
                {
                    if (flags == 0u) return;   // never needed yet
                    r.Handle = VortexAPI.NavObstacleAdd(r.Shape, _oc, _oh, _oq, flags);
                    if (r.Handle != 0 && avoiding) VortexAPI.NavObstacleSet(r.Handle, _oc, _oh, _oq, flags, _ov);
                }
                else VortexAPI.NavObstacleSet(r.Handle, _oc, _oh, _oq, flags, _ov);
            }
            catch (EntryPointNotFoundException) { ObstaclesUnavailable(); return; }
            if (r.Handle == 0) return;
            r.Sent = true;
            r.SentMoving = moving;
            r.SentFlags = flags;
            r.SentCenter = c; r.SentHalf = h; r.SentRotation = q;
        }

        /// <summary>On: a script obstacle unless switched off; an entity's while its entity is active in the run scene and —
        /// the runtime switch (<see cref="SetObstacleEnabled"/>) first, until the component itself is switched — its component
        /// is enabled.</summary>
        private static bool Active(ObstacleRec r)
        {
            if (r.Entity == null) return r.Enabled != false;
            if (r.Component == null || !r.Entity.ActiveInHierarchy || !InRunScene(r.Entity)) return false;
            if (r.Enabled.HasValue && r.Component.IsEnabled != r.EnabledSeen) r.Enabled = null;
            return r.Enabled ?? r.Component.IsEnabled;
        }

        /// <summary>How far the obstacle moved from a pose: its centre, plus the arc its farthest corner swept turning.</summary>
        private static float Displacement(ObstacleRec r, SysVec center, SysQuat rotation, SysVec half)
        {
            float d = (r.Center - center).Length();
            if (r.Shape == 0)
            {
                float dot = Math.Min(1f, Math.Abs(SysQuat.Dot(SysQuat.Normalize(r.Rotation), SysQuat.Normalize(rotation))));
                float angle = 2f * (float)Math.Acos(dot);
                d += angle * Math.Max(half.Length(), r.Half.Length());
            }
            return d;
        }

        /// <summary>The component's shape in world space: its centre (local offset), half extents (scaled), rotation.</summary>
        private static void PoseOf(ObstacleRec r)
        {
            var o = r.Component;
            var world = Animation.BoneSocketService.EntityWorld(r.Entity);
            var scale = new SysVec(new SysVec(world.M11, world.M12, world.M13).Length(), new SysVec(world.M21, world.M22, world.M23).Length(),
                new SysVec(world.M31, world.M32, world.M33).Length());
            r.Center = SysVec.Transform(new SysVec(o.Center.X, o.Center.Y, o.Center.Z), world);
            if (o.Shape == NavObstacleShape.Cylinder)
            {
                r.Shape = 1;
                r.Half = new SysVec(o.Radius * Math.Max(scale.X, scale.Z), 0.5f * o.Height * scale.Y, o.Radius * Math.Max(scale.X, scale.Z));
                r.Rotation = SysQuat.Identity;
            }
            else
            {
                r.Shape = 0;
                r.Half = new SysVec(0.5f * o.Size.X * scale.X, 0.5f * o.Size.Y * scale.Y, 0.5f * o.Size.Z * scale.Z);
                var b = Animation.BoneSocketService.NormalizeBasis(world);
                float det = b.M11 * (b.M22 * b.M33 - b.M23 * b.M32) - b.M12 * (b.M21 * b.M33 - b.M23 * b.M31) + b.M13 * (b.M21 * b.M32 - b.M22 * b.M31);
                if (det < 0f) { b.M11 = -b.M11; b.M12 = -b.M12; b.M13 = -b.M13; }   // a mirroring scale: the box is symmetric
                r.Rotation = SafeRotation(SysQuat.CreateFromRotationMatrix(b));
            }
            if (!(r.Half.X > 0f)) r.Half.X = 0.001f;
            if (!(r.Half.Y > 0f)) r.Half.Y = 0.001f;
            if (!(r.Half.Z > 0f)) r.Half.Z = 0.001f;
        }

        private static SysQuat SafeRotation(SysQuat q)
        {
            float l = q.Length();
            return l > 1e-6f && float.IsFinite(l) ? SysQuat.Normalize(q) : SysQuat.Identity;
        }

        private static bool Finite(SysVec v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        // ---------------------------------------------------------------- the scene geometry the rebuilds start from

        /// <summary>Gather the run scene's geometry for the rebuilds over the next frames — once (the first carving obstacle),
        /// or again (<paramref name="rebake"/>: <see cref="Rebake"/> after destruction; a gather under way starts over).</summary>
        private static void StartGather(bool rebake)
        {
            if (_runScene == null || !TryGetInfo(out var settings, out _)) return;
            _gather = new GeometryBuilder((NavGeometrySource)(settings.SourceFlags == 0 ? (int)NavGeometrySource.Default : settings.SourceFlags));
            _gather.Start(_runScene);
            _gatherRebake |= rebake;
            _gatherMs = 0;
        }

        /// <summary>Gather on for <paramref name="budgetMs"/>; done, the geometry goes to the native rebuilds (copied there, binned
        /// on a thread of their own) and the tiles of the Rebake boxes asked for meanwhile are rebuilt from it.</summary>
        private static void StepGather(double budgetMs)
        {
            var sw = Stopwatch.StartNew();
            bool done = _gather.Step(budgetMs);
            if (!done) { _gatherMs += sw.Elapsed.TotalMilliseconds; return; }
            var g = _gather.Finish();
            bool rebake = _gatherRebake;
            _gather = null;
            _gatherRebake = false;
            int ok;
            try { ok = VortexAPI.NavRebuildSetSource(g.Verts, g.Verts.Length / 3, g.Tris, g.TriangleCount, g.TriAreas, g.Boxes, g.BoxCount, g.BoxAreas); }
            catch (EntryPointNotFoundException) { ObstaclesUnavailable(); return; }
            _gatherMs += sw.Elapsed.TotalMilliseconds;
            SourceGatherMs = (float)_gatherMs;
            if (ok == 0)
            {
                _sourceFailed = true;
                _rebakeBoxes.Clear();
                Log("[Navigation] obstacles cannot carve: the scene geometry does not fit the loaded navmesh — rebake it (Window ▸ Navigation).", true);
                return;
            }
            bool first = !_sourceSet;
            _sourceSet = true;
            _sourceFailed = false;
            foreach (var (min, max) in _rebakeBoxes)
            {
                Fill(_oc, min); Fill(_oh, max);
                VortexAPI.NavRebuildTiles(_oc, _oh);
            }
            _rebakeBoxes.Clear();
            if (first && !rebake && !_sceneChangedWarned && TryGetInfo(out _, out var baked) && (g.TriangleCount != baked.InputTriangles || g.BoxCount != baked.InputBoxes))
            {
                _sceneChangedWarned = true;
                Log("[Navigation] the scene changed since its navmesh was baked (" + g.TriangleCount + " triangles + " + g.BoxCount + " boxes now, "
                    + baked.InputTriangles + " + " + baked.InputBoxes + " baked): tiles rebuilt around obstacles follow the scene as it is now — rebake for seamless tiles.", true);
            }
            if (first)
                Log("[Navigation] obstacle rebuilds: the scene geometry gathered (" + g.TriangleCount + " triangles, " + g.BoxCount + " boxes) in "
                    + SourceGatherMs.ToString("0.0") + " ms over frames of " + GatherBudgetMs.ToString("0") + " ms");
        }

        private static ObstacleRec FindObstacle(GameEntity entity, long scriptId, bool registerComponent)
        {
            if (entity != null)
            {
                if (_obstacles.TryGetValue(entity, out var r)) return r;
                if (!registerComponent || !IsRunning) return null;
                var o = entity.GetComponent<NavMeshObstacle>();
                if (o == null) return null;
                r = new ObstacleRec { Entity = entity, Component = o };
                _obstacles[entity] = r;
                _obstacleList.Add(r);
                return r;
            }
            return scriptId != 0 && _scriptObstacles.TryGetValue(scriptId, out var s) ? s : null;
        }

        private static void Release(ObstacleRec r)
        {
            if (r.Handle != 0 && !_obstaclesUnavailable) { try { VortexAPI.NavObstacleRemove(r.Handle); } catch { } }
            r.Handle = 0;
            r.Sent = false;
        }

        private static void ObstaclesUnavailable()
        {
            if (_obstaclesUnavailable) return;
            _obstaclesUnavailable = true;
            _gather = null;
            Log("[Navigation] this engine library has no dynamic obstacles (#435) — rebuild the native engine; Nav Mesh Obstacles do nothing.", true);
        }

        /// <summary>Play stop: every obstacle and the rebuild source go (the navmesh stays loaded, its tiles as rebuilt — the
        /// next play loads the .vnav again); the rebuild budget is back to its default.</summary>
        private static void EndObstacles()
        {
            if (Available && !_obstaclesUnavailable && (_obstacleList.Count > 0 || _sourceSet))
            {
                try { VortexAPI.NavObstacleClear(); VortexAPI.NavRebuildClearSource(); }
                catch (EntryPointNotFoundException) { ObstaclesUnavailable(); }
            }
            _obstacles.Clear();
            _scriptObstacles.Clear();
            _obstacleList.Clear();
            _sourceSet = false;
            _sourceFailed = false;
            _sceneChangedWarned = false;
            _gather = null;
            _gatherRebake = false;
            _rebakeBoxes.Clear();
            _rebuildBudgetMs = DefaultRebuildBudgetMs;
            SourceGatherMs = 0f;
        }

        /// <summary>The navmesh was replaced or unloaded: its obstacles and source went with it (they are made again).</summary>
        private static void DropObstacleHandles()
        {
            foreach (var r in _obstacleList) { r.Handle = 0; r.Sent = false; }
            _sourceSet = false;
            _sourceFailed = false;
            _gather = null;
            _gatherRebake = false;
            _rebakeBoxes.Clear();
        }

        // ---------------------------------------------------------------- debug mesh

        private static int _debugMeshRevision = int.MinValue;
        private static long _debugMeshRevisionAt;

        /// <summary>Has the loaded navmesh changed at runtime since the debug mesh was made (rebuilt tiles, a door switched)?
        /// At most four redraws a second.</summary>
        private static bool DebugMeshStale()
        {
            if (_obstaclesUnavailable || (!IsRunning && _obstacleList.Count == 0)) return false;
            long now = Environment.TickCount64;
            if (now - _debugMeshRevisionAt < 250) return false;
            int rev = GetRebuildStats().Revision;
            if (rev == _debugMeshRevision) return false;
            _debugMeshRevision = rev;
            _debugMeshRevisionAt = now;
            return true;
        }
    }
}
