using System;
using System.Collections.Generic;

namespace Editor.Core.UndoRedo.Commands
{
    /// <summary>
    /// Befehl der mehrere Befehle zu einem zusammenfasst.
    /// Alle Befehle werden als eine Einheit rückgängig gemacht.
    /// </summary>
    public class CompositeCommand : UndoableCommandBase
    {
        private readonly List<IUndoableCommand> _commands;
        private readonly string _name;

        public override string Name => _name;

        /// <summary>
        /// Gibt die Anzahl der enthaltenen Befehle zurück.
        /// </summary>
        public int Count => _commands.Count;

        /// <summary>
        /// Erstellt einen neuen CompositeCommand.
        /// </summary>
        /// <param name="name">Anzeigename des zusammengesetzten Befehls.</param>
        public CompositeCommand(string name)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _commands = new List<IUndoableCommand>();
        }

        /// <summary>
        /// Erstellt einen neuen CompositeCommand mit initialen Befehlen.
        /// </summary>
        /// <param name="name">Anzeigename des zusammengesetzten Befehls.</param>
        /// <param name="commands">Initiale Befehle.</param>
        public CompositeCommand(string name, IEnumerable<IUndoableCommand> commands)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _commands = new List<IUndoableCommand>(commands ?? throw new ArgumentNullException(nameof(commands)));
        }

        /// <summary>
        /// Fügt einen Befehl hinzu.
        /// </summary>
        public void Add(IUndoableCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            
            _commands.Add(command);
        }

        /// <summary>
        /// Fügt mehrere Befehle hinzu.
        /// </summary>
        public void AddRange(IEnumerable<IUndoableCommand> commands)
        {
            if (commands == null)
                throw new ArgumentNullException(nameof(commands));
            
            _commands.AddRange(commands);
        }

        public override void Execute()
        {
            foreach (var command in _commands)
            {
                command.Execute();
            }
        }

        public override void Undo()
        {
            // In umgekehrter Reihenfolge rückgängig machen
            for (int i = _commands.Count - 1; i >= 0; i--)
            {
                _commands[i].Undo();
            }
        }

        public override void Redo()
        {
            foreach (var command in _commands)
            {
                command.Redo();
            }
        }
    }
}
