using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;

namespace VortexEditor.Shell
{
    /// <summary>The Screen API in editor play (#462): the play view's points, drawable pixels (Width × ContentScale — twice the
    /// points on a Retina display) and render size; the display's modes and current mode; Settings.SetRenderResolution picking
    /// render scale 2 for twice the drawable (the view still renders) and 0.5 for half; Stop gives the edit view its render
    /// scale back.</summary>
    internal static class ScriptingScreenSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("scripting screen", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            if (ProjectData.Current?.ActiveScene == null) { log.Log("scripting screen: no scene — skipped"); return true; }
            float scaleBefore = VortexAPI.GetRenderScale();
            bool playing = false, ok = true;
            try
            {
                EditorCommands.Play();
                playing = PlayModeService.Instance.State == PlayState.Playing;
                if (!playing) { log.LogError("scripting screen: play did not start"); return false; }
                await SmokeRegistry.Settle(600);

                float w = Vortex.Screen.Width, h = Vortex.Screen.Height, cs = Vortex.Screen.ContentScale;
                int pw = Vortex.Screen.PixelWidth, ph = Vortex.Screen.PixelHeight;
                log.Log("scripting screen: view " + w + " × " + h + " points, " + pw + " × " + ph + " pixels, content scale " + cs
                    + ", render " + Vortex.Screen.RenderWidth + " × " + Vortex.Screen.RenderHeight);
                if (w < 10f || h < 10f || pw < 10 || ph < 10) { log.LogError("scripting screen: the play view's size is unknown to scripts"); ok = false; }
                if (Math.Abs(pw - w * cs) > 2f || Math.Abs(ph - h * cs) > 2f) { log.LogError("scripting screen: PixelWidth / PixelHeight are not the points × the content scale"); ok = false; }

                var modes = Vortex.Screen.DisplayModes();
                var cur = Vortex.Screen.CurrentDisplayMode;
                log.Log("scripting screen: " + modes.Length + " display modes (" + string.Join(", ", modes.Take(4).Select(m => m.ToString())) + (modes.Length > 4 ? ", …" : "")
                    + "), current " + cur + " at " + cur.Scale + "×");
                if (modes.Length == 0) { log.LogError("scripting screen: Screen.DisplayModes() lists nothing for the main display"); ok = false; }
                if (cur.Width <= 0 || cur.Height <= 0) { log.LogError("scripting screen: Screen.CurrentDisplayMode is unknown"); ok = false; }

                float up = Vortex.Settings.SetRenderResolution(pw * 2, ph * 2);
                var frame = await CameraSkySmoke.Sample("screen_supersampled.bmp", 0.5, 0.5);
                log.Log("scripting screen: SetRenderResolution(" + (pw * 2) + ", " + (ph * 2) + ") -> scale " + up + ", render "
                    + Vortex.Screen.RenderWidth + " × " + Vortex.Screen.RenderHeight + ", centre " + CameraSkySmoke.Rgb(frame));
                if (Math.Abs(up - 2f) > 1e-3f || Math.Abs(Vortex.Settings.RenderScale - 2f) > 1e-3f || Math.Abs(Vortex.Screen.RenderWidth - pw * 2) > 1)
                { log.LogError("scripting screen: twice the drawable did not supersample 2×"); ok = false; }
                if (frame.r < 0) { log.LogError("scripting screen: no frame at render scale 2"); ok = false; }
                float down = Vortex.Settings.SetRenderResolution(pw / 2, ph / 2);
                if (Math.Abs(down - 0.5f) > 0.01f) { log.LogError("scripting screen: half the drawable did not give render scale 0.5 (" + down + ")"); ok = false; }
            }
            finally
            {
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(300); }
            }
            float after = VortexAPI.GetRenderScale();
            if (Math.Abs(after - scaleBefore) > 1e-4f) { log.LogError("scripting screen: Stop left the render scale at " + after + " (was " + scaleBefore + ")"); ok = false; }
            return ok;
        }
    }
}
