using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using Editor.Core.Serialization;

namespace Editor.Core.Services
{
    /// <summary>
    /// A project template surfaced in the "Create New Project" dialog. Either the built-in <see cref="IsEmpty"/>
    /// blank scaffold or a shipped 3D template discovered under the editor's <c>Templates/</c> folder.
    /// </summary>
    public sealed class ProjectTemplate
    {
        public string Id;               // "empty" or the template folder name
        public string Name;
        public string Description;
        public string Tagline;          // short one-liner under the name
        public string ProjectDir;       // absolute dir to copy from (null for the Empty template)
        public string PreviewImagePath; // absolute path to a rendered preview.png (may be null)
        public bool IsEmpty;
        public int Order;
    }

    /// <summary>
    /// Discovers project templates that ship next to the editor. Each template is a folder under
    /// <c>&lt;AppDir&gt;/Templates/</c> containing a <c>template.json</c> (name/description/preview/project) and a
    /// <c>project/</c> subfolder with the actual, copyable Vortex project (or the folder itself IS the project).
    /// Templates are wired into the build via a git submodule + a post-build copy in Editor.csproj.
    /// </summary>
    public static class ProjectTemplateService
    {
        [DataContract]
        private sealed class TemplateManifest
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "tagline")] public string Tagline { get; set; }
            [DataMember(Name = "description")] public string Description { get; set; }
            [DataMember(Name = "preview")] public string Preview { get; set; }
            [DataMember(Name = "project")] public string Project { get; set; }
            [DataMember(Name = "order")] public int Order { get; set; }
        }

        public static string TemplatesRoot()
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates");

        /// <summary>The Empty template first, then any shipped 3D templates (by their declared order, then name).</summary>
        public static List<ProjectTemplate> Discover()
        {
            var list = new List<ProjectTemplate>
            {
                new ProjectTemplate
                {
                    Id = "empty", Name = "Empty Project", IsEmpty = true, Order = -1,
                    Tagline = "A clean slate",
                    Description = "A blank 3D scene with just a camera, a directional light and a ground plane. " +
                                  "Nothing else is included — start from scratch and build exactly the world you want."
                }
            };

            try
            {
                var root = TemplatesRoot();
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        var t = LoadFromFolder(dir);
                        if (t != null) list.Add(t);
                    }
                }
            }
            catch { }

            list.Sort((a, b) =>
            {
                int c = a.Order.CompareTo(b.Order);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private static ProjectTemplate LoadFromFolder(string dir)
        {
            try
            {
                string name = Path.GetFileName(dir);
                string tagline = "3D template", description = "", previewFile = "preview.png", projectSub = "project";
                int order = 0;

                var jsonPath = Path.Combine(dir, "template.json");
                if (File.Exists(jsonPath))
                {
                    var m = SafeLoad(jsonPath);
                    if (m != null)
                    {
                        if (!string.IsNullOrWhiteSpace(m.Name)) name = m.Name;
                        if (!string.IsNullOrWhiteSpace(m.Tagline)) tagline = m.Tagline;
                        if (!string.IsNullOrWhiteSpace(m.Description)) description = m.Description;
                        if (!string.IsNullOrWhiteSpace(m.Preview)) previewFile = m.Preview;
                        if (!string.IsNullOrWhiteSpace(m.Project)) projectSub = m.Project;
                        order = m.Order;
                    }
                }

                // Resolve the copyable project dir: <dir>/<projectSub> if it has a manifest, else <dir> itself.
                string projectDir = Path.Combine(dir, projectSub);
                if (!File.Exists(Path.Combine(projectDir, "project.vortex")))
                {
                    if (File.Exists(Path.Combine(dir, "project.vortex"))) projectDir = dir;
                    else return null;   // no usable project inside -> skip
                }

                string previewPath = Path.Combine(dir, previewFile);
                if (!File.Exists(previewPath)) previewPath = null;

                return new ProjectTemplate
                {
                    Id = Path.GetFileName(dir), Name = name, Tagline = tagline,
                    Description = description, ProjectDir = projectDir, PreviewImagePath = previewPath, Order = order
                };
            }
            catch { return null; }
        }

        private static TemplateManifest SafeLoad(string path)
        {
            try { return DataSerializer.LoadFromJson<TemplateManifest>(path); }
            catch { return null; }
        }
    }
}
