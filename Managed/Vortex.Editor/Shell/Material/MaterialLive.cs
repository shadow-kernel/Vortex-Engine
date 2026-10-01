using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell.Material
{
    /// <summary>
    /// Material ↔ engine glue for the material editor: which custom-shader file this renderer compiles, and pushing a
    /// saved .vmat to everything in the open scene that renders it.
    /// </summary>
    internal static class MaterialLive
    {
        private static readonly Regex SidecarName = new Regex(@"^submesh_(\d+)\.vmat$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The custom-shader file THIS backend compiles for a material's ShaderAsset: the .hlsl on Windows (DX12), the
        /// .metal on macOS (the Metal backend ignores .hlsl) — an assigned X.hlsl uses a sibling X.metal when there is one.
        /// Null = the built-in shader renders.
        /// </summary>
        public static string ResolveShaderFile(string shaderAsset)
        {
            if (string.IsNullOrEmpty(shaderAsset)) return null;
            if (OperatingSystem.IsWindows()) return ShaderAssetService.ResolveShaderHlsl(shaderAsset);
            string full = EditorKit.ToAbsolute(shaderAsset);
            try
            {
                if (full.EndsWith(".metal", StringComparison.OrdinalIgnoreCase)) return File.Exists(full) ? full : null;
                string metal = Path.ChangeExtension(full, ".metal");
                if (File.Exists(metal)) return metal;
                if (full.EndsWith(".vshader", StringComparison.OrdinalIgnoreCase))
                {
                    var vs = VortexShader.Load(full);
                    if (vs != null && !string.IsNullOrEmpty(vs.PixelShaderPath))
                    {
                        string m = Path.ChangeExtension(EditorKit.ToAbsolute(vs.PixelShaderPath), ".metal");
                        if (File.Exists(m)) return m;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>MaterialService binds only .hlsl shader assets; on the Metal backend bind the .metal instead (or clear
        /// the binding so the built-in shader renders). No-op on Windows.</summary>
        public static void BindPlatformShader(long material, string shaderAsset)
        {
            if (material < 0 || OperatingSystem.IsWindows() || string.IsNullOrEmpty(shaderAsset)) return;
            try { VortexAPI.SetMaterialShader((int)material, ResolveShaderFile(shaderAsset) ?? ""); } catch { }
        }

        private static long _sentinelMesh = -1;

        /// <summary>
        /// Make the engine's active render queue stop referencing preview meshes BEFORE they are deleted. The renderer
        /// keeps the last swapped queue (and its recorded draw runs cache raw mesh pointers) until something new is
        /// swapped in; views that render without submitting (split / quad secondary views, a frame with an empty scene)
        /// would otherwise draw a freed mesh. Swaps in a single degenerate item (a tiny cube collapsed onto the origin —
        /// draws nothing) and asks the main viewport to re-submit its scene.
        /// </summary>
        public static void PurgeRenderQueue()
        {
            try
            {
                if (_sentinelMesh < 0) _sentinelMesh = VortexAPI.CreateCubeMesh(0.001f);   // kept for the session
                if (_sentinelMesh >= 0)
                {
                    var collapse = new float[16];
                    collapse[15] = 1f;
                    VortexAPI.SubmitMeshForRendering(_sentinelMesh, -1, collapse);
                    VortexAPI.SwapRenderQueue();
                }
            }
            catch { }
            finally
            {
                try { EditorViewportSession.RequestResubmit(); } catch { }
                SceneRenderService.RuntimeDirty = true;
            }
        }

        /// <summary>Scene entities whose MeshRenderer renders this .vmat.</summary>
        public static int CountUsers(string vmatFullPath)
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene?.Entities == null || string.IsNullOrEmpty(vmatFullPath)) return 0;
            int n = 0;
            void Walk(IEnumerable<GameEntity> list)
            {
                foreach (var e in list)
                {
                    if (e == null) continue;
                    var mr = e.GetComponent<MeshRenderer>();
                    if (mr != null && !string.IsNullOrEmpty(mr.MaterialPath) && EditorKit.SamePath(mr.MaterialPath, vmatFullPath)) n++;
                    if (e.Children != null && e.Children.Count > 0) Walk(e.Children);
                }
            }
            try { Walk(scene.Entities); } catch { }
            return n;
        }

        /// <summary>
        /// Make the open scene render a just-saved .vmat: drop MaterialService's shared engine material (it is rebuilt
        /// from the file), clear the scene renderer's MaterialPath → id cache so every renderer re-resolves it, bind the
        /// platform shader, update imported-model materials that use the file as their submesh sidecar, and redraw.
        /// Returns how many scene objects use the material.
        /// </summary>
        public static int PushToScene(string vmatFullPath, VortexMaterial saved)
        {
            if (string.IsNullOrEmpty(vmatFullPath)) return 0;
            string full;
            try { full = Path.GetFullPath(vmatFullPath); } catch { full = vmatFullPath; }
            try { MaterialService.Instance.InvalidateVortexMaterial(full); } catch { }
            var scene = ProjectData.Current?.ActiveScene;
            try { if (scene != null) SceneRenderService.Instance.PreloadSceneAssets(scene); } catch { }
            int users = CountUsers(full);
            if (users > 0 && saved != null && !string.IsNullOrEmpty(saved.ShaderAsset) && !OperatingSystem.IsWindows())
            {
                // Build the shared material now so the Metal shader is bound before the viewport picks it up.
                try { BindPlatformShader(MaterialService.Instance.GetOrBuildVortexMaterial(full), saved.ShaderAsset); } catch { }
            }
            try { ApplyToModelSidecar(full, saved); } catch { }
            try { EditorViewportSession.RequestResubmit(); } catch { }
            SceneRenderService.RuntimeDirty = true;
            return users;
        }

        /// <summary>Imported models keep one .vmat per submesh next to them (materials/submesh_N.vmat). Scenes that bound
        /// the imported material by mesh path (not by MaterialPath) get the saved values pushed in place.</summary>
        private static void ApplyToModelSidecar(string vmatFull, VortexMaterial saved)
        {
            var dir = Path.GetDirectoryName(vmatFull);
            if (dir == null || !string.Equals(Path.GetFileName(dir), "materials", StringComparison.OrdinalIgnoreCase)) return;
            var m = SidecarName.Match(Path.GetFileName(vmatFull));
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int index)) return;
            string modelDir = Path.GetDirectoryName(dir);
            if (modelDir == null || !Directory.Exists(modelDir)) return;
            foreach (var f in Directory.GetFiles(modelDir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".glb" && ext != ".gltf" && ext != ".fbx" && ext != ".obj" && ext != ".dae" && ext != ".3ds" && ext != ".blend") continue;
                SceneRenderService.ApplyToLiveMaterialsForModel(f, index, id =>
                {
                    MaterialService.Instance.ApplyVmatToMaterial(id, vmatFull, includeTextures: true);
                    if (saved != null) BindPlatformShader(id, saved.ShaderAsset);
                });
            }
        }
    }
}
