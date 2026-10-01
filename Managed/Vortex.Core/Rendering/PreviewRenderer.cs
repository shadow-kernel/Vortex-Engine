using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Viewport;
using Editor.DllWrapper;

namespace Editor.Core.Services.Rendering
{
    /// <summary>BGRA8 pixels read back from an offscreen preview render (top row first).</summary>
    public sealed class PreviewImage
    {
        public int Width, Height, Stride;
        public byte[] Bgra;
    }

    /// <summary>One mesh of a preview scene: engine mesh + material, a world matrix (16 row-major floats, null = identity)
    /// and an optional bone palette for skinned meshes.</summary>
    public sealed class PreviewItem
    {
        public long Mesh = -1;
        public long Material = -1;
        public float[] World;
        public float[] BonePalette;
        public int BoneCount;
    }

    /// <summary>Orbit camera around the framed content: yaw/pitch in radians, DistScale 1 = content fills the frame.
    /// Focus overrides the framed centre (pan / frame a bone).</summary>
    public struct PreviewCamera
    {
        public float Yaw, Pitch, DistScale;
        public float[] Focus;
        public float FovDeg;
        public static PreviewCamera Default => new PreviewCamera { Yaw = 0.74f, Pitch = 0.42f, DistScale = 1f, FovDeg = 35f };
    }

    /// <summary>What to draw in a preview: meshes, an optional callback that submits gizmo lines/wireframes (drawn
    /// on top when RenderGizmos is set), a bounds scale for content whose palette scales it (cm rigs) and the
    /// studio-lighting switch.</summary>
    public sealed class PreviewScene
    {
        public readonly List<PreviewItem> Items = new List<PreviewItem>();
        public Action SubmitGizmos;
        public bool RenderGizmos;
        public float BoundsScale = 1f;
        public bool StudioLights = true;
        /// <summary>Render even without mesh items (a VFX preview: particles only). Frame it with <see cref="Bounds"/>.</summary>
        public bool AllowEmpty;
        /// <summary>Called right before the offscreen render (after the meshes are queued): bind a particle preview
        /// world here (ParticleService.Preview.BindForNextRender) so this target draws its particles.</summary>
        public Action BeforeTargetRender;
        /// <summary>Optional explicit framing (centre xyz + radius); null = derived from the mesh bounds.</summary>
        public float[] Bounds;
    }

    /// <summary>
    /// Framework-neutral offscreen preview renderer for every editor window on every platform (the macOS port of the
    /// WPF AssetPreviewRenderer): submits ONLY the preview content with neutral studio lighting into an engine
    /// secondary render target, reads it back as BGRA pixels and asks the main viewport to re-submit its scene
    /// afterwards (the preview swaps the shared render queue). Must run on the UI thread, like every render call.
    /// </summary>
    public static class PreviewRenderer
    {
        private static uint _rt;
        private static int _rtW, _rtH;

        private static uint AcquireTarget(int w, int h)
        {
            if (_rt != 0 && _rtW == w && _rtH == h) return _rt;
            if (_rt != 0) { try { VortexAPI.DestroySecondaryRenderTarget(_rt); } catch { } _rt = 0; }
            _rt = VortexAPI.CreateSecondaryRenderTarget((uint)w, (uint)h);
            _rtW = w; _rtH = h;
            return _rt;
        }

        /// <summary>Release the cached offscreen target (editor shutdown).</summary>
        public static void Shutdown()
        {
            if (_rt != 0) { try { VortexAPI.DestroySecondaryRenderTarget(_rt); } catch { } _rt = 0; _rtW = _rtH = 0; }
        }

