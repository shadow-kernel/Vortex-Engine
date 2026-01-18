using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Serialization;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Service für Clipboard-Operationen auf GameEntities.
    /// Unterstützt Cut, Copy, Paste mit Undo/Redo.
    /// </summary>
    public class EntityClipboardService
    {
        private static EntityClipboardService _instance;
        private static readonly object _lock = new object();

        public static EntityClipboardService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new EntityClipboardService();
                        }
                    }
                }
                return _instance;
            }
        }

        private List<EntityClipboardData> _clipboardData = new List<EntityClipboardData>();
        private bool _isCutOperation = false;
        private List<GameEntity> _cutEntities = new List<GameEntity>();

        /// <summary>
        /// Gibt an, ob Entities im Clipboard sind
        /// </summary>
        public bool HasContent => _clipboardData.Count > 0;

        /// <summary>
        /// Gibt an, ob es sich um eine Cut-Operation handelt
        /// </summary>
        public bool IsCut => _isCutOperation;

        /// <summary>
        /// Anzahl der Entities im Clipboard
        /// </summary>
        public int Count => _clipboardData.Count;

        private EntityClipboardService() { }

        /// <summary>
        /// Kopiert Entities in das Clipboard
        /// </summary>
        public void Copy(IEnumerable<GameEntity> entities)
        {
            if (entities == null || !entities.Any()) return;

            _clipboardData.Clear();
            _isCutOperation = false;
            _cutEntities.Clear();

            foreach (var entity in entities)
            {
                var data = SerializeEntity(entity);
                if (data != null)
                {
                    _clipboardData.Add(data);
                }
            }
        }

        /// <summary>
        /// Schneidet Entities aus (markiert zum späteren Löschen beim Paste)
        /// </summary>
        public void Cut(IEnumerable<GameEntity> entities)
        {
            if (entities == null || !entities.Any()) return;

            _clipboardData.Clear();
            _isCutOperation = true;
            _cutEntities = entities.ToList();

            foreach (var entity in entities)
            {
                var data = SerializeEntity(entity);
                if (data != null)
                {
                    _clipboardData.Add(data);
                }
            }
        }

        /// <summary>
        /// Fügt Entities aus dem Clipboard in eine Szene ein (mit Undo/Redo)
        /// </summary>
        public List<GameEntity> Paste(Data.Scene targetScene, GameEntity parentEntity = null)
        {
            if (targetScene == null || !HasContent) return new List<GameEntity>();

            var pastedEntities = new List<GameEntity>();
            var command = new PasteEntitiesCommand(
                targetScene,
                parentEntity,
                _clipboardData,
                _isCutOperation ? _cutEntities : null,
                pastedEntities);

            UndoRedoManager.Instance.Execute(command);

            // Nach Cut+Paste, lösche die Cut-Markierung
            if (_isCutOperation)
            {
                _isCutOperation = false;
                _cutEntities.Clear();
            }

            return pastedEntities;
        }

        /// <summary>
        /// Serialisiert eine Entity für das Clipboard
        /// </summary>
        private EntityClipboardData SerializeEntity(GameEntity entity)
        {
            try
            {
                // Verwende Binary-Serialisierung für Deep Copy
                var bytes = DataSerializer.ToBinary(entity);
                return new EntityClipboardData
                {
                    SerializedData = bytes,
                    OriginalName = entity.Name,
                    OriginalId = entity.Id
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Deserialisiert eine Entity aus dem Clipboard
        /// </summary>
        public GameEntity DeserializeEntity(EntityClipboardData data, Data.Scene targetScene)
        {
            try
            {
                var entity = DataSerializer.FromBinary<GameEntity>(data.SerializedData);
                if (entity != null)
                {
                    // Neue IDs vergeben
                    entity.RegenerateIds();
                    entity.Scene = targetScene;
                }
                return entity;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Leert das Clipboard
        /// </summary>
        public void Clear()
        {
            _clipboardData.Clear();
            _isCutOperation = false;
            _cutEntities.Clear();
        }
    }

    /// <summary>
    /// Daten für eine Entity im Clipboard
    /// </summary>
    public class EntityClipboardData
    {
        public byte[] SerializedData { get; set; }
        public string OriginalName { get; set; }
        public Guid OriginalId { get; set; }
    }

    /// <summary>
    /// Undo/Redo Command für Paste-Operationen
    /// </summary>
    public class PasteEntitiesCommand : UndoableCommandBase
    {
        private readonly Data.Scene _targetScene;
        private readonly GameEntity _parentEntity;
        private readonly List<EntityClipboardData> _clipboardData;
        private readonly List<GameEntity> _cutEntities;
        private readonly List<GameEntity> _pastedEntities;
        private readonly List<CutEntityInfo> _cutEntityInfos = new List<CutEntityInfo>();

        public override string Name => _cutEntities?.Count > 0 ? "Cut & Paste Entities" : "Paste Entities";

        public PasteEntitiesCommand(
            Data.Scene targetScene,
            GameEntity parentEntity,
            List<EntityClipboardData> clipboardData,
            List<GameEntity> cutEntities,
            List<GameEntity> pastedEntitiesOutput)
        {
            _targetScene = targetScene;
            _parentEntity = parentEntity;
            _clipboardData = clipboardData.ToList();
            _cutEntities = cutEntities?.ToList();
            _pastedEntities = pastedEntitiesOutput;
        }

        public override void Execute()
        {
            // Wenn Cut, speichere Info und entferne Original-Entities
            if (_cutEntities != null && _cutEntities.Count > 0)
            {
                _cutEntityInfos.Clear();
                foreach (var entity in _cutEntities)
                {
                    _cutEntityInfos.Add(new CutEntityInfo
                    {
                        Entity = entity,
                        Parent = entity.Parent,
                        Scene = entity.Scene,
                        Index = entity.Parent?.Children.IndexOf(entity) ?? entity.Scene?.Entities.IndexOf(entity) ?? -1
                    });

                    // Entferne aus Original-Position
                    if (entity.Parent != null)
                    {
                        entity.Parent.Children.Remove(entity);
                    }
                    else
                    {
                        entity.Scene?.Entities.Remove(entity);
                    }
                }
            }

            // Paste neue Entities
            _pastedEntities.Clear();
            foreach (var data in _clipboardData)
            {
                var newEntity = EntityClipboardService.Instance.DeserializeEntity(data, _targetScene);
                if (newEntity != null)
                {
                    if (_parentEntity != null)
                    {
                        _parentEntity.AddChild(newEntity);
                    }
                    else
                    {
                        _targetScene.Entities.Add(newEntity);
                    }
                    _pastedEntities.Add(newEntity);
                }
            }
        }

        public override void Undo()
        {
            // Entferne gepastete Entities
            foreach (var entity in _pastedEntities)
            {
                if (entity.Parent != null)
                {
                    entity.Parent.Children.Remove(entity);
                }
                else
                {
                    _targetScene.Entities.Remove(entity);
                }
            }

            // Wenn Cut, stelle Original-Entities wieder her
            if (_cutEntityInfos.Count > 0)
            {
                foreach (var info in _cutEntityInfos)
                {
                    if (info.Parent != null)
                    {
                        if (info.Index >= 0 && info.Index <= info.Parent.Children.Count)
                        {
                            info.Parent.Children.Insert(info.Index, info.Entity);
                        }
                        else
                        {
                            info.Parent.Children.Add(info.Entity);
                        }
                    }
                    else if (info.Scene != null)
                    {
                        if (info.Index >= 0 && info.Index <= info.Scene.Entities.Count)
                        {
                            info.Scene.Entities.Insert(info.Index, info.Entity);
                        }
                        else
                        {
                            info.Scene.Entities.Add(info.Entity);
                        }
                    }
                }
            }
        }

        private class CutEntityInfo
        {
            public GameEntity Entity { get; set; }
            public GameEntity Parent { get; set; }
            public Data.Scene Scene { get; set; }
            public int Index { get; set; }
        }
    }

    /// <summary>
    /// Undo/Redo Command für Delete-Operationen mit mehreren Entities
    /// </summary>
    public class DeleteEntitiesCommand : UndoableCommandBase
    {
        private readonly List<DeleteEntityInfo> _deleteInfos = new List<DeleteEntityInfo>();

        public override string Name => _deleteInfos.Count == 1 ? "Delete Entity" : $"Delete {_deleteInfos.Count} Entities";

        public DeleteEntitiesCommand(IEnumerable<GameEntity> entities)
        {
            foreach (var entity in entities)
            {
                _deleteInfos.Add(new DeleteEntityInfo
                {
                    Entity = entity,
                    Parent = entity.Parent,
                    Scene = entity.Scene,
                    Index = entity.Parent?.Children.IndexOf(entity) ?? entity.Scene?.Entities.IndexOf(entity) ?? -1
                });
            }
        }

        public override void Execute()
        {
            foreach (var info in _deleteInfos)
            {
                if (info.Parent != null)
                {
                    info.Parent.Children.Remove(info.Entity);
                }
                else if (info.Scene != null)
                {
                    info.Scene.Entities.Remove(info.Entity);
                }
            }
        }

        public override void Undo()
        {
            // Rückwärts wiederherstellen für korrekte Indizes
            for (int i = _deleteInfos.Count - 1; i >= 0; i--)
            {
                var info = _deleteInfos[i];
                if (info.Parent != null)
                {
                    if (info.Index >= 0 && info.Index <= info.Parent.Children.Count)
                    {
                        info.Parent.Children.Insert(info.Index, info.Entity);
                    }
                    else
                    {
                        info.Parent.Children.Add(info.Entity);
                    }
                }
                else if (info.Scene != null)
                {
                    if (info.Index >= 0 && info.Index <= info.Scene.Entities.Count)
                    {
                        info.Scene.Entities.Insert(info.Index, info.Entity);
                    }
                    else
                    {
                        info.Scene.Entities.Add(info.Entity);
                    }
                }
            }
        }

        private class DeleteEntityInfo
        {
            public GameEntity Entity { get; set; }
            public GameEntity Parent { get; set; }
            public Data.Scene Scene { get; set; }
            public int Index { get; set; }
        }
    }
}
