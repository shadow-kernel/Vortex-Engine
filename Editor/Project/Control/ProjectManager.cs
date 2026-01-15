using Editor.Project.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Editor.Project.Control
{
    public class ProjectManager
    {
        private static readonly ProjectManager instance;

        public static ProjectManager Instance => instance;

        static ProjectManager()
        {
            instance = new ProjectManager();
            var a = ProjectFileManager.Instance;
            var obj = new ProjectEntity("C:\\Users\\kernel\\VortexEngineProjects\\New Project", "Test Project");
            a.SaveProjectFile(obj);
            /*
            
            */
        }

        private ProjectManager()
        {

        }

    }
}
