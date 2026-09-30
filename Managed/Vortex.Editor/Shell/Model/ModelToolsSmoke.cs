using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using VortexEditor.Shell.AssetImport;

namespace VortexEditor.Shell.ModelTools
{
    /// <summary>Smoke checks for the Model Editor, Mesh Editor, Asset Viewer, Texture Editor, Import dialog and Stress Test:
    /// each opens on real assets of the test project, verifies its preview / data / round trip and captures a screenshot.</summary>
    internal static class ModelToolsSmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("model editor: live preview, material edit re-renders, Save Materials writes the sidecar", ModelEditor);
            SmokeRegistry.Add("model editor: skeleton + animation clips", ModelEditorAnimations);
            SmokeRegistry.Add("mesh editor: submeshes, highlight + isolate", MeshEditor);
            SmokeRegistry.Add("asset viewer: model", () => Viewer(FindModel(true), "asset_viewer_model.png"));
            SmokeRegistry.Add("asset viewer: prefab", () => Viewer(FindPrefab(), "asset_viewer_prefab.png"));
            SmokeRegistry.Add("asset viewer: material (textured, shape switch)", ViewerMaterial);
            SmokeRegistry.Add("asset viewer: texture (channels, zoom)", ViewerTexture);
            SmokeRegistry.Add("asset viewer: primitive", () => Viewer("Primitive:Cube", "asset_viewer_primitive.png"));
            SmokeRegistry.Add("texture editor: info, channel view, settings round trip", TextureEditor);
            SmokeRegistry.Add("asset import: model + texture into the project, result summary", Import);
            SmokeRegistry.Add("stress test: instanced crowd in the editor + live stats", StressTest);
        }

        // ------------------------------------------------------------------ asset lookup

        private static string Root => ProjectData.Current?.Path;

        private static IEnumerable<string> Assets(params string[] exts)
        {
            if (string.IsNullOrEmpty(Root)) return Enumerable.Empty<string>();
            var dir = Path.Combine(Root, "Assets");
            if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
            return Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()) && !f.Contains("/SmokeImport/") && !f.Contains("/.ve/"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A model alone in its folder (so saving its sidecars touches nothing else) — preferably an unrigged prop;
        /// any model otherwise.</summary>
        private static string FindModel(bool ownFolder)
        {
            var models = Assets(".gltf", ".glb").ToList();
            if (ownFolder)
            {
                bool Alone(string m) => Directory.GetFiles(Path.GetDirectoryName(m)).Count(ModelDocument.IsSupportedModel) == 1;
                var own = models.FirstOrDefault(m => m.Contains("/Props/") && Alone(m)) ?? models.FirstOrDefault(Alone);
                if (own != null) return own;
            }
            return models.FirstOrDefault();
        }

        private static string FindRigged() => Assets(".glb", ".gltf").FirstOrDefault(f => Path.GetFileName(f).Contains("soldier", StringComparison.OrdinalIgnoreCase))
                                              ?? Assets(".glb").FirstOrDefault(f => f.Contains("/Character/"));

        private static string FindPrefab()
        {
            foreach (var p in Assets(".ventity"))
            {
                try { if (File.ReadAllText(p).Contains("\"meshPath\"")) return p; } catch { }
            }
            return Assets(".ventity").FirstOrDefault();
        }

        private static async Task<bool> Wait(Task<bool> t, int ms = 30000)
        {
            var done = await Task.WhenAny(t, Task.Delay(ms));
            return done == t && t.Result;
        }

        /// <summary>Mean B, G, R of a rendered frame.</summary>
        private static double[] Mean(PreviewImage img)
        {
            var m = new double[3];
            if (img?.Bgra == null) return m;
            long n = 0;
            for (int i = 0; i + 2 < img.Bgra.Length; i += 4) { m[0] += img.Bgra[i]; m[1] += img.Bgra[i + 1]; m[2] += img.Bgra[i + 2]; n++; }
            if (n > 0) for (int c = 0; c < 3; c++) m[c] /= n;
            return m;
        }

        /// <summary>True when two frames differ visibly (mean colour moved).</summary>
        private static bool Differs(double[] a, double[] b, double min = 0.3)
            => Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]) > min;

        private static string Fmt(double[] m) => string.Join("/", m.Select(v => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));

        private static void Log(string s) => ConsoleService.Instance.Log("  smoke · " + s);

        // ------------------------------------------------------------------ model editor

        private static async Task<bool> ModelEditor()
        {
            var model = FindModel(true);
            if (model == null) { Log("no model in the project"); return false; }
            var w = new ModelEditorWindow(model);
            EditorWindows.Show(w);
            try
            {
                if (!await Wait(w.WhenReady)) { Log("model editor did not load " + model); return false; }
                await SmokeRegistry.Settle(700);
                bool rendered = Ui.HasContent(w.Preview.LastImage);
                var doc = w.Document;
                Log("model editor: " + Path.GetFileName(model) + " — " + doc.StatsSummary + " · " + (doc.GeometrySummary ?? "no counts"));
                SmokeRegistry.Capture(w, "model_editor.png");

                // a live edit must re-render the preview: edit the material of the biggest submesh (surely visible)
                var biggest = doc.Submeshes.OrderByDescending(s => s.TriangleCount >= 0 ? s.TriangleCount : (s.BoundsSize != null ? s.BoundsSize[0] * s.BoundsSize[1] * s.BoundsSize[2] : 0)).First();
                w.SelectMaterial(biggest.MaterialIndex);
                await SmokeRegistry.Settle(300);
                var before = Mean(w.Preview.LastImage);
                // (an assigned albedo map replaces the base colour in the renderer, so clear it to see the colour)
                w.ApplyEdit(m => { m.BaseColor = new[] { 0.9f, 0.1f, 0.1f, 1f }; m.Roughness = 0.2f; m.GetSlot(TextureMapType.Albedo)?.Clear(); });
                await SmokeRegistry.Settle(500);
                var after = Mean(w.Preview.LastImage);
                bool changed = Differs(before, after);
                Log("model editor: mean colour before " + Fmt(before) + " after " + Fmt(after));

                // Save Materials writes materials/submesh_N.vmat with the edit
                w.Save();
                var sidecar = doc.SidecarPath(biggest.Index);
                var saved = File.Exists(sidecar) ? VortexMaterial.Load(sidecar) : null;
                bool savedOk = saved != null && Math.Abs(saved.BaseColor[0] - 0.9f) < 0.01f && Math.Abs(saved.Roughness - 0.2f) < 0.01f;
                await SmokeRegistry.Settle(400);
                SmokeRegistry.Capture(w, "model_editor_edited.png");

                // the Material Editor saving the same .vmat must reach the open Model Editor (live reload)
                bool reloaded = false;
                if (saved != null)
                {
                    await Task.Delay(50);
                    saved.Metallic = 0.33f;
                    saved.Save(sidecar);
                    File.SetLastWriteTimeUtc(sidecar, DateTime.UtcNow.AddSeconds(1));
                    for (int i = 0; i < 30 && !reloaded; i++) { await SmokeRegistry.Settle(100); reloaded = Math.Abs(doc.MaterialOf(biggest).Metallic - 0.33f) < 0.01f; }
                }

                w.Tabs.SelectedIndex = 1;   // Texture Library
                await SmokeRegistry.Settle(600);
                SmokeRegistry.Capture(w, "model_editor_library.png");
                w.Tabs.SelectedIndex = 2;   // Assigned
                await SmokeRegistry.Settle(300);
                SmokeRegistry.Capture(w, "model_editor_assigned.png");
                Log("model editor: rendered=" + rendered + " editReRendered=" + changed + " sidecarSaved=" + savedOk + " liveReload=" + reloaded);
                return rendered && changed && savedOk && reloaded;
            }
            finally { w.Close(); }
        }

        private static async Task<bool> ModelEditorAnimations()
        {
            var model = FindRigged();
            if (model == null) { Log("no rigged model — skipped"); return true; }
            var w = new ModelEditorWindow(model);
            EditorWindows.Show(w);
            try
            {
                if (!await Wait(w.WhenReady)) return false;
                w.Tabs.SelectedIndex = 3;   // Animations (loads lazily)
                for (int i = 0; i < 100; i++) { await SmokeRegistry.Settle(200); if (w.Tabs.SelectedIndex == 3 && Ui.HasContent(w.Preview.LastImage)) break; }
                await SmokeRegistry.Settle(2500);
                var info = await w.Document.LoadAnimationInfoAsync();
                Log("model editor animations: " + Path.GetFileName(model) + " bones=" + info.Bones + " clips=" + info.Clips.Count);
                SmokeRegistry.Capture(w, "model_editor_animations.png");
                return Ui.HasContent(w.Preview.LastImage) && (info.Bones > 0 || info.Clips.Count > 0 || info.SkeletonNodes > 0);
            }
            finally { w.Close(); }
        }

        // ------------------------------------------------------------------ mesh editor

        private static async Task<bool> MeshEditor()
        {
            var model = FindRigged() ?? FindModel(false);
            if (model == null) return false;
            var w = new MeshEditorWindow(model, 1);   // the small submesh selected: the big one is ghosted
            EditorWindows.Show(w);
            try
            {
                if (!await Wait(w.WhenReady)) return false;
                await SmokeRegistry.Settle(600);
                bool highlight = Ui.HasContent(w.Preview.LastImage);
                SmokeRegistry.Capture(w, "mesh_editor.png");
                var h1 = Mean(w.Preview.LastImage);
                w.FrameSelection();
                await SmokeRegistry.Settle(400);
                SmokeRegistry.Capture(w, "mesh_editor_framed.png");
                w.Preview.ResetView();
                await SmokeRegistry.Settle(300);
                w.Select(-1);
                await SmokeRegistry.Settle(400);
                var h2 = Mean(w.Preview.LastImage);
                bool all = Ui.HasContent(w.Preview.LastImage) && (w.Document.Submeshes.Count < 2 || Differs(h1, h2));
                Log("mesh editor: highlighted " + Fmt(h1) + " / all " + Fmt(h2));
                SmokeRegistry.Capture(w, "mesh_editor_all.png");
                // isolate: only the selected submesh remains, re-framed (the small last submesh: a very different frame)
                w.Select(w.Document.Submeshes.Count - 1);
                w.SetIsolate(true);
                await SmokeRegistry.Settle(400);
                bool isolated = Ui.HasContent(w.Preview.LastImage) && (w.Document.Submeshes.Count < 2 || Differs(h2, Mean(w.Preview.LastImage)));
                SmokeRegistry.Capture(w, "mesh_editor_isolated.png");
                Log("mesh editor: " + Path.GetFileName(model) + " submeshes=" + w.Document.Submeshes.Count + " counts=" + (w.Document.Submeshes.FirstOrDefault()?.GeometryInfo ?? "n/a") + " isolate=" + isolated);
                return highlight && all && isolated;
            }
            finally { w.Close(); }
        }

        // ------------------------------------------------------------------ asset viewer

        private static async Task<bool> Viewer(string path, string capture)
        {
            if (path == null) { Log("nothing to preview for " + capture); return false; }
            var w = new ModelViewerWindow(path);
            EditorWindows.Show(w);
            try
            {
                bool ok = await Wait(w.WhenReady);
                await SmokeRegistry.Settle(700);
                ok = ok && Ui.HasContent(w.Preview?.LastImage);
                SmokeRegistry.Capture(w, capture);
                Log("asset viewer " + w.Kind + ": " + Path.GetFileName(path) + " rendered=" + ok);
                return ok;
            }
            finally { w.Close(); }
        }

        private static async Task<bool> ViewerMaterial()
        {
            // a material with texture maps (relative paths must resolve against the .vmat's folder)
            var vmat = Assets(".vmat").FirstOrDefault(p => { try { var v = VortexMaterial.Load(p); return v != null && !string.IsNullOrEmpty(v.AlbedoTexture) && !v.AlbedoTexture.Contains("*"); } catch { return false; } })
                       ?? Assets(".vmat").FirstOrDefault();
            if (vmat == null) return false;
            var w = new ModelViewerWindow(vmat);
            EditorWindows.Show(w);
            try
            {
                bool ok = await Wait(w.WhenReady);
                await SmokeRegistry.Settle(600);
                ok = ok && Ui.HasContent(w.Preview.LastImage);
                SmokeRegistry.Capture(w, "asset_viewer_material.png");
                var h = Mean(w.Preview.LastImage);
                w.SetShape("Cube");
                await SmokeRegistry.Settle(500);
                bool switched = Ui.HasContent(w.Preview.LastImage) && Differs(h, Mean(w.Preview.LastImage));
                SmokeRegistry.Capture(w, "asset_viewer_material_cube.png");
                Log("asset viewer material: " + Path.GetFileName(vmat) + " rendered=" + ok + " shapeSwitch=" + switched);
                return ok && switched;
            }
            finally { w.Close(); }
        }

        /// <summary>A PNG with an alpha channel (IHDR colour type 4 / 6) — shows the checkerboard and the A channel.</summary>
        private static string FindAlphaTexture()
            => Assets(".png").FirstOrDefault(f =>
            {
                try { using (var fs = File.OpenRead(f)) { var h = new byte[26]; return fs.Read(h, 0, 26) == 26 && h[1] == (byte)'P' && (h[25] == 6 || h[25] == 4) && new FileInfo(f).Length > 4096; } }
                catch { return false; }
            });

        /// <summary>A real colour texture (a *_diff map from the Textures folder), else any image.</summary>
        private static string FindColorTexture()
            => Assets(".jpg", ".png").FirstOrDefault(f => f.Contains("/Textures/") && Path.GetFileName(f).Contains("_diff")) ?? Assets(".png", ".jpg").FirstOrDefault();

        private static string FindTexture() => FindColorTexture();

        private static async Task<bool> ViewerTexture()
        {
            var tex = FindAlphaTexture() ?? FindColorTexture();
            if (tex == null) return false;
            var w = new ModelViewerWindow(tex);
            EditorWindows.Show(w);
            try
            {
                bool ok = await Wait(w.WhenReady);
                await SmokeRegistry.Settle(500);
                var rgb = w.TextureViewer.View.CurrentBitmap;
                w.TextureViewer.SetChannel(ChannelView.R);
                w.TextureViewer.View.ZoomIn();
                await SmokeRegistry.Settle(300);
                var red = w.TextureViewer.View.CurrentBitmap;
                SmokeRegistry.Capture(w, "asset_viewer_texture.png");
                bool channels = rgb != null && red != null && !ReferenceEquals(rgb, red);
                Log("asset viewer texture: " + Path.GetFileName(tex) + " decoded=" + ok + " channelView=" + channels + " zoom=" + w.TextureViewer.View.ZoomText);
                return ok && channels;
            }
            finally { w.Close(); }
        }

        // ------------------------------------------------------------------ texture editor

        private static async Task<bool> TextureEditor()
        {
            var tex = FindTexture();
            if (tex == null) return false;
            var w = new TextureEditorWindow(tex);
            EditorWindows.Show(w);
            bool ok;
            try
            {
                ok = await Wait(w.WhenReady);
                await SmokeRegistry.Settle(400);
                ok = ok && w.SizeText.Contains("x");
                w.Viewer.SetChannel(ChannelView.G);
                w.SetTextureType(1);                          // Normal Map -> sRGB off
                bool srgbOff = !w.Srgb;
                await SmokeRegistry.Settle(300);
                SmokeRegistry.Capture(w, "texture_editor.png");
                w.Apply();
                var meta = DataSerializer.LoadFromJson<AssetMetadata>(tex + ".vmeta");
                ok = ok && srgbOff && meta?.ImportSettings != null && meta.ImportSettings.TryGetValue("textureType", out var t) && t == "1";
                Log("texture editor: " + Path.GetFileName(tex) + " size=" + w.SizeText + " saved=" + ok);
            }
            finally { w.Close(); }
            // reopen: the saved settings win over the file-name guess
            var w2 = new TextureEditorWindow(tex);
            EditorWindows.Show(w2);
            try
            {
                bool loaded = await Wait(w2.WhenReady);
                bool roundTrip = loaded && w2.TextureTypeIndex == 1 && !w2.Srgb && !w2.IsDirty;
                Log("texture editor round trip=" + roundTrip);
                return ok && roundTrip;
            }
            finally { w2.Close(); }
        }

        // ------------------------------------------------------------------ import

        private static async Task<bool> Import()
        {
            // source outside the project: a copy of a glTF prop folder (model + .bin + textures/) and a loose image
            var model = Assets(".gltf").FirstOrDefault(f => File.Exists(Path.ChangeExtension(f, ".bin")) || Directory.GetFiles(Path.GetDirectoryName(f)).Any(x => x.EndsWith(".bin")));
            var image = FindTexture();
            if (model == null || image == null) { Log("import: no source assets"); return false; }
            string src = Path.Combine(Path.GetTempPath(), "vortex-smoke-import-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            CopyDir(Path.GetDirectoryName(model), Path.Combine(src, "model"));
            string srcModel = Path.Combine(src, "model", Path.GetFileName(model));
            Directory.CreateDirectory(Path.Combine(src, "loose"));
            string srcImage = Path.Combine(src, "loose", "smoke_decal" + Path.GetExtension(image));
            File.Copy(image, srcImage, true);
            string target = Path.Combine(Root, "Assets", "SmokeImport");
            var dlg = new AssetImportDialog(new[] { srcModel, srcImage }, target);
            EditorWindows.Show(dlg);
            try
            {
                await SmokeRegistry.Settle(400);
                SmokeRegistry.Capture(dlg, "import_options.png");
                await dlg.ImportAsync(confirm: false);
                await SmokeRegistry.Settle(500);
                SmokeRegistry.Capture(dlg, "import_result.png");
                var mr = dlg.Reports.FirstOrDefault(r => r.IsModel);
                var ir = dlg.Reports.FirstOrDefault(r => !r.IsModel);
                string name = Path.GetFileNameWithoutExtension(srcModel);
                bool modelOk = mr != null && mr.Success && File.Exists(Path.Combine(target, name, Path.GetFileName(srcModel)))
                               && File.Exists(Path.Combine(target, name, "materials", "submesh_0.vmat")) && mr.MaterialFiles.Count > 0;
                bool imageOk = ir != null && ir.Success && File.Exists(Path.Combine(target, Path.GetFileName(srcImage)));
                var meta = imageOk ? DataSerializer.LoadFromJson<AssetMetadata>(Path.Combine(target, Path.GetFileName(srcImage) + ".vmeta")) : null;
                bool tagged = meta != null && AssetTagService.Instance.GetTags(meta.Guid).Contains("Imported");
                Log("import: model=" + modelOk + " (" + (mr?.SubmeshNames.Count ?? 0) + " submeshes, " + (mr?.MaterialFiles.Count ?? 0) + " .vmat, " + (mr?.CopiedTextures.Count ?? 0) + " textures, " + (mr?.MissingTextures.Count ?? 0) + " missing) image=" + imageOk + " tagged=" + tagged + (mr?.Error != null ? " error=" + mr.Error : ""));
                // Add to Scene: one entity (a parent with one child per submesh bound to its sidecar .vmat)
                var scene = ProjectData.Current?.ActiveScene;
                int before = scene?.Entities?.Count ?? 0;
                var placed = mr != null && mr.Success ? AssetImportDialog.AddToScene(new List<ImportReport> { mr }) : null;
                bool added = placed != null && scene != null && scene.Entities.Count == before + 1;
                if (placed != null && placed.Children != null && placed.Children.Count > 0)
                {
                    var mrc = placed.Children[0].GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>();
                    added = added && mrc != null && (mrc.MaterialPath ?? "").EndsWith("materials/submesh_0.vmat");
                }
                if (placed != null) { try { scene.RemoveEntity(placed); } catch { } }
                Log("import: add to scene=" + added + (placed != null ? " (" + placed.Name + ", " + (placed.Children?.Count ?? 0) + " parts)" : ""));
                return dlg.ShowingResults && modelOk && imageOk && tagged && added && dlg.ImportedPaths.Length == 2;
            }
            finally
            {
                dlg.Close();
                try { Directory.Delete(src, true); } catch { }
            }
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) if (!f.EndsWith(".vmeta") && !f.EndsWith(".vimport")) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) if (!Path.GetFileName(d).Equals("materials", StringComparison.OrdinalIgnoreCase)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        // ------------------------------------------------------------------ stress test

        private static async Task<bool> StressTest()
        {
            var model = FindModel(true);
            if (model == null) return false;
            var w = new StressTestWindow(model);
            EditorWindows.Show(w);
            try
            {
                w.CountText = "64";
                w.RunInEditor();
                await SmokeRegistry.Settle(1200);
                bool running = StressTestService.Active && StressTestService.Count == 64;
                SmokeRegistry.Capture(w, "stress_test.png");
                Log("stress test: " + w.StatsText.Replace("\n", " | "));
                // benchmark scene: several models near -> far, 64 copies each
                w.RunBenchmarkInEditor();
                await SmokeRegistry.Settle(1000);
                bool bench = StressTestService.Active && (StressTestService.ModelName ?? "").StartsWith("Benchmark") && StressTestService.Count >= 64;
                Log("stress benchmark: " + StressTestService.ModelName + " x" + StressTestService.Count);
                StressTestService.Stop();
                await SmokeRegistry.Settle(300);
                return running && bench && !StressTestService.Active && w.StatsText.Contains("FPS");
            }
            finally { StressTestService.Stop(); w.Close(); }
        }
    }
}
