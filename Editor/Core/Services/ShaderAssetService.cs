using System;
using System.Collections.Generic;
using System.IO;

namespace Editor.Core.Services
{
    /// <summary>
    /// THE resolver for material shader assets. A material's <c>VortexMaterial.ShaderAsset</c> stores a
    /// project-relative .hlsl (or legacy .vshader) path; everything that needs the actual compilable file
    /// (MaterialService when binding a material, the Material Editor's Edit-in-VS) resolves through here.
    /// Two private copies used to live in MaterialService and MaterialEditorDialog and had DIVERGED (one
    /// returned null on a miss, the other a nonexistent path) — unified here on: null when unresolvable.
    ///
    /// Shipped game: the .hlsl lives ONLY inside the mounted .vpak (AssetVfs), but the native shader
    /// compiler can only read disk files — so a packed shader is extracted ONCE to
    /// %TEMP%/VortexShaders/&lt;sha1-of-relpath&gt;.hlsl and that temp path is returned. Without this,
    /// Release exports silently rendered every custom-shader material with the built-in PBR.
    /// </summary>
    public static class ShaderAssetService
    {
        // Pak-extracted shader files, keyed by normalized project-relative path. The pak is immutable at
        // runtime, so one extraction per path is enough for the whole session.
        private static readonly Dictionary<string, string> _vfsExtracted =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Resolve a material's ShaderAsset (.vshader/.hlsl, project-relative or absolute) to an
        /// absolute on-disk .hlsl the NATIVE compiler can open. Null when unresolvable — the caller keeps
        /// the built-in shader (never pass a nonexistent path to the engine).</summary>
        public static string ResolveShaderHlsl(string shaderAsset)
        {
            if (string.IsNullOrEmpty(shaderAsset)) return null;
            var proj = Editor.Core.Data.ProjectData.Current?.Path ?? "";
            string full = Path.IsPathRooted(shaderAsset) ? shaderAsset : Path.Combine(proj, shaderAsset);

            if (full.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase))
                return MaterializeHlsl(full, proj);

            // Legacy .vshader indirection: a JSON asset pointing at its pixel-shader .hlsl.
            try
            {
                var vs = Editor.Core.Assets.VortexShader.Load(full);
                if (vs != null && !string.IsNullOrEmpty(vs.PixelShaderPath))
                {
                    var p = vs.PixelShaderPath;
                    var h = Path.IsPathRooted(p) ? p : Path.Combine(proj, p);
                    var resolved = MaterializeHlsl(h, proj);
                    if (resolved != null) return resolved;
                }
            }
            catch { }

            // Last chance: an .hlsl with the same name sitting next to the .vshader.
            return MaterializeHlsl(Path.ChangeExtension(full, ".hlsl"), proj);
        }

        /// <summary>Project-relative (forward-slash) paths of every .hlsl under Assets/ — what the Material
        /// Editor's shader dropdown offers. Uses the AssetDatabase index first (it already skips meta/hidden
        /// files), then a direct disk scan tops up anything the database hasn't picked up yet. Empty list
        /// when no project is open.</summary>
        public static List<string> EnumerateProjectShaders()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var proj = Editor.Core.Data.ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(proj)) return result;

            try
            {
                foreach (var meta in Editor.Core.Assets.AssetDatabase.Instance.GetAssetsByType(Editor.Core.Assets.AssetType.Shader))
                {
                    var rel = meta != null ? meta.RelativePath : null;
                    if (string.IsNullOrEmpty(rel) || !rel.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase)) continue;
                    rel = rel.Replace('\\', '/');
                    if (seen.Add(rel)) result.Add(rel);
                }
            }
            catch { }

            try
            {
                var assets = Path.Combine(proj, "Assets");
                if (Directory.Exists(assets))
                {
                    foreach (var f in Directory.EnumerateFiles(assets, "*.hlsl", SearchOption.AllDirectories))
                    {
                        var rel = ToProjectRelative(f, proj);
                        if (string.IsNullOrEmpty(rel)) continue;
                        // Build junk is never a shader asset.
                        if (rel.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            rel.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (seen.Add(rel)) result.Add(rel);
                    }
                }
            }
            catch { }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>Turn a candidate absolute .hlsl path into a file the native compiler can open: the path
        /// itself when it exists on disk (editor / loose files), else a one-time %TEMP% extraction when the
        /// bytes live in the mounted pak (shipped game). Null when the shader exists nowhere.</summary>
        private static string MaterializeHlsl(string fullPath, string projectRoot)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            try { if (File.Exists(fullPath)) return fullPath; } catch { }

            byte[] bytes;
            if (!AssetVfs.IsMounted || !AssetVfs.TryGetBytes(fullPath, out bytes) || bytes == null) return null;

            string key = NormalizeRelKey(fullPath, projectRoot);
            string cached;
            if (_vfsExtracted.TryGetValue(key, out cached) && File.Exists(cached)) return cached;

            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "VortexShaders");
                Directory.CreateDirectory(dir);
                string tmp = Path.Combine(dir, Sha1Hex(key) + ".hlsl");
                if (!File.Exists(tmp)) File.WriteAllBytes(tmp, bytes);   // extract ONCE; pak content never changes
                _vfsExtracted[key] = tmp;
                return tmp;
            }
            catch { return null; }
        }

        /// <summary>Stable cache key: project-relative, forward slashes, lowercase.</summary>
        private static string NormalizeRelKey(string fullPath, string projectRoot)
        {
            string p = fullPath;
            try
            {
                if (!string.IsNullOrEmpty(projectRoot) && Path.IsPathRooted(p))
                {
                    var root = projectRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    var abs = Path.GetFullPath(p);
                    if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase)) p = abs.Substring(root.Length);
                }
            }
            catch { }
            return p.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        }

        private static string ToProjectRelative(string fullPath, string projectRoot)
        {
            try
            {
                var root = projectRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                var abs = Path.GetFullPath(fullPath);
                if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return abs.Substring(root.Length).Replace('\\', '/');
                return abs.Replace('\\', '/');
            }
            catch { return null; }
        }

        private static string Sha1Hex(string s)
        {
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                var hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
