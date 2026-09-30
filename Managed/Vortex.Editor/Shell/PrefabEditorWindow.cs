using Avalonia.Controls;

namespace VortexEditor.Shell
{
    /// <summary>Prefab Editor (being ported from the Windows editor).</summary>
    public sealed class PrefabEditorWindow : Window
    {
        public static void Open(string fullPath) => EditorWindows.Show(new PlaceholderWindow("Prefab Editor", "Prefab Editor — port in progress."));
    }
}
