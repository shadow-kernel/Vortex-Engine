using System;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Media;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;

namespace Editor.Core.Data
{
    /// <summary>
    /// Repr�sentiert ein vollst�ndiges Projekt mit allen Szenen und Metadaten.
    /// Erbt von ProjectRef f�r die grundlegenden Projektinformationen.
    /// </summary>
    [DataContract(Name = "Project", Namespace = "")]
    public class ProjectData : ProjectRef
    {
        private DateTime _lastModified;
        private ObservableCollection<Scene> _scenes;
        private Scene _activeScene;

        [DataMember(Name = "lastModified", Order = 10)]
        public DateTime LastModified
        {
            get => _lastModified;
            set => SetProperty(ref _lastModified, value, nameof(LastModified));
        }

        [DataMember(Name = "scenes", Order = 11)]
        public ObservableCollection<Scene> Scenes
        {
            get => _scenes ?? (_scenes = new ObservableCollection<Scene>());
            set => _scenes = value ?? new ObservableCollection<Scene>();
        }

        /// <summary>
        /// Die aktuell aktive Szene (nicht serialisiert - Runtime State)
        /// </summary>
		[IgnoreDataMember]
		public Scene ActiveScene
		{
			get => _activeScene;
			set
			{
				if (_activeScene == value)
					return;

				var previous = _activeScene;
				if (SetProperty(ref _activeScene, value, nameof(ActiveScene)))
				{
					if (previous != null)
						previous.IsActive = false;

					if (_activeScene != null)
						_activeScene.IsActive = true;
				}
			}
		}

        /// <summary>
        /// Die Startszene des fertigen Spiels (z.B. die Lobby) — der gepackte Player bootet IMMER hier hinein,
        /// unabhängig davon, welche Szene im Editor zuletzt offen war. Persistiert im Manifest (StartSceneId),
        /// nicht in ProjectData selbst. Null ⇒ erste Szene.
        /// </summary>
        [IgnoreDataMember]
        public Guid? StartSceneId { get; set; }

        /// <summary>
        /// Thumbnail f�r die Projektliste (nicht serialisiert - wird aus ImagePath geladen)
        /// </summary>
        [IgnoreDataMember]
        public ImageSource Thumbnail { get; set; }

        /// <summary>
        /// Formatierte Anzeige des letzten �nderungsdatums
        /// </summary>
        [IgnoreDataMember]
        public string LastModifiedDisplay => LastModified.ToString("dd.MM.yyyy HH:mm");

        public ProjectData()
        {
            _scenes = new ObservableCollection<Scene>();
        }

        public ProjectData(string path, string name) : base(path, name)
        {
            LastModified = DateTime.Now;
            _scenes = new ObservableCollection<Scene>();
            _scenes.Add(new Scene(this, "Default Scene"));
        }

        public ProjectData(Guid id, string path, string name) : base(id, path, name)
        {
            LastModified = DateTime.Now;
            _scenes = new ObservableCollection<Scene>();
        }

        /// <summary>
        /// Wird aufgerufen wenn das Projekt entladen wird
        /// </summary>
        public void Unload()
        {
            // Cleanup-Logik hier
        }

        /// <summary>
        /// Wird nach der Deserialisierung aufgerufen
        /// </summary>
        [OnDeserialized]
        internal void OnDeserializedMethod(StreamingContext context)
        {
            // Setze Project-Referenz f�r alle Szenen
            if (_scenes != null)
            {
                foreach (var scene in _scenes)
                {
                    scene.Project = this;
                }

				// Setze die erste Szene als aktive Szene
				if (_scenes.Count > 0)
				{
					_activeScene = _scenes[0];
					_activeScene.IsActive = true;
				}
            }
        }

        /// <summary>
        /// F�gt eine neue Szene zum Projekt hinzu (mit Undo/Redo Support)
        /// </summary>
        /// <param name="scene">Die hinzuzuf�gende Szene</param>
        public void AddScene(Scene scene)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));

            scene.Project = this;
            var command = new CollectionAddCommand<Scene>(Scenes, scene, "Scenes");
            UndoRedoManager.Instance.Execute(command);
        }

        /// <summary>
        /// Entfernt eine Szene aus dem Projekt (mit Undo/Redo Support)
        /// </summary>
        /// <param name="scene">Die zu entfernende Szene</param>
        public void RemoveScene(Scene scene)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));

            if (!Scenes.Contains(scene))
                return;

            var command = new CollectionRemoveCommand<Scene>(Scenes, scene, "Scenes");
            UndoRedoManager.Instance.Execute(command);

            // Falls die aktive Szene entfernt wurde, setze eine neue aktive Szene
            if (ActiveScene == scene)
            {
                ActiveScene = Scenes.Count > 0 ? Scenes[0] : null;
            }
        }

        /// <summary>
        /// Gibt das aktuell geladene Projekt zur�ck
        /// </summary>
        private static ProjectData _currentCache;
        // Thread-safe: reads MainWindow.DataContext on the UI thread and caches it, so a dedicated render
        // thread (the standalone game loop) can read the active project without a Dispatcher.VerifyAccess throw.
        public static ProjectData Current
        {
            get
            {
                try
                {
                    var c = (Application.Current != null && Application.Current.MainWindow != null)
                        ? Application.Current.MainWindow.DataContext as ProjectData : null;
                    if (c != null) _currentCache = c;
                    return c ?? _currentCache;
                }
                catch { return _currentCache; } // off the UI thread → last cached project
            }
        }
    }
}
