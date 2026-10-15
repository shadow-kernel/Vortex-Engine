using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.AI;
using Editor.DllWrapper;
using ModelContextProtocol.Server;
using VortexEditor.Shell;

namespace VortexEditor.Claude.Tools
{
    /// <summary>The scene's navmesh over MCP (#449): bake it the way Window ▸ Navigation's Bake button does, clear it, and read
    /// what the baked one holds and whether it still matches the scene — an agent that moved walls can finish the job.</summary>
    [McpServerToolType, DisplayName("Navigation")]
    public static class NavigationTools
    {
        // ManagesUndo: a bake takes seconds on a worker thread — an open undo group would swallow the user's own edits meanwhile
        [McpServerTool(Name = "bake_navmesh"), ManagesUndo]
        [Description("Bakes the scene's navmesh the way the Bake button of Window ▸ Navigation does — the static colliders and static render meshes, " +
                     "Recast on a worker thread — writes it next to the scene as <Scene>.vnav and loads it (active scene), so Nav Agents and " +
                     "Navigation.CalculatePath use it. Bake again after moving walls, floors or props (navmesh_info says when it is stale). Settings " +
                     "default to the ones the scene's navmesh was baked with, else the engine's; the overrides given here are stored with the new " +
                     "navmesh. Returns the window's message, the polygon / tile counts, the geometry it read, warnings (parts baked coarser or not " +
                     "at all) and notes (geometry left out). Edit mode only. Not undoable: it overwrites the .vnav.")]
        public static async Task<object> BakeNavmesh(
            [Description("Scene name (default: the active scene)")] string scene = null,
            [Description("Agent radius in m (0–5): the walkable area keeps this far from walls")] float agent_radius = -1,
            [Description("Agent height in m (0.1–20): the headroom it needs")] float agent_height = -1,
            [Description("Step height in m (0–5): the highest step it climbs")] float step_height = -1,
            [Description("Steepest walkable slope in degrees (0–89)")] float max_slope = -1,
            [Description("Voxel size in m (0.02–5): smaller follows the geometry closer and bakes slower; agent_radius / 2 is a good start")] float cell_size = -1,
            [Description("Voxel height in m (0.02–5)")] float cell_height = -1,
            [Description("Tile size in cells (16–1024): large levels are baked in tiles of this many cells")] int tile_size = -1,
            [Description("Geometry the bake reads, any of: 'colliders' (box / sphere / capsule / mesh colliders), 'static_meshes' (render meshes of " +
                         "entities marked Static), 'all_meshes' (render meshes of every non-moving entity). Moving things, characters and entities " +
                         "tagged NavIgnore are always left out.")] string[] sources = null,
            [Description("Seconds before the bake is cancelled (10–600)")] double timeout_seconds = 120)
        {
            var s = SceneOf(scene);
            if (SceneModel.Playing)
                throw new ToolError("Bake in edit mode (exit_play_mode first): the game's agents walk on the loaded navmesh, and the .vnav is baked from the edit scene.");
            if (!NavigationService.Available) throw new ToolError(NavigationService.ErrorText(-6));
            var settings = NavigationService.SettingsFor(s);
            if (Given(agent_radius, 0f, 5f, "agent_radius")) settings.AgentRadius = agent_radius;
            if (Given(agent_height, 0.1f, 20f, "agent_height")) settings.AgentHeight = agent_height;
            if (Given(step_height, 0f, 5f, "step_height")) settings.AgentMaxClimb = step_height;
            if (Given(max_slope, 0f, 89f, "max_slope")) settings.AgentMaxSlope = max_slope;
            if (Given(cell_size, 0.02f, 5f, "cell_size")) settings.CellSize = cell_size;
            if (Given(cell_height, 0.02f, 5f, "cell_height")) settings.CellHeight = cell_height;
            if (Given(tile_size, 16, 1024, "tile_size")) settings.TileSize = tile_size;
            if (sources != null) settings.SourceFlags = (int)Sources(sources);
            timeout_seconds = Math.Clamp(timeout_seconds, 10, 600);

            // the window's path: geometry on the UI thread, Recast on a worker, then save + load — other tools run meanwhile
            var project = SceneModel.Project;
            var geo = NavigationService.CollectGeometry(s, settings.SourceFlags);
            NavBakeResult r = null;
            bool timedOut = false;
            await ToolHost.Unlocked(async () =>
            {
                var bake = Task.Run(() => NavigationService.Bake(geo, settings));
                if (await Task.WhenAny(bake, Task.Delay(TimeSpan.FromSeconds(timeout_seconds))) != bake)
                {
                    timedOut = true;
                    NavigationService.CancelBake();
                    try { await bake.WaitAsync(TimeSpan.FromSeconds(30)); } catch { }
                }
                else r = await bake;
                return true;
            });
            if (timedOut) throw new ToolError("The bake took longer than " + timeout_seconds + " s and was cancelled — raise cell_size or tile_size, or timeout_seconds.");
            // the editor went on while the bake ran (#449 review): only into the scene it was baked from, and never into a running game
            if (!ReferenceEquals(ProjectData.Current, project) || !project.Scenes.Contains(s) || !s.IsLoaded)
                throw new ToolError("The project or the scene changed while the bake ran: nothing was saved — bake again.");
            if (SceneModel.Playing)
                throw new ToolError("Play started while the bake ran: nothing was saved or loaded (the game's agents walk on the loaded navmesh) — exit_play_mode and bake again.");
            foreach (var n in geo.Notes) ConsoleService.Instance.LogWarning("[Navigation] " + n);
            if (!r.Success) throw new ToolError(r.Message + (geo.Notes.Count > 0 ? " Notes: " + string.Join(" ", geo.Notes) : ""));
            if (!NavigationService.Save(s, r.Data, out var path))
                throw new ToolError("Baked (" + r.Message + ") but " + Rel(NavigationService.NavMeshPathFor(s)) + " could not be written — see read_console.");
            r.Path = path;
            bool loaded = ReferenceEquals(s, SceneModel.Project.ActiveScene) && NavigationService.LoadData(r.Data, s);
            // the window's Console line: a bake that left part of the level coarser or without navmesh (#451) is a warning
            string line = "[Navigation] baked '" + s.Name + "': " + r.Message;
            if (r.Warnings.Count > 0) ConsoleService.Instance.LogWarning(line); else ConsoleService.Instance.Log(line);
            NavigationWindow.RefreshOpen();
            var st = r.Stats;
            return new
            {
                scene = s.Name,
                file = Rel(path),
                loaded,
                message = r.Message,
                polygons = st.PolyCount,
                tiles = st.TileCount,
                tile_grid = new[] { st.TilesX, st.TilesZ },
                milliseconds = R(st.BakeMs),
                read = new { triangles = st.InputTriangles, solid_boxes = st.InputBoxes, entities = geo.EntityCount },
                warnings = r.Warnings.Count > 0 ? r.Warnings.ToArray() : null,
                notes = geo.Notes.Count > 0 ? geo.Notes.ToArray() : null,
                settings = Describe(settings),
            };
        }

