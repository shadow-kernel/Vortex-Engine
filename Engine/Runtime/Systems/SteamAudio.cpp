#include "SteamAudio.h"
#include "AudioInternal.h"

#include <atomic>
#include <mutex>
#include <thread>
#include <vector>
#include <algorithm>
#include <cmath>
#include <chrono>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// phonon.h is included ONLY for the IPL* types/structs/enums. Every entry point is resolved through
// GetProcAddress (see load_dll) so phonon.dll stays a truly optional runtime dependency — we never take a
// link-time reference to it.
#include <phonon.h>                        // resolved via AdditionalIncludeDirectories (ThirdParty/steam-audio/include)
#include "../../ThirdParty/miniaudio.h"   // ma_node types (no MA_IMPLEMENTATION here)

namespace vortex::runtime::audio::steam {
namespace {

	// ----------------------------------------------------------------------------------------------------
	// Dynamically-loaded phonon entry points
	// ----------------------------------------------------------------------------------------------------
	HMODULE g_dll = nullptr;

	typedef IPLerror (IPLCALL *pfn_iplContextCreate)(IPLContextSettings*, IPLContext*);
	typedef void     (IPLCALL *pfn_iplContextRelease)(IPLContext*);
	typedef IPLerror (IPLCALL *pfn_iplHRTFCreate)(IPLContext, IPLAudioSettings*, IPLHRTFSettings*, IPLHRTF*);
	typedef void     (IPLCALL *pfn_iplHRTFRelease)(IPLHRTF*);
	typedef IPLerror (IPLCALL *pfn_iplBinauralEffectCreate)(IPLContext, IPLAudioSettings*, IPLBinauralEffectSettings*, IPLBinauralEffect*);
	typedef void     (IPLCALL *pfn_iplBinauralEffectRelease)(IPLBinauralEffect*);
	typedef IPLAudioEffectState (IPLCALL *pfn_iplBinauralEffectApply)(IPLBinauralEffect, IPLBinauralEffectParams*, IPLAudioBuffer*, IPLAudioBuffer*);
	typedef IPLVector3 (IPLCALL *pfn_iplCalculateRelativeDirection)(IPLContext, IPLVector3, IPLVector3, IPLVector3, IPLVector3);
	typedef IPLerror (IPLCALL *pfn_iplSceneCreate)(IPLContext, IPLSceneSettings*, IPLScene*);
	typedef void     (IPLCALL *pfn_iplSceneRelease)(IPLScene*);
	typedef void     (IPLCALL *pfn_iplSceneCommit)(IPLScene);
	typedef IPLerror (IPLCALL *pfn_iplStaticMeshCreate)(IPLScene, IPLStaticMeshSettings*, IPLStaticMesh*);
	typedef void     (IPLCALL *pfn_iplStaticMeshRelease)(IPLStaticMesh*);
	typedef void     (IPLCALL *pfn_iplStaticMeshAdd)(IPLStaticMesh, IPLScene);
	typedef IPLerror (IPLCALL *pfn_iplSimulatorCreate)(IPLContext, IPLSimulationSettings*, IPLSimulator*);
	typedef void     (IPLCALL *pfn_iplSimulatorRelease)(IPLSimulator*);
	typedef void     (IPLCALL *pfn_iplSimulatorSetScene)(IPLSimulator, IPLScene);
	typedef void     (IPLCALL *pfn_iplSimulatorCommit)(IPLSimulator);
	typedef void     (IPLCALL *pfn_iplSimulatorSetSharedInputs)(IPLSimulator, IPLSimulationFlags, IPLSimulationSharedInputs*);
	typedef void     (IPLCALL *pfn_iplSimulatorRunDirect)(IPLSimulator);
	typedef IPLerror (IPLCALL *pfn_iplSourceCreate)(IPLSimulator, IPLSourceSettings*, IPLSource*);
	typedef void     (IPLCALL *pfn_iplSourceRelease)(IPLSource*);
	typedef void     (IPLCALL *pfn_iplSourceAdd)(IPLSource, IPLSimulator);
	typedef void     (IPLCALL *pfn_iplSourceRemove)(IPLSource, IPLSimulator);
	typedef void     (IPLCALL *pfn_iplSourceSetInputs)(IPLSource, IPLSimulationFlags, IPLSimulationInputs*);
	typedef void     (IPLCALL *pfn_iplSourceGetOutputs)(IPLSource, IPLSimulationFlags, IPLSimulationOutputs*);

