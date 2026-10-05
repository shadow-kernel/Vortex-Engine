using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Editor.Core.Assets;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using ModelContextProtocol.Server;
using VortexEditor.Panels;
using VortexEditor.Shell.Material;

namespace VortexEditor.Claude.Tools
{
    /// <summary>Materials (#87): find, read, create and edit .vmat files and assign them — the viewport follows every change.</summary>
    [McpServerToolType, DisplayName("Materials")]
    public static class MaterialTools
    {
        internal static readonly string[] BlendModes = { "Opaque", "AlphaBlend", "AlphaTest", "Additive" };
        internal static readonly string[] ShaderTypes = { "StandardPBR", "Unlit", "Subsurface" };
        private static readonly string[] Images = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".psd", ".hdr", ".dds", ".exr", ".webp" };

        [McpServerTool(Name = "list_materials", ReadOnly = true, Idempotent = true)]
        [Description("The project's materials (.vmat): path, base color, albedo texture, and how many entities of the active scene use each.")]
        public static object ListMaterials(
            [Description("Only paths containing this text")] string filter = null,
            [Description("Maximum results")] int limit = 100)
        {
            var users = UsageCounts();
            var files = Directory.Exists(ProjectFiles.AssetsDir) ? Directory.GetFiles(ProjectFiles.AssetsDir, "*.vmat", SearchOption.AllDirectories) : Array.Empty<string>();
            var list = files.Select(ProjectFiles.Rel)
                .Where(r => string.IsNullOrEmpty(filter) || r.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
            limit = Math.Clamp(limit, 1, 1000);
            return new
            {
                count = list.Count,
                materials = list.Take(limit).Select(r =>
                {
                    var m = VortexMaterial.Load(Path.Combine(ProjectFiles.Root, r));
                    return new
                    {
                        path = r,
                        base_color = m == null ? null : Hex(m.BaseColor),
                        albedo = m?.AlbedoTexture == null ? null : Path.GetFileName(m.AlbedoTexture),
                        blend = m == null || m.BlendMode == "Opaque" ? null : m.BlendMode,
                        used_by = users.TryGetValue(r.ToLowerInvariant(), out int n) ? (int?)n : null,
                    };
                }).ToArray(),
                truncated = list.Count > limit ? (bool?)true : null,
            };
        }

        [McpServerTool(Name = "get_material", ReadOnly = true, Idempotent = true)]
        [Description("All settings of a material (.vmat): colors, PBR values, textures (as project paths, flagged when missing), blend mode, tiling, shader.")]
        public static object GetMaterial([Description("Project path of the .vmat, e.g. Assets/Materials/Rust.vmat")] string material)
        {
            string full = ProjectFiles.Resolve(material, new[] { ".vmat" }, mustExist: true);
            var m = VortexMaterial.Load(full) ?? throw new ToolError(material + " is not a readable material.");
            return Describe(full, m);
        }

        [McpServerTool(Name = "create_material", Destructive = false)]
        [Description("Creates a material (.vmat) in the project — by default in Assets/Materials — with optional properties " +
                     "(same names as set_material_properties) and assigns it to entities if given. One undo step (Undo deletes the file).")]
        public static object CreateMaterial(
            [Description("Material name (also the file name)")] string name,
            [Description("Properties, e.g. {\"base_color\": \"#8a3b2a\", \"roughness\": 0.8, \"metallic\": 0.2}")] JsonElement? properties = null,
            [Description("Folder relative to the project")] string folder = "Assets/Materials",
            [Description("Entities to assign it to")] string[] assign_to = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ToolError("A material name is required.");
            string dir = Path.GetDirectoryName(ProjectFiles.Resolve(Path.Combine(folder ?? "Assets/Materials", "x.vmat").Replace('\\', '/')));
            string full = ProjectFiles.Unique(dir, name, ".vmat");
            var m = new VortexMaterial { Name = name.Trim() };
            var done = new List<string>();
            if (properties.HasValue && properties.Value.ValueKind == JsonValueKind.Object) Apply(m, full, properties.Value, done);
            ProjectFiles.Write(full, Serialize(m), Push);
            string rel = ProjectFiles.Rel(full);
            var assigned = assign_to != null && assign_to.Length > 0 ? Assign(SceneModel.ResolveMany(assign_to), full, -1) : null;
            ToolContext.UndoLabel = "create material " + Path.GetFileNameWithoutExtension(full);
            return new { path = rel, set = done, assigned };
        }

        [McpServerTool(Name = "set_material_properties", Idempotent = true)]
        [Description("Edits a material file — every entity that uses it changes. Properties: base_color (\"#rrggbb\" or [r,g,b,a]), metallic, " +
                     "roughness, ambient_occlusion, normal_strength, height_scale, emissive_color, emissive_strength, alpha_cutoff, blend_mode " +
                     "(Opaque|AlphaBlend|AlphaTest|Additive), shader_type (StandardPBR|Unlit|Subsurface), two_sided, cast_shadows, receive_shadows, " +
                     "uv_tiling [u,v], uv_offset [u,v], real_world_size [w,h] metres, textures as project paths (albedo_texture, normal_texture, " +
                     "roughness_texture, metallic_texture, ao_texture, emissive_texture, height_texture, opacity_texture; \"\" removes), shader_asset " +
                     "(a custom shader from write_shader; \"\" = the built-in PBR shader), footstep_sound.")]
        public static object SetMaterialProperties(
            [Description("Project path of the .vmat")] string material,
            [Description("Properties {\"name\": value}")] JsonElement properties)
        {
            string full = ProjectFiles.Resolve(material, new[] { ".vmat" }, mustExist: true);
            var m = VortexMaterial.Load(full) ?? throw new ToolError(material + " is not a readable material.");
            if (properties.ValueKind != JsonValueKind.Object) throw new ToolError("properties must be an object {\"name\": value}.");
            var done = new List<string>();
            Apply(m, full, properties, done);
            ProjectFiles.Write(full, Serialize(m), Push);
            ToolContext.UndoLabel = "edit material " + Path.GetFileNameWithoutExtension(full);
            int users = UsageCounts().TryGetValue(ProjectFiles.Rel(full).ToLowerInvariant(), out int n) ? n : 0;
            return new { path = ProjectFiles.Rel(full), set = done, used_by_in_scene = users };
        }

        [McpServerTool(Name = "assign_material"), NoDryRun]
        [Description("Gives entities a material. For an imported model, all its submeshes get it unless submesh picks one (0-based). " +
                     "A store material that knows its real-world size, put on a cube/plane floor or wall, is tiled for the object's size " +
                     "(a copy named <material>_<W>x<H>m.vmat).")]
        public static object AssignMaterial(
            [Description("Entities (ids, paths or names)")] string[] entities,
            [Description("Project path of the .vmat")] string material,
            [Description("Submesh index of an imported model (default: all)")] int submesh = -1)
        {
            string full = ProjectFiles.Resolve(material, new[] { ".vmat" }, mustExist: true);
            var list = SceneModel.ResolveMany(entities);
            var result = Assign(list, full, submesh);
            ToolContext.UndoLabel = "assign " + Path.GetFileNameWithoutExtension(full) + (list.Count == 1 ? " to " + list[0].Name : " to " + list.Count + " entities");
            return result;
        }

        // ------------------------------------------------------------------ helpers

        internal static object[] Assign(List<GameEntity> list, string full, int submesh)
        {
            var results = new List<object>();
            foreach (var e in list)
            {
                if (submesh >= 0)
                {
                    var target = e.Children?.FirstOrDefault(c => c.GetComponent<MeshRenderer>()?.MeshPath?.EndsWith("#submesh" + submesh, StringComparison.OrdinalIgnoreCase) == true)
                                 ?? throw new ToolError(e.Name + " has no submesh " + submesh + ".");
                    var mr = target.GetComponent<MeshRenderer>();
                    string rel = ProjectFiles.Rel(full), old = mr.MaterialPath;
                    SceneModel.Set(mr, "MaterialPath", () => mr.MaterialPath, v => mr.MaterialPath = v, rel);
                    results.Add(new { entity = SceneModel.ShortId(target), material = rel });
                    continue;
                }
                if (!ViewportPanel.ApplyMaterial(e, full))
                    throw new ToolError(e.Name + " has no MeshRenderer (nor imported submeshes) to take a material.");
                var mr2 = e.GetComponent<MeshRenderer>();
                results.Add(new { entity = SceneModel.ShortId(e), material = mr2?.MaterialPath ?? ProjectFiles.Rel(full) });
            }
            return results.ToArray();
        }

        /// <summary>After a write, undo or redo: the open scene renders the file as it is now.</summary>
        private static void Push(string full)
        {
            try { MaterialLive.PushToScene(full, VortexMaterial.Load(full)); } catch { }
        }

        internal static string Serialize(VortexMaterial m) => JsonSerializer.Serialize(m, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        internal static void Apply(VortexMaterial m, string matFull, JsonElement props, List<string> done)
        {
            string matDir = Path.GetDirectoryName(matFull);
            foreach (var p in props.EnumerateObject())
            {
                string n = ComponentProps.Norm(p.Name);
                var v = p.Value;
                switch (n)
                {
                    case "basecolor": case "color": case "albedocolor": m.BaseColor = Color(v, true, m.BaseColor); done.Add("base_color"); continue;
                    case "emissivecolor": case "emission": m.EmissiveColor = Color(v, false, null); done.Add("emissive_color"); continue;
                    case "ao": n = "ambientocclusion"; break;
                    case "blendmode": m.BlendMode = OneOf(v, BlendModes, "blend_mode"); done.Add("blend_mode"); continue;
                    case "shadertype": m.ShaderType = OneOf(v, ShaderTypes, "shader_type"); done.Add("shader_type"); continue;
                    case "shaderasset": case "shader":
                    {
                        string s = Str(v);
                        m.ShaderAsset = string.IsNullOrEmpty(s) ? null : ProjectFiles.Rel(ProjectFiles.Resolve(s, new[] { ".hlsl", ".metal", ".glsl", ".vshader" }, mustExist: true));
                        done.Add("shader_asset");
                        continue;
                    }
                    case "footstepsound":
                    {
                        string s = Str(v);
                        m.FootstepSound = string.IsNullOrEmpty(s) ? null : ProjectFiles.Rel(ProjectFiles.Resolve(s, mustExist: true));
                        done.Add("footstep_sound");
                        continue;
                    }
                    case "realworldsize":
                        m.RealWorldSize = v.ValueKind == JsonValueKind.Null ? null : Floats(v, 2, "real_world_size");
                        done.Add("real_world_size");
                        continue;
                    case "name": m.Name = Str(v); done.Add("name"); continue;
                }
                var prop = typeof(VortexMaterial).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(x => x.CanWrite && ComponentProps.Norm(x.Name) == n);
                if (prop == null)
                    throw new ToolError("Unknown material property '" + p.Name + "'. Valid: " + string.Join(", ", ValidNames()));
                if (prop.Name.EndsWith("Texture", StringComparison.Ordinal))
                {
                    string s = Str(v);
                    if (string.IsNullOrEmpty(s)) prop.SetValue(m, null);
                    else
                    {
                        string tex = ProjectFiles.Resolve(s, Images, mustExist: true);
                        prop.SetValue(m, Path.GetRelativePath(matDir, tex).Replace('\\', '/'));
                    }
                }
                else if (prop.PropertyType == typeof(float)) prop.SetValue(m, (float)Num(v, p.Name));
                else if (prop.PropertyType == typeof(bool))
                {
                    if (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False) throw new ToolError(p.Name + " expects true or false.");
                    prop.SetValue(m, v.GetBoolean());
                }
                else if (prop.PropertyType == typeof(float[])) prop.SetValue(m, Floats(v, ((float[])prop.GetValue(m))?.Length ?? 2, p.Name));
                else if (prop.PropertyType == typeof(string)) prop.SetValue(m, Str(v));
                else throw new ToolError(p.Name + " cannot be set by tools.");
                done.Add(ComponentProps.SnakeName(prop.Name));
            }
        }

        private static IEnumerable<string> ValidNames() => new[] { "base_color", "emissive_color", "blend_mode", "shader_type", "shader_asset", "footstep_sound", "real_world_size" }
            .Concat(typeof(VortexMaterial).GetProperties().Where(x => x.CanWrite && x.Name != "Version" && x.Name != "BaseColor" && x.Name != "EmissiveColor"
                                                                        && x.Name != "BlendMode" && x.Name != "ShaderType" && x.Name != "ShaderAsset" && x.Name != "FootstepSound" && x.Name != "RealWorldSize")
                .Select(x => ComponentProps.SnakeName(x.Name)));

        internal static object Describe(string full, VortexMaterial m)
        {
            string dir = Path.GetDirectoryName(full);
            var textures = new Dictionary<string, object>();
            foreach (var prop in typeof(VortexMaterial).GetProperties().Where(x => x.Name.EndsWith("Texture", StringComparison.Ordinal)))
            {
                string val = prop.GetValue(m) as string;
                if (string.IsNullOrEmpty(val)) continue;
                string abs = Path.IsPathRooted(val) ? val : Path.GetFullPath(Path.Combine(dir, val));
                textures[ComponentProps.SnakeName(prop.Name)] = File.Exists(abs) ? (object)ProjectFiles.Rel(abs) : new { path = val, missing = true };
            }
            return new
            {
                path = ProjectFiles.Rel(full),
                name = m.Name,
                base_color = Hex(m.BaseColor),
                metallic = ToolJson.R(m.Metallic),
                roughness = ToolJson.R(m.Roughness),
                ambient_occlusion = ToolJson.R(m.AmbientOcclusion),
                normal_strength = ToolJson.R(m.NormalStrength),
                height_scale = ToolJson.R(m.HeightScale),
                emissive_color = m.EmissiveStrength > 0 ? Hex(m.EmissiveColor) : null,
                emissive_strength = m.EmissiveStrength > 0 ? (double?)ToolJson.R(m.EmissiveStrength) : null,
                blend_mode = m.BlendMode,
                alpha_cutoff = m.BlendMode == "AlphaTest" ? (double?)ToolJson.R(m.AlphaCutoff) : null,
                shader_type = m.ShaderType,
                shader_asset = m.ShaderAsset,
                two_sided = m.TwoSided ? (bool?)true : null,
                cast_shadows = m.CastShadows,
                receive_shadows = m.ReceiveShadows,
                uv_tiling = m.UVTiling?.Select(x => ToolJson.R(x)).ToArray(),
                uv_offset = m.UVOffset != null && m.UVOffset.Any(x => x != 0) ? m.UVOffset.Select(x => ToolJson.R(x)).ToArray() : null,
                real_world_size = m.RealWorldSize?.Select(x => ToolJson.R(x)).ToArray(),
                footstep_sound = m.FootstepSound,
                textures = textures.Count > 0 ? textures : null,
            };
        }

        /// <summary>Lower-cased project path → entities of the active scene that render it.</summary>
        internal static Dictionary<string, int> UsageCounts()
        {
            var d = new Dictionary<string, int>();
            var scene = Editor.Core.Data.ProjectData.Current?.ActiveScene;
            if (scene == null) return d;
            foreach (var e in SceneModel.All(scene))
            {
                string mp = e.GetComponent<MeshRenderer>()?.MaterialPath;
                if (string.IsNullOrEmpty(mp)) continue;
                string key;
                try { key = (Path.IsPathRooted(mp) ? ProjectFiles.Rel(mp) : mp.Replace('\\', '/')).ToLowerInvariant(); } catch { continue; }
                d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            return d;
        }

        private static float[] Color(JsonElement v, bool alpha, float[] current)
        {
            float[] rgb;
            if (v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString().Trim().TrimStart('#');
                if (s.Length == 8 && alpha && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgba))
                    return new[] { ((rgba >> 24) & 255) / 255f, ((rgba >> 16) & 255) / 255f, ((rgba >> 8) & 255) / 255f, (rgba & 255) / 255f };
                rgb = ComponentProps.ParseColor(v);
            }
            else if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 4 && alpha)
            {
                var a = v.EnumerateArray().Select(x => (float)Num(x, "color")).ToArray();
                if (a.Any(x => x > 1f)) a = a.Select(x => x / 255f).ToArray();
                return a;
            }
            else rgb = ComponentProps.ParseColor(v);
            if (!alpha) return rgb;
            float a0 = current != null && current.Length > 3 ? current[3] : 1f;
            return new[] { rgb[0], rgb[1], rgb[2], a0 };
        }

        private static string OneOf(JsonElement v, string[] valid, string what)
        {
            string s = ComponentProps.Norm(Str(v));
            return valid.FirstOrDefault(x => ComponentProps.Norm(x) == s) ?? throw new ToolError(what + " must be one of: " + string.Join(", ", valid) + ".");
        }

        private static string Str(JsonElement v) => v.ValueKind == JsonValueKind.Null ? null : v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();

        private static double Num(JsonElement v, string what)
        {
            if (v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble())) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) return d;
            throw new ToolError(what + " expects a number (got " + v.GetRawText() + ").");
        }

        private static float[] Floats(JsonElement v, int count, string what)
        {
            if (v.ValueKind == JsonValueKind.Number) { float f = (float)v.GetDouble(); return Enumerable.Repeat(f, count).ToArray(); }
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != count) throw new ToolError(what + " expects " + count + " numbers.");
            return v.EnumerateArray().Select(x => (float)Num(x, what)).ToArray();
        }

        internal static string Hex(float[] c)
        {
            if (c == null || c.Length < 3) return null;
            string B(float f) => ((int)Math.Round(Math.Clamp(f, 0f, 1f) * 255)).ToString("x2");
            return "#" + B(c[0]) + B(c[1]) + B(c[2]) + (c.Length > 3 && c[3] < 0.999f ? B(c[3]) : "");
        }
    }
}
