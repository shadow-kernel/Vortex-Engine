#include "DX12Streamline.h"

#include <windows.h>
#include <cstdio>
#include <cstdlib>

// NVIDIA Streamline headers (vendored, source/headers only). We CONSUME them via GetProcAddress, so nothing is
// linked — we only need the types (Preferences/AdapterInfo/Result/Feature) and the PFun_* fn-pointer typedefs.
#include "sl.h"
#include "sl_consts.h"
#include "sl_dlss.h"
#include "sl_dlss_g.h"           // Frame Generation
#include "sl_reflex.h"           // Reflex (required by DLSS-G)
#include "sl_pcl.h"              // PCL latency markers
#include "sl_matrix_helpers.h"   // recalculateCameraMatrices (computes clipToPrevClip etc.)

namespace vortex::graphics::dx12
{
	namespace
	{
		// Resolved entry points (null until init()).
		PFun_slInit*               g_slInit{};
		PFun_slShutdown*           g_slShutdown{};
		PFun_slIsFeatureSupported* g_slIsFeatureSupported{};
		PFun_slSetD3DDevice*       g_slSetD3DDevice{};
		PFun_slGetNewFrameToken*   g_slGetNewFrameToken{};
		PFun_slSetConstants*       g_slSetConstants{};
		PFun_slSetTag*             g_slSetTag{};
		PFun_slEvaluateFeature*    g_slEvaluateFeature{};
		PFun_slGetFeatureFunction* g_slGetFeatureFunction{};
		// DLSS feature functions (resolved via slGetFeatureFunction after the device is set).
		PFun_slDLSSSetOptions*        g_slDLSSSetOptions{};
		PFun_slDLSSGetOptimalSettings* g_slDLSSGetOptimalSettings{};
		// Frame Generation + Reflex + PCL feature functions (also via slGetFeatureFunction).
		PFun_slReflexSetOptions*  g_slReflexSetOptions{};
		PFun_slReflexSleep*       g_slReflexSleep{};
		PFun_slPCLSetMarker*      g_slPCLSetMarker{};
		PFun_slDLSSGSetOptions*   g_slDLSSGSetOptions{};
		PFun_slDLSSGGetState*     g_slDLSSGGetState{};

		// XMFLOAT4X4 (row-major) -> sl::float4x4 (row-major).
		sl::float4x4 toSL(const DirectX::XMFLOAT4X4& m)
		{
			sl::float4x4 r;
			r[0] = sl::float4(m._11, m._12, m._13, m._14);
			r[1] = sl::float4(m._21, m._22, m._23, m._24);
			r[2] = sl::float4(m._31, m._32, m._33, m._34);
			r[3] = sl::float4(m._41, m._42, m._43, m._44);
			return r;
		}

		// A custom-engine project id (GUID). For a registered title NVIDIA assigns an applicationId instead; the
		// projectId path is the supported route for custom engines during development.
		const char* kProjectId = "c7e8f9a0-1b2c-4d3e-8f50-a1b2c3d4e5f6";

		void sl_log(const char* line)
		{
			char path[MAX_PATH]{};
			DWORD n = GetTempPathA(MAX_PATH, path);
			if (n == 0 || n > MAX_PATH) return;
			strncat_s(path, "vortex_streamline.log", _TRUNCATE);
			FILE* f = nullptr;
			if (fopen_s(&f, path, "a") == 0 && f)
			{
				fputs(line, f);
				fputc('\n', f);
				fclose(f);
			}
			OutputDebugStringA(line);
			OutputDebugStringA("\n");
		}

		// SL routes its own warnings/errors here — invaluable for diagnosing why support/init failed.
		void sl_message_cb(sl::LogType type, const char* msg)
		{
			char buf[1024];
			_snprintf_s(buf, _TRUNCATE, "[sl:%d] %s", (int)type, msg ? msg : "");
			sl_log(buf);
		}
	}

