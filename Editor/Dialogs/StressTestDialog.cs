using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Editor.Core.Services;
using Editor.DllWrapper;

namespace Editor.Dialogs
{
    /// <summary>
    /// Rendering stress-test panel: enter ANY copy count, hit Run, and the chosen model is spawned that many times
    /// in the viewport via GPU instancing. Live stats (FPS / Draw Calls / Vertices / Instances) update here so the
    /// scaling is obvious. Non-modal so the viewport keeps rendering the crowd while this stays open.
    /// </summary>
    public class StressTestDialog : Window
    {
        private readonly string _modelPath;
        private readonly string _modelName;
        private TextBox _countBox;
        private TextBlock _stats;
        private DispatcherTimer _timer;

        public StressTestDialog(string modelPath, string modelName)
        {
            _modelPath = modelPath;
            _modelName = modelName;

            Title = "Stress Test - " + modelName;
            Width = 440; Height = 340;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 26));
            ResizeMode = ResizeMode.NoResize;

            BuildUI();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => UpdateStats();
            _timer.Start();
            Closed += (s, e) => { try { _timer.Stop(); } catch { } };
        }

        private void BuildUI()
        {
            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = "GPU Instancing Stress Test",
                FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White
            });
            root.Children.Add(new TextBlock
            {
                Text = "Model: " + _modelName,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 135, 255)),
                Margin = new Thickness(0, 2, 0, 16)
            });

            root.Children.Add(new TextBlock
            {
                Text = "Number of copies",
                Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 176)),
                FontSize = 12, Margin = new Thickness(0, 0, 0, 4)
            });
            _countBox = new TextBox
            {
                Text = "1000",
                Background = new SolidColorBrush(Color.FromRgb(38, 38, 42)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 78)),
                CaretBrush = Brushes.White,
                Padding = new Thickness(8, 7, 8, 7),
                FontSize = 15
            };
            _countBox.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) Run(); };
            root.Children.Add(_countBox);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var runGame = MakeButton("▶ Run in Game", Color.FromRgb(108, 92, 231), 150);
            runGame.ToolTip = "Launch the real GameHost (uncapped FPS) in its own window — fly around with WASD";
            runGame.Click += (s, e) => RunInGame();
            btnRow.Children.Add(runGame);
            var run = MakeButton("In Editor", Color.FromRgb(70, 70, 78), 90);
            run.Margin = new Thickness(8, 0, 0, 0);
            run.Click += (s, e) => Run();
            btnRow.Children.Add(run);
            var stop = MakeButton("Stop", Color.FromRgb(70, 70, 78), 80);
            stop.Margin = new Thickness(8, 0, 0, 0);
            stop.Click += (s, e) => { StressTestService.Stop(); UpdateStats(); };
            btnRow.Children.Add(stop);
            root.Children.Add(btnRow);

            var statsBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 36)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 18, 0, 0)
            };
            _stats = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 226)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                LineHeight = 22
            };
            statsBorder.Child = _stats;
            root.Children.Add(statsBorder);

            Content = root;
            UpdateStats();
        }

        private Button MakeButton(string text, Color bg, double w)
        {
            return new Button
            {
                Content = text, Width = w, Height = 34,
                Background = new SolidColorBrush(bg), Foreground = Brushes.White,
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                FontWeight = FontWeights.SemiBold
            };
        }

        private void Run()
        {
            if (!int.TryParse(_countBox.Text.Trim().Replace(",", "").Replace(".", ""), out int count) || count <= 0)
            {
                MessageBox.Show("Enter a positive whole number of copies.", "Stress Test", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StressTestService.Start(_modelPath, count);
            try { Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit(); } catch { }
            UpdateStats();
        }

        /// <summary>Launch the real GameHost (uncapped FPS) in a SEPARATE process rendering the crowd — the export
        /// render path, with a free-fly camera and on-screen stats, instead of the 60fps editor viewport.</summary>
        private void RunInGame()
        {
            if (!int.TryParse(_countBox.Text.Trim().Replace(",", "").Replace(".", ""), out int count) || count <= 0)
            {
                MessageBox.Show("Enter a positive whole number of copies.", "Stress Test", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                var psi = new System.Diagnostics.ProcessStartInfo(exe, "--stress=\"" + _modelPath + "\" --count=" + count) { UseShellExecute = false };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not launch the stress player:\n" + ex.Message, "Stress Test", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateStats()
        {
            int fps = 0, draws = 0, verts = 0, inst = 0;
            try { fps = VortexAPI.CurrentFPS; } catch { }
            try { draws = VortexAPI.DrawCalls; } catch { }
            try { verts = VortexAPI.VertexCount; } catch { }
            try { inst = VortexAPI.InstancesDrawn; } catch { }

            string state = StressTestService.Active ? $"running — {StressTestService.Count:N0} copies of {StressTestService.ModelName}" : "idle";
            _stats.Text =
                $"State        {state}\n" +
                $"FPS          {fps}\n" +
                $"Draw calls   {draws:N0}\n" +
                $"Instances    {inst:N0}\n" +
                $"Vertices     {verts:N0}";
        }
    }
}
