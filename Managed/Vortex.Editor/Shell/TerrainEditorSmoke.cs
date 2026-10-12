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

        private static async Task Tour(TerrainEditorWindow w, GameEntity ter, TerrainData data, float cell, string dir)
        {
            var log = ConsoleService.Instance;
            System.IO.Directory.CreateDirectory(dir);
            int n = data.Resolution; float mid = (n - 1) * 0.5f;
            async Task Snap(string name, int settle = 500) { await Task.Delay(settle); w.RedrawNow(); await Task.Delay(120); w.CaptureTo(System.IO.Path.Combine(dir, name + ".png")); log.Log("tour: " + name); }
            w.Width = 1240; w.Height = 860;
            w.ShowTab(0); await Snap("01_terrain_editor_geoeffnet_sculpt_tab", 900);
            TerrainToolService.Radius = Math.Max(3f, cell * 14f); TerrainToolService.Strength = 0.9f; TerrainToolService.Hardness = 0.3f;
            w.SelectTool(0); w.SimulateStroke(mid - 40f, mid + 30f, mid + 40f, mid + 30f, 24); await Snap("02_sculpt_raise_strich_auf_der_karte");
            Editor.DllWrapper.VortexAPI.CaptureFrame(System.IO.Path.Combine(dir, "03_derselbe_strich_im_3d_viewport.bmp"));
            w.SelectTool(2); w.SimulateStroke(mid - 40f, mid + 30f, mid + 40f, mid + 30f, 24); await Snap("04_smooth_glaettet_den_strich");
            w.ShowTab(1); w.ShowLayersMap(true); await Snap("05_paint_tab_karte_zeigt_layer", 700);
            w.SelectTool(5); TerrainToolService.PaintLayer = 2; w.SimulateStroke(mid - 30f, mid - 30f, mid + 30f, mid - 30f, 24); await Snap("06_paint_layer3_gemalt");
            w.ShowLayersMap(false);
            w.ShowTab(2); await Snap("07_generate_tab_landschaft_aus_rauschen", 600);
            w.ShowTab(3); await Snap("08_heightmap_tab_import_export", 600);
            w.ShowTab(4); await Snap("09_layers_tab_materialien", 600);
            w.ShowTab(5); await Snap("10_settings_tab", 600);
            for (int i = 0; i < 3; i++) { try { Editor.Core.UndoRedo.UndoRedoManager.Instance.Undo(); } catch { } }
            w.ShowTab(0); await Snap("11_dreimal_undo_alles_zurueck", 900);
            Editor.DllWrapper.VortexAPI.CaptureFrame(System.IO.Path.Combine(dir, "12_viewport_nach_undo.bmp"));
        }

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
            // VORTEX_TERRAIN_TOUR=<dir>: a picture of every step for the user (the window after each tab and stroke)
            string tour = Environment.GetEnvironmentVariable("VORTEX_TERRAIN_TOUR");
            if (!string.IsNullOrEmpty(tour)) { await Tour(w, ter, data, cell, tour); }
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
