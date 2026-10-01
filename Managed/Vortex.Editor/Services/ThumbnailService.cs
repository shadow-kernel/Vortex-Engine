using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services.Rendering;

namespace VortexEditor.Services
{
    /// <summary>
    /// Asset thumbnails for the Asset Browser, pickers and editor slots: image files are decoded on a worker thread,
    /// models / materials / prefabs / primitives are rendered through <see cref="PreviewRenderer"/> on the UI thread
    /// (one per dispatcher slice, so browsing never stutters). Results are kept in memory and on disk under
    /// <c>&lt;project&gt;/.ve/thumbs</c> (keyed by path + modification time + size), so a project opens with its
    /// thumbnails instantly the second time. Call <see cref="Invalidate"/> when an asset changes.
    /// </summary>
    public static class ThumbnailService
    {
        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
        public static readonly string[] ModelExtensions = { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".3ds", ".blend" };

        private sealed class Job { public string Path; public int Size; public List<Action<Bitmap>> Callbacks = new List<Action<Bitmap>>(); }

        private static readonly Dictionary<string, Bitmap> _memory = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Job> _pending = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<Job> _renderQueue = new Queue<Job>();
        private static DispatcherTimer _pump;

        /// <summary>Kind of thumbnail a file gets: Image (decoded), Render (3D preview) or None (type icon only).</summary>
        public enum Kind { None, Image, Render }

        public static Kind KindOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return Kind.None;
            if (path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return Kind.Render;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(ImageExtensions, ext) >= 0) return Kind.Image;
            if (Array.IndexOf(ModelExtensions, ext) >= 0 || ext == ".vmat" || ext == ".ventity" || ext == ".vprefab" || ext == ".vfx") return Kind.Render;
            return Kind.None;
        }

        /// <summary>A thumbnail that is already in memory (null otherwise).</summary>
        public static Bitmap TryGet(string fullPath, int size)
        {
            lock (_memory) return _memory.TryGetValue(Key(fullPath, size), out var b) ? b : null;
        }

        /// <summary>Get the thumbnail asynchronously; <paramref name="ready"/> runs on the UI thread (possibly
        /// immediately when cached). Nothing happens for files without a thumbnail kind.</summary>
        public static void Request(string fullPath, int size, Action<Bitmap> ready)
        {
            var kind = KindOf(fullPath);
            if (kind == Kind.None || ready == null) return;
            string key = Key(fullPath, size);
            Bitmap hit;
            lock (_memory) _memory.TryGetValue(key, out hit);
            if (hit != null) { ready(hit); return; }
            lock (_pending)
            {
                if (_pending.TryGetValue(key, out var existing)) { existing.Callbacks.Add(ready); return; }
                var job = new Job { Path = fullPath, Size = size };
                job.Callbacks.Add(ready);
                _pending[key] = job;
            }
            Task.Run(() => LoadOrQueue(key, fullPath, size, kind));
        }

        /// <summary>Forget a file's thumbnails (memory + disk), e.g. after its .vmat or model changed. Fires
        /// <see cref="Invalidated"/> so visible tiles can re-request.</summary>
        public static void Invalidate(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            lock (_memory)
            {
                var drop = new List<string>();
                foreach (var k in _memory.Keys) if (k.StartsWith(fullPath + "|", StringComparison.OrdinalIgnoreCase)) drop.Add(k);
                foreach (var k in drop) _memory.Remove(k);
            }
            try
            {
                string proj = ProjectData.Current?.Path;
                if (!string.IsNullOrEmpty(proj) && Directory.Exists(ThumbDir(proj)))
                    foreach (var f in Directory.EnumerateFiles(ThumbDir(proj), PathPrefix(fullPath) + "_*.png")) { try { File.Delete(f); } catch { } }
            }
            catch { }
            try { Invalidated?.Invoke(fullPath); } catch { }
        }

        /// <summary>Raised (UI thread or caller thread) after <see cref="Invalidate"/> with the asset path.</summary>
        public static event Action<string> Invalidated;

        public static void ClearMemory() { lock (_memory) _memory.Clear(); }

        private static string Key(string path, int size) => path + "|" + size;

        /// <summary>Disk cache file: &lt;sha1(path)&gt;_&lt;sha1(content stamp|size)&gt;.png — the path prefix lets
        /// <see cref="Invalidate"/> delete every size/version of one asset.</summary>
        private static string DiskPath(string fullPath, int size)
        {
            string proj = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(proj) || fullPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.Combine(ThumbDir(proj), PathPrefix(fullPath) + "_" + Hash(ContentStamp(fullPath) + "|" + size, 12) + ".png");
        }

        private static string ThumbDir(string proj) => Path.Combine(proj, ".ve", "thumbs");
        private static string PathPrefix(string fullPath) => Hash(fullPath.ToLowerInvariant(), 16);

