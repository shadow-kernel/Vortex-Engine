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
        [Description("Returns an image of the editor viewport as it is now (edit mode: the editor camera — or the scene's main camera " +
                     "while the Game view is on, which the text then says; play mode: the game). Frame the subject first with focus_camera " +
                     "(or pass focus), which also makes the editor camera the one that renders. Use it to check your work.")]
        public static async Task<ToolResult> CaptureViewport(
            [Description("Longest side of the image in pixels (256–2048)")] int max_size = 1280,
            [Description("PNG instead of JPEG (exact pixels, larger)")] bool png = false,
            [Description("Frame these entities before capturing")] string[] focus = null,
            [Description("Keep the editor overlays (selection outline, move gizmo, light/camera icons) in the image")] bool gizmos = false)
        {
            string switched = null;
            if (focus != null && focus.Length > 0)
            {
                var list = SceneModel.ResolveMany(focus);
                switched = UseEditorCamera();
                Frame(list, null);
            }
            await Task.Delay(focus != null && focus.Length > 0 ? 120 : 0);
            var shot = await ViewportCapture.CaptureAsync(max_size, png, clean: !gizmos);
            string info = "Viewport " + shot.SourceWidth + "×" + shot.SourceHeight + " (sent " + shot.Width + "×" + shot.Height + "), " + RenderedView() +
                          (switched != null ? " — " + switched : "");
            return ToolResult.Text(info).WithImage(shot.Data, shot.MimeType);
        }

        /// <summary>Which camera the frame was rendered through, for the text next to an image (#444): the editor camera's
        /// pose only when the editor camera is the one that renders.</summary>
        private static string RenderedView()
        {
            if (PlayModeService.Instance.IsPlaying) return "play mode (game camera)";
            var session = EditorViewportSession.Main;
            switch (session?.CurrentView ?? EditorViewportSession.ViewSource.EditorCamera)
            {
                case EditorViewportSession.ViewSource.GameView:
                {
                    var scene = ProjectData.Current?.ActiveScene;
                    var main = PlayCameraHelper.FindMainCameraEntity(scene);
                    PlayCameraHelper.TryGetMainCameraWorld(scene, out var p, out var r, out _);
                    return "edit mode, Game view: the scene's main camera '" + main?.Name + "' at [" + F(p.X) + ", " + F(p.Y) + ", " + F(p.Z) + "] yaw " + F(r.Y) + "° pitch " + F(r.X) +
                           "° — not the editor camera (focus_camera switches to it)";
                }
                case EditorViewportSession.ViewSource.SceneCamera:
                    return "edit mode, looking through a scene camera — not the editor camera (focus_camera switches to it)";
                default:
                {
                    var cam = session?.Camera;
                    return "edit mode (editor camera)" + (cam != null ? ", camera at [" + F(cam.PositionX) + ", " + F(cam.PositionY) + ", " + F(cam.PositionZ) + "] yaw " + F(cam.Yaw) + "° pitch " + F(cam.Pitch) + "°" : "");
                }
            }
        }

        /// <summary>The editor camera renders from here on (#444): in the Game view, or looking through a scene camera, every
        /// frame would put that camera back over the pose a tool sets. Returns a note when the view changed, else null.</summary>
        private static string UseEditorCamera()
        {
            bool gameView = PlayModeService.Instance.IsGameView;
            var before = EditorViewportSession.Main?.UseEditorCamera() ?? EditorViewportSession.ViewSource.EditorCamera;
            if (gameView) return "switched the viewport from the Game view (the scene's main camera) to the Scene view, so the editor camera renders";
            return before == EditorViewportSession.ViewSource.SceneCamera ? "stopped looking through a scene camera, so the editor camera renders" : null;
        }

        /// <summary>For get_editor_info (#444): which camera the viewport renders through.</summary>
        internal static string ViewportCamera()
        {
            if (PlayModeService.Instance.IsPlaying) return "game (play mode)";
            switch (EditorViewportSession.Main?.CurrentView ?? EditorViewportSession.ViewSource.EditorCamera)
            {
                case EditorViewportSession.ViewSource.GameView: return "main camera (Game view)";
                case EditorViewportSession.ViewSource.SceneCamera: return "scene camera";
                default: return "editor camera";
            }
        }

        [McpServerTool(Name = "set_geometric_lod", Idempotent = true)]
        [Description("Sets the renderer's geometric LOD thresholds for instanced meshes (foliage, props): distant instances draw a decimated mesh. " +
                     "mid / far are multiples of the model's radius at which LOD 1 / LOD 2 start (engine default 16 / 40). Smaller = faster, coarser.")]
        public static object SetGeometricLod(
            [Description("LOD 1 distance in radii (e.g. 16)")] float mid,
            [Description("LOD 2 distance in radii (e.g. 40)")] float far,
            [Description("Off = every instance at full detail")] bool enabled = true)
        {
            if (mid <= 0f || far <= mid) throw new ToolError("mid must be positive and far larger than mid.");
            Editor.DllWrapper.VortexAPI.GeometricLod(enabled, mid, far);
            EditorViewportSession.RequestResubmit();
            return new { enabled, mid, far };
        }

        [McpServerTool(Name = "focus_camera", ReadOnly = true)]
        [Description("Moves the editor camera (edit mode): frame entities (fits them in view), or look from position at look_at. " +
                     "When the viewport shows the Game view (the scene's main camera) it switches to the Scene view, so this camera is the " +
                     "one capture_viewport sees (the answer's note says so). Returns the camera position. Does not change the scene.")]
        public static object FocusCamera(
            [Description("Entities to frame")] string[] entities = null,
            [Description("Camera position [x, y, z]")] float[] position = null,
            [Description("Point to look at [x, y, z]")] float[] look_at = null,
            [Description("Viewing direction when framing: yaw, pitch in degrees (default: the current direction, pitch at least 15° down)")] float[] angles = null)
        {
            var session = EditorViewportSession.Main ?? throw new ToolError("The editor viewport is not running.");
            if (PlayModeService.Instance.IsPlaying) throw new ToolError("The game camera drives the view in play mode; exit_play_mode first.");
            var list = entities != null && entities.Length > 0 ? SceneModel.ResolveMany(entities) : null;
            Vector3? at = SceneModel.Vec(position, "position"), target = SceneModel.Vec(look_at, "look_at");
            if (list == null && at == null && target == null) throw new ToolError("Give entities to frame, or a position and/or look_at.");
            string switched = UseEditorCamera();   // #444: otherwise the Game view renders the main camera over the pose set here
            if (list != null) Frame(list, angles);
            else
            {
                var cam = session.Camera;
                var from = at ?? new Vector3(cam.PositionX, cam.PositionY, cam.PositionZ);
                if (target != null)
                {
                    TransformMath.LookAngles(from, target.Value, out float yaw, out float pitch);
                    cam.SetPositionAndRotation(from.X, from.Y, from.Z, yaw, pitch);
                }
                else cam.SetPositionAndRotation(from.X, from.Y, from.Z, cam.Yaw, cam.Pitch);
            }
            var c = session.Camera;
            return new { position = new[] { R(c.PositionX), R(c.PositionY), R(c.PositionZ) }, yaw = R(c.Yaw), pitch = R(c.Pitch), note = switched };
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
        [Description("Renderer statistics of the last frame: FPS, draw calls (total, and split into main pass / shadow atlases / SSAO prepass), indices drawn by the main pass (vertices), instances tested/drawn by culling, entity count, and the live GPU resource counts (meshes incl. LOD meshes, materials, textures) — a count that only grows is a leak.")]
        public static object EngineStats()
        {
            VortexAPI.TryGetResourceCounts(out int meshes, out int materials, out int textures);
            int draws = VortexAPI.DrawCalls, shadowDraws = VortexAPI.DrawCallsShadow, postDraws = VortexAPI.DrawCallsPost;
            return new
            {
                fps = VortexAPI.CurrentFPS,
                draw_calls = draws,
                draw_calls_main = Math.Max(0, draws - shadowDraws - postDraws),
                draw_calls_shadow = shadowDraws,
                draw_calls_post = postDraws,
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
