using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Editor.Core.Services
{
    /// <summary>A file the template update would add or overwrite.</summary>
    public sealed class TemplateFileChange
    {
        /// <summary>Path relative to the project ("Assets/Scripts/PlayerRig.cs").</summary>
        public string Path { get; set; }
        /// <summary>The project does not have the file yet.</summary>
        public bool IsNew { get; set; }
        public long Size { get; set; }
        /// <summary>The top folder it belongs to ("Scripts", "Models" …) — the dialog groups by it.</summary>
        public string Group { get; set; }
    }

    /// <summary>What updating a project from a template would do — the dry run the dialog shows.</summary>
    public sealed class TemplateUpdatePlan
    {
        public string ProjectDir { get; set; }
        public string TemplateDir { get; set; }
        public List<TemplateFileChange> Changes { get; } = new List<TemplateFileChange>();
        /// <summary>Template scenes the project's scene list does not have yet (names).</summary>
        public List<string> NewScenes { get; } = new List<string>();
        public int Unchanged { get; set; }
        public int Overwrites => Changes.Count(c => !c.IsNew);
        public int Additions => Changes.Count(c => c.IsNew);
    }

    /// <summary>What <see cref="TemplateUpdateService.Apply"/> did.</summary>
    public sealed class TemplateUpdateResult
    {
        /// <summary>The project's previous versions of the overwritten files and project.vortex.</summary>
        public string BackupDir { get; set; }
        public int FilesCopied { get; set; }
        /// <summary>Scenes added to the project's scene list.</summary>
        public List<string> ScenesAdded { get; } = new List<string>();
    }

    /// <summary>
    /// Project Hub ▸ Update from Template (#186): brings an existing game up to the current content of the template it
    /// came from. Template files are copied in (scripts, prefabs, materials, textures, models, audio, scenes, shaders,
    /// animations, UI) — new ones added, changed ones overwritten after a backup of the project's version; nothing the
    /// user added is deleted. The scene list of project.vortex gains the template's new scenes. <see cref="Plan"/> is
    /// the dry run; <see cref="Apply"/> backs up into .ve/backups/template-update-&lt;time&gt;/ and copies.
    /// </summary>
    public static class TemplateUpdateService
    {
        public static readonly string[] Folders = { "Scripts", "Prefabs", "Materials", "Textures", "Models", "Audio", "Scenes", "Shaders", "Animations", "UI", "Fonts" };
        public static readonly string[] RootFiles = { "WEAPONS_GUIDE.md", "CHARACTER_SETUP_GUIDE.md", "ATTRIBUTIONS.txt" };

        /// <summary>Compare a project with a template's project folder (content, not dates). <paramref name="folders"/>
        /// limits the update to some top folders (null = all).</summary>
        public static TemplateUpdatePlan Plan(string projectDir, string templateDir, IEnumerable<string> folders = null)
        {
            if (!File.Exists(System.IO.Path.Combine(projectDir, "project.vortex"))) throw new InvalidOperationException("Not a Vortex project: " + projectDir);
            if (!File.Exists(System.IO.Path.Combine(templateDir, "project.vortex"))) throw new InvalidOperationException("Not a template project: " + templateDir);
            if (TemplatePacks.HasLfsPointers(templateDir)) throw new InvalidOperationException("The template's content is not installed (Git LFS pointers) — download it first.");
            var plan = new TemplateUpdatePlan { ProjectDir = projectDir, TemplateDir = templateDir };
            var wanted = new HashSet<string>(folders ?? Folders, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Folders.Where(wanted.Contains))
            {
                string src = System.IO.Path.Combine(templateDir, "Assets", folder);
                if (!Directory.Exists(src)) continue;
                foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    string name = System.IO.Path.GetFileName(file);
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                    string rel = Rel(templateDir, file);
                    Compare(plan, file, System.IO.Path.Combine(projectDir, rel), rel, folder);
                }
            }
            foreach (var f in RootFiles)
            {
                string file = System.IO.Path.Combine(templateDir, f);
                if (File.Exists(file)) Compare(plan, file, System.IO.Path.Combine(projectDir, f), f, "Guides");
            }
            plan.NewScenes.AddRange(NewSceneNames(projectDir, templateDir));
            return plan;
        }

        private static void Compare(TemplateUpdatePlan plan, string src, string dst, string rel, string group)
        {
            var si = new FileInfo(src);
            if (!File.Exists(dst)) { plan.Changes.Add(new TemplateFileChange { Path = rel, IsNew = true, Size = si.Length, Group = group }); return; }
            var di = new FileInfo(dst);
            if (si.Length == di.Length && SameContent(src, dst)) { plan.Unchanged++; return; }
            plan.Changes.Add(new TemplateFileChange { Path = rel, IsNew = false, Size = si.Length, Group = group });
        }

        private static bool SameContent(string a, string b)
        {
            using (var sha = SHA256.Create())
            {
                byte[] ha, hb;
                using (var fa = File.OpenRead(a)) ha = sha.ComputeHash(fa);
                using (var fb = File.OpenRead(b)) hb = sha.ComputeHash(fb);
                return ha.SequenceEqual(hb);
            }
        }

        private static List<string> NewSceneNames(string projectDir, string templateDir)
        {
            var result = new List<string>();
            try
            {
                var p = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(projectDir, "project.vortex")));
                var t = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(templateDir, "project.vortex")));
                var have = new HashSet<string>(((p?["scenes"] as JsonArray) ?? new JsonArray()).Select(s => (string)s?["path"]).Where(s => s != null), StringComparer.OrdinalIgnoreCase);
                foreach (var s in (t?["scenes"] as JsonArray) ?? new JsonArray())
                {
                    string path = (string)s?["path"];
                    if (path == null || have.Contains(path)) continue;
                    if (SceneFile(templateDir, path) == null && SceneFile(projectDir, path) == null) continue;   // listed, but no file to add
                    result.Add((string)s["name"] ?? path);
                }
            }
            catch { }
            return result;
        }

        /// <summary>The scene file a manifest entry points at ("Yard.vscene" lives in Assets/Scenes), or null.</summary>
        private static string SceneFile(string projectDir, string path)
        {
            foreach (var candidate in new[] { System.IO.Path.Combine(projectDir, "Assets", "Scenes", path), System.IO.Path.Combine(projectDir, path) })
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        /// <summary>Back up the files that get overwritten (and project.vortex), copy the plan's files (all, or the
        /// <paramref name="selection"/>), add the template scenes whose files the project now has to its scene list.</summary>
        public static TemplateUpdateResult Apply(TemplateUpdatePlan plan, IEnumerable<TemplateFileChange> selection = null)
        {
            var changes = (selection ?? plan.Changes).ToList();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string backup = System.IO.Path.Combine(plan.ProjectDir, ".ve", "backups", "template-update-" + stamp);
            Directory.CreateDirectory(backup);
            string manifest = System.IO.Path.Combine(plan.ProjectDir, "project.vortex");
            File.Copy(manifest, System.IO.Path.Combine(backup, "project.vortex"), true);
            foreach (var c in changes.Where(c => !c.IsNew))
            {
                string from = System.IO.Path.Combine(plan.ProjectDir, c.Path);
                string to = System.IO.Path.Combine(backup, c.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
                File.Copy(from, to, true);
            }
            foreach (var c in changes)
            {
                string from = System.IO.Path.Combine(plan.TemplateDir, c.Path);
                string to = System.IO.Path.Combine(plan.ProjectDir, c.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
                File.Copy(from, to, true);
            }
            var result = new TemplateUpdateResult { BackupDir = backup, FilesCopied = changes.Count };
            if (plan.NewScenes.Count > 0) result.ScenesAdded.AddRange(MergeScenes(plan.ProjectDir, System.IO.Path.Combine(plan.TemplateDir, "project.vortex")));
            File.WriteAllText(System.IO.Path.Combine(backup, "README.txt"),
                "Template update of " + DateTime.Now.ToString("u", System.Globalization.CultureInfo.InvariantCulture) + " from " + plan.TemplateDir + "\r\n" +
                "Your previous versions of the overwritten files and project.vortex — copy them back to undo.\r\n");
            return result;
        }

        /// <summary>Adds the template's scenes the project lacks — only those whose file the project has (a scene the
        /// user did not take stays out of the list).</summary>
        private static List<string> MergeScenes(string projectDir, string templateManifest)
        {
            var added = new List<string>();
            string projectManifest = System.IO.Path.Combine(projectDir, "project.vortex");
            string text = File.ReadAllText(projectManifest);
            bool bom = File.ReadAllBytes(projectManifest).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF });
            var p = JsonNode.Parse(text) as JsonObject;
            var t = JsonNode.Parse(File.ReadAllText(templateManifest)) as JsonObject;
            if (p == null || t == null) return added;
            if (!(p["scenes"] is JsonArray scenes)) { scenes = new JsonArray(); p["scenes"] = scenes; }
            var have = new HashSet<string>(scenes.Select(s => (string)s?["path"]).Where(s => s != null), StringComparer.OrdinalIgnoreCase);
            foreach (var s in (t["scenes"] as JsonArray) ?? new JsonArray())
            {
                string path = (string)s?["path"];
                if (path == null || have.Contains(path) || SceneFile(projectDir, path) == null) continue;
                scenes.Add(JsonNode.Parse(s.ToJsonString()));
                added.Add((string)s["name"] ?? path);
            }
            if (added.Count == 0) return added;
            string json = p.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(projectManifest, json, new UTF8Encoding(bom));
            return added;
        }

        private static string Rel(string root, string full)
        {
            string r = full.Substring(root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).Length + 1);
            return r.Replace('\\', '/');
        }

        /// <summary>Best guess of the template a project came from (scripts project / name), or null.</summary>
        public static ProjectTemplate GuessTemplate(string projectDir, IEnumerable<ProjectTemplate> templates)
        {
            var list = templates.Where(t => !t.IsEmpty && t.ProjectDir != null).ToList();
            foreach (var t in list)
            {
                // a template's own scripts project ("HorrorStarterScripts.csproj") copied into the project
                foreach (var csproj in Directory.Exists(t.ProjectDir) ? Directory.GetFiles(t.ProjectDir, "*Scripts.csproj") : new string[0])
                    if (!System.IO.Path.GetFileName(csproj).StartsWith("NewProject", StringComparison.Ordinal) && File.Exists(System.IO.Path.Combine(projectDir, System.IO.Path.GetFileName(csproj)))) return t;
                // or a scene file of the template
                if (Directory.Exists(System.IO.Path.Combine(t.ProjectDir, "Assets", "Scenes")))
                    foreach (var scene in Directory.GetFiles(System.IO.Path.Combine(t.ProjectDir, "Assets", "Scenes"), "*.vscene"))
                        if (File.Exists(System.IO.Path.Combine(projectDir, "Assets", "Scenes", System.IO.Path.GetFileName(scene)))) return t;
            }
            return null;
        }
    }
}
