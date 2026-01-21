#pragma once

#include "../Common/CommonHeaders.h"
#include <atomic>
#include <thread>
#include <functional>
#include <chrono>

namespace vortex::runtime
{
	/// <summary>
	/// Engine-side render loop with timing control.
	/// This runs on a dedicated thread and handles frame timing, VSync, and FPS limiting.
	/// </summary>
	class RenderLoop
	{
	public:
		static RenderLoop& instance();

		/// <summary>
		/// Start the render loop on a dedicated thread.
		/// </summary>
		/// <param name="render_callback">Function to call each frame</param>
		void start(std::function<void()> render_callback);

		/// <summary>
		/// Stop the render loop and wait for the thread to exit.
		/// </summary>
		void stop();

		/// <summary>
		/// Check if the render loop is running.
		/// </summary>
		bool is_running() const { return m_running; }

		/// <summary>
		/// Set target FPS (0 = unlimited).
		/// </summary>
		void set_target_fps(int fps);
		int get_target_fps() const { return m_target_fps; }

		/// <summary>
		/// Enable/disable VSync.
		/// </summary>
		void set_vsync(bool enabled) { m_vsync_enabled = enabled; }
		bool is_vsync_enabled() const { return m_vsync_enabled; }

		/// <summary>
		/// Get performance statistics.
		/// </summary>
		int get_current_fps() const { return m_current_fps; }
		float get_delta_time() const { return m_delta_time; }
		float get_total_time() const { return m_total_time; }

		/// <summary>
		/// Render a single frame (for editor integration where external timing is used).
		/// </summary>
		void render_single_frame();

	private:
		RenderLoop() = default;
		~RenderLoop() { stop(); }
		RenderLoop(const RenderLoop&) = delete;
		RenderLoop& operator=(const RenderLoop&) = delete;

		void loop_thread_func();
		void update_timing();

		std::atomic<bool> m_running{ false };
		std::atomic<bool> m_vsync_enabled{ false };
		std::atomic<int> m_target_fps{ 0 }; // 0 = unlimited
		
		std::thread m_render_thread;
		std::function<void()> m_render_callback;

		// Timing
		std::chrono::high_resolution_clock::time_point m_last_frame_time;
		std::chrono::high_resolution_clock::time_point m_fps_update_time;
		std::chrono::high_resolution_clock::time_point m_start_time;
		
		std::atomic<int> m_current_fps{ 0 };
		std::atomic<float> m_delta_time{ 0.0f };
		std::atomic<float> m_total_time{ 0.0f };
		int m_frame_count{ 0 };
	};
}
