using System;

namespace Editor.Core.UndoRedo
{
    /// <summary>
    /// Interface für alle rückgängig machbaren Befehle im Editor.
    /// Implementiert das Command Pattern für Undo/Redo Funktionalität.
    /// </summary>
    public interface IUndoableCommand
    {
        /// <summary>
        /// Eindeutiger Name des Befehls für Anzeigezwecke.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Führt den Befehl aus.
        /// </summary>
        void Execute();

        /// <summary>
        /// Macht den Befehl rückgängig.
        /// </summary>
        void Undo();

        /// <summary>
        /// Führt den Befehl erneut aus (nach einem Undo).
        /// Standard-Implementation ruft Execute() auf.
        /// </summary>
        void Redo();

        /// <summary>
        /// Gibt an, ob dieser Befehl mit dem vorherigen zusammengeführt werden kann.
        /// Nützlich für kontinuierliche Änderungen wie Slider-Bewegungen.
        /// </summary>
        bool CanMergeWith(IUndoableCommand other);

        /// <summary>
        /// Führt diesen Befehl mit einem anderen zusammen.
        /// Gibt den zusammengeführten Befehl zurück.
        /// </summary>
        IUndoableCommand MergeWith(IUndoableCommand other);
    }
}
