using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using Editor.DllWrapper;
using Editor.Core.Abstractions;

namespace Editor.Core.Data
{
    /// <summary>
    /// Repr�sentiert eine Szene im Projekt.
    /// Szenen enthalten GameEntities und werden als .vscene Bin�rdateien gespeichert.
    /// </summary>
    [DataContract(Name = "Scene", Namespace = "")]
    public class Scene : ViewModelBase, IEngineScene
    {
        public const string FileExtension = ".vscene";

        private string _name;
        private Guid _id;
        private bool _isLoaded;
        private bool _isDirty;
        private string _filePath;
        private ObservableCollection<GameEntity> _entities;
		private bool _isActive;
        private SceneHandle _engineHandle;

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
            set
            {
                if (SetProperty(ref _name, value, nameof(Name)))
                    IsDirty = true;
            }
        }

        [DataMember(Name = "entities", Order = 2)]
        public ObservableCollection<GameEntity> Entities
        {
            get => _entities ?? (_entities = new ObservableCollection<GameEntity>());
            set => _entities = value ?? new ObservableCollection<GameEntity>();
        }

        /// <summary>
        /// Referenz zum �bergeordneten Projekt (nicht serialisiert)
        /// </summary>
        [IgnoreDataMember]
        public ProjectData Project { get; set; }

        /// <summary>
        /// Gibt an ob die Szene geladen ist (nicht serialisiert)
        /// </summary>
        [IgnoreDataMember]
        public bool IsLoaded
        {
            get => _isLoaded;
            private set => SetProperty(ref _isLoaded, value, nameof(IsLoaded));
        }

        /// <summary>
        /// Gibt an ob die Szene ungespeicherte �nderungen hat (nicht serialisiert)
        /// </summary>
        [IgnoreDataMember]
        public bool IsDirty
        {
            get => _isDirty;
            set => SetProperty(ref _isDirty, value, nameof(IsDirty));
        }

        /// <summary>
        /// Dateipfad der Szene (nicht serialisiert - wird aus Projekt-Ordner berechnet)
        /// </summary>
        [IgnoreDataMember]
        public string FilePath
        {
            get
            {
                if (string.IsNullOrEmpty(_filePath) && Project != null)
                {
                    var scenesFolder = Path.Combine(Project.Path, "Assets", "Scenes");
                    _filePath = Path.Combine(scenesFolder, $"{Name}{FileExtension}");
                }
                return _filePath;
            }
            set => _filePath = value;
        }

        /// <summary>
        /// Aktuell ausgew�hlte Entity (nicht serialisiert - UI State)
        /// </summary>
        [IgnoreDataMember]
        public GameEntity SelectedEntity { get; set; }

		/// <summary>
		/// UI/Runtime Status ob die Szene aktuell aktiv ist (nicht serialisiert)
		/// </summary>
		[IgnoreDataMember]
		public bool IsActive
		{
			get => _isActive;
			internal set => SetProperty(ref _isActive, value, nameof(IsActive));
		}

        public Scene()
        {
            _id = Guid.NewGuid();
            _entities = new ObservableCollection<GameEntity>();
            _engineHandle = SceneHandle.Invalid;
			_isActive = false;
        }

        public Scene(ProjectData project, string name) : this()
        {
            Debug.Assert(project != null, "Project darf nicht null sein.");
            Project = project;
            Name = name;
            IsLoaded = true;
        }

