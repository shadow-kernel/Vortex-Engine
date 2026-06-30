#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <DirectXMath.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	/// <summary>
	/// Offscreen render target for multi-viewport rendering.
	/// Supports rendering to texture and reading back pixel data.
	/// </summary>
	class DX12RenderTarget
	{
	public:
		DX12RenderTarget() = default;
		~DX12RenderTarget();

		/// <summary>
		/// Initialize the render target with specified dimensions.
		/// Uses BGRA format for WPF compatibility.
		/// </summary>
		bool initialize(ID3D12Device* device, u32 width, u32 height,
						DXGI_FORMAT format = DXGI_FORMAT_B8G8R8A8_UNORM,
						bool sampleable_depth = false,
						bool allow_uav = false);
		
		/// <summary>
		/// Shutdown and release all resources.
		/// </summary>
		void shutdown();

		/// <summary>
		/// Resize the render target.
		/// </summary>
		bool resize(ID3D12Device* device, u32 width, u32 height);

		/// <summary>
		/// Get the render target resource.
		/// </summary>
		ID3D12Resource* resource() const { return m_render_target.Get(); }
		
		/// <summary>
		/// Get the depth buffer resource.
		/// </summary>
		ID3D12Resource* depth_resource() const { return m_depth_buffer.Get(); }

		/// <summary>
		/// Get the RTV descriptor handle.
		/// </summary>
		D3D12_CPU_DESCRIPTOR_HANDLE rtv() const;
		
		/// <summary>
		/// Get the DSV descriptor handle.
		/// </summary>
		D3D12_CPU_DESCRIPTOR_HANDLE dsv() const;
		
		/// <summary>
		/// Get the SRV descriptor handle (for sampling the texture).
		/// </summary>
		D3D12_CPU_DESCRIPTOR_HANDLE srv() const;
		
		/// <summary>
		/// Get GPU descriptor handle for shader access.
		/// </summary>
		D3D12_GPU_DESCRIPTOR_HANDLE srv_gpu() const;

		/// <summary>
		/// GPU descriptor for sampling the DEPTH buffer (only valid when created with sampleable_depth=true;
		/// the depth resource is then R32_TYPELESS with a D32_FLOAT DSV + an R32_FLOAT SRV at heap slot 1).
		/// Used as the DLSS depth input. Returns {} if sampleable depth wasn't requested.
		/// </summary>
		D3D12_GPU_DESCRIPTOR_HANDLE depth_srv_gpu() const;

		/// <summary>
		/// The (shader-visible) SRV heap holding this target's SRV — bind via SetDescriptorHeaps to sample it.
		/// </summary>
		ID3D12DescriptorHeap* srv_heap() const { return m_srv_heap.Get(); }

		/// <summary>
		/// Transition to render target state.
		/// </summary>
		void transition_to_render_target(ID3D12GraphicsCommandList* cmd);
		
		/// <summary>
		/// Transition to shader resource state (for reading).
		/// </summary>
		void transition_to_shader_resource(ID3D12GraphicsCommandList* cmd);
		
		/// <summary>
		/// Transition to copy source state (for readback).
		/// </summary>
		void transition_to_copy_source(ID3D12GraphicsCommandList* cmd);

		/// <summary>Depth transitions (only meaningful with sampleable_depth) — flip the depth between DEPTH_WRITE
		/// (3D pass) and PIXEL_SHADER_RESOURCE (mvec / DLSS sampling). Tracked separately from the color state.</summary>
		void transition_depth_to_shader_resource(ID3D12GraphicsCommandList* cmd);
		void transition_depth_to_depth_write(ID3D12GraphicsCommandList* cmd);

		/// <summary>
		/// Copy render target data to a staging buffer for CPU readback.
		/// </summary>
		bool copy_to_staging(ID3D12GraphicsCommandList* cmd);
		
		/// <summary>
		/// Map the staging buffer and return pointer to pixel data.
		/// Returns nullptr if not available. Call unmap() when done.
		/// </summary>
		const void* map_staging_buffer();
		
		/// <summary>
		/// Unmap the staging buffer.
		/// </summary>
		void unmap_staging_buffer();

		u32 width() const { return m_width; }
		u32 height() const { return m_height; }
		DXGI_FORMAT format() const { return m_format; }
		bool is_initialized() const { return m_initialized; }
		
		/// <summary>
		/// Get the row pitch of the staging buffer.
		/// </summary>
		u32 staging_row_pitch() const { return m_staging_row_pitch; }

	private:
		bool create_render_target(ID3D12Device* device);
		bool create_depth_buffer(ID3D12Device* device);
		bool create_descriptor_heaps(ID3D12Device* device);
		bool create_staging_buffer(ID3D12Device* device);

		ComPtr<ID3D12Resource> m_render_target;
		ComPtr<ID3D12Resource> m_depth_buffer;
		ComPtr<ID3D12Resource> m_staging_buffer;
		
		ComPtr<ID3D12DescriptorHeap> m_rtv_heap;
		ComPtr<ID3D12DescriptorHeap> m_dsv_heap;
		ComPtr<ID3D12DescriptorHeap> m_srv_heap;
		
		u32 m_width{ 0 };
		u32 m_height{ 0 };
		u32 m_staging_row_pitch{ 0 };
		u32 m_srv_increment{ 0 };           // CBV_SRV_UAV descriptor size (for the depth SRV at slot 1)
		DXGI_FORMAT m_format{ DXGI_FORMAT_R8G8B8A8_UNORM };
		D3D12_RESOURCE_STATES m_current_state{ D3D12_RESOURCE_STATE_COMMON };
		D3D12_RESOURCE_STATES m_depth_state{ D3D12_RESOURCE_STATE_DEPTH_WRITE };
		bool m_sampleable_depth{ false };   // depth created as R32_TYPELESS with an SRV (DLSS input)
		bool m_allow_uav{ false };          // color RT also allows UAV (DLSS writes the upscaled output via UAV)
		bool m_initialized{ false };
		bool m_staging_mapped{ false };
	};
}
