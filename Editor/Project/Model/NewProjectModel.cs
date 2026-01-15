using Editor;
using Editor.Project.Control;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Editor.Project.Model
{
    public class NewProjectModel : ViewModelBase
    {
        public NewProjectModel()
        {
            var manager = ProjectManager.Instance;
        }

        private string _projectName = "New Project";
        public string ProjectName
        {
            get => _projectName;
            set
            {
                if (_projectName != value)
                {
                    _projectName = value;
                    OnPropertyChanged(nameof(ProjectName));

                    Path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path), _projectName);
                }
            }
        }

        private string _path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VortexEngineProjects", "New Project");
        public string Path
        {
            get => _path;
            set
            {
                if (_path != value)
                {
                    _path = value;
                    OnPropertyChanged(nameof(Path));
                }
            }
        }


    }
}
