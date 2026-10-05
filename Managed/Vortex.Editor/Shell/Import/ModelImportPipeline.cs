using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.DllWrapper;

namespace VortexEditor.Shell.AssetImport
{
    /// <summary>What the import does besides copying (the Windows import dialog's options + the model importer's steps).</summary>
    public sealed class ImportOptions
    {
        /// <summary>Copy the file into the target folder (off = register a file that already lives in the project).</summary>
        public bool CopyToProject = true;
        /// <summary>Write / keep the asset's .vmeta (GUID, type, tags).</summary>
        public bool GenerateMeta = true;
        /// <summary>Models: bring the textures next to the source model (and its textures/ folder) along.</summary>
        public bool CopyRelatedTextures = true;
        /// <summary>Models: write each embedded animation clip as a .vanim (animations/).</summary>
        public bool ExtractAnimations = true;
        /// <summary>Replace files that already exist in the target (off = keep both, the new one gets a suffix).</summary>
        public bool Overwrite = true;
        public readonly List<string> Tags = new List<string>();
        /// <summary>Register the import in the global asset library (subject to the library's settings).</summary>
        public bool RegisterInLibrary = true;
        /// <summary>"Import as new": give the content a new library entry (this name) even when the library knows it.</summary>
        public bool LibraryForceNew;
        public string LibraryName;
    }

    /// <summary>Everything one imported file produced (for the result summary).</summary>
    public sealed class ImportReport
    {
        public string SourcePath, TargetPath, Name, Kind;
        public bool Success, IsModel;
        public string Error;
        public Guid AssetGuid;
        public readonly List<string> Warnings = new List<string>();
        // models
        public string Folder;
        public readonly List<string> SubmeshNames = new List<string>();
        public readonly List<bool> SubmeshTextured = new List<bool>();
        public readonly List<string> MaterialFiles = new List<string>();
        public readonly List<string> CopiedFiles = new List<string>();
        public readonly List<string> CopiedTextures = new List<string>();
        public readonly List<string> ExtractedTextures = new List<string>();
        public readonly List<string> MissingTextures = new List<string>();
        public readonly List<string> AnimationClips = new List<string>();
        public int Bones;
    }

    /// <summary>
    /// The import pipeline behind the Import dialog — the macOS port of the WPF ModelImportService (which is WPF-bound):
    /// a model lands in its own folder &lt;target&gt;/&lt;name&gt;/ with its companion files (.mtl / .bin / referenced images),
    /// the textures next to it, one sidecar .vmat per submesh (materials/submesh_N.vmat, with the base colour, PBR factors
    /// and the maps the importer found), its animation clips as .vanim files (animations/) and a .vmeta; other files are
    /// copied with a .vmeta. Tags go to the AssetTagService. UI thread (native import).
    /// </summary>
    public static class ModelImportPipeline
    {
        public static readonly string[] ModelExtensions = { ".fbx", ".obj", ".gltf", ".glb", ".dae", ".3ds", ".blend", ".vmesh" };
        private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".tif", ".tiff", ".hdr" };

        public static bool IsModel(string path) => ModelExtensions.Contains((Path.GetExtension(path ?? "") ?? "").ToLowerInvariant());
        public static bool IsTexture(string path) => TextureExtensions.Contains((Path.GetExtension(path ?? "") ?? "").ToLowerInvariant());

        public static string KindOf(string path)
        {
            switch (AssetTypeOf(path))
            {
                case AssetType.Mesh: return "Model";
                case AssetType.Texture: return "Texture";
                case AssetType.Material: return "Material";
                case AssetType.Shader: return "Shader";
                case AssetType.Audio: return "Audio";
                case AssetType.Script: return "Script";
                case AssetType.Prefab: return "Prefab";
                case AssetType.Scene: return "Scene";
                case AssetType.Animation: return "Animation";
                case AssetType.UI: return "UI";
                case AssetType.Font: return "Font";
                default: return "File";
            }
        }

