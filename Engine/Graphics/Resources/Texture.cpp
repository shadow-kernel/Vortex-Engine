#include "Texture.h"
#include <cstring>

namespace vortex::graphics
{
	DXGI_FORMAT Texture::to_dxgi_format(TextureFormat format)
	{
		switch (format)
		{
		case TextureFormat::RGBA8_UNORM: return DXGI_FORMAT_R8G8B8A8_UNORM;
		case TextureFormat::RGBA8_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
		case TextureFormat::BGRA8_UNORM: return DXGI_FORMAT_B8G8R8A8_UNORM;
		case TextureFormat::R8_UNORM: return DXGI_FORMAT_R8_UNORM;
		case TextureFormat::RG8_UNORM: return DXGI_FORMAT_R8G8_UNORM;
		case TextureFormat::RGBA16_FLOAT: return DXGI_FORMAT_R16G16B16A16_FLOAT;
		case TextureFormat::RGBA32_FLOAT: return DXGI_FORMAT_R32G32B32A32_FLOAT;
		case TextureFormat::D24_UNORM_S8_UINT: return DXGI_FORMAT_D24_UNORM_S8_UINT;
		case TextureFormat::D32_FLOAT: return DXGI_FORMAT_D32_FLOAT;
		default: return DXGI_FORMAT_R8G8B8A8_UNORM;
		}
	}

	bool Texture::create(ID3D12Device* device, const TextureDesc& desc, const void* data)
	{
		if (!device || desc.width == 0 || desc.height == 0) return false;

		destroy();

		m_width = desc.width;
		m_height = desc.height;
		m_format = desc.format;

		DXGI_FORMAT dxgi_format = to_dxgi_format(desc.format);

		D3D12_RESOURCE_DESC res_desc{};
		res_desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		res_desc.Width = desc.width;
		res_desc.Height = desc.height;
		res_desc.DepthOrArraySize = 1;
		res_desc.MipLevels = 1;
		res_desc.Format = dxgi_format;
		res_desc.SampleDesc.Count = 1;
		res_desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;

		D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE;
		if (desc.is_render_target)
			flags |= D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
		if (desc.is_depth_stencil)
			flags |= D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
		res_desc.Flags = flags;

		D3D12_HEAP_PROPERTIES heap_props{};
		heap_props.Type = D3D12_HEAP_TYPE_DEFAULT;

		D3D12_RESOURCE_STATES initial_state = D3D12_RESOURCE_STATE_COPY_DEST;
		if (desc.is_depth_stencil)
			initial_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;
		else if (desc.is_render_target)
			initial_state = D3D12_RESOURCE_STATE_RENDER_TARGET;

		if (FAILED(device->CreateCommittedResource(&heap_props, D3D12_HEAP_FLAG_NONE,
			&res_desc, initial_state, nullptr, IID_PPV_ARGS(&m_resource))))
		{
			return false;
		}

		if (data && !desc.is_depth_stencil && !desc.is_render_target)
		{
			// Create upload buffer
			UINT64 upload_size = 0;
			device->GetCopyableFootprints(&res_desc, 0, 1, 0, nullptr, nullptr, nullptr, &upload_size);

			D3D12_HEAP_PROPERTIES upload_heap{};
			upload_heap.Type = D3D12_HEAP_TYPE_UPLOAD;

			D3D12_RESOURCE_DESC upload_desc{};
			upload_desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
			upload_desc.Width = upload_size;
			upload_desc.Height = 1;
			upload_desc.DepthOrArraySize = 1;
			upload_desc.MipLevels = 1;
			upload_desc.Format = DXGI_FORMAT_UNKNOWN;
			upload_desc.SampleDesc.Count = 1;
			upload_desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

			if (FAILED(device->CreateCommittedResource(&upload_heap, D3D12_HEAP_FLAG_NONE,
				&upload_desc, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_upload_buffer))))
			{
				return false;
			}

			void* mapped = nullptr;
			D3D12_RANGE range{ 0, 0 };
			if (SUCCEEDED(m_upload_buffer->Map(0, &range, &mapped)))
			{
				// Simple copy - assumes tightly packed row-major data
				u32 row_pitch = desc.width * 4; // Assuming 4 bytes per pixel
				std::memcpy(mapped, data, desc.width * desc.height * 4);
				m_upload_buffer->Unmap(0, nullptr);
			}
		}

		return true;
	}

	bool Texture::create_from_color(ID3D12Device* device, u32 color)
	{
		TextureDesc desc{};
		desc.width = 1;
		desc.height = 1;
		desc.format = TextureFormat::RGBA8_UNORM;

		return create(device, desc, &color);
	}

	void Texture::destroy()
	{
		m_resource.Reset();
		m_upload_buffer.Reset();
		m_srv = {};
		m_srv_gpu = {};
		m_width = 0;
		m_height = 0;
	}

	void Texture::set_srv_handles(D3D12_CPU_DESCRIPTOR_HANDLE cpu, D3D12_GPU_DESCRIPTOR_HANDLE gpu)
	{
		m_srv = cpu;
		m_srv_gpu = gpu;
	}
}
