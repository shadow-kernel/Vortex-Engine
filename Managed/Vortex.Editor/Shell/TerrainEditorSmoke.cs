using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Terrain;
using Editor.Core.Terrain;
using Editor.ECS;

namespace VortexEditor.Shell
{
    /// <summary>"terrain editor": open the Terrain Editor on the first terrain of the scene (a flat 64 m one is created when
    /// the scene has none), raise a stroke across its map, check the heights rose and were saved, lower them back, close.</summary>
    internal static class TerrainEditorSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("terrain editor", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("terrain editor: no scene — skipped"); return true; }
            GameEntity ter = null;
            foreach (var e in Panels.AssetBrowser.WorldThumbs.Walk(scene)) if (e.GetComponent<Editor.ECS.Components.Rendering.Terrain>() != null) { ter = e; break; }
            bool created = false;
            if (ter == null) { ter = EditorCommands.CreateTerrain(scene); created = true; }
            if (ter == null) { log.LogError("SMOKE FAIL terrain editor: no terrain and none could be created"); return false; }
            EditorWindows.TerrainEditor(ter);
            await Task.Delay(400);
            var w = TerrainEditorWindow.Current;
            if (w == null || !ReferenceEquals(w.ShownTerrain, ter)) { log.LogError("SMOKE FAIL terrain editor: the window did not open on " + ter.Name); return false; }
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(ter, out data, out cell)) { log.LogError("SMOKE FAIL terrain editor: no data"); return false; }
            int n = data.Resolution; float mid = (n - 1) * 0.5f;
            float before = data.SampleHeight(mid, mid);
            TerrainToolService.Radius = Math.Max(2f, cell * 6f); TerrainToolService.Strength = 0.8f;
            w.SelectTool(0);   // raise
            var rect = w.SimulateStroke(mid - 4f, mid, mid + 4f, mid);
            float after = data.SampleHeight(mid, mid);
            bool rose = after > before + 0.05f && !rect.IsEmpty;
            string file = TerrainService.ResolvePath(ter.GetComponent<Editor.ECS.Components.Rendering.Terrain>().DataPath);
            bool saved = !string.IsNullOrEmpty(file) && System.IO.File.Exists(file) && (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(file)).TotalSeconds < 30;
            w.SelectTool(1);   // lower it back
            w.SimulateStroke(mid - 4f, mid, mid + 4f, mid);
            float restored = data.SampleHeight(mid, mid);
            await Task.Delay(200);
            w.Close();
            if (created) { scene.RemoveEntity(ter); try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); } catch { } }
            string r = "raise " + before.ToString("0.00") + " -> " + after.ToString("0.00") + " m over " + rect.Width + "x" + rect.Height + " samples, lowered back to " + restored.ToString("0.00") + ", saved=" + saved;
            if (rose && saved) { log.Log("SMOKE OK   terrain editor: " + r); return true; }
            log.LogError("SMOKE FAIL terrain editor: " + r); return false;
        }
    }
}
