using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using SkiaSharp;

namespace VortexEditor.Claude
{
    /// <summary>
    /// The editor viewport as an image for Claude (#90): the native renderer writes the next presented frame to a
    /// 32-bit BMP (<see cref="VortexAPI.CaptureFrame"/>, serviced inside render_frame on the UI thread), which becomes
    /// an opaque JPEG/PNG no larger than the requested size — what the model needs to see, small enough to send.
    /// </summary>
    public static class ViewportCapture
    {
        public sealed class Shot
        {
            public byte[] Data;
            public string MimeType;
            public int Width, Height, SourceWidth, SourceHeight;
        }

        /// <param name="clean">Hide the editor overlays (selection outline, gizmo, icons) in the captured frame.</param>
        public static async Task<Shot> CaptureAsync(int maxSize = 1280, bool png = false, bool clean = true, int timeoutMs = 4000)
        {
            if (EditorViewportSession.Main == null || !EditorViewportSession.Main.IsInitialized)
                throw new ToolError("The editor viewport is not running (no project open, or the editor is still starting).");
            string bmp = Path.Combine(Path.GetTempPath(), "vortex-capture-" + Guid.NewGuid().ToString("N") + ".bmp");
            bool gizmos = VortexAPI.AreGizmosVisible;
            try
            {
                if (clean)
                {
                    SceneRenderService.HideEditorOverlays = true;
                    if (gizmos) VortexAPI.ShowGizmos(false);
                    // one frame without the overlays before the captured one
                    EditorViewportSession.RequestResubmit();
                    await Task.Delay(60);
                }
                VortexAPI.CaptureFrame(bmp);
                EditorViewportSession.RequestResubmit();
                var start = DateTime.UtcNow;
                while (!(File.Exists(bmp) && new FileInfo(bmp).Length > 54))
                {
                    if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
                    {
                        string why = EditorViewportSession.ActivePreviewDialogs > 0 ? " — a preview dialog is open and pauses the main viewport; close it"
                                   : " — is the editor window minimised or hidden?";
                        throw new ToolError("The viewport did not render a frame within " + timeoutMs / 1000 + " s" + why);
                    }
                    await Task.Delay(30);
                }
                await Task.Delay(20);
                return Encode(File.ReadAllBytes(bmp), maxSize, png);
            }
            finally
            {
                try { if (File.Exists(bmp)) File.Delete(bmp); } catch { }
                if (clean)
                {
                    SceneRenderService.HideEditorOverlays = false;
                    if (gizmos) VortexAPI.ShowGizmos(true);
                    EditorViewportSession.RequestResubmit();
                }
            }
        }

        /// <summary>BGRA BMP (top-down or bottom-up, 32 bpp, as the engine writes it) → resized opaque JPEG/PNG.</summary>
        internal static Shot Encode(byte[] file, int maxSize, bool png)
        {
            if (file.Length < 54 || file[0] != 'B' || file[1] != 'M') throw new ToolError("The capture is not a BMP.");
            int offset = BitConverter.ToInt32(file, 10);
            int w = BitConverter.ToInt32(file, 18), h = BitConverter.ToInt32(file, 22);
            int bpp = BitConverter.ToInt16(file, 28);
            bool topDown = h < 0; h = Math.Abs(h);
            if (bpp != 32 || w <= 0 || h <= 0 || offset + (long)w * h * 4 > file.Length) throw new ToolError("Unsupported capture format (" + bpp + " bpp, " + w + "×" + h + ").");
            using var src = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int srcRow = topDown ? y : h - 1 - y;
                Buffer.BlockCopy(file, offset + srcRow * w * 4, px, y * w * 4, w * 4);
            }
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;   // the swapchain's alpha is meaningless
            Marshal.Copy(px, 0, src.GetPixels(), px.Length);

            maxSize = Math.Clamp(maxSize, 256, 2048);
            double scale = Math.Min(1.0, (double)maxSize / Math.Max(w, h));
            int tw = Math.Max(1, (int)Math.Round(w * scale)), th = Math.Max(1, (int)Math.Round(h * scale));
            SKBitmap scaled = scale < 1.0 ? src.Resize(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Opaque), SKFilterQuality.High) : null;
            try
            {
                using var image = SKImage.FromBitmap(scaled ?? src);
                using var data = image.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, png ? 100 : 85);
                return new Shot { Data = data.ToArray(), MimeType = png ? "image/png" : "image/jpeg", Width = tw, Height = th, SourceWidth = w, SourceHeight = h };
            }
            finally { scaled?.Dispose(); }
        }
    }
}
