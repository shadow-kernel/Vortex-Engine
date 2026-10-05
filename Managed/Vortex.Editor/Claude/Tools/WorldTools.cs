using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.Editors.WorldEditor.Components.SceneHierarchy;
using ModelContextProtocol.Server;
using VortexEditor.Services;
using AssetActions = VortexEditor.Services.AssetActions;
using VortexEditor.Shell;

namespace VortexEditor.Claude.Tools
{
    /// <summary>
    /// World building (#89): macros that build levels in one call — grids, scattering on surfaces, alignment, snapping,
    /// bulk edits, measuring. Each call is ONE undo step however many entities it creates, and the same seed gives the
    /// same layout.
    /// </summary>
    [McpServerToolType, DisplayName("World")]
    public static class WorldTools
    {
        public const int MaxInstances = 2000;

        // ================================================================== grid

        [McpServerTool(Name = "place_grid", Destructive = false)]
        [Description("Places rows × cols copies in a grid (rows along Z, cols along X) inside a new group entity — floor tiles, wall segments, " +
                     "pillars, ceiling lights. source: a kind (cube, point_light, …), a project asset (model / .ventity prefab / Primitive:Cube) " +
                     "or an existing entity (id/path) to copy. One undo step.")]
        public static object PlaceGrid(
            [Description("What to place")] string source,
            [Description("World position of the first copy [x, y, z]")] float[] origin,
            [Description("Rows (along +Z)")] int rows,
            [Description("Columns (along +X)")] int cols,
            [Description("Spacing in metres: [x, z] or one number for both")] float[] spacing,
            [Description("Rotation of every copy [x, y, z] degrees")] float[] rotation = null,
            [Description("Scale of every copy [x, y, z] (or [s])")] float[] scale = null,
            [Description("Name of the group (and prefix of the copies)")] string name = null,
            [Description("Parent of the group")] string parent = null,
            [Description("Only report what would be created")] bool dry_run = false)
        {
            if (rows < 1 || cols < 1) throw new ToolError("rows and cols must be at least 1.");
            if ((long)rows * cols > MaxInstances) throw new ToolError("At most " + MaxInstances + " copies per call (asked for " + (long)rows * cols + ").");
            var o = SceneModel.Vec(origin, "origin") ?? throw new ToolError("origin [x, y, z] is required.");
            if (spacing == null || spacing.Length == 0 || spacing.Length > 2) throw new ToolError("spacing is [x, z] or one number.");
            float sx = spacing[0], sz = spacing.Length > 1 ? spacing[1] : spacing[0];
            var positions = new List<Vector3>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    positions.Add(new Vector3(o.X + c * sx, o.Y, o.Z + r * sz));
            if (dry_run) return Plan(source, positions, "grid " + rows + "×" + cols);
            var rot = SceneModel.Vec(rotation, "rotation");
            var scl = SceneModel.Vec(scale, "scale");
            var created = Spawn(source, positions.Select(p => (p, rot, scl)).ToList(), name ?? DefaultName(source) + " Grid", parent, out var group);
            ToolContext.UndoLabel = "grid of " + created.Count + " " + DefaultName(source);
            return Summary(group, created);
        }

        // ================================================================== scatter

