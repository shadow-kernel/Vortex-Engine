using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Editor.UI.Vui
{
    /// <summary>
    /// System.Text.Json round-trip for .vui files (the SAME serializer pattern as VortexMaterial). The DTO is the
    /// single source of truth shared by the runtime (VuiCanvas) and the builder, so save->reopen is identical.
    /// </summary>
    public sealed class VuiDocument
    {
        public int Vui { get; set; } = 1;
        public int DesignW { get; set; } = 1920;
        public int DesignH { get; set; } = 1080;
        public VuiNodeDto Root { get; set; }

        private static readonly JsonSerializerOptions s_opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Load a .vui from disk into a ready-to-tick VuiCanvas (returns null on any failure).</summary>
        public static VuiCanvas Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var doc = JsonSerializer.Deserialize<VuiDocument>(File.ReadAllText(path), s_opts);
                if (doc == null || doc.Root == null) return null;
                var canvas = new VuiCanvas
                {
                    Name = path,
                    DesignW = doc.DesignW > 0 ? doc.DesignW : 1920,
                    DesignH = doc.DesignH > 0 ? doc.DesignH : 1080,
                    Root = ToRuntime(doc.Root, null)
                };
                canvas.Reindex();
                return canvas;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[VUI] Load failed: " + ex.Message); return null; }
        }

        /// <summary>Serialize a canvas back to an indented .vui on disk.</summary>
        public static void Save(VuiCanvas canvas, string path)
        {
            if (canvas == null || canvas.Root == null) return;
            var doc = new VuiDocument { Vui = 1, DesignW = canvas.DesignW, DesignH = canvas.DesignH, Root = FromRuntime(canvas.Root) };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, s_opts));
        }

        // ---- DTO <-> runtime ----
        public static VuiElement ToRuntime(VuiNodeDto d, VuiElement parent)
        {
            if (d == null) return null;
            var e = new VuiElement { Parent = parent };
            Enum.TryParse(d.Kind ?? "Panel", true, out e.Kind);
            e.Id = d.Id;
            if (!string.IsNullOrEmpty(d.Anchor)) Enum.TryParse(d.Anchor, true, out e.Anchor);
            if (d.Off != null && d.Off.Length >= 2) { e.OffX = d.Off[0]; e.OffY = d.Off[1]; }
            if (d.Pct != null && d.Pct.Length >= 2) { e.PctX = d.Pct[0]; e.PctY = d.Pct[1]; }
            e.StretchX = d.StretchX; e.StretchY = d.StretchY;
            if (d.Size != null && d.Size.Length >= 2) { e.W = d.Size[0]; e.H = d.Size[1]; }
            if (d.SizePct != null && d.SizePct.Length >= 2) { e.WPct = d.SizePct[0]; e.HPct = d.SizePct[1]; }
            if (d.Pivot != null && d.Pivot.Length >= 2) { e.HasPivot = true; e.PivotX = d.Pivot[0]; e.PivotY = d.Pivot[1]; }
            if (d.Bg != null && d.Bg.Length >= 4) e.Bg = d.Bg;
            if (d.Fg != null && d.Fg.Length >= 4) e.Fg = d.Fg;
            if (d.HoverTint != null && d.HoverTint.Length >= 4) e.HoverTint = d.HoverTint;
            e.Radius = d.Radius; e.Align = d.Align; e.Weight = d.Weight > 0 ? d.Weight : 600;
            e.FontSize = d.FontSize > 0 ? d.FontSize : 16;
            e.Text = d.Text; e.ImageAsset = d.Asset;
            e.Value = d.Value; e.Min = d.Min; e.Max = d.Max != 0 || d.Min != 0 ? d.Max : 1f;
            e.On = d.On; e.Options = d.Options; e.OptionIndex = d.OptionIndex; e.TargetSetting = d.TargetSetting;
            e.CapturesKey = d.CapturesKey; e.MaxChars = d.MaxChars > 0 ? d.MaxChars : 64;
            e.Visible = d.Visible; e.Opacity = d.Opacity <= 0 ? 1f : d.Opacity;
            e.BlocksInput = d.BlocksInput; e.CursorLocked = d.CursorLocked; e.ClipChildren = d.Clip;
            if (!string.IsNullOrEmpty(d.Layout)) Enum.TryParse(d.Layout, true, out e.LayoutMode);
            e.Spacing = d.Spacing; e.Padding = d.Padding; e.GridCols = d.GridCols > 0 ? d.GridCols : 1;
            if (d.Bind != null)
            {
                e.BindValue = d.Bind.Value; e.BindText = d.Bind.Text; e.BindVisible = d.Bind.Visible;
                e.BindColor = d.Bind.Color; e.BindImage = d.Bind.Image; e.BindClicked = d.Bind.Clicked; e.BindList = d.Bind.List;
            }
            if (d.RowTemplate != null) e.RowTemplate = ToRuntime(d.RowTemplate, e);
            if (d.Children != null)
                foreach (var c in d.Children) { var ce = ToRuntime(c, e); if (ce != null) e.Children.Add(ce); }
            return e;
        }

        public static VuiNodeDto FromRuntime(VuiElement e)
        {
            if (e == null) return null;
            var d = new VuiNodeDto
            {
                Kind = e.Kind.ToString(),
                Id = e.Id,
                Anchor = e.Anchor.ToString(),
                Off = new[] { e.OffX, e.OffY },
                StretchX = e.StretchX,
                StretchY = e.StretchY,
                Size = new[] { e.W, e.H },
                Radius = e.Radius,
                Align = e.Align,
                Weight = e.Weight,
                FontSize = e.FontSize,
                Text = e.Text,
                Asset = e.ImageAsset,
                TargetSetting = e.TargetSetting,
                Layout = e.LayoutMode != StackDir.None ? e.LayoutMode.ToString() : null,
            };
            if (e.PctX != 0 || e.PctY != 0) d.Pct = new[] { e.PctX, e.PctY };
            if (e.WPct != 0 || e.HPct != 0) d.SizePct = new[] { e.WPct, e.HPct };
            if (e.HasPivot) d.Pivot = new[] { e.PivotX, e.PivotY };
            if (e.Bg != null && (e.Bg[0] != 0 || e.Bg[1] != 0 || e.Bg[2] != 0 || e.Bg[3] != 0)) d.Bg = e.Bg;
            if (e.Fg != null) d.Fg = e.Fg;
            if (e.HoverTint != null) d.HoverTint = e.HoverTint;
            if (e.Kind == VuiKind.Bar || e.Kind == VuiKind.Slider) { d.Value = e.Value; d.Min = e.Min; d.Max = e.Max; }
            if (e.On) d.On = true;
            if (e.Options != null) { d.Options = e.Options; d.OptionIndex = e.OptionIndex; }
            if (e.CapturesKey) d.CapturesKey = true;
            if (e.MaxChars != 64) d.MaxChars = e.MaxChars;
            if (!e.Visible) d.Visible = false;
            if (e.Opacity != 1f) d.Opacity = e.Opacity;
            if (e.BlocksInput) d.BlocksInput = true;
            if (e.CursorLocked) d.CursorLocked = true;
            if (e.ClipChildren) d.Clip = true;
            if (e.Spacing != 0) d.Spacing = e.Spacing;
            if (e.Padding != 0) d.Padding = e.Padding;
            if (e.GridCols != 1) d.GridCols = e.GridCols;
            if (e.BindValue || e.BindText || e.BindVisible || e.BindColor || e.BindImage || e.BindClicked || e.BindList)
                d.Bind = new BindDto { Value = e.BindValue, Text = e.BindText, Visible = e.BindVisible, Color = e.BindColor, Image = e.BindImage, Clicked = e.BindClicked, List = e.BindList };
            if (e.RowTemplate != null) d.RowTemplate = FromRuntime(e.RowTemplate);
            if (e.Children != null && e.Children.Count > 0)
            {
                d.Children = new List<VuiNodeDto>(e.Children.Count);
                foreach (var c in e.Children) d.Children.Add(FromRuntime(c));
            }
            return d;
        }
    }

    /// <summary>Recursive JSON DTO mirroring a VuiElement (camelCase keys in the .vui file).</summary>
    public sealed class VuiNodeDto
    {
        public string Kind { get; set; }
        public string Id { get; set; }
        public string Anchor { get; set; }
        public float[] Off { get; set; }
        public float[] Pct { get; set; }
        public bool StretchX { get; set; }
        public bool StretchY { get; set; }
        public float[] Size { get; set; }
        public float[] SizePct { get; set; }
        public float[] Pivot { get; set; }
        public float[] Bg { get; set; }
        public float[] Fg { get; set; }
        public float[] HoverTint { get; set; }
        public float Radius { get; set; }
        public int Align { get; set; }
        public int Weight { get; set; }
        public float FontSize { get; set; }
        public string Text { get; set; }
        public string Asset { get; set; }
        public float Value { get; set; }
        public float Min { get; set; }
        public float Max { get; set; }
        public bool On { get; set; }
        public string[] Options { get; set; }
        public int OptionIndex { get; set; }
        public string TargetSetting { get; set; }
        public bool CapturesKey { get; set; }
        public int MaxChars { get; set; } = 64;
        public bool Visible { get; set; } = true;
        public float Opacity { get; set; } = 1f;
        public bool BlocksInput { get; set; }
        public bool CursorLocked { get; set; }
        public bool Clip { get; set; }
        public string Layout { get; set; }
        public float Spacing { get; set; }
        public float Padding { get; set; }
        public int GridCols { get; set; } = 1;
        public BindDto Bind { get; set; }
        public VuiNodeDto RowTemplate { get; set; }
        public List<VuiNodeDto> Children { get; set; }
    }

    public sealed class BindDto
    {
        public bool Value { get; set; }
        public bool Text { get; set; }
        public bool Visible { get; set; }
        public bool Color { get; set; }
        public bool Image { get; set; }
        public bool Clicked { get; set; }
        public bool List { get; set; }
    }
}
