using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Data;
using Editor.Core.Services.Physics;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.AI;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.Utilities;
using SysVec = System.Numerics.Vector3;
using SysQuat = System.Numerics.Quaternion;
using SysMat = System.Numerics.Matrix4x4;

namespace Editor.Core.Services.AI
{
    /// <summary>Result of a path query / an agent's current path.</summary>
    public enum NavPathStatus
    {
        /// <summary>No navmesh, or start / end are not on (near) it.</summary>
        Invalid = 0,
        /// <summary>The path reaches the destination.</summary>
        Complete = 1,
        /// <summary>The destination is unreachable: the path leads to the closest reachable point.</summary>
        Partial = 2,
        /// <summary>An agent's path is still being computed (SetDestination returned immediately).</summary>
        Pending = 3
    }

    /// <summary>Which scene geometry a bake reads (stored with the navmesh in <see cref="NavBakeSettings.SourceFlags"/>).</summary>
    [Flags]
    public enum NavGeometrySource
    {
        None = 0,
        /// <summary>Enabled, solid colliders of entities that don't move (no dynamic / kinematic Rigidbody, no joint).</summary>
        Colliders = 1,
        /// <summary>Render meshes of entities marked Static that have no collider.</summary>
        StaticRenderMeshes = 2,
        /// <summary>Render meshes of every non-moving entity that has no collider.</summary>
        AllRenderMeshes = 4,
        Default = Colliders | StaticRenderMeshes
    }

    /// <summary>World-space bake input gathered from a scene (see <see cref="NavigationService.CollectGeometry"/>).</summary>
    public sealed class NavBakeGeometry
    {
        public float[] Verts = new float[0];
        public int[] Tris = new int[0];
        public byte[] TriAreas;
        public float[] Boxes = new float[0];
        public byte[] BoxAreas;
        public int TriangleCount, BoxCount, EntityCount;
        public readonly List<string> Notes = new List<string>();
        /// <summary>Each entity the geometry came from, with what it contributed (#449: the stale check).</summary>
        public readonly List<NavGeometryOwner> Owners = new List<NavGeometryOwner>();
    }

    /// <summary>
    /// One entity's part of a bake's input (#449 review), to tell later whether it changed. What the scene stores and the bake
    /// reads in bulk — the content of the model files it uses, terrain heights and holes, foliage trunks — is hashed exactly into
    /// <see cref="Signature"/>: stored bits read back the same on every machine. What the bake computes from transforms —
    /// <see cref="Pose"/> and <see cref="Extent"/> — is compared within a tolerance (<c>NavigationService.SameInput</c>), so
    /// float noise from another machine or evaluation order never reads as a change.
    /// </summary>
    public sealed class NavGeometryOwner
    {
        public Guid Id;
        /// <summary>The entity's name when the geometry was collected (not stored in the file).</summary>
        public string Name;
        /// <summary>Exact hash of the stored data its geometry comes from: model file content, terrain resolution, heights and
        /// holes, foliage trunks.</summary>
        public ulong Signature;
        /// <summary>Triangles and solid boxes it added.</summary>
        public int Triangles, Boxes;
        /// <summary>The transform the bake applied: the rotation-and-scale rows (3 × 3), then the translation.</summary>
        public readonly float[] Pose = new float[12];
        /// <summary>What it added, in world space: bounds min (x, y, z) and max, the mean of its triangle and box centres, and
        /// its triangle area plus box volume.</summary>
        public readonly float[] Extent = new float[10];
    }

    /// <summary>A scene's .vnav as its header describes it (#449) — see <see cref="NavigationService.ReadFileInfo"/>.</summary>
    public sealed class NavMeshFileInfo
    {
        public string Path;
        public long Size;
        /// <summary>When the file was written (the bake), UTC.</summary>
        public DateTime BakedUtc;
        /// <summary>The format version; -1 when the file is no navmesh this engine knows.</summary>
        public int Version;
        public NavBakeSettings Settings;
        public NavBakeStats Stats;
        /// <summary>The entities it was baked from (<see cref="NavGeometryOwner"/>, without names); null for a navmesh baked
        /// before bakes recorded them.</summary>
        public List<NavGeometryOwner> Inputs;
    }

    /// <summary>Outcome of a bake.</summary>
    public sealed class NavBakeResult
    {
        public bool Success;
        /// <summary>Native error code (&lt; 0) when the bake failed.</summary>
        public int Error;
        public string Message = "";
        public byte[] Data;
        public NavBakeStats Stats;
        /// <summary>Where the .vnav was written (null when not saved).</summary>
        public string Path;
        /// <summary>What the bake could not do as asked although it may have produced a navmesh (#451): tiles baked with a
        /// coarser cell height, geometry left without navmesh, tiles that failed — one sentence each, also appended to
        /// <see cref="Message"/>.</summary>
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// AI &amp; Navigation (GitHub #109 / #110): the scene's navmesh and the agents that walk on it.
    ///
    /// <b>Bake</b> (editor, Window ▸ Navigation): <see cref="CollectGeometry"/> flattens the scene's static geometry into
    /// world space — box / sphere / capsule colliders and primitive meshes as SOLID oriented boxes, mesh colliders and
    /// model render meshes as triangles (the same model-triangle export the mesh colliders use) — skipping moving
    /// entities (dynamic / kinematic Rigidbody, joints), characters (Animator, NavAgent, tag "Player") and everything
    /// tagged <see cref="IgnoreTag"/>. <see cref="Bake"/> runs Recast (tiled, multi-threaded; safe on a worker thread)
    /// and the result is saved as <c>&lt;Scene&gt;.vnav</c> next to the <c>.vscene</c> (it ships inside Assets.vpak).
    ///
    /// <b>Runtime</b> (play / standalone): <see cref="Begin"/> loads the scene's .vnav and registers every enabled
    /// <see cref="NavAgent"/> with the native crowd (DetourCrowd: path corridors, steering, local avoidance),
    /// <see cref="Tick"/> updates the crowd and writes each agent's pose back to its entity (collide-and-slide against the
    /// colliders through <see cref="CollisionService.MoveCharacter"/> when <see cref="NavAgent.ResolveCollisions"/> is
    /// on), <see cref="End"/> drops the agents (the navmesh stays loaded for the editor overlay). Scripts drive agents
    /// through <c>Vortex.Navigation</c>. The lifecycle is owned by <see cref="AiRuntime"/>.
    ///
    /// Without a Recast-enabled engine (<see cref="NavigationNative.Available"/> false) every call is a harmless no-op.
    /// </summary>
    public static class NavigationService
    {
        public const string NavMeshExtension = ".vnav";
        /// <summary>Entities with this tag (and their children) are left out of the bake (doors a script moves, clutter).</summary>
        public const string IgnoreTag = "NavIgnore";

        public static bool Available => NavigationNative.Available;
        public static string UnavailableReason => NavigationNative.Reason;

        // ================================================================================================ settings

        /// <summary>The engine's default bake settings with the editor's default geometry sources.</summary>
        public static NavBakeSettings DefaultSettings()
        {
            var s = new NavBakeSettings
            {
                CellSize = 0.2f, CellHeight = 0.1f, AgentHeight = 1.8f, AgentRadius = 0.4f, AgentMaxClimb = 0.4f, AgentMaxSlope = 45f,
                RegionMinSize = 8f, RegionMergeSize = 20f, EdgeMaxLen = 12f, EdgeMaxError = 1.3f, DetailSampleDist = 6f, DetailSampleMaxError = 1f,
                VertsPerPoly = 6, TileSize = 48, Partition = 0, FilterFlags = 7,
                BoundsMinX = 1f, BoundsMinY = 1f, BoundsMinZ = 1f, BoundsMaxX = -1f, BoundsMaxY = -1f, BoundsMaxZ = -1f
            };
            if (Available) { try { VortexAPI.NavGetDefaultSettings(out s); } catch { } }
            s.SourceFlags = (int)NavGeometrySource.Default;
            return s;
        }

        /// <summary>Settings to show for a scene: those its navmesh was baked with, else the defaults.</summary>
        public static NavBakeSettings SettingsFor(Scene scene)
        {
            if (scene != null && ReferenceEquals(scene, LoadedScene) && TryGetInfo(out var s, out _))
            {
                if (s.SourceFlags == 0) s.SourceFlags = (int)NavGeometrySource.Default;
                return s;
            }
            var data = scene != null ? ReadNavMeshFile(scene) : null;
            if (data != null && data.Length >= 8 + 23 * 4)
            {
                // The .vnav header starts with magic, version, then the settings block (NavigationFormat.h).
                try
                {
                    var h = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        if (BitConverter.ToUInt32(data, 0) == 0x56414E56u)
                        {
                            var p = new IntPtr(h.AddrOfPinnedObject().ToInt64() + 8);
                            var s2 = (NavBakeSettings)System.Runtime.InteropServices.Marshal.PtrToStructure(p, typeof(NavBakeSettings));
                            if (s2.SourceFlags == 0) s2.SourceFlags = (int)NavGeometrySource.Default;
                            if (s2.CellSize > 0f) return s2;
                        }
                    }
                    finally { h.Free(); }
                }
                catch { }
            }
            return DefaultSettings();
        }

        // ================================================================================================ files

        /// <summary><c>Assets/Scenes/Level.vscene</c> -> <c>Assets/Scenes/Level.vnav</c> (null without a scene path).</summary>
        public static string NavMeshPathFor(Scene scene)
        {
            string p = scene?.FilePath;
            return string.IsNullOrEmpty(p) ? null : Path.ChangeExtension(p, NavMeshExtension);
        }

        public static bool HasNavMeshFile(Scene scene)
        {
            var p = NavMeshPathFor(scene);
            return p != null && AssetVfs.Exists(p);
        }