        [McpServerTool(Name = "scatter", Destructive = false)]
        [Description("Scatters count copies over an area: each lands on the highest surface below from_height (the tops of mesh bounding " +
                     "boxes — floors, platforms, tables) with optional random yaw and scale. Debris, rocks, crates, bottles, props. Deterministic for a given seed. " +
                     "source as in place_grid. One undo step.")]
        public static object Scatter(
            [Description("What to scatter")] string source,
            [Description("Area corner [x, z] (world)")] float[] area_min,
            [Description("Opposite area corner [x, z] (world)")] float[] area_max,
            [Description("Number of copies")] int count,
            [Description("Random seed — same seed, same layout")] int seed = 1,
            [Description("Random rotation around Y")] bool random_yaw = true,
            [Description("Random uniform scale range [min, max] (default [1, 1])")] float[] scale_range = null,
            [Description("Keep at least this distance (m) between copies")] float min_distance = 0f,
            [Description("Height to use where nothing is below (default: skip that spot)")] float? fallback_y = null,
            [Description("World height the downward rays start at (default: above everything). Indoors, use a height below the ceiling.")] float? from_height = null,
            [Description("Name of the group")] string name = null,
            [Description("Parent of the group")] string parent = null,
            [Description("Only report what would be created")] bool dry_run = false)
        {
            if (count < 1 || count > MaxInstances) throw new ToolError("count must be 1–" + MaxInstances + ".");
            if (area_min == null || area_max == null || area_min.Length != 2 || area_max.Length != 2) throw new ToolError("area_min and area_max are [x, z].");
            float x0 = Math.Min(area_min[0], area_max[0]), x1 = Math.Max(area_min[0], area_max[0]);
            float z0 = Math.Min(area_min[1], area_max[1]), z1 = Math.Max(area_min[1], area_max[1]);
            float smin = 1f, smax = 1f;
            if (scale_range != null)
            {
                if (scale_range.Length != 2 || scale_range[0] <= 0 || scale_range[1] < scale_range[0]) throw new ToolError("scale_range is [min, max] with 0 < min ≤ max.");
                smin = scale_range[0]; smax = scale_range[1];
            }
            var scene = SceneModel.ActiveScene;
            var rng = new Random(seed);
            var spots = new List<(Vector3 p, Vector3? r, Vector3? s)>();
            int attempts = 0, skipped = 0;
            var surfaces = Surfaces(scene);
            float top = from_height ?? surfaces.Select(b => b.max.Y).DefaultIfEmpty(0f).Max() + 1f;
            while (spots.Count < count && attempts < count * 30)
            {
                attempts++;
                float x = (float)(x0 + rng.NextDouble() * (x1 - x0)), z = (float)(z0 + rng.NextDouble() * (z1 - z0));
                float yaw = random_yaw ? (float)(rng.NextDouble() * 360.0) : 0f;
                float s = smin + (float)rng.NextDouble() * (smax - smin);
                if (min_distance > 0 && spots.Any(q => (q.p.X - x) * (q.p.X - x) + (q.p.Z - z) * (q.p.Z - z) < min_distance * min_distance)) continue;
                float? y = SurfaceY(surfaces, x, z, top) ?? fallback_y;
                if (!y.HasValue) { skipped++; continue; }
                spots.Add((new Vector3(x, y.Value, z), random_yaw ? new Vector3(0, yaw, 0) : (Vector3?)null, smin != 1f || smax != 1f ? new Vector3(s) : (Vector3?)null));
            }
            if (spots.Count == 0) throw new ToolError("No surface below the area (x " + x0 + "…" + x1 + ", z " + z0 + "…" + z1 + "). Give fallback_y or check the area.");
            if (dry_run)
            {
                var plan = (Dictionary<string, object>)Plan(source, spots.Select(q => q.p).ToList(), "scatter");
                plan["spots_without_surface"] = skipped;
                return plan;
            }
            var created = Spawn(source, spots, name ?? DefaultName(source) + " Scatter", parent, out var group, restOnSurface: true);
            ToolContext.UndoLabel = "scatter " + created.Count + " " + DefaultName(source);
            var result = (Dictionary<string, object>)Summary(group, created);
            if (spots.Count < count) result["note"] = "placed " + spots.Count + " of " + count + " (min_distance / no surface)";
            return result;
        }

        /// <summary>World boxes of every active mesh in the scene — the surfaces scatter lands on (exact mesh bounds,
        /// not the inflated click hitboxes of the viewport picker).</summary>
        private static List<(Vector3 min, Vector3 max)> Surfaces(Scene scene)
        {
            var list = new List<(Vector3, Vector3)>();
            foreach (var e in SceneModel.All(scene))
                if (e.ActiveInHierarchy && !e.IsHiddenInEditor && Bounds.OwnWorldBox(e, out var mn, out var mx)) list.Add((mn, mx));
            return list;
        }

        /// <summary>The highest surface top at (x, z) that lies below <paramref name="fromY"/>.</summary>
        private static float? SurfaceY(List<(Vector3 min, Vector3 max)> surfaces, float x, float z, float fromY)
        {
            float? best = null;
            foreach (var (mn, mx) in surfaces)
            {
                if (x < mn.X || x > mx.X || z < mn.Z || z > mx.Z || mx.Y > fromY) continue;
                if (best == null || mx.Y > best.Value) best = mx.Y;
            }
            return best;
        }

        // ================================================================== align / snap

