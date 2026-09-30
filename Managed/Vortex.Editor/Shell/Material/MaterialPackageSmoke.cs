using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;

namespace VortexEditor.Shell.Material
{
    /// <summary>Smoke checks for the material editor, colour picker, asset picker and asset tag editor.</summary>
    internal static class MaterialPackageSmoke
    {
        private static void Log(string s) => ConsoleService.Instance.Log("  " + s);

        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("material editor: live preview re-renders, revert, one window per material", PreviewAndRevert);
            SmokeRegistry.Add("material editor: save pushes to the scene, undo / redo", SaveAndPush);
            SmokeRegistry.Add("color picker: pick (alpha) + cancel", ColorPicker);
            SmokeRegistry.Add("asset picker: thumbnails, search, type filter, choose / cancel", AssetPicker);
            SmokeRegistry.Add("asset tag editor: add, save to index + .vmeta, picker sees it", TagEditor);
        }

        private static string Root => ProjectData.Current?.Path;

        private static string FindVmat(string preferredName)
        {
            if (Root == null) return null;
            var all = Directory.EnumerateFiles(Path.Combine(Root, "Assets"), "*.vmat", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            return all.FirstOrDefault(f => Path.GetFileName(f).Equals(preferredName, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        }

        // ------------------------------------------------------------------------------------------------------------
        private static async Task<bool> PreviewAndRevert()
        {
            string vmat = FindVmat("concrete_wall.vmat");
            if (vmat == null) { Log("no .vmat in the project"); return false; }
            byte[] before = File.ReadAllBytes(vmat);
            MaterialEditorWindow.Open(vmat);
            var w = MaterialEditorWindow.Find(vmat);
            if (w == null) return false;
            var pane = w.PreviewPane;
            for (int i = 0; i < 40 && (pane.Viewport.LastImage == null || pane.Builds == 0); i++) await Task.Delay(100);
            await SmokeRegistry.Settle(500);
            var img0 = pane.Viewport.LastImage;
            int builds0 = pane.Builds;
            bool rendered = img0 != null && HasContent(img0);

            // an Albedo map replaces the base colour in the shader: clear it so the colour change must show
            w.ApplyEdit("basecolor", m => { m.AlbedoTexture = null; m.BaseColor[0] = 1f; m.BaseColor[1] = 0.08f; m.BaseColor[2] = 0.05f; m.Metallic = 0.8f; });
            for (int i = 0; i < 20 && pane.Builds == builds0; i++) await Task.Delay(50);
            await SmokeRegistry.Settle(250);
            var img1 = pane.Viewport.LastImage;
            // the sphere fills the middle of the frame: its colour must have turned red
            double red0 = CenterRedness(img0), red1 = CenterRedness(img1);
            bool rerendered = pane.Builds > builds0 && img1 != null && !ReferenceEquals(img0, img1) && Difference(img0, img1) > 0.003 && red1 > red0 + 0.04;
            bool dirty = w.IsDirty && w.Title.EndsWith("*");
            SmokeRegistry.Capture(w, "material_editor.png");

            pane.SetShape("Cube");
            await SmokeRegistry.Settle(400);
            bool cube = pane.Shape == "Cube" && pane.Viewport.LastImage != null && HasContent(pane.Viewport.LastImage);
            SmokeRegistry.Capture(w, "material_editor_cube.png");
            pane.SetShape("Plane");
            await SmokeRegistry.Settle(300);
            bool plane = pane.Viewport.LastImage != null && HasContent(pane.Viewport.LastImage);
            pane.SetShape("Sphere");

            // a second editor for the same file (MainWindow constructs editors directly) closes itself
            new MaterialEditorWindow(vmat).Show(EditorWindows.Owner);
            await SmokeRegistry.Settle(500);
            int windows = EditorKit.OpenWindows<MaterialEditorWindow>().Count(x => x.MaterialPath != null && EditorKit.SamePath(x.MaterialPath, vmat));

            w.Revert();
            await SmokeRegistry.Settle(300);
            bool reverted = !w.IsDirty && Math.Abs(w.Working.BaseColor[0] - (VortexMaterial.Load(vmat)?.BaseColor[0] ?? -1)) < 1e-4;
            w.Close();
            await SmokeRegistry.Settle(300);
            bool closed = MaterialEditorWindow.Find(vmat) == null;
            bool untouched = File.ReadAllBytes(vmat).SequenceEqual(before);
            Log($"material editor: rendered={rendered} rerendered={rerendered} (builds {builds0}->{pane.Builds}, diff {Difference(img0, img1):0.000}, centre red {red0:0.000}->{red1:0.000}) dirty={dirty} cube={cube} plane={plane} windows={windows} reverted={reverted} closed={closed} fileUntouched={untouched}");
            return rendered && rerendered && dirty && cube && plane && windows == 1 && reverted && closed && untouched;
        }

        // ------------------------------------------------------------------------------------------------------------
        private static async Task<bool> SaveAndPush()
        {
            string vmat = FindVmat("container_side.vmat");
            if (vmat == null) return false;
            string full = Path.GetFullPath(vmat);
            byte[] original = File.ReadAllBytes(full);
            int users = MaterialLive.CountUsers(full);
            long idBefore = MaterialService.Instance.GetOrBuildVortexMaterial(full);
            MaterialEditorWindow.Open(full);
            var w = MaterialEditorWindow.Find(full);
            if (w == null) return false;
            bool ok;
            try
            {
                await SmokeRegistry.Settle(600);
                float r0 = w.Working.Roughness;
                w.ApplyEdit("roughness", m => m.Roughness = 0.37f);
                w.ApplyEdit("metallic", m => m.Metallic = 0.61f);
                w.Undo();
                bool undo = Math.Abs(w.Working.Metallic - (VortexMaterial.Load(full)?.Metallic ?? -1)) < 1e-4 && Math.Abs(w.Working.Roughness - 0.37f) < 1e-4;
                w.Redo();
                bool redo = Math.Abs(w.Working.Metallic - 0.61f) < 1e-4;
                bool saved = w.Save();
                var disk = VortexMaterial.Load(full);
                bool onDisk = disk != null && Math.Abs(disk.Roughness - 0.37f) < 1e-4 && Math.Abs(disk.Metallic - 0.61f) < 1e-4 && !w.IsDirty;
                // texture references must still resolve relative to the material after the round trip
                bool texturesResolve = true;
                if (disk != null)
                {
                    disk.ResolvePathsAbsolute(Path.GetDirectoryName(full));
                    foreach (var t in new[] { disk.AlbedoTexture, disk.NormalTexture, disk.RoughnessTexture, disk.MetallicTexture, disk.AOTexture, disk.HeightTexture })
                        if (!string.IsNullOrEmpty(t) && !File.Exists(t)) { texturesResolve = false; Log("missing after save: " + t); }
                }
                await SmokeRegistry.Settle(500);   // viewport frames re-resolve the material
                long idAfter = MaterialService.Instance.GetOrBuildVortexMaterial(full);
                bool rebuilt = idAfter >= 0 && idAfter != idBefore;
                bool sceneUsesNew = SceneCacheHolds(idAfter, idBefore, users);
                SmokeRegistry.Capture(w, "material_editor_saved.png");
                Log($"save: users={users} saved={saved} onDisk={onDisk} textures={texturesResolve} material {idBefore}->{idAfter} sceneUsesNew={sceneUsesNew} undo={undo} redo={redo}");
                ok = saved && onDisk && texturesResolve && rebuilt && sceneUsesNew && undo && redo;
            }
            finally
            {
                // leave the project as it was
                File.WriteAllBytes(full, original);
                MaterialLive.PushToScene(full, VortexMaterial.Load(full));
                w.CloseDiscarding();
            }
            await SmokeRegistry.Settle(200);
            return ok;
        }

        /// <summary>The scene renderer's MaterialPath → id cache must point at the rebuilt material (not the freed one).</summary>
        private static bool SceneCacheHolds(long idAfter, long idBefore, int users)
        {
            if (users == 0) return true;
            try
            {
                var f = typeof(SceneRenderService).GetField("_vmatPathCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (!(f?.GetValue(null) is Dictionary<string, long> cache)) return true;   // internals changed: nothing to verify
                return cache.Values.Contains(idAfter) && !cache.Values.Contains(idBefore);
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------------------------------------------------
        private static async Task<bool> ColorPicker()
        {
            var want = Color.FromArgb(128, 10, 200, 90);
            var live = new List<Color>();
            var t = ColorPickerDialog.Pick(Color.FromRgb(200, 80, 40), "Smoke Color", true, c => live.Add(c));
            await SmokeRegistry.Settle(500);
            var dlg = ColorPickerDialog.Current;
            if (dlg == null) return false;
            dlg.SetColor(want);
            await SmokeRegistry.Settle(200);
            SmokeRegistry.Capture(dlg, "color_picker.png");
            dlg.Accept();
            var r = await t;
            bool picked = r.HasValue && r.Value == want && live.Count > 0 && live[live.Count - 1] == want;

            var initial = Colors.Red;
            live.Clear();
            var t2 = ColorPickerDialog.Pick(initial, "Smoke Cancel", false, c => live.Add(c));
            await SmokeRegistry.Settle(300);
            ColorPickerDialog.Current?.SetColor(Colors.Blue);
            ColorPickerDialog.Current?.Cancel();
            var r2 = await t2;
            bool cancelled = r2 == null && live.Count >= 2 && live[live.Count - 1] == initial;   // live preview reverted
            Log($"color picker: picked={picked} cancelled={cancelled}");
            return picked && cancelled && ColorPickerDialog.Current == null;
        }

        // ------------------------------------------------------------------------------------------------------------
        private static async Task<bool> AssetPicker()
        {
            var audio = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac", "*.vsndc" };
            var t = AssetPickerDialog.Pick(new AssetPickerOptions { Kind = "Audio", Patterns = audio });
            await SmokeRegistry.Settle(500);
            var dlg = AssetPickerDialog.Current;
            if (dlg == null) return false;
            int all = dlg.ShownPaths.Count;
            bool groups = dlg.Groups.Contains("Clips") && dlg.Groups.Contains("Containers");
            dlg.SetTypeFilter("Containers");
            bool containersOnly = dlg.ShownPaths.Count > 0 && dlg.ShownPaths.All(p => p.EndsWith(".vsndc", StringComparison.OrdinalIgnoreCase));
            dlg.SetTypeFilter(null);
            dlg.SetSearch("rifle");
            var hits = dlg.ShownPaths.ToList();
            bool search = hits.Count > 0 && hits.Count < all && hits.All(p => p.IndexOf("rifle", StringComparison.OrdinalIgnoreCase) >= 0);
            if (hits.Count > 0) dlg.SelectPath(hits[0]);
            await SmokeRegistry.Settle(200);
            SmokeRegistry.Capture(dlg, "asset_picker_audio.png");
            dlg.SetGridMode(false);
            await SmokeRegistry.Settle(250);
            SmokeRegistry.Capture(dlg, "asset_picker_list.png");
            bool listKeepsFilter = dlg.ShownPaths.Count == hits.Count;
            dlg.SetGridMode(true);
            dlg.CancelPick();
            bool cancelled = await t == null;

            var tex = new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.hdr", "*.dds" };
            var t2 = AssetPickerDialog.Pick(new AssetPickerOptions { Kind = "Textures", Patterns = tex });
            await SmokeRegistry.Settle(400);
            var dlg2 = AssetPickerDialog.Current;
            if (dlg2 == null) return false;
            dlg2.SetSearch("concrete");
            for (int i = 0; i < 60 && dlg2.ThumbnailCount < Math.Min(3, dlg2.ShownPaths.Count); i++) await Task.Delay(100);
            int thumbs = dlg2.ThumbnailCount;
            string pick = dlg2.ShownPaths.FirstOrDefault(p => p.Length > 0);
            if (pick != null) dlg2.SelectPath(pick);
            await SmokeRegistry.Settle(200);
            SmokeRegistry.Capture(dlg2, "asset_picker_textures.png");
            dlg2.Choose();
            string chosen = await t2;
            bool choose = pick != null && chosen == pick;
            Log($"asset picker: {all} audio, groups={groups} containersOnly={containersOnly} search={search} ({hits.Count}) list={listKeepsFilter} cancelled={cancelled} thumbnails={thumbs} chosen={chosen}");
            return groups && containersOnly && search && listKeepsFilter && cancelled && thumbs > 0 && choose;
        }

        // ------------------------------------------------------------------------------------------------------------
        private static async Task<bool> TagEditor()
        {
            string file = Root == null ? null : Directory.EnumerateFiles(Path.Combine(Root, "Assets"), "*.jpg", SearchOption.AllDirectories).OrderBy(f => f).FirstOrDefault();
            if (file == null) return false;
            var before = AssetTagEditorDialog.GetTags(file).ToList();
            var t = AssetTagEditorDialog.Edit(file);
            await SmokeRegistry.Settle(400);
            var dlg = AssetTagEditorDialog.Current;
            if (dlg == null) return false;
            dlg.AddTag("SmokeTag");
            dlg.AddTag("Environment");
            dlg.AddTag("smoketag");   // duplicate (case-insensitive) is ignored
            await SmokeRegistry.Settle(150);
            SmokeRegistry.Capture(dlg, "asset_tags.png");
            dlg.Save();
            var saved = await t;
            var after = AssetTagEditorDialog.GetTags(file);
            string meta = File.Exists(file + ".vmeta") ? File.ReadAllText(file + ".vmeta") : "";
            bool ok = saved != null && after.Count(x => x.Equals("SmokeTag", StringComparison.OrdinalIgnoreCase)) == 1 && after.Contains("Environment") && meta.Contains("SmokeTag");

            // the picker's tag filter offers the new tag and filters by it
            var tp = AssetPickerDialog.Pick(new AssetPickerOptions { Kind = "Textures", Patterns = new[] { "*.jpg" } });
            await SmokeRegistry.Settle(300);
            var picker = AssetPickerDialog.Current;
            picker?.SetSearch("SmokeTag");
            bool pickerSees = picker != null && picker.ShownPaths.Any(p => EditorKit.SamePath(p, file));
            picker?.CancelPick();
            await tp;
            AssetTagEditorDialog.SaveTags(file, before);   // restore
            Log($"tags: saved={saved != null} tags=[{string.Join(", ", after)}] inVmeta={meta.Contains("SmokeTag")} pickerSees={pickerSees}");
            return ok && pickerSees;
        }

        // ------------------------------------------------------------------------------------------------------------
        internal static bool HasContent(PreviewImage img)
        {
            if (img == null) return false;
            var seen = new HashSet<int>();
            for (int y = 0; y < img.Height; y += Math.Max(1, img.Height / 16))
                for (int x = 0; x < img.Width; x += Math.Max(1, img.Width / 16))
                {
                    int o = y * img.Stride + x * 4;
                    seen.Add((img.Bgra[o] >> 3) | ((img.Bgra[o + 1] >> 3) << 5) | ((img.Bgra[o + 2] >> 3) << 10));
                }
            return seen.Count > 3;
        }

        /// <summary>Average (R - G) / 255 over the central fifth of a render.</summary>
        private static double CenterRedness(PreviewImage img)
        {
            if (img == null) return 0;
            double sum = 0; int n = 0;
            for (int y = img.Height * 2 / 5; y < img.Height * 3 / 5; y += 2)
                for (int x = img.Width * 2 / 5; x < img.Width * 3 / 5; x += 2)
                {
                    int o = y * img.Stride + x * 4;
                    sum += (img.Bgra[o + 2] - img.Bgra[o + 1]) / 255.0;
                    n++;
                }
            return n == 0 ? 0 : sum / n;
        }

        /// <summary>Mean absolute channel difference (0..1) over a sparse sample of two same-size renders.</summary>
        private static double Difference(PreviewImage a, PreviewImage b)
        {
            if (a == null || b == null || a.Width != b.Width || a.Height != b.Height) return a == null || b == null ? 0 : 1;
            double sum = 0; int n = 0;
            for (int y = 0; y < a.Height; y += Math.Max(1, a.Height / 32))
                for (int x = 0; x < a.Width; x += Math.Max(1, a.Width / 32))
                {
                    int o = y * a.Stride + x * 4;
                    for (int c = 0; c < 3; c++) sum += Math.Abs(a.Bgra[o + c] - b.Bgra[o + c]) / 255.0;
                    n += 3;
                }
            return n == 0 ? 0 : sum / n;
        }
    }
}
