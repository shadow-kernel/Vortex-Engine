using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.Scripting;
using ModelContextProtocol.Server;
using VortexEditor.Shell;

namespace VortexEditor.Claude.Tools
{
    /// <summary>Claude sees what it builds (#90): viewport captures, the editor camera, play mode, the console, engine stats.</summary>
    [McpServerToolType, DisplayName("Viewport")]
    public static class ViewTools
    {
        // ================================================================== viewport

        [McpServerTool(Name = "capture_viewport", ReadOnly = true)]
        [Description("Returns an image of the editor viewport as it is now (edit mode: the editor camera; play mode: the game). " +
                     "Frame the subject first with focus_camera. Use it to check your work.")]
        public static async Task<ToolResult> CaptureViewport(
            [Description("Longest side of the image in pixels (256–2048)")] int max_size = 1280,
            [Description("PNG instead of JPEG (exact pixels, larger)")] bool png = false,
            [Description("Frame these entities before capturing")] string[] focus = null,
            [Description("Keep the editor overlays (selection outline, move gizmo, light/camera icons) in the image")] bool gizmos = false)
        {
            if (focus != null && focus.Length > 0) Frame(SceneModel.ResolveMany(focus), null);
            await Task.Delay(focus != null && focus.Length > 0 ? 120 : 0);
            var shot = await ViewportCapture.CaptureAsync(max_size, png, clean: !gizmos);
            var cam = EditorViewportSession.Main?.Camera;
            string mode = PlayModeService.Instance.IsPlaying ? "play mode (game camera)" : "edit mode (editor camera)";
            string info = "Viewport " + shot.SourceWidth + "×" + shot.SourceHeight + " (sent " + shot.Width + "×" + shot.Height + "), " + mode +
                          (cam != null && !PlayModeService.Instance.IsPlaying ? ", camera at [" + F(cam.PositionX) + ", " + F(cam.PositionY) + ", " + F(cam.PositionZ) + "] yaw " + F(cam.Yaw) + "° pitch " + F(cam.Pitch) + "°" : "");
            return ToolResult.Text(info).WithImage(shot.Data, shot.MimeType);
        }

        [McpServerTool(Name = "focus_camera", ReadOnly = true)]
        [Description("Moves the editor camera (edit mode): frame entities (fits them in view), or look from position at look_at. " +
                     "Returns the camera position. Does not change the scene.")]
        public static object FocusCamera(
            [Description("Entities to frame")] string[] entities = null,
            [Description("Camera position [x, y, z]")] float[] position = null,
            [Description("Point to look at [x, y, z]")] float[] look_at = null,
            [Description("Viewing direction when framing: yaw, pitch in degrees (default: the current direction, pitch at least 15° down)")] float[] angles = null)
        {
            var session = EditorViewportSession.Main ?? throw new ToolError("The editor viewport is not running.");
            if (PlayModeService.Instance.IsPlaying) throw new ToolError("The game camera drives the view in play mode; exit_play_mode first.");
            if (entities != null && entities.Length > 0) Frame(SceneModel.ResolveMany(entities), angles);
            else if (position != null || look_at != null)
            {
                var cam = session.Camera;
                var from = SceneModel.Vec(position, "position") ?? new Vector3(cam.PositionX, cam.PositionY, cam.PositionZ);
                if (look_at != null)
                {
                    TransformMath.LookAngles(from, SceneModel.Vec(look_at, "look_at").Value, out float yaw, out float pitch);
                    cam.SetPositionAndRotation(from.X, from.Y, from.Z, yaw, pitch);
                }
                else cam.SetPositionAndRotation(from.X, from.Y, from.Z, cam.Yaw, cam.Pitch);
            }
            else throw new ToolError("Give entities to frame, or a position and/or look_at.");
            var c = session.Camera;
            return new { position = new[] { R(c.PositionX), R(c.PositionY), R(c.PositionZ) }, yaw = R(c.Yaw), pitch = R(c.Pitch) };
        }

        /// <summary>Point the editor camera at the entities' bounds from the current (or given) direction.</summary>
        internal static void Frame(List<GameEntity> list, float[] angles)
        {
            var session = EditorViewportSession.Main;
            if (session == null || list.Count == 0) return;
            Bounds.Of(list, out var min, out var max);
            var center = new Vector3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
            float radius = Math.Max(0.5f, 0.5f * (float)Math.Sqrt((max.X - min.X) * (max.X - min.X) + (max.Y - min.Y) * (max.Y - min.Y) + (max.Z - min.Z) * (max.Z - min.Z)));
            var cam = session.Camera;
            float yaw = angles != null && angles.Length >= 1 ? angles[0] : cam.Yaw;
            float pitch = angles != null && angles.Length >= 2 ? angles[1] : Math.Max(15f, cam.Pitch);
            pitch = Math.Clamp(pitch, -85f, 85f);
            // distance for a ~60° vertical field of view with some margin
            float dist = radius / (float)Math.Tan(30.0 * Math.PI / 180.0) * 1.15f;
            double yr = yaw * Math.PI / 180, pr = pitch * Math.PI / 180;
            float fx = (float)(Math.Sin(yr) * Math.Cos(pr)), fy = (float)(-Math.Sin(pr)), fz = (float)(Math.Cos(yr) * Math.Cos(pr));
            cam.SetPositionAndRotation(center.X - fx * dist, center.Y - fy * dist, center.Z - fz * dist, yaw, pitch);
        }