        [McpServerTool(Name = "navmesh_info", ReadOnly = true, Idempotent = true)]
        [Description("The scene's navmesh: whether <Scene>.vnav exists and is the loaded one, what it holds (polygons, tiles, vertices, detail " +
                     "triangles, bounds, the triangles and solid boxes it was baked from, its size), the settings it was baked with, when it was baked " +
                     "and the scene file was saved — and stale: whether the scene's static geometry changed since the bake (walls moved, floors or props " +
                     "added or removed), with the reason; absent when it cannot be told (a navmesh baked by an older editor, or play mode). Collects the scene's " +
                     "geometry to compare, like a bake; nothing is baked or loaded.")]
        public static object NavmeshInfo([Description("Scene name (default: the active scene)")] string scene = null)
        {
            var s = SceneOf(scene);
            string rel = Rel(NavigationService.NavMeshPathFor(s));
            bool loaded = ReferenceEquals(NavigationService.LoadedScene, s) && NavigationService.IsLoaded;
            var file = NavigationService.ReadFileInfo(s);
            if (file == null)
                return new { scene = s.Name, file = rel, exists = false, loaded, note = "Not baked yet: Nav Agents stand still and Navigation.CalculatePath finds no path until bake_navmesh runs." };
            if (file.Version < 0)
                return new { scene = s.Name, file = rel, exists = true, loaded, note = "The file is no navmesh this engine reads — bake_navmesh replaces it." };
            bool? stale = null;
            string why;
            if (SceneModel.Playing) why = "not checked while playing: the scene is the game's now — ask again in edit mode";
            else stale = NavigationService.IsStale(s, file, out why);
            var st = file.Stats;
            string sceneFile = s.FilePath;
            return new
            {
                scene = s.Name,
                file = rel,
                exists = true,
                loaded,
                stale,
                stale_reason = why,
                polygons = st.PolyCount,
                tiles = st.TileCount,
                tile_grid = new[] { st.TilesX, st.TilesZ },
                vertices = st.VertCount,
                detail_triangles = st.DetailTriCount,
                bounds = new
                {
                    min = new[] { R(st.BoundsMinX), R(st.BoundsMinY), R(st.BoundsMinZ) },
                    max = new[] { R(st.BoundsMaxX), R(st.BoundsMaxY), R(st.BoundsMaxZ) },
                },
                baked_from = new { triangles = st.InputTriangles, solid_boxes = st.InputBoxes },
                bake_ms = R(st.BakeMs),
                size_kb = Math.Max(1, (int)(file.Size / 1024)),
                baked_at = file.BakedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                scene_saved_at = !string.IsNullOrEmpty(sceneFile) && File.Exists(sceneFile) ? File.GetLastWriteTime(sceneFile).ToString("yyyy-MM-dd HH:mm:ss") : null,
                settings = Describe(file.Settings),
                note = loaded ? null : "Not loaded now: bake_navmesh loads it, Play loads it for the agents.",
            };
        }