	DX12Streamline& DX12Streamline::instance()
	{
		static DX12Streamline inst;
		return inst;
	}

	bool DX12Streamline::init()
	{
		if (m_available) return true;

		// sl.interposer.dll is expected next to the exe (we copy the SL DLLs there post-build). If it's not there
		// this is a non-Streamline machine/build -> stay disabled, the renderer uses the render-scale upscale.
		HMODULE mod = LoadLibraryW(L"sl.interposer.dll");
		if (!mod)
		{
			sl_log("[streamline] sl.interposer.dll not found next to exe -> DLSS disabled (render-scale fallback)");
			return false;
		}
		m_module = mod;

		g_slInit               = reinterpret_cast<PFun_slInit*>(GetProcAddress(mod, "slInit"));
		g_slShutdown           = reinterpret_cast<PFun_slShutdown*>(GetProcAddress(mod, "slShutdown"));
		g_slIsFeatureSupported = reinterpret_cast<PFun_slIsFeatureSupported*>(GetProcAddress(mod, "slIsFeatureSupported"));
		g_slSetD3DDevice       = reinterpret_cast<PFun_slSetD3DDevice*>(GetProcAddress(mod, "slSetD3DDevice"));
		g_slGetNewFrameToken   = reinterpret_cast<PFun_slGetNewFrameToken*>(GetProcAddress(mod, "slGetNewFrameToken"));
		g_slSetConstants       = reinterpret_cast<PFun_slSetConstants*>(GetProcAddress(mod, "slSetConstants"));
		g_slSetTag             = reinterpret_cast<PFun_slSetTag*>(GetProcAddress(mod, "slSetTag"));
		g_slEvaluateFeature    = reinterpret_cast<PFun_slEvaluateFeature*>(GetProcAddress(mod, "slEvaluateFeature"));
		g_slGetFeatureFunction = reinterpret_cast<PFun_slGetFeatureFunction*>(GetProcAddress(mod, "slGetFeatureFunction"));
		if (!g_slInit || !g_slShutdown || !g_slIsFeatureSupported || !g_slSetD3DDevice ||
			!g_slGetNewFrameToken || !g_slSetConstants || !g_slSetTag || !g_slEvaluateFeature || !g_slGetFeatureFunction)
		{
			sl_log("[streamline] sl.interposer.dll loaded but entry points missing -> DLSS disabled");
			FreeLibrary(mod); m_module = nullptr;
			return false;
		}

		sl::Preferences pref{};
		pref.showConsole = false;
		pref.logLevel = sl::LogLevel::eDefault;
		pref.logMessageCallback = sl_message_cb;
		pref.engine = sl::EngineType::eCustom;
		pref.projectId = kProjectId;
		pref.renderAPI = sl::RenderAPI::eD3D12;
		// Only load what we use; deterministic + offline (no OTA plugin download). DLSS-G needs Reflex + PCL too.
		static const sl::Feature s_features[] = { sl::kFeatureDLSS, sl::kFeatureReflex, sl::kFeaturePCL, sl::kFeatureDLSS_G };
		pref.featuresToLoad = s_features;
		pref.numFeaturesToLoad = (uint32_t)(sizeof(s_features) / sizeof(s_features[0]));
		pref.flags = sl::PreferenceFlags::eDisableCLStateTracking;

		sl::Result res = g_slInit(pref, sl::kSDKVersion);
		if (res != sl::Result::eOk)
		{
			char buf[256];
			_snprintf_s(buf, _TRUNCATE, "[streamline] slInit FAILED (result=%d) -> DLSS disabled", (int)res);
			sl_log(buf);
			FreeLibrary(mod); m_module = nullptr;
			g_slInit = nullptr; g_slShutdown = nullptr; g_slIsFeatureSupported = nullptr; g_slSetD3DDevice = nullptr;
			return false;
		}

		m_available = true;
		sl_log("[streamline] slInit OK (DLSS plugin requested)");
		return true;
	}

