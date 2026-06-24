using System;

namespace Editor.Core.Services
{
    /// <summary>Runtime play state of the editor.</summary>
    public enum PlayState
    {
        Editing,
        Playing,
        Paused
    }

    /// <summary>
    /// Single source of truth for play-mode state. Replaces the scattered
    /// _isPlaying/_isPaused flags previously duplicated across HeaderBar and
    /// GamePreview. Components subscribe to <see cref="StateChanged"/> to react
    /// (e.g. the editor viewport suspends rendering, the main window freezes).
    /// </summary>
    public sealed class PlayModeService
    {
        public static PlayModeService Instance { get; } = new PlayModeService();
        private PlayModeService() { }

        public PlayState State { get; private set; } = PlayState.Editing;

        public bool IsPlaying => State == PlayState.Playing || State == PlayState.Paused;

        /// <summary>Raised whenever the play state changes (on the UI thread).</summary>
        public event EventHandler<PlayState> StateChanged;

        public void Play()   => SetState(PlayState.Playing);
        public void Pause()  { if (State == PlayState.Playing) SetState(PlayState.Paused); }
        public void Resume() { if (State == PlayState.Paused) SetState(PlayState.Playing); }
        public void Stop()   => SetState(PlayState.Editing);

        private void SetState(PlayState next)
        {
            if (State == next) return;
            State = next;
            StateChanged?.Invoke(this, next);
        }
    }
}
