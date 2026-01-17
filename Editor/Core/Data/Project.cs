using System;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Editor.Core.Data
{
    /// <summary>
    /// Repräsentiert ein vollständiges Projekt mit allen Szenen und Metadaten.
    /// Erbt von ProjectRef für die grundlegenden Projektinformationen.
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
            set => SetProperty(ref _activeScene, value, nameof(ActiveScene));
        }

        /// <summary>
        /// Thumbnail für die Projektliste (nicht serialisiert - wird aus ImagePath geladen)
        /// </summary>
        [IgnoreDataMember]
        public ImageSource Thumbnail { get; set; }

        /// <summary>
        /// Formatierte Anzeige des letzten Änderungsdatums
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
            // Setze Project-Referenz für alle Szenen
            if (_scenes != null)
            {
                foreach (var scene in _scenes)
                {
                    scene.Project = this;
                }
            }
        }

        /// <summary>
        /// Gibt das aktuell geladene Projekt zurück
        /// </summary>
        public static ProjectData Current => Application.Current?.MainWindow?.DataContext as ProjectData;
    }
}
