using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Editor.Project.Data
{
    public class ProjectEntity : ProjectRef
    {
        public ProjectEntity(string path, string name) : base(path, name)
        {
        }

        public ProjectEntity(Guid id, string path, string name) : base(id, path, name)
        {
        }

    }
}
