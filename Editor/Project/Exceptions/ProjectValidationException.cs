using System;

namespace Editor.Project.Exceptions
{
    /// <summary>
    /// Exception wird geworfen, wenn die Projektvalidierung fehlschlägt
    /// </summary>
    public class ProjectValidationException : ProjectException
    {
        public string ValidationError { get; }

        public ProjectValidationException(string validationError) 
            : base($"Projektvalidierung fehlgeschlagen: {validationError}")
        {
            ValidationError = validationError;
        }

        public ProjectValidationException(string validationError, string message) 
            : base(message)
        {
            ValidationError = validationError;
        }

        public ProjectValidationException(string validationError, string message, Exception innerException) 
            : base(message, innerException)
        {
            ValidationError = validationError;
        }
    }
}