        /// <summary>The scene's baked navmesh bytes — the mounted pak in a shipped game, the loose file in the editor.</summary>
        public static byte[] ReadNavMeshFile(Scene scene)
        {
            var p = NavMeshPathFor(scene);
            if (p == null) return null;
            try
            {
                if (AssetVfs.IsMounted && AssetVfs.TryGetBytes(p, out var bytes) && bytes != null) return bytes;
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
            catch (Exception ex) { Log("[Navigation] could not read " + p + ": " + ex.Message, true); }
            return null;
        }

        /// <summary>Write a baked navmesh next to the scene (<c>.vnav</c>). False (with the reason logged) on failure.</summary>
        public static bool Save(Scene scene, byte[] data, out string path)
        {
            path = NavMeshPathFor(scene);
            if (path == null || data == null || data.Length == 0) return false;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {
                Log("[Navigation] could not save " + path + ": " + ex.Message, true);
                return false;
            }
        }

        // ================================================================================================ loaded navmesh

        private static Scene _loadedScene;
        private static int _version;

        /// <summary>The scene whose navmesh is loaded (null = none, or one loaded from raw data).</summary>
        public static Scene LoadedScene => _loadedScene;
        /// <summary>Bumped on every load / unload (debug-mesh cache key).</summary>
        public static int Version => _version;

        public static bool IsLoaded
        {
            get
            {
                if (!Available) return false;
                try { return VortexAPI.NavIsLoaded() != 0; } catch { return false; }
            }
        }

        public static bool TryGetInfo(out NavBakeSettings settings, out NavBakeStats stats)
        {
            settings = default(NavBakeSettings); stats = default(NavBakeStats);
            if (!Available) return false;
            try { return VortexAPI.NavGetInfo(out settings, out stats) != 0; } catch { return false; }
        }

        /// <summary>Load navmesh bytes (a .vnav blob) as the current navmesh; every agent is re-registered on the next tick.</summary>
        public static bool LoadData(byte[] data, Scene owner)
        {
            if (!Available || data == null || data.Length == 0) return false;
            bool ok;
            try { ok = VortexAPI.NavLoad(data, data.Length) != 0; } catch { ok = false; }
            _version++;
            _loadedScene = ok ? owner : null;
            DropAgentHandles();
            return ok;
        }

        /// <summary>Load the scene's .vnav (VFS-aware). False when the scene has none or it is invalid.</summary>
        public static bool LoadForScene(Scene scene)
        {
            var data = ReadNavMeshFile(scene);
            if (data == null) return false;
            if (!LoadData(data, scene))
            {
                Log("[Navigation] '" + NavMeshPathFor(scene) + "' is not a valid navmesh for this engine version — rebake it (Window ▸ Navigation).", true);
                return false;
            }
            return true;
        }

        public static void Unload()
        {
            if (Available) { try { VortexAPI.NavUnload(); } catch { } }
            _loadedScene = null;
            _version++;
            DropAgentHandles();
        }

        // ================================================================================================ bake

        /// <summary>Flatten the scene's static geometry into world space (UI / game thread: reads the ECS).</summary>
        public static NavBakeGeometry CollectGeometry(Scene scene, int sourceFlags)
        {
            var g = new GeometryBuilder((NavGeometrySource)(sourceFlags == 0 ? (int)NavGeometrySource.Default : sourceFlags));
            if (scene?.Entities != null)
                foreach (var e in scene.Entities) g.Visit(e);
            return g.Finish();
        }

        /// <summary>Run the bake on prepared geometry. Thread-safe: call it from a worker thread and poll
        /// <see cref="BakeProgress"/>; the loaded navmesh is untouched (load the result with <see cref="LoadData"/>).</summary>
        public static NavBakeResult Bake(NavBakeGeometry geometry, NavBakeSettings settings)
        {
            var r = new NavBakeResult();
            if (!Available) { r.Error = -6; r.Message = "Navigation is unavailable: " + UnavailableReason; return r; }
            if (geometry == null || (geometry.TriangleCount == 0 && geometry.BoxCount == 0))
            {
                r.Error = -1; r.Message = "Nothing to bake: the scene has no static colliders or meshes."; return r;
            }
            int size;
            NavBakeStats stats;
            try
            {
                size = VortexAPI.NavBake(geometry.Verts, geometry.Verts.Length / 3, geometry.Tris, geometry.TriangleCount, geometry.TriAreas,
                    geometry.Boxes, geometry.BoxCount, geometry.BoxAreas, ref settings, out stats);
            }
            catch (Exception ex) { r.Error = -4; r.Message = "Bake failed: " + ex.Message; return r; }
            r.Stats = stats;
            ReadBakeNotes(r);
            string warnings = r.Warnings.Count > 0 ? " Warning: " + string.Join(" ", r.Warnings) : "";
            if (size <= 0) { r.Error = size; r.Message = ErrorText(size) + warnings; return r; }
            var data = new byte[size];
            int got = VortexAPI.NavGetBakeResult(data, size);
            try { VortexAPI.NavClearBakeResult(); } catch { }
            if (got != size) { r.Error = -4; r.Message = "Bake result could not be read back."; return r; }
            r.Data = StampInputs(data, geometry);   // what it was baked from: navmesh_info tells a stale one (#449)
            r.Success = true;
            r.Message = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} polygons in {1} tiles, {2:0.0} ms ({3} triangles + {4} solid boxes from {5} entities)",
                stats.PolyCount, stats.TileCount, stats.BakeMs, stats.InputTriangles, stats.InputBoxes, geometry.EntityCount) + warnings;
            return r;
        }

