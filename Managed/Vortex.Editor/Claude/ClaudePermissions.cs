using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor.Core.Data;
using Editor.Core.Services;

namespace VortexEditor.Claude
{
    /// <summary>
    /// "Always allow" choices of the Claude panel, per project — kept in the user's app data
    /// (<c>claude-permissions.json</c>, keyed by project folder), never in the project itself: a teammate who clones the
    /// project starts with their own permissions.
    /// </summary>
    public static class ClaudePermissions
    {
        private static Dictionary<string, List<string>> _all;
        private static string FilePath => Path.Combine(EditorPaths.VortexAppData, "claude-permissions.json");

        private static Dictionary<string, List<string>> All
        {
            get
            {
                if (_all != null) return _all;
                try { _all = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath)) : null; } catch { _all = null; }
                return _all ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
            }
        }

        private static string Key => ProjectData.Current?.Path is string p ? Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) : "";

        public static bool IsAlwaysAllowed(string tool) => All.TryGetValue(Key, out var list) && list.Contains(tool);

        public static IReadOnlyList<string> AlwaysAllowed => All.TryGetValue(Key, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();

        public static void AllowAlways(string tool)
        {
            if (string.IsNullOrEmpty(Key)) return;
            if (!All.TryGetValue(Key, out var list)) All[Key] = list = new List<string>();
            if (!list.Contains(tool)) { list.Add(tool); Save(); }
        }

        public static void Reset()
        {
            if (All.Remove(Key)) Save();
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(EditorPaths.VortexAppData);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { ConsoleService.Instance.LogWarning("Claude permissions not saved: " + ex.Message); }
        }
    }
}
