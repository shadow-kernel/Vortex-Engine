using System;
using System.IO;
using System.Linq;
using System.Text;
using Editor.Core.Assets;
using Editor.Core.UndoRedo;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Files the tools read and write: always inside the open project's <c>Assets/</c> folder (no "..", no absolute
    /// paths elsewhere), named relative to the project ("Assets/Materials/Rust.vmat"). Writes go through
    /// <see cref="WriteFileCommand"/>, so a file a tool creates or changes is part of the call's undo step: Undo
    /// restores the previous content (or removes a file the call created).
    /// </summary>
    public static class ProjectFiles
    {
        public static string Root => SceneModel.Project.Path;
        public static string AssetsDir => Path.Combine(Root, "Assets");

        /// <summary>Absolute path of a project file given relative to the project ("Assets/…") or to Assets/ ("Materials/…").</summary>
        public static string Resolve(string relative, string[] extensions = null, bool mustExist = false)
        {
            if (string.IsNullOrWhiteSpace(relative)) throw new ToolError("A project path is required (e.g. Assets/Materials/Rust.vmat).");
            string r = relative.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(r))
            {
                string full0 = Path.GetFullPath(r);
                if (!Inside(full0)) throw new ToolError("'" + relative + "' is outside the project's Assets folder.");
                r = Rel(full0);
            }
            if (!r.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) r = "Assets/" + r.TrimStart('/');
            string full = Path.GetFullPath(Path.Combine(Root, r));
            if (!Inside(full)) throw new ToolError("'" + relative + "' is outside the project's Assets folder.");
            if (extensions != null && !extensions.Any(x => full.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                throw new ToolError("'" + relative + "' must be a " + string.Join(" / ", extensions) + " file.");
            if (mustExist && !File.Exists(full)) throw new ToolError("There is no file '" + Rel(full) + "' in the project.");
            return full;
        }

        public static bool Inside(string full)
        {
            string assets = Path.GetFullPath(AssetsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(full).StartsWith(assets, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>Project-relative path with forward slashes ("Assets/Materials/Rust.vmat").</summary>
        public static string Rel(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');

        /// <summary>A free file name: "Rust.vmat", then "Rust 2.vmat", …</summary>
        public static string Unique(string dir, string name, string ext)
        {
            string clean = string.Concat((name ?? "").Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' || c == '\\' ? '_' : c));
            if (clean.Length == 0) clean = "New";
            string p = Path.Combine(dir, clean + ext);
            for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, clean + " " + i + ext);
            return p;
        }

        /// <summary>Write a text file as part of the running call's undo step; <paramref name="after"/> runs after every
        /// write, undo and redo (e.g. to push a material to the viewport).</summary>
        public static void Write(string full, string content, Action<string> after = null)
        {
            if (!Inside(full)) throw new ToolError("'" + full + "' is outside the project's Assets folder.");
            UndoRedoManager.Instance.Execute(new WriteFileCommand(full, content, after));
        }

        /// <summary>Undoable text-file write: remembers the previous bytes (or that there was no file).</summary>
        internal sealed class WriteFileCommand : UndoableCommandBase
        {
            private readonly string _path;
            private readonly byte[] _new;
            private readonly byte[] _old;
            private readonly Action<string> _after;

            public WriteFileCommand(string path, string content, Action<string> after)
            {
                _path = path;
                _new = new UTF8Encoding(false).GetBytes(content ?? "");
                _old = File.Exists(path) ? File.ReadAllBytes(path) : null;
                _after = after;
            }

            public override string Name => (_old == null ? "Create " : "Write ") + Path.GetFileName(_path);
            public bool Created => _old == null;
            public string OldText => _old == null ? null : Encoding.UTF8.GetString(_old);

            public override void Execute()
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllBytes(_path, _new);
                try { AssetDatabase.Instance.RegisterFile(_path); } catch { }
                _after?.Invoke(_path);
            }

            public override void Undo()
            {
                if (_old != null) File.WriteAllBytes(_path, _old);
                else
                {
                    try { if (File.Exists(_path)) File.Delete(_path); } catch { }
                    try { if (File.Exists(_path + AssetDatabase.MetaFileExtension)) File.Delete(_path + AssetDatabase.MetaFileExtension); } catch { }
                }
                _after?.Invoke(_path);
            }
        }
    }
}