	pfn_iplContextCreate            P_ContextCreate = nullptr;
	pfn_iplContextRelease           P_ContextRelease = nullptr;
	pfn_iplHRTFCreate               P_HRTFCreate = nullptr;
	pfn_iplHRTFRelease              P_HRTFRelease = nullptr;
	pfn_iplBinauralEffectCreate     P_BinauralCreate = nullptr;
	pfn_iplBinauralEffectRelease    P_BinauralRelease = nullptr;
	pfn_iplBinauralEffectApply      P_BinauralApply = nullptr;
	pfn_iplCalculateRelativeDirection P_RelDir = nullptr;
	pfn_iplSceneCreate              P_SceneCreate = nullptr;
	pfn_iplSceneRelease             P_SceneRelease = nullptr;
	pfn_iplSceneCommit              P_SceneCommit = nullptr;
	pfn_iplStaticMeshCreate         P_MeshCreate = nullptr;
	pfn_iplStaticMeshRelease        P_MeshRelease = nullptr;
	pfn_iplStaticMeshAdd            P_MeshAdd = nullptr;
	pfn_iplSimulatorCreate          P_SimCreate = nullptr;
	pfn_iplSimulatorRelease         P_SimRelease = nullptr;
	pfn_iplSimulatorSetScene        P_SimSetScene = nullptr;
	pfn_iplSimulatorCommit          P_SimCommit = nullptr;
	pfn_iplSimulatorSetSharedInputs P_SimSetShared = nullptr;
	pfn_iplSimulatorRunDirect       P_SimRunDirect = nullptr;
	pfn_iplSourceCreate             P_SourceCreate = nullptr;
	pfn_iplSourceRelease            P_SourceRelease = nullptr;
	pfn_iplSourceAdd                P_SourceAdd = nullptr;
	pfn_iplSourceRemove             P_SourceRemove = nullptr;
	pfn_iplSourceSetInputs          P_SourceSetInputs = nullptr;
	pfn_iplSourceGetOutputs         P_SourceGetOutputs = nullptr;

	bool load_dll()
	{
		if (g_dll) return true;
		g_dll = LoadLibraryW(L"phonon.dll");
		if (!g_dll) return false;
		#define LOAD(field, name) field = (pfn_##name)GetProcAddress(g_dll, #name); if (!field) return false;
		LOAD(P_ContextCreate, iplContextCreate); LOAD(P_ContextRelease, iplContextRelease);
		LOAD(P_HRTFCreate, iplHRTFCreate); LOAD(P_HRTFRelease, iplHRTFRelease);
		LOAD(P_BinauralCreate, iplBinauralEffectCreate); LOAD(P_BinauralRelease, iplBinauralEffectRelease);
		LOAD(P_BinauralApply, iplBinauralEffectApply); LOAD(P_RelDir, iplCalculateRelativeDirection);
		LOAD(P_SceneCreate, iplSceneCreate); LOAD(P_SceneRelease, iplSceneRelease); LOAD(P_SceneCommit, iplSceneCommit);
		LOAD(P_MeshCreate, iplStaticMeshCreate); LOAD(P_MeshRelease, iplStaticMeshRelease); LOAD(P_MeshAdd, iplStaticMeshAdd);
		LOAD(P_SimCreate, iplSimulatorCreate); LOAD(P_SimRelease, iplSimulatorRelease);
		LOAD(P_SimSetScene, iplSimulatorSetScene); LOAD(P_SimCommit, iplSimulatorCommit);
		LOAD(P_SimSetShared, iplSimulatorSetSharedInputs); LOAD(P_SimRunDirect, iplSimulatorRunDirect);
		LOAD(P_SourceCreate, iplSourceCreate); LOAD(P_SourceRelease, iplSourceRelease);
		LOAD(P_SourceAdd, iplSourceAdd); LOAD(P_SourceRemove, iplSourceRemove);
		LOAD(P_SourceSetInputs, iplSourceSetInputs); LOAD(P_SourceGetOutputs, iplSourceGetOutputs);
		#undef LOAD
		return true;
	}

