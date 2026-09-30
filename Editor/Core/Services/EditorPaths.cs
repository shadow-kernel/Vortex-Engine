using System;
using System.IO;

namespace Editor.Core.Services
{
    /// <summary>
    /// Where the editor keeps its per-user state (project registry, last project, preferences).
    /// VORTEX_APPDATA_DIR overrides the location — automated runs (smoke tests, CI) use a scratch folder so they
    /// never touch the user's real project list.
    /// </summary>
    public static class EditorPaths
    {
        public static string AppDataRoot()
        {
            var o = Environment.GetEnvironmentVariable("VORTEX_APPDATA_DIR");
            if (!string.IsNullOrEmpty(o)) return o;
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        public static string VortexAppData => Path.Combine(AppDataRoot(), "VortexEngine");
    }
}
