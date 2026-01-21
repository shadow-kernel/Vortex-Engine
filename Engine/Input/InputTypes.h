#pragma once

#include "../Common/CommonHeaders.h"

namespace vortex::input {

	/// <summary>
	/// Keyboard key codes - matches Windows Virtual Key codes for compatibility
	/// </summary>
	enum class key_code : u32 {
		// Letters
		A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
		
		// Numbers
		Num0 = 0x30, Num1, Num2, Num3, Num4, Num5, Num6, Num7, Num8, Num9,
		
		// Numpad
		Numpad0 = 0x60, Numpad1, Numpad2, Numpad3, Numpad4, 
		Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
		NumpadMultiply = 0x6A,
		NumpadAdd = 0x6B,
		NumpadSeparator = 0x6C,
		NumpadSubtract = 0x6D,
		NumpadDecimal = 0x6E,
		NumpadDivide = 0x6F,
		
		// Function keys
		F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
		F13 = 0x7C, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
		
		// Special keys
		Backspace = 0x08,
		Tab = 0x09,
		Enter = 0x0D,
		Shift = 0x10,
		Ctrl = 0x11,
		Alt = 0x12,
		Pause = 0x13,
		CapsLock = 0x14,
		Escape = 0x1B,
		Space = 0x20,
		PageUp = 0x21,
		PageDown = 0x22,
		End = 0x23,
		Home = 0x24,
		Left = 0x25,
		Up = 0x26,
		Right = 0x27,
		Down = 0x28,
		PrintScreen = 0x2C,
		Insert = 0x2D,
		Delete = 0x2E,
		
		// Modifier keys
		LeftShift = 0xA0,
		RightShift = 0xA1,
		LeftCtrl = 0xA2,
		RightCtrl = 0xA3,
		LeftAlt = 0xA4,
		RightAlt = 0xA5,
		
		// OEM keys
		Semicolon = 0xBA,    // ;:
		Plus = 0xBB,         // =+
		Comma = 0xBC,        // ,<
		Minus = 0xBD,        // -_
		Period = 0xBE,       // .>
		Slash = 0xBF,        // /?
		Tilde = 0xC0,        // `~
		LeftBracket = 0xDB,  // [{
		Backslash = 0xDC,    // \|
		RightBracket = 0xDD, // ]}
		Quote = 0xDE,        // '"
		
		// Windows keys
		LeftWindows = 0x5B,
		RightWindows = 0x5C,
		Apps = 0x5D,
		
		NumLock = 0x90,
		ScrollLock = 0x91,
		
		None = 0xFF
	};

	/// <summary>
	/// Mouse button identifiers
	/// </summary>
	enum class mouse_button : u32 {
		Left = 0,
		Right = 1,
		Middle = 2,
		X1 = 3,
		X2 = 4,
		Count = 5
	};

	/// <summary>
	/// Input state for a key or button
	/// </summary>
	enum class input_state : u8 {
		Released = 0,  // Not pressed
		Pressed = 1,   // Just pressed this frame
		Held = 2,      // Held down
		JustReleased = 3  // Just released this frame
	};

	/// <summary>
	/// Controller/Gamepad buttons (future expansion)
	/// </summary>
	enum class gamepad_button : u32 {
		A = 0,
		B = 1,
		X = 2,
		Y = 3,
		LeftBumper = 4,
		RightBumper = 5,
		Back = 6,
		Start = 7,
		LeftStick = 8,
		RightStick = 9,
		DPadUp = 10,
		DPadDown = 11,
		DPadLeft = 12,
		DPadRight = 13,
		Count = 14
	};

	/// <summary>
	/// Controller axis identifiers
	/// </summary>
	enum class gamepad_axis : u32 {
		LeftStickX = 0,
		LeftStickY = 1,
		RightStickX = 2,
		RightStickY = 3,
		LeftTrigger = 4,
		RightTrigger = 5,
		Count = 6
	};

	/// <summary>
	/// Mouse movement/position data
	/// </summary>
	struct mouse_state {
		f32 x{ 0 };           // Current X position
		f32 y{ 0 };           // Current Y position
		f32 delta_x{ 0 };     // Movement since last frame
		f32 delta_y{ 0 };     // Movement since last frame
		f32 scroll_delta{ 0 }; // Mouse wheel delta
		bool buttons[static_cast<u32>(mouse_button::Count)]{ false };
	};

	/// <summary>
	/// Gamepad state (stub for future implementation)
	/// </summary>
	struct gamepad_state {
		bool connected{ false };
		f32 axes[static_cast<u32>(gamepad_axis::Count)]{ 0 };
		bool buttons[static_cast<u32>(gamepad_button::Count)]{ false };
		f32 left_vibration{ 0 };
		f32 right_vibration{ 0 };
	};

	constexpr u32 max_gamepads = 4;
	constexpr u32 max_keys = 256;
}
