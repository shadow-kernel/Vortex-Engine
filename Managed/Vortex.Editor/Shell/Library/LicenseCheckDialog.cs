using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Assets.Library;
using VortexEditor.Controls;
using VortexEditor.Panels;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell.Library
{
    /// <summary>
    /// The license check before a build (#77): every asset whose license needs a decision — NonCommercial,
    /// NoDerivatives, ShareAlike / GPL, unknown — grouped, each with a Show link that cancels the build and jumps to the
    /// asset in the Asset Browser. ShareAlike / GPL assets are viral, so "Build Anyway" stays disabled until the user
    /// confirms that explicitly. CC-BY assets need no decision: they are credited in CREDITS.md automatically.
    /// </summary>
    public sealed class LicenseCheckDialog : Window
    {
        private bool _build;

        /// <summary>"Build Anyway" (disabled until ShareAlike / GPL assets are confirmed) — the smoke check reads it.</summary>
        internal readonly Button BuildButton;
        /// <summary>The ShareAlike / GPL confirmation; null when there are no such assets.</summary>
        internal readonly CheckBox ViralBox;

        /// <summary>True = build anyway.</summary>
        public static async Task<bool> Run(LicenseAuditReport report, string projectRoot)
        {
            var d = new LicenseCheckDialog(report, projectRoot);
            await LibraryUi.ShowModal(d);
            return d._build;
        }

        internal LicenseCheckDialog(LicenseAuditReport r, string projectRoot)
        {
            Title = "License Check";
            Width = 640; Height = 560; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 14), Spacing = 4 };
            stack.Children.Add(LibraryUi.Title("License check"));
            stack.Children.Add(LibraryUi.Para("Some assets in this build have licenses that need a decision before you ship. CC-BY assets are fine — they are credited automatically in CREDITS.md."));
            Group(stack, "NonCommercial", "Not allowed in a game you sell — replace them or keep the game free.", r.NonCommercial, projectRoot, "VxRedBrush");
            Group(stack, "NoDerivatives", "May not be modified: no re-texturing, cutting or re-rigging.", r.NoDerivatives, projectRoot, "VxOrangeBrush");
            Group(stack, "ShareAlike / GPL", "Viral: what you build from them must be released under the same license.", r.ShareAlike, projectRoot, "VxOrangeBrush");
            Group(stack, "Unknown license", "Check the source before shipping.", r.Unknown, projectRoot, "VxRedBrush");

            var cancel = Ui.Button("Cancel", Close, null, null, 84);
            var build = BuildButton = Ui.Button("Build Anyway", () => { _build = true; Close(); }, null, "accent", 120);
            if (r.ShareAlike.Count > 0)
            {
                var viral = ViralBox = new CheckBox { Content = "I will release the derived work under the same license (ShareAlike / GPL)", Margin = new Thickness(0, 12, 0, 0) };
                viral.IsCheckedChanged += (s, e) => build.IsEnabled = viral.IsChecked == true;
                build.IsEnabled = false;
                stack.Children.Add(viral);
            }
            Content = LibraryUi.Layout(stack, LibraryUi.Footer(cancel, build));
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private void Group(StackPanel stack, string title, string why, List<LicensedAsset> assets, string projectRoot, string brush)
        {
            if (assets.Count == 0) return;
            stack.Children.Add(new TextBlock { Text = title + " (" + assets.Count + ")", FontWeight = FontWeight.SemiBold, Foreground = EditorKit.Brush(brush), Margin = new Thickness(0, 12, 0, 0) });
            stack.Children.Add(LibraryUi.Small(why));
            foreach (var a in assets)
            {
                string full = Path.Combine(projectRoot, a.RelPath.Replace('/', Path.DirectorySeparatorChar));
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = a.RelPath, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = a.License + (string.IsNullOrEmpty(a.Author) ? "" : " · " + a.Author) + (string.IsNullOrEmpty(a.Source) ? "" : " · " + a.Source), Classes = { "small", "tertiary" }, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(text);
                var show = Ui.Button("Show", () => JumpTo(full), "Cancel the build and show this asset in the Asset Browser");
                show.Classes.Add("ghost");
                show.VerticalAlignment = VerticalAlignment.Center;
                show.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(show, 1);
                row.Children.Add(show);
                stack.Children.Add(row);
            }
        }

        internal void JumpTo(string fullPath)
        {
            _build = false;
            Close();
            var w = EditorCommands.Window;
            if (w != null && !w.IsPanelVisible(MainWindow.PanelProject)) w.TogglePanel(MainWindow.PanelProject);
            AssetBrowserPanel.Current?.Reveal(fullPath);
        }
    }
}
