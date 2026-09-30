using System;
using System.Runtime.Serialization;

using Editor.Core.Abstractions;

namespace Editor.ECS
{
    /// <summary>
    /// Abstrakte Basisklasse für alle Komponenten.
    /// Dies ist ein reines Daten-Modell für den Editor.
    /// Die eigentliche Engine-Logik wird in C++ implementiert.
    /// </summary>
    [DataContract(Name = "Component", Namespace = "")]
    public abstract class Component : Core.ViewModelBase, IEngineComponent
    {
        private Guid _id;
        private bool _isEnabled = true;

        [DataMember(Name = "id", Order = 0)]
        public Guid Id
        {
            get => _id;
            set => SetProperty(ref _id, value, nameof(Id));
        }

        [DataMember(Name = "isEnabled", Order = 1)]
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value, nameof(IsEnabled));
        }

        /// <summary>
        /// Referenz zur übergeordneten GameEntity (nicht serialisiert - nur Editor)
        /// </summary>
        [IgnoreDataMember]
        public GameEntity Entity { get; set; }

        /// <summary>
        /// Name der Komponente für Anzeige im Inspector
        /// </summary>
        [IgnoreDataMember]
        public abstract string DisplayName { get; }

        /// <summary>
        /// Icon-Code für die Anzeige (Segoe MDL2 Assets)
        /// </summary>
        [IgnoreDataMember]
        public abstract string IconCode { get; }

        /// <summary>
        /// Farbe des Icons (Hex-Format)
        /// </summary>
        [IgnoreDataMember]
        public virtual string IconColor => "#C5C5C5";

        protected Component()
        {
            _id = Guid.NewGuid();
        }

        protected Component(GameEntity entity) : this()
        {
            Entity = entity;
        }

        /// <summary>
        /// Generiert eine neue ID für diese Komponente.
        /// Wird beim Kopieren im Editor verwendet.
        /// </summary>
        public void RegenerateId()
        {
            _id = Guid.NewGuid();
        }
    }
}
