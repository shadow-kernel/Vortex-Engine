using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Editor.Core.Data;
using Editor.Core.Services.Rendering;
using VortexEditor.Controls;
using VortexEditor.Services;

namespace VortexEditor.Shell.ModelTools
{
    /// <summary>Small UI vocabulary shared by the model / texture / import / stress windows (theme tokens only).</summary>
    internal static class Ui
    {
        public static IBrush Brush(string key)
            => Application.Current != null && Application.Current.TryFindResource(key, Application.Current.ActualThemeVariant, out var r) && r is IBrush b ? b : Brushes.Gray;

        public static readonly IBrush PreviewBg = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18));

        /// <summary>Uppercase section header (the Windows editor's "SUBMESHES" / "TEXTURE MAPS" style).</summary>
        public static TextBlock Header(string text, Thickness? margin = null)
            => new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Brush("VxTextSecondaryBrush"), Margin = margin ?? new Thickness(0, 12, 0, 6), LetterSpacing = 0.4 };

        /// <summary>A header bar that separates the regions of a side panel.</summary>
        public static Border HeaderBar(string text, Control right = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            g.Children.Add(new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Brush("VxTextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center, LetterSpacing = 0.4 });
            if (right != null) { Grid.SetColumn(right, 1); g.Children.Add(right); }
            return new Border { Background = Brush("VxToolbarBrush"), BorderBrush = Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(12, 6), Child = g };
        }

        public static TextBlock Small(string text, string brush = "VxTextSecondaryBrush")
            => new TextBlock { Text = text, FontSize = 11, Foreground = Brush(brush), TextTrimming = TextTrimming.CharacterEllipsis };

        public static TextBlock Muted(string text)
            => new TextBlock { Text = text, FontStyle = FontStyle.Italic, Foreground = Brush("VxTextTertiaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };

        public static Button Button(string text, Action click, string tip = null, string cls = null, double minWidth = 0)
        {
            var b = new Button { Content = text, MinWidth = minWidth };
            if (!string.IsNullOrEmpty(cls)) foreach (var c in cls.Split(' ')) b.Classes.Add(c);
            if (tip != null) ToolTip.SetTip(b, tip);
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { EditorCommands.Fail(text, ex); } };
            return b;
        }

        public static Button IconButton(string icon, string tip, Action click)
        {
            var b = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = icon } };
            ToolTip.SetTip(b, tip);
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { EditorCommands.Fail(tip, ex); } };
            return b;
        }

        /// <summary>A list card: leading visual + title + subtitle (the Windows model editor's submesh / material cards).</summary>
        public static Control Card(Control leading, string title, string subtitle, IBrush subtitleBrush = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 3) };
            if (leading != null) { leading.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(leading); }
            var sp = new StackPanel { Margin = new Thickness(leading != null ? 10 : 0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(subtitle)) sp.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = subtitleBrush ?? Brush("VxTextSecondaryBrush"), Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            return g;
        }

        /// <summary>Rounded accent chip with a glyph (submesh cards).</summary>
        public static Border Chip(string icon)
            => new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Background = Brush("VxAccentSoftBrush"), Child = new VxIcon { Icon = icon, Width = 15, Height = 15, Foreground = Brush("VxAccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };

        /// <summary>Round colour swatch (material cards).</summary>
        public static Border Swatch(float[] rgba, double size = 30)
            => new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), Background = new SolidColorBrush(ToColor(rgba)) };

        public static Color ToColor(float[] rgba)
        {
            byte B(float v) => (byte)Math.Max(0, Math.Min(255, (int)(v * 255f + 0.5f)));
            if (rgba == null || rgba.Length < 3) return Colors.White;
            return Color.FromArgb(rgba.Length > 3 ? B(rgba[3]) : (byte)255, B(rgba[0]), B(rgba[1]), B(rgba[2]));
        }

        /// <summary>A texture thumbnail (async decode through the ThumbnailService); placeholder text when there is none.</summary>
        public static Border Thumb(string path, double w, double h, string emptyText = null)
        {
            var host = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(5), ClipToBounds = true, Background = Brush("VxFieldBrush"), BorderBrush = Brush("VxHairlineBrush"), BorderThickness = new Thickness(1) };
            var img = new Image { Stretch = Stretch.UniformToFill };
            var label = new TextBlock { Text = emptyText ?? "", FontSize = 10, Foreground = Brush("VxTextTertiaryBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var g = new Grid(); g.Children.Add(label); g.Children.Add(img);
            host.Child = g;
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && ThumbnailService.KindOf(path) == ThumbnailService.Kind.Image)
            {
                label.Text = "…";
                ThumbnailService.Request(path, 128, bmp => { img.Source = bmp; label.Text = ""; });
            }
            else if (!string.IsNullOrEmpty(path) && File.Exists(path)) label.Text = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            return host;
        }

        /// <summary>WASD / QE keyboard navigation for a preview (the Windows model viewer's keys): W/S zoom, A/D orbit, Q/E tilt.</summary>
        public static void AddKeyboardNavigation(PreviewViewport pv)
        {
            pv.KeyDown += (s, e) =>
            {
                var c = pv.Camera;
                float ds = c.DistScale <= 0 ? 1f : c.DistScale;
                switch (e.Key)
                {
                    case Key.W: c.DistScale = Math.Max(0.05f, ds * 0.9f); break;
                    case Key.S: c.DistScale = Math.Min(12f, ds * 1.1f); break;
                    case Key.A: c.Yaw -= 0.12f; break;
                    case Key.D: c.Yaw += 0.12f; break;
                    case Key.Q: c.Pitch = Math.Max(-1.5f, c.Pitch - 0.12f); break;
                    case Key.E: c.Pitch = Math.Min(1.5f, c.Pitch + 0.12f); break;
                    case Key.F: case Key.Home: pv.ResetView(); e.Handled = true; return;
                    default: return;
                }
                pv.Camera = c;
                e.Handled = true;
            };
            pv.PointerPressed += (s, e) => pv.Focus();
        }

        /// <summary>An on/off toolbar toggle that reads as a button (outlined; accent-filled when on).</summary>
        public static ToggleButton Toggle(string text, string tip, Action<bool> changed, bool initial = false)
        {
            var t = new ToggleButton { Content = text, IsChecked = initial, Classes = { "accentcheck" }, BorderThickness = new Thickness(1), BorderBrush = Brush("VxControlBorderBrush"), Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center };
            if (tip != null) ToolTip.SetTip(t, tip);
            t.IsCheckedChanged += (s, e) => changed?.Invoke(t.IsChecked == true);
            return t;
        }

        public static ToggleButton TurntableToggle(PreviewViewport pv) => Toggle("Turntable", "Slowly spin the model", on => pv.AutoRotate = on ? 0.5f : 0f);

        /// <summary>Toolbar above a 3D preview: title, navigation hint (trimmed when narrow), extras, turntable, Reset View.</summary>
        public static Border PreviewToolbar(string title, PreviewViewport pv, params Control[] extras)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            g.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis });
            var hint = new TextBlock { Text = "Drag: orbit · Right-drag: pan · Wheel: zoom · WASD/QE: navigate", FontSize = 11, Foreground = Brush("VxTextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 10, 0) };
            Grid.SetColumn(hint, 1); g.Children.Add(hint);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            foreach (var x in extras) if (x != null) right.Children.Add(x);
            right.Children.Add(TurntableToggle(pv));
            right.Children.Add(Button("Reset View", () => { pv.ResetView(); pv.Focus(); }, "Frame the whole model again (F / double-click)"));
            Grid.SetColumn(right, 2); g.Children.Add(right);
            return new Border { Background = Brush("VxToolbarBrush"), BorderBrush = Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 6), Child = g };
        }

        /// <summary>Overlay message centred over a preview ("Could not load this model.").</summary>
        public static TextBlock Overlay(string text)
            => new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(200, 140, 140)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, TextAlignment = TextAlignment.Center };

        // ------------------------------------------------------------------ paths / pickers / drops

        public static string ProjectRoot => ProjectData.Current?.Path;

        public static string ProjectRelative(string abs)
        {
            var root = ProjectRoot;
            if (string.IsNullOrEmpty(abs) || string.IsNullOrEmpty(root)) return abs;
            try
            {
                var full = Path.GetFullPath(abs);
                var r = Path.GetFullPath(root).TrimEnd('/', '\\');
                if (full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return full.Substring(r.Length + 1).Replace('\\', '/');
            }
            catch { }
            return abs;
        }

        /// <summary>Absolute path from a dropped / picked value (absolute, or project-relative "vortex/asset" drags).</summary>
        public static string Absolute(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            if (Path.IsPathRooted(p)) return p;
            var root = ProjectRoot;
            return string.IsNullOrEmpty(root) ? p : Path.GetFullPath(Path.Combine(root, p.Replace('\\', '/')));
        }

        public static readonly string[] ImagePatterns = { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.dds", "*.hdr", "*.gif", "*.webp" };

        public static bool IsImage(string path) => Panels.Inspector.PropertyRows.Matches(path ?? "", ImagePatterns);

        /// <summary>OS file picker for one image, starting in <paramref name="startDir"/>. Null when cancelled.</summary>
        public static async Task<string> PickImage(Visual owner, string title, string startDir)
        {
            var top = TopLevel.GetTopLevel(owner);
            if (top == null) return null;
            var opts = new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = ImagePatterns }, FilePickerFileTypes.All }
            };
            try { if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir)) opts.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(startDir); } catch { }
            var files = await top.StorageProvider.OpenFilePickerAsync(opts);
            return files?.FirstOrDefault()?.TryGetLocalPath();
        }

        /// <summary>Accept dropped images (OS files or Asset Browser drags) on a control.</summary>
        public static void AcceptImageDrop(Control target, Action<string> dropped)
        {
            DragDrop.SetAllowDrop(target, true);
            target.AddHandler(DragDrop.DragOverEvent, (s, e) =>
            {
                var p = Panels.Inspector.PropertyRows.DroppedPath(e, "vortex/asset");
                e.DragEffects = p != null && IsImage(p) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            });
            target.AddHandler(DragDrop.DropEvent, (s, e) =>
            {
                var p = Panels.Inspector.PropertyRows.DroppedPath(e, "vortex/asset");
                if (p != null && IsImage(p)) { dropped(Absolute(p)); e.Handled = true; }
            });
        }

        /// <summary>Start dragging a file (as an Asset Browser drag: project-relative when inside the project).</summary>
        public static async void BeginFileDrag(PointerEventArgs e, string absPath)
        {
            try
            {
                var data = new DataObject();
                data.Set("vortex/asset", ProjectRelative(absPath));
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Link);
            }
            catch { }
        }

        /// <summary>
        /// Call right before freeing engine meshes a preview drew: the renderer caches its draw runs with raw mesh
        /// pointers and an empty swap keeps the last queue, so clear the queues (after a GPU idle) and let the
        /// viewports re-submit their scenes next frame. Otherwise the next frame can touch the freed meshes.
        /// </summary>
        public static void DropQueuedDraws()
        {
            try { Editor.DllWrapper.VortexAPI.OnSceneSwitch(); } catch { }
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
            try { Editor.Core.Services.SceneRenderService.RuntimeDirty = true; } catch { }
        }

        /// <summary>True when the rendered preview has more than a few distinct colours (something was drawn).</summary>
        public static bool HasContent(PreviewImage img)
        {
            if (img == null || img.Bgra == null) return false;
            var seen = new HashSet<int>();
            for (int y = 0; y < img.Height; y += Math.Max(1, img.Height / 16))
                for (int x = 0; x < img.Width; x += Math.Max(1, img.Width / 16))
                {
                    int o = y * img.Stride + x * 4;
                    seen.Add((img.Bgra[o] >> 3) | ((img.Bgra[o + 1] >> 3) << 5) | ((img.Bgra[o + 2] >> 3) << 10));
                }
            return seen.Count > 3;
        }
    }
}
