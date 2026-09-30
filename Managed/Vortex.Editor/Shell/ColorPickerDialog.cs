using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace VortexEditor.Shell
{
    /// <summary>Color picker (being ported from the Windows editor).</summary>
    public sealed class ColorPickerDialog : Window
    {
        public static Task<Color?> Pick(Color initial, string title) => Task.FromResult<Color?>(null);
    }
}
