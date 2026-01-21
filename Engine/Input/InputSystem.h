#pragma once

#include "InputTypes.h"
#include <mutex>
#include <functional>
#include <vector>

namespace vortex::input {

	/// <summary>
	/// Callback types for input events
	/// </summary>
	using key_callback = std::function<void(key_code key, input_state state)>;
	using mouse_button_callback = std::function<void(mouse_button button, input_state state)>;
	using mouse_move_callback = std::function<void(f32 x, f32 y, f32 delta_x, f32 delta_y)>;
	using mouse_scroll_callback = std::function<void(f32 delta)>;
	using gamepad_callback = std::function<void(u32 gamepad_id, gamepad_button button, input_state state)>;

	/// <summary>
	/// Central input management system for the Vortex Engine.
	/// Handles keyboard, mouse, and gamepad input with event callbacks.
	/// Thread-safe singleton pattern for global access.
	/// </summary>
	class InputSystem {
	public:
		static InputSystem& instance();

		// ============================================
		// Initialization & Update
		// ============================================
		
		/// <summary>
		/// Initialize the input system
		/// </summary>
		void initialize();

		/// <summary>
		/// Shutdown and cleanup
		/// </summary>
		void shutdown();

		/// <summary>
		/// Update input states - call once per frame at the beginning
		/// </summary>
		void update();

		/// <summary>
		/// Process raw input from window message handler
		/// </summary>
		void process_keyboard_event(u32 key, bool pressed);
		void process_mouse_button_event(mouse_button button, bool pressed);
		void process_mouse_move_event(f32 x, f32 y);
		void process_mouse_scroll_event(f32 delta);

		// ============================================
		// Keyboard Queries
		// ============================================
		
		/// <summary>
		/// Check if a key is currently held down
		/// </summary>
		bool is_key_down(key_code key) const;

		/// <summary>
		/// Check if a key was just pressed this frame
		/// </summary>
		bool is_key_pressed(key_code key) const;

		/// <summary>
		/// Check if a key was just released this frame
		/// </summary>
		bool is_key_released(key_code key) const;

		/// <summary>
		/// Get the current state of a key
		/// </summary>
		input_state get_key_state(key_code key) const;

		/// <summary>
		/// Check common modifier states
		/// </summary>
		bool is_shift_down() const;
		bool is_ctrl_down() const;
		bool is_alt_down() const;

		// ============================================
		// Mouse Queries
		// ============================================
		
		/// <summary>
		/// Check if a mouse button is held down
		/// </summary>
		bool is_mouse_button_down(mouse_button button) const;

		/// <summary>
		/// Check if a mouse button was just pressed
		/// </summary>
		bool is_mouse_button_pressed(mouse_button button) const;

		/// <summary>
		/// Check if a mouse button was just released
		/// </summary>
		bool is_mouse_button_released(mouse_button button) const;

		/// <summary>
		/// Get mouse position
		/// </summary>
		void get_mouse_position(f32& x, f32& y) const;
		f32 get_mouse_x() const { return m_mouse.x; }
		f32 get_mouse_y() const { return m_mouse.y; }

		/// <summary>
		/// Get mouse delta since last frame
		/// </summary>
		void get_mouse_delta(f32& dx, f32& dy) const;
		f32 get_mouse_delta_x() const { return m_mouse.delta_x; }
		f32 get_mouse_delta_y() const { return m_mouse.delta_y; }

		/// <summary>
		/// Get mouse scroll delta
		/// </summary>
		f32 get_mouse_scroll_delta() const { return m_mouse.scroll_delta; }

		/// <summary>
		/// Get the complete mouse state
		/// </summary>
		const mouse_state& get_mouse_state() const { return m_mouse; }

		// ============================================
		// Gamepad Queries (Stub for future)
		// ============================================
		
		/// <summary>
		/// Check if a gamepad is connected
		/// </summary>
		bool is_gamepad_connected(u32 gamepad_id) const;

		/// <summary>
		/// Get gamepad button state
		/// </summary>
		bool is_gamepad_button_down(u32 gamepad_id, gamepad_button button) const;

