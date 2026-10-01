using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using Editor.Core.Assets;
using VortexEditor.Services;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>What changed on disk under the project's Assets folder during one debounce window.</summary>
    public sealed class AssetChanges
    {
        /// <summary>Every created / changed / deleted / renamed path (old and new names of renames).</summary>
        public HashSet<string> Paths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Items were created, deleted or renamed (the folder tree may need to change).</summary>
        public bool Structural { get; set; }
        /// <summary>The OS dropped events (buffer overflow) — refresh everything.</summary>
        public bool Overflow { get; set; }
        /// <summary>Paths whose thumbnails were invalidated (incl. models whose sidecar materials changed).</summary>
        public HashSet<string> Invalidated { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool Touches(string folder)
        {
            if (Overflow) return true;
            foreach (var p in Paths)
                if (AssetFileOps.PathsEqual(Path.GetDirectoryName(p), folder) || AssetFileOps.PathsEqual(p, folder)) return true;
            return false;
        }
    }

    /// <summary>
    /// Watches the open project's Assets folder (recursive) so the Asset Browser and the file tree refresh by
    /// themselves when files change — also changes made outside the editor (Finder, git, a DCC exporter). Events are
    /// coalesced on the UI thread (a burst of 100 copied files is one refresh); every changed file's thumbnail is
    /// invalidated in <see cref="ThumbnailService"/>, and a changed <c>materials/*.vmat</c> sidecar also invalidates
    /// the models next to that folder. Hidden files and <c>.vmeta</c> sidecars are ignored.
    /// </summary>
    public sealed class AssetWatcher : IDisposable
    {
        private static AssetWatcher _current;

        /// <summary>Debounced change notification (UI thread).</summary>
        public static event Action<AssetChanges> Changed;

        /// <summary>The folder being watched (null when stopped).</summary>
        public static string WatchedFolder => _current?._root;

        public static void Start(string projectRoot)
        {
            string assets = string.IsNullOrEmpty(projectRoot) ? null : Path.Combine(projectRoot, "Assets");
            if (_current != null && AssetFileOps.PathsEqual(_current._root, assets)) return;
            Stop();
            if (assets == null || !Directory.Exists(assets)) return;
            try { _current = new AssetWatcher(assets); } catch { _current = null; }
        }

        public static void Stop() { _current?.Dispose(); _current = null; }

        private readonly string _root;
        private readonly FileSystemWatcher _fsw;
        private readonly object _lock = new object();
        private AssetChanges _pending = new AssetChanges();
        private DispatcherTimer _debounce;
        private bool _disposed;

        private AssetWatcher(string root)
        {
            _root = AssetTile.Normalize(root);
            _fsw = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024
            };
            _fsw.Created += (s, e) => Add(e.FullPath, structural: true);
            _fsw.Deleted += (s, e) => Add(e.FullPath, structural: true);
            _fsw.Changed += (s, e) => Add(e.FullPath, structural: false);
            _fsw.Renamed += (s, e) => { Add(e.OldFullPath, structural: true); Add(e.FullPath, structural: true); };
            _fsw.Error += (s, e) => { lock (_lock) _pending.Overflow = true; Schedule(); };
            _fsw.EnableRaisingEvents = true;
        }

        /// <summary>Hidden/temp files and asset-metadata sidecars never refresh the browser.</summary>
        internal static bool Ignored(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name) || name.StartsWith(".") || name.EndsWith("~") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(AssetDatabase.MetaFileExtension, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.IndexOf(Path.DirectorySeparatorChar + ".", StringComparison.Ordinal) >= 0) return true;   // inside a hidden folder
            return false;
        }

        private void Add(string path, bool structural)
        {
            if (_disposed || Ignored(path)) return;
            lock (_lock)
            {
                _pending.Paths.Add(path);
                if (structural) _pending.Structural = true;
            }
            Schedule();
        }

        private void Schedule()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                if (_debounce == null)
                {
                    _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _debounce.Tick += (s, e) => { _debounce.Stop(); Flush(); };
                }
                _debounce.Stop();
                _debounce.Start();
            });
        }

        private void Flush()
        {
            AssetChanges batch;
            lock (_lock) { batch = _pending; _pending = new AssetChanges(); }
            if (_disposed || (batch.Paths.Count == 0 && !batch.Overflow)) return;
            foreach (var p in batch.Paths)
            {
                ThumbnailService.Invalidate(p);
                batch.Invalidated.Add(p);
                // A model's per-submesh materials live in <model folder>/materials/*.vmat: editing one changes how the
                // model (and prefabs of it) render, so the model thumbnails next to that folder are stale too.
                if (p.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
                {
                    string dir = Path.GetDirectoryName(p);
                    if (string.Equals(Path.GetFileName(dir), "materials", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            foreach (var m in Directory.EnumerateFiles(Path.GetDirectoryName(dir)).Where(f => AssetKinds.Is(Path.GetExtension(f).ToLowerInvariant(), AssetKinds.ModelExt)))
                            { ThumbnailService.Invalidate(m); batch.Invalidated.Add(m); }
                        }
                        catch { }
                    }
                }
            }
            try { Changed?.Invoke(batch); } catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); } catch { }
            _debounce?.Stop();
        }
    }
}
