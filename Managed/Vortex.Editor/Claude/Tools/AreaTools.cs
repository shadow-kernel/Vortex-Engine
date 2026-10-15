using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Editor.Core.Areas;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components.World;
using ModelContextProtocol.Server;

namespace VortexEditor.Claude.Tools
{
    /// <summary>Areas (zones): place a box / sphere / cylinder region with tags and properties, list them, ask which areas
    /// contain a point, and write / read <c>.varea</c> files.</summary>
    [McpServerToolType, DisplayName("Areas")]
    public static class AreaTools
    {
        private static Area AreaOf(string entity)
        {
            var e = SceneModel.Resolve(entity);
            return e.GetComponent<Area>() ?? throw new ToolError("'" + e.Name + "' has no Area component.");
        }

        private static object Describe(GameEntity e, Area a)
        {
            var d = a.Resolve(ProjectData.Current?.Path);
            var p = e.Transform?.LocalPosition ?? Editor.ECS.Vector3.Zero;
            return new
            {
                id = SceneModel.ShortId(e), name = e.Name, position = new[] { p.X, p.Y, p.Z },
                shape = ((AreaShape)a.Shape).ToString().ToLowerInvariant(), size = new[] { a.SizeX, a.SizeY, a.SizeZ },
                tags = d.Tags.ToArray(), properties = d.Properties, priority = a.Priority, color = new[] { a.ColorR, a.ColorG, a.ColorB },
                file = string.IsNullOrEmpty(a.DataPath) ? null : a.DataPath, track_all = a.TrackAll, enabled = a.IsEnabled,
            };
        }

        [McpServerTool(Name = "create_area")]
        [Description("Creates an Area (zone) entity: a box / sphere / cylinder region of the world with tags and free properties. Scripts get " +
                     "OnAreaEnter / OnAreaStay / OnAreaExit and ask Areas.IsInside(id, tag). The region is as big as you like, and the entity moves, " +
                     "turns and scales like any other. One undo step.")]
        public static object CreateArea(
            [Description("Entity name (e.g. 'Cave', 'No-fly zone')")] string name,
            [Description("Centre position [x, y, z]")] float[] position,
            [Description("Full size [x, y, z] in metres (sphere: x = diameter; cylinder: x = diameter, y = height)")] float[] size,
            [Description("'box' (default), 'sphere' or 'cylinder'")] string shape = "box",
            [Description("Tags, e.g. ['cave', 'no-fly'] — what scripts ask for")] string[] tags = null,
            [Description("Free properties, e.g. {\"flightAllowed\": \"false\", \"ambient\": \"cave_drips\"}")] Dictionary<string, string> properties = null,
            [Description("Rotation [x, y, z] in degrees")] float[] rotation = null,
            [Description("Outline colour [r, g, b] 0..1")] float[] color = null,
            [Description("Priority when areas overlap (higher wins)")] int priority = 0,
            [Description("A .varea file to link (project-relative); its values are used instead of the ones given")] string file = null)
        {
            var scene = SceneModel.ActiveScene;
            var pos = SceneModel.Vec(position, "position") ?? throw new ToolError("position is required.");
            var e = scene.CreateEntity(string.IsNullOrWhiteSpace(name) ? "Area" : name);
            var a = new Area(e);
            a.Shape = ParseShape(shape);
            if (size != null && size.Length >= 3) { a.SizeX = size[0]; a.SizeY = size[1]; a.SizeZ = size[2]; }
            else if (size != null && size.Length == 1) { a.SizeX = a.SizeY = a.SizeZ = size[0]; }
            if (tags != null) a.Tags = string.Join(", ", tags);
            if (properties != null) a.Properties = string.Join("\n", properties.Select(kv => kv.Key + "=" + kv.Value));
            if (color != null && color.Length >= 3) { a.ColorR = color[0]; a.ColorG = color[1]; a.ColorB = color[2]; }
            a.Priority = priority;
            if (!string.IsNullOrEmpty(file))
            {
                string full = System.IO.Path.IsPathRooted(file) ? file : System.IO.Path.Combine(ProjectData.Current?.Path ?? "", file);
                if (!System.IO.File.Exists(full)) throw new ToolError("No area file '" + file + "'.");
                a.DataPath = file;
                a.ApplyDefinition(a.Resolve(ProjectData.Current?.Path));   // the file's values, as the game will use them
            }
            e.AddComponent(a);
            SceneModel.SetTransform(e, pos, SceneModel.Vec(rotation, "rotation"), null);
            ToolContext.UndoLabel = "create area " + e.Name;
            return Describe(e, a);
        }

