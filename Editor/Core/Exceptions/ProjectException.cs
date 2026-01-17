using System;

namespace Editor.Core.Exceptions
{
    /// <summary>
    /// Basis-Exception für alle projektbezogenen Fehler
    /// </summary>
    public class ProjectException : Exception
    {
        public ProjectException() : base()
        {
        }

        public ProjectException(string message) : base(message)
        {
        }

        public ProjectException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
