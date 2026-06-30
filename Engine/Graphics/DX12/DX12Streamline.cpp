#include "DX12Streamline.h"

#include <windows.h>
#include <cstdio>
#include <cstdlib>

// NVIDIA Streamline headers (vendored, source/headers only). We CONSUME them via GetProcAddress, so nothing is
// linked — we only need the types (Preferences/AdapterInfo/Result/Feature) and the PFun_* fn-pointer typedefs.
#include "sl.h"
#include "sl_consts.h"
#include "sl_dlss.h"

namespace vortex::graphics::dx12
{
	namespace
	{
		// Resolved entry points (null until init()).
		PFun_slInit*               g_slInit{};
		PFun_slShutdown*           g_slShutdown{};
		PFun_slIsFeatureSupported* g_slIsFeatureSupported{};
		PFun_slSetD3DDevice*       g_slSetD3DDevice{};

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
		if (!g_slInit || !g_slShutdown || !g_slIsFeatureSupported || !g_slSetD3DDevice)
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
		// Only load what we use; deterministic + offline (no OTA plugin download).
		static const sl::Feature s_features[] = { sl::kFeatureDLSS };
		pref.featuresToLoad = s_features;
		pref.numFeaturesToLoad = 1;
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
	}

	void DX12Streamline::shutdown()
	{
		if (m_available && g_slShutdown) g_slShutdown();
		if (m_module) { FreeLibrary((HMODULE)m_module); m_module = nullptr; }
		g_slInit = nullptr; g_slShutdown = nullptr; g_slIsFeatureSupported = nullptr; g_slSetD3DDevice = nullptr;
		m_available = false;
	}
}