        [McpServerTool(Name = "set_area", Idempotent = true)]
        [Description("Changes an Area's shape, size, tags, properties, colour, priority or file; omitted parts stay. One undo step.")]
        public static object SetArea(
            [Description("Entity id, path or name")] string entity,
            [Description("'box', 'sphere' or 'cylinder'")] string shape = null,
            [Description("Full size [x, y, z]")] float[] size = null,
            [Description("Tags (replaces the list)")] string[] tags = null,
            [Description("Properties (merged into the existing ones; an empty value removes a key)")] Dictionary<string, string> properties = null,
            [Description("Outline colour [r, g, b]")] float[] color = null,
            [Description("Priority")] int? priority = null,
            [Description("A .varea file to link ('' unlinks)")] string file = null,
            [Description("Track every scripted entity (default) or only registered ones")] bool? track_all = null)
        {
            var e = SceneModel.Resolve(entity);
            var a = AreaOf(entity);
            int? shapeValue = string.IsNullOrEmpty(shape) ? (int?)null : ParseShape(shape);
            if (!string.IsNullOrEmpty(file))
            {
                string full = System.IO.Path.IsPathRooted(file) ? file : System.IO.Path.Combine(ProjectData.Current?.Path ?? "", file);
                if (!System.IO.File.Exists(full)) throw new ToolError("No area file '" + file + "'.");
            }
            // the values are set directly: one undo step records them (#411 review)
            SceneModel.RecordEdit(a, "Area", () =>
            {
                if (shapeValue.HasValue) a.Shape = shapeValue.Value;
                if (size != null && size.Length >= 3) { a.SizeX = size[0]; a.SizeY = size[1]; a.SizeZ = size[2]; }
                if (tags != null) a.Tags = string.Join(", ", tags);
                if (properties != null)
                {
                    var map = AreaDefinition.ParseProperties(a.Properties);
                    foreach (var kv in properties) { if (string.IsNullOrEmpty(kv.Value)) map.Remove(kv.Key); else map[kv.Key] = kv.Value; }
                    a.Properties = string.Join("\n", map.Select(kv => kv.Key + "=" + kv.Value));
                }
                if (color != null && color.Length >= 3) { a.ColorR = color[0]; a.ColorG = color[1]; a.ColorB = color[2]; }
                if (priority.HasValue) a.Priority = priority.Value;
                if (file != null)
                {
                    a.DataPath = file;
                    // a linked file wins at run time (AreaService resolves it): show its values here too, so the outline and the
                    // inspector match what the game will use
                    if (file.Length > 0) a.ApplyDefinition(a.Resolve(ProjectData.Current?.Path));
                }
                if (track_all.HasValue) a.TrackAll = track_all.Value;
            });
            ToolContext.UndoLabel = "edit area " + e.Name;
            return Describe(e, a);
        }

        [McpServerTool(Name = "list_areas", ReadOnly = true, Idempotent = true)]
        [Description("Every Area (zone) of the active scene with its shape, size, tags, properties and file.")]
        public static object ListAreas()
        {
            var list = new List<object>();
            foreach (var e in SceneModel.All(SceneModel.ActiveScene))
            {
                var a = e.GetComponent<Area>();
                if (a != null) list.Add(Describe(e, a));
            }
            return new { count = list.Count, areas = list };
        }

        [McpServerTool(Name = "areas_at", ReadOnly = true, Idempotent = true)]
        [Description("Which areas contain a world point (highest priority first) — the same answer a script gets from Areas.At.")]
        public static object AreasAt([Description("World point [x, y, z]")] float[] point)
        {
            var p = SceneModel.Vec(point, "point") ?? throw new ToolError("point is required.");
            var srs = Editor.Core.Services.SceneRenderService.Instance;
            Editor.Core.Services.Areas.AreaService.Refresh(SceneModel.ActiveScene, e => srs?.WorldMatrixOf(e));
            var hits = Editor.Core.Services.Areas.AreaService.At(new System.Numerics.Vector3(p.X, p.Y, p.Z));
            return new { count = hits.Count, areas = hits.Select(h => new { id = SceneModel.ShortId(h.Entity), name = h.Entity.Name, tags = h.Definition.Tags.ToArray(), priority = h.Definition.Priority }) };
        }

        [McpServerTool(Name = "save_area_file")]
        [Description("Writes an Area's values to a .varea file (Assets/Areas/<name>.varea by default) and links it, so other areas can share the definition.")]
        public static object SaveAreaFile(
            [Description("Entity id, path or name")] string entity,
            [Description("Project-relative path; default Assets/Areas/<entity name>.varea")] string path = null)
        {
            var e = SceneModel.Resolve(entity);
            var a = AreaOf(entity);
            var project = ProjectData.Current ?? throw new ToolError("No project is open.");
            string name = e.Name; foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            string rel = string.IsNullOrEmpty(path) ? Path.Combine("Assets", "Areas", name + AreaDefinition.Extension).Replace('\\', '/') : path;
            var d = new AreaDefinition { Name = e.Name, Shape = (AreaShape)a.Shape, SizeX = a.SizeX, SizeY = a.SizeY, SizeZ = a.SizeZ, ColorR = a.ColorR, ColorG = a.ColorG, ColorB = a.ColorB, Priority = a.Priority };
            foreach (var t in AreaDefinition.ParseTags(a.Tags)) d.Tags.Add(t);
            foreach (var kv in AreaDefinition.ParseProperties(a.Properties)) d.Properties[kv.Key] = kv.Value;
            d.Save(Path.Combine(project.Path, rel), e.Scene);
            SceneModel.RecordEdit(a, "Area file", () => a.DataPath = rel);
            ToolContext.UndoLabel = "save area " + e.Name;
            return new { file = rel, tags = d.Tags.ToArray() };
        }

        private static int ParseShape(string shape)
        {
            switch ((shape ?? "box").Trim().ToLowerInvariant())
            {
                case "box": return 0;
                case "sphere": return 1;
                case "cylinder": return 2;
                default: throw new ToolError("shape must be 'box', 'sphere' or 'cylinder'.");
            }
        }
    }
}
