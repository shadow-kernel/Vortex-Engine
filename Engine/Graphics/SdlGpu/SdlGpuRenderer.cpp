#include "SdlGpuRenderer.h"
#include "../../Common/Platform.h"
#include "../../Common/VerboseLog.h"
#if VORTEX_PLATFORM_APPLE
#include <objc/message.h>
#include <objc/runtime.h>
#endif
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>

namespace vortex::graphics::sdlgpu
{
	namespace
	{
		void log(const std::string& s) { platform::debug_output(("[sdlgpu] " + s + "\n").c_str()); }

		bool read_text_file(const std::string& path, std::string& out)
		{
			std::ifstream f(path, std::ios::binary);
			if (!f) return false;
			out.assign((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
			return true;
		}

		// The shaders directory: next to the executable (shipped), the CMake build tree, or the source tree.
		std::string find_shader_dir(const std::string& override_dir)
		{
			const std::string env = platform::env_string("VORTEX_SHADER_DIR");
			std::vector<std::string> candidates;
			if (!override_dir.empty()) candidates.push_back(override_dir);
			if (!env.empty()) candidates.push_back(env);
			const std::string exe = platform::executable_directory();
			candidates.push_back(exe + "Shaders/msl");
			candidates.push_back(exe + "../Shaders/msl");
			candidates.push_back(exe + "../Resources/Shaders/msl");   // inside a .app bundle
			candidates.push_back(exe + "../../Engine/Shaders/msl");
			candidates.push_back(exe + "../../../Engine/Shaders/msl");
			candidates.push_back("Engine/Shaders/msl");
			for (const std::string& c : candidates)
			{
				std::error_code ec;
				if (std::filesystem::is_regular_file(std::filesystem::path(c) / "standard.metal", ec))
					return std::filesystem::absolute(c, ec).string();
			}
			return {};
		}
	}

	SdlGpuRenderer& SdlGpuRenderer::instance()
	{
		static SdlGpuRenderer inst;
		return inst;
	}

	// ---------------------------------------------------------------------------------------------
	// Initialization
	// ---------------------------------------------------------------------------------------------
	bool SdlGpuRenderer::initialize(const RendererDesc& desc)
	{
		if (m_initialized) return true;
		if (!desc.native_window) { log("initialize: no native window"); return false; }

		if (!SDL_WasInit(SDL_INIT_VIDEO))
		{
			if (!SDL_Init(SDL_INIT_VIDEO)) { log(std::string("SDL_Init failed: ") + SDL_GetError()); return false; }
			m_sdl_video_inited_here = true;
		}

		m_width = desc.width; m_height = desc.height;
		if (!create_device()) return false;

		m_main.window = create_or_wrap_window(desc.native_window, desc.native_is_sdl_window, desc.width, desc.height);
		m_main.owns_window = !desc.native_is_sdl_window;
		if (!m_main.window || !claim_window(m_main.window)) { shutdown(); return false; }
		m_present_format = SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM;   // present targets are ours; the swapchain blit converts

		m_shader_dir = find_shader_dir(m_shader_dir_override);
		if (m_shader_dir.empty()) { log("shader directory not found (Engine/Shaders/msl)"); shutdown(); return false; }
		log("shaders: " + m_shader_dir);
		if (!load_shaders() || !create_pipelines() || !create_dynamic_buffers()) { shutdown(); return false; }

		SDL_GPUSamplerCreateInfo sci{};
		sci.min_filter = sci.mag_filter = SDL_GPU_FILTER_LINEAR;
		sci.mipmap_mode = SDL_GPU_SAMPLERMIPMAPMODE_LINEAR;
		sci.address_mode_u = sci.address_mode_v = sci.address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_REPEAT;
		sci.enable_anisotropy = true; sci.max_anisotropy = 16.0f;
		sci.max_lod = 1000.0f;
		m_sampler_linear_wrap = SDL_CreateGPUSampler(m_device, &sci);
		sci.address_mode_u = sci.address_mode_v = sci.address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
		sci.enable_anisotropy = false; sci.max_anisotropy = 1.0f;
		m_sampler_linear_clamp = SDL_CreateGPUSampler(m_device, &sci);
		if (!m_sampler_linear_wrap || !m_sampler_linear_clamp) { log("sampler creation failed"); shutdown(); return false; }

		ResourceRegistry::instance().initialize(m_device);
		if (!m_overlay.initialize(m_device, m_shader_dir, m_present_format))
			log("UI overlay unavailable (continuing without it)");
		if (!m_particles.initialize(m_device, m_shader_dir, m_scene_format, m_sampler_linear_wrap, m_sampler_linear_clamp))
			log("particles unavailable (continuing without them)");

		m_initialized = true;
		log("initialized (" + m_gpu_name + ")");
		return true;
	}

	bool SdlGpuRenderer::create_device()
	{
		const bool debug = platform::env_flag("VORTEX_GPU_DEBUG");
		m_device = SDL_CreateGPUDevice(SDL_GPU_SHADERFORMAT_MSL, debug, nullptr);
		if (!m_device) { log(std::string("SDL_CreateGPUDevice failed: ") + SDL_GetError()); return false; }
		const char* driver = SDL_GetGPUDeviceDriver(m_device);
		m_gpu_name = driver ? driver : "gpu";
#ifdef SDL_PROP_GPU_DEVICE_NAME_STRING
		const SDL_PropertiesID props = SDL_GetGPUDeviceProperties(m_device);
		if (props)
		{
			const char* name = SDL_GetStringProperty(props, SDL_PROP_GPU_DEVICE_NAME_STRING, nullptr);
			if (name && *name) m_gpu_name = std::string(name) + " (" + m_gpu_name + ")";
		}
#endif
		return true;
	}

#if VORTEX_PLATFORM_APPLE
	// Minimal Objective-C runtime bridge for the editor-embedded viewport (this is a .cpp file; the few AppKit
	// calls below are made through objc_msgSend so the engine core stays free of Objective-C++).
	namespace objc
	{
		using id_t = void*;
		struct Rect { double x, y, w, h; };
		inline SEL sel(const char* s) { return sel_registerName(s); }
		inline id_t msg_id(id_t o, const char* s) { return reinterpret_cast<id_t (*)(id_t, SEL)>(objc_msgSend)(o, sel(s)); }
		inline void msg_void(id_t o, const char* s) { reinterpret_cast<void (*)(id_t, SEL)>(objc_msgSend)(o, sel(s)); }
		inline void msg_void_id(id_t o, const char* s, id_t a) { reinterpret_cast<void (*)(id_t, SEL, id_t)>(objc_msgSend)(o, sel(s), a); }
		inline void msg_void_ulong(id_t o, const char* s, unsigned long a) { reinterpret_cast<void (*)(id_t, SEL, unsigned long)>(objc_msgSend)(o, sel(s), a); }
		inline unsigned long msg_ulong(id_t o, const char* s) { return reinterpret_cast<unsigned long (*)(id_t, SEL)>(objc_msgSend)(o, sel(s)); }
		inline id_t msg_id_ulong(id_t o, const char* s, unsigned long a) { return reinterpret_cast<id_t (*)(id_t, SEL, unsigned long)>(objc_msgSend)(o, sel(s), a); }
		inline bool is_kind_of(id_t o, Class c) { return reinterpret_cast<BOOL (*)(id_t, SEL, Class)>(objc_msgSend)(o, sel("isKindOfClass:"), c) != NO; }
		inline Rect msg_rect(id_t o, const char* s)
		{
#if defined(__x86_64__)
			return reinterpret_cast<Rect (*)(id_t, SEL)>(objc_msgSend_stret)(o, sel(s));   // 32-byte struct: hidden return pointer
#else
			return reinterpret_cast<Rect (*)(id_t, SEL)>(objc_msgSend)(o, sel(s));         // arm64: homogeneous float aggregate in v0-v3
#endif
		}
		inline void msg_set_rect(id_t o, const char* s, Rect r) { reinterpret_cast<void (*)(id_t, SEL, Rect)>(objc_msgSend)(o, sel(s), r); }

		// SDL's Metal view class (SDL 3.4 renamed it with the SDL3_ prefix).
		inline Class metal_view_class()
		{
			Class c = objc_getClass("SDL3_cocoametalview");
			return c ? c : objc_getClass("SDL_cocoametalview");
		}

		// The toolkit's host NSView is a plain NSView, which refuses the first click on a non-key window (AppKit
		// only activates the window then). A 3D viewport should act on that click like any canvas does, so the
		// host view is re-classed at runtime into a subclass that accepts first-mouse events.
		BOOL host_accepts_first_mouse(id_t, SEL, id_t) { return YES; }
		void make_host_accept_first_mouse(id_t host)
		{
			Class sub = objc_getClass("VortexViewportHostView");
			if (!sub)
			{
				Class base = object_getClass(reinterpret_cast<::id>(host));
				sub = objc_allocateClassPair(base, "VortexViewportHostView", 0);
				if (!sub) return;
				class_addMethod(sub, sel("acceptsFirstMouse:"), reinterpret_cast<IMP>(host_accepts_first_mouse), "B@:@");
				objc_registerClassPair(sub);
			}
			object_setClass(reinterpret_cast<::id>(host), sub);
		}
	}
#endif

	SDL_Window* SdlGpuRenderer::create_or_wrap_window(void* native, bool is_sdl_window, u32 w, u32 h)
	{
		if (is_sdl_window) return static_cast<SDL_Window*>(native);
		// Editor-embedded viewport: wrap the host's NSView so SDL attaches its Metal layer to it.
		SDL_PropertiesID props = SDL_CreateProperties();
#if VORTEX_PLATFORM_APPLE
		// UI toolkits (Avalonia, WPF-like shells) keep their NSWindow non-opaque so they can offer window
		// transparency. SDL mirrors [NSWindow isOpaque] into SDL_WINDOW_TRANSPARENT and the GPU API refuses to
		// claim a transparent window - the editor viewport is always opaque, so mark the host window opaque first.
		{
			objc::id_t ns_window = objc::msg_id(native, "window");
			if (!ns_window) { log("viewport: the host view is not inside a window yet"); SDL_DestroyProperties(props); return nullptr; }
			reinterpret_cast<void (*)(objc::id_t, SEL, BOOL)>(objc_msgSend)(ns_window, objc::sel("setOpaque:"), YES);
			// SDL is handed the NSWindow, not the host view: given a view, SDL 3.x installs it as the window's
			// content view ([NSWindow setContentView:]), which throws the toolkit's own view - the whole editor UI -
			// out of the window. With the window, SDL keeps the existing content view and only adds its Metal layer
			// to it; claim_window() then moves that layer into the host view (the viewport panel) and restores the
			// responder chain SDL rewires to its own event listener.
			m_host_view = native;
			m_host_metal_view = nullptr;
			m_host_content_view = objc::msg_id(ns_window, "contentView");
			m_host_content_prev_responder = m_host_content_view ? objc::msg_id(m_host_content_view, "nextResponder") : nullptr;
			m_host_window_prev_responder = objc::msg_id(ns_window, "nextResponder");
			SDL_SetPointerProperty(props, SDL_PROP_WINDOW_CREATE_COCOA_WINDOW_POINTER, ns_window);
		}
#else
		(void)native;
#endif
		SDL_SetBooleanProperty(props, SDL_PROP_WINDOW_CREATE_HIGH_PIXEL_DENSITY_BOOLEAN, true);
		SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER, (Sint64)(w ? w : 1));
		SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER, (Sint64)(h ? h : 1));
		SDL_Window* window = SDL_CreateWindowWithProperties(props);
		SDL_DestroyProperties(props);
		if (!window) log(std::string("SDL_CreateWindowWithProperties failed: ") + SDL_GetError());
		return window;
	}

