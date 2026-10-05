using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Editor.Core.Assets.Library;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell.Library
{
    /// <summary>
    /// Editor smoke check of the library preview (VORTEX_SMOKE_ONLY=library preview): a glTF with its .bin and a texture
    /// in textures/ goes into the library; Preview opens it in the Model Viewer straight from the library — the
    /// companions sit next to it at their relative paths, the model renders, and nothing lands in the project.
    /// </summary>
    internal static class LibraryPreviewSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("library preview", Run);

        private static string WriteTriangle(string dir)
        {
            Directory.CreateDirectory(Path.Combine(dir, "textures"));
            // one double-sided triangle with UVs, its vertex data in tri.bin, a texture in textures/
            var bin = new MemoryStream();
            using (var w = new BinaryWriter(bin, Encoding.UTF8, true))
            {
                foreach (var f in new[] { -1f, 0f, 0f, 1f, 0f, 0f, 0f, 1.5f, 0f }) w.Write(f);
                foreach (var f in new[] { 0f, 1f, 1f, 1f, 0.5f, 0f }) w.Write(f);
            }
            File.WriteAllBytes(Path.Combine(dir, "smoke_tri.bin"), bin.ToArray());
            var rtb = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext()) dc.FillRectangle(new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x1F)), new Rect(0, 0, 32, 32));
            rtb.Save(Path.Combine(dir, "textures", "smoke_tri_diff.png"));
            string gltf = Path.Combine(dir, "smoke_tri.gltf");
            File.WriteAllText(gltf,
                "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1},\"material\":0}]}]," +
                "\"materials\":[{\"doubleSided\":true,\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0}}]," +
                "\"textures\":[{\"source\":0}],\"images\":[{\"uri\":\"textures/smoke_tri_diff.png\"}]," +
                "\"buffers\":[{\"uri\":\"smoke_tri.bin\",\"byteLength\":60}]," +
                "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":24}]," +
                "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[-1,0,0],\"max\":[1,1.5,0]}," +
                "{\"bufferView\":1,\"componentType\":5126,\"count\":3,\"type\":\"VEC2\"}]}");
            return gltf;
        }

        private static int CountFiles(string dir) { try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count(); } catch { return -1; } }

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("library preview smoke: " + why); return false; }
            var lib = GlobalAssetDatabase.Instance;
            string project = ProjectData.Current?.Path;
            if (!lib.IsAvailable || project == null) return Fail("library / project unavailable");
            string dir = Path.Combine(Path.GetTempPath(), "vx-libpreview-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ModelViewerWindow w = null;
            long entryId = 0;
            try
            {
                string gltf = WriteTriangle(dir);
                var r = lib.Register(gltf, new RegisterOptions
                {
                    Name = "smoke_tri", Explicit = true, Tags = new List<string> { "smoke" },
                    Companions = new Dictionary<string, string>
                    {
                        [Path.GetFullPath(Path.Combine(dir, "smoke_tri.bin"))] = "smoke_tri.bin",
                        [Path.GetFullPath(Path.Combine(dir, "textures", "smoke_tri_diff.png"))] = "textures/smoke_tri_diff.png",
                    },
                });
                if (!r.Success) return Fail("register: " + r.Error);
                entryId = r.Entry.Id;
                var entry = lib.Get(entryId);
                if (entry.Companions.Count != 2) return Fail("expected 2 companions, got " + entry.Companions.Count);
                int before = CountFiles(Path.Combine(project, "Assets"));

                LibraryView.Preview(entry);
                string path = LibraryView.LastPreviewPath;
                if (path == null || !File.Exists(path)) return Fail("no preview file");
                string pdir = Path.GetDirectoryName(path);
                if (!File.Exists(Path.Combine(pdir, "smoke_tri.bin")) || !File.Exists(Path.Combine(pdir, "textures", "smoke_tri_diff.png")))
                    return Fail("companions not laid out next to the preview file");
                w = EditorKit.OpenWindows<ModelViewerWindow>().LastOrDefault(x => x.AssetPath == path);
                if (w == null) return Fail("the Model Viewer did not open");
                if (!w.Title.Contains("(Library)")) return Fail("the viewer does not say the asset comes from the library");
                var ready = await Task.WhenAny(w.WhenReady, Task.Delay(8000));
                if (ready != w.WhenReady || !w.WhenReady.Result) return Fail("the model did not load in the viewer");
                for (int i = 0; i < 40 && w.Preview?.LastImage == null; i++) await Task.Delay(100);
                await SmokeRegistry.Settle(600);
                if (!MaterialPackageSmoke.HasContent(w.Preview?.LastImage)) return Fail("the viewer rendered nothing");
                SmokeRegistry.Capture(w, "library_preview.png");
                int after = CountFiles(Path.Combine(project, "Assets"));
                if (after != before) return Fail("the preview changed the project (" + before + " → " + after + " files)");
                log.Log("library preview smoke: OK → " + Path.GetFileName(path));
                return true;
            }
            finally
            {
                w?.Close();
                if (entryId != 0) lib.Delete(new[] { entryId }, out _);
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