	// ----------------------------------------------------------------------------------------------------
	// Global state
	// ----------------------------------------------------------------------------------------------------
	std::atomic<bool> g_enabled{ false };
	bool g_available = false;
	IPLContext  g_context = nullptr;
	IPLHRTF     g_hrtf = nullptr;
	IPLScene    g_scene = nullptr;
	IPLStaticMesh g_static_mesh = nullptr;
	IPLSimulator g_simulator = nullptr;
	IPLAudioSettings g_audio{};
	u32 g_frame_size = 1024;
	u32 g_channels = 2;

	std::mutex g_listener_mutex;
	IPLVector3 g_listener_origin{ 0,0,0 };   // RH (z-mirrored)
	IPLVector3 g_listener_ahead{ 0,0,-1 };
	IPLVector3 g_listener_up{ 0,1,0 };

	std::thread g_sim_thread;
	std::atomic<bool> g_sim_run{ false };

	inline IPLVector3 mirror(f32 x, f32 y, f32 z) { return IPLVector3{ x, y, -z }; } // LH game -> RH steam

}	// anonymous

	// ----------------------------------------------------------------------------------------------------
	// Per-voice source (its ma_node MUST be the first member so (source*)pNode == the source).
	// ----------------------------------------------------------------------------------------------------
	struct source
	{
		ma_node_base node_base{};            // FIRST member — node<->source aliasing
		IPLSource        ipl_source = nullptr;
		IPLBinauralEffect binaural = nullptr;
		IPLint32         fx_frames = 0;      // frame size the current binaural effect was created for

		// audio thread reads these; update/sim threads write them (lock-free)
		std::atomic<float> dir_x{ 0.f }, dir_y{ 0.f }, dir_z{ -1.f };  // listener-space unit dir to source
		std::atomic<float> occ_gain{ 1.0f };
		std::atomic<float> px{ 0.f }, py{ 0.f }, pz{ 0.f };            // game-space position
		std::atomic<bool>  occ_enabled{ false };
		bool               added_to_sim = false;

		// deinterleave scratch, sized to g_frame_size
		std::vector<float> in0, in1, out0, out1;
		float* in_ptrs[2]{};
		float* out_ptrs[2]{};
		IPLAudioBuffer in_buf{}, out_buf{};
	};

namespace {
	std::mutex g_sources_mutex;
	std::vector<source*> g_sources;

