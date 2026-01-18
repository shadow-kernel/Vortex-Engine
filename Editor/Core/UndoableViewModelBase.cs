using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;

namespace Editor.Core
{
    /// <summary>
    /// ViewModelBase mit integriertem Undo/Redo Support.
    /// Property-Änderungen werden automatisch im UndoRedoManager registriert.
    /// </summary>
    [DataContract]
    public abstract class UndoableViewModelBase : INotifyPropertyChanged
    {
        private bool _suppressUndoTracking = false;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Gibt an, ob Undo-Tracking für dieses ViewModel aktiviert ist.
        /// Standard: true.
        /// </summary>
        protected virtual bool EnableUndoTracking => true;

        /// <summary>
        /// Löst das PropertyChanged-Event aus.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Setzt eine Property ohne Undo-Tracking.
        /// Nützlich für initiale Werte oder interne Updates.
        /// </summary>
        protected bool SetPropertyWithoutUndo<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Setzt eine Property mit automatischem Undo-Tracking.
        /// Verwendet Reflection um das Backing-Field zu finden.
        /// </summary>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
                return false;

            if (!EnableUndoTracking || _suppressUndoTracking)
            {
                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            T oldValue = field;

            var command = new PropertyChangeCommand<T>(
                this,
                propertyName,
                newVal =>
                {
                    _suppressUndoTracking = true;
                    try
                    {
                        SetFieldValue(propertyName, newVal);
                        OnPropertyChanged(propertyName);
                    }
                    finally
                    {
                        _suppressUndoTracking = false;
                    }
                },
                oldValue,
                value
            );

            field = value;
            OnPropertyChanged(propertyName);

            // Registriere bei UndoRedoManager (ohne erneut auszuführen)
            UndoRedoManager.Instance.Execute(command, execute: false);

            return true;
        }

        /// <summary>
        /// Setzt eine Property mit Undo-Tracking und benutzerdefiniertem Setter.
        /// Der Setter wird sowohl bei Execute/Redo als auch bei Undo aufgerufen.
        /// </summary>
        protected bool SetPropertyWithSetter<T>(T oldValue, T newValue, Action<T> setter, [CallerMemberName] string propertyName = null)
        {
            if (Equals(oldValue, newValue))
                return false;

            if (!EnableUndoTracking || _suppressUndoTracking)
            {
                setter?.Invoke(newValue);
                OnPropertyChanged(propertyName);
                return true;
            }

            var command = new PropertyChangeCommand<T>(
                this,
                propertyName,
                newVal =>
                {
                    _suppressUndoTracking = true;
                    try
                    {
                        setter?.Invoke(newVal);
                        OnPropertyChanged(propertyName);
                    }
                    finally
                    {
                        _suppressUndoTracking = false;
                    }
                },
                oldValue,
                newValue
            );

            setter?.Invoke(newValue);
            OnPropertyChanged(propertyName);

            UndoRedoManager.Instance.Execute(command, execute: false);

            return true;
        }

        /// <summary>
        /// Führt eine Aktion innerhalb eines Undo-Tracking-freien Bereichs aus.
        /// </summary>
        protected void SuppressUndo(Action action)
        {
            _suppressUndoTracking = true;
            try
            {
                action?.Invoke();
            }
            finally
            {
                _suppressUndoTracking = false;
            }
        }

        /// <summary>
        /// Setzt den Wert eines Feldes per Reflection.
        /// Sucht nach Backing-Fields mit den Konventionen _propertyName oder propertyName.
        /// </summary>
        protected virtual void SetFieldValue<T>(string propertyName, T value)
        {
            var type = GetType();
            
            // Versuche _propertyName (z.B. _name für Name)
            var fieldName = "_" + char.ToLower(propertyName[0]) + propertyName.Substring(1);
            var field = type.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field == null)
            {
                // Versuche propertyName (z.B. name für Name)
                fieldName = char.ToLower(propertyName[0]) + propertyName.Substring(1);
                field = type.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }

            if (field != null)
            {
                field.SetValue(this, value);
            }
        }
    }
}
