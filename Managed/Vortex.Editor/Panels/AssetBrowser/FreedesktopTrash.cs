using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>
    /// Move a file or folder to the desktop trash on Linux, following the freedesktop.org Trash specification:
    /// the item goes to <c>$XDG_DATA_HOME/Trash/files</c> and a sibling <c>.trashinfo</c> in <c>Trash/info</c>
    /// records where it came from, so every file manager shows it and can restore it.
    ///
    /// The new path inside the trash is returned so the delete stays undoable (Undo moves the item back)
    /// without holding the file contents in memory — the same contract as <see cref="MacTrash"/>.
    /// </summary>
    internal static class FreedesktopTrash
    {
        public static bool IsSupported => OperatingSystem.IsLinux();

        private static string TrashRoot
        {
            get
            {
                string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrEmpty(data))
                    data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                return Path.Combine(data, "Trash");
            }
        }

        /// <summary>Trash <paramref name="path"/>; returns the item's new path inside the trash, or null on failure.</summary>
        public static string Trash(string path)
        {
            if (!IsSupported || string.IsNullOrEmpty(path)) return null;
            try
            {
                string full = Path.GetFullPath(path);
                bool isDir = Directory.Exists(full);
                if (!isDir && !File.Exists(full)) return null;

                string root = TrashRoot;
                string filesDir = Path.Combine(root, "files");
                string infoDir = Path.Combine(root, "info");
                Directory.CreateDirectory(filesDir);
                Directory.CreateDirectory(infoDir);

                // The spec requires the names in files/ and info/ to agree, and the info file to be created
                // before the move so two deletes cannot claim the same name.
                string baseName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
                string name = baseName;
                string stem = Path.GetFileNameWithoutExtension(baseName);
                string ext = Path.GetExtension(baseName);
                for (int i = 1; File.Exists(Path.Combine(infoDir, name + ".trashinfo"))
                                || File.Exists(Path.Combine(filesDir, name))
                                || Directory.Exists(Path.Combine(filesDir, name)); ++i)
                    name = stem + "_" + i.ToString(CultureInfo.InvariantCulture) + ext;

                string info = "[Trash Info]\n"
                    + "Path=" + UrlEncodePath(full) + "\n"
                    + "DeletionDate=" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "\n";
                string infoPath = Path.Combine(infoDir, name + ".trashinfo");
                File.WriteAllText(infoPath, info);

                string target = Path.Combine(filesDir, name);
                try
                {
                    if (isDir) Directory.Move(full, target); else File.Move(full, target);
                }
                catch
                {
                    try { File.Delete(infoPath); } catch { }
                    return null;   // e.g. a different filesystem: the caller falls back to its own delete
                }
                return target;
            }
            catch { return null; }
        }

        /// <summary>Percent-encodes a path for the trashinfo Path field (RFC 2396), keeping the separators.</summary>
        private static string UrlEncodePath(string path)
        {
            var sb = new StringBuilder(path.Length + 16);
            foreach (byte b in Encoding.UTF8.GetBytes(path))
            {
                char c = (char)b;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '/' || c == '-' || c == '_' || c == '.' || c == '~')
                    sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
