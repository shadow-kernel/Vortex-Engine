#include "DX12Renderer.h"
#include "../Resources/ResourceRegistry.h"
#include <algorithm>
#include <memory>
#include <thread>
#include <atomic>
#include <unordered_map>

namespace vortex::graphics::dx12
{
	DX12Renderer& DX12Renderer::instance()
	{
		static DX12Renderer inst;
		return inst;
	}

	bool DX12Renderer::initialize(const RendererDesc& desc)
	{
		if (m_initialized) return true;
		if (!desc.hwnd || desc.width == 0 || desc.height == 0) return false;

		auto& core = DX12Core::instance(); 
		if (!core.initialize()) return false;
		if (!m_command_queue.initialize(core.device())) return false;

		SwapchainDesc sc_desc{};
		sc_desc.hwnd = desc.hwnd;
		sc_desc.width = desc.width;
		sc_desc.height = desc.height;
		sc_desc.buffer_count = 2;

		if (!m_swapchain.initialize(core.factory(), m_command_queue.queue(), core.device(), sc_desc))
			return false;

		if (!create_command_allocators()) return false;
		if (!create_command_list()) return false;

		if (!m_depth_buffer.initialize(core.device(), desc.width, desc.height, DXGI_FORMAT_D32_FLOAT))
			return false;

		if (!m_pipeline.initialize(core.device())) return false;

		if (!m_pipeline_3d.initialize(core.device(), DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_D32_FLOAT))
			return false;

		if (m_grid_pipeline.initialize(core.device(), DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_D32_FLOAT))
		{
			OutputDebugStringA("Grid pipeline OK\n");
		}
		else
		{
			OutputDebugStringA("Grid pipeline FAILED\n");
			m_grid_visible = false;
		}

		if (m_skybox_pipeline.initialize(core.device(), DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_D32_FLOAT))
		{
			OutputDebugStringA("Skybox pipeline OK\n");
		}
		else
		{
			OutputDebugStringA("Skybox pipeline FAILED\n");
			m_skybox_enabled = false;
		}

		// Upscale pipeline (render-scale composite). Writes the swapchain format. If it fails, render-scale just
		// stays disabled (m_render_scale<1 falls back to direct rendering); the rest of the renderer is unaffected.
		if (m_upscale.initialize(core.device(), DXGI_FORMAT_R8G8B8A8_UNORM))
			OutputDebugStringA("Upscale pipeline OK\n");
		else
			OutputDebugStringA("Upscale pipeline FAILED (render-scale will fall back to native)\n");

		if (!m_geometry.initialize(core.device())) return false;
		if (!create_constant_buffers()) return false;
		if (!create_srv_heap()) return false;
		create_grid_resources();
		create_skybox_resources();

		ResourceRegistry::instance().initialize(core.device());

		// 2D UI overlay (optional — if D2D/DirectWrite init fails the 3D renderer is unaffected).
		if (m_ui_overlay.initialize(core.device(), m_command_queue.queue(), DX12Swapchain::MaxBufferCount))
			OutputDebugStringA("UI overlay OK\n");
		else
			OutputDebugStringA("UI overlay unavailable (continuing without it)\n");

		m_initialized = true;
		return true;
	}

	void DX12Renderer::shutdown()
	{
		if (!m_initialized) return;
		m_command_queue.flush();

		// Release the UI overlay (its wrapped resources alias the back buffers) before any swapchain teardown.
		m_ui_overlay.shutdown();

		// Close the standalone game window (second swapchain) if it's open.
		destroy_game_window();

		// Destroy all secondary render targets
		m_render_targets.clear();
		
		ResourceRegistry::instance().shutdown();
		{
			std::lock_guard<std::mutex> lock(m_queue_mutex);
			m_render_queue.clear();
			m_submit_queue.clear();
		}

		if (m_per_frame_cb && m_per_frame_cb_mapped) { m_per_frame_cb->Unmap(0, nullptr); m_per_frame_cb_mapped = nullptr; }
		m_per_frame_cb.Reset();
		if (m_per_object_cb && m_per_object_cb_mapped) { m_per_object_cb->Unmap(0, nullptr); m_per_object_cb_mapped = nullptr; }
		m_per_object_cb.Reset();
		if (m_instance_vb && m_instance_vb_mapped) { m_instance_vb->Unmap(0, nullptr); m_instance_vb_mapped = nullptr; }
		m_instance_vb.Reset();
		if (m_light_cb && m_light_cb_mapped) { m_light_cb->Unmap(0, nullptr); m_light_cb_mapped = nullptr; }
		m_light_cb.Reset();
		if (m_grid_cb && m_grid_cb_mapped) { m_grid_cb->Unmap(0, nullptr); m_grid_cb_mapped = nullptr; }
		m_grid_cb.Reset();
		if (m_skybox_cb && m_skybox_cb_mapped) { m_skybox_cb->Unmap(0, nullptr); m_skybox_cb_mapped = nullptr; }
		m_skybox_cb.Reset();
		m_grid_vertex_buffer.Reset();

		m_depth_buffer.shutdown();
		m_scaled_rt.shutdown();
		m_geometry.shutdown();
		m_grid_pipeline.shutdown();
		m_skybox_pipeline.shutdown();
		m_upscale.shutdown();
		m_pipeline_3d.shutdown();
		m_pipeline.shutdown();
		m_command_list.Reset();
		for (auto& a : m_command_allocators) a.Reset();
		m_swapchain.shutdown();
		m_command_queue.shutdown();
		DX12Core::instance().shutdown();
		m_initialized = false;
	}

	void DX12Renderer::resize(u32 w, u32 h)
	{
		if (!m_initialized || w == 0 || h == 0) return;
		// FULLY TEAR DOWN the D3D11On12 UI overlay before ResizeBuffers. The overlay's 11on12 device aliases the
		// swapchain back buffers (its Flush only QUEUES work, it does not idle the device), so if it still holds
		// those buffers when ResizeBuffers runs, the flip chain never rebinds and the display FREEZES on the last
		// pre-resize frame (render keeps running, FPS even rises). shutdown() ClearState+Flush+Resets the 11on12
		// device; the queue flush then waits for that work so the buffers are fully released; we re-init a fresh
		// overlay over the new buffers afterwards. This is the robust cure for the F11/maximize freeze.
		auto* dev = DX12Core::instance().device();
		m_ui_overlay.shutdown();
		m_command_queue.flush();
		if (!m_swapchain.resize(w, h)) { m_ui_overlay.initialize(dev, m_command_queue.queue(), DX12Swapchain::MaxBufferCount); return; }
		m_depth_buffer.resize(dev, w, h);
		m_ui_overlay.initialize(dev, m_command_queue.queue(), DX12Swapchain::MaxBufferCount);   // fresh 11on12 over the new buffers
		// GPU is idle after the flush; collapse per-buffer fence tracking so the next frame's wait is correct.
		UINT64 fv = m_command_queue.current_fence_value();
		for (auto& v : m_frame_fence_values) v = fv;

		// FLIP-MODEL FREEZE FIX (F11 / maximize): after ResizeBuffers, a borderless/maximized window's DWM
		// composition binding is stale until a frame is PRESENTED at the new size — otherwise the display stays
		// frozen on the last pre-resize image even though render_frame keeps running (FPS even rises). Cycle
		// every back buffer through a bare clear+present (no 3D, no UI overlay, which was just invalidated) so
		// DWM re-acquires the resized buffers. Resize is already a flush-heavy slow path, so the cost is moot.
		for (u32 i = 0; i < m_swapchain.buffer_count(); ++i)
		{
			u32 idx = m_swapchain.current_back_buffer_index();
			m_command_allocators[idx]->Reset();
			m_command_list->Reset(m_command_allocators[idx].Get(), nullptr);

			D3D12_RESOURCE_BARRIER b{};
			b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
			b.Transition.pResource = m_swapchain.current_back_buffer();
			b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
			b.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
			b.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
			m_command_list->ResourceBarrier(1, &b);
			m_command_list->ClearRenderTargetView(m_swapchain.current_rtv(), m_clear_color, 0, nullptr);
			b.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
			b.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
			m_command_list->ResourceBarrier(1, &b);
			m_command_list->Close();
			m_command_queue.execute_command_list(m_command_list.Get());
			m_command_queue.signal_and_wait();
			m_swapchain.present(false);   // windowed ALLOW_TEARING flag intact (vsync off)
		}
		fv = m_command_queue.current_fence_value();
		for (auto& v : m_frame_fence_values) v = fv;
	}

