using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Editor.Project.Control
{
    internal class ProjectManager
    {
        private static readonly ProjectManager instance;
        private static readonly string appDataPath;

        public static ProjectManager Instance => instance;

        static ProjectManager()
        {
            instance = new ProjectManager();
            appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/VortexEngine";

            if(!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }
        }

        private ProjectManager()
        {
        }

    }
}