	bool DX12Streamline::is_dlss_supported(const LUID& luid)
	{
		if (!m_available || !g_slIsFeatureSupported) return false;

		sl::AdapterInfo info{};
		info.deviceLUID = (uint8_t*)&luid;
		info.deviceLUIDSizeInBytes = sizeof(LUID);

		sl::Result res = g_slIsFeatureSupported(sl::kFeatureDLSS, info);
		char buf[256];
		_snprintf_s(buf, _TRUNCATE, "[streamline] slIsFeatureSupported(DLSS) result=%d -> %s",
			(int)res, res == sl::Result::eOk ? "SUPPORTED" : "not supported");
		sl_log(buf);
		return res == sl::Result::eOk;
	}

	void DX12Streamline::set_device(ID3D12Device* device)
	{
		if (!m_available || !g_slSetD3DDevice || !device) return;
		sl::Result res = g_slSetD3DDevice(device);
		char buf[128];
		_snprintf_s(buf, _TRUNCATE, "[streamline] slSetD3DDevice result=%d", (int)res);
		sl_log(buf);

		// DLSS feature functions can only be fetched AFTER the device is set (and the plugin has started).
		if (g_slGetFeatureFunction)
		{
			g_slGetFeatureFunction(sl::kFeatureDLSS, "slDLSSSetOptions", (void*&)g_slDLSSSetOptions);
			g_slGetFeatureFunction(sl::kFeatureDLSS, "slDLSSGetOptimalSettings", (void*&)g_slDLSSGetOptimalSettings);
			m_dlss_ready = (g_slDLSSSetOptions != nullptr && g_slDLSSGetOptimalSettings != nullptr);
			_snprintf_s(buf, _TRUNCATE, "[streamline] DLSS functions resolved -> dlss_ready=%d", m_dlss_ready ? 1 : 0);
			sl_log(buf);

			// Frame-Generation stack: Reflex + PCL markers + DLSS-G.
			g_slGetFeatureFunction(sl::kFeatureReflex, "slReflexSetOptions", (void*&)g_slReflexSetOptions);
			g_slGetFeatureFunction(sl::kFeatureReflex, "slReflexSleep", (void*&)g_slReflexSleep);
			g_slGetFeatureFunction(sl::kFeaturePCL, "slPCLSetMarker", (void*&)g_slPCLSetMarker);
			g_slGetFeatureFunction(sl::kFeatureDLSS_G, "slDLSSGSetOptions", (void*&)g_slDLSSGSetOptions);
			g_slGetFeatureFunction(sl::kFeatureDLSS_G, "slDLSSGGetState", (void*&)g_slDLSSGGetState);
			m_fg_ready = (g_slReflexSetOptions && g_slReflexSleep && g_slPCLSetMarker && g_slDLSSGSetOptions && g_slDLSSGGetState);
			_snprintf_s(buf, _TRUNCATE, "[streamline] FrameGen functions resolved -> fg_ready=%d", m_fg_ready ? 1 : 0);
			sl_log(buf);
		}
	}

	void DX12Streamline::set_reflex(bool enabled)
	{
		if (!m_fg_ready || !g_slReflexSetOptions) return;
		sl::ReflexOptions opts{};
		opts.mode = enabled ? sl::ReflexMode::eLowLatency : sl::ReflexMode::eOff;
		g_slReflexSetOptions(opts);
	}

	void* DX12Streamline::new_frame_token(unsigned frame_index)
	{
		if (!m_available || !g_slGetNewFrameToken) return nullptr;
		sl::FrameToken* token = nullptr;
		uint32_t fi = frame_index;
		if (g_slGetNewFrameToken(token, &fi) != sl::Result::eOk) return nullptr;
		return token;
	}

	void DX12Streamline::reflex_sleep(void* frame_token)
	{
		if (!m_fg_ready || !g_slReflexSleep || !frame_token) return;
		g_slReflexSleep(*reinterpret_cast<sl::FrameToken*>(frame_token));
	}

