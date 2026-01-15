using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Editor.Project.Data
{
    public class ProjectEntity : ProjectRef
    {
        public DateTime LastModified { get; set; }
        public ImageSource Thumbnail { get; set; }
        public string LastModifiedDisplay => LastModified.ToString("dd.MM.yyyy HH:mm");

        public ProjectEntity(string path, string name) : base(path, name)
        {
            LastModified = DateTime.Now;
        }

        public ProjectEntity(Guid id, string path, string name) : base(id, path, name)
        {
            LastModified = DateTime.Now;
        }

    }
}
