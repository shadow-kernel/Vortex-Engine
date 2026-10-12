using System;
using System.Collections.Generic;
using Vector3 = System.Numerics.Vector3;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Editor.Core.Areas;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components.World;

namespace Editor.Core.Services.Areas
{
    /// <summary>One area as the runtime sees it: the component, its resolved definition and its world → local matrix.</summary>
    public sealed class AreaInstance
    {
        public GameEntity Entity;
        public Area Component;
        public AreaDefinition Definition;
        public Matrix4x4 WorldToLocal;
        public Vector3 WorldPosition;
        public bool Contains(Vector3 worldPoint)
        {
            var l = Vector3.Transform(worldPoint, WorldToLocal);
            var d = Definition;
            switch (d.Shape)
            {
                case AreaShape.Sphere: { float r = d.SizeX * 0.5f; return l.LengthSquared() <= r * r; }
                case AreaShape.Cylinder: { float r = d.SizeX * 0.5f; return Math.Abs(l.Y) <= d.SizeY * 0.5f && l.X * l.X + l.Z * l.Z <= r * r; }
                default: return Math.Abs(l.X) <= d.SizeX * 0.5f && Math.Abs(l.Y) <= d.SizeY * 0.5f && Math.Abs(l.Z) <= d.SizeZ * 0.5f;
            }
        }
        public bool HasTag(string tag) => !string.IsNullOrEmpty(tag) && Definition.Tags.Contains(tag.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// The areas of the live scene and who is inside them (#zones). Every tick the service gathers the Area components,
    /// their world matrices and the tracked entities (every entity that has a behaviour, plus anything a script registered)
    /// and compares: a point that enters an area raises Enter, stays raise Stay with the tick's dt, leaving raises Exit —
    /// on the entity's behaviours (OnAreaEnter / OnAreaStay / OnAreaExit) and on the global events (Entered / Exited) a
    /// manager script subscribes to. Scripts also ask directly: inside a "cave"? which areas at this point? The engine
    /// never interprets a tag; a cave is a cave because a script says so.
    /// </summary>
    public static class AreaService
    {
        /// <summary>Raised from the script runtime's tick: (entity, area) on entering / leaving an area.</summary>
        public static event Action<GameEntity, AreaInstance> Entered;
        public static event Action<GameEntity, AreaInstance> Exited;
        /// <summary>Per-entity behaviour dispatch — set by the script runtime (it owns the behaviour lists).</summary>
        public static Action<GameEntity, AreaInstance, int, float> Dispatch;   // kind: 0 enter, 1 stay, 2 exit

        private static readonly List<AreaInstance> _areas = new List<AreaInstance>();
        private static readonly Dictionary<GameEntity, HashSet<Area>> _inside = new Dictionary<GameEntity, HashSet<Area>>();
        private static readonly HashSet<GameEntity> _tracked = new HashSet<GameEntity>();
        private static readonly List<GameEntity> _trackedNow = new List<GameEntity>();
        private static readonly Dictionary<Area, AreaDefinition> _defCache = new Dictionary<Area, AreaDefinition>();
        private static readonly Dictionary<Area, string> _defKey = new Dictionary<Area, string>();
        private static float _refreshT;
        public static int AreaCount => _areas.Count;
        public static IReadOnlyList<AreaInstance> Areas => _areas;

        /// <summary>A script asks to be tracked (an entity without behaviours, e.g. a camera rig or a projectile).</summary>
        public static void Track(GameEntity e) { if (e != null) _tracked.Add(e); }
        public static void Untrack(GameEntity e) { if (e != null) { _tracked.Remove(e); _inside.Remove(e); } }

        /// <summary>Forget everything (play stopped, scene switched).</summary>
        public static void Reset()
        {
            _areas.Clear(); _inside.Clear(); _tracked.Clear(); _defCache.Clear(); _defKey.Clear(); _refreshT = 0f;
        }

        /// <summary>Gather the scene's areas (cheap; done every tick so a moved area counts at once).</summary>
        public static void Refresh(Scene scene, Func<GameEntity, float[]> worldMatrixOf)
        {
            _areas.Clear();
            if (scene == null) return;
            string projectPath = ProjectData.Current?.Path;
            foreach (var root in scene.Entities) Walk(root, projectPath, worldMatrixOf);
        }

        private static void Walk(GameEntity e, string projectPath, Func<GameEntity, float[]> worldMatrixOf)
        {
            if (e == null) return;
            if (e.ActiveInHierarchy)
            {
                var a = e.GetComponent<Area>();
                if (a != null && a.IsEnabled)
                {
                    var inst = Build(e, a, projectPath, worldMatrixOf);
                    if (inst != null) _areas.Add(inst);
                }
            }
            if (e.Children != null) foreach (var c in e.Children) Walk(c, projectPath, worldMatrixOf);
        }

        private static AreaInstance Build(GameEntity e, Area a, string projectPath, Func<GameEntity, float[]> worldMatrixOf)
        {
            // the definition is re-read when the component's own values or its file reference change
            string key = a.DataPath + "|" + a.Shape + "|" + a.SizeX + "|" + a.SizeY + "|" + a.SizeZ + "|" + a.Tags + "|" + a.Properties + "|" + a.Priority;
            if (!_defKey.TryGetValue(a, out var old) || old != key || !_defCache.ContainsKey(a))
            {
                try { _defCache[a] = a.Resolve(projectPath); } catch { _defCache[a] = null; }
                _defKey[a] = key;
            }
            var def = _defCache[a];
            if (def == null) return null;
            float[] m = null;
            try { m = worldMatrixOf?.Invoke(e); } catch { }
            Matrix4x4 world;
            if (m != null && m.Length >= 16)
                world = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            else
            {
                var p = e.Transform != null ? e.Transform.LocalPosition : new Editor.ECS.Vector3(0f, 0f, 0f);
                world = Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
            }
            Matrix4x4 inv;
            if (!Matrix4x4.Invert(world, out inv)) inv = Matrix4x4.Identity;
            return new AreaInstance { Entity = e, Component = a, Definition = def, WorldToLocal = inv, WorldPosition = new Vector3(world.M41, world.M42, world.M43) };
        }

        /// <summary>One tick: the tracked entities against the areas, the events for every change.</summary>
        public static void Tick(float dt, IEnumerable<GameEntity> scripted, Func<GameEntity, Vector3?> positionOf)
        {
            _trackedNow.Clear();
            if (scripted != null) foreach (var e in scripted) if (e != null && e.ActiveInHierarchy) _trackedNow.Add(e);
            foreach (var e in _tracked) if (e.ActiveInHierarchy && !_trackedNow.Contains(e)) _trackedNow.Add(e);
            bool anyTrackAll = false;
            foreach (var a in _areas) if (a.Component.TrackAll) { anyTrackAll = true; break; }
            foreach (var e in _trackedNow)
            {
                var p = positionOf?.Invoke(e);
                if (!p.HasValue) continue;
                HashSet<Area> was;
                if (!_inside.TryGetValue(e, out was)) { was = new HashSet<Area>(); _inside[e] = was; }
                foreach (var a in _areas)
                {
                    bool eligible = a.Component.TrackAll ? true : _tracked.Contains(e);
                    if (!eligible && !anyTrackAll) continue;
                    if (!eligible) continue;
                    bool inside = a.Contains(p.Value);
                    bool before = was.Contains(a.Component);
                    if (inside && !before) { was.Add(a.Component); Raise(e, a, 0, dt); }
                    else if (inside) Raise(e, a, 1, dt);
                    else if (before) { was.Remove(a.Component); Raise(e, a, 2, dt); }
                }
                // an area that vanished (deleted, disabled) counts as left
                if (was.Count > 0)
                {
                    List<Area> gone = null;
                    foreach (var c in was) { bool still = false; foreach (var a in _areas) if (a.Component == c) { still = true; break; } if (!still) (gone ?? (gone = new List<Area>())).Add(c); }
                    if (gone != null) foreach (var c in gone) was.Remove(c);
                }
            }
        }

        private static void Raise(GameEntity e, AreaInstance a, int kind, float dt)
        {
            try { Dispatch?.Invoke(e, a, kind, dt); } catch { }
            try { if (kind == 0) Entered?.Invoke(e, a); else if (kind == 2) Exited?.Invoke(e, a); } catch { }
        }

        // ---------------------------------------------------------------- queries (scripts)

        /// <summary>Every area containing the point, highest priority first.</summary>
        public static List<AreaInstance> At(Vector3 worldPoint)
        {
            var list = new List<AreaInstance>();
            foreach (var a in _areas) if (a.Contains(worldPoint)) list.Add(a);
            list.Sort((x, y) => y.Definition.Priority.CompareTo(x.Definition.Priority));
            return list;
        }
        public static bool IsInside(Vector3 worldPoint, string tag)
        {
            foreach (var a in _areas) if ((string.IsNullOrEmpty(tag) || a.HasTag(tag)) && a.Contains(worldPoint)) return true;
            return false;
        }
        /// <summary>The areas an entity is known to be in right now (from the last tick).</summary>
        public static List<AreaInstance> Of(GameEntity e)
        {
            var list = new List<AreaInstance>();
            if (e == null || !_inside.TryGetValue(e, out var set)) return list;
            foreach (var a in _areas) if (set.Contains(a.Component)) list.Add(a);
            list.Sort((x, y) => y.Definition.Priority.CompareTo(x.Definition.Priority));
            return list;
        }
        public static AreaInstance Find(string nameOrTag)
        {
            if (string.IsNullOrEmpty(nameOrTag)) return null;
            foreach (var a in _areas) if (string.Equals(a.Entity?.Name, nameOrTag, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Definition.Name, nameOrTag, StringComparison.OrdinalIgnoreCase)) return a;
            foreach (var a in _areas) if (a.HasTag(nameOrTag)) return a;
            return null;
        }
    }
}
