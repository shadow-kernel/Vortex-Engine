using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Editor.Core.Assets;
using Editor.Core.Native;
using Editor.DllWrapper;
using ModelContextProtocol.Server;

namespace VortexEditor.Claude.Tools
{
    /// <summary>
    /// Custom material shaders (#87): the platform's template, writing a shader with an immediate compile check
    /// (errors verbatim — write → validate → fix is the loop), hot reload of materials that use it. A material uses a
    /// shader through its shader_asset (set_material_properties).
    /// </summary>
    [McpServerToolType, DisplayName("Materials")]
    public static class ShaderTools
    {
        private static readonly string[] ShaderExts = { ".metal", ".hlsl", ".glsl" };

        private static string Language(string ext) => ext == ".metal" ? "Metal (MSL)" : ext == ".hlsl" ? "HLSL (DirectX 12)" : "GLSL (Vulkan)";

        [McpServerTool(Name = "get_shader_template", ReadOnly = true, Idempotent = true)]
        [Description("The starting point for a custom material shader on this platform — the engine's standard PBR shader in the renderer's " +
                     "language (macOS: Metal .metal, Windows: HLSL .hlsl, Linux: GLSL .glsl) with entry points VSMain / PSMain and the " +
                     "engine's vertex layout and bindings. Change the pixel stage for the look; keep the entry points and bindings.")]
        public static object GetShaderTemplate([Description("Standard, Unlit or Transparent (HLSL); Metal/GLSL start from the standard shader")] string type = "Standard")
        {
            string ext = NativeLoader.MaterialShaderExtension;
            return new
            {
                language = Language(ext),
                extension = ext,
                entry_points = new[] { "VSMain", "PSMain" },
                notes = "Write it with write_shader (it compiles it and reports errors), then set a material's shader_asset to the path. " +
                        "Only this renderer's language compiles here" + (ext == ".hlsl" ? "." : "; a project shipping to Windows also needs an .hlsl twin with the same name."),
                source = Template(ext, type),
            };
        }

        internal static string Template(string ext, string type)
        {
            try
            {
                var dir = NativeLoader.ShaderDirectory;
                if (ext == ".metal")
                {
                    string std = dir != null ? Path.Combine(dir, "standard.metal") : null;
                    if (std != null && File.Exists(std)) return File.ReadAllText(std);
                }
                else if (ext == ".glsl")
                {
                    string tpl = dir != null ? Path.Combine(Path.GetDirectoryName(dir) ?? "", "material_template.glsl") : null;
                    if (tpl != null && File.Exists(tpl)) return File.ReadAllText(tpl);
                }
            }
            catch { }
            var st = string.Equals(type, "Unlit", StringComparison.OrdinalIgnoreCase) ? ShaderType.Unlit
                   : string.Equals(type, "Transparent", StringComparison.OrdinalIgnoreCase) ? ShaderType.Transparent : ShaderType.Standard;
            return VortexShader.HlslTemplate(st);
        }

        [McpServerTool(Name = "write_shader")]
        [Description("Creates or replaces a custom material shader in Assets/Shaders and compiles it right away: returns the compiler errors " +
                     "verbatim (fix and write again), or hot-reloads every material that uses it. Use this renderer's language " +
                     "(get_shader_template). One undo step (Undo restores the previous file).")]
        public static object WriteShader(
            [Description("Shader name or path, e.g. Glow or Shaders/Glow.metal (the extension defaults to this renderer's)")] string shader,
            [Description("The complete shader source")] string code)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ToolError("code is empty.");
            string s = (shader ?? "").Trim().Replace('\\', '/');
            if (s.Length == 0) throw new ToolError("A shader name is required.");
            if (!ShaderExts.Any(x => s.EndsWith(x, StringComparison.OrdinalIgnoreCase))) s += NativeLoader.MaterialShaderExtension;
            if (!s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) s = s.StartsWith("Shaders/", StringComparison.OrdinalIgnoreCase) ? "Assets/" + s : "Assets/Shaders/" + s;
            string full = ProjectFiles.Resolve(s, ShaderExts);
            bool created = !File.Exists(full);
            ProjectFiles.Write(full, code.Replace("\r\n", "\n"), _ => { try { VortexAPI.ReloadMaterialShaders(); } catch { } });
            ToolContext.UndoLabel = (created ? "create shader " : "edit shader ") + Path.GetFileName(full);
            return Check(full, created);
        }

        [McpServerTool(Name = "validate_shader", ReadOnly = true)]
        [Description("Compiles a custom material shader (both stages and a pipeline with the engine's layout) and returns the compiler output.")]
        public static object ValidateShader([Description("Project path of the shader")] string shader)
        {
            string full = ProjectFiles.Resolve(shader, ShaderExts, mustExist: true);
            return Check(full, null);
        }

        private static object Check(string full, bool? created)
        {
            string ext = Path.GetExtension(full).ToLowerInvariant();
            string rel = ProjectFiles.Rel(full);
            var users = Directory.Exists(ProjectFiles.AssetsDir)
                ? Directory.EnumerateFiles(ProjectFiles.AssetsDir, "*.vmat", SearchOption.AllDirectories)
                    .Where(f => { var m = VortexMaterial.Load(f); return m?.ShaderAsset != null && SameShader(m.ShaderAsset, rel); })
                    .Select(ProjectFiles.Rel).Take(20).ToArray()
                : Array.Empty<string>();
            if (ext != NativeLoader.MaterialShaderExtension)
                return new
                {
                    path = rel, created,
                    compiles = (bool?)null,
                    note = "This renderer compiles " + Language(NativeLoader.MaterialShaderExtension) + "; " + Language(ext) + " is checked on its own platform.",
                    used_by = users.Length > 0 ? users : null,
                };
            bool ok;
            string errors;
            try { ok = VortexAPI.ValidateMaterialShader(full, out errors); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                throw new ToolError("The engine library is not available to compile shaders (" + ex.GetType().Name + ").");
            }
            return new
            {
                path = rel,
                created,
                compiles = ok,
                errors = ok ? null : Clean(errors),
                used_by = users.Length > 0 ? users : null,
                hint = ok ? (users.Length == 0 ? "Set a material's shader_asset to " + rel + " to use it." : "Materials using it were hot-reloaded.") : null,
            };
        }

        /// <summary>Same file, or the .hlsl twin a material references while this platform compiles the sibling.</summary>
        private static bool SameShader(string asset, string rel)
        {
            string a = asset.Replace('\\', '/');
            return string.Equals(a, rel, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.ChangeExtension(a, null), Path.ChangeExtension(rel, null), StringComparison.OrdinalIgnoreCase);
        }

        private static string Clean(string errors)
        {
            if (string.IsNullOrWhiteSpace(errors)) return "compile failed (no compiler output)";
            // absolute temp/source paths add nothing for the model
            string root = ProjectFiles.Root.Replace('\\', '/');
            return errors.Replace('\\', '/').Replace(root + "/", "").Trim();
        }
    }
}
