using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Data;
using Editor.Core.Exceptions;

namespace Editor.Core.Validation
{
    /// <summary>
    /// Validiert Projektdaten vor dem Erstellen oder Speichern
    /// </summary>
    public static class ProjectValidator
    {
        /// <summary>
        /// Validiert ein Projekt vollständig
        /// </summary>
        public static void ValidateProject(ProjectRef project, Dictionary<Guid, ProjectRef> existingProjects)
        {
            if (project == null)
            {
                throw new ProjectValidationException("Das Projekt darf nicht null sein.");
            }

            ValidateProjectName(project.Name);
            ValidateProjectPath(project.Path);
            ValidateProjectPathUniqueness(project, existingProjects);
        }

        /// <summary>
        /// Validiert den Projektnamen
        /// </summary>
        public static void ValidateProjectName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ProjectValidationException("Der Projektname darf nicht leer sein.");
            }

            if (name.Length > 255)
            {
                throw new ProjectValidationException("Der Projektname darf maximal 255 Zeichen lang sein.");
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            if (name.Any(c => invalidChars.Contains(c)))
            {
                var foundInvalid = invalidChars.Where(c => name.Contains(c));
                throw new ProjectValidationException(
                    $"Der Projektname enthält ungültige Zeichen: {string.Join(", ", foundInvalid)}");
            }
        }

        /// <summary>
        /// Validiert den Projektpfad
        /// </summary>
        public static void ValidateProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ProjectValidationException("Der Projektpfad darf nicht leer sein.");
            }

            try
            {
                string fullPath = Path.GetFullPath(path);

                if (!Path.IsPathRooted(fullPath))
                {
                    throw new ProjectValidationException("Der Projektpfad muss ein absoluter Pfad sein.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException || 
                                       ex is System.Security.SecurityException || 
                                       ex is NotSupportedException || 
                                       ex is PathTooLongException)
            {
                throw new ProjectValidationException($"Der Projektpfad '{path}' ist ungültig.", ex.Message, ex);
            }
        }

        /// <summary>
        /// Prüft ob der Projektpfad eindeutig ist
        /// </summary>
        public static void ValidateProjectPathUniqueness(ProjectRef project, Dictionary<Guid, ProjectRef> existingProjects)
        {
            if (existingProjects == null || existingProjects.Count == 0)
            {
                return;
            }

            string normalizedPath = NormalizePath(project.Path);

            foreach (var existingProject in existingProjects.Values)
            {
                // Überspringe das Projekt selbst beim Update
                if (existingProject.Id == project.Id)
                    continue;

                if (string.Equals(NormalizePath(existingProject.Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DuplicateProjectPathException(project.Path, existingProject.Name);
                }
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }
    }
}
