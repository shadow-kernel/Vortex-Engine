using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Asset creation and scene placement, shared by the editor shells (port of the WPF asset browser's
    /// logic without any UI). Paths in are absolute; paths stored on components are project-relative.
    /// </summary>
    public static class AssetActions
    {
        private static readonly string[] ModelExt = { ".fbx", ".obj", ".gltf", ".glb", ".dae", ".3ds", ".blend" };

        public static string ProjectRoot => ProjectData.Current?.Path;

        public static string DefaultFolderFor(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".bmp": case ".hdr": case ".dds": return "Textures";
                case ".wav": case ".mp3": case ".ogg": case ".flac": return "Audio";
                case ".cs": return "Scripts";
                case ".vmat": return "Materials";
                case ".ventity": return "Prefabs";
                case ".vscene": return "Scenes";
                case ".vanim": return "Animations";
                case ".vui": return "UI";
                case ".hlsl": case ".metal": return "Shaders";
                default: return ModelExt.Contains((ext ?? "").ToLowerInvariant()) ? "Models" : "";
            }
        }

        public static string Relative(string absolute)
        {
            var root = ProjectRoot;
            if (string.IsNullOrEmpty(absolute) || string.IsNullOrEmpty(root) || !Path.IsPathRooted(absolute)) return absolute;
            if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return absolute;
            return absolute.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
        }

        // ------------------------------------------------------------------ create
        public static string CreateMaterial(string path, string type)
        {
            if (!path.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase)) path += ".vmat";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var material = new VortexMaterial { Name = Path.GetFileNameWithoutExtension(path), BlendMode = type == "Transparent" ? "AlphaBlend" : "Opaque", ShaderType = type };
            if (type == "Transparent") material.BaseColor = new[] { 1f, 1f, 1f, 0.5f };
            if (!material.Save(path)) throw new IOException("Could not write " + path);
            try { AssetDatabase.Instance.Refresh(); } catch { }
            return path;
        }

        public static string CreateShader(string path, string type)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".glsl")
            {
                // The Vulkan backend compiles a material shader from ONE .glsl file, once per stage
                // (VORTEX_VERTEX_STAGE / VORTEX_FRAGMENT_STAGE). Start from the template that ships next to
                // the shaders, which already declares the standard bindings and compiles as-is.
                string src = null;
                try
                {
                    var dir = Editor.Core.Native.NativeLoader.ShaderDirectory;
                    var tpl = dir != null ? Path.Combine(Path.GetDirectoryName(dir) ?? "", "material_template.glsl") : null;
                    if (tpl != null && File.Exists(tpl)) src = File.ReadAllText(tpl);
                }
                catch { }
                if (src == null) src = "#version 450\n// Custom material shader — copy Engine/Shaders/glsl/material_template.glsl as the starting point.\n";
                File.WriteAllText(path, "// " + Path.GetFileName(path) + " — custom " + type + " material shader (GLSL/Vulkan). Edit the fragment stage to change the look.\n" + src);
            }
            else if (ext == ".metal")
            {
                // The Metal backend compiles a material shader from its VSMain/PSMain with the standard bindings:
                // start from the engine's own PBR shader so every binding is already right.
                string src = null;
                try
                {
                    var dir = Editor.Core.Native.NativeLoader.ShaderDirectory;
                    var std = dir != null ? Path.Combine(dir, "standard.metal") : null;
                    if (std != null && File.Exists(std)) src = File.ReadAllText(std);
                }
                catch { }
                if (src == null) src = "#include <metal_stdlib>\nusing namespace metal;\n// Custom material shader — copy Engine/Shaders/msl/standard.metal as the starting point (VSMain/PSMain).\n";
                File.WriteAllText(path, "// " + Path.GetFileName(path) + " — custom " + type + " material shader (Metal). Edit PSMain to change the look.\n" + src);
            }
            else
            {
                var st = type == "Unlit" ? ShaderType.Unlit : type == "Transparent" ? ShaderType.Transparent : ShaderType.Standard;
                File.WriteAllText(path, VortexShader.HlslTemplate(st));
            }
            try { AssetDatabase.Instance.Refresh(); } catch { }
            return path;
        }

        public static string CreateEmptyPrefab()
        {
            var created = PrefabService.Instance.CreateEmptyPrefab("NewPrefab");
            if (string.IsNullOrEmpty(created)) throw new InvalidOperationException("Open a project first.");
            try { AssetDatabase.Instance.Refresh(); } catch { }
            return created;
        }

        public static string CreateUiScreen(string path)
        {
            const string template = "{\n  \"vui\": 1, \"designW\": 1920, \"designH\": 1080,\n  \"root\": {\n    \"kind\": \"Panel\", \"id\": \"root\", \"anchor\": \"TopLeft\",\n    \"stretchX\": true, \"stretchY\": true, \"off\": [0,0], \"size\": [0,0],\n    \"bg\": [0.06,0.06,0.08,1], \"blocksInput\": true,\n    \"children\": []\n  }\n}\n";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, template);
            try { AssetDatabase.Instance.Refresh(); } catch { }
            return path;
        }

        public static string CreateAnimationClip(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var clip = new Editor.Core.Animation.VortexAnimClip { Name = Path.GetFileNameWithoutExtension(path) };
            clip.Save(path);
            try { AssetDatabase.Instance.Refresh(); } catch { }
            return path;
        }

        public static string CreateSoundContainer()
        {
            var root = ProjectRoot; if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("Open a project first.");
            var dir = Path.Combine(root, "Assets", "Audio");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "NewSoundContainer" + Editor.Core.Audio.SoundContainer.FileExtension);
            int n = 1;
            while (File.Exists(path)) path = Path.Combine(dir, "NewSoundContainer" + (++n) + Editor.Core.Audio.SoundContainer.FileExtension);
            new Editor.Core.Audio.SoundContainer().Save(path);
            return path;
        }

        // ------------------------------------------------------------------ import
        /// <summary>
        /// Import a model file into the project: copies the model (and the textures next to it) into
        /// Assets/&lt;targetFolder&gt;/&lt;model name&gt;/, validates it with the native importer and writes one
        /// .vmat per submesh (materials/submesh_N.vmat) so placement binds real materials. Returns the imported
        /// model's absolute path.
        /// </summary>
        public static string ImportModel(string sourcePath, string targetFolderRelative = "Models") => ImportModel(sourcePath, targetFolderRelative, true);

        /// <summary>leftHanded (#352): write the model's .vimport sidecar so the importer converts the right-handed
        /// source into the engine's left-handed space — on by default for new imports; existing models keep theirs.</summary>
        public static string ImportModel(string sourcePath, string targetFolderRelative, bool leftHanded)
        {
            var root = ProjectRoot; if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("Open a project first.");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException(sourcePath);
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            string destDir = Path.Combine(root, "Assets", targetFolderRelative.Replace('\\', '/').Trim('/').Replace("Assets/", ""), name);
            Directory.CreateDirectory(destDir);
            string dest = Path.Combine(destDir, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, dest, true);
            if (leftHanded) ModelImportSettings.SaveLeftHanded(dest, true);   // read by the native import below (#352)
            // Sidecar textures (and .mtl / .bin companions) from the source folder.
            string srcDir = Path.GetDirectoryName(sourcePath);
            foreach (var f in Directory.GetFiles(srcDir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tga" || ext == ".bmp" || ext == ".dds" || ext == ".mtl" || ext == ".bin")
                {
                    string t = Path.Combine(destDir, Path.GetFileName(f));
                    if (!File.Exists(t)) File.Copy(f, t);
                }
            }
            int submeshes = 0;
            try { submeshes = VortexAPI.GetSubmeshCount(dest); } catch { }
            if (submeshes <= 0) throw new InvalidOperationException("The model could not be read by the importer (" + Path.GetFileName(sourcePath) + ").");
            var textures = FindTexturesForModel(dest);
            string[] names = null; try { names = VortexAPI.GetSubmeshNames(dest, submeshes); } catch { }
            string matDir = Path.Combine(destDir, "materials");
            Directory.CreateDirectory(matDir);
            for (int i = 0; i < submeshes; i++)
            {
                string vmat = Path.Combine(matDir, "submesh_" + i + ".vmat");
                if (File.Exists(vmat)) continue;
                string smName = names != null && i < names.Length ? names[i] : "Submesh_" + i;
                var m = new VortexMaterial { Name = smName };
                string tex = textures.Count > 0 ? FindTextureForSubmesh(smName, textures) : null;
                if (!string.IsNullOrEmpty(tex)) m.AlbedoTexture = Path.GetRelativePath(matDir, tex).Replace('/', '\\');
                m.Save(vmat);
            }
            EnsureAnimationClips(dest);   // #340
            try { AssetDatabase.Instance.Refresh(); } catch { }
            return dest;
        }

        private static readonly HashSet<string> _clipsChecked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Write a model's embedded animation clips to its animations/ folder when none are there yet (#340).
        /// A file copied into Assets by hand, by a script or over MCP never went through the import dialog, so its
        /// takes were never extracted. Once per file and session; returns the number of clips written.</summary>
        public static int EnsureAnimationClips(string fullModelPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fullModelPath) || !File.Exists(fullModelPath)) return 0;
                if (!_clipsChecked.Add(fullModelPath)) return 0;
                string animDir = Path.Combine(Path.GetDirectoryName(fullModelPath) ?? "", "animations");
                if (Directory.Exists(animDir) && Directory.GetFiles(animDir, "*.vanim").Length > 0) return 0;
                if (VortexAPI.GetAnimationCount(fullModelPath) <= 0) return 0;
                var written = Editor.Core.Animation.AnimationService.ExtractClipsFromModel(fullModelPath);
                if (written.Count > 0)
                    try { ConsoleService.Instance.LogSystem("Animation clips: " + written.Count + " extracted from " + Path.GetFileName(fullModelPath) + " into animations/"); } catch { }
                return written.Count;
            }
            catch (Exception ex)
            {
                try { ConsoleService.Instance.LogWarning("Animation clips of " + Path.GetFileName(fullModelPath) + ": " + ex.Message); } catch { }
                return 0;
            }
        }

        // ------------------------------------------------------------------ place in scene
        public static GameEntity PlacePrefab(string prefabPath)
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) throw new InvalidOperationException("No active scene.");
            var inst = PrefabService.Instance.InstantiatePrefab(prefabPath, scene);
            if (inst == null) throw new InvalidOperationException("The prefab is empty or unreadable.");
            return inst;
        }

        /// <summary>Add a model file to the active scene: one entity, or a parent with one child per submesh.</summary>
        public static GameEntity AddModelToScene(string modelPath)
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) throw new InvalidOperationException("No active scene.");
            string projectPath = ProjectRoot ?? "";
            string fullPath = Path.IsPathRooted(modelPath) ? modelPath : Path.Combine(projectPath, modelPath);
            string entityPath = Relative(fullPath);
            string name = Path.GetFileNameWithoutExtension(modelPath);
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();

            if (ModelExt.Contains(ext) && File.Exists(fullPath))
            {
                EnsureAnimationClips(fullPath);   // takes of a hand-copied model (#340)
                // through the render cache: one load per model and session, no import per placement (#357)
                var result = SceneRenderService.LoadModelSubmeshes(entityPath);
                if (result != null && result.Length > 1) return CreateMultiMaterialEntity(scene, name, entityPath, result, projectPath);
                if (result != null && result.Length == 1)
                {
                    var textures = FindTexturesForModel(fullPath);
                    var entity = new GameEntity(scene, name);
                    var mr = new MeshRenderer(entity) { MeshPath = entityPath, MaterialHandle = result[0].MaterialId };
                    try
                    {
                        string vmatRel = Path.Combine(Path.GetDirectoryName(entityPath) ?? "", "materials", "submesh_0.vmat").Replace('\\', '/');
                        if (File.Exists(Path.Combine(projectPath, vmatRel))) mr.MaterialPath = vmatRel;
                    }
                    catch { }
                    if (string.IsNullOrEmpty(mr.MaterialPath) && textures.Count > 0 && !HasOwnTexture(result[0].MaterialId)) BindTexture(mr, result[0].MaterialId, textures[0], projectPath);
                    if (result[0].MaterialId >= 0) SceneRenderService.RegisterMaterialForMeshPath(entityPath, result[0].MaterialId);
                    entity.AddComponent(mr);
                    entity.Transform.LocalPosition = new Vector3(0, 0, 0);
                    scene.AddEntity(entity);
                    return entity;
                }
            }
            var fallback = new GameEntity(scene, name);
            fallback.AddComponent(new MeshRenderer(fallback) { MeshPath = entityPath });
            fallback.Transform.LocalPosition = new Vector3(0, 0, 0);
            scene.AddEntity(fallback);
            return fallback;
        }

        /// <summary>The import material already carries the model's own albedo map (#351): the folder-scan fallback
        /// must not replace it with whatever image lies next to the file.</summary>
        private static bool HasOwnTexture(long materialId)
        {
            if (materialId < 0) return false;
            try { return VortexAPI.HasMaterialTexture(materialId); } catch { return false; }
        }

        private static void BindTexture(MeshRenderer mr, long materialId, string texPath, string projectPath)
        {
            // a GRAPHICS texture id (cached per file): LoadTextureResource returned a resource-manager handle from
            // another id space, which bound a random texture or none (#351)
            try { long texId = MaterialService.ImportTextureCached(texPath); if (texId >= 0 && materialId >= 0) VortexAPI.SetMaterialAlbedoTexture(materialId, texId); } catch { }
            mr.TexturePath = Relative(texPath);
        }

        private static GameEntity CreateMultiMaterialEntity(Scene scene, string modelName, string relativePath, VortexAPI.SubmeshImportData[] submeshes, string projectPath)
        {
            string fullModelPath = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(projectPath, relativePath);
            var textures = FindTexturesForModel(fullModelPath);
            string[] names = VortexAPI.GetSubmeshNames(fullModelPath, submeshes.Length) ?? Array.Empty<string>();
            var parent = new GameEntity(scene, modelName);
            parent.Transform.LocalPosition = new Vector3(0, 0, 0);
            scene.AddEntity(parent);
            for (int i = 0; i < submeshes.Length; i++)
            {
                var sm = submeshes[i];
                string childName = i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : "Submesh_" + i;
                var child = new GameEntity(scene, childName) { IsLockedToParent = true };
                string submeshPath = relativePath + "#submesh" + i;
                var mr = new MeshRenderer(child) { MeshPath = submeshPath, MaterialHandle = sm.MaterialId };
                try
                {
                    string vmatRel = Path.Combine(Path.GetDirectoryName(relativePath) ?? "", "materials", "submesh_" + i + ".vmat").Replace('\\', '/');
                    if (File.Exists(Path.Combine(projectPath, vmatRel))) mr.MaterialPath = vmatRel;
                }
                catch { }
                if (string.IsNullOrEmpty(mr.MaterialPath) && textures.Count > 0 && !HasOwnTexture(sm.MaterialId))
                {
                    string tex = FindTextureForSubmesh(childName, textures);
                    if (!string.IsNullOrEmpty(tex)) BindTexture(mr, sm.MaterialId, tex, projectPath);
                }
                if (sm.MaterialId >= 0) SceneRenderService.RegisterMaterialForMeshPath(submeshPath, sm.MaterialId);
                child.AddComponent(mr);
                child.Transform.LocalPosition = new Vector3(0, 0, 0);
                parent.AddChild(child);
            }
            return parent;
        }

        public static List<string> FindTexturesForModel(string modelPath)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath)) return result;
            var dir = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;
            var color = new List<string>(); var other = new List<string>();
            // the importer extracts a model's embedded images as embedded_<stem>_N: only this model's own ones count —
            // the folder-wide scan bound a tree's leaf PNG to every other prop in the folder (#351)
            string ownEmbedded = MeshRenderer.EmbeddedTexturePrefix(modelPath).ToLowerInvariant();
            foreach (var ext in new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.dds" })
            {
                try
                {
                    foreach (var file in Directory.GetFiles(dir, ext))
                    {
                        var fn = Path.GetFileName(file).ToLowerInvariant();
                        if (fn.StartsWith("embedded_") && !fn.StartsWith(ownEmbedded)) continue;
                        bool unwanted = fn.Contains("_nor") || fn.Contains("_normal") || fn.Contains("_nrm") || fn.Contains("normal.") || fn.Contains("_ao") || fn.Contains("_occ") || fn.Contains("occlusion")
                                        || fn.Contains("_rough") || fn.Contains("roughness") || fn.Contains("_metal") || fn.Contains("metallic") || fn.Contains("_spec") || fn.Contains("specular")
                                        || fn.Contains("_height") || fn.Contains("_disp") || fn.Contains("_emis") || fn.Contains("emission");
                        if (unwanted) continue;
                        bool isColor = fn.Contains("_col") || fn.Contains("col.") || fn.Contains("_color") || fn.Contains("color.") || fn.Contains("_diffuse") || fn.Contains("diffuse.") || fn.Contains("_albedo") || fn.Contains("albedo.") || fn.Contains("_base") || fn.Contains("basecolor");
                        (isColor ? color : other).Add(file);
                    }
                }
                catch { }
            }
            result.AddRange(color); result.AddRange(other);
            return result;
        }

        private static string FindTextureForSubmesh(string submeshName, List<string> textures)
        {
            if (string.IsNullOrEmpty(submeshName) || textures == null || textures.Count == 0) return textures?.FirstOrDefault() ?? "";
            string n = submeshName.ToLowerInvariant().Replace(" ", "_");
            foreach (var t in textures) if (Path.GetFileName(t).ToLowerInvariant().Contains(n)) return t;
            return textures.FirstOrDefault() ?? "";
        }
    }
}
