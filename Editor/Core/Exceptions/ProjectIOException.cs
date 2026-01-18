using System;

namespace Editor.Core.Exceptions
{
    /// <summary>
    /// Exception wird geworfen, wenn ein Ein-/Ausgabe-Fehler beim Arbeiten mit Projekten auftritt
    /// </summary>
    public class ProjectIOException : ProjectException
    {
        public string FilePath { get; }

        public ProjectIOException(string filePath, string message)
            : base($"Fehler beim Zugriff auf '{filePath}': {message}")
        {
            FilePath = filePath;
        }

        public ProjectIOException(string filePath, string message, Exception innerException)
            : base($"Fehler beim Zugriff auf '{filePath}': {message}", innerException)
        {
            FilePath = filePath;
        }
    }
}
