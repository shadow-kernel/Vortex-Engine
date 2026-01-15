using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Project.Data;
using Editor.Project.Exceptions;

namespace Editor.Project.Validation
{
    /// <summary>
    /// Validiert Projektdaten vor dem Erstellen oder Speichern
    /// </summary>
    public class ProjectValidator
    {
        /// <summary>
        /// Validiert ein Projekt vollständig
        /// </summary>
        /// <param name="project">Das zu validierende Projekt</param>
        /// <param name="existingProjects">Bereits existierende Projekte</param>
        /// <exception cref="ProjectValidationException">Wenn die Validierung fehlschlägt</exception>
        /// <exception cref="DuplicateProjectPathException">Wenn der Pfad bereits verwendet wird</exception>
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
        /// <param name="name">Der Projektname</param>
        /// <exception cref="ProjectValidationException">Wenn der Name ungültig ist</exception>
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
                throw new ProjectValidationException($"Der Projektname enthält ungültige Zeichen: {string.Join(", ", invalidChars.Where(c => name.Contains(c)))}");
            }
        }

        /// <summary>
        /// Validiert den Projektpfad
        /// </summary>
        /// <param name="path">Der Projektpfad</param>
        /// <exception cref="ProjectValidationException">Wenn der Pfad ungültig ist</exception>
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
            catch (Exception ex) when (ex is ArgumentException || ex is System.Security.SecurityException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new ProjectValidationException($"Der Projektpfad '{path}' ist ungültig.", ex.Message, ex);
            }
        }

        /// <summary>
        /// Prüft ob der Projektpfad eindeutig ist
        /// </summary>
        /// <param name="project">Das zu prüfende Projekt</param>
        /// <param name="existingProjects">Bereits existierende Projekte</param>
        /// <exception cref="DuplicateProjectPathException">Wenn der Pfad bereits verwendet wird</exception>
        public static void ValidateProjectPathUniqueness(ProjectRef project, Dictionary<Guid, ProjectRef> existingProjects)
        {
            if (existingProjects == null || existingProjects.Count == 0)
            {
                return;
            }

            string normalizedPath = NormalizePath(project.Path);

            foreach (var existingProject in existingProjects.Values)
            {
                // Überspringe das gleiche Projekt (beim Update)
                if (existingProject.Id == project.Id)
                {
                    continue;
                }

                string existingNormalizedPath = NormalizePath(existingProject.Path);

                if (string.Equals(normalizedPath, existingNormalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DuplicateProjectPathException(
                        project.Path, 
                        existingProject.Id, 
                        $"Ein Projekt mit dem Pfad '{project.Path}' existiert bereits. " +
                        $"Projektname: '{existingProject.Name}', ID: {existingProject.Id}"
                    );
                }
            }
        }

        /// <summary>
        /// Normalisiert einen Pfad für den Vergleich
        /// </summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

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
