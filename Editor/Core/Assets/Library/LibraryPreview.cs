using System;
using System.IO;

namespace Editor.Core.Assets.Library
{
    /// <summary>
    /// Looking at a library asset without adding it to a project: the main file and its companions (a glTF's .bin and
    /// textures, a material's maps) are copied — copy-on-write clones where the file system supports them (APFS,
    /// ReFS) — into <c>&lt;library&gt;/tmp/preview/&lt;hash&gt;/</c> at their relative paths, so viewers that resolve
    /// relative references work. The copies are disposable (the temp folder is cleaned on start); blobs are never handed
    /// out directly, so a viewer can't change the content-addressed store. Framework-free (both editors).
    /// </summary>
    public static class LibraryPreview
    {
        /// <summary>Lay out <paramref name="e"/> with its companions and return the main file's path.</summary>
        public static string Materialize(GlobalAssetDatabase lib, LibraryEntry e)
        {
            if (lib == null) throw new ArgumentNullException(nameof(lib));
            if (e == null) throw new ArgumentNullException(nameof(e));
            if (!lib.HasBlob(e.Hash)) throw new FileNotFoundException("The library has lost the bytes of " + e.FileName + " — import the original again to restore it.");
            // companions may sit above the main file ("../textures/wood.png"): nest the main file deep enough
            int up = 0;
            foreach (var c in e.Companions) up = Math.Max(up, LeadingUps(c.RelPath));
            char sep = Path.DirectorySeparatorChar;
            string root = Path.GetFullPath(Path.Combine(lib.TempDir, "preview", e.Hash.Substring(0, 16))).TrimEnd(sep) + sep;
            string dir = root;
            for (int i = 0; i < up; i++) dir = Path.Combine(dir, "_");
            string name = Path.GetFileName(e.FileName ?? "");
            if (string.IsNullOrEmpty(name)) name = e.Hash.Substring(0, 16) + e.Extension;
            string main = Path.Combine(dir, name);
            Place(lib.BlobPath(e.Hash), main, e.Size);
            foreach (var c in e.Companions)
            {
                if (string.IsNullOrEmpty(c.RelPath) || !lib.HasBlob(c.Hash)) continue;
                string target = Path.GetFullPath(Path.Combine(dir, c.RelPath.Replace('/', sep).Replace('\\', sep)));
                if (!target.StartsWith(root, StringComparison.Ordinal)) continue;   // never outside the preview folder
                Place(lib.BlobPath(c.Hash), target, c.Size);
            }
            return main;
        }

        /// <summary>The number of leading "../" segments of a companion path.</summary>
        internal static int LeadingUps(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return 0;
            var parts = rel.Replace('\\', '/').Split('/');
            int n = 0;
            while (n < parts.Length && parts[n] == "..") n++;
            return n;
        }

        private static void Place(string blob, string target, long size)
        {
            if (File.Exists(target) && (size <= 0 || new FileInfo(target).Length == size)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(blob, target, true);
        }
    }
}
