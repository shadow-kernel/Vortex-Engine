using System;
using System.Runtime.InteropServices;

namespace Editor.DllWrapper
{
    /// <summary>The display the game shows on (#462): its modes and its current mode, for a script's options menu
    /// (Vortex.Screen). An engine library from before #462 answers with nothing.</summary>
    public static partial class VortexAPI
    {
        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "GameHostDisplayModes")]
        private static extern int GameHostDisplayModesNative([Out] int[] widths, [Out] int[] heights, [Out] float[] refreshHz, [Out] float[] densities, int max);

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "GameHostCurrentDisplayMode")]
        private static extern int GameHostCurrentDisplayModeNative(out int width, out int height, out float refreshHz, out float density);

        /// <summary>The display modes in pixels (largest first): fills the arrays (up to their length) and returns how many modes
        /// there are — more than the arrays hold when they are too short. 0 when the engine cannot tell.</summary>
        public static int GameHostDisplayModes(int[] widths, int[] heights, float[] refreshHz, float[] densities)
        {
            int max = Math.Min(Math.Min(widths?.Length ?? 0, heights?.Length ?? 0), Math.Min(refreshHz?.Length ?? 0, densities?.Length ?? 0));
            try { return Math.Max(0, GameHostDisplayModesNative(widths, heights, refreshHz, densities, max)); }
            catch (EntryPointNotFoundException) { return 0; }
            catch (DllNotFoundException) { return 0; }
        }

        /// <summary>The display's current (desktop) mode in pixels; false when it cannot be read.</summary>
        public static bool GameHostCurrentDisplayMode(out int width, out int height, out float refreshHz, out float density)
        {
            width = height = 0; refreshHz = 0f; density = 1f;
            try { return GameHostCurrentDisplayModeNative(out width, out height, out refreshHz, out density) != 0; }
            catch (EntryPointNotFoundException) { return false; }
            catch (DllNotFoundException) { return false; }
        }
    }
}
