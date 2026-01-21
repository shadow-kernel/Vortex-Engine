#include "RenderLoop.h"
#include "../Graphics/DX12/DX12Renderer.h"

namespace vortex::runtime
{
	RenderLoop& RenderLoop::instance()
	{
		static RenderLoop inst;
		return inst;
	}

	void RenderLoop::start(std::function<void()> render_callback)
	{
		if (m_running) return;

		m_render_callback = render_callback;
		m_running = true;
		m_start_time = std::chrono::high_resolution_clock::now();
		m_last_frame_time = m_start_time;
		m_fps_update_time = m_start_time;
		m_frame_count = 0;

		m_render_thread = std::thread(&RenderLoop::loop_thread_func, this);
	}

	void RenderLoop::stop()
	{
		if (!m_running) return;

		m_running = false;
		if (m_render_thread.joinable())
		{
			m_render_thread.join();
		}
	}

	void RenderLoop::set_target_fps(int fps)
	{
		m_target_fps = fps;
	}

	void RenderLoop::loop_thread_func()
	{
		while (m_running)
		{
			auto now = std::chrono::high_resolution_clock::now();
			
			// FPS limiting (if not VSync and target FPS is set)
			if (!m_vsync_enabled && m_target_fps > 0)
			{
				auto elapsed = std::chrono::duration_cast<std::chrono::microseconds>(now - m_last_frame_time).count();
				long long target_frame_time = 1000000 / m_target_fps; // microseconds
				
				if (elapsed < target_frame_time)
				{
					// Sleep for most of the remaining time
					long long sleep_time = target_frame_time - elapsed - 1000; // Leave 1ms for spin wait
					if (sleep_time > 0)
					{
						std::this_thread::sleep_for(std::chrono::microseconds(sleep_time));
					}
					
					// Spin wait for precision
					while (std::chrono::duration_cast<std::chrono::microseconds>(
						std::chrono::high_resolution_clock::now() - m_last_frame_time).count() < target_frame_time)
					{
						std::this_thread::yield();
					}
				}
			}

			update_timing();

			// Call the render callback
			if (m_render_callback)
			{
				m_render_callback();
			}
		}
	}

	void RenderLoop::update_timing()
	{
		auto now = std::chrono::high_resolution_clock::now();
		
		// Calculate delta time
		auto elapsed = std::chrono::duration<float>(now - m_last_frame_time);
		m_delta_time = elapsed.count();
		m_last_frame_time = now;

		// Calculate total time
		auto total = std::chrono::duration<float>(now - m_start_time);
		m_total_time = total.count();

		// Update FPS counter
		m_frame_count++;
		auto fps_elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - m_fps_update_time).count();
		if (fps_elapsed >= 500) // Update every 0.5 seconds
		{
			m_current_fps = static_cast<int>((m_frame_count * 1000) / fps_elapsed);
			m_frame_count = 0;
			m_fps_update_time = now;
		}
	}

	void RenderLoop::render_single_frame()
	{
		update_timing();
		
		if (m_render_callback)
		{
			m_render_callback();
		}
	}
}