	bool SdlGpuRenderer::claim_window(SDL_Window* window)
	{
		if (!SDL_ClaimWindowForGPUDevice(m_device, window))
		{
			log(std::string("SDL_ClaimWindowForGPUDevice failed: ") + SDL_GetError());
			return false;
		}
		apply_swapchain_params(window);
		if (window == m_main.window && m_host_view) attach_metal_view_to_host();
		return true;
	}

	// Editor-embedded viewport only. SDL (3.x) always parents the Metal layer it creates for a wrapped view to the
	// NSWindow's *content view*, sized to the whole window - in the editor that covers every panel with the 3D
	// view. Move the layer into the host view the editor gave us (the viewport panel's own NSView) and keep it
	// sized to that view; the swapchain follows the layer's drawable size. SDL also makes itself the next
	// responder of the host view and the window to read mouse events; the editor toolkit owns input on this
	// window (SDL never pumps it), so the original responder chain is restored - clicks, drags and wheel events
	// over the viewport reach the toolkit again.
	void SdlGpuRenderer::attach_metal_view_to_host()
	{
#if VORTEX_PLATFORM_APPLE
		if (!m_host_view) return;
		objc::id_t host = m_host_view;
		objc::id_t window = objc::msg_id(host, "window");
		objc::id_t content = window ? objc::msg_id(window, "contentView") : nullptr;
		objc::id_t metal = nullptr;
		if (Class cls = objc::metal_view_class(); cls && content)
		{
			objc::id_t subviews = objc::msg_id(content, "subviews");
			const unsigned long n = subviews ? objc::msg_ulong(subviews, "count") : 0;
			for (unsigned long i = 0; i < n && !metal; ++i)
			{
				objc::id_t v = objc::msg_id_ulong(subviews, "objectAtIndex:", i);
				if (v && objc::is_kind_of(v, cls)) metal = v;
			}
		}
		if (metal)
		{
			const objc::Rect b = objc::msg_rect(host, "bounds");
			objc::msg_void(metal, "removeFromSuperview");
			objc::msg_void_ulong(metal, "setAutoresizingMask:", 0);   // sized explicitly by sync_host_metal_view()
			objc::msg_set_rect(metal, "setFrame:", objc::Rect{ 0.0, 0.0, b.w, b.h });
			objc::msg_void_id(host, "addSubview:", metal);
			objc::msg_void(metal, "updateDrawableSize");
			objc::make_host_accept_first_mouse(host);
			m_host_metal_view = metal;
			log("viewport: Metal layer attached to the host view (" + std::to_string((int)b.w) + "x" + std::to_string((int)b.h) + " pt)");
		}
		else log("viewport: SDL Metal view not found under the window's content view - the 3D view may cover the whole window");
		// SDL made its event listener the next responder of the content view and of the window; the toolkit owns
		// input on this window (SDL never pumps it), so put the original chain back.
		if (m_host_content_view) objc::msg_void_id(m_host_content_view, "setNextResponder:", m_host_content_prev_responder);
		if (window) objc::msg_void_id(window, "setNextResponder:", m_host_window_prev_responder);
#endif
	}

