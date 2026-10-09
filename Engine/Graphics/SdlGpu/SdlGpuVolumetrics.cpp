#include "SdlGpuVolumetrics.h"
#include "SdlGpuShaderFormat.h"
#include "../../Common/Platform.h"
#include <algorithm>
#include <cstring>
#include <fstream>
#include <vector>

namespace vortex::graphics::sdlgpu
{
	namespace
	{
		void log(const std::string& s) { platform::debug_output(("[volumetrics] " + s + "\n").c_str()); }

		// byte-matched to VFrame in volumetrics.metal / volumetrics_common.glsl
		struct VFrame
		{
			DirectX::XMFLOAT4X4 inv_view_projection;
			float eye[4];
			float screen[4];
			float depth_params[4];
			float fog[4], fog2[4];
			float vol[4];
			float sun_dir[4], sun_color[4];
			float params2[4];
			u32 counts[4];
			float params3[4];
			float pad[4];
		};
		static_assert(sizeof(VFrame) == 256, "VFrame must byte-match volumetrics.metal");
		constexpr u32 LIGHT_BYTES = 2304;
	}

	SDL_GPUShader* SdlGpuVolumetrics::create_shader(const char* entry, SDL_GPUShaderStage stage, u32 samplers, u32 storage, u32 uniforms)
	{
		const std::string file = shaderfmt::module_file("volumetrics", entry);
		std::vector<unsigned char> code;
		{
			std::ifstream f(m_shader_dir + "/" + file, std::ios::binary);
			if (!f) { log("missing shader " + file); return nullptr; }
			code.assign((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
			if (code.empty()) { log("empty shader " + file); return nullptr; }
			if (shaderfmt::is_text) code.push_back('\0');
		}
		SDL_GPUShaderCreateInfo ci{};
		ci.code = code.data();
		ci.code_size = code.size();
		ci.entrypoint = shaderfmt::entrypoint(entry);
		ci.format = shaderfmt::format;
		ci.stage = stage;
		ci.num_samplers = samplers;
		ci.num_storage_buffers = storage;
		ci.num_uniform_buffers = uniforms;
		SDL_GPUShader* sh = SDL_CreateGPUShader(m_device, &ci);
		if (!sh) log(std::string("shader '") + entry + "' failed: " + SDL_GetError());
		return sh;
	}

	bool SdlGpuVolumetrics::initialize(SDL_GPUDevice* device, const std::string& shader_dir, SDL_GPUTextureFormat scene_format, SDL_GPUSampler* linear_clamp)
	{
		shutdown();
		m_device = device;
		m_shader_dir = shader_dir;
		m_scene_format = scene_format;
		m_linear_clamp = linear_clamp;
		m_vs = create_shader("VolFogVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 0);
		m_fs_march = create_shader("VolFogPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 4, 0, 2);
		m_fs_composite = create_shader("VolCompositePS", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0, 0);
		if (!m_vs || !m_fs_march || !m_fs_composite) { shutdown(); return false; }

		auto fullscreen = [&](SDL_GPUShader* fs, SDL_GPUTextureFormat format, bool blend) -> SDL_GPUGraphicsPipeline*
		{
			SDL_GPUColorTargetDescription color{};
			color.format = format;
			if (blend)
			{
				color.blend_state.enable_blend = true;            // scene * T + S
				color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
				color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_ALPHA;
				color.blend_state.color_blend_op = SDL_GPU_BLENDOP_ADD;
				color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ZERO;
				color.blend_state.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
				color.blend_state.alpha_blend_op = SDL_GPU_BLENDOP_ADD;
				color.blend_state.enable_color_write_mask = true;
				color.blend_state.color_write_mask = SDL_GPU_COLORCOMPONENT_R | SDL_GPU_COLORCOMPONENT_G | SDL_GPU_COLORCOMPONENT_B;
			}
			SDL_GPUGraphicsPipelineCreateInfo pci{};
			pci.vertex_shader = m_vs;
			pci.fragment_shader = fs;
			pci.primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
			pci.rasterizer_state.fill_mode = SDL_GPU_FILLMODE_FILL;
			pci.rasterizer_state.cull_mode = SDL_GPU_CULLMODE_NONE;
			pci.rasterizer_state.front_face = SDL_GPU_FRONTFACE_CLOCKWISE;
			pci.multisample_state.sample_count = SDL_GPU_SAMPLECOUNT_1;
			pci.target_info.color_target_descriptions = &color;
			pci.target_info.num_color_targets = 1;
			pci.target_info.has_depth_stencil_target = false;
			SDL_GPUGraphicsPipeline* p = SDL_CreateGPUGraphicsPipeline(m_device, &pci);
			if (!p) log(std::string("pipeline failed: ") + SDL_GetError());
			return p;
		};
		m_march = fullscreen(m_fs_march, SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT, false);
		m_composite = fullscreen(m_fs_composite, m_scene_format, true);
		if (!m_march || !m_composite) { shutdown(); return false; }
		SDL_GPUSamplerCreateInfo sci{};
		sci.min_filter = sci.mag_filter = SDL_GPU_FILTER_NEAREST;
		sci.mipmap_mode = SDL_GPU_SAMPLERMIPMAPMODE_NEAREST;
		sci.address_mode_u = sci.address_mode_v = sci.address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
		m_point_clamp = SDL_CreateGPUSampler(m_device, &sci);
		if (!m_point_clamp) { shutdown(); return false; }
		m_ready = true;
		log("ready (half-res ray march, sun / point / spot in-scattering, shadowed)");
		return true;
	}

	void SdlGpuVolumetrics::shutdown()
	{
		if (m_device)
		{
			if (m_march) SDL_ReleaseGPUGraphicsPipeline(m_device, m_march);
			if (m_composite) SDL_ReleaseGPUGraphicsPipeline(m_device, m_composite);
			if (m_vs) SDL_ReleaseGPUShader(m_device, m_vs);
			if (m_fs_march) SDL_ReleaseGPUShader(m_device, m_fs_march);
			if (m_fs_composite) SDL_ReleaseGPUShader(m_device, m_fs_composite);
			if (m_point_clamp) SDL_ReleaseGPUSampler(m_device, m_point_clamp);
			if (m_target) SDL_ReleaseGPUTexture(m_device, m_target);
		}
		m_march = m_composite = nullptr;
		m_vs = m_fs_march = m_fs_composite = nullptr;
		m_point_clamp = nullptr;
		m_target = nullptr;
		m_w = m_h = 0;
		m_ready = false;
		m_device = nullptr;
	}

	bool SdlGpuVolumetrics::ensure_target(u32 w, u32 h)
	{
		if (m_target && m_w == w && m_h == h) return true;
		if (m_target) SDL_ReleaseGPUTexture(m_device, m_target);   // released once the GPU is done with it
		SDL_GPUTextureCreateInfo tci{};
		tci.type = SDL_GPU_TEXTURETYPE_2D;
		tci.format = SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT;
		tci.usage = SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER;
		tci.width = w; tci.height = h; tci.layer_count_or_depth = 1; tci.num_levels = 1;
		tci.sample_count = SDL_GPU_SAMPLECOUNT_1;
		m_target = SDL_CreateGPUTexture(m_device, &tci);
		if (!m_target) { log(std::string("fog target failed: ") + SDL_GetError()); m_w = m_h = 0; return false; }
		m_w = w; m_h = h;
		return true;
	}

	void SdlGpuVolumetrics::draw(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* color, SDL_GPUTexture* depth, u32 w, u32 h,
		const View& view, const SdlGpuParticles::Environment& env, const void* lights, const Shadows& shadows)
	{
		if (!active() || !cmd || !color || !depth || !lights || w < 2 || h < 2) return;
		const u32 fw = (std::max)(1u, w / 2), fh = (std::max)(1u, h / 2);
		if (!ensure_target(fw, fh)) return;
		const bool shadowed = m_params.shadows && shadows.spot_atlas && shadows.csm_atlas && shadows.point_atlas && shadows.comparison;

		VFrame f{};
		f.inv_view_projection = view.inv_view_projection;
		f.eye[0] = view.eye.x; f.eye[1] = view.eye.y; f.eye[2] = view.eye.z;
		f.eye[3] = std::chrono::duration<float>(std::chrono::steady_clock::now() - m_origin).count();
		f.screen[0] = (float)fw; f.screen[1] = (float)fh; f.screen[2] = 1.0f / (float)fw; f.screen[3] = 1.0f / (float)fh;
		f.depth_params[0] = view.near_clip; f.depth_params[1] = view.far_clip; f.depth_params[2] = view.ortho ? 1.0f : 0.0f;
		f.fog[0] = env.fog_color.x; f.fog[1] = env.fog_color.y; f.fog[2] = env.fog_color.z; f.fog[3] = m_params.density;
		f.fog2[0] = env.fog_height_y; f.fog2[1] = env.fog_height_falloff; f.fog2[2] = m_params.noise_strength; f.fog2[3] = m_params.noise_scale;
		f.vol[0] = m_params.anisotropy; f.vol[1] = m_params.max_distance; f.vol[2] = (float)m_params.steps; f.vol[3] = m_params.noise_speed;
		f.sun_dir[0] = env.sun_direction.x; f.sun_dir[1] = env.sun_direction.y; f.sun_dir[2] = env.sun_direction.z; f.sun_dir[3] = env.sun_intensity;
		f.sun_color[0] = env.sun_color.x; f.sun_color[1] = env.sun_color.y; f.sun_color[2] = env.sun_color.z; f.sun_color[3] = env.ambient;
		f.params2[0] = m_params.lights; f.params2[1] = m_params.sun; f.params2[2] = shadowed ? 1.0f : 0.0f; f.params2[3] = shadows.map_texel;
		f.counts[0] = env.point_lights; f.counts[1] = env.spot_lights;
		f.params3[0] = m_params.ambient;

		// 1. ray march into the half-resolution target
		{
			SDL_GPUColorTargetInfo ct{};
			ct.texture = m_target;
			ct.load_op = SDL_GPU_LOADOP_DONT_CARE;
			ct.store_op = SDL_GPU_STOREOP_STORE;
			SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, &ct, 1, nullptr);
			if (!pass) return;
			SDL_GPUViewport vp{ 0, 0, (float)fw, (float)fh, 0.0f, 1.0f };
			SDL_SetGPUViewport(pass, &vp);
			SDL_BindGPUGraphicsPipeline(pass, m_march);
			SDL_PushGPUFragmentUniformData(cmd, 0, &f, sizeof(f));
			SDL_PushGPUFragmentUniformData(cmd, 1, lights, LIGHT_BYTES);
			SDL_GPUTexture* dummy = depth;
			SDL_GPUTextureSamplerBinding tb[4] = {
				{ depth, m_point_clamp },
				{ shadowed ? shadows.spot_atlas : dummy, shadowed ? shadows.comparison : m_point_clamp },
				{ shadowed ? shadows.csm_atlas : dummy, shadowed ? shadows.comparison : m_point_clamp },
				{ shadowed ? shadows.point_atlas : dummy, shadowed ? shadows.comparison : m_point_clamp },
			};
			SDL_BindGPUFragmentSamplers(pass, 0, tb, 4);
			SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
			SDL_EndGPURenderPass(pass);
		}
		// 2. composite over the scene: scene * T + S
		{
			SDL_GPUColorTargetInfo ct{};
			ct.texture = color;
			ct.load_op = SDL_GPU_LOADOP_LOAD;
			ct.store_op = SDL_GPU_STOREOP_STORE;
			SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, &ct, 1, nullptr);
			if (!pass) return;
			SDL_GPUViewport vp{ 0, 0, (float)w, (float)h, 0.0f, 1.0f };
			SDL_SetGPUViewport(pass, &vp);
			SDL_BindGPUGraphicsPipeline(pass, m_composite);
			SDL_GPUTextureSamplerBinding tb{ m_target, m_linear_clamp };
			SDL_BindGPUFragmentSamplers(pass, 0, &tb, 1);
			SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
			SDL_EndGPURenderPass(pass);
		}
	}
}
