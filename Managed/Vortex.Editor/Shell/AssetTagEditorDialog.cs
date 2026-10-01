using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Assets;
using Editor.Core.Serialization;
using VortexEditor.Controls;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Edit the tags of one asset (port of the Windows editor's AssetTagEditorDialog): current tags as removable chips,
    /// a field to add a tag (with suggestions from the project's tags), quick-add buttons for the predefined tags,
    /// Cancel / Save. Tags are stored in the project's tag index (Library/AssetTags.xml, <see cref="AssetTagService"/>)
    /// AND in the asset's .vmeta, keyed by the asset's stable GUID — the asset pickers filter on them.
    /// </summary>
    public sealed class AssetTagEditorDialog : Window
    {
        /// <summary>The dialog that is open right now (tests / tools).</summary>
        public static AssetTagEditorDialog Current { get; private set; }

        /// <summary>Raised after an asset's tags were saved (absolute path) — browsers / pickers refresh their filters.</summary>
        public static event Action<string> TagsChanged;

        /// <summary>Open the tag editor for an asset (modal to the active window).</summary>
        public static void Open(string fullPath) => _ = Edit(fullPath);

        /// <summary>Edit an asset's tags; returns the saved tags, or null when cancelled.</summary>
        public static async Task<IReadOnlyList<string>> Edit(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) { EditorCommands.Toast("Tags can only be set on asset files"); return null; }
            var dlg = new AssetTagEditorDialog(fullPath);
            var owner = EditorKit.ActiveWindow();
            Current = dlg;
            try
            {
                if (owner != null) await dlg.ShowDialog(owner);
                else { var tcs = new TaskCompletionSource<bool>(); dlg.Closed += (s, e) => tcs.TrySetResult(true); dlg.Show(); await tcs.Task; }
            }
            finally { if (ReferenceEquals(Current, dlg)) Current = null; }
            return dlg._saved ? dlg._tags.ToList() : null;
        }

        // ---------------------------------------------------------------- tag storage (shared with the pickers)
        /// <summary>The asset's tags: its .vmeta tags merged with the project tag index.</summary>
        public static IReadOnlyList<string> GetTags(string fullPath)
        {
            var meta = FindMeta(fullPath, create: false);
            if (meta == null) return Array.Empty<string>();
            var tags = new List<string>();
            foreach (var t in meta.Tags ?? new List<string>()) if (!string.IsNullOrWhiteSpace(t) && !tags.Contains(t, StringComparer.OrdinalIgnoreCase)) tags.Add(t.Trim());
            try { foreach (var t in AssetTagService.Instance.GetTags(meta.Guid)) if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase)) tags.Add(t); } catch { }
            return tags;
        }

        /// <summary>Replace an asset's tags (tag index + .vmeta). Creates the .vmeta when the asset has none yet.</summary>
        public static bool SaveTags(string fullPath, IEnumerable<string> tags)
        {
            var meta = FindMeta(fullPath, create: true);
            if (meta == null) return false;
            var list = (tags ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            try { AssetTagService.Instance.SetTags(meta.Guid, list); } catch { }
            meta.Tags = list;
            try { AssetDatabase.Instance.SaveMetadata(meta, fullPath + AssetDatabase.MetaFileExtension); } catch { return false; }
            try { TagsChanged?.Invoke(fullPath); } catch { }
            return true;
        }

        private static AssetMetadata FindMeta(string fullPath, bool create)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            string rel = EditorKit.ToProjectRelative(fullPath);
            AssetMetadata meta = null;
            try { meta = AssetDatabase.Instance.GetAssetByPath(rel); } catch { }
            if (meta != null) return meta;
            string metaPath = fullPath + AssetDatabase.MetaFileExtension;
            if (File.Exists(metaPath)) { try { meta = DataSerializer.LoadFromJson<AssetMetadata>(metaPath); } catch { meta = null; } }
            if (meta != null || !create) return meta;
            // No metadata yet (file added after the database scan): register this one file — writes its .vmeta.
            try { if (EditorKit.IsInsideProject(fullPath)) meta = AssetDatabase.Instance.ImportAsset(fullPath, rel); } catch { meta = null; }
            return meta;
        }

        // ---------------------------------------------------------------- instance
        private readonly string _path;
        private readonly List<string> _tags;
        private bool _saved;
        private readonly WrapPanel _current = new WrapPanel();
        private readonly WrapPanel _quick = new WrapPanel();
        private readonly WrapPanel _suggest = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBox _newTag = new TextBox { Watermark = "New tag" };

        /// <summary>The tags as currently edited (tests / tools).</summary>
        public IReadOnlyList<string> Tags => _tags;

        public AssetTagEditorDialog() : this(null) { }

        public AssetTagEditorDialog(string fullPath)
        {
            _path = fullPath;
            _tags = fullPath == null ? new List<string>() : GetTags(fullPath).ToList();
            Title = "Edit Tags";
            Width = 470; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 4 };
            root.Children.Add(new TextBlock { Text = "Asset", Classes = { "small", "tertiary" } });
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new VxIcon { Icon = AssetPickerDialog.IconFor(fullPath ?? ""), Width = 18, Height = 18, Foreground = EditorKit.Brush(FileIconBrushConverter.BrushKeyFor(fullPath ?? "", false)), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = Path.GetFileName(fullPath ?? ""), FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(head);
            root.Children.Add(new TextBlock { Text = EditorKit.ToProjectRelative(fullPath ?? ""), Classes = { "small", "tertiary" }, Margin = new Thickness(0, 0, 0, 8) });

            root.Children.Add(new TextBlock { Text = "Current tags", Classes = { "small", "secondary" }, Margin = new Thickness(0, 6, 0, 2) });
            root.Children.Add(new Border { Background = EditorKit.Brush("VxFieldBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 8, 2, 2), MinHeight = 44, Child = _current });

            root.Children.Add(new TextBlock { Text = "Add tag", Classes = { "small", "secondary" }, Margin = new Thickness(0, 12, 0, 2) });
            var addRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            _newTag.KeyDown += (s, e) => { if (e.Key == Key.Return) { AddFromBox(); e.Handled = true; } };
            _newTag.TextChanged += (s, e) => BuildSuggestions();
            addRow.Children.Add(_newTag);
            var add = new Button { Content = "Add", MinWidth = 64, Margin = new Thickness(8, 0, 0, 0) };
            add.Click += (s, e) => AddFromBox();
            Grid.SetColumn(add, 1); addRow.Children.Add(add);
            root.Children.Add(addRow);
            root.Children.Add(_suggest);

            root.Children.Add(new TextBlock { Text = "Quick add", Classes = { "small", "secondary" }, Margin = new Thickness(0, 12, 0, 4) });
            root.Children.Add(_quick);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var cancel = new Button { Content = "Cancel", MinWidth = 84, IsCancel = true };
            cancel.Click += (s, e) => CancelEdit();
            var save = new Button { Content = "Save", MinWidth = 84, Classes = { "accent" } };
            save.Click += (s, e) => Save();
            buttons.Children.Add(cancel); buttons.Children.Add(save);
            root.Children.Add(buttons);
            Content = root;

            Rebuild();
            Opened += (s, e) => _newTag.Focus();
        }

        public void AddTag(string tag)
        {
            tag = tag?.Trim();
            if (string.IsNullOrEmpty(tag) || _tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;
            _tags.Add(tag);
            Rebuild();
        }

        public void RemoveTag(string tag)
        {
            _tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            Rebuild();
        }

        /// <summary>Save the tags and close (the Save button).</summary>
        public void Save()
        {
            if (!string.IsNullOrWhiteSpace(_newTag.Text)) AddFromBox();   // a typed-but-not-added tag counts
            if (_path != null && !SaveTags(_path, _tags)) { EditorCommands.Toast("Could not save the tags"); return; }
            _saved = true;
            Close();
        }

        public void CancelEdit() { _saved = false; Close(); }

        private void AddFromBox()
        {
            var t = _newTag.Text?.Trim();
            if (string.IsNullOrEmpty(t)) return;
            AddTag(t);
            _newTag.Text = "";
            _newTag.Focus();
        }

        private void Rebuild()
        {
            _current.Children.Clear();
            if (_tags.Count == 0)
                _current.Children.Add(new TextBlock { Text = "No tags yet — add one below.", Classes = { "tertiary" }, Margin = new Thickness(2, 2, 0, 8) });
            foreach (var t in _tags) { string tag = t; _current.Children.Add(EditorKit.Chip(tag, () => RemoveTag(tag))); }

            _quick.Children.Clear();
            var projectTags = new List<string>();
            try { projectTags = AssetTagService.Instance.AllTags.Where(t => !AssetTagService.PredefinedTags.Contains(t, StringComparer.OrdinalIgnoreCase)).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(); } catch { }
            foreach (var t in AssetTagService.PredefinedTags.Concat(projectTags))
            {
                string tag = t;
                bool has = _tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
                var b = new Button { Content = "+ " + tag, Padding = new Thickness(8, 2), MinHeight = 22, FontSize = 12, Margin = new Thickness(0, 0, 6, 6), IsEnabled = !has };
                ToolTip.SetTip(b, has ? "Already tagged" : "Add \"" + tag + "\"");
                b.Click += (s, e) => AddTag(tag);
                _quick.Children.Add(b);
            }
            BuildSuggestions();
        }

        private void BuildSuggestions()
        {
            _suggest.Children.Clear();
            string q = _newTag.Text?.Trim() ?? "";
            if (q.Length == 0) { _suggest.IsVisible = false; return; }
            IEnumerable<string> hits = Array.Empty<string>();
            try { hits = AssetTagService.Instance.SearchTags(q).Where(t => !_tags.Contains(t, StringComparer.OrdinalIgnoreCase)).Take(8).ToList(); } catch { }
            foreach (var t in hits)
            {
                string tag = t;
                var b = new Button { Content = tag, Classes = { "ghost" }, Padding = new Thickness(8, 1), FontSize = 12, Margin = new Thickness(0, 0, 4, 0) };
                b.Click += (s, e) => { AddTag(tag); _newTag.Text = ""; };
                _suggest.Children.Add(b);
            }
            _suggest.IsVisible = _suggest.Children.Count > 0;
        }
    }
}