	// ----------------------------------------------------------------------------------------------------
	// ma_node: HRTF binaural + occlusion gain. Input = engine channels (interleaved), output = 2ch stereo.
	// ----------------------------------------------------------------------------------------------------
	void node_process(ma_node* pNode, const float** ppFramesIn, ma_uint32* pFrameCountIn,
		float** ppFramesOut, ma_uint32* pFrameCountOut)
	{
		source* s = reinterpret_cast<source*>(pNode);
		const ma_uint32 frames = (*pFrameCountIn < *pFrameCountOut) ? *pFrameCountIn : *pFrameCountOut;
		const float* in = ppFramesIn[0];
		float* out = ppFramesOut[0];
		const u32 inCh = g_channels;
		const float occ = s->occ_gain.load(std::memory_order_relaxed);

		// Match the binaural effect to the ACTUAL block size (stable in practice, so this (re)creates once).
		// If anything is unavailable — or the block is larger than our scratch — fall back to a plain passthrough
		// so a Steam Audio hiccup can never drop the voice.
		bool ok = P_BinauralApply && g_hrtf && frames > 0 && frames <= s->in0.size();
		if (ok && (!s->binaural || s->fx_frames != (IPLint32)frames))
		{
			if (s->binaural) P_BinauralRelease(&s->binaural);
			s->binaural = nullptr;
			IPLAudioSettings as = g_audio; as.frameSize = (IPLint32)frames;
			IPLBinauralEffectSettings bs{}; bs.hrtf = g_hrtf;
			if (P_BinauralCreate(g_context, &as, &bs, &s->binaural) == IPL_STATUS_SUCCESS && s->binaural)
				s->fx_frames = (IPLint32)frames;
			else { s->binaural = nullptr; s->fx_frames = 0; }
		}
		if (!ok || !s->binaural)
		{
			for (ma_uint32 i = 0; i < frames; ++i)
			{
				float l = in[i * inCh];
				float r = inCh > 1 ? in[i * inCh + 1] : l;
				out[i * 2] = l * occ; out[i * 2 + 1] = r * occ;   // still honour occlusion in passthrough
			}
			*pFrameCountOut = frames;
			return;
		}

		// Deinterleave input -> Steam Audio's per-channel buffers, spatialize, then interleave the stereo result.
		for (ma_uint32 i = 0; i < frames; ++i)
		{
			float l = in[i * inCh];
			s->in0[i] = l;
			s->in1[i] = inCh > 1 ? in[i * inCh + 1] : l;
		}
		IPLBinauralEffectParams p{};
		p.direction = IPLVector3{ s->dir_x.load(std::memory_order_relaxed),
								  s->dir_y.load(std::memory_order_relaxed),
								  s->dir_z.load(std::memory_order_relaxed) };
		p.interpolation = IPL_HRTFINTERPOLATION_NEAREST;
		p.spatialBlend = 1.0f;
		p.hrtf = g_hrtf;
		p.peakDelays = nullptr;
		s->in_buf.numChannels = (IPLint32)(inCh > 1 ? 2 : 1);
		s->in_buf.numSamples = (IPLint32)frames;
		s->out_buf.numChannels = 2;
		s->out_buf.numSamples = (IPLint32)frames;
		P_BinauralApply(s->binaural, &p, &s->in_buf, &s->out_buf);
		for (ma_uint32 i = 0; i < frames; ++i)
		{
			out[i * 2] = s->out0[i] * occ;
			out[i * 2 + 1] = s->out1[i] * occ;
		}
		*pFrameCountOut = frames;
	}

	ma_node_vtable g_node_vtable = { node_process, nullptr, 1, 1, 0 };

	// ----------------------------------------------------------------------------------------------------
	// Simulation thread: occlusion rays for every active occluded source, off the audio thread.
	// ----------------------------------------------------------------------------------------------------
	void sim_loop()
	{
		using namespace std::chrono_literals;
		while (g_sim_run.load(std::memory_order_relaxed))
		{
			if (g_simulator && g_scene)
			{
				IPLVector3 lo, la, lu;
				{
					std::lock_guard<std::mutex> lk(g_listener_mutex);
					lo = g_listener_origin; la = g_listener_ahead; lu = g_listener_up;
				}
				IPLSimulationSharedInputs shared{};
				shared.listener.origin = lo;
				shared.listener.ahead = la;
				shared.listener.up = lu;
				shared.listener.right = IPLVector3{ la.z * lu.y - la.y * lu.z, la.x * lu.z - la.z * lu.x, la.y * lu.x - la.x * lu.y };
				P_SimSetShared(g_simulator, IPL_SIMULATIONFLAGS_DIRECT, &shared);

				std::vector<source*> active;
				{
					std::lock_guard<std::mutex> lk(g_sources_mutex);
					active = g_sources;
				}
				bool any = false;
				for (source* s : active)
				{
					if (!s->ipl_source || !s->occ_enabled.load(std::memory_order_relaxed)) continue;
					IPLSimulationInputs in{};
					in.flags = IPL_SIMULATIONFLAGS_DIRECT;
					in.directFlags = (IPLDirectSimulationFlags)(IPL_DIRECTSIMULATIONFLAGS_OCCLUSION);
					in.source.origin = mirror(s->px.load(), s->py.load(), s->pz.load());
					in.source.ahead = IPLVector3{ 0,0,-1 };
					in.source.up = IPLVector3{ 0,1,0 };
					in.source.right = IPLVector3{ 1,0,0 };
					in.occlusionType = IPL_OCCLUSIONTYPE_RAYCAST;
					P_SourceSetInputs(s->ipl_source, IPL_SIMULATIONFLAGS_DIRECT, &in);
					any = true;
				}
				if (any)
				{
					P_SimRunDirect(g_simulator);
					for (source* s : active)
					{
						if (!s->ipl_source || !s->occ_enabled.load(std::memory_order_relaxed)) continue;
						IPLSimulationOutputs out{};
						P_SourceGetOutputs(s->ipl_source, IPL_SIMULATIONFLAGS_DIRECT, &out);
						// occlusion 1 = clear, 0 = fully blocked. Floor at 0.08 so blocked is "muffled" not dead.
						float g = out.direct.occlusion;
						if (g < 0.08f) g = 0.08f; else if (g > 1.0f) g = 1.0f;
						s->occ_gain.store(g, std::memory_order_relaxed);
					}
				}
			}
			std::this_thread::sleep_for(33ms);   // ~30 Hz, decoupled from audio + render
		}
	}

}	// anonymous