	void DX12Streamline::pcl_marker(int marker, void* frame_token)
	{
		if (!m_fg_ready || !g_slPCLSetMarker || !frame_token) return;
		g_slPCLSetMarker((sl::PCLMarker)marker, *reinterpret_cast<sl::FrameToken*>(frame_token));
	}

	void DX12Streamline::set_frame_gen(int num_frames_to_generate, unsigned out_w, unsigned out_h)
	{
		if (!m_fg_ready || !g_slDLSSGSetOptions) return;
		sl::ViewportHandle viewport(0);
		sl::DLSSGOptions opts{};
		if (num_frames_to_generate <= 0)
		{
			opts.mode = sl::DLSSGMode::eOff;
		}
		else
		{
			opts.mode = sl::DLSSGMode::eOn;
			opts.numFramesToGenerate = (uint32_t)num_frames_to_generate; // 1=x2, 2=x3, 3=x4
			opts.colorWidth = out_w; opts.colorHeight = out_h;
		}
		sl::Result res = g_slDLSSGSetOptions(viewport, opts);
		char b[160]; _snprintf_s(b, _TRUNCATE, "[streamline] slDLSSGSetOptions mode=%d numGen=%d result=%d",
			(int)opts.mode, num_frames_to_generate, (int)res);
		sl_log(b);
	}

	int DX12Streamline::fg_presented_frames()
	{
		if (!m_fg_ready || !g_slDLSSGGetState) return 0;
		sl::ViewportHandle viewport(0);
		sl::DLSSGState state{};
		if (g_slDLSSGGetState(viewport, state, nullptr) != sl::Result::eOk) return 0;
		return (int)state.numFramesActuallyPresented;
	}

