using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Editor.Core.Services.Decals;
using Editor.ECS.Components.Rendering;
using VortexEditor.Shell;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>Inspector card of the Decal component (#120): the projected material, the box, tint and opacity, the blend
    /// mode and the fades. The box shows as a magenta net in the viewport while the entity is selected.</summary>
    internal static class DecalCard
    {
        private static readonly string[] MaterialPatterns = { "*.vmat" };

        [ModuleInitializer]
        internal static void Register()
        {
            ComponentEditors.Custom[typeof(Decal)] = (c, e) => Rows((Decal)c);
        }

        private static IEnumerable<Control> Rows(Decal d)
        {
            yield return Row("Material", AssetPath(() => d.MaterialPath, v => { d.MaterialPath = string.IsNullOrEmpty(v) ? null : v.Replace('\\', '/'); Touch(); }, "Material", MaterialPatterns,
                () => AssetPickerDialog.Pick("Material", MaterialPatterns, DecalService.ResolvePath(d.MaterialPath))), "A .vmat: its albedo texture and base colour are projected (empty = the tint alone).");
            yield return Row("Size", Vector3(() => d.Size, v => { d.Size = v; Touch(); }, 0.1, 0.01f), "Box extents in metres: X and Z across the surface, Y the projection depth");
            yield return Row("Color", Color(() => (d.ColorR, d.ColorG, d.ColorB), (r, g, b) => { d.ColorR = r; d.ColorG = g; d.ColorB = b; Touch(); }), "Tint on the material");
            yield return Row("Opacity", SliderRow(() => d.Opacity, v => { d.Opacity = v; Touch(); }, 0, 1));
            yield return Row("Blend", Choice(() => d.Blend, v => { d.Blend = v; Touch(); }, "Lit", "Multiply", "Additive"),
                "Lit: shaded like a surface. Multiply: darkens what is under it (blood, grime). Additive: glows.");
            yield return Row("Angle fade", SliderRow(() => d.AngleFade, v => { d.AngleFade = v; Touch(); }, 0, 1),
                "Facing at which the decal is fully opaque — it fades on surfaces turning away from the projection; 0 = hard");
            yield return Row("Fade distance", FloatBox(() => d.FadeDistance, v => { d.FadeDistance = v; Touch(); }, 1, 0f), "Fully faded at this view distance in metres (0 = never)");
            yield return Row("Sort order", FloatBox(() => d.SortOrder, v => { d.SortOrder = (int)Math.Round(v); Touch(); }, 1, null, null, "0"), "Higher draws on top of lower");
            yield return Hint("Projects along the entity's local +Y (the green axis) onto the geometry inside the box. From scripts: Decal.Spawn(hit.Point, hit.Normal, \"Assets/Materials/Blood.vmat\", 0.4f, 20f).");
        }

        private static void Touch()
        {
            ComponentEditors.Dirty();
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }
    }
}
