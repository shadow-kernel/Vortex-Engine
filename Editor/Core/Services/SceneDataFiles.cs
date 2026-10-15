using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Data;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// The data files a scene keeps beside its .vscene — terrain heights (.vterrain), painted foliage (.vfoliage), area
    /// definitions (.varea) — are written with every stroke, not when the scene is saved (#411 review). So that a revert puts
    /// them back too, the first write after the scene was read or saved keeps a copy of the file as it was (in the temp folder,
    /// for the session); a revert restores the copies — a file that did not exist goes again, with its .vmeta — and a save or
    /// a revert makes the files on disk the new baseline. A file changed on disk since the editor last wrote it (a git pull, a
    /// teammate's version) is left as it is: a revert never overwrites what the editor did not write. Each write also notes
    /// the scene it was for, so a file made for an entity that was deleted again is still the scene's to clean up. Water keeps
    /// no data file: its settings are in the scene.
    /// </summary>
    public static class SceneDataFiles
    {
        private sealed class Kept
        {
            public string Copy;          // the file as it was at the baseline; null when it did not exist
            public bool Existed;
            public long Length = -1;     // the file as the editor last wrote it (-1: no write finished)
            public DateTime TimeUtc;
            public readonly HashSet<Scene> Owners = new HashSet<Scene>(ReferenceEqualityComparer.Instance);
        }

        private static readonly Dictionary<string, Kept> _kept = new Dictionary<string, Kept>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();
        private static string _dir;

        /// <summary>Where this editor keeps its copies: the temp folder, one subfolder per process.</summary>
        internal static string Root => Path.Combine(Path.GetTempPath(), "vortex-scene-data");

        /// <summary>Call right before a scene data file is written: the first write since the baseline keeps the file as it was.
        /// <paramref name="owner"/> is the scene the file is written for (its entity's scene), when the writer knows it.</summary>
        public static void BeforeWrite(string path, Scene owner = null)
        {
            string full = Full(path);
            if (full == null) return;
            lock (_lock)
            {
                if (!_kept.TryGetValue(full, out var k))
                {
                    k = new Kept { Existed = File.Exists(full) };
                    if (k.Existed)
                    {
                        try
                        {
                            _dir ??= Path.Combine(Root, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            Directory.CreateDirectory(_dir);
                            k.Copy = Path.Combine(_dir, Guid.NewGuid().ToString("N") + Path.GetExtension(full));
                            File.Copy(full, k.Copy, true);
                        }
                        catch (Exception ex)
                        {
                            // without a copy a revert cannot put this file back: it is not remembered, so a revert does not claim to
                            try { ConsoleService.Instance.LogWarning("Could not keep a copy of " + Path.GetFileName(full) + " for Revert: " + ex.Message); } catch { }
                            return;
                        }
                    }
                    _kept[full] = k;
                }
                if (owner != null) k.Owners.Add(owner);
            }
        }

        /// <summary>Call after the editor wrote a scene data file: this is its version of the file now — a revert puts the kept
        /// copy back only while the file on disk still is.</summary>
        public static void Written(string path)
        {
            string full = Full(path);
            if (full == null) return;
            lock (_lock)
            {
                if (!_kept.TryGetValue(full, out var k)) return;
                try
                {
                    var fi = new FileInfo(full);
                    if (fi.Exists) { k.Length = fi.Length; k.TimeUtc = fi.LastWriteTimeUtc; }
                }
                catch { }
            }
        }

        /// <summary>The scene's data files written since it was read or saved (absolute paths): those its entities use, and those
        /// written for it whose entity is gone again.</summary>
        public static List<string> Pending(Scene scene)
        {
            var paths = PathsOf(scene);
            lock (_lock)
                return _kept.Where(kv => paths.Contains(kv.Key) || (scene != null && kv.Value.Owners.Contains(scene))).Select(kv => kv.Key)
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The scene was read or saved: its data files are the baseline now.</summary>
        public static void Forget(Scene scene)
        {
            var paths = PathsOf(scene);
            lock (_lock)
                foreach (var p in _kept.Where(kv => paths.Contains(kv.Key) || (scene != null && kv.Value.Owners.Contains(scene))).Select(kv => kv.Key).ToList())
                    Drop(p);
        }

        /// <summary>
        /// Put the kept versions of <paramref name="paths"/> back and forget them: a file that existed gets its copy back, one that
        /// did not exist goes again with its .vmeta. A file changed on disk since the editor last wrote it stays as it is and is
        /// listed in <paramref name="changedOnDisk"/>. Returns the files restored or removed; paths without a kept version are
        /// left alone.
        /// </summary>
        public static List<string> Restore(IEnumerable<string> paths, out List<string> changedOnDisk)
        {
            var done = new List<string>();
            changedOnDisk = new List<string>();
            lock (_lock)
            {
                foreach (var raw in paths ?? Enumerable.Empty<string>())
                {
                    string p = Full(raw);
                    if (p == null || !_kept.TryGetValue(p, out var k)) continue;
                    try
                    {
                        if (!k.Existed && !File.Exists(p))
                        {
                            // made since the baseline and gone again already: only its sidecar may be left
                            if (File.Exists(p + ".vmeta")) File.Delete(p + ".vmeta");
                            Drop(p);
                            continue;
                        }
                        if (ChangedSinceWrite(p, k))
                        {
                            changedOnDisk.Add(p);   // not the editor's version any more: whoever wrote it keeps it
                            Drop(p);
                            continue;
                        }
                        if (k.Existed) File.Copy(k.Copy, p, true);
                        else
                        {
                            if (File.Exists(p)) File.Delete(p);
                            if (File.Exists(p + ".vmeta")) File.Delete(p + ".vmeta");   // the asset database's sidecar of the new file
                        }
                        done.Add(p);
                        Drop(p);
                    }
                    catch (Exception ex) { try { ConsoleService.Instance.LogError("Revert could not put back " + Path.GetFileName(p) + ": " + ex.Message); } catch { } }
                }
            }
            return done;
        }

        /// <summary>The files written for <paramref name="scene"/> since its baseline whose entity is not in it any more — a
        /// terrain made and deleted again.</summary>
        public static List<string> OwnedBy(Scene scene)
        {
            if (scene == null) return new List<string>();
            lock (_lock) return _kept.Where(kv => kv.Value.Owners.Contains(scene)).Select(kv => kv.Key).ToList();
        }

        /// <summary>Every kept copy goes — the project closed (<paramref name="removeFolder"/>: the editor quits, and this
        /// process's folder goes too).</summary>
        public static void Clear(bool removeFolder = false)
        {
            lock (_lock)
            {
                foreach (var p in _kept.Keys.ToList()) Drop(p);
                if (removeFolder && _dir != null)
                {
                    try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
                }
            }
        }

        /// <summary>
        /// Delete the copies an editor left behind when it crashed or was killed (#411 review): every process folder under the
        /// temp folder whose process is gone. A folder of a running process stays (another editor, or a process that took the
        /// id over — its folder goes once that one ends). Returns how many folders went.
        /// </summary>
        public static int SweepStale()
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(Root)) return 0;
                foreach (var dir in Directory.GetDirectories(Root))
                {
                    if (!int.TryParse(Path.GetFileName(dir), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid)) continue;
                    if (pid == Environment.ProcessId || Alive(pid)) continue;
                    try { Directory.Delete(dir, true); n++; } catch { }
                }
            }
            catch { }
            return n;
        }

        private static bool Alive(int pid)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
            catch (ArgumentException) { return false; }   // no such process
            catch { return true; }                       // cannot tell: keep it
        }

        /// <summary>Every data file the scene's entities use — terrain, foliage and area files — as absolute paths.</summary>
        public static HashSet<string> PathsOf(Scene scene)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scene?.Entities == null) return set;
            var todo = new Stack<GameEntity>(scene.Entities);
            while (todo.Count > 0)
            {
                var e = todo.Pop();
                if (e == null) continue;
                foreach (var c in e.Components)
                {
                    string rel = c switch
                    {
                        Editor.ECS.Components.Rendering.Terrain t => t.DataPath,
                        Editor.ECS.Components.Rendering.Foliage f => f.DataPath,
                        Editor.ECS.Components.World.Area a => a.DataPath,
                        _ => null,
                    };
                    string full = Full(rel);
                    if (full != null) set.Add(full);
                }
                if (e.Children != null) foreach (var c in e.Children) todo.Push(c);
            }
            return set;
        }

        /// <summary>The file is not what the editor last wrote there (a write that did not finish counts as none).</summary>
        private static bool ChangedSinceWrite(string path, Kept k)
        {
            if (k.Length < 0) return false;
            try
            {
                var fi = new FileInfo(path);
                return !fi.Exists || fi.Length != k.Length || fi.LastWriteTimeUtc != k.TimeUtc;
            }
            catch { return false; }
        }

        private static void Drop(string full)
        {
            if (!_kept.TryGetValue(full, out var k)) return;
            _kept.Remove(full);
            try { if (k.Copy != null && File.Exists(k.Copy)) File.Delete(k.Copy); } catch { }
        }

        private static string Full(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string p = path.Replace('\\', '/');
                if (Path.IsPathRooted(p)) return Path.GetFullPath(p);
                string root = ProjectData.Current?.Path;
                return string.IsNullOrEmpty(root) ? null : Path.GetFullPath(Path.Combine(root, p));
            }
            catch { return null; }
        }
    }
}
