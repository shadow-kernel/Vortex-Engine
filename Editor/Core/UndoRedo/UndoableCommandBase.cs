using System;

namespace Editor.Core.UndoRedo
{
    /// <summary>
    /// Abstrakte Basisklasse für Undo/Redo Befehle.
    /// Bietet Standard-Implementierungen für häufig verwendete Funktionen.
    /// </summary>
    public abstract class UndoableCommandBase : IUndoableCommand
    {
        /// <summary>
        /// Eindeutiger Name des Befehls für Anzeigezwecke.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Zeitstempel wann der Befehl erstellt wurde.
        /// Wird für Merge-Logik verwendet.
        /// </summary>
        public DateTime CreatedAt { get; } = DateTime.Now;

        /// <summary>
        /// Führt den Befehl aus.
        /// </summary>
        public abstract void Execute();

        /// <summary>
        /// Macht den Befehl rückgängig.
        /// </summary>
        public abstract void Undo();

        /// <summary>
        /// Führt den Befehl erneut aus.
        /// Standard-Implementation ruft Execute() auf.
        /// </summary>
        public virtual void Redo()
        {
            Execute();
        }

        /// <summary>
        /// Gibt an, ob dieser Befehl mit dem vorherigen zusammengeführt werden kann.
        /// Standard: false - keine Zusammenführung.
        /// </summary>
        public virtual bool CanMergeWith(IUndoableCommand other)
        {
            return false;
        }

        /// <summary>
        /// Führt diesen Befehl mit einem anderen zusammen.
        /// Standard: Gibt diesen Befehl zurück (keine Zusammenführung).
        /// </summary>
        public virtual IUndoableCommand MergeWith(IUndoableCommand other)
        {
            return this;
        }
    }
}
