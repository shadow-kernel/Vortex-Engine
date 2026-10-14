using System;
using Editor.Core.Services;

namespace VortexTests
{
    /// <summary>#444: ▶ shows the Game view while the game runs and ■ returns to the view play started from. The Game view
    /// left on after Stop kept the main camera over the editor camera in edit mode, so focus_camera moved a camera that
    /// capture_viewport never saw.</summary>
    public static class PlayViewTests
    {
        /// <summary>A play-mode state of its own: the editor's singleton has listeners (the particle driver) this test must not wake.</summary>
        private static PlayModeService Fresh() => (PlayModeService)Activator.CreateInstance(typeof(PlayModeService), nonPublic: true);

        [Test]
        public static void StopReturnsToTheViewPlayStartedFrom(TestContext t)
        {
            var pms = Fresh();
            int switches = 0;
            pms.GameViewChanged += _ => switches++;
            pms.EnterGameViewForPlay();
            pms.Play();
            t.True(pms.IsGameView, "▶ shows the Game view");
            pms.Pause();
            pms.Resume();
            t.True(pms.IsGameView, "pause and resume keep it");
            pms.Stop();
            t.False(pms.IsGameView, "■ goes back to the Scene view");
            t.Equal(2, switches, "one switch each way, announced to the view tabs and the viewport");
        }

        [Test]
        public static void AGameViewChosenBeforePlayStays(TestContext t)
        {
            var chosen = Fresh();
            chosen.SetGameView(true);   // the Game tab, or Game View (without playing)
            chosen.EnterGameViewForPlay();
            chosen.Play();
            chosen.Stop();
            t.True(chosen.IsGameView, "the Game view the user picked stays after ■");

            var mcp = Fresh();   // enter_play_mode / exit_play_mode play without switching views
            mcp.Play();
            mcp.Stop();
            t.False(mcp.IsGameView, "play without ▶ leaves the Scene view alone");

            var scene = Fresh();   // the Scene tab during play: Stop, then the Scene view
            scene.EnterGameViewForPlay();
            scene.Play();
            scene.Stop();
            scene.SetGameView(false);
            t.False(scene.IsGameView, "Scene tab");
            scene.SetGameView(true);   // later the user picks the Game view without playing
            scene.Play();
            scene.Stop();
            t.True(scene.IsGameView, "a later play without ▶ keeps the user's Game view");
        }
    }
}
