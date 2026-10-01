using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Editor.Core.Services.Particles
{
    /// <summary>
    /// Runtime side of the VFX module (epic #116): turns <see cref="ParticleSystem"/> components into native emitters
    /// (Engine/Graphics/Particles), keeps them on their entities every frame, and hosts the effects scripts spawn
    /// (<c>Vortex.Vfx.SpawnAt</c> / <c>Vfx.Beam</c>).
    ///
    /// Frame driver: the renderer calls the registered native frame callback at the start of every main-surface frame
    /// — after the game tick (scripts, animation, bone sockets) and before anything is drawn — so no hook in the play
    /// loops is needed: the service notices play start / stop / pause (<see cref="PlayModeService"/>) and scene
    /// switches (<see cref="ProjectData.Current"/>.ActiveScene) by itself, rebuilds, follows the entity transforms
    /// and steps the simulation. In edit mode it runs a live preview of every ParticleSystem with PreviewInEditor.
    /// A host that prefers an explicit call may use <see cref="Update"/> (the callback then skips that frame).
    /// Registration is lazy (<see cref="EnsureRegistered"/>): play start, the first API call, or the editor.
    /// </summary>
    public static class ParticleService
    {
        // --------------------------------------------------------------------------------------------- state
        private sealed class Instance
        {
            public ParticleSystem Component;
            public GameEntity Entity;
            public string Path;              // resolved absolute .vfx path
            public long Stamp;               // asset version it was built from
            public uint[] Emitters = new uint[0];
            public bool[] Looping = new bool[0];
            public bool Visible = true;
            public int Layer;
            public float IdleTime;           // edit mode: seconds since a one-shot finished (auto replay when selected)
            public bool Seen;
        }

        private sealed class Spawned
        {
            public uint[] Emitters = new uint[0];
            public uint Beam;
            public bool Scene;              // belongs to the running scene (dropped on stop / scene switch)
        }

        private sealed class CachedAsset { public VfxAsset Asset; public long Stamp; public DateTime CheckedUtc; }

        private static bool _registered, _available;
        private static VortexAPI.ParticleFrameCallback _callback;   // kept alive while registered
        private static bool _explicitThisFrame;
        private static bool _playing;
        private static Scene _scene;
        private static readonly Dictionary<ParticleSystem, Instance> _instances = new Dictionary<ParticleSystem, Instance>();
        private static readonly Dictionary<long, Spawned> _spawned = new Dictionary<long, Spawned>();
        private static readonly Dictionary<string, CachedAsset> _assets = new Dictionary<string, CachedAsset>(StringComparer.OrdinalIgnoreCase);
        private static long _nextSpawnId;
        private static long _stampCounter;
        private static readonly HashSet<GameEntity> _visited = new HashSet<GameEntity>();
        private static readonly List<ParticleSystem> _remove = new List<ParticleSystem>();
        private static readonly float[] _matrix = new float[16];
        private static DateTime _lastAssetCheckUtc = DateTime.MinValue;

        /// <summary>True once the native particle module answered (false: old native library / not initialised yet).</summary>
        public static bool Available { get { EnsureRegistered(); return _available; } }

        /// <summary>Raised after a .vfx was (re)loaded from disk — the editor refreshes open views.</summary>
        public static event Action<string> AssetReloaded;

#if VORTEX_CORE
        // Self-registration (Vortex.Core hosts: player + Avalonia editor): only managed wiring here — the native library
        // may not be resolved yet when the module loads; the callback registers on the first play-state change.
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void AutoWire()
        {
            try { PlayModeService.Instance.StateChanged += (s, st) => EnsureRegistered(); } catch { }
        }
#endif

        /// <summary>Register the native frame driver (idempotent). Safe to call before the engine is up: it retries.</summary>
        public static void EnsureRegistered()
        {
            if (_registered) return;
            try
            {
                if (VortexAPI.ParticleInit() == 0) return;
                _callback = OnFrame;
                VortexAPI.ParticleSetFrameCallback(_callback);
                _available = true;
                _registered = true;
            }
            catch (DllNotFoundException) { }                             // engine not loaded yet: retried on the next call
            catch (EntryPointNotFoundException) { _registered = true; }   // an engine without the particle module: stay off
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ParticleService] " + ex.Message); }
        }

        /// <summary>Optional explicit driver (a host loop may call it after its game tick instead of relying on the
        /// render-frame callback; the callback then skips its update for that frame).</summary>
        public static void Update(float dt)
        {
            EnsureRegistered();
            if (!_available) return;
            _explicitThisFrame = true;
            Tick(dt);
        }

        private static void OnFrame(float dt)
        {
            try
            {
                if (_explicitThisFrame) { _explicitThisFrame = false; return; }
                Tick(dt);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ParticleService] frame: " + ex); }
        }

        // --------------------------------------------------------------------------------------------- frame
        private static void Tick(float dt)
        {
            var pms = PlayModeService.Instance;
            bool playing = pms.IsPlaying;
            bool paused = pms.State == PlayState.Paused;
            var scene = ProjectData.Current?.ActiveScene;

            if (playing != _playing || !ReferenceEquals(scene, _scene))
            {
                DestroySceneInstances();
                DestroySpawned(sceneOnly: false);
                _playing = playing;
                _scene = scene;
            }

            CheckAssetChanges();
            Sync(scene, playing, paused ? 0f : dt);
            PruneSpawned();
            try { VortexAPI.ParticleUpdate(0, paused ? 0f : dt); } catch { }
        }

        /// <summary>Create / follow / drop the emitters of every ParticleSystem in the scene.</summary>
        private static void Sync(Scene scene, bool playing, float dt)
        {
            foreach (var inst in _instances.Values) inst.Seen = false;
            if (scene?.Entities != null)
            {
                _visited.Clear();
                foreach (var e in scene.Entities) Visit(e, true, false, playing, dt);
            }
            _remove.Clear();
            foreach (var kv in _instances) if (!kv.Value.Seen) _remove.Add(kv.Key);
            foreach (var c in _remove) { DestroyInstance(_instances[c]); _instances.Remove(c); }
        }

        private static void Visit(GameEntity e, bool parentActive, bool parentHidden, bool playing, float dt)
        {
            if (e == null || !_visited.Add(e)) return;
            bool active = parentActive && e.IsActive;
            if (!active) return;
            bool hidden = parentHidden || (!playing && e.IsHiddenInEditor);
            var comps = e.Components;
            for (int i = 0; i < comps.Count; i++)
            {
                if (!(comps[i] is ParticleSystem ps) || !ps.IsEnabled) continue;
                if (!playing && !ps.PreviewInEditor) continue;
                if (string.IsNullOrEmpty(ps.VfxPath)) continue;
                var inst = GetOrCreate(ps, e, playing);
                if (inst == null) continue;
                inst.Seen = true;
                Follow(inst, e);
                bool visible = !hidden;
                if (visible != inst.Visible)
                {
                    inst.Visible = visible;
                    foreach (var h in inst.Emitters) VortexAPI.ParticleSetVisible(h, visible ? 1 : 0);
                }
                if (!playing) EditPreviewReplay(inst, dt);
            }
            var children = e.Children;
            for (int i = 0; i < children.Count; i++) Visit(children[i], active, hidden, playing, dt);
        }

        private static void Follow(Instance inst, GameEntity e)
        {
            var w = BoneSocketServiceWorld(e);
            _matrix[0] = w.M11; _matrix[1] = w.M12; _matrix[2] = w.M13; _matrix[3] = w.M14;
            _matrix[4] = w.M21; _matrix[5] = w.M22; _matrix[6] = w.M23; _matrix[7] = w.M24;
            _matrix[8] = w.M31; _matrix[9] = w.M32; _matrix[10] = w.M33; _matrix[11] = w.M34;
            _matrix[12] = w.M41; _matrix[13] = w.M42; _matrix[14] = w.M43; _matrix[15] = w.M44;
            foreach (var h in inst.Emitters) VortexAPI.ParticleSetTransform(h, _matrix);
            if (inst.Layer != inst.Component.RenderLayer)
            {
                inst.Layer = inst.Component.RenderLayer;
                foreach (var h in inst.Emitters) VortexAPI.ParticleSetLayer(h, inst.Layer);
            }
        }

        private static Matrix4x4 BoneSocketServiceWorld(GameEntity e) => Editor.Core.Animation.BoneSocketService.EntityWorld(e);

        /// <summary>Edit mode: a finished one-shot on the SELECTED entity replays after a short pause (authoring loop).</summary>
        private static void EditPreviewReplay(Instance inst, float dt)
        {
            if (!inst.Component.PlayOnStart || inst.Emitters.Length == 0) return;
            bool finished = true;
            foreach (var h in inst.Emitters) if (VortexAPI.ParticleIsFinished(h) == 0) { finished = false; break; }
            if (!finished) { inst.IdleTime = 0f; return; }
            if (!ReferenceEquals(SelectionService.Instance.SelectedEntity, inst.Entity)) return;
            inst.IdleTime += dt;
            if (inst.IdleTime < 0.8f) return;
            inst.IdleTime = 0f;
            foreach (var h in inst.Emitters) VortexAPI.ParticleRestart(h);
        }

        private static Instance GetOrCreate(ParticleSystem ps, GameEntity e, bool playing)
        {
            string path = ResolveAssetPath(ps.VfxPath);
            if (_instances.TryGetValue(ps, out var inst))
            {
                if (string.Equals(inst.Path, path, StringComparison.OrdinalIgnoreCase)) return inst;
                DestroyInstance(inst);       // the component points at another effect now
                _instances.Remove(ps);
            }
            var asset = GetAsset(path, out long stamp);
            inst = new Instance { Component = ps, Entity = e, Path = path, Stamp = stamp, Layer = ps.RenderLayer };
            _instances[ps] = inst;          // also cache failures (null asset) so a bad path is not retried every frame
            if (asset == null) return inst;
            Build(inst, asset, playing ? ps.PlayOnStart : (ps.PlayOnStart && ps.PreviewInEditor));
            Follow(inst, e);
            foreach (var h in inst.Emitters) VortexAPI.ParticleResetMotion(h);   // no smear from the origin
            return inst;
        }

        private static void Build(Instance inst, VfxAsset asset, bool play)
        {
            var ps = inst.Component;
            var list = new List<uint>();
            var loops = new List<bool>();
            for (int i = 0; i < asset.Emitters.Count; i++)
            {
                var src = asset.Emitters[i];
                if (src == null || !src.Enabled) continue;
                var em = CloneEmitter(src);
                if (ps.Loop == (int)ParticleLoopMode.Loop) em.Looping = true;
                else if (ps.Loop == (int)ParticleLoopMode.Once) em.Looping = false;
                if (ps.SimulationSpace == (int)ParticleSpaceMode.World) em.SimulationSpace = VfxSpace.World;
                else if (ps.SimulationSpace == (int)ParticleSpaceMode.Local) em.SimulationSpace = VfxSpace.Local;
                if (ps.Seed != 0) em.Seed = unchecked((uint)ps.Seed * 2654435761u + (uint)(i * 97 + 1));
                em.SimulationSpeed *= Math.Max(0f, ps.SimulationSpeed);
                uint h = CreateEmitter(em, inst.Path, ps.RenderLayer);
                if (h == 0) continue;
                list.Add(h);
                loops.Add(em.Looping);
                if (play) VortexAPI.ParticlePlay(h);
            }
            inst.Emitters = list.ToArray();
            inst.Looping = loops.ToArray();
            inst.Visible = true;
        }

        private static uint CreateEmitter(VfxEmitter em, string vfxPath, int layer)
        {
            uint h = VortexAPI.ParticleCreateEmitter(VfxAsset.EmitterJson(em), 0);
            if (h == 0)
            {
                ConsoleService.Instance.LogError("VFX: emitter '" + em.Name + "' of " + System.IO.Path.GetFileName(vfxPath) + " failed: " + VortexAPI.ParticleLastError());
                return 0;
            }
            ApplyTextures(h, em, vfxPath);
            VortexAPI.ParticleSetLayer(h, layer);
            return h;
        }

        private static void ApplyTextures(uint h, VfxEmitter em, string vfxPath)
        {
            string root = ProjectData.Current?.Path;
            VortexAPI.ParticleSetTexture(h, TextureId(VfxAsset.ResolveTexture(em.Render?.Texture, vfxPath, root)));
            VortexAPI.ParticleSetTrailTexture(h, TextureId(VfxAsset.ResolveTexture(em.Trails?.Texture, vfxPath, root)));
        }

        private static ulong TextureId(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return VortexAPI.ParticleNoTexture;
            long id = -1;
            try { id = MaterialService.ImportTextureCached(absPath); } catch { }
            return id >= 0 ? (ulong)id : VortexAPI.ParticleNoTexture;
        }

        private static void DestroyInstance(Instance inst)
        {
            foreach (var h in inst.Emitters) { try { VortexAPI.ParticleDestroyEmitter(h); } catch { } }
            inst.Emitters = new uint[0];
        }

        private static void DestroySceneInstances()
        {
            foreach (var inst in _instances.Values) DestroyInstance(inst);
            _instances.Clear();
        }

        // --------------------------------------------------------------------------------------------- assets
        /// <summary>Absolute path of a component / script effect path (project-relative, absolute, or already resolved).</summary>
        public static string ResolveAssetPath(string vfxPath)
        {
            if (string.IsNullOrWhiteSpace(vfxPath)) return null;
            string p = vfxPath.Replace('\\', '/');
            try
            {
                if (System.IO.Path.IsPathRooted(p)) return System.IO.Path.GetFullPath(p);
                string root = ProjectData.Current?.Path;
                return System.IO.Path.GetFullPath(string.IsNullOrEmpty(root) ? p : System.IO.Path.Combine(root, p));
            }
            catch { return p; }
        }

        private static VfxAsset GetAsset(string path, out long stamp)
        {
            stamp = 0;
            if (string.IsNullOrEmpty(path)) return null;
            if (_assets.TryGetValue(path, out var c)) { stamp = c.Stamp; return c.Asset; }
            var asset = VfxAsset.Load(path);
            if (asset == null) ConsoleService.Instance.LogWarning("VFX: effect not found or invalid: " + path);
            c = new CachedAsset { Asset = asset, Stamp = ++_stampCounter, CheckedUtc = DateTime.UtcNow };
            try { if (!AssetVfs.IsMounted && File.Exists(path)) c.CheckedUtc = File.GetLastWriteTimeUtc(path); } catch { }
            _assets[path] = c;
            stamp = c.Stamp;
            return asset;
        }

        /// <summary>Editor: a .vfx changed (the Particle Editor saved it) — reload it and rebuild every user now.</summary>
        public static void NotifyAssetChanged(string vfxPath)
        {
            string path = ResolveAssetPath(vfxPath);
            if (string.IsNullOrEmpty(path)) return;
            _assets.Remove(path);
            bool playing = PlayModeService.Instance.IsPlaying;
            foreach (var inst in _instances.Values)
            {
                if (!string.Equals(inst.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                DestroyInstance(inst);
                var asset = GetAsset(path, out long stamp);
                inst.Stamp = stamp;
                if (asset == null) continue;
                Build(inst, asset, playing ? inst.Component.PlayOnStart : inst.Component.PlayOnStart && inst.Component.PreviewInEditor);
                Follow(inst, inst.Entity);
                foreach (var h in inst.Emitters) VortexAPI.ParticleResetMotion(h);
            }
            try { AssetReloaded?.Invoke(path); } catch { }
        }

        /// <summary>Loose-file hot reload (editor): twice a second, a .vfx whose file changed is reloaded.</summary>
        private static void CheckAssetChanges()
        {
            if (AssetVfs.IsMounted) return;
            var now = DateTime.UtcNow;
            if ((now - _lastAssetCheckUtc).TotalSeconds < 0.5) return;
            _lastAssetCheckUtc = now;
            List<string> changed = null;
            foreach (var kv in _assets)
            {
                try
                {
                    if (!File.Exists(kv.Key)) continue;
                    var t = File.GetLastWriteTimeUtc(kv.Key);
                    if (t != kv.Value.CheckedUtc) (changed ?? (changed = new List<string>())).Add(kv.Key);
                }
                catch { }
            }
            if (changed != null) foreach (var p in changed) NotifyAssetChanged(p);
        }

        private static VfxEmitter CloneEmitter(VfxEmitter e)
            => System.Text.Json.JsonSerializer.Deserialize<VfxEmitter>(System.Text.Json.JsonSerializer.Serialize(e, VfxAsset.JsonOptions), VfxAsset.JsonOptions);

        // --------------------------------------------------------------------------------------------- component control
        private static Instance Find(ParticleSystem ps, bool create)
        {
            if (ps == null) return null;
            EnsureRegistered();
            if (!_available) return null;
            if (_instances.TryGetValue(ps, out var inst)) return inst;
            if (!create || ps.Entity == null || string.IsNullOrEmpty(ps.VfxPath)) return null;
            bool playing = PlayModeService.Instance.IsPlaying;
            // created on demand (a script's Start() may run before the first frame callback saw the component)
            inst = GetOrCreate(ps, ps.Entity, playing);
            return inst;
        }

        /// <summary>Start (or resume) the effect; a finished one-shot restarts.</summary>
        public static void Play(ParticleSystem ps)
        {
            var inst = Find(ps, true);
            if (inst == null) return;
            foreach (var h in inst.Emitters)
            {
                if (VortexAPI.ParticleIsFinished(h) != 0) VortexAPI.ParticleRestart(h);
                else VortexAPI.ParticlePlay(h);
            }
        }

        public static void Stop(ParticleSystem ps, bool clear = false)
        {
            var inst = Find(ps, false);
            if (inst == null) return;
            foreach (var h in inst.Emitters) VortexAPI.ParticleStop(h, clear ? 1 : 0);
        }

        public static void Pause(ParticleSystem ps, bool paused)
        {
            var inst = Find(ps, false);
            if (inst == null) return;
            foreach (var h in inst.Emitters) VortexAPI.ParticlePause(h, paused ? 1 : 0);
        }

        public static void Restart(ParticleSystem ps)
        {
            var inst = Find(ps, true);
            if (inst == null) return;
            foreach (var h in inst.Emitters) VortexAPI.ParticleRestart(h);
        }

        /// <summary>Emit <paramref name="count"/> particles on every emitter of the effect right now (works while stopped).</summary>
        public static void Burst(ParticleSystem ps, int count)
        {
            var inst = Find(ps, true);
            if (inst == null || count <= 0) return;
            foreach (var h in inst.Emitters) VortexAPI.ParticleBurst(h, count);
        }

        public static bool IsPlaying(ParticleSystem ps)
        {
            var inst = Find(ps, false);
            if (inst == null) return false;
            foreach (var h in inst.Emitters) if (VortexAPI.ParticleIsPlaying(h) != 0) return true;
            return false;
        }

        public static int AliveCount(ParticleSystem ps)
        {
            var inst = Find(ps, false);
            if (inst == null) return 0;
            int n = 0;
            foreach (var h in inst.Emitters) n += VortexAPI.ParticleGetAliveCount(h);
            return n;
        }

        /// <summary>Native emitter handles of a component (empty while it has no live effect) — tools and tests.</summary>
        public static uint[] EmittersOf(ParticleSystem ps)
        {
            return ps != null && _instances.TryGetValue(ps, out var inst) ? (uint[])inst.Emitters.Clone() : new uint[0];
        }

        /// <summary>Every ParticleSystem on the entity and its children (Vfx.Play(entity) acts on all of them).</summary>
        public static List<ParticleSystem> SystemsOf(GameEntity e)
        {
            var list = new List<ParticleSystem>();
            Collect(e, list, 0);
            return list;
        }

        private static void Collect(GameEntity e, List<ParticleSystem> list, int depth)
        {
            if (e == null || depth > 64) return;
            foreach (var c in e.Components) if (c is ParticleSystem ps) list.Add(ps);
            foreach (var ch in e.Children) Collect(ch, list, depth + 1);
        }

        // --------------------------------------------------------------------------------------------- spawned effects
        /// <summary>Fire-and-forget effect at a pose (world space): every enabled emitter of the .vfx, removed when finished.
        /// Returns an id (0 = failed). Looping emitters keep running until <see cref="StopSpawned"/> / <see cref="DestroySpawned"/>.</summary>
        public static long SpawnAt(string vfxPath, Vector3 position, Quaternion rotation, float scale = 1f, int layer = 0)
        {
            EnsureRegistered();
            if (!_available) return 0;
            string path = ResolveAssetPath(vfxPath);
            var asset = GetAsset(path, out _);
            if (asset == null) return 0;
            var m = Matrix4x4.CreateScale(scale <= 0f ? 1f : scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(position);
            float[] world = ToArray(m);
            var list = new List<uint>();
            foreach (var src in asset.Emitters)
            {
                if (src == null || !src.Enabled) continue;
                uint h = CreateEmitter(CloneEmitter(src), path, layer);
                if (h == 0) continue;
                VortexAPI.ParticleSetTransform(h, world);
                VortexAPI.ParticleResetMotion(h);
                VortexAPI.ParticleSetAutoDestroy(h, 1);
                VortexAPI.ParticlePlay(h);
                list.Add(h);
            }
            if (list.Count == 0) return 0;
            long id = ++_nextSpawnId;
            _spawned[id] = new Spawned { Emitters = list.ToArray(), Scene = true };
            return id;
        }

        /// <summary>A beam from <paramref name="from"/> to <paramref name="to"/> with the .vfx's beam section (null / no
        /// beam section = a default hot tracer). duration &gt; 0 overrides; for travelling tracers (speed &gt; 0) a zero
        /// duration becomes the flight time. Returns an id (0 = failed).</summary>
        public static long Beam(Vector3 from, Vector3 to, string vfxPath, float duration, int layer = 0)
        {
            EnsureRegistered();
            if (!_available) return 0;
            string path = string.IsNullOrEmpty(vfxPath) ? null : ResolveAssetPath(vfxPath);
            VfxAsset asset = path != null ? GetAsset(path, out _) : null;
            VfxBeam b = asset?.Beam;
            if (b == null)
            {
                b = new VfxBeam { Width = 0.025f, Color = new[] { 1f, 0.82f, 0.45f, 1f }, Emissive = 1.6f, Speed = 260f, Length = 3.5f, Duration = 0f, FadeOut = 0.03f,
                    WidthCurve = VfxCurve.Linear(0.5f, 1f), ColorGradient = VfxGradient.Of(new VfxGradientKey(0f, 1f, 1f, 1f, 0.2f), new VfxGradientKey(1f, 1f, 1f, 1f, 1f)) };
            }
            else b = System.Text.Json.JsonSerializer.Deserialize<VfxBeam>(System.Text.Json.JsonSerializer.Serialize(b, VfxAsset.JsonOptions), VfxAsset.JsonOptions);
            if (duration > 0f) b.Duration = duration;
            else if (b.Speed > 0f && b.Duration <= 0f) b.Duration = (Vector3.Distance(from, to) + b.Length) / b.Speed + b.FadeOut;
            uint h = VortexAPI.ParticleCreateBeam(VfxAsset.BeamJson(b, layer), 0);
            if (h == 0) { ConsoleService.Instance.LogError("VFX: beam failed: " + VortexAPI.ParticleLastError()); return 0; }
            if (path != null) VortexAPI.ParticleSetBeamTexture(h, TextureId(VfxAsset.ResolveTexture(b.Texture, path, ProjectData.Current?.Path)));
            VortexAPI.ParticleSetBeamPoints(h, new[] { from.X, from.Y, from.Z }, new[] { to.X, to.Y, to.Z });
            long id = ++_nextSpawnId;
            _spawned[id] = new Spawned { Beam = h, Scene = true };
            return id;
        }

        public static bool IsSpawnedAlive(long id)
        {
            if (!_spawned.TryGetValue(id, out var s)) return false;
            if (s.Beam != 0 && VortexAPI.ParticleBeamValid(s.Beam) != 0) return true;
            foreach (var h in s.Emitters) if (VortexAPI.ParticleEmitterValid(h) != 0) return true;
            return false;
        }

        public static int SpawnedAliveCount(long id)
        {
            if (!_spawned.TryGetValue(id, out var s)) return 0;
            int n = 0;
            foreach (var h in s.Emitters) n += VortexAPI.ParticleGetAliveCount(h);
            return n;
        }

        /// <summary>Stop emitting (the particles live out their lifetime, then the effect removes itself).</summary>
        public static void StopSpawned(long id, bool clear = false)
        {
            if (!_spawned.TryGetValue(id, out var s)) return;
            foreach (var h in s.Emitters) VortexAPI.ParticleStop(h, clear ? 1 : 0);
            if (clear && s.Beam != 0) { VortexAPI.ParticleDestroyBeam(s.Beam); s.Beam = 0; }
        }

        public static void DestroySpawned(long id)
        {
            if (!_spawned.TryGetValue(id, out var s)) return;
            foreach (var h in s.Emitters) VortexAPI.ParticleDestroyEmitter(h);
            if (s.Beam != 0) VortexAPI.ParticleDestroyBeam(s.Beam);
            _spawned.Remove(id);
        }

        /// <summary>Move a spawned effect (emitters) — e.g. a smoke trail following a projectile.</summary>
        public static void MoveSpawned(long id, Vector3 position, Quaternion rotation)
        {
            if (!_spawned.TryGetValue(id, out var s)) return;
            float[] world = ToArray(Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(position));
            foreach (var h in s.Emitters) VortexAPI.ParticleSetTransform(h, world);
        }

        public static void SetBeamPoints(long id, Vector3 from, Vector3 to)
        {
            if (_spawned.TryGetValue(id, out var s) && s.Beam != 0)
                VortexAPI.ParticleSetBeamPoints(s.Beam, new[] { from.X, from.Y, from.Z }, new[] { to.X, to.Y, to.Z });
        }

        private static void PruneSpawned()
        {
            if (_spawned.Count == 0) return;
            List<long> dead = null;
            foreach (var kv in _spawned)
            {
                bool alive = kv.Value.Beam != 0 && VortexAPI.ParticleBeamValid(kv.Value.Beam) != 0;
                if (!alive) foreach (var h in kv.Value.Emitters) if (VortexAPI.ParticleEmitterValid(h) != 0) { alive = true; break; }
                if (!alive) (dead ?? (dead = new List<long>())).Add(kv.Key);
            }
            if (dead != null) foreach (var id in dead) _spawned.Remove(id);
        }

        private static void DestroySpawned(bool sceneOnly)
        {
            var ids = new List<long>(_spawned.Keys);
            foreach (var id in ids) if (!sceneOnly || _spawned[id].Scene) DestroySpawned(id);
        }

        // --------------------------------------------------------------------------------------------- misc
        /// <summary>Counters of the scene world (world 0).</summary>
        public static ParticleStats Stats()
        {
            ParticleStats s = default;
            try { if (Available) VortexAPI.ParticleGetStats(0, out s); } catch { }
            return s;
        }

        /// <summary>Drop every scene effect (tests / project close). The next frame rebuilds from the scene.</summary>
        public static void Reset()
        {
            DestroySceneInstances();
            DestroySpawned(sceneOnly: false);
            _assets.Clear();
            _scene = null;
        }

        private static float[] ToArray(Matrix4x4 m) => new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };

        // --------------------------------------------------------------------------------------------- previews
        private static readonly Stack<uint> _freePreviewWorlds = new Stack<uint>();

        /// <summary>
        /// An isolated particle world for editor previews (VFX editor, thumbnails, asset viewer): the effect only
        /// simulates when <see cref="Step"/> is called and only draws into the render target that follows
        /// <see cref="BindForNextRender"/>. Worlds are pooled (the native module has no destroy-world call).
        /// </summary>
        public sealed class Preview : IDisposable
        {
            private readonly List<uint> _emitters = new List<uint>();
            private uint _beam;
            private VfxBeam _beamDesc;
            private string _beamPath;

            public uint World { get; private set; } = uint.MaxValue;
            public bool IsValid => World != uint.MaxValue;
            /// <summary>Seconds simulated since the last <see cref="Load"/> / restart.</summary>
            public float Time { get; private set; }
            /// <summary>Beam preview segment (the effect's beam runs from here to <see cref="BeamTo"/>).</summary>
            public Vector3 BeamFrom = new Vector3(0f, 0f, 0f), BeamTo = new Vector3(0f, 0f, 6f);
            private Matrix4x4 _transform = Matrix4x4.Identity;

            /// <summary>Pose of the effect (its +Z is the emission axis): applied to every emitter now and on Load.</summary>
            public void SetTransform(Matrix4x4 world)
            {
                _transform = world;
                var m = ToArray(world);
                foreach (var h in _emitters) { try { VortexAPI.ParticleSetTransform(h, m); VortexAPI.ParticleResetMotion(h); } catch { } }
            }

            public Preview()
            {
                EnsureRegistered();
                if (!_available) return;
                World = _freePreviewWorlds.Count > 0 ? _freePreviewWorlds.Pop() : VortexAPI.ParticleCreateWorld();
            }

            /// <summary>Instantiate every enabled emitter of <paramref name="asset"/> (and its beam) at the origin,
            /// +Z forward. <paramref name="loop"/> forces looping so one-shot effects replay.</summary>
            public void Load(VfxAsset asset, string vfxPath, bool loop)
            {
                Clear();
                if (!IsValid || asset == null) return;
                for (int i = 0; i < asset.Emitters.Count; i++)
                {
                    var src = asset.Emitters[i];
                    if (src == null || !src.Enabled) continue;
                    var em = CloneEmitter(src);
                    if (loop) em.Looping = true;
                    uint h = VortexAPI.ParticleCreateEmitter(VfxAsset.EmitterJson(em), World);
                    if (h == 0) continue;
                    ApplyTextures(h, em, vfxPath);
                    VortexAPI.ParticleSetTransform(h, ToArray(_transform));
                    VortexAPI.ParticleResetMotion(h);
                    VortexAPI.ParticlePlay(h);
                    _emitters.Add(h);
                }
                _beamDesc = asset.Beam; _beamPath = vfxPath;
                SpawnBeam();
                Time = 0f;
            }

            private void SpawnBeam()
            {
                if (_beam != 0) { try { VortexAPI.ParticleDestroyBeam(_beam); } catch { } _beam = 0; }
                if (_beamDesc == null || !IsValid) return;
                var b = System.Text.Json.JsonSerializer.Deserialize<VfxBeam>(System.Text.Json.JsonSerializer.Serialize(_beamDesc, VfxAsset.JsonOptions), VfxAsset.JsonOptions);
                if (b.Speed > 0f && b.Duration <= 0f) b.Duration = (Vector3.Distance(BeamFrom, BeamTo) + b.Length) / b.Speed + b.FadeOut;
                _beam = VortexAPI.ParticleCreateBeam(VfxAsset.BeamJson(b, 0), World);
                if (_beam == 0) return;
                VortexAPI.ParticleSetBeamTexture(_beam, TextureId(VfxAsset.ResolveTexture(b.Texture, _beamPath, ProjectData.Current?.Path)));
                VortexAPI.ParticleSetBeamPoints(_beam, new[] { BeamFrom.X, BeamFrom.Y, BeamFrom.Z }, new[] { BeamTo.X, BeamTo.Y, BeamTo.Z });
            }

            /// <summary>Advance the simulation; a finished beam is fired again (looping preview).</summary>
            public void Step(float dt)
            {
                if (!IsValid) return;
                VortexAPI.ParticleUpdate(World, dt);
                Time += dt;
                if (_beamDesc != null && (_beam == 0 || VortexAPI.ParticleBeamValid(_beam) == 0)) SpawnBeam();
            }

            /// <summary>Simulate <paramref name="seconds"/> in fixed steps (thumbnails: show the effect mid-flight).</summary>
            public void Warm(float seconds, float step = 1f / 60f)
            {
                for (float t = 0f; t < seconds; t += step) Step(step);
            }

            public void Restart()
            {
                foreach (var h in _emitters) { try { VortexAPI.ParticleRestart(h); } catch { } }
                SpawnBeam();
                Time = 0f;
            }

            public void SetPaused(bool paused) { foreach (var h in _emitters) { try { VortexAPI.ParticlePause(h, paused ? 1 : 0); } catch { } } }

            /// <summary>Call right before rendering the preview target: that render draws this world's particles.</summary>
            public void BindForNextRender() { if (IsValid) VortexAPI.ParticleSetNextTargetWorld((int)World); }

            public int AliveCount
            {
                get { int n = 0; foreach (var h in _emitters) { try { n += VortexAPI.ParticleGetAliveCount(h); } catch { } } return n; }
            }

            public void Clear()
            {
                foreach (var h in _emitters) { try { VortexAPI.ParticleDestroyEmitter(h); } catch { } }
                _emitters.Clear();
                if (_beam != 0) { try { VortexAPI.ParticleDestroyBeam(_beam); } catch { } _beam = 0; }
                if (IsValid) { try { VortexAPI.ParticleClear(World); } catch { } }
                Time = 0f;
            }

            public void Dispose()
            {
                if (!IsValid) return;
                Clear();
                _freePreviewWorlds.Push(World);
                World = uint.MaxValue;
            }
        }
    }
}
