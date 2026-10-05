using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Assets.Library;

namespace Editor.Core.Audio.SoundStudio
{
    /// <summary>
    /// Saving Sound Studio takes (#79, #83): the chosen take goes into the asset library (hash-deduplicated, waveform
    /// thumbnail, tagged SFX / Generated / backend) with its generation recipe, and optionally into the open project
    /// (Assets/Audio) with the recipe in its .vmeta — so "Open in Sound Studio" can regenerate a sibling later.
    /// </summary>
    public static class SoundStudioLibrary
    {
        public sealed class SaveResult
        {
            public bool Success;
            public string Error;
            public LibraryEntry Entry;
            public string ProjectPath;
        }

        /// <summary>A file-name friendly name for a take ("Door creak — slow, heavy").</summary>
        public static string NameFor(GeneratedSound g)
        {
            string n = (g.Label ?? g.Request?.Label ?? g.Request?.Prompt ?? "Generated sound").Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, ' ');
            n = string.Join(" ", n.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return n.Length > 48 ? n.Substring(0, 48).TrimEnd() : n;
        }

        public static SaveResult Save(GeneratedSound g, SoundRecipe recipe, bool addToProject, string projectRoot, string projectName, IEnumerable<string> extraTags = null)
        {
            var res = new SaveResult();
            try
            {
                var backend = SoundBackends.Get(g.BackendId);
                var tags = new List<string> { "SFX", "Generated", backend.Name };
                if (g.Request?.Loop == true) tags.Add("Loop");
                if (extraTags != null) tags.AddRange(extraTags);
                var r = GlobalAssetDatabase.Instance.Register(g.FilePath, new RegisterOptions
                {
                    Name = NameFor(g), Tags = tags, Explicit = true,
                    SourceKind = LibrarySource.Generated, SourceName = backend.Name,
                    License = backend.LicenseId, Redistributable = backend.LicenseId == null,
                    Recipe = recipe?.ToJson(), Notes = g.Request?.Prompt,
                    Companions = new Dictionary<string, string>(),
                });
                if (!r.Success) { res.Error = r.Error; return res; }
                res.Entry = r.Entry;
                if (addToProject && !string.IsNullOrEmpty(projectRoot))
                {
                    var a = LibraryProjects.AddToProject(GlobalAssetDatabase.Instance, r.Entry.Id, projectRoot, null, false, projectName);
                    if (a.Status == AddToProjectStatus.Failed) { res.Error = a.Error; return res; }
                    res.ProjectPath = a.Path;
                }
                res.Success = true;
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        /// <summary>The recipe of a library entry or project asset, or null.</summary>
        public static SoundRecipe RecipeOf(LibraryEntry e) => SoundRecipe.FromJson(e?.Recipe);
    }
}
