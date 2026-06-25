using System;
using System.Reflection;
using AvalonDock.Themes;

namespace Editor.Editors.WorldEditor.Themes
{
    /// <summary>
    /// AvalonDock theme that uses our own <c>DarkDockTheme.xaml</c> as the BASE theme
    /// dictionary. Without this, the DockingManager falls back to AvalonDock's light
    /// <c>GenericTheme</c> for every control type we did not explicitly re-template
    /// (document pane border, auto-hide flyout, drag overlay, drop targets) — which is
    /// the source of the white hairline around the Scene document pane.
    ///
    /// Dirkster.AvalonDock 4.72.1 ships no <c>Vs2013DarkTheme</c> and its
    /// <c>DictionaryTheme</c> is abstract, so a concrete <see cref="Theme"/> subclass is
    /// the supported way to point the manager at a custom dark dictionary.
    /// </summary>
    public sealed class DarkAvalonTheme : Theme
    {
        public override Uri GetResourceUri()
        {
            // Build the pack URI from the real assembly name ("Vortex Engine") so the
            // space in the name is resolved correctly.
            string assembly = Assembly.GetExecutingAssembly().GetName().Name;
            return new Uri(
                $"/{assembly};component/Editors/WorldEditor/Themes/DarkDockTheme.xaml",
                UriKind.Relative);
        }
    }
}
