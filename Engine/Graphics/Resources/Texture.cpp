#include "Texture.h"
#include "../DX12/DX12Core.h"
#include "../../Common/VerboseLog.h"
#include <wrl/client.h>
#include <cstring>

namespace vortex::graphics
{
	using Microsoft::WRL::ComPtr;
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
				// Get the row pitch from the texture layout
				D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint;
				UINT num_rows;
				UINT64 row_size_bytes;
				device->GetCopyableFootprints(&res_desc, 0, 1, 0, &footprint, &num_rows, &row_size_bytes, nullptr);

				// Copy data row by row (handling pitch differences)
				u32 src_row_pitch = desc.width * 4; // Source data is tightly packed
				u8* dst = static_cast<u8*>(mapped);
				const u8* src = static_cast<const u8*>(data);
				
				for (UINT row = 0; row < num_rows; ++row)
				{
					std::memcpy(dst + row * footprint.Footprint.RowPitch, 
					            src + row * src_row_pitch, 
					            src_row_pitch);
				}
				m_upload_buffer->Unmap(0, nullptr);

				// Note: The actual GPU copy is deferred - caller must execute copy commands
				// For now, we'll do an immediate copy using the DX12Core command queue
				VORTEX_VLOG("Texture data uploaded to staging buffer\n");

				// Create a temporary command list to copy texture data
				ComPtr<ID3D12CommandAllocator> cmd_alloc;
				ComPtr<ID3D12GraphicsCommandList> cmd_list;
				ComPtr<ID3D12CommandQueue> cmd_queue;
				ComPtr<ID3D12Fence> fence;

				if (SUCCEEDED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&cmd_alloc))) &&
					SUCCEEDED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, cmd_alloc.Get(), nullptr, IID_PPV_ARGS(&cmd_list))))
				{
					D3D12_COMMAND_QUEUE_DESC queue_desc{};
					queue_desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
					
					if (SUCCEEDED(device->CreateCommandQueue(&queue_desc, IID_PPV_ARGS(&cmd_queue))) &&
						SUCCEEDED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence))))
					{
						// Copy texture data from upload buffer to GPU texture
						D3D12_TEXTURE_COPY_LOCATION dst_loc{};
						dst_loc.pResource = m_resource.Get();
						dst_loc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
						dst_loc.SubresourceIndex = 0;

						D3D12_TEXTURE_COPY_LOCATION src_loc{};
						src_loc.pResource = m_upload_buffer.Get();
						src_loc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
						src_loc.PlacedFootprint = footprint;

						cmd_list->CopyTextureRegion(&dst_loc, 0, 0, 0, &src_loc, nullptr);

						// Transition texture to shader resource state
						D3D12_RESOURCE_BARRIER barrier{};
						barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
						barrier.Transition.pResource = m_resource.Get();
						barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
						barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
						barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
						cmd_list->ResourceBarrier(1, &barrier);

						cmd_list->Close();

						// Execute and wait
						ID3D12CommandList* lists[] = { cmd_list.Get() };
						cmd_queue->ExecuteCommandLists(1, lists);
						cmd_queue->Signal(fence.Get(), 1);

						HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
						fence->SetEventOnCompletion(1, event);
						WaitForSingleObject(event, INFINITE);
						CloseHandle(event);

						VORTEX_VLOG("Texture GPU copy completed\n");
					}
				}
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
