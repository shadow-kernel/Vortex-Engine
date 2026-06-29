using System.Collections.Generic;

namespace Editor.UI.Vui
{
    /// <summary>
    /// The single passive retained node. ALL logic lives in VuiCanvas — this is just a data bag + children, so it
    /// stays trivial to allocate, serialize and reason about. A new screen is a tree of these (authored as .vui).
    /// </summary>
    public sealed class VuiElement
    {
        public VuiKind Kind;
        public string Id;                       // script-binding handle; unique within a canvas

        // --- layout authoring fields (the ONLY layout inputs; presets bake into these) ---
        public AnchorEnum Anchor = AnchorEnum.TopLeft;
        public float OffX, OffY;                // signed px offset from the anchor
        public float PctX, PctY;                // 0..1 fraction of parent W/H added to the offset (px+% mixable)
        public bool StretchX, StretchY;         // when true OffX / W act as left / right margins (size derived)
        public float W, H;                      // px size when not stretching
        public float WPct, HPct;                // 0..1 size as fraction of parent (overrides W/H when > 0)
        public bool HasPivot; public float PivotX, PivotY; // optional pivot override (default = anchor ax/ay)

        // --- visual ---
        public float[] Bg = { 0, 0, 0, 0 };     // RGBA 0..1 (track / background / button face)
        public float[] Fg = { 1, 1, 1, 1 };     // RGBA 0..1 (text / bar fill / handle)
        public float[] HoverTint;               // Button optional hover face
        public float Radius;
        public int Align;                       // Text: 0/1/2 = left/center/right
        public int Weight = 600;                // 400/600/700
        public float FontSize = 16;
        public string Text;
        public string ImageAsset;               // file path (absolute or project-relative)

        // --- value widgets ---
        public float Value;                     // Bar/Slider value (mapped into Min..Max)
        public float Min, Max = 1f;
        public bool On;                         // Toggle
        public string[] Options; public int OptionIndex;  // Stepper
        public string TargetSetting;            // advisory (e.g. "Camera.Fov") — the script reads + applies
        public bool CapturesKey;                // Button: clicking it enters keybind-capture mode
        public int CapturedKey;                 // the last virtual-key captured (0 = none) — read via GetCapturedKey
        public int MaxChars = 64;               // TextField input cap

        // --- state / flags ---
        public bool Visible = true;
        public float Opacity = 1f;
        public bool BlocksInput;                // a screen/modal that consumes clicks + owns the cursor
        public bool CursorLocked;               // canvas-level pref hoisted from the root (HUD = locked mouse-look)
        public bool ClipChildren;               // push a scissor around the children (lists / scroll)

        // --- containers ---
        public StackDir LayoutMode = StackDir.None;
        public float Spacing, Padding;
        public int GridCols = 1;
        public float ScrollY;                   // scroll offset for clipped lists
        public VuiElement RowTemplate;          // List repeater template (authored once, cloned per data row)

        // --- bind metadata (advisory: drives builder hints + an unknown-id warning) ---
        public bool BindValue, BindText, BindVisible, BindColor, BindImage, BindClicked, BindList;

        // --- tree ---
        public VuiElement Parent;
        public List<VuiElement> Children = new List<VuiElement>();

        // --- per-frame scratch (NOT serialized) ---
        public RectF Resolved;                  // THE invariant: read by BOTH hit-test and draw
        public bool RuntimeVisibleEffective;    // Visible && whole parent chain visible
        public bool Hot;                         // hovered this frame
        public List<VuiElement> RowPool;        // pooled clones for List rows (reused, no per-frame GC)
    }
}