	// ----------------------------------------------------------------------------------------------------
	// Public API
	// ----------------------------------------------------------------------------------------------------
	void set_enabled(bool enabled) { g_enabled.store(enabled, std::memory_order_relaxed); }
	bool is_enabled() { return g_enabled.load(std::memory_order_relaxed); }
	bool is_available() { return g_available; }

	bool initialize(u32 sample_rate, u32 frame_size)
	{
		if (g_available) return true;
		if (!g_enabled.load(std::memory_order_relaxed)) return false;   // opt-in only
		if (!load_dll()) { internal_log("[SteamAudio] phonon.dll not found — HRTF/occlusion disabled, using v1."); return false; }

		g_frame_size = frame_size ? frame_size : 1024;
		g_channels = 2;
		g_audio.samplingRate = (IPLint32)(sample_rate ? sample_rate : 48000);
		g_audio.frameSize = (IPLint32)g_frame_size;

		IPLContextSettings cs{};
		cs.version = STEAMAUDIO_VERSION;
		if (P_ContextCreate(&cs, &g_context) != IPL_STATUS_SUCCESS || !g_context)
		{ internal_log("[SteamAudio] iplContextCreate failed — using v1."); shutdown(); return false; }

		IPLHRTFSettings hs{};
		hs.type = IPL_HRTFTYPE_DEFAULT;
		hs.volume = 1.0f;
		hs.normType = IPL_HRTFNORMTYPE_NONE;
		if (P_HRTFCreate(g_context, &g_audio, &hs, &g_hrtf) != IPL_STATUS_SUCCESS || !g_hrtf)
		{ internal_log("[SteamAudio] iplHRTFCreate failed — using v1."); shutdown(); return false; }

		// Empty scene up front (geometry published later per scene). Default built-in ray tracer.
		IPLSceneSettings ss{};
		ss.type = IPL_SCENETYPE_DEFAULT;
		if (P_SceneCreate(g_context, &ss, &g_scene) != IPL_STATUS_SUCCESS || !g_scene)
		{ internal_log("[SteamAudio] iplSceneCreate failed — HRTF only, no occlusion."); }
		else P_SceneCommit(g_scene);

		IPLSimulationSettings sim{};
		sim.flags = IPL_SIMULATIONFLAGS_DIRECT;
		sim.sceneType = IPL_SCENETYPE_DEFAULT;
		sim.maxNumOcclusionSamples = 16;
		sim.samplingRate = g_audio.samplingRate;
		sim.frameSize = g_audio.frameSize;
		if (g_scene && P_SimCreate(g_context, &sim, &g_simulator) == IPL_STATUS_SUCCESS && g_simulator)
		{
			P_SimSetScene(g_simulator, g_scene);
			P_SimCommit(g_simulator);
			g_sim_run.store(true, std::memory_order_relaxed);
			g_sim_thread = std::thread(sim_loop);
		}

		g_available = true;
		internal_log("[SteamAudio] initialized: HRTF binaural%s (Steam Audio v4.8.1).", g_simulator ? " + occlusion" : "");
		return true;
	}

