using System;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>
    /// Deleting an asset puts it in the desktop's own trash where the platform has one — NSFileManager on
    /// macOS (<see cref="MacTrash"/>), the freedesktop Trash on Linux (<see cref="FreedesktopTrash"/>) — so
    /// the user can restore it from Finder / their file manager. Where it is not supported the asset browser
    /// falls back to its own undoable delete.
    /// </summary>
    internal static class SystemTrash
    {
        public static bool IsSupported => MacTrash.IsSupported || FreedesktopTrash.IsSupported;

        /// <summary>Trash <paramref name="path"/>; returns the item's new path inside the trash, or null.</summary>
        public static string Trash(string path)
        {
            if (MacTrash.IsSupported) return MacTrash.Trash(path);
            if (FreedesktopTrash.IsSupported) return FreedesktopTrash.Trash(path);
            return null;
        }
    }
}
