using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using VortexEditor.Services;
using VortexEditor.Shell;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>
    /// Undoable file operations of the Asset Browser and the file tree (create folder, rename, delete, duplicate,
    /// move). Every operation goes through the global <see cref="UndoRedoManager"/> like in the Windows editor, and
    /// keeps a file's <c>.vmeta</c> sidecar (asset GUID + tags) with it. Deletes move items to the macOS Trash (Undo
    /// puts them back); elsewhere they fall back to the in-memory backup commands the WPF editor uses.
    /// </summary>
    internal static class AssetFileOps
    {
        public static string Meta => AssetDatabase.MetaFileExtension;
        public static string ProjectRoot => ProjectData.Current?.Path;

        // ------------------------------------------------------------------ paths
        public static bool PathsEqual(string a, string b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(AssetTile.Normalize(a), AssetTile.Normalize(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
        public static bool IsWithin(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string p = AssetTile.Normalize(path), r = AssetTile.Normalize(root);
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Project-relative, '/'-separated (the form components and the drag payload use).</summary>
        public static string ToRelative(string abs)
        {
            var root = ProjectRoot;
            if (string.IsNullOrEmpty(abs) || string.IsNullOrEmpty(root) || !Path.IsPathRooted(abs)) return abs?.Replace('\\', '/');
            return IsWithin(abs, root) ? Path.GetRelativePath(root, abs).Replace('\\', '/') : abs.Replace('\\', '/');
        }

        public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        /// <summary>Why a new file/folder name is not acceptable (null = fine).</summary>
        public static string ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "The name can't be empty.";
            name = name.Trim();
            if (name == "." || name == "..") return "That name is reserved.";
            if (name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0) return "Names can't contain / \\ or :.";
            if (name.StartsWith(".")) return "Names starting with a dot are hidden — choose another name.";
            if (name.EndsWith(Meta, StringComparison.OrdinalIgnoreCase)) return "\"" + Meta + "\" files are reserved for asset metadata.";
            return null;
        }

        /// <summary>"New Folder", "New Folder (1)", … (the FileExplorerService naming).</summary>
        public static string UniqueChild(string dir, string name, bool isDir)
        {
            string stem = isDir ? name : Path.GetFileNameWithoutExtension(name), ext = isDir ? "" : Path.GetExtension(name);
            string p = Path.Combine(dir, name);
            for (int i = 1; Exists(p) && i < 10000; i++) p = Path.Combine(dir, stem + " (" + i + ")" + ext);
            return p;
        }

        /// <summary>"Name - Copy.ext", "Name - Copy (2).ext", … (the Windows editor's copy naming).</summary>
        public static string UniqueCopy(string path, bool isDir)
        {
            string dir = Path.GetDirectoryName(path.TrimEnd('/', '\\'));
            string name = Path.GetFileName(path.TrimEnd('/', '\\'));
            string stem = isDir ? name : Path.GetFileNameWithoutExtension(name), ext = isDir ? "" : Path.GetExtension(name);
            string p = Path.Combine(dir, stem + " - Copy" + ext);
            for (int i = 2; Exists(p) && i < 10000; i++) p = Path.Combine(dir, stem + " - Copy (" + i + ")" + ext);
            return p;
        }

        // ------------------------------------------------------------------ create
        /// <summary>Create a folder (undoable). Returns its path, or null.</summary>
        public static string CreateFolder(string parentDir, string name = "New Folder")
        {
            if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir)) return null;
            string path = UniqueChild(parentDir, name, true);
            UndoRedoManager.Instance.Execute(new CreateFolderCommand(path));
            return Directory.Exists(path) ? path : null;
        }

        // ------------------------------------------------------------------ rename
        /// <summary>Rename a file or folder in place (undoable, sidecar follows). Returns the new path; throws with a
        /// user-facing message when the name is invalid or taken.</summary>
        public static string Rename(string path, string newName)
        {
            if (!Exists(path)) throw new IOException("\"" + Path.GetFileName(path) + "\" no longer exists.");
            string err = ValidateName(newName);
            if (err != null) throw new IOException(err);
            newName = newName.Trim();
            bool isDir = Directory.Exists(path);
            string dest = Path.Combine(Path.GetDirectoryName(path.TrimEnd('/', '\\')), newName);
            if (string.Equals(path, dest, StringComparison.Ordinal)) return path;
            bool caseOnly = string.Equals(path, dest, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && Exists(dest)) throw new IOException("An item named \"" + newName + "\" already exists here.");
            var ops = new List<FsMove> { new FsMove(path, dest, isDir) };
            if (!isDir && File.Exists(path + Meta) && (caseOnly || !File.Exists(dest + Meta))) ops.Add(new FsMove(path + Meta, dest + Meta, false));
            UndoRedoManager.Instance.Execute(new FsMoveCommand("Rename " + Path.GetFileName(path), ops));
            if (!Exists(dest)) throw new IOException("Could not rename \"" + Path.GetFileName(path) + "\".");
            ThumbnailService.Invalidate(path);
            return dest;
        }

        // ------------------------------------------------------------------ delete
        /// <summary>Delete files/folders (undoable as ONE step; each file's .vmeta goes too). Returns the deleted paths.</summary>
        public static List<string> Delete(IEnumerable<string> paths)
        {
            var list = paths.Where(Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // a folder that is being deleted swallows everything inside it
            list = list.Where(p => !list.Any(o => !ReferenceEquals(o, p) && Directory.Exists(o) && IsWithin(p, o) && !PathsEqual(p, o))).ToList();
            if (list.Count == 0) return list;
            var items = new List<string>();
            foreach (var p in list)
            {
                items.Add(p);
                if (File.Exists(p) && File.Exists(p + Meta)) items.Add(p + Meta);
            }
            string name = list.Count == 1 ? "Delete " + Path.GetFileName(list[0].TrimEnd('/', '\\')) : "Delete " + list.Count + " items";
            UndoRedoManager.Instance.Execute(new DeleteItemsToTrashCommand(name, items));
            foreach (var p in list) ThumbnailService.Invalidate(p);
            return list.Where(p => !Exists(p)).ToList();
        }

        // ------------------------------------------------------------------ duplicate
        /// <summary>Duplicate files/folders next to themselves (undoable). Copies get fresh asset GUIDs (the .vmeta
        /// sidecars are not copied). Returns the new paths.</summary>
        public static List<string> Duplicate(IEnumerable<string> paths)
        {
            var ops = new List<FsMove>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths.Where(Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool dir = Directory.Exists(p);
                string dst = UniqueCopy(p, dir);
                for (int i = 2; taken.Contains(dst) && i < 1000; i++) dst = UniqueCopy(dst, dir);
                taken.Add(dst);
                ops.Add(new FsMove(p, dst, dir));
            }
            if (ops.Count == 0) return new List<string>();
            UndoRedoManager.Instance.Execute(new DuplicateCommand(ops));
            return ops.Select(o => o.To).Where(Exists).ToList();
        }

        // ------------------------------------------------------------------ move (port of the WPF AssetBrowserView drop-move)
        public struct MoveOp { public string Src; public string Dst; public bool IsDir; }

        /// <summary>Compute the moves without performing them: skips items already in the folder, refuses to move a
        /// folder into itself/a descendant, and disambiguates name clashes with " (N)" once so redo/undo match.</summary>
        public static List<MoveOp> PlanMoves(IEnumerable<string> sourcePaths, string destDir)
        {
            var list = new List<MoveOp>();
            if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir)) return list;
            var destTrim = AssetTile.Normalize(destDir);
            var sep = Path.DirectorySeparatorChar;
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in sourcePaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(src)) continue;
                bool isDir = Directory.Exists(src), isFile = File.Exists(src);
                if (!isDir && !isFile) continue;
                var trimmed = AssetTile.Normalize(src);
                var srcParent = Path.GetDirectoryName(trimmed);
                if (string.Equals(srcParent, destTrim, StringComparison.OrdinalIgnoreCase)) continue;                       // already here
                if (isDir && (destTrim + sep).StartsWith(trimmed + sep, StringComparison.OrdinalIgnoreCase)) continue;  // into itself/descendant
                var target = Path.Combine(destTrim, Path.GetFileName(trimmed));
                if (Exists(target) || claimed.Contains(target))
                {
                    string stem = isDir ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed), ext = isDir ? "" : Path.GetExtension(trimmed);
                    for (int i = 2; i < 1000 && (Exists(target) || claimed.Contains(target)); i++) target = Path.Combine(destTrim, stem + " (" + i + ")" + ext);
                }
                claimed.Add(target);
                list.Add(new MoveOp { Src = trimmed, Dst = target, IsDir = isDir });
            }
            return list;
        }

        /// <summary>True when dropping <paramref name="sources"/> on <paramref name="destDir"/> would move anything.</summary>
        public static bool CanMoveInto(IEnumerable<string> sources, string destDir) => PlanMoves(sources, destDir).Count > 0;

        /// <summary>Apply planned moves; forward = src->dst (do/redo), else dst->src (undo). Never overwrites an occupied
        /// destination (skipped + counted); moves a file's .vmeta sidecar with it. Returns the number of skipped ops.</summary>
        public static int ApplyMoves(List<MoveOp> moves, bool forward)
        {
            if (moves == null) return 0;
            int skipped = 0;
            foreach (var mv in moves)
            {
                var from = forward ? mv.Src : mv.Dst;
                var to = forward ? mv.Dst : mv.Src;
                try
                {
                    if (mv.IsDir)
                    {
                        if (!Directory.Exists(from) || Directory.Exists(to)) { skipped++; continue; }
                        Directory.Move(from, to);
                    }
                    else
                    {
                        if (!File.Exists(from) || File.Exists(to)) { skipped++; continue; }
                        File.Move(from, to);
                        if (File.Exists(from + Meta) && !File.Exists(to + Meta)) { try { File.Move(from + Meta, to + Meta); } catch { } }
                    }
                    ThumbnailService.Invalidate(from);
                }
                catch (Exception ex) { skipped++; Debug.WriteLine("[AssetBrowser] move '" + from + "' failed: " + ex.Message); }
            }
            return skipped;
        }

        /// <summary>Move items into a folder as ONE undoable step (Undo moves them back). Returns the new paths.</summary>
        public static List<string> Move(IEnumerable<string> sources, string destDir)
        {
            var moves = PlanMoves(sources, destDir);
            if (moves.Count == 0) return new List<string>();
            UndoRedoManager.Instance.Execute(new ActionCommand(
                moves.Count == 1 ? "Move 1 item" : "Move " + moves.Count + " items",
                () => ReportMoveSkips(ApplyMoves(moves, forward: true)),
                () => ReportMoveSkips(ApplyMoves(moves, forward: false))));
            return moves.Select(m => m.Dst).Where(Exists).ToList();
        }

        private static void ReportMoveSkips(int skipped)
        {
            if (skipped <= 0) return;
            EditorCommands.Toast(skipped == 1 ? "1 item couldn't be moved — an item with that name already exists there"
                                              : skipped + " items couldn't be moved — names already exist there");
        }

        // ------------------------------------------------------------------ shell
        /// <summary>Reveal in Finder (<c>open -R</c>; Explorer /select on Windows).</summary>
        public static void Reveal(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!OperatingSystem.IsMacOS()) { EditorCommands.RevealInFinder(path); return; }
            RunOpen("-R", path);
        }

        /// <summary>Open with the application the OS associates with the file (<c>open</c>).</summary>
        public static void OpenWithDefaultApp(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (OperatingSystem.IsMacOS()) { RunOpen(null, path); return; }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { EditorCommands.Fail("Open", ex); }
        }

        /// <summary>Open in the default text editor (<c>open -t</c>).</summary>
        public static void OpenInTextEditor(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (OperatingSystem.IsMacOS()) { RunOpen("-t", path); return; }
            OpenWithDefaultApp(path);
        }

        private static void RunOpen(string flag, string path)
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, CreateNoWindow = true };
                if (flag != null) psi.ArgumentList.Add(flag);
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            catch (Exception ex) { EditorCommands.Fail("Open", ex); }
        }

        // ------------------------------------------------------------------ commands
        internal readonly struct FsMove
        {
            public readonly string From, To; public readonly bool IsDir;
            public FsMove(string from, string to, bool isDir) { From = from; To = to; IsDir = isDir; }
        }

        /// <summary>Rename/move of one or more paths (a file + its sidecar); case-only renames hop through a temp name.</summary>
        private sealed class FsMoveCommand : UndoableCommandBase
        {
            private readonly string _name; private readonly List<FsMove> _ops;
            public FsMoveCommand(string name, List<FsMove> ops) { _name = name; _ops = ops; }
            public override string Name => _name;
            public override void Execute() { foreach (var op in _ops) MoveOne(op.From, op.To, op.IsDir); }
            public override void Undo() { for (int i = _ops.Count - 1; i >= 0; i--) MoveOne(_ops[i].To, _ops[i].From, _ops[i].IsDir); }

            private static void MoveOne(string from, string to, bool dir)
            {
                bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
                if (dir ? !Directory.Exists(from) : !File.Exists(from)) return;
                if (!caseOnly && (dir ? Directory.Exists(to) : File.Exists(to))) return;   // never clobber
                if (caseOnly)
                {
                    string tmp = from + ".vxrename-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    if (dir) { Directory.Move(from, tmp); Directory.Move(tmp, to); } else { File.Move(from, tmp); File.Move(tmp, to); }
                }
                else if (dir) Directory.Move(from, to);
                else File.Move(from, to);
            }
        }

        /// <summary>Delete = move to the Trash (macOS; Undo moves the items back). Items the Trash refuses — and every
        /// item on other platforms — use the WPF editor's in-memory backup commands instead.</summary>
        private sealed class DeleteItemsToTrashCommand : UndoableCommandBase
        {
            private sealed class Item { public string Path; public bool IsDir; public string Trashed; public IUndoableCommand Fallback; }
            private readonly string _name; private readonly List<Item> _items;
            public DeleteItemsToTrashCommand(string name, List<string> paths)
            {
                _name = name;
                _items = paths.Select(p => new Item { Path = p, IsDir = Directory.Exists(p) }).ToList();
            }
            public override string Name => _name;
            public override void Execute()
            {
                foreach (var it in _items)
                {
                    it.Trashed = null; it.Fallback = null;
                    if (!Exists(it.Path)) continue;
                    if (SystemTrash.IsSupported) it.Trashed = SystemTrash.Trash(it.Path);
                    if (it.Trashed == null)
                    {
                        it.Fallback = it.IsDir ? (IUndoableCommand)new DeleteFolderCommand(it.Path) : new DeleteFileCommand(it.Path);
                        it.Fallback.Execute();
                    }
                }
            }
            public override void Undo()
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    var it = _items[i];
                    try
                    {
                        if (it.Fallback != null) { it.Fallback.Undo(); continue; }
                        if (it.Trashed == null || Exists(it.Path)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(it.Path));
                        if (it.IsDir) { if (Directory.Exists(it.Trashed)) Directory.Move(it.Trashed, it.Path); }
                        else if (File.Exists(it.Trashed)) File.Move(it.Trashed, it.Path);
                    }
                    catch (Exception ex) { EditorCommands.Toast("Could not restore " + System.IO.Path.GetFileName(it.Path) + ": " + ex.Message); }
                }
            }
        }

        /// <summary>Copy files/folders; Undo deletes the copies (and any .vmeta the asset database created meanwhile).</summary>
        private sealed class DuplicateCommand : UndoableCommandBase
        {
            private readonly List<FsMove> _ops;
            public DuplicateCommand(List<FsMove> ops) { _ops = ops; }
            public override string Name => _ops.Count == 1 ? "Duplicate " + Path.GetFileName(_ops[0].From.TrimEnd('/', '\\')) : "Duplicate " + _ops.Count + " items";
            public override void Execute()
            {
                foreach (var op in _ops)
                {
                    if (Exists(op.To)) continue;
                    if (op.IsDir)
                    {
                        if (!Directory.Exists(op.From)) continue;
                        CopyDirectory(op.From, op.To);
                        // fresh GUIDs for the copies: the asset database writes new sidecars on its next scan
                        foreach (var m in Directory.EnumerateFiles(op.To, "*" + Meta, SearchOption.AllDirectories).ToList()) { try { File.Delete(m); } catch { } }
                    }
                    else if (File.Exists(op.From)) File.Copy(op.From, op.To, false);
                }
            }
            public override void Undo()
            {
                for (int i = _ops.Count - 1; i >= 0; i--)
                {
                    var op = _ops[i];
                    try
                    {
                        if (op.IsDir) { if (Directory.Exists(op.To)) Directory.Delete(op.To, true); }
                        else
                        {
                            if (File.Exists(op.To)) File.Delete(op.To);
                            if (File.Exists(op.To + Meta)) File.Delete(op.To + Meta);
                        }
                    }
                    catch { }
                }
            }
            private static void CopyDirectory(string src, string dst)
            {
                Directory.CreateDirectory(dst);
                foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false);
                foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
            }
        }
    }
}
