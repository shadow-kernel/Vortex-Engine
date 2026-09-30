using System;
using System.IO;
using System.Text.Json;
using Editor.Core.Services;

namespace Editor.Core.Editing
{
    /// <summary>Per-user editor preferences (editor-prefs.json in the Vortex app-data folder).</summary>
    public sealed class EditorPreferences
    {
        private static EditorPreferences _current;
        public static EditorPreferences Current => _current ?? (_current = Load());

        /// <summary>Skip the project hub and reopen the last project when the editor starts (off by default).</summary>
        public bool OpenLastProjectOnStart { get; set; }
        /// <summary>"System", "Dark" or "Light".</summary>
        public string Theme { get; set; } = "System";
        /// <summary>Preferred IDE for scripts: "" (auto), "code", "rider".</summary>
        public string ScriptIde { get; set; } = "";

        private static string FilePath => Path.Combine(EditorPaths.VortexAppData, "editor-prefs.json");

        public static EditorPreferences Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var p = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(FilePath));
                    if (p != null) return p;
                }
            }
            catch { }
            return new EditorPreferences();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(EditorPaths.VortexAppData);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
