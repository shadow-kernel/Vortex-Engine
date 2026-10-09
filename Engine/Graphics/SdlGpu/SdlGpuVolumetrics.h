#pragma once

// ============================================================================
// SdlGpuVolumetrics — volumetric fog on the SDL GPU backend (#119), fed by volumetrics.metal / volumetrics.*.glsl:
// a half-resolution ray march (RGBA16F: scattered light in rgb, transmittance in a) through the scene depth, the
// renderer's light buffer and its shadow atlases, then a composite over the scene (ONE / SRC_ALPHA) after the
// world's opaque + transparent meshes and before the particles. See Graphics/Volumetrics/Volumetrics.h.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include "../Volumetrics/Volumetrics.h"
#include "SdlGpuParticles.h"
#include <SDL3/SDL.h>
#include <chrono>
#include <string>

namespace vortex::graphics::sdlgpu
{
	class SdlGpuVolumetrics
	{
	public:
		struct View
		{
			DirectX::XMFLOAT4X4 inv_view_projection;
			DirectX::XMFLOAT3 eye;
			float near_clip{ 0.1f }, far_clip{ 1000.0f };
			bool ortho{ false };
		};
		struct Shadows
		{
			SDL_GPUTexture* spot_atlas{ nullptr };
			SDL_GPUTexture* csm_atlas{ nullptr };
			SDL_GPUTexture* point_atlas{ nullptr };
			SDL_GPUSampler* comparison{ nullptr };
			float map_texel{ 1.0f / 2048.0f };
		};

		bool initialize(SDL_GPUDevice* device, const std::string& shader_dir, SDL_GPUTextureFormat scene_format, SDL_GPUSampler* linear_clamp);
		void shutdown();
		bool ready() const { return m_ready; }

		void set_params(const volumetrics::Params& p) { m_params = p; }
		const volumetrics::Params& params() const { return m_params; }
		bool active() const { return m_ready && m_params.enabled && m_params.density > 0.0f; }

		// Draw the fog over `color` (between the world's meshes and the particles). `lights` = the renderer's 2304-byte
		// light buffer; a null shadow texture turns the shadows off.
		void draw(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* color, SDL_GPUTexture* depth, u32 w, u32 h,
			const View& view, const SdlGpuParticles::Environment& env, const void* lights, const Shadows& shadows);

	private:
		SDL_GPUShader* create_shader(const char* entry, SDL_GPUShaderStage stage, u32 samplers, u32 storage, u32 uniforms);
		bool ensure_target(u32 w, u32 h);

		SDL_GPUDevice* m_device{ nullptr };
		std::string m_shader_dir;
		bool m_ready{ false };
		volumetrics::Params m_params{};
		SDL_GPUTextureFormat m_scene_format{ SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM };
		SDL_GPUSampler* m_linear_clamp{ nullptr };
		SDL_GPUSampler* m_point_clamp{ nullptr };
		SDL_GPUShader* m_vs{ nullptr };
		SDL_GPUShader* m_fs_march{ nullptr };
		SDL_GPUShader* m_fs_composite{ nullptr };
		SDL_GPUGraphicsPipeline* m_march{ nullptr };
		SDL_GPUGraphicsPipeline* m_composite{ nullptr };
		SDL_GPUTexture* m_target{ nullptr };
		u32 m_w{ 0 }, m_h{ 0 };
		std::chrono::steady_clock::time_point m_origin{ std::chrono::steady_clock::now() };
	};
}
