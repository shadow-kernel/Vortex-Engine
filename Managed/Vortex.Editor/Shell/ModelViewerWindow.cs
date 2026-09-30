using Avalonia.Controls;

namespace VortexEditor.Shell
{
    /// <summary>Asset Viewer (being ported from the Windows editor).</summary>
    public sealed class ModelViewerWindow : Window
    {
        public static void Open(string fullPath) => EditorWindows.Show(new PlaceholderWindow("Asset Viewer", "Asset Viewer — port in progress."));
    }
}
