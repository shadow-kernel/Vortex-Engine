using Avalonia.Controls;

namespace VortexEditor.Shell
{
    /// <summary>History (being ported from the Windows editor).</summary>
    public sealed class HistoryWindow : Window
    {
        public static void Open() => EditorWindows.Show(new PlaceholderWindow("History", "History — port in progress."));
    }
}
