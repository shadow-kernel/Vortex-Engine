// Gamepads for the .NET hosts (VortexAPI PollGamepad, SDL3 builds): a virtual SDL gamepad drives the export and the
// snapshot must come back in the XInput convention the gameplay API uses — button bits, sticks -1..1 with Y up,
// triggers 0..1 — and report the pad gone once it is unplugged. Headless: SDL's virtual joysticks need no device.
#include <SDL3/SDL.h>
#include <cstdint>
#include <cstdio>

struct VortexGamepadState { int32_t connected; uint16_t buttons; uint16_t reserved; float lx, ly, rx, ry, lt, rt; };
extern "C" int32_t PollGamepad(VortexGamepadState* out);

static int g_failures = 0;
static void check(bool ok, const char* what)
{
	std::printf("  [%s] %s\n", ok ? "ok" : "FAIL", what);
	if (!ok) ++g_failures;
}

int main()
{
	std::printf("Gamepad smoke test\n");
	VortexGamepadState s{};
	check(PollGamepad(&s) == 0 && s.connected == 0, "no gamepad: not connected");

	if (!SDL_Init(SDL_INIT_GAMEPAD)) { std::printf("SDL_Init failed: %s\nFAILURES!\n", SDL_GetError()); return 1; }
	SDL_VirtualJoystickDesc d;
	SDL_INIT_INTERFACE(&d);
	d.type = SDL_JOYSTICK_TYPE_GAMEPAD;
	d.naxes = SDL_GAMEPAD_AXIS_COUNT;
	d.nbuttons = SDL_GAMEPAD_BUTTON_COUNT;
	d.name = "Vortex Virtual Pad";
	SDL_JoystickID id = SDL_AttachVirtualJoystick(&d);
	SDL_Joystick* j = id ? SDL_OpenJoystick(id) : nullptr;
	check(j != nullptr, "virtual gamepad attached");
	if (!j) { std::printf("FAILURES!\n"); return 1; }

	check(PollGamepad(&s) == 1 && s.buttons == 0, "plugged in: connected, nothing pressed");   // opened at rest
	SDL_SetJoystickVirtualButton(j, SDL_GAMEPAD_BUTTON_SOUTH, true);
	SDL_SetJoystickVirtualButton(j, SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, true);
	SDL_SetJoystickVirtualButton(j, SDL_GAMEPAD_BUTTON_DPAD_LEFT, true);
	SDL_SetJoystickVirtualAxis(j, SDL_GAMEPAD_AXIS_LEFTX, 32767);
	SDL_SetJoystickVirtualAxis(j, SDL_GAMEPAD_AXIS_LEFTY, 32767);          // SDL: pushed down
	SDL_SetJoystickVirtualAxis(j, SDL_GAMEPAD_AXIS_RIGHTX, 2000);          // inside the dead zone
	SDL_SetJoystickVirtualAxis(j, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, 32767);
	SDL_UpdateJoysticks();
	check(PollGamepad(&s) == 1, "still connected");
	check(s.buttons == (0x1000 | 0x0200 | 0x0004), "A + RB + DPad left as XInput bits");
	check(s.lx > 0.99f && s.ly < -0.99f, "left stick right + down -> X +1, Y -1 (Y up)");
	check(s.rx == 0.0f, "dead zone on the right stick");
	check(s.rt > 0.99f && s.lt == 0.0f, "right trigger 1, left trigger 0");

	SDL_SetJoystickVirtualButton(j, SDL_GAMEPAD_BUTTON_SOUTH, false);
	SDL_UpdateJoysticks();
	PollGamepad(&s);
	check((s.buttons & 0x1000) == 0, "A released");

	SDL_CloseJoystick(j);
	SDL_DetachVirtualJoystick(id);
	SDL_UpdateJoysticks();
	check(PollGamepad(&s) == 0 && s.connected == 0, "unplugged: not connected");

	std::printf(g_failures ? "FAILURES!\n" : "ALL PASSED\n");
	return g_failures ? 1 : 0;
}
