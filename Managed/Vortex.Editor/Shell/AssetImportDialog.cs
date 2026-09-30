using System.Threading.Tasks;
using Avalonia.Controls;

namespace VortexEditor.Shell
{
    /// <summary>Asset import (being ported from the Windows editor): copies files into the project and imports them.</summary>
    public sealed class AssetImportDialog : Window
    {
        /// <summary>Import <paramref name="files"/> into <paramref name="targetFolder"/> (absolute); returns the imported paths.</summary>
        public static Task<string[]> Run(string[] files, string targetFolder) => Task.FromResult(new string[0]);
    }
}
