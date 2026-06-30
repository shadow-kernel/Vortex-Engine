#include "DX12Core.h"
#include "DX12Streamline.h"

namespace vortex::graphics::dx12
{
	namespace
	{
		using PFN_CreateDXGIFactory2 = HRESULT(WINAPI*)(UINT, REFIID, void**);
		using PFN_D3D12CreateDevice = HRESULT(WINAPI*)(IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);

		// When Streamline is up, resolve the DXGI/D3D12 entry points from sl.interposer.dll (which exports proxy
		// versions) instead of the real DLLs — so the factory, device, swapchain and command lists are SL proxies
		// that slEvaluateFeature(DLSS) can hook. The proxies pass straight through to the real APIs when no SL
		// feature is active, so this is transparent. Falls back to the real dxgi.dll/d3d12.dll otherwise.
		HMODULE dxgi_provider()
		{
			HMODULE sl = reinterpret_cast<HMODULE>(DX12Streamline::instance().interposer_module());
			return sl ? sl : LoadLibraryW(L"dxgi.dll");
		}
		HMODULE d3d12_provider()
		{
			HMODULE sl = reinterpret_cast<HMODULE>(DX12Streamline::instance().interposer_module());
			return sl ? sl : LoadLibraryW(L"d3d12.dll");
		}

		PFN_CreateDXGIFactory2 get_create_dxgi_factory()
		{
			static auto fn = reinterpret_cast<PFN_CreateDXGIFactory2>(
				GetProcAddress(dxgi_provider(), "CreateDXGIFactory2"));
			return fn;
		}

		PFN_D3D12CreateDevice get_d3d12_create_device()
		{
			static auto fn = reinterpret_cast<PFN_D3D12CreateDevice>(
				GetProcAddress(d3d12_provider(), "D3D12CreateDevice"));
			return fn;
		}
	}

	DX12Core& DX12Core::instance()
	{
		static DX12Core inst;
		return inst;
	}

	bool DX12Core::initialize()
	{
		if (m_initialized) return true;

		// Bring up Streamline (DLSS) BEFORE any dxgi/d3d12 device creation, as its docs require. Optional +
		// self-contained: if the SL DLLs aren't present or init fails it just stays disabled and we fall back to
		// the render-scale upscale. This does NOT route device creation through the interposer (no pixel impact);
		// it only enables the real slIsFeatureSupported gate below + registers the device for later DLSS eval.
		DX12Streamline::instance().init();

		if (!create_factory()) return false;
		if (!create_device()) return false;

		m_initialized = true;
		return true;
	}

	void DX12Core::shutdown()
	{
		m_device.Reset();
		m_factory.Reset();
		m_initialized = false;
	}

	bool DX12Core::create_factory()
	{
		auto fn = get_create_dxgi_factory();
		if (!fn) return false;

		UINT flags = 0;
#ifdef _DEBUG
		flags = DXGI_CREATE_FACTORY_DEBUG;
#endif
		return SUCCEEDED(fn(flags, IID_PPV_ARGS(&m_factory)));
	}

	bool DX12Core::create_device()
	{
		auto fn = get_d3d12_create_device();
		if (!fn) return false;

		ComPtr<IDXGIAdapter1> adapter;
		for (UINT i = 0; SUCCEEDED(m_factory->EnumAdapters1(i, &adapter)); ++i)
		{
			DXGI_ADAPTER_DESC1 desc;
			adapter->GetDesc1(&desc);
			if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;

			if (SUCCEEDED(fn(adapter.Get(), D3D_FEATURE_LEVEL_11_0, _uuidof(ID3D12Device), nullptr)))
			{
				break;
			}
			adapter.Reset();
		}

		if (!adapter) return false;

		// Capture adapter identity for the DLSS hardware gate (NVIDIA RTX -> DLSS; else render-scale fallback).
		LUID chosen_luid{};
		{
			DXGI_ADAPTER_DESC1 chosen{};
			adapter->GetDesc1(&chosen);
			m_adapter_vendor_id = chosen.VendorId;
			m_adapter_name = chosen.Description;
			chosen_luid = chosen.AdapterLuid;
			bool isNvidia = (chosen.VendorId == 0x10DE);
			bool hasRtx = (m_adapter_name.find(L"RTX") != std::wstring::npos);
			// Prefer Streamline's REAL support query (driver/OS/HW checked); fall back to the NVIDIA+RTX heuristic
			// when Streamline isn't available, so the gate still works on every machine.
			if (DX12Streamline::instance().available())
				m_dlss_capable = DX12Streamline::instance().is_dlss_supported(chosen_luid);
			else
				m_dlss_capable = isNvidia && hasRtx;
		}

		HRESULT hr = fn(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&m_device));
		// Register the main device with Streamline right after creation (required before any DLSS feature use).
		if (SUCCEEDED(hr) && m_device)
			DX12Streamline::instance().set_device(m_device.Get());
		return SUCCEEDED(hr);
	}
}
