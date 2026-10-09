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
            // beyond lights (#318): a renderer's enabled flag, an entity's tag, a component added during play
            Editor.ECS.Components.Rendering.MeshRenderer mr = null; GameEntity mrEntity = null;
            void FindMr(GameEntity e) { if (e == null || mr != null) return; mr = e.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>(); if (mr != null) mrEntity = e; else if (e.Children != null) foreach (var c in e.Children) FindMr(c); }
            foreach (var e in scene.Entities.ToList()) FindMr(e);
            bool mrOn = mr?.IsEnabled ?? true; string tag = mrEntity?.Tag; int comps = mrEntity?.Components.Count ?? 0;
            EditorCommands.Play();
            if (PlayModeService.Instance.State != PlayState.Playing) { log.LogError("play restore: play did not start"); return false; }
            await Task.Delay(150);
            // what a script's Light handle writes
            light.Intensity = intensity * 3f + 7f; light.ColorR = r > 0.5f ? 0.1f : 0.9f; light.IsEnabled = !on;
            if (mr != null) { mr.IsEnabled = !mrOn; mrEntity.Tag = "SmokeChanged"; mrEntity.Components.Add(new Editor.ECS.Components.Physics.BoxCollider(mrEntity)); }
            await Task.Delay(150);
            EditorCommands.Stop();
            await Task.Delay(100);
            bool ok = Math.Abs(light.Intensity - intensity) < 1e-4f && Math.Abs(light.ColorR - r) < 1e-4f && light.IsEnabled == on;
            bool okMore = mr == null || (mr.IsEnabled == mrOn && mrEntity.Tag == tag && mrEntity.Components.Count == comps);
            log.Log("play restore: " + light.Entity?.Name + " intensity " + light.Intensity + " (was " + intensity + "), enabled " + light.IsEnabled + " (was " + on + ")"
                + (mr != null ? "; " + mrEntity.Name + " renderer " + mr.IsEnabled + " (was " + mrOn + "), tag '" + mrEntity.Tag + "', components " + mrEntity.Components.Count + " (was " + comps + ")" : ""));
            if (!okMore) log.LogError("play restore: renderer flag / tag / added component were not put back (#318)");
            return ok && okMore;
        }
    }
}
