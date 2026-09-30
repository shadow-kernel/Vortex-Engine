using Avalonia.Controls;

namespace VortexEditor.Shell
{
    /// <summary>Stress Test (being ported from the Windows editor).</summary>
    public sealed class StressTestWindow : Window
    {
        public static void Open(string modelFullPath) => EditorWindows.Show(new PlaceholderWindow("Stress Test", "Stress Test — port in progress."));
    }
}
