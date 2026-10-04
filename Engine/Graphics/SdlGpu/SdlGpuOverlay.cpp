#include "SdlGpuOverlay.h"
#include "SdlGpuShaderFormat.h"
#include "../../Common/Platform.h"
#include "../../Common/VerboseLog.h"
#include "../../ThirdParty/stb_truetype.h"
#include "../Importers/TextureImporter.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <cstdio>
#include <fstream>

namespace vortex::graphics::sdlgpu
{
	namespace
	{
		bool read_file(const std::string& path, std::vector<unsigned char>& out)
		{
			std::ifstream f(path, std::ios::binary | std::ios::ate);
			if (!f) return false;
			const auto n = f.tellg();
			if (n <= 0) return false;
			out.resize((size_t)n);
			f.seekg(0);
			f.read(reinterpret_cast<char*>(out.data()), n);
			return true;
		}

#if VORTEX_PLATFORM_LINUX
		// Linux has no fixed system-font path: every distribution lays /usr/share/fonts out differently
		// (Arch keeps Liberation/Noto in their own folders, Debian uses truetype/<family>, ...). fontconfig
		// is the one thing they all agree on, so the UI font is resolved with it and the hard-coded list
		// below is only the fallback for a system without fc-match.
		std::string font_from_fontconfig(const char* pattern)
		{
			std::string cmd = "fc-match -f '%{file}' '";
			cmd += pattern;
			cmd += "' 2>/dev/null";
			FILE* pipe = popen(cmd.c_str(), "r");
			if (!pipe) return {};
			char buffer[1024];
			std::string out;
			while (fgets(buffer, sizeof(buffer), pipe)) out += buffer;
			if (pclose(pipe) != 0) return {};
			while (!out.empty() && (out.back() == '\n' || out.back() == '\r')) out.pop_back();
			return out;
		}
#endif