	void set_geometry(const float* verts, u32 vertex_count, const s32* indices, u32 index_count)
	{
		if (!g_available || !g_scene || !P_MeshCreate || vertex_count == 0 || index_count < 3) return;
		// Rebuild: drop the old static mesh, build a new one from the triangle soup.
		if (g_static_mesh) { P_MeshRelease(&g_static_mesh); g_static_mesh = nullptr; }

		std::vector<IPLVector3> v(vertex_count);
		for (u32 i = 0; i < vertex_count; ++i) v[i] = mirror(verts[i * 3], verts[i * 3 + 1], verts[i * 3 + 2]);
		const u32 tri_count = index_count / 3;
		std::vector<IPLTriangle> tris(tri_count);
		std::vector<IPLint32> matIdx(tri_count, 0);
		for (u32 t = 0; t < tri_count; ++t)
		{
			tris[t].indices[0] = (IPLint32)indices[t * 3];
			tris[t].indices[1] = (IPLint32)indices[t * 3 + 1];
			tris[t].indices[2] = (IPLint32)indices[t * 3 + 2];
		}
		IPLMaterial mat{};   // "generic" acoustic material
		mat.absorption[0] = 0.10f; mat.absorption[1] = 0.20f; mat.absorption[2] = 0.30f;
		mat.scattering = 0.05f;
		mat.transmission[0] = 0.100f; mat.transmission[1] = 0.050f; mat.transmission[2] = 0.030f;

		IPLStaticMeshSettings ms{};
		ms.numVertices = (IPLint32)vertex_count;
		ms.numTriangles = (IPLint32)tri_count;
		ms.numMaterials = 1;
		ms.vertices = v.data();
		ms.triangles = tris.data();
		ms.materialIndices = matIdx.data();
		ms.materials = &mat;
		if (P_MeshCreate(g_scene, &ms, &g_static_mesh) == IPL_STATUS_SUCCESS && g_static_mesh)
		{
			P_MeshAdd(g_static_mesh, g_scene);
			P_SceneCommit(g_scene);
			if (g_simulator) P_SimCommit(g_simulator);
			internal_log("[SteamAudio] occlusion geometry: %u verts / %u tris.", vertex_count, tri_count);
		}
	}

	void set_listener(f32 px, f32 py, f32 pz, f32 fx, f32 fy, f32 fz, f32 ux, f32 uy, f32 uz)
	{
		if (!g_available) return;
		std::lock_guard<std::mutex> lk(g_listener_mutex);
		g_listener_origin = mirror(px, py, pz);
		g_listener_ahead = mirror(fx, fy, fz);
		g_listener_up = mirror(ux, uy, uz);
	}