        /// <summary>The native bake's notes (#451) into <see cref="NavBakeResult.Warnings"/>: a bake that baked part of the
        /// level coarser — or not at all — says so instead of reporting a plain success.</summary>
        private static void ReadBakeNotes(NavBakeResult r)
        {
            try
            {
                int n = VortexAPI.NavGetBakeNotes(null, 0);
                if (n <= 0) return;
                var buf = new byte[n + 1];
                int got = Math.Max(0, Math.Min(n, VortexAPI.NavGetBakeNotes(buf, buf.Length)));
                foreach (var line in System.Text.Encoding.UTF8.GetString(buf, 0, got).Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) r.Warnings.Add(line.Trim());
            }
            catch (EntryPointNotFoundException) { }   // an engine library from before the notes
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Navigation] bake notes: " + ex.Message); }
        }

        /// <summary>Gather, bake, save next to the scene and load — synchronously (the Navigation window does the same on a
        /// worker thread with progress).</summary>
        public static NavBakeResult BakeScene(Scene scene, NavBakeSettings settings, bool save = true, bool load = true)
        {
            var geo = CollectGeometry(scene, settings.SourceFlags);
            var r = Bake(geo, settings);
            if (!r.Success) return r;
            if (save && Save(scene, r.Data, out var path)) r.Path = path;
            if (load) LoadData(r.Data, scene);
            return r;
        }

        /// <summary>0..1 of the running bake.</summary>
        public static float BakeProgress
        {
            get { if (!Available) return 0f; try { return VortexAPI.NavGetBakeProgress(); } catch { return 0f; } }
        }

        public static void CancelBake() { if (Available) { try { VortexAPI.NavCancelBake(); } catch { } } }

        public static string ErrorText(int code)
        {
            switch (code)
            {
                case -1: return "Nothing to bake: the scene has no static colliders or meshes.";
                case -2: return "Invalid bake settings.";
                case -3: return "Bake cancelled.";
                case -5: return "Nothing walkable: no surface is flat enough / large enough for the agent (check Max Slope, Agent Radius, Min Region Size).";
                case -6: return "Navigation is unavailable in this engine build: " + UnavailableReason;
                case -7: return "The level needs more than 16384 tiles: raise the Tile Size or the Cell Size.";
                case -8: return "Another bake is still running.";
                default: return "Bake failed (see the Console / log).";
            }
        }

        // ================================================================================================ the baked file (#449)

        // The .vnav header (Engine/Navigation/NavigationFormat.h): magic, version, settings (23 × 4 bytes), stats (16 × 4),
        // twelve more words, the last four reserved. Behind the engine's blob the editor appends what a bake read — one record
        // per entity (NavGeometryOwner) — and notes it in reserved words 0–2: the record's length, its entity count, "VNE1".
        // The engine reads its tiles by count and never looks behind them, so such a navmesh loads everywhere.
        internal const int HeaderSize = 212;
        private const int SettingsOffset = 8, StatsOffset = 100, ReservedOffset = 196;
        private const uint NavMagic = 0x56414E56u, InputsTag = 0x31454E56u, InputsMagic = 0x4E494E56u;   // "VNAV", "VNE1", "VNIN"
        // The record: magic, version, entity count, bytes per entity; then per entity its id (16 bytes), signature (8), triangle
        // and box counts (2 × 4), pose (12 floats) and extent (10 floats).
        private const int InputsVersion = 1, InputsHead = 16, InputSize = 16 + 8 + 8 + 12 * 4 + 10 * 4;

        /// <summary>The navmesh blob with what the bake read appended behind it (#449 review): every entity's id, the exact
        /// signature of its stored input and its pose and extent — see <see cref="NavGeometryOwner"/>.</summary>
        internal static byte[] StampInputs(byte[] blob, NavBakeGeometry g)
        {
            if (blob == null || blob.Length < HeaderSize || g == null || BitConverter.ToUInt32(blob, 0) != NavMagic) return blob;
            var owners = g.Owners;
            int length = InputsHead + owners.Count * InputSize;
            var data = new byte[blob.Length + length];
            Buffer.BlockCopy(blob, 0, data, 0, blob.Length);
            var t = data.AsSpan(blob.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(t, InputsMagic);
            BinaryPrimitives.WriteInt32LittleEndian(t.Slice(4), InputsVersion);
            BinaryPrimitives.WriteInt32LittleEndian(t.Slice(8), owners.Count);
            BinaryPrimitives.WriteInt32LittleEndian(t.Slice(12), InputSize);
            for (int i = 0; i < owners.Count; i++)
            {
                var o = owners[i];
                var r = t.Slice(InputsHead + i * InputSize, InputSize);
                o.Id.TryWriteBytes(r);
                BinaryPrimitives.WriteUInt64LittleEndian(r.Slice(16), o.Signature);
                BinaryPrimitives.WriteInt32LittleEndian(r.Slice(24), o.Triangles);
                BinaryPrimitives.WriteInt32LittleEndian(r.Slice(28), o.Boxes);
                for (int k = 0; k < 12; k++) BinaryPrimitives.WriteSingleLittleEndian(r.Slice(32 + 4 * k), o.Pose[k]);
                for (int k = 0; k < 10; k++) BinaryPrimitives.WriteSingleLittleEndian(r.Slice(80 + 4 * k), o.Extent[k]);
            }
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(ReservedOffset), length);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(ReservedOffset + 4), owners.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(ReservedOffset + 8), InputsTag);
            return data;
        }

        /// <summary>What a .vnav says (null when it is none): version, settings, stats — and, given the whole file, what it was
        /// baked from.</summary>
        internal static NavMeshFileInfo ParseHeader(byte[] data)
        {
            if (data == null || data.Length < HeaderSize || BitConverter.ToUInt32(data, 0) != NavMagic) return null;
            var info = new NavMeshFileInfo { Version = BitConverter.ToInt32(data, 4) };
            info.Settings = Read<NavBakeSettings>(data, SettingsOffset);
            info.Stats = Read<NavBakeStats>(data, StatsOffset);
            if (info.Settings.SourceFlags == 0) info.Settings.SourceFlags = (int)NavGeometrySource.Default;
            int length = InputsLength(data);
            if (length > 0 && length <= data.Length - HeaderSize) info.Inputs = ParseInputs(data.AsSpan(data.Length - length, length));
            return info;
        }

        /// <summary>The length of the record behind the blob, from the header; 0 when there is none.</summary>
        private static int InputsLength(byte[] header)
            => BitConverter.ToUInt32(header, ReservedOffset + 8) == InputsTag ? Math.Max(0, BitConverter.ToInt32(header, ReservedOffset)) : 0;

        private static List<NavGeometryOwner> ParseInputs(ReadOnlySpan<byte> t)
        {
            if (t.Length < InputsHead || BinaryPrimitives.ReadUInt32LittleEndian(t) != InputsMagic) return null;
            if (BinaryPrimitives.ReadInt32LittleEndian(t.Slice(4)) != InputsVersion) return null;
            int count = BinaryPrimitives.ReadInt32LittleEndian(t.Slice(8)), size = BinaryPrimitives.ReadInt32LittleEndian(t.Slice(12));
            if (count < 0 || size < InputSize || InputsHead + (long)count * size > t.Length) return null;
            var list = new List<NavGeometryOwner>(count);
            for (int i = 0; i < count; i++)
            {
                var r = t.Slice(InputsHead + i * size, InputSize);
                var o = new NavGeometryOwner
                {
                    Id = new Guid(r.Slice(0, 16)),
                    Signature = BinaryPrimitives.ReadUInt64LittleEndian(r.Slice(16)),
                    Triangles = BinaryPrimitives.ReadInt32LittleEndian(r.Slice(24)),
                    Boxes = BinaryPrimitives.ReadInt32LittleEndian(r.Slice(28)),
                };
                for (int k = 0; k < 12; k++) o.Pose[k] = BinaryPrimitives.ReadSingleLittleEndian(r.Slice(32 + 4 * k));
                for (int k = 0; k < 10; k++) o.Extent[k] = BinaryPrimitives.ReadSingleLittleEndian(r.Slice(80 + 4 * k));
                list.Add(o);
            }
            return list;
        }

        private static T Read<T>(byte[] data, int offset) where T : struct
        {
            var h = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { return System.Runtime.InteropServices.Marshal.PtrToStructure<T>(new IntPtr(h.AddrOfPinnedObject().ToInt64() + offset)); }
            finally { h.Free(); }
        }

        /// <summary>The scene's navmesh file as its header describes it, with what it was baked from — or null when the scene has
        /// none (#449). Reads the header and the record at the end of the file only; no navmesh is loaded.</summary>
        public static NavMeshFileInfo ReadFileInfo(Scene scene)
        {
            string path = NavMeshPathFor(scene);
            if (path == null || !File.Exists(path)) return null;
            NavMeshFileInfo info = null;
            try
            {
                using var fs = File.OpenRead(path);
                var header = new byte[HeaderSize];
                int got = 0;
                for (int n; got < header.Length && (n = fs.Read(header, got, header.Length - got)) > 0;) got += n;
                if (got == HeaderSize) info = ParseHeader(header);
                int length = info != null ? InputsLength(header) : 0;
                if (length > 0 && length <= fs.Length - HeaderSize)
                {
                    var tail = new byte[length];
                    fs.Seek(-length, SeekOrigin.End);
                    int read = 0;
                    for (int n; read < length && (n = fs.Read(tail, read, length - read)) > 0;) read += n;
                    if (read == length) info.Inputs = ParseInputs(tail);
                }
            }
            catch (Exception ex) { Log("[Navigation] could not read " + path + ": " + ex.Message, true); return null; }
            if (info == null) info = new NavMeshFileInfo { Version = -1 };   // there, but no navmesh this engine knows
            var fi = new FileInfo(path);
            info.Path = path;
            info.Size = fi.Length;
            info.BakedUtc = fi.LastWriteTimeUtc;
            return info;
        }

        /// <summary>
        /// Whether a scene's navmesh no longer matches the scene (#449): the entities a bake would read now — with the sources the
        /// navmesh was baked with — against those it was baked from, by id, so the entity order does not matter: one whose input
        /// changed (moved, turned, resized, sculpted, another model), one added, one gone. Within a tolerance (see
        /// <see cref="SameInput"/>): float noise from another machine or evaluation order never reads as a change. Null when that
        /// cannot be told: a navmesh baked before bakes recorded their input. Collects the geometry like a bake (UI / game thread).
        /// </summary>
        public static bool? IsStale(Scene scene, NavMeshFileInfo file, out string reason)
        {
            reason = null;
            if (scene == null || file == null) return null;
            if (file.Inputs == null)
            {
                reason = "unknown: this navmesh was baked before bakes recorded what they read — bake it again and the next check can tell";
                return null;
            }
            return IsStale(file, CollectGeometry(scene, file.Settings.SourceFlags), out reason);
        }

        internal static bool? IsStale(NavMeshFileInfo file, NavBakeGeometry now, out string reason)
        {
            reason = null;
            if (file?.Inputs == null || now == null) return null;
            // by id — and, should a scene hold an id twice, by its occurrence in the tree, which a bake walks in order
            var baked = new Dictionary<(Guid, int), NavGeometryOwner>();
            var seen = new Dictionary<Guid, int>();
            (Guid, int) Key(NavGeometryOwner o) { seen.TryGetValue(o.Id, out int n); seen[o.Id] = n + 1; return (o.Id, n); }
            foreach (var o in file.Inputs) baked[Key(o)] = o;
            seen.Clear();
            var changed = new List<string>();
            var added = new List<string>();
            foreach (var o in now.Owners)
            {
                var key = Key(o);
                if (!baked.TryGetValue(key, out var b)) { added.Add(o.Name); continue; }
                if (!SameInput(b, o)) changed.Add(o.Name);
                baked.Remove(key);
            }
            int removed = baked.Count;
            if (changed.Count == 0 && added.Count == 0 && removed == 0) return false;
            string Few(List<string> names) => string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(names, 5), n => "'" + n + "'")) + (names.Count > 5 ? " and " + (names.Count - 5) + " more" : "");
            var parts = new List<string>();
            if (changed.Count > 0) parts.Add("changed since the bake: " + Few(changed));
            if (added.Count > 0) parts.Add("new: " + Few(added));
            if (removed > 0) parts.Add(removed + (removed == 1 ? " entity is" : " entities are") + " gone or no longer static geometry");
            reason = string.Join("; ", parts) + " — bake again";
            return true;
        }

        /// <summary>
        /// Whether two records of one entity describe the same input (#449 review). The signature is compared exactly: it hashes
        /// values the scene stores (model files, terrain heights, foliage trunks), which read back bit for bit everywhere. What a
        /// bake computes from transforms is compared within a tolerance: positions — the pose's translation, the bounds, the
        /// centre — within a millimetre plus a millionth of their distance from the origin (a few float steps far out); rotation
        /// and scale within 1e-5 (about a thousandth of a degree); the area plus volume within 0.01 %. Smaller differences are
        /// float noise or far below a navmesh cell.
        /// </summary>
        internal static bool SameInput(NavGeometryOwner a, NavGeometryOwner b)
        {
            if (a == null || b == null || a.Signature != b.Signature) return false;
            for (int k = 0; k < 9; k++) if (!Near(a.Pose[k], b.Pose[k], 1e-5, 1e-5)) return false;
            for (int k = 9; k < 12; k++) if (!Near(a.Pose[k], b.Pose[k], 1e-3, 1e-6)) return false;
            for (int k = 0; k < 9; k++) if (!Near(a.Extent[k], b.Extent[k], 1e-3, 1e-6)) return false;
            return Near(a.Extent[9], b.Extent[9], 1e-4, 1e-4);
        }

        private static bool Near(float a, float b, double absolute, double relative)
        {
            if (a == b) return true;
            if (float.IsNaN(a) || float.IsNaN(b)) return float.IsNaN(a) && float.IsNaN(b);
            return Math.Abs((double)a - b) <= absolute + relative * Math.Max(Math.Abs((double)a), Math.Abs((double)b));
        }

        private static readonly Dictionary<string, (long Length, DateTime Time, ulong Hash)> _modelHashes = new Dictionary<string, (long, DateTime, ulong)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A model file's content as a number — another export of the model is another input (#449 review); cached while
        /// the file keeps its size and time. 0 for primitives and files that are not there.</summary>
        private static ulong ModelHash(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath) || meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return 0;
            string actual = meshPath;
            int cut = actual.LastIndexOf('#');
            if (cut > 0) actual = actual.Substring(0, cut);
            string root = ProjectData.Current?.Path;
            string abs = Path.IsPathRooted(actual) || root == null ? actual : Path.Combine(root, actual);
            try
            {
                var fi = new FileInfo(abs);
                if (!fi.Exists) return 0;
                lock (_modelHashes)
                    if (_modelHashes.TryGetValue(abs, out var known) && known.Length == fi.Length && known.Time == fi.LastWriteTimeUtc) return known.Hash;
                byte[] sha;
                using (var s = File.OpenRead(abs)) sha = System.Security.Cryptography.SHA256.HashData(s);
                ulong v = BitConverter.ToUInt64(sha, 0);
                lock (_modelHashes) _modelHashes[abs] = (fi.Length, fi.LastWriteTimeUtc, v);
                return v;
            }
            catch { return 0; }
        }

        // ================================================================================================ queries

        private static readonly float[] _qa = new float[3], _qb = new float[3], _qc = new float[3], _qd = new float[3], _qext = new float[3];
        private static float[] _pathBuf = new float[256 * 3];

        /// <summary>Straight path from <paramref name="from"/> to <paramref name="to"/> (corner points, start and end included,
        /// written to <paramref name="corners"/> when given).</summary>
        public static NavPathStatus CalculatePath(Vector3 from, Vector3 to, List<Vector3> corners)
        {
            corners?.Clear();
            if (!IsLoaded) return NavPathStatus.Invalid;
            Fill(_qa, from); Fill(_qb, to);
            int status;
            int n = VortexAPI.NavFindPath(_qa, _qb, null, _pathBuf, _pathBuf.Length / 3, out status);
            if (n <= 0) return NavPathStatus.Invalid;
            if (corners != null) for (int i = 0; i < n; i++) corners.Add(new Vector3(_pathBuf[i * 3], _pathBuf[i * 3 + 1], _pathBuf[i * 3 + 2]));
            return status == 1 ? NavPathStatus.Complete : status == 2 ? NavPathStatus.Partial : NavPathStatus.Invalid;
        }

        /// <summary>The closest navmesh point within <paramref name="maxDistance"/> of <paramref name="position"/>.</summary>
        public static bool SamplePosition(Vector3 position, float maxDistance, out Vector3 hit)
        {
            hit = position;
            if (!IsLoaded || maxDistance <= 0f) return false;
            Fill(_qa, position);
            _qext[0] = maxDistance; _qext[1] = Math.Max(maxDistance, 0.5f); _qext[2] = maxDistance;
            if (VortexAPI.NavClosestPoint(_qa, _qext, _qb) == 0) return false;
            var p = new Vector3(_qb[0], _qb[1], _qb[2]);
            if ((p - position).Magnitude > maxDistance + 1e-4f) return false;
            hit = p;
            return true;
        }

        /// <summary>Walk the navmesh surface from <paramref name="from"/> towards <paramref name="to"/>: true = a navmesh edge
        /// (wall / drop) blocks the way; <paramref name="hit"/> is where (or <paramref name="to"/> when clear).</summary>
        public static bool Raycast(Vector3 from, Vector3 to, out Vector3 hit, out Vector3 normal)
        {
            hit = to; normal = Vector3.Zero;
            if (!IsLoaded) return false;
            Fill(_qa, from); Fill(_qb, to);
            float t;
            bool blocked = VortexAPI.NavRaycast(_qa, _qb, null, _qc, _qd, out t) != 0;
            hit = new Vector3(_qc[0], _qc[1], _qc[2]);
            normal = new Vector3(_qd[0], _qd[1], _qd[2]);
            return blocked;
        }

        /// <summary>A random point anywhere on the navmesh.</summary>
        public static bool RandomPoint(out Vector3 point)
        {
            point = Vector3.Zero;
            if (!IsLoaded || VortexAPI.NavRandomPoint(_qa) == 0) return false;
            point = new Vector3(_qa[0], _qa[1], _qa[2]);
            return true;
        }

        /// <summary>A random point reachable from <paramref name="center"/> within <paramref name="radius"/> (along the surface).</summary>
        public static bool RandomPointAround(Vector3 center, float radius, out Vector3 point)
        {
            point = center;
            if (!IsLoaded || radius <= 0f) return false;
            Fill(_qa, center);
            if (VortexAPI.NavRandomPointAround(_qa, radius, null, _qb) == 0) return false;
            point = new Vector3(_qb[0], _qb[1], _qb[2]);
            return true;
        }

        // ================================================================================================ agents (runtime)

        private sealed class AgentRec
        {
            public GameEntity Entity;
            public NavAgent Agent;
            public uint Handle;
            public long SelfId;
            public bool HasColliders;
            public SysVec LastFeet;
            public bool HasLastFeet;
            public bool HasDestination;
            public SysVec Destination;
            public bool Arrived;
            public float SpeedOverride = -1f;
            public bool Stopped;
            public NavAgentState State;
            public bool StateValid;
            public volatile bool ParamsDirty = true;
            public float Yaw;
            public bool YawKnown;
            public bool Active = true;
        }

        private static readonly Dictionary<GameEntity, AgentRec> _agents = new Dictionary<GameEntity, AgentRec>();
        private static readonly List<AgentRec> _agentList = new List<AgentRec>();
        private static Scene _runScene;
        private static float _rescanTimer;
        private static bool _warnedNoNavMesh;
        private static readonly float[] _fa = new float[3], _fb = new float[3];

        /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
        public static bool IsRunning { get; private set; }
        /// <summary>The scene the runtime was started for.</summary>
        public static Scene RunScene => _runScene;
        /// <summary>Agents registered with the crowd right now.</summary>
        public static int AgentCount { get { int n = 0; foreach (var r in _agentList) if (r.Handle != 0) n++; return n; } }

        /// <summary>Play start: load the scene's navmesh and register its agents. Call after the collision / physics worlds
        /// were built (agents take their own colliders out of the static collision world).</summary>
        public static void Begin(Scene scene)
        {
            End();
            _runScene = scene;
            IsRunning = true;
            _rescanTimer = 0f;
            if (!Available)
            {
                if (HasAgents(scene)) Log("[Navigation] unavailable (" + UnavailableReason + ") — Nav Agents will not move.", true);
                return;
            }
            bool loaded = LoadForScene(scene);
            if (!loaded && IsLoaded && ReferenceEquals(_loadedScene, scene)) loaded = true;
            if (!loaded && HasAgents(scene) && !_warnedNoNavMesh)
            {
                _warnedNoNavMesh = true;
                Log("[Navigation] scene '" + scene?.Name + "' has Nav Agents but no baked navmesh (" + Path.GetFileName(NavMeshPathFor(scene) ?? "?")
                    + ") — bake it in Window ▸ Navigation; the agents stand still until then.", true);
            }
            Rescan();
            if (loaded)
            {
                TryGetInfo(out _, out var st);
                Log("[Navigation] navmesh loaded for '" + scene?.Name + "': " + st.PolyCount + " polygons, " + AgentCount + " agents");
            }
        }

        /// <summary>Play stop / scene switch: remove every agent from the crowd (the navmesh stays loaded).</summary>
        public static void End()
        {
            if (Available) { try { VortexAPI.NavCrowdClear(); } catch { } }
            foreach (var r in _agentList) if (r.Agent != null) r.Agent.PropertyChanged -= OnAgentChanged;
            _agents.Clear();
            _agentList.Clear();
            _runScene = null;
            IsRunning = false;
        }

        /// <summary>One frame: register new / re-activated agents, update the crowd, move the entities.</summary>
        public static void Tick(float dt)
        {
            if (!IsRunning || !Available) return;
            if (dt < 0f) dt = 0f;
            if (dt > 0.1f) dt = 0.1f;
            _rescanTimer -= dt;
            if (_rescanTimer <= 0f) { _rescanTimer = 0.5f; Rescan(); }
            if (!IsLoaded) return;

            // 1) sync entity -> crowd (spawns, teleports by scripts, parameter changes)
            for (int i = 0; i < _agentList.Count; i++)
            {
                var r = _agentList[i];
                // a limp agent (#407: a ragdoll at or under it) leaves the crowd like a switched-off one — no pose writes, no
                // capsule (Ragdoll.Activate removed it). When the ragdoll is over it rejoins at the entity's position: where
                // it went down — the ragdoll moves the model's bones, not the agent — unless a script moved it meanwhile
                // (a get-up places the root at Ragdoll.PelvisPosition first). Its destination is kept.
                bool live = r.Agent != null && r.Entity != null && MayJoinCrowd(r) && InRunScene(r.Entity);
                if (!live)
                {
                    if (r.Handle != 0) { try { VortexAPI.NavAgentRemove(r.Handle); } catch { } r.Handle = 0; }
                    r.StateValid = false;
                    continue;
                }
                var feet = EntityFeet(r);
                if (r.Handle == 0)
                {
                    AddToCrowd(r, feet);
                    continue;
                }
                if (r.HasLastFeet && (feet - r.LastFeet).LengthSquared() > 0.05f * 0.05f)
                {
                    // Moved by a script / physics since our last write: teleport and keep the destination.
                    Fill(_fa, feet);
                    VortexAPI.NavAgentTeleport(r.Handle, _fa);
                    if (r.HasDestination) { Fill(_fb, r.Destination); VortexAPI.NavAgentSetTarget(r.Handle, _fb); }
                }
                if (r.ParamsDirty)
                {
                    var p = ParamsOf(r);
                    VortexAPI.NavAgentSetParams(r.Handle, ref p);
                    r.ParamsDirty = false;
                }
            }

            // 2) the crowd: path requests, corridor following, avoidance
            VortexAPI.NavCrowdUpdate(dt);

            // 3) crowd -> entities
            bool moved = false;
            for (int i = 0; i < _agentList.Count; i++)
            {
                var r = _agentList[i];
                if (r.Handle == 0) continue;
                if (VortexAPI.NavAgentGetState(r.Handle, out r.State) == 0) { r.StateValid = false; r.Handle = 0; continue; }
                r.StateValid = true;
                var nav = new SysVec(r.State.PosX, r.State.PosY, r.State.PosZ);
                var feet = nav;
                bool rootDriven = Animation.AnimationService.Instance.RootMotionDrives(r.Entity);
                if (rootDriven)
                {
                    // #113: the clip's root motion moves the entity (through collision); the crowd agent follows it, so the
                    // path corridor and the desired velocity (Navigation.Velocity — the script picks walk / run from it)
                    // stay valid, and the agent only turns the body toward the path.
                    var cur = EntityFeet(r);
                    if (!r.HasLastFeet || (cur - nav).LengthSquared() > 1e-6f) { Fill(_fa, cur); VortexAPI.NavAgentMovePosition(r.Handle, _fa); }
                    feet = cur;
                }
                else if (r.Agent.ResolveCollisions && CollisionService.IsBuilt && r.HasLastFeet)
                {
                    if (r.HasColliders) { try { CollisionService.RemoveEntityShapes(r.Entity); } catch { } }   // never collide with itself
                    var delta = nav - r.LastFeet;
                    var from = new Vector3(r.LastFeet.X, r.LastFeet.Y, r.LastFeet.Z);
                    var res = CollisionService.MoveCharacter(from, r.Agent.Radius, r.Agent.Height, new Vector3(delta.X, delta.Y, delta.Z), out _, r.SelfId,
                        owner: r.Entity);
                    var resolved = new SysVec(res.X, res.Y, res.Z);
                    if (Math.Abs(resolved.Y - nav.Y) > 1.0f) resolved = nav;   // off the navmesh vertically: trust the navmesh
                    float dx = resolved.X - nav.X, dz = resolved.Z - nav.Z;
                    if (dx * dx + dz * dz > 0.02f * 0.02f)
                    {
                        // A collider (prop, closed door, the player) pushed the capsule: move the crowd agent there too.
                        Fill(_fa, resolved);
                        VortexAPI.NavAgentMovePosition(r.Handle, _fa);
                    }
                    feet = resolved;
                }

                // Face the walking direction.
                float vx = r.State.VelX, vz = r.State.VelZ;
                float speed = (float)Math.Sqrt(vx * vx + vz * vz);
                if (r.Agent.UpdateRotation && speed > 0.1f && r.Agent.AngularSpeed > 0f)
                {
                    float target = (float)(Math.Atan2(vx, vz) * 180.0 / Math.PI);
                    if (!r.YawKnown) { r.Yaw = EntityYaw(r.Entity); r.YawKnown = true; }
                    float d = DeltaAngle(r.Yaw, target);
                    float step = r.Agent.AngularSpeed * dt;
                    r.Yaw = Math.Abs(d) <= step ? target : r.Yaw + Math.Sign(d) * step;
                    WritePose(r.Entity, feet + new SysVec(0f, r.Agent.BaseOffset, 0f), r.Yaw);
                }
                else WritePose(r.Entity, feet + new SysVec(0f, r.Agent.BaseOffset, 0f), null);
                if (r.HasColliders && PhysicsService.IsBuilt && (feet - r.LastFeet).LengthSquared() > 1e-6f) SyncStaticBodies(r.Entity);
                r.LastFeet = feet;
                r.HasLastFeet = true;
                moved = true;

                // Arrival: stopping distance reached (without auto-braking before DetourCrowd's own slow-down radius).
                if (r.HasDestination && r.State.MoveState == 2 && r.State.RemainingDistance >= 0f)
                {
                    float stop = Math.Max(r.Agent.StoppingDistance, 0.05f);
                    if (!r.Agent.AutoBraking) stop = Math.Max(stop, r.Agent.Radius * 2f);
                    if (r.State.RemainingDistance <= stop)
                    {
                        VortexAPI.NavAgentResetTarget(r.Handle);
                        r.HasDestination = false;
                        r.Arrived = true;
                    }
                }
            }
            if (moved) SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>Send an agent to <paramref name="target"/> (asynchronous path request). False when the entity has no
        /// enabled NavAgent, there is no navmesh, the target is nowhere near it, or the agent is limp (#407: a ragdoll at
        /// or under it is active — it is out of the crowd until the ragdoll is over; send it again after
        /// Ragdoll.Deactivate).</summary>
        public static bool SetDestination(GameEntity e, Vector3 target)
        {
            var r = Rec(e, true);
            if (r == null || Limp(r)) return false;
            r.Destination = ToSys(target);
            r.HasDestination = true;
            r.Arrived = false;
            if (r.Handle == 0 && IsLoaded && MayJoinCrowd(r)) AddToCrowd(r, EntityFeet(r));
            if (r.Handle == 0) return false;
            Fill(_fa, target);
            bool ok = VortexAPI.NavAgentSetTarget(r.Handle, _fa) != 0;
            if (!ok) r.HasDestination = false;
            if (ok) { VortexAPI.NavAgentGetState(r.Handle, out r.State); r.StateValid = true; }
            return ok;
        }

        /// <summary>Stop moving (the destination is cleared; the agent slows to a halt).</summary>
        public static void Stop(GameEntity e)
        {
            var r = Rec(e, false);
            if (r == null) return;
            r.HasDestination = false;
            if (r.Handle != 0) VortexAPI.NavAgentResetTarget(r.Handle);
        }

        /// <summary>Freeze / unfreeze an agent in place (keeps its destination; speed 0 while paused).</summary>
        public static void SetPaused(GameEntity e, bool paused)
        {
            var r = Rec(e, true);
            if (r == null || r.Stopped == paused) return;
            r.Stopped = paused;
            r.ParamsDirty = true;
        }

        public static bool IsPaused(GameEntity e) { var r = Rec(e, false); return r != null && r.Stopped; }

        /// <summary>True while the agent walks (has a destination and some speed).</summary>
        public static bool IsMoving(GameEntity e)
        {
            var r = Rec(e, false);
            if (r == null || !r.StateValid) return false;
            float v = r.State.VelX * r.State.VelX + r.State.VelZ * r.State.VelZ;
            return (r.HasDestination && r.State.MoveState != 3) || v > 0.01f;
        }

        public static bool HasDestination(GameEntity e) { var r = Rec(e, false); return r != null && r.HasDestination; }

        /// <summary>True once the agent reached its last destination (until the next SetDestination).</summary>
        public static bool HasArrived(GameEntity e) { var r = Rec(e, false); return r != null && r.Arrived; }

        public static NavPathStatus GetPathStatus(GameEntity e)
        {
            var r = Rec(e, false);
            if (r == null || r.Handle == 0 || !r.StateValid) return NavPathStatus.Invalid;
            switch (r.State.MoveState)
            {
                case 1: return NavPathStatus.Pending;
                case 2: return r.State.Partial != 0 ? NavPathStatus.Partial : NavPathStatus.Complete;
                case 0: return r.Arrived ? NavPathStatus.Complete : NavPathStatus.Invalid;
                default: return NavPathStatus.Invalid;
            }
        }

        /// <summary>Distance left along the path (0 after arrival, -1 without a path).</summary>
        public static float RemainingDistance(GameEntity e)
        {
            var r = Rec(e, false);
            if (r == null || !r.StateValid) return -1f;
            if (r.Arrived && !r.HasDestination) return 0f;
            return r.State.MoveState == 2 ? r.State.RemainingDistance : (r.State.MoveState == 1 ? float.PositiveInfinity : -1f);
        }

        public static Vector3 GetVelocity(GameEntity e)
        {
            var r = Rec(e, false);
            return r != null && r.StateValid ? new Vector3(r.State.VelX, r.State.VelY, r.State.VelZ) : Vector3.Zero;
        }

        public static Vector3 GetDestination(GameEntity e)
        {
            var r = Rec(e, false);
            return r != null && r.HasDestination ? new Vector3(r.Destination.X, r.Destination.Y, r.Destination.Z) : Vector3.Zero;
        }

        /// <summary>The next corner the agent walks towards (its own position without a path).</summary>
        public static Vector3 GetSteeringTarget(GameEntity e)
        {
            var r = Rec(e, false);
            return r != null && r.StateValid ? new Vector3(r.State.CornerX, r.State.CornerY, r.State.CornerZ) : Vector3.Zero;
        }

        /// <summary>Place an agent somewhere else (its path is cleared). False when the point is not on / near the navmesh, or
        /// while the agent is limp (#407: a ragdoll at or under it is active — warp it after Ragdoll.Deactivate).</summary>
        public static bool Warp(GameEntity e, Vector3 position)
        {
            var r = Rec(e, true);
            if (r == null || Limp(r)) return false;
            var p = ToSys(position);
            WritePose(r.Entity, p + new SysVec(0f, r.Agent.BaseOffset, 0f), null);
            r.LastFeet = p; r.HasLastFeet = true;
            r.HasDestination = false;
            if (r.Handle == 0) { if (IsLoaded) AddToCrowd(r, p); return r.Handle != 0; }
            Fill(_fa, p);
            return VortexAPI.NavAgentTeleport(r.Handle, _fa) != 0;
        }

        /// <summary>Runtime speed (m/s) override; negative = back to the component's Speed. The component is not changed.</summary>
        public static void SetSpeedOverride(GameEntity e, float speed)
        {
            var r = Rec(e, true);
            if (r == null) return;
            r.SpeedOverride = speed;
            r.ParamsDirty = true;
        }

        public static float GetSpeed(GameEntity e)
        {
            var r = Rec(e, false);
            if (r == null) return 0f;
            return r.SpeedOverride >= 0f ? r.SpeedOverride : r.Agent.Speed;
        }

        /// <summary>The agent's remaining path corners (empty without a path).</summary>
        public static List<Vector3> GetAgentPath(GameEntity e)
        {
            var list = new List<Vector3>();
            var r = Rec(e, false);
            if (r == null || r.Handle == 0) return list;
            int n = VortexAPI.NavAgentGetCorners(r.Handle, _pathBuf, _pathBuf.Length / 3);
            for (int i = 0; i < n; i++) list.Add(new Vector3(_pathBuf[i * 3], _pathBuf[i * 3 + 1], _pathBuf[i * 3 + 2]));
            return list;
        }

        /// <summary>True when the entity is a registered (enabled) agent in the running game.</summary>
        public static bool IsAgent(GameEntity e) => Rec(e, false) != null;

        // ---- internals ----

        private static AgentRec Rec(GameEntity e, bool create)
        {
            if (e == null || !IsRunning) return null;
            if (_agents.TryGetValue(e, out var r)) return r.Agent != null && r.Agent.IsEnabled ? r : null;
            if (!create) return null;
            var a = e.GetComponent<NavAgent>();
            if (a == null || !a.IsEnabled) return null;
            return Register(e, a);
        }

        private static AgentRec Register(GameEntity e, NavAgent a)
        {
            var r = new AgentRec { Entity = e, Agent = a };
            try { r.SelfId = Editor.Scripting.ScriptRuntime.Instance.HandleForEntity(e); } catch { r.SelfId = 0; }
            if (r.SelfId == 0) r.SelfId = -1000000 - _agentList.Count;
            r.HasColliders = HasColliderInSubtree(e);
            if (a.ResolveCollisions && r.HasColliders && CollisionService.IsBuilt)
            {
                // A moving agent must not keep a static copy of its own collider in the character world.
                try { CollisionService.RemoveEntityShapes(e); } catch { }
            }
            a.PropertyChanged += OnAgentChanged;
            _agents[e] = r;
            _agentList.Add(r);
            if (IsLoaded && MayJoinCrowd(r)) AddToCrowd(r, EntityFeet(r));
            return r;
        }

        /// <summary>#407: a ragdoll at or under the agent is active — it takes no navigation orders and stays out of the crowd.</summary>
        private static bool Limp(AgentRec r) => RagdollService.ActiveAtOrUnder(r.Entity);

        /// <summary>May the agent be in the crowd: its component enabled, its entity active, not limp (the callers check that a
        /// navmesh is loaded).</summary>
        private static bool MayJoinCrowd(AgentRec r) => r.Agent.IsEnabled && r.Entity.ActiveInHierarchy && !Limp(r);

        private static void OnAgentChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var a = sender as NavAgent;
            if (a?.Entity != null && _agents.TryGetValue(a.Entity, out var r)) r.ParamsDirty = true;
        }

        private static void AddToCrowd(AgentRec r, SysVec feet)
        {
            if (!IsLoaded) return;
            var p = ParamsOf(r);
            Fill(_fa, feet);
            r.Handle = VortexAPI.NavAgentAdd(_fa, ref p);
            r.ParamsDirty = false;
            if (r.Handle == 0) return;
            r.LastFeet = feet;
            r.HasLastFeet = true;
            if (r.HasDestination) { Fill(_fb, r.Destination); VortexAPI.NavAgentSetTarget(r.Handle, _fb); }
            VortexAPI.NavAgentGetState(r.Handle, out r.State);
            r.StateValid = true;
        }

        private static NavAgentParams ParamsOf(AgentRec r)
        {
            var a = r.Agent;
            float speed = r.SpeedOverride >= 0f ? r.SpeedOverride : a.Speed;
            float lowPriority = a.AvoidancePriority / 99f;   // 0 = most important .. 1 = gives way most
            return new NavAgentParams
            {
                Radius = a.Radius,
                Height = a.Height,
                MaxSpeed = r.Stopped ? 0f : speed,
                MaxAcceleration = r.Stopped ? 1000f : a.Acceleration,
                SeparationWeight = 0.5f + 3.5f * lowPriority,
                CollisionQueryRange = a.Radius * 12f,
                PathOptimizationRange = a.Radius * 30f,
                AvoidanceQuality = 3,
                UpdateFlags = 0
            };
        }

        /// <summary>Find every NavAgent of the run scene (runtime spawns included) and forget removed ones.</summary>
        private static void Rescan()
        {
            if (_runScene?.Entities == null) return;
            var seen = new HashSet<GameEntity>();
            foreach (var root in _runScene.Entities) Collect(root, seen);
            for (int i = _agentList.Count - 1; i >= 0; i--)
            {
                var r = _agentList[i];
                if (seen.Contains(r.Entity)) continue;
                if (r.Handle != 0) { try { VortexAPI.NavAgentRemove(r.Handle); } catch { } }
                if (r.Agent != null) r.Agent.PropertyChanged -= OnAgentChanged;
                _agents.Remove(r.Entity);
                _agentList.RemoveAt(i);
            }
        }

        private static void Collect(GameEntity e, HashSet<GameEntity> seen)
        {
            if (e == null) return;
            var a = e.GetComponent<NavAgent>();
            if (a != null)
            {
                seen.Add(e);
                if (!_agents.ContainsKey(e)) Register(e, a);
            }
            if (e.Children != null) foreach (var c in e.Children) Collect(c, seen);
        }

        private static bool HasAgents(Scene scene)
        {
            if (scene?.Entities == null) return false;
            bool Walk(GameEntity e)
            {
                if (e == null) return false;
                if (e.GetComponent<NavAgent>() != null) return true;
                if (e.Children != null) foreach (var c in e.Children) if (Walk(c)) return true;
                return false;
            }
            foreach (var e in scene.Entities) if (Walk(e)) return true;
            return false;
        }

        private static bool InRunScene(GameEntity e)
        {
            var top = e;
            while (top.Parent != null) top = top.Parent;
            return _runScene?.Entities != null && _runScene.Entities.Contains(top);
        }

        private static void DropAgentHandles()
        {
            foreach (var r in _agentList) { r.Handle = 0; r.StateValid = false; }
        }

        /// <summary>An agent whose collider became a STATIC physics body (no Rigidbody) would leave that body at its spawn
        /// point: teleport it along so raycasts / bullets hit the agent where it is. (A Kinematic Rigidbody is the better
        /// setup — PhysicsService drives those itself.)</summary>
        private static void SyncStaticBodies(GameEntity e)
        {
            if (e == null) return;
            try
            {
                if (PhysicsService.HasBody(e) && !PhysicsService.HasRigidbody(e)) PhysicsService.RefreshEntity(e);
            }
            catch { }
            if (e.Children != null) foreach (var c in e.Children) SyncStaticBodies(c);
        }

        private static bool HasColliderInSubtree(GameEntity e)
        {
            if (e == null) return false;
            foreach (var c in e.Components) if (c is Collider) return true;
            if (e.Children != null) foreach (var c in e.Children) if (HasColliderInSubtree(c)) return true;
            return false;
        }

        private static SysVec EntityFeet(AgentRec r)
        {
            var w = Animation.BoneSocketService.EntityWorld(r.Entity).Translation;
            return new SysVec(w.X, w.Y - r.Agent.BaseOffset, w.Z);
        }

        internal static float EntityYaw(GameEntity e)
        {
            var m = Animation.BoneSocketService.EntityWorld(e);
            var f = SysVec.TransformNormal(SysVec.UnitZ, m);
            if (f.X * f.X + f.Z * f.Z < 1e-8f) return 0f;
            return (float)(Math.Atan2(f.X, f.Z) * 180.0 / Math.PI);
        }

        private static float DeltaAngle(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f; else if (d < -180f) d += 360f;
            return d;
        }

        /// <summary>World position (+ optional world yaw, pitch / roll cleared) -> the entity's local transform.</summary>
        internal static void WritePose(GameEntity e, SysVec worldPos, float? yawDeg)
        {
            var t = e?.Transform;
            if (t == null) return;
            SysMat desired;
            if (yawDeg.HasValue) desired = Animation.BoneSocketService.EulerZXY(new SysVec(0f, yawDeg.Value, 0f));
            else desired = Animation.BoneSocketService.NormalizeBasis(Animation.BoneSocketService.EntityWorld(e));
            desired.M41 = worldPos.X; desired.M42 = worldPos.Y; desired.M43 = worldPos.Z; desired.M44 = 1f;
            var local = desired;
            var parentWorld = Animation.BoneSocketService.EntityWorld(e.Parent);
            if (SysMat.Invert(parentWorld, out var inv)) local = desired * inv;
            var lp = local.Translation;
            var cp = t.LocalPosition;
            if (Math.Abs(cp.X - lp.X) > 1e-5f || Math.Abs(cp.Y - lp.Y) > 1e-5f || Math.Abs(cp.Z - lp.Z) > 1e-5f)
                t.LocalPosition = new Vector3(lp.X, lp.Y, lp.Z);
            if (yawDeg.HasValue)
            {
                var euler = Animation.BoneSocketService.ToEulerZXY(Animation.BoneSocketService.NormalizeBasis(local));
                var cr = t.LocalRotation;
                if (Math.Abs(DeltaAngle(cr.X, euler.X)) > 1e-3f || Math.Abs(DeltaAngle(cr.Y, euler.Y)) > 1e-3f || Math.Abs(DeltaAngle(cr.Z, euler.Z)) > 1e-3f)
                    t.LocalRotation = new Vector3(euler.X, euler.Y, euler.Z);
            }
        }

        // ================================================================================================ debug drawing

        /// <summary>Draw the navmesh surface (a tinted wire net through the gizmo pass) — the Navigation window's toggle.</summary>
        public static bool ShowNavMesh;
        /// <summary>Draw the agents' remaining paths in play mode.</summary>
        public static bool ShowAgentPaths;

        private static long _debugMesh = ID.INVALID_ID;
        private static int _debugMeshVersion = -1;

        /// <summary>Submit the navmesh overlay (when <see cref="ShowNavMesh"/> is on) and, in play, the agents' paths
        /// (<see cref="ShowAgentPaths"/>). Call once per frame before the renderer draws (UI / game thread).</summary>
        public static void SubmitDebugDraw()
        {
            try
            {
                if (ShowNavMesh) SubmitNavMeshSurface();
                if (ShowAgentPaths && IsRunning) SubmitAgentPaths();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Navigation] debug draw: " + ex.Message); }
        }

        private static void SubmitNavMeshSurface()
        {
            if (!IsLoaded) return;
            if (_debugMeshVersion != _version || _debugMesh == ID.INVALID_ID)
            {
                if (_debugMesh != ID.INVALID_ID) { try { VortexAPI.DeleteMesh(_debugMesh); } catch { } }
                _debugMesh = VortexAPI.NavCreateDebugMesh(0.05f);
                _debugMeshVersion = _version;
            }
            if (_debugMesh == ID.INVALID_ID) return;
            long mat = VortexAPI.NavigationGizmoMaterial(0.15f, 0.85f, 0.75f);
            if (mat != ID.INVALID_ID) VortexAPI.SubmitGizmoWireForRendering(_debugMesh, mat, null);
        }

        private static void SubmitAgentPaths()
        {
            var seg = new List<float>();
            foreach (var r in _agentList)
            {
                if (r.Handle == 0 || !r.StateValid) continue;
                int n = VortexAPI.NavAgentGetCorners(r.Handle, _pathBuf, 32);
                float px = r.State.PosX, py = r.State.PosY + 0.1f, pz = r.State.PosZ;
                for (int i = 0; i < n; i++)
                {
                    float x = _pathBuf[i * 3], y = _pathBuf[i * 3 + 1] + 0.1f, z = _pathBuf[i * 3 + 2];
                    seg.Add(px); seg.Add(py); seg.Add(pz); seg.Add(x); seg.Add(y); seg.Add(z);
                    px = x; py = y; pz = z;
                }
            }
            if (seg.Count >= 6) VortexAPI.RenderNavigationLines(seg.ToArray(), seg.Count, 1f, 0.85f, 0.2f, 0.03f, 96);
        }

        /// <summary>Navmesh edges as line segments (6 floats each) for a viewport gizmo layer: flags bit0 = boundary edges,
        /// bit1 = internal polygon edges. Empty without a navmesh.</summary>
        public static float[] GetNavMeshDebugLines(int flags = 1)
        {
            if (!IsLoaded) return new float[0];
            int n = VortexAPI.NavGetDebugLines(null, 0, flags);
            if (n <= 0) return new float[0];
            var buf = new float[n];
            int got = VortexAPI.NavGetDebugLines(buf, n, flags);
            if (got != n) Array.Resize(ref buf, Math.Max(0, got));
            return buf;
        }

        /// <summary>The navmesh surface as world-space triangles (9 floats each).</summary>
        public static float[] GetNavMeshDebugTriangles()
        {
            if (!IsLoaded) return new float[0];
            int n = VortexAPI.NavGetDebugTriangles(null, 0);
            if (n <= 0) return new float[0];
            var buf = new float[n];
            int got = VortexAPI.NavGetDebugTriangles(buf, n);
            if (got != n) Array.Resize(ref buf, Math.Max(0, got));
            return buf;
        }

        // ================================================================================================ helpers

        private static void Fill(float[] a, Vector3 v) { a[0] = v.X; a[1] = v.Y; a[2] = v.Z; }
        private static void Fill(float[] a, SysVec v) { a[0] = v.X; a[1] = v.Y; a[2] = v.Z; }
        private static SysVec ToSys(Vector3 v) => new SysVec(v.X, v.Y, v.Z);

        internal static void Log(string msg, bool warning = false)
        {
            System.Diagnostics.Debug.WriteLine(msg);
            try { if (warning) ConsoleService.Instance.LogWarning(msg); else ConsoleService.Instance.Log(msg); } catch { }
        }

        // ================================================================================================ bake input

        /// <summary>Walks the entity tree and flattens static geometry into world space.</summary>
        private sealed class GeometryBuilder
        {
            private readonly NavGeometrySource _src;
            private readonly List<float> _verts = new List<float>(4096);
            private readonly List<int> _tris = new List<int>(4096);
            private readonly List<float> _boxes = new List<float>(256);
            private readonly HashSet<GameEntity> _contributors = new HashSet<GameEntity>();
            private readonly NavBakeGeometry _out = new NavBakeGeometry();
            private readonly Dictionary<string, float[]> _modelCache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            // whose geometry is being added, and what each entity added — the stale check's records (#449)
            private GameEntity _owner;
            private readonly Dictionary<GameEntity, Part> _parts = new Dictionary<GameEntity, Part>(ReferenceEqualityComparer.Instance);

            public GeometryBuilder(NavGeometrySource src) { _src = src; }

            public void Visit(GameEntity e)
            {
                if (e == null || !e.IsActive) return;
                if (Excluded(e)) return;
                _owner = e;   // its own geometry comes before its children's
                var terrain = e.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
                if (terrain != null && terrain.IsEnabled && terrain.Collision && terrain.Navigation)
                {
                    // #124: the terrain's surface (every sample up to 257², coarser beyond) is level geometry
                    float cell;
                    var raw = Editor.Core.Services.Terrain.TerrainService.CollisionTriangles(e, out cell);
                    if (raw != null && raw.Length >= 9)
                    {
                        SysVec tpos; SysQuat trot;
                        Editor.Core.Services.Terrain.TerrainService.Pose(e, out tpos, out trot);
                        SysMat tworld = SysMat.CreateFromQuaternion(trot) * SysMat.CreateTranslation(tpos);
                        for (int i = 0; i + 8 < raw.Length; i += 9)
                            AddTriangle(tworld, new SysVec(raw[i], raw[i + 1], raw[i + 2]), new SysVec(raw[i + 3], raw[i + 4], raw[i + 5]), new SysVec(raw[i + 6], raw[i + 7], raw[i + 8]));
                        if (_parts.TryGetValue(e, out var part)) part.Terrain = true;
                        _contributors.Add(e);
                    }
                    if (e.Children != null) foreach (var c in e.Children) Visit(c);
                    return;
                }
                var waterComp = e.GetComponent<Editor.ECS.Components.Rendering.Water>();
                if (waterComp != null && waterComp.IsEnabled)
                {
                    // #200: the deep part of a lake blocks the navmesh — agents wade the shallows, not the lake bed
                    var deep = Editor.Core.Services.Water.WaterService.DeepBoxes(e);
                    foreach (var b in deep)
                        AddBox(SysMat.Identity, new SysVec(b.centre.X, b.centre.Y, b.centre.Z), new SysVec(b.half.X, b.half.Y, b.half.Z), SysQuat.Identity);
                    if (deep.Count > 0) _contributors.Add(e);
                }
                var foliageComp = e.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
                if (foliageComp != null && foliageComp.IsEnabled)
                {
                    // #125: the colliding foliage (trunks) blocks the navmesh as boxes
                    var cols = Editor.Core.Services.Foliage.FoliageService.Collidables(e);
                    if (cols.Count > 0)
                    {
                        foreach (var c in cols)
                            AddBox(SysMat.Identity, new SysVec(c.Base.X, c.Base.Y + c.Height * 0.5f, c.Base.Z), new SysVec(c.Radius, c.Height * 0.5f, c.Radius), SysQuat.Identity);
                        if (_parts.TryGetValue(e, out var part)) part.Foliage = true;
                        _contributors.Add(e);
                    }
                    if (e.Children != null) foreach (var c in e.Children) Visit(c);
                    return;
                }
                bool hadCollider = false;
                if ((_src & NavGeometrySource.Colliders) != 0)
                {
                    SysMat world = default(SysMat);
                    bool haveWorld = false;
                    foreach (var c in e.Components)
                    {
                        if (!(c is Collider col) || !col.IsEnabled) continue;
                        if (col.IsTrigger) { hadCollider = true; continue; }   // triggers never block, but the entity is "collided"
                        if (!haveWorld) { world = Animation.BoneSocketService.EntityWorld(e); haveWorld = true; }
                        if (AddCollider(e, col, world)) { _contributors.Add(e); }
                        hadCollider = true;
                    }
                }
                if (!hadCollider)
                {
                    bool wantMesh = (_src & NavGeometrySource.AllRenderMeshes) != 0 || ((_src & NavGeometrySource.StaticRenderMeshes) != 0 && e.IsStatic);
                    var mr = wantMesh ? e.GetComponent<MeshRenderer>() : null;
                    if (mr != null && mr.IsEnabled && !string.IsNullOrEmpty(mr.MeshPath))
                    {
                        if (AddMesh(e, mr.MeshPath, SysVec.Zero, Animation.BoneSocketService.EntityWorld(e))) _contributors.Add(e);
                    }
                }
                if (e.Children != null) foreach (var c in e.Children) Visit(c);
            }

            /// <summary>Moving things and characters are not level geometry.</summary>
            private static bool Excluded(GameEntity e)
            {
                if (string.Equals(e.Tag, IgnoreTag, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(e.Tag, "Player", StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var c in e.Components)
                {
                    if (c is NavAgent || c is Animator) return true;
                    if (c is Rigidbody rb && rb.IsEnabled && rb.BodyType != RigidbodyType.Static) return true;
                    if (c is PhysicsJoint j && j.IsEnabled) return true;
                }
                return false;
            }

            private bool AddCollider(GameEntity e, Collider col, SysMat world)
            {
                var center = new SysVec(col.Center.X, col.Center.Y, col.Center.Z);
                switch (col)
                {
                    case BoxCollider box:
                        AddBox(world, center, new SysVec(Math.Abs(box.Size.X) * 0.5f, Math.Abs(box.Size.Y) * 0.5f, Math.Abs(box.Size.Z) * 0.5f), SysQuat.Identity);
                        return true;
                    case SphereCollider sph:
                        {
                            // A sphere as its (rotated) bounding cube in the entity frame, radius scaled by the largest axis.
                            var sc = ScaleOf(world);
                            float r = Math.Abs(sph.Radius) * Math.Max(sc.X, Math.Max(sc.Y, sc.Z));
                            AddBoxWorldHalf(world, center, new SysVec(r, r, r));
                            return true;
                        }
                    case CapsuleCollider cap:
                        {
                            float r = Math.Abs(cap.Radius), h = Math.Max(Math.Abs(cap.Height), 2f * r) * 0.5f;
                            var half = cap.Direction == 0 ? new SysVec(h, r, r) : cap.Direction == 2 ? new SysVec(r, r, h) : new SysVec(r, h, r);
                            AddBox(world, center, half, SysQuat.Identity);
                            return true;
                        }
                    case MeshCollider mc:
                        {
                            string path = !string.IsNullOrEmpty(mc.MeshPath) ? mc.MeshPath : e.GetComponent<MeshRenderer>()?.MeshPath;
                            if (string.IsNullOrEmpty(path)) { Note(e, "Mesh Collider without a mesh — skipped"); return false; }
                            return AddMesh(e, path, center, world);
                        }
                    default:
                        {
                            // A plain Collider of type Mesh/Convex: the render mesh.
                            string path = e.GetComponent<MeshRenderer>()?.MeshPath;
                            return !string.IsNullOrEmpty(path) && AddMesh(e, path, center, world);
                        }
                }
            }

            private bool AddMesh(GameEntity e, string meshPath, SysVec localOffset, SysMat world)
            {
                if (meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                {
                    switch (meshPath.Substring("Primitive:".Length).ToLowerInvariant())
                    {
                        case "plane":
                        case "quad":
                            {
                                // One-sided unit quad in XZ at y = 0, walkable side up.
                                var a = new SysVec(-0.5f, 0f, -0.5f) + localOffset; var b = new SysVec(-0.5f, 0f, 0.5f) + localOffset;
                                var c = new SysVec(0.5f, 0f, 0.5f) + localOffset; var d = new SysVec(0.5f, 0f, -0.5f) + localOffset;
                                AddTriangle(world, a, b, c);
                                AddTriangle(world, a, c, d);
                                return true;
                            }
                        case "cube":
                        case "sphere":
                        case "cylinder":
                        case "capsule":
                        case "cone":
                        case "torus":
                            AddBox(world, localOffset, new SysVec(0.5f, 0.5f, 0.5f), SysQuat.Identity);
                            return true;
                        default:
                            Note(e, "unknown primitive '" + meshPath + "' — skipped");
                            return false;
                    }
                }
                var tris = ModelTriangles(meshPath);
                if (tris == null || tris.Length < 9) { Note(e, "no triangles for '" + meshPath + "' — skipped"); return false; }
                Current(world)?.Models.Add(meshPath);
                for (int i = 0; i + 8 < tris.Length; i += 9)
                {
                    AddTriangle(world,
                        new SysVec(tris[i], tris[i + 1], tris[i + 2]) + localOffset,
                        new SysVec(tris[i + 3], tris[i + 4], tris[i + 5]) + localOffset,
                        new SysVec(tris[i + 6], tris[i + 7], tris[i + 8]) + localOffset);
                }
                return true;
            }

            /// <summary>The record of the entity whose geometry is being added; the first transform applied is its pose.</summary>
            private Part Current(SysMat world)
            {
                if (_owner == null) return null;
                if (!_parts.TryGetValue(_owner, out var p)) _parts[_owner] = p = new Part();
                if (!p.Posed) { p.Pose = world; p.Posed = true; }
                return p;
            }

            private void AddTriangle(SysMat world, SysVec a, SysVec b, SysVec c)
            {
                var wa = SysVec.Transform(a, world); var wb = SysVec.Transform(b, world); var wc = SysVec.Transform(c, world);
                Current(world)?.Triangle(wa, wb, wc);
                bool mirrored = Det3(world) < 0f;   // a mirroring transform flips the winding: keep the walkable side up
                int i0 = _verts.Count / 3;
                Push(wa); Push(mirrored ? wc : wb); Push(mirrored ? wb : wc);
                _tris.Add(i0); _tris.Add(i0 + 1); _tris.Add(i0 + 2);
            }

            /// <summary>Oriented box: local centre / half extents (scaled by the entity) and a local rotation.</summary>
            private void AddBox(SysMat world, SysVec localCenter, SysVec localHalf, SysQuat localRot)
            {
                var sc = ScaleOf(world);
                var rot = RotationOf(world);
                var wc = SysVec.Transform(localCenter, world);
                var half = new SysVec(Math.Max(0.005f, localHalf.X * sc.X), Math.Max(0.005f, localHalf.Y * sc.Y), Math.Max(0.005f, localHalf.Z * sc.Z));
                var q = SysQuat.Normalize(SysQuat.Concatenate(localRot, rot));
                Current(world)?.Box(wc, half, q);
                _boxes.Add(wc.X); _boxes.Add(wc.Y); _boxes.Add(wc.Z);
                _boxes.Add(half.X); _boxes.Add(half.Y); _boxes.Add(half.Z);
                _boxes.Add(q.X); _boxes.Add(q.Y); _boxes.Add(q.Z); _boxes.Add(q.W);
            }

            /// <summary>Box whose half extents are already in world units (spheres).</summary>
            private void AddBoxWorldHalf(SysMat world, SysVec localCenter, SysVec worldHalf)
            {
                var rot = RotationOf(world);
                var wc = SysVec.Transform(localCenter, world);
                Current(world)?.Box(wc, new SysVec(Math.Max(0.005f, worldHalf.X), Math.Max(0.005f, worldHalf.Y), Math.Max(0.005f, worldHalf.Z)), rot);
                _boxes.Add(wc.X); _boxes.Add(wc.Y); _boxes.Add(wc.Z);
                _boxes.Add(Math.Max(0.005f, worldHalf.X)); _boxes.Add(Math.Max(0.005f, worldHalf.Y)); _boxes.Add(Math.Max(0.005f, worldHalf.Z));
                _boxes.Add(rot.X); _boxes.Add(rot.Y); _boxes.Add(rot.Z); _boxes.Add(rot.W);
            }

            private void Push(SysVec v) { _verts.Add(v.X); _verts.Add(v.Y); _verts.Add(v.Z); }

            private float[] ModelTriangles(string meshPath)
            {
                if (_modelCache.TryGetValue(meshPath, out var cached)) return cached;
                float[] tris = null;
                try { tris = CollisionService.MeshTriangleProvider?.Invoke(meshPath); } catch { }
                if (tris == null)
                {
                    try
                    {
                        var actual = meshPath;
                        int h = actual.LastIndexOf('#'); if (h > 0) actual = actual.Substring(0, h);
                        var proj = ProjectData.Current?.Path;
                        var abs = Path.IsPathRooted(actual) ? actual : (proj != null ? Path.Combine(proj, actual) : actual);
                        var ext = Path.GetExtension(actual)?.TrimStart('.');
                        if (AssetVfs.IsMounted && AssetVfs.TryGetBytes(abs, out var bytes) && bytes != null) tris = VortexAPI.GetModelTrianglesFromMemory(bytes, ext);
                        else if (File.Exists(abs)) tris = VortexAPI.GetModelTriangles(abs);
                    }
                    catch { }
                }
                _modelCache[meshPath] = tris;
                return tris;
            }

            private void Note(GameEntity e, string text)
            {
                if (_out.Notes.Count < 50) _out.Notes.Add("'" + (e?.Name ?? "?") + "': " + text);
            }

            public NavBakeGeometry Finish()
            {
                _out.Verts = _verts.ToArray();
                _out.Tris = _tris.ToArray();
                _out.TriangleCount = _tris.Count / 3;
                _out.Boxes = _boxes.ToArray();
                _out.BoxCount = _boxes.Count / 10;
                _out.EntityCount = _contributors.Count;
                foreach (var kv in _parts)
                {
                    var p = kv.Value;
                    if (p.Triangles == 0 && p.Boxes == 0) continue;
                    var o = new NavGeometryOwner { Id = kv.Key.Id, Name = kv.Key.Name, Signature = Signature(kv.Key, p), Triangles = p.Triangles, Boxes = p.Boxes };
                    p.WriteTo(o);
                    _out.Owners.Add(o);
                }
                return _out;
            }

            /// <summary>The exact part of an entity's record (#449 review): the stored data its geometry comes from in bulk — the
            /// content of the model files it used, terrain resolution, heights and holes, foliage trunks. Stored bits, so no float
            /// noise; whatever the bake computes from them (and from transforms) is in the pose and extent, compared within a
            /// tolerance — a primitive, a collider's size or a water's depth is in there too.</summary>
            private static ulong Signature(GameEntity e, Part p)
            {
                var h = new Fnv(0);
                foreach (var m in p.Models) h.Int((long)ModelHash(m));
                if (p.Terrain && Editor.Core.Services.Terrain.TerrainService.TryGetData(e, out var data, out _) && data != null)
                {
                    h.Int(data.Resolution);
                    if (data.Heights != null) h.Bulk(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.Heights.AsSpan()));
                    if (data.HasHoles) h.Bulk(data.Holes);
                }
                if (p.Foliage)
                {
                    var cols = Editor.Core.Services.Foliage.FoliageService.Collidables(e);
                    var v = new float[cols.Count * 5];
                    for (int i = 0; i < cols.Count; i++)
                    {
                        v[i * 5] = cols[i].Base.X; v[i * 5 + 1] = cols[i].Base.Y; v[i * 5 + 2] = cols[i].Base.Z;
                        v[i * 5 + 3] = cols[i].Radius; v[i * 5 + 4] = cols[i].Height;
                    }
                    h.Int(cols.Count);
                    h.Bulk(System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.AsSpan()));
                }
                return h.Value;
            }

            /// <summary>FNV-1a over stored values; a bulk array goes in as its SHA-256.</summary>
            private struct Fnv
            {
                public ulong Value;
                public Fnv(int _) { Value = 14695981039346656037UL; }
                private void Byte(byte b) { Value ^= b; Value *= 1099511628211UL; }
                public void Int(long v) { for (int i = 0; i < 8; i++) Byte((byte)(v >> (8 * i))); }
                public void Bulk(ReadOnlySpan<byte> b) { Int(b.Length); Int(BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(b), 0)); }
            }

            /// <summary>What one entity added (#449 review): its counts, the transform the bake applied, the world bounds, the sum
            /// of its triangle and box centres, its area plus volume, and the stored data it was read from.</summary>
            private sealed class Part
            {
                public int Triangles, Boxes;
                public bool Posed, Terrain, Foliage;
                public SysMat Pose;
                public readonly List<string> Models = new List<string>(1);
                private double _minX = double.MaxValue, _minY = double.MaxValue, _minZ = double.MaxValue;
                private double _maxX = double.MinValue, _maxY = double.MinValue, _maxZ = double.MinValue;
                private double _sumX, _sumY, _sumZ, _measure;

                public void Triangle(SysVec a, SysVec b, SysVec c)
                {
                    Triangles++;
                    Include(a.X, a.Y, a.Z); Include(b.X, b.Y, b.Z); Include(c.X, c.Y, c.Z);
                    _sumX += ((double)a.X + b.X + c.X) / 3.0;
                    _sumY += ((double)a.Y + b.Y + c.Y) / 3.0;
                    _sumZ += ((double)a.Z + b.Z + c.Z) / 3.0;
                    double ux = (double)b.X - a.X, uy = (double)b.Y - a.Y, uz = (double)b.Z - a.Z;
                    double vx = (double)c.X - a.X, vy = (double)c.Y - a.Y, vz = (double)c.Z - a.Z;
                    double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                    _measure += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
                }

                /// <summary>A solid box: world centre, half extents and rotation.</summary>
                public void Box(SysVec centre, SysVec half, SysQuat rotation)
                {
                    Boxes++;
                    var m = SysMat.CreateFromQuaternion(rotation);   // row i: where the box's local axis i points
                    double ex = Math.Abs(m.M11) * half.X + Math.Abs(m.M21) * half.Y + Math.Abs(m.M31) * half.Z;
                    double ey = Math.Abs(m.M12) * half.X + Math.Abs(m.M22) * half.Y + Math.Abs(m.M32) * half.Z;
                    double ez = Math.Abs(m.M13) * half.X + Math.Abs(m.M23) * half.Y + Math.Abs(m.M33) * half.Z;
                    Include(centre.X - ex, centre.Y - ey, centre.Z - ez);
                    Include(centre.X + ex, centre.Y + ey, centre.Z + ez);
                    _sumX += centre.X; _sumY += centre.Y; _sumZ += centre.Z;
                    _measure += 8.0 * half.X * half.Y * half.Z;
                }

                private void Include(double x, double y, double z)
                {
                    if (x < _minX) _minX = x; if (y < _minY) _minY = y; if (z < _minZ) _minZ = z;
                    if (x > _maxX) _maxX = x; if (y > _maxY) _maxY = y; if (z > _maxZ) _maxZ = z;
                }

                public void WriteTo(NavGeometryOwner o)
                {
                    var m = Pose;
                    o.Pose[0] = m.M11; o.Pose[1] = m.M12; o.Pose[2] = m.M13;
                    o.Pose[3] = m.M21; o.Pose[4] = m.M22; o.Pose[5] = m.M23;
                    o.Pose[6] = m.M31; o.Pose[7] = m.M32; o.Pose[8] = m.M33;
                    o.Pose[9] = m.M41; o.Pose[10] = m.M42; o.Pose[11] = m.M43;
                    double n = Math.Max(1, Triangles + Boxes);
                    o.Extent[0] = (float)_minX; o.Extent[1] = (float)_minY; o.Extent[2] = (float)_minZ;
                    o.Extent[3] = (float)_maxX; o.Extent[4] = (float)_maxY; o.Extent[5] = (float)_maxZ;
                    o.Extent[6] = (float)(_sumX / n); o.Extent[7] = (float)(_sumY / n); o.Extent[8] = (float)(_sumZ / n);
                    o.Extent[9] = (float)_measure;
                }
            }

            private static SysVec ScaleOf(SysMat m)
                => new SysVec(new SysVec(m.M11, m.M12, m.M13).Length(), new SysVec(m.M21, m.M22, m.M23).Length(), new SysVec(m.M31, m.M32, m.M33).Length());

            /// <summary>The entity's world rotation. A mirroring scale is folded out (boxes are symmetric, so un-mirroring
            /// one axis keeps the shape and yields a proper rotation).</summary>
            private static SysQuat RotationOf(SysMat world)
            {
                var b = Animation.BoneSocketService.NormalizeBasis(world);
                if (Det3(b) < 0f) { b.M11 = -b.M11; b.M12 = -b.M12; b.M13 = -b.M13; }
                return SysQuat.Normalize(SysQuat.CreateFromRotationMatrix(b));
            }

            private static float Det3(SysMat m)
                => m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);
        }
    }
}
