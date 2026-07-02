#include "DX12Renderer_Internal.h"

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

		// Motion-vector pipeline (DLSS input). RG16F. Optional — if it fails, DLSS just can't run (bilinear upscale).
		if (m_mvec_pipeline.initialize(core.device(), DXGI_FORMAT_R16G16_FLOAT))
			OutputDebugStringA("Motion-vector pipeline OK\n");
		else
			OutputDebugStringA("Motion-vector pipeline FAILED (DLSS will fall back to bilinear)\n");

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
		if (m_bone_vb && m_bone_vb_mapped) { m_bone_vb->Unmap(0, nullptr); m_bone_vb_mapped = nullptr; }
		m_bone_vb.Reset();
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
		// Gizmos/overlays are re-submitted EVERY frame by the editor (cheap) so they always reflect the current tool
		// mode / drag / hover / selection + camera — independent of the scene's static-reuse below. Swap them
		// unconditionally: an empty submit means "no gizmo this frame" (e.g. deselect / play mode), so they clear.
		m_gizmo_render.swap(m_gizmo_submit);
		m_gizmo_submit.clear();
		m_gizmo_wire_render.swap(m_gizmo_wire_submit);
		m_gizmo_wire_submit.clear();

		if (m_submit_queue.empty()) return;   // scene: nothing new -> KEEP last frame's (camera-only orbit is free)
		m_render_queue.swap(m_submit_queue);
		m_submit_queue.clear();
		m_queue_dirty = true;   // new geometry -> re-sort + rebuild runs this frame

		// Bone palettes travel with the scene queue: adopt the staged palettes now, but do NOT touch the
		// GPU buffer yet — the previous frame's command list may still be reading it. The actual memcpy
		// happens in upload_staged_bone_palettes(), which callers run only after a fence wait / flush and
		// which writes the ALTERNATE buffer half. A reused queue (no new submissions) keeps the previous
		// upload + active half intact, so its items' bone offsets stay valid.
		m_bone_render.swap(m_bone_submit);
		m_bone_submit.clear();
		m_bone_upload_pending = true;
	}


	void DX12Renderer::upload_staged_bone_palettes()
	{
		if (!m_bone_upload_pending) return;
		m_bone_upload_pending = false;
		if (!m_bone_vb_mapped || m_bone_render.empty()) return;
		// Flip halves: the half we write was last used TWO uploads ago — the caller's fence wait/flush
		// guarantees that frame has finished, so the write can never tear an in-flight read.
		m_bone_active_half ^= 1u;
		size_t bytes = (std::min)(m_bone_render.size() * sizeof(float), (size_t)MAX_BONE_MATRICES_PER_FRAME * 64);
		memcpy((u8*)m_bone_vb_mapped + (size_t)m_bone_active_half * MAX_BONE_MATRICES_PER_FRAME * 64,
			m_bone_render.data(), bytes);
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
			m_bone_submit.clear();
			m_bone_render.clear();
			m_bone_upload_pending = false;
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
		// sampleable_depth=true: the scaled RT's depth is the DLSS depth input (R32_TYPELESS + SRV); harmless for
		// the plain bilinear upscale path (the SRV just goes unused).
		if (!m_scaled_rt.is_initialized())
			return m_scaled_rt.initialize(dev, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, true);
		return m_scaled_rt.resize(dev, width, height);
	}


	bool DX12Renderer::ensure_mvec_rt(u32 width, u32 height)
	{
		if (width < 1) width = 1; if (height < 1) height = 1;
		if (m_mvec_rt.is_initialized() && m_mvec_rt.width() == width && m_mvec_rt.height() == height) return true;
		m_command_queue.flush();
		auto* dev = DX12Core::instance().device();
		if (!dev) return false;
		if (!m_mvec_rt.is_initialized())
			return m_mvec_rt.initialize(dev, width, height, DXGI_FORMAT_R16G16_FLOAT, false, false);
		return m_mvec_rt.resize(dev, width, height);
	}


	bool DX12Renderer::ensure_dlss_output(u32 width, u32 height)
	{
		if (width < 1) width = 1; if (height < 1) height = 1;
		if (m_dlss_output.is_initialized() && m_dlss_output.width() == width && m_dlss_output.height() == height) return true;
		m_command_queue.flush();
		auto* dev = DX12Core::instance().device();
		if (!dev) return false;
		// R8G8B8A8 + UAV (DLSS writes the upscaled output via UAV); SRV used to blit it to the back buffer.
		if (!m_dlss_output.is_initialized())
			return m_dlss_output.initialize(dev, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, false, true);
		return m_dlss_output.resize(dev, width, height);
	}


	static unsigned long long shader_file_mtime(const std::wstring& p)
	{
		WIN32_FILE_ATTRIBUTE_DATA d{};
		if (!GetFileAttributesExW(p.c_str(), GetFileExInfoStandard, &d)) return 0ull;
		return ((unsigned long long)d.ftLastWriteTime.dwHighDateTime << 32) | d.ftLastWriteTime.dwLowDateTime;
	}

	// Compile a .hlsl into a PSO ONCE per (path, mtime) and cache it. Repeated calls with the same up-to-date file
	// return the cached PSO with no recompile — this is what stops the Material Editor preview from re-running fxc on
	// every orbit frame. A compile failure keeps the last-good cached PSO (never black).
	ComPtr<ID3D12PipelineState> DX12Renderer::get_or_compile_pso(const std::wstring& hlsl_path)
	{
		if (hlsl_path.empty()) return nullptr;
		unsigned long long mt = shader_file_mtime(hlsl_path);
		auto it = m_pso_cache.find(hlsl_path);
		if (it != m_pso_cache.end() && it->second.pso && it->second.mtime == mt)
			return it->second.pso;                                 // cached + up-to-date -> no recompile
		if (it != m_pso_cache.end() && it->second.pso)
			m_command_queue.flush();                               // GPU-idle before swapping an in-use PSO
		auto pso = m_pipeline_3d.create_custom_pso(DX12Core::instance().device(), hlsl_path);
		auto& e = m_pso_cache[hlsl_path];
		e.mtime = mt;
		if (pso) e.pso = pso;                                      // else keep last-good (or nullptr -> built-in)
		return e.pso;
	}

	void DX12Renderer::set_material_shader(u32 material_id, const std::wstring& hlsl_path)
	{
		if (hlsl_path.empty()) { m_custom_shaders.erase(material_id); return; }   // revert to built-in
		auto& e = m_custom_shaders[material_id];
		e.path = hlsl_path;
		e.pso = get_or_compile_pso(hlsl_path);   // shared cache: recompiles only if the file changed
		e.mtime = shader_file_mtime(hlsl_path);
	}

	int DX12Renderer::reload_dirty_shaders()
	{
		if (m_pso_cache.empty()) return 0;
		// Recompile each distinct .hlsl whose file changed (the cache is keyed by path, so shared shaders compile once).
		int changed = 0;
		for (auto& kv : m_pso_cache)
		{
			unsigned long long mt = shader_file_mtime(kv.first);
			if (mt == 0ull || mt == kv.second.mtime) continue;     // unchanged
			m_command_queue.flush();                               // GPU-idle before swapping in-use PSOs
			auto pso = m_pipeline_3d.create_custom_pso(DX12Core::instance().device(), kv.first);
			kv.second.mtime = mt;
			if (pso) { kv.second.pso = pso; ++changed; }           // keep old PSO on compile failure -> never black
		}
		if (changed == 0) return 0;
		// Re-point every live material to its refreshed cached PSO.
		for (auto& kv : m_custom_shaders)
		{
			if (kv.second.path.empty()) continue;
			auto it = m_pso_cache.find(kv.second.path);
			if (it != m_pso_cache.end()) { kv.second.pso = it->second.pso; kv.second.mtime = it->second.mtime; }
		}
		return changed;
	}

	bool DX12Renderer::any_material_shader_dirty() const
	{
		for (auto& kv : m_pso_cache)
		{
			unsigned long long mt = shader_file_mtime(kv.first);
			if (mt != 0ull && mt != kv.second.mtime) return true;
		}
		return false;
	}

	void DX12Renderer::render_frame()
	{
		if (!m_initialized) return;

		DX12Streamline::instance().frame_marker(2 /*eRenderSubmitStart*/); // FG/Reflex latency marker (no-op if FG off)

		// Swap render queues (thread-safe) and clear submit queue for next frame
		swap_render_queue();
		
		// Update FPS counter (REAL rendered frames). Also accumulate DLSS-G's presented frames (real + AI-generated)
		// here, ONCE per frame, into a smoothed rate — slDLSSGGetState returns a delta-since-last-call, so reading it
		// from a single place avoids the double-read footgun (HUD + Options would otherwise zero each other).
		m_frame_count++;
		if (DX12Streamline::instance().frame_gen_active())
			m_presented_accum += DX12Streamline::instance().fg_presented_frames();
		auto now = std::chrono::high_resolution_clock::now();
		auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - m_last_fps_time).count();
		if (elapsed >= 500) // Update every 0.5 seconds
		{
			m_current_fps = static_cast<int>((m_frame_count * 1000) / elapsed);
			m_presented_fps = static_cast<int>(((long long)m_presented_accum * 1000) / elapsed);
			m_frame_count = 0;
			m_presented_accum = 0;
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

		// Bone palettes: now that the 2-frames-back work is provably done, upload this frame's staged
		// palettes into the buffer half that frame used (the in-flight previous frame reads the other half).
		upload_staged_bone_palettes();

		update_per_frame_constants();   // aspect from swapchain dims — a UNIFORM render-scale keeps it matching

		m_command_allocators[idx]->Reset();

		auto* pso = m_render_queue.empty() ? m_pipeline.pipeline_state() :
			(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
		m_command_list->Reset(m_command_allocators[idx].Get(), pso);

		// Render-scale: when < 1.0 (and the upscale pipeline is ready) render the 3D into a smaller offscreen RT,
		// then upscale it onto the full-res back buffer (this is also the slot DLSS plugs into). Scale 1.0 takes the
		// direct path below = byte-for-byte the old behaviour, zero overhead. This is the safe default.
		// DLSS Frame Generation forces the scaled-RT path on (even at scale 1.0) so the sampleable depth + the
		// motion-vector pass exist for DLSS-G to interpolate. fg_on also gates the present-time depth/mvec tagging.
		bool fg_on = (m_fg_mode > 0) && DX12Streamline::instance().fg_ready() && m_mvec_pipeline.is_initialized();
		bool use_scale = (m_render_scale < 0.999f || fg_on) && m_upscale.is_initialized();
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
			m_scaled_rt.transition_depth_to_depth_write(m_command_list.Get()); // FG leaves depth in PSR -> restore (no-op otherwise)

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
			render_gizmos();   // always-on-top editor gizmos, drawn into the scaled RT before mvec/upscale

			// ---- DLSS SR (optional) + Frame Generation (optional): both need the motion-vector pass ----
			bool dlss_active = (m_dlss_mode > 0) && DX12Streamline::instance().dlss_ready()
				&& m_mvec_pipeline.is_initialized() && m_scaled_rt.depth_srv_gpu().ptr != 0;
			bool fg_inputs = fg_on && m_scaled_rt.depth_srv_gpu().ptr != 0;
			bool need_mvec = dlss_active || fg_inputs;
			bool dlss_done = false;
			if (need_mvec && ensure_mvec_rt(m_scaled_rt.width(), m_scaled_rt.height()))
			{
				using namespace DirectX;
				// Motion-vector pass: sample the scaled depth, write pixel-space velocity to m_mvec_rt.
				m_scaled_rt.transition_depth_to_shader_resource(m_command_list.Get());
				m_mvec_rt.transition_to_render_target(m_command_list.Get());
				D3D12_VIEWPORT mvp{}; mvp.Width = (float)m_scaled_rt.width(); mvp.Height = (float)m_scaled_rt.height(); mvp.MaxDepth = 1.0f;
				D3D12_RECT msc{}; msc.right = (LONG)m_scaled_rt.width(); msc.bottom = (LONG)m_scaled_rt.height();
				m_command_list->RSSetViewports(1, &mvp);
				m_command_list->RSSetScissorRects(1, &msc);
				auto mrtv = m_mvec_rt.rtv();
				m_command_list->OMSetRenderTargets(1, &mrtv, FALSE, nullptr);
				m_command_list->SetPipelineState(m_mvec_pipeline.pipeline_state());
				m_command_list->SetGraphicsRootSignature(m_mvec_pipeline.root_signature());
				ID3D12DescriptorHeap* mh[] = { m_scaled_rt.srv_heap() };
				m_command_list->SetDescriptorHeaps(1, mh);
				m_command_list->SetGraphicsRootDescriptorTable(0, m_scaled_rt.depth_srv_gpu());
				DX12MotionVectorPipeline::Constants mc{};
				XMMATRIX curVP = XMLoadFloat4x4(&m_frame_constants.view_projection);
				XMStoreFloat4x4(&mc.inv_view_proj, XMMatrixInverse(nullptr, curVP));
				mc.prev_view_proj = m_prev_view_projection;
				mc.dims[0] = (float)m_scaled_rt.width(); mc.dims[1] = (float)m_scaled_rt.height();
				m_command_list->SetGraphicsRoot32BitConstants(1, sizeof(mc) / 4, &mc, 0);
				m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
				m_command_list->DrawInstanced(3, 1, 0, 0);

				// Shared camera + depth + mvec description (SL manages tagged-resource states).
				DlssEvalDesc ed{};
				ed.cmd = m_command_list.Get();
				ed.outW = out_w; ed.outH = out_h;
				ed.renderW = m_scaled_rt.width(); ed.renderH = m_scaled_rt.height();
				ed.depth    = m_scaled_rt.depth_resource(); ed.depthState = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
				ed.mvec     = m_mvec_rt.resource();         ed.mvecState  = D3D12_RESOURCE_STATE_RENDER_TARGET;
				XMStoreFloat4x4(&ed.proj, XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees),
					(float)m_scaled_rt.width() / (float)m_scaled_rt.height(), 0.1f, 1000.0f));
				ed.camPos = m_camera_position;
				XMVECTOR cp = XMLoadFloat3(&m_camera_position), ct = XMLoadFloat3(&m_camera_target), cu = XMLoadFloat3(&m_camera_up);
				XMVECTOR fwd = XMVector3Normalize(XMVectorSubtract(ct, cp));
				XMVECTOR rgt = XMVector3Normalize(XMVector3Cross(cu, fwd));
				XMStoreFloat3(&ed.camFwd, fwd); XMStoreFloat3(&ed.camRight, rgt);
				ed.fovY = XMConvertToRadians(m_fov_degrees); ed.nearZ = 0.1f; ed.farZ = 1000.0f;

				// DLSS Super Resolution: also needs color in/out, then evaluate -> m_dlss_output.
				if (dlss_active && ensure_dlss_output(out_w, out_h))
				{
					ed.mode = m_dlss_mode;
					ed.colorIn  = m_scaled_rt.resource();   ed.colorInState  = D3D12_RESOURCE_STATE_RENDER_TARGET;
					ed.colorOut = m_dlss_output.resource(); ed.colorOutState = D3D12_RESOURCE_STATE_RENDER_TARGET;
					dlss_done = DX12Streamline::instance().evaluate_dlss(ed);
				}

				// Frame Generation: tag depth + mvec for the Present hook (uses the markers' frame token, no evaluate).
				if (fg_inputs) DX12Streamline::instance().tag_fg_frame(ed);

				// Depth for next frame: FG leaves it in PSR (consumed at Present; restored lazily at frame start);
				// SR-only restores depth_write now so the next 3D pass can clear/write it.
				if (!fg_inputs) m_scaled_rt.transition_depth_to_depth_write(m_command_list.Get());
			}

			// ---- composite onto the full-res back buffer (DLSS output if it ran, else the scaled color) ----
			barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
			barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
			m_command_list->ResourceBarrier(1, &barrier);

			DX12RenderTarget* blit_src = dlss_done ? &m_dlss_output : &m_scaled_rt;
			blit_src->transition_to_shader_resource(m_command_list.Get());

			auto bbrtv = m_swapchain.current_rtv();
			D3D12_VIEWPORT fvp{}; fvp.Width = (float)out_w; fvp.Height = (float)out_h; fvp.MaxDepth = 1.0f;
			D3D12_RECT fsc{}; fsc.right = (LONG)out_w; fsc.bottom = (LONG)out_h;
			m_command_list->RSSetViewports(1, &fvp);
			m_command_list->RSSetScissorRects(1, &fsc);
			m_command_list->OMSetRenderTargets(1, &bbrtv, FALSE, nullptr);   // no depth on the composite

			m_command_list->SetPipelineState(m_upscale.pipeline_state());
			m_command_list->SetGraphicsRootSignature(m_upscale.root_signature());
			ID3D12DescriptorHeap* heaps[] = { blit_src->srv_heap() };
			m_command_list->SetDescriptorHeaps(1, heaps);
			m_command_list->SetGraphicsRootDescriptorTable(0, blit_src->srv_gpu());
			m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
			m_command_list->DrawInstanced(3, 1, 0, 0);   // fullscreen triangle

			blit_src->transition_to_render_target(m_command_list.Get());   // leave RT ready for next frame

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
			render_gizmos();   // always-on-top editor gizmos, drawn last so they're never occluded

			barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
			barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
			m_command_list->ResourceBarrier(1, &barrier);
		}

		m_command_list->Close();

		m_command_queue.execute_command_list(m_command_list.Get());
		DX12Streamline::instance().frame_marker(3 /*eRenderSubmitEnd*/);

		// Signal after this frame's commands are queued (non-blocking)
		m_frame_fence_values[idx] = m_command_queue.signal();

		// 2D UI overlay (Direct2D over the same back buffer) — drawn after the 3D, before present.
		m_ui_overlay.render(m_swapchain.current_back_buffer());

		// Reliable verification: capture the fully-rendered back buffer (3D + UI) BEFORE present.
		if (m_capture_requested) { m_capture_requested = false; capture_backbuffer_to_bmp(m_capture_path.c_str()); }

		DX12Streamline::instance().frame_marker(4 /*ePresentStart*/);
		m_swapchain.present(m_vsync_enabled);   // SL-proxied -> DLSS-G inserts its generated frames here
		DX12Streamline::instance().frame_marker(5 /*ePresentEnd*/);
		// Note: m_render_queue is NOT cleared - we keep last frame's data
		// for re-rendering if no new data is submitted (prevents flickering)
		}


	void DX12Renderer::wait_for_previous_frame() { m_command_queue.signal_and_wait(); }
	

}
