using Editor.Core.Abstractions;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.DllWrapper;
using Editor.ECS.Components;
using Editor.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;

namespace Editor.ECS
{
    /// <summary>
    /// Repr�sentiert eine Spielentit�t in der Szene.
    /// Enth�lt Komponenten und kann in einer Hierarchie organisiert werden.
    /// </summary>
    [DataContract(Name = "GameEntity", Namespace = "")]
    [KnownType(typeof(Transform))]
    [KnownType(typeof(Components.Rendering.MeshRenderer))]
    [KnownType(typeof(Components.Rendering.SpriteRenderer))]
    [KnownType(typeof(Components.Rendering.Camera))]
    [KnownType(typeof(Components.Lighting.Light))]
    [KnownType(typeof(Components.Physics.Collider))]
    [KnownType(typeof(Components.Physics.BoxCollider))]
    [KnownType(typeof(Components.Physics.SphereCollider))]
    [KnownType(typeof(Components.Physics.CapsuleCollider))]
    [KnownType(typeof(Components.Physics.MeshCollider))]
    [KnownType(typeof(Components.Physics.Rigidbody))]
    [KnownType(typeof(Components.Audio.AudioSource))]
    [KnownType(typeof(Components.Audio.AudioListener))]
    [KnownType(typeof(Components.Scripting.Script))]
    public class GameEntity : Core.ViewModelBase, IEngineEntity
    {
        private long _entityId = ID.INVALID_ID;
        private Guid _id;
        private string _name;
        private bool _isActive = true;
        private bool _isStatic;
        private int _layer;
        private string _tag = "Untagged";
        private bool _isExpanded = true;
        private GameEntity _parent;
        private ObservableCollection<GameEntity> _children;
        private ObservableCollection<Component> _components;
        private Transform _transform;
        private bool _isDeserializing;

        #region Serialized Properties

        [IgnoreDataMember]
        public long EntityId
        {
            get => _entityId;
            set => SetProperty(ref _entityId, value, nameof(EntityId));
        }

        [DataMember(Name = "id", Order = 0)]
        public Guid Id
        {
            get => _id;
            set => SetProperty(ref _id, value, nameof(Id));
        }