		bool read_text(const std::string& path, std::string& out)
		{
			std::ifstream f(path, std::ios::binary);
			if (!f) return false;
			out.assign((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
			return true;
		}

		// Decodes one UTF-8 code point; advances i. Invalid bytes decode as '?'.
		u32 next_code_point(const std::string& s, size_t& i)
		{
			const unsigned char c = (unsigned char)s[i];
			u32 cp; int extra;
			if (c < 0x80) { cp = c; extra = 0; }
			else if ((c & 0xE0) == 0xC0) { cp = c & 0x1F; extra = 1; }
			else if ((c & 0xF0) == 0xE0) { cp = c & 0x0F; extra = 2; }
			else if ((c & 0xF8) == 0xF0) { cp = c & 0x07; extra = 3; }
			else { ++i; return '?'; }
			++i;
			for (int k = 0; k < extra; ++k, ++i)
			{
				if (i >= s.size() || ((unsigned char)s[i] & 0xC0) != 0x80) return '?';
				cp = (cp << 6) | ((unsigned char)s[i] & 0x3F);
			}
			return cp;
		}
	}

	bool SdlGpuOverlay::initialize(SDL_GPUDevice* device, const std::string& shader_dir, SDL_GPUTextureFormat target_format)
	{
		shutdown();
		m_device = device;
		if (!device) return false;

		// `entry` of the "overlay" shader set: an entrypoint inside overlay.metal on Metal, or its own
		// overlay.<Entry>.spv module on Vulkan (SdlGpuShaderFormat.h).
		auto make_shader = [&](const char* entry, SDL_GPUShaderStage stage, u32 samplers, u32 uniforms) -> SDL_GPUShader*
		{
			const std::string file = shaderfmt::module_file("overlay", entry);
			std::vector<unsigned char> code;
			if (!read_file(shader_dir + "/" + file, code))
			{
				platform::debug_output(("[overlay] " + file + " not found — UI overlay disabled\n").c_str());
				return nullptr;
			}
			if (shaderfmt::is_text) code.push_back('\0');

			SDL_GPUShaderCreateInfo ci{};
			ci.code = code.data();
			ci.code_size = code.size();
			ci.entrypoint = shaderfmt::entrypoint(entry);
			ci.format = shaderfmt::format;
			ci.stage = stage;
			ci.num_samplers = samplers;
			ci.num_uniform_buffers = uniforms;
			SDL_GPUShader* sh = SDL_CreateGPUShader(device, &ci);
			if (!sh) platform::debug_output((std::string("[overlay] shader ") + entry + " failed: " + SDL_GetError() + "\n").c_str());
			return sh;
		};
		SDL_GPUShader* vs = make_shader("OverlayVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 1);
		SDL_GPUShader* fs = make_shader("OverlayPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0);
		if (!vs || !fs) { if (vs) SDL_ReleaseGPUShader(device, vs); if (fs) SDL_ReleaseGPUShader(device, fs); return false; }

		SDL_GPUVertexBufferDescription vb{};
		vb.slot = 0; vb.pitch = sizeof(Vertex); vb.input_rate = SDL_GPU_VERTEXINPUTRATE_VERTEX;
		SDL_GPUVertexAttribute attrs[5] = {
			{ 0, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, 0 },
			{ 1, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, 8 },
			{ 2, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, 16 },
			{ 3, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, 32 },
			{ 4, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, 48 },
		};
		SDL_GPUColorTargetDescription color{};
		color.format = target_format;
		color.blend_state.enable_blend = true;
		color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_ALPHA;
		color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
		color.blend_state.color_blend_op = SDL_GPU_BLENDOP_ADD;
		color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
		color.blend_state.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
		color.blend_state.alpha_blend_op = SDL_GPU_BLENDOP_ADD;

		SDL_GPUGraphicsPipelineCreateInfo pci{};
		pci.vertex_shader = vs;
		pci.fragment_shader = fs;
		pci.vertex_input_state.vertex_buffer_descriptions = &vb;
		pci.vertex_input_state.num_vertex_buffers = 1;
		pci.vertex_input_state.vertex_attributes = attrs;
		pci.vertex_input_state.num_vertex_attributes = 5;
		pci.primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
		pci.rasterizer_state.fill_mode = SDL_GPU_FILLMODE_FILL;
		pci.rasterizer_state.cull_mode = SDL_GPU_CULLMODE_NONE;
		pci.rasterizer_state.front_face = SDL_GPU_FRONTFACE_CLOCKWISE;
		pci.multisample_state.sample_count = SDL_GPU_SAMPLECOUNT_1;
		pci.target_info.color_target_descriptions = &color;
		pci.target_info.num_color_targets = 1;
		m_pipeline = SDL_CreateGPUGraphicsPipeline(device, &pci);
		SDL_ReleaseGPUShader(device, vs);
		SDL_ReleaseGPUShader(device, fs);
		if (!m_pipeline)
		{
			platform::debug_output((std::string("[overlay] pipeline failed: ") + SDL_GetError() + "\n").c_str());
			return false;
		}

		SDL_GPUSamplerCreateInfo sci{};
		sci.min_filter = SDL_GPU_FILTER_LINEAR; sci.mag_filter = SDL_GPU_FILTER_LINEAR;
		sci.mipmap_mode = SDL_GPU_SAMPLERMIPMAPMODE_LINEAR;
		sci.address_mode_u = sci.address_mode_v = sci.address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
		sci.max_lod = 1000.0f;
		m_sampler = SDL_CreateGPUSampler(device, &sci);

		const u32 white = 0xFFFFFFFFu;
		m_white = create_texture(&white, 1, 1, SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, 4);

		// System UI fonts: SF (macOS), Helvetica bold for the heavier weights; whatever fontconfig picks on
		// Linux, with the common distribution paths as a fallback; env overrides win everywhere.
		std::vector<std::string> regular = { platform::env_string("VORTEX_UI_FONT") };
		std::vector<std::string> bold = { platform::env_string("VORTEX_UI_FONT_BOLD") };
#if VORTEX_PLATFORM_LINUX
		regular.push_back(font_from_fontconfig("sans-serif"));
		bold.push_back(font_from_fontconfig("sans-serif:bold"));
#endif
		for (const char* p : { "/System/Library/Fonts/SFNS.ttf",
		                       "/System/Library/Fonts/Helvetica.ttc",
		                       "/System/Library/Fonts/Supplemental/Arial.ttf",
		                       "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",   // Debian / Ubuntu
		                       "/usr/share/fonts/dejavu/DejaVuSans.ttf",            // Fedora
		                       "/usr/share/fonts/TTF/DejaVuSans.ttf",               // Arch
		                       "/usr/share/fonts/liberation/LiberationSans-Regular.ttf",
		                       "/usr/share/fonts/TTF/LiberationSans-Regular.ttf",
		                       "/usr/share/fonts/noto/NotoSans-Regular.ttf" })
			regular.push_back(p);
		for (const char* p : { "/System/Library/Fonts/Helvetica.ttc",
		                       "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
		                       "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
		                       "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf",
		                       "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
		                       "/usr/share/fonts/liberation/LiberationSans-Bold.ttf",
		                       "/usr/share/fonts/TTF/LiberationSans-Bold.ttf",
		                       "/usr/share/fonts/noto/NotoSans-Bold.ttf" })
			bold.push_back(p);
		load_font(m_font_regular, regular, false);
		if (!load_font(m_font_bold, bold, true) && m_font_regular.ok)
		{
			m_font_bold.data = m_font_regular.data;
			m_font_bold.index = m_font_regular.index;
			m_font_bold.ascent = m_font_regular.ascent; m_font_bold.descent = m_font_regular.descent;
			m_font_bold.info = nullptr; m_font_bold.ok = false;
			auto* info = new stbtt_fontinfo{};
			if (stbtt_InitFont(info, m_font_bold.data.data(), stbtt_GetFontOffsetForIndex(m_font_bold.data.data(), m_font_bold.index)))
			{ m_font_bold.info = info; m_font_bold.ok = true; }
			else delete info;
		}
		m_ready = m_pipeline && m_sampler && m_white;
		return m_ready;
	}

	bool SdlGpuOverlay::load_font(FontFace& face, const std::vector<std::string>& candidates, bool bold)
	{
		for (const std::string& path : candidates)
		{
			if (path.empty()) continue;
			std::vector<unsigned char> data;
			if (!read_file(path, data)) continue;
			int index = 0;
			if (bold)
			{
				int found = stbtt_FindMatchingFont(data.data(), "Helvetica", STBTT_MACSTYLE_BOLD);
				if (found < 0) found = stbtt_FindMatchingFont(data.data(), "Arial", STBTT_MACSTYLE_BOLD);
				if (found >= 0) index = found;
				else if (stbtt_GetNumberOfFonts(data.data()) > 1) continue;   // a collection without a bold face
			}
			auto* info = new stbtt_fontinfo{};
			const int offset = stbtt_GetFontOffsetForIndex(data.data(), index);
			if (offset < 0 || !stbtt_InitFont(info, data.data(), offset)) { delete info; continue; }
			int asc = 0, desc = 0, gap = 0;
			stbtt_GetFontVMetrics(info, &asc, &desc, &gap);
			face.data = std::move(data);
			face.index = index;
			face.ascent = (float)asc; face.descent = (float)desc;
			face.info = info;
			face.ok = true;
			VORTEX_VLOG(("[overlay] font: " + path + "\n").c_str());
			return true;
		}
		return false;
	}

	void SdlGpuOverlay::shutdown()
	{
		if (m_device)
		{
			for (auto& [key, atlas] : m_atlases) if (atlas->texture) SDL_ReleaseGPUTexture(m_device, atlas->texture);
			for (auto& [path, tex] : m_images) if (tex) SDL_ReleaseGPUTexture(m_device, tex);
			if (m_white) SDL_ReleaseGPUTexture(m_device, m_white);
			if (m_sampler) SDL_ReleaseGPUSampler(m_device, m_sampler);
			if (m_pipeline) SDL_ReleaseGPUGraphicsPipeline(m_device, m_pipeline);
			if (m_vertex_buffer) SDL_ReleaseGPUBuffer(m_device, m_vertex_buffer);
			if (m_vertex_transfer) SDL_ReleaseGPUTransferBuffer(m_device, m_vertex_transfer);
		}
		m_atlases.clear(); m_images.clear();
		m_white = nullptr; m_sampler = nullptr; m_pipeline = nullptr; m_vertex_buffer = nullptr; m_vertex_transfer = nullptr;
		m_vertex_capacity = 0;
		delete static_cast<stbtt_fontinfo*>(m_font_regular.info); m_font_regular = FontFace{};
		delete static_cast<stbtt_fontinfo*>(m_font_bold.info); m_font_bold = FontFace{};
		m_cmds.clear(); m_vertices.clear(); m_batches.clear();
		m_ready = false;
		m_device = nullptr;
	}

	SDL_GPUTexture* SdlGpuOverlay::create_texture(const void* pixels, u32 w, u32 h, SDL_GPUTextureFormat format, u32 bpp)
	{
		SDL_GPUTextureCreateInfo tci{};
		tci.type = SDL_GPU_TEXTURETYPE_2D;
		tci.format = format;
		tci.usage = SDL_GPU_TEXTUREUSAGE_SAMPLER;
		tci.width = w; tci.height = h;
		tci.layer_count_or_depth = 1;
		tci.num_levels = 1;
		tci.sample_count = SDL_GPU_SAMPLECOUNT_1;
		SDL_GPUTexture* tex = SDL_CreateGPUTexture(m_device, &tci);
		if (!tex) return nullptr;

		const u32 size = w * h * bpp;
		SDL_GPUTransferBufferCreateInfo xci{};
		xci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD;
		xci.size = size;
		SDL_GPUTransferBuffer* transfer = SDL_CreateGPUTransferBuffer(m_device, &xci);
		if (!transfer) { SDL_ReleaseGPUTexture(m_device, tex); return nullptr; }
		void* mapped = SDL_MapGPUTransferBuffer(m_device, transfer, false);
		if (!mapped) { SDL_ReleaseGPUTransferBuffer(m_device, transfer); SDL_ReleaseGPUTexture(m_device, tex); return nullptr; }
		std::memcpy(mapped, pixels, size);
		SDL_UnmapGPUTransferBuffer(m_device, transfer);

		SDL_GPUCommandBuffer* cmd = SDL_AcquireGPUCommandBuffer(m_device);
		SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
		SDL_GPUTextureTransferInfo src{ transfer, 0, w, h };
		SDL_GPUTextureRegion dst{}; dst.texture = tex; dst.w = w; dst.h = h; dst.d = 1;
		SDL_UploadToGPUTexture(copy, &src, &dst, false);
		SDL_EndGPUCopyPass(copy);
		SDL_SubmitGPUCommandBuffer(cmd);
		SDL_ReleaseGPUTransferBuffer(m_device, transfer);
		return tex;
	}

	SdlGpuOverlay::Atlas* SdlGpuOverlay::atlas_for(float size, bool bold)
	{
		const u32 px = (u32)std::lround(size < 4.0f ? 4.0f : (size > 200.0f ? 200.0f : size));
		const u64 key = ((u64)(bold ? 1 : 0) << 32) | px;
		auto it = m_atlases.find(key);
		if (it != m_atlases.end()) return it->second.get();

		FontFace& face = (bold && m_font_bold.ok) ? m_font_bold : m_font_regular;
		if (!face.ok) return nullptr;

		auto atlas = std::make_unique<Atlas>();
		atlas->bold = bold;
		// Pack ASCII + Latin-1 (umlauts, ß, accents) with light oversampling; grow the bitmap until it fits.
		for (u32 dim = 256; dim <= 4096; dim *= 2)
		{
			std::vector<unsigned char> bitmap((size_t)dim * dim, 0);
			stbtt_pack_context pc{};
			if (!stbtt_PackBegin(&pc, bitmap.data(), (int)dim, (int)dim, 0, 1, nullptr)) return nullptr;
			stbtt_PackSetOversampling(&pc, 2, 2);
			// ASCII, Latin-1 (umlauts, ß, accents), general punctuation (dashes, quotes, bullets, ellipsis),
			// arrows, geometric shapes and the check-mark/dingbat block the game UI uses.
			struct Range { int first, count; };
			const Range wanted[] = { { 32, 95 }, { 160, 96 }, { 0x2010, 0x28 }, { 0x2190, 0x30 }, { 0x25A0, 0x60 }, { 0x2700, 0xC0 } };
			constexpr int range_count = (int)(sizeof(wanted) / sizeof(wanted[0]));
			std::vector<std::vector<stbtt_packedchar>> chars(range_count);
			stbtt_pack_range ranges[range_count]{};
			for (int r = 0; r < range_count; ++r)
			{
				chars[r].resize((size_t)wanted[r].count);
				ranges[r].font_size = STBTT_POINT_SIZE((float)px);
				ranges[r].first_unicode_codepoint_in_range = wanted[r].first;
				ranges[r].num_chars = wanted[r].count;
				ranges[r].chardata_for_range = chars[r].data();
			}
			const int ok = stbtt_PackFontRanges(&pc, face.data.data(), face.index, ranges, range_count);
			stbtt_PackEnd(&pc);
			if (!ok) continue;   // did not fit: try the next size

			auto put = [&](int first, const std::vector<stbtt_packedchar>& chars)
			{
				for (size_t i = 0; i < chars.size(); ++i)
				{
					const stbtt_packedchar& c = chars[i];
					Glyph g{};
					g.x0 = c.xoff; g.y0 = c.yoff; g.x1 = c.xoff2; g.y1 = c.yoff2;
					g.u0 = (float)c.x0 / dim; g.v0 = (float)c.y0 / dim; g.u1 = (float)c.x1 / dim; g.v1 = (float)c.y1 / dim;
					g.advance = c.xadvance;
					atlas->glyphs[(u32)(first + (int)i)] = g;
				}
			};
			for (int r = 0; r < range_count; ++r) put(wanted[r].first, chars[r]);
			atlas->texture = create_texture(bitmap.data(), dim, dim, SDL_GPU_TEXTUREFORMAT_R8_UNORM, 1);
			atlas->size = dim;
			atlas->scale = stbtt_ScaleForMappingEmToPixels(static_cast<stbtt_fontinfo*>(face.info), (float)px);
			atlas->ascent_px = face.ascent * atlas->scale;
			atlas->descent_px = face.descent * atlas->scale;
			break;
		}
		if (!atlas->texture) return nullptr;
		Atlas* raw = atlas.get();
		m_atlases[key] = std::move(atlas);
		return raw;
	}

	SDL_GPUTexture* SdlGpuOverlay::image_for(const std::string& path)
	{
		auto it = m_images.find(path);
		if (it != m_images.end()) return it->second;
		SDL_GPUTexture* tex = nullptr;
		ImageData img = TextureImporter::import_from_file(path);
		if (img.is_valid())
		{
			std::vector<u8> rgba;
			const u8* pixels = img.pixels.data();
			if (img.channels != 4)
			{
				rgba.resize((size_t)img.width * img.height * 4);
				for (size_t i = 0; i < (size_t)img.width * img.height; ++i)
				{
					const u8* s = img.pixels.data() + i * img.channels;
					rgba[i * 4 + 0] = s[0];
					rgba[i * 4 + 1] = img.channels > 1 ? s[1] : s[0];
					rgba[i * 4 + 2] = img.channels > 2 ? s[2] : s[0];
					rgba[i * 4 + 3] = img.channels > 3 ? s[3] : 255;
				}
				pixels = rgba.data();
			}
			tex = create_texture(pixels, img.width, img.height, SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, 4);
		}
		m_images[path] = tex;   // cache misses too, so a missing file is not re-read every frame
		return tex;
	}

	void SdlGpuOverlay::begin(float width, float height)
	{
		m_w = width > 1.0f ? width : 1.0f;
		m_h = height > 1.0f ? height : 1.0f;
		m_cmds.clear();
	}

	void SdlGpuOverlay::add_rect(float x, float y, float w, float h, float r, float g, float b, float a, float radius)
	{
		Cmd c{}; c.type = 0; c.x = x; c.y = y; c.w = w; c.h = h; c.r = r; c.g = g; c.b = b; c.a = a; c.radius = radius;
		m_cmds.push_back(std::move(c));
	}

	void SdlGpuOverlay::add_text(float x, float y, float w, float h, const std::string& utf8, float size, float r, float g, float b, float a, int align, int weight)
	{
		if (utf8.empty()) return;
		Cmd c{}; c.type = 1; c.x = x; c.y = y; c.w = w; c.h = h; c.r = r; c.g = g; c.b = b; c.a = a;
		c.size = size <= 0.0f ? 16.0f : size; c.align = align; c.weight = weight <= 0 ? 600 : weight; c.text = utf8;
		m_cmds.push_back(std::move(c));
	}

	void SdlGpuOverlay::add_line(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thickness)
	{
		Cmd c{}; c.type = 2; c.x = x1; c.y = y1; c.x2 = x2; c.y2 = y2; c.r = r; c.g = g; c.b = b; c.a = a; c.thickness = thickness;
		m_cmds.push_back(std::move(c));
	}

	void SdlGpuOverlay::add_image(float x, float y, float w, float h, const std::string& path, float r, float g, float b, float a)
	{
		Cmd c{}; c.type = 3; c.x = x; c.y = y; c.w = w; c.h = h; c.r = r; c.g = g; c.b = b; c.a = a; c.text = path;
		m_cmds.push_back(std::move(c));
	}

	void SdlGpuOverlay::push_clip(float x, float y, float w, float h)
	{
		Cmd c{}; c.type = 4; c.x = x; c.y = y; c.w = w; c.h = h;
		m_cmds.push_back(std::move(c));
	}

	void SdlGpuOverlay::pop_clip()
	{
		Cmd c{}; c.type = 5;
		m_cmds.push_back(std::move(c));
	}

	void SdlGpuOverlay::flush_batch()
	{
		const u32 count = (u32)m_vertices.size() - m_batch_first;
		if (count > 0 && m_batch_texture)
			m_batches.push_back({ m_batch_texture, m_batch_has_clip, m_batch_clip, m_batch_first, count });
		m_batch_first = (u32)m_vertices.size();
	}

	void SdlGpuOverlay::quad(SDL_GPUTexture* tex, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1,
		float r, float g, float b, float a, float mode, float radius, float hw, float hh)
	{
		if (tex != m_batch_texture) { flush_batch(); m_batch_texture = tex; }
		auto push = [&](float x, float y, float u, float v, float lx, float ly)
		{
			m_vertices.push_back({ x, y, u, v, r, g, b, a, mode, radius, hw, hh, lx, ly });
		};
		// rect-local coordinates (centered) drive the rounded-corner distance field
		push(x0, y0, u0, v0, -hw, -hh); push(x1, y0, u1, v0, hw, -hh); push(x1, y1, u1, v1, hw, hh);
		push(x0, y0, u0, v0, -hw, -hh); push(x1, y1, u1, v1, hw, hh);  push(x0, y1, u0, v1, -hw, hh);
	}

	void SdlGpuOverlay::render(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* target, u32 target_w, u32 target_h)
	{
		if (!m_ready || !target || m_cmds.empty()) { m_cmds.clear(); return; }
		m_vertices.clear(); m_batches.clear(); m_clip_stack.clear();
		m_batch_texture = nullptr; m_batch_has_clip = false; m_batch_first = 0;
		m_scale_x = (float)target_w / m_w;
		m_scale_y = (float)target_h / m_h;

		auto set_clip = [&](bool has, SDL_Rect rc)
		{
			flush_batch();
			m_batch_has_clip = has; m_batch_clip = rc;
		};

		for (const Cmd& c : m_cmds)
		{
			switch (c.type)
			{
			case 0:   // rect
				quad(m_white, c.x, c.y, c.x + c.w, c.y + c.h, 0, 0, 1, 1, c.r, c.g, c.b, c.a, 0.0f, c.radius, c.w * 0.5f, c.h * 0.5f);
				break;
			case 2:   // line -> thin quad along the segment
			{
				float dx = c.x2 - c.x, dy = c.y2 - c.y;
				float len = std::sqrt(dx * dx + dy * dy);
				if (len < 0.001f) break;
				float nx = -dy / len * c.thickness * 0.5f, ny = dx / len * c.thickness * 0.5f;
				if (m_white != m_batch_texture) { flush_batch(); m_batch_texture = m_white; }
				auto push = [&](float x, float y) { m_vertices.push_back({ x, y, 0, 0, c.r, c.g, c.b, c.a, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f }); };
				push(c.x + nx, c.y + ny); push(c.x2 + nx, c.y2 + ny); push(c.x2 - nx, c.y2 - ny);
				push(c.x + nx, c.y + ny); push(c.x2 - nx, c.y2 - ny); push(c.x - nx, c.y - ny);
				break;
			}
			case 3:   // image
				if (SDL_GPUTexture* tex = image_for(c.text))
					quad(tex, c.x, c.y, c.x + c.w, c.y + c.h, 0, 0, 1, 1, c.r, c.g, c.b, c.a, 2.0f, 0.0f, 0.0f, 0.0f);
				break;
			case 4:   // push clip (overlay units -> target pixels)
			{
				SDL_Rect rc{ (int)std::floor(c.x * m_scale_x), (int)std::floor(c.y * m_scale_y),
					(int)std::ceil(c.w * m_scale_x), (int)std::ceil(c.h * m_scale_y) };
				if (!m_clip_stack.empty())
				{
					// intersect with the current clip so nested clips only shrink
					const SDL_Rect& p = m_clip_stack.back();
					int x0 = std::max(rc.x, p.x), y0 = std::max(rc.y, p.y);
					int x1 = std::min(rc.x + rc.w, p.x + p.w), y1 = std::min(rc.y + rc.h, p.y + p.h);
					rc = { x0, y0, std::max(0, x1 - x0), std::max(0, y1 - y0) };
				}
				m_clip_stack.push_back(rc);
				set_clip(true, rc);
				break;
			}
			case 5:   // pop clip
				if (!m_clip_stack.empty()) m_clip_stack.pop_back();
				if (m_clip_stack.empty()) set_clip(false, SDL_Rect{});
				else set_clip(true, m_clip_stack.back());
				break;
			case 1:   // text
			{
				Atlas* atlas = atlas_for(c.size, c.weight >= 600);
				if (!atlas) break;
				// measure
				float width = 0.0f;
				for (size_t i = 0; i < c.text.size();)
				{
					u32 cp = next_code_point(c.text, i);
					auto g = atlas->glyphs.find(cp);
					if (g == atlas->glyphs.end()) g = atlas->glyphs.find('?');
					if (g != atlas->glyphs.end()) width += g->second.advance;
				}
				float pen_x = c.x;
				if (c.align == 1) pen_x = c.x + (c.w - width) * 0.5f;
				else if (c.align == 2) pen_x = c.x + c.w - width;
				const float line_h = atlas->ascent_px - atlas->descent_px;
				const float baseline = c.y + (c.h - line_h) * 0.5f + atlas->ascent_px;
				for (size_t i = 0; i < c.text.size();)
				{
					u32 cp = next_code_point(c.text, i);
					auto g = atlas->glyphs.find(cp);
					if (g == atlas->glyphs.end()) g = atlas->glyphs.find('?');
					if (g == atlas->glyphs.end()) continue;
					const Glyph& gl = g->second;
					quad(atlas->texture, pen_x + gl.x0, baseline + gl.y0, pen_x + gl.x1, baseline + gl.y1,
						gl.u0, gl.v0, gl.u1, gl.v1, c.r, c.g, c.b, c.a, 1.0f, 0.0f, 0.0f, 0.0f);
					pen_x += gl.advance;
				}
				break;
			}
			}
		}
		flush_batch();
		m_cmds.clear();
		if (m_vertices.empty()) return;

		// Upload the vertices (grow the GPU buffer when needed), then replay the batches in one LOAD pass.
		const u32 bytes = (u32)(m_vertices.size() * sizeof(Vertex));
		if (bytes > m_vertex_capacity)
		{
			if (m_vertex_buffer) SDL_ReleaseGPUBuffer(m_device, m_vertex_buffer);
			if (m_vertex_transfer) SDL_ReleaseGPUTransferBuffer(m_device, m_vertex_transfer);
			m_vertex_capacity = bytes + bytes / 2 + 4096;
			SDL_GPUBufferCreateInfo bci{}; bci.usage = SDL_GPU_BUFFERUSAGE_VERTEX; bci.size = m_vertex_capacity;
			m_vertex_buffer = SDL_CreateGPUBuffer(m_device, &bci);
			SDL_GPUTransferBufferCreateInfo tci{}; tci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD; tci.size = m_vertex_capacity;
			m_vertex_transfer = SDL_CreateGPUTransferBuffer(m_device, &tci);
			if (!m_vertex_buffer || !m_vertex_transfer) { m_vertex_capacity = 0; return; }
		}
		void* mapped = SDL_MapGPUTransferBuffer(m_device, m_vertex_transfer, true);
		if (!mapped) return;
		std::memcpy(mapped, m_vertices.data(), bytes);
		SDL_UnmapGPUTransferBuffer(m_device, m_vertex_transfer);
		SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
		SDL_GPUTransferBufferLocation src{ m_vertex_transfer, 0 };
		SDL_GPUBufferRegion dst{ m_vertex_buffer, 0, bytes };
		SDL_UploadToGPUBuffer(copy, &src, &dst, true);
		SDL_EndGPUCopyPass(copy);

		SDL_GPUColorTargetInfo color{};
		color.texture = target;
		color.load_op = SDL_GPU_LOADOP_LOAD;
		color.store_op = SDL_GPU_STOREOP_STORE;
		SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, &color, 1, nullptr);
		if (!pass) return;
		SDL_BindGPUGraphicsPipeline(pass, m_pipeline);
		SDL_GPUViewport vp{ 0, 0, (float)target_w, (float)target_h, 0.0f, 1.0f };
		SDL_SetGPUViewport(pass, &vp);
		SDL_GPUBufferBinding vb{ m_vertex_buffer, 0 };
		SDL_BindGPUVertexBuffers(pass, 0, &vb, 1);
		float screen[4] = { m_w, m_h, 0.0f, 0.0f };
		SDL_PushGPUVertexUniformData(cmd, 0, screen, sizeof(screen));
		const SDL_Rect full{ 0, 0, (int)target_w, (int)target_h };
		for (const Batch& b : m_batches)
		{
			SDL_Rect sc = b.has_clip ? b.clip : full;
			if (sc.w <= 0 || sc.h <= 0) continue;
			SDL_SetGPUScissor(pass, &sc);
			SDL_GPUTextureSamplerBinding tsb{ b.texture, m_sampler };
			SDL_BindGPUFragmentSamplers(pass, 0, &tsb, 1);
			SDL_DrawGPUPrimitives(pass, b.count, 1, b.first, 0);
		}
		SDL_EndGPURenderPass(pass);
	}
}
