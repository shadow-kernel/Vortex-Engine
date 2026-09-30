using Avalonia.Controls;

namespace VortexEditor.Shell
{
    /// <summary>Texture Editor (being ported from the Windows editor).</summary>
    public sealed class TextureEditorWindow : Window
    {
        public static void Open(string fullPath) => EditorWindows.Show(new PlaceholderWindow("Texture Editor", "Texture Editor — port in progress."));
    }
}
