using Avalonia.Controls;

namespace VortexEditor.Shell
{
    /// <summary>Asset Tags (being ported from the Windows editor).</summary>
    public sealed class AssetTagEditorDialog : Window
    {
        public static void Open(string fullPath) => EditorWindows.Show(new PlaceholderWindow("Asset Tags", "Asset Tags — port in progress."));
    }
}
