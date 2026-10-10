using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Editor.Core.Services.Foliage;
using Editor.Core.Services.Terrain;
using Editor.ECS.Components.Rendering;
using VortexEditor.Shell;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>Inspector card of the Foliage component (#125): the layer's wind and view distance, its types (a model each
    /// with placement, distance, collision and wind settings) and the paint / erase brush that works in the viewport while
    /// the entity is selected (drag to paint, Shift erases; every stroke is one undo step and saves the data file).</summary>
    internal static class FoliageCard
    {
        private static readonly string[] ModelPatterns = { "*.glb", "*.gltf", "*.fbx", "*.obj", "*.vmesh" };
        private static readonly string[] ToolLabels = { "Select", "Paint", "Erase" };
        private static readonly string[] CollisionLabels = { "None", "Capsule (trunk)", "Box" };

        [ModuleInitializer]
        internal static void Register()
        {
            ComponentEditors.Custom[typeof(Editor.ECS.Components.Rendering.Foliage)] = (c, e) => Rows((Editor.ECS.Components.Rendering.Foliage)c, e);
        }

        private static IEnumerable<Control> Rows(Editor.ECS.Components.Rendering.Foliage f, Editor.ECS.GameEntity entity)
        {
            yield return Row("Wind", SliderRow(() => f.Wind, v => { f.Wind = v; Touch(f); }, 0, 3, "0.00"), "Sway multiplier for every type (0 calm, 1 breeze, 2+ storm); scripts drive it through Foliage.SetWind / the weather");
            yield return Row("View distance", SliderRow(() => f.ViewDistanceScale, v => { f.ViewDistanceScale = v; Touch(f); }, 0.25, 3, "0.00 ×"), "Scales every type's cull distance");
            yield return Row("Data file", DataFileRow(f, entity), "The .vfoliage with the instances; saved after every stroke and with the scene");
            yield return Row("Instances", CountRow(entity), "Painted instances on this layer");

            yield return Section("Types");
            for (int i = 0; i < f.Types.Count; i++)
            {
                int ti = i;
                var t = f.Types[ti];
                yield return Row("Type " + (ti + 1), NameRow(f, t), "The type's name (instances bind to it); × removes the type and its instances");
                yield return Row("  model", AssetPath(() => t.MeshPath, v => { t.MeshPath = string.IsNullOrEmpty(v) ? null : v.Replace('\\', '/'); Touch(f); }, "Model", ModelPatterns,
                    () => AssetPickerDialog.Pick("Model", ModelPatterns, TerrainService.ResolvePath(t.MeshPath))), "A model (.glb / .gltf / .fbx / .vmesh) or Primitive:Sphere");
                yield return Row("  scale", ScaleRow(f, t), "Random uniform scale range per instance");
                yield return Row("  density", FloatBox(() => t.Density, v => { t.Density = Math.Max(0f, v); Touch(f); }, 0.05, 0f, 50f, "0.###"), "Instances per m² at full brush strength");
                yield return Row("  spacing", FloatBox(() => t.MinSpacing, v => { t.MinSpacing = Math.Max(0f, v); Touch(f); }, 0.1, 0f, 100f, "0.##"), "Minimum distance (m) between two instances of this type");
                yield return Row("  max slope", SliderRow(() => t.MaxSlope, v => { t.MaxSlope = v; Touch(f); }, 0, 89, "0°"), "Steepest ground the type grows on");
                yield return Row("  align", Check("to the surface normal", () => t.AlignToNormal, v => { t.AlignToNormal = v; Touch(f); }, "Grass and ground cover lie on the slope; trees stand upright"), null);
                yield return Row("  tilt", SliderRow(() => t.MaxTilt, v => { t.MaxTilt = v; Touch(f); }, 0, 45, "0°"), "Random lean per instance");
                yield return Row("  cull distance", FloatBox(() => t.CullDistance, v => { t.CullDistance = Math.Max(1f, v); Touch(f); }, 5, 1f, 5000f, "0"), "Instances beyond this camera distance (m) are not drawn");
                yield return Row("  thin from", FloatBox(() => t.ThinDistance, v => { t.ThinDistance = Math.Max(0f, v); Touch(f); }, 5, 0f, 5000f, "0"), "Beyond this distance every second instance draws, beyond twice it every fourth (grass); 0 = never");
                yield return Row("  collision", Choice(() => t.Collision, v => { t.Collision = v; Touch(f); }, CollisionLabels), "Trunks as capsules: the player and the physics collide, the navmesh goes around them");
                yield return Row("  collider", CollisionRow(f, t), "Capsule radius and height in metres (× the instance scale)");
                yield return Row("  wind", WindRow(f, t), "Sway at the top (m), speed, and the height (m) of the top — the roots stay");
                yield return Row("  cutout", Check("alpha-tested leaves, both sides", () => t.Cutout, v => { t.Cutout = v; Touch(f); }, "The model's materials cut out below 50 % alpha and draw both sides (leaves, blades)"), null);
                yield return Row("  weight", FloatBox(() => t.Weight, v => { t.Weight = Math.Max(0f, v); Touch(f); }, 0.1, 0f, 100f, "0.##"), "Share of the brush when every type paints at once");
            }
            var addTree = Ghost("+ Tree type", () => { f.Types.Add(Editor.ECS.Components.Rendering.Foliage.DefaultType("Tree " + (f.Types.Count + 1), null, false)); Touch(f); Refresh(); }, "Upright, colliding, far view distance — pick its model");
            var addPlant = Ghost("+ Plant type", () => { f.Types.Add(Editor.ECS.Components.Rendering.Foliage.DefaultType("Plant " + (f.Types.Count + 1), null, true)); Touch(f); Refresh(); }, "Aligned to the ground, dense, thinned with distance — grass, ferns, bushes");
            yield return Row("", Actions(addTree, addPlant));

            yield return Section("Brush");
            yield return Row("Tool", Choice(() => (int)FoliageToolService.Tool, v => { FoliageToolService.SetTool((FoliageTool)v); Refresh(); }, ToolLabels),
                "Paint: drag over terrain or mesh tops. Erase: drag to remove (Shift while painting erases too). Select: the normal gizmo");
            yield return Row("Type", TypeChoice(f), "What the brush paints: one type or every type by weight");
            yield return Row("Radius", SliderRow(() => FoliageToolService.Radius, v => FoliageToolService.Radius = v, 0.5, 60, "0.0 m"), "Brush radius in metres");
            yield return Row("Strength", SliderRow(() => FoliageToolService.Strength, v => FoliageToolService.Strength = v, 0.02, 1, "0.00"), "How densely one dab paints (× density) / how thoroughly it erases");
            var clear = Ghost("Clear all instances", () =>
            {
                int n = FoliageService.ClearInstances(entity, -1);
                FoliageService.Save(entity);
                Touch(f); Refresh();
            }, "Remove every painted instance of this layer (undo not available)");
            var save = Ghost("Save now", () => { FoliageService.Save(entity); Refresh(); }, "Write the instances to the data file");
            yield return Row("", Actions(clear, save));
            yield return Hint("Add a type, pick its model, choose Paint and drag in the viewport. Trees with a capsule collider block the player and the navmesh. From scripts: Foliage.SetWind(2f), Foliage.Erase(Foliage.Find(), hit.Point, 5f), Foliage.Paint(...).");
        }

        private static Control NameRow(Editor.ECS.Components.Rendering.Foliage f, FoliageType t)
        {
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            var box = new TextBox { Text = t.Name, MinWidth = 120, MinHeight = 22 };
            box.LostFocus += (s, e) =>
            {
                string v = (box.Text ?? "").Trim();
                if (v.Length == 0 || v == t.Name) return;
                var entity = Editor.Core.Services.SelectionService.Instance.SelectedEntity;
                Editor.Core.Foliage.FoliageData data;
                if (entity != null && FoliageService.TryGetData(entity, out data)) data.RenameLayer(t.Name, v);
                t.Name = v; Touch(f);
            };
            panel.Children.Add(box);
            var remove = new Button { Content = "×", Classes = { "ghost" }, Padding = new Avalonia.Thickness(8, 2) };
            ToolTip.SetTip(remove, "Remove this type and its instances");
            remove.Click += (s, e) =>
            {
                var entity = Editor.Core.Services.SelectionService.Instance.SelectedEntity;
                Editor.Core.Foliage.FoliageData data;
                if (entity != null && FoliageService.TryGetData(entity, out data)) { data.RemoveLayer(t.Name); FoliageService.MarkDirty(entity); FoliageService.Save(entity); }
                f.Types.Remove(t); Touch(f); Refresh();
            };
            panel.Children.Add(remove);
            return panel;
        }

        private static Control ScaleRow(Editor.ECS.Components.Rendering.Foliage f, FoliageType t)
        {
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(FloatBox(() => t.MinScale, v => { t.MinScale = Math.Max(0.01f, v); Touch(f); }, 0.05, 0.01f, 100f, "0.##"));
            panel.Children.Add(new TextBlock { Text = "to", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            panel.Children.Add(FloatBox(() => t.MaxScale, v => { t.MaxScale = Math.Max(0.01f, v); Touch(f); }, 0.05, 0.01f, 100f, "0.##"));
            return panel;
        }

        private static Control CollisionRow(Editor.ECS.Components.Rendering.Foliage f, FoliageType t)
        {
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = "r", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            panel.Children.Add(FloatBox(() => t.CollisionRadius, v => { t.CollisionRadius = Math.Max(0.01f, v); Touch(f); }, 0.05, 0.01f, 50f, "0.##"));
            panel.Children.Add(new TextBlock { Text = "h", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            panel.Children.Add(FloatBox(() => t.CollisionHeight, v => { t.CollisionHeight = Math.Max(0.05f, v); Touch(f); }, 0.5, 0.05f, 200f, "0.##"));
            return panel;
        }

        private static Control WindRow(Editor.ECS.Components.Rendering.Foliage f, FoliageType t)
        {
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(FloatBox(() => t.WindStrength, v => { t.WindStrength = Math.Max(0f, v); Touch(f); }, 0.05, 0f, 10f, "0.##"));
            panel.Children.Add(new TextBlock { Text = "×", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            panel.Children.Add(FloatBox(() => t.WindSpeed, v => { t.WindSpeed = Math.Max(0.1f, v); Touch(f); }, 0.1, 0.1f, 20f, "0.#"));
            panel.Children.Add(new TextBlock { Text = "@", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            panel.Children.Add(FloatBox(() => t.WindHeight, v => { t.WindHeight = Math.Max(0.05f, v); Touch(f); }, 0.5, 0.05f, 99f, "0.#"));
            return panel;
        }

        private static Control TypeChoice(Editor.ECS.Components.Rendering.Foliage f)
        {
            var labels = new List<string> { "Every type" };
            labels.AddRange(f.Types.Select(t => t.Name ?? "?"));
            return Choice(() => FoliageToolService.TypeIndex + 1, v => FoliageToolService.TypeIndex = v - 1, labels.ToArray());
        }

        private static Control DataFileRow(Editor.ECS.Components.Rendering.Foliage f, Editor.ECS.GameEntity entity)
        {
            var tb = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            Func<string> text = () => string.IsNullOrEmpty(f.DataPath) ? "(saved with the first stroke: " + FoliageService.DefaultDataPath(entity) + ")" : f.DataPath + (FoliageService.HasUnsavedChanges(entity) ? "  •" : "");
            tb.Text = text();
            Refreshers[tb] = () => tb.Text = text();
            return tb;
        }

        private static Control CountRow(Editor.ECS.GameEntity entity)
        {
            var tb = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Func<string> text = () => FoliageService.InstanceCount(entity).ToString("N0") + " (drawn last frame: " + FoliageService.LastInstancesDrawn.ToString("N0") + ")";
            tb.Text = text();
            Refreshers[tb] = () => tb.Text = text();
            return tb;
        }

        private static void Touch(Editor.ECS.Components.Rendering.Foliage f)
        {
            f.Touch();
            ComponentEditors.Dirty();
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }

        private static void Refresh()
        {
            ComponentEditors.Dirty();
            foreach (var kv in Refreshers) { try { kv.Value(); } catch { } }
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }
    }
}
