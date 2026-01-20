#include "DX12Core.h"

namespace vortex::graphics::dx12
{
	namespace
	{
		using PFN_CreateDXGIFactory2 = HRESULT(WINAPI*)(UINT, REFIID, void**);
		using PFN_D3D12CreateDevice = HRESULT(WINAPI*)(IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);

		PFN_CreateDXGIFactory2 get_create_dxgi_factory()
		{
			static auto fn = reinterpret_cast<PFN_CreateDXGIFactory2>(
				GetProcAddress(LoadLibraryW(L"dxgi.dll"), "CreateDXGIFactory2"));
			return fn;
		}

		PFN_D3D12CreateDevice get_d3d12_create_device()
		{
			static auto fn = reinterpret_cast<PFN_D3D12CreateDevice>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12CreateDevice"));
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
		return SUCCEEDED(fn(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&m_device)));
	}
}
