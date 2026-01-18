using System;
using Editor.Core.UndoRedo.Commands;

namespace Editor.Core.UndoRedo
{
    /// <summary>
    /// Extension-Methoden für einfache Undo/Redo Integration.
    /// </summary>
    public static class UndoRedoExtensions
    {
        /// <summary>
        /// Führt eine Aktion mit Undo-Support aus.
        /// </summary>
        /// <param name="manager">Der UndoRedoManager.</param>
        /// <param name="name">Name der Aktion.</param>
        /// <param name="doAction">Action für Execute/Redo.</param>
        /// <param name="undoAction">Action für Undo.</param>
        public static void ExecuteAction(this UndoRedoManager manager, string name, Action doAction, Action undoAction)
        {
            var command = new ActionCommand(name, doAction, undoAction);
            manager.Execute(command);
        }

        /// <summary>
        /// Ändert eine Property mit Undo-Support.
        /// </summary>
        /// <typeparam name="T">Typ der Property.</typeparam>
        /// <param name="manager">Der UndoRedoManager.</param>
        /// <param name="target">Das Zielobjekt.</param>
        /// <param name="propertyName">Name der Property.</param>
        /// <param name="setter">Setter-Action.</param>
        /// <param name="oldValue">Alter Wert.</param>
        /// <param name="newValue">Neuer Wert.</param>
        public static void ChangeProperty<T>(this UndoRedoManager manager, object target, string propertyName, 
            Action<T> setter, T oldValue, T newValue)
        {
            if (Equals(oldValue, newValue))
                return;

            var command = new PropertyChangeCommand<T>(target, propertyName, setter, oldValue, newValue);
            manager.Execute(command);
        }

        /// <summary>
        /// Fügt ein Element zu einer Liste mit Undo-Support hinzu.
        /// </summary>
        /// <typeparam name="T">Typ des Elements.</typeparam>
        /// <param name="manager">Der UndoRedoManager.</param>
        /// <param name="collection">Die Ziel-Collection.</param>
        /// <param name="item">Das hinzuzufügende Element.</param>
        /// <param name="collectionName">Name der Collection.</param>
        public static void AddToCollection<T>(this UndoRedoManager manager, System.Collections.Generic.IList<T> collection, 
            T item, string collectionName = "Collection")
        {
            var command = new CollectionAddCommand<T>(collection, item, collectionName);
            manager.Execute(command);
        }

        /// <summary>
        /// Entfernt ein Element aus einer Liste mit Undo-Support.
        /// </summary>
        /// <typeparam name="T">Typ des Elements.</typeparam>
        /// <param name="manager">Der UndoRedoManager.</param>
        /// <param name="collection">Die Ziel-Collection.</param>
        /// <param name="item">Das zu entfernende Element.</param>
        /// <param name="collectionName">Name der Collection.</param>
        public static void RemoveFromCollection<T>(this UndoRedoManager manager, System.Collections.Generic.IList<T> collection, 
            T item, string collectionName = "Collection")
        {
            var command = new CollectionRemoveCommand<T>(collection, item, collectionName);
            manager.Execute(command);
        }

        /// <summary>
        /// Verschiebt ein Element in einer Liste mit Undo-Support.
        /// </summary>
        /// <typeparam name="T">Typ des Elements.</typeparam>
        /// <param name="manager">Der UndoRedoManager.</param>
        /// <param name="collection">Die Ziel-Collection.</param>
        /// <param name="oldIndex">Alter Index.</param>
        /// <param name="newIndex">Neuer Index.</param>
        /// <param name="collectionName">Name der Collection.</param>
        public static void MoveInCollection<T>(this UndoRedoManager manager, System.Collections.Generic.IList<T> collection, 
            int oldIndex, int newIndex, string collectionName = "Collection")
        {
            var command = new CollectionMoveCommand<T>(collection, oldIndex, newIndex, collectionName);
            manager.Execute(command);
        }
    }
}