        [McpServerTool(Name = "align_entities", Idempotent = true)]
        [Description("Aligns entities on one world axis: 'min' / 'center' / 'max' line up their bounds with the first entity's, " +
                     "'distribute' spaces them evenly between the two outermost. One undo step.")]
        public static object AlignEntities(
            [Description("Entities (at least 2)")] string[] entities,
            [Description("x, y or z")] string axis,
            [Description("min, center, max or distribute")] string mode = "center")
        {
            var list = SceneModel.ResolveMany(entities);
            if (list.Count < 2) throw new ToolError("Give at least two entities.");
            int a = AxisIndex(axis);
            var boxes = list.Select(e => { Bounds.Of(new[] { e }, out var mn, out var mx); return (e, mn: Get(mn, a), mx: Get(mx, a)); }).ToList();
            switch ((mode ?? "").ToLowerInvariant())
            {
                case "min": { float t = boxes[0].mn; foreach (var b in boxes.Skip(1)) Shift(b.e, a, t - b.mn); break; }
                case "max": { float t = boxes[0].mx; foreach (var b in boxes.Skip(1)) Shift(b.e, a, t - b.mx); break; }
                case "center": { float t = (boxes[0].mn + boxes[0].mx) / 2; foreach (var b in boxes.Skip(1)) Shift(b.e, a, t - (b.mn + b.mx) / 2); break; }
                case "distribute":
                {
                    var sorted = boxes.OrderBy(b => (b.mn + b.mx) / 2).ToList();
                    float c0 = (sorted[0].mn + sorted[0].mx) / 2, c1 = (sorted[^1].mn + sorted[^1].mx) / 2;
                    for (int i = 1; i < sorted.Count - 1; i++)
                    {
                        float target = c0 + (c1 - c0) * i / (sorted.Count - 1);
                        Shift(sorted[i].e, a, target - (sorted[i].mn + sorted[i].mx) / 2);
                    }
                    break;
                }
                default: throw new ToolError("mode must be min, center, max or distribute.");
            }
            ToolContext.UndoLabel = "align " + list.Count + " entities (" + axis + ")";
            return list.Select(e => new { id = SceneModel.ShortId(e), position = ToolJson.V(TransformMath.WorldPosition(e)) }).ToArray();
        }

        [McpServerTool(Name = "snap_to_grid", Idempotent = true)]
        [Description("Rounds entities' world positions to a grid (cell size in metres) on the given axes. One undo step.")]
        public static object SnapToGrid(
            [Description("Entities")] string[] entities,
            [Description("Cell size in metres")] float cell = 1f,
            [Description("Axes to snap, e.g. \"xz\" or \"xyz\"")] string axes = "xyz")
        {
            if (!(cell > 0)) throw new ToolError("cell must be > 0.");
            var list = SceneModel.ResolveMany(entities);
            foreach (var e in list)
            {
                var w = TransformMath.WorldPosition(e);
                float Snap(float v) => (float)Math.Round(v / cell) * cell;
                var target = new Vector3(axes.Contains('x') ? Snap(w.X) : w.X, axes.Contains('y') ? Snap(w.Y) : w.Y, axes.Contains('z') ? Snap(w.Z) : w.Z);
                MoveWorld(e, target);
            }
            ToolContext.UndoLabel = "snap " + list.Count + " entities to " + cell + " m";
            return list.Select(e => new { id = SceneModel.ShortId(e), position = ToolJson.V(TransformMath.WorldPosition(e)) }).ToArray();
        }

        // ================================================================== bulk edit / measure

        [McpServerTool(Name = "bulk_set_properties", Idempotent = true)]
        [Description("Sets component properties on EVERY entity matching the filters (name with * wildcards, tag, component, under) that has " +
                     "that component — e.g. dim all ceiling lights: name \"Lamp*\", component Light, properties {\"intensity\": 0.6}. " +
                     "Reports the affected count; dry_run lists the matches. One undo step.")]
        public static object BulkSetProperties(
            [Description("Component type to edit, e.g. Light")] string component,
            [Description("Property values {\"name\": value}")] JsonElement properties,
            [Description("Name filter ('Lamp*', substring)")] string name = null,
            [Description("Tag filter")] string tag = null,
            [Description("Only descendants of this entity")] string under = null,
            [Description("Only report the matches")] bool dry_run = false)
        {
            if (name == null && tag == null && under == null) throw new ToolError("Give at least one filter (name, tag or under) — bulk edits on the whole scene are refused.");
            var type = ComponentProps.FindType(component);
            var matches = SceneTools.Query(name, tag, type.Name, under);
            if (dry_run) return new { dry_run = true, would_change = matches.Count, entities = matches.Take(50).Select(SceneModel.Brief).ToArray() };
            int n = 0;
            foreach (var e in matches)
                foreach (var c in e.Components.Where(x => type.IsInstanceOfType(x)).ToList()) { SceneTools.SetProps(c, properties); n++; }
            if (n == 0) throw new ToolError("No entity matched (with a " + type.Name + ").");
            ToolContext.UndoLabel = "edit " + n + " " + type.Name + " components";
            return new { changed = n, entities = matches.Take(30).Select(e => SceneModel.ShortId(e) + " " + e.Name).ToArray() };
        }

