using System;
using System.IO;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>
    /// The folder the Asset Browser and the file tree show — one source of truth so selecting a folder in either
    /// navigates the other (the WPF editor syncs both through FileExplorerService). Every navigation is mirrored into
    /// the shared <see cref="FileExplorerService"/>, and navigation requested through that service by other code
    /// (e.g. "locate asset") is picked up here.
    /// </summary>
    public static class AssetNavigation
    {
        private static bool _mirroring;

        /// <summary>Absolute path of the folder currently shown (null when no project is open).</summary>
        public static string CurrentFolder { get; private set; }

        /// <summary>Raised on the UI thread after <see cref="CurrentFolder"/> changed.</summary>
        public static event Action<string> Changed;

        static AssetNavigation()
        {
            FileExplorerService.Instance.CurrentFolderChanged += (s, folder) =>
            {
                if (_mirroring || folder == null) return;
                string path = folder.FullPath;
                Dispatcher.UIThread.Post(() => NavigateTo(path, mirror: false));
            };
        }

        /// <summary>Show <paramref name="folder"/> in the browser + tree.</summary>
        public static void NavigateTo(string folder, bool mirror = true)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            folder = AssetTile.Normalize(folder);
            if (string.Equals(folder, CurrentFolder, StringComparison.Ordinal)) return;
            CurrentFolder = folder;
            if (mirror) Mirror(folder);
            Changed?.Invoke(folder);
        }

        /// <summary>Forget the current folder (project closed / switched).</summary>
        public static void Reset() => CurrentFolder = null;

        /// <summary>Initialise the shared FileExplorerService for the open project if it still points elsewhere.</summary>
        public static void EnsureExplorer()
        {
            string root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            var svc = FileExplorerService.Instance;
            if (svc.RootItem != null && AssetFileOps.PathsEqual(svc.RootItem.FullPath, root)) return;
            bool prev = _mirroring;
            _mirroring = true;
            try { svc.Initialize(root); } catch { } finally { _mirroring = prev; }
        }

        private static void Mirror(string folder)
        {
            bool prev = _mirroring;
            _mirroring = true;
            try { EnsureExplorer(); FileExplorerService.Instance.NavigateToPath(folder); }
            catch { }
            finally { _mirroring = prev; }
        }
    }
}
