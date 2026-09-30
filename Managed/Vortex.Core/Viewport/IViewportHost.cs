namespace Editor.Core.Viewport
{
    /// <summary>
    /// What the editor viewport session needs from the UI shell that embeds the native render surface.
    /// All sizes are logical (DPI-independent) units of the host control; the shell converts to pixels.
    /// </summary>
    public interface IViewportHost
    {
        double Width { get; }
        double Height { get; }
        /// <summary>Device pixels per logical unit (2.0 on Retina displays).</summary>
        double Scaling { get; }
        /// <summary>True while the editor window is the active (foreground) window.</summary>
        bool IsWindowActive { get; }
        /// <summary>Hide/show the mouse cursor (fly mode, game mouse-look).</summary>
        void SetCursorHidden(bool hidden);
        /// <summary>Move the OS cursor to the centre of the viewport (game mouse-look re-centering).</summary>
        void WarpCursorToCenter();
        /// <summary>Give keyboard focus to the viewport.</summary>
        void FocusViewport();
        /// <summary>Drop keyboard focus from the viewport (ESC).</summary>
        void ClearFocus();
        /// <summary>Short transient message for the user (material assigned, ...).</summary>
        void ShowToast(string message);
    }
}