	bool DX12Streamline::evaluate_dlss(const DlssEvalDesc& d)
	{
		if (!m_dlss_ready || !d.cmd || d.renderW == 0 || d.renderH == 0) return false;

		sl::ViewportHandle viewport(0);

		// Per-frame token.
		sl::FrameToken* token = nullptr;
		uint32_t fi = m_frame_index++;
		if (g_slGetNewFrameToken(token, &fi) != sl::Result::eOk || !token) return false;

		// DLSS mode + output size.
		sl::DLSSOptions opts{};
		opts.mode = (d.mode == 1) ? sl::DLSSMode::eMaxQuality
				  : (d.mode == 2) ? sl::DLSSMode::eBalanced
				  : (d.mode == 3) ? sl::DLSSMode::eMaxPerformance
								  : sl::DLSSMode::eUltraPerformance;
		opts.outputWidth = d.outW;
		opts.outputHeight = d.outH;
		opts.colorBuffersHDR = sl::Boolean::eFalse;     // scene color is R8G8B8A8 SDR
		opts.useAutoExposure = sl::Boolean::eTrue;      // no exposure buffer provided
		if (g_slDLSSSetOptions(viewport, opts) != sl::Result::eOk) return false;

		// Common constants. Matrices are row-major, jitter-free; recalculateCameraMatrices fills the derived ones.
		sl::Constants c{};
		c.cameraViewToClip = toSL(d.proj);
		c.cameraPos   = sl::float3(d.camPos.x,   d.camPos.y,   d.camPos.z);
		c.cameraRight = sl::float3(d.camRight.x, d.camRight.y, d.camRight.z);
		c.cameraFwd   = sl::float3(d.camFwd.x,   d.camFwd.y,   d.camFwd.z);
		c.cameraUp    = sl::float3(0.0f, 1.0f, 0.0f);   // recomputed by recalculateCameraMatrices
		c.jitterOffset = sl::float2(0.0f, 0.0f);         // no jitter yet (quality refinement)
		c.mvecScale    = sl::float2(1.0f / (float)d.renderW, 1.0f / (float)d.renderH); // pixel-space mvecs -> NDC
		c.cameraPinholeOffset = sl::float2(0.0f, 0.0f);
		c.cameraNear = d.nearZ;
		c.cameraFar  = d.farZ;
		c.cameraFOV  = d.fovY;
		c.cameraAspectRatio = (float)d.renderW / (float)d.renderH;
		c.depthInverted = sl::Boolean::eFalse;
		c.cameraMotionIncluded = sl::Boolean::eTrue;     // our mvec buffer already includes camera motion
		c.motionVectors3D = sl::Boolean::eFalse;
		c.reset = sl::Boolean::eFalse;
		c.orthographicProjection = sl::Boolean::eFalse;
		c.motionVectorsDilated = sl::Boolean::eFalse;
		c.motionVectorsJittered = sl::Boolean::eFalse;
		sl::recalculateCameraMatrices(c);                // clipToCameraView / clipToPrevClip / prevClipToClip
		if (g_slSetConstants(c, *token, viewport) != sl::Result::eOk) return false;

		// Tag the four required resources (SL manages their states from the provided current state).
		sl::Resource rIn(sl::ResourceType::eTex2d, d.colorIn, d.colorInState);
		rIn.width = d.renderW; rIn.height = d.renderH; rIn.nativeFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
		sl::Resource rOut(sl::ResourceType::eTex2d, d.colorOut, d.colorOutState);
		rOut.width = d.outW; rOut.height = d.outH; rOut.nativeFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
		sl::Resource rDepth(sl::ResourceType::eTex2d, d.depth, d.depthState);
		rDepth.width = d.renderW; rDepth.height = d.renderH; rDepth.nativeFormat = DXGI_FORMAT_R32_FLOAT;
		sl::Resource rMvec(sl::ResourceType::eTex2d, d.mvec, d.mvecState);
		rMvec.width = d.renderW; rMvec.height = d.renderH; rMvec.nativeFormat = DXGI_FORMAT_R16G16_FLOAT;

		sl::Extent eRender{ 0, 0, d.renderW, d.renderH };
		sl::Extent eOut{ 0, 0, d.outW, d.outH };
		sl::ResourceTag tags[4] = {
			sl::ResourceTag(&rIn,    sl::kBufferTypeScalingInputColor,  sl::ResourceLifecycle::eValidUntilPresent, &eRender),
			sl::ResourceTag(&rOut,   sl::kBufferTypeScalingOutputColor, sl::ResourceLifecycle::eValidUntilPresent, &eOut),
			sl::ResourceTag(&rDepth, sl::kBufferTypeDepth,              sl::ResourceLifecycle::eValidUntilPresent, &eRender),
			sl::ResourceTag(&rMvec,  sl::kBufferTypeMotionVectors,      sl::ResourceLifecycle::eValidUntilPresent, &eRender),
		};
		if (g_slSetTag(viewport, tags, 4, (sl::CommandBuffer*)d.cmd) != sl::Result::eOk) return false;

		// Inject DLSS into the command list.
		const sl::BaseStructure* inputs[] = { &viewport };
		sl::Result ev = g_slEvaluateFeature(sl::kFeatureDLSS, *token, inputs, 1, (sl::CommandBuffer*)d.cmd);
		if (ev != sl::Result::eOk)
		{
			char b[128]; _snprintf_s(b, _TRUNCATE, "[streamline] slEvaluateFeature(DLSS) failed result=%d -> bilinear fallback", (int)ev);
			sl_log(b);
			return false;
		}
		return true;
	}

	void DX12Streamline::shutdown()
	{
		if (m_available && g_slShutdown) g_slShutdown();
		if (m_module) { FreeLibrary((HMODULE)m_module); m_module = nullptr; }
		g_slInit = nullptr; g_slShutdown = nullptr; g_slIsFeatureSupported = nullptr; g_slSetD3DDevice = nullptr;
		m_available = false;
	}
}
