using Editor.Core.Input;

namespace VortexTests
{
    /// <summary>Script input: the key names Input.GetKey accepts (#336) and the per-tick press latch behind
    /// GetKey / GetKeyDown that keeps a tap shorter than one frame from being lost (#337).</summary>
    public static class InputTests
    {
        [Test]
        public static void DigitKeyNamesInEverySpelling(TestContext t)
        {
            foreach (var name in new[] { "1", "D1", "Num1", "Alpha1", "Digit1", "Key1", "num1", "ALPHA1" })
                t.Equal((int)'1', KeyNames.VirtualKeyFromName(name), name + " is the number-row 1");
            t.Equal((int)'0', KeyNames.VirtualKeyFromName("Num0"), "Num0");
            t.Equal((int)'9', KeyNames.VirtualKeyFromName("Digit9"), "Digit9");
            foreach (var name in new[] { "Keypad5", "NumPad5", "keypad5" })
                t.Equal(0x65, KeyNames.VirtualKeyFromName(name), name + " is the numeric keypad 5");
            // the letters, F-keys and named keys still work
            t.Equal((int)'W', KeyNames.VirtualKeyFromName("W"), "W");
            t.Equal(0x74, KeyNames.VirtualKeyFromName("F5"), "F5");
            t.Equal(0x20, KeyNames.VirtualKeyFromName("Space"), "Space");
            t.Equal(0xA0, KeyNames.VirtualKeyFromName("LeftShift"), "LeftShift");
        }

        [Test]
        public static void UnknownKeyNamesAreZero(TestContext t)
        {
            foreach (var name in new[] { "Nmu1", "Num", "Num12", "Alpha", "Keypad", "Keypad10", "", "   " })
                t.Equal(0, KeyNames.VirtualKeyFromName(name), "'" + name + "' is unknown");
        }

        [Test]
        public static void PressLatchShowsATapForOneTick(TestContext t)
        {
            var old = HostInput.KeyDown;
            try
            {
                HostInput.KeyDown = vk => false;              // physically nothing is held
                HostInput.BeginTick();                        // start clean
                HostInput.BeginTick();
                t.False(HostInput.IsKeyDownThisTick(0x45), "E is not down");

                HostInput.NotifyKeyDown(0x45);                // a tap between two ticks (down + up before the next update)
                t.False(HostInput.IsKeyDownThisTick(0x45), "the press is pending until the next tick starts");
                HostInput.BeginTick();
                t.True(HostInput.IsKeyDownThisTick(0x45), "the tick after the tap sees the key down");
                HostInput.BeginTick();
                t.False(HostInput.IsKeyDownThisTick(0x45), "one tick only — then it is a release");

                HostInput.KeyDown = vk => vk == 0x45;         // held for real
                t.True(HostInput.IsKeyDownThisTick(0x45), "a held key is down regardless of the latch");
                HostInput.NotifyKeyDown(0);                   // vk 0 (unknown) is ignored
                HostInput.BeginTick();
                t.False(HostInput.IsKeyDownThisTick(0), "vk 0 never latches");
            }
            finally { HostInput.KeyDown = old; HostInput.BeginTick(); HostInput.BeginTick(); }
        }
    }
}
