using System;
using Editor.Core.Data;

namespace Editor.Core.Services
{
    /// <summary>
    /// Runtime game-flow helpers shared by the standalone player and the editor's play mode. The generic
    /// scene SWITCH lives here (engine side); the DECISION of when/which scene is the game's (project scripts
    /// call Vortex.Scene.Load, which defers to here after the tick).
    /// </summary>
    public static class GameRuntime
    {
        /// <summary>Switch the active scene by name: stop the old scene's scripts, deactivate it, load +
        /// activate the new one, reset renderables, re-aim the main camera, and start the new scripts.
        /// Returns false if no scene with that name exists in the project.</summary>
        public static bool SwitchScene(string name)
        {
            var project = ProjectData.Current;
            if (project == null || string.IsNullOrEmpty(name)) return false;

            Scene target = null;
            foreach (var s in project.Scenes)
            {
                if (s != null && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) { target = s; break; }
            }
            if (target == null)
            {
                System.Diagnostics.Debug.WriteLine("[GameRuntime] SwitchScene: no scene named '" + name + "'");
                return false;
            }

            // 1) stop the current scene's gameplay scripts
            try { Editor.Scripting.ScriptRuntime.Instance.End(); } catch { }

            // 2) deactivate the old scene, activate the target (mirrors the editor's ActivateScene)
            var previous = project.ActiveScene;
            if (previous != null && previous != target)
            {
                try { previous.DeactivateEntities(); } catch { }
            }
            project.ActiveScene = target;
            target.Load();
            target.ActivateEntities();
            target.IsActive = true;

            // 3) drop the old scene's cached engine meshes/materials/cameras, preload the new scene's assets.
            // Make the GPU idle + drop the renderer's overlay/queue caches BEFORE freeing meshes, so an
            // in-flight frame never references a just-released buffer (use-after-free) and the UI overlay
            // doesn't carry the old scene's wrapped back buffers across the transition.
            try { Editor.DllWrapper.VortexAPI.OnSceneSwitch(); } catch { }
            try { SceneRenderService.Instance.ClearAllRenderables(); } catch { }
            try { SceneRenderService.Instance.PreloadSceneAssets(target); } catch { }

            // 4) point the renderer at the new scene's main camera
            try
            {
                var cam = CameraService.Instance.GetMainCamera();
                if (cam.IsValid) CameraService.Instance.SetActiveCamera(cam);
            }
            catch { }

            // 5) start the new scene's gameplay scripts
            try { Editor.Scripting.ScriptRuntime.Instance.Begin(target); } catch { }

            System.Diagnostics.Debug.WriteLine("[GameRuntime] switched to scene '" + target.Name + "'");
            return true;
        }

        /// <summary>If a script requested a scene change this tick, perform it now (call AFTER ScriptRuntime.Update).</summary>
        public static void ProcessPendingSceneSwitch()
        {
            var pending = Editor.Scripting.ScriptRuntime.Instance.ConsumePendingScene();
            if (!string.IsNullOrEmpty(pending)) SwitchScene(pending);
        }
    }
}
