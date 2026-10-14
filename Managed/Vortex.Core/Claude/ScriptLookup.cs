using System;
using System.Collections.Generic;
using System.Linq;

namespace Editor.Core.Claude
{
    /// <summary>
    /// Which project script a tool means (#456, #494). Claude passes "Weapon", "Weapon.cs", "Player/Weapon",
    /// "Scripts/Player/Weapon.cs" or "Assets/Scripts/Player/Weapon.cs"; this turns it into one file under Assets/Scripts,
    /// the only folder the script compiler reads. A path names its file exactly. A bare name is looked up by file name
    /// in every subfolder — the way list_scripts shows the scripts — because projects keep their scripts in folders
    /// (Player/, AI/, Gameplay/) and a behaviour's class is its file name, so the name is unique in practice. Two files
    /// of that name are reported with both paths instead of one being picked; a name that matches nothing lists the
    /// scripts with similar names. Every script tool (read, write, edit, attach) resolves through here.
    /// </summary>
    public static class ScriptLookup
    {
        /// <summary>Where scripts live, project-relative.</summary>
        public const string ScriptsFolder = "Assets/Scripts/";

        /// <summary>The script a reference names: <see cref="Path"/> when one was chosen, otherwise <see cref="Error"/>.</summary>
        public sealed class Result
        {
            /// <summary>Project-relative, '/'-separated ("Assets/Scripts/Player/Weapon.cs"); null when <see cref="Error"/> is set.</summary>
            public string Path;
            /// <summary>The file exists; false = a script that writing will create.</summary>
            public bool Exists;
            /// <summary>Why no script was chosen, worded for the model (null when <see cref="Path"/> is set).</summary>
            public string Error;
            /// <summary>Nothing of that name is in Assets/Scripts (the error is "not found", not "ambiguous").</summary>
            public bool Missing;
            /// <summary>Other scripts of the same file name — a new file at <see cref="Path"/> would define their class twice.</summary>
            public List<string> SameName = new List<string>();
        }

        /// <param name="script">What the caller passed (an absolute path is turned into a project-relative one before).</param>
        /// <param name="scripts">The project's scripts, project-relative — <c>ScriptingService.EnumerateScripts()</c>.</param>
        /// <param name="mustExist">True to read / edit / attach an existing script; false to write one (a new name
        /// becomes Assets/Scripts/&lt;name&gt;.cs, an existing one is replaced where it is).</param>
        public static Result Resolve(string script, IEnumerable<string> scripts, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(script)) return Fail("A script name or path is required.");
            string s = script.Trim().Replace('\\', '/');
            while (s.StartsWith("./", StringComparison.Ordinal)) s = s.Substring(2);
            s = s.TrimStart('/');
            if (!s.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) s += ".cs";
            bool bare = s.IndexOf('/') < 0;
            string rel = s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? s
                       : s.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase) ? "Assets/" + s
                       : ScriptsFolder + s;
            var all = (scripts ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p.Replace('\\', '/'))
                      .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            string file = FileName(rel);
            var sameName = all.Where(p => string.Equals(FileName(p), file, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!bare)
            {
                // a path names its file — exactly
                string hit = all.FirstOrDefault(p => string.Equals(p, rel, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return new Result { Path = hit, Exists = true, SameName = sameName.Where(p => !string.Equals(p, hit, StringComparison.OrdinalIgnoreCase)).ToList() };
                if (!mustExist) return new Result { Path = rel, SameName = sameName };
                if (sameName.Count > 0) return Fail("There is no file '" + rel + "' in the project. A script of that name is at " + string.Join(", ", sameName) + ".");
                return Missing("There is no file '" + rel + "' in the project." + Similar(file, all));
            }

            // a bare name: every folder of Assets/Scripts, the top level included — two of them are ambiguous
            if (sameName.Count == 1) return new Result { Path = sameName[0], Exists = true };
            if (sameName.Count > 1)
                return Fail("'" + script.Trim() + "' matches " + sameName.Count + " scripts — pass the path of the one you mean: " + string.Join(", ", sameName) + ".");
            if (!mustExist) return new Result { Path = rel };   // a new script at the top of Assets/Scripts
            return Missing("There is no script '" + Stem(file) + "' in Assets/Scripts or its subfolders." + Similar(file, all));
        }

        private static Result Fail(string message) => new Result { Error = message };
        private static Result Missing(string message) => new Result { Error = message, Missing = true };

        private static string FileName(string rel)
        {
            int i = rel.LastIndexOf('/');
            return i < 0 ? rel : rel.Substring(i + 1);
        }

        private static string Stem(string file) => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 3) : file;

        /// <summary>" Similar: …" — scripts whose name contains the wanted one (or the other way round) or is a typo away.</summary>
        private static string Similar(string file, List<string> all)
        {
            if (all.Count == 0) return " The project has no scripts yet — write_script creates one.";
            string want = Stem(file).ToLowerInvariant();
            var close = all.Where(p =>
            {
                string n = Stem(FileName(p)).ToLowerInvariant();
                return n.Contains(want) || (n.Length >= 3 && want.Contains(n)) || Distance(n, want) <= Math.Max(1, want.Length / 4);
            }).Take(8).ToList();
            return close.Count > 0 ? " Similar: " + string.Join(", ", close) + "." : " list_scripts shows every script.";
        }

        private static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
