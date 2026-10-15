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
    /// for the session); a revert restores the copies — a file that did not exist goes again — and a save or a revert makes the
    /// files on disk the new baseline. Water keeps no data file: its settings are in the scene.
    /// </summary>
    public static class SceneDataFiles
    {
        private sealed class Kept { public string Copy; public bool Existed; }

        private static readonly Dictionary<string, Kept> _kept = new Dictionary<string, Kept>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();
        private static string _dir;

        /// <summary>Call right before a scene data file is written: the first write since the baseline keeps the file as it was.</summary>
        public static void BeforeWrite(string path)
        {
            string full = Full(path);
            if (full == null) return;
            lock (_lock)
            {
                if (_kept.ContainsKey(full)) return;
                var k = new Kept { Existed = File.Exists(full) };
                if (k.Existed)
                {
                    try
                    {
                        _dir ??= Path.Combine(Path.GetTempPath(), "vortex-scene-data", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        }

        /// <summary>The scene's data files written since it was read or saved (absolute paths).</summary>
        public static List<string> Pending(Scene scene)
        {
            var paths = PathsOf(scene);
            lock (_lock) return paths.Where(p => _kept.ContainsKey(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The scene was read or saved: its data files are the baseline now.</summary>
        public static void Forget(Scene scene)
        {
            var paths = PathsOf(scene);
            lock (_lock) foreach (var p in paths) Drop(p);
        }

        /// <summary>Put the kept versions of <paramref name="paths"/> back (a file that did not exist goes again) and forget them;
        /// returns the files restored. Paths without a kept version are left alone.</summary>
        public static List<string> Restore(IEnumerable<string> paths)
        {
            var done = new List<string>();
            lock (_lock)
            {
                foreach (var raw in paths ?? Enumerable.Empty<string>())
                {
                    string p = Full(raw);
                    if (p == null || !_kept.TryGetValue(p, out var k)) continue;
                    try
                    {
                        if (k.Existed) File.Copy(k.Copy, p, true);
                        else if (File.Exists(p)) File.Delete(p);
                        done.Add(p);
                        Drop(p);
                    }
                    catch (Exception ex) { try { ConsoleService.Instance.LogError("Revert could not put back " + Path.GetFileName(p) + ": " + ex.Message); } catch { } }
                }
            }
            return done;
        }

        /// <summary>The project closed: every kept copy goes.</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                foreach (var p in _kept.Keys.ToList()) Drop(p);
            }
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
