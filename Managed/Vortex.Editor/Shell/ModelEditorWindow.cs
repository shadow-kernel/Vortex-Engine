using Avalonia.Controls;

namespace VortexEditor.Shell
{
    /// <summary>Model Editor (being ported from the Windows editor).</summary>
    public sealed class ModelEditorWindow : Window
    {
        public static void Open(string fullPath) => EditorWindows.Show(new PlaceholderWindow("Model Editor", "Model Editor — port in progress."));
    }
}
