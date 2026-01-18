using System;

namespace Editor.Core.UndoRedo.Commands
{
    /// <summary>
    /// Generischer Befehl für Property-Änderungen.
    /// Speichert den alten und neuen Wert einer Eigenschaft.
    /// </summary>
    /// <typeparam name="T">Typ des Property-Werts.</typeparam>
    public class PropertyChangeCommand<T> : UndoableCommandBase
    {
        private readonly Action<T> _setter;
        private readonly T _oldValue;
        private T _newValue;
        private readonly string _propertyName;
        private readonly object _target;

        public override string Name => $"Change {_propertyName}";

        /// <summary>
        /// Erstellt einen neuen PropertyChangeCommand.
        /// </summary>
        /// <param name="target">Das Zielobjekt (für Merge-Vergleich).</param>
        /// <param name="propertyName">Name der Eigenschaft.</param>
        /// <param name="setter">Action zum Setzen des Werts.</param>
        /// <param name="oldValue">Alter Wert.</param>
        /// <param name="newValue">Neuer Wert.</param>
        public PropertyChangeCommand(object target, string propertyName, Action<T> setter, T oldValue, T newValue)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _propertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
            _setter = setter ?? throw new ArgumentNullException(nameof(setter));
            _oldValue = oldValue;
            _newValue = newValue;
        }

        public override void Execute()
        {
            _setter(_newValue);
        }

        public override void Undo()
        {
            _setter(_oldValue);
        }

        public override bool CanMergeWith(IUndoableCommand other)
        {
            if (other is PropertyChangeCommand<T> otherCmd)
            {
                // Nur zusammenführen wenn gleiches Ziel und gleiche Eigenschaft
                if (otherCmd._target == _target && otherCmd._propertyName == _propertyName)
                {
                    // Nur wenn innerhalb des Zeitfensters
                    var timeDiff = (CreatedAt - otherCmd.CreatedAt).TotalMilliseconds;
                    return timeDiff <= UndoRedoManager.Instance.MergeTimeWindowMs;
                }
            }
            return false;
        }

        public override IUndoableCommand MergeWith(IUndoableCommand other)
        {
            if (other is PropertyChangeCommand<T> otherCmd)
            {
                // Behalte den alten Wert vom vorherigen Command und den neuen Wert von diesem
                return new PropertyChangeCommand<T>(_target, _propertyName, _setter, otherCmd._oldValue, _newValue);
            }
            return this;
        }
    }
}
