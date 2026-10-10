using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Editor.Core.Services.Water;
using VortexEditor.Shell;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>Inspector card of the Water component (#200): the surface's size and resolution, its colours and clarity, the
    /// sky reflection, the ripples and the shore foam. The surface sits at the entity's height and follows the terrain below.</summary>
    internal static class WaterCard
    {
        [ModuleInitializer]
        internal static void Register()
        {
            ComponentEditors.Custom[typeof(Editor.ECS.Components.Rendering.Water)] = (c, e) => Rows((Editor.ECS.Components.Rendering.Water)c, e);
        }

        private static IEnumerable<Control> Rows(Editor.ECS.Components.Rendering.Water w, Editor.ECS.GameEntity entity)
        {
            yield return Row("Size", FloatBox(() => w.Size, v => { w.Size = v; Touch(); }, 1, 1f, 8192f, "0.#"), "Edge length (m) of the square surface centred on the entity; the entity's height is the water level");
            yield return Row("Cell size", FloatBox(() => w.CellSize, v => { w.CellSize = v; Touch(); }, 0.25, 0.25f, 64f, "0.##"), "Metres between surface vertices (the shore follows the terrain this finely)");
            yield return Row("Deep colour", Color(() => (w.DeepR, w.DeepG, w.DeepB), (r, g, b) => { w.DeepR = r; w.DeepG = g; w.DeepB = b; Touch(); }), "The colour of deep water");
            yield return Row("Shallow colour", Color(() => (w.ShallowR, w.ShallowG, w.ShallowB), (r, g, b) => { w.ShallowR = r; w.ShallowG = g; w.ShallowB = b; Touch(); }), "The colour over the bank");
            yield return Row("Absorption", SliderRow(() => w.Absorption, v => { w.Absorption = v; Touch(); }, 0.2, 20, "0.0 m"), "Depth at which the deep colour is reached — clear mountain lake: 8 m, murky pond: 0.5 m");
            yield return Row("Reflection", SliderRow(() => w.Reflection, v => { w.Reflection = v; Touch(); }, 0, 1, "0.00"), "How much sky the surface reflects at grazing angles");
            yield return Row("Roughness", SliderRow(() => w.Roughness, v => { w.Roughness = v; Touch(); }, 0.01, 0.6, "0.00"), "The sun's glitter: 0.02 glassy, 0.3 choppy");
            yield return Row("Wave scale", SliderRow(() => w.WaveScale, v => { w.WaveScale = v; Touch(); }, 0.5, 40, "0.0 m"), "Ripple wavelength");
            yield return Row("Wave speed", SliderRow(() => w.WaveSpeed, v => { w.WaveSpeed = v; Touch(); }, 0, 4, "0.00"), null);
            yield return Row("Wave height", SliderRow(() => w.WaveHeight, v => { w.WaveHeight = v; Touch(); }, 0, 2, "0.00"), "0 a mirror, 1 a windy lake");
            yield return Row("Foam width", SliderRow(() => w.FoamWidth, v => { w.FoamWidth = v; Touch(); }, 0, 5, "0.0 m"), "The foam band along the bank (0 = none)");
            yield return Row("Default depth", FloatBox(() => w.DefaultDepth, v => { w.DefaultDepth = v; Touch(); }, 0.5, 0f, 500f, "0.#"), "Depth assumed where no terrain lies under the surface (a pool over a mesh floor)");
            yield return Row("Surface", SurfaceRow(entity), "Vertices of the surface that lie over ground below the level (the wet part)");
            yield return Hint("Move the entity to set the water level; sculpt the terrain around it for the shore. From scripts: Water.Height(x, z), Water.IsUnderwater(pos), Water.Depth(pos).");
        }

        private static Control SurfaceRow(Editor.ECS.GameEntity entity)
        {
            var tb = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            System.Func<string> text = () => WaterService.WetVertices(entity).ToString("N0") + " wet vertices";
            tb.Text = text();
            Refreshers[tb] = () => tb.Text = text();
            return tb;
        }

        private static void Touch()
        {
            ComponentEditors.Dirty();
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }
    }
}
