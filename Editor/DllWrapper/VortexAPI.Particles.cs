using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Editor.DllWrapper
{
    /// <summary>Particle module counters — mirrors the C struct <c>ParticleStats</c> (VortexAPI/Api/ParticleApi.cpp).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ParticleStats
    {
        public int Emitters, Beams, AliveParticles, Capacity;
        public int DrawnParticles, DrawBatches, RibbonVertices, WorkerThreads;
        /// <summary>CPU milliseconds of the last simulation step / the last render gather.</summary>
        public float SimulateMs, GatherMs;
        public int DepthSnapshots;
        /// <summary>1 when the active render backend draws particles (SDL GPU / Metal / Vulkan, and DX12 since #117).</summary>
        public int RendererDraws;
        public ulong SpawnedTotal;
    }

    /// <summary>
    /// VFX (epic #116) native entry points — P/Invoke declarations for <c>VortexAPI/Api/ParticleApi.cpp</c>.
    /// Emitters and beams are <c>uint</c> handles (0 = invalid, stale handles are ignored natively); worlds are
    /// <c>uint</c> (0 = the scene, <see cref="ParticleCreateWorld"/> for editor previews); world matrices are 16
    /// row-major floats (the SubmitRenderItem layout); descriptors are the .vfx JSON. Gameplay code uses
    /// <c>Vortex.Vfx</c>; editor/runtime code goes through <see cref="Editor.Core.Services.Particles.ParticleService"/>.
    /// </summary>
    public static partial class VortexAPI
    {
        /// <summary>Native frame driver: invoked at the start of every main-surface frame with the real frame time.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ParticleFrameCallback(float dt);

        public const uint ParticleAllWorlds = 0xFFFFFFFFu;
        public const ulong ParticleNoTexture = ulong.MaxValue;

        #region Lifecycle

        [DllImport(_dllName, CallingConvention = _cc)] public static extern int ParticleInit();
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleShutdown();
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetWorkerThreads(int count);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern uint ParticleCreateWorld();
        /// <summary>Destroy every emitter and beam of a world (<see cref="ParticleAllWorlds"/> = all).</summary>
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleClear(uint world);
        /// <summary>Step a world by dt seconds (clamped to 0.1).</summary>
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleUpdate(uint world, float dt);
        /// <summary>Register (or clear with null) the frame driver. Keep the delegate alive while registered.</summary>
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetFrameCallback(ParticleFrameCallback cb);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetAutoUpdate(int enabled);
        /// <summary>The NEXT RenderToSecondaryTarget draws this world's particles (one-shot; -1 = none).</summary>
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetNextTargetWorld(int world);
        [DllImport(_dllName, CallingConvention = _cc)] private static extern int ParticleGetLastError(byte[] buffer, int size);

        public static string ParticleLastError()
        {
            try
            {
                var buf = new byte[1024];
                int n = ParticleGetLastError(buf, buf.Length);
                return Encoding.UTF8.GetString(buf, 0, Math.Max(0, Math.Min(n, buf.Length - 1)));
            }
            catch { return ""; }
        }

        #endregion

        #region Emitters

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern uint ParticleCreateEmitterJson(byte[] utf8Json, uint world);
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int ParticleCreateEffectJson(byte[] utf8Json, uint world, [Out] uint[] outHandles, int maxHandles);
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int ParticleSetEmitterJson(uint handle, byte[] utf8Json);

        /// <summary>One emitter from its .vfx JSON object. 0 = failed (<see cref="ParticleLastError"/>).</summary>
        public static uint ParticleCreateEmitter(string json, uint world) => ParticleCreateEmitterJson(Utf8(json), world);

        /// <summary>Every enabled emitter of an effect JSON (<c>{"emitters":[...]}</c>); null on a parse error.</summary>
        public static uint[] ParticleCreateEffect(string json, uint world, int maxHandles = 64)
        {
            var handles = new uint[Math.Max(1, maxHandles)];
            int n = ParticleCreateEffectJson(Utf8(json), world, handles, handles.Length);
            if (n < 0) return null;
            Array.Resize(ref handles, n);
            return handles;
        }

        /// <summary>Live edit of an emitter (particles are kept). The emitter's texture stays unless the JSON names one.</summary>
        public static bool ParticleSetEmitter(uint handle, string json) => ParticleSetEmitterJson(handle, Utf8(json)) != 0;

        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleDestroyEmitter(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern int ParticleEmitterValid(uint handle);
        /// <summary>World matrix, 16 row-major floats.</summary>
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetTransform(uint handle, float[] world4x4);
        /// <summary>After a teleport: no pose interpolation / inherited velocity from the previous pose.</summary>
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleResetMotion(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticlePlay(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleStop(uint handle, int clearParticles);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticlePause(uint handle, int paused);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleRestart(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleBurst(uint handle, int count);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleClearParticles(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetSeed(uint handle, uint seed);
        /// <summary>0 = world, 1 = first-person viewmodel layer.</summary>
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetLayer(uint handle, int layer);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetVisible(uint handle, int visible);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetSimulationSpeed(uint handle, float speed);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetTexture(uint handle, ulong textureId);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetTrailTexture(uint handle, ulong textureId);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetAutoDestroy(uint handle, int enabled);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern int ParticleGetAliveCount(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern int ParticleIsPlaying(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern int ParticleIsFinished(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern float ParticleGetTime(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern int ParticleGetPositions(uint handle, [Out] float[] outXyz, int maxCount);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern int ParticleGetEmitterCount(uint world);

        #endregion

        #region Beams

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern uint ParticleCreateBeamJson(byte[] utf8Json, uint world);

        /// <summary>A beam from a beam JSON object or a .vfx with a "beam" section. 0 = failed.</summary>
        public static uint ParticleCreateBeam(string json, uint world) => ParticleCreateBeamJson(Utf8(json), world);

        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetBeamPoints(uint handle, float[] from3, float[] to3);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleSetBeamTexture(uint handle, ulong textureId);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleDestroyBeam(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern int ParticleBeamValid(uint handle);
        [DllImport(_dllName, CallingConvention = _cc)] public static extern int ParticleGetBeamCount(uint world);

        #endregion

        [DllImport(_dllName, CallingConvention = _cc)] public static extern void ParticleGetStats(uint world, out ParticleStats stats);

        private static byte[] Utf8(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            Array.Resize(ref bytes, bytes.Length + 1);   // NUL terminator
            return bytes;
        }
    }
}
