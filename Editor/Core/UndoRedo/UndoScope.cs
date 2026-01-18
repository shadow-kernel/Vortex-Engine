using System;
using Editor.Core.UndoRedo.Commands;

namespace Editor.Core.UndoRedo
{
    /// <summary>
    /// Ermöglicht das Gruppieren mehrerer Aktionen in einer Undo-Transaktion.
    /// Verwendung mit using-Statement für automatisches Commit/Rollback.
    /// </summary>
    /// <example>
    /// using (var scope = new UndoScope("Complex Operation"))
    /// {
    ///     scope.Execute(command1);
    ///     scope.Execute(command2);
    ///     scope.Commit(); // Optional - wird auch bei Dispose aufgerufen
    /// }
    /// </example>
    public class UndoScope : IDisposable
    {
        private readonly CompositeCommand _compositeCommand;
        private bool _isCommitted = false;
        private bool _isDisposed = false;

        /// <summary>
        /// Name der Transaktion.
        /// </summary>
        public string Name => _compositeCommand.Name;

        /// <summary>
        /// Anzahl der Befehle in dieser Transaktion.
        /// </summary>
        public int CommandCount => _compositeCommand.Count;

        /// <summary>
        /// Erstellt eine neue Undo-Transaktion.
        /// </summary>
        /// <param name="name">Name der zusammengesetzten Operation.</param>
        public UndoScope(string name)
        {
            _compositeCommand = new CompositeCommand(name);
        }

        /// <summary>
        /// Führt einen Befehl innerhalb dieser Transaktion aus.
        /// </summary>
        /// <param name="command">Der auszuführende Befehl.</param>
        public void Execute(IUndoableCommand command)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UndoScope));
            if (_isCommitted)
                throw new InvalidOperationException("UndoScope wurde bereits committed.");

            if (command == null)
                throw new ArgumentNullException(nameof(command));

            command.Execute();
            _compositeCommand.Add(command);
        }

        /// <summary>
        /// Fügt einen Befehl hinzu ohne ihn auszuführen.
        /// Nützlich wenn der Befehl bereits ausgeführt wurde.
        /// </summary>
        /// <param name="command">Der hinzuzufügende Befehl.</param>
        public void AddExecuted(IUndoableCommand command)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UndoScope));
            if (_isCommitted)
                throw new InvalidOperationException("UndoScope wurde bereits committed.");

            if (command == null)
                throw new ArgumentNullException(nameof(command));

            _compositeCommand.Add(command);
        }

        /// <summary>
        /// Schließt die Transaktion ab und registriert sie beim UndoRedoManager.
        /// </summary>
        public void Commit()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UndoScope));
            if (_isCommitted)
                return;

            _isCommitted = true;

            if (_compositeCommand.Count > 0)
            {
                // Nicht execute aufrufen, da Befehle bereits ausgeführt wurden
                UndoRedoManager.Instance.Execute(_compositeCommand, execute: false);
            }
        }

        /// <summary>
        /// Macht alle Befehle in dieser Transaktion rückgängig ohne sie zu registrieren.
        /// </summary>
        public void Rollback()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UndoScope));
            if (_isCommitted)
                throw new InvalidOperationException("Kann nicht rollback nach commit ausführen.");

            _compositeCommand.Undo();
            _isCommitted = true; // Verhindert erneutes Commit
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            // Automatisches Commit wenn noch nicht geschehen
            if (!_isCommitted && _compositeCommand.Count > 0)
            {
                Commit();
            }
        }
    }
}
