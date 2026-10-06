using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Editor.Core.Services;

namespace VortexEditor.Claude
{
    /// <summary>The Claude sidebar's choices across sessions (<c>&lt;VortexAppData&gt;/claude-panel.json</c>): model, effort
    /// per model, context size, Ask / Agent, whether the sidebar is open and how wide.</summary>
    internal sealed class ClaudePanelSettings
    {
        public string Model { get; set; }
        /// <summary>Effort per model id (the levels mean different amounts of thinking on each model).</summary>
        public Dictionary<string, string> Effort { get; set; } = new Dictionary<string, string>();
        public int Context { get; set; }
        public string Mode { get; set; }
        public bool Open { get; set; }
        public double Width { get; set; }
        /// <summary>Tell Claude what is selected in the editor with each message.</summary>
        public bool ShareSelection { get; set; } = true;

        private static ClaudePanelSettings _current;
        private static string FilePath => Path.Combine(EditorPaths.VortexAppData, "claude-panel.json");

        public static ClaudePanelSettings Current
        {
            get
            {
                if (_current != null) return _current;
                try { _current = File.Exists(FilePath) ? JsonSerializer.Deserialize<ClaudePanelSettings>(File.ReadAllText(FilePath)) : null; }
                catch { _current = null; }
                _current ??= new ClaudePanelSettings();
                _current.Effort ??= new Dictionary<string, string>();
                return _current;
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        /// <summary>Forget the cached file (tests).</summary>
        internal static void Reload() => _current = null;
    }
}
