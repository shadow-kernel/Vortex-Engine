using System;
using System.Collections.Generic;
using System.ComponentModel;
#if !VORTEX_CORE
using System.Media;
#endif
using System.Runtime.CompilerServices;

namespace Editor.Core.UndoRedo
{
    /// <summary>
    /// Zentraler Manager für Undo/Redo Operationen.
    /// Verwendet das Singleton-Pattern für globalen Zugriff.
    /// </summary>
    public class UndoRedoManager : INotifyPropertyChanged
    {
        private static UndoRedoManager _instance;
        private static readonly object _lock = new object();

        /// <summary>
        /// Singleton-Instanz des UndoRedoManagers.
        /// </summary>
        public static UndoRedoManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new UndoRedoManager();
                        }
                    }
                }
                return _instance;
            }
        }

        private readonly Stack<IUndoableCommand> _undoStack = new Stack<IUndoableCommand>();
        private readonly Stack<IUndoableCommand> _redoStack = new Stack<IUndoableCommand>();
        private bool _isExecuting = false;

        /// <summary>
        /// Aktiviert oder deaktiviert den Sound bei Undo/Redo-Limit.
        /// Standard: true.
        /// </summary>
        public bool EnableLimitSound { get; set; } = true;

        /// <summary>
        /// Maximale Anzahl von Befehlen im Undo-Stack.
        /// Standard: 300 Befehle (agent sessions — a Claude tool call is one step each — must not silently drop history).
        /// </summary>
        public int MaxUndoStackSize { get; set; } = 300;

        /// <summary>
        /// Zeitfenster in Millisekunden für das Zusammenführen von Befehlen.
        /// Standard: 500ms.
        /// </summary>
        public int MergeTimeWindowMs { get; set; } = 500;

        /// <summary>
        /// Gibt an, ob ein Undo möglich ist.
        /// </summary>
        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>
        /// Gibt an, ob ein Redo möglich ist.
        /// </summary>
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// Gibt den Namen des nächsten Undo-Befehls zurück.
        /// </summary>
        public string UndoName => CanUndo ? _undoStack.Peek().Name : string.Empty;

        /// <summary>
        /// Gibt den Namen des nächsten Redo-Befehls zurück.
        /// </summary>
        public string RedoName => CanRedo ? _redoStack.Peek().Name : string.Empty;

        /// <summary>
        /// Anzahl der Befehle im Undo-Stack.
        /// </summary>
        public int UndoCount => _undoStack.Count;

        /// <summary>
        /// Anzahl der Befehle im Redo-Stack.
        /// </summary>
        public int RedoCount => _redoStack.Count;

        /// <summary>
        /// Event wird ausgelöst, wenn sich der Undo/Redo-Status ändert.
        /// </summary>
        public event EventHandler StateChanged;

        /// <summary>
        /// Event wird ausgelöst, wenn ein Befehl ausgeführt wurde.
        /// </summary>
        public event EventHandler<CommandExecutedEventArgs> CommandExecuted;

        /// <summary>
        /// Event wird ausgelöst, wenn Undo/Redo am Limit ist (keine weiteren Aktionen möglich).
        /// </summary>
        public event EventHandler<UndoRedoLimitEventArgs> LimitReached;

        public event PropertyChangedEventHandler PropertyChanged;

        private UndoRedoManager() { }

        /// <summary>
        /// Führt einen Befehl aus und fügt ihn zum Undo-Stack hinzu.
        /// </summary>
        /// <param name="command">Der auszuführende Befehl.</param>
        /// <param name="execute">Wenn true, wird Execute() aufgerufen. Standard: true.</param>
        public void Execute(IUndoableCommand command, bool execute = true)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (_isExecuting)
                return;

            if (_group != null)
            {
                // an open group (BeginGroup) collects the command; the whole group becomes one undo step at EndGroup
                try
                {
                    _isExecuting = true;
                    if (execute)
                    {
                        command.Execute();
                    }
                }
                finally
                {
                    _isExecuting = false;
                }
                _group.Add(command);
                return;
            }

            try
            {
                _isExecuting = true;

                if (execute)
                {
                    command.Execute();
                }

                // Versuche mit dem letzten Befehl zusammenzuführen
                if (_undoStack.Count > 0)
                {
                    var lastCommand = _undoStack.Peek();
                    if (command.CanMergeWith(lastCommand))
                    {
                        _undoStack.Pop();
                        command = command.MergeWith(lastCommand);
                    }
                }

                _undoStack.Push(command);

                // Redo-Stack leeren, da neue Aktion ausgeführt wurde
                _redoStack.Clear();

                // Stack-Größe begrenzen
                TrimUndoStack();

                OnStateChanged();
                OnCommandExecuted(command, CommandExecutionType.Execute);
            }
            finally
            {
                _isExecuting = false;
            }
        }

        /// <summary>
        /// Macht den letzten Befehl rückgängig.
        /// </summary>
        /// <returns>True wenn ein Befehl rückgängig gemacht wurde.</returns>
        public bool Undo()
        {
            if (_groupDepth > 0)
                return false;

            if (!CanUndo || _isExecuting)
            {
                // Am Limit - Sound abspielen
                PlayLimitSound(true);
                OnLimitReached(true);
                return false;
            }

            try
            {
                _isExecuting = true;

                var command = _undoStack.Pop();
                command.Undo();
                _redoStack.Push(command);

                OnStateChanged();
                OnCommandExecuted(command, CommandExecutionType.Undo);

                return true;
            }
            finally
            {
                _isExecuting = false;
            }
        }

        /// <summary>
        /// Führt den letzten rückgängig gemachten Befehl erneut aus.
        /// </summary>
        /// <returns>True wenn ein Befehl wiederholt wurde.</returns>
        public bool Redo()
        {
            if (_groupDepth > 0)
                return false;

            if (!CanRedo || _isExecuting)
            {
                // Am Limit - Sound abspielen
                PlayLimitSound(false);
                OnLimitReached(false);
                return false;
            }

            try
            {
                _isExecuting = true;

                var command = _redoStack.Pop();
                command.Redo();
                _undoStack.Push(command);

                OnStateChanged();
                OnCommandExecuted(command, CommandExecutionType.Redo);

                return true;
            }
            finally
            {
                _isExecuting = false;
            }
        }

        /// <summary>
        /// Macht mehrere Befehle auf einmal rückgängig.
        /// </summary>
        /// <param name="count">Anzahl der rückgängig zu machenden Befehle.</param>
        public void UndoMultiple(int count)
        {
            for (int i = 0; i < count && CanUndo; i++)
            {
                Undo();
            }
        }

        /// <summary>
        /// Führt mehrere rückgängig gemachte Befehle erneut aus.
        /// </summary>
        /// <param name="count">Anzahl der wiederherzustellenden Befehle.</param>
        public void RedoMultiple(int count)
        {
            for (int i = 0; i < count && CanRedo; i++)
            {
                Redo();
            }
        }

        // ------------------------------------------------------------------ groups

        private Commands.CompositeCommand _group;
        private int _groupDepth;

        /// <summary>True while <see cref="BeginGroup"/> collects commands into one undo step.</summary>
        public bool IsGrouping => _groupDepth > 0;

        /// <summary>
        /// Collects every command executed until the matching <see cref="EndGroup"/> into ONE undo step named
        /// <paramref name="name"/> — a Claude tool call or a macro that creates hundreds of entities reverts with a
        /// single Undo. Groups nest; only the outermost one reaches the stack. Undo/Redo are refused while a group is open.
        /// </summary>
        public void BeginGroup(string name)
        {
            if (_groupDepth++ == 0)
                _group = new Commands.CompositeCommand(string.IsNullOrEmpty(name) ? "Edit" : name);
        }

        /// <summary>Renames the open group (the operation knows what it did only at the end).</summary>
        public void RenameGroup(string name)
        {
            if (_group != null)
                _group.Rename(name);
        }

        /// <summary>
        /// Closes the group opened by <see cref="BeginGroup"/>. The outermost group becomes one undo step (none when no
        /// command ran); with <paramref name="commit"/> false its commands are undone instead (an operation that failed
        /// half-way leaves no trace). Returns the pushed step, or null.
        /// </summary>
        public IUndoableCommand EndGroup(bool commit = true)
        {
            if (_groupDepth == 0)
                return null;
            if (--_groupDepth > 0)
                return null;

            var group = _group;
            _group = null;
            if (group == null || group.Count == 0)
                return null;

            if (!commit)
            {
                try
                {
                    _isExecuting = true;
                    group.Undo();
                }
                catch
                {
                    // a half-undone group is still better than a stuck manager
                }
                finally
                {
                    _isExecuting = false;
                }
                OnStateChanged();
                return null;
            }

            _undoStack.Push(group);
            _redoStack.Clear();
            TrimUndoStack();
            OnStateChanged();
            OnCommandExecuted(group, CommandExecutionType.Execute);
            return group;
        }

        /// <summary>
        /// Leert beide Stacks.
        /// </summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            OnStateChanged();
        }

        /// <summary>
        /// Gibt eine Liste aller Undo-Befehle zurück (neueste zuerst).
        /// </summary>
        public IReadOnlyList<IUndoableCommand> GetUndoHistory()
        {
            return new List<IUndoableCommand>(_undoStack);
        }

        /// <summary>
        /// Gibt eine Liste aller Redo-Befehle zurück (neueste zuerst).
        /// </summary>
        public IReadOnlyList<IUndoableCommand> GetRedoHistory()
        {
            return new List<IUndoableCommand>(_redoStack);
        }

        private void TrimUndoStack()
        {
            if (_undoStack.Count <= MaxUndoStackSize)
                return;

            var tempStack = new Stack<IUndoableCommand>();
            for (int i = 0; i < MaxUndoStackSize; i++)
            {
                tempStack.Push(_undoStack.Pop());
            }

            _undoStack.Clear();

            while (tempStack.Count > 0)
            {
                _undoStack.Push(tempStack.Pop());
            }
        }

        private void OnStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(UndoName));
            OnPropertyChanged(nameof(RedoName));
            OnPropertyChanged(nameof(UndoCount));
            OnPropertyChanged(nameof(RedoCount));
        }

        private void OnCommandExecuted(IUndoableCommand command, CommandExecutionType executionType)
        {
            CommandExecuted?.Invoke(this, new CommandExecutedEventArgs(command, executionType));
        }

        private void OnLimitReached(bool isUndo)
        {
            LimitReached?.Invoke(this, new UndoRedoLimitEventArgs(isUndo));
        }

        /// <summary>
        /// Spielt einen Sound ab, wenn Undo/Redo am Limit ist.
        /// </summary>
        private void PlayLimitSound(bool isUndo)
        {
            if (!EnableLimitSound)
                return;

            try
            {
#if VORTEX_CORE
                Editor.Core.Threading.HostShell.PlayAlertSound();
#else
                // Windows System-Sound für "Hinweis" - ähnlich wie Windows Explorer bei Limit
                SystemSounds.Exclamation.Play();
#endif
            }
            catch
            {
                // Sound-Fehler ignorieren
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Typ der Befehlsausführung.
    /// </summary>
    public enum CommandExecutionType
    {
        Execute,
        Undo,
        Redo
    }

    /// <summary>
        /// Event-Argumente für ausgeführte Befehle.
        /// </summary>
        public class CommandExecutedEventArgs : EventArgs
        {
            public IUndoableCommand Command { get; }
            public CommandExecutionType ExecutionType { get; }

            public CommandExecutedEventArgs(IUndoableCommand command, CommandExecutionType executionType)
            {
                Command = command;
                ExecutionType = executionType;
            }
        }

        /// <summary>
        /// Event-Argumente wenn Undo/Redo am Limit ist.
        /// </summary>
        public class UndoRedoLimitEventArgs : EventArgs
        {
            /// <summary>
            /// True wenn Undo am Limit ist, False wenn Redo am Limit ist.
            /// </summary>
            public bool IsUndo { get; }

            public UndoRedoLimitEventArgs(bool isUndo)
            {
                IsUndo = isUndo;
            }
        }
    }
