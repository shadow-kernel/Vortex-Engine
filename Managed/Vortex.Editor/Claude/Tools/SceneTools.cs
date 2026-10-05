using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using Editor.Editors.WorldEditor.Components.SceneHierarchy;
using ModelContextProtocol.Server;
using Component = Editor.ECS.Component;
using VortexEditor.Shell;

namespace VortexEditor.Claude.Tools
{
    /// <summary>Scenes and entities (#86): read the scene, create / delete / transform / parent entities, edit components.</summary>
    [McpServerToolType, DisplayName("Scenes")]
    public static class SceneTools
    {
        // ================================================================== scenes

        [McpServerTool(Name = "list_scenes", ReadOnly = true, Idempotent = true)]
        [Description("Lists the project's scenes: name, file, whether it is the active (edited) scene, entity count, unsaved changes.")]
        public static object ListScenes()
        {
            var p = SceneModel.Project;
            return p.Scenes.Where(s => s != null).Select(s => new
            {
                name = s.Name,
                file = string.IsNullOrEmpty(s.FilePath) ? null : Rel(s.FilePath),
                active = ReferenceEquals(s, p.ActiveScene) ? (bool?)true : null,
                loaded = s.IsLoaded,
                entities = s.IsLoaded ? SceneModel.All(s).Count() : (int?)null,
                unsaved = s.IsDirty ? (bool?)true : null,
            }).ToArray();
        }

