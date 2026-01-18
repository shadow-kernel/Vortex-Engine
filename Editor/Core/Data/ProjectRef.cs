using System;
using System.Runtime.Serialization;

namespace Editor.Core.Data
{
    /// <summary>
    /// Referenz zu einem Projekt-Eintrag in der Registry.
    /// Speichert die grundlegenden Metadaten eines Projekts.
    /// </summary>
    [DataContract(Name = "ProjectRef", Namespace = "")]
    public class ProjectRef : ViewModelBase
    {
        private Guid _id;
        private string _path;
        private string _name;
        private string _imagePath;

        [DataMember(Name = "id", Order = 0)]
        public Guid Id
        {
            get => _id;
            set => SetProperty(ref _id, value, nameof(Id));
        }

        [DataMember(Name = "path", Order = 1)]
        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value, nameof(Path));
        }

        [DataMember(Name = "name", Order = 2)]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value, nameof(Name));
        }

        [DataMember(Name = "imagePath", Order = 3)]
        public string ImagePath
        {
            get => _imagePath;
            set => SetProperty(ref _imagePath, value, nameof(ImagePath));
        }

        public ProjectRef()
        {
            _imagePath = string.Empty;
        }

        public ProjectRef(Guid id, string path, string name) : this()
        {
            Id = id;
            Path = path;
            Name = name;
        }

        public ProjectRef(string path, string name) : this(Guid.NewGuid(), path, name)
        {
        }
    }
}
