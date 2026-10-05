using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;
using ModelContextProtocol.Server;

namespace VortexEditor.Claude.Tools
{
    /// <summary>Scripts (#91): write and edit VortexBehaviour C# scripts, compile them (errors with file/line/column),
    /// attach them with field values, and look up the real Vortex scripting API.</summary>
    [McpServerToolType, DisplayName("Scripts")]
    public static class ScriptTools
    {
        private static readonly Regex BehaviourClass = new Regex(@"class\s+(\w+)\s*(?:<[^>]*>)?\s*:\s*[^{]*\bVortexBehaviour\b", RegexOptions.CultureInvariant);

        private static string ScriptsDir => Path.Combine(ProjectFiles.AssetsDir, "Scripts");

        /// <summary>A script path inside Assets/Scripts: "Door", "Door.cs", "Scripts/AI/Guard.cs" or "Assets/Scripts/AI/Guard.cs".</summary>
        private static string ScriptPath(string script, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(script)) throw new ToolError("A script name or path is required.");
            string s = script.Trim().Replace('\\', '/');
            if (!s.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) s += ".cs";
            if (!s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) s = s.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase) ? "Assets/" + s : "Assets/Scripts/" + s;
            string full = ProjectFiles.Resolve(s, new[] { ".cs" }, mustExist);
            string dir = Path.GetFullPath(ScriptsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) throw new ToolError("Scripts live in Assets/Scripts — '" + script + "' is outside it.");
            if (string.Equals(Path.GetFileName(full), "VortexScripting.cs", StringComparison.OrdinalIgnoreCase)) throw new ToolError("VortexScripting.cs is the generated API stub for IDEs; it is not edited.");
            return full;
        }

        [McpServerTool(Name = "list_scripts", ReadOnly = true, Idempotent = true)]
        [Description("The project's scripts (Assets/Scripts) with the VortexBehaviour classes they define and how many entities of the active scene use them.")]
        public static object ListScripts()
        {
            var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var scene = Editor.Core.Data.ProjectData.Current?.ActiveScene;
            if (scene != null)
                foreach (var e in SceneModel.All(scene))
                    foreach (var sc in e.Components.OfType<Script>())
                        if (!string.IsNullOrEmpty(sc.ScriptPath)) uses[sc.ScriptPath.Replace('\\', '/')] = uses.TryGetValue(sc.ScriptPath.Replace('\\', '/'), out int n) ? n + 1 : 1;
            return ScriptingService.EnumerateScripts().Select(rel =>
            {
                string text = "";
                try { text = File.ReadAllText(Path.Combine(ProjectFiles.Root, rel)); } catch { }
                return new
                {
                    path = rel,
                    behaviours = BehaviourClass.Matches(text).Select(m => m.Groups[1].Value).ToArray(),
                    lines = text.Count(c => c == '\n') + 1,
                    used_by = uses.TryGetValue(rel, out int u) ? (int?)u : null,
                };
            }).ToArray();
        }

        [McpServerTool(Name = "read_script", ReadOnly = true, Idempotent = true)]
        [Description("The source of a project script.")]
        public static object ReadScript([Description("Script name or path, e.g. Door or Assets/Scripts/Door.cs")] string script)
        {
            string full = ScriptPath(script, mustExist: true);
            string text = File.ReadAllText(full);
            return new { path = ProjectFiles.Rel(full), lines = text.Count(c => c == '\n') + 1, source = text };
        }

        [McpServerTool(Name = "write_script")]
        [Description("Creates or replaces a script in Assets/Scripts with the given C# source, then compiles the project and returns the " +
                     "errors (file, line, column). A behaviour derives from Vortex.VortexBehaviour (Start, Update(float dt), OnTriggerEnter … — see " +
                     "get_scripting_api). While the game runs, the change hot-reloads. One undo step (Undo restores the old file).")]
        public static object WriteScript(
            [Description("Script name or path, e.g. FlickerLight or AI/Guard.cs")] string script,
            [Description("The complete C# source")] string code)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ToolError("code is empty.");
            string full = ScriptPath(script, mustExist: false);
            bool created = !File.Exists(full);
            ProjectFiles.Write(full, code.Replace("\r\n", "\n"));
            if (created) { try { ScriptingService.EnsureScriptsProject(); } catch { } }
            ToolContext.UndoLabel = (created ? "create script " : "edit script ") + Path.GetFileName(full);
            return AfterWrite(full, created);
        }

        [McpServerTool(Name = "edit_script")]
        [Description("Replaces an exact piece of text in a script (it must occur exactly once unless replace_all), then compiles and returns the " +
                     "errors. Cheaper than write_script for small fixes. One undo step.")]
        public static object EditScript(
            [Description("Script name or path")] string script,
            [Description("Exact text to replace (include enough context to be unique)")] string old_text,
            [Description("Replacement text")] string new_text,
            [Description("Replace every occurrence")] bool replace_all = false)
        {
            string full = ScriptPath(script, mustExist: true);
            if (string.IsNullOrEmpty(old_text)) throw new ToolError("old_text is empty.");
            string text = File.ReadAllText(full).Replace("\r\n", "\n");
            string find = old_text.Replace("\r\n", "\n");
            int count = 0;
            for (int i = text.IndexOf(find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(find, i + find.Length, StringComparison.Ordinal)) count++;
            if (count == 0) throw new ToolError("old_text was not found in " + ProjectFiles.Rel(full) + " (it must match exactly, whitespace included — read_script shows the source).");
            if (count > 1 && !replace_all) throw new ToolError("old_text occurs " + count + " times — add context to make it unique, or set replace_all.");
            ProjectFiles.Write(full, text.Replace(find, new_text ?? ""));
            ToolContext.UndoLabel = "edit script " + Path.GetFileName(full);
            return AfterWrite(full, false, count);
        }

        private static object AfterWrite(string full, bool created, int replaced = 0)
        {
            var diags = Check(false);
            string reload = null;
            if (PlayModeService.Instance.IsPlaying)
            {
                try
                {
                    ScriptRuntime.Instance.ReloadScripts();
                    var outcome = ScriptRuntime.Instance.LastReloadOutcome;
                    reload = outcome == ScriptRuntime.ReloadOutcome.Reloaded ? "hot-reloaded into the running game"
                           : outcome == ScriptRuntime.ReloadOutcome.CompileError ? "compile error — the running game keeps the previous scripts"
                           : "no reload needed";
                }
                catch (Exception ex) { reload = "hot reload failed: " + ex.Message; }
            }
            return new
            {
                path = ProjectFiles.Rel(full),
                created = created ? (bool?)true : null,
                replaced = replaced > 0 ? (int?)replaced : null,
                compiles = diags.Count == 0,
                errors = diags.Count > 0 ? diags.Take(30).Select(Format).ToArray() : null,
                behaviours = BehaviourClass.Matches(File.ReadAllText(full)).Select(m => m.Groups[1].Value).ToArray(),
                play_mode = reload,
            };
        }

        [McpServerTool(Name = "compile_scripts", ReadOnly = true)]
        [Description("Compiles all project scripts exactly like Play does and returns the errors (and optionally warnings) with file, line and column.")]
        public static object CompileScripts([Description("Include warnings")] bool warnings = false)
        {
            var diags = Check(warnings);
            int errors = diags.Count(d => d.IsError);
            return new
            {
                compiles = errors == 0,
                errors,
                warnings = warnings ? (int?)diags.Count(d => !d.IsError) : null,
                messages = diags.Take(60).Select(Format).ToArray(),
                scripts = ScriptingService.EnumerateScripts().Count,
            };
        }

        private static List<RoslynScriptCompiler.ScriptDiagnostic> Check(bool warnings)
        {
            var files = Directory.Exists(ScriptsDir)
                ? Directory.GetFiles(ScriptsDir, "*.cs", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).Equals("VortexScripting.cs", StringComparison.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<string>();
            return RoslynScriptCompiler.Check(files, warnings);
        }

        private static object Format(RoslynScriptCompiler.ScriptDiagnostic d) => new
        {
            file = Rel(d.File),
            line = d.Line,
            column = d.Column,
            severity = d.IsError ? "error" : "warning",
            code = d.Id,
            message = d.Message,
        };

        private static string Rel(string full) { try { return ProjectFiles.Rel(full); } catch { return full; } }

        [McpServerTool(Name = "attach_script")]
        [Description("Adds a script to an entity (its behaviour runs in play mode) and sets public fields of the behaviour: " +
                     "{\"Speed\": 2.5, \"Target\": \"Door\", \"Offset\": [0, 1, 0]} — numbers, bools, strings, enums by name, [x,y,z], arrays. One undo step.")]
        public static object AttachScript(
            [Description("Entity id, path or name")] string entity,
            [Description("Script name or path")] string script,
            [Description("Public field values {\"Field\": value}")] JsonElement? fields = null)
        {
            var e = SceneModel.Resolve(entity);
            string full = ScriptPath(script, mustExist: true);
            string rel = ProjectFiles.Rel(full);
            var sc = e.Components.OfType<Script>().FirstOrDefault(x => string.Equals(x.ScriptPath?.Replace('\\', '/'), rel, StringComparison.OrdinalIgnoreCase));
            bool added = sc == null;
            if (added) { sc = new Script(e, rel); e.AddComponent(sc); }
            string note = null;
            var set = new List<string>();
            if (fields.HasValue && fields.Value.ValueKind == JsonValueKind.Object)
            {
                var type = ScriptRuntime.Instance.GetScriptTypeForInspector(sc.ScriptClassName);
                if (type == null) throw new ToolError("The script's class " + sc.ScriptClassName + " could not be compiled or found, so its fields are unknown — fix compile_scripts errors first (the class name must match the file name).");
                foreach (var p in fields.Value.EnumerateObject())
                {
                    var f = type.GetField(p.Name, BindingFlags.Public | BindingFlags.Instance)
                            ?? type.GetFields(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => ComponentProps.Norm(x.Name) == ComponentProps.Norm(p.Name))
                            ?? throw new ToolError(type.Name + " has no public field '" + p.Name + "'. Fields: " + string.Join(", ", type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(x => ScriptRuntime.IsInspectableFieldType(x.FieldType)).Select(x => x.Name)));
                    if (!ScriptRuntime.IsInspectableFieldType(f.FieldType)) throw new ToolError(f.Name + " (" + f.FieldType.Name + ") cannot be set from the editor.");
                    string formatted = ScriptRuntime.FormatFieldValue(FieldValue(p.Value, f.FieldType, f.Name));
                    string old = sc.GetFieldValue(f.Name);
                    var target = sc; string fname = f.Name;
                    UndoRedoManager.Instance.Execute(new Editor.Core.UndoRedo.Commands.ActionCommand("Set " + fname, () => target.SetFieldValue(fname, formatted), () => target.SetFieldValue(fname, old)));
                    set.Add(f.Name);
                }
            }
            else if (ScriptRuntime.Instance.GetScriptTypeForInspector(sc.ScriptClassName) == null)
                note = "the class " + sc.ScriptClassName + " is not compiled yet (or the file has errors) — check compile_scripts";
            ToolContext.UndoLabel = (added ? "attach " : "set fields of ") + Path.GetFileNameWithoutExtension(rel) + " on " + e.Name;
            return new { entity = SceneModel.ShortId(e), script = rel, behaviour = sc.ScriptClassName, added, fields_set = set.Count > 0 ? set : null, note };
        }

        private static object FieldValue(JsonElement v, Type t, string name)
        {
            if (t == typeof(Vortex.Vector3))
            {
                var e = (Editor.ECS.Vector3)ComponentProps.Convert(v, typeof(Editor.ECS.Vector3), name);
                return new Vortex.Vector3(e.X, e.Y, e.Z);
            }
            if (t.IsArray)
            {
                if (v.ValueKind != JsonValueKind.Array) throw new ToolError(name + " expects an array.");
                var et = t.GetElementType();
                var items = v.EnumerateArray().Select(x => FieldValue(x, et, name)).ToList();
                var arr = Array.CreateInstance(et, items.Count);
                for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                return arr;
            }
            return ComponentProps.Convert(v, t, name);
        }

        // ================================================================== API reference

        [McpServerTool(Name = "get_scripting_api", ReadOnly = true, Idempotent = true)]
        [Description("The real Vortex scripting API (namespace Vortex), read from the engine — use it instead of guessing Unity names. " +
                     "Without topic: the types and a primer. With topic (e.g. VortexBehaviour, Input, Physics, Audio, Scene, Animation): its members.")]
        public static string GetScriptingApi([Description("Type name, e.g. Input")] string topic = null)
        {
            var asm = typeof(Vortex.VortexBehaviour).Assembly;
            var types = asm.GetExportedTypes().Where(t => t.Namespace == "Vortex" && !t.IsNested).OrderBy(t => t.Name).ToList();
            var sb = new StringBuilder();
            if (string.IsNullOrWhiteSpace(topic))
            {
                sb.Append("Vortex scripting API (C#, namespace Vortex). A behaviour:\n\n")
                  .Append("using Vortex;\npublic class Spinner : VortexBehaviour\n{\n    public float Speed = 90f;          // public fields show in the Inspector (attach_script sets them)\n")
                  .Append("    public override void Start() { }\n    public override void Update(float dt) { Rotate(0, Speed * dt, 0); }\n}\n\n")
                  .Append("The class name must match the file name. Types (get_scripting_api topic=<name> for members):\n");
                foreach (var t in types)
                {
                    int n = t.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Count(m => !(m is MethodInfo mi && mi.IsSpecialName));
                    sb.Append("- ").Append(t.Name).Append(t.IsAbstract && t.IsSealed ? " (static)" : t.IsValueType ? " (struct)" : t.IsEnum ? " (enum)" : "").Append(" — ").Append(n).Append(" members\n");
                }
                return sb.ToString();
            }
            var type = types.FirstOrDefault(t => string.Equals(t.Name, topic.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw new ToolError("No type '" + topic + "' in the Vortex API. Types: " + string.Join(", ", types.Select(t => t.Name)));
            sb.Append(type.IsAbstract && type.IsSealed ? "static class " : type.IsValueType ? "struct " : "class ").Append("Vortex.").Append(type.Name).Append('\n');
            if (type.IsEnum) { sb.Append("values: ").Append(string.Join(", ", Enum.GetNames(type))).Append('\n'); return sb.ToString(); }
            const BindingFlags all = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var f in type.GetFields(all)) sb.Append("  ").Append(f.IsStatic ? "static " : "").Append(Tn(f.FieldType)).Append(' ').Append(f.Name).Append('\n');
            foreach (var p in type.GetProperties(all))
            {
                bool st = (p.GetMethod ?? p.SetMethod)?.IsStatic == true;
                sb.Append("  ").Append(st ? "static " : "").Append(Tn(p.PropertyType)).Append(' ').Append(p.Name)
                  .Append(p.CanWrite && p.SetMethod?.IsPublic == true ? " { get; set; }" : " { get; }").Append('\n');
            }
            foreach (var m in type.GetMethods(all).Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
            {
                sb.Append("  ").Append(m.IsStatic ? "static " : m.IsVirtual && !m.IsFinal ? "virtual " : "").Append(Tn(m.ReturnType)).Append(' ').Append(m.Name).Append('(')
                  .Append(string.Join(", ", m.GetParameters().Select(p => Tn(p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : ""))))
                  .Append(")\n");
            }
            foreach (var nt in type.GetNestedTypes(BindingFlags.Public)) sb.Append("  nested ").Append(nt.IsEnum ? "enum " : "type ").Append(nt.Name).Append(nt.IsEnum ? ": " + string.Join(", ", Enum.GetNames(nt)) : "").Append('\n');
            return sb.ToString();
        }

        private static string Tn(Type t)
        {
            if (t == typeof(void)) return "void";
            if (t == typeof(float)) return "float";
            if (t == typeof(int)) return "int";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(double)) return "double";
            if (t == typeof(long)) return "long";
            if (t == typeof(object)) return "object";
            if (t.IsArray) return Tn(t.GetElementType()) + "[]";
            if (t.IsGenericType)
            {
                string n = t.Name.Substring(0, t.Name.IndexOf('`'));
                if (n == "Nullable") return Tn(t.GetGenericArguments()[0]) + "?";
                return n + "<" + string.Join(", ", t.GetGenericArguments().Select(Tn)) + ">";
            }
            return t.Name;
        }
    }
}
