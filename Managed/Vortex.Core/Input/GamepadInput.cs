using System;
using System.Runtime.InteropServices;

namespace Editor.Core.Input
{
    /// <summary>
    /// The controller source of the .NET hosts (the cross-platform editor, Vortex.Player) — what the WPF editor had
    /// through Windows.Gaming.Input, now on every platform:
    /// Windows: a DualSense / DualShock 4 read straight from its HID report, else XInput (Xbox, and pads Steam Input or
    /// DS4Windows present as Xbox); macOS / Linux: SDL3's gamepad API in the native engine (Xbox, PlayStation, Switch
    /// Pro …, USB and Bluetooth). One snapshot per tick in the XInput convention <see cref="GamepadState"/> uses.
    /// </summary>
    public static class GamepadInput
    {
        /// <summary>Plug this source into <see cref="HostInput.Gamepad"/> unless the host already has one.</summary>
        public static void Install()
        {
            if (HostInput.Gamepad == null) HostInput.Gamepad = Poll;
        }

        public static GamepadState Poll()
        {
            try { return OperatingSystem.IsWindows() ? PollWindows() : PollSdl(); }
            catch { return default(GamepadState); }   // never let a controller take the game tick down
        }

        // ---------------------------------------------------------------- macOS / Linux: SDL3 in the native engine

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeState
        {
            public int Connected;
            public ushort Buttons;
            public ushort Reserved;
            public float LX, LY, RX, RY, LT, RT;
        }

        [DllImport("VortexAPI", EntryPoint = "PollGamepad", CallingConvention = CallingConvention.Cdecl)]
        private static extern int PollGamepadNative(out NativeState state);

        private static bool _nativeMissing;

        private static GamepadState PollSdl()
        {
            if (_nativeMissing) return default(GamepadState);
            NativeState s;
            try { if (PollGamepadNative(out s) == 0) return default(GamepadState); }
            catch (EntryPointNotFoundException) { _nativeMissing = true; return default(GamepadState); }
            catch (DllNotFoundException) { _nativeMissing = true; return default(GamepadState); }
            return new GamepadState
            {
                Connected = s.Connected != 0, Buttons = s.Buttons,
                LeftX = s.LX, LeftY = s.LY, RightX = s.RX, RightY = s.RY, LeftTrigger = s.LT, RightTrigger = s.RT,
            };
        }

        // ---------------------------------------------------------------- Windows: DualSense HID, then XInput

        private static GamepadState PollWindows()
        {
            // A PlayStation pad straight from its HID report: XInput never sees one without Steam Input / DS4Windows.
            try
            {
                if (Editor.Scripting.DualSenseHid.Poll())
                    return new GamepadState
                    {
                        Connected = true, Buttons = Editor.Scripting.DualSenseHid.Buttons,
                        LeftX = Editor.Scripting.DualSenseHid.LX, LeftY = Editor.Scripting.DualSenseHid.LY,
                        RightX = Editor.Scripting.DualSenseHid.RX, RightY = Editor.Scripting.DualSenseHid.RY,
                        LeftTrigger = Editor.Scripting.DualSenseHid.L2, RightTrigger = Editor.Scripting.DualSenseHid.R2,
                    };
            }
            catch { }
            return PollXInput();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger; public byte bRightTrigger; public short sThumbLX; public short sThumbLY; public short sThumbRX; public short sThumbRY; }
        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint userIndex, out XINPUT_STATE state);

        private static bool _xinputMissing;

        private static GamepadState PollXInput()
        {
            if (_xinputMissing) return default(GamepadState);
            try
            {
                for (uint i = 0; i < 4; i++)
                {
                    XINPUT_STATE s;
                    if (XInputGetState(i, out s) != 0) continue;
                    return new GamepadState
                    {
                        Connected = true, Buttons = s.Gamepad.wButtons,
                        LeftX = Stick(s.Gamepad.sThumbLX, 7849), LeftY = Stick(s.Gamepad.sThumbLY, 7849),
                        RightX = Stick(s.Gamepad.sThumbRX, 8689), RightY = Stick(s.Gamepad.sThumbRY, 8689),
                        LeftTrigger = Trigger(s.Gamepad.bLeftTrigger), RightTrigger = Trigger(s.Gamepad.bRightTrigger),
                    };
                }
            }
            catch (DllNotFoundException) { _xinputMissing = true; }
            catch (EntryPointNotFoundException) { _xinputMissing = true; }
            return default(GamepadState);
        }

        private static float Stick(short v, int dead)
        {
            float f = v;
            if (f > dead) f = (f - dead) / (32767f - dead);
            else if (f < -dead) f = (f + dead) / (32768f - dead);
            else f = 0f;
            return f < -1f ? -1f : (f > 1f ? 1f : f);
        }

        private static float Trigger(byte t) => t <= 30 ? 0f : (t - 30) / (255f - 30f);
    }
}
