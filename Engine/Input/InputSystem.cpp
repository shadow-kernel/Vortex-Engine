#include "InputSystem.h"

#ifdef _WIN64
#include <Windows.h>
#endif

namespace vortex::input {

	InputSystem& InputSystem::instance() {
		static InputSystem instance;
		return instance;
	}

	void InputSystem::initialize() {
		std::lock_guard<std::mutex> lock(m_mutex);
		if (m_initialized) return;

		// Reset all states
		for (u32 i = 0; i < max_keys; ++i) {
			m_key_states[i] = input_state::Released;
			m_key_current[i] = false;
			m_key_previous[i] = false;
		}

		for (u32 i = 0; i < static_cast<u32>(mouse_button::Count); ++i) {
			m_mouse_button_states[i] = input_state::Released;
			m_mouse_button_current[i] = false;
			m_mouse_button_previous[i] = false;
		}

		m_mouse = mouse_state{};
		for (u32 i = 0; i < max_gamepads; ++i) {
			m_gamepads[i] = gamepad_state{};
		}

		m_initialized = true;
	}

	void InputSystem::shutdown() {
		std::lock_guard<std::mutex> lock(m_mutex);
		if (!m_initialized) return;

		clear_callbacks();
		m_initialized = false;
	}

	void InputSystem::update() {
		std::lock_guard<std::mutex> lock(m_mutex);

		// Update keyboard states
		update_key_states();

		// Update mouse button states
		update_mouse_button_states();

		// Calculate mouse delta
		m_mouse.delta_x = m_mouse.x - m_mouse_prev_x;
		m_mouse.delta_y = m_mouse.y - m_mouse_prev_y;
		m_mouse_prev_x = m_mouse.x;
		m_mouse_prev_y = m_mouse.y;

		// Apply pending scroll delta and reset
		m_mouse.scroll_delta = m_pending_scroll_delta;
		m_pending_scroll_delta = 0;

		// TODO: Poll gamepad state here when implemented
	}

	void InputSystem::update_key_states() {
		for (u32 i = 0; i < max_keys; ++i) {
			const bool was_pressed = m_key_previous[i];
			const bool is_pressed = m_key_current[i];
			m_key_previous[i] = m_key_current[i];

			if (is_pressed && !was_pressed) {
				m_key_states[i] = input_state::Pressed;
				fire_key_callback(static_cast<key_code>(i), input_state::Pressed);
			}
			else if (is_pressed && was_pressed) {
				m_key_states[i] = input_state::Held;
			}
			else if (!is_pressed && was_pressed) {
				m_key_states[i] = input_state::JustReleased;
				fire_key_callback(static_cast<key_code>(i), input_state::JustReleased);
			}
			else {
				m_key_states[i] = input_state::Released;
			}
		}
	}

	void InputSystem::update_mouse_button_states() {
		for (u32 i = 0; i < static_cast<u32>(mouse_button::Count); ++i) {
			const bool was_pressed = m_mouse_button_previous[i];
			const bool is_pressed = m_mouse_button_current[i];
			m_mouse_button_previous[i] = m_mouse_button_current[i];
			m_mouse.buttons[i] = is_pressed;

			if (is_pressed && !was_pressed) {
				m_mouse_button_states[i] = input_state::Pressed;
				fire_mouse_button_callback(static_cast<mouse_button>(i), input_state::Pressed);
			}
			else if (is_pressed && was_pressed) {
				m_mouse_button_states[i] = input_state::Held;
			}
			else if (!is_pressed && was_pressed) {
				m_mouse_button_states[i] = input_state::JustReleased;
				fire_mouse_button_callback(static_cast<mouse_button>(i), input_state::JustReleased);
			}
			else {
				m_mouse_button_states[i] = input_state::Released;
			}
		}
	}

	void InputSystem::process_keyboard_event(u32 key, bool pressed) {
		if (key >= max_keys) return;
		std::lock_guard<std::mutex> lock(m_mutex);
		m_key_current[key] = pressed;
	}

	void InputSystem::process_mouse_button_event(mouse_button button, bool pressed) {
		const u32 idx = static_cast<u32>(button);
		if (idx >= static_cast<u32>(mouse_button::Count)) return;
		std::lock_guard<std::mutex> lock(m_mutex);
		m_mouse_button_current[idx] = pressed;
	}

