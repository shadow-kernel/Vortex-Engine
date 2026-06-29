namespace Editor.UI.Vui
{
    /// <summary>
    /// Per-frame consumed input snapshot handed to the UI. The game tick fills it from the GameHost getters; the
    /// buffers (Chars/KeyEvents) are REUSED arrays (no per-frame allocation on the hot path).
    /// </summary>
    public struct VuiInput
    {
        public float Mx, My;        // cursor in client pixels (top-left origin)
        public bool Down;           // left button currently held
        public bool Pressed;        // edge-trigger: down this frame && !down last frame
        public int Wheel;           // accumulated wheel notches this frame (+up / -down)
        public char[] Chars;        // typed characters this frame (reused buffer)
        public int CharCount;
        public int[] KeyEvents;     // edge-pressed virtual-keys this frame (reused buffer)
        public int KeyCount;
    }
}