        /// <summary>Render a preview scene into a w×h image. Returns null when the renderer is not ready.</summary>
        public static PreviewImage Render(PreviewScene scene, int w, int h, PreviewCamera cam)
        {
            if (scene == null || (scene.Items.Count == 0 && !scene.AllowEmpty) || w < 8 || h < 8) return null;
            w = Math.Min(w, 4096); h = Math.Min(h, 4096);
            uint rt = AcquireTarget(w, h);
            if (rt == 0) return null;
            try
            {
                float cx, cy, cz, radius;
                Frame(scene, out cx, out cy, out cz, out radius);

                if (scene.StudioLights)
                {
                    // Bright all-around studio lighting (the WPF look): high ambient, a key light and a light box.
                    VortexAPI.ClearAllLights();
                    VortexAPI.SetAmbientLightStrength(0.85f);
                    VortexAPI.SetDirectionalLightParams(-0.4f, -0.55f, -0.6f, 1f, 1f, 0.98f, 3.5f);
                    float ld = radius * 3f, lrange = radius * 60f, li = 4.5f;
                    try
                    {
                        VortexAPI.SubmitPointLight(cx + ld, cy, cz, 1f, 1f, 1f, li, lrange);
                        VortexAPI.SubmitPointLight(cx - ld, cy, cz, 1f, 1f, 1f, li, lrange);
                        VortexAPI.SubmitPointLight(cx, cy + ld, cz, 1f, 1f, 1f, li, lrange);
                        VortexAPI.SubmitPointLight(cx, cy - ld, cz, 1f, 1f, 1f, li, lrange);
                        VortexAPI.SubmitPointLight(cx, cy, cz + ld, 1f, 1f, 1f, li, lrange);
                        VortexAPI.SubmitPointLight(cx, cy, cz - ld, 1f, 1f, 1f, li, lrange);
                    }
                    catch { }
                }

                if (cam.Focus != null && cam.Focus.Length >= 3) { cx = cam.Focus[0]; cy = cam.Focus[1]; cz = cam.Focus[2]; }
                float fov = cam.FovDeg > 1f ? cam.FovDeg : 35f;
                float fovHalf = fov * 0.5f * (float)Math.PI / 180f;
                float aspect = (float)w / h;
                float fit = aspect < 1f ? aspect : 1f;   // portrait: fit the horizontal extent
                float dist = radius / (0.92f * (float)Math.Tan(fovHalf) * fit);
                float pitch = Math.Max(-1.5f, Math.Min(1.5f, cam.Pitch));
                float ds = Math.Max(0.02f, Math.Min(12f, cam.DistScale <= 0f ? 1f : cam.DistScale));
                float d = dist * ds;
                float px = cx + d * (float)(Math.Cos(pitch) * Math.Sin(cam.Yaw));
                float py = cy + d * (float)Math.Sin(pitch);
                float pz = cz + d * (float)(Math.Cos(pitch) * Math.Cos(cam.Yaw));
                if (scene.StudioLights)
                    try { VortexAPI.SubmitPointLight(px, py, pz, 1f, 0.98f, 0.95f, 5f, Math.Max(radius, 0.001f) * 22f); } catch { }
                var desc = VortexAPI.ViewportCameraDesc.CreatePerspective(px, py, pz, cx, cy, cz, 0, 1, 0, fov,
                    Math.Max(0.005f, d * 0.01f), d * 4f + 50f);

                float[] idm = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                foreach (var it in scene.Items)
                {
                    if (it == null || it.Mesh < 0) continue;
                    var world = it.World ?? idm;
                    if (it.BonePalette != null && it.BoneCount > 0 && VortexAPI.MeshIsSkinned(it.Mesh))
                        VortexAPI.SubmitSkinnedMesh(it.Mesh, it.Material, world, it.BonePalette, it.BoneCount);
                    else
                        VortexAPI.SubmitMeshForRendering(it.Mesh, it.Material, world);
                }
                if (scene.SubmitGizmos != null) { try { scene.SubmitGizmos(); } catch { } }

                VortexAPI.SwapRenderQueue();
                if (scene.BeforeTargetRender != null) { try { scene.BeforeTargetRender(); } catch { } }
                VortexAPI.RenderToSecondaryTarget(rt, desc, false, scene.RenderGizmos);
                if (!VortexAPI.PrepareSecondaryRenderTargetReadback(rt)) return null;
                return ReadBack(rt);
            }
            catch { return null; }
            finally
            {
                // The preview used the SHARED render queue: the main viewport must re-submit its scene next frame.
                try { EditorViewportSession.RequestResubmit(); } catch { }
                try { SceneRenderService.RuntimeDirty = true; } catch { }
            }
        }

