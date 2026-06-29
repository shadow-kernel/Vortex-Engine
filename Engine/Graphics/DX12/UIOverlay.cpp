#include "UIOverlay.h"
#include <Windows.h>
#undef DrawText   // <Windows.h> defines DrawText -> DrawTextW, which would mangle the ID2D1 method call

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "windowscodecs.lib")

using Microsoft::WRL::ComPtr;

namespace vortex::graphics::dx12
{
	bool UIOverlay::initialize(ID3D12Device* device, ID3D12CommandQueue* queue, u32 /*backBufferCount*/)
	{
		if (m_ready) return true;
		if (!device || !queue) return false;

		// Wrap the existing D3D12 device + command queue in a D3D11On12 device so D2D can draw onto the
		// same swapchain back buffers. Everything below is HRESULT-guarded — any failure leaves the
		// overlay disabled and the 3D renderer completely unaffected.
		ComPtr<ID3D11Device> d11;
		IUnknown* queues[] = { queue };
		HRESULT hr = D3D11On12CreateDevice(device, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
			queues, 1, 0, &d11, &m_11ctx, nullptr);
		if (FAILED(hr)) { OutputDebugStringA("[UIOverlay] D3D11On12CreateDevice failed\n"); return false; }
		if (FAILED(d11.As(&m_11on12))) return false;

		D2D1_FACTORY_OPTIONS fo{};
		hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1), &fo,
			reinterpret_cast<void**>(m_d2dFactory.GetAddressOf()));
		if (FAILED(hr)) { OutputDebugStringA("[UIOverlay] D2D1CreateFactory failed\n"); return false; }

		ComPtr<IDXGIDevice> dxgiDevice;
		if (FAILED(m_11on12.As(&dxgiDevice))) return false;
		if (FAILED(m_d2dFactory->CreateDevice(dxgiDevice.Get(), &m_d2dDevice))) return false;
		if (FAILED(m_d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &m_d2dCtx))) return false;

		if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
			reinterpret_cast<IUnknown**>(m_dwrite.GetAddressOf())))) return false;

		if (FAILED(m_d2dCtx->CreateSolidColorBrush(D2D1::ColorF(1, 1, 1, 1), &m_brush))) return false;

		// WIC image factory for UIImage. Best-effort: if COM/WIC is unavailable, images simply don't draw and the
		// rest of the overlay is unaffected. CoInitializeEx is harmless if the thread is already initialized.
		CoInitializeEx(nullptr, COINIT_MULTITHREADED);
		if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&m_wic))))
			OutputDebugStringA("[UIOverlay] WIC factory unavailable - UIImage disabled\n");

		m_ready = true;
		OutputDebugStringA("[UIOverlay] ready (D3D11On12 + Direct2D + DirectWrite)\n");
		return true;
	}

	void UIOverlay::shutdown()
	{
		m_cmds.clear();
		m_formats.clear();
		m_targets.clear();
		m_bitmaps.clear();
		m_wic.Reset();
		m_brush.Reset();
		m_dwrite.Reset();
		m_d2dCtx.Reset();
		m_d2dDevice.Reset();
		m_d2dFactory.Reset();
		if (m_11ctx) { m_11ctx->ClearState(); m_11ctx->Flush(); }
		m_11ctx.Reset();
		m_11on12.Reset();
		m_ready = false;
	}

	void UIOverlay::invalidate_targets()
	{
		if (m_11ctx) { m_11ctx->ClearState(); m_11ctx->Flush(); }
		m_targets.clear();
	}

	void UIOverlay::begin(float width, float height)
	{
		m_w = width; m_h = height;
		m_cmds.clear();
	}

	void UIOverlay::add_rect(float x, float y, float w, float h, float r, float g, float b, float a, float radius)
	{
		Cmd c{}; c.type = 0; c.x = x; c.y = y; c.w = w; c.h = h;
		c.r = r; c.g = g; c.b = b; c.a = a; c.radius = radius;
		m_cmds.push_back(std::move(c));
	}

	void UIOverlay::add_text(float x, float y, float w, float h, const wchar_t* text,
		float size, float r, float g, float b, float a, int align, int weight)
	{
		Cmd c{}; c.type = 1; c.x = x; c.y = y; c.w = w; c.h = h;
		c.r = r; c.g = g; c.b = b; c.a = a; c.size = size; c.align = align; c.weight = weight;
		c.text = text ? text : L"";
		m_cmds.push_back(std::move(c));
	}

	void UIOverlay::add_line(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thickness)
	{
		Cmd c{}; c.type = 2; c.x = x1; c.y = y1; c.x2 = x2; c.y2 = y2;
		c.r = r; c.g = g; c.b = b; c.a = a; c.thickness = thickness <= 0 ? 1.0f : thickness;
		m_cmds.push_back(std::move(c));
	}

	void UIOverlay::add_image(float x, float y, float w, float h, const wchar_t* path, float r, float g, float b, float a)
	{
		Cmd c{}; c.type = 3; c.x = x; c.y = y; c.w = w; c.h = h;
		c.r = r; c.g = g; c.b = b; c.a = a; c.text = path ? path : L"";
		m_cmds.push_back(std::move(c));
	}

	void UIOverlay::push_clip(float x, float y, float w, float h)
	{
		Cmd c{}; c.type = 4; c.x = x; c.y = y; c.w = w; c.h = h;
		m_cmds.push_back(std::move(c));
	}

	void UIOverlay::pop_clip()
	{
		Cmd c{}; c.type = 5;
		m_cmds.push_back(std::move(c));
	}

	ID2D1Bitmap* UIOverlay::bitmap_for(const std::wstring& path)
	{
		auto it = m_bitmaps.find(path);
		if (it != m_bitmaps.end()) return it->second.Get();   // cached (even a null = known-missing, no per-frame retry)

		ComPtr<ID2D1Bitmap> bmp;
		if (m_wic && !path.empty())
		{
			ComPtr<IWICBitmapDecoder> dec;
			if (SUCCEEDED(m_wic->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
				WICDecodeMetadataCacheOnLoad, &dec)))
			{
				ComPtr<IWICBitmapFrameDecode> frame;
				if (SUCCEEDED(dec->GetFrame(0, &frame)))
				{
					ComPtr<IWICFormatConverter> conv;
					if (SUCCEEDED(m_wic->CreateFormatConverter(&conv)) &&
						SUCCEEDED(conv->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA,
							WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeMedianCut)))
					{
						m_d2dCtx->CreateBitmapFromWicBitmap(conv.Get(), nullptr, &bmp);
					}
				}
			}
		}
		m_bitmaps[path] = bmp;   // store (possibly null) so a missing file isn't re-decoded every frame
		return bmp.Get();
	}

	IDWriteTextFormat* UIOverlay::format_for(float size, int weight)
	{
		if (size < 6) size = 6; if (size > 200) size = 200;
		unsigned long long key = ((unsigned long long)(int)(size + 0.5f) << 16) | (unsigned)weight;
		auto it = m_formats.find(key);
		if (it != m_formats.end()) return it->second.Get();

		DWRITE_FONT_WEIGHT w = DWRITE_FONT_WEIGHT_NORMAL;
		if (weight >= 700) w = DWRITE_FONT_WEIGHT_BOLD;
		else if (weight >= 600) w = DWRITE_FONT_WEIGHT_SEMI_BOLD;

		ComPtr<IDWriteTextFormat> fmt;
		if (FAILED(m_dwrite->CreateTextFormat(L"Segoe UI", nullptr, w, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, size, L"en-us", &fmt)))
			return nullptr;
		IDWriteTextFormat* raw = fmt.Get();
		m_formats[key] = fmt;
		return raw;
	}

	void UIOverlay::render(ID3D12Resource* backBuffer)
	{
		if (!m_ready || !backBuffer || m_cmds.empty()) { m_cmds.clear(); return; }

		// Get (or create) the D2D bitmap that aliases this back buffer through D3D11On12.
		auto& t = m_targets[backBuffer];
		if (!t.bitmap)
		{
			D3D11_RESOURCE_FLAGS rf{}; rf.BindFlags = D3D11_BIND_RENDER_TARGET;
			if (FAILED(m_11on12->CreateWrappedResource(backBuffer, &rf,
				D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_PRESENT, IID_PPV_ARGS(&t.wrapped))))
			{
				m_cmds.clear(); return;
			}
			ComPtr<IDXGISurface> surface;
			if (FAILED(t.wrapped.As(&surface))) { t = Target{}; m_cmds.clear(); return; }
			D2D1_BITMAP_PROPERTIES1 bp = D2D1::BitmapProperties1(
				D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
				D2D1::PixelFormat(DXGI_FORMAT_R8G8B8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96.0f, 96.0f);
			if (FAILED(m_d2dCtx->CreateBitmapFromDxgiSurface(surface.Get(), &bp, &t.bitmap)))
			{
				t = Target{}; m_cmds.clear(); return;
			}
		}

		ID3D11Resource* wrappedRaw = t.wrapped.Get();
		m_11on12->AcquireWrappedResources(&wrappedRaw, 1);

		m_d2dCtx->SetTarget(t.bitmap.Get());
		m_d2dCtx->BeginDraw();

		int clipDepth = 0;   // balance PushAxisAlignedClip/PopAxisAlignedClip before EndDraw (D2D asserts otherwise)
		for (const auto& c : m_cmds)
		{
			m_brush->SetColor(D2D1::ColorF(c.r, c.g, c.b, c.a));
			if (c.type == 0) // rect
			{
				D2D1_RECT_F rc = D2D1::RectF(c.x, c.y, c.x + c.w, c.y + c.h);
				if (c.radius > 0.5f)
				{
					D2D1_ROUNDED_RECT rr = D2D1::RoundedRect(rc, c.radius, c.radius);
					m_d2dCtx->FillRoundedRectangle(rr, m_brush.Get());
				}
				else m_d2dCtx->FillRectangle(rc, m_brush.Get());
			}
			else if (c.type == 1) // text
			{
				IDWriteTextFormat* fmt = format_for(c.size <= 0 ? 16.0f : c.size, c.weight <= 0 ? 600 : c.weight);
				if (fmt && !c.text.empty())
				{
					fmt->SetTextAlignment(c.align == 1 ? DWRITE_TEXT_ALIGNMENT_CENTER
						: c.align == 2 ? DWRITE_TEXT_ALIGNMENT_TRAILING : DWRITE_TEXT_ALIGNMENT_LEADING);
					fmt->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
					D2D1_RECT_F rc = D2D1::RectF(c.x, c.y, c.x + c.w, c.y + c.h);
					m_d2dCtx->DrawTextW(c.text.c_str(), (UINT32)c.text.size(), fmt, rc, m_brush.Get());
				}
			}
			else if (c.type == 2) // line
			{
				m_d2dCtx->DrawLine(D2D1::Point2F(c.x, c.y), D2D1::Point2F(c.x2, c.y2), m_brush.Get(), c.thickness);
			}
			else if (c.type == 3) // image
			{
				ID2D1Bitmap* bmp = bitmap_for(c.text);
				if (bmp)
				{
					D2D1_RECT_F rc = D2D1::RectF(c.x, c.y, c.x + c.w, c.y + c.h);
					m_d2dCtx->DrawBitmap(bmp, rc, c.a, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
				}
			}
			else if (c.type == 4) // push clip
			{
				m_d2dCtx->PushAxisAlignedClip(D2D1::RectF(c.x, c.y, c.x + c.w, c.y + c.h), D2D1_ANTIALIAS_MODE_ALIASED);
				++clipDepth;
			}
			else if (c.type == 5) // pop clip
			{
				if (clipDepth > 0) { m_d2dCtx->PopAxisAlignedClip(); --clipDepth; }
			}
		}

		while (clipDepth-- > 0) m_d2dCtx->PopAxisAlignedClip();   // safety: never leave a clip open at EndDraw
		m_d2dCtx->EndDraw();
		m_d2dCtx->SetTarget(nullptr);

		m_11on12->ReleaseWrappedResources(&wrappedRaw, 1);
		m_11ctx->Flush(); // submit the D2D work onto the shared queue (after the 3D, before Present)

		m_cmds.clear();
	}
}
