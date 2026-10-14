using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Editor.Scripting
{
    /// <summary>
    /// The scripting API's own documentation at runtime (#404): the XML doc comments of the engine assembly, read from the
    /// compiler's doc file next to it (Vortex.Core.xml, GenerateDocumentationFile), as plain text per type or member.
    /// get_scripting_api lists the API by reflection and prints these under the signatures — a rule that lives in a summary
    /// (a fade envelope multiplies an Audio Source's Volume) used to reach only readers of the source. No doc file
    /// (a trimmed install) = no summaries, nothing else changes.
    /// </summary>
    public static class ScriptApiDocs
    {
        /// <summary>Each assembly's summaries by doc id, read once from its own doc file.</summary>
        private static readonly Dictionary<Assembly, Dictionary<string, string>> _summaries = new Dictionary<Assembly, Dictionary<string, string>>();
        private static readonly object Gate = new object();

        /// <summary>The summary of a type or member as plain text, code samples kept on their own lines; null when it has none.</summary>
        public static string Summary(MemberInfo member)
        {
            if (member == null) return null;
            string id = IdOf(member);
            if (id == null) return null;
            var docs = Load(member is Type t ? t.Assembly : member.DeclaringType?.Assembly);
            return docs != null && docs.TryGetValue(id, out var s) ? s : null;
        }

        /// <summary>Forget every loaded doc file (tests).</summary>
        internal static void Forget() { lock (Gate) _summaries.Clear(); }

        private static Dictionary<string, string> Load(Assembly asm)
        {
            if (asm == null) return null;
            lock (Gate)
            {
                if (_summaries.TryGetValue(asm, out var docs)) return docs;
                docs = new Dictionary<string, string>(StringComparer.Ordinal);
                _summaries[asm] = docs;
                try
                {
                    string file = DocFile(asm);
                    if (file == null) return docs;
                    foreach (var m in XDocument.Load(file).Root?.Element("members")?.Elements("member") ?? Enumerable.Empty<XElement>())
                    {
                        string name = (string)m.Attribute("name");
                        var summary = m.Element("summary");
                        if (name == null || summary == null) continue;
                        string text = Render(summary);
                        if (text.Length > 0) docs[name] = text;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptApiDocs] " + ex.Message); }
                return docs;
            }
        }

        private static string DocFile(Assembly asm)
        {
            if (asm == null) return null;
            string name = asm.GetName().Name + ".xml";
            string loc = null;
            try { loc = asm.Location; } catch { }
            foreach (string dir in new[] { string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc), AppContext.BaseDirectory })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string f = Path.Combine(dir, name);
                if (File.Exists(f)) return f;
            }
            return null;
        }

        // ---- doc ids (ECMA-334 annex D): T:Ns.Type, M:Ns.Type.Method(System.Single,Ns.Vector3@), nested types with '.'

        /// <summary>The XML doc id of a type or member, or null for kinds the doc file does not carry.</summary>
        public static string IdOf(MemberInfo m)
        {
            switch (m)
            {
                case Type t: return "T:" + TypeId(t);
                case FieldInfo f: return "F:" + TypeId(f.DeclaringType) + "." + f.Name;
                case PropertyInfo p:
                    var ip = p.GetIndexParameters();
                    return "P:" + TypeId(p.DeclaringType) + "." + p.Name + Params(ip);
                case EventInfo e: return "E:" + TypeId(e.DeclaringType) + "." + e.Name;
                case MethodBase mb:
                    string name = mb.IsConstructor ? (mb.IsStatic ? "#cctor" : "#ctor") : mb.Name.Replace('.', '#');
                    if (mb.IsGenericMethodDefinition) name += "``" + mb.GetGenericArguments().Length;
                    string id = "M:" + TypeId(mb.DeclaringType) + "." + name + Params(mb.GetParameters());
                    if (mb is MethodInfo mi && (mi.Name == "op_Implicit" || mi.Name == "op_Explicit")) id += "~" + ParamType(mi.ReturnType);
                    return id;
                default: return null;
            }
        }

        private static string TypeId(Type t) => (t.FullName ?? (t.Namespace + "." + t.Name)).Replace('+', '.');

        private static string Params(ParameterInfo[] ps)
            => ps.Length == 0 ? "" : "(" + string.Join(",", ps.Select(p => ParamType(p.ParameterType))) + ")";

        private static string ParamType(Type t)
        {
            if (t.IsByRef) return ParamType(t.GetElementType()) + "@";
            if (t.IsPointer) return ParamType(t.GetElementType()) + "*";
            if (t.IsArray)
                return ParamType(t.GetElementType()) + (t.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", t.GetArrayRank())) + "]");
            if (t.IsGenericParameter) return (t.DeclaringMethod != null ? "``" : "`") + t.GenericParameterPosition;
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                string n = Regex.Replace((def.FullName ?? def.Namespace + "." + def.Name).Replace('+', '.'), @"`\d+", "");
                return n + "{" + string.Join(",", t.GetGenericArguments().Select(ParamType)) + "}";
            }
            return (t.FullName ?? t.Name).Replace('+', '.');
        }

        // ---- the summary as text

        private static string Render(XElement summary)
        {
            var sb = new StringBuilder();
            RenderNodes(summary.Nodes(), sb);
            // one space between words; the line breaks left are the code samples' own
            var lines = sb.ToString().Split('\n').Select(l => l.TrimEnd()).ToList();
            while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join("\n", lines.Select(l => l.StartsWith(" ", StringComparison.Ordinal) && !l.StartsWith("    ", StringComparison.Ordinal) ? l.TrimStart() : l));
        }

        private static void RenderNodes(IEnumerable<XNode> nodes, StringBuilder sb)
        {
            foreach (var node in nodes)
            {
                if (node is XText text) { sb.Append(Regex.Replace(text.Value, @"\s+", " ")); continue; }
                if (!(node is XElement e)) continue;
                switch (e.Name.LocalName)
                {
                    case "see":
                    case "seealso":
                        if (!e.IsEmpty && e.Value.Trim().Length > 0) sb.Append(Regex.Replace(e.Value, @"\s+", " "));
                        else sb.Append((string)e.Attribute("langword") ?? CrefName((string)e.Attribute("cref")));
                        break;
                    case "paramref":
                    case "typeparamref":
                        sb.Append((string)e.Attribute("name"));
                        break;
                    case "code":
                        sb.Append('\n').Append(Code(e.Value)).Append('\n');
                        break;
                    case "para":
                    case "br":
                        sb.Append('\n');
                        RenderNodes(e.Nodes(), sb);
                        sb.Append('\n');
                        break;
                    case "item":
                        sb.Append("\n- ");
                        RenderNodes(e.Nodes(), sb);
                        break;
                    default:
                        RenderNodes(e.Nodes(), sb);
                        break;
                }
            }
        }

        /// <summary>A cref as the name a script writes: "M:Vortex.AudioSource.FadeTo(System.Single,System.Single)" → "AudioSource.FadeTo".</summary>
        private static string CrefName(string cref)
        {
            if (string.IsNullOrEmpty(cref)) return "";
            string s = cref.Length > 2 && cref[1] == ':' ? cref.Substring(2) : cref;
            int paren = s.IndexOf('(');
            if (paren >= 0) s = s.Substring(0, paren);
            if (s.StartsWith("Vortex.", StringComparison.Ordinal)) s = s.Substring("Vortex.".Length);
            else { int dot = s.LastIndexOf('.'); if (dot >= 0) s = s.Substring(dot + 1); }
            return Regex.Replace(s, @"`+\d+", "");
        }

        /// <summary>A code sample, indented four spaces, its own common indentation removed.</summary>
        private static string Code(string code)
        {
            var lines = code.Replace("\r\n", "\n").Split('\n').ToList();
            while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            return string.Join("\n", lines.Select(l => "    " + (l.Length >= indent ? l.Substring(indent) : l.TrimStart())));
        }
    }
}