	void InputSystem::process_mouse_move_event(f32 x, f32 y) {
		std::lock_guard<std::mutex> lock(m_mutex);
		m_mouse.x = x;
		m_mouse.y = y;

		// Fire move callbacks
		const f32 dx = x - m_mouse_prev_x;
		const f32 dy = y - m_mouse_prev_y;
		for (auto& callback : m_mouse_move_callbacks) {
			if (callback) callback(x, y, dx, dy);
		}
	}

	void InputSystem::process_mouse_scroll_event(f32 delta) {
		std::lock_guard<std::mutex> lock(m_mutex);
		m_pending_scroll_delta += delta;

		for (auto& callback : m_mouse_scroll_callbacks) {
			if (callback) callback(delta);
		}
	}

	// ============================================
	// Keyboard Queries
	// ============================================

	bool InputSystem::is_key_down(key_code key) const {
		const u32 idx = static_cast<u32>(key);
		if (idx >= max_keys) return false;
		std::lock_guard<std::mutex> lock(m_mutex);
		const auto state = m_key_states[idx];
		return state == input_state::Pressed || state == input_state::Held;
	}

	bool InputSystem::is_key_pressed(key_code key) const {
		const u32 idx = static_cast<u32>(key);
		if (idx >= max_keys) return false;
		std::lock_guard<std::mutex> lock(m_mutex);
		return m_key_states[idx] == input_state::Pressed;
	}

	bool InputSystem::is_key_released(key_code key) const {
		const u32 idx = static_cast<u32>(key);
		if (idx >= max_keys) return false;
		std::lock_guard<std::mutex> lock(m_mutex);
		return m_key_states[idx] == input_state::JustReleased;
	}

	input_state InputSystem::get_key_state(key_code key) const {
		const u32 idx = static_cast<u32>(key);
		if (idx >= max_keys) return input_state::Released;
		std::lock_guard<std::mutex> lock(m_mutex);
		return m_key_states[idx];
	}

	bool InputSystem::is_shift_down() const {
		return is_key_down(key_code::Shift) || 
			   is_key_down(key_code::LeftShift) || 
			   is_key_down(key_code::RightShift);
	}

	bool InputSystem::is_ctrl_down() const {
		return is_key_down(key_code::Ctrl) || 
			   is_key_down(key_code::LeftCtrl) || 
			   is_key_down(key_code::RightCtrl);
	}

	bool InputSystem::is_alt_down() const {
		return is_key_down(key_code::Alt) || 
			   is_key_down(key_code::LeftAlt) || 
			   is_key_down(key_code::RightAlt);
	}

	// ============================================
	// Mouse Queries
	// ============================================

	bool InputSystem::is_mouse_button_down(mouse_button button) const {
		const u32 idx = static_cast<u32>(button);
		if (idx >= static_cast<u32>(mouse_button::Count)) return false;
		std::lock_guard<std::mutex> lock(m_mutex);
		const auto state = m_mouse_button_states[idx];
		return state == input_state::Pressed || state == input_state::Held;
	}

	bool InputSystem::is_mouse_button_pressed(mouse_button button) const {
		const u32 idx = static_cast<u32>(button);
		if (idx >= static_cast<u32>(mouse_button::Count)) return false;
		std::lock_guard<std::mutex> lock(m_mutex);
		return m_mouse_button_states[idx] == input_state::Pressed;
	}

	bool InputSystem::is_mouse_button_released(mouse_button button) const {
		const u32 idx = static_cast<u32>(button);
		if (idx >= static_cast<u32>(mouse_button::Count)) return false;
		std::lock_guard<std::mutex> lock(m_mutex);
		return m_mouse_button_states[idx] == input_state::JustReleased;
	}

	void InputSystem::get_mouse_position(f32& x, f32& y) const {
		std::lock_guard<std::mutex> lock(m_mutex);
		x = m_mouse.x;
		y = m_mouse.y;
	}

	void InputSystem::get_mouse_delta(f32& dx, f32& dy) const {
		std::lock_guard<std::mutex> lock(m_mutex);
		dx = m_mouse.delta_x;
		dy = m_mouse.delta_y;
	}

