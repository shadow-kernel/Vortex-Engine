#include "../ApiCommon.h"

EDITOR_INTERFACE void InitializeInput()
{
	input::InputSystem::instance().initialize();
}

EDITOR_INTERFACE void ShutdownInput()
{
	input::InputSystem::instance().shutdown();
}

EDITOR_INTERFACE void UpdateInput()
{
	input::InputSystem::instance().update();
}

EDITOR_INTERFACE void ProcessKeyboardEvent(unsigned int key, bool pressed)
{
	input::InputSystem::instance().process_keyboard_event(key, pressed);
}

EDITOR_INTERFACE void ProcessMouseButtonEvent(unsigned int button, bool pressed)
{
	input::InputSystem::instance().process_mouse_button_event(static_cast<input::mouse_button>(button), pressed);
}

EDITOR_INTERFACE void ProcessMouseMoveEvent(float x, float y)
{
	input::InputSystem::instance().process_mouse_move_event(x, y);
}

EDITOR_INTERFACE void ProcessMouseScrollEvent(float delta)
{
	input::InputSystem::instance().process_mouse_scroll_event(delta);
}

EDITOR_INTERFACE bool IsKeyDown(unsigned int key)
{
	return input::InputSystem::instance().is_key_down(static_cast<input::key_code>(key));
}

EDITOR_INTERFACE bool IsKeyPressed(unsigned int key)
{
	return input::InputSystem::instance().is_key_pressed(static_cast<input::key_code>(key));
}

EDITOR_INTERFACE bool IsKeyReleased(unsigned int key)
{
	return input::InputSystem::instance().is_key_released(static_cast<input::key_code>(key));
}

EDITOR_INTERFACE bool IsShiftDown()
{
	return input::InputSystem::instance().is_shift_down();
}

EDITOR_INTERFACE bool IsCtrlDown()
{
	return input::InputSystem::instance().is_ctrl_down();
}

EDITOR_INTERFACE bool IsAltDown()
{
	return input::InputSystem::instance().is_alt_down();
}

EDITOR_INTERFACE bool IsMouseButtonDown(unsigned int button)
{
	return input::InputSystem::instance().is_mouse_button_down(static_cast<input::mouse_button>(button));
}

EDITOR_INTERFACE bool IsMouseButtonPressed(unsigned int button)
{
	return input::InputSystem::instance().is_mouse_button_pressed(static_cast<input::mouse_button>(button));
}

EDITOR_INTERFACE bool IsMouseButtonReleased(unsigned int button)
{
	return input::InputSystem::instance().is_mouse_button_released(static_cast<input::mouse_button>(button));
}

EDITOR_INTERFACE void GetMousePosition(float* x, float* y)
{
	if (x && y) {
		input::InputSystem::instance().get_mouse_position(*x, *y);
	}
}

EDITOR_INTERFACE void GetMouseDelta(float* dx, float* dy)
{
	if (dx && dy) {
		input::InputSystem::instance().get_mouse_delta(*dx, *dy);
	}
}

EDITOR_INTERFACE float GetMouseScrollDelta()
{
	return input::InputSystem::instance().get_mouse_scroll_delta();
}

EDITOR_INTERFACE void SetCursorLocked(bool locked)
{
	input::InputSystem::instance().set_cursor_locked(locked);
}

EDITOR_INTERFACE void SetCursorVisible(bool visible)
{
	input::InputSystem::instance().set_cursor_visible(visible);
}

EDITOR_INTERFACE bool IsCursorLocked()
{
	return input::InputSystem::instance().is_cursor_locked();
}

EDITOR_INTERFACE bool IsCursorVisible()
{
	return input::InputSystem::instance().is_cursor_visible();
}

// Gamepad stubs
EDITOR_INTERFACE bool IsGamepadConnected(unsigned int gamepad_id)
{
	return input::InputSystem::instance().is_gamepad_connected(gamepad_id);
}

EDITOR_INTERFACE bool IsGamepadButtonDown(unsigned int gamepad_id, unsigned int button)
{
	return input::InputSystem::instance().is_gamepad_button_down(gamepad_id, static_cast<input::gamepad_button>(button));
}

EDITOR_INTERFACE float GetGamepadAxis(unsigned int gamepad_id, unsigned int axis)
{
	return input::InputSystem::instance().get_gamepad_axis(gamepad_id, static_cast<input::gamepad_axis>(axis));
}

EDITOR_INTERFACE void SetGamepadVibration(unsigned int gamepad_id, float left_motor, float right_motor)
{
	input::InputSystem::instance().set_gamepad_vibration(gamepad_id, left_motor, right_motor);
}

// ============== CAMERA SYSTEM API ==============

