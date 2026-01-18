using System;

namespace Editor.Core.UndoRedo.Commands
{
    /// <summary>
    /// Einfacher Befehl mit Execute und Undo Actions.
    /// Für schnelle Implementierungen ohne eigene Klasse.
    /// </summary>
    public class ActionCommand : UndoableCommandBase
    {
        private readonly Action _executeAction;
        private readonly Action _undoAction;
        private readonly string _name;

        public override string Name => _name;

        /// <summary>
        /// Erstellt einen neuen ActionCommand.
        /// </summary>
        /// <param name="name">Anzeigename des Befehls.</param>
        /// <param name="executeAction">Action für Execute/Redo.</param>
        /// <param name="undoAction">Action für Undo.</param>
        public ActionCommand(string name, Action executeAction, Action undoAction)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _executeAction = executeAction ?? throw new ArgumentNullException(nameof(executeAction));
            _undoAction = undoAction ?? throw new ArgumentNullException(nameof(undoAction));
        }

        public override void Execute()
        {
            _executeAction();
        }

        public override void Undo()
        {
            _undoAction();
        }
    }
}
