using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Editor.Core.Services;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Smoke check "workspace layout": with every panel and the Claude sidebar open, the columns sit side by side
    /// without overlapping, the centre keeps its minimum width while the window has room for it, and every control of
    /// the viewport toolbar and the asset browser's header lies inside the centre column — nothing reaches under the
    /// Inspector (3.0.3: the toolbar ran under it as soon as the sidebar opened). Checked at a 1480 px window and at the
    /// window's minimum width (where the toolbar wraps); captures layout_claude_wide.png and layout_claude_min.png.
    /// </summary>
    internal static class WorkspaceLayoutSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("workspace layout", RunAsync);

        private static async Task<bool> RunAsync()
        {
            var w = EditorCommands.Window;
            if (w == null) return false;
            var problems = new List<string>();
            var panels = new[] { MainWindow.PanelHierarchy, MainWindow.PanelFiles, MainWindow.PanelInspector, MainWindow.PanelProject, MainWindow.PanelClaude };
            var hidden = panels.Where(p => !w.IsPanelVisible(p)).ToList();
            var state = w.WindowState;
            double width = w.Width;
            try
            {
                if (w.WindowState != WindowState.Normal) { w.WindowState = WindowState.Normal; await SmokeRegistry.Settle(500); }
                foreach (var p in panels) w.ShowPanel(p);
                w.ShowPanel(MainWindow.PanelInspector);
                w.ShowPanel(MainWindow.PanelProject);

                foreach (var (target, name) in new[] { (1480.0, "wide"), (w.MinWidth, "min") })
                {
                    w.Width = Math.Max(w.MinWidth, target);
                    await SmokeRegistry.Settle(1000)   /* two layout passes: the OS may clamp the window to the work area first */;
                    Check(w, name, problems, expectCentreMin: name == "wide");
                    SmokeRegistry.Capture(w, "layout_claude_" + name + ".png");
                }
            }
            catch (Exception ex) { problems.Add(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                w.Width = width;
                foreach (var p in hidden) if (w.IsPanelVisible(p)) w.TogglePanel(p);
                if (w.WindowState != state) w.WindowState = state;
                await SmokeRegistry.Settle(300);
            }
            foreach (var p in problems) ConsoleService.Instance.LogError("workspace layout: " + p);
            return problems.Count == 0;
        }

        /// <summary>The asset browser's header controls (both rows).</summary>
        private static readonly string[] HeaderControls = { "BackButton", "ForwardButton", "SearchBox", "CreateButton", "ImportButton", "TagButton", "SortButton", "GridModeButton", "ListModeButton", "SizeSlider" };

        private static Rect R(Visual v, Visual to) => new Rect(v.TranslatePoint(new Point(0, 0), to) ?? new Point(-1e4, -1e4), v.Bounds.Size);

        private static void Check(MainWindow w, string when, List<string> problems, bool expectCentreMin)
        {
            Rect Of(Visual v) => R(v, w);
            var left = Of(w.LeftColumn); var centre = Of(w.CenterColumn); var right = Of(w.RightColumn); var claude = Of(w.ClaudeColumn);
            var frame = w.Workspace.Bounds;
            ConsoleService.Instance.Log($"workspace layout {when}: window {w.Bounds.Width:0} px; left {left.X:0}–{left.Right:0}, centre {centre.X:0}–{centre.Right:0}, " +
                                        $"right {right.X:0}–{right.Right:0}, Claude {claude.X:0}–{claude.Right:0}; toolbar " +
                                        (w.ViewportPanel.ToolbarLayout.IsWrapped ? "in two rows" : "in one row"));
            if (!w.ClaudeColumn.IsVisible || claude.Width < 290) problems.Add(when + ": the Claude sidebar is not open (" + claude.Width.ToString("0") + " px)");
            if (left.Right > centre.X + 0.5 || centre.Right > right.X + 0.5 || right.Right > claude.X + 0.5) problems.Add(when + ": the columns overlap");
            if (claude.Right > frame.Right + 0.5) problems.Add(when + ": the Claude sidebar reaches past the window");
            if (expectCentreMin && centre.Width < 459) problems.Add(when + ": the centre is only " + centre.Width.ToString("0") + " px wide");

            // every control of the viewport toolbar and of the asset browser's header row stays inside the centre column
            var controls = w.ViewportPanel.Toolbar.GetVisualDescendants().OfType<Control>()
                .Where(c => c is Button || c is ToggleButton || c is ComboBox || c is RadioButton)
                .Concat(w.AssetBrowser.GetVisualDescendants().OfType<Control>().Where(c => HeaderControls.Contains(c.Name)))
                .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0);
            foreach (var c in controls)
            {
                var r = Of(c);
                if (r.X < centre.X - 0.5 || r.Right > centre.Right + 0.5)
                    problems.Add(when + ": " + (c.Name ?? ToolTip.GetTip(c) as string ?? c.GetType().Name) + " at " + r.X.ToString("0") + "–" + r.Right.ToString("0") +
                                 " leaves the centre column (" + centre.X.ToString("0") + "–" + centre.Right.ToString("0") + ")");
            }
        }
    }
}
