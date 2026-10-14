using System.Collections.Generic;
using Editor.ECS;

namespace Editor.Core.UndoRedo.Commands
{
    /// <summary>
    /// Adds an entity to a scene's root list or to a parent's Children and keeps its engine registration in step: it is
    /// registered while it is active in an active scene (<see cref="GameEntity.SyncEngineState"/>), and an undone add takes
    /// it out of the engine again — every entity of a loaded scene owns a native entity, so an undone create, Create Child
    /// or prefab placement must not leave one behind. Redo registers it again.
    /// </summary>
    internal sealed class AddEntityCommand : CollectionAddCommand<GameEntity>
    {
        private readonly GameEntity _entity;

        public AddEntityCommand(IList<GameEntity> list, GameEntity entity, string collectionName) : base(list, entity, collectionName)
        {
            _entity = entity;
        }

        public override void Execute()
        {
            base.Execute();
            _entity.SyncEngineState();
        }

        public override void Undo()
        {
            base.Undo();
            _entity.SyncEngineStateRecursive(false);
        }
    }

    /// <summary>
    /// Takes an entity out of a scene's root list or a parent's Children and out of the engine; undo puts it back in its
    /// slot — under its parent again — and registers it again.
    /// </summary>
    internal sealed class RemoveEntityCommand : CollectionRemoveCommand<GameEntity>
    {
        private readonly GameEntity _entity;
        private readonly GameEntity _parent;

        public RemoveEntityCommand(IList<GameEntity> list, GameEntity entity, string collectionName) : base(list, entity, collectionName)
        {
            _entity = entity;
            _parent = entity.Parent;
        }

        public override void Execute()
        {
            base.Execute();
            _entity.SyncEngineStateRecursive(false);
        }

        public override void Undo()
        {
            base.Undo();
            _entity.Parent = _parent;   // RemoveChild drops the link after the removal
            _entity.SyncEngineState();
        }
    }
}
