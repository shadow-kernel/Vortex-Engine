using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Editor.Core.Data;
using VortexEditor.Controls;

namespace VortexEditor.Shell
{
    /// <summary>Project asset chooser (by file patterns) with search. Returns a project-relative path.</summary>
    public static class AssetPickerDialog
    {
        public static async Task<string> Pick(string kind, string[] patterns)
        {
            var root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root) || Dialogs.Owner == null) return null;
            var all = new List<string>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (f.Contains(Path.DirectorySeparatorChar + ".") || f.Contains("/obj/") || f.Contains("/bin/") || f.Contains("/.ve/")) continue;
                    if (Panels.Inspector.PropertyRows.Matches(f, patterns)) all.Add(Path.GetRelativePath(root, f).Replace('\\', '/'));
                }
            }
            catch { }
            all.Sort(StringComparer.OrdinalIgnoreCase);

            var win = new Window { Title = "Choose " + kind, Width = 520, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true, ShowInTaskbar = false };
            string result = null;
            var search = new TextBox { Classes = { "search" }, Watermark = "Search " + kind.ToLowerInvariant(), Margin = new Thickness(14, 12, 14, 8) };
            var list = new ListBox { Margin = new Thickness(8, 0) };
            void Fill() { string q = search.Text?.Trim() ?? ""; list.ItemsSource = string.IsNullOrEmpty(q) ? all : all.Where(a => a.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList(); }
            list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((p, _) =>
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                sp.Children.Add(new VxIcon { Icon = IconFor(p) });
                sp.Children.Add(new TextBlock { Text = Path.GetFileName(p), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = Path.GetDirectoryName(p), Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
                return sp;
            });
            search.TextChanged += (s, e) => Fill();
            Fill();
            list.DoubleTapped += (s, e) => { if (list.SelectedItem is string p) { result = p; win.Close(); } };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 8, 14, 12) };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true }; cancel.Click += (s, e) => win.Close();
            var ok = new Button { Content = "Choose", MinWidth = 80, Classes = { "accent" }, IsDefault = true }; ok.Click += (s, e) => { result = list.SelectedItem as string; win.Close(); };
            var none = new Button { Content = "None", MinWidth = 80 }; none.Click += (s, e) => { result = ""; win.Close(); };
            buttons.Children.Add(none); buttons.Children.Add(cancel); buttons.Children.Add(ok);
            var dock = new DockPanel();
            DockPanel.SetDock(search, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(search); dock.Children.Add(buttons); dock.Children.Add(list);
            win.Content = dock;
            win.KeyDown += (s, e) => { if (e.Key == Key.Escape) win.Close(); };
            await win.ShowDialog(Dialogs.Owner);
            return result;
        }

        public static string IconFor(string path)
        {
            switch (Path.GetExtension(path)?.ToLowerInvariant())
            {
                case ".vmat": return "Material";
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".bmp": case ".hdr": case ".dds": return "Image";
                case ".wav": case ".mp3": case ".ogg": case ".flac": case ".vsndc": return "Audio";
                case ".cs": return "Script";
                case ".ventity": return "Prefab";
                case ".vscene": return "Scene";
                case ".vanim": return "Bone";
                case ".fbx": case ".obj": case ".gltf": case ".glb": case ".dae": case ".3ds": case ".blend": case ".vmesh": return "Cube";
                default: return "File";
            }
        }
    }
}
