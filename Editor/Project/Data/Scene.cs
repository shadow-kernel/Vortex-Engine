using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Editor.Project.Data
{
    public class Scene : ViewModelBase
    {
        private string _name;
        
        [JsonPropertyName("name")]
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        [JsonIgnore]
        public ProjectEntity Project { get; set; }

        public Scene()
        {
        }

        public Scene(ProjectEntity project, string name)
        {
            Debug.Assert(project != null, "Project darf nicht null sein.");
            Project = project;
            Name = name;
        }
    }
}
