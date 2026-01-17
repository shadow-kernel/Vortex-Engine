using System;
using System.Diagnostics;
using System.Runtime.Serialization;

namespace Editor.Core.Data
{
    /// <summary>
    /// Repräsentiert eine Szene im Projekt.
    /// Szenen enthalten GameObjects und können binär oder als JSON gespeichert werden.
    /// </summary>
    [DataContract(Name = "Scene", Namespace = "")]
    public class Scene : ViewModelBase
    {
        private string _name;
        private Guid _id;

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

        /// <summary>
        /// Referenz zum übergeordneten Projekt (nicht serialisiert)
        /// </summary>
        [IgnoreDataMember]
        public ProjectData Project { get; set; }

        public Scene()
        {
            _id = Guid.NewGuid();
        }

        public Scene(ProjectData project, string name) : this()
        {
            Debug.Assert(project != null, "Project darf nicht null sein.");
            Project = project;
            Name = name;
        }

        /// <summary>
        /// Wird nach der Deserialisierung aufgerufen um die Parent-Referenz wiederherzustellen
        /// </summary>
        [OnDeserialized]
        internal void OnDeserializedMethod(StreamingContext context)
        {
            // Project wird vom ProjectService nach dem Laden gesetzt
        }
    }
}
