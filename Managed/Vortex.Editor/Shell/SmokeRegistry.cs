using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Editor.Core.Services;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Self-registering editor smoke checks: every window / panel adds its own checks from its OWN file
    /// (<c>[ModuleInitializer] static void Register() =&gt; SmokeRegistry.Add("material editor", async () =&gt; …)</c>),
    /// and the full smoke run (<c>VORTEX_SMOKE_FULL=1 … --smoke=N</c>) executes them after the built-in checks,
    /// logging <c>SMOKE OK / SMOKE FAIL &lt;name&gt;</c>. <see cref="Capture"/> saves a window screenshot into the
    /// smoke capture folder so a run can be verified visually.
    /// </summary>
    public static class SmokeRegistry
    {
        private sealed class Entry { public string Name; public Func<Task<bool>> Run; }
        private static readonly List<Entry> _checks = new List<Entry>();

        /// <summary>True while the registered checks are running (the smoke exit waits for it).</summary>
        public static bool Running { get; private set; }
        /// <summary>Folder for screenshots (the editor's --capture directory).</summary>
        public static string CaptureDir { get; set; }

        public static void Add(string name, Func<Task<bool>> run) { lock (_checks) _checks.Add(new Entry { Name = name, Run = run }); }
        public static void Add(string name, Func<bool> run) => Add(name, () => Task.FromResult(run()));

        public static async Task RunAll()
        {
            var log = ConsoleService.Instance;
            Entry[] list; lock (_checks) list = _checks.ToArray();
            Running = true;
            try
            {
                foreach (var c in list)
                {
                    bool ok = false; string err = null;
                    try { ok = await c.Run(); } catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
                    if (ok) log.Log("SMOKE OK   " + c.Name); else log.LogError("SMOKE FAIL " + c.Name + (err != null ? ": " + err : ""));
                }
            }
            finally { Running = false; }
        }

        /// <summary>Save a PNG of a window / control into <see cref="CaptureDir"/> (no-op without a folder).</summary>
        public static bool Capture(Visual visual, string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(CaptureDir) || visual == null) return false;
                double scale = TopLevel.GetTopLevel(visual)?.RenderScaling ?? 1.0;
                var b = visual.Bounds;
                if (b.Width < 2 || b.Height < 2) return false;
                var rtb = new RenderTargetBitmap(new PixelSize((int)(b.Width * scale), (int)(b.Height * scale)), new Vector(96 * scale, 96 * scale));
                rtb.Render(visual);
                Directory.CreateDirectory(CaptureDir);
                rtb.Save(Path.Combine(CaptureDir, fileName));
                return true;
            }
            catch { return false; }
        }

        /// <summary>Let the UI settle (layout + a few render ticks) inside an async check.</summary>
        public static Task Settle(int ms = 600) => Task.Delay(ms);
    }
}
