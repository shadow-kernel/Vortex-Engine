#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <string>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	/// <summary>
	/// 3D Pipeline with MVP transformation, lighting, and depth testing.
	/// </summary>
	class DX12Pipeline3D
	{
	public:
		bool initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);
		void shutdown();

		ID3D12RootSignature* root_signature() const { return m_root_signature.Get(); }
		ID3D12PipelineState* pipeline_state() const { return m_pipeline_state.Get(); }

		// Wireframe mode for debugging
		ID3D12PipelineState* wireframe_pso() const { return m_wireframe_pso.Get(); }
		
		// Double-sided PSO for skybox/unlit materials (no backface culling)
		ID3D12PipelineState* double_sided_pso() const { return m_double_sided_pso.Get(); }

		// Gizmo PSO: same shaders/root sig, but depth test+write DISABLED and no backface culling, so editor
		// transform gizmos render ALWAYS ON TOP of scene geometry (never occluded) and never cull when the camera
		// is close/inside them. Drawn in a dedicated pass AFTER the scene.
		ID3D12PipelineState* gizmo_pso() const { return m_gizmo_pso.Get(); }

		// Gizmo WIRE PSO: like gizmo_pso but rasterized WIREFRAME — draws a whole range sphere / zone shape
		// as one fine triangle net (thin lines) in a single draw call.
		ID3D12PipelineState* gizmo_wire_pso() const { return m_gizmo_wire_pso.Get(); }

		// Skinned PSO: skinned.hlsl VS (GPU skinning off the bone-palette root SRV at param 8) + the
		// standard PS. Input layout adds BLENDINDICES/BLENDWEIGHT on slot 0 (52-byte vertex); the
		// per-instance INSTANCEWORLD stream on slot 1 is kept, so a skinned draw is a 1-instance
		// DrawIndexedInstanced through the exact same binding flow. nullptr = skinned shader failed to
		// compile (renderer falls back to the rigid PSO -> bind pose, never a crash).
		ID3D12PipelineState* skinned_pso() const { return m_skinned_pso.Get(); }

		// Shadow PSO: depth-only (standard VS, no PS, no RTV, D32 DSV + depth bias) — renders casters from
		// the spot light's view into the shadow map. Rigid input layout, so it draws straight from the
		// shadow instance VB. nullptr = creation failed (spot shadows silently disabled, never a crash).
		ID3D12PipelineState* shadow_pso() const { return m_shadow_pso.Get(); }

		// Z-prepass PSO (#32): the shadow PSO recipe without depth biases — renders the camera-view
		// half-res AO depth the SSAO pass reconstructs from. nullptr = SSAO silently disabled.
		ID3D12PipelineState* zprepass_pso() const { return m_zprepass_pso.Get(); }

		// Transparent PSOs (#33): standard shaders with blending ENABLED and depth WRITE off (test stays
		// on LESS_EQUAL) — drawn in the sorted back-to-front pass after all opaques. blend_mode 1 = alpha
		// (SrcAlpha/InvSrcAlpha), 2 = additive (SrcAlpha/One); double_sided mirrors the opaque unlit rule.
		// Returns nullptr for opaque/unknown modes (callers must route those through the opaque pass).
		ID3D12PipelineState* transparent_pso(u32 blend_mode, bool double_sided) const
		{
			if (blend_mode == 1) return double_sided ? m_alpha_ds_pso.Get() : m_alpha_pso.Get();
			if (blend_mode == 2) return double_sided ? m_additive_ds_pso.Get() : m_additive_pso.Get();
			return nullptr;
		}

		// Compile a CUSTOM material shader (.hlsl, VSMain/PSMain) into a PSO that reuses this pipeline's root
		// signature + input layout + render state — only the shader stages differ, so it stays binding-compatible
		// with the same PerFrame/PerObject/light/texture setup. Returns nullptr on any compile/create failure (the
		// caller keeps the built-in PSO as a fallback -> a bad custom shader never black-screens). No device state
		// is mutated. hlsl_path is an ABSOLUTE path to the project's shader file.
		ComPtr<ID3D12PipelineState> create_custom_pso(ID3D12Device* device, const std::wstring& hlsl_path);

	private:
		bool compile_shaders();
		bool create_root_signature(ID3D12Device* device);
		bool create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);

		DXGI_FORMAT m_rtv_format{ DXGI_FORMAT_R8G8B8A8_UNORM };  // remembered from initialize (for custom PSOs)
		DXGI_FORMAT m_dsv_format{ DXGI_FORMAT_D32_FLOAT };
		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3D12PipelineState> m_pipeline_state;
		ComPtr<ID3D12PipelineState> m_wireframe_pso;
		ComPtr<ID3D12PipelineState> m_double_sided_pso;
		ComPtr<ID3D12PipelineState> m_gizmo_pso;   // depth-disabled, cull-none: gizmos always on top
		ComPtr<ID3D12PipelineState> m_gizmo_wire_pso; // gizmo PSO variant with WIREFRAME fill (fine-net shapes)
		ComPtr<ID3D12PipelineState> m_skinned_pso; // GPU skinning (skinned.hlsl VS + standard PS)
		ComPtr<ID3D12PipelineState> m_shadow_pso;  // depth-only shadow-map pass (standard VS, no PS/RTV)
		ComPtr<ID3D12PipelineState> m_zprepass_pso; // #32: camera-view depth-only pass, no biases
		ComPtr<ID3D12PipelineState> m_alpha_pso;       // #33: alpha blend, cull back, depth write off
		ComPtr<ID3D12PipelineState> m_alpha_ds_pso;    // #33: alpha blend, double-sided
		ComPtr<ID3D12PipelineState> m_additive_pso;    // #33: additive, cull back, depth write off
		ComPtr<ID3D12PipelineState> m_additive_ds_pso; // #33: additive, double-sided
		ComPtr<ID3DBlob> m_vs_blob;
		ComPtr<ID3DBlob> m_ps_blob;
		ComPtr<ID3DBlob> m_skinned_vs_blob;        // optional — skinned PSO skipped if it fails to load
	};
}