        [McpServerTool(Name = "get_bounds", ReadOnly = true, Idempotent = true)]
        [Description("World-space bounding box (min, max, size, center in metres) of entities including their children — to plan where things fit.")]
        public static object GetBounds([Description("Entities")] string[] entities)
        {
            var list = SceneModel.ResolveMany(entities);
            Bounds.Of(list, out var mn, out var mx);
            return new
            {
                min = ToolJson.V(mn), max = ToolJson.V(mx),
                size = ToolJson.V(new Vector3(mx.X - mn.X, mx.Y - mn.Y, mx.Z - mn.Z)),
                center = ToolJson.V(new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2)),
            };
        }

        [McpServerTool(Name = "measure", ReadOnly = true, Idempotent = true)]
        [Description("Distance between two entities: between their positions, and the gap between their bounding boxes (0 when they touch or overlap).")]
        public static object Measure([Description("First entity")] string a, [Description("Second entity")] string b)
        {
            var ea = SceneModel.Resolve(a); var eb = SceneModel.Resolve(b);
            var pa = TransformMath.WorldPosition(ea); var pb = TransformMath.WorldPosition(eb);
            var d = new Vector3(pb.X - pa.X, pb.Y - pa.Y, pb.Z - pa.Z);
            Bounds.Of(new[] { ea }, out var amin, out var amax);
            Bounds.Of(new[] { eb }, out var bmin, out var bmax);
            float gx = Math.Max(0, Math.Max(bmin.X - amax.X, amin.X - bmax.X));
            float gy = Math.Max(0, Math.Max(bmin.Y - amax.Y, amin.Y - bmax.Y));
            float gz = Math.Max(0, Math.Max(bmin.Z - amax.Z, amin.Z - bmax.Z));
            return new
            {
                distance = ToolJson.R(d.Magnitude),
                delta = ToolJson.V(d),
                horizontal_distance = ToolJson.R((float)Math.Sqrt(d.X * d.X + d.Z * d.Z)),
                bounds_gap = ToolJson.R((float)Math.Sqrt(gx * gx + gy * gy + gz * gz)),
            };
        }

        // ================================================================== spawning

        /// <summary>Create the copies inside a new group: the template is made once (kind / asset) or taken from the
        /// scene (copy of an entity) and duplicated per spot — one model load, linked prefab instances stay linked.</summary>
        private static List<GameEntity> Spawn(string source, List<(Vector3 p, Vector3? r, Vector3? s)> spots, string groupName, string parentRef,
                                              out GameEntity group, bool restOnSurface = false)
        {
            var scene = SceneModel.ActiveScene;
            var parent = parentRef != null ? SceneModel.Resolve(parentRef) : null;
            group = scene.CreateEntity(groupName);
            group.IsFolder = true;
            if (parent != null) UndoRedoManager.Instance.Execute(new MoveEntityCommand(group, parent));
            var template = Template(scene, source, out bool templateIsNew);
            var created = new List<GameEntity>();
            var exclude = new HashSet<GameEntity>();
            for (int i = 0; i < spots.Count; i++)
            {
                GameEntity e;
                if (i == 0 && templateIsNew) e = template;
                else
                {
                    var dup = new DuplicateEntitiesCommand(new[] { template });
                    UndoRedoManager.Instance.Execute(dup);
                    e = dup.Copies.FirstOrDefault() ?? throw new ToolError("Could not copy " + template.Name + ".");
                }
                UndoRedoManager.Instance.Execute(new MoveEntityCommand(e, group));
                string baseName = templateIsNew ? template.Name : DefaultName(source);
                SceneModel.Set(e, "Name", () => e.Name, v => e.Name = v, baseName + " " + (i + 1));
                var (p, r, s) = spots[i];
                // group sits at the origin (or under its parent): convert the world spot into the group's space
                var local = ToLocal(group, p);
                SceneModel.SetTransform(e, local, r ?? (templateIsNew || i > 0 ? (Vector3?)null : template.Transform.LocalRotation), s);
                if (restOnSurface)
                {
                    // lift / lower the copy so its bounds rest on the hit point
                    Bounds.Of(new[] { e }, out var mn, out var _);
                    if (float.IsFinite(mn.Y) && Math.Abs(mn.Y - p.Y) > 1e-4f) Shift(e, 1, p.Y - mn.Y);
                }
                created.Add(e);
                exclude.Add(e);
            }
            EditorCommands.SelectMany(new List<GameEntity> { group });
            return created;
        }

