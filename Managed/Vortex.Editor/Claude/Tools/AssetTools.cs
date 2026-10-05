using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Assets.Store;
using Editor.Core.Data;
using Editor.ECS;
using ModelContextProtocol.Server;
using VortexEditor.Services;
using AssetActions = VortexEditor.Services.AssetActions;
using VortexEditor.Shell;
using VortexEditor.Shell.Prefab;

namespace VortexEditor.Claude.Tools
{
    /// <summary>Assets and prefabs (#88): the project's files, the PC-wide asset library, the Asset Store, placing models
    /// and prefabs, saving prefabs.</summary>
    [McpServerToolType, DisplayName("Assets")]
    public static class AssetTools
    {
        private static readonly Dictionary<string, string[]> Kinds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = new[] { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".3ds", ".blend", ".vmesh" },
            ["texture"] = new[] { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".psd", ".hdr", ".dds", ".exr", ".webp" },
            ["material"] = new[] { ".vmat" },
            ["prefab"] = new[] { ".ventity", ".vprefab" },
            ["audio"] = new[] { ".wav", ".mp3", ".ogg", ".flac", ".vsndc" },
            ["script"] = new[] { ".cs" },
            ["scene"] = new[] { ".vscene" },
            ["shader"] = new[] { ".hlsl", ".metal", ".glsl", ".vshader" },
            ["animation"] = new[] { ".vanim" },
            ["vfx"] = new[] { ".vfx" },
            ["ui"] = new[] { ".vui" },
        };

        // ================================================================== project files

