using System;
using Avalonia;

namespace VortexEditor
{
    internal static class Program
    {
        /// <summary>Command line for this run (project to open, smoke-test hooks).</summary>
        public static LaunchOptions Options { get; private set; } = new LaunchOptions();

        // Avalonia + the native renderer both live on the main thread.
        [STAThread]
        public static int Main(string[] args)
        {
            Options = LaunchOptions.Parse(args);
            if (Options.SmokeSeconds > 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VORTEX_APPDATA_DIR")))
                Environment.SetEnvironmentVariable("VORTEX_APPDATA_DIR", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex-smoke-appdata"));
            // An exception in a pointer/key/window handler must not take the editor (and unsaved work) down.
            Shell.CrashGuard.Install();
            return Shell.CrashGuard.Run(BuildAvaloniaApp(), args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }

    public sealed class LaunchOptions
    {
        public string ProjectPath;
        public string SceneName;
        /// <summary>Smoke test: capture the viewport + window after this many seconds, then exit.</summary>
        public double SmokeSeconds;
        public string CaptureDir;

        public static LaunchOptions Parse(string[] args)
        {
            var o = new LaunchOptions();
            foreach (string raw in args ?? Array.Empty<string>())
            {
                string a = raw.Trim();
                if (a.StartsWith("--project=", StringComparison.OrdinalIgnoreCase)) o.ProjectPath = a.Substring(10).Trim('"');
                else if (a.StartsWith("--scene=", StringComparison.OrdinalIgnoreCase)) o.SceneName = a.Substring(8).Trim('"');
                else if (a.StartsWith("--smoke=", StringComparison.OrdinalIgnoreCase) && double.TryParse(a.Substring(8), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s)) o.SmokeSeconds = s;
                else if (a.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase)) o.CaptureDir = a.Substring(10).Trim('"');
                else if (!a.StartsWith("-") && a.Length > 0)
                {
                    // A project handed over by the OS (double-clicked project.vortex, "Open with", `open -a`).
                    string p = a.Trim('"');
                    if (System.IO.File.Exists(p) && p.EndsWith(".vortex", StringComparison.OrdinalIgnoreCase)) o.ProjectPath = System.IO.Path.GetDirectoryName(p);
                    else if (System.IO.Directory.Exists(p)) o.ProjectPath = p;
                }
            }
            return o;
        }
    }
}