		/// <summary>
		/// Get gamepad axis value (-1.0 to 1.0)
		/// </summary>
		f32 get_gamepad_axis(u32 gamepad_id, gamepad_axis axis) const;

		/// <summary>
		/// Set gamepad vibration (0.0 to 1.0)
		/// </summary>
		void set_gamepad_vibration(u32 gamepad_id, f32 left_motor, f32 right_motor);

		/// <summary>
		/// Get complete gamepad state
		/// </summary>
		const gamepad_state& get_gamepad_state(u32 gamepad_id) const;

		// ============================================
		// Event Callbacks
		// ============================================
		
		/// <summary>
		/// Register callbacks for input events
		/// </summary>
		void register_key_callback(key_callback callback);
		void register_mouse_button_callback(mouse_button_callback callback);
		void register_mouse_move_callback(mouse_move_callback callback);
		void register_mouse_scroll_callback(mouse_scroll_callback callback);
		void register_gamepad_callback(gamepad_callback callback);

		/// <summary>
		/// Clear all registered callbacks
		/// </summary>
		void clear_callbacks();

		// ============================================
		// Input Mode
		// ============================================
		
		/// <summary>
		/// Lock/unlock mouse cursor (for FPS-style camera control)
		/// </summary>
		void set_cursor_locked(bool locked);
		bool is_cursor_locked() const { return m_cursor_locked; }

		/// <summary>
		/// Show/hide mouse cursor
		/// </summary>
		void set_cursor_visible(bool visible);
		bool is_cursor_visible() const { return m_cursor_visible; }

	private:
		InputSystem() = default;
		~InputSystem() { shutdown(); }
		InputSystem(const InputSystem&) = delete;
		InputSystem& operator=(const InputSystem&) = delete;

		void update_key_states();
		void update_mouse_button_states();
		void fire_key_callback(key_code key, input_state state);
		void fire_mouse_button_callback(mouse_button button, input_state state);

		// Keyboard state
		input_state m_key_states[max_keys]{ input_state::Released };
		bool m_key_current[max_keys]{ false };
		bool m_key_previous[max_keys]{ false };

		// Mouse state
		mouse_state m_mouse{};
		input_state m_mouse_button_states[static_cast<u32>(mouse_button::Count)]{ input_state::Released };
		bool m_mouse_button_current[static_cast<u32>(mouse_button::Count)]{ false };
		bool m_mouse_button_previous[static_cast<u32>(mouse_button::Count)]{ false };
		f32 m_mouse_prev_x{ 0 };
		f32 m_mouse_prev_y{ 0 };
		f32 m_pending_scroll_delta{ 0 };

		// Gamepad state (stub)
		gamepad_state m_gamepads[max_gamepads]{};

		// Callbacks
		std::vector<key_callback> m_key_callbacks;
		std::vector<mouse_button_callback> m_mouse_button_callbacks;
		std::vector<mouse_move_callback> m_mouse_move_callbacks;
		std::vector<mouse_scroll_callback> m_mouse_scroll_callbacks;
		std::vector<gamepad_callback> m_gamepad_callbacks;

		// Input mode
		bool m_cursor_locked{ false };
		bool m_cursor_visible{ true };
		bool m_initialized{ false };

		// Thread safety
		mutable std::mutex m_mutex;
	};

	// Convenience global access functions
	inline InputSystem& input() { return InputSystem::instance(); }
	inline bool key_down(key_code key) { return InputSystem::instance().is_key_down(key); }
	inline bool key_pressed(key_code key) { return InputSystem::instance().is_key_pressed(key); }
	inline bool key_released(key_code key) { return InputSystem::instance().is_key_released(key); }
	inline bool mouse_down(mouse_button btn) { return InputSystem::instance().is_mouse_button_down(btn); }
	inline bool mouse_pressed(mouse_button btn) { return InputSystem::instance().is_mouse_button_pressed(btn); }
	inline bool mouse_released(mouse_button btn) { return InputSystem::instance().is_mouse_button_released(btn); }
}
