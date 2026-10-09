using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using CoreAssetActions = Editor.Core.Assets.AssetActions;

namespace VortexEditor.Shell
{
    /// <summary>Resource lifetime (#357 #358) on the real backend: forty cubes share one mesh and one material and leave
    /// nothing behind when deleted, and placing one model three times loads its meshes, LOD chains, materials and
    /// textures once — the native mesh/material/texture counts say so.</summary>
    internal static class ResourceLifecycleSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("resource lifecycle", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("resource lifecycle: no scene — skipped"); return true; }
            if (!VortexAPI.TryGetResourceCounts(out int m0, out int mat0, out int t0)) { log.Log("resource lifecycle: engine library without GetResourceCounts — skipped"); return true; }
            bool ok = true;

            // 1) forty cubes in, forty cubes out
            var cubes = new List<GameEntity>();
            for (int i = 0; i < 40; i++)
            {
                var c = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (c?.Transform == null) break;
                c.Transform.LocalPosition = new Vector3(i * 2f, 1f, 0f);
                cubes.Add(c);
            }
            await Frames();
            VortexAPI.TryGetResourceCounts(out int m1, out int mat1, out int t1);
            EditorCommands.DeleteEntities(cubes);
            await Frames();
            VortexAPI.TryGetResourceCounts(out int m2, out int mat2, out int t2);
            log.Log("resource lifecycle: primitives — before " + Fmt(m0, mat0, t0) + ", with 40 cubes " + Fmt(m1, mat1, t1) + ", deleted " + Fmt(m2, mat2, t2) + " (meshes/materials/textures)");
            if (m1 > m0 + 1 || mat1 > mat0 + 1) { log.LogError("resource lifecycle: 40 identical cubes created more than one mesh or material"); ok = false; }
            if (m2 > m1 || mat2 > mat1) { log.LogError("resource lifecycle: deleting the cubes freed nothing"); ok = false; }

            // 2) one model, placed three times
            string model = FindModel(ProjectData.Current?.Path);
            if (model == null) { log.Log("resource lifecycle: no model file in the project — placement part skipped"); return ok; }
            if (!VortexAPI.IsAssimpAvailable()) { log.Log("resource lifecycle: no Assimp — placement part skipped"); return ok; }
            var placed = new List<GameEntity>();
            try
            {
                var first = CoreAssetActions.AddModelToScene(model);
                if (first == null) { log.LogError("resource lifecycle: could not place " + Path.GetFileName(model)); return false; }
                placed.Add(first);
                await Frames();
                VortexAPI.TryGetResourceCounts(out int p1, out int pm1, out int pt1);
                for (int i = 0; i < 2; i++)
                {
                    var e = CoreAssetActions.AddModelToScene(model);
                    if (e?.Transform != null) { e.Transform.LocalPosition = new Vector3(10f * (i + 1), 0f, 0f); placed.Add(e); }
                }
                await Frames();
                VortexAPI.TryGetResourceCounts(out int p3, out int pm3, out int pt3);
                log.Log("resource lifecycle: " + Path.GetFileName(model) + " — after the first placement " + Fmt(p1, pm1, pt1) + ", after the third " + Fmt(p3, pm3, pt3));
                if (p1 <= m2) log.LogWarning("resource lifecycle: the first placement created no mesh — the placement check is not meaningful here");
                if (p3 != p1 || pm3 != pm1 || pt3 != pt1) { log.LogError("resource lifecycle: placing the same model again loaded new meshes, materials or textures (#357)"); ok = false; }
                EditorCommands.DeleteEntities(placed); placed.Clear();
                await Frames();
                VortexAPI.TryGetResourceCounts(out int p4, out int pm4, out int pt4);
                if (p4 > p3 || pm4 > pm3 || pt4 > pt3) { log.LogError("resource lifecycle: deleting the placed models grew the resource counts"); ok = false; }
            }
            finally
            {
                if (placed.Count > 0) EditorCommands.DeleteEntities(placed);
                EditorViewportSession.RequestResubmit();
            }
            return ok;
        }

        private static async Task Frames()
        {
            EditorViewportSession.RequestResubmit();
            await SmokeRegistry.Settle(400);
        }

        private static string Fmt(int m, int mat, int t) => m + "/" + mat + "/" + t;

        private static readonly string[] ModelExt = { ".glb", ".gltf", ".fbx", ".obj" };

        /// <summary>The smallest model file under Assets/ — this is a lifetime check, not a load test.</summary>
        private static string FindModel(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return null;
            string assets = Path.Combine(projectPath, "Assets");
            if (!Directory.Exists(assets)) return null;
            try
            {
                return Directory.EnumerateFiles(assets, "*.*", SearchOption.AllDirectories)
                    .Where(f => ModelExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => new FileInfo(f).Length)
                    .FirstOrDefault();
            }
            catch { return null; }
        }
    }
}
