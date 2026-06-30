#include "DX12Renderer_Internal.h"

namespace vortex::graphics::dx12
{
	void DX12Renderer::render_grid()
	{
		if (!m_grid_pipeline.pipeline_state()) return;

		auto rtv = m_active_rtv;
		auto dsv = m_active_dsv;
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		
		m_command_list->SetPipelineState(m_grid_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_grid_pipeline.root_signature());

		using namespace DirectX;
		XMVECTOR eye = XMLoadFloat3(&m_camera_position);
		XMVECTOR at = XMLoadFloat3(&m_camera_target);
		XMVECTOR up = XMLoadFloat3(&m_camera_up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		float aspect = (float)m_active_width / (float)m_active_height;
		XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, 0.1f, 1000.0f); // MUST match the scene FOV (update_per_frame_constants) or grid/sky misalign vs objects
		XMMATRIX vp = view * proj;

		GridConstants gc{};
		XMStoreFloat4x4(&gc.view_projection, vp);
		XMStoreFloat4x4(&gc.inverse_view_projection, XMMatrixInverse(nullptr, vp));
		gc.camera_position = m_camera_position;
		gc.grid_spacing = m_grid_spacing;
		gc.grid_extent = m_grid_extent;
		gc.major_line_interval = m_grid_major_interval;

		if (m_grid_cb_mapped)
			memcpy(m_grid_cb_mapped, &gc, sizeof(gc));

		m_command_list->SetGraphicsRootConstantBufferView(0, m_grid_cb->GetGPUVirtualAddress());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->DrawInstanced(3, 1, 0, 0);
	}


	void DX12Renderer::set_grid_settings(float s, float m, float e)
	{
		m_grid_spacing = s; m_grid_major_interval = m; m_grid_extent = e;
	}


	bool DX12Renderer::create_grid_resources()
	{
		auto dev = DX12Core::instance().device();

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_UPLOAD;
		D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		rd.Width = 256; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.SampleDesc.Count = 1; rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_grid_cb))))
			return false;
		
		D3D12_RANGE r{0,0};
		if (FAILED(m_grid_cb->Map(0, &r, &m_grid_cb_mapped))) return false;

		return true;
	}


}