	// Keep the Metal layer exactly the size of the host view (the viewport panel). Called every frame and on
	// resize: the toolkit re-frames the host view during layout, and SDL only refreshes the drawable size on
	// whole-window size events, so a panel-only resize (splitter drag) would otherwise stretch the old drawable.
	void SdlGpuRenderer::sync_host_metal_view()
	{
#if VORTEX_PLATFORM_APPLE
		if (!m_host_view || !m_host_metal_view) return;
		const objc::Rect b = objc::msg_rect(m_host_view, "bounds");
		const objc::Rect f = objc::msg_rect(m_host_metal_view, "frame");
		if (f.x != 0.0 || f.y != 0.0 || f.w != b.w || f.h != b.h)
		{
			objc::msg_set_rect(m_host_metal_view, "setFrame:", objc::Rect{ 0.0, 0.0, b.w, b.h });
			objc::msg_void(m_host_metal_view, "updateDrawableSize");
		}
#endif
	}

	void SdlGpuRenderer::apply_swapchain_params(SDL_Window* window)
	{
		SDL_GPUPresentMode mode = SDL_GPU_PRESENTMODE_VSYNC;
		if (!m_vsync_enabled)
		{
			if (SDL_WindowSupportsGPUPresentMode(m_device, window, SDL_GPU_PRESENTMODE_IMMEDIATE)) mode = SDL_GPU_PRESENTMODE_IMMEDIATE;
			else if (SDL_WindowSupportsGPUPresentMode(m_device, window, SDL_GPU_PRESENTMODE_MAILBOX)) mode = SDL_GPU_PRESENTMODE_MAILBOX;
		}
		SDL_SetGPUSwapchainParameters(m_device, window, SDL_GPU_SWAPCHAINCOMPOSITION_SDR, mode);
	}

	SDL_GPUShader* SdlGpuRenderer::create_shader(const std::string& source, const char* entry, SDL_GPUShaderStage stage,
		u32 samplers, u32 storage_buffers, u32 uniform_buffers)
	{
		SDL_GPUShaderCreateInfo ci{};
		ci.code = reinterpret_cast<const Uint8*>(source.c_str());
		ci.code_size = source.size() + 1;
		ci.entrypoint = entry;
		ci.format = SDL_GPU_SHADERFORMAT_MSL;
		ci.stage = stage;
		ci.num_samplers = samplers;
		ci.num_storage_buffers = storage_buffers;
		ci.num_uniform_buffers = uniform_buffers;
		SDL_GPUShader* sh = SDL_CreateGPUShader(m_device, &ci);
		if (!sh) log(std::string("shader '") + entry + "' failed: " + SDL_GetError());
		return sh;
	}