	// ============================================
	// Gamepad Queries (Stub Implementation)
	// ============================================

	bool InputSystem::is_gamepad_connected(u32 gamepad_id) const {
		if (gamepad_id >= max_gamepads) return false;
		std::lock_guard<std::mutex> lock(m_mutex);
		return m_gamepads[gamepad_id].connected;
	}

	bool InputSystem::is_gamepad_button_down(u32 gamepad_id, gamepad_button button) const {
		if (gamepad_id >= max_gamepads) return false;
		const u32 btn_idx = static_cast<u32>(button);
		if (btn_idx >= static_cast<u32>(gamepad_button::Count)) return false;
		std::lock_guard<std::mutex> lock(m_mutex);
		return m_gamepads[gamepad_id].buttons[btn_idx];
	}

	f32 InputSystem::get_gamepad_axis(u32 gamepad_id, gamepad_axis axis) const {
		if (gamepad_id >= max_gamepads) return 0.0f;
		const u32 axis_idx = static_cast<u32>(axis);
		if (axis_idx >= static_cast<u32>(gamepad_axis::Count)) return 0.0f;
		std::lock_guard<std::mutex> lock(m_mutex);
		return m_gamepads[gamepad_id].axes[axis_idx];
	}

	void InputSystem::set_gamepad_vibration(u32 gamepad_id, f32 left_motor, f32 right_motor) {
		if (gamepad_id >= max_gamepads) return;
		std::lock_guard<std::mutex> lock(m_mutex);
		m_gamepads[gamepad_id].left_vibration = left_motor;
		m_gamepads[gamepad_id].right_vibration = right_motor;
		// TODO: Actually apply vibration via XInput or similar
	}

	const gamepad_state& InputSystem::get_gamepad_state(u32 gamepad_id) const {
		static gamepad_state empty_state{};
		if (gamepad_id >= max_gamepads) return empty_state;
		std::lock_guard<std::mutex> lock(m_mutex);
		return m_gamepads[gamepad_id];
	}

	// ============================================
	// Event Callbacks
	// ============================================

	void InputSystem::register_key_callback(key_callback callback) {
		std::lock_guard<std::mutex> lock(m_mutex);
		m_key_callbacks.push_back(std::move(callback));
	}

	void InputSystem::register_mouse_button_callback(mouse_button_callback callback) {
		std::lock_guard<std::mutex> lock(m_mutex);
		m_mouse_button_callbacks.push_back(std::move(callback));
	}

	void InputSystem::register_mouse_move_callback(mouse_move_callback callback) {
		std::lock_guard<std::mutex> lock(m_mutex);
		m_mouse_move_callbacks.push_back(std::move(callback));
	}

	void InputSystem::register_mouse_scroll_callback(mouse_scroll_callback callback) {
		std::lock_guard<std::mutex> lock(m_mutex);
		m_mouse_scroll_callbacks.push_back(std::move(callback));
	}

	void InputSystem::register_gamepad_callback(gamepad_callback callback) {
		std::lock_guard<std::mutex> lock(m_mutex);
		m_gamepad_callbacks.push_back(std::move(callback));
	}

	void InputSystem::clear_callbacks() {
		m_key_callbacks.clear();
		m_mouse_button_callbacks.clear();
		m_mouse_move_callbacks.clear();
		m_mouse_scroll_callbacks.clear();
		m_gamepad_callbacks.clear();
	}

	void InputSystem::fire_key_callback(key_code key, input_state state) {
		for (auto& callback : m_key_callbacks) {
			if (callback) callback(key, state);
		}
	}

	void InputSystem::fire_mouse_button_callback(mouse_button button, input_state state) {
		for (auto& callback : m_mouse_button_callbacks) {
			if (callback) callback(button, state);
		}
	}

	// ============================================
	// Input Mode
	// ============================================

	void InputSystem::set_cursor_locked(bool locked) {
		m_cursor_locked = locked;
#ifdef _WIN64
		if (locked) {
			// Lock cursor to center of screen - implementation depends on window handle
			// This is a stub - actual implementation needs window handle
		}
#endif
	}

	void InputSystem::set_cursor_visible(bool visible) {
		m_cursor_visible = visible;
#ifdef _WIN64
		ShowCursor(visible ? TRUE : FALSE);
#endif
	}
}
