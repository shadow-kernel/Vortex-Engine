using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Editor.Core.Areas;
using Editor.Core.Data;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>The Area (zone) component in the inspector: shape and size, the tags scripts ask for, free properties, the
    /// outline colour, the priority, and the <c>.varea</c> file it can take its values from or be saved to.</summary>
    internal static class AreaCard
    {
        [ModuleInitializer]
        internal static void Register()
        {
            ComponentEditors.Custom[typeof(Editor.ECS.Components.World.Area)] = (c, e) => Rows((Editor.ECS.Components.World.Area)c, e);
        }

        private static IEnumerable<Control> Rows(Editor.ECS.Components.World.Area a, Editor.ECS.GameEntity entity)
        {
            yield return Row("Shape", Choice(() => a.Shape, v => { a.Shape = v; Touch(); }, "Box", "Sphere", "Cylinder"), "A box (width / height / depth), a sphere (diameter = Size X) or a vertical cylinder (diameter = Size X, height = Size Y); turned and scaled by the entity's transform");
            yield return Row("Size X", FloatBox(() => a.SizeX, v => { a.SizeX = v; Touch(); }, 1, 0.1f, 100000f, "0.#"), "Metres before the transform's scale");
            yield return Row("Size Y", FloatBox(() => a.SizeY, v => { a.SizeY = v; Touch(); }, 1, 0.1f, 100000f, "0.#"), null);
            yield return Row("Size Z", FloatBox(() => a.SizeZ, v => { a.SizeZ = v; Touch(); }, 1, 0.1f, 100000f, "0.#"), null);
            yield return Row("Tags", TextRow(() => a.Tags, v => { a.Tags = v; Touch(); }, "cave, no-fly"), "What scripts ask for: Areas.IsInside(id, \"cave\"), OnAreaEnter(a) with a.HasTag(\"no-fly\"). Comma-separated, case does not matter.");
            yield return Row("Properties", TextRow(() => a.Properties, v => { a.Properties = v; Touch(); }, "flightAllowed=false", multiline: true), "Free key=value lines a script reads: a.GetBool(\"flightAllowed\"), a.Get(\"ambient\"), a.GetFloat(\"gravity\")");
            yield return Row("Colour", Color(() => (a.ColorR, a.ColorG, a.ColorB), (r, g, b) => { a.ColorR = r; a.ColorG = g; a.ColorB = b; Touch(); }), "The outline in the viewport");
            yield return Row("Priority", FloatBox(() => a.Priority, v => { a.Priority = (int)Math.Round(v); Touch(); }, 1, -1000f, 1000f, "0"), "Higher wins when areas overlap and a script asks for THE area at a point (Areas.Current)");
            yield return Row("Track all", Bool(() => a.TrackAll, v => { a.TrackAll = v; Touch(); }), "Every scripted entity is tracked; off = only entities a script registers with Areas.Track");
            yield return Row("Draw in game", Bool(() => a.DrawInGame, v => { a.DrawInGame = v; Touch(); }), "Show the outline in play mode too");
            yield return Row("File", FileRow(a, entity), "A .varea file the area takes its values from (shared by many areas). Load reads it into the fields above; Save as… writes the fields to a file and links it.");
            yield return Hint("Select the entity and move, turn or scale it in the viewport like anything else — the area follows. Events: OnAreaEnter / OnAreaStay(dt) / OnAreaExit on the entity's behaviours, Areas.Entered / Exited for a manager, Areas.IsInside / Of / At / Find to ask.");
        }

        private static Control TextRow(Func<string> get, Action<string> set, string watermark, bool multiline = false)
        {
            var box = new TextBox { Text = get() ?? "", Watermark = watermark, FontSize = 11, MinHeight = multiline ? 54 : 22, AcceptsReturn = multiline, TextWrapping = multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap };
            void Commit() { var v = box.Text ?? ""; if (v != (get() ?? "")) set(v); }
            box.LostFocus += (s, e) => Commit();
            box.KeyDown += (s, e) => { if (!multiline && e.Key == Avalonia.Input.Key.Return) { Commit(); e.Handled = true; } };
            Refreshers[box] = () => { if (!box.IsFocused) box.Text = get() ?? ""; };
            return box;
        }

        private static Control FileRow(Editor.ECS.Components.World.Area a, Editor.ECS.GameEntity entity)
        {
            var panel = new StackPanel { Spacing = 4 };
            var path = new TextBox { Text = a.DataPath ?? "", Watermark = "Assets/Areas/Cave.varea", FontSize = 11, MinHeight = 22 };
            path.LostFocus += (s, e) => { var v = path.Text ?? ""; if (v != (a.DataPath ?? "")) { a.DataPath = v; Touch(); } };
            Refreshers[path] = () => { if (!path.IsFocused) path.Text = a.DataPath ?? ""; };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var load = new Button { Content = "Load", FontSize = 11, Padding = new Thickness(8, 2) };
            ToolTip.SetTip(load, "Read the linked .varea into the fields (the file stays linked)");
            load.Click += (s, e) =>
            {
                try
                {
                    var d = a.Resolve(ProjectData.Current?.Path);
                    if (d != null) { a.ApplyDefinition(d); Touch(); }
                }
                catch (Exception ex) { VortexEditor.Shell.EditorCommands.Fail("Load area", ex); }
            };
            var save = new Button { Content = "Save as…", FontSize = 11, Padding = new Thickness(8, 2) };
            ToolTip.SetTip(save, "Write the fields to a .varea file in Assets/Areas and link it");
            save.Click += (s, e) =>
            {
                try
                {
                    var project = ProjectData.Current; if (project == null) return;
                    string name = string.IsNullOrWhiteSpace(entity?.Name) ? "Area" : entity.Name;
                    foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
                    string rel = System.IO.Path.Combine("Assets", "Areas", name + AreaDefinition.Extension).Replace('\\', '/');
                    string full = System.IO.Path.Combine(project.Path, rel);
                    var d = new AreaDefinition { Name = name, Shape = (AreaShape)a.Shape, SizeX = a.SizeX, SizeY = a.SizeY, SizeZ = a.SizeZ, ColorR = a.ColorR, ColorG = a.ColorG, ColorB = a.ColorB, Priority = a.Priority };
                    foreach (var t in AreaDefinition.ParseTags(a.Tags)) d.Tags.Add(t);
                    foreach (var kv in AreaDefinition.ParseProperties(a.Properties)) d.Properties[kv.Key] = kv.Value;
                    d.Save(full);
                    a.DataPath = rel; Touch();
                }
                catch (Exception ex) { VortexEditor.Shell.EditorCommands.Fail("Save area", ex); }
            };
            var unlink = new Button { Content = "Unlink", FontSize = 11, Padding = new Thickness(8, 2) };
            ToolTip.SetTip(unlink, "Keep the fields as this area's own values, forget the file");
            unlink.Click += (s, e) => { if (!string.IsNullOrEmpty(a.DataPath)) { a.DataPath = ""; Touch(); } };
            buttons.Children.Add(load); buttons.Children.Add(save); buttons.Children.Add(unlink);
            panel.Children.Add(path); panel.Children.Add(buttons);
            return panel;
        }

        private static void Touch()
        {
            ComponentEditors.Dirty();
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }
    }
}
