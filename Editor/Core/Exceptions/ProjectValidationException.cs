using System;

namespace Editor.Core.Exceptions
{
    /// <summary>
    /// Exception für Validierungsfehler bei Projektdaten
    /// </summary>
    public class ProjectValidationException : ProjectException
    {
        public string Details { get; }

        public ProjectValidationException(string message)
            : base(message)
        {
        }

        public ProjectValidationException(string message, string details)
            : base(message)
        {
            Details = details;
        }

        public ProjectValidationException(string message, string details, Exception innerException)
            : base(message, innerException)
        {
            Details = details;
        }
    }
}
