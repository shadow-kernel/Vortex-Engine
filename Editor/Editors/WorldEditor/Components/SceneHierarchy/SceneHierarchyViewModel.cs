using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;

namespace Editor.Editors.WorldEditor.Components.SceneHierarchy
{
    /// <summary>
    /// ViewModel f�r die Scene Hierarchy View.
    /// Verwaltet die Anzeige und Manipulation von Szenen und GameEntities.
    /// </summary>
    public class SceneHierarchyViewModel : Core.ViewModelBase
    {
        private ProjectData _currentProject;
        private Scene _selectedScene;
        private GameEntity _selectedEntity;
        private string _searchText;
        private ObservableCollection<GameEntity> _selectedEntities = new ObservableCollection<GameEntity>();

        public ProjectData CurrentProject
        {
            get => _currentProject;
            set
            {
                if (SetProperty(ref _currentProject, value, nameof(CurrentProject)))
                {
                    OnPropertyChanged(nameof(Scenes));
                }
            }
        }

        public ObservableCollection<Scene> Scenes => _currentProject?.Scenes;

		public Scene SelectedScene
		{
			get => _selectedScene;
			set
			{
				if (SetProperty(ref _selectedScene, value, nameof(SelectedScene)))
				{
					OnPropertyChanged(nameof(Entities));
				}
			}
		}

        public ObservableCollection<GameEntity> Entities => _selectedScene?.Entities;

        public GameEntity SelectedEntity
        {
            get => _selectedEntity;
            set
            {
                if (SetProperty(ref _selectedEntity, value, nameof(SelectedEntity)))
                {
                    if (_selectedScene != null)
                        _selectedScene.SelectedEntity = value;
                    
                    // Notify SelectionService
                    if (value != null)
                        SelectionService.Instance.Select(value);
                }
            }
        }

        /// <summary>
        /// Mehrfach-Selektion von Entities
        /// </summary>
        public ObservableCollection<GameEntity> SelectedEntities
        {
            get => _selectedEntities;
            set => SetProperty(ref _selectedEntities, value, nameof(SelectedEntities));
        }

        /// <summary>
        /// Gibt an, ob mehrere Entities ausgew�hlt sind
        /// </summary>
        public bool HasMultipleSelection => _selectedEntities.Count > 1;