	bool SdlGpuRenderer::load_shaders()
	{
		const char* files[] = { "standard.metal", "grid.metal", "skybox.metal", "postfx.metal", "ssao.metal", "bloom.metal" };
		for (const char* f : files)
		{
			std::string src;
			if (!read_text_file(m_shader_dir + "/" + f, src)) { log(std::string("missing shader ") + f); return false; }
			m_shader_sources[f] = std::move(src);
		}
		const std::string& standard = m_shader_sources["standard.metal"];
		m_vs_standard = create_shader(standard, "VSMain", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
		m_vs_skinned = create_shader(standard, "VSSkinned", SDL_GPU_SHADERSTAGE_VERTEX, 0, 1, 2);
		m_fs_standard = create_shader(standard, "PSMain", SDL_GPU_SHADERSTAGE_FRAGMENT, 10, 0, 3);
		m_vs_grid = create_shader(m_shader_sources["grid.metal"], "GridVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
		m_fs_grid = create_shader(m_shader_sources["grid.metal"], "GridPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 0, 0, 1);
		m_vs_sky = create_shader(m_shader_sources["skybox.metal"], "SkyVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
		m_fs_sky = create_shader(m_shader_sources["skybox.metal"], "SkyPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 0, 0, 1);
		m_vs_blit = create_shader(m_shader_sources["postfx.metal"], "BlitVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 0);
		m_fs_blit = create_shader(m_shader_sources["postfx.metal"], "BlitPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0, 0);
		m_fs_postfx = create_shader(m_shader_sources["postfx.metal"], "PostFxPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 2, 0, 1);
		return m_vs_standard && m_fs_standard && m_vs_grid && m_fs_grid && m_vs_sky && m_fs_sky && m_vs_blit && m_fs_blit && m_fs_postfx;
	}

	SDL_GPUGraphicsPipeline* SdlGpuRenderer::create_scene_pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, u32 stride, bool skinned_layout,
		SDL_GPUFillMode fill, SDL_GPUCullMode cull, bool depth_test, bool depth_write, SDL_GPUCompareOp depth_op, u32 blend_mode)
	{
		SDL_GPUVertexBufferDescription buffers[2] = {
			{ 0, stride, SDL_GPU_VERTEXINPUTRATE_VERTEX, 0 },
			{ 1, 64, SDL_GPU_VERTEXINPUTRATE_INSTANCE, 0 },
		};
		std::vector<SDL_GPUVertexAttribute> attrs = {
			{ 0, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, 0 },
			{ 1, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, 12 },
			{ 2, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, 24 },
		};
		u32 loc = 3;
		if (skinned_layout)
		{
			attrs.push_back({ loc++, 0, SDL_GPU_VERTEXELEMENTFORMAT_UBYTE4, 32 });
			attrs.push_back({ loc++, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, 36 });
		}
		for (u32 r = 0; r < 4; ++r) attrs.push_back({ loc++, 1, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, r * 16 });

		SDL_GPUColorTargetDescription color{};
		color.format = m_scene_format;
		if (blend_mode == 1 || blend_mode == 2)
		{
			color.blend_state.enable_blend = true;
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_ALPHA;
			color.blend_state.dst_color_blendfactor = blend_mode == 1 ? SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA : SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.color_blend_op = SDL_GPU_BLENDOP_ADD;
			color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.dst_alpha_blendfactor = blend_mode == 1 ? SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA : SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.alpha_blend_op = SDL_GPU_BLENDOP_ADD;
		}

		SDL_GPUGraphicsPipelineCreateInfo pci{};
		pci.vertex_shader = vs;
		pci.fragment_shader = fs;
		pci.vertex_input_state.vertex_buffer_descriptions = buffers;
		pci.vertex_input_state.num_vertex_buffers = 2;
		pci.vertex_input_state.vertex_attributes = attrs.data();
		pci.vertex_input_state.num_vertex_attributes = (Uint32)attrs.size();
		pci.primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
		pci.rasterizer_state.fill_mode = fill;
		pci.rasterizer_state.cull_mode = cull;
		pci.rasterizer_state.front_face = SDL_GPU_FRONTFACE_CLOCKWISE;   // D3D convention (the mesh generators wind clockwise)
		pci.rasterizer_state.enable_depth_clip = true;
		pci.multisample_state.sample_count = SDL_GPU_SAMPLECOUNT_1;
		pci.depth_stencil_state.enable_depth_test = depth_test;
		pci.depth_stencil_state.enable_depth_write = depth_write;
		pci.depth_stencil_state.compare_op = depth_op;
		pci.target_info.color_target_descriptions = &color;
		pci.target_info.num_color_targets = 1;
		pci.target_info.depth_stencil_format = m_depth_format;
		pci.target_info.has_depth_stencil_target = true;
		SDL_GPUGraphicsPipeline* p = SDL_CreateGPUGraphicsPipeline(m_device, &pci);
		if (!p) log(std::string("scene pipeline failed: ") + SDL_GetError());
		return p;
	}

	SDL_GPUGraphicsPipeline* SdlGpuRenderer::create_fullscreen_pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, SDL_GPUTextureFormat color_format,
		bool depth, bool blend)
	{
		SDL_GPUColorTargetDescription color{};
		color.format = color_format;
		if (blend)
		{
			color.blend_state.enable_blend = true;
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_ALPHA;
			color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
			color.blend_state.color_blend_op = SDL_GPU_BLENDOP_ADD;
			color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ZERO;
			color.blend_state.alpha_blend_op = SDL_GPU_BLENDOP_ADD;
		}
		SDL_GPUGraphicsPipelineCreateInfo pci{};
		pci.vertex_shader = vs;
		pci.fragment_shader = fs;
		pci.primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
		pci.rasterizer_state.fill_mode = SDL_GPU_FILLMODE_FILL;
		pci.rasterizer_state.cull_mode = SDL_GPU_CULLMODE_NONE;
		pci.rasterizer_state.front_face = SDL_GPU_FRONTFACE_CLOCKWISE;
		pci.rasterizer_state.enable_depth_clip = !depth ? true : false;   // grid/skybox reach the far plane
		pci.multisample_state.sample_count = SDL_GPU_SAMPLECOUNT_1;
		pci.depth_stencil_state.enable_depth_test = depth;
		pci.depth_stencil_state.enable_depth_write = false;
		pci.depth_stencil_state.compare_op = SDL_GPU_COMPAREOP_LESS_OR_EQUAL;
		pci.target_info.color_target_descriptions = &color;
		pci.target_info.num_color_targets = 1;
		pci.target_info.depth_stencil_format = m_depth_format;
		pci.target_info.has_depth_stencil_target = depth;
		SDL_GPUGraphicsPipeline* p = SDL_CreateGPUGraphicsPipeline(m_device, &pci);
		if (!p) log(std::string("fullscreen pipeline failed: ") + SDL_GetError());
		return p;
	}

	bool SdlGpuRenderer::create_pipeline_set(PipelineSet& set, u32 stride, bool skinned_layout)
	{
		(void)skinned_layout;   // the rigid shading pipelines read pos/normal/uv only; the stride skips the skin data
		set.opaque = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, true, SDL_GPU_COMPAREOP_LESS, 0);
		set.wireframe = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_LINE, SDL_GPU_CULLMODE_NONE, true, true, SDL_GPU_COMPAREOP_LESS, 0);
		set.double_sided = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_NONE, true, true, SDL_GPU_COMPAREOP_LESS, 0);
		set.gizmo = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_NONE, false, false, SDL_GPU_COMPAREOP_ALWAYS, 0);
		set.gizmo_wire = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_LINE, SDL_GPU_CULLMODE_NONE, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 0);
		set.alpha = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 1);
		set.alpha_ds = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_NONE, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 1);
		set.additive = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 2);
		set.additive_ds = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_NONE, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 2);
		return set.opaque && set.wireframe && set.double_sided && set.gizmo && set.gizmo_wire && set.alpha && set.alpha_ds && set.additive && set.additive_ds;
	}

	bool SdlGpuRenderer::create_pipelines()
	{
		if (!create_pipeline_set(m_pipelines, 32, false)) return false;
		if (!create_pipeline_set(m_pipelines_skinned_stride, 52, false)) return false;
		m_pipeline_skinned = create_scene_pipeline(m_vs_skinned, m_fs_standard, 52, true, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, true, SDL_GPU_COMPAREOP_LESS, 0);
		m_pipeline_grid = create_fullscreen_pipeline(m_vs_grid, m_fs_grid, m_scene_format, true, true);
		m_pipeline_skybox = create_fullscreen_pipeline(m_vs_sky, m_fs_sky, m_scene_format, true, false);
		if (!ensure_shadow_resources()) log("shadow maps unavailable on this device - rendering without shadows");
		if (!ensure_postfx_resources()) log("SSAO/bloom unavailable on this device");
		m_pipeline_blit = create_fullscreen_pipeline(m_vs_blit, m_fs_blit, m_present_format, false, false);
		m_pipeline_postfx = create_fullscreen_pipeline(m_vs_blit, m_fs_postfx, m_present_format, false, false);
		if (!m_pipeline_skinned) log("skinned pipeline unavailable — skinned meshes render in bind pose");
		return m_pipeline_grid && m_pipeline_skybox && m_pipeline_blit && m_pipeline_postfx;
	}

	bool SdlGpuRenderer::create_dynamic_buffers()
	{
		SDL_GPUBufferCreateInfo bci{};
		bci.usage = SDL_GPU_BUFFERUSAGE_VERTEX;
		bci.size = MAX_RENDER_OBJECTS * 64;
		m_instance_buffer = SDL_CreateGPUBuffer(m_device, &bci);
		SDL_GPUTransferBufferCreateInfo tci{};
		tci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD;
		tci.size = MAX_RENDER_OBJECTS * 64;
		m_instance_transfer = SDL_CreateGPUTransferBuffer(m_device, &tci);

		bci.usage = SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ;
		bci.size = MAX_BONE_MATRICES * 64;
		m_bone_buffer = SDL_CreateGPUBuffer(m_device, &bci);
		tci.size = MAX_BONE_MATRICES * 64;
		m_bone_transfer = SDL_CreateGPUTransferBuffer(m_device, &tci);
		if (!m_instance_buffer || !m_instance_transfer || !m_bone_buffer || !m_bone_transfer)
		{
			log(std::string("dynamic buffer creation failed: ") + SDL_GetError());
			return false;
		}
		m_instance_staging.reserve(4096 * 16);
		return true;
	}

	bool SdlGpuRenderer::ensure_target(GpuTarget& target, u32 w, u32 h, bool with_depth, bool readback)
	{
		if (w < 1) w = 1;
		if (h < 1) h = 1;
		if (target.color && target.width == w && target.height == h && (!with_depth || target.depth) && (!readback || target.readback))
			return true;
		wait_idle();
		release_target(target);

		SDL_GPUTextureCreateInfo tci{};
		tci.type = SDL_GPU_TEXTURETYPE_2D;
		tci.format = m_scene_format;
		tci.usage = SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER;
		tci.width = w; tci.height = h;
		tci.layer_count_or_depth = 1;
		tci.num_levels = 1;
		tci.sample_count = SDL_GPU_SAMPLECOUNT_1;
		target.color = SDL_CreateGPUTexture(m_device, &tci);
		if (with_depth)
		{
			tci.format = m_depth_format;
			// sampled by the particle passes (depth test + soft particles) and the collision snapshot
			tci.usage = SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER;
			target.depth = SDL_CreateGPUTexture(m_device, &tci);
		}
		if (readback)
		{
			SDL_GPUTransferBufferCreateInfo rci{};
			rci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD;
			rci.size = w * h * 4;
			target.readback = SDL_CreateGPUTransferBuffer(m_device, &rci);
			target.readback_size = w * h * 4;
		}
		target.width = w; target.height = h;
		if (!target.color || (with_depth && !target.depth) || (readback && !target.readback))
		{
			log(std::string("render target creation failed: ") + SDL_GetError());
			release_target(target);
			return false;
		}
		return true;
	}

	void SdlGpuRenderer::release_target(GpuTarget& target)
	{
		if (m_device)
		{
			if (target.readback_mapped) { SDL_UnmapGPUTransferBuffer(m_device, target.readback); target.readback_mapped = nullptr; }
			if (target.color) SDL_ReleaseGPUTexture(m_device, target.color);
			if (target.depth) SDL_ReleaseGPUTexture(m_device, target.depth);
			if (target.readback) SDL_ReleaseGPUTransferBuffer(m_device, target.readback);
		}
		target = GpuTarget{};
	}

	void SdlGpuRenderer::release_pipeline_set(PipelineSet& set)
	{
		SDL_GPUGraphicsPipeline** all[] = { &set.opaque, &set.wireframe, &set.double_sided, &set.gizmo, &set.gizmo_wire,
			&set.alpha, &set.alpha_ds, &set.additive, &set.additive_ds };
		for (auto** p : all) { if (*p) SDL_ReleaseGPUGraphicsPipeline(m_device, *p); *p = nullptr; }
	}

	void SdlGpuRenderer::wait_idle()
	{
		if (m_device) SDL_WaitForGPUIdle(m_device);
	}

	void SdlGpuRenderer::shutdown()
	{
		if (!m_device) return;
		wait_idle();
		destroy_postfx_resources();
		destroy_shadow_resources();
		m_overlay.shutdown();
		m_particles.shutdown();
		::vortex::particles::on_renderer_shutdown();   // the engine texture ids the emitters hold die with the registry
		destroy_game_window();
		for (auto& [id, target] : m_render_targets) release_target(*target);
		m_render_targets.clear();
		ResourceRegistry::instance().shutdown();
		{
			std::lock_guard<std::mutex> lock(m_queue_mutex);
			m_render_queue.clear(); m_submit_queue.clear();
			m_gizmo_render.clear(); m_gizmo_submit.clear(); m_gizmo_wire_render.clear(); m_gizmo_wire_submit.clear();
		}
		for (auto& [id, cs] : m_custom_shaders) cs.pipeline = nullptr;
		m_custom_shaders.clear();
		for (auto& [path, cp] : m_pipeline_cache) if (cp.pipeline) SDL_ReleaseGPUGraphicsPipeline(m_device, cp.pipeline);
		m_pipeline_cache.clear();
		release_pipeline_set(m_pipelines);
		release_pipeline_set(m_pipelines_skinned_stride);
		SDL_GPUGraphicsPipeline** singles[] = { &m_pipeline_skinned, &m_pipeline_grid, &m_pipeline_skybox, &m_pipeline_blit, &m_pipeline_postfx };
		for (auto** p : singles) { if (*p) SDL_ReleaseGPUGraphicsPipeline(m_device, *p); *p = nullptr; }
		SDL_GPUShader** shaders[] = { &m_vs_standard, &m_vs_skinned, &m_fs_standard, &m_vs_grid, &m_fs_grid, &m_vs_sky, &m_fs_sky, &m_vs_blit, &m_fs_blit, &m_fs_postfx };
		for (auto** s : shaders) { if (*s) SDL_ReleaseGPUShader(m_device, *s); *s = nullptr; }
		if (m_sampler_linear_wrap) SDL_ReleaseGPUSampler(m_device, m_sampler_linear_wrap);
		if (m_sampler_linear_clamp) SDL_ReleaseGPUSampler(m_device, m_sampler_linear_clamp);
		m_sampler_linear_wrap = m_sampler_linear_clamp = nullptr;
		if (m_instance_buffer) SDL_ReleaseGPUBuffer(m_device, m_instance_buffer);
		if (m_instance_transfer) SDL_ReleaseGPUTransferBuffer(m_device, m_instance_transfer);
		if (m_bone_buffer) SDL_ReleaseGPUBuffer(m_device, m_bone_buffer);
		if (m_bone_transfer) SDL_ReleaseGPUTransferBuffer(m_device, m_bone_transfer);
		m_instance_buffer = nullptr; m_instance_transfer = nullptr; m_bone_buffer = nullptr; m_bone_transfer = nullptr;
		release_target(m_main.scene); release_target(m_main.present); release_target(m_main.postfx);
		if (m_main.window)
		{
			SDL_ReleaseWindowFromGPUDevice(m_device, m_main.window);   // removes the Metal view from the host view
			if (m_main.owns_window) SDL_DestroyWindow(m_main.window);
			m_main.window = nullptr;
		}
		m_host_view = nullptr; m_host_metal_view = nullptr; m_host_content_view = nullptr;
		m_host_content_prev_responder = nullptr; m_host_window_prev_responder = nullptr;
		SDL_DestroyGPUDevice(m_device);
		m_device = nullptr;
		m_shader_sources.clear();
		m_initialized = false;
		if (m_sdl_video_inited_here) { SDL_QuitSubSystem(SDL_INIT_VIDEO); m_sdl_video_inited_here = false; }
	}

	void SdlGpuRenderer::resize(u32 w, u32 h)
	{
		if (!m_initialized || w == 0 || h == 0) return;
		m_width = w; m_height = h;   // the swapchain follows the host view / window; targets are re-sized on the next frame
		sync_host_metal_view();
	}

	// ---------------------------------------------------------------------------------------------
	// Frames
	// ---------------------------------------------------------------------------------------------
	void SdlGpuRenderer::render_frame()
	{
		if (!m_initialized) return;
		// Particles: hand finished collision readbacks to the simulation, then let the frame driver (the managed
		// ParticleService callback, or the automatic world-0 update) step it before anything is gathered.
		m_particles.begin_frame();
		::vortex::particles::begin_frame();
		swap_render_queue();

		m_frame_count++;
		auto now = std::chrono::steady_clock::now();
		auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - m_last_fps_time).count();
		if (elapsed >= 500)
		{
			m_current_fps = static_cast<int>((m_frame_count * 1000) / elapsed);
			m_frame_count = 0;
			m_last_fps_time = now;
		}
		m_draw_call_count = 0; m_vertex_count = 0; m_instances_tested = 0; m_instances_drawn = 0;

		if (m_vsync_dirty) { m_vsync_dirty = false; apply_swapchain_params(m_main.window); if (m_game.window) apply_swapchain_params(m_game.window); }
		sync_host_metal_view();
		render_surface(m_main, 0);
	}

	void SdlGpuRenderer::render_surface(WindowSurface& surface, int slot)
	{
		SDL_GPUCommandBuffer* cmd = SDL_AcquireGPUCommandBuffer(m_device);
		if (!cmd) { log(std::string("AcquireGPUCommandBuffer failed: ") + SDL_GetError()); return; }
		SDL_GPUTexture* swapchain = nullptr;
		u32 w = 0, h = 0;
		if (!SDL_WaitAndAcquireGPUSwapchainTexture(cmd, surface.window, &swapchain, &w, &h))
		{
			log(std::string("swapchain acquire failed: ") + SDL_GetError());
			SDL_SubmitGPUCommandBuffer(cmd);
			return;
		}
		if (!swapchain || w == 0 || h == 0) { SDL_SubmitGPUCommandBuffer(cmd); return; }   // minimized / occluded
		if (w != surface.last_w || h != surface.last_h)
		{
			surface.last_w = w; surface.last_h = h;
			log(std::string(slot == 0 ? "main" : "game") + " surface: swapchain " + std::to_string(w) + "x" + std::to_string(h) + " px");
		}

		const bool post_on = m_postfx.active() && (slot == 1 || m_postfx.main_view_enabled());
		u32 rw = (u32)((float)w * m_render_scale + 0.5f), rh = (u32)((float)h * m_render_scale + 0.5f);
		if (rw < 1) rw = 1; if (rh < 1) rh = 1;
		if (!ensure_target(surface.scene, rw, rh, true, false) || !ensure_target(surface.present, w, h, false, true))
		{
			SDL_SubmitGPUCommandBuffer(cmd);
			return;
		}

		FrameView view = build_main_view(rw, rh);
		if (slot == 0) m_frame_constants = view.frame;
		record_scene(cmd, surface.scene, view, m_skybox_enabled, m_grid_visible, true, 0, true);

		// Composite the scene into the present target (render-scale upscale), through the post chain if active.
		if (post_on && ensure_target(surface.postfx, w, h, false, false))
		{
			fullscreen_blit(cmd, surface.scene.color, surface.postfx.color, w, h, m_pipeline_blit, nullptr);
			PostFxCB cb{};
			const auto& p = m_postfx.params();
			cb.texel[0] = 1.0f / (float)w; cb.texel[1] = 1.0f / (float)h;
			cb.time = elapsed_seconds();
			cb.flags = (p.vignette ? 1u : 0u) | (p.grain ? 2u : 0u) | (p.ca ? 4u : 0u) | (p.debug_invert ? 8u : 0u) | (p.grade ? 16u : 0u);
			cb.vignette[0] = p.vig_intensity; cb.vignette[1] = p.vig_smoothness; cb.vignette[2] = p.vig_roundness;
			cb.vignette_color[0] = p.vig_r; cb.vignette_color[1] = p.vig_g; cb.vignette_color[2] = p.vig_b;
			cb.grain_ca[0] = p.grain_intensity; cb.grain_ca[1] = p.grain_size; cb.grain_ca[2] = p.ca_strength; cb.grain_ca[3] = p.ca_falloff;
			cb.grade1[0] = p.exposure; cb.grade1[1] = p.contrast; cb.grade1[2] = p.saturation; cb.grade1[3] = p.temperature;
			cb.grade2[0] = p.tint;
			cb.bloom[0] = p.bloom_intensity;
			if (p.bloom && m_post_ready)
			{
				record_bloom(cmd, surface.scene.color, rw, rh);
				if (m_bloom_result) cb.flags |= 32u;
			}
			fullscreen_blit(cmd, surface.postfx.color, surface.present.color, w, h, m_pipeline_postfx, &cb, m_bloom_result);
		}
		else
		{
			fullscreen_blit(cmd, surface.scene.color, surface.present.color, w, h, m_pipeline_blit, nullptr);
		}

		// 2D overlay on top of the composite, then hand the present target to the swapchain.
		m_overlay.render(cmd, surface.present.color, w, h);
		composite_and_present(cmd, surface, slot, swapchain, w, h);
	}

	void SdlGpuRenderer::fullscreen_blit(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* src, SDL_GPUTexture* dst, u32 w, u32 h,
		SDL_GPUGraphicsPipeline* pipeline, const PostFxCB* postfx, SDL_GPUTexture* second)
	{
		SDL_GPUColorTargetInfo color{};
		color.texture = dst;
		color.load_op = SDL_GPU_LOADOP_DONT_CARE;
		color.store_op = SDL_GPU_STOREOP_STORE;
		SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, &color, 1, nullptr);
		if (!pass) return;
		SDL_BindGPUGraphicsPipeline(pass, pipeline);
		SDL_GPUViewport vp{ 0, 0, (float)w, (float)h, 0.0f, 1.0f };
		SDL_SetGPUViewport(pass, &vp);
		SDL_GPUTextureSamplerBinding bindings[2] = { { src, m_sampler_linear_clamp }, { second ? second : src, m_sampler_linear_clamp } };
		SDL_BindGPUFragmentSamplers(pass, 0, bindings, postfx ? 2 : 1);
		if (postfx) SDL_PushGPUFragmentUniformData(cmd, 0, postfx, sizeof(PostFxCB));
		SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
		SDL_EndGPURenderPass(pass);
	}

	void SdlGpuRenderer::composite_and_present(SDL_GPUCommandBuffer* cmd, WindowSurface& surface, int slot, SDL_GPUTexture* swapchain, u32 w, u32 h)
	{
		SDL_GPUBlitInfo blit{};
		blit.source.texture = surface.present.color;
		blit.source.w = w; blit.source.h = h;
		blit.destination.texture = swapchain;
		blit.destination.w = w; blit.destination.h = h;
		blit.load_op = SDL_GPU_LOADOP_DONT_CARE;
		blit.filter = SDL_GPU_FILTER_NEAREST;
		SDL_BlitGPUTexture(cmd, &blit);

		if (slot == 0 && m_capture_requested)
		{
			m_capture_requested = false;
			// Download the finished present target (3D + overlay) as part of this submission, then write it out.
			SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
			SDL_GPUTextureRegion region{}; region.texture = surface.present.color; region.w = w; region.h = h; region.d = 1;
			SDL_GPUTextureTransferInfo dst{ surface.present.readback, 0, w, h };
			SDL_DownloadFromGPUTexture(copy, &region, &dst);
			SDL_EndGPUCopyPass(copy);
			SDL_GPUFence* fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
			if (fence)
			{
				SDL_WaitForGPUFences(m_device, true, &fence, 1);
				SDL_ReleaseGPUFence(m_device, fence);
				capture_target_to_bmp(surface.present, m_capture_path.c_str());
			}
			return;
		}
		SDL_SubmitGPUCommandBuffer(cmd);
	}

	bool SdlGpuRenderer::capture_target_to_bmp(GpuTarget& target, const char* path)
	{
		if (!target.readback || !path) return false;
		void* mapped = SDL_MapGPUTransferBuffer(m_device, target.readback, false);
		if (!mapped) return false;
		const u32 width = target.width, height = target.height;
		FILE* f = nullptr;
		bool ok = false;
		if (fopen_s(&f, path, "wb") == 0 && f)
		{
			const u32 img_size = width * height * 4;
			const u32 file_size = 54 + img_size;
			unsigned char fh[14] = { 0 };
			fh[0] = 'B'; fh[1] = 'M';
			memcpy(&fh[2], &file_size, 4);
			const u32 offset = 54; memcpy(&fh[10], &offset, 4);
			fwrite(fh, 1, 14, f);
			unsigned char ih[40] = { 0 };
			const u32 hdr = 40; memcpy(&ih[0], &hdr, 4);
			const int w = (int)width, hneg = -(int)height;
			memcpy(&ih[4], &w, 4); memcpy(&ih[8], &hneg, 4);
			const unsigned short planes = 1, bpp = 32;
			memcpy(&ih[12], &planes, 2); memcpy(&ih[14], &bpp, 2);
			memcpy(&ih[20], &img_size, 4);
			fwrite(ih, 1, 40, f);
			// present targets are BGRA8: rows go out as-is (BMP is BGRA top-down with a negative height)
			fwrite(mapped, 1, img_size, f);
			fclose(f);
			ok = true;
		}
		SDL_UnmapGPUTransferBuffer(m_device, target.readback);
		return ok;
	}

	void SdlGpuRenderer::request_capture(const char* path)
	{
		if (!path) return;
		m_capture_path = path;
		m_capture_requested = true;
	}

	void SdlGpuRenderer::on_scene_switch()
	{
		if (!m_initialized) return;
		wait_idle();
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_render_queue.clear(); m_submit_queue.clear();
		m_bone_submit.clear(); m_bone_render.clear();
		m_bone_upload_pending = false;
		m_queue_dirty = true;
	}

	// ---------------------------------------------------------------------------------------------
	// Standalone game window
	// ---------------------------------------------------------------------------------------------
	bool SdlGpuRenderer::create_game_window(void* native_window, u32 width, u32 height)
	{
		if (!m_initialized || width == 0 || height == 0) return false;
		if (m_game.window) destroy_game_window();
		if (native_window)
		{
			m_game.window = create_or_wrap_window(native_window, false, width, height);
			m_game.owns_window = true;
		}
		else
		{
			m_game.window = SDL_CreateWindow("Vortex — Game", (int)width, (int)height, SDL_WINDOW_RESIZABLE | SDL_WINDOW_HIGH_PIXEL_DENSITY);
			m_game.owns_window = true;
		}
		if (!m_game.window || !claim_window(m_game.window))
		{
			if (m_game.window) { SDL_DestroyWindow(m_game.window); m_game.window = nullptr; }
			return false;
		}
		return true;
	}

	void SdlGpuRenderer::render_game_window()
	{
		if (!m_initialized || !m_game.window) return;
		render_surface(m_game, 1);
	}

	void SdlGpuRenderer::resize_game_window(u32 width, u32 height)
	{
		if (!m_game.window || width == 0 || height == 0) return;
		if (m_game.owns_window) SDL_SetWindowSize(m_game.window, (int)width, (int)height);
	}

	void SdlGpuRenderer::destroy_game_window()
	{
		if (!m_game.window) return;
		wait_idle();
		release_target(m_game.scene); release_target(m_game.present); release_target(m_game.postfx);
		SDL_ReleaseWindowFromGPUDevice(m_device, m_game.window);
		if (m_game.owns_window) SDL_DestroyWindow(m_game.window);
		m_game.window = nullptr;
	}

	// ---------------------------------------------------------------------------------------------
	// Custom material shaders (.metal, VSMain/PSMain with the standard bindings)
	// ---------------------------------------------------------------------------------------------
	unsigned long long SdlGpuRenderer::file_mtime(const std::string& path) const
	{
		std::error_code ec;
		auto t = std::filesystem::last_write_time(path, ec);
		if (ec) return 0ull;
		return (unsigned long long)t.time_since_epoch().count();
	}

	SDL_GPUGraphicsPipeline* SdlGpuRenderer::get_or_compile_pipeline(const std::string& path)
	{
		if (path.empty()) return nullptr;
		const unsigned long long mt = file_mtime(path);
		auto it = m_pipeline_cache.find(path);
		if (it != m_pipeline_cache.end() && it->second.pipeline && it->second.mtime == mt) return it->second.pipeline;

		std::string source;
		SDL_GPUGraphicsPipeline* pipeline = nullptr;
		if (path.size() > 6 && path.compare(path.size() - 6, 6, ".metal") == 0 && read_text_file(path, source))
		{
			SDL_GPUShader* vs = create_shader(source, "VSMain", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
			SDL_GPUShader* fs = create_shader(source, "PSMain", SDL_GPU_SHADERSTAGE_FRAGMENT, 10, 0, 3);
			if (vs && fs)
			{
				wait_idle();
				pipeline = create_scene_pipeline(vs, fs, 32, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, true, SDL_GPU_COMPAREOP_LESS, 0);
			}
			if (vs) SDL_ReleaseGPUShader(m_device, vs);
			if (fs) SDL_ReleaseGPUShader(m_device, fs);
		}
		else if (it == m_pipeline_cache.end())
		{
			log("custom material shader '" + path + "' is not a .metal file — the built-in PBR shader stays active on this backend");
		}
		auto& e = m_pipeline_cache[path];
		e.mtime = mt;
		if (pipeline)
		{
			if (e.pipeline) SDL_ReleaseGPUGraphicsPipeline(m_device, e.pipeline);
			e.pipeline = pipeline;
		}
		return e.pipeline;
	}

	void SdlGpuRenderer::set_material_shader(u32 material_id, const std::string& shader_path)
	{
		if (shader_path.empty()) { m_custom_shaders.erase(material_id); return; }
		auto& e = m_custom_shaders[material_id];
		e.path = shader_path;
		e.pipeline = get_or_compile_pipeline(shader_path);
		e.mtime = file_mtime(shader_path);
	}

	int SdlGpuRenderer::reload_dirty_shaders()
	{
		int changed = 0;
		for (auto& kv : m_pipeline_cache)
		{
			const unsigned long long mt = file_mtime(kv.first);
			if (mt == 0ull || mt == kv.second.mtime) continue;
			SDL_GPUGraphicsPipeline* before = kv.second.pipeline;
			kv.second.mtime = 0;   // force a rebuild
			if (get_or_compile_pipeline(kv.first) != before || kv.second.mtime == mt) ++changed;
		}
		if (changed == 0) return 0;
		for (auto& kv : m_custom_shaders)
		{
			auto it = m_pipeline_cache.find(kv.second.path);
			if (it != m_pipeline_cache.end()) { kv.second.pipeline = it->second.pipeline; kv.second.mtime = it->second.mtime; }
		}
		return changed;
	}

	bool SdlGpuRenderer::any_material_shader_dirty() const
	{
		for (auto& kv : m_pipeline_cache)
		{
			const unsigned long long mt = file_mtime(kv.first);
			if (mt != 0ull && mt != kv.second.mtime) return true;
		}
		return false;
	}

	// ---------------------------------------------------------------------------------------------
	// Overlay wide-string entry points (wchar_t is UTF-32 on macOS/Linux; the API's UTF-8 twins skip this)
	// ---------------------------------------------------------------------------------------------
	namespace
	{
		std::string wide_to_utf8(const wchar_t* s)
		{
			std::string out;
			if (!s) return out;
			for (; *s; ++s)
			{
				u32 cp = (u32)*s;
				if (cp < 0x80) out.push_back((char)cp);
				else if (cp < 0x800) { out.push_back((char)(0xC0 | (cp >> 6))); out.push_back((char)(0x80 | (cp & 0x3F))); }
				else if (cp < 0x10000) { out.push_back((char)(0xE0 | (cp >> 12))); out.push_back((char)(0x80 | ((cp >> 6) & 0x3F))); out.push_back((char)(0x80 | (cp & 0x3F))); }
				else { out.push_back((char)(0xF0 | (cp >> 18))); out.push_back((char)(0x80 | ((cp >> 12) & 0x3F))); out.push_back((char)(0x80 | ((cp >> 6) & 0x3F))); out.push_back((char)(0x80 | (cp & 0x3F))); }
			}
			return out;
		}
	}

	void SdlGpuRenderer::ui_text(float x, float y, float w, float h, const wchar_t* s, float size, float r, float g, float b, float a, int align, int weight)
	{
		m_overlay.add_text(x, y, w, h, wide_to_utf8(s), size, r, g, b, a, align, weight);
	}

	void SdlGpuRenderer::ui_image(float x, float y, float w, float h, const wchar_t* path, float r, float g, float b, float a)
	{
		m_overlay.add_image(x, y, w, h, wide_to_utf8(path), r, g, b, a);
	}

	// ---------------------------------------------------------------------------------------------
	// Secondary render targets
	// ---------------------------------------------------------------------------------------------
	u32 SdlGpuRenderer::create_render_target(u32 width, u32 height)
	{
		if (!m_initialized || width == 0 || height == 0) return 0;
		if (m_render_targets.size() >= MAX_RENDER_TARGETS) return 0;
		auto target = std::make_unique<GpuTarget>();
		if (!ensure_target(*target, width, height, true, true)) return 0;
		const u32 id = m_next_render_target_id++;
		m_render_targets[id] = std::move(target);
		return id;
	}

	void SdlGpuRenderer::destroy_render_target(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return;
		wait_idle();
		release_target(*it->second);
		m_render_targets.erase(it);
	}

	bool SdlGpuRenderer::resize_render_target(u32 target_id, u32 width, u32 height)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return false;
		return ensure_target(*it->second, width, height, true, true);
	}

	void SdlGpuRenderer::render_to_target(u32 target_id, const ViewportCamera& camera, bool render_grid, bool render_gizmos)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end() || !m_initialized) return;
		GpuTarget& target = *it->second;
		if (target.readback_mapped) { SDL_UnmapGPUTransferBuffer(m_device, target.readback); target.readback_mapped = nullptr; }

		SDL_GPUCommandBuffer* cmd = SDL_AcquireGPUCommandBuffer(m_device);
		if (!cmd) return;
		FrameView view = build_camera_view(camera, target.width, target.height);
		record_scene(cmd, target, view, true, render_grid, render_gizmos, m_particles.consume_target_world(), false);
		SDL_SubmitGPUCommandBuffer(cmd);
	}

	bool SdlGpuRenderer::download_target(GpuTarget& target)
	{
		if (!target.color || !target.readback) return false;
		if (target.readback_mapped) { SDL_UnmapGPUTransferBuffer(m_device, target.readback); target.readback_mapped = nullptr; }
		SDL_GPUCommandBuffer* cmd = SDL_AcquireGPUCommandBuffer(m_device);
		if (!cmd) return false;
		SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
		SDL_GPUTextureRegion region{}; region.texture = target.color; region.w = target.width; region.h = target.height; region.d = 1;
		SDL_GPUTextureTransferInfo dst{ target.readback, 0, target.width, target.height };
		SDL_DownloadFromGPUTexture(copy, &region, &dst);
		SDL_EndGPUCopyPass(copy);
		SDL_GPUFence* fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
		if (!fence) return false;
		SDL_WaitForGPUFences(m_device, true, &fence, 1);
		SDL_ReleaseGPUFence(m_device, fence);
		return true;
	}

	bool SdlGpuRenderer::prepare_render_target_readback(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return false;
		return download_target(*it->second);
	}

	const void* SdlGpuRenderer::read_render_target_pixels(u32 target_id, u32& out_width, u32& out_height, u32& out_row_pitch)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) { out_width = out_height = out_row_pitch = 0; return nullptr; }
		GpuTarget& target = *it->second;
		out_width = target.width; out_height = target.height; out_row_pitch = target.width * 4;
		if (!target.readback_mapped) target.readback_mapped = SDL_MapGPUTransferBuffer(m_device, target.readback, false);
		return target.readback_mapped;
	}

	void SdlGpuRenderer::release_render_target_pixels(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return;
		GpuTarget& target = *it->second;
		if (target.readback_mapped) { SDL_UnmapGPUTransferBuffer(m_device, target.readback); target.readback_mapped = nullptr; }
	}
}
