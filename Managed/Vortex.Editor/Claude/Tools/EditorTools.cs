using System;
using System.ComponentModel;
using System.Linq;
using Editor.Core;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using ModelContextProtocol.Server;
using VortexEditor.Shell;

namespace VortexEditor.Claude.Tools
{
    /// <summary>The editor itself: what is open, and the undo history Claude's calls live in.</summary>
    [McpServerToolType, DisplayName("Editor")]
    public static class EditorTools
    {
        [McpServerTool(Name = "get_editor_info", ReadOnly = true, Idempotent = true)]
        [Description("What the Vortex editor has open: engine version, project, active scene, play state, selection, undo state. " +
                     "Call it first to check the connection.")]
        public static object GetEditorInfo()
        {
            var p = ProjectData.Current;
            var undo = UndoRedoManager.Instance;
            return new
            {
                engine = "Vortex Engine " + EngineInfo.VersionString,
                platform = Environment.OSVersion.Platform == PlatformID.Win32NT ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
                project = p == null ? null : new { name = p.Name, path = p.Path },
                active_scene = p?.ActiveScene?.Name,
                scenes = p?.Scenes.Count(s => s != null),
                play_state = PlayModeService.Instance.State.ToString(),
                selection = EditorCommands.SelectedEntities().Select(e => SceneModel.ShortId(e) + " " + e.Name).ToArray(),
                undo = undo.CanUndo ? undo.UndoName : null,
                redo = undo.CanRedo ? undo.RedoName : null,
                tools = ToolCatalog.All.Count,
                note = p == null ? "No project is open — ask the user to open one in the editor." : null,
            };
        }

        [McpServerTool(Name = "undo"), ManagesUndo]
        [Description("Undoes the last step(s) of the editor's undo history — each of your mutating calls is one step ('Claude: …'), " +
                     "but the user's own edits are on the same stack: check get_history first.")]
        public static object Undo([Description("How many steps")] int steps = 1)
        {
            var undo = UndoRedoManager.Instance;
            var done = new System.Collections.Generic.List<string>();
            for (int i = 0; i < Math.Clamp(steps, 1, 50) && undo.CanUndo; i++)
            {
                string name = undo.UndoName;
                if (!undo.Undo()) break;
                done.Add(name);
            }
            EditorCommands.AfterSceneEdit();
            return new { undone = done.ToArray(), next_undo = undo.CanUndo ? undo.UndoName : null };
        }

        [McpServerTool(Name = "redo"), ManagesUndo]
        [Description("Redoes step(s) that were undone.")]
        public static object Redo([Description("How many steps")] int steps = 1)
        {
            var undo = UndoRedoManager.Instance;
            var done = new System.Collections.Generic.List<string>();
            for (int i = 0; i < Math.Clamp(steps, 1, 50) && undo.CanRedo; i++)
            {
                string name = undo.RedoName;
                if (!undo.Redo()) break;
                done.Add(name);
            }
            EditorCommands.AfterSceneEdit();
            return new { redone = done.ToArray(), next_redo = undo.CanRedo ? undo.RedoName : null };
        }

        [McpServerTool(Name = "get_history", ReadOnly = true, Idempotent = true)]
        [Description("The editor's undo history, newest first (your steps are named 'Claude: …'), and what can be redone.")]
        public static object GetHistory([Description("Maximum entries")] int limit = 20)
        {
            var undo = UndoRedoManager.Instance;
            limit = Math.Clamp(limit, 1, 200);
            return new
            {
                undo = undo.GetUndoHistory().Take(limit).Select(c => c.Name).ToArray(),
                redo = undo.GetRedoHistory().Take(limit).Select(c => c.Name).ToArray(),
            };
        }
    }
}
