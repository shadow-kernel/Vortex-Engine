using System;
using System.Collections;
using System.Collections.Generic;

namespace Editor.Core.UndoRedo.Commands
{
    /// <summary>
    /// Befehl für das Hinzufügen eines Elements zu einer Collection.
    /// </summary>
    /// <typeparam name="T">Typ des Elements.</typeparam>
    public class CollectionAddCommand<T> : UndoableCommandBase
    {
        private readonly IList<T> _collection;
        private readonly T _item;
        private readonly string _collectionName;
        private int _insertedIndex = -1;

        public override string Name => $"Add to {_collectionName}";

        /// <summary>
        /// Erstellt einen neuen CollectionAddCommand.
        /// </summary>
        /// <param name="collection">Die Ziel-Collection.</param>
        /// <param name="item">Das hinzuzufügende Element.</param>
        /// <param name="collectionName">Name der Collection für Anzeige.</param>
        public CollectionAddCommand(IList<T> collection, T item, string collectionName = "Collection")
        {
            _collection = collection ?? throw new ArgumentNullException(nameof(collection));
            _item = item;
            _collectionName = collectionName;
        }

        public override void Execute()
        {
            _insertedIndex = _collection.Count;
            _collection.Add(_item);
        }

        public override void Undo()
        {
            if (_insertedIndex >= 0 && _insertedIndex < _collection.Count)
            {
                _collection.RemoveAt(_insertedIndex);
            }
            else
            {
                _collection.Remove(_item);
            }
        }
    }

    /// <summary>
    /// Befehl für das Entfernen eines Elements aus einer Collection.
    /// </summary>
    /// <typeparam name="T">Typ des Elements.</typeparam>
    public class CollectionRemoveCommand<T> : UndoableCommandBase
    {
        private readonly IList<T> _collection;
        private readonly T _item;
        private readonly string _collectionName;
        private int _removedIndex = -1;

        public override string Name => $"Remove from {_collectionName}";

        /// <summary>
        /// Erstellt einen neuen CollectionRemoveCommand.
        /// </summary>
        /// <param name="collection">Die Ziel-Collection.</param>
        /// <param name="item">Das zu entfernende Element.</param>
        /// <param name="collectionName">Name der Collection für Anzeige.</param>
        public CollectionRemoveCommand(IList<T> collection, T item, string collectionName = "Collection")
        {
            _collection = collection ?? throw new ArgumentNullException(nameof(collection));
            _item = item;
            _collectionName = collectionName;
        }

        public override void Execute()
        {
            _removedIndex = _collection.IndexOf(_item);
            if (_removedIndex >= 0)
            {
                _collection.RemoveAt(_removedIndex);
            }
        }

        public override void Undo()
        {
            if (_removedIndex >= 0)
            {
                _collection.Insert(_removedIndex, _item);
            }
            else
            {
                _collection.Add(_item);
            }
        }
    }

    /// <summary>
    /// Befehl für das Verschieben eines Elements in einer Collection.
    /// </summary>
    /// <typeparam name="T">Typ des Elements.</typeparam>
    public class CollectionMoveCommand<T> : UndoableCommandBase
    {
        private readonly IList<T> _collection;
        private readonly int _oldIndex;
        private readonly int _newIndex;
        private readonly string _collectionName;

        public override string Name => $"Move in {_collectionName}";

        /// <summary>
        /// Erstellt einen neuen CollectionMoveCommand.
        /// </summary>
        /// <param name="collection">Die Ziel-Collection.</param>
        /// <param name="oldIndex">Alter Index des Elements.</param>
        /// <param name="newIndex">Neuer Index des Elements.</param>
        /// <param name="collectionName">Name der Collection für Anzeige.</param>
        public CollectionMoveCommand(IList<T> collection, int oldIndex, int newIndex, string collectionName = "Collection")
        {
            _collection = collection ?? throw new ArgumentNullException(nameof(collection));
            _oldIndex = oldIndex;
            _newIndex = newIndex;
            _collectionName = collectionName;
        }

        public override void Execute()
        {
            MoveItem(_oldIndex, _newIndex);
        }

        public override void Undo()
        {
            MoveItem(_newIndex, _oldIndex);
        }

        private void MoveItem(int from, int to)
        {
            if (from < 0 || from >= _collection.Count)
                return;
            if (to < 0 || to >= _collection.Count)
                return;

            var item = _collection[from];
            _collection.RemoveAt(from);
            _collection.Insert(to, item);
        }
    }
}