        /// <summary>Centre (x, y, z) and radius the camera frames for this content — callers use it to pan in world units.</summary>
        public static float[] ComputeFrame(PreviewScene scene)
        {
            if (scene == null) return new float[] { 0, 0, 0, 0.5f };
            Frame(scene, out float cx, out float cy, out float cz, out float r);
            return new[] { cx, cy, cz, r };
        }

        private static void Frame(PreviewScene scene, out float cx, out float cy, out float cz, out float radius)
        {
            if (scene.Bounds != null && scene.Bounds.Length >= 4)
            {
                cx = scene.Bounds[0]; cy = scene.Bounds[1]; cz = scene.Bounds[2]; radius = Math.Max(0.01f, scene.Bounds[3]);
                return;
            }
            Vector3 mn = new Vector3(float.MaxValue), mx = new Vector3(float.MinValue);
            bool any = false;
            foreach (var it in scene.Items)
            {
                if (it == null || it.Mesh < 0) continue;
                if (!VortexAPI.GetMeshBounds(it.Mesh, out float sx, out float sy, out float sz)) continue;
                VortexAPI.GetMeshBoundsCenter(it.Mesh, out float bx, out float by, out float bz);
                var he = new Vector3(sx, sy, sz) * 0.5f;
                var c = new Vector3(bx, by, bz);
                // transform the 8 corners of the mesh box by the item's world matrix (row-major, row vectors)
                Matrix4x4 m = it.World != null && it.World.Length >= 16 ? ToMatrix(it.World) : Matrix4x4.Identity;
                for (int i = 0; i < 8; i++)
                {
                    var p = c + new Vector3((i & 1) != 0 ? he.X : -he.X, (i & 2) != 0 ? he.Y : -he.Y, (i & 4) != 0 ? he.Z : -he.Z);
                    var wp = Vector3.Transform(p, m);
                    mn = Vector3.Min(mn, wp); mx = Vector3.Max(mx, wp);
                }
                any = true;
            }
            if (!any) { cx = cy = cz = 0; radius = 0.5f; return; }
            var center = (mn + mx) * 0.5f;
            float r = (mx - mn).Length() * 0.5f;
            float s = scene.BoundsScale > 0f ? scene.BoundsScale : 1f;
            cx = center.X * s; cy = center.Y * s; cz = center.Z * s; radius = Math.Max(0.02f, r * s);
        }

        private static Matrix4x4 ToMatrix(float[] f) => new Matrix4x4(
            f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);

        private static PreviewImage ReadBack(uint rt)
        {
            IntPtr src = VortexAPI.ReadSecondaryRenderTargetPixels(rt, out uint w, out uint h, out uint pitch);
            if (src == IntPtr.Zero || w == 0 || h == 0) { VortexAPI.ReleaseSecondaryRenderTargetPixels(rt); return null; }
            try
            {
                var img = new PreviewImage { Width = (int)w, Height = (int)h, Stride = (int)w * 4, Bgra = new byte[(int)w * (int)h * 4] };
                for (int y = 0; y < (int)h; y++)
                    Marshal.Copy(IntPtr.Add(src, y * (int)pitch), img.Bgra, y * img.Stride, img.Stride);
                return img;
            }
            finally { VortexAPI.ReleaseSecondaryRenderTargetPixels(rt); }
        }

        // ------------------------------------------------------------------ convenience builders

        /// <summary>A sphere with the given material (the canonical material preview).</summary>
        public static PreviewImage RenderMaterialSphere(long material, int w, int h, PreviewCamera cam)
        {
            long sphere = VortexAPI.CreateSphereMesh(0.62f);
            if (sphere < 0) return null;
            try
            {
                var s = new PreviewScene();
                s.Items.Add(new PreviewItem { Mesh = sphere, Material = material });
                return Render(s, w, h, cam);
            }
            finally { try { VortexAPI.DeleteMesh(sphere); } catch { } }
        }

