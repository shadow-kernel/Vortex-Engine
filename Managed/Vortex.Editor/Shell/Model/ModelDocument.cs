using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.DllWrapper;

namespace VortexEditor.Shell.ModelTools
{
    /// <summary>One texture map slot of a model material (absolute file path, or none).</summary>
    public sealed class TextureSlot
    {
        public TextureMapType MapType;
        public string CustomName;
        public string FilePath;

        public string DisplayName => MapType == TextureMapType.Custom ? (CustomName ?? "Custom") : ModelDocument.MapName(MapType);
        public bool IsAssigned => !string.IsNullOrEmpty(FilePath);
        public bool FileExists => IsAssigned && File.Exists(FilePath);
        public string FileName => IsAssigned ? Path.GetFileName(FilePath) : "None";
        public string StatusText => !IsAssigned ? "Not assigned" : !FileExists ? "File not found" : FileName;
        public void Clear() => FilePath = null;
    }

    /// <summary>
    /// A model material as the Model Editor edits it (framework-neutral port of the WPF UniversalMaterial): PBR scalars,
    /// base colour (RGBA floats, the .vmat convention) and the texture slots the import actually found (slots are dynamic,
    /// "+ Add Map" adds more).
    /// </summary>
    public sealed class ModelMaterial
    {
        public int Index;
        public string Name = "Material";
        public float[] BaseColor = { 1f, 1f, 1f, 1f };
        public float Metallic;
        public float Roughness = 0.5f;
        public float NormalStrength = 1f;
        public float AOStrength = 1f;
        public float EmissiveStrength;
        public float[] EmissiveColor = { 0f, 0f, 0f };
        public bool TwoSided;
        public readonly List<TextureSlot> Slots = new List<TextureSlot>();

        /// <summary>The standard PBR map types, for the "Add Map" picker (same list as the Windows editor).</summary>
        public static readonly TextureMapType[] StandardMapTypes =
        {
            TextureMapType.Albedo, TextureMapType.Normal, TextureMapType.Metallic, TextureMapType.Roughness,
            TextureMapType.AmbientOcclusion, TextureMapType.Emissive, TextureMapType.Height, TextureMapType.Opacity,
            TextureMapType.MetallicRoughness, TextureMapType.OcclusionRoughnessMetallic
        };

        public TextureSlot GetSlot(TextureMapType type) => type == TextureMapType.Custom ? null : Slots.FirstOrDefault(s => s.MapType == type);
        public string GetTexture(TextureMapType type) => GetSlot(type)?.FilePath;

        /// <summary>Assign a map: updates the slot when present (null clears it), adds a slot for a new non-empty map.</summary>
        public void SetTexture(TextureMapType type, string filePath)
        {
            var slot = GetSlot(type);
            if (slot != null) slot.FilePath = string.IsNullOrEmpty(filePath) ? null : filePath;
            else if (!string.IsNullOrEmpty(filePath)) Slots.Add(new TextureSlot { MapType = type, FilePath = filePath });
        }

        public TextureSlot AddStandardSlot(TextureMapType type)
        {
            var s = GetSlot(type);
            if (s != null) return s;
            s = new TextureSlot { MapType = type };
            Slots.Add(s);
            return s;
        }

        public TextureSlot AddCustomSlot(string name)
        {
            var s = new TextureSlot { MapType = TextureMapType.Custom, CustomName = name };
            Slots.Add(s);
            return s;
        }

        public int AssignedTextureCount => Slots.Count(t => t.IsAssigned);
        public string TextureSummary => AssignedTextureCount == 0 ? "No textures" : AssignedTextureCount + " texture(s)";
    }

    /// <summary>One submesh (one engine mesh) of a model.</summary>
    public sealed class ModelSubmesh
    {
        public int Index;
        public string Name;
        public int VertexCount = -1;     // -1 = unknown (the native importer exposes no per-mesh counts)
        public int TriangleCount = -1;
        public int MaterialIndex;
        public long MeshId = -1;
        public float[] BoundsCenter;     // mesh-local AABB (from the engine mesh)
        public float[] BoundsSize;

        public string DisplayName => string.IsNullOrEmpty(Name) ? "Submesh " + Index : Name;
        public bool HasGeometryCounts => VertexCount >= 0 && TriangleCount >= 0;
        public string GeometryInfo => HasGeometryCounts ? VertexCount.ToString("N0", CultureInfo.InvariantCulture) + " verts, " + TriangleCount.ToString("N0", CultureInfo.InvariantCulture) + " tris" : null;
    }

    /// <summary>A texture file found in or around the model's folder (the Texture Library).</summary>
    public sealed class DiscoveredTexture
    {
        public string FilePath;
        public long FileSize;
        public TextureMapType DetectedType = TextureMapType.Custom;
        public string FileName => Path.GetFileName(FilePath);
        public string FileSizeText => ModelDocument.FormatBytes(FileSize);
    }

    /// <summary>An animation clip embedded in the model file.</summary>
    public sealed class AnimationClipInfo
    {
        public string Name;
        public float DurationSec;
    }

    /// <summary>Skeleton + clips of a model (gathered off the UI thread: every native query re-imports the file).</summary>
    public sealed class ModelAnimationInfo
    {
        public int SkeletonNodes;
        public int Bones;
        public readonly List<AnimationClipInfo> Clips = new List<AnimationClipInfo>();
        /// <summary>.vanim files already extracted next to the model (animations/).</summary>
        public readonly List<string> ExtractedClips = new List<string>();
    }