        public static AssetType AssetTypeOf(string path)
        {
            switch ((Path.GetExtension(path ?? "") ?? "").ToLowerInvariant())
            {
                case ".vscene": return AssetType.Scene;
                case ".vmesh": case ".fbx": case ".obj": case ".gltf": case ".glb": case ".dae": case ".blend": case ".3ds": return AssetType.Mesh;
                case ".vmat": case ".mat": return AssetType.Material;
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".bmp": case ".psd": case ".hdr": case ".dds": case ".tif": case ".tiff": case ".gif": case ".webp": return AssetType.Texture;
                case ".ventity": return AssetType.Prefab;
                case ".hlsl": case ".glsl": case ".shader": case ".vshader": case ".metal": return AssetType.Shader;
                case ".wav": case ".mp3": case ".ogg": case ".flac": case ".vsndc": return AssetType.Audio;
                case ".cs": case ".cpp": case ".h": return AssetType.Script;
                case ".ttf": case ".otf": return AssetType.Font;
                case ".vui": return AssetType.UI;
                case ".vanim": return AssetType.Animation;
                default: return AssetType.Unknown;
            }
        }

        /// <summary>Default target folder (project-relative) for a file type — the Windows import dialog's defaults.</summary>
        public static string DefaultFolderFor(string path)
        {
            switch (AssetTypeOf(path))
            {
                case AssetType.Mesh: return "Assets/Models";
                case AssetType.Texture: return "Assets/Textures";
                case AssetType.Material: return "Assets/Materials";
                case AssetType.Shader: return "Assets/Shaders";
                case AssetType.Audio: return "Assets/Audio";
                case AssetType.Script: return "Assets/Scripts";
                case AssetType.Prefab: return "Assets/Prefabs";
                case AssetType.Scene: return "Assets/Scenes";
                case AssetType.Animation: return "Assets/Animations";
                default: return "Assets";
            }
        }

        /// <summary>Where a file would land (to detect overwrites before importing).</summary>
        public static string TargetPathFor(string source, string targetFolder, string newName = null)
        {
            string name = string.IsNullOrWhiteSpace(newName) ? Path.GetFileNameWithoutExtension(source) : newName.Trim();
            string ext = Path.GetExtension(source);
            if (IsModel(source)) return Path.Combine(targetFolder, name, name + ext);
            return Path.Combine(targetFolder, name + ext);
        }

        private static bool InsideProject(string path)
        {
            var root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static string Rel(string abs)
        {
            var root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return abs;
            try { return Path.GetRelativePath(root, abs).Replace('\\', '/'); } catch { return abs; }
        }

        private static string Unique(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int n = 1; ; n++) { var p = Path.Combine(dir, name + "_" + n + ext); if (!File.Exists(p) && !Directory.Exists(p)) return p; }
        }

