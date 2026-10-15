using System;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>The Screen API (#462): points, drawable pixels, content scale and render size from what the host feeds, the
    /// render scale a render resolution asks for, and display queries that answer "nothing" without a display.</summary>
    public static class ScreenTests
    {
        [Test]
        public static void ARenderResolutionBecomesTheMatchingRenderScale(TestContext t)
        {
            t.Equal(2f, Vortex.Screen.RenderScaleFor(3840, 2160, 1920, 1080), "4K on a 1080p drawable: 2× (supersampled)");
            t.Equal(1f, Vortex.Screen.RenderScaleFor(3840, 2160, 3840, 2160), "4K on a 4K drawable: native");
            t.Equal(0.5f, Vortex.Screen.RenderScaleFor(1280, 720, 2560, 1440), "720p on a 1440p drawable: half");
            t.True(Math.Abs(Vortex.Screen.RenderScaleFor(2560, 1440, 3024, 1964) - (float)Math.Sqrt(2560.0 * 1440 / (3024.0 * 1964))) < 1e-5f,
                   "another aspect: the pixel count decides");
            t.Equal(0.25f, Vortex.Screen.RenderScaleFor(100, 100, 4000, 4000), "clamped to 0.25");
            t.Equal(2f, Vortex.Screen.RenderScaleFor(8000, 8000, 1000, 1000), "clamped to 2");
            t.Equal(1f, Vortex.Screen.RenderScaleFor(1920, 1080, 0, 0), "an unknown drawable: 1");
            t.Equal(1f, Vortex.Screen.RenderScaleFor(0, 1080, 1920, 1080), "a zero request: 1");
        }

        [Test]
        public static void TheViewIsInPointsAndPixelsOnEveryHost(TestContext t)
        {
            var rt = ScriptRuntime.Instance;
            var oldHost = Vortex.UI.Host;
            float oldScale = Vortex.Settings.RenderScale;
            try
            {
                Vortex.UI.Host = rt;
                // the player on a Retina display: 756 × 472 points at density 2, no exact pixels told yet
                rt.SetScreenPixels(0, 0);
                rt.SetUIFrame(756, 472, 0, 0, false, false, 2f);
                t.Equal(756f, Vortex.Screen.Width, "Width is points (= UI.Width)");
                t.Equal(1512, Vortex.Screen.PixelWidth, "PixelWidth = 2 × Width on a Retina window");
                t.Equal(944, Vortex.Screen.PixelHeight, "PixelHeight too");
                t.Equal(2f, Vortex.Screen.ContentScale, "ContentScale = the pixel density (= UI.Scale)");
                // the host's exact drawable wins (the editor viewport's swapchain may round differently)
                rt.SetScreenPixels(1511, 943);
                t.Equal(1511, Vortex.Screen.PixelWidth, "the host's drawable size");
                // a standard display / the Windows player: points are pixels
                rt.SetScreenPixels(1280, 720);
                rt.SetUIFrame(1280, 720, 0, 0, false, false, 1f);
                t.True(Vortex.Screen.PixelWidth == 1280 && Vortex.Screen.ContentScale == 1f, "scale 1: pixels = points");

                // render resolution: 4K asked of a 1080p drawable supersamples 2× (with an engine library to hold the scale)
                rt.SetScreenPixels(1920, 1080);
                float s = Vortex.Settings.SetRenderResolution(3840, 2160);
                t.Equal(2f, s, "SetRenderResolution(3840, 2160) on 1920 × 1080 picks render scale 2");
                if (Math.Abs(Vortex.Settings.RenderScale - 2f) < 1e-4f)
                    t.True(Vortex.Screen.RenderWidth == 3840 && Vortex.Screen.RenderHeight == 2160, "the 3D renders at 3840 × 2160 (" + Vortex.Screen.RenderWidth + " × " + Vortex.Screen.RenderHeight + ")");
                else Console.WriteLine("        (render scale not held: no engine library — the render size part is skipped)");
            }
            finally
            {
                Vortex.Settings.SetRenderScale(oldScale > 0f ? oldScale : 1f);
                Vortex.UI.Host = oldHost;
                rt.SetScreenPixels(0, 0);
                rt.SetUIFrame(0, 0, 0, 0, false, false, 1f);
            }
        }

        [Test]
        public static void DisplayQueriesAnswerNothingWithoutADisplay(TestContext t)
        {
            // no window and no video subsystem in the test process: empty, not a crash
            var modes = Vortex.Screen.DisplayModes();
            t.NotNull(modes, "an array");
            var cur = Vortex.Screen.CurrentDisplayMode;
            t.True(modes.Length == 0 || (modes[0].Width > 0 && modes[0].Height > 0), "either nothing or real modes (" + modes.Length + ")");
            t.True(cur.Width >= 0 && cur.Height >= 0, "the current mode reads as a size or 0 × 0");
            t.Equal("2560 × 1440 @ 144 Hz", new Vortex.DisplayMode(2560, 1440, 144f).ToString(), "a mode prints as the menu shows it");
            t.Equal("800 × 600", new Vortex.DisplayMode(800, 600, 0f).ToString(), "without a rate");
            t.False(Vortex.Screen.FullScreen, "no game window: not fullscreen");
        }
    }
}
