using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Lighting;

namespace VortexEditor.Shell
{
    /// <summary>Play is non-destructive for lights too: what a script changes on a Light during play (a flicker, a
    /// flashlight) is back to the authored values after Stop — before, it was saved into the scene.</summary>
    internal static class PlayRestoreSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("play restores lights scripts changed", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            Light light = null;
            void Walk(GameEntity e) { if (e == null || light != null) return; light = e.GetComponent<Light>(); if (light == null && e.Children != null) foreach (var c in e.Children) Walk(c); }
            foreach (var e in scene?.Entities?.ToList() ?? new System.Collections.Generic.List<GameEntity>()) Walk(e);
            if (light == null) { log.Log("play restore: no light in the scene — skipped"); return true; }

            float intensity = light.Intensity, r = light.ColorR; bool on = light.IsEnabled;
            EditorCommands.Play();
            if (PlayModeService.Instance.State != PlayState.Playing) { log.LogError("play restore: play did not start"); return false; }
            await Task.Delay(150);
            // what a script's Light handle writes
            light.Intensity = intensity * 3f + 7f; light.ColorR = r > 0.5f ? 0.1f : 0.9f; light.IsEnabled = !on;
            await Task.Delay(150);
            EditorCommands.Stop();
            await Task.Delay(100);
            bool ok = Math.Abs(light.Intensity - intensity) < 1e-4f && Math.Abs(light.ColorR - r) < 1e-4f && light.IsEnabled == on;
            log.Log("play restore: " + light.Entity?.Name + " intensity " + light.Intensity + " (was " + intensity + "), enabled " + light.IsEnabled + " (was " + on + ")");
            return ok;
        }
    }
}
