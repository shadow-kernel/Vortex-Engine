using System;

namespace Editor.Core.Exceptions
{
    /// <summary>
    /// Exception wird geworfen, wenn versucht wird ein Projekt mit einem bereits existierenden Pfad zu erstellen
    /// </summary>
    public class DuplicateProjectPathException : ProjectException
    {
        public string ProjectPath { get; }
        public string ExistingProjectName { get; }

        public DuplicateProjectPathException(string path, string existingProjectName)
            : base($"Ein Projekt existiert bereits unter diesem Pfad: '{path}' (Projekt: {existingProjectName})")
        {
            ProjectPath = path;
            ExistingProjectName = existingProjectName;
        }
    }
}