        private static string Hash(string text, int len)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").Substring(0, len);
        }

        /// <summary>Newest modification time of the asset AND what its look depends on: a model's sidecar materials
        /// (materials/*.vmat and *.vmat next to it), a material's textures are covered by the explicit Invalidate
        /// from the material editor. Editing a sidecar material therefore refreshes the model tile by itself.</summary>
        private static long ContentStamp(string fullPath)
        {
            long stamp = 0;
            try { stamp = File.GetLastWriteTimeUtc(fullPath).Ticks; } catch { }
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (Array.IndexOf(ModelExtensions, ext) >= 0)
            {
                try
                {
                    string dir = Path.GetDirectoryName(fullPath);
                    foreach (var f in Directory.EnumerateFiles(dir, "*.vmat")) stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(f).Ticks);
                    string mats = Path.Combine(dir, "materials");
                    if (Directory.Exists(mats))
                        foreach (var f in Directory.EnumerateFiles(mats, "*.vmat")) stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(f).Ticks);
                }
                catch { }
            }
            return stamp;
        }

        private static void LoadOrQueue(string key, string fullPath, int size, Kind kind)
        {
            Bitmap bmp = null;
            try
            {
                string disk = kind == Kind.Render ? DiskPath(fullPath, size) : null;
                if (disk != null && File.Exists(disk)) using (var s = File.OpenRead(disk)) bmp = new Bitmap(s);
                else if (kind == Kind.Image && File.Exists(fullPath)) using (var s = File.OpenRead(fullPath)) bmp = Bitmap.DecodeToWidth(s, size);
            }
            catch { bmp = null; }
            if (bmp != null || kind == Kind.Image) { Complete(key, bmp); return; }
            // 3D renders must run on the UI thread (render calls) — queue them for the pump.
            Dispatcher.UIThread.Post(() =>
            {
                Job job; lock (_pending) _pending.TryGetValue(key, out job);
                if (job == null) return;
                _renderQueue.Enqueue(job);
                EnsurePump();
            });
        }

        private static void EnsurePump()
        {
            if (_pump != null) { if (!_pump.IsEnabled) _pump.Start(); return; }
            _pump = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (s, e) => PumpOne());
            _pump.Start();
        }

        private static void PumpOne()
        {
            if (_renderQueue.Count == 0) { _pump?.Stop(); return; }
            var job = _renderQueue.Dequeue();
            Bitmap bmp = null;
            try
            {
                PreviewImage img;
                string ext = Path.GetExtension(job.Path).ToLowerInvariant();
                if (job.Path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) img = PreviewRenderer.RenderPrimitive(job.Path, job.Size);
                else if (ext == ".vmat") img = PreviewRenderer.RenderMaterialFile(job.Path, job.Size);
                else if (ext == ".ventity" || ext == ".vprefab") img = PreviewRenderer.RenderPrefabFile(job.Path, ProjectData.Current?.Path, job.Size);
                else if (ext == ".vfx") img = RenderVfx(job.Path, job.Size);
                else img = PreviewRenderer.RenderModelFile(job.Path, job.Size);
                if (img != null)
                {
                    bmp = ToBitmap(img);
                    string disk = DiskPath(job.Path, job.Size);
                    if (disk != null)
                        try { Directory.CreateDirectory(Path.GetDirectoryName(disk)); using (var f = File.Create(disk)) ((WriteableBitmap)bmp).Save(f); } catch { }
                }
            }
            catch { bmp = null; }
            Complete(Key(job.Path, job.Size), bmp);
        }

        /// <summary>A frame of the effect (+Z up, looping) simulated a quarter of its typical particle lifetime in, so
        /// bursts are mid-flight and short flashes still show.</summary>
        private static PreviewImage RenderVfx(string path, int size)
        {
            var asset = Editor.Core.Assets.VfxAsset.Load(path);
            if (asset == null) return null;
            using (var pv = new Editor.Core.Services.Particles.ParticleService.Preview())
            {
                if (!pv.IsValid) return null;
                pv.BeamFrom = new System.Numerics.Vector3(-1.2f, 0.6f, 0f);
                pv.BeamTo = new System.Numerics.Vector3(1.2f, 0.6f, 0f);
                pv.SetTransform(System.Numerics.Matrix4x4.CreateRotationX(-MathF.PI / 2f));
                pv.Load(asset, path, loop: true);
                var lives = new List<float>();
                foreach (var e in asset.Emitters) if (e != null && e.Enabled && e.Lifetime != null && e.Lifetime.Length > 1) lives.Add(e.Lifetime[1]);
                lives.Sort();
                float median = lives.Count > 0 ? lives[lives.Count / 2] : 0.4f;
                pv.Warm(Math.Max(0.02f, Math.Min(0.5f, median * 0.25f)));
                var scene = new PreviewScene { AllowEmpty = true, Bounds = new[] { 0f, 0.45f, 0f, 0.9f }, BeforeTargetRender = pv.BindForNextRender };
                return PreviewRenderer.Render(scene, size, size, new PreviewCamera { Yaw = 0.6f, Pitch = 0.32f, DistScale = 1f, FovDeg = 40f });
            }
        }

        /// <summary>Convert rendered BGRA pixels to an Avalonia bitmap.</summary>
        public static WriteableBitmap ToBitmap(PreviewImage img)
        {
            var wb = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var fb = wb.Lock())
                for (int y = 0; y < img.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(img.Bgra, y * img.Stride, IntPtr.Add(fb.Address, y * fb.RowBytes), img.Stride);
            return wb;
        }

        private static void Complete(string key, Bitmap bmp)
        {
            Job job;
            lock (_pending) { _pending.TryGetValue(key, out job); _pending.Remove(key); }
            if (bmp != null) lock (_memory) _memory[key] = bmp;
            if (job == null || bmp == null) return;
            Dispatcher.UIThread.Post(() => { foreach (var cb in job.Callbacks) { try { cb(bmp); } catch { } } });
        }
    }
}