        // ================================================================== play mode

        [McpServerTool(Name = "enter_play_mode"), ManagesUndo]
        [Description("Starts play mode (compiles the project scripts first). Returns the play state, script compile errors (file, line, message) " +
                     "and errors logged while starting. Scene changes made while playing are thrown away when play stops.")]
        public static async Task<object> EnterPlayMode([Description("Seconds to let the game run before answering")] double run_seconds = 1.0)
        {
            _ = SceneModel.ActiveScene;
            var pms = PlayModeService.Instance;
            int before = ConsoleService.Instance.Entries.Count;
            if (!pms.IsPlaying) EditorSession.Instance.Play();
            else if (pms.State == PlayState.Paused) pms.Resume();
            await Task.Delay((int)(Math.Clamp(run_seconds, 0.2, 10) * 1000));
            string build = ScriptRuntime.Instance.LastBuildLog;
            bool failed = !string.IsNullOrEmpty(build) && build.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0;
            return new
            {
                state = pms.State.ToString(),
                script_errors = failed ? build.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Script compile", StringComparison.Ordinal)).Take(30).ToArray() : null,
                console_errors = NewEntries(before, LogLevel.Error, 20),
                console_warnings = NewEntries(before, LogLevel.Warning, 10),
            };
        }

        [McpServerTool(Name = "exit_play_mode"), ManagesUndo]
        [Description("Stops play mode and returns to editing (the scene is restored to its state before play).")]
        public static object ExitPlayMode()
        {
            var pms = PlayModeService.Instance;
            if (pms.IsPlaying) EditorSession.Instance.Stop();
            return new { state = pms.State.ToString() };
        }

        [McpServerTool(Name = "get_play_state", ReadOnly = true, Idempotent = true)]
        [Description("Whether the editor is editing, playing or paused.")]
        public static object GetPlayState() => new { state = PlayModeService.Instance.State.ToString() };

        // ================================================================== console & stats

        [McpServerTool(Name = "read_console", ReadOnly = true, Idempotent = true)]
        [Description("Recent editor console lines (newest last): engine messages, warnings, errors and script Debug.Log output.")]
        public static object ReadConsole(
            [Description("'all', 'warning' (warnings and errors) or 'error'")] string severity = "all",
            [Description("Maximum lines")] int limit = 50,
            [Description("Only lines containing this text")] string contains = null)
        {
            IEnumerable<LogEntry> entries = ConsoleService.Instance.Entries.ToList();
            switch ((severity ?? "all").ToLowerInvariant())
            {
                case "all": case "info": break;
                case "warning": case "warn": case "warnings": entries = entries.Where(e => e.Level == LogLevel.Warning || e.Level == LogLevel.Error); break;
                case "error": case "errors": entries = entries.Where(e => e.Level == LogLevel.Error); break;
                default: throw new ToolError("severity must be 'all', 'warning' or 'error'.");
            }
            if (!string.IsNullOrEmpty(contains)) entries = entries.Where(e => e.Message?.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);
            var list = entries.ToList();
            limit = Math.Clamp(limit, 1, 500);
            return new
            {
                total = list.Count,
                lines = list.Skip(Math.Max(0, list.Count - limit)).Select(e => e.Time + " " + Tag(e.Level) + " " + ToolHost.Shorten(e.Message, 1200)).ToArray(),
            };
        }

        [McpServerTool(Name = "engine_stats", ReadOnly = true)]
        [Description("Renderer statistics of the last frame: FPS, draw calls, vertices, instances tested/drawn by culling, entity count, and the live GPU resource counts (meshes incl. LOD meshes, materials, textures) — a count that only grows is a leak.")]
        public static object EngineStats()
        {
            VortexAPI.TryGetResourceCounts(out int meshes, out int materials, out int textures);
            return new
            {
                fps = VortexAPI.CurrentFPS,
                draw_calls = VortexAPI.DrawCalls,
                vertices = VortexAPI.VertexCount,
                instances_tested = VortexAPI.InstancesTested,
                instances_drawn = VortexAPI.InstancesDrawn,
                entities = ProjectData.Current?.ActiveScene != null ? SceneModel.All(ProjectData.Current.ActiveScene).Count() : 0,
                meshes, materials, textures,
                play_state = PlayModeService.Instance.State.ToString(),
            };
        }

        private static string[] NewEntries(int before, LogLevel level, int max)
        {
            var e = ConsoleService.Instance.Entries;
            var list = new List<string>();
            for (int i = Math.Min(before, e.Count); i < e.Count && list.Count < max; i++)
                if (e[i].Level == level) list.Add(ToolHost.Shorten(e[i].Message, 600));
            return list.Count == 0 ? null : list.ToArray();
        }

        private static string Tag(LogLevel l) => l == LogLevel.Error ? "ERROR" : l == LogLevel.Warning ? "WARN" : l == LogLevel.System ? "SYS" : "INFO";
        private static double R(float v) => ToolJson.R(v);
        private static string F(float v) => ToolJson.R(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