        /// <summary>Render a .vmat on a sphere (builds a private engine material, released afterwards).</summary>
        public static PreviewImage RenderMaterialFile(string vmatPath, int size)
        {
            long mat = -1;
            try
            {
                var vm = Editor.Core.Assets.VortexMaterial.Load(vmatPath);
                if (vm == null) return null;
                vm.ResolvePathsAbsolute(Path.GetDirectoryName(vmatPath));   // texture paths in a .vmat are relative to it
                mat = MaterialService.Instance.BuildEngineMaterial(vm);
                return RenderMaterialSphere(mat, size, size, PreviewCamera.Default);
            }
            catch { return null; }
            finally { if (mat >= 0) { try { VortexAPI.DeleteMaterial(mat); } catch { } } }
        }

        /// <summary>Import a model file (all submeshes + their materials, sidecar .vmat overrides applied) and render it.</summary>
        public static PreviewImage RenderModelFile(string fullPath, int size)
        {
            using (var m = PreviewModel.Load(fullPath))
            {
                if (m == null) return null;
                return Render(m.Scene, size, size, PreviewCamera.Default);
            }
        }

        /// <summary>Render what a prefab (.ventity) spawns: every MeshRenderer of the template with its local transforms.</summary>
        public static PreviewImage RenderPrefabFile(string ventityPath, string projectRoot, int size)
        {
            using (var m = PreviewModel.LoadPrefab(ventityPath, projectRoot))
            {
                if (m == null) return null;
                return Render(m.Scene, size, size, PreviewCamera.Default);
            }
        }

        /// <summary>A primitive (Primitive:Cube / Sphere / Plane / Cylinder / Cone) with the default material.</summary>
        public static PreviewImage RenderPrimitive(string primitive, int size)
        {
            long mesh = PreviewModel.CreatePrimitive(primitive);
            if (mesh < 0) return null;
            try
            {
                var s = new PreviewScene();
                s.Items.Add(new PreviewItem { Mesh = mesh, Material = -1 });
                return Render(s, size, size, PreviewCamera.Default);
            }
            finally { try { VortexAPI.DeleteMesh(mesh); } catch { } }
        }
    }

    /// <summary>
    /// Engine resources for an interactive preview (load once, render every frame while orbiting): a model file's
    /// submeshes, or everything a prefab template draws. Dispose releases every mesh/material it created.
    /// </summary>
    public sealed class PreviewModel : IDisposable
    {
        public readonly PreviewScene Scene = new PreviewScene();
        private readonly List<long> _meshes = new List<long>();
        private readonly List<long> _materials = new List<long>();
        private readonly List<string> _itemModels = new List<string>();   // model file behind each scene item (null = primitive)
        public string SourcePath { get; private set; }

        public static PreviewModel Load(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;
            try
            {
                var subs = VortexAPI.ImportModelWithMaterialsFromFile(fullPath);
                if (subs == null || subs.Length == 0) return null;
                var pm = new PreviewModel { SourcePath = fullPath };
                var mats = new long[subs.Length];
                for (int i = 0; i < subs.Length; i++) mats[i] = subs[i].MaterialId;
                try { MaterialService.Instance.ApplyModelSidecarVmats(mats, fullPath); } catch { }
                for (int i = 0; i < subs.Length; i++)
                {
                    pm._meshes.Add(subs[i].MeshId);
                    if (mats[i] >= 0) pm._materials.Add(mats[i]);
                    pm.Scene.Items.Add(new PreviewItem { Mesh = subs[i].MeshId, Material = mats[i] });
                }
                // rigged models: draw the scene's bind pose (without a palette they lie flat, in cm, shaded black)
                try { PreviewSkinning.Apply(pm.Scene, fullPath); } catch { }
                return pm;
            }
            catch { return null; }
        }

