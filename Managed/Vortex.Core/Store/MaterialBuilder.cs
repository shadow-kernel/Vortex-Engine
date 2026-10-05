using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Editor.Core.Assets.Store
{
    /// <summary>
    /// Turns a folder of PBR maps (an ambientCG zip, a Poly Haven texture set) into a ready <c>.vmat</c> (#69): map roles
    /// come from the provider when it knows them, otherwise from the file names (Color / Diffuse / BaseColor, NormalGL /
    /// nor_gl, NormalDX / nor_dx, Roughness, Metalness, AmbientOcclusion, Displacement, Opacity, Emission, ARM packs).
    /// Texture paths are written relative to the material, so the folder can move as a whole.
    /// </summary>
    public static class MaterialBuilder
    {
        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".webp" };

        public static bool IsImage(string path) => Array.IndexOf(ImageExtensions, Path.GetExtension(path ?? "").ToLowerInvariant()) >= 0;

        /// <summary>The role a map plays judging by its file name, or null.</summary>
        public static string RoleOf(string path)
        {
            string n = Path.GetFileNameWithoutExtension(path ?? "").ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            var t = n.Split('_');
            bool Has(params string[] words) => words.Any(w => t.Contains(w));
            bool Contains(params string[] parts) => parts.Any(p => n.Contains(p));
            if (Contains("preview", "thumb", "sample", "sphere", "cube_")) return null;
            if (Contains("normalgl", "nor_gl", "normal_gl", "nrm_gl")) return "NormalGL";
            if (Contains("normaldx", "nor_dx", "normal_dx", "nrm_dx")) return "NormalDX";
            if (Has("arm", "orm") || Contains("occlusionroughnessmetallic")) return "ARM";
            if (Contains("rough_ao", "roughao")) return null;   // packed roughness+AO with no standard channel order — skip
            if (Contains("basecolor", "base_color", "albedo", "diffuse") || Has("color", "col", "diff", "diffuse", "albedo")) return "Albedo";
            if (Contains("normal") || Has("nor", "nrm", "norm")) return "NormalGL";
            if (Contains("roughness") || Has("rough", "rgh")) return "Roughness";
            if (Contains("metalness", "metallic") || Has("metal", "met")) return "Metallic";
            if (Contains("ambientocclusion", "occlusion") || Has("ao")) return "AO";
            if (Contains("displacement", "height") || Has("disp", "bump")) return "Height";
            if (Contains("opacity", "alpha", "mask")) return "Opacity";
            if (Contains("emission", "emissive") || Has("emit")) return "Emissive";
            return null;
        }

        /// <summary>
        /// Build the material for the maps in <paramref name="files"/> (absolute paths inside or below
        /// <paramref name="materialDir"/>). <paramref name="roles"/> overrides name detection (absolute path → role).
        /// Returns null when no colour or normal map was found.
        /// </summary>
        public static VortexMaterial Build(string name, string materialDir, IEnumerable<string> files, IDictionary<string, string> roles = null)
        {
            var byRole = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files.Where(IsImage).OrderBy(f => f.Length))
            {
                string role = null;
                if (roles != null) roles.TryGetValue(f, out role);
                role = role ?? RoleOf(f);
                if (role != null && !byRole.ContainsKey(role)) byRole[role] = f;
            }
            if (!byRole.ContainsKey("Albedo") && !byRole.ContainsKey("NormalGL") && !byRole.ContainsKey("NormalDX")) return null;

            string Rel(string abs) => LibraryRel(materialDir, abs);
            var m = new VortexMaterial { Name = name, BaseColor = new[] { 1f, 1f, 1f, 1f }, Metallic = 0f, Roughness = 0.8f };
            if (byRole.TryGetValue("Albedo", out var albedo)) m.AlbedoTexture = Rel(albedo);
            // prefer the OpenGL normal map (the engine flips DirectX maps with UseDirectXNormals)
            if (byRole.TryGetValue("NormalGL", out var ngl)) { m.NormalTexture = Rel(ngl); m.UseDirectXNormals = false; }
            else if (byRole.TryGetValue("NormalDX", out var ndx)) { m.NormalTexture = Rel(ndx); m.UseDirectXNormals = true; }
            if (byRole.TryGetValue("ARM", out var arm))
            {
                m.OcclusionRoughnessMetallicTexture = Rel(arm);
                m.Roughness = 1f; m.Metallic = 1f;
            }
            else
            {
                if (byRole.TryGetValue("Roughness", out var r)) { m.RoughnessTexture = Rel(r); m.Roughness = 1f; }
                if (byRole.TryGetValue("Metallic", out var mt)) { m.MetallicTexture = Rel(mt); m.Metallic = 1f; }
                if (byRole.TryGetValue("AO", out var ao)) m.AOTexture = Rel(ao);
            }
            if (byRole.TryGetValue("Height", out var h)) { m.HeightTexture = Rel(h); m.HeightScale = 0.03f; }
            if (byRole.TryGetValue("Opacity", out var o)) { m.OpacityTexture = Rel(o); m.BlendMode = "AlphaTest"; }
            if (byRole.TryGetValue("Emissive", out var e)) { m.EmissiveTexture = Rel(e); m.EmissiveStrength = 1f; m.EmissiveColor = new[] { 1f, 1f, 1f }; }
            return m;
        }

        /// <summary>The real-world tile size a provider reported for <paramref name="item"/> ("size_m" = "w,h" metres), or null.</summary>
        public static float[] RealWorldSizeOf(StoreItem item)
        {
            if (item == null || !item.Extra.TryGetValue("size_m", out var s) || string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(',');
            if (parts.Length < 2) return null;
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float w) ||
                !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float h) || w <= 0f || h <= 0f) return null;
            return new[] { w, h };
        }

        /// <summary>
        /// Parallax depth for a texture that covers <paramref name="tileMetres"/>: the engine offsets UVs in tile units,
        /// so a fixed depth exaggerates large scans (0.03 on a 15 m tile smears the surface by ~45 cm at grazing angles).
        /// About 5 cm of relief, kept between 0.002 and 0.03 tile units.
        /// </summary>
        public static float HeightScaleFor(float tileMetres)
            => tileMetres > 0f ? Math.Max(0.002f, Math.Min(0.03f, 0.05f / tileMetres)) : 0.03f;

        /// <summary>
        /// Tiling that keeps a material with a known real-world size at its true scale on a cube or plane primitive of
        /// world size (<paramref name="sx"/>, <paramref name="sy"/>, <paramref name="sz"/>) metres: the two largest
        /// extents are the face the material is seen on (a floor: x/z, a wall: x/y or z/y, matching the primitives' UV
        /// layout). Null when the mesh is not a cube/plane primitive or the size is unknown. <paramref name="extentU"/>
        /// / <paramref name="extentV"/> are the face's size in metres.
        /// </summary>
        public static float[] FitTiling(string meshPath, float sx, float sy, float sz, float[] realWorldSize, out float extentU, out float extentV)
        {
            extentU = extentV = 0f;
            if (realWorldSize == null || realWorldSize.Length < 2 || realWorldSize[0] <= 0f || realWorldSize[1] <= 0f) return null;
            string m = (meshPath ?? "").Trim();
            bool plane = m.Equals("Primitive:Plane", StringComparison.OrdinalIgnoreCase);
            bool cube = m.Equals("Primitive:Cube", StringComparison.OrdinalIgnoreCase);
            if (!plane && !cube) return null;
            sx = Math.Abs(sx); sy = Math.Abs(sy); sz = Math.Abs(sz);
            if (plane || (sy <= sx && sy <= sz)) { extentU = sx; extentV = sz; }      // floor / ceiling (planes lie in x/z)
            else if (sz <= sx && sz <= sy) { extentU = sx; extentV = sy; }            // wall facing z
            else { extentU = sz; extentV = sy; }                                      // wall facing x
            if (extentU <= 0f || extentV <= 0f) return null;
            return new[] { (float)Math.Round(extentU / realWorldSize[0], 2), (float)Math.Round(extentV / realWorldSize[1], 2) };
        }

        /// <summary>Every texture path a material references.</summary>
        public static List<string> TexturePaths(VortexMaterial m)
            => new[] { m.AlbedoTexture, m.NormalTexture, m.MetallicTexture, m.RoughnessTexture, m.AOTexture, m.EmissiveTexture, m.HeightTexture,
                       m.OpacityTexture, m.MetallicRoughnessTexture, m.OcclusionRoughnessMetallicTexture }
               .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Rewrite every texture path of a material.</summary>
        public static void MapPaths(VortexMaterial m, Func<string, string> map)
        {
            string M(string p) => string.IsNullOrEmpty(p) ? p : map(p);
            m.AlbedoTexture = M(m.AlbedoTexture); m.NormalTexture = M(m.NormalTexture); m.MetallicTexture = M(m.MetallicTexture);
            m.RoughnessTexture = M(m.RoughnessTexture); m.AOTexture = M(m.AOTexture); m.EmissiveTexture = M(m.EmissiveTexture);
            m.HeightTexture = M(m.HeightTexture); m.OpacityTexture = M(m.OpacityTexture); m.MetallicRoughnessTexture = M(m.MetallicRoughnessTexture);
            m.OcclusionRoughnessMetallicTexture = M(m.OcclusionRoughnessMetallicTexture);
        }

        private static string LibraryRel(string dir, string file)
        {
            string r = Path.GetFullPath(dir).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(file);
            return f.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? f.Substring(r.Length).Replace('\\', '/') : Path.GetFileName(f);
        }
    }
}
