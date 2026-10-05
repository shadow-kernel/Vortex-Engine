using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Editor.Core.Assets.Library
{
    /// <summary>
    /// The files a model needs next to it, so a library model is complete when it is added to another project:
    /// <list type="bullet">
    /// <item>a model in its own folder (the import pipeline's layout <c>&lt;target&gt;/&lt;name&gt;/</c>, or the only model
    /// in its folder) brings the whole folder: buffers, textures, <c>materials/*.vmat</c>, <c>animations/*.vanim</c>;</item>
    /// <item>otherwise the files the model references: glTF <c>buffers[].uri</c> / <c>images[].uri</c>, OBJ <c>mtllib</c>
    /// and the maps of those .mtl files.</item>
    /// </list>
    /// Paths are relative to the model's folder ('/' separators).
    /// </summary>
    public static class LibraryCompanions
    {
        public const int MaxFiles = 2000;
        public const long MaxBytes = 4L * 1024 * 1024 * 1024;

        public static readonly string[] ModelExtensions = { ".fbx", ".obj", ".gltf", ".glb", ".dae", ".3ds", ".blend" };

        public static bool IsModelFile(string path) => Array.IndexOf(ModelExtensions, Path.GetExtension(path ?? "").ToLowerInvariant()) >= 0;

        /// <summary>absolute path → relative path of every companion of <paramref name="mainFile"/> (empty for non-models).</summary>
        public static Dictionary<string, string> Detect(string mainFile)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(mainFile) || !File.Exists(mainFile) || !IsModelFile(mainFile)) return result;
            string dir = Path.GetDirectoryName(Path.GetFullPath(mainFile));
            try
            {
                if (OwnsFolder(mainFile)) AddFolder(dir, mainFile, result);
                string ext = Path.GetExtension(mainFile).ToLowerInvariant();
                if (ext == ".gltf") foreach (var f in GltfReferences(mainFile)) Add(dir, f, result);
                else if (ext == ".obj") foreach (var f in ObjReferences(mainFile)) Add(dir, f, result);
            }
            catch { }
            return result;
        }

        /// <summary>The model's folder belongs to it: the folder is named like the model, or it is the only model there.</summary>
        public static bool OwnsFolder(string mainFile)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(mainFile));
            if (string.IsNullOrEmpty(dir)) return false;
            string folderName = Path.GetFileName(dir.TrimEnd('/', '\\'));
            if (string.Equals(folderName, "Assets", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(folderName, Path.GetFileNameWithoutExtension(mainFile), StringComparison.OrdinalIgnoreCase)) return true;
            int models = 0;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (IsModelFile(f) && ++models > 1) return false;
            return models == 1;
        }

        private static void AddFolder(string dir, string mainFile, Dictionary<string, string> result)
        {
            // sub-folders that hold another model belong to that model's entry
            var foreign = new List<string>();
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                if (Directory.EnumerateFiles(sub).Any(IsModelFile)) foreign.Add(Path.GetFullPath(sub).TrimEnd('/', '\\') + Path.DirectorySeparatorChar);
            long bytes = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (result.Count >= MaxFiles) break;
                if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(mainFile), StringComparison.OrdinalIgnoreCase)) continue;
                if (IsIgnored(dir, f)) continue;
                string full = Path.GetFullPath(f);
                if (foreign.Any(x => full.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                if (IsModelFile(f)) continue;
                long len = 0; try { len = new FileInfo(f).Length; } catch { }
                if (bytes + len > MaxBytes) break;
                bytes += len;
                Add(dir, f, result);
            }
        }

        /// <summary>Editor/OS clutter that never travels with an asset.</summary>
        public static bool IsIgnored(string root, string file)
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal)) return true;
            if (name.EndsWith(AssetDatabase.MetaFileExtension, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
            string rel = Rel(root, file);
            foreach (var part in rel.Split('/'))
                if (part.StartsWith(".", StringComparison.Ordinal)) return true;   // .ve/, .git/ …
            return false;
        }

        private static void Add(string dir, string file, Dictionary<string, string> result)
        {
            if (!File.Exists(file)) return;
            string full = Path.GetFullPath(file);
            string rel = Rel(dir, full);
            if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return;   // outside the model folder: not portable
            if (!result.ContainsKey(full)) result[full] = rel;
        }

        public static string Rel(string root, string file)
        {
            string r = Path.GetFullPath(root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(file);
            if (f.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return f.Substring(r.Length).Replace('\\', '/');
            return "../" + Path.GetFileName(f);
        }

        private static IEnumerable<string> GltfReferences(string gltf)
        {
            string dir = Path.GetDirectoryName(gltf);
            string json = File.ReadAllText(gltf);
            // "uri": "..." in buffers and images (data: URIs are embedded)
            foreach (Match m in Regex.Matches(json, "\"uri\"\\s*:\\s*\"([^\"]+)\""))
            {
                string uri = m.Groups[1].Value;
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                string p;
                try { p = Path.Combine(dir, Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar)); } catch { continue; }
                yield return p;
            }
        }

        private static IEnumerable<string> ObjReferences(string obj)
        {
            string dir = Path.GetDirectoryName(obj);
            var mtls = new List<string>();
            foreach (var raw in File.ReadLines(obj))
            {
                var line = raw.Trim();
                if (line.StartsWith("mtllib ", StringComparison.Ordinal)) mtls.Add(Path.Combine(dir, line.Substring(7).Trim()));
            }
            foreach (var mtl in mtls)
            {
                yield return mtl;
                if (!File.Exists(mtl)) continue;
                string mdir = Path.GetDirectoryName(mtl);
                foreach (var raw in File.ReadLines(mtl))
                {
                    var line = raw.Trim();
                    if (!(line.StartsWith("map_", StringComparison.OrdinalIgnoreCase) || line.StartsWith("bump ", StringComparison.OrdinalIgnoreCase)
                          || line.StartsWith("disp ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("norm ", StringComparison.OrdinalIgnoreCase)
                          || line.StartsWith("refl ", StringComparison.OrdinalIgnoreCase))) continue;
                    // the file name is the last token (options like "-bm 1.0" come first)
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    yield return Path.Combine(mdir, parts[parts.Length - 1]);
                }
            }
        }
    }
}
