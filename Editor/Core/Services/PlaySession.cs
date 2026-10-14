using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Data;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// What an editor play session wrote to disk (#423). Stop puts the scenes back through the play snapshot, but files stay
    /// where they are: this ledger knows which data files play created (Stop deletes those no entity uses any more), which
    /// prefabs were written (Stop re-syncs their instances) and which files to name in the Stop line. The editor's viewport
    /// begins and ends it — the standalone player never does, so nothing is recorded there.
    /// </summary>
    public static class PlaySession
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, bool> _writes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // full path → play created it

        /// <summary>The data files a terrain, a foliage layer or an area keeps beside the scene.</summary>
        private static readonly string[] DataExtensions = { ".vterrain", ".vfoliage", ".varea" };

        public static bool Active { get; private set; }
        public static DateTime StartedUtc { get; private set; }

        public static void Begin()
        {
            lock (_lock) { _writes.Clear(); Active = true; StartedUtc = DateTime.UtcNow; }
        }

        /// <summary>Recording stops; what was recorded stays readable until the next <see cref="Begin"/>.</summary>
        public static void End() { lock (_lock) Active = false; }

        /// <summary>A writer reports a project file it wrote — <paramref name="created"/> when it did not exist before. The first
        /// report of a path wins, so a file play created stays "created" through later writes. Ignored outside play.</summary>
        public static void NoteWrite(string path, bool created)
        {
            if (!Active || string.IsNullOrEmpty(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch { return; }
            lock (_lock) { if (!_writes.ContainsKey(full)) _writes[full] = created; }
        }

        /// <summary>The files writers reported this session (full paths).</summary>
        public static List<string> Written()
        {
            lock (_lock) return new List<string>(_writes.Keys);
        }

        /// <summary>A terrain, foliage or area data file this play session created (it did not exist at Play).</summary>
        public static bool CreatedDataFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !IsDataFile(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); } catch { return false; }
            lock (_lock) return _writes.TryGetValue(full, out bool created) && created;
        }

        private static bool IsDataFile(string path)
        {
            string ext = Path.GetExtension(path);
            foreach (var e in DataExtensions) if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Delete the data files play created that no entity of <paramref name="scenes"/> uses after Stop — a terrain, a
        /// foliage layer or an area made while playing is gone again, and so is its file (with its .vmeta). Nothing is deleted
        /// when a scene or a prefab file was written while playing (<paramref name="kept"/> says so): it may refer to them.
        /// Returns the deleted files.
        /// </summary>
        public static List<string> DeleteOrphanedDataFiles(IEnumerable<Scene> scenes, out string kept)
        {
            kept = null;
            var deleted = new List<string>();
            List<string> created = new List<string>();
            bool savedSceneOrPrefab = false;
            lock (_lock)
            {
                foreach (var kv in _writes)
                {
                    string ext = Path.GetExtension(kv.Key);
                    if (string.Equals(ext, Scene.FileExtension, StringComparison.OrdinalIgnoreCase) || string.Equals(ext, PrefabService.PrefabExtension, StringComparison.OrdinalIgnoreCase))
                        savedSceneOrPrefab = true;
                    else if (kv.Value && IsDataFile(kv.Key)) created.Add(kv.Key);
                }
            }
            if (created.Count == 0) return deleted;
            if (savedSceneOrPrefab)
            {
                kept = "kept " + created.Count + " data file(s) created while playing — a scene or prefab saved while playing may use them";
                return deleted;
            }
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Collect(GameEntity e)
            {
                if (e == null) return;
                foreach (var c in e.Components)
                {
                    string rel = c is ECS.Components.Rendering.Terrain t ? t.DataPath
                               : c is ECS.Components.Rendering.Foliage f ? f.DataPath
                               : c is ECS.Components.World.Area a ? a.DataPath : null;
                    if (string.IsNullOrWhiteSpace(rel)) continue;
                    string full = Terrain.TerrainService.ResolvePath(rel);
                    if (full != null) used.Add(full);
                }
                if (e.Children != null) foreach (var child in e.Children) Collect(child);
            }
            if (scenes != null)
                foreach (var s in scenes)
                    if (s?.Entities != null) foreach (var e in s.Entities) Collect(e);
            foreach (var path in created)
            {
                if (used.Contains(path) || !File.Exists(path)) continue;
                try
                {
                    File.Delete(path);
                    if (File.Exists(path + ".vmeta")) File.Delete(path + ".vmeta");
                    deleted.Add(path);
                }
                catch { }
            }
            return deleted;
        }

        /// <summary>Project files written since <paramref name="sinceUtc"/> — anything under Assets/ and the project file, by
        /// their time stamps, whoever wrote them (.vmeta sidecars are not listed). At most <paramref name="max"/>.</summary>
        public static List<string> FilesWrittenSince(string projectPath, DateTime sinceUtc, int max = 200)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath)) return list;
            void Check(string f)
            {
                if (list.Count >= max || f.EndsWith(".vmeta", StringComparison.OrdinalIgnoreCase)) return;
                try { if (File.GetLastWriteTimeUtc(f) >= sinceUtc) list.Add(f); } catch { }
            }
            try
            {
                foreach (var f in Directory.EnumerateFiles(projectPath, "*" + FileExtensions.Project)) Check(f);
                string assets = Path.Combine(projectPath, "Assets");
                if (Directory.Exists(assets))
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                    foreach (var f in Directory.EnumerateFiles(assets, "*", options)) { Check(f); if (list.Count >= max) break; }
                }
            }
            catch { }
            return list;
        }
    }
}