        /// <summary>
        /// L�dt die Szene aus der Datei
        /// </summary>
        public void Load()
        {
            if (IsLoaded)
                return;

            if (Editor.Core.Services.AssetVfs.Exists(FilePath))
            {
                try
                {
                    var loadedScene = Serialization.DataSerializer.LoadFromBinary<Scene>(FilePath);
                    _entities = loadedScene._entities ?? new ObservableCollection<GameEntity>();
                    
                    // Setze Referenzen
                    foreach (var entity in _entities)
                    {
                        entity.Scene = this;
                        SetEntityReferencesRecursive(entity);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Fehler beim Laden der Szene: {ex.Message}");
                    _entities = new ObservableCollection<GameEntity>();
                }
            }

			// Runtime-Zustand nach Deserialisierung zur�cksetzen
			_engineHandle = SceneHandle.Invalid;
			_isActive = false;
			SelectedEntity = null;

            IsLoaded = true;
            IsDirty = false;
        }

        /// <summary>
        /// Entl�dt die Szene (beh�lt nur Metadaten)
        /// </summary>
        public void Unload()
        {
            if (!IsLoaded)
                return;

            // Optional: Speichern vor dem Entladen
            if (IsDirty)
            {
                Save();
            }

            _entities?.Clear();
            SelectedEntity = null;
            IsLoaded = false;
            ReleaseEngineScene();
        }

        /// <summary>
        /// Speichert die Szene als Bin�rdatei
        /// </summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(FilePath))
                return;

            // Stelle sicher dass das Verzeichnis existiert
            var directory = Path.GetDirectoryName(FilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Serialization.DataSerializer.SaveAsBinary(this, FilePath);
            IsDirty = false;
        }

        /// <summary>
        /// F�gt eine GameEntity zur Szene hinzu (mit Undo/Redo Support)
        /// </summary>
        public void AddEntity(GameEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            entity.Scene = this;
            var command = new CollectionAddCommand<GameEntity>(Entities, entity, "Entities");
            UndoRedoManager.Instance.Execute(command);
			entity.SyncEngineStateRecursive(IsActive);
            IsDirty = true;
        }

        /// <summary>
        /// Entfernt eine GameEntity aus der Szene (mit Undo/Redo Support)
        /// </summary>
        public void RemoveEntity(GameEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (!Entities.Contains(entity))
                return;

			entity.SyncEngineStateRecursive(false);
            var command = new CollectionRemoveCommand<GameEntity>(Entities, entity, "Entities");
            UndoRedoManager.Instance.Execute(command);

            if (SelectedEntity == entity)
                SelectedEntity = null;

            IsDirty = true;
        }

        /// <summary>
        /// Erstellt eine neue leere GameEntity
        /// </summary>
        public GameEntity CreateEntity(string name = "New Entity")
        {
            var entity = new GameEntity(this, name);
            AddEntity(entity);
            return entity;
        }

        public void Activate()
        {
            if (IsActive)
                return;

            IsActive = true;

            // Preload textures and materials for this scene
            Services.SceneRenderService.Instance.PreloadSceneAssets(this);

            foreach (var entity in Entities)
            {
                entity.SyncEngineStateRecursive(IsActive);
            }
        }

        public void Deactivate()
        {
            if (!IsActive)
                return;

            IsActive = false;

            foreach (var entity in Entities)
            {
                entity.SyncEngineStateRecursive(false);
            }
        }

        IEnumerable<IEngineEntity> IEngineScene.Entities => Entities;

        IEngineEntity IEngineScene.CreateEntity(string name)
        {
            return CreateEntity(name);
        }

        void IEngineScene.RemoveEntity(IEngineEntity entity)
        {
            if (entity is GameEntity ge)
            {
                RemoveEntity(ge);
            }
        }


        /// <summary>
        /// Erstellt eine GameEntity mit Standard-Komponenten f�r einen bestimmten Typ
        /// </summary>
        public GameEntity CreatePrimitive(PrimitiveType type)
        {
            var entity = new GameEntity(this, type.ToString());
            entity.AddComponent(new MeshRenderer(entity) { MeshPath = $"Primitive:{type}" });
            AddEntity(entity);
            return entity;
        }

        /// <summary>
        /// Erstellt eine Kamera-Entity
        /// </summary>
        public GameEntity CreateCamera(string name = "Camera")
        {
            var entity = new GameEntity(this, name);
            entity.AddComponent(new Camera(entity));
            AddEntity(entity);
            return entity;
        }

        /// <summary>
        /// Erstellt eine Licht-Entity
        /// </summary>
        public GameEntity CreateLight(LightType lightType, string name = null)
        {
            var entity = new GameEntity(this, name ?? $"{lightType} Light");
            entity.AddComponent(new Light(entity, lightType));
            AddEntity(entity);
            return entity;
        }

        /// <summary>
        /// Erstellt eine Skybox-Entity f�r Umgebungsbeleuchtung
        /// </summary>
        public GameEntity CreateSkybox(string name = "Skybox")
        {
            var entity = new GameEntity(this, name);
            entity.AddComponent(new Skybox(entity));
            AddEntity(entity);
            return entity;
        }

        private void SetEntityReferencesRecursive(GameEntity entity)
        {
            entity.Scene = this;
            foreach (var component in entity.Components)
            {
                component.Entity = entity;
            }
            foreach (var child in entity.Children)
            {
                child.Parent = entity;
                SetEntityReferencesRecursive(child);
            }
        }

        /// <summary>
        /// Sucht eine Entity anhand ihrer ID (rekursiv)
        /// </summary>
        public GameEntity FindEntityById(Guid id)
        {
            return FindEntityByIdRecursive(Entities, id);
        }

        private GameEntity FindEntityByIdRecursive(IEnumerable<GameEntity> entities, Guid id)
        {
            foreach (var entity in entities)
            {
                if (entity.Id == id)
                    return entity;
                
                var found = FindEntityByIdRecursive(entity.Children, id);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// Aktiviert alle Entities dieser Szene in der Engine.
        /// Sollte aufgerufen werden, nachdem die Szene vollst�ndig geladen wurde.
        /// </summary>
        public void ActivateEntities()
        {
			IsActive = true;

			if (!_engineHandle.IsValid)
			{
				_engineHandle = VortexAPI.CreateEngineScene();
			}

			VortexAPI.ActivateEngineScene(_engineHandle);

			if (_entities != null)
			{
				foreach (var entity in _entities)
				{
					SetEntityActiveRecursive(entity, true);
					entity.SyncEngineStateRecursive();
				}
			}
        }

        /// <summary>
        /// Deaktiviert alle Entities dieser Szene in der Engine.
        /// Sollte vor dem Entladen der Szene aufgerufen werden.
        /// </summary>
        public void DeactivateEntities()
        {
			IsActive = false;

			if (_engineHandle.IsValid)
			{
				VortexAPI.DeactivateEngineScene(_engineHandle);
			}

			if (_entities != null)
			{
				foreach (var entity in _entities)
				{
					SetEntityActiveRecursive(entity, false);
					entity.SyncEngineStateRecursive(false);
				}
			}
        }

		private void SetEntityActiveRecursive(GameEntity entity, bool active)
		{
			entity.IsActive = active;
			if (entity.Children != null)
			{
				foreach (var child in entity.Children)
				{
					SetEntityActiveRecursive(child, active);
				}
			}
		}

		internal void ReleaseEngineScene()
		{
			if (_engineHandle.IsValid)
			{
				VortexAPI.DestroyEngineScene(_engineHandle);
				_engineHandle = SceneHandle.Invalid;
			}
		}

		internal SceneHandle EngineHandle => _engineHandle;

        /// <summary>
        /// Wird nach der Deserialisierung aufgerufen um die Parent-Referenz wiederherzustellen
        /// </summary>
        [OnDeserialized]
        internal void OnDeserializedMethod(StreamingContext context)
        {
            // Project wird vom ProjectService nach dem Laden gesetzt
            if (_entities != null)
            {
                foreach (var entity in _entities)
                {
                    SetEntityReferencesRecursive(entity);
                }
            }
            IsLoaded = true;
			_engineHandle = SceneHandle.Invalid;
			_isActive = false;
        }
    }

    /// <summary>
    /// Primitive Mesh-Typen
    /// </summary>
    public enum PrimitiveType
    {
        Cube,
        Sphere,
        Capsule,
        Cylinder,
        Plane,
        Quad
    }
}