        private static void CopyFile(string from, string to, bool overwrite, List<string> log)
        {
            if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(to) && !overwrite) return;
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to, true);
            log?.Add(to);
        }

        // ================================================================== any file

        /// <summary>Import one file into <paramref name="targetFolder"/> (absolute). Models go through <see cref="ImportModel"/>.</summary>
        public static ImportReport Import(string source, string targetFolder, ImportOptions o, string newName = null)
        {
            var r = IsModel(source) ? ImportModel(source, targetFolder, o, newName) : ImportFile(source, targetFolder, o, newName);
            // global asset library (#55): hash into the .vmeta + register machine-wide, in the background — a slow or
            // broken library never delays or fails the import
            if (r.Success && r.TargetPath != null && o.RegisterInLibrary)
                LibraryProjects.QueueImported(r.TargetPath, o.Tags, ProjectData.Current?.Path, ProjectData.Current?.Name, o.LibraryName, o.LibraryForceNew);
            return r;
        }

        public static ImportReport ImportFile(string source, string targetFolder, ImportOptions o, string newName = null)
        {
            var r = new ImportReport { SourcePath = source, Kind = KindOf(source), Name = string.IsNullOrWhiteSpace(newName) ? Path.GetFileNameWithoutExtension(source) : newName.Trim() };
            try
            {
                if (!File.Exists(source)) { r.Error = "Source file not found."; return r; }
                if (string.IsNullOrEmpty(ProjectData.Current?.Path)) { r.Error = "No project is open."; return r; }
                string target;
                if (o.CopyToProject || !InsideProject(source))
                {
                    if (!o.CopyToProject) r.Warnings.Add("The file is outside the project, so it was copied.");
                    Directory.CreateDirectory(targetFolder);
                    target = TargetPathFor(source, targetFolder, newName);
                    if (File.Exists(target) && !o.Overwrite) target = Unique(target);
                    CopyFile(source, target, true, null);
                }
                else target = source;
                r.TargetPath = target;
                if (o.GenerateMeta) r.AssetGuid = WriteMeta(target, AssetTypeOf(target));
                ApplyTags(r.AssetGuid, target, o);
                if (AssetTypeOf(source) == AssetType.Material) r.Warnings.Add("Texture paths inside the material are relative to its original folder — re-assign them in the Material Editor if they no longer resolve.");
                r.Success = true;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        // ================================================================== models

        public static ImportReport ImportModel(string source, string targetFolder, ImportOptions o, string newName = null)
        {
            var r = new ImportReport { SourcePath = source, Kind = "Model", IsModel = true, Name = string.IsNullOrWhiteSpace(newName) ? Path.GetFileNameWithoutExtension(source) : newName.Trim() };
            try
            {
                if (!File.Exists(source)) { r.Error = "Source file not found."; return r; }
                string project = ProjectData.Current?.Path;
                if (string.IsNullOrEmpty(project)) { r.Error = "No project is currently open."; return r; }
                string ext = Path.GetExtension(source).ToLowerInvariant();
                if (ext != ".vmesh" && !VortexAPI.IsAssimpAvailable()) { r.Error = "The model importer (assimp) is not available in this engine build."; return r; }

                // ---- 1. files: the model gets its own self-contained folder
                string modelPath;
                if (o.CopyToProject || !InsideProject(source))
                {
                    if (!o.CopyToProject) r.Warnings.Add("The model is outside the project, so it was copied.");
                    modelPath = TargetPathFor(source, targetFolder, newName);
                    if (File.Exists(modelPath) && !o.Overwrite) modelPath = Path.Combine(Unique(Path.GetDirectoryName(modelPath)), Path.GetFileName(modelPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(modelPath));
                    CopyFile(source, modelPath, true, null);
                    CopyCompanions(source, modelPath, o, r);
                }
                else modelPath = source;
                r.TargetPath = modelPath;
                r.Folder = Path.GetDirectoryName(modelPath);
                string rel = Rel(modelPath);

                if (ext == ".vmesh")
                {
                    long mesh = VortexAPI.LoadVMeshFromFile(modelPath);
                    if (mesh < 0) { r.Error = "Failed to load the .vmesh file."; return r; }
                    r.SubmeshNames.Add(r.Name); r.SubmeshTextured.Add(false);
                    SceneRenderService.RegisterMeshIdForPath(rel, mesh);
                }
                else
                {
                    // ---- 2. native import (validates the file; embedded textures are written next to the model)
                    var before = SafeFiles(r.Folder);
                    var subs = VortexAPI.ImportModelWithMaterialsFromFile(modelPath);
                    if (subs == null || subs.Length == 0)
                    {
                        r.Error = "Failed to import '" + Path.GetFileName(modelPath) + "'. The model may be corrupted, use an unsupported format or unsupported features. Try exporting it as glTF (.glb) or FBX.";
                        return r;
                    }
                    foreach (var f in SafeFiles(r.Folder).Except(before, StringComparer.OrdinalIgnoreCase))
                        if (IsTexture(f)) r.ExtractedTextures.Add(f);

                    string[] names = null; try { names = VortexAPI.GetSubmeshNames(modelPath, subs.Length); } catch { }
                    VortexAPI.SubmeshTextureSet[] texSets = null; try { texSets = VortexAPI.GetSubmeshTexturePaths(modelPath, subs.Length); } catch { }
                    VortexAPI.SubmeshMaterialProps[] props = null; try { props = VortexAPI.GetSubmeshMaterialProps(modelPath, subs.Length); } catch { }

                    // old importers report embedded maps as "*N": extract them into textures/ and resolve
                    string[] embedded = null;
                    string texDir = Path.Combine(r.Folder, "textures");
                    if (texSets != null && texSets.Any(t => new[] { t.Albedo, t.Normal, t.Metallic, t.Roughness, t.AO, t.Emissive }.Any(p => p != null && p.StartsWith("*"))))
                    {
                        Directory.CreateDirectory(texDir);
                        try { embedded = VortexAPI.ExtractEmbeddedTextures(modelPath, texDir); } catch { }
                        if (embedded != null) foreach (var e in embedded) if (!string.IsNullOrEmpty(e)) r.ExtractedTextures.Add(Path.Combine(texDir, e));
                    }
                    string Resolve(string p)
                    {
                        if (string.IsNullOrEmpty(p)) return null;
                        if (p.StartsWith("*") && embedded != null && int.TryParse(p.Substring(1), out int i) && i >= 0 && i < embedded.Length && !string.IsNullOrEmpty(embedded[i])) return Path.Combine(texDir, embedded[i]);
                        return p;
                    }

                    // ---- 3. one .vmat per submesh (materials/submesh_N.vmat) — what scene placement binds
                    string matDir = Path.Combine(r.Folder, "materials");
                    Directory.CreateDirectory(matDir);
                    var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < subs.Length; i++)
                    {
                        string smName = names != null && i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : "Submesh_" + i;
                        var v = new VortexMaterial { Name = smName };
                        if (props != null && i < props.Length)
                        {
                            if (props[i].BaseColor != null && props[i].BaseColor.Length >= 4) v.BaseColor = props[i].BaseColor;
                            v.Metallic = props[i].Metallic; v.Roughness = props[i].Roughness;
                        }
                        bool textured = false;
                        if (texSets != null && i < texSets.Length)
                        {
                            var t = texSets[i];
                            string Map(string p) { var a = Resolve(p); if (a == null) return null; textured = true; if (!File.Exists(a)) missing.Add(a); return a; }
                            v.AlbedoTexture = Map(t.Albedo); v.NormalTexture = Map(t.Normal); v.MetallicTexture = Map(t.Metallic);
                            v.RoughnessTexture = Map(t.Roughness); v.AOTexture = Map(t.AO); v.EmissiveTexture = Map(t.Emissive);
                        }
                        var vmatPath = Path.Combine(matDir, "submesh_" + i + ".vmat");
                        if (File.Exists(vmatPath))
                        {
                            // keep what the user authored on a previous import (shader, footsteps, blend, tiling …)
                            try { var prev = VortexMaterial.Load(vmatPath); if (prev != null) { v.ShaderAsset = prev.ShaderAsset; v.FootstepSound = prev.FootstepSound; } } catch { }
                        }
                        v.MakePathsRelative(matDir);
                        if (v.Save(vmatPath)) r.MaterialFiles.Add(vmatPath);
                        r.SubmeshNames.Add(smName);
                        r.SubmeshTextured.Add(textured || subs[i].TextureId >= 0);

                        // the render cache reuses the imported meshes when the model is placed (no second import)
                        string subPath = rel + "#submesh" + i;
                        SceneRenderService.RegisterMeshIdForPath(subPath, subs[i].MeshId);
                        SceneRenderService.RegisterMaterialForMeshPath(subPath, subs[i].MaterialId);
                    }
                    SceneRenderService.RegisterMaterialForMeshPath(rel, subs[0].MaterialId);
                    r.MissingTextures.AddRange(missing);

                    // ---- 4. animation clips -> animations/*.vanim
                    if (o.ExtractAnimations) ExtractClips(modelPath, r);
                }

                // ---- 5. asset database + tags
                if (o.GenerateMeta)
                {
                    r.AssetGuid = WriteMeta(modelPath, AssetType.Mesh);
                    foreach (var t in r.CopiedTextures.Concat(r.ExtractedTextures).Distinct(StringComparer.OrdinalIgnoreCase))
                        if (File.Exists(t)) WriteMeta(t, AssetType.Texture);
                }
                ApplyTags(r.AssetGuid, modelPath, o);
                r.Success = true;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        /// <summary>Write every embedded animation clip of a model as a .vanim into its animations/ folder.</summary>
        public static int ExtractClips(string modelPath, ImportReport r = null)
        {
            int written = 0;
            try
            {
                int clipCount = VortexAPI.GetAnimationCount(modelPath);
                if (clipCount <= 0) return 0;
                var nodes = VortexAPI.GetSkeletonNodes(modelPath);
                if (r != null) { try { r.Bones = VortexAPI.GetSkeletonBones(modelPath)?.Length ?? 0; } catch { } }
                var animDir = Path.Combine(Path.GetDirectoryName(modelPath), "animations");
                Directory.CreateDirectory(animDir);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < clipCount; c++)
                {
                    if (!VortexAPI.GetAnimationInfo(modelPath, c, out string clipName, out float dur)) continue;
                    var flat = VortexAPI.GetAnimationData(modelPath, c);
                    var clip = Editor.Core.Animation.AnimationService.ClipFromModelData(clipName, dur, flat, nodes);
                    if (clip == null || clip.Tracks.Count == 0) continue;
                    clip.Model = Rel(modelPath);
                    string safe = string.Concat((clipName ?? "Clip" + c).Split(Path.GetInvalidFileNameChars()));
                    if (string.IsNullOrWhiteSpace(safe)) safe = "Clip" + c;
                    string unique = safe; int n = 1;
                    while (!used.Add(unique)) unique = safe + "_" + n++;
                    var p = Path.Combine(animDir, unique + ".vanim");
                    if (clip.Save(p)) { written++; r?.AnimationClips.Add(p); }
                }
            }
            catch (Exception ex) { r?.Warnings.Add("Animation clips: " + ex.Message); }
            return written;
        }

        /// <summary>Companion files a model needs: OBJ .mtl (+ mtllib), glTF buffers / images (relative URIs, sub-folders kept),
        /// and — when enabled — the textures next to the source model and in its textures/ folder.</summary>
        private static void CopyCompanions(string source, string modelPath, ImportOptions o, ImportReport r)
        {
            string srcDir = Path.GetDirectoryName(source), dstDir = Path.GetDirectoryName(modelPath);
            string ext = Path.GetExtension(source).ToLowerInvariant();
            void Take(string relPath, bool texture)
            {
                if (string.IsNullOrEmpty(relPath) || relPath.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || relPath.Contains("://")) return;
                relPath = Uri.UnescapeDataString(relPath).Replace('\\', '/');
                var from = Path.GetFullPath(Path.Combine(srcDir, relPath));
                if (!File.Exists(from)) { if (texture) r.MissingTextures.Add(Path.Combine(dstDir, relPath)); return; }
                // keep the relative layout, but never escape the model folder
                var to = Path.GetFullPath(Path.Combine(dstDir, relPath));
                if (!to.StartsWith(Path.GetFullPath(dstDir), StringComparison.OrdinalIgnoreCase)) to = Path.Combine(dstDir, Path.GetFileName(relPath));
                CopyFile(from, to, o.Overwrite, texture ? r.CopiedTextures : r.CopiedFiles);
            }
            try
            {
                if (ext == ".obj")
                {
                    var mtls = new List<string>();
                    foreach (var line in File.ReadLines(source))
                        if (line.StartsWith("mtllib ", StringComparison.OrdinalIgnoreCase)) mtls.Add(line.Substring(7).Trim());
                    var sameName = Path.ChangeExtension(Path.GetFileName(source), ".mtl");
                    if (!mtls.Contains(sameName, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(srcDir, sameName))) mtls.Add(sameName);
                    foreach (var m in mtls)
                    {
                        Take(m, false);
                        var mtlFull = Path.Combine(srcDir, m.Replace('\\', '/'));
                        if (!File.Exists(mtlFull)) continue;
                        foreach (var raw in File.ReadLines(mtlFull))
                        {
                            var l = raw.Trim(); if (!l.StartsWith("map_", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("bump", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("norm", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("disp", StringComparison.OrdinalIgnoreCase)) continue;
                            var parts = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2) Take(parts[parts.Length - 1], true);
                        }
                    }
                }
                else if (ext == ".gltf")
                {
                    using (var doc = JsonDocument.Parse(File.ReadAllText(source), new JsonDocumentOptions { AllowTrailingCommas = true }))
                    {
                        foreach (var arr in new[] { "buffers", "images" })
                            if (doc.RootElement.TryGetProperty(arr, out var a) && a.ValueKind == JsonValueKind.Array)
                                foreach (var e in a.EnumerateArray())
                                    if (e.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String) Take(u.GetString(), arr == "images");
                    }
                }
                else
                {
                    var bin = Path.ChangeExtension(source, ".bin");
                    if (File.Exists(bin)) Take(Path.GetFileName(bin), false);
                }

                if (o.CopyRelatedTextures)
                {
                    foreach (var f in Directory.GetFiles(srcDir)) if (IsTexture(f)) Take(Path.GetFileName(f), true);
                    foreach (var sub in new[] { "textures", "Textures" })
                    {
                        var d = Path.Combine(srcDir, sub);
                        if (!Directory.Exists(d)) continue;
                        foreach (var f in Directory.GetFiles(d)) if (IsTexture(f)) Take(sub + "/" + Path.GetFileName(f), true);
                    }
                }
            }
            catch (Exception ex) { r.Warnings.Add("Companion files: " + ex.Message); }
            // de-duplicate (a glTF image may also be a sibling texture)
            Dedup(r.CopiedTextures); Dedup(r.CopiedFiles);
            r.MissingTextures.RemoveAll(File.Exists);
        }

        private static void Dedup(List<string> l) { var d = l.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); l.Clear(); l.AddRange(d); }

        private static List<string> SafeFiles(string dir) { try { return Directory.Exists(dir) ? Directory.GetFiles(dir).ToList() : new List<string>(); } catch { return new List<string>(); } }

        /// <summary>Create (or refresh, keeping the GUID) the .vmeta of an asset. Returns its GUID.</summary>
        public static Guid WriteMeta(string assetPath, AssetType type)
        {
            string metaPath = assetPath + AssetDatabase.MetaFileExtension;
            AssetMetadata meta = null;
            if (File.Exists(metaPath)) { try { meta = DataSerializer.LoadFromJson<AssetMetadata>(metaPath); } catch { meta = null; } }
            if (meta == null) meta = new AssetMetadata(type, Rel(assetPath), Path.GetFileName(assetPath));
            else { meta.RelativePath = Rel(assetPath); meta.FileName = Path.GetFileName(assetPath); if (meta.Type == AssetType.Unknown) meta.Type = type; }
            try { var fi = new FileInfo(assetPath); meta.LastModified = fi.LastWriteTime; meta.FileSize = fi.Length; } catch { }
            AssetDatabase.Instance.SaveMetadata(meta, metaPath);
            return meta.Guid;
        }

        private static void ApplyTags(Guid guid, string assetPath, ImportOptions o)
        {
            if (o.Tags.Count == 0) return;
            if (guid == Guid.Empty)
            {
                // tags live by GUID: an asset without a .vmeta gets one so its tags stick
                guid = WriteMeta(assetPath, AssetTypeOf(assetPath));
            }
            foreach (var t in o.Tags) { try { AssetTagService.Instance.AddTag(guid, t); } catch { } }
            try
            {
                string metaPath = assetPath + AssetDatabase.MetaFileExtension;
                var meta = File.Exists(metaPath) ? DataSerializer.LoadFromJson<AssetMetadata>(metaPath) : null;
                if (meta != null)
                {
                    meta.Tags = (meta.Tags ?? new List<string>()).Union(o.Tags, StringComparer.OrdinalIgnoreCase).ToList();
                    AssetDatabase.Instance.SaveMetadata(meta, metaPath);
                }
            }
            catch { }
        }
    }
}
