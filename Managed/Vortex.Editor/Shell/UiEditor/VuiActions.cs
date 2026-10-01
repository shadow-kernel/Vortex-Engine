using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Editor.Core.Data;

namespace VortexEditor.Shell.UiEditor
{
    /// <summary>
    /// The transparent button ↔ code link of the UI editor (port of the Windows editor): ONE actions class per UI
    /// screen — PauseMenu.vui → Assets/Scripts/UI/PauseMenuActions.cs (class PauseMenuActions). A Button's "On Click"
    /// names a method of that class; the runtime routes the click to it (ScriptRuntime.InvokeUiActions), so screens
    /// never share one dumping-ground file and method names can't collide across UIs.
    /// </summary>
    public static class VuiActions
    {
        /// <summary>PauseMenu.vui → PauseMenuActions.</summary>
        public static string ClassName(string vuiPath)
        {
            var name = Path.GetFileNameWithoutExtension(vuiPath) ?? "Screen";
            var sb = new StringBuilder();
            foreach (char c in name) if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            if (sb.Length == 0) sb.Append("Screen");
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            sb.Append("Actions");
            return sb.ToString();
        }

        /// <summary>Assets/Scripts/UI/&lt;Class&gt;.cs of the open project (null without a project).</summary>
        public static string ScriptPath(string vuiPath)
        {
            var proj = ProjectData.Current?.Path;
            return string.IsNullOrEmpty(proj) ? null : Path.Combine(proj, "Assets", "Scripts", "UI", ClassName(vuiPath) + ".cs");
        }

        /// <summary>A valid C# method name from what the user typed (null when nothing usable is left).</summary>
        public static string SanitizeMethod(string raw)
        {
            var sb = new StringBuilder();
            foreach (char c in (raw ?? "").Trim()) if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            if (sb.Length == 0) return null;
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        /// <summary>Create the screen's actions file (empty class) if it doesn't exist yet. Returns its path (or null).</summary>
        public static string EnsureFile(string vuiPath)
        {
            try
            {
                var path = ScriptPath(vuiPath);
                if (path == null) return null;
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string cls = ClassName(vuiPath), screen = Path.GetFileName(vuiPath);
                    File.WriteAllText(path,
                        "using Vortex;\n\n" +
                        "// Button actions for " + screen + " — ONE class per UI screen.\n" +
                        "// Each Button's \"On Click\" method lands here and is called when that button is clicked.\n" +
                        "// The engine wires this up automatically (no scene attachment needed): a click on " + screen + "\n" +
                        "// is routed to " + cls + ". UI actions use the static facades (Scene.Load, Application.Quit,\n" +
                        "// Settings.*, Gui.*) — they have no scene entity, so don't use Position/Rotation here.\n" +
                        "public class " + cls + " : VortexBehaviour\n{\n}\n");
                }
                return path;
            }
            catch { return null; }
        }

        /// <summary>True when the actions class already declares the method.</summary>
        public static bool HasMethod(string vuiPath, string method)
        {
            try
            {
                var path = ScriptPath(vuiPath);
                if (path == null || string.IsNullOrEmpty(method) || !File.Exists(path)) return false;
                return Regex.IsMatch(File.ReadAllText(path), @"\b" + Regex.Escape(method) + @"\s*\(");
            }
            catch { return false; }
        }

        /// <summary>Append an empty handler for the method to the actions class (no-op when it exists).</summary>
        public static bool EnsureStub(string vuiPath, string method)
        {
            try
            {
                var path = EnsureFile(vuiPath);
                if (path == null || string.IsNullOrEmpty(method)) return false;
                var text = File.ReadAllText(path);
                if (Regex.IsMatch(text, @"\b" + Regex.Escape(method) + @"\s*\(")) return true;
                int idx = text.LastIndexOf('}');
                if (idx < 0) return false;
                string stub = "\n    public void " + method + "()\n    {\n        // TODO: handle the '" + method + "' button click\n    }\n";
                File.WriteAllText(path, text.Substring(0, idx) + stub + text.Substring(idx));
                return true;
            }
            catch { return false; }
        }
    }
}