    /// <summary>
    /// Framework-neutral model data for the Model / Mesh editors (the macOS port of the WPF UniversalModelData +
    /// UniversalModelParser, which are WPF-bound and not part of Vortex.Core): submeshes, materials with their real
    /// texture slots, the textures around the model, auto-assignment by naming convention, the per-submesh sidecar
    /// .vmat files (materials/submesh_N.vmat — the single source of truth scene placement binds) and geometry stats.
    /// Loading imports the model through the native importer; the engine meshes it creates are kept for the live preview
    /// and released by <see cref="Dispose"/>. Must be loaded on the UI thread (engine calls).
    /// </summary>
    public sealed class ModelDocument : IDisposable
    {
        private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".hdr", ".tif", ".tiff" };
        private static readonly string[] TextureSubfolders = { "textures", "Textures", "texture", "Texture", "tex", "maps", "Materials", "material" };

        public string FilePath { get; }
        public string FileName => Path.GetFileName(FilePath);
        public string FileNameWithoutExtension => Path.GetFileNameWithoutExtension(FilePath);
        public string Directory => Path.GetDirectoryName(FilePath);
        public string Extension => (Path.GetExtension(FilePath) ?? "").ToLowerInvariant();
        public string MaterialsDirectory => Path.Combine(Directory ?? "", "materials");
        public string AnimationsDirectory => Path.Combine(Directory ?? "", "animations");

        public readonly List<ModelSubmesh> Submeshes = new List<ModelSubmesh>();
        public readonly List<ModelMaterial> Materials = new List<ModelMaterial>();
        public readonly List<DiscoveredTexture> DiscoveredTextures = new List<DiscoveredTexture>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Totals (-1 = unknown). Filled synchronously for glTF/OBJ, asynchronously (<see cref="LoadTotalsAsync"/>) otherwise.</summary>
        public long TotalVertices = -1, TotalTriangles = -1;
        /// <summary>True when at least one material was overlaid from an existing sidecar .vmat.</summary>
        public bool UsesSidecarMaterials { get; private set; }

        public string FormatName
        {
            get
            {
                switch (Extension)
                {
                    case ".obj": return "Wavefront OBJ";
                    case ".fbx": return "Autodesk FBX";
                    case ".gltf": return "glTF 2.0";
                    case ".glb": return "glTF Binary";
                    case ".dae": return "Collada";
                    case ".blend": return "Blender";
                    case ".3ds": return "3D Studio";
                    case ".vmesh": return "Vortex Mesh";
                    default: return "Unknown Format";
                }
            }
        }

        public string StatsSummary => Submeshes.Count + " submesh(es), " + Materials.Count + " material(s), " + DiscoveredTextures.Count + " texture(s)";

        public string GeometrySummary
        {
            get
            {
                if (TotalTriangles < 0) return null;
                string t = TotalTriangles.ToString("N0", CultureInfo.InvariantCulture) + " triangles";
                return TotalVertices >= 0 ? TotalVertices.ToString("N0", CultureInfo.InvariantCulture) + " vertices, " + t : t;
            }
        }

        private ModelDocument(string path) { FilePath = path; }

        public static bool IsSupportedModel(string path)
        {
            switch ((Path.GetExtension(path ?? "") ?? "").ToLowerInvariant())
            {
                case ".obj": case ".fbx": case ".gltf": case ".glb": case ".dae": case ".blend": case ".3ds": case ".vmesh": return true;
                default: return false;
            }
        }

        // ================================================================== load

