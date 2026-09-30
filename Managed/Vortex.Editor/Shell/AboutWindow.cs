using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VortexEditor.Controls;

namespace VortexEditor.Shell
{
    public sealed class AboutWindow : Window
    {
        public AboutWindow()
        {
            Title = "About Vortex Engine"; Width = 380; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            string version = ""; try { version = Editor.Core.EngineInfo.VersionString; } catch { }
            string gpu = ""; try { gpu = Editor.DllWrapper.VortexAPI.GpuName(); } catch { }
            var stack = new StackPanel { Spacing = 6, Margin = new Thickness(28, 30, 28, 24), HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new VxIcon { Icon = "Vortex", Width = 64, Height = 64, Foreground = (IBrush)Application.Current.FindResource("VxAccentBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = "Vortex Engine", Classes = { "large" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "Version " + version, Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = (System.OperatingSystem.IsMacOS() ? "macOS · Metal via SDL GPU" : System.OperatingSystem.IsWindows() ? "Windows · DirectX 12" : "Linux · Vulkan via SDL GPU") + (string.IsNullOrEmpty(gpu) ? "" : "\n" + gpu), Classes = { "small", "tertiary" }, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "© shadow-kernel. Native C++20 core, .NET 10 editor and player.", Classes = { "small", "tertiary" }, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HolizontalCenter(), Margin = new Thickness(0, 14, 0, 0) };
            var docs = new Button { Content = "Documentation" }; docs.Click += (s, e) => EditorCommands.Documentation();
            var ok = new Button { Content = "OK", Classes = { "accent" }, MinWidth = 80, IsDefault = true }; ok.Click += (s, e) => Close();
            row.Children.Add(docs); row.Children.Add(ok);
            stack.Children.Add(row);
            Content = stack;
        }
        private static HorizontalAlignment HolizontalCenter() => HorizontalAlignment.Center;
    }
}
