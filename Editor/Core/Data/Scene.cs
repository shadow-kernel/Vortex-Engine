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
		private bool _isExpanded;
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
        /// Per-scene environment (fog + post-FX). Optional in the file: scenes saved before v2.7.0
        /// deserialize with null and lazily get defaults (everything off). Applied in Activate().
        /// </summary>
        private SceneSettings _settings;
        [DataMember(Name = "settings", Order = 3, IsRequired = false, EmitDefaultValue = false)]
        public SceneSettings Settings
        {
            get => _settings ?? (_settings = new SceneSettings());
            set => _settings = value;
        }

        /// <summary>
        /// 1 = the entities' active flags in this file are authored ("Toggle Active") and are honoured when the scene
        /// becomes active. 0 (missing) in scenes saved before v3.0: deactivating a scene used to clear every entity's
        /// flag, so such a file can call everything inactive — it is switched on once when first activated, as before,
        /// and carries the marker from its next save.
        /// </summary>
        [DataMember(Name = "activeFlags", Order = 4, IsRequired = false, EmitDefaultValue = false)]
        public int ActiveFlagsVersion { get; set; }

        /// <summary>A scene saved before v3.0 gets every entity switched on (see <see cref="ActiveFlagsVersion"/>);
        /// from then on the flags are authored. Returns true when it switched the flags.</summary>
        public bool EnsureAuthoredActiveFlags()
        {
            if (ActiveFlagsVersion >= 1) return false;
            // part of reading an old file, not an edit: a scene that was its file still is (#411 review)
            bool asRead = _baseline != null && MatchesBaseline();
            ActiveFlagsVersion = 1;
            void On(GameEntity e) { e.SetActiveFlagOnly(true); if (e.Children != null) foreach (var c in e.Children) On(c); }
            if (_entities != null)
                foreach (var entity in _entities) On(entity);   // the caller syncs the engine right after
            if (asRead) _baseline = ContentHash();
            return true;
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

		/// <summary>
		/// UI-State: Ist die Szene im Hierarchy-View aufgeklappt (nicht serialisiert).
		/// Standardm��ig sind Szenen zugeklappt � nur die aktive Szene wird aufgeklappt.
		/// Zweiweg-gebunden an den TreeViewItem, damit manuelles Auf-/Zuklappen erhalten bleibt.
		/// </summary>
		[IgnoreDataMember]
		public bool IsExpanded
		{
			get => _isExpanded;
			set => SetProperty(ref _isExpanded, value, nameof(IsExpanded));
		}

        public Scene()
        {
            _id = Guid.NewGuid();
            _entities = new ObservableCollection<GameEntity>();
            _engineHandle = SceneHandle.Invalid;
			_isActive = false;
            ActiveFlagsVersion = 1;   // a new scene's flags are authored (deserialization skips constructors)
        }

        public Scene(ProjectData project, string name) : this()
        {
            // No Debug.Assert (a failed managed assert breaks into the debugger / pops a modal
            // behind the splash) — log; a null project surfaces immediately in normal use anyway.
            if (project == null) Debug.WriteLine("[Scene] ctor called with null project for '" + name + "'");
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
                    _settings = loadedScene._settings;   // per-scene environment (null on pre-v2.7 files -> defaults)
                    
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
        /// Take over what a freshly read copy of this scene's file holds — entities, environment, name and the active-flag
        /// marker — keeping this object (#411): the project, the hierarchy and every service that holds the scene go on
        /// working with it. The old entities leave the engine and the renderer first; when the scene is active the new ones
        /// are registered and the file's environment applies. Returns the old top-level entities.
        /// </summary>
        internal List<GameEntity> ReplaceContent(Scene file)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            var old = _entities != null ? new List<GameEntity>(_entities) : new List<GameEntity>();
            bool active = IsActive;
            // the old entities' native twins and renderer resources go: the reloaded ones carry the same ids (#358)
            if (active) DeactivateEntities();
            else foreach (var entity in old) entity.SyncEngineStateRecursive(false);
            foreach (var entity in old) { try { Services.SceneRenderService.Instance.RemoveEntityTree(entity); } catch { } }

            _entities = file._entities ?? new ObservableCollection<GameEntity>();
            _settings = file._settings;
            ActiveFlagsVersion = file.ActiveFlagsVersion;
            if (!string.IsNullOrEmpty(file._name)) SetProperty(ref _name, file._name, nameof(Name));
            if (file._id != Guid.Empty) Id = file._id;   // the file's identity too: everything of the scene is the file's now
            foreach (var entity in _entities)
                SetEntityReferencesRecursive(entity);
            SelectedEntity = null;
            OnPropertyChanged(nameof(Entities));   // the hierarchy binds the list itself
            OnPropertyChanged(nameof(Settings));

            if (active) ActivateEntities();
            NoteFileState();
            return old;
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

            RaiseSaving(this);   // terrain / foliage data first: a data path assigned now is saved with the scene
            // the editor's view travels with the scene: whoever opens it next starts where it was saved from
            try { if (ReferenceEquals(ProjectData.Current?.ActiveScene, this)) Services.EditorViewState.Capture(this); } catch { }
            bool created = !File.Exists(FilePath);
            var data = Serialization.DataSerializer.ToBinary(this);
            File.WriteAllBytes(FilePath, data);
            NoteFileState(data);
            Services.PlaySession.NoteWrite(FilePath, created);   // a scene saved while playing may refer to data files play created (#423)
        }

        /// <summary>
        /// F�gt eine GameEntity zur Szene hinzu (mit Undo/Redo Support)
        /// </summary>
        public void AddEntity(GameEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            entity.Scene = this;
            var command = new AddEntityCommand(Entities, entity, "Entities");   // registers it with the engine; undo unregisters it
            UndoRedoManager.Instance.Execute(command);
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

            var command = new RemoveEntityCommand(Entities, entity, "Entities");   // unregisters it; undo registers it again
            UndoRedoManager.Instance.Execute(command);
            try { Services.SceneRenderService.Instance.RemoveEntityTree(entity); } catch { }   // its own meshes/materials go (#358)

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
            EnsureAuthoredActiveFlags();

            // Authored environment (fog + post-FX) follows the live scene — editor open, editor play
            // AND the shipped game all funnel through Activate(). Scripts may override afterwards.
            Settings.Apply();

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

			// Authored environment (fog + post-FX) — this is the path the GameHost player boots
			// through (Activate() covers the editor's open-scene path; both are idempotent).
			Settings.Apply();

			if (!_engineHandle.IsValid)
			{
				_engineHandle = VortexAPI.CreateEngineScene();
			}

			VortexAPI.ActivateEngineScene(_engineHandle);

			// Each entity keeps its authored active flag ("Toggle Active"): one saved inactive — a monster a trigger
			// enables later — stays inactive in the shipped game and after a scene switch, as in editor play. Scenes
			// saved before v3.0 are switched on once (their flags were not authored, see ActiveFlagsVersion).
			EnsureAuthoredActiveFlags();
			if (_entities != null)
			{
				foreach (var entity in _entities)
					entity.SyncEngineStateRecursive();
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

			// Out of the engine — the entities keep their own active flags (clearing them here lost every
			// "Toggle Active" on a scene switch and saved inactive scenes as all-inactive).
			if (_entities != null)
			{
				foreach (var entity in _entities)
					entity.SyncEngineStateRecursive(false);
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
            // reading the name went through its setter, which marks an edit: a scene just read has nothing unsaved (#411)
            _isDirty = false;
        }

        /// <summary>Raised before a scene is written to its file (by any save path): the terrain and foliage data are written
        /// first, so a data path they assign is in the file (#411 review).</summary>
        internal static event Action<Scene> Saving;

        internal static void RaiseSaving(Scene scene)
        {
            var handlers = Saving;
            if (handlers == null || scene == null) return;
            foreach (Action<Scene> h in handlers.GetInvocationList())
            {
                try { h(scene); } catch (Exception ex) { Debug.WriteLine("[Scene] before-save hook failed: " + ex.Message); }
            }
        }

        private long _stampLength = -1;
        private DateTime _stampTimeUtc;
        private byte[] _baseline;   // SHA-256 of the content as this engine writes it, at the last read, save or revert

        /// <summary>How often the scene was read from or written to its file in this session — a save while playing shows here
        /// (<see cref="Services.PlaySnapshot"/>).</summary>
        internal int FileStateVersion { get; private set; }

        /// <summary>The scene was just read from or written to its file (#411): nothing is unsaved, the file's size and time are
        /// what <see cref="FileChangedOnDisk"/> compares with, its content as this engine writes it is what
        /// <see cref="MatchesBaseline"/> compares with (<paramref name="written"/>: the bytes just written), and its data files
        /// are the baseline of a revert now.</summary>
        internal void NoteFileState(byte[] written = null)
        {
            IsDirty = false;
            FileStateVersion++;
            try
            {
                var fi = string.IsNullOrEmpty(FilePath) ? null : new FileInfo(FilePath);
                if (fi != null && fi.Exists) { _stampLength = fi.Length; _stampTimeUtc = fi.LastWriteTimeUtc; }
                else _stampLength = -1;
            }
            catch { _stampLength = -1; }
            try { _baseline = written != null ? System.Security.Cryptography.SHA256.HashData(written) : ContentHash(); }
            catch { _baseline = null; }
            try { Services.SceneDataFiles.Forget(this); } catch { }
        }

        private byte[] ContentHash() => System.Security.Cryptography.SHA256.HashData(Serialization.DataSerializer.ToBinary(this));

        /// <summary>Whether the scene knows the content of its file: it was read from or written to it in this session.</summary>
        internal bool HasBaseline => _baseline != null;

        /// <summary>
        /// Whether the scene in memory is what it was when the editor last read, saved or reverted it (#411 review) — compared as
        /// this engine writes it, so a file written by an older engine and not touched since matches, and an edit taken back by
        /// hand matches again. Exact for every edit, whichever way it came: an inspector row, a gizmo, a tool, a script. False
        /// when the scene was never read from or written to a file.
        /// </summary>
        internal bool MatchesBaseline()
        {
            if (_baseline == null) return false;
            try { return ContentHash().AsSpan().SequenceEqual(_baseline); }
            catch { return false; }
        }

        /// <summary>Whether the scene's file was changed on disk (a git checkout, another program) since the editor read or wrote
        /// it — the scene in memory does not show that version. False when the editor never read or wrote it.</summary>
        internal bool FileChangedOnDisk()
        {
            if (_stampLength < 0 || string.IsNullOrEmpty(FilePath)) return false;
            try
            {
                var fi = new FileInfo(FilePath);
                return !fi.Exists || fi.Length != _stampLength || fi.LastWriteTimeUtc != _stampTimeUtc;
            }
            catch { return false; }
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
