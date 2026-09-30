using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Rendering stress test (port of the Windows StressTestDialog): enter any copy count and the model is drawn that many
    /// times through GPU instancing in the editor viewport (grid), or — "Benchmark Scene" — several of the project's models
    /// spread near → far with varied scale / rotation (instancing + LOD + culling together). Live FPS / draw calls /
    /// instances / vertices update four times a second. Non-modal: the crowd keeps rendering while you fly around.
    /// "Run in Game" launches the standalone player on the same crowd when the player supports it.
    /// </summary>
    public sealed class StressTestWindow : Window
    {
        public static void Open(string modelFullPath)
        {
            if (string.IsNullOrEmpty(modelFullPath) || !File.Exists(modelFullPath)) { EditorCommands.Toast("Model file not found"); return; }
            EditorWindows.Show(new StressTestWindow(modelFullPath));
        }

        private readonly string _modelPath;
        private readonly string _modelName;
        private readonly TextBox _count = new TextBox { Text = "1000", FontSize = 15, MinHeight = 32, Padding = new Thickness(8, 5) };
        private readonly TextBlock _stats = new TextBlock { Classes = { "mono" }, FontSize = 13, LineHeight = 22 };
        private readonly TextBlock _note = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private readonly DispatcherTimer _timer;
        private Button _runGame, _benchGame;

        public StressTestWindow(string modelPath)
        {
            _modelPath = modelPath;
            _modelName = Path.GetFileNameWithoutExtension(modelPath);
            Title = "Stress Test — " + _modelName;
            Width = 470; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            _note.Foreground = Ui.Brush("VxTextTertiaryBrush");

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock { Text = "GPU Instancing Stress Test", FontSize = 17, FontWeight = FontWeight.SemiBold });
            root.Children.Add(new TextBlock { Text = "Model: " + _modelName, Foreground = Ui.Brush("VxAccentBrush"), Margin = new Thickness(0, 2, 0, 16) });
            root.Children.Add(new TextBlock { Text = "Number of copies", FontSize = 12, Foreground = Ui.Brush("VxTextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 4) });
            _count.KeyDown += (s, e) => { if (e.Key == Key.Return) { RunInEditor(); e.Handled = true; } };
            root.Children.Add(_count);

            string player = FindPlayer();
            bool stressSupported = PlayerSupports(player, "--stress=");
            bool benchSupported = PlayerSupports(player, "--benchmark=");

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
            _runGame = Ui.Button("▶ Run in Game", RunInGame, null, "accent", 150);
            ToolTip.SetTip(_runGame, stressSupported ? "Launch the standalone player (uncapped FPS) on this crowd in its own window — fly with WASD"
                                                     : "Needs a Vortex.Player with the stress mode (--stress=<model> --count=N); run the test in the editor meanwhile");
            _runGame.IsEnabled = stressSupported;
            row.Children.Add(_runGame);
            row.Children.Add(Ui.Button("In Editor", RunInEditor, "Spawn the copies in the editor viewport (GPU instancing)", null, 90));
            row.Children.Add(Ui.Button("Stop", () => { StressTestService.Stop(); Resubmit(); UpdateStats(); }, "Remove the crowd", null, 80));
            root.Children.Add(row);

            var bench = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            var benchEditor = Ui.Button("Benchmark Scene (all models)", RunBenchmarkInEditor, "Every model of the project (up to 8) spread from very near to very far — per-model copies = the count above. Instancing + LOD + culling together.");
            benchEditor.Background = Ui.Brush("VxGreenBrush");
            benchEditor.Foreground = Brushes.White;
            bench.Children.Add(benchEditor);
            _benchGame = Ui.Button("in Game", RunBenchmarkInGame, benchSupported ? "Launch the generated benchmark scene in the standalone player" : "Needs a Vortex.Player with the benchmark mode (--benchmark=<assets dir> --count=N)", null, 80);
            _benchGame.IsEnabled = benchSupported;
            bench.Children.Add(_benchGame);
            root.Children.Add(bench);

            root.Children.Add(new Border { Background = Ui.Brush("VxFieldBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 18, 0, 0), Child = _stats });
            _note.Text = stressSupported ? "Close this window any time — the crowd keeps rendering until you press Stop."
                                         : "The crowd renders in the editor viewport (capped at the editor's frame rate). Close this window any time — it keeps rendering until you press Stop.";
            root.Children.Add(_note);
            Content = root;
            UpdateStats();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => UpdateStats();
            _timer.Start();
            Closed += (s, e) => _timer.Stop();
        }

        /// <summary>The copy count typed by the user (thousand separators allowed); null + message when invalid.</summary>
        private int? Count()
        {
            var t = (_count.Text ?? "").Trim().Replace(",", "").Replace(".", "").Replace(" ", "").Replace("'", "");
            if (int.TryParse(t, out int n) && n > 0) return n;
            _ = Dialogs.Alert("Stress Test", "Enter a positive whole number of copies.");
            return null;
        }

        private static void Resubmit()
        {
            SceneRenderService.RuntimeDirty = true;
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }

        public void RunInEditor()
        {
            var n = Count(); if (n == null) return;
            StressTestService.Start(_modelPath, n.Value);
            Resubmit();
            UpdateStats();
        }

        /// <summary>The benchmark in the editor viewport: up to 8 distinct models of the project's Assets folder.</summary>
        public void RunBenchmarkInEditor()
        {
            int perModel = CountOrDefault(1500);
            var models = BenchmarkModels(AssetsDir());
            if (models.Count == 0) { _ = Dialogs.Alert("Benchmark", "No models (.glb / .gltf / .obj / .fbx) found in the project's Assets folder."); return; }
            StressTestService.StartBenchmark(models, perModel);
            try { EditorCameraController.Instance.FocusOn(0f, 20f, 0f, 140f); } catch { }
            Resubmit();
            UpdateStats();
        }

        private int CountOrDefault(int fallback)
        {
            var t = (_count.Text ?? "").Trim().Replace(",", "").Replace(".", "").Replace(" ", "");
            return int.TryParse(t, out int n) && n > 0 ? n : fallback;
        }

        private void RunInGame()
        {
            var n = Count(); if (n == null) return;
            Launch("--stress=" + _modelPath, n.Value);
        }

        private void RunBenchmarkInGame()
        {
            var dir = AssetsDir();
            if (string.IsNullOrEmpty(dir)) { _ = Dialogs.Alert("Benchmark", "Could not locate the project's Assets folder."); return; }
            Launch("--benchmark=" + dir, CountOrDefault(1500));
        }

        private void Launch(string modeArg, int count)
        {
            string exe = FindPlayer();
            if (exe == null) { _ = Dialogs.Alert("Stress Test", "Vortex.Player was not found next to the editor (build Managed/Vortex.Player)."); return; }
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                var proj = ProjectData.Current?.Path;
                if (!string.IsNullOrEmpty(proj)) psi.ArgumentList.Add("--project=" + proj);
                psi.ArgumentList.Add(modeArg);
                psi.ArgumentList.Add("--count=" + count.ToString(CultureInfo.InvariantCulture));
                var native = Environment.GetEnvironmentVariable("VORTEX_NATIVE_DIR");
                if (!string.IsNullOrEmpty(native)) psi.Environment["VORTEX_NATIVE_DIR"] = native;
                Process.Start(psi);
            }
            catch (Exception ex) { _ = Dialogs.Alert("Stress Test", "Could not launch the stress player:\n" + ex.Message); }
        }

        /// <summary>The project's Assets folder: walk up from the model, else the open project's.</summary>
        private string AssetsDir()
        {
            try
            {
                var d = new DirectoryInfo(Path.GetDirectoryName(_modelPath));
                while (d != null) { if (string.Equals(d.Name, "Assets", StringComparison.OrdinalIgnoreCase)) return d.FullName; d = d.Parent; }
            }
            catch { }
            var proj = ProjectData.Current?.Path;
            var a = proj != null ? Path.Combine(proj, "Assets") : null;
            return a != null && Directory.Exists(a) ? a : Path.GetDirectoryName(_modelPath);
        }

        /// <summary>Distinct models (by name) for the benchmark — the same selection as the Windows game host (max 8).</summary>
        public static List<string> BenchmarkModels(string assetsDir)
        {
            var paths = new List<string>();
            if (string.IsNullOrEmpty(assetsDir) || !Directory.Exists(assetsDir)) return paths;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in new[] { "*.glb", "*.gltf", "*.obj", "*.fbx" })
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(assetsDir, ext, SearchOption.AllDirectories))
                    {
                        if (paths.Count >= 8) return paths;
                        if (seen.Add(Path.GetFileNameWithoutExtension(f))) paths.Add(f);
                    }
                }
                catch { }
            }
            return paths;
        }

        private void UpdateStats()
        {
            int fps = 0, draws = 0, verts = 0, inst = 0;
            try { fps = VortexAPI.CurrentFPS; } catch { }
            try { draws = VortexAPI.DrawCalls; } catch { }
            try { verts = VortexAPI.VertexCount; } catch { }
            try { inst = VortexAPI.InstancesDrawn; } catch { }
            var ci = CultureInfo.InvariantCulture;
            string state = StressTestService.Active ? "running — " + StressTestService.Count.ToString("N0", ci) + " copies of " + StressTestService.ModelName : "idle";
            _stats.Text =
                "State        " + state + "\n" +
                "FPS          " + fps + "\n" +
                "Draw calls   " + draws.ToString("N0", ci) + "\n" +
                "Instances    " + inst.ToString("N0", ci) + "\n" +
                "Vertices     " + verts.ToString("N0", ci);
        }

        // ------------------------------------------------------------------ standalone player

        private static string FindPlayer()
        {
            string baseDir = AppContext.BaseDirectory;
            string name = OperatingSystem.IsWindows() ? "Vortex.Player.exe" : "Vortex.Player";
            foreach (var dir in new[] { baseDir, Path.Combine(baseDir, "..", "Resources", "Player"), Path.Combine(baseDir, "player"),
                                         Path.Combine(baseDir, "..", "..", "..", "..", "Vortex.Player", "bin", "Debug", "net10.0"),
                                         Path.Combine(baseDir, "..", "..", "..", "..", "Vortex.Player", "bin", "Release", "net10.0") })
            {
                try { var p = Path.GetFullPath(Path.Combine(dir, name)); if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        /// <summary>Whether the player build understands a command-line mode (its assembly carries the switch literal).</summary>
        private static bool PlayerSupports(string exe, string flag)
        {
            if (exe == null) return false;
            try
            {
                var dll = Path.Combine(Path.GetDirectoryName(exe), "Vortex.Player.dll");
                if (!File.Exists(dll)) return false;
                var bytes = File.ReadAllBytes(dll);
                var needle = Encoding.Unicode.GetBytes(flag);   // C# string literals are UTF-16 in the #US heap
                for (int i = 0; i + needle.Length <= bytes.Length; i++)
                {
                    int k = 0;
                    while (k < needle.Length && bytes[i + k] == needle[k]) k++;
                    if (k == needle.Length) return true;
                }
            }
            catch { }
            return false;
        }

        internal string CountText { get => _count.Text; set => _count.Text = value; }
        internal string StatsText => _stats.Text;
    }
}
