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

        /// <summary>
        /// True when the "Game" tab is selected (the viewport shows the game view). This is independent
        /// of <see cref="IsPlaying"/>: the Game tab shows a placeholder + the static main-camera view
        /// until the user presses Play. The "Scene" tab clears it.
        /// </summary>
        public bool IsGameView { get; private set; }

        /// <summary>Raised when the Game/Scene view selection changes (on the UI thread).</summary>
        public event Action<bool> GameViewChanged;

        public void SetGameView(bool active)
        {
            if (IsGameView == active) return;
            IsGameView = active;
            GameViewChanged?.Invoke(active);
        }

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
