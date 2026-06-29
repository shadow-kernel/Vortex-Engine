namespace Editor.UI.Vui
{
    /// <summary>The element kinds the retained UI supports. A whole screen is built from these — no per-screen C#.</summary>
    public enum VuiKind
    {
        Panel,      // container / colored background (the layout box)
        Text,       // bound label
        Image,      // textured quad (icon / logo / portrait)
        Bar,        // track + fill (health / sanity / battery / xp / progress)
        Button,     // clickable; hover tint; reports a click
        Slider,     // draggable handle -> 0..1 (volume / FOV / sensitivity)
        Toggle,     // two-state (VSync / fullscreen / subtitles)
        Stepper,    // cycle a list of options (resolution / quality)
        TextField,  // typed text (player name / chat)
        List,       // repeater: clones a RowTemplate per data row (player list / kill feed / scoreboard)
        Crosshair   // center reticle (4 lines / dot)
    }

    /// <summary>9-point anchor. ax/ay double as the default pivot, so a Center-anchored box centers on itself.</summary>
    public enum AnchorEnum
    {
        TopLeft, TopCenter, TopRight,
        MidLeft, Center, MidRight,
        BottomLeft, BottomCenter, BottomRight
    }

    /// <summary>Container child-layout mode (the one imperative layout path).</summary>
    public enum StackDir { None, Vertical, Horizontal, Grid }

    /// <summary>A resolved pixel rectangle (top-left origin). THE invariant read by both hit-test and render.</summary>
    public struct RectF
    {
        public float X, Y, W, H;
        public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
        public bool Contains(float px, float py) => px >= X && px <= X + W && py >= Y && py <= Y + H;
        public float CenterX => X + W * 0.5f;
        public float CenterY => Y + H * 0.5f;
    }
}
