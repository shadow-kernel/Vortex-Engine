using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VortexEditor.Controls;
using VortexEditor.Services;

namespace VortexEditor.Shell.Material
{
    /// <summary>
    /// One texture map of the material editor (port of the Windows slot): title, a thumbnail that accepts drops from
    /// the Asset Browser or Finder, the file name, Browse… / Clear. Double-click the thumbnail to open the texture in
    /// the Texture Editor; right-click for more. Files dropped from outside the project are copied into Assets/Textures.
    /// </summary>
    internal sealed class TextureSlotCard : Border
    {
        public static readonly string[] Patterns = { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.hdr", "*.dds", "*.exr", "*.gif", "*.webp" };

        private readonly string _title;
        private readonly Func<string> _get;
        private readonly Action<string> _set;
        private readonly Border _thumbBox;
        private readonly Image _image = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
        private readonly StackPanel _placeholder;
        private readonly VxIcon _placeholderIcon = new VxIcon { Icon = "Image", Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock _placeholderText = new TextBlock { Text = "Drop texture", Classes = { "small", "tertiary" }, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock _file = new TextBlock { Classes = { "small", "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Button _clear;
        private string _shown;

        public string Title => _title;

        /// <param name="get">Current absolute path (null = none).</param>
        /// <param name="set">Assign an absolute path (null = clear).</param>
        /// <param name="note">Optional caption under the title (e.g. "stored, not rendered yet").</param>
        public TextureSlotCard(string title, string note, Func<string> get, Action<string> set)
        {
            _title = title; _get = get; _set = set;
            Classes.Add("card");
            Padding = new Thickness(9);
            Margin = new Thickness(0, 0, 8, 8);
            Width = 166;

            var stack = new StackPanel { Spacing = 5 };
            stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12 });
            if (!string.IsNullOrEmpty(note)) stack.Children.Add(new TextBlock { Text = note, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, -3, 0, 0) });

            _placeholder = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { _placeholderIcon, _placeholderText } };
            _thumbBox = new Border
            {
                Height = 116, CornerRadius = new CornerRadius(6), ClipToBounds = true,
                Background = EditorKit.Brush("VxFieldBrush"), BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1),
                Child = new Grid { Children = { _placeholder, _image } }
            };
            ToolTip.SetTip(_thumbBox, "Drop a texture here · double-click to open it in the Texture Editor");
            DragDrop.SetAllowDrop(_thumbBox, true);
            _thumbBox.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
            _thumbBox.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            _thumbBox.AddHandler(DragDrop.DragLeaveEvent, (s, e) => SetDropHighlight(false));
            _thumbBox.AddHandler(DragDrop.DropEvent, OnDrop);
            _thumbBox.DoubleTapped += (s, e) => OpenInTextureEditor();
            _thumbBox.ContextMenu = BuildContextMenu();
            stack.Children.Add(_thumbBox);
            stack.Children.Add(_file);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(EditorKit.SmallButton("Browse…", () => _ = Browse(), "Choose a project texture"));
            _clear = EditorKit.SmallButton("Clear", () => Assign(null), "Remove this map");
            buttons.Children.Add(_clear);
            stack.Children.Add(buttons);
            Child = stack;
            Refresh();
        }

        /// <summary>Re-read the slot (after undo / revert / load).</summary>
        public void Refresh()
        {
            string path = _get();
            _clear.IsEnabled = !string.IsNullOrEmpty(path);
            if (string.Equals(path, _shown, StringComparison.Ordinal) && (_image.IsVisible || string.IsNullOrEmpty(path))) return;
            _shown = path;
            _image.Source = null; _image.IsVisible = false; _placeholder.IsVisible = true;
            if (string.IsNullOrEmpty(path))
            {
                _file.Text = "None"; ToolTip.SetTip(_file, null); _file.Foreground = EditorKit.Brush("VxTextTertiaryBrush");
                _placeholderIcon.Icon = "Image"; _placeholderIcon.Foreground = EditorKit.Brush("VxTextTertiaryBrush");
                _placeholderText.Text = "Drop texture";
                return;
            }
            bool exists = File.Exists(path);
            _file.Text = (exists ? "" : "Missing: ") + Path.GetFileName(path);
            _file.Foreground = exists ? EditorKit.Brush("VxTextSecondaryBrush") : EditorKit.Brush("VxRedBrush");
            ToolTip.SetTip(_file, EditorKit.ToProjectRelative(path));
            _placeholderIcon.Icon = exists ? "Image" : "Warning";
            _placeholderIcon.Foreground = exists ? EditorKit.Brush("VxTealBrush") : EditorKit.Brush("VxRedBrush");
            _placeholderText.Text = exists ? Path.GetExtension(path).TrimStart('.').ToUpperInvariant() : "File not found";
            if (!exists) return;
            string want = path;
            ThumbnailService.Request(path, 256, bmp =>
            {
                if (!string.Equals(_shown, want, StringComparison.Ordinal) || bmp == null) return;
                _image.Source = bmp; _image.IsVisible = true; _placeholder.IsVisible = false;
            });
        }

        private void Assign(string absolutePath)
        {
            _set(string.IsNullOrEmpty(absolutePath) ? null : absolutePath);
            Refresh();
        }

        private async System.Threading.Tasks.Task Browse()
        {
            var r = await AssetPickerDialog.Pick(new AssetPickerOptions { Kind = "Textures", Title = "Choose the " + _title + " texture", Patterns = Patterns, Current = _get(), AllowNone = true });
            if (r == null) return;
            Assign(r.Length == 0 ? null : EditorKit.ToAbsolute(r));
        }

        private string AcceptedFile(DragEventArgs e) => EditorKit.DroppedFiles(e).FirstOrDefault(f => EditorKit.HasExtension(f, Patterns) && File.Exists(f));

        private void OnDragOver(object sender, DragEventArgs e)
        {
            bool ok = AcceptedFile(e) != null;
            e.DragEffects = ok ? DragDropEffects.Link | DragDropEffects.Copy : DragDropEffects.None;
            SetDropHighlight(ok);
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            SetDropHighlight(false);
            var f = AcceptedFile(e);
            if (f == null) return;
            try
            {
                string inProject = EditorKit.ImportIntoProject(f, "Textures");
                if (!ReferenceEquals(inProject, f)) EditorCommands.Toast("Copied " + Path.GetFileName(f) + " into Assets/Textures");
                Assign(inProject);
            }
            catch (Exception ex) { EditorCommands.Fail("Assign texture", ex); }
            e.Handled = true;
        }

        private void SetDropHighlight(bool on)
        {
            _thumbBox.BorderBrush = on ? EditorKit.Brush("VxAccentBrush") : EditorKit.Brush("VxHairlineBrush");
            _thumbBox.BorderThickness = new Thickness(on ? 2 : 1);
        }

        private void OpenInTextureEditor()
        {
            string p = _get();
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) EditorWindows.TextureEditor(p);
        }

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();
            MenuItem Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (s, e) => a(); return mi; }
            menu.Items.Add(Item("Browse…", () => _ = Browse()));
            menu.Items.Add(Item("Open in Texture Editor", OpenInTextureEditor));
            menu.Items.Add(Item("Reveal in Finder", () => { var p = _get(); if (!string.IsNullOrEmpty(p)) EditorCommands.RevealInFinder(p); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Clear", () => Assign(null)));
            return menu;
        }
    }
}
