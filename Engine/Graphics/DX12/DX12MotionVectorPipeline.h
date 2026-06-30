#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <DirectXMath.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	/// <summary>
	/// Fullscreen motion-vector pass for DLSS. Reads the scaled scene depth (SRV t0) and, from the current
	/// inverse-view-projection + previous view-projection (root constants b0), reprojects each pixel to its
	/// previous-frame screen position and writes the pixel-space velocity into an RG16F target. Camera-motion
	/// only (static geometry); moving objects would need per-object previous-world matrices (future).
	/// Matrix convention matches the rest of the engine: row-major, mul(vector, matrix), clip.y = -ndc.y.
	/// </summary>
	class DX12MotionVectorPipeline
	{
	public:
		// Root constants (b0). Matrices are row-major (as stored by XMStoreFloat4x4).
		struct Constants
		{
			DirectX::XMFLOAT4X4 inv_view_proj;   // inverse(current VP)
			DirectX::XMFLOAT4X4 prev_view_proj;  // previous frame VP
			float dims[2];                        // render width/height (pixel-space mvec scale)
			float pad[2];
		};

		bool initialize(ID3D12Device* device, DXGI_FORMAT rtv_format /* RG16F */);
		void shutdown();

		ID3D12PipelineState* pipeline_state() const { return m_pipeline_state.Get(); }
		ID3D12RootSignature* root_signature() const { return m_root_signature.Get(); }
		bool is_initialized() const { return m_pipeline_state != nullptr; }

	private:
		bool compile_shaders();
		bool create_root_signature(ID3D12Device* device);
		bool create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format);

		ComPtr<ID3D12PipelineState> m_pipeline_state;
		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3DBlob> m_vs_blob;
		ComPtr<ID3DBlob> m_ps_blob;
	};
}
