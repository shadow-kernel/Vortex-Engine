#pragma once

// ============================================================================
// SdlGpuDecals — the projected decal pass on the SDL GPU backend (#120), fed by decals.metal / decals.*.glsl.
// prepare() builds and uploads the frame's decal boxes (Graphics/Decals/Decals.h) in a copy pass; draw() renders
// them in a pass of their own WITHOUT a depth attachment between the opaque and the transparent meshes: back faces
// only, the scene depth sampled for the covered position, the material's albedo texture + base colour blended lit /
// multiplied / additive.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include "../Decals/Decals.h"
#include "SdlGpuParticles.h"
#include <SDL3/SDL.h>
#include <string>
#include <vector>

namespace vortex::graphics::sdlgpu
{
	class SdlGpuDecals
	{
	public:
		struct View
		{
			DirectX::XMFLOAT4X4 view_projection;
			DirectX::XMFLOAT4X4 inv_view_projection;
			DirectX::XMFLOAT3 eye;
			float near_clip{ 0.1f }, far_clip{ 1000.0f };
			bool ortho{ false };
		};

		bool initialize(SDL_GPUDevice* device, const std::string& shader_dir, SDL_GPUTextureFormat color_format, SDL_GPUSampler* linear_clamp);
		void shutdown();
		bool ready() const { return m_ready; }

		// Build + upload (copy pass, before the scene pass). True when there is something to draw.
		bool prepare(SDL_GPUCommandBuffer* cmd, const std::vector<decals::Decal>& list);
		void draw(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* color, SDL_GPUTexture* depth, u32 w, u32 h,
			const View& view, const SdlGpuParticles::Environment& env);

	private:
		SDL_GPUShader* create_shader(const char* entry, SDL_GPUShaderStage stage, u32 samplers, u32 storage, u32 uniforms);
		SDL_GPUGraphicsPipeline* create_pipeline(u32 blend);

		SDL_GPUDevice* m_device{ nullptr };
		std::string m_shader_dir;
		bool m_ready{ false };
		SDL_GPUTextureFormat m_color_format{ SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM };
		SDL_GPUSampler* m_linear_clamp{ nullptr };
		SDL_GPUSampler* m_point_clamp{ nullptr };
		SDL_GPUShader* m_vs{ nullptr };
		SDL_GPUShader* m_fs{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline[3]{};   // by blend mode
		SDL_GPUBuffer* m_instances{ nullptr };
		SDL_GPUTransferBuffer* m_upload{ nullptr };
		u32 m_instance_cap{ 0 };
		std::vector<decals::GpuDecal> m_gpu;
		std::vector<decals::Batch> m_batches;
	};
}
