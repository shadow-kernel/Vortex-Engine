using System;
using Editor.Core.Data;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Centralized service for managing selection state across the editor.
    /// Allows different views to communicate about what is selected.
    /// </summary>
    public class SelectionService
    {
        private static SelectionService _instance;
        public static SelectionService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new SelectionService();
                return _instance;
            }
        }

        private GameEntity _selectedEntity;
        private Scene _selectedScene;

        public event EventHandler<SelectionEventArgs> SelectionChanged;

        private SelectionService() { }

        /// <summary>
        /// Currently selected entity.
        /// </summary>
        public GameEntity SelectedEntity
        {
            get => _selectedEntity;
            set
            {
                if (_selectedEntity != value)
                {
                    _selectedEntity = value;
                    OnSelectionChanged();
                }
            }
        }

        /// <summary>
        /// Currently selected scene.
        /// </summary>
        public Scene SelectedScene
        {
            get => _selectedScene;
            set
            {
                if (_selectedScene != value)
                {
                    _selectedScene = value;
                    OnSelectionChanged();
                }
            }
        }

        /// <summary>
        /// Select an entity.
        /// If entity is locked to parent, select the parent instead.
        /// </summary>
        public void Select(GameEntity entity)
        {
            // If entity is locked to parent, select the parent instead
            var targetEntity = GetSelectableEntity(entity);
            
            _selectedEntity = targetEntity;
            _selectedScene = targetEntity?.Scene;
            OnSelectionChanged();
        }

        /// <summary>
        /// Gets the selectable entity (walks up hierarchy if locked to parent).
        /// </summary>
        private GameEntity GetSelectableEntity(GameEntity entity)
        {
            if (entity == null)
                return null;
            
            // Walk up the hierarchy until we find an entity that's not locked
            while (entity != null && entity.IsLockedToParent && entity.Parent != null)
            {
                entity = entity.Parent;
            }
            
            return entity;
        }

        /// <summary>
        /// Select a scene.
        /// </summary>
        public void Select(Scene scene)
        {
            _selectedScene = scene;
            _selectedEntity = null;
            OnSelectionChanged();
        }

        /// <summary>
        /// Clear selection.
        /// </summary>
        public void ClearSelection()
        {
            _selectedEntity = null;
            _selectedScene = null;
            OnSelectionChanged();
        }

        private void OnSelectionChanged()
        {
            SelectionChanged?.Invoke(this, new SelectionEventArgs
            {
                SelectedEntity = _selectedEntity,
                SelectedScene = _selectedScene
            });
        }

        /// <summary>
        /// Fired when a view explicitly asks the editor camera to frame an entity
        /// (e.g. right after creating it). Deliberately separate from SelectionChanged
        /// so the camera only moves on explicit focus, not on every click.
        /// </summary>
        public event EventHandler<SelectionEventArgs> FocusRequested;

        /// <summary>
        /// Request that the editor viewport frame the given entity.
        /// </summary>
        public void RequestFocus(GameEntity entity)
        {
            if (entity == null) return;
            FocusRequested?.Invoke(this, new SelectionEventArgs
            {
                SelectedEntity = entity,
                SelectedScene = entity.Scene
            });
        }

        /// <summary>
        /// Event fired when the selected entity's transform is modified (e.g., during gizmo drag).
        /// </summary>
        public event EventHandler<TransformChangedEventArgs> TransformChanged;

        /// <summary>
        /// Notify that the selected entity's transform has changed.
        /// Call this during gizmo dragging to update the inspector in real-time.
        /// </summary>
        public void NotifyTransformChanged()
        {
            if (_selectedEntity != null)
            {
                TransformChanged?.Invoke(this, new TransformChangedEventArgs
                {
                    Entity = _selectedEntity
                });
            }
        }
    }

    /// <summary>
    /// Event args for selection changes.
    /// </summary>
    public class SelectionEventArgs : EventArgs
    {
        public GameEntity SelectedEntity { get; set; }
        public Scene SelectedScene { get; set; }
    }

    /// <summary>
    /// Event args for transform changes.
    /// </summary>
    public class TransformChangedEventArgs : EventArgs
    {
        public GameEntity Entity { get; set; }
    }
}
