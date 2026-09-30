using System;
using System.IO;

namespace Vortex.Player
{
    internal static class Program
    {
        // Must run on the process main thread: the native GameHost creates the window and pumps events there.
        private static int Main(string[] args)
        {
            var options = PlayerOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine("Vortex.Player [--project=<dir>] [--scene=<name>] [--width=<px>] [--height=<px>] [--renderscale=<f>] [--exit-after=<sec>] [--capture=<file.bmp>]");
                Console.WriteLine("              [--input=<script.txt>] [--capture-dir=<dir>] [--dump-scene]   (automation: timed keys/mouse/captures)");
                Console.WriteLine("Without --project the player runs the exported game next to the executable (player.vortex + Assets.vpak).");
                return 0;
            }
            try
            {
                return PlayerHost.Run(options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Vortex.Player failed: " + ex);
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "player_error.log"), DateTime.Now + " [Main]\n" + ex + "\n\n"); } catch { }
                return 1;
            }
        }
    }
}