        /// <summary>
        /// Gibt an, ob etwas im Clipboard ist
        /// </summary>
        public bool CanPaste => EntityClipboardService.Instance.HasContent;

        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value, nameof(SearchText));
        }

        #region Scene Commands
        public ICommand CreateSceneCommand { get; }
        public ICommand DeleteSceneCommand { get; }
        public ICommand RenameSceneCommand { get; }
        public ICommand SaveSceneCommand { get; }
        #endregion

        #region Entity Commands
        public ICommand CreateEmptyEntityCommand { get; }
        public ICommand CreatePlayerCommand { get; }
        public ICommand CreateFolderCommand { get; }
        public ICommand CreateChildEntityCommand { get; }
        public ICommand DeleteEntityCommand { get; }
        public ICommand DuplicateEntityCommand { get; }
        public ICommand RenameEntityCommand { get; }
        #endregion

        #region Clipboard Commands
        public ICommand CutCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand PasteCommand { get; }
        public ICommand SelectAllCommand { get; }
        #endregion

        #region 3D Object Commands
        public ICommand CreateCubeCommand { get; }
        public ICommand CreateSphereCommand { get; }
        public ICommand CreateCapsuleCommand { get; }
        public ICommand CreateCylinderCommand { get; }
        public ICommand CreatePlaneCommand { get; }
        public ICommand CreateQuadCommand { get; }
        #endregion

        #region Light Commands
        public ICommand CreateDirectionalLightCommand { get; }
        public ICommand CreatePointLightCommand { get; }
        public ICommand CreateSpotLightCommand { get; }
        public ICommand CreateSkyboxCommand { get; }
        #endregion

        #region Other Commands
        public ICommand CreateCameraCommand { get; }
        public ICommand CreateAudioSourceCommand { get; }
        #endregion

        #region UI Commands
        public ICommand CreateUICanvasCommand { get; }
        public ICommand CreateUITextCommand { get; }
        public ICommand CreateUIImageCommand { get; }
        public ICommand CreateUIButtonCommand { get; }
        #endregion

        public SceneHierarchyViewModel()
        {
            // Scene Commands
            CreateSceneCommand = new RelayCommand(_ => CreateScene());
            DeleteSceneCommand = new RelayCommand(_ => DeleteScene(), _ => SelectedScene != null && Scenes?.Count > 1);
            RenameSceneCommand = new RelayCommand(_ => { /* Handled in View */ }, _ => SelectedScene != null);
            SaveSceneCommand = new RelayCommand(_ => SaveScene(), _ => SelectedScene != null);

            // Entity Commands
            CreateEmptyEntityCommand = new RelayCommand(_ => CreateEmptyEntity());
            CreatePlayerCommand = new RelayCommand(_ => CreatePlayer());
            CreateFolderCommand = new RelayCommand(_ => CreateFolder());
            CreateChildEntityCommand = new RelayCommand(_ => CreateChildEntity(), _ => SelectedEntity != null);
            DeleteEntityCommand = new RelayCommand(_ => DeleteSelectedEntities(), _ => SelectedEntity != null || _selectedEntities.Count > 0);
            DuplicateEntityCommand = new RelayCommand(_ => DuplicateSelectedEntities(), _ => SelectedEntity != null || _selectedEntities.Count > 0);
            RenameEntityCommand = new RelayCommand(_ => { /* Handled in View */ }, _ => SelectedEntity != null);

            // Clipboard Commands
            CutCommand = new RelayCommand(_ => CutSelectedEntities(), _ => SelectedEntity != null || _selectedEntities.Count > 0);
            CopyCommand = new RelayCommand(_ => CopySelectedEntities(), _ => SelectedEntity != null || _selectedEntities.Count > 0);
            PasteCommand = new RelayCommand(_ => PasteEntities(), _ => CanPaste && SelectedScene != null);
            SelectAllCommand = new RelayCommand(_ => SelectAll(), _ => SelectedScene != null && Entities?.Count > 0);

            // 3D Objects
            CreateCubeCommand = new RelayCommand(_ => CreatePrimitive(PrimitiveType.Cube));
            CreateSphereCommand = new RelayCommand(_ => CreatePrimitive(PrimitiveType.Sphere));
            CreateCapsuleCommand = new RelayCommand(_ => CreatePrimitive(PrimitiveType.Capsule));
            CreateCylinderCommand = new RelayCommand(_ => CreatePrimitive(PrimitiveType.Cylinder));
            CreatePlaneCommand = new RelayCommand(_ => CreatePrimitive(PrimitiveType.Plane));
            CreateQuadCommand = new RelayCommand(_ => CreatePrimitive(PrimitiveType.Quad));

            // Lights
            CreateDirectionalLightCommand = new RelayCommand(_ => CreateLight(LightType.Directional));
            CreatePointLightCommand = new RelayCommand(_ => CreateLight(LightType.Point));
            CreateSpotLightCommand = new RelayCommand(_ => CreateLight(LightType.Spot));
            CreateSkyboxCommand = new RelayCommand(_ => CreateSkybox());

            // Camera & Audio
            CreateCameraCommand = new RelayCommand(_ => CreateCamera());
            CreateAudioSourceCommand = new RelayCommand(_ => CreateAudioSource());

            // UI
            CreateUICanvasCommand = new RelayCommand(_ => CreateUIElement("Canvas"));
            CreateUITextCommand = new RelayCommand(_ => CreateUIElement("Text"));
            CreateUIImageCommand = new RelayCommand(_ => CreateUIElement("Image"));
            CreateUIButtonCommand = new RelayCommand(_ => CreateUIElement("Button"));
        }

        public void SetProject(ProjectData project)
        {
            CurrentProject = project;
			if (project?.Scenes?.Count > 0)
			{
				var initialScene = project.ActiveScene ?? project.Scenes[0];

				// Mark all scenes inactive first
				foreach (var scene in project.Scenes)
				{
					if (scene != initialScene)
					{
						scene.IsActive = false;
						scene.DeactivateEntities();
					}
				}

				// Activate the chosen scene
				ActivateScene(initialScene);
			}
        }

        public void SetScene(Scene scene)
        {
            SelectedScene = scene;
        }

		public void ActivateScene(Scene scene)
		{
			if (scene == null)
				return;

			var previous = _currentProject?.ActiveScene;
			if (previous != null && previous != scene)
			{
				previous.DeactivateEntities();
			}

			if (_currentProject != null)
			{
				_currentProject.ActiveScene = scene;
			}

			scene.Load();
			scene.ActivateEntities();
			scene.IsActive = true;
			SelectedScene = scene;
			OnPropertyChanged(nameof(Entities));
		}

		public void DeactivateScene(Scene scene)
		{
			if (scene == null)
				return;

			scene.DeactivateEntities();
			scene.IsActive = false;

			if (_currentProject != null && _currentProject.ActiveScene == scene)
			{
				_currentProject.ActiveScene = null;
			}

			SelectedScene = scene;
			OnPropertyChanged(nameof(Entities));
		}

        #region Scene Methods

        private void CreateScene()
        {
            if (_currentProject == null) return;

            var scene = new Scene(_currentProject, $"New Scene {_currentProject.Scenes.Count + 1}");
            _currentProject.AddScene(scene);
            SelectedScene = scene;
        }

        private void DeleteScene()
        {
            if (_currentProject == null || _selectedScene == null) return;
            if (_currentProject.Scenes.Count <= 1) return;

            var sceneToDelete = _selectedScene;
            var index = _currentProject.Scenes.IndexOf(sceneToDelete);
            
            // W�hle eine andere Szene
            SelectedScene = index > 0 ? _currentProject.Scenes[index - 1] : _currentProject.Scenes[1];
            
			// Deaktivieren und Engine-Ressourcen freigeben
			sceneToDelete.DeactivateEntities();
			sceneToDelete.ReleaseEngineScene();

            _currentProject.RemoveScene(sceneToDelete);
        }

        private void SaveScene()
        {
            if (_selectedScene == null) return;
            SceneService.Instance.SaveScene(_selectedScene);
        }

        #endregion

        #region Entity Methods

        private void CreateEmptyEntity()
        {
            if (_selectedScene == null) return;
            var entity = _selectedScene.CreateEntity("New Entity");
            SelectedEntity = entity;
            SelectionService.Instance.RequestFocus(entity);
        }

        /// <summary>Creates a real "Player" object: a root entity carrying the PlayerController script
        /// (movement/look — all game-side), with the Main Camera as a CHILD at eye height so it follows
        /// the player automatically via the ECS hierarchy. Nothing is hardcoded in the engine.</summary>
        private void CreatePlayer()
        {
            if (_selectedScene == null) return;

            // Ensure the starter controller script exists in the project.
            string scriptRel = "Assets/Scripts/Player/PlayerController.cs";
            try
            {
                var root = ProjectData.Current?.Path;
                if (!string.IsNullOrEmpty(root))
                {
                    var p = ScriptingService.EnsurePlayerController(root);
                    if (!string.IsNullOrEmpty(p)) scriptRel = p;
                }
            }
            catch { }

            // The Player IS the camera (single entity): the play camera renders from its own transform, so a
            // separate child camera would not follow the moving player. Controller + Camera live together.
            var player = _selectedScene.CreateEntity("Player");
            player.AddComponent(new Camera(player, true)); // main camera
            player.AddComponent(new Script(player, scriptRel));
            var t = player.GetComponent<Transform>();
            if (t != null) t.LocalPosition = new Vector3(0f, 1.7f, 0f); // eye height
            player.IsExpanded = true;

            SelectedEntity = player;
            SelectionService.Instance.RequestFocus(player);
        }

        private void CreateFolder()
        {
            if (_selectedScene == null) return;
            var folder = new GameEntity(_selectedScene, "New Folder");
            folder.IsFolder = true;
            _selectedScene.AddEntity(folder);
            SelectedEntity = folder;
        }

        private void CreateChildEntity()
        {
            if (_selectedScene == null || _selectedEntity == null) return;
            var child = new GameEntity(_selectedScene, "New Child");
            _selectedEntity.AddChild(child);
            _selectedEntity.IsExpanded = true;
            SelectedEntity = child;
            SelectionService.Instance.RequestFocus(child);
        }

        private void CreatePrimitive(PrimitiveType type)
        {
            if (_selectedScene == null) return;
            var entity = _selectedScene.CreatePrimitive(type);
            SelectedEntity = entity;
            SelectionService.Instance.RequestFocus(entity);
        }

        private void CreateCamera()
        {
            if (_selectedScene == null) return;
            var entity = _selectedScene.CreateCamera();
            SelectedEntity = entity;
            SelectionService.Instance.RequestFocus(entity);
        }

        private void CreateLight(LightType type)
        {
            if (_selectedScene == null) return;
            var entity = _selectedScene.CreateLight(type);
            SelectedEntity = entity;
            SelectionService.Instance.RequestFocus(entity);
        }

        private void CreateSkybox()
        {
            if (_selectedScene == null) return;
            var entity = _selectedScene.CreateSkybox();
            SelectedEntity = entity;
        }

        private void CreateAudioSource()
        {
            if (_selectedScene == null) return;
            var entity = new GameEntity(_selectedScene, "Audio Source");
            entity.AddComponent(new AudioSource(entity));
            _selectedScene.AddEntity(entity);
            SelectedEntity = entity;
            SelectionService.Instance.RequestFocus(entity);
        }

        private void CreateUIElement(string type)
        {
            if (_selectedScene == null) return;
            var entity = new GameEntity(_selectedScene, $"UI {type}");
            // TODO: Add UI Component when implemented
            _selectedScene.AddEntity(entity);
            SelectedEntity = entity;
        }

        private void DeleteSelectedEntities()
        {
            if (_selectedScene == null) return;
            
            var entitiesToDelete = GetEntitiesToOperate();
            if (entitiesToDelete.Count == 0) return;

            var command = new DeleteEntitiesCommand(entitiesToDelete);
            UndoRedoManager.Instance.Execute(command);
            
            SelectedEntity = null;
            _selectedEntities.Clear();
            OnPropertyChanged(nameof(HasMultipleSelection));
        }

        private void DuplicateSelectedEntities()
        {
            if (_selectedScene == null) return;
            
            var entitiesToDuplicate = GetEntitiesToOperate();
            if (entitiesToDuplicate.Count == 0) return;

            // Kopiere und f�ge sofort ein
            EntityClipboardService.Instance.Copy(entitiesToDuplicate);
            var pasted = EntityClipboardService.Instance.Paste(_selectedScene, null);
            
            if (pasted.Count > 0)
            {
                _selectedEntities.Clear();
                foreach (var entity in pasted)
                {
                    _selectedEntities.Add(entity);
                }
                SelectedEntity = pasted.First();
            }
        }

        #endregion

        #region Clipboard Methods

        private void CutSelectedEntities()
        {
            var entities = GetEntitiesToOperate();
            if (entities.Count == 0) return;

            EntityClipboardService.Instance.Cut(entities);
            OnPropertyChanged(nameof(CanPaste));
        }

        private void CopySelectedEntities()
        {
            var entities = GetEntitiesToOperate();
            if (entities.Count == 0) return;

            EntityClipboardService.Instance.Copy(entities);
            OnPropertyChanged(nameof(CanPaste));
        }

        private void PasteEntities()
        {
            if (_selectedScene == null || !EntityClipboardService.Instance.HasContent) return;

            // Paste als Kinder der ausgew�hlten Entity oder auf Root-Ebene
            var pasted = EntityClipboardService.Instance.Paste(_selectedScene, _selectedEntity);
            
            if (pasted.Count > 0)
            {
                _selectedEntities.Clear();
                foreach (var entity in pasted)
                {
                    _selectedEntities.Add(entity);
                }
                SelectedEntity = pasted.First();
                
                if (_selectedEntity?.Parent != null)
                {
                    _selectedEntity.Parent.IsExpanded = true;
                }
            }
            
            OnPropertyChanged(nameof(CanPaste));
        }

        private void SelectAll()
        {
            if (_selectedScene?.Entities == null) return;

            // Deselektiere alle vorherigen
            foreach (var e in _selectedEntities)
            {
                e.IsSelected = false;
            }
            _selectedEntities.Clear();
            SelectAllRecursive(_selectedScene.Entities);
            
            if (_selectedEntities.Count > 0)
            {
                SelectedEntity = _selectedEntities.First();
            }
            
            OnPropertyChanged(nameof(HasMultipleSelection));
        }

        private void SelectAllRecursive(ObservableCollection<GameEntity> entities)
        {
            foreach (var entity in entities)
            {
                _selectedEntities.Add(entity);
                entity.IsSelected = true;
                if (entity.Children?.Count > 0)
                {
                    SelectAllRecursive(entity.Children);
                }
            }
        }

        /// <summary>
        /// Gibt die Entities zur�ck, auf die eine Operation angewendet werden soll
        /// </summary>
        private List<GameEntity> GetEntitiesToOperate()
        {
            if (_selectedEntities.Count > 0)
            {
                return _selectedEntities.ToList();
            }
            else if (_selectedEntity != null)
            {
                return new List<GameEntity> { _selectedEntity };
            }
            return new List<GameEntity>();
        }


        /// <summary>
        /// F�gt eine Entity zur Multi-Selektion hinzu (Ctrl+Click)
        /// </summary>
        public void AddToSelection(GameEntity entity)
        {
            if (entity == null) return;
            
            if (!_selectedEntities.Contains(entity))
            {
                _selectedEntities.Add(entity);
                entity.IsSelected = true;
            }
            
            SelectedEntity = entity;
            OnPropertyChanged(nameof(HasMultipleSelection));
        }

        /// <summary>
        /// Entfernt eine Entity aus der Multi-Selektion
        /// </summary>
        public void RemoveFromSelection(GameEntity entity)
        {
            if (entity == null) return;
            
            _selectedEntities.Remove(entity);
            entity.IsSelected = false;
            
            if (_selectedEntity == entity)
            {
                SelectedEntity = _selectedEntities.FirstOrDefault();
            }
            
            OnPropertyChanged(nameof(HasMultipleSelection));
        }

        /// <summary>
        /// Setzt die Selektion auf eine einzelne Entity
        /// </summary>
        public void SetSelection(GameEntity entity)
        {
            // Deselektiere alle vorherigen
            foreach (var e in _selectedEntities)
            {
                e.IsSelected = false;
            }
            _selectedEntities.Clear();
            
            if (entity != null)
            {
                _selectedEntities.Add(entity);
                entity.IsSelected = true;
            }
            
            SelectedEntity = entity;
            OnPropertyChanged(nameof(HasMultipleSelection));
        }

        /// <summary>
        /// Erweitert die Selektion bis zu einer Entity (Shift+Click)
        /// </summary>
        public void ExtendSelection(GameEntity toEntity)
        {
            if (toEntity == null || _selectedEntity == null || _selectedScene == null) return;
            
            // Einfache Implementation: W�hle alle Entities zwischen den beiden aus
            var allEntities = GetAllEntitiesFlat();
            var fromIndex = allEntities.IndexOf(_selectedEntity);
            var toIndex = allEntities.IndexOf(toEntity);
            
            if (fromIndex < 0 || toIndex < 0) return;
            
            var start = Math.Min(fromIndex, toIndex);
            var end = Math.Max(fromIndex, toIndex);
            
            // Deselektiere alle vorherigen
            foreach (var e in _selectedEntities)
            {
                e.IsSelected = false;
            }
            _selectedEntities.Clear();
            
            for (int i = start; i <= end; i++)
            {
                _selectedEntities.Add(allEntities[i]);
                allEntities[i].IsSelected = true;
            }
            
            SelectedEntity = toEntity;
            OnPropertyChanged(nameof(HasMultipleSelection));
        }

        /// <summary>
        /// Gibt alle Entities als flache Liste zur�ck
        /// </summary>
        private List<GameEntity> GetAllEntitiesFlat()
        {
            var result = new List<GameEntity>();
            if (_selectedScene?.Entities != null)
            {
                GetAllEntitiesFlatRecursive(_selectedScene.Entities, result);
            }
            return result;
        }

        private void GetAllEntitiesFlatRecursive(ObservableCollection<GameEntity> entities, List<GameEntity> result)
        {
            foreach (var entity in entities)
            {
                result.Add(entity);
                if (entity.Children?.Count > 0)
                {
                    GetAllEntitiesFlatRecursive(entity.Children, result);
                }
            }
        }

        /// <summary>
        /// Leert die Selektion
        /// </summary>
        public void ClearSelection()
        {
            foreach (var e in _selectedEntities)
            {
                e.IsSelected = false;
            }
            _selectedEntities.Clear();
            SelectedEntity = null;
            OnPropertyChanged(nameof(HasMultipleSelection));
        }

        #endregion

        #region Drag & Drop Methods

        /// <summary>
        /// Verschiebt eine Entity zu einem neuen Parent (Ordner oder Entity)
        /// </summary>
        public void MoveEntityToParent(GameEntity entity, GameEntity newParent)
        {
            if (entity == null || entity == newParent) return;
            
            // Verhindere zirkul�re Referenzen
            if (newParent != null && IsDescendantOf(newParent, entity)) return;

            var command = new MoveEntityCommand(entity, newParent);
            UndoRedoManager.Instance.Execute(command);
        }

        /// <summary>
        /// Verschiebt eine Entity zu einer anderen Scene
        /// </summary>
        public void MoveEntityToScene(GameEntity entity, Scene targetScene)
        {
            if (entity == null || targetScene == null) return;
            if (entity.Scene == targetScene) return;

            var command = new MoveEntityToSceneCommand(entity, targetScene);
            UndoRedoManager.Instance.Execute(command);
        }

        /// <summary>
        /// Verschiebt eine Entity an eine bestimmte Position in der Liste
        /// </summary>
        public void MoveEntityToPosition(GameEntity entity, GameEntity targetEntity, bool insertAfter)
        {
            if (entity == null || targetEntity == null || entity == targetEntity) return;
            
            var command = new ReorderEntityCommand(entity, targetEntity, insertAfter);
            UndoRedoManager.Instance.Execute(command);
        }

        /// <summary>
        /// Pr�ft ob eine Entity ein Nachfahre einer anderen ist
        /// </summary>
        private bool IsDescendantOf(GameEntity potentialDescendant, GameEntity ancestor)
        {
            var current = potentialDescendant;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = current.Parent;
            }
            return false;
        }

        #endregion
    }

    #region Drag & Drop Commands

    /// <summary>
    /// Command zum Verschieben einer Entity zu einem neuen Parent
    /// </summary>
    public class MoveEntityCommand : Core.UndoRedo.UndoableCommandBase
    {
        private readonly GameEntity _entity;
        private readonly GameEntity _newParent;
        private readonly GameEntity _oldParent;
        private readonly Scene _oldScene;
        private readonly int _oldIndex;

        public override string Name => "Move Entity";

        public MoveEntityCommand(GameEntity entity, GameEntity newParent)
        {
            _entity = entity;
            _newParent = newParent;
            _oldParent = entity.Parent;
            _oldScene = entity.Scene;
            
            if (_oldParent != null)
                _oldIndex = _oldParent.Children.IndexOf(entity);
            else if (_oldScene != null)
                _oldIndex = _oldScene.Entities.IndexOf(entity);
        }

        public override void Execute()
        {
            // Entferne von alter Position
            if (_oldParent != null)
                _oldParent.Children.Remove(_entity);
            else
                _oldScene?.Entities.Remove(_entity);

            // F�ge an neuer Position hinzu
            _entity.Parent = _newParent;
            if (_newParent != null)
            {
                _newParent.Children.Add(_entity);
                _entity.Scene = _newParent.Scene;
                _newParent.IsExpanded = true;
            }
            else
            {
                _oldScene?.Entities.Add(_entity);
            }
        }

        public override void Undo()
        {
            // Entferne von aktueller Position
            if (_newParent != null)
                _newParent.Children.Remove(_entity);
            else
                _oldScene?.Entities.Remove(_entity);

            // Stelle alte Position wieder her
            _entity.Parent = _oldParent;
            _entity.Scene = _oldScene;
            
            if (_oldParent != null)
            {
                if (_oldIndex >= 0 && _oldIndex <= _oldParent.Children.Count)
                    _oldParent.Children.Insert(_oldIndex, _entity);
                else
                    _oldParent.Children.Add(_entity);
            }
            else if (_oldScene != null)
            {
                if (_oldIndex >= 0 && _oldIndex <= _oldScene.Entities.Count)
                    _oldScene.Entities.Insert(_oldIndex, _entity);
                else
                    _oldScene.Entities.Add(_entity);
            }
        }
    }

    /// <summary>
    /// Command zum Verschieben einer Entity zu einer anderen Scene
    /// </summary>
    public class MoveEntityToSceneCommand : Core.UndoRedo.UndoableCommandBase
    {
        private readonly GameEntity _entity;
        private readonly Scene _targetScene;
        private readonly Scene _sourceScene;
        private readonly GameEntity _oldParent;
        private readonly int _oldIndex;

        public override string Name => "Move Entity to Scene";

        public MoveEntityToSceneCommand(GameEntity entity, Scene targetScene)
        {
            _entity = entity;
            _targetScene = targetScene;
            _sourceScene = entity.Scene;
            _oldParent = entity.Parent;
            
            if (_oldParent != null)
                _oldIndex = _oldParent.Children.IndexOf(entity);
            else if (_sourceScene != null)
                _oldIndex = _sourceScene.Entities.IndexOf(entity);
        }

        public override void Execute()
        {
            // Entferne aus alter Scene
            if (_oldParent != null)
                _oldParent.Children.Remove(_entity);
            else
                _sourceScene?.Entities.Remove(_entity);

            // F�ge zur neuen Scene hinzu
            _entity.Parent = null;
            _entity.Scene = _targetScene;
            _targetScene.Entities.Add(_entity);
            
            // Aktualisiere Scene-Referenz f�r alle Kinder
            UpdateChildScenes(_entity, _targetScene);
        }

        public override void Undo()
        {
            // Entferne aus Ziel-Scene
            _targetScene.Entities.Remove(_entity);

            // Stelle alte Position wieder her
            _entity.Parent = _oldParent;
            _entity.Scene = _sourceScene;
            UpdateChildScenes(_entity, _sourceScene);
            
            if (_oldParent != null)
            {
                if (_oldIndex >= 0 && _oldIndex <= _oldParent.Children.Count)
                    _oldParent.Children.Insert(_oldIndex, _entity);
                else
                    _oldParent.Children.Add(_entity);
            }
            else if (_sourceScene != null)
            {
                if (_oldIndex >= 0 && _oldIndex <= _sourceScene.Entities.Count)
                    _sourceScene.Entities.Insert(_oldIndex, _entity);
                else
                    _sourceScene.Entities.Add(_entity);
            }
        }

        private void UpdateChildScenes(GameEntity entity, Scene scene)
        {
            foreach (var child in entity.Children)
            {
                child.Scene = scene;
                UpdateChildScenes(child, scene);
            }
        }
    }

    /// <summary>
    /// Command zum Umordnen einer Entity (hoch/runter verschieben)
    /// </summary>
    public class ReorderEntityCommand : Core.UndoRedo.UndoableCommandBase
    {
        private readonly GameEntity _entity;
        private readonly GameEntity _targetEntity;
        private readonly bool _insertAfter;
        private readonly GameEntity _oldParent;
        private readonly int _oldIndex;

        public override string Name => "Reorder Entity";

        public ReorderEntityCommand(GameEntity entity, GameEntity targetEntity, bool insertAfter)
        {
            _entity = entity;
            _targetEntity = targetEntity;
            _insertAfter = insertAfter;
            _oldParent = entity.Parent;
            
            if (_oldParent != null)
                _oldIndex = _oldParent.Children.IndexOf(entity);
            else if (entity.Scene != null)
                _oldIndex = entity.Scene.Entities.IndexOf(entity);
        }

        public override void Execute()
        {
            var targetParent = _targetEntity.Parent;
            var targetCollection = targetParent?.Children ?? _targetEntity.Scene?.Entities;
            var sourceCollection = _oldParent?.Children ?? _entity.Scene?.Entities;
            
            if (targetCollection == null || sourceCollection == null) return;

            // Entferne von alter Position
            sourceCollection.Remove(_entity);

            // Berechne neue Position
            var targetIndex = targetCollection.IndexOf(_targetEntity);
            if (_insertAfter) targetIndex++;
            
            // Wenn gleiche Collection und Entity vorher war, Index anpassen
            if (sourceCollection == targetCollection && _oldIndex < targetIndex)
                targetIndex--;

            // F�ge an neuer Position ein
            _entity.Parent = targetParent;
            if (targetIndex >= 0 && targetIndex <= targetCollection.Count)
                targetCollection.Insert(targetIndex, _entity);
            else
                targetCollection.Add(_entity);
        }

        public override void Undo()
        {
            var currentParent = _entity.Parent;
            var currentCollection = currentParent?.Children ?? _entity.Scene?.Entities;
            var oldCollection = _oldParent?.Children ?? _entity.Scene?.Entities;
            
            if (currentCollection == null) return;

            // Entferne von aktueller Position
            currentCollection.Remove(_entity);

            // Stelle alte Position wieder her
            _entity.Parent = _oldParent;
            if (oldCollection != null)
            {
                if (_oldIndex >= 0 && _oldIndex <= oldCollection.Count)
                    oldCollection.Insert(_oldIndex, _entity);
                else
                    oldCollection.Add(_entity);
            }
        }
    }

    #endregion

    /// <summary>
    /// Einfache ICommand-Implementierung
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object parameter) => _execute(parameter);
    }
}