        [McpServerTool(Name = "clear_navmesh", Destructive = true, Idempotent = true), NoDryRun]
        [Description("Deletes the scene's baked navmesh (<Scene>.vnav), like the Clear button of Window ▸ Navigation, and unloads it: Nav Agents " +
                     "stand still until bake_navmesh runs again. Edit mode only; not undoable.")]
        public static object ClearNavmesh([Description("Scene name (default: the active scene)")] string scene = null)
        {
            var s = SceneOf(scene);
            if (SceneModel.Playing) throw new ToolError("Clear the navmesh in edit mode (exit_play_mode first): the game's agents walk on it.");
            string path = NavigationService.NavMeshPathFor(s);
            bool existed = path != null && File.Exists(path);
            if (existed)
            {
                try { File.Delete(path); }
                catch (Exception ex) { throw new ToolError("Could not delete " + Rel(path) + ": " + ex.Message); }
            }
            bool unloaded = ReferenceEquals(NavigationService.LoadedScene, s);
            if (unloaded) NavigationService.Unload();
            NavigationWindow.RefreshOpen();
            return new
            {
                scene = s.Name,
                deleted = existed ? Rel(path) : null,
                unloaded = unloaded ? (bool?)true : null,
                note = existed ? null : "The scene had no navmesh file.",
            };
        }

        // ------------------------------------------------------------------ helpers

        private static Scene SceneOf(string name)
        {
            var p = SceneModel.Project;
            if (string.IsNullOrEmpty(name)) return SceneModel.ActiveScene;
            return p.Scenes.FirstOrDefault(x => x != null && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ToolError("No scene named '" + name + "'. Scenes: " + string.Join(", ", p.Scenes.Where(x => x != null).Select(x => x.Name)));
        }

        /// <summary>An override was given (not the -1 of "keep"): it must be in the window's range.</summary>
        private static bool Given(float v, float min, float max, string name)
        {
            if (v == -1f) return false;
            if (!float.IsFinite(v) || v < min || v > max)
                throw new ToolError(name + " must be between " + F(min) + " and " + F(max) + " (or left out to keep " + name + " of the scene's navmesh).");
            return true;
        }

        private static readonly (string Name, NavGeometrySource Flag)[] SourceNames =
        {
            ("colliders", NavGeometrySource.Colliders), ("static_meshes", NavGeometrySource.StaticRenderMeshes), ("all_meshes", NavGeometrySource.AllRenderMeshes),
        };

        private static NavGeometrySource Sources(string[] names)
        {
            var flags = NavGeometrySource.None;
            foreach (var n in names)
            {
                var hit = SourceNames.FirstOrDefault(x => string.Equals(x.Name, n?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (hit.Name == null) throw new ToolError("Unknown source '" + n + "'. Sources: " + string.Join(", ", SourceNames.Select(x => x.Name)) + ".");
                flags |= hit.Flag;
            }
            if (flags == NavGeometrySource.None) throw new ToolError("Give at least one source: " + string.Join(", ", SourceNames.Select(x => x.Name)) + ".");
            return flags;
        }

        private static object Describe(NavBakeSettings s)
        {
            var filters = new List<string>();
            if ((s.FilterFlags & 1) != 0) filters.Add("walk_over_low_obstacles");
            if ((s.FilterFlags & 2) != 0) filters.Add("no_navmesh_over_ledges");
            if ((s.FilterFlags & 4) != 0) filters.Add("no_navmesh_under_low_ceilings");
            int flags = s.SourceFlags == 0 ? (int)NavGeometrySource.Default : s.SourceFlags;
            return new
            {
                agent_radius = R(s.AgentRadius),
                agent_height = R(s.AgentHeight),
                step_height = R(s.AgentMaxClimb),
                max_slope = R(s.AgentMaxSlope),
                cell_size = R(s.CellSize),
                cell_height = R(s.CellHeight),
                tile_size = s.TileSize,
                sources = SourceNames.Where(x => (flags & (int)x.Flag) != 0).Select(x => x.Name).ToArray(),
                region_min_size = R(s.RegionMinSize),
                region_merge_size = R(s.RegionMergeSize),
                edge_max_length = R(s.EdgeMaxLen),
                edge_max_error = R(s.EdgeMaxError),
                verts_per_polygon = s.VertsPerPoly,
                detail_sample_distance = R(s.DetailSampleDist),
                detail_sample_max_error = R(s.DetailSampleMaxError),
                partitioning = s.Partition == 1 ? "monotone" : s.Partition == 2 ? "layers" : "watershed",
                filters,
            };
        }

        private static string Rel(string path) => string.IsNullOrEmpty(path) ? null : ProjectFiles.Rel(path);
        private static double R(float v) => ToolJson.R(v);
        private static string F(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