        /// <summary>A sphere wearing a .vmat (owned material) — the material editor / big material preview.</summary>
        public static PreviewModel MaterialSphere(string vmatPath)
        {
            try
            {
                var vm = Editor.Core.Assets.VortexMaterial.Load(vmatPath);
                if (vm == null) return null;
                vm.ResolvePathsAbsolute(Path.GetDirectoryName(vmatPath));
                var pm = new PreviewModel { SourcePath = vmatPath };
                long mat = MaterialService.Instance.BuildEngineMaterial(vm);
                long sphere = VortexAPI.CreateSphereMesh(0.62f);
                if (sphere < 0) { if (mat >= 0) VortexAPI.DeleteMaterial(mat); return null; }
                pm._meshes.Add(sphere); if (mat >= 0) pm._materials.Add(mat);
                pm.Scene.Items.Add(new PreviewItem { Mesh = sphere, Material = mat });
                return pm;
            }
            catch { return null; }
        }

        /// <summary>Wrap engine meshes/materials the caller created; they are released with this model.</summary>
        public static PreviewModel FromOwned(long[] meshes, long[] materials)
        {
            var pm = new PreviewModel();
            for (int i = 0; meshes != null && i < meshes.Length; i++)
            {
                long mat = materials != null && i < materials.Length ? materials[i] : -1;
                pm._meshes.Add(meshes[i]); if (mat >= 0 && !pm._materials.Contains(mat)) pm._materials.Add(mat);
                pm.Scene.Items.Add(new PreviewItem { Mesh = meshes[i], Material = mat });
            }
            return pm;
        }

