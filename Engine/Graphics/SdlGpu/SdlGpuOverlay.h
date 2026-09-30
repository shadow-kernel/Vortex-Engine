#pragma once

// 2D UI overlay for the SDL GPU backend: rects (rounded), text, lines, images and clip rects recorded as an
// immediate-mode command list by the game (Vortex.UI script API) and replayed over the finished frame.
// Text is rasterized with stb_truetype from the system UI font into R8 glyph atlases (one per size/weight).
#include "../../Common/CommonHeaders.h"
#include <SDL3/SDL.h>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

namespace vortex::graphics::sdlgpu
{
	class SdlGpuOverlay
	{
	public:
		bool initialize(SDL_GPUDevice* device, const std::string& shader_dir, SDL_GPUTextureFormat target_format);
		void shutdown();
		bool is_ready() const { return m_ready; }

		void begin(float width, float height);
		void add_rect(float x, float y, float w, float h, float r, float g, float b, float a, float radius);
		void add_text(float x, float y, float w, float h, const std::string& utf8, float size, float r, float g, float b, float a, int align, int weight);
		void add_line(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thickness);
		void add_image(float x, float y, float w, float h, const std::string& utf8_path, float r, float g, float b, float a);
		void push_clip(float x, float y, float w, float h);
		void pop_clip();
		bool has_commands() const { return !m_cmds.empty(); }

		// Replay the recorded commands onto `target` (its own LOAD render pass) and clear the list.
		void render(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* target, u32 target_w, u32 target_h);
		void invalidate_targets() {}

	private:
		struct Cmd
		{
			int type;   // 0 rect, 1 text, 2 line, 3 image, 4 push clip, 5 pop clip
			float x, y, w, h;
			float r, g, b, a;
			float radius, size, thickness, x2, y2;
			int align, weight;
			std::string text;   // text content OR (type 3) the image path
		};
		struct Vertex { float x, y, u, v; float r, g, b, a; float mode, radius, hw, hh; float lx, ly; };
		struct Glyph { float x0, y0, x1, y1; float u0, v0, u1, v1; float advance; };
		struct FontFace
		{
			std::vector<unsigned char> data;
			int index{ 0 };
			float ascent{ 0 }, descent{ 0 };   // unscaled font units
			bool ok{ false };
			void* info{ nullptr };             // stbtt_fontinfo*
		};
		struct Atlas
		{
			SDL_GPUTexture* texture{ nullptr };
			u32 size{ 0 };
			float scale{ 0 };                  // font units -> pixels at this size
			float ascent_px{ 0 }, descent_px{ 0 };
			std::unordered_map<u32, Glyph> glyphs;
			bool bold{ false };
		};
		struct Batch { SDL_GPUTexture* texture; bool has_clip; SDL_Rect clip; u32 first; u32 count; };

		bool load_font(FontFace& face, const std::vector<std::string>& candidates, bool bold);
		Atlas* atlas_for(float size, bool bold);
		SDL_GPUTexture* image_for(const std::string& path);
		SDL_GPUTexture* create_texture(const void* pixels, u32 w, u32 h, SDL_GPUTextureFormat format, u32 bpp);
		void quad(SDL_GPUTexture* tex, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1,
			float r, float g, float b, float a, float mode, float radius, float hw, float hh);
		void flush_batch();

		bool m_ready{ false };
		SDL_GPUDevice* m_device{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline{ nullptr };
		SDL_GPUSampler* m_sampler{ nullptr };
		SDL_GPUTexture* m_white{ nullptr };
		SDL_GPUBuffer* m_vertex_buffer{ nullptr };
		SDL_GPUTransferBuffer* m_vertex_transfer{ nullptr };
		u32 m_vertex_capacity{ 0 };
		float m_w{ 0 }, m_h{ 0 };
		std::vector<Cmd> m_cmds;
		std::vector<Vertex> m_vertices;
		std::vector<Batch> m_batches;
		SDL_GPUTexture* m_batch_texture{ nullptr };
		bool m_batch_has_clip{ false };
		SDL_Rect m_batch_clip{};
		u32 m_batch_first{ 0 };
		float m_scale_x{ 1.0f }, m_scale_y{ 1.0f };   // overlay units -> target pixels
		std::vector<SDL_Rect> m_clip_stack;
		FontFace m_font_regular, m_font_bold;
		std::unordered_map<u64, std::unique_ptr<Atlas>> m_atlases;   // key = (bold << 32) | size
		std::unordered_map<std::string, SDL_GPUTexture*> m_images;
	};
}
