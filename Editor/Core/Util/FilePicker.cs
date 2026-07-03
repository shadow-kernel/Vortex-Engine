using System.Threading;

namespace Editor.Core.Util
{
    /// <summary>
    /// File open/save dialogs that are SAFE to call while the editor's DX12/DXGI renderer is live.
    ///
    /// Calling a WPF <c>Microsoft.Win32.OpenFileDialog</c>/<c>SaveFileDialog</c> on the UI thread DEADLOCKS: the shell
    /// common-item-dialog's COM STA initialisation waits on the COM apartment the live DX12/DXGI renderer holds on the
    /// SAME UI thread — the dialog never appears, the editor hangs white ("not responding" → WerFault), which reads as
    /// a full-engine crash. (This is exactly what crashed the Import button and the Sound Container "Add" button.)
    ///
    /// The fix: run the picker on a DEDICATED background STA thread — its own fresh COM apartment, no contention with
    /// the renderer's apartment — using WinForms' dialog (WPF's <c>Microsoft.Win32</c> dialog does NOT work off the UI
    /// thread). The UI thread blocks on <see cref="Thread.Join"/> for the modal's duration, so nothing on it re-enters
    /// native rendering meanwhile. Every editor file dialog opened while a project/renderer is loaded must route here.
    /// </summary>
    public static class FilePicker
    {
        /// <summary>Pick one or more files. Returns the chosen paths, or null if cancelled.</summary>
        public static string[] OpenFiles(string filter, string title, string initialDir, bool multiselect = true)
        {
            string[] files = null;
            var t = new Thread(() =>
            {
                try
                {
                    using (var dlg = new System.Windows.Forms.OpenFileDialog())
                    {
                        dlg.Multiselect = multiselect;
                        if (!string.IsNullOrEmpty(filter)) dlg.Filter = filter;
                        if (!string.IsNullOrEmpty(title)) dlg.Title = title;
                        if (!string.IsNullOrEmpty(initialDir) && System.IO.Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir;
                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) files = dlg.FileNames;
                    }
                }
                catch { }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            return files;
        }

        /// <summary>Pick a single file. Returns the chosen path, or null if cancelled.</summary>
        public static string OpenFile(string filter, string title, string initialDir)
        {
            var r = OpenFiles(filter, title, initialDir, false);
            return (r != null && r.Length > 0) ? r[0] : null;
        }

        /// <summary>Pick a folder (proper folder browser). Returns the chosen folder path, or null if cancelled.</summary>
        public static string PickFolder(string description, string initialDir)
        {
            string result = null;
            var t = new Thread(() =>
            {
                try
                {
                    using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                    {
                        if (!string.IsNullOrEmpty(description)) dlg.Description = description;
                        if (!string.IsNullOrEmpty(initialDir) && System.IO.Directory.Exists(initialDir)) dlg.SelectedPath = initialDir;
                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) result = dlg.SelectedPath;
                    }
                }
                catch { }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            return result;
        }

        /// <summary>Choose a save path. Returns the chosen path, or null if cancelled.</summary>
        public static string SaveFile(string filter, string title, string defaultFileName, string defaultExt, string initialDir)
        {
            string result = null;
            var t = new Thread(() =>
            {
                try
                {
                    using (var dlg = new System.Windows.Forms.SaveFileDialog())
                    {
                        if (!string.IsNullOrEmpty(filter)) dlg.Filter = filter;
                        if (!string.IsNullOrEmpty(title)) dlg.Title = title;
                        if (!string.IsNullOrEmpty(defaultFileName)) dlg.FileName = defaultFileName;
                        if (!string.IsNullOrEmpty(defaultExt)) dlg.DefaultExt = defaultExt;
                        if (!string.IsNullOrEmpty(initialDir) && System.IO.Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir;
                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) result = dlg.FileName;
                    }
                }
                catch { }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            return result;
        }
    }
}