        private static GameEntity Template(Scene scene, string source, out bool isNew)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ToolError("source is required: a kind (cube, point_light, …), a project asset path or an entity to copy.");
            string s = source.Trim();
            string kindKey = s.ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            if (SceneTools.Kinds.Contains(kindKey)) { isNew = true; return SceneTools.Make(scene, kindKey); }
            if (s.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) || s.Contains('/') && Path.HasExtension(s) || Path.HasExtension(s) && !s.Contains(' '))
            {
                string path = s.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) ? s : ProjectFiles.Resolve(s, mustExist: true);
                if (!AssetActions.CanAddToScene(path)) throw new ToolError(s + " cannot be placed — use a model, a .ventity prefab or a primitive.");
                isNew = true;
                return AssetActions.AddToScene(path, new Vector3(0, 0, 0)) ?? throw new ToolError("Could not place " + s + ".");
            }
            isNew = false;
            return SceneModel.Resolve(s);
        }

        private static string DefaultName(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return "Item";
            string s = source.Trim();
            if (s.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return s.Substring(10);
            if (Path.HasExtension(s)) return Path.GetFileNameWithoutExtension(s);
            if (SceneTools.Kinds.Contains(s.ToLowerInvariant().Replace(' ', '_')))
                return string.Join(" ", s.Split('_', ' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1)));
            var ent = s.Contains('/') ? s.Substring(s.LastIndexOf('/') + 1) : s;
            return ent;
        }

        private static object Plan(string source, List<Vector3> positions, string what)
        {
            Vector3 mn = positions[0], mx = positions[0];
            foreach (var p in positions)
            {
                mn = new Vector3(Math.Min(mn.X, p.X), Math.Min(mn.Y, p.Y), Math.Min(mn.Z, p.Z));
                mx = new Vector3(Math.Max(mx.X, p.X), Math.Max(mx.Y, p.Y), Math.Max(mx.Z, p.Z));
            }
            return new Dictionary<string, object>
            {
                ["dry_run"] = true,
                ["would_create"] = positions.Count,
                ["of"] = DefaultName(source),
                ["layout"] = what,
                ["positions_min"] = ToolJson.V(mn),
                ["positions_max"] = ToolJson.V(mx),
                ["first_positions"] = positions.Take(10).Select(ToolJson.V).ToArray(),
            };
        }

        private static object Summary(GameEntity group, List<GameEntity> created)
        {
            Bounds.Of(new[] { group }, out var mn, out var mx);
            return new Dictionary<string, object>
            {
                ["group"] = SceneModel.Brief(group),
                ["created"] = created.Count,
                ["bounds"] = new { min = ToolJson.V(mn), max = ToolJson.V(mx) },
                ["first_ids"] = created.Take(20).Select(SceneModel.ShortId).ToArray(),
            };
        }

        // ------------------------------------------------------------------ transform helpers

        private static Vector3 ToLocal(GameEntity parent, Vector3 world)
        {
            if (parent == null) return world;
            var inv = TransformMath.InverseAffine(TransformMath.World(parent));
            return inv == null ? world : TransformMath.TransformPoint(inv, world);
        }

        private static void MoveWorld(GameEntity e, Vector3 world)
        {
            var local = e.Parent == null ? world : ToLocal(e.Parent, world);
            SceneModel.SetTransform(e, local, null, null);
        }

        private static void Shift(GameEntity e, int axis, float delta)
        {
            if (Math.Abs(delta) < 1e-6f) return;
            var w = TransformMath.WorldPosition(e);
            var target = axis == 0 ? new Vector3(w.X + delta, w.Y, w.Z) : axis == 1 ? new Vector3(w.X, w.Y + delta, w.Z) : new Vector3(w.X, w.Y, w.Z + delta);
            MoveWorld(e, target);
        }

        private static int AxisIndex(string axis)
        {
            switch ((axis ?? "").Trim().ToLowerInvariant())
            {
                case "x": return 0;
                case "y": return 1;
                case "z": return 2;
                default: throw new ToolError("axis must be x, y or z.");
            }
        }

        private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    }
}
