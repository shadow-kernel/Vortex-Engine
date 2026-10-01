using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Editor.Core.Data;
using Editor.Core.Services.Rendering;
using VortexEditor.Controls;
using VortexEditor.Services;

namespace VortexEditor.Shell
{
    /// <summary>Smoke checks for the shared preview/thumbnail foundation every editor window builds on.</summary>
    internal static class FoundationSmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("preview renderer: primitive renders pixels", () =>
            {
                var img = PreviewRenderer.RenderPrimitive("Primitive:Sphere", 96);
                return img != null && HasContent(img);
            });
            SmokeRegistry.Add("thumbnail service: model + image", async () =>
            {
                string root = ProjectData.Current?.Path;
                if (string.IsNullOrEmpty(root)) return false;
                var files = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.*", SearchOption.AllDirectories).ToList();
                string model = files.FirstOrDefault(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase));
                string image = files.FirstOrDefault(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
                Bitmap m = null, i = null;
                if (model != null) ThumbnailService.Request(model, 128, b => m = b);
                if (image != null) ThumbnailService.Request(image, 128, b => i = b);
                for (int t = 0; t < 100 && ((model != null && m == null) || (image != null && i == null)); t++) await Task.Delay(100);
                return (model == null || m != null) && (image == null || i != null);
            });
            SmokeRegistry.Add("preview viewport window: orbit render + capture", async () =>
            {
                string root = ProjectData.Current?.Path;
                string model = root == null ? null : Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.glb", SearchOption.AllDirectories).FirstOrDefault();
                var pv = new PreviewViewport();
                bool loaded = model != null ? pv.LoadAsset(model, root) : (pv.Model = PreviewModel.FromOwned(new[] { PreviewModel.CreatePrimitive("Cube") }, null)) != null;
                var w = new Window { Title = "Preview smoke", Width = 520, Height = 420, Content = pv };
                EditorWindows.Show(w);
                await SmokeRegistry.Settle(900);
                pv.Camera = new PreviewCamera { Yaw = 1.2f, Pitch = 0.35f, DistScale = 1.1f, FovDeg = 35f };
                await SmokeRegistry.Settle(400);
                bool ok = loaded && pv.LastImage != null && HasContent(pv.LastImage);
                SmokeRegistry.Capture(w, "foundation_preview.png");
                w.Close();
                return ok;
            });
        }

        private static bool HasContent(PreviewImage img)
        {
            // more than one distinct colour in a sparse sample = something was drawn over the clear colour
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int y = 0; y < img.Height; y += Math.Max(1, img.Height / 16))
                for (int x = 0; x < img.Width; x += Math.Max(1, img.Width / 16))
                {
                    int o = y * img.Stride + x * 4;
                    seen.Add((img.Bgra[o] >> 3) | ((img.Bgra[o + 1] >> 3) << 5) | ((img.Bgra[o + 2] >> 3) << 10));
                }
            return seen.Count > 3;
        }
    }
}
