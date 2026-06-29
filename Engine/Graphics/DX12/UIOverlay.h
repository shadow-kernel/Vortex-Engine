#pragma once

// Generic 2D UI overlay for the engine: renders panels, rounded rects, lines and TEXT on top of the 3D
// in the SAME swapchain (so it works over the live game — no WPF airspace problem). Implemented with
// Direct2D + DirectWrite via D3D11On12 (Microsoft's standard "text over DX12" path). It is purely a
// rendering backend driven by an immediate-mode command list the game fills each frame through the
// Vortex.UI script API — nothing game-specific lives here.

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <d3d11on12.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <vector>
#include <string>
#include <unordered_map>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	class UIOverlay
	{
	public:
		bool initialize(ID3D12Device* device, ID3D12CommandQueue* queue, u32 backBufferCount);
		void shutdown();
		bool is_ready() const { return m_ready; }

		// --- immediate-mode command recording (called from the game via the API, before render) ---
		void begin(float width, float height);
		void add_rect(float x, float y, float w, float h, float r, float g, float b, float a, float radius);
		void add_text(float x, float y, float w, float h, const wchar_t* text,
			float size, float r, float g, float b, float a, int align /*0 left,1 center,2 right*/, int weight /*400/600/700*/);
		void add_line(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thickness);
		// Textured-quad (PNG/JPG via WIC), tinted by (r,g,b,a) — a=opacity. path is cached, NOT invalidated on resize.
		void add_image(float x, float y, float w, float h, const wchar_t* path, float r, float g, float b, float a);
		void push_clip(float x, float y, float w, float h);   // scissor for clipped subtrees (lists/scroll)
		void pop_clip();
		bool has_commands() const { return !m_cmds.empty(); }

		// Replay the recorded commands onto 'backBuffer' (which the 3D pass left in PRESENT state), then clear.
		void render(ID3D12Resource* backBuffer);

		// Release the cached wrapped back-buffer bitmaps — MUST be called before a swapchain resize/shutdown
		// (the wrapped 11on12 resources alias the back buffers that are about to be freed).
		void invalidate_targets();

	private:
		struct Cmd
		{
			int type;            // 0 rect, 1 text, 2 line, 3 image, 4 push-clip, 5 pop-clip
			float x, y, w, h;
			float r, g, b, a;
			float radius, size, thickness, x2, y2;
			int align, weight;
			std::wstring text;   // text content, OR (type 3) the image file path
		};

		struct Target
		{
			ComPtr<ID3D11Resource> wrapped;
			ComPtr<ID2D1Bitmap1> bitmap;
		};

		IDWriteTextFormat* format_for(float size, int weight);
		ID2D1Bitmap* bitmap_for(const std::wstring& path);   // WIC-load + cache by path (survives resize)

		bool m_ready{ false };
		float m_w{ 0 }, m_h{ 0 };
		std::vector<Cmd> m_cmds;

		ComPtr<ID3D11On12Device> m_11on12;
		ComPtr<ID3D11DeviceContext> m_11ctx;
		ComPtr<ID2D1Factory1> m_d2dFactory;
		ComPtr<ID2D1Device> m_d2dDevice;
		ComPtr<ID2D1DeviceContext> m_d2dCtx;
		ComPtr<IDWriteFactory> m_dwrite;
		ComPtr<ID2D1SolidColorBrush> m_brush;
		ComPtr<IWICImagingFactory> m_wic;                                 // image decoder factory (UIImage)

		std::unordered_map<ID3D12Resource*, Target> m_targets;            // per back-buffer D2D bitmap cache
		std::unordered_map<unsigned long long, ComPtr<IDWriteTextFormat>> m_formats; // cache by (size<<8|weight)
		std::unordered_map<std::wstring, ComPtr<ID2D1Bitmap>> m_bitmaps;  // UIImage cache by path; NOT invalidated on resize
	};
}
