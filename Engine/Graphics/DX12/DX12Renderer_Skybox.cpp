#include "DX12Renderer_Internal.h"

namespace vortex::graphics::dx12
{
	bool DX12Renderer::create_skybox_resources()
	{
		auto dev = DX12Core::instance().device();

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_UPLOAD;
		D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		rd.Width = 256; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.SampleDesc.Count = 1; rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_skybox_cb))))
			return false;
		
		D3D12_RANGE r{0,0};
		if (FAILED(m_skybox_cb->Map(0, &r, &m_skybox_cb_mapped))) return false;

		return true;
	}


	void DX12Renderer::render_skybox()
	{
		if (!m_skybox_pipeline.pipeline_state()) return;

		auto rtv = m_active_rtv;
		auto dsv = m_active_dsv;
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		
		m_command_list->SetPipelineState(m_skybox_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_skybox_pipeline.root_signature());

		using namespace DirectX;
		XMVECTOR eye = XMLoadFloat3(&m_camera_position);
		XMVECTOR at = XMLoadFloat3(&m_camera_target);
		XMVECTOR up = XMLoadFloat3(&m_camera_up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		float aspect = (float)m_active_width / (float)m_active_height;
		XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, 0.1f, 1000.0f); // MUST match the scene FOV (update_per_frame_constants) or grid/sky misalign vs objects
		XMMATRIX vp = view * proj;

		// Update skybox constants with inverse VP matrix
		auto constants_ptr = reinterpret_cast<u8*>(m_skybox_cb_mapped);
		if (constants_ptr)
		{
			// Copy pipeline constants first
			memcpy(constants_ptr, m_skybox_pipeline.get_constants(), m_skybox_pipeline.get_constants_size());
			
			// Update inverse view projection and camera position
			XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp);
			XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(constants_ptr), inv_vp);
			
			// Camera position is at offset 64 (after the 4x4 matrix)
			*reinterpret_cast<XMFLOAT3*>(constants_ptr + 64) = m_camera_position;
		}

		m_command_list->SetGraphicsRootConstantBufferView(0, m_skybox_cb->GetGPUVirtualAddress());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->DrawInstanced(3, 1, 0, 0);
	}


	void DX12Renderer::set_skybox_colors(
		const DirectX::XMFLOAT3& sky_color,
		const DirectX::XMFLOAT3& horizon_color,
		const DirectX::XMFLOAT3& ground_color)
	{
		m_skybox_pipeline.set_colors(sky_color, horizon_color, ground_color);
	}


	void DX12Renderer::set_skybox_solid_color(const DirectX::XMFLOAT3& color)
	{
		// For solid color, set all three colors to the same value
		m_skybox_pipeline.set_colors(color, color, color);
	}


	void DX12Renderer::set_skybox_mode(SkyboxMode mode)
	{
		m_skybox_mode = mode;
	}


	void DX12Renderer::set_skybox_sun(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity)
	{
		m_skybox_pipeline.set_sun(direction, color, intensity);
	}


}