        [McpServerTool(Name = "open_scene"), NoDryRun]
        [Description("Makes a scene the active (edited) scene — by name, or by .vscene path relative to the project (loaded into the project if needed).")]
        public static object OpenScene([Description("Scene name or project-relative .vscene path")] string scene)
        {
            var p = SceneModel.Project;
            var hit = p.Scenes.FirstOrDefault(s => s != null && string.Equals(s.Name, scene, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
            {
                string full = Path.GetFullPath(Path.Combine(p.Path, scene ?? ""));
                if (!full.StartsWith(Path.GetFullPath(p.Path), StringComparison.Ordinal) || !File.Exists(full) || !full.EndsWith(".vscene", StringComparison.OrdinalIgnoreCase))
                    throw new ToolError("No scene '" + scene + "'. Scenes: " + string.Join(", ", p.Scenes.Where(s => s != null).Select(s => s.Name)));
                hit = EditorCommands.LoadSceneFile(full) ?? throw new ToolError("Could not load " + scene + ".");
            }
            else EditorSession.Instance.ActivateScene(hit);
            return new { active = hit.Name, entities = SceneModel.All(hit).Count() };
        }

        [McpServerTool(Name = "save_scene", Idempotent = true), NoDryRun]
        [Description("Saves a scene to its .vscene file (default: the active scene). Tool edits live in memory until saved.")]
        public static object SaveScene([Description("Scene name (default: the active scene)")] string scene = null)
        {
            var p = SceneModel.Project;
            var s = string.IsNullOrEmpty(scene) ? SceneModel.ActiveScene
                : p.Scenes.FirstOrDefault(x => x != null && string.Equals(x.Name, scene, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolError("No scene named '" + scene + "'.");
            SceneService.Instance.SaveScene(s);
            return new { saved = s.Name, file = string.IsNullOrEmpty(s.FilePath) ? null : Rel(s.FilePath) };
        }

        [McpServerTool(Name = "scene_outline", ReadOnly = true, Idempotent = true)]
        [Description("The active scene's hierarchy as an indented tree: 'name [id] Component, Component'. Depth- and count-limited; " +
                     "collapsed branches show how many entities they hide. Pass root to outline one subtree.")]
        public static string SceneOutline(
            [Description("Deepest level to expand (0 = top level only)")] int max_depth = 3,
            [Description("Maximum number of lines")] int max_entities = 150,
            [Description("Entity to start from (default: the whole scene)")] string root = null)
        {
            var scene = SceneModel.ActiveScene;
            var sb = new StringBuilder();
            int lines = 0, total = 0;
            IEnumerable<GameEntity> top = root == null ? scene.Entities : new[] { SceneModel.Resolve(root) };
            sb.Append("Scene '").Append(scene.Name).Append("'").Append(scene.IsDirty ? " (unsaved)" : "").Append('\n');
            void Walk(GameEntity e, int depth)
            {
                total++;
                if (lines >= max_entities) return;
                lines++;
                sb.Append(new string(' ', 2 * (depth + 1))).Append(e.Name).Append(" [").Append(SceneModel.ShortId(e)).Append(']');
                var comps = e.Components.Where(c => !(c is Transform)).Select(c => c.GetType().Name).ToList();
                if (comps.Count > 0) sb.Append(' ').Append(string.Join(", ", comps));
                if (!e.IsActive) sb.Append(" (inactive)");
                if (e.IsPrefabInstance) sb.Append(" (prefab)");
                int kids = e.Children?.Count ?? 0;
                if (kids > 0 && depth >= max_depth)
                {
                    int hidden = SubtreeCount(e) - 1;
                    sb.Append(" (+").Append(hidden).Append(" below)");
                    total += hidden;
                    sb.Append('\n');
                    return;
                }
                sb.Append('\n');
                if (kids > 0) foreach (var c in e.Children) Walk(c, depth + 1);
            }
            foreach (var e in top) if (e != null) Walk(e, 0);
            if (lines >= max_entities && total > lines) sb.Append("  … ").Append(total - lines).Append(" more (raise max_entities or outline a root)\n");
            if (lines == 0) sb.Append("  (empty)\n");
            return sb.ToString();
        }

        private static int SubtreeCount(GameEntity e) => 1 + (e.Children?.Sum(SubtreeCount) ?? 0);

        // ================================================================== query

        [McpServerTool(Name = "find_entities", ReadOnly = true, Idempotent = true)]
        [Description("Finds entities in the active scene. All filters are optional and combine: name (case-insensitive, * wildcards, " +
                     "otherwise substring), tag, component type, under (an ancestor). Returns id, name, path and component types.")]
        public static object FindEntities(
            [Description("Name filter: 'Lamp*', '*wall*' or a substring")] string name = null,
            [Description("Exact tag")] string tag = null,
            [Description("Component type the entity must have, e.g. Light, BoxCollider, AudioSource")] string component = null,
            [Description("Only descendants of this entity")] string under = null,
            [Description("Maximum results")] int limit = 50)
        {
            var all = Query(name, tag, component, under);
            limit = Math.Clamp(limit, 1, 500);
            return new { count = all.Count, entities = all.Take(limit).Select(SceneModel.Brief).ToArray(), truncated = all.Count > limit ? (bool?)true : null };
        }

        /// <summary>Entities of the active scene matching every given filter (find_entities, bulk edits).</summary>
        internal static List<GameEntity> Query(string name, string tag, string component, string under)
        {
            IEnumerable<GameEntity> pool = under != null ? SceneModel.All(SceneModel.ActiveScene).Where(IsUnder(SceneModel.Resolve(under))) : SceneModel.All(SceneModel.ActiveScene);
            if (!string.IsNullOrEmpty(name))
            {
                var match = NameMatcher(name);
                pool = pool.Where(e => match(e.Name ?? ""));
            }
            if (!string.IsNullOrEmpty(tag)) pool = pool.Where(e => string.Equals(e.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(component))
            {
                var t = ComponentProps.FindType(component);
                pool = pool.Where(e => e.Components.Any(c => t.IsInstanceOfType(c)));
            }
            return pool.ToList();
        }

        internal static Func<GameEntity, bool> IsUnder(GameEntity ancestor) => e =>
        {
            for (var p = e.Parent; p != null; p = p.Parent) if (ReferenceEquals(p, ancestor)) return true;
            return false;
        };

        internal static Func<string, bool> NameMatcher(string pattern)
        {
            if (pattern.Contains('*'))
            {
                var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                return s => rx.IsMatch(s);
            }
            return s => s.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [McpServerTool(Name = "get_entity", ReadOnly = true, Idempotent = true)]
        [Description("Everything about one entity: transform (local, plus world position under a parent), every component with its " +
                     "editable properties (the names set_component_properties accepts), children, prefab link.")]
        public static object GetEntity([Description("Entity id, path or name")] string entity)
        {
            var e = SceneModel.Resolve(entity);
            return new
            {
                id = SceneModel.ShortId(e),
                name = e.Name,
                path = SceneModel.PathOf(e),
                tag = string.IsNullOrEmpty(e.Tag) || e.Tag == "Untagged" ? null : e.Tag,
                active = e.IsActive,
                @static = e.IsStatic ? (bool?)true : null,
                prefab = string.IsNullOrEmpty(e.PrefabPath) ? null : e.PrefabPath,
                transform = SceneModel.TransformInfo(e),
                components = e.Components.Where(c => !(c is Transform)).Select(c => new { type = c.GetType().Name, properties = ComponentProps.Values(c) }).ToArray(),
                children = e.Children?.Count > 0 ? e.Children.Take(50).Select(c => SceneModel.ShortId(c) + " " + c.Name).ToArray() : null,
                more_children = e.Children?.Count > 50 ? (int?)(e.Children.Count - 50) : null,
            };
        }

        [McpServerTool(Name = "list_component_types", ReadOnly = true, Idempotent = true)]
        [Description("Component types add_component accepts, each with its settable properties and value types (enums list their values).")]
        public static object ListComponentTypes() => ComponentProps.Types.Select(ComponentProps.Describe).ToArray();

        // ================================================================== create / delete

        internal static readonly string[] Kinds =
        {
            "empty", "folder", "cube", "sphere", "capsule", "cylinder", "plane", "quad",
            "point_light", "spot_light", "directional_light", "camera", "audio_source", "reverb_zone",
        };

        [McpServerTool(Name = "create_entity", Destructive = false)]
        [Description("Creates an entity in the active scene and returns it. kind: empty, folder, cube, sphere, capsule, cylinder, plane, " +
                     "quad, point_light, spot_light, directional_light, camera, audio_source, reverb_zone. Primitives are 1 m (cube 1×1×1, " +
                     "sphere/cylinder Ø 1 m, cylinder 1 m high, plane 1×1 m lying in XZ — scale it, e.g. [20, 1, 20]). Optional parent, " +
                     "local position/rotation/scale, extra components with properties: [{\"type\":\"BoxCollider\"}, " +
                     "{\"type\":\"Light\",\"properties\":{\"intensity\":3}}]. One undo step.")]
        public static object CreateEntity(
            [Description("What to create")] string kind = "empty",
            [Description("Name (default: from the kind)")] string name = null,
            [Description("Parent entity (default: scene root)")] string parent = null,
            [Description("Local position [x, y, z] in metres")] float[] position = null,
            [Description("Local rotation [x, y, z] in degrees")] float[] rotation = null,
            [Description("Local scale [x, y, z] (or [s] for uniform)")] float[] scale = null,
            [Description("Extra components: [{\"type\": \"…\", \"properties\": {…}}]")] JsonElement? components = null)
        {
            var scene = SceneModel.ActiveScene;
            var parentEntity = parent != null ? SceneModel.Resolve(parent) : null;
            var e = Make(scene, kind);
            if (!string.IsNullOrWhiteSpace(name)) SceneModel.Set(e, "Name", () => e.Name, v => e.Name = v, name.Trim());
            if (parentEntity != null) UndoRedoManager.Instance.Execute(new MoveEntityCommand(e, parentEntity));
            SceneModel.SetTransform(e, SceneModel.Vec(position, "position"), SceneModel.Vec(rotation, "rotation"), SceneModel.Vec(scale, "scale"));
            if (components.HasValue) AddComponents(e, components.Value);
            ToolContext.UndoLabel = "create " + e.Name;
            Select(e);
            return SceneModel.Brief(e);
        }

        internal static GameEntity Make(Scene scene, string kind)
        {
            switch ((kind ?? "empty").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_'))
            {
                case "empty": case "entity": return scene.CreateEntity("Entity");
                case "folder":
                {
                    var f = scene.CreateEntity("Folder");
                    f.IsFolder = true;
                    return f;
                }
                case "cube": return scene.CreatePrimitive(PrimitiveType.Cube);
                case "sphere": return scene.CreatePrimitive(PrimitiveType.Sphere);
                case "capsule": return scene.CreatePrimitive(PrimitiveType.Capsule);
                case "cylinder": return scene.CreatePrimitive(PrimitiveType.Cylinder);
                case "plane": return scene.CreatePrimitive(PrimitiveType.Plane);
                case "quad": return scene.CreatePrimitive(PrimitiveType.Quad);
                case "point_light": case "light": return scene.CreateLight(LightType.Point);
                case "spot_light": return scene.CreateLight(LightType.Spot);
                case "directional_light": case "sun": return scene.CreateLight(LightType.Directional);
                case "camera": return scene.CreateCamera();
                case "audio_source":
                {
                    var a = scene.CreateEntity("Audio Source");
                    a.AddComponent(new AudioSource(a));
                    return a;
                }
                case "reverb_zone":
                {
                    var r = scene.CreateEntity("Reverb Zone");
                    r.AddComponent(new ReverbZone(r));
                    return r;
                }
                default: throw new ToolError("Unknown kind '" + kind + "'. Valid: " + string.Join(", ", Kinds) + ".");
            }
        }

        internal static void AddComponents(GameEntity e, JsonElement list)
        {
            if (list.ValueKind == JsonValueKind.Null || list.ValueKind == JsonValueKind.Undefined) return;
            if (list.ValueKind != JsonValueKind.Array) throw new ToolError("components must be an array of {\"type\": …, \"properties\": {…}}.");
            foreach (var item in list.EnumerateArray())
            {
                string type = item.ValueKind == JsonValueKind.String ? item.GetString()
                    : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var t) ? t.GetString() : null;
                var c = AddComponentTo(e, type);
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("properties", out var props)) SetProps(c, props);
            }
        }

        internal static Component AddComponentTo(GameEntity e, string type)
        {
            var t = ComponentProps.FindType(type);
            if (t == typeof(Transform)) throw new ToolError("Every entity already has a Transform; use set_transform.");
            var c = ComponentProps.Create(t, e);
            e.AddComponent(c);
            return c;
        }

        internal static List<string> SetProps(Component c, JsonElement props)
        {
            if (props.ValueKind != JsonValueKind.Object) throw new ToolError("properties must be an object {\"name\": value}.");
            var done = new List<string>();
            foreach (var p in props.EnumerateObject()) done.Add(ComponentProps.Set(c, p.Name, p.Value));
            return done;
        }

        [McpServerTool(Name = "delete_entities", Destructive = true)]
        [Description("Deletes entities (with their children) from the scene. One undo step. dry_run lists what would be deleted.")]
        public static object DeleteEntities(
            [Description("Entity ids, paths or names")] string[] entities,
            [Description("Only report what would be deleted")] bool dry_run = false)
        {
            var list = EditorCommands.TopLevelOnly(SceneModel.ResolveMany(entities));
            int total = list.Sum(SubtreeCount);
            if (dry_run) return new { dry_run = true, would_delete = list.Select(SceneModel.Brief).ToArray(), entities_including_children = total };
            UndoRedoManager.Instance.Execute(new DeleteEntitiesCommand(list));
            if (list.Contains(SelectionService.Instance.SelectedEntity)) { SelectionService.Instance.ClearSelection(); EditorSession.Instance.Hierarchy.ClearSelection(); }
            ToolContext.UndoLabel = list.Count == 1 ? "delete " + list[0].Name : "delete " + list.Count + " entities";
            return new { deleted = list.Select(e => SceneModel.ShortId(e) + " " + e.Name).ToArray(), entities_including_children = total };
        }

        [McpServerTool(Name = "duplicate_entities", Destructive = false)]
        [Description("Duplicates entities (deep copy with children, new ids) next to the originals, optionally moved by an offset. One undo step.")]
        public static object DuplicateEntities(
            [Description("Entity ids, paths or names")] string[] entities,
            [Description("Move each copy by [x, y, z] metres")] float[] offset = null)
        {
            var list = EditorCommands.TopLevelOnly(SceneModel.ResolveMany(entities));
            var cmd = new DuplicateEntitiesCommand(list);
            UndoRedoManager.Instance.Execute(cmd);
            var off = SceneModel.Vec(offset, "offset");
            if (off.HasValue)
                foreach (var c in cmd.Copies)
                {
                    var p = c.Transform.LocalPosition;
                    SceneModel.SetTransform(c, new Vector3(p.X + off.Value.X, p.Y + off.Value.Y, p.Z + off.Value.Z), null, null);
                }
            ToolContext.UndoLabel = "duplicate " + (list.Count == 1 ? list[0].Name : list.Count + " entities");
            return cmd.Copies.Select(SceneModel.Brief).ToArray();
        }

        // ================================================================== transform / hierarchy

        [McpServerTool(Name = "set_transform", Idempotent = true)]
        [Description("Sets an entity's position (metres), rotation (Euler degrees) and/or scale; omitted parts stay. " +
                     "space 'world' places it in world coordinates even under a transformed parent.")]
        public static object SetTransform(
            [Description("Entity id, path or name")] string entity,
            [Description("Position [x, y, z]")] float[] position = null,
            [Description("Rotation [x, y, z] in degrees")] float[] rotation = null,
            [Description("Scale [x, y, z] (or [s])")] float[] scale = null,
            [Description("'local' (relative to the parent, default) or 'world'")] string space = "local")
        {
            var e = SceneModel.Resolve(entity);
            Vector3? pos = SceneModel.Vec(position, "position"), rot = SceneModel.Vec(rotation, "rotation"), scl = SceneModel.Vec(scale, "scale");
            if (string.Equals(space, "world", StringComparison.OrdinalIgnoreCase) && e.Parent != null)
            {
                // the wanted world matrix (missing parts from the current world transform), back into the parent's space
                TransformMath.Decompose(TransformMath.World(e), out var wp, out var wr, out var ws);
                var wanted = TransformMath.Local(pos ?? wp, rot ?? wr, scl ?? ws);
                var inv = TransformMath.InverseAffine(TransformMath.World(e.Parent)) ?? throw new ToolError("The parent has a zero scale; world placement is impossible.");
                TransformMath.Decompose(TransformMath.Multiply(wanted, inv), out var lp, out var lr, out var ls);
                SceneModel.SetTransform(e, pos.HasValue ? lp : (Vector3?)null, rot.HasValue ? lr : (Vector3?)null, scl.HasValue ? ls : (Vector3?)null);
            }
            else if (!string.Equals(space, "local", StringComparison.OrdinalIgnoreCase) && !string.Equals(space, "world", StringComparison.OrdinalIgnoreCase))
                throw new ToolError("space must be 'local' or 'world'.");
            else SceneModel.SetTransform(e, pos, rot, scl);
            ToolContext.UndoLabel = "move " + e.Name;
            SelectionService.Instance.NotifyTransformChanged();
            return new { id = SceneModel.ShortId(e), name = e.Name, transform = SceneModel.TransformInfo(e) };
        }

        [McpServerTool(Name = "set_entity", Idempotent = true)]
        [Description("Renames an entity and/or sets its tag, active state (inactive = hidden and not simulated) and static flag.")]
        public static object SetEntity(
            [Description("Entity id, path or name")] string entity,
            [Description("New name")] string name = null,
            [Description("Tag, e.g. Player, Enemy, Pickup")] string tag = null,
            [Description("Active in the scene")] bool? active = null,
            [Description("Static (never moves; batched)")] bool? @static = null)
        {
            var e = SceneModel.Resolve(entity);
            if (!string.IsNullOrWhiteSpace(name)) SceneModel.Set(e, "Name", () => e.Name, v => e.Name = v, name.Trim());
            if (tag != null) SceneModel.Set(e, "Tag", () => e.Tag, v => e.Tag = v, tag);
            if (active.HasValue) SceneModel.Set(e, "Active", () => e.IsActive, v => e.IsActive = v, active.Value);
            if (@static.HasValue) SceneModel.Set(e, "Static", () => e.IsStatic, v => e.IsStatic = v, @static.Value);
            return SceneModel.Brief(e);
        }

        [McpServerTool(Name = "parent_entity")]
        [Description("Moves an entity under a new parent. keep_world (default) keeps it where it is in the world; false keeps its local values.")]
        public static object ParentEntity(
            [Description("Entity to move")] string entity,
            [Description("New parent")] string parent,
            [Description("Keep the world position/rotation/scale")] bool keep_world = true)
        {
            var e = SceneModel.Resolve(entity);
            var p = SceneModel.Resolve(parent);
            for (var cur = p; cur != null; cur = cur.Parent)
                if (ReferenceEquals(cur, e)) throw new ToolError("Cannot parent " + e.Name + " under its own descendant " + p.Name + ".");
            if (!ReferenceEquals(e.Scene, p.Scene)) throw new ToolError("Parent and child must be in the same scene.");
            Reparent(e, p, keep_world);
            ToolContext.UndoLabel = "parent " + e.Name + " under " + p.Name;
            return new { id = SceneModel.ShortId(e), path = SceneModel.PathOf(e), transform = SceneModel.TransformInfo(e) };
        }

        [McpServerTool(Name = "unparent_entity")]
        [Description("Moves an entity to the scene root. keep_world (default) keeps it where it is in the world.")]
        public static object UnparentEntity(
            [Description("Entity to move")] string entity,
            [Description("Keep the world position/rotation/scale")] bool keep_world = true)
        {
            var e = SceneModel.Resolve(entity);
            if (e.Parent == null) return new { id = SceneModel.ShortId(e), path = SceneModel.PathOf(e), note = "already at the scene root" };
            Reparent(e, null, keep_world);
            ToolContext.UndoLabel = "unparent " + e.Name;
            return new { id = SceneModel.ShortId(e), path = SceneModel.PathOf(e), transform = SceneModel.TransformInfo(e) };
        }

        internal static void Reparent(GameEntity e, GameEntity newParent, bool keepWorld)
        {
            float[] world = keepWorld ? TransformMath.World(e) : null;
            UndoRedoManager.Instance.Execute(new MoveEntityCommand(e, newParent));
            if (!keepWorld) return;
            float[] local = world;
            if (newParent != null)
            {
                var inv = TransformMath.InverseAffine(TransformMath.World(newParent));
                if (inv == null) return;
                local = TransformMath.Multiply(world, inv);
            }
            TransformMath.Decompose(local, out var lp, out var lr, out var ls);
            SceneModel.SetTransform(e, lp, lr, ls);
        }

        // ================================================================== components

        [McpServerTool(Name = "add_component", Destructive = false)]
        [Description("Adds a component to an entity, optionally with properties, e.g. type 'Light' properties {\"light_type\": \"Spot\", " +
                     "\"intensity\": 4, \"color\": \"#ffd8a0\"}. list_component_types shows all types and properties.")]
        public static object AddComponent(
            [Description("Entity id, path or name")] string entity,
            [Description("Component type, e.g. Light, BoxCollider, Rigidbody, AudioSource, MeshRenderer")] string type,
            [Description("Property values {\"name\": value}")] JsonElement? properties = null)
        {
            var e = SceneModel.Resolve(entity);
            var c = AddComponentTo(e, type);
            if (properties.HasValue && properties.Value.ValueKind != JsonValueKind.Null) SetProps(c, properties.Value);
            ToolContext.UndoLabel = "add " + c.GetType().Name + " to " + e.Name;
            return new { entity = SceneModel.ShortId(e), type = c.GetType().Name, properties = ComponentProps.Values(c) };
        }

        [McpServerTool(Name = "remove_component", Destructive = true)]
        [Description("Removes a component from an entity (the Transform cannot be removed).")]
        public static object RemoveComponent(
            [Description("Entity id, path or name")] string entity,
            [Description("Component type")] string type,
            [Description("Which one when the entity has several of that type (0 = first)")] int index = 0)
        {
            var e = SceneModel.Resolve(entity);
            var c = ComponentProps.On(e, type, index);
            if (c is Transform) throw new ToolError("The Transform cannot be removed.");
            e.RemoveComponent(c);
            ToolContext.UndoLabel = "remove " + c.GetType().Name + " from " + e.Name;
            return new { entity = SceneModel.ShortId(e), removed = c.GetType().Name, components = e.Components.Select(x => x.GetType().Name).ToArray() };
        }

        [McpServerTool(Name = "set_component_properties", Idempotent = true)]
        [Description("Sets properties of an entity's component: {\"intensity\": 2.5, \"light_type\": \"Spot\", \"color\": \"#ff8800\"}. " +
                     "Names as get_entity shows them (snake_case or PascalCase); enums take their names; vectors [x, y, z]; " +
                     "'enabled' turns the component on/off. Errors list the valid names and values.")]
        public static object SetComponentProperties(
            [Description("Entity id, path or name")] string entity,
            [Description("Component type, e.g. Light")] string component,
            [Description("Property values {\"name\": value}")] JsonElement properties,
            [Description("Which component when the entity has several of that type (0 = first)")] int index = 0)
        {
            var e = SceneModel.Resolve(entity);
            var c = ComponentProps.On(e, component, index);
            var done = SetProps(c, properties);
            ToolContext.UndoLabel = "edit " + c.GetType().Name + " of " + e.Name;
            return new { entity = SceneModel.ShortId(e), type = c.GetType().Name, set = done, properties = ComponentProps.Values(c) };
        }

        // ================================================================== selection

        [McpServerTool(Name = "get_selection", ReadOnly = true, Idempotent = true)]
        [Description("The entities the user has selected in the editor (\"this\", \"the selected lamp\").")]
        public static object GetSelection() => EditorCommands.SelectedEntities().Select(SceneModel.Brief).ToArray();

        [McpServerTool(Name = "select_entities", ReadOnly = true, Idempotent = true)]
        [Description("Selects entities in the editor (hierarchy + inspector), e.g. to show the user what you changed. Empty list clears the selection.")]
        public static object SelectEntities([Description("Entity ids, paths or names")] string[] entities)
        {
            var list = entities == null || entities.Length == 0 ? new List<GameEntity>() : SceneModel.ResolveMany(entities);
            if (list.Count == 0) { SelectionService.Instance.ClearSelection(); EditorSession.Instance.Hierarchy.ClearSelection(); }
            else EditorCommands.SelectMany(list);
            return list.Select(SceneModel.Brief).ToArray();
        }

        internal static void Select(GameEntity e)
        {
            try
            {
                EditorSession.Instance.Hierarchy.SetSelection(e);
                SelectionService.Instance.Select(e);
            }
            catch { }
        }

        internal static string Rel(string full)
        {
            try { return Path.GetRelativePath(SceneModel.Project.Path, full).Replace('\\', '/'); } catch { return full; }
        }
    }
}