        /// <summary>Parse a model (throws on unreadable files). <paramref name="applySidecars"/> overlays the saved
        /// materials/submesh_N.vmat values so the editor shows what the scene renders.</summary>
        public static ModelDocument Load(string path, bool applySidecars = true)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) throw new FileNotFoundException("Model file not found", path);
            var doc = new ModelDocument(path);
            try
            {
                switch (doc.Extension)
                {
                    case ".obj":
                        // Prefer the native import (real material slots, same as the game); the hand-rolled OBJ/MTL reader
                        // is only the fallback when the native import fails.
                        try { doc.ParseNative(); }
                        catch { doc.ReleaseMeshes(); doc.Materials.Clear(); doc.Submeshes.Clear(); doc.ParseObj(); }
                        break;
                    case ".fbx": case ".dae": case ".3ds": case ".blend": case ".gltf": case ".glb":
                        doc.ParseNative();
                        break;
                    case ".vmesh":
                        doc.ParseVMesh();
                        break;
                    default:
                        throw new NotSupportedException("Unsupported model format: " + doc.Extension);
                }
                doc.DiscoverTextures();
                doc.AutoAssignTextures();
                if (applySidecars) doc.ApplySidecarMaterials();
                doc.ReadMeshBounds();
                doc.ComputeGeometryStats();
            }
            catch
            {
                doc.Dispose();
                throw;
            }
            return doc;
        }

        private void ParseNative()
        {
            if (!VortexAPI.IsAssimpAvailable()) throw new InvalidOperationException("The model importer (assimp) is not available in this engine build.");
            var subs = VortexAPI.ImportModelWithMaterialsFromFile(FilePath);
            if (subs == null || subs.Length == 0) throw new InvalidOperationException("The native importer could not read " + FileName + ".");

            string[] names = null;
            try { names = VortexAPI.GetSubmeshNames(FilePath, subs.Length); } catch { }
            VortexAPI.SubmeshTextureSet[] tex = null;
            try { tex = VortexAPI.GetSubmeshTexturePaths(FilePath, subs.Length); } catch { }
            VortexAPI.SubmeshMaterialProps[] props = null;
            try { props = VortexAPI.GetSubmeshMaterialProps(FilePath, subs.Length); } catch { }

            var byEngineMaterial = new Dictionary<long, int>();
            for (int i = 0; i < subs.Length; i++)
            {
                var d = subs[i];
                string name = names != null && i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : "Submesh_" + i;
                if (!byEngineMaterial.TryGetValue(d.MaterialId, out int matIndex) || d.MaterialId < 0)
                {
                    var m = new ModelMaterial { Index = Materials.Count, Name = names != null && i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : "Material_" + Materials.Count };
                    if (props != null && i < props.Length)
                    {
                        var p = props[i];
                        if (p.BaseColor != null && p.BaseColor.Length >= 3) m.BaseColor = new[] { p.BaseColor[0], p.BaseColor[1], p.BaseColor[2], p.BaseColor.Length > 3 ? p.BaseColor[3] : 1f };
                        m.Metallic = Clamp01(p.Metallic);
                        m.Roughness = Clamp01(p.Roughness);
                    }
                    if (tex != null && i < tex.Length)
                    {
                        var t = tex[i];
                        AddImportedMap(m, TextureMapType.Albedo, t.Albedo);
                        AddImportedMap(m, TextureMapType.Normal, t.Normal);
                        AddImportedMap(m, TextureMapType.Metallic, t.Metallic);
                        AddImportedMap(m, TextureMapType.Roughness, t.Roughness);
                        AddImportedMap(m, TextureMapType.AmbientOcclusion, t.AO);
                        AddImportedMap(m, TextureMapType.Emissive, t.Emissive);
                    }
                    matIndex = Materials.Count;
                    if (d.MaterialId >= 0) byEngineMaterial[d.MaterialId] = matIndex;
                    Materials.Add(m);
                }
                Submeshes.Add(new ModelSubmesh { Index = i, Name = name, MeshId = d.MeshId, MaterialIndex = matIndex });
            }
            // The import's own engine materials are not used (the editor builds its materials from the edited state).
            foreach (var d in subs) if (d.MaterialId >= 0) { try { VortexAPI.DeleteMaterial(d.MaterialId); } catch { } }
            if (Materials.Count == 0) Materials.Add(new ModelMaterial { Index = 0, Name = "Default" });
        }

        private void AddImportedMap(ModelMaterial m, TextureMapType type, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            m.SetTexture(type, path);
            RegisterDiscoveredTexture(path, type);
        }

        private void ParseVMesh()
        {
            long mesh = VortexAPI.LoadVMeshFromFile(FilePath);
            if (mesh < 0) throw new InvalidOperationException("Failed to load the .vmesh file.");
            Materials.Add(new ModelMaterial { Index = 0, Name = "Default" });
            Submeshes.Add(new ModelSubmesh { Index = 0, Name = FileNameWithoutExtension, MeshId = mesh, MaterialIndex = 0 });
        }

        // ------------------------------------------------------------------ OBJ / MTL fallback (no engine meshes)

        private sealed class ObjGroup { public string Name, Material; public int Vertices, Faces, Triangles; }

        private void ParseObj()
        {
            string dir = Directory;
            var mtl = FindMtlFile();
            if (mtl != null) foreach (var m in ParseMtl(mtl, dir)) { m.Index = Materials.Count; Materials.Add(m); }
            if (Materials.Count == 0) Materials.Add(new ModelMaterial { Index = 0, Name = "Default" });
            foreach (var g in ReadObjGroups(out _, out _))
            {
                int mi = 0;
                for (int i = 0; i < Materials.Count; i++) if (string.Equals(Materials[i].Name, g.Material, StringComparison.OrdinalIgnoreCase)) { mi = i; break; }
                Submeshes.Add(new ModelSubmesh { Index = Submeshes.Count, Name = g.Name, VertexCount = g.Vertices, TriangleCount = g.Triangles, MaterialIndex = mi });
            }
            if (Submeshes.Count == 0) Submeshes.Add(new ModelSubmesh { Index = 0, Name = FileNameWithoutExtension, MaterialIndex = 0 });
        }

        private List<ObjGroup> ReadObjGroups(out long totalVerts, out long totalTris)
        {
            var groups = new List<ObjGroup>();
            ObjGroup cur = null; string curMat = null; int verts = 0, groupStart = 0;
            totalVerts = 0; totalTris = 0;
            try
            {
                foreach (var raw in File.ReadLines(FilePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line.StartsWith("v ", StringComparison.Ordinal)) { verts++; continue; }
                    if (line.StartsWith("g ", StringComparison.Ordinal) || line.StartsWith("o ", StringComparison.Ordinal))
                    {
                        if (cur != null) { cur.Vertices = verts - groupStart; groups.Add(cur); }
                        cur = new ObjGroup { Name = line.Substring(2).Trim(), Material = curMat };
                        groupStart = verts;
                    }
                    else if (line.StartsWith("usemtl ", StringComparison.Ordinal))
                    {
                        curMat = line.Substring(7).Trim();
                        if (cur != null) cur.Material = curMat;
                        else { cur = new ObjGroup { Name = curMat, Material = curMat }; groupStart = verts; }
                    }
                    else if (line.StartsWith("f ", StringComparison.Ordinal))
                    {
                        if (cur == null) { cur = new ObjGroup { Name = "default", Material = curMat ?? "default" }; groupStart = verts; }
                        int n = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length - 1;
                        cur.Faces++;
                        if (n >= 3) { cur.Triangles += n - 2; totalTris += n - 2; }
                    }
                }
                if (cur != null) { cur.Vertices = verts - groupStart; groups.Add(cur); }
            }
            catch { }
            totalVerts = verts;
            return groups;
        }

        private string FindMtlFile()
        {
            string dir = Directory;
            try
            {
                foreach (var line in File.ReadLines(FilePath))
                {
                    if (!line.StartsWith("mtllib ", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = line.Substring(7).Trim();
                    foreach (var cand in new[] { Path.Combine(dir, name), Path.Combine(dir, name.Replace('\\', '/')), Path.Combine(dir, Path.GetFileName(name.Replace('\\', '/'))) })
                        if (File.Exists(cand)) return cand;
                }
                var mtls = System.IO.Directory.GetFiles(dir, "*.mtl");
                if (mtls.Length == 0) return null;
                string baseName = FileNameWithoutExtension;
                var exact = mtls.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(baseName, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
                string Simplify(string s) => new string(s.ToLowerInvariant().Replace("(wavefront obj)", "").Replace("-", "_").Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()).Trim('_');
                var sb = Simplify(baseName);
                foreach (var f in mtls)
                {
                    var n = Path.GetFileNameWithoutExtension(f); var sn = Simplify(n);
                    if (sn.Contains(sb) || sb.Contains(sn) || n.Contains(baseName) || baseName.Contains(n)) return f;
                }
                return mtls[0];
            }
            catch { return null; }
        }

        private List<ModelMaterial> ParseMtl(string mtlPath, string modelDir)
        {
            var list = new List<ModelMaterial>();
            string mtlDir = Path.GetDirectoryName(mtlPath);
            ModelMaterial cur = null;
            float specExp = 100f;
            float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
            string Resolve(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return null;
                var clean = raw.Trim();
                if (clean.StartsWith("-")) { int idx = clean.LastIndexOf(' '); if (idx > 0) clean = clean.Substring(idx + 1).Trim(); }
                clean = clean.Replace('\\', '/');
                var just = Path.GetFileName(clean);
                var tries = new List<string>();
                try { tries.Add(Path.GetFullPath(Path.Combine(mtlDir, clean))); } catch { }
                try { tries.Add(Path.GetFullPath(Path.Combine(modelDir, clean))); } catch { }
                tries.Add(Path.Combine(mtlDir, just)); tries.Add(Path.Combine(modelDir, just));
                tries.Add(Path.Combine(mtlDir, "textures", just)); tries.Add(Path.Combine(mtlDir, "Textures", just));
                tries.Add(Path.Combine(modelDir, "textures", just)); tries.Add(Path.Combine(modelDir, "Textures", just));
                var parent = Path.GetDirectoryName(mtlDir);
                if (!string.IsNullOrEmpty(parent)) { tries.Add(Path.Combine(parent, just)); tries.Add(Path.Combine(parent, "textures", just)); }
                tries.Add(Path.Combine(mtlDir, just.ToLowerInvariant())); tries.Add(Path.Combine(modelDir, just.ToLowerInvariant()));
                foreach (var t in tries) { try { if (File.Exists(t)) return t; } catch { } }
                return null;
            }
            void Map(TextureMapType type, string[] parts)
            {
                if (cur == null) return;
                var p = Resolve(string.Join(" ", parts.Skip(1)));
                if (p == null) return;
                // the specular map is only the roughness fallback when no roughness map exists
                if (type == TextureMapType.Specular) { if (cur.GetSlot(TextureMapType.Roughness) == null) AddImportedMap(cur, TextureMapType.Roughness, p); return; }
                AddImportedMap(cur, type, p);
            }
            void Finish() { if (cur != null) { if (specExp > 0) cur.Roughness = 1f - Math.Min(specExp / 1000f, 1f); list.Add(cur); } }
            try
            {
                foreach (var raw in File.ReadLines(mtlPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    switch (parts[0].ToLowerInvariant())
                    {
                        case "newmtl": Finish(); cur = new ModelMaterial { Name = string.Join(" ", parts.Skip(1)) }; specExp = 100f; break;
                        case "kd": if (cur != null && parts.Length >= 4) cur.BaseColor = new[] { F(parts[1]), F(parts[2]), F(parts[3]), cur.BaseColor[3] }; break;
                        case "ke": if (cur != null && parts.Length >= 4) { var e = new[] { F(parts[1]), F(parts[2]), F(parts[3]) }; if (e[0] + e[1] + e[2] > 0.01f) { cur.EmissiveColor = e; cur.EmissiveStrength = 1f; } } break;
                        case "ns": specExp = F(parts[1]); break;
                        case "d": if (cur != null) cur.BaseColor[3] = F(parts[1]); break;
                        case "tr": if (cur != null) cur.BaseColor[3] = 1f - F(parts[1]); break;
                        case "pm": if (cur != null) cur.Metallic = Clamp01(F(parts[1])); break;
                        case "pr": if (cur != null) { cur.Roughness = Clamp01(F(parts[1])); specExp = 0; } break;
                        case "map_kd": Map(TextureMapType.Albedo, parts); break;
                        case "map_bump": case "bump": case "map_kn": case "norm": Map(TextureMapType.Normal, parts); break;
                        case "map_ks": Map(TextureMapType.Specular, parts); break;
                        case "map_pr": case "map_ns": Map(TextureMapType.Roughness, parts); break;
                        case "map_pm": Map(TextureMapType.Metallic, parts); break;
                        case "map_ka": Map(TextureMapType.AmbientOcclusion, parts); break;
                        case "map_ke": Map(TextureMapType.Emissive, parts); break;
                        case "map_d": Map(TextureMapType.Opacity, parts); break;
                        case "disp": Map(TextureMapType.Height, parts); break;
                    }
                }
                Finish();
            }
            catch { }
            return list;
        }

        // ================================================================== textures around the model

        private void RegisterDiscoveredTexture(string path, TextureMapType type)
        {
            if (string.IsNullOrEmpty(path)) return;
            string full; try { full = Path.GetFullPath(path); } catch { full = path; }
            var existing = DiscoveredTextures.FirstOrDefault(t => string.Equals(t.FilePath, full, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (existing.DetectedType == TextureMapType.Custom && type != TextureMapType.Custom) existing.DetectedType = type;
                return;
            }
            var d = new DiscoveredTexture { FilePath = full, DetectedType = type };
            try { d.FileSize = new FileInfo(full).Length; } catch { }
            DiscoveredTextures.Add(d);
        }

        /// <summary>Find every texture in the model folder, its texture subfolders, the parent folder and the parent's texture subfolders.</summary>
        public void DiscoverTextures()
        {
            var dir = Directory;
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return;
            var searchDirs = new List<string> { dir };
            foreach (var sub in TextureSubfolders) { var p = Path.Combine(dir, sub); if (System.IO.Directory.Exists(p)) searchDirs.Add(p); }
            var parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent) && System.IO.Directory.Exists(parent))
            {
                searchDirs.Add(parent);
                foreach (var sub in TextureSubfolders) { var p = Path.Combine(parent, sub); if (System.IO.Directory.Exists(p)) searchDirs.Add(p); }
            }
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { foreach (var f in System.IO.Directory.GetFiles(d)) if (TextureExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) found.Add(Path.GetFullPath(f)); }
                catch { }
            }
            foreach (var f in found.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var existing = DiscoveredTextures.FirstOrDefault(t => string.Equals(t.FilePath, f, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (existing.DetectedType == TextureMapType.Custom) existing.DetectedType = TextureNamingConventions.DetectType(f);
                    continue;
                }
                var t2 = new DiscoveredTexture { FilePath = f, DetectedType = TextureNamingConventions.DetectType(f) };
                try { t2.FileSize = new FileInfo(f).Length; } catch { }
                DiscoveredTextures.Add(t2);
            }
        }

        /// <summary>Fill every empty (non-custom) slot of every material from the Texture Library by naming convention.</summary>
        public void AutoAssignTextures() { foreach (var m in Materials) AutoAssignTexturesForMaterial(m); }

        public void AutoAssignTexturesForMaterial(ModelMaterial material)
        {
            var matName = (material.Name ?? "").ToLowerInvariant();
            var used = new HashSet<string>(material.Slots.Where(s => s.IsAssigned).Select(s => s.FilePath), StringComparer.OrdinalIgnoreCase);
            foreach (var slot in material.Slots)
            {
                if (slot.IsAssigned || slot.MapType == TextureMapType.Custom) continue;
                var match = BestMatch(matName, slot.MapType, used);
                if (match != null) { slot.FilePath = match.FilePath; used.Add(match.FilePath); }
            }
        }

        private DiscoveredTexture BestMatch(string materialName, TextureMapType type, HashSet<string> exclude)
        {
            var candidates = DiscoveredTextures.Where(t => t.DetectedType == type && !exclude.Contains(t.FilePath)).ToList();
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            if (!string.IsNullOrEmpty(materialName))
            {
                var byName = candidates.FirstOrDefault(t =>
                {
                    var fn = Path.GetFileNameWithoutExtension(t.FilePath).ToLowerInvariant();
                    return fn.Contains(materialName) || materialName.Contains(fn);
                });
                if (byName != null) return byName;
            }
            return candidates[0];
        }

        // ================================================================== sidecar .vmat files

        /// <summary>materials/submesh_N.vmat — the file scene placement binds for submesh N.</summary>
        public string SidecarPath(int submeshIndex) => Path.Combine(MaterialsDirectory, "submesh_" + submeshIndex + ".vmat");

        /// <summary>The sidecar .vmat of a material: the one of the first submesh that uses it.</summary>
        public string ResolveMaterialVmatPath(ModelMaterial m)
        {
            int mi = Materials.IndexOf(m);
            if (mi < 0) return null;
            for (int i = 0; i < Submeshes.Count; i++) if (Submeshes[i].MaterialIndex == mi) return SidecarPath(i);
            return null;
        }

        /// <summary>Other model files in this model's folder: they all share materials/submesh_N.vmat (the sidecar
        /// convention is per folder — the importer gives every model its own folder).</summary>
        public int SiblingModelCount
        {
            get
            {
                try { return System.IO.Directory.GetFiles(Directory).Count(f => IsSupportedModel(f) && !string.Equals(Path.GetFullPath(f), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase)); }
                catch { return 0; }
            }
        }

        /// <summary>Overlay the saved sidecar .vmat of each material (what the scene renders) over the imported values.
        /// In a folder shared by several models a sidecar is only taken when its name matches (it may belong to a sibling).</summary>
        public void ApplySidecarMaterials()
        {
            bool shared = SiblingModelCount > 0;
            foreach (var m in Materials)
            {
                var p = ResolveMaterialVmatPath(m);
                if (p == null || !File.Exists(p)) continue;
                if (shared)
                {
                    string name = null;
                    try { name = VortexMaterial.Load(p)?.Name; } catch { }
                    if (!string.Equals(name, m.Name, StringComparison.OrdinalIgnoreCase)) { Warnings.Add(Path.GetFileName(p) + " belongs to another model in this folder (\"" + name + "\") — not applied."); continue; }
                }
                if (ReloadMaterialFromVmat(m, p)) UsesSidecarMaterials = true;
            }
        }

        /// <summary>Legacy sidecars reference embedded GLB textures as "*N": map that to the file the native importer
        /// extracts next to the model (embedded_&lt;stem&gt;_N.*) or into textures/ (embedded_N.*).</summary>
        public string ResolveEmbeddedMarker(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string name = Path.GetFileName(path.Replace('\\', '/'));
            if (!name.StartsWith("*")) return path;
            string tag = new string(name.Where(char.IsLetterOrDigit).ToArray());
            if (tag.Length == 0) tag = "0";
            string stem = new string(FileNameWithoutExtension.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            try
            {
                var hit = System.IO.Directory.GetFiles(Directory, "embedded_" + stem + "_" + tag + ".*").FirstOrDefault();
                if (hit != null) return hit;
                var texDir = Path.Combine(Directory, "textures");
                if (System.IO.Directory.Exists(texDir)) { hit = System.IO.Directory.GetFiles(texDir, "embedded_" + tag + ".*").FirstOrDefault(); if (hit != null) return hit; }
            }
            catch { }
            return path;
        }

        /// <summary>Re-read a material's values + maps from a .vmat (after the Material Editor saved it). False if unreadable.</summary>
        public bool ReloadMaterialFromVmat(ModelMaterial m, string vmatPath)
        {
            if (m == null) return false;
            VortexMaterial v;
            try { v = VortexMaterial.Load(vmatPath); } catch { v = null; }
            if (v == null) return false;
            try { v.ResolvePathsAbsolute(Path.GetDirectoryName(vmatPath)); } catch { }
            if (v.BaseColor != null && v.BaseColor.Length >= 3) m.BaseColor = new[] { v.BaseColor[0], v.BaseColor[1], v.BaseColor[2], v.BaseColor.Length > 3 ? v.BaseColor[3] : 1f };
            m.Metallic = Clamp01(v.Metallic);
            m.Roughness = Clamp01(v.Roughness);
            m.NormalStrength = Math.Max(0f, Math.Min(2f, v.NormalStrength));
            m.AOStrength = Clamp01(v.AmbientOcclusion);
            m.EmissiveStrength = Math.Max(0f, v.EmissiveStrength);
            if (v.EmissiveColor != null && v.EmissiveColor.Length >= 3) m.EmissiveColor = new[] { v.EmissiveColor[0], v.EmissiveColor[1], v.EmissiveColor[2] };
            m.TwoSided = v.TwoSided;
            foreach (var kv in MapsOf(v))
            {
                var p = ResolveEmbeddedMarker(kv.Value);
                var slot = m.GetSlot(kv.Key);
                if (slot != null) slot.FilePath = string.IsNullOrEmpty(p) ? null : p;
                else if (!string.IsNullOrEmpty(p)) m.Slots.Add(new TextureSlot { MapType = kv.Key, FilePath = p });
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) RegisterDiscoveredTexture(p, kv.Key);
            }
            return true;
        }

        private static IEnumerable<KeyValuePair<TextureMapType, string>> MapsOf(VortexMaterial v)
        {
            yield return new KeyValuePair<TextureMapType, string>(TextureMapType.Albedo, v.AlbedoTexture);
            yield return new KeyValuePair<TextureMapType, string>(TextureMapType.Normal, v.NormalTexture);
            yield return new KeyValuePair<TextureMapType, string>(TextureMapType.Metallic, v.MetallicTexture);
            yield return new KeyValuePair<TextureMapType, string>(TextureMapType.Roughness, v.RoughnessTexture);
            yield return new KeyValuePair<TextureMapType, string>(TextureMapType.AmbientOcclusion, v.AOTexture);
            yield return new KeyValuePair<TextureMapType, string>(TextureMapType.Emissive, v.EmissiveTexture);
            yield return new KeyValuePair<TextureMapType, string>(TextureMapType.Height, v.HeightTexture);
            yield return new KeyValuePair<TextureMapType, string>(TextureMapType.Opacity, v.OpacityTexture);
            yield return new KeyValuePair<TextureMapType, string>(TextureMapType.MetallicRoughness, v.MetallicRoughnessTexture);
            yield return new KeyValuePair<TextureMapType, string>(TextureMapType.OcclusionRoughnessMetallic, v.OcclusionRoughnessMetallicTexture);
        }

        /// <summary>
        /// The .vmat a material saves as: the existing sidecar (so every field the Model Editor does not author — blend
        /// mode, UV tiling, custom shader, footstep sound, shadows … — survives) overlaid with the editor's values and maps.
        /// Texture paths are absolute; the caller makes them relative for saving.
        /// </summary>
        public VortexMaterial ComposeVmat(ModelMaterial m, string basePath)
        {
            VortexMaterial v = null;
            if (!string.IsNullOrEmpty(basePath) && File.Exists(basePath)) { try { v = VortexMaterial.Load(basePath); } catch { v = null; } }
            if (v == null) v = new VortexMaterial();
            v.Name = string.IsNullOrEmpty(m.Name) ? v.Name : m.Name;
            v.BaseColor = new[] { m.BaseColor[0], m.BaseColor[1], m.BaseColor[2], m.BaseColor.Length > 3 ? m.BaseColor[3] : 1f };
            v.Metallic = m.Metallic;
            v.Roughness = m.Roughness;
            v.NormalStrength = m.NormalStrength;
            v.AmbientOcclusion = m.AOStrength;
            v.EmissiveStrength = m.EmissiveStrength;
            v.EmissiveColor = new[] { m.EmissiveColor[0], m.EmissiveColor[1], m.EmissiveColor[2] };
            v.TwoSided = m.TwoSided;
            v.AlbedoTexture = m.GetTexture(TextureMapType.Albedo);
            v.NormalTexture = m.GetTexture(TextureMapType.Normal);
            v.MetallicTexture = m.GetTexture(TextureMapType.Metallic);
            v.RoughnessTexture = m.GetTexture(TextureMapType.Roughness);
            v.AOTexture = m.GetTexture(TextureMapType.AmbientOcclusion);
            v.EmissiveTexture = m.GetTexture(TextureMapType.Emissive);
            v.HeightTexture = m.GetTexture(TextureMapType.Height);
            v.OpacityTexture = m.GetTexture(TextureMapType.Opacity);
            v.MetallicRoughnessTexture = m.GetTexture(TextureMapType.MetallicRoughness);
            v.OcclusionRoughnessMetallicTexture = m.GetTexture(TextureMapType.OcclusionRoughnessMetallic);
            return v;
        }

        /// <summary>Material for the live preview: exactly what <see cref="SaveMaterials"/> would write (absolute paths).</summary>
        public VortexMaterial PreviewVmat(ModelMaterial m) => ComposeVmat(m, ResolveMaterialVmatPath(m));

        public ModelMaterial MaterialOf(ModelSubmesh s)
        {
            if (s == null) return null;
            if (s.MaterialIndex >= 0 && s.MaterialIndex < Materials.Count) return Materials[s.MaterialIndex];
            return Materials.FirstOrDefault();
        }

        /// <summary>Write one .vmat per submesh (materials/submesh_N.vmat). Returns the written files.</summary>
        public List<string> SaveMaterials()
        {
            var saved = new List<string>();
            var dir = MaterialsDirectory;
            System.IO.Directory.CreateDirectory(dir);
            for (int i = 0; i < Submeshes.Count; i++)
            {
                var m = MaterialOf(Submeshes[i]);
                if (m == null) continue;
                var path = SidecarPath(i);
                var v = ComposeVmat(m, path);
                v.MakePathsRelative(dir);
                if (v.Save(path)) saved.Add(path);
            }
            return saved;
        }

        // ================================================================== geometry stats

        private void ReadMeshBounds()
        {
            foreach (var s in Submeshes)
            {
                if (s.MeshId < 0) continue;
                try
                {
                    if (VortexAPI.GetMeshBounds(s.MeshId, out float sx, out float sy, out float sz))
                    {
                        VortexAPI.GetMeshBoundsCenter(s.MeshId, out float cx, out float cy, out float cz);
                        s.BoundsSize = new[] { sx, sy, sz };
                        s.BoundsCenter = new[] { cx, cy, cz };
                    }
                }
                catch { }
            }
        }

        /// <summary>Per-submesh vertex/triangle counts where they can be derived exactly: glTF/GLB primitives (mapped to
        /// the engine submeshes in import order and verified against each mesh's bounds) and OBJ totals.</summary>
        private void ComputeGeometryStats()
        {
            try
            {
                if (Extension == ".gltf" || Extension == ".glb")
                {
                    var prims = GltfStats.ReadPrimitives(FilePath);
                    if (prims != null && prims.Count > 0)
                    {
                        if (prims.Count == Submeshes.Count)
                        {
                            TotalVertices = prims.Sum(p => (long)p.Vertices);
                            TotalTriangles = prims.Sum(p => (long)p.Triangles);
                            bool verified = true;
                            for (int i = 0; i < prims.Count && verified; i++) verified = prims[i].Matches(Submeshes[i].BoundsCenter, Submeshes[i].BoundsSize);
                            if (verified)
                                for (int i = 0; i < prims.Count; i++) { Submeshes[i].VertexCount = prims[i].Vertices; Submeshes[i].TriangleCount = prims[i].Triangles; }
                        }
                    }
                }
                else if (Extension == ".obj")
                {
                    ReadObjGroups(out long v, out long t);
                    if (t > 0) { TotalVertices = v; TotalTriangles = t; }
                }
                else if (Submeshes.All(s => s.HasGeometryCounts))
                {
                    TotalVertices = Submeshes.Sum(s => (long)s.VertexCount);
                    TotalTriangles = Submeshes.Sum(s => (long)s.TriangleCount);
                }
            }
            catch { }
        }

        // Every native model query re-runs the whole import — which also (re)writes a GLB's embedded textures next to the
        // model. Running them on a worker thread would race UI-thread texture loads of those very files, so they run on
        // the UI thread, one call per background-priority dispatcher slice (the window stays responsive in between).
        private static async Task<T> Slice<T>(Func<T> f)
            => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { try { return f(); } catch { return default(T); } }, Avalonia.Threading.DispatcherPriority.Background);

        /// <summary>Triangle total from the importer, for formats whose file can't be read here (FBX, DAE, …).</summary>
        public async Task LoadTotalsAsync()
        {
            if (TotalTriangles >= 0) return;
            string path = FilePath;
            var tris = await Slice(() => VortexAPI.GetModelTriangles(path));
            if (tris != null && tris.Length >= 9) TotalTriangles = tris.Length / 9;
        }

        /// <summary>Skeleton, embedded clips and already-extracted .vanim files.</summary>
        public async Task<ModelAnimationInfo> LoadAnimationInfoAsync()
        {
            string path = FilePath, animDir = AnimationsDirectory;
            var info = new ModelAnimationInfo();
            try { if (System.IO.Directory.Exists(animDir)) info.ExtractedClips.AddRange(System.IO.Directory.GetFiles(animDir, "*.vanim").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)); } catch { }
            if (Extension == ".vmesh") return info;
            info.SkeletonNodes = (await Slice(() => VortexAPI.GetSkeletonNodes(path)))?.Length ?? 0;
            info.Bones = (await Slice(() => VortexAPI.GetSkeletonBones(path)))?.Length ?? 0;
            int n = await Slice(() => VortexAPI.GetAnimationCount(path));
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                var clip = await Slice(() => VortexAPI.GetAnimationInfo(path, idx, out string name, out float dur)
                    ? new AnimationClipInfo { Name = string.IsNullOrEmpty(name) ? "Clip " + idx : name, DurationSec = dur } : null);
                if (clip != null) info.Clips.Add(clip);
            }
            return info;
        }

        // ================================================================== helpers

        public static string MapName(TextureMapType t)
        {
            switch (t)
            {
                case TextureMapType.AmbientOcclusion: return "Ambient Occlusion";
                case TextureMapType.MetallicRoughness: return "Metallic-Roughness";
                case TextureMapType.OcclusionRoughnessMetallic: return "ORM";
                default: return t.ToString();
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return bytes + " B";
        }

        private static float Clamp01(float v) => float.IsNaN(v) ? 0f : Math.Max(0f, Math.Min(1f, v));

        public void ReleaseMeshes()
        {
            foreach (var s in Submeshes) { if (s.MeshId >= 0) { try { VortexAPI.DeleteMesh(s.MeshId); } catch { } s.MeshId = -1; } }
        }

        public void Dispose() => ReleaseMeshes();
    }

    /// <summary>
    /// Reads the per-primitive vertex / triangle counts (and POSITION bounds) of a glTF / GLB in the order the native
    /// importer emits submeshes (depth-first node traversal, each node's mesh primitives in order).
    /// </summary>
    internal static class GltfStats
    {
        internal sealed class Prim
        {
            public int Vertices, Triangles;
            public float[] Min, Max;

            /// <summary>True when the engine mesh bounds agree with this primitive's accessor bounds (or none are known).</summary>
            public bool Matches(float[] center, float[] size)
            {
                if (Min == null || Max == null || center == null || size == null) return true;
                for (int a = 0; a < 3; a++)
                {
                    float c = (Min[a] + Max[a]) * 0.5f, s = Max[a] - Min[a];
                    float tol = Math.Max(1e-3f, Math.Abs(s) * 0.01f);
                    if (Math.Abs(c - center[a]) > tol || Math.Abs(s - size[a]) > tol) return false;
                }
                return true;
            }
        }

        public static List<Prim> ReadPrimitives(string path)
        {
            string json = ReadJson(path);
            if (json == null) return null;
            using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("meshes", out var meshes) || meshes.ValueKind != JsonValueKind.Array) return null;
                root.TryGetProperty("accessors", out var accessors);
                root.TryGetProperty("nodes", out var nodes);
                var result = new List<Prim>();

                var roots = new List<int>();
                if (root.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array && scenes.GetArrayLength() > 0)
                {
                    int si = root.TryGetProperty("scene", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : 0;
                    if (si < 0 || si >= scenes.GetArrayLength()) si = 0;
                    if (scenes[si].TryGetProperty("nodes", out var rn) && rn.ValueKind == JsonValueKind.Array)
                        foreach (var n in rn.EnumerateArray()) roots.Add(n.GetInt32());
                }
                else if (nodes.ValueKind == JsonValueKind.Array)
                {
                    var isChild = new HashSet<int>();
                    foreach (var n in nodes.EnumerateArray())
                        if (n.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array) foreach (var c in ch.EnumerateArray()) isChild.Add(c.GetInt32());
                    for (int i = 0; i < nodes.GetArrayLength(); i++) if (!isChild.Contains(i)) roots.Add(i);
                }

                if (nodes.ValueKind != JsonValueKind.Array || roots.Count == 0)
                {
                    // no node graph: every mesh once, in order
                    foreach (var m in meshes.EnumerateArray()) AddMesh(m, accessors, result);
                    return result;
                }

                var visiting = new HashSet<int>();
                void Visit(int ni)
                {
                    if (ni < 0 || ni >= nodes.GetArrayLength() || !visiting.Add(ni)) return;
                    var node = nodes[ni];
                    if (node.TryGetProperty("mesh", out var mi) && mi.ValueKind == JsonValueKind.Number)
                    {
                        int idx = mi.GetInt32();
                        if (idx >= 0 && idx < meshes.GetArrayLength()) AddMesh(meshes[idx], accessors, result);
                    }
                    if (node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                        foreach (var c in ch.EnumerateArray()) Visit(c.GetInt32());
                    visiting.Remove(ni);
                }
                foreach (var r in roots) Visit(r);
                return result;
            }
        }

        private static void AddMesh(JsonElement mesh, JsonElement accessors, List<Prim> result)
        {
            if (!mesh.TryGetProperty("primitives", out var prims) || prims.ValueKind != JsonValueKind.Array) return;
            foreach (var p in prims.EnumerateArray())
            {
                int mode = p.TryGetProperty("mode", out var md) && md.ValueKind == JsonValueKind.Number ? md.GetInt32() : 4;
                if (mode < 4) continue;   // points / lines produce no triangle submesh
                var prim = new Prim();
                if (p.TryGetProperty("attributes", out var attr) && attr.TryGetProperty("POSITION", out var pos) && pos.ValueKind == JsonValueKind.Number)
                {
                    var acc = Accessor(accessors, pos.GetInt32());
                    if (acc.ValueKind == JsonValueKind.Object)
                    {
                        prim.Vertices = Count(acc);
                        prim.Min = Vec3(acc, "min"); prim.Max = Vec3(acc, "max");
                    }
                }
                int indexCount = prim.Vertices;
                if (p.TryGetProperty("indices", out var ind) && ind.ValueKind == JsonValueKind.Number)
                {
                    var acc = Accessor(accessors, ind.GetInt32());
                    if (acc.ValueKind == JsonValueKind.Object) indexCount = Count(acc);
                }
                prim.Triangles = mode == 4 ? indexCount / 3 : Math.Max(0, indexCount - 2);
                if (prim.Vertices > 0) result.Add(prim);
            }
        }

        private static JsonElement Accessor(JsonElement accessors, int i)
            => accessors.ValueKind == JsonValueKind.Array && i >= 0 && i < accessors.GetArrayLength() ? accessors[i] : default;

        private static int Count(JsonElement acc) => acc.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;

        private static float[] Vec3(JsonElement acc, string name)
        {
            if (!acc.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < 3) return null;
            return new[] { (float)a[0].GetDouble(), (float)a[1].GetDouble(), (float)a[2].GetDouble() };
        }

        private static string ReadJson(string path)
        {
            try
            {
                if (path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) return File.ReadAllText(path);
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 20 || br.ReadUInt32() != 0x46546C67) return null;   // "glTF"
                    br.ReadUInt32(); br.ReadUInt32();                                      // version, length
                    int chunkLen = br.ReadInt32();
                    if (br.ReadUInt32() != 0x4E4F534A || chunkLen <= 0 || chunkLen > fs.Length) return null;   // "JSON"
                    return Encoding.UTF8.GetString(br.ReadBytes(chunkLen));
                }
            }
            catch { return null; }
        }
    }
}
