using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Editor.Project.Data
{
    public class ProjectFileRef : ViewModelBase
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }
        [JsonPropertyName("path")]
        public string Path { get; set; }
        [JsonPropertyName("name")]
        public string Name { get; set; }
        [JsonPropertyName("image_path")]
        public string ImagePath { get; set; } = string.Empty;

        public ProjectFileRef()
        {
        }

        public ProjectFileRef(Guid id, string path, string name)
        {
            Id = id;
            Path = path;
            Name = name;
        }

        public ProjectFileRef(string path, string name)
        {
            Id = Guid.NewGuid();
            Path = path;
            Name = name;
        }
    }
}
