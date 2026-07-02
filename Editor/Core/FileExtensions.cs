namespace Editor.Core
{
    /// <summary>
    /// Konstanten f�r Vortex Engine Dateierweiterungen
    /// </summary>
    public static class FileExtensions
    {
        /// <summary>
        /// Vortex Engine Projekt-Datei (.vortex)
        /// </summary>
        public const string Project = ".vortex";

        /// <summary>
        /// Vortex Engine Szenen-Datei (.vscene)
        /// </summary>
        public const string Scene = ".vscene";

        /// <summary>
        /// Vortex Engine Entity/Prefab-Datei (.ventity)
        /// </summary>
        public const string Entity = ".ventity";

        /// <summary>
        /// Vortex Engine Material-Datei (.vmat)
        /// </summary>
        public const string Material = ".vmat";

        /// <summary>
        /// Vortex Engine Shader-Datei (.vshader)
        /// </summary>
        public const string Shader = ".vshader";

        /// <summary>
        /// Vortex Engine Animations-Clip (.vanim)
        /// </summary>
        public const string Animation = ".vanim";

        /// <summary>
        /// Gibt alle Vortex Engine Dateierweiterungen zur�ck
        /// </summary>
        public static string[] All => new[]
        {
            Project,
            Scene,
            Entity,
            Material,
            Shader,
            Animation
        };

        /// <summary>
        /// Pr�ft ob eine Dateiendung eine Vortex Engine Datei ist
        /// </summary>
        public static bool IsVortexFile(string extension)
        {
            switch (extension?.ToLowerInvariant())
            {
                case Project:
                case Scene:
                case Entity:
                case Material:
                case Shader:
                case Animation:
                    return true;
                default:
                    return false;
            }
        }
    }
}