	source* create_source()
	{
		if (!g_available || !P_BinauralCreate) return nullptr;
		ma_engine* engine = internal_engine();
		if (!engine) return nullptr;

		source* s = new source();
		// Scratch sized to a generous max block; node_process guards against anything larger (passthrough) and
		// (re)creates the binaural effect to match the real block size on the audio thread (once, size is stable).
		const size_t kMaxBlock = 8192;
		s->in0.resize(kMaxBlock); s->in1.resize(kMaxBlock);
		s->out0.resize(kMaxBlock); s->out1.resize(kMaxBlock);
		s->in_ptrs[0] = s->in0.data(); s->in_ptrs[1] = s->in1.data();
		s->out_ptrs[0] = s->out0.data(); s->out_ptrs[1] = s->out1.data();
		s->in_buf.data = s->in_ptrs; s->out_buf.data = s->out_ptrs;

		// the ma_node: input = engine channels, output = stereo
		ma_node_config cfg = ma_node_config_init();
		cfg.vtable = &g_node_vtable;
		g_channels = ma_engine_get_channels(engine);
		ma_uint32 inCh = g_channels, outCh = 2;
		cfg.pInputChannels = &inCh;
		cfg.pOutputChannels = &outCh;
		if (ma_node_init(ma_engine_get_node_graph(engine), &cfg, nullptr, &s->node_base) != MA_SUCCESS)
		{
			if (s->binaural) P_BinauralRelease(&s->binaural);
			delete s;
			return nullptr;
		}

		if (g_simulator && P_SourceCreate)
		{
			IPLSourceSettings srs{}; srs.flags = IPL_SIMULATIONFLAGS_DIRECT;
			if (P_SourceCreate(g_simulator, &srs, &s->ipl_source) == IPL_STATUS_SUCCESS && s->ipl_source)
			{
				P_SourceAdd(s->ipl_source, g_simulator);
				P_SimCommit(g_simulator);
				s->added_to_sim = true;
			}
		}
		{
			std::lock_guard<std::mutex> lk(g_sources_mutex);
			g_sources.push_back(s);
		}
		return s;
	}

	void destroy_source(source* s)
	{
		if (!s) return;
		{
			std::lock_guard<std::mutex> lk(g_sources_mutex);
			g_sources.erase(std::remove(g_sources.begin(), g_sources.end(), s), g_sources.end());
		}
		ma_node_uninit(&s->node_base, nullptr);
		if (s->ipl_source)
		{
			if (s->added_to_sim && g_simulator) { P_SourceRemove(s->ipl_source, g_simulator); P_SimCommit(g_simulator); }
			P_SourceRelease(&s->ipl_source);
		}
		if (s->binaural) P_BinauralRelease(&s->binaural);
		delete s;
	}

	ma_node* node_of(source* s) { return s ? (ma_node*)&s->node_base : nullptr; }

	void set_source(source* s, f32 px, f32 py, f32 pz, bool occlusion)
	{
		if (!s || !g_available) return;
		s->px.store(px, std::memory_order_relaxed);
		s->py.store(py, std::memory_order_relaxed);
		s->pz.store(pz, std::memory_order_relaxed);
		s->occ_enabled.store(occlusion && s->ipl_source != nullptr, std::memory_order_relaxed);
		if (!occlusion || !s->ipl_source) s->occ_gain.store(1.0f, std::memory_order_relaxed);

		// binaural direction: listener-space unit vector to the source (RH), computed by the SDK.
		if (P_RelDir && g_context)
		{
			IPLVector3 lo, la, lu;
			{ std::lock_guard<std::mutex> lk(g_listener_mutex); lo = g_listener_origin; la = g_listener_ahead; lu = g_listener_up; }
			IPLVector3 dir = P_RelDir(g_context, mirror(px, py, pz), lo, la, lu);
			s->dir_x.store(dir.x, std::memory_order_relaxed);
			s->dir_y.store(dir.y, std::memory_order_relaxed);
			s->dir_z.store(dir.z, std::memory_order_relaxed);
		}
	}

	void shutdown()
	{
		if (g_sim_run.exchange(false)) { if (g_sim_thread.joinable()) g_sim_thread.join(); }
		{
			std::lock_guard<std::mutex> lk(g_sources_mutex);
			g_sources.clear();   // voices own their sources; they're destroyed via destroy_source before this
		}
		if (g_static_mesh && P_MeshRelease) P_MeshRelease(&g_static_mesh);
		if (g_simulator && P_SimRelease) P_SimRelease(&g_simulator);
		if (g_scene && P_SceneRelease) P_SceneRelease(&g_scene);
		if (g_hrtf && P_HRTFRelease) P_HRTFRelease(&g_hrtf);
		if (g_context && P_ContextRelease) P_ContextRelease(&g_context);
		g_static_mesh = nullptr; g_simulator = nullptr; g_scene = nullptr; g_hrtf = nullptr; g_context = nullptr;
		g_available = false;
	}

}	// namespace vortex::runtime::audio::steam
