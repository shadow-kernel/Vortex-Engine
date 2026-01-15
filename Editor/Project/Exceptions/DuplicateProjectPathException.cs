using System;

namespace Editor.Project.Exceptions
{
    /// <summary>
    /// Exception wird geworfen, wenn ein Projekt mit einem bereits existierenden Pfad erstellt werden soll
    /// </summary>
    public class DuplicateProjectPathException : ProjectException
    {
        public string ProjectPath { get; }
        public Guid ExistingProjectId { get; }

        public DuplicateProjectPathException(string projectPath, Guid existingProjectId) 
            : base($"Ein Projekt mit dem Pfad '{projectPath}' existiert bereits (ID: {existingProjectId}).")
        {
            ProjectPath = projectPath;
            ExistingProjectId = existingProjectId;
        }

        public DuplicateProjectPathException(string projectPath, Guid existingProjectId, string message) 
            : base(message)
        {
            ProjectPath = projectPath;
            ExistingProjectId = existingProjectId;
        }

        public DuplicateProjectPathException(string projectPath, Guid existingProjectId, string message, Exception innerException) 
            : base(message, innerException)
        {
            ProjectPath = projectPath;
            ExistingProjectId = existingProjectId;
        }
    }
}