        /// <summary>Build the preview of a prefab template (JSON read without instantiating anything in the scene).</summary>
        public static PreviewModel LoadPrefab(string ventityPath, string projectRoot)
        {
            if (string.IsNullOrEmpty(ventityPath) || !File.Exists(ventityPath)) return null;
            try
            {
                var text = File.ReadAllText(ventityPath);
                using (var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
                {
                    var pm = new PreviewModel { SourcePath = ventityPath };
                    var modelCache = new Dictionary<string, VortexAPI.SubmeshImportData[]>(StringComparer.OrdinalIgnoreCase);
                    pm.AddEntity(doc.RootElement, Matrix4x4.Identity, projectRoot ?? Path.GetDirectoryName(ventityPath), modelCache, true);
                    if (pm.Scene.Items.Count == 0) { pm.Dispose(); return null; }
                    try { PreviewSkinning.Apply(pm.Scene, pm._itemModels); } catch { }
                    return pm;
                }
            }
            catch { return null; }
        }

        private void AddEntity(JsonElement e, Matrix4x4 parentWorld, string root, Dictionary<string, VortexAPI.SubmeshImportData[]> cache, bool isRoot)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("isActive", out var act) && act.ValueKind == JsonValueKind.False) return;
            Matrix4x4 local = Matrix4x4.Identity;
            JsonElement comps;
            if (e.TryGetProperty("components", out comps) && comps.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in comps.EnumerateArray())
                    if (Str(c, "__type") == "Transform")
                    {
                        var p = V3(c, "localPosition", 0f); var r = V3(c, "localRotation", 0f); var s = V3(c, "localScale", 1f);
                        if (isRoot) p = Vector3.Zero;   // the template origin is where it spawns
                        local = Matrix4x4.CreateScale(s) * BoneSocketService.EulerZXY(r) * Matrix4x4.CreateTranslation(p);
                    }
            }
            var world = local * parentWorld;
            if (comps.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in comps.EnumerateArray())
                {
                    if (Str(c, "__type") != "MeshRenderer") continue;
                    if (c.TryGetProperty("isEnabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
                    string meshPath = Str(c, "meshPath"), matPath = Str(c, "materialPath");
                    if (string.IsNullOrEmpty(meshPath)) continue;
                    AddMesh(meshPath, matPath, world, root, cache);
                }
            }
            if (e.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                foreach (var k in kids.EnumerateArray()) AddEntity(k, world, root, cache, false);
        }

        private void AddMesh(string meshPath, string matPath, Matrix4x4 world, string root, Dictionary<string, VortexAPI.SubmeshImportData[]> cache)
        {
            float[] w = {
                world.M11, world.M12, world.M13, world.M14, world.M21, world.M22, world.M23, world.M24,
                world.M31, world.M32, world.M33, world.M34, world.M41, world.M42, world.M43, world.M44 };
            long material = -1;
            if (!string.IsNullOrEmpty(matPath))
            {
                string full = Resolve(matPath, root);
                try
                {
                    var vm = Editor.Core.Assets.VortexMaterial.Load(full);
                    if (vm != null)
                    {
                        vm.ResolvePathsAbsolute(Path.GetDirectoryName(full));
                        material = MaterialService.Instance.BuildEngineMaterial(vm);
                        if (material >= 0) _materials.Add(material);
                    }
                }
                catch { }
            }
            if (meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                long mesh = CreatePrimitive(meshPath);
                if (mesh < 0) return;
                _meshes.Add(mesh);
                Scene.Items.Add(new PreviewItem { Mesh = mesh, Material = material, World = w });
                _itemModels.Add(null);
                return;
            }
            string file = meshPath; int sub = -1;
            int hash = meshPath.LastIndexOf("#submesh", StringComparison.OrdinalIgnoreCase);
            if (hash > 0 && int.TryParse(meshPath.Substring(hash + 8), out int n)) { file = meshPath.Substring(0, hash); sub = n; }
            string fullModel = Resolve(file, root);
            if (!cache.TryGetValue(fullModel, out var subs))
            {
                subs = File.Exists(fullModel) ? VortexAPI.ImportModelWithMaterialsFromFile(fullModel) : null;
                if (subs != null)
                {
                    var mats = new long[subs.Length];
                    for (int i = 0; i < subs.Length; i++) mats[i] = subs[i].MaterialId;
                    try { MaterialService.Instance.ApplyModelSidecarVmats(mats, fullModel); } catch { }
                    for (int i = 0; i < subs.Length; i++) { subs[i].MaterialId = mats[i]; _meshes.Add(subs[i].MeshId); if (mats[i] >= 0) _materials.Add(mats[i]); }
                }
                cache[fullModel] = subs;
            }
            if (subs == null) return;
            for (int i = 0; i < subs.Length; i++)
            {
                if (sub >= 0 && i != sub) continue;
                Scene.Items.Add(new PreviewItem { Mesh = subs[i].MeshId, Material = material >= 0 ? material : subs[i].MaterialId, World = w });
                _itemModels.Add(fullModel);
            }
        }

        /// <summary>Create an engine primitive mesh from "Primitive:Cube" / "Cube" etc. (same sizes as the scene renderer).</summary>
        public static long CreatePrimitive(string name)
        {
            string p = name ?? "";
            if (p.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) p = p.Substring(10);
            switch (p.ToLowerInvariant())
            {
                case "cube": return VortexAPI.CreateCubeMesh(1f);
                case "sphere": case "torus": return VortexAPI.CreateSphereMesh(0.5f);
                case "plane": case "quad": return VortexAPI.CreatePlaneMesh(1f, 1f);
                case "cylinder": case "capsule": return VortexAPI.CreateCylinderMesh(0.5f, 1f);
                case "cone": return VortexAPI.CreateConeMesh(0.5f, 1f);
                default: return -1;
            }
        }

        private static string Resolve(string path, string root)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string p = path.Replace('\\', '/');
            if (Path.IsPathRooted(p)) return p;
            var proj = ProjectData.Current?.Path;
            if (!string.IsNullOrEmpty(proj) && File.Exists(Path.Combine(proj, p))) return Path.Combine(proj, p);
            if (!string.IsNullOrEmpty(root))
            {
                // walk up from the prefab folder until the relative path resolves (prefab in Assets/Prefabs -> project root)
                var dir = root;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    var cand = Path.Combine(dir, p);
                    if (File.Exists(cand)) return cand;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return Path.Combine(proj ?? root ?? "", p);
        }

        private static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static Vector3 V3(JsonElement e, string name, float def)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return new Vector3(def);
            float F(string k) => v.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number ? (float)x.GetDouble() : def;
            return new Vector3(F("x"), F("y"), F("z"));
        }

        public void Dispose()
        {
            foreach (var m in _meshes) { try { VortexAPI.DeleteMesh(m); } catch { } }
            foreach (var m in _materials) { try { VortexAPI.DeleteMaterial(m); } catch { } }
            _meshes.Clear(); _materials.Clear(); Scene.Items.Clear();
        }
    }
}