	void DX12Renderer::swap_render_queue()
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		// Nothing new submitted this frame -> KEEP last frame's render queue (reuse it). This lets the game
		// submit a static scene ONCE and re-render it every frame with only the camera changing — a big CPU
		// win (no per-frame scene walk + interop). Also prevents flicker on idle frames.
		if (m_submit_queue.empty()) return;
		m_render_queue.swap(m_submit_queue);
		m_submit_queue.clear();
		m_queue_dirty = true;   // new geometry -> re-sort + rebuild runs this frame
	}

	void DX12Renderer::on_scene_switch()
	{
		if (!m_initialized) return;
		// GPU idle BEFORE the managed layer frees the old scene's meshes (DeleteMesh has no flush).
		m_command_queue.flush();
		// Drop the overlay's cached wrapped back-buffer bitmaps (they alias the live back buffers).
		m_ui_overlay.invalidate_targets();
		// Clear the stale render queue so the one frame before the new scene re-submits doesn't draw freed geometry.
		{
			std::lock_guard<std::mutex> lock(m_queue_mutex);
			m_render_queue.clear();
			m_submit_queue.clear();
		}
		// The GPU is idle; collapse per-buffer fence tracking to the completed value.
		UINT64 fv = m_command_queue.current_fence_value();
		for (auto& v : m_frame_fence_values) v = fv;
	}

	void DX12Renderer::request_capture(const char* path)
	{
		if (!path) return;
		m_capture_path = path;
		m_capture_requested = true;
	}

	// Copy the current back buffer (already rendered, in PRESENT state) into a READBACK buffer and write a
	// 32-bit top-down BMP. Self-contained: flushes, records a copy on the frame command list, waits, maps.
	bool DX12Renderer::capture_backbuffer_to_bmp(const char* path)
	{
		if (!m_initialized) return false;
		auto* device = DX12Core::instance().device();
		ID3D12Resource* bb = m_swapchain.current_back_buffer();
		if (!device || !bb) return false;

		D3D12_RESOURCE_DESC rd = bb->GetDesc();
		UINT width = (UINT)rd.Width;
		UINT height = rd.Height;

		D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp{};
		UINT numRows = 0; UINT64 rowSizeBytes = 0, totalBytes = 0;
		device->GetCopyableFootprints(&rd, 0, 1, 0, &fp, &numRows, &rowSizeBytes, &totalBytes);

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_READBACK;
		D3D12_RESOURCE_DESC bd{};
		bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		bd.Width = totalBytes; bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1;
		bd.Format = DXGI_FORMAT_UNKNOWN; bd.SampleDesc.Count = 1;
		bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
		ComPtr<ID3D12Resource> readback;
		if (FAILED(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd,
			D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback)))) return false;

		m_command_queue.flush();
		u32 idx = m_swapchain.current_back_buffer_index();
		m_command_allocators[idx]->Reset();
		m_command_list->Reset(m_command_allocators[idx].Get(), nullptr);

		D3D12_RESOURCE_BARRIER b{};
		b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		b.Transition.pResource = bb;
		b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
		b.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
		b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
		m_command_list->ResourceBarrier(1, &b);

		D3D12_TEXTURE_COPY_LOCATION dst{}; dst.pResource = readback.Get();
		dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; dst.PlacedFootprint = fp;
		D3D12_TEXTURE_COPY_LOCATION src{}; src.pResource = bb;
		src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; src.SubresourceIndex = 0;
		m_command_list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);

		b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
		b.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
		m_command_list->ResourceBarrier(1, &b);

		m_command_list->Close();
		m_command_queue.execute_command_list(m_command_list.Get());
		m_command_queue.signal_and_wait();

		void* mapped = nullptr;
		D3D12_RANGE readRange{ 0, (SIZE_T)totalBytes };
		if (FAILED(readback->Map(0, &readRange, &mapped))) return false;

		bool ok = false;
		FILE* f = nullptr; fopen_s(&f, path, "wb");
		if (f)
		{
			UINT rowPitch = fp.Footprint.RowPitch;
			UINT imgSize = width * height * 4;
			UINT fileSize = 54 + imgSize;
			unsigned char fh[14] = { 0 };
			fh[0] = 'B'; fh[1] = 'M';
			*(UINT*)&fh[2] = fileSize;
			*(UINT*)&fh[10] = 54;
			fwrite(fh, 1, 14, f);
			unsigned char ih[40] = { 0 };
			*(UINT*)&ih[0] = 40;
			*(int*)&ih[4] = (int)width;
			*(int*)&ih[8] = -(int)height; // negative => top-down
			*(unsigned short*)&ih[12] = 1;
			*(unsigned short*)&ih[14] = 32;
			*(UINT*)&ih[20] = imgSize;
			fwrite(ih, 1, 40, f);
			std::vector<unsigned char> row(width * 4);
			const unsigned char* base = (const unsigned char*)mapped;
			bool bgra = (rd.Format == DXGI_FORMAT_B8G8R8A8_UNORM);
			for (UINT y = 0; y < height; ++y)
			{
				const unsigned char* s = base + (size_t)y * rowPitch;
				for (UINT x = 0; x < width; ++x)
				{
					if (bgra) { row[x * 4 + 0] = s[x * 4 + 0]; row[x * 4 + 1] = s[x * 4 + 1]; row[x * 4 + 2] = s[x * 4 + 2]; }
					else { row[x * 4 + 0] = s[x * 4 + 2]; row[x * 4 + 1] = s[x * 4 + 1]; row[x * 4 + 2] = s[x * 4 + 0]; } // RGBA->BGR
					row[x * 4 + 3] = s[x * 4 + 3];
				}
				fwrite(row.data(), 1, width * 4, f);
			}
			fclose(f);
			ok = true;
		}
		D3D12_RANGE noWrite{ 0, 0 };
		readback->Unmap(0, &noWrite);
		return ok;
	}

	bool DX12Renderer::ensure_scaled_rt(u32 width, u32 height)
	{
		if (width < 1) width = 1;
		if (height < 1) height = 1;
		if (m_scaled_rt.is_initialized() && m_scaled_rt.width() == width && m_scaled_rt.height() == height)
			return true;
		// The RT may be referenced by an in-flight frame — idle the GPU before destroying/recreating it. This only
		// runs on a scale/window-size change (rare), not per frame, so the stall is a one-off.
		m_command_queue.flush();
		auto* dev = DX12Core::instance().device();
		if (!dev) return false;
		// MUST be R8G8B8A8_UNORM to match the 3D/grid/skybox PSOs (DX12RenderTarget defaults to BGRA — would mismatch).
		if (!m_scaled_rt.is_initialized())
			return m_scaled_rt.initialize(dev, width, height, DXGI_FORMAT_R8G8B8A8_UNORM);
		return m_scaled_rt.resize(dev, width, height);
	}

	void DX12Renderer::render_frame()
	{
		if (!m_initialized) return;

		// Swap render queues (thread-safe) and clear submit queue for next frame
		swap_render_queue();
		
		// Update FPS counter
		m_frame_count++;
		auto now = std::chrono::high_resolution_clock::now();
		auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - m_last_fps_time).count();
		if (elapsed >= 500) // Update every 0.5 seconds
		{
			m_current_fps = static_cast<int>((m_frame_count * 1000) / elapsed);
			m_frame_count = 0;
			m_last_fps_time = now;
		}
		
		// Reset per-frame stats
		m_draw_call_count = 0;
		m_vertex_count = 0;
		m_instances_tested = 0;
		m_instances_drawn = 0;
		
		u32 idx = m_swapchain.current_back_buffer_index();

		// Wait for this frame's previous work to complete (proper double buffering)
		// Only wait if GPU hasn't finished with this buffer yet
		m_command_queue.wait_for_fence_value(m_frame_fence_values[idx]);

		update_per_frame_constants();   // aspect from swapchain dims — a UNIFORM render-scale keeps it matching

		m_command_allocators[idx]->Reset();

		auto* pso = m_render_queue.empty() ? m_pipeline.pipeline_state() :
			(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
		m_command_list->Reset(m_command_allocators[idx].Get(), pso);

		// Render-scale: when < 1.0 (and the upscale pipeline is ready) render the 3D into a smaller offscreen RT,
		// then upscale it onto the full-res back buffer (this is also the slot DLSS plugs into). Scale 1.0 takes the
		// direct path below = byte-for-byte the old behaviour, zero overhead. This is the safe default.
		bool use_scale = (m_render_scale < 0.999f) && m_upscale.is_initialized();
		u32 out_w = m_swapchain.width(), out_h = m_swapchain.height();
		if (use_scale)
		{
			u32 rw = (u32)((float)out_w * m_render_scale + 0.5f); if (rw < 1) rw = 1;
			u32 rh = (u32)((float)out_h * m_render_scale + 0.5f); if (rh < 1) rh = 1;
			if (!ensure_scaled_rt(rw, rh)) use_scale = false;   // creation failed -> fall back to direct
		}

		D3D12_RESOURCE_BARRIER barrier{};
		barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		barrier.Transition.pResource = m_swapchain.current_back_buffer();
		barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;

		if (use_scale)
		{
			// ---- 3D into the scaled offscreen RT (skybox + grid + scene read m_active_*) ----
			m_active_rtv = m_scaled_rt.rtv();
			m_active_dsv = m_scaled_rt.dsv();
			m_active_width = m_scaled_rt.width();
			m_active_height = m_scaled_rt.height();

			m_scaled_rt.transition_to_render_target(m_command_list.Get());

			D3D12_VIEWPORT svp{}; svp.Width = (float)m_scaled_rt.width(); svp.Height = (float)m_scaled_rt.height(); svp.MaxDepth = 1.0f;
			D3D12_RECT ssc{}; ssc.right = (LONG)m_scaled_rt.width(); ssc.bottom = (LONG)m_scaled_rt.height();
			m_command_list->RSSetViewports(1, &svp);
			m_command_list->RSSetScissorRects(1, &ssc);
			auto srtv = m_scaled_rt.rtv();
			auto sdsv = m_scaled_rt.dsv();
			m_command_list->ClearRenderTargetView(srtv, m_clear_color, 0, nullptr);
			m_command_list->ClearDepthStencilView(sdsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
			m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
			m_command_list->OMSetRenderTargets(1, &srtv, FALSE, &sdsv);

			if (m_skybox_enabled) render_skybox();
			if (m_grid_visible) render_grid();
			if (!m_render_queue.empty()) render_3d_scene();

			// ---- upscale composite onto the full-res back buffer ----
			barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
			barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
			m_command_list->ResourceBarrier(1, &barrier);

			m_scaled_rt.transition_to_shader_resource(m_command_list.Get());

			auto bbrtv = m_swapchain.current_rtv();
			D3D12_VIEWPORT fvp{}; fvp.Width = (float)out_w; fvp.Height = (float)out_h; fvp.MaxDepth = 1.0f;
			D3D12_RECT fsc{}; fsc.right = (LONG)out_w; fsc.bottom = (LONG)out_h;
			m_command_list->RSSetViewports(1, &fvp);
			m_command_list->RSSetScissorRects(1, &fsc);
			m_command_list->OMSetRenderTargets(1, &bbrtv, FALSE, nullptr);   // no depth on the composite

			m_command_list->SetPipelineState(m_upscale.pipeline_state());
			m_command_list->SetGraphicsRootSignature(m_upscale.root_signature());
			ID3D12DescriptorHeap* heaps[] = { m_scaled_rt.srv_heap() };
			m_command_list->SetDescriptorHeaps(1, heaps);
			m_command_list->SetGraphicsRootDescriptorTable(0, m_scaled_rt.srv_gpu());
			m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
			m_command_list->DrawInstanced(3, 1, 0, 0);   // fullscreen triangle

			m_scaled_rt.transition_to_render_target(m_command_list.Get());   // leave RT ready for next frame

			barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
			barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
			m_command_list->ResourceBarrier(1, &barrier);
		}
		else
		{
			// ---- direct path: render straight to the back buffer at native res (unchanged) ----
			m_active_rtv = m_swapchain.current_rtv();
			m_active_dsv = m_depth_buffer.dsv();
			m_active_width = m_swapchain.width();
			m_active_height = m_swapchain.height();

			barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
			barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
			m_command_list->ResourceBarrier(1, &barrier);

			auto rtv = m_swapchain.current_rtv();
			auto dsv = m_depth_buffer.dsv();

			D3D12_VIEWPORT vp{}; vp.Width = (float)m_swapchain.width(); vp.Height = (float)m_swapchain.height(); vp.MaxDepth = 1.0f;
			D3D12_RECT sc{}; sc.right = m_swapchain.width(); sc.bottom = m_swapchain.height();
			m_command_list->RSSetViewports(1, &vp);
			m_command_list->RSSetScissorRects(1, &sc);
			m_command_list->ClearRenderTargetView(rtv, m_clear_color, 0, nullptr);
			m_command_list->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
			m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
			m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);

			if (m_skybox_enabled) render_skybox();
			if (m_grid_visible) render_grid();
			if (!m_render_queue.empty()) render_3d_scene();

			barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
			barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
			m_command_list->ResourceBarrier(1, &barrier);
		}

		m_command_list->Close();

		m_command_queue.execute_command_list(m_command_list.Get());
		
		// Signal after this frame's commands are queued (non-blocking)
		m_frame_fence_values[idx] = m_command_queue.signal();

		// 2D UI overlay (Direct2D over the same back buffer) — drawn after the 3D, before present.
		m_ui_overlay.render(m_swapchain.current_back_buffer());

		// Reliable verification: capture the fully-rendered back buffer (3D + UI) BEFORE present.
		if (m_capture_requested) { m_capture_requested = false; capture_backbuffer_to_bmp(m_capture_path.c_str()); }

		m_swapchain.present(m_vsync_enabled);
		// Note: m_render_queue is NOT cleared - we keep last frame's data
		// for re-rendering if no new data is submitted (prevents flickering)
		}

		bool DX12Renderer::create_game_window(HWND hwnd, u32 width, u32 height)
	{
		if (!m_initialized || !hwnd || width == 0 || height == 0) return false;
		if (m_game_window_active) destroy_game_window();

		auto& core = DX12Core::instance();
		SwapchainDesc sc_desc{};
		sc_desc.hwnd = hwnd;
		sc_desc.width = width;
		sc_desc.height = height;
		sc_desc.buffer_count = 2;
		if (!m_game_swapchain.initialize(core.factory(), m_command_queue.queue(), core.device(), sc_desc))
			return false;
		if (!m_game_depth.initialize(core.device(), width, height, DXGI_FORMAT_D32_FLOAT))
		{
			m_game_swapchain.shutdown();
			return false;
		}
		if (FAILED(core.device()->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
				IID_PPV_ARGS(&m_game_cmd_allocator))))
		{
			m_game_depth.shutdown();
			m_game_swapchain.shutdown();
			return false;
		}
		m_game_window_active = true;
		return true;
	}

	void DX12Renderer::render_game_window()
	{
		if (!m_initialized || !m_game_window_active) return;

		// The command list + queue are shared with the editor frame; finish all prior GPU work before
		// reusing them. Simple full sync — perf is not the priority for the play window.
		m_command_queue.flush();

		// Target the game window's own swapchain + depth at its own size, using the CURRENT camera
		// (the caller sets the game's main camera before calling this).
		m_active_rtv = m_game_swapchain.current_rtv();
		m_active_dsv = m_game_depth.dsv();
		m_active_width = m_game_swapchain.width();
		m_active_height = m_game_swapchain.height();

		update_per_frame_constants();

		m_game_cmd_allocator->Reset();
		auto* pso = m_render_queue.empty() ? m_pipeline.pipeline_state() :
			(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
		m_command_list->Reset(m_game_cmd_allocator.Get(), pso);

		D3D12_RESOURCE_BARRIER barrier{};
		barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		barrier.Transition.pResource = m_game_swapchain.current_back_buffer();
		barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
		barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
		m_command_list->ResourceBarrier(1, &barrier);

		D3D12_VIEWPORT vp{}; vp.Width = (float)m_active_width; vp.Height = (float)m_active_height; vp.MaxDepth = 1.0f;
		D3D12_RECT sc{}; sc.right = (LONG)m_active_width; sc.bottom = (LONG)m_active_height;
		m_command_list->RSSetViewports(1, &vp);
		m_command_list->RSSetScissorRects(1, &sc);
		m_command_list->ClearRenderTargetView(m_active_rtv, m_clear_color, 0, nullptr);
		m_command_list->ClearDepthStencilView(m_active_dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->OMSetRenderTargets(1, &m_active_rtv, FALSE, &m_active_dsv);

		if (m_skybox_enabled) render_skybox();
		if (m_grid_visible) render_grid();
		if (!m_render_queue.empty()) render_3d_scene();

		barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
		m_command_list->ResourceBarrier(1, &barrier);
		m_command_list->Close();

		m_command_queue.execute_command_list(m_command_list.Get());
		m_command_queue.flush(); // wait before present (safe)

		// 2D UI overlay (Direct2D over the same back buffer) — drawn after the 3D, before present.
		m_ui_overlay.render(m_game_swapchain.current_back_buffer());

		m_game_swapchain.present(false);
	}

	void DX12Renderer::resize_game_window(u32 width, u32 height)
	{
		if (!m_game_window_active || width == 0 || height == 0) return;
		m_command_queue.flush();
		m_ui_overlay.invalidate_targets();   // drop cached bitmaps aliasing the old game back buffers
		m_game_swapchain.resize(width, height);
		m_game_depth.resize(DX12Core::instance().device(), width, height);
	}

	void DX12Renderer::destroy_game_window()
	{
		if (!m_game_window_active) return;
		m_command_queue.flush();
		m_ui_overlay.invalidate_targets();   // wrapped game back buffers are about to be freed
		m_game_window_active = false;
		m_game_depth.shutdown();
		m_game_swapchain.shutdown();
		m_game_cmd_allocator.Reset();
	}

	// --- Frustum culling helpers (don't render what the camera can't see) ---
	namespace {
		struct FrustumPlanes { float p[6][4]; };

		// Extract the 6 normalized frustum planes from a row-major view-projection matrix (Gribb-Hartmann).
		// Plane form (a,b,c,d): a point is inside that plane when a*x+b*y+c*z+d >= 0.
		FrustumPlanes extract_frustum(const DirectX::XMFLOAT4X4& m)
		{
			FrustumPlanes f = {
				{
					{ m._14 + m._11, m._24 + m._21, m._34 + m._31, m._44 + m._41 }, // left
					{ m._14 - m._11, m._24 - m._21, m._34 - m._31, m._44 - m._41 }, // right
					{ m._14 + m._12, m._24 + m._22, m._34 + m._32, m._44 + m._42 }, // bottom
					{ m._14 - m._12, m._24 - m._22, m._34 - m._32, m._44 - m._42 }, // top
					{ m._13,         m._23,         m._33,         m._43         }, // near
					{ m._14 - m._13, m._24 - m._23, m._34 - m._33, m._44 - m._43 }, // far
				}
			};
			for (int i = 0; i < 6; ++i)
			{
				float a = f.p[i][0], b = f.p[i][1], c = f.p[i][2];
				float len = sqrtf(a * a + b * b + c * c);
				if (len > 1e-6f) { f.p[i][0] /= len; f.p[i][1] /= len; f.p[i][2] /= len; f.p[i][3] /= len; }
			}
			return f;
		}

		// True if a world-space bounding sphere is at least partially inside the frustum.
		bool sphere_in_frustum(const FrustumPlanes& f, float cx, float cy, float cz, float r)
		{
			for (int i = 0; i < 6; ++i)
			{
				float dist = f.p[i][0] * cx + f.p[i][1] * cy + f.p[i][2] * cz + f.p[i][3];
				if (dist < -r) return false; // fully outside this plane -> not visible
			}
			return true;
		}
	}

	void DX12Renderer::render_3d_scene()
	{
		auto rtv = m_active_rtv;
		auto dsv = m_active_dsv;
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);

	auto* pso = m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state();
	auto* double_sided_pso = m_pipeline_3d.double_sided_pso();
	m_command_list->SetPipelineState(pso);
		m_command_list->SetGraphicsRootSignature(m_pipeline_3d.root_signature());
		m_command_list->SetGraphicsRootConstantBufferView(0, m_per_frame_cb->GetGPUVirtualAddress());
		
		// Bind light buffer at root parameter 2
		m_command_list->SetGraphicsRootConstantBufferView(2, m_light_cb->GetGPUVirtualAddress());

		// Set descriptor heap for texture sampling (from ResourceRegistry)
		auto* srv_heap = ResourceRegistry::instance().srv_heap();
		if (srv_heap)
		{
			ID3D12DescriptorHeap* heaps[] = { srv_heap };
			m_command_list->SetDescriptorHeaps(1, heaps);
		}

		auto& reg = ResourceRegistry::instance();
		
		// Limit to MAX_RENDER_OBJECTS to prevent buffer overflow
		size_t objectCount = (std::min)(m_render_queue.size(), static_cast<size_t>(MAX_RENDER_OBJECTS));
		if (objectCount == 0) return;

		bool preCullRan = false;   // the pre-cull mutates the queue -> forces a layout rebuild this frame
		// PRE-CULL for huge instanced scenes: when far more instances are SUBMITTED than can render, the O(n log n)
		// sort below over ALL of them was the bottleneck (millions of items). Drop frustum-invisible instances FIRST
		// (parallel), so the sort + run-build only touch what's actually on screen. Only kicks in over the cap, so
		// normal scenes are untouched.
		if (m_render_queue.size() > static_cast<size_t>(MAX_RENDER_OBJECTS))
		{
			using namespace DirectX;
			preCullRan = true;
			FrustumPlanes preFr = extract_frustum(m_frame_constants.view_projection);
			const size_t N = m_render_queue.size();
			std::vector<unsigned char> vis(N, 0);
			auto cullRange = [&](size_t a, size_t b)
			{
				std::unordered_map<id::id_type, XMFLOAT4> cache;   // mesh_id -> (local center xyz, radius in w)
				cache.reserve(64);
				for (size_t k = a; k < b; ++k)
				{
					const auto& it = m_render_queue[k];
					XMFLOAT4 bd;
					auto cit = cache.find(it.mesh_id);
					if (cit == cache.end())
					{
						Mesh* mp = reg.get_mesh(it.mesh_id);
						float mnx = 0, mny = 0, mnz = 0, mxx = 1, mxy = 1, mxz = 1;
						if (mp && mp->is_valid()) { mp->get_min(mnx, mny, mnz); mp->get_max(mxx, mxy, mxz); }
						float dx = mxx - mnx, dy = mxy - mny, dz = mxz - mnz;
						bd = XMFLOAT4((mnx + mxx) * 0.5f, (mny + mxy) * 0.5f, (mnz + mxz) * 0.5f, 0.5f * sqrtf(dx * dx + dy * dy + dz * dz));
						cache.emplace(it.mesh_id, bd);
					}
					else bd = cit->second;
					const XMFLOAT4X4& W = it.world_matrix;
					XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(bd.x, bd.y, bd.z, 1.f), XMLoadFloat4x4(&W));
					float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
					float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
					float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
					float ms = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
					if (sphere_in_frustum(preFr, XMVectorGetX(wc), XMVectorGetY(wc), XMVectorGetZ(wc), bd.w * ms + 0.05f))
						vis[k] = 1;
				}
			};
			if (m_mt_enabled && m_worker_count > 1)
			{
				u32 n = m_worker_count; size_t per = (N + n - 1) / n;
				std::vector<std::thread> th; th.reserve(n - 1);
				for (u32 t = 1; t < n; ++t) { size_t a = (size_t)t * per, b = (std::min)(a + per, N); if (a < b) th.emplace_back([&, a, b] { cullRange(a, b); }); }
				cullRange(0, (std::min)(per, N)); for (auto& t : th) t.join();
			}
			else cullRange(0, N);
			size_t w = 0;
			for (size_t k = 0; k < N; ++k) if (vis[k]) { if (w != k) m_render_queue[w] = m_render_queue[k]; ++w; }
			m_render_queue.resize(w);
			objectCount = (std::min)(m_render_queue.size(), static_cast<size_t>(MAX_RENDER_OBJECTS));
			if (objectCount == 0) return;
		}

		// Rebuild the sorted (material,mesh) layout + run table ONLY when the submit queue changed (or the pre-cull
		// mutated it). A static map with a moving camera reuses the cached layout — the per-frame cull+pack still
		// runs (frustum-dependent), but the O(n log n) sort + O(n) run-build are skipped.
		bool needRebuild = m_queue_dirty || preCullRan || m_draw_runs.empty();

		// Sort by (material, mesh, distance): identical (mesh+material) objects become ADJACENT so each run
		// draws as ONE instanced call (fewer draw calls); distance is the tiebreaker for partial early-Z within a run.
		if (needRebuild)
		{
			const DirectX::XMFLOAT3 eye = m_camera_position;
			std::sort(m_render_queue.begin(), m_render_queue.end(),
				[&eye](const RenderItem& a, const RenderItem& b)
				{
					if (a.material_id != b.material_id) return a.material_id < b.material_id;
					if (a.mesh_id != b.mesh_id) return a.mesh_id < b.mesh_id;
					float ax = a.world_matrix._41 - eye.x, ay = a.world_matrix._42 - eye.y, az = a.world_matrix._43 - eye.z;
					float bx = b.world_matrix._41 - eye.x, by = b.world_matrix._42 - eye.y, bz = b.world_matrix._43 - eye.z;
					return (ax * ax + ay * ay + az * az) < (bx * bx + by * by + bz * bz);
				});
		}

		// Frustum culling: build the 6 view planes once; objects fully outside are skipped (no draw, no
		// vertex work). The biggest 2026-standard CPU win — don't render what the camera can't see.
		FrustumPlanes frustum = extract_frustum(m_frame_constants.view_projection);

		// Distance cull: skip instances whose world center is beyond the render-distance setting (0 = off).
		const DirectX::XMFLOAT3 eye = m_camera_position;
		const bool useDist = m_render_distance > 0.0f;
		const float rd2 = m_render_distance * m_render_distance;
		const bool useLod = m_lod_enabled && m_lod_mid > 0.0f;
		const float lodMid2 = m_lod_mid * m_lod_mid;
		const float lodFar2 = m_lod_far * m_lod_far;

		// Build (mesh,material) runs over the sorted queue, precomputing each run's mesh bounds + a worst-case
		// instance-VB slab (vbBase = item count). Then CULL+PACK visible instances in PARALLEL across the flat
		// item list (atomic append into each run's slab — lock-free, and order-independent because instancing
		// ignores instance order), and finally record the draws single-threaded. This parallelizes the
		// per-instance frustum/distance test + memcpy — the CPU cost when a mesh is rendered thousands of times.
		using namespace DirectX;
		if (needRebuild)
		{
		m_draw_runs.clear();
		m_item_run.resize(objectCount);
		{
			u32 vbBase = 0; size_t i = 0;
			while (i < objectCount)
			{
				const auto idMesh = m_render_queue[i].mesh_id;
				const auto idMat = m_render_queue[i].material_id;
				size_t j = i;
				while (j < objectCount && m_render_queue[j].mesh_id == idMesh && m_render_queue[j].material_id == idMat) ++j;
				u32 cnt = (u32)(j - i);
				Mesh* meshp = reg.get_mesh(idMesh);
				float minx = 0, miny = 0, minz = 0, maxx = 1, maxy = 1, maxz = 1;
				if (meshp && meshp->is_valid()) { meshp->get_min(minx, miny, minz); meshp->get_max(maxx, maxy, maxz); }
				DrawRun run{};
				run.start = i; run.count = cnt; run.mesh = idMesh; run.mat = idMat; run.meshp = meshp;
				run.defaultBounds = (minx == 0.f && miny == 0.f && minz == 0.f && maxx == 1.f && maxy == 1.f && maxz == 1.f);
				run.lcx = (minx + maxx) * 0.5f; run.lcy = (miny + maxy) * 0.5f; run.lcz = (minz + maxz) * 0.5f;
				float bdx = maxx - minx, bdy = maxy - miny, bdz = maxz - minz;
				run.localR = 0.5f * sqrtf(bdx * bdx + bdy * bdy + bdz * bdz);
				run.vbBase = vbBase; run.visible = 0;
				if (m_geo_lod_enabled)
				{
					const auto* chain = reg.get_lod_chain(idMesh);
					if (chain && chain->lod_count > 1)
					{
						run.lodLevels = chain->lod_count;
						for (u32 L = 0; L < chain->lod_count && L < 4; ++L) run.lodMesh[L] = chain->lods[L];
						run.lodT1sq = m_lod_mid * m_lod_mid;
						run.lodT2sq = m_lod_far * m_lod_far;
						float t3 = m_lod_far * 1.8f; run.lodT3sq = t3 * t3;
					}
				}
				u32 ri = (u32)m_draw_runs.size();
				m_draw_runs.push_back(run);
				for (size_t k = i; k < j; ++k) m_item_run[k] = ri;
				vbBase += cnt;
				i = j;
			}
		}
		m_queue_dirty = false;
		}
		const size_t runN = m_draw_runs.size();
		m_instances_tested += (int)objectCount;

		if (m_worker_count == 0)
		{
			unsigned hc = std::thread::hardware_concurrency(); if (hc == 0) hc = 4;
			m_worker_count = (hc > 9) ? 8u : (hc - 1u); if (m_worker_count < 1) m_worker_count = 1;
		}

		// Per-run atomic pack counters (visible instances appended into each run's reserved slab).
		std::unique_ptr<std::atomic<u32>[]> counters(new std::atomic<u32>[runN ? runN : 1]);
		for (size_t r = 0; r < runN; ++r) counters[r].store(0, std::memory_order_relaxed);

		auto cullPack = [&](size_t a, size_t b)
		{
			for (size_t k = a; k < b; ++k)
			{
				const u32 ri = m_item_run[k];
				const DrawRun& run = m_draw_runs[ri];
				const auto& item = m_render_queue[k];
				bool visible = true;
					int instLod = 0;   // geometric-LOD level for this instance (0 = full)
				if (!run.defaultBounds)
				{
					const XMFLOAT4X4& W = item.world_matrix;
					XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(run.lcx, run.lcy, run.lcz, 1.f), XMLoadFloat4x4(&W));
					float cx = XMVectorGetX(wc), cy = XMVectorGetY(wc), cz = XMVectorGetZ(wc);
					float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
					float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
					float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
					float maxScale = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
					visible = sphere_in_frustum(frustum, cx, cy, cz, run.localR * maxScale + 0.05f);
					if (visible && (useDist || useLod))
					{
						float ddx = cx - eye.x, ddy = cy - eye.y, ddz = cz - eye.z;
						float d2 = ddx * ddx + ddy * ddy + ddz * ddz;
						if (useDist && d2 > rd2) visible = false;
						else if (useLod && !m_geo_lod_enabled)
						{
							// Density LOD: keep every instance up close, 1/2 beyond mid, 1/4 beyond far. Selection
							// by the stable item index k -> deterministic, no per-frame flicker.
							if (d2 > lodFar2) { if ((k & 3) != 0) visible = false; }
							else if (d2 > lodMid2) { if ((k & 1) != 0) visible = false; }
						}
					}
				}
				if (visible && m_instance_vb_mapped)
				{
					u32 slot = counters[ri].fetch_add(1, std::memory_order_relaxed);
					if (run.vbBase + slot < MAX_RENDER_OBJECTS)
						memcpy((u8*)m_instance_vb_mapped + (size_t)(run.vbBase + slot) * 64, &item.world_matrix, 64);
					// Geometric LOD: bucket this instance by distance. Single-threaded when geo-LOD is on (see
					// m_mt_active), so the slab is distance-ordered and lodCount segments are contiguous.
					if (run.lodLevels > 1)
					{
						const XMFLOAT4X4& Wl = item.world_matrix;
						float lx = Wl._41 - eye.x, ly = Wl._42 - eye.y, lz = Wl._43 - eye.z;
						float ld2 = lx * lx + ly * ly + lz * lz;
						instLod = (ld2 > run.lodT3sq) ? 3 : (ld2 > run.lodT2sq) ? 2 : (ld2 > run.lodT1sq) ? 1 : 0;
						if (instLod >= (int)run.lodLevels) instLod = (int)run.lodLevels - 1;
						m_draw_runs[ri].lodCount[instLod]++;
					}
				}
			}
		};

		// Parallelize only when there's enough per-instance work to amortize the threads (force ignores it).
		// Geometric LOD needs the per-run slab in distance order (so LOD segments are contiguous) — that requires
		// the single-threaded ordered pack. MT gives no FPS here anyway (GPU-bound), so just disable it for LOD.
		m_mt_active = m_mt_enabled && runN > 0 && m_worker_count > 1 && (m_mt_force || objectCount >= (size_t)m_mt_threshold);
		if (m_geo_lod_enabled)
		{
			// 2-PASS MULTITHREADED cull for geometric LOD: parallel count -> compact prefix-sum offsets ->
			// parallel pack. Uses ALL worker threads (the single-threaded ordered pack was the FPS bottleneck at
			// high instance counts) and packs ONLY visible instances compactly (no reserved gaps for culled
			// copies), so the instance VB holds far more on screen.
			if (m_item_lod.size() < objectCount) m_item_lod.resize(objectCount);
			const size_t c4n = (runN ? runN : 1) * 4;
			std::unique_ptr<std::atomic<u32>[]> c4(new std::atomic<u32>[c4n]);
			for (size_t z = 0; z < c4n; ++z) c4[z].store(0, std::memory_order_relaxed);

			auto passA = [&](size_t a, size_t b)
			{
				for (size_t k = a; k < b; ++k)
				{
					const u32 ri = m_item_run[k];
					const DrawRun& run = m_draw_runs[ri];
					const auto& item = m_render_queue[k];
					bool visible = true; int lod = 0;
					if (!run.defaultBounds)
					{
						const XMFLOAT4X4& W = item.world_matrix;
						XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(run.lcx, run.lcy, run.lcz, 1.f), XMLoadFloat4x4(&W));
						float cx = XMVectorGetX(wc), cy = XMVectorGetY(wc), cz = XMVectorGetZ(wc);
						float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
						float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
						float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
						float maxScale = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
						visible = sphere_in_frustum(frustum, cx, cy, cz, run.localR * maxScale + 0.05f);
						if (visible)
						{
							float dx = cx - eye.x, dy = cy - eye.y, dz = cz - eye.z;
							float d2 = dx * dx + dy * dy + dz * dz;
							if (useDist && d2 > rd2) visible = false;
							else if (run.lodLevels > 1)
							{
								lod = (d2 > run.lodT3sq) ? 3 : (d2 > run.lodT2sq) ? 2 : (d2 > run.lodT1sq) ? 1 : 0;
								if (lod >= (int)run.lodLevels) lod = (int)run.lodLevels - 1;
							}
						}
					}
					if (visible) { m_item_lod[k] = (unsigned char)lod; c4[ri * 4 + lod].fetch_add(1, std::memory_order_relaxed); }
					else m_item_lod[k] = 0xFF;
				}
			};
			if (m_mt_active)
			{
				u32 n = m_worker_count; size_t per = (objectCount + n - 1) / n;
				std::vector<std::thread> th; th.reserve(n - 1);
				for (u32 t = 1; t < n; ++t) { size_t a = (size_t)t * per, b = (std::min)(a + per, objectCount); if (a < b) th.emplace_back([&, a, b] { passA(a, b); }); }
				passA(0, (std::min)(per, objectCount)); for (auto& t : th) t.join();
			}
			else passA(0, objectCount);

			// Compact prefix sums: each run's base = the running VISIBLE total (not the submitted count); c4[run,LOD]
			// becomes the absolute pack pointer. Clamp to the VB cap so a huge view can't overrun the buffer.
			u32 globalBase = 0;
			for (size_t r = 0; r < runN; ++r)
			{
				m_draw_runs[r].vbBase = globalBase;
				u32 localBase = 0;
				for (int L = 0; L < 4; ++L)
				{
					u32 cnt = c4[r * 4 + L].load(std::memory_order_relaxed);
					u32 segStart = globalBase + localBase;
					if (segStart >= MAX_RENDER_OBJECTS) cnt = 0;
					else if (segStart + cnt > MAX_RENDER_OBJECTS) cnt = MAX_RENDER_OBJECTS - segStart;
					m_draw_runs[r].lodCount[L] = cnt;
					c4[r * 4 + L].store(segStart, std::memory_order_relaxed);
					localBase += cnt;
				}
				m_draw_runs[r].visible = localBase;
				globalBase += localBase;
			}

			auto passB = [&](size_t a, size_t b)
			{
				for (size_t k = a; k < b; ++k)
				{
					unsigned char lod = m_item_lod[k];
					if (lod == 0xFF) continue;
					const u32 ri = m_item_run[k];
					u32 dst = c4[ri * 4 + lod].fetch_add(1, std::memory_order_relaxed);
					if (dst < MAX_RENDER_OBJECTS && m_instance_vb_mapped)
						memcpy((u8*)m_instance_vb_mapped + (size_t)dst * 64, &m_render_queue[k].world_matrix, 64);
				}
			};
			if (m_mt_active)
			{
				u32 n = m_worker_count; size_t per = (objectCount + n - 1) / n;
				std::vector<std::thread> th; th.reserve(n - 1);
				for (u32 t = 1; t < n; ++t) { size_t a = (size_t)t * per, b = (std::min)(a + per, objectCount); if (a < b) th.emplace_back([&, a, b] { passB(a, b); }); }
				passB(0, (std::min)(per, objectCount)); for (auto& t : th) t.join();
			}
			else passB(0, objectCount);
		}
		else if (m_mt_active)
		{
			u32 n = m_worker_count;
			size_t per = (objectCount + n - 1) / n;
			std::vector<std::thread> threads; threads.reserve(n - 1);
			for (u32 t = 1; t < n; ++t)
			{
				size_t a = (size_t)t * per, b = (std::min)(a + per, objectCount);
				if (a < b) threads.emplace_back([&, a, b] { cullPack(a, b); });
			}
			cullPack(0, (std::min)(per, objectCount));
			for (auto& th : threads) th.join();
		}
		else
		{
			cullPack(0, objectCount);
		}

		if (!m_geo_lod_enabled)
			for (size_t r = 0; r < runN; ++r) m_draw_runs[r].visible = counters[r].load(std::memory_order_relaxed);

		// Record the draws single-threaded: one DrawIndexedInstanced per run with visible instances.
		u32 cbSlot = 0;
		for (size_t r = 0; r < runN; ++r)
		{
			const DrawRun& run = m_draw_runs[r];
			m_instances_drawn += run.visible;
			Mesh* mesh = run.meshp;
			if (!mesh || !mesh->is_valid() || run.visible == 0) continue;

			// Material + PSO + textures: set ONCE for the whole run (shared by all its instances).
			PerObjectConstants obj{};
			obj.base_color = { 0.85f, 0.85f, 0.88f, 1.0f };
			obj.metallic = 0.7f; obj.roughness = 0.35f; obj.ao = 1.0f; obj.normal_strength = 1.0f;
			obj.use_directx_normals = 1;
			auto* mat = reg.get_material(run.mat);
			if (mat && mat->properties().is_unlit) m_command_list->SetPipelineState(double_sided_pso);
			else m_command_list->SetPipelineState(pso);
			if (mat)
			{
				const auto& props = mat->properties();
				obj.base_color = props.base_color; obj.metallic = props.metallic; obj.roughness = props.roughness;
				obj.ao = props.ao; obj.normal_strength = props.normal_strength; obj.use_directx_normals = props.use_directx_normals;
				auto* tex = mat->albedo_texture();
				if (tex && tex->is_valid() && tex->srv_gpu().ptr != 0) { obj.has_albedo_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(3, tex->srv_gpu()); }
				auto* normal = mat->normal_texture();
				if (normal && normal->is_valid() && normal->srv_gpu().ptr != 0) { obj.has_normal_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(4, normal->srv_gpu()); }
				auto* metallic_tex = mat->metallic_texture();
				if (metallic_tex && metallic_tex->is_valid() && metallic_tex->srv_gpu().ptr != 0) { obj.has_metallic_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(5, metallic_tex->srv_gpu()); }
				auto* roughness_tex = mat->roughness_texture();
				if (roughness_tex && roughness_tex->is_valid() && roughness_tex->srv_gpu().ptr != 0) { obj.has_roughness_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(6, roughness_tex->srv_gpu()); }
				auto* ao_tex = mat->ao_texture();
				if (ao_tex && ao_tex->is_valid() && ao_tex->srv_gpu().ptr != 0) { obj.has_ao_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(7, ao_tex->srv_gpu()); }
			}
			// One CB slot per run; clamp to the CB capacity (runs beyond reuse the last slot — only matters
			// with thousands of DISTINCT materials, which instancing makes rare).
			u32 slot = (cbSlot < MAX_DRAW_RUNS) ? cbSlot : (MAX_DRAW_RUNS - 1);
			if (m_per_object_cb_mapped) memcpy((u8*)m_per_object_cb_mapped + (size_t)slot * 256, &obj, sizeof(obj));
			m_command_list->SetGraphicsRootConstantBufferView(1, m_per_object_cb->GetGPUVirtualAddress() + (size_t)slot * 256);
			if (cbSlot < MAX_DRAW_RUNS) ++cbSlot;

			// Bind mesh vertices (slot 0) + this run's per-instance world matrices (slot 1); draw all at once.
			if (run.lodLevels > 1)
			{
				// Geometric LOD: the slab is distance-ordered, so each LOD's instances are a contiguous segment.
				// Draw each LOD's segment with its own (decimated) mesh — whole crowd visible, far ones low-poly.
				u32 segStart = 0;
				for (u32 L = 0; L < run.lodLevels; ++L)
				{
					u32 c = run.lodCount[L];
					if (c == 0) continue;
					Mesh* lm = (L == 0) ? mesh : reg.get_mesh(run.lodMesh[L]);
					if (!lm || !lm->is_valid()) lm = mesh;
					D3D12_VERTEX_BUFFER_VIEW lv[2];
					lv[0] = lm->vertex_buffer_view();
					lv[1].BufferLocation = m_instance_vb->GetGPUVirtualAddress() + (UINT64)(run.vbBase + segStart) * 64;
					lv[1].SizeInBytes = c * 64;
					lv[1].StrideInBytes = 64;
					m_command_list->IASetVertexBuffers(0, 2, lv);
					if (lm->has_indices())
					{
						m_command_list->IASetIndexBuffer(&lm->index_buffer_view());
						m_command_list->DrawIndexedInstanced(lm->index_count(), c, 0, 0, 0);
						m_vertex_count += lm->index_count() * c;
					}
					else
					{
						m_command_list->DrawInstanced(lm->vertex_count(), c, 0, 0);
						m_vertex_count += lm->vertex_count() * c;
					}
					++m_draw_call_count;
					segStart += c;
				}
			}
			else
			{
			// Bind mesh vertices (slot 0) + this run's per-instance world matrices (slot 1); draw all at once.
			D3D12_VERTEX_BUFFER_VIEW vbs[2];
			vbs[0] = mesh->vertex_buffer_view();
			vbs[1].BufferLocation = m_instance_vb->GetGPUVirtualAddress() + (UINT64)run.vbBase * 64;
			vbs[1].SizeInBytes = run.visible * 64;
			vbs[1].StrideInBytes = 64;
			m_command_list->IASetVertexBuffers(0, 2, vbs);
			if (mesh->has_indices())
			{
				m_command_list->IASetIndexBuffer(&mesh->index_buffer_view());
				m_command_list->DrawIndexedInstanced(mesh->index_count(), run.visible, 0, 0, 0);
				m_vertex_count += mesh->index_count() * run.visible;
			}
			else
			{
				m_command_list->DrawInstanced(mesh->vertex_count(), run.visible, 0, 0);
				m_vertex_count += mesh->vertex_count() * run.visible;
			}
			++m_draw_call_count;
			}
		}
		}

		void DX12Renderer::render_fallback_triangle()
	{
		if (m_grid_visible) return;
		auto rtv = m_active_rtv;
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
		m_command_list->SetPipelineState(m_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_pipeline.root_signature());
		m_command_list->IASetVertexBuffers(0, 1, &m_geometry.vertex_buffer_view());
		m_command_list->DrawInstanced(m_geometry.vertex_count(), 1, 0, 0);
	}

	void DX12Renderer::render_grid()
	{
		if (!m_grid_pipeline.pipeline_state()) return;

		auto rtv = m_active_rtv;
		auto dsv = m_active_dsv;
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		
		m_command_list->SetPipelineState(m_grid_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_grid_pipeline.root_signature());

		using namespace DirectX;
		XMVECTOR eye = XMLoadFloat3(&m_camera_position);
		XMVECTOR at = XMLoadFloat3(&m_camera_target);
		XMVECTOR up = XMLoadFloat3(&m_camera_up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		float aspect = (float)m_active_width / (float)m_active_height;
		XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, 0.1f, 1000.0f); // MUST match the scene FOV (update_per_frame_constants) or grid/sky misalign vs objects
		XMMATRIX vp = view * proj;

		GridConstants gc{};
		XMStoreFloat4x4(&gc.view_projection, vp);
		XMStoreFloat4x4(&gc.inverse_view_projection, XMMatrixInverse(nullptr, vp));
		gc.camera_position = m_camera_position;
		gc.grid_spacing = m_grid_spacing;
		gc.grid_extent = m_grid_extent;
		gc.major_line_interval = m_grid_major_interval;

		if (m_grid_cb_mapped)
			memcpy(m_grid_cb_mapped, &gc, sizeof(gc));

		m_command_list->SetGraphicsRootConstantBufferView(0, m_grid_cb->GetGPUVirtualAddress());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->DrawInstanced(3, 1, 0, 0);
	}

	void DX12Renderer::set_grid_settings(float s, float m, float e)
	{
		m_grid_spacing = s; m_grid_major_interval = m; m_grid_extent = e;
	}

	void DX12Renderer::submit_render_item(const RenderItem& item)
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_queue.push_back(item);
	}

	void DX12Renderer::submit_mesh_instances(id::id_type mesh, id::id_type material, const float* world_matrices, u32 count)
	{
		if (!world_matrices || count == 0) return;
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_queue.reserve(m_submit_queue.size() + count);
		for (u32 i = 0; i < count; ++i)
		{
			RenderItem item;
			item.mesh_id = mesh;
			item.material_id = material;
			memcpy(&item.world_matrix, world_matrices + (size_t)i * 16, sizeof(DirectX::XMFLOAT4X4));
			m_submit_queue.push_back(item);
		}
	}
	
	void DX12Renderer::clear_render_queue() 
	{ 
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_queue.clear(); 
	}

	void DX12Renderer::set_camera(const DirectX::XMFLOAT3& pos, const DirectX::XMFLOAT3& target, const DirectX::XMFLOAT3& up)
	{
		m_camera_position = pos; m_camera_target = target; m_camera_up = up;
	}

	void DX12Renderer::set_projection(float fov_degrees, float aspect, float near_clip, float far_clip)
	{
		m_fov_degrees = fov_degrees;
		m_aspect_ratio = aspect;
		m_near_clip = near_clip;
		m_far_clip = far_clip;
	}

	void DX12Renderer::render_camera_gizmo(
		const DirectX::XMFLOAT3& position,
		const DirectX::XMFLOAT3& forward,
		const DirectX::XMFLOAT3& right,
		const DirectX::XMFLOAT3& up,
		float near_width, float near_height,
		float far_width, float far_height,
		float near_dist, float far_dist,
		const DirectX::XMFLOAT4& color)
	{
		// This renders a wireframe camera frustum gizmo
		// For now, we'll add the gizmo as lines to a line buffer that gets rendered
		// This is a placeholder - full implementation would use a line rendering system
		
		using namespace DirectX;
		
		// Calculate frustum corners in world space
		XMVECTOR pos = XMLoadFloat3(&position);
		XMVECTOR fwd = XMLoadFloat3(&forward);
		XMVECTOR rgt = XMLoadFloat3(&right);
		XMVECTOR upv = XMLoadFloat3(&up);
		
		// Near plane corners
		XMVECTOR near_center = pos + fwd * near_dist;
		XMVECTOR near_tl = near_center - rgt * near_width + upv * near_height;
		XMVECTOR near_tr = near_center + rgt * near_width + upv * near_height;
		XMVECTOR near_bl = near_center - rgt * near_width - upv * near_height;
		XMVECTOR near_br = near_center + rgt * near_width - upv * near_height;
		
		// Far plane corners
		XMVECTOR far_center = pos + fwd * far_dist;
		XMVECTOR far_tl = far_center - rgt * far_width + upv * far_height;
		XMVECTOR far_tr = far_center + rgt * far_width + upv * far_height;
		XMVECTOR far_bl = far_center - rgt * far_width - upv * far_height;
		XMVECTOR far_br = far_center + rgt * far_width - upv * far_height;
		
		// TODO: Add these lines to a line buffer for rendering
		// For now, this is a stub that would be connected to a line rendering system
		// The lines would be:
		// - Near plane rectangle (4 lines)
		// - Far plane rectangle (4 lines)
		// - Connecting lines from camera to far corners (4 lines)
		// - Camera body (small box at position)
	}

	void DX12Renderer::set_directional_light(const DirectX::XMFLOAT3& dir, const DirectX::XMFLOAT3& col)
	{
		m_light_direction = dir; m_light_color = col;
	}

	void DX12Renderer::set_ambient_strength(float s) { m_ambient_strength = s; }

	bool DX12Renderer::create_command_allocators()
	{
		auto dev = DX12Core::instance().device();
		for (u32 i = 0; i < DX12Swapchain::MaxBufferCount; ++i)
			if (FAILED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&m_command_allocators[i]))))
				return false;
		return true;
	}

	bool DX12Renderer::create_command_list()
	{
		auto dev = DX12Core::instance().device();
		return SUCCEEDED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
			m_command_allocators[0].Get(), nullptr, IID_PPV_ARGS(&m_command_list))) && SUCCEEDED(m_command_list->Close());
	}

	bool DX12Renderer::create_constant_buffers()
	{
	auto dev = DX12Core::instance().device();
	D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_UPLOAD;
	D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
	rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1; rd.SampleDesc.Count = 1;
	rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

	rd.Width = 256;
	if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_per_frame_cb))))
	return false;
	D3D12_RANGE r{0,0};
	if (FAILED(m_per_frame_cb->Map(0, &r, &m_per_frame_cb_mapped))) return false;

	// Per-object constant buffer: ONE 256-byte slot per DRAW RUN (mesh+material), not per instance.
	rd.Width = (UINT64)256 * MAX_DRAW_RUNS;
	if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_per_object_cb))))
	return false;
	if (FAILED(m_per_object_cb->Map(0, &r, &m_per_object_cb_mapped))) return false;

	// Per-instance world matrices for GPU instancing: 64 bytes (4x float4 rows) per instance.
	rd.Width = (UINT64)64 * MAX_RENDER_OBJECTS;
	if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_instance_vb))))
	return false;
	if (FAILED(m_instance_vb->Map(0, &r, &m_instance_vb_mapped))) return false;

	// Light buffer: Point lights (16 * 32 bytes) + Spot lights (8 * 64 bytes) = 1024 bytes, aligned to 256
	rd.Width = 1280; // 256-byte aligned
	if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_light_cb))))
	return false;
	if (FAILED(m_light_cb->Map(0, &r, &m_light_cb_mapped))) return false;

	return true;
	}

	bool DX12Renderer::create_grid_resources()
	{
		auto dev = DX12Core::instance().device();

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_UPLOAD;
		D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		rd.Width = 256; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.SampleDesc.Count = 1; rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_grid_cb))))
			return false;
		
		D3D12_RANGE r{0,0};
		if (FAILED(m_grid_cb->Map(0, &r, &m_grid_cb_mapped))) return false;

		return true;
	}

	bool DX12Renderer::create_skybox_resources()
	{
		auto dev = DX12Core::instance().device();

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_UPLOAD;
		D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		rd.Width = 256; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.SampleDesc.Count = 1; rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_skybox_cb))))
			return false;
		
		D3D12_RANGE r{0,0};
		if (FAILED(m_skybox_cb->Map(0, &r, &m_skybox_cb_mapped))) return false;

		return true;
	}

	void DX12Renderer::render_skybox()
	{
		if (!m_skybox_pipeline.pipeline_state()) return;

		auto rtv = m_active_rtv;
		auto dsv = m_active_dsv;
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		
		m_command_list->SetPipelineState(m_skybox_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_skybox_pipeline.root_signature());

		using namespace DirectX;
		XMVECTOR eye = XMLoadFloat3(&m_camera_position);
		XMVECTOR at = XMLoadFloat3(&m_camera_target);
		XMVECTOR up = XMLoadFloat3(&m_camera_up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		float aspect = (float)m_active_width / (float)m_active_height;
		XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, 0.1f, 1000.0f); // MUST match the scene FOV (update_per_frame_constants) or grid/sky misalign vs objects
		XMMATRIX vp = view * proj;

		// Update skybox constants with inverse VP matrix
		auto constants_ptr = reinterpret_cast<u8*>(m_skybox_cb_mapped);
		if (constants_ptr)
		{
			// Copy pipeline constants first
			memcpy(constants_ptr, m_skybox_pipeline.get_constants(), m_skybox_pipeline.get_constants_size());
			
			// Update inverse view projection and camera position
			XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp);
			XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(constants_ptr), inv_vp);
			
			// Camera position is at offset 64 (after the 4x4 matrix)
			*reinterpret_cast<XMFLOAT3*>(constants_ptr + 64) = m_camera_position;
		}

		m_command_list->SetGraphicsRootConstantBufferView(0, m_skybox_cb->GetGPUVirtualAddress());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->DrawInstanced(3, 1, 0, 0);
	}

	void DX12Renderer::set_skybox_colors(
		const DirectX::XMFLOAT3& sky_color,
		const DirectX::XMFLOAT3& horizon_color,
		const DirectX::XMFLOAT3& ground_color)
	{
		m_skybox_pipeline.set_colors(sky_color, horizon_color, ground_color);
	}

	void DX12Renderer::set_skybox_solid_color(const DirectX::XMFLOAT3& color)
	{
		// For solid color, set all three colors to the same value
		m_skybox_pipeline.set_colors(color, color, color);
	}

	void DX12Renderer::set_skybox_mode(SkyboxMode mode)
	{
		m_skybox_mode = mode;
	}

	void DX12Renderer::set_skybox_sun(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity)
	{
		m_skybox_pipeline.set_sun(direction, color, intensity);
	}

	bool DX12Renderer::create_srv_heap()
	{
		auto dev = DX12Core::instance().device();
		if (!dev) return false;

		D3D12_DESCRIPTOR_HEAP_DESC heap_desc{};
		heap_desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
		heap_desc.NumDescriptors = MAX_SRV_DESCRIPTORS;
		heap_desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

		if (FAILED(dev->CreateDescriptorHeap(&heap_desc, IID_PPV_ARGS(&m_srv_heap))))
		{
			OutputDebugStringA("Failed to create SRV descriptor heap\n");
			return false;
		}

		m_srv_descriptor_size = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
		
		OutputDebugStringA("SRV descriptor heap created successfully\n");
		return true;
	}

	void DX12Renderer::update_per_frame_constants()
	{
	using namespace DirectX;
	XMVECTOR eye = XMLoadFloat3(&m_camera_position);
	XMVECTOR at = XMLoadFloat3(&m_camera_target);
	XMVECTOR up = XMLoadFloat3(&m_camera_up);

	XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
	float aspect = (float)m_swapchain.width() / (float)m_swapchain.height();
	XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, 0.1f, 1000.0f); // settable FOV (game camera)
	XMMATRIX vp = view * proj;

	XMStoreFloat4x4(&m_frame_constants.view_projection, vp);
	m_frame_constants.camera_position = m_camera_position;
	m_frame_constants.light_direction = m_light_direction;
	m_frame_constants.directional_intensity = m_directional_intensity;
	m_frame_constants.light_color = m_light_color;
	m_frame_constants.ambient_strength = m_ambient_strength;
	m_frame_constants.point_light_count = static_cast<u32>(m_point_lights.size());
	m_frame_constants.spot_light_count = static_cast<u32>(m_spot_lights.size());

	if (m_per_frame_cb_mapped)
	memcpy(m_per_frame_cb_mapped, &m_frame_constants, sizeof(m_frame_constants));
			
	// Update light buffer
	if (m_light_cb_mapped)
	{
	u8* ptr = static_cast<u8*>(m_light_cb_mapped);
			
	// Copy point lights
	size_t point_light_size = m_point_lights.size() * sizeof(GPUPointLight);
	if (point_light_size > 0)
	{
	for (size_t i = 0; i < m_point_lights.size() && i < MAX_POINT_LIGHTS; ++i)
	{
		GPUPointLight gpu_light{};
		gpu_light.position = m_point_lights[i].position;
		gpu_light.range = m_point_lights[i].range;
		gpu_light.color = m_point_lights[i].color;
		gpu_light.intensity = m_point_lights[i].intensity;
		memcpy(ptr + i * sizeof(GPUPointLight), &gpu_light, sizeof(GPUPointLight));
	}
	}
			
	// Copy spot lights (after point lights)
	u8* spot_ptr = ptr + MAX_POINT_LIGHTS * sizeof(GPUPointLight);
	for (size_t i = 0; i < m_spot_lights.size() && i < MAX_SPOT_LIGHTS; ++i)
	{
	GPUSpotLight gpu_light{};
	gpu_light.position = m_spot_lights[i].position;
	gpu_light.range = m_spot_lights[i].range;
		
	// Normalize direction
	DirectX::XMFLOAT3 dir = m_spot_lights[i].direction;
	float len = sqrtf(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
	if (len > 0.0001f) {
	dir.x /= len;
	dir.y /= len;
	dir.z /= len;
	}
	gpu_light.direction = dir;
		
	gpu_light.spot_angle = m_spot_lights[i].spot_angle;
	gpu_light.color = m_spot_lights[i].color;
	gpu_light.intensity = m_spot_lights[i].intensity;
	gpu_light.inner_spot_angle = m_spot_lights[i].inner_spot_angle;
	memcpy(spot_ptr + i * sizeof(GPUSpotLight), &gpu_light, sizeof(GPUSpotLight));
	}
	}
	}

	void DX12Renderer::wait_for_previous_frame() { m_command_queue.signal_and_wait(); }
	
	void DX12Renderer::clear_lights()
	{
	m_point_lights.clear();
	m_spot_lights.clear();
	}
	
	void DX12Renderer::set_directional_light_full(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity)
	{
	m_light_direction = direction;
	m_light_color = color;
	m_directional_intensity = intensity;
	}
	
	void DX12Renderer::add_point_light(const PointLightData& light)
	{
	if (m_point_lights.size() < MAX_POINT_LIGHTS)
	{
	m_point_lights.push_back(light);
	}
	}
	
	void DX12Renderer::add_spot_light(const SpotLightData& light)
	{
	if (m_spot_lights.size() < MAX_SPOT_LIGHTS)
	{
	m_spot_lights.push_back(light);
	}
	}
	
	// ============== Multi-Viewport Rendering ==============
	
	u32 DX12Renderer::create_render_target(u32 width, u32 height)
	{
		if (!m_initialized || width == 0 || height == 0) return 0;
		if (m_render_targets.size() >= MAX_RENDER_TARGETS) return 0;
		
		auto target = std::make_unique<DX12RenderTarget>();
		if (!target->initialize(DX12Core::instance().device(), width, height))
		{
			return 0;
		}
		
		u32 id = m_next_render_target_id++;
		m_render_targets[id] = std::move(target);
		
		OutputDebugStringA(("Created render target ID " + std::to_string(id) + 
			" (" + std::to_string(width) + "x" + std::to_string(height) + ")\n").c_str());
		
		return id;
	}
	
	void DX12Renderer::destroy_render_target(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it != m_render_targets.end())
		{
			m_command_queue.flush(); // Ensure GPU is done with this target
			m_render_targets.erase(it);
			OutputDebugStringA(("Destroyed render target ID " + std::to_string(target_id) + "\n").c_str());
		}
	}
	
	bool DX12Renderer::resize_render_target(u32 target_id, u32 width, u32 height)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return false;
		
		m_command_queue.flush(); // Ensure GPU is done
		return it->second->resize(DX12Core::instance().device(), width, height);
	}
	
	bool DX12Renderer::has_render_target(u32 target_id) const
	{
		return m_render_targets.find(target_id) != m_render_targets.end();
	}
	
	void DX12Renderer::render_to_target(u32 target_id, const ViewportCamera& camera, bool render_grid)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return;
		
		render_scene_to_target(it->second.get(), camera, render_grid);
	}
	
	void DX12Renderer::render_scene_to_target(DX12RenderTarget* target, const ViewportCamera& camera, bool render_grid)
	{
		using namespace DirectX;
		
		if (!target || !target->is_initialized()) return;
		
		// Get current back buffer index for command allocator
		u32 idx = m_swapchain.current_back_buffer_index();
		
		// Ensure previous work is complete before rendering to secondary target
		m_command_queue.flush();
		m_command_allocators[idx]->Reset();
		
		auto* pso = m_render_queue.empty() ? m_pipeline.pipeline_state() :
			(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
		m_command_list->Reset(m_command_allocators[idx].Get(), pso);
		
		// Transition render target to render target state
		target->transition_to_render_target(m_command_list.Get());
		
		// Setup viewport and scissor
		D3D12_VIEWPORT vp{};
		vp.Width = (float)target->width();
		vp.Height = (float)target->height();
		vp.MaxDepth = 1.0f;
		
		D3D12_RECT sc{};
		sc.right = target->width();
		sc.bottom = target->height();
		
		m_command_list->RSSetViewports(1, &vp);
		m_command_list->RSSetScissorRects(1, &sc);
		
		// Clear render target and depth - skybox will fill background
		FLOAT clear_color[4] = { 0.0f, 0.0f, 0.0f, 1.0f }; // Black - skybox renders over this
		auto rtv = target->rtv();
		auto dsv = target->dsv();
		m_command_list->ClearRenderTargetView(rtv, clear_color, 0, nullptr);
		m_command_list->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		
		// Build view/projection matrix for this camera
		XMVECTOR eye = XMLoadFloat3(&camera.position);
		XMVECTOR at = XMLoadFloat3(&camera.target);
		XMVECTOR up = XMLoadFloat3(&camera.up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		
		float aspect = (float)target->width() / (float)target->height();
		XMMATRIX proj;
		if (camera.orthographic)
		{
			proj = XMMatrixOrthographicLH(camera.ortho_size * aspect, camera.ortho_size, 
				camera.near_clip, camera.far_clip);
		}
		else
		{
			proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(camera.fov_degrees), aspect, 
				camera.near_clip, camera.far_clip);
		}
		
		XMMATRIX vp_matrix = view * proj;
		
		// Update per-frame constants with this camera's view/proj
		PerFrameConstants frame_constants = m_frame_constants;
		XMStoreFloat4x4(&frame_constants.view_projection, vp_matrix);
		frame_constants.camera_position = camera.position;

		// Pull in the CURRENT lighting state. Offscreen previews (AssetPreviewRenderer) set ambient + directional
		// + point lights via the API right before this call, but m_frame_constants is only refreshed by the main
		// loop — so without this the preview rendered with stale (dark) lighting and NO point lights. Copy them
		// here AND upload the point lights to the light CB so they actually illuminate the preview.
		frame_constants.light_direction = m_light_direction;
		frame_constants.directional_intensity = m_directional_intensity;
		frame_constants.light_color = m_light_color;
		frame_constants.ambient_strength = m_ambient_strength;
		frame_constants.point_light_count = static_cast<u32>((std::min)(m_point_lights.size(), static_cast<size_t>(MAX_POINT_LIGHTS)));
		frame_constants.spot_light_count = static_cast<u32>((std::min)(m_spot_lights.size(), static_cast<size_t>(MAX_SPOT_LIGHTS)));
		if (m_light_cb_mapped)
		{
			u8* lptr = static_cast<u8*>(m_light_cb_mapped);
			for (size_t i = 0; i < m_point_lights.size() && i < MAX_POINT_LIGHTS; ++i)
			{
				GPUPointLight gl{};
				gl.position = m_point_lights[i].position; gl.range = m_point_lights[i].range;
				gl.color = m_point_lights[i].color; gl.intensity = m_point_lights[i].intensity;
				memcpy(lptr + i * sizeof(GPUPointLight), &gl, sizeof(GPUPointLight));
			}
		}

		if (m_per_frame_cb_mapped)
			memcpy(m_per_frame_cb_mapped, &frame_constants, sizeof(frame_constants));
		
		// ========== RENDER SKYBOX FIRST (appears behind everything) ==========
		if (m_skybox_pipeline.pipeline_state())
		{
		m_command_list->SetPipelineState(m_skybox_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_skybox_pipeline.root_signature());
			
		// Update skybox constants with THIS camera's inverse VP matrix
		if (m_skybox_cb_mapped)
		{
		auto skybox_ptr = reinterpret_cast<u8*>(m_skybox_cb_mapped);
		memcpy(skybox_ptr, m_skybox_pipeline.get_constants(), m_skybox_pipeline.get_constants_size());
				
		XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp_matrix);
		XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(skybox_ptr), inv_vp);
		}
			
		m_command_list->SetGraphicsRootConstantBufferView(0, m_skybox_cb->GetGPUVirtualAddress());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->DrawInstanced(3, 1, 0, 0);
			
		// Reset render target state after skybox
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		}
		
		// Render grid if requested
		if (render_grid && m_grid_pipeline.pipeline_state() && m_grid_vertex_count > 0)
		{
			// Update grid constants
			if (m_grid_cb_mapped)
			{
				GridConstants grid_cb{};
				XMStoreFloat4x4(&grid_cb.view_projection, vp_matrix);
				
				XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp_matrix);
				XMStoreFloat4x4(&grid_cb.inverse_view_projection, inv_vp);
				
				grid_cb.camera_position = camera.position;
				grid_cb.grid_spacing = m_grid_spacing;
				grid_cb.grid_extent = m_grid_extent;
				grid_cb.major_line_interval = m_grid_major_interval;
				
				memcpy(m_grid_cb_mapped, &grid_cb, sizeof(grid_cb));
			}
			
			m_command_list->SetPipelineState(m_grid_pipeline.pipeline_state());
			m_command_list->SetGraphicsRootSignature(m_grid_pipeline.root_signature());
			m_command_list->SetGraphicsRootConstantBufferView(0, m_grid_cb->GetGPUVirtualAddress());
			m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_LINELIST);
			m_command_list->IASetVertexBuffers(0, 1, &m_grid_vbv);
			m_command_list->DrawInstanced(m_grid_vertex_count, 1, 0, 0);
		}
		
		// Render 3D objects
		if (!m_render_queue.empty())
		{
			m_command_list->SetPipelineState(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
			m_command_list->SetGraphicsRootSignature(m_pipeline_3d.root_signature());
			m_command_list->SetGraphicsRootConstantBufferView(0, m_per_frame_cb->GetGPUVirtualAddress());
			m_command_list->SetGraphicsRootConstantBufferView(2, m_light_cb->GetGPUVirtualAddress()); // point/spot lights
				// MUST set the SRV heap before any texture descriptor table is bound in the loop below — without
				// it, SetGraphicsRootDescriptorTable removes the device (the crash when a textured model's
				// thumbnail rendered, e.g. opening the Models folder).
				{ auto* _sh = ResourceRegistry::instance().srv_heap(); if (_sh) { ID3D12DescriptorHeap* _hh[] = { _sh }; m_command_list->SetDescriptorHeaps(1, _hh); } }
				m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
			
			auto& reg = ResourceRegistry::instance();
			size_t objectCount = (std::min)(m_render_queue.size(), static_cast<size_t>(MAX_RENDER_OBJECTS));
			
			for (size_t i = 0; i < objectCount; ++i)
			{
				const auto& item = m_render_queue[i];
				auto* mesh = reg.get_mesh(item.mesh_id);
				if (!mesh) continue;
				
				PerObjectConstants obj_cb{};
				obj_cb.world = item.world_matrix;
				obj_cb.base_color = { 0.8f, 0.8f, 0.8f, 1.0f };
				obj_cb.metallic = 0.0f; obj_cb.roughness = 0.5f; obj_cb.ao = 1.0f; obj_cb.normal_strength = 1.0f;

				// Full PBR material + textures (matches render_3d_scene) so previews show the REAL material, not a
				// flat base color.
				auto* mat = reg.get_material(item.material_id);
				if (mat)
				{
					const auto& props = mat->properties();
					obj_cb.base_color = props.base_color;
					obj_cb.metallic = props.metallic; obj_cb.roughness = props.roughness;
					obj_cb.ao = props.ao; obj_cb.normal_strength = props.normal_strength;
					obj_cb.use_directx_normals = props.use_directx_normals;
					auto* tex = mat->albedo_texture();
					if (tex && tex->is_valid() && tex->srv_gpu().ptr != 0) { obj_cb.has_albedo_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(3, tex->srv_gpu()); }
					auto* normal = mat->normal_texture();
					if (normal && normal->is_valid() && normal->srv_gpu().ptr != 0) { obj_cb.has_normal_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(4, normal->srv_gpu()); }
					auto* metallic_tex = mat->metallic_texture();
					if (metallic_tex && metallic_tex->is_valid() && metallic_tex->srv_gpu().ptr != 0) { obj_cb.has_metallic_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(5, metallic_tex->srv_gpu()); }
					auto* roughness_tex = mat->roughness_texture();
					if (roughness_tex && roughness_tex->is_valid() && roughness_tex->srv_gpu().ptr != 0) { obj_cb.has_roughness_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(6, roughness_tex->srv_gpu()); }
					auto* ao_tex = mat->ao_texture();
					if (ao_tex && ao_tex->is_valid() && ao_tex->srv_gpu().ptr != 0) { obj_cb.has_ao_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(7, ao_tex->srv_gpu()); }
				}
				
				void* dest = static_cast<u8*>(m_per_object_cb_mapped) + i * 256;
				memcpy(dest, &obj_cb, sizeof(obj_cb));
				
				D3D12_GPU_VIRTUAL_ADDRESS obj_cb_addr = m_per_object_cb->GetGPUVirtualAddress() + i * 256;
				m_command_list->SetGraphicsRootConstantBufferView(1, obj_cb_addr);
				
				// The shared 3D PSO now REQUIRES per-instance world data on slot 1 (instancing). The editor
				// preview draws one instance per object, so stage this world matrix + bind the instance VB.
				if (m_instance_vb_mapped)
					memcpy(static_cast<u8*>(m_instance_vb_mapped) + (size_t)i * 64, &item.world_matrix, 64);
				D3D12_VERTEX_BUFFER_VIEW vbs2[2];
				vbs2[0] = mesh->vertex_buffer_view();
				vbs2[1].BufferLocation = m_instance_vb->GetGPUVirtualAddress() + (UINT64)i * 64;
				vbs2[1].SizeInBytes = 64;
				vbs2[1].StrideInBytes = 64;
				m_command_list->IASetVertexBuffers(0, 2, vbs2);
				m_command_list->IASetIndexBuffer(&mesh->index_buffer_view());
				m_command_list->DrawIndexedInstanced(mesh->index_count(), 1, 0, 0, 0);
			}
		}
		
		// Execute commands
		m_command_list->Close();
		m_command_queue.execute_command_list(m_command_list.Get());
		m_command_queue.signal_and_wait();
		
		// Restore main camera constants
		if (m_per_frame_cb_mapped)
			memcpy(m_per_frame_cb_mapped, &m_frame_constants, sizeof(m_frame_constants));
	}
	
	bool DX12Renderer::prepare_render_target_readback(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return false;
		
		auto* target = it->second.get();
		
		// Ensure GPU is idle before readback
		m_command_queue.flush();
		
		u32 idx = m_swapchain.current_back_buffer_index();
		m_command_allocators[idx]->Reset();
		m_command_list->Reset(m_command_allocators[idx].Get(), nullptr);
		
		target->copy_to_staging(m_command_list.Get());
		
		m_command_list->Close();
		m_command_queue.execute_command_list(m_command_list.Get());
		m_command_queue.signal_and_wait();
		
		return true;
	}
	
	const void* DX12Renderer::read_render_target_pixels(u32 target_id, u32& out_width, u32& out_height, u32& out_row_pitch)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end())
		{
			out_width = out_height = out_row_pitch = 0;
			return nullptr;
		}
		
		auto* target = it->second.get();
		out_width = target->width();
		out_height = target->height();
		out_row_pitch = target->staging_row_pitch();
		
		return target->map_staging_buffer();
	}
	
	void DX12Renderer::release_render_target_pixels(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it != m_render_targets.end())
		{
			it->second->unmap_staging_buffer();
		}
	}
}