        [DataMember(Name = "name", Order = 1)]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value, nameof(Name));
        }

        [DataMember(Name = "isActive", Order = 2)]
        public bool IsActive
        {
            get => _isActive;
            set
            {
                SetProperty(ref _isActive, value, nameof(IsActive));

                // W�hrend der Deserialisierung keine Engine-Registrierung
                if (_isDeserializing)
                    return;

                SyncEngineStateRecursive(Parent?.ActiveInHierarchy ?? true);
            }
        }

        [DataMember(Name = "isStatic", Order = 3)]
        public bool IsStatic
        {
            get => _isStatic;
            set => SetProperty(ref _isStatic, value, nameof(IsStatic));
        }

        [DataMember(Name = "layer", Order = 4)]
        public int Layer
        {
            get => _layer;
            set => SetProperty(ref _layer, value, nameof(Layer));
        }

        [DataMember(Name = "tag", Order = 5)]
        public string Tag
        {
            get => _tag;
            set => SetProperty(ref _tag, value, nameof(Tag));
        }

        private string _prefabPath;
        /// <summary>If this entity is a PREFAB INSTANCE, the project-relative path to its source .ventity file
        /// (e.g. "Assets/Prefabs/Tree.ventity"). Null/empty for a plain scene entity. Links the instance to its
        /// prefab so changes can be Applied back / Reverted, and so the UI can mark it as an instance.</summary>
        [DataMember(Name = "prefabPath", Order = 8)]
        public string PrefabPath
        {
            get => _prefabPath;
            set => SetProperty(ref _prefabPath, value, nameof(PrefabPath));
        }

        /// <summary>True when this entity is linked to a prefab asset.</summary>
        [IgnoreDataMember]
        public bool IsPrefabInstance => !string.IsNullOrEmpty(_prefabPath);

        [DataMember(Name = "children", Order = 6)]
        public ObservableCollection<GameEntity> Children
        {
            get => _children ?? (_children = new ObservableCollection<GameEntity>());
            set => _children = value ?? new ObservableCollection<GameEntity>();
        }

        [DataMember(Name = "components", Order = 7)]
        public ObservableCollection<Component> Components
        {
            get => _components ?? (_components = new ObservableCollection<Component>());
            set => _components = value ?? new ObservableCollection<Component>();
        }

        #endregion

        #region Runtime Properties

        /// <summary>
        /// Transform-Komponente (jede Entity hat eine)
        /// </summary>
        [IgnoreDataMember]
        public Transform Transform
        {
            get
            {
                if (_transform == null)
                {
                    _transform = GetComponent<Transform>();
                }
                return _transform;
            }
        }

        /// <summary>
        /// Referenz zur �bergeordneten Entity
        /// </summary>
        [IgnoreDataMember]
        public GameEntity Parent
        {
            get => _parent;
            set => SetProperty(ref _parent, value, nameof(Parent));
        }

        /// <summary>
        /// Referenz zur Szene
        /// </summary>
        [IgnoreDataMember]
        public Core.Data.Scene Scene { get; set; }

        /// <summary>
        /// UI-State: Ist im Hierarchy-View aufgeklappt
        /// </summary>
        [IgnoreDataMember]
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value, nameof(IsExpanded));
        }

        private bool _isSelected;
        
        /// <summary>
        /// UI-State: Ist ausgew�hlt (f�r Multi-Select)
        /// </summary>
        [IgnoreDataMember]
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value, nameof(IsSelected));
        }

        private bool _isFolder;
        
        /// <summary>
        /// Markiert diese Entity als Ordner (nur f�r Organisation, keine Komponenten)
        /// </summary>
        [DataMember(Name = "isFolder", Order = 8)]
        public bool IsFolder
        {
            get => _isFolder;
            set => SetProperty(ref _isFolder, value, nameof(IsFolder));
        }

        private bool _isLockedToParent;
        
        /// <summary>
        /// Wenn true, kann diese Entity nicht einzeln ausgew�hlt oder verschoben werden.
        /// Sie bewegt sich nur mit ihrem Parent zusammen (z.B. Submeshes eines importierten Modells).
        /// </summary>
        [DataMember(Name = "isLockedToParent", Order = 9)]
        public bool IsLockedToParent
        {
            get => _isLockedToParent;
            set => SetProperty(ref _isLockedToParent, value, nameof(IsLockedToParent));
        }

        /// <summary>
        /// Ob die Entity in der Hierarchie aktiv ist (ber�cksichtigt Parent)
        /// </summary>
        [IgnoreDataMember]
        public bool ActiveInHierarchy
        {
            get
            {
                if (Scene != null && !Scene.IsActive) return false;
                if (!IsActive) return false;
                if (Parent != null) return Parent.ActiveInHierarchy;
                return true;
            }
        }

        long IEngineEntity.EngineId => EntityId;

        IEnumerable<IEngineEntity> IEngineEntity.Children => _children ?? (_children = new ObservableCollection<GameEntity>());

        IEngineScene IEngineEntity.Scene => Scene;

        #endregion

        #region Constructors

		internal void SyncEngineStateRecursive(bool parentActive = true)
		{
			if (_isDeserializing)
				return;

			// Ohne Transform keine Engine-Repr�sentation (z.B. Folder-Only-Entities)
			if (Transform == null)
				return;

			var shouldBeActive = parentActive && _isActive;
			var sceneHandle = Scene != null ? Scene.EngineHandle : DllWrapper.SceneHandle.Invalid;
			if (shouldBeActive && Scene != null && !sceneHandle.IsValid)
			{
				// Stelle sicher, dass eine Engine-Szene existiert
				Scene.ActivateEntities();
				sceneHandle = Scene.EngineHandle;
			}

			if (shouldBeActive)
			{
				if (!ID.IsValid(_entityId))
				{
					EntityId = VortexAPI.CreateGameEntity(this, sceneHandle);
					Debug.Assert(ID.IsValid(_entityId), "Failed to create GameEntity in engine.");
				}
				
				// Synchronisiere alle MeshRenderer-Komponenten zur Engine
				SyncMeshRenderersToEngine();
			}
			else if (ID.IsValid(_entityId))
			{
				// Entferne alle MeshRenderer aus der Engine bevor das Entity entfernt wird
				RemoveMeshRenderersFromEngine();
				
				VortexAPI.RemoveGameEntity(this, sceneHandle);
				_entityId = ID.INVALID_ID;
			}

			if (_children != null)
			{
				foreach (var child in _children)
				{
				child.SyncEngineStateRecursive(shouldBeActive);
				}
			}
		}

		private void SyncMeshRenderersToEngine()
		{
			if (_components == null) return;
			
			foreach (var component in _components)
			{
				if (component is Components.Rendering.MeshRenderer meshRenderer)
				{
					meshRenderer.SyncToEngine();
				}
			}
		}

		private void RemoveMeshRenderersFromEngine()
		{
			if (_components == null) return;
			
			foreach (var component in _components)
			{
				if (component is Components.Rendering.MeshRenderer meshRenderer)
				{
					meshRenderer.RemoveFromEngine();
				}
			}
		}


        public GameEntity()
        {
            _id = Guid.NewGuid();
            _children = new ObservableCollection<GameEntity>();
            _components = new ObservableCollection<Component>();
        }

        public GameEntity(string name) : this()
        {
            Name = name;
            // Jede Entity hat standardm��ig eine Transform-Komponente
            var transform = new Transform(this);
            _components.Add(transform);
            _transform = transform;
        }

        public GameEntity(Core.Data.Scene scene, string name) : this(name)
        {
            Scene = scene;
        }

        #endregion

        #region Component Management

        /// <summary>
        /// F�gt eine Komponente hinzu (mit Undo/Redo Support)
        /// </summary>
        public void AddComponent(Component component)
        {
            if (component == null)
                throw new ArgumentNullException(nameof(component));

            component.Entity = this;
            var command = new CollectionAddCommand<Component>(Components, component, "Components");
            UndoRedoManager.Instance.Execute(command);
        }

        /// <summary>
        /// F�gt eine Komponente direkt hinzu (ohne Undo/Redo)
        /// </summary>
        internal void AddComponentDirect(Component component)
        {
            if (component == null)
                throw new ArgumentNullException(nameof(component));

            component.Entity = this;
            Components.Add(component);
        }

        /// <summary>
        /// Entfernt eine Komponente (mit Undo/Redo Support)
        /// Transform kann nicht entfernt werden.
        /// </summary>
        public void RemoveComponent(Component component)
        {
            if (component == null)
                throw new ArgumentNullException(nameof(component));

            // Transform-Komponente kann nicht entfernt werden
            if (component is Transform)
                return;

            if (!Components.Contains(component))
                return;

            var command = new CollectionRemoveCommand<Component>(Components, component, "Components");
            UndoRedoManager.Instance.Execute(command);
        }

        /// <summary>
        /// Holt eine Komponente eines bestimmten Typs
        /// </summary>
        public T GetComponent<T>() where T : Component
        {
            return Components.OfType<T>().FirstOrDefault();
        }

        /// <summary>
        /// Holt alle Komponenten eines bestimmten Typs
        /// </summary>
        public T[] GetComponents<T>() where T : Component
        {
            return Components.OfType<T>().ToArray();
        }

        /// <summary>
        /// Pr�ft ob eine Komponente eines bestimmten Typs existiert
        /// </summary>
        public bool HasComponent<T>() where T : Component
        {
            return GetComponent<T>() != null;
        }

        /// <summary>
        /// Erstellt und f�gt eine Komponente hinzu
        /// </summary>
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T { Entity = this };
            AddComponent(component);
            return component;
        }

        #endregion

        #region Hierarchy Management

        /// <summary>
        /// F�gt eine Kind-Entity hinzu (mit Undo/Redo Support)
        /// </summary>
        public void AddChild(GameEntity child)
        {
            if (child == null)
                throw new ArgumentNullException(nameof(child));

            child.Parent = this;
            child.Scene = Scene;
            var command = new CollectionAddCommand<GameEntity>(Children, child, "Children");
            UndoRedoManager.Instance.Execute(command);
        }

        /// <summary>
        /// Entfernt eine Kind-Entity (mit Undo/Redo Support)
        /// </summary>
        public void RemoveChild(GameEntity child)
        {
            if (child == null)
                throw new ArgumentNullException(nameof(child));

            if (!Children.Contains(child))
                return;

            var command = new CollectionRemoveCommand<GameEntity>(Children, child, "Children");
            UndoRedoManager.Instance.Execute(command);
            child.Parent = null;
        }

        /// <summary>
        /// Setzt die �bergeordnete Entity
        /// </summary>
        public void SetParent(GameEntity parent)
        {
            if (Parent != null)
            {
                Parent.Children.Remove(this);
            }

            Parent = parent;

            if (parent != null)
            {
                parent.Children.Add(this);
                Scene = parent.Scene;
            }
        }

        /// <summary>
        /// Findet ein Kind nach Namen
        /// </summary>
        public GameEntity Find(string name)
        {
            foreach (var child in Children)
            {
                if (child.Name == name)
                    return child;

                var found = child.Find(name);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// Gibt den vollst�ndigen Pfad der Entity zur�ck
        /// </summary>
        public string GetPath()
        {
            if (Parent == null)
                return "/" + Name;
            return Parent.GetPath() + "/" + Name;
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Wird vor der Deserialisierung aufgerufen
        /// </summary>
        [OnDeserializing]
        internal void OnDeserializingMethod(StreamingContext context)
        {
            _isDeserializing = true;
        }

        /// <summary>
        /// Wird nach der Deserialisierung aufgerufen
        /// </summary>
        [OnDeserialized]
        internal void OnDeserializedMethod(StreamingContext context)
        {
            _isDeserializing = false;
            // Setze Parent-Referenzen f�r Kinder
            if (_children != null)
            {
                foreach (var child in _children)
                {
                    child.Parent = this;
                    child.Scene = Scene;
                }
            }

            // Setze Entity-Referenzen f�r Komponenten
            if (_components != null)
            {
                foreach (var component in _components)
                {
                    component.Entity = this;
                }

                // Cache Transform
                _transform = _components.OfType<Transform>().FirstOrDefault();
            }
        }

        /// <summary>
        /// Generiert neue IDs f�r diese Entity und alle Kinder/Komponenten.
        /// Wird beim Kopieren/Einf�gen verwendet.
        /// </summary>
        public void RegenerateIds()
        {
            _id = Guid.NewGuid();

            // Regeneriere IDs f�r alle Komponenten
            if (_components != null)
            {
                foreach (var component in _components)
                {
                    component.RegenerateId();
                }
            }

            // Regeneriere IDs f�r alle Kinder
            if (_children != null)
            {
                foreach (var child in _children)
                {
                    child.RegenerateIds();
                }
            }
        }

        #endregion
    }
}
