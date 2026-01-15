using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Editor.Project.Data
{
    public class ProjectRef
    {
        public Guid Id { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public string ImagePath { get; set; } = string.Empty;

        public ProjectRef()
        {
        }

        public ProjectRef(Guid id, string path, string name)
        {
            Id = id;
            Path = path;
            Name = name;
        }

        public ProjectRef(string path, string name)
        {
            Id = Guid.NewGuid();
            Path = path;
            Name = name;
        }
    }
}
