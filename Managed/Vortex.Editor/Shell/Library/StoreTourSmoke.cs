using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Services;
using VortexEditor.Panels;
using VortexEditor.Panels.AssetBrowser;

namespace VortexEditor.Shell.Library
{
    /// <summary>
    /// A live walk through every Asset Store source (VORTEX_STORE_LIVE=1 VORTEX_SMOKE_ONLY="store tour"): open the source,
    /// search, select the first result, capture <c>store_&lt;source&gt;.png</c>. Sources that need the user's key must
    /// show the key prompt instead of an error. Without VORTEX_STORE_LIVE it does nothing (no network in normal runs).
    /// </summary>
    internal static class StoreTourSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("store tour (live)", Run);

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            if (Environment.GetEnvironmentVariable("VORTEX_STORE_LIVE") != "1") { log.Log("store tour: skipped (set VORTEX_STORE_LIVE=1)"); return true; }
            var panel = StorePanel.Current;
            if (panel == null) { log.LogError("store tour: no Asset Store tab"); return false; }
            bool ok = true;
            foreach (var (id, query) in new[] { ("polyhaven", "chair"), ("ambientcg", "wood"), ("kenney", ""), ("polypizza", "crate"), ("freesound", "door creak"), ("sketchfab", "door"), ("mixamo", null), ("sonniss", null), ("soundstudio", null) })
            {
                EditorCommands.ShowStore(id);
                var view = panel.View;
                if (query != null)
                {
                    panel.SetSearch(query);
                    for (int i = 0; i < 150 && (view.Loading || (view.Tiles.Count == 0 && view.LastError == null && !view.ShowsKeyPrompt)); i++) await Task.Delay(100);
                    for (int i = 0; i < 60 && view.Tiles.Take(6).Any(t => !t.HasThumbnail); i++) await Task.Delay(100);
                    if (view.Tiles.Count > 0) view.Select(view.Tiles[0]);
                }
                await SmokeRegistry.Settle(1200);
                SmokeRegistry.Capture(EditorCommands.Window, "store_" + id + ".png");
                string state = query == null ? "page" : view.ShowsKeyPrompt ? "asks for the user's key" : view.Tiles.Count + " results, " + view.Tiles.Count(t => t.HasThumbnail) + " thumbnails" + (view.LastError != null ? ", error: " + view.LastError : "");
                bool good = query == null || view.ShowsKeyPrompt || (view.Tiles.Count > 0 && view.LastError == null);
                if (good) log.Log("store tour: " + id + " — " + state); else { log.LogError("store tour: " + id + " — " + state); ok = false; }
            }
            panel.SetSearch("");
            EditorCommands.Window.ShowPanel(MainWindow.PanelProject);
            return ok;
        }
    }
}