        [McpServerTool(Name = "list_assets", ReadOnly = true, Idempotent = true)]
        [Description("Files in the project's Assets folder by kind: model, texture, material, prefab, audio, script, scene, shader, animation, vfx, ui " +
                     "(omit for all). Paths are what the other tools take (e.g. place_asset, set_material_properties).")]
        public static object ListAssets(
            [Description("Kind of asset")] string kind = null,
            [Description("Only paths containing this text")] string filter = null,
            [Description("Only below this folder, e.g. Assets/Models")] string folder = null,
            [Description("Maximum results")] int limit = 100)
        {
            string[] exts = null;
            if (!string.IsNullOrEmpty(kind) && !Kinds.TryGetValue(kind.Trim().TrimEnd('s'), out exts))
                throw new ToolError("kind must be one of: " + string.Join(", ", Kinds.Keys) + ".");
            string root = folder != null ? Path.GetDirectoryName(ProjectFiles.Resolve(folder.TrimEnd('/') + "/x")) : ProjectFiles.AssetsDir;
            if (!Directory.Exists(root)) throw new ToolError("No folder " + folder + " in the project.");
            var all = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(AssetDatabase.MetaFileExtension, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith("."))
                .Where(f => exts == null ? KindOf(f) != null : exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(ProjectFiles.Rel)
                .Where(r => string.IsNullOrEmpty(filter) || r.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
            limit = Math.Clamp(limit, 1, 1000);
            return new
            {
                count = all.Count,
                assets = all.Take(limit).Select(r => kind != null ? (object)r : new { path = r, kind = KindOf(r) }).ToArray(),
                truncated = all.Count > limit ? (bool?)true : null,
            };
        }

        private static string KindOf(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            foreach (var kv in Kinds) if (kv.Value.Contains(ext)) return kv.Key;
            return null;
        }

        // ================================================================== place / prefab

        [McpServerTool(Name = "place_asset", Destructive = false)]
        [Description("Places a project asset in the active scene: an imported model (.glb/.gltf/.fbx/.obj …), a prefab (.ventity — a linked " +
                     "instance) or a primitive (\"Primitive:Cube\"). position is in world space (default: in front of the editor camera). One undo step.")]
        public static object PlaceAsset(
            [Description("Project path of the model / prefab, or Primitive:Cube|Sphere|Cylinder|Plane|Capsule|Quad")] string asset,
            [Description("World position [x, y, z]")] float[] position = null,
            [Description("Rotation [x, y, z] in degrees")] float[] rotation = null,
            [Description("Scale [x, y, z] (or [s])")] float[] scale = null,
            [Description("Parent entity")] string parent = null,
            [Description("Name of the new entity")] string name = null)
        {
            _ = SceneModel.ActiveScene;
            string path = asset != null && asset.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) ? asset : ProjectFiles.Resolve(asset, mustExist: true);
            if (!AssetActions.CanAddToScene(path)) throw new ToolError(asset + " cannot be placed — use a model, a .ventity prefab or a primitive.");
            var p = parent != null ? SceneModel.Resolve(parent) : null;
            var e = AssetActions.AddToScene(path, SceneModel.Vec(position, "position"), p) ?? throw new ToolError("Could not place " + asset + " (see read_console).");
            if (!string.IsNullOrWhiteSpace(name)) SceneModel.Set(e, "Name", () => e.Name, v => e.Name = v, name.Trim());
            SceneModel.SetTransform(e, null, SceneModel.Vec(rotation, "rotation"), SceneModel.Vec(scale, "scale"));
            ToolContext.UndoLabel = "place " + e.Name;
            Bounds.Of(new[] { e }, out var min, out var max);
            return new
            {
                entity = SceneModel.Brief(e),
                bounds = new { min = ToolJson.V(min), max = ToolJson.V(max), size = ToolJson.V(new Vector3(max.X - min.X, max.Y - min.Y, max.Z - min.Z)) },
            };
        }

        [McpServerTool(Name = "create_prefab"), NoDryRun]
        [Description("Saves an entity (with its children) as a prefab, Assets/Prefabs/<name>.ventity; the entity becomes a linked instance. " +
                     "place_asset puts more instances in the scene.")]
        public static object CreatePrefab(
            [Description("Entity id, path or name")] string entity,
            [Description("Prefab name (default: the entity's name)")] string name = null,
            [Description("Replace an existing prefab of that name")] bool overwrite = false)
        {
            var e = SceneModel.Resolve(entity);
            string n = string.IsNullOrWhiteSpace(name) ? e.Name : name.Trim();
            string target = Path.Combine(ProjectFiles.AssetsDir, "Prefabs", n + ".ventity");
            if (File.Exists(target) && !overwrite) throw new ToolError(ProjectFiles.Rel(target) + " exists — pass overwrite: true to replace it (it updates every instance), or pick another name.");
            string path = PrefabWorkflow.SaveAsPrefab(e, n) ?? throw new ToolError("Could not save the prefab (see read_console).");
            return new { prefab = ProjectFiles.Rel(path), instance = SceneModel.ShortId(e) };
        }

        // ================================================================== asset library (PC-wide)

        [McpServerTool(Name = "search_library", ReadOnly = true, Idempotent = true)]
        [Description("Searches the user's PC-wide asset library (everything downloaded or imported in any project): name/tag text, type " +
                     "(model, texture, material, audio, …), tags. Returns entry ids for add_library_asset, with source and license.")]
        public static object SearchLibrary(
            [Description("Search text")] string query = null,
            [Description("Asset type: model, texture, material, prefab, audio, script, scene, shader, animation")] string type = null,
            [Description("All of these tags")] string[] tags = null,
            [Description("Maximum results")] int limit = 30)
        {
            var lib = GlobalAssetDatabase.Instance;
            if (!lib.EnsureOpen()) throw new ToolError("The asset library is not available: " + lib.LastError);
            var q = new LibraryQuery { Search = query, Limit = Math.Clamp(limit, 1, 200), Tags = tags?.ToList() };
            if (!string.IsNullOrEmpty(type)) q.Types = new List<AssetType> { LibraryType(type) };
            var hits = lib.Query(q);
            return new
            {
                total = lib.Count(new LibraryQuery { Search = query, Types = q.Types, Tags = q.Tags }),
                entries = hits.Select(e => new
                {
                    id = e.Id,
                    name = e.Name,
                    type = e.Type.ToString(),
                    file = e.FileName,
                    size_kb = e.Size / 1024,
                    source = e.SourceName,
                    author = e.Author,
                    license = e.License,
                    duration_s = e.Duration.HasValue ? (double?)Math.Round(e.Duration.Value, 2) : null,
                    tags = e.Tags.Count > 0 ? e.Tags.ToArray() : null,
                }).ToArray(),
            };
        }

        private static AssetType LibraryType(string t)
        {
            switch (t.Trim().ToLowerInvariant().TrimEnd('s'))
            {
                case "model": case "mesh": return AssetType.Mesh;
                case "texture": case "image": return AssetType.Texture;
                case "material": return AssetType.Material;
                case "prefab": return AssetType.Prefab;
                case "audio": case "sound": return AssetType.Audio;
                case "script": return AssetType.Script;
                case "scene": return AssetType.Scene;
                case "shader": return AssetType.Shader;
                case "animation": return AssetType.Animation;
                default: throw new ToolError("type must be one of: model, texture, material, prefab, audio, script, scene, shader, animation.");
            }
        }

        [McpServerTool(Name = "add_library_asset", Destructive = false), NoDryRun]
        [Description("Copies an asset library entry (search_library id) into the open project (a model brings its textures; the default folder " +
                     "per type) and optionally places a model/prefab in the scene. Content the project already has is reused, not copied twice.")]
        public static async Task<object> AddLibraryAsset(
            [Description("Library entry id from search_library")] long id,
            [Description("Target folder relative to the project (default: by type)")] string folder = null,
            [Description("Place a model or prefab in the active scene")] bool place = false,
            [Description("World position for place [x, y, z]")] float[] position = null)
        {
            var project = SceneModel.Project;
            var lib = GlobalAssetDatabase.Instance;
            if (!lib.EnsureOpen()) throw new ToolError("The asset library is not available: " + lib.LastError);
            string target = folder != null ? Path.GetDirectoryName(ProjectFiles.Resolve(folder.TrimEnd('/') + "/x")) : null;
            var r = await Task.Run(() => LibraryProjects.AddToProject(lib, id, project.Path, target, false, project.Name));
            if (r.Status == AddToProjectStatus.Failed) throw new ToolError(r.Error ?? "The entry could not be added.");
            try { AssetDatabase.Instance.Refresh(); } catch { }
            try { EditorCommands.Window?.FileTree?.Reload(); } catch { }
            object placed = null;
            if (place)
            {
                if (!AssetActions.CanAddToScene(r.Path)) throw new ToolError(Path.GetFileName(r.Path) + " was added but cannot be placed (not a model or prefab).");
                var e = AssetActions.AddToScene(r.Path, SceneModel.Vec(position, "position")) ?? throw new ToolError("Added, but placing it failed (see read_console).");
                placed = SceneModel.Brief(e);
                ToolContext.UndoLabel = "place " + e.Name;
            }
            return new
            {
                path = ProjectFiles.Rel(r.Path),
                status = r.Status == AddToProjectStatus.AlreadyInProject ? "already in the project" : "added",
                files = r.Files.Count > 1 ? (int?)r.Files.Count : null,
                placed,
            };
        }

        // ================================================================== asset store (internet)

        private static readonly ConcurrentDictionary<string, StoreItem> Seen = new ConcurrentDictionary<string, StoreItem>();

        [McpServerTool(Name = "search_store", ReadOnly = true), OpenWorld]
        [Description("Searches a free asset source of the Asset Store: polyhaven (CC0 models, materials, HDRIs), ambientcg (CC0 materials), " +
                     "kenney (CC0 packs), polypizza (low-poly models), freesound (sounds), sketchfab (models). Results carry their license — " +
                     "credit authors where the license asks for attribution (CC-BY). Non-commercial and no-derivatives licenses are filtered out.")]
        public static async Task<object> SearchStore(
            [Description("polyhaven, ambientcg, kenney, polypizza, freesound or sketchfab")] string provider,
            [Description("Search text")] string query,
            [Description("model, material, hdri, texture, sound, animation or pack")] string kind = null,
            [Description("Results page (0 = first)")] int page = 0,
            [Description("Results per page")] int limit = 20,
            CancellationToken ct = default)
        {
            var p = Provider(provider);
            var q = new StoreQuery { Text = query, Page = Math.Max(0, page), PageSize = Math.Clamp(limit, 1, 50) };
            if (!string.IsNullOrEmpty(kind))
            {
                if (!Enum.TryParse<StoreKind>(kind, true, out var k)) throw new ToolError("kind must be one of: " + string.Join(", ", Enum.GetNames(typeof(StoreKind)).Select(x => x.ToLowerInvariant())) + ".");
                q.Kind = k;
            }
            var res = await p.SearchAsync(q, ct);
            if (res.NeedsKey) throw new ToolError(p.Name + " needs the user's own " + p.KeyName + " — ask them to add it in the Asset Store tab (API Keys…). Keys are never shared.");
            if (!string.IsNullOrEmpty(res.Error)) throw new ToolError(p.Name + ": " + res.Error);
            foreach (var it in res.Items) Seen[it.Key] = it;
            return new
            {
                total = res.Total >= 0 ? (int?)res.Total : null,
                more = res.HasMore,
                items = res.Items.Select(it => new
                {
                    id = it.Id,
                    name = it.Name,
                    kind = it.Kind.ToString().ToLowerInvariant(),
                    author = it.Author,
                    license = it.License?.Badge,
                    attribution_required = it.License?.Attribution == true ? (bool?)true : null,
                    duration_s = it.Duration.HasValue ? (double?)Math.Round(it.Duration.Value, 1) : null,
                    page = it.PageUrl,
                }).ToArray(),
            };
        }

        [McpServerTool(Name = "download_store_asset", Destructive = false), OpenWorld]
        [Description("Downloads a search_store result into the asset library (hash-checked, license recorded) and, by default, into the open " +
                     "project. Waits up to wait_seconds; a longer download keeps running — poll get_download_status with the job id.")]
        public static async Task<object> DownloadStoreAsset(
            [Description("Provider id, as in search_store")] string provider,
            [Description("Item id from search_store")] string id,
            [Description("Variant id (e.g. 2k, 4k; default: the source's default)")] string variant = null,
            [Description("Also copy it into the open project")] bool add_to_project = true,
            [Description("Seconds to wait for the download")] int wait_seconds = 30,
            CancellationToken ct = default)
        {
            var p = Provider(provider);
            var project = SceneModel.Project;
            if (!Seen.TryGetValue(p.Id + ":" + id, out var item))
            {
                var res = await p.SearchAsync(new StoreQuery { Text = id, PageSize = 40 }, ct);
                item = res.Items.FirstOrDefault(x => x.Id == id) ?? throw new ToolError("No item '" + id + "' at " + p.Name + " — use an id from search_store.");
                Seen[item.Key] = item;
            }
            var det = await p.DetailsAsync(item, ct);
            var v = det.Variants.FirstOrDefault(x => string.Equals(x.Id, variant ?? det.DefaultVariant, StringComparison.OrdinalIgnoreCase))
                    ?? (variant == null ? det.Variants.FirstOrDefault() : null)
                    ?? throw new ToolError("Variant '" + variant + "' does not exist. Variants: " + string.Join(", ", det.Variants.Select(x => x.Id)));
            var job = StoreDownloads.Enqueue(p, item, v, add_to_project, project.Path, project.Name);
            var until = DateTime.UtcNow.AddSeconds(Math.Clamp(wait_seconds, 0, 120));
            while (job.IsActive && DateTime.UtcNow < until && !ct.IsCancellationRequested) await Task.Delay(200, CancellationToken.None);
            if (job.State == StoreJobState.Done) { try { AssetDatabase.Instance.Refresh(); } catch { } }
            return JobInfo(job);
        }

        [McpServerTool(Name = "get_download_status", ReadOnly = true)]
        [Description("State of an Asset Store download (job id from download_store_asset): progress, and when done the files in the project and the license.")]
        public static async Task<object> GetDownloadStatus(
            [Description("Job id")] int job,
            [Description("Seconds to wait for it to finish")] int wait_seconds = 0)
        {
            var j = StoreDownloads.Jobs.FirstOrDefault(x => x.Id == job) ?? throw new ToolError("No download job " + job + ".");
            var until = DateTime.UtcNow.AddSeconds(Math.Clamp(wait_seconds, 0, 120));
            while (j.IsActive && DateTime.UtcNow < until) await Task.Delay(200);
            if (j.State == StoreJobState.Done) { try { AssetDatabase.Instance.Refresh(); } catch { } }
            return JobInfo(j);
        }

        private static object JobInfo(StoreJob j) => new
        {
            job = j.Id,
            state = j.State.ToString(),
            progress = j.IsActive ? (int?)(int)Math.Round(j.Progress * 100) : null,
            message = j.IsActive ? j.Message : null,
            error = j.Error,
            item = j.Item?.Name,
            license = j.Item?.License?.Badge,
            attribution = j.Item?.License?.Attribution == true ? "credit " + (j.Item.Author ?? j.Provider?.Name) + " (" + j.Item.License.Name + ")" : null,
            project_files = j.ProjectPaths.Count > 0 ? j.ProjectPaths.Select(SafeRel).ToArray() : null,
            library_entries = j.Entries.Count > 0 ? j.Entries.Select(e => e.Id).ToArray() : null,
        };

        private static string SafeRel(string full) { try { return ProjectFiles.Rel(full); } catch { return full; } }

        private static IAssetProvider Provider(string id)
        {
            var p = StoreProviders.Get((id ?? "").Trim().ToLowerInvariant().Replace(".", "").Replace(" ", ""));
            return p ?? throw new ToolError("Unknown provider '" + id + "'. Providers: " + string.Join(", ", StoreProviders.Web.Select(x => x.Id)) + ".");
        }
    }
}
