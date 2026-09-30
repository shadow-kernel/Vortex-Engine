using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;

namespace VortexEditor.Shell.ModelTools
{
    /// <summary>
    /// Skinned meshes in a preview: without a bone palette a rigged model is drawn through the static path — lying in
    /// its raw mesh space (rig root rotation / centimetre scale missing) and shaded black (the skinned vertex layout read
    /// as a static one). This gives every skinned item the model's bind-pose palette — exactly what the scene draws
    /// when no animation plays — and frames the posed content.
    /// </summary>
    internal static class PreviewSkinning
    {
        /// <summary>All items come from one model file.</summary>
        public static int Apply(PreviewScene scene, string modelPath)
            => scene == null ? 0 : Apply(scene, Enumerable.Repeat(modelPath, scene.Items.Count).ToList());

        /// <summary>Item i comes from <paramref name="itemModels"/>[i] (null = static / primitive). Returns the number of skinned items posed.</summary>
        public static int Apply(PreviewScene scene, IList<string> itemModels)
        {
            if (scene == null) return 0;
            int posed = 0;
            var cache = new Dictionary<string, SkeletonDef>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < scene.Items.Count; i++)
            {
                var it = scene.Items[i];
                string model = itemModels != null && i < itemModels.Count ? itemModels[i] : null;
                if (it == null || it.Mesh < 0 || string.IsNullOrEmpty(model)) continue;
                bool skinned = false;
                try { skinned = VortexAPI.MeshIsSkinned(it.Mesh); } catch { }
                if (!skinned) continue;
                if (!cache.TryGetValue(model, out var skel))
                {
                    try { skel = AnimationService.Instance.GetSkeleton(model); } catch { skel = null; }
                    cache[model] = skel;
                }
                if (skel == null || !skel.IsValid) continue;
                it.BonePalette = skel.BindPosePalette();
                it.BoneCount = skel.Bones.Length;
                posed++;
            }
            scene.Bounds = posed > 0 ? PosedBounds(scene) : null;
            return posed;
        }

        /// <summary>Centre + radius of the content as drawn (skinned items through their palette, then their world matrix).</summary>
        public static float[] PosedBounds(PreviewScene scene)
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            bool any = false;
            foreach (var it in scene.Items)
                if (Accumulate(it, ref mn, ref mx)) any = true;
            if (!any) return null;
            var ctr = (mn + mx) * 0.5f;
            return new[] { ctr.X, ctr.Y, ctr.Z, Math.Max(0.02f, (mx - mn).Length() * 0.5f) };
        }

        /// <summary>Axis-aligned size of one item as drawn (null when unknown).</summary>
        public static float[] PosedSize(PreviewItem it)
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            if (!Accumulate(it, ref mn, ref mx)) return null;
            var s = mx - mn;
            return new[] { s.X, s.Y, s.Z };
        }

        private static bool Accumulate(PreviewItem it, ref Vector3 mn, ref Vector3 mx)
        {
            if (it == null || it.Mesh < 0) return false;
            if (!VortexAPI.GetMeshBounds(it.Mesh, out float sx, out float sy, out float sz)) return false;
            VortexAPI.GetMeshBoundsCenter(it.Mesh, out float cx, out float cy, out float cz);
            var he = new Vector3(sx, sy, sz) * 0.5f; var c = new Vector3(cx, cy, cz);
            Matrix4x4 m = Matrix4x4.Identity;
            if (it.BonePalette != null && it.BonePalette.Length >= 16) m = SkeletonDef.ToMatrix(it.BonePalette.Take(16).ToArray());
            if (it.World != null && it.World.Length >= 16) m = m * SkeletonDef.ToMatrix(it.World);
            for (int k = 0; k < 8; k++)
            {
                var p = Vector3.Transform(c + new Vector3((k & 1) != 0 ? he.X : -he.X, (k & 2) != 0 ? he.Y : -he.Y, (k & 4) != 0 ? he.Z : -he.Z), m);
                mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
            }
            return true;
        }

        /// <summary>
        /// The model file behind each item of a prefab preview, in the order <see cref="PreviewModel.LoadPrefab"/> adds
        /// them (depth-first; inactive entities and disabled renderers skipped; a whole-model reference adds every
        /// submesh, "#submeshN" one). Null entries for primitives. Null when the prefab can't be read.
        /// </summary>
        public static List<string> PrefabItemModels(string ventityPath, string projectRoot)
        {
            try
            {
                var list = new List<string>();
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                string root = projectRoot ?? Path.GetDirectoryName(ventityPath);
                using (var doc = JsonDocument.Parse(File.ReadAllText(ventityPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
                    Walk(doc.RootElement, root, Path.GetDirectoryName(ventityPath), list, counts);
                return list;
            }
            catch { return null; }
        }

        private static void Walk(JsonElement e, string projectRoot, string prefabDir, List<string> list, Dictionary<string, int> counts)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("isActive", out var act) && act.ValueKind == JsonValueKind.False) return;
            if (e.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                foreach (var c in comps.EnumerateArray())
                {
                    if (Str(c, "__type") != "MeshRenderer") continue;
                    if (c.TryGetProperty("isEnabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
                    string meshPath = Str(c, "meshPath");
                    if (string.IsNullOrEmpty(meshPath)) continue;
                    if (meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) { if (PreviewModelHasPrimitive(meshPath)) list.Add(null); continue; }
                    string file = meshPath; int sub = -1;
                    int hash = meshPath.LastIndexOf("#submesh", StringComparison.OrdinalIgnoreCase);
                    if (hash > 0 && int.TryParse(meshPath.Substring(hash + 8), out int n)) { file = meshPath.Substring(0, hash); sub = n; }
                    string full = Resolve(file, projectRoot, prefabDir);
                    if (!counts.TryGetValue(full, out int count))
                    {
                        try { count = File.Exists(full) ? VortexAPI.GetSubmeshCount(full) : 0; } catch { count = 0; }
                        counts[full] = count;
                    }
                    for (int i = 0; i < count; i++) if (sub < 0 || i == sub) list.Add(full);
                }
            if (e.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                foreach (var k in kids.EnumerateArray()) Walk(k, projectRoot, prefabDir, list, counts);
        }

        private static bool PreviewModelHasPrimitive(string meshPath)
        {
            var p = meshPath.Substring(10).ToLowerInvariant();
            return p == "cube" || p == "sphere" || p == "torus" || p == "plane" || p == "quad" || p == "cylinder" || p == "capsule" || p == "cone";
        }

        private static string Resolve(string path, string projectRoot, string prefabDir)
        {
            string p = path.Replace('\\', '/');
            if (Path.IsPathRooted(p)) return p;
            var proj = ProjectData.Current?.Path ?? projectRoot;
            if (!string.IsNullOrEmpty(proj) && File.Exists(Path.Combine(proj, p))) return Path.Combine(proj, p);
            var dir = prefabDir;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                var cand = Path.Combine(dir, p);
                if (File.Exists(cand)) return cand;
                dir = Path.GetDirectoryName(dir);
            }
            return Path.Combine(proj ?? projectRoot ?? "", p);
        }

        private static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
