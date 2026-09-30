using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using VortexEditor.Controls;
using VortexEditor.Panels.Inspector;
using VortexEditor.Shell;
using VortexEditor.Shell.Prefab;
using Transform = Editor.ECS.Components.Transform;
using Component = Editor.ECS.Component;

namespace VortexEditor.Panels
{
    /// <summary>
    /// The inspector (port of the Windows editor's DynamicInspectorView): entity header (active, name, tag, layer,
    /// static, entity actions), the prefab-instance bar (open / select / apply / revert / unpack + live override
    /// count), one card per component (enable, collapse, reset, copy / paste values, move up / down, remove — all
    /// undoable in the scene) whose rows come from <see cref="ComponentEditors.Build"/>, the searchable Add Component
    /// catalog and script / audio drops. <see cref="IsolatedMode"/> turns it into the Prefab Editor's inspector: it
    /// ignores the scene selection (driven by <see cref="SetEntity"/>) and edits the template directly, never
    /// touching the global undo stack (the template is not part of any scene).
    /// </summary>
    public partial class InspectorPanel : UserControl
    {
        public static readonly string[] BuiltInTags = { "Untagged", "Player", "Enemy", "MainCamera", "Ground", "Prop", "Trigger" };
        private const string AddTagItem = "Add Tag…";
        private static readonly HashSet<Type> _collapsed = new HashSet<Type>();

        private GameEntity _entity;
        private bool _loading;
        private bool _subscribed;
        private bool _rebuildQueued;
        private Component _revealAfterBuild;
        private readonly List<Control> _refresherKeys = new List<Control>();
        private readonly List<Component> _hookedComponents = new List<Component>();
        private EntityTreeWatcher _overrideWatcher;
        private TextBlock _prefabSubtitle;

        /// <summary>Edit a standalone entity (the Prefab Editor's template) instead of the scene selection. Set before
        /// the control is shown.</summary>
        public bool IsolatedMode { get; set; }

        /// <summary>Raised after the inspector changed the entity's structure or values (add / remove / move / reset /
        /// paste) — the Prefab Editor refreshes its tree and preview.</summary>
        public event Action<GameEntity> Edited;

        public GameEntity Entity => _entity;
        /// <summary>The prefab-instance bar of the current entity (null when it is not part of a prefab instance).</summary>
        public Control PrefabBar { get; private set; }
        /// <summary>Summary line of the prefab bar (e.g. "Prefab instance · 2 overrides").</summary>
        public string PrefabBarText => _prefabSubtitle?.Text;
        /// <summary>The component cards currently shown (Tag = the component).</summary>
        public IEnumerable<Control> Cards => Components.Children;

        public InspectorPanel()
        {
            InitializeComponent();
            for (int i = 0; i < 32; i++) LayerBox.Items.Add(i == 0 ? "0 · Default" : i.ToString());
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            AttachedToVisualTree += (s, e) => Subscribe();
            DetachedFromVisualTree += (s, e) => Unsubscribe();
        }

        // ================================================================ binding
        /// <summary>Show this entity (isolated mode; in scene mode the selection drives the inspector).</summary>
        public void SetEntity(GameEntity entity) => Bind(entity);

        /// <summary>Rebuild the cards for the current entity (keeps the scroll position).</summary>
        public void Refresh() => Bind(_entity, keepScroll: true);

        /// <summary>Drop every hook (the Prefab Editor calls this when it closes).</summary>
        public void Release()
        {
            UnhookEntity();
            ReleaseRefreshers();
            _entity = null;
            Components.Children.Clear();
            PrefabHost.Children.Clear();
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            if (IsolatedMode) return;
            SelectionService.Instance.SelectionChanged += OnSelectionChanged;
            SelectionService.Instance.TransformChanged += OnTransformChanged;
            UndoRedoManager.Instance.StateChanged += OnUndoStateChanged;
            var sel = SelectionService.Instance.SelectedEntity;
            if (!ReferenceEquals(sel, _entity) || Components.Children.Count == 0) Bind(sel);
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (IsolatedMode) { Release(); return; }
            SelectionService.Instance.SelectionChanged -= OnSelectionChanged;
            SelectionService.Instance.TransformChanged -= OnTransformChanged;
            UndoRedoManager.Instance.StateChanged -= OnUndoStateChanged;
        }

        private void OnSelectionChanged(object s, SelectionEventArgs e) => Dispatcher.UIThread.Post(() => { if (!IsolatedMode) Bind(e.SelectedEntity); });
        private void OnTransformChanged(object s, TransformChangedEventArgs e) => Dispatcher.UIThread.Post(PropertyRows.RefreshAll);
        private void OnUndoStateChanged(object s, EventArgs e) => Dispatcher.UIThread.Post(() => { PropertyRows.RefreshAll(); if (_entity != null) SyncHeader(); });

        private void Bind(GameEntity entity, bool keepScroll = false)
        {
            var offset = Scroller.Offset;
            bool same = ReferenceEquals(entity, _entity);
            UnhookEntity();
            ReleaseRefreshers();
            _entity = entity;
            Components.Children.Clear();
            PrefabHost.Children.Clear();
            PrefabBar = null; _prefabSubtitle = null;
            bool has = entity != null;
            NoSelection.IsVisible = !has;
            Header.IsVisible = has;
            Footer.IsVisible = has;
            if (!has) return;
            HookEntity();
            SyncHeader(rescanTags: true);
            var before = new HashSet<Control>(PropertyRows.Refreshers.Keys);
            BuildPrefabBar();
            foreach (var c in OrderedComponents(entity)) Components.Children.Add(BuildCard(c));
            foreach (var k in PropertyRows.Refreshers.Keys.ToList()) if (!before.Contains(k)) _refresherKeys.Add(k);
            if (same && keepScroll) Dispatcher.UIThread.Post(() => { try { Scroller.Offset = offset; } catch { } }, DispatcherPriority.Loaded);
            if (_revealAfterBuild != null)
            {
                var target = _revealAfterBuild; _revealAfterBuild = null;
                var card = Components.Children.FirstOrDefault(x => ReferenceEquals(x.Tag, target));
                if (card != null) Dispatcher.UIThread.Post(() => { try { card.BringIntoView(); } catch { } }, DispatcherPriority.Background);
            }
        }

        private static List<Component> OrderedComponents(GameEntity e)
        {
            var list = new List<Component>();
            if (e.Transform != null) list.Add(e.Transform);
            foreach (var c in e.Components) if (c != null && !(c is Transform) && !list.Contains(c)) list.Add(c);
            return list;
        }

        private void ReleaseRefreshers()
        {
            // only this inspector's rows: other windows (material editor, prefab editor) keep theirs
            foreach (var k in _refresherKeys) PropertyRows.Refreshers.Remove(k);
            _refresherKeys.Clear();
        }

        private void HookEntity()
        {
            var e = _entity;
            if (e == null) return;
            e.PropertyChanged += OnEntityPropertyChanged;
            e.Components.CollectionChanged += OnComponentsChanged;
            foreach (var c in e.Components)
                if (c is Script) { c.PropertyChanged += OnComponentPropertyChanged; _hookedComponents.Add(c); }
            var root = PrefabWorkflow.FindInstanceRoot(e);
            if (root != null && !IsolatedMode)
            {
                _overrideWatcher = new EntityTreeWatcher(root, 450);
                _overrideWatcher.Changed += UpdatePrefabSummary;
            }
        }

        private void UnhookEntity()
        {
            var e = _entity;
            if (e != null)
            {
                e.PropertyChanged -= OnEntityPropertyChanged;
                e.Components.CollectionChanged -= OnComponentsChanged;
            }
            foreach (var c in _hookedComponents) c.PropertyChanged -= OnComponentPropertyChanged;
            _hookedComponents.Clear();
            _overrideWatcher?.Dispose();
            _overrideWatcher = null;
        }

        private void OnEntityPropertyChanged(object s, PropertyChangedEventArgs e)
        {
            string p = e?.PropertyName;
            Dispatcher.UIThread.Post(() =>
            {
                if (_entity == null || !ReferenceEquals(s, _entity)) return;
                if (p == nameof(GameEntity.PrefabPath)) QueueRebuild();
                else if (p == nameof(GameEntity.Name) || p == nameof(GameEntity.IsActive) || p == nameof(GameEntity.Tag) || p == nameof(GameEntity.Layer) || p == nameof(GameEntity.IsStatic)) SyncHeader(rescanTags: p == nameof(GameEntity.Tag));
            });
        }

        private void OnComponentsChanged(object s, NotifyCollectionChangedEventArgs e) => QueueRebuild();

        private void OnComponentPropertyChanged(object s, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(Script.ScriptPath)) QueueRebuild();   // other class -> other fields
        }

        private void QueueRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            Dispatcher.UIThread.Post(() => { _rebuildQueued = false; Bind(_entity, keepScroll: true); }, DispatcherPriority.Background);
        }

        private Window OwnerWindow => TopLevel.GetTopLevel(this) as Window;

        // ================================================================ header
        private void SyncHeader(bool rescanTags = false)
        {
            var e = _entity;
            if (e == null) return;
            _loading = true;
            try
            {
                ActiveBox.IsChecked = e.IsActive;
                if (!NameBox.IsFocused) NameBox.Text = e.Name;
                FillTags(e.Tag, rescanTags);
                LayerBox.SelectedIndex = Math.Max(0, Math.Min(31, e.Layer));
                StaticBox.IsChecked = e.IsStatic;
            }
            finally { _loading = false; }
        }

        private List<string> _sceneTags;

        /// <summary>Tag choices: the built-in tags + every tag used in the open scenes (scanned on bind / tag edits only —
        /// the header re-syncs on every undo step) + the current one + "Add Tag…".</summary>
        private void FillTags(string current, bool rescan)
        {
            if (rescan || _sceneTags == null)
            {
                _sceneTags = new List<string>();
                try
                {
                    var project = ProjectData.Current;
                    void Walk(IEnumerable<GameEntity> es) { if (es == null) return; foreach (var x in es) { if (x == null) continue; if (!string.IsNullOrWhiteSpace(x.Tag) && !_sceneTags.Contains(x.Tag)) _sceneTags.Add(x.Tag); Walk(x.Children); } }
                    if (project?.Scenes != null) foreach (var sc in project.Scenes) Walk(sc?.Entities);
                }
                catch { }
            }
            var tags = new List<string>(BuiltInTags);
            void Add(string t) { if (!string.IsNullOrWhiteSpace(t) && !tags.Contains(t)) tags.Add(t); }
            foreach (var t in _sceneTags) Add(t);
            Add(current);
            bool changed = TagBox.Items.Count != tags.Count + 1;
            if (!changed) for (int i = 0; i < tags.Count; i++) if (!Equals(TagBox.Items[i], tags[i])) { changed = true; break; }
            if (changed)
            {
                TagBox.Items.Clear();
                foreach (var t in tags) TagBox.Items.Add(t);
                TagBox.Items.Add(AddTagItem);
            }
            TagBox.SelectedIndex = Math.Max(0, tags.IndexOf(string.IsNullOrEmpty(current) ? "Untagged" : current));
        }

        /// <summary>Run an edit: undoable in the scene, direct on an isolated template.</summary>
        private void Exec(string name, Action doIt, Action undo)
        {
            if (IsolatedMode) { doIt(); return; }
            try { UndoRedoManager.Instance.Execute(new ActionCommand(name, doIt, undo)); }
            catch (Exception ex) { EditorCommands.Fail(name, ex); }
        }

        private void OnActiveChanged(object s, RoutedEventArgs e)
        {
            if (_loading || _entity == null) return;
            var ent = _entity;
            bool v = ActiveBox.IsChecked == true;
            if (ent.IsActive == v) return;
            if (IsolatedMode) { PrefabWorkflow.SetActiveDetached(ent, v); Edited?.Invoke(ent); return; }
            Exec(v ? "Activate " + ent.Name : "Deactivate " + ent.Name,
                () => { ent.IsActive = v; SceneRenderService.RuntimeDirty = true; },
                () => { ent.IsActive = !v; SceneRenderService.RuntimeDirty = true; });
        }

        private void OnNameCommit(object s, RoutedEventArgs e)
        {
            if (_loading || _entity == null) return;
            var ent = _entity;
            string v = NameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(v) || v == ent.Name) { NameBox.Text = ent.Name; return; }
            string old = ent.Name;
            Exec("Rename " + old, () => ent.Name = v, () => ent.Name = old);
        }

        private void OnNameKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Return) { OnNameCommit(s, e); e.Handled = true; }
            else if (e.Key == Key.Escape && _entity != null) { NameBox.Text = _entity.Name; e.Handled = true; }
        }

        private async void OnTagChanged(object s, SelectionChangedEventArgs e)
        {
            if (_loading || _entity == null || TagBox.SelectedItem == null) return;
            var ent = _entity;
            string v = TagBox.SelectedItem as string;
            if (v == AddTagItem)
            {
                SyncHeader();
                var t = await PrefabWorkflow.Prompt(OwnerWindow, "Add Tag", "Name of the new tag (scripts: Scene.FindWithTag / hit.Tag)", "", "Add");
                if (string.IsNullOrWhiteSpace(t)) return;
                v = t.Trim();
            }
            string old = ent.Tag;
            if (v == old) return;
            Exec("Set Tag " + v, () => ent.Tag = v, () => ent.Tag = old);
            SyncHeader();
        }

        private void OnLayerChanged(object s, SelectionChangedEventArgs e)
        {
            if (_loading || _entity == null || LayerBox.SelectedIndex < 0) return;
            var ent = _entity;
            int v = LayerBox.SelectedIndex, old = ent.Layer;
            if (v == old) return;
            Exec("Set Layer " + v, () => ent.Layer = v, () => ent.Layer = old);
        }

        private void OnStaticChanged(object s, RoutedEventArgs e)
        {
            if (_loading || _entity == null) return;
            var ent = _entity;
            bool v = StaticBox.IsChecked == true;
            if (ent.IsStatic == v) return;
            Exec(v ? "Mark Static" : "Mark Dynamic", () => ent.IsStatic = v, () => ent.IsStatic = !v);
        }

        private void OnHeaderMenu(object s, RoutedEventArgs e)
        {
            var ent = _entity;
            if (ent == null) return;
            var m = new MenuFlyout();
            m.Items.Add(Item("Save as Prefab…", async () => { var p = await PrefabWorkflow.SaveAsPrefabInteractive(ent, OwnerWindow); if (p != null) QueueRebuild(); }, "Prefab"));
            var root = PrefabWorkflow.FindInstanceRoot(ent);
            if (root != null)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Item("Open Prefab", () => PrefabWorkflow.OpenInEditor(root.PrefabPath), "Link"));
                m.Items.Add(Item("Select Prefab Asset", () => PrefabWorkflow.SelectAsset(root.PrefabPath), "Search"));
                if (!IsolatedMode)
                {
                    m.Items.Add(Item("Apply Overrides to Prefab", async () => await PrefabWorkflow.ApplyInteractive(root, OwnerWindow), "Upload"));
                    m.Items.Add(Item("Revert to Prefab", async () => await PrefabWorkflow.RevertInteractive(root, OwnerWindow), "Undo"));
                }
                m.Items.Add(Item("Unpack Prefab", () => DoUnpack(root, false)));
                if (HasNestedInstance(root)) m.Items.Add(Item("Unpack Completely", () => DoUnpack(root, true)));
            }
            m.Items.Add(new Separator());
            var paste = Item("Paste Component as New", () => PasteAsNew(ent), "Plus");
            paste.IsEnabled = ComponentClipboard.CanPasteAsNew(ent);
            m.Items.Add(paste);
            m.Items.Add(new Separator());
            m.Items.Add(Item("Expand All Components", () => { foreach (var c in ent.Components) _collapsed.Remove(c.GetType()); QueueRebuild(); }));
            m.Items.Add(Item("Collapse All Components", () => { foreach (var c in ent.Components) _collapsed.Add(c.GetType()); QueueRebuild(); }));
            m.ShowAt(HeaderMenuButton);
        }

        private static bool HasNestedInstance(GameEntity root)
        {
            foreach (var c in root.Children) { if (c.IsPrefabInstance || HasNestedInstance(c)) return true; }
            return false;
        }

        private void DoUnpack(GameEntity root, bool completely)
        {
            PrefabWorkflow.Unpack(root, completely, undoable: !IsolatedMode);
            EditorCommands.Toast("Unpacked — \"" + root.Name + "\" is no longer linked to its prefab");
            Edited?.Invoke(root);
            QueueRebuild();
        }

        private static MenuItem Item(string header, Action a, string icon = null)
        {
            var mi = new MenuItem { Header = header };
            if (icon != null && Application.Current != null && Application.Current.TryFindResource("Icon" + icon, out _)) mi.Icon = new VxIcon { Icon = icon, Width = 13, Height = 13 };
            mi.Click += (s, e) => { try { a(); } catch (Exception ex) { EditorCommands.Fail(header, ex); } };
            return mi;
        }
        private static MenuItem Item(string header, Func<Task> a, string icon = null) => Item(header, () => { _ = a(); }, icon);

        // ================================================================ prefab bar
        private void BuildPrefabBar()
        {
            var ent = _entity;
            var root = PrefabWorkflow.FindInstanceRoot(ent);
            if (root == null) return;
            string full = PrefabWorkflow.Resolve(root.PrefabPath);
            bool exists = !string.IsNullOrEmpty(full) && File.Exists(full);
            bool isRoot = ReferenceEquals(root, ent);

            // opaque card + soft accent layer + accent outline: reads as "linked to an asset" in both themes
            var bar = new Border { Background = PrefabWorkflow.Res("VxPanelRaisedBrush"), CornerRadius = new CornerRadius(10), Name = "PrefabBar" };
            var tint = new Border { Background = PrefabWorkflow.Res("VxAccentSoftBrush"), BorderBrush = PrefabWorkflow.Res(exists ? "VxAccentBrush" : "VxRedBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 6, 8) };
            bar.Child = tint;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            var icon = new VxIcon { Icon = "Prefab", Width = 18, Height = 18, Foreground = PrefabWorkflow.Res("VxAccentBrush"), Margin = new Thickness(0, 1, 9, 0), VerticalAlignment = VerticalAlignment.Top };
            var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = Path.GetFileNameWithoutExtension(root.PrefabPath ?? ""), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = PrefabWorkflow.Res("VxAccentBrush") };
            ToolTip.SetTip(name, root.PrefabPath);
            _prefabSubtitle = new TextBlock { Classes = { "small", "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            texts.Children.Add(name);
            texts.Children.Add(_prefabSubtitle);
            var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
            var open = IconButton("Link", "Open the prefab in the Prefab Editor", () => PrefabWorkflow.OpenInEditor(root.PrefabPath));
            var select = IconButton("Search", "Select the prefab asset in the Project panel", () => PrefabWorkflow.SelectAsset(root.PrefabPath));
            open.IsEnabled = select.IsEnabled = exists;
            quick.Children.Add(open); quick.Children.Add(select);
            Grid.SetColumn(texts, 1); Grid.SetColumn(quick, 2);
            grid.Children.Add(icon); grid.Children.Add(texts); grid.Children.Add(quick);

            var actions = new UniformGrid { Columns = IsolatedMode ? 2 : 3, Margin = new Thickness(0, 8, 4, 0) };
            if (!IsolatedMode)
            {
                actions.Children.Add(BarButton("Apply", "Apply overrides — write this instance into the prefab asset and update every other instance", async () => { await PrefabWorkflow.ApplyInteractive(root, OwnerWindow); UpdatePrefabSummary(); }));
                actions.Children.Add(BarButton("Revert", "Revert to prefab — discard this instance's local changes (keeps its transform)", async () => await PrefabWorkflow.RevertInteractive(root, OwnerWindow)));
            }
            else
            {
                actions.Children.Add(BarButton("Open Prefab", "Edit the nested prefab in its own Prefab Editor", () => { PrefabWorkflow.OpenInEditor(root.PrefabPath); return Task.CompletedTask; }));
            }
            actions.Children.Add(BarButton("Unpack", "Unpack — make this a unique entity that is no longer linked to the prefab", () => { DoUnpack(root, false); return Task.CompletedTask; }));
            Grid.SetRow(actions, 1); Grid.SetColumnSpan(actions, 3);
            grid.Children.Add(actions);
            tint.Child = grid;
            bar.ContextRequested += (s, e) => { OnHeaderMenu(HeaderMenuButton, null); e.Handled = true; };
            PrefabHost.Children.Add(bar);
            PrefabBar = bar;
            UpdatePrefabSummary();
        }

        private void UpdatePrefabSummary()
        {
            var ent = _entity;
            if (_prefabSubtitle == null || ent == null) return;
            var root = PrefabWorkflow.FindInstanceRoot(ent);
            if (root == null) return;
            bool isRoot = ReferenceEquals(root, ent);
            string lead = isRoot ? (IsolatedMode ? "Nested prefab" : "Prefab instance") : "Part of “" + root.Name + "”";
            string full = PrefabWorkflow.Resolve(root.PrefabPath);
            if (string.IsNullOrEmpty(full) || !File.Exists(full))
            {
                _prefabSubtitle.Text = lead + " · asset missing";
                _prefabSubtitle.Foreground = PrefabWorkflow.Res("VxRedBrush");
                ToolTip.SetTip(_prefabSubtitle, root.PrefabPath + " was not found — Unpack, or Apply to recreate the asset");
                return;
            }
            if (IsolatedMode) { _prefabSubtitle.Text = lead; ToolTip.SetTip(_prefabSubtitle, root.PrefabPath); return; }
            var r = PrefabOverrides.Compute(root);
            _prefabSubtitle.Text = lead + (string.IsNullOrEmpty(r.Summary) ? "" : " · " + r.Summary);
            _prefabSubtitle.ClearValue(TextBlock.ForegroundProperty);
            ToolTip.SetTip(_prefabSubtitle, r.Count == 0 ? "This instance matches its prefab" : "Overrides (Apply writes them into the prefab, Revert discards them):\n" + string.Join("\n", r.Items) + (r.Count > r.Items.Count ? "\n…" : ""));
        }

        private static Button IconButton(string icon, string tip, Action a)
        {
            var b = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = icon, Width = 13, Height = 13 } };
            ToolTip.SetTip(b, tip);
            b.Click += (s, e) => a();
            return b;
        }

        private static Button BarButton(string text, string tip, Func<Task> a)
        {
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), MinHeight = 24, Padding = new Thickness(6, 2) };
            ToolTip.SetTip(b, tip);
            b.Click += async (s, e) => { try { await a(); } catch (Exception ex) { EditorCommands.Fail(text, ex); } };
            return b;
        }

        // ================================================================ component cards
        private Control BuildCard(Component c)
        {
            var ent = _entity;
            var (icon, brushKey) = ComponentEditors.Style(c);
            bool isTransform = c is Transform;
            bool expanded = !_collapsed.Contains(c.GetType());
            var card = new Border { Classes = { "card" }, Padding = new Thickness(0), Tag = c };
            var stack = new StackPanel();
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), Height = 32, Margin = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            var chevron = new VxIcon { Icon = expanded ? "ChevronDown" : "ChevronRight", Width = 11, Height = 11, Margin = new Thickness(2, 0, 4, 0), Foreground = PrefabWorkflow.Res("VxTextSecondaryBrush") };
            var ic = new VxIcon { Icon = icon, Foreground = PrefabWorkflow.Res(brushKey), Margin = new Thickness(2, 0, 8, 0) };
            var title = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            title.Bind(TextBlock.TextProperty, new Binding(nameof(Component.DisplayName)) { Source = c, Mode = BindingMode.OneWay });
            var body = new StackPanel { Margin = new Thickness(10, 0, 10, 10), IsVisible = expanded, Opacity = c.IsEnabled || isTransform ? 1 : 0.5 };
            bool guard = false;
            var enabled = new ToggleSwitch { IsChecked = c.IsEnabled, Margin = new Thickness(6, 0), IsVisible = !isTransform, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(enabled, "Enabled");
            enabled.IsCheckedChanged += (s, e) => { if (!guard) SetComponentEnabled(c, enabled.IsChecked == true); };
            PropertyRows.Refreshers[enabled] = () => { guard = true; enabled.IsChecked = c.IsEnabled; guard = false; body.Opacity = c.IsEnabled || isTransform ? 1 : 0.5; };
            var more = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "More" }, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(more, "Component actions");
            more.Click += (s, e) => BuildComponentMenu(c).ShowAt(more);
            Grid.SetColumn(ic, 1); Grid.SetColumn(title, 2); Grid.SetColumn(enabled, 3); Grid.SetColumn(more, 4);
            header.Children.Add(chevron); header.Children.Add(ic); header.Children.Add(title); header.Children.Add(enabled); header.Children.Add(more);
            header.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(header).Properties.IsRightButtonPressed) { BuildComponentMenu(c).ShowAt(header, true); e.Handled = true; }
            };
            header.Tapped += (s, e) =>
            {
                if (IsWithin(e.Source, enabled) || IsWithin(e.Source, more)) return;
                bool open = !body.IsVisible;
                body.IsVisible = open;
                chevron.Icon = open ? "ChevronDown" : "ChevronRight";
                if (open) _collapsed.Remove(c.GetType()); else _collapsed.Add(c.GetType());
            };
            stack.Children.Add(header);
            try { foreach (var row in ComponentEditors.Build(c, ent)) body.Children.Add(row); }
            catch (Exception ex) { body.Children.Add(PropertyRows.Warning("Editor error: " + ex.Message)); }
            stack.Children.Add(body);
            card.Child = stack;
            return card;
        }

        private static bool IsWithin(object source, Control container)
        {
            for (var v = source as Visual; v != null; v = v.GetVisualParent()) if (ReferenceEquals(v, container)) return true;
            return false;
        }

        private MenuFlyout BuildComponentMenu(Component c)
        {
            var m = new MenuFlyout();
            bool isTransform = c is Transform;
            var reset = Item("Reset", () => ResetComponent(c), "Refresh");
            ToolTip.SetTip(reset, isTransform ? "Position 0, rotation 0, scale 1" : "Default values for every setting — the assets it uses (mesh, material, clip, script) are kept");
            m.Items.Add(reset);
            m.Items.Add(new Separator());
            m.Items.Add(Item("Copy Component", () => CopyComponent(c), "File"));
            var pv = Item("Paste Component Values", () => PasteValues(c));
            pv.IsEnabled = ComponentClipboard.Type == c.GetType();
            m.Items.Add(pv);
            if (!isTransform)
            {
                var pn = Item("Paste Component as New", () => PasteAsNew(_entity));
                pn.IsEnabled = ComponentClipboard.CanPasteAsNew(_entity);
                m.Items.Add(pn);
                m.Items.Add(new Separator());
                var up = Item("Move Up", () => MoveComponent(c, -1), "ChevronLeft");
                up.IsEnabled = CanMove(c, -1);
                var down = Item("Move Down", () => MoveComponent(c, 1), "ChevronRight");
                down.IsEnabled = CanMove(c, 1);
                m.Items.Add(up); m.Items.Add(down);
                m.Items.Add(new Separator());
                m.Items.Add(Item("Remove Component", () => RemoveComponentFromEntity(c), "Trash"));
            }
            return m;
        }

        // ---------------------------------------------------------------- component operations (undoable in the scene)
        internal void SetComponentEnabled(Component c, bool v)
        {
            if (c == null || c.IsEnabled == v) return;
            var ent = _entity;
            Exec((v ? "Enable " : "Disable ") + c.DisplayName,
                () => { c.IsEnabled = v; SceneRenderService.RuntimeDirty = true; PropertyRows.RefreshAll(); },
                () => { c.IsEnabled = !v; SceneRenderService.RuntimeDirty = true; PropertyRows.RefreshAll(); });
            Edited?.Invoke(ent);
        }

        internal void AddNew(Component c, GameEntity target = null)
        {
            var ent = target ?? c?.Entity ?? _entity;
            if (ent == null || c == null) return;
            c.Entity = ent;
            try
            {
                if (IsolatedMode) ent.AddComponentDirect(c);
                else ent.AddComponent(c);
            }
            catch (Exception ex) { EditorCommands.Fail("Add component", ex); return; }
            _collapsed.Remove(c.GetType());
            _revealAfterBuild = c;
            SceneRenderService.RuntimeDirty = true;
            Edited?.Invoke(ent);
            QueueRebuild();
        }

        internal void RemoveComponentFromEntity(Component c)
        {
            var ent = _entity;
            if (ent == null || c == null || c is Transform) return;
            if (c is Camera) { try { SceneRenderService.Instance.RemoveEntityCamera(ent.Id); } catch { } }
            if (IsolatedMode) ent.Components.Remove(c);
            else ent.RemoveComponent(c);
            SceneRenderService.RuntimeDirty = true;
            Edited?.Invoke(ent);
            QueueRebuild();
        }

        private int MoveTarget(Component c, int dir)
        {
            var list = _entity?.Components;
            if (list == null) return -1;
            int i = list.IndexOf(c);
            if (i < 0) return -1;
            int j = i + dir;
            while (j >= 0 && j < list.Count && list[j] is Transform) j += dir;
            return j >= 0 && j < list.Count ? j : -1;
        }

        internal bool CanMove(Component c, int dir) => !(c is Transform) && MoveTarget(c, dir) >= 0;

        internal void MoveComponent(Component c, int dir)
        {
            var ent = _entity;
            int j = MoveTarget(c, dir);
            if (ent == null || j < 0) return;
            int i = ent.Components.IndexOf(c);
            if (IsolatedMode) ent.Components.Move(i, j);
            else UndoRedoManager.Instance.Execute(new CollectionMoveCommand<Component>(ent.Components, i, j, "Components"));
            SceneRenderService.RuntimeDirty = true;
            Edited?.Invoke(ent);
            QueueRebuild();
        }

        internal void CopyComponent(Component c)
        {
            if (c == null) return;
            ComponentClipboard.Copy(c);
            EditorCommands.Toast("Copied " + c.DisplayName);
        }

        internal void PasteValues(Component c)
        {
            if (c == null || ComponentClipboard.Type != c.GetType()) return;
            string before = ComponentClipboard.Snapshot(c), after = ComponentClipboard.Json;
            var ent = _entity;
            Exec("Paste " + c.DisplayName + " values",
                () => { ComponentClipboard.Apply(c, after); AfterValueEdit(ent); },
                () => { ComponentClipboard.Apply(c, before); AfterValueEdit(ent); });
            Edited?.Invoke(ent);
        }

        internal void PasteAsNew(GameEntity ent)
        {
            if (ent == null || !ComponentClipboard.CanPasteAsNew(ent)) return;
            var c = ComponentClipboard.CreateCopy();
            if (c == null) return;
            AddNew(c, ent);
        }

        internal void ResetComponent(Component c)
        {
            if (c == null) return;
            string before = ComponentClipboard.Snapshot(c);
            string after = ComponentClipboard.DefaultsSnapshot(c);
            if (after == null) return;
            var ent = _entity;
            Exec("Reset " + c.DisplayName,
                () => { ComponentClipboard.Apply(c, after); AfterValueEdit(ent); },
                () => { ComponentClipboard.Apply(c, before); AfterValueEdit(ent); });
            Edited?.Invoke(ent);
        }

        private void AfterValueEdit(GameEntity ent)
        {
            SceneRenderService.RuntimeDirty = true;
            if (ReferenceEquals(ent, _entity)) QueueRebuild();
        }

        // ================================================================ add component (searchable catalog)
        private sealed class CatalogEntry
        {
            public string Category, Name, Hint, Icon, BrushKey, Keywords;
            public Func<GameEntity, bool> Present;
            public Action<GameEntity> Run;
        }

        private void OnAddComponent(object sender, RoutedEventArgs e) => ShowAddComponentPopup(AddButton);

        /// <summary>The catalog of everything that can be added to an entity (components by category, project scripts, tools).</summary>
        private List<CatalogEntry> BuildCatalog()
        {
            var list = new List<CatalogEntry>();
            void C<T>(string cat, string name, string icon, string brush, Func<GameEntity, Component> make, string keywords, string hint = null) where T : Component
                => list.Add(new CatalogEntry { Category = cat, Name = name, Hint = hint, Icon = icon, BrushKey = brush, Keywords = keywords + " " + typeof(T).Name, Present = x => x.GetComponent<T>() != null, Run = x => AddNew(make(x), x) });
            C<MeshRenderer>("Rendering", "Mesh Renderer", "Cube", "VxAccentBrush", x => new MeshRenderer(x), "mesh model renderer draw");
            C<SpriteRenderer>("Rendering", "Sprite Renderer", "Image", "VxPinkBrush", x => new SpriteRenderer(x), "sprite 2d image");
            C<Camera>("Rendering", "Camera", "Camera", "VxPurpleBrush", x => new Camera(x), "camera view render");
            C<Skybox>("Rendering", "Skybox", "World", "VxTealBrush", x => new Skybox(x), "sky environment background");
            C<Light>("Lighting", "Directional Light", "Sun", "VxYellowBrush", x => new Light(x, LightType.Directional), "light sun directional");
            C<Light>("Lighting", "Point Light", "Light", "VxYellowBrush", x => new Light(x, LightType.Point), "light point lamp bulb");
            C<Light>("Lighting", "Spot Light", "Light", "VxYellowBrush", x => new Light(x, LightType.Spot), "light spot torch flashlight");
            C<Rigidbody>("Physics", "Rigidbody", "Sphere", "VxGreenBrush", x => new Rigidbody(x), "physics body mass gravity dynamic kinematic");
            C<BoxCollider>("Physics", "Box Collider", "Collider", "VxGreenBrush", x => new BoxCollider(x), "physics collision box");
            C<SphereCollider>("Physics", "Sphere Collider", "Collider", "VxGreenBrush", x => new SphereCollider(x), "physics collision sphere ball");
            C<CapsuleCollider>("Physics", "Capsule Collider", "Collider", "VxGreenBrush", x => new CapsuleCollider(x), "physics collision capsule character");
            C<MeshCollider>("Physics", "Mesh Collider", "Collider", "VxGreenBrush", x => new MeshCollider(x), "physics collision mesh exact", "edge-accurate");
            list.Add(new CatalogEntry { Category = "Physics", Name = "Open Collision Editor…", Icon = "Gizmo", BrushKey = "VxGreenBrush", Keywords = "collision editor collider fit", Run = x => EditorWindows.CollisionEditor(x) });
            // physics joints (#103) — an entity may carry several (e.g. a rope segment linked both ways)
            void J(string name, string hint, string keywords, Func<GameEntity, Component> make)
                => list.Add(new CatalogEntry { Category = "Physics · Joints", Name = name, Hint = hint, Icon = "Link", BrushKey = "VxGreenBrush", Keywords = "joint constraint physics " + keywords, Run = x => AddNew(make(x), x) });
            J("Hinge Joint", "doors", "hinge door lid gate rotate HingeJoint", x => new HingeJoint(x));
            J("Ball Joint", null, "ball socket ragdoll swing BallJoint", x => new BallJoint(x));
            J("Slider Joint", null, "slider prismatic drawer lift piston SliderJoint", x => new SliderJoint(x));
            J("Fixed Joint", null, "fixed weld glue attach FixedJoint", x => new FixedJoint(x));
            J("Distance Joint", "rope", "distance rope chain spring tether DistanceJoint", x => new DistanceJoint(x));
            C<AudioSource>("Audio", "Audio Source", "Audio", "VxGreenBrush", x => new AudioSource(x), "audio sound source clip music");
            C<AudioListener>("Audio", "Audio Listener", "Audio", "VxGreenBrush", x => new AudioListener(x), "audio listener ears");
            C<ReverbZone>("Audio", "Reverb Zone", "Audio", "VxGreenBrush", x => new ReverbZone(x), "audio reverb zone echo");
            C<Animator>("Animation", "Animator", "Bone", "VxPinkBrush", x => new Animator(x), "animation animator clips skeleton");
            C<BoneAttachment>("Animation", "Bone Attachment", "Bone", "VxPinkBrush", x => new BoneAttachment(x), "bone socket attachment weapon hand");
            C<TwoBoneIk>("Animation", "Two-Bone IK", "Bone", "VxPinkBrush", x => new TwoBoneIk(x), "ik inverse kinematics arm hand", "support hand");
            C<HandPose>("Animation", "Hand Pose", "Bone", "VxPinkBrush", x => new HandPose(x), "hand pose fingers grip");
            List<string> scripts = null;
            try { scripts = ScriptingService.EnumerateScripts(); } catch { }
            foreach (var rel in scripts ?? new List<string>())
            {
                string r = rel;
                string dir = Path.GetDirectoryName(r)?.Replace('\\', '/') ?? "";
                if (dir.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) dir = dir.Substring(7);
                list.Add(new CatalogEntry { Category = "Scripts", Name = Path.GetFileNameWithoutExtension(r), Hint = dir, Icon = "Script", BrushKey = "VxOrangeBrush", Keywords = "script behaviour " + r, Present = x => HasScript(x, r), Run = x => AssignScript(x, r) });
            }
            list.Add(new CatalogEntry { Category = "Scripts", Name = "Browse Scripts…", Icon = "Folder", BrushKey = "VxOrangeBrush", Keywords = "script pick browse cs", Run = x => _ = BrowseScript(x) });
            list.Add(new CatalogEntry { Category = "Scripts", Name = "New Script…", Icon = "Plus", BrushKey = "VxOrangeBrush", Keywords = "script new create behaviour", Run = x => _ = NewScript(x) });
            return list;
        }

        private static bool Matches(CatalogEntry en, string q)
        {
            string hay = (en.Name + " " + en.Category + " " + en.Hint + " " + en.Keywords).ToLowerInvariant();
            foreach (var tok in q.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) if (!hay.Contains(tok)) return false;
            return true;
        }

        /// <summary>The searchable Add Component popup (type to filter, ↑↓ + Return to add, Esc to close).</summary>
        public Flyout ShowAddComponentPopup(Control anchor)
        {
            var ent = _entity;
            if (ent == null) return null;
            var entries = BuildCatalog();
            var search = new TextBox { Classes = { "search" }, Watermark = "Search components and scripts", Margin = new Thickness(6, 6, 6, 6) };
            var list = new StackPanel { Spacing = 1, Margin = new Thickness(2, 0, 2, 4) };
            var scroll = new ScrollViewer { Content = list, MaxHeight = 400, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            var dock = new DockPanel { Width = 300 };
            DockPanel.SetDock(search, Dock.Top);
            dock.Children.Add(search); dock.Children.Add(scroll);
            var body = new Border { Child = dock, Background = PrefabWorkflow.Res("VxPanelRaisedBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(2) };
            var fly = new Flyout { Content = body, Placement = PlacementMode.Top };
            var rows = new List<(CatalogEntry entry, Border row)>();
            int sel = -1;
            void Highlight()
            {
                for (int i = 0; i < rows.Count; i++) rows[i].row.Background = i == sel ? PrefabWorkflow.Res("VxSelectionBrush") : Brushes.Transparent;
                if (sel >= 0 && sel < rows.Count) { try { rows[sel].row.BringIntoView(); } catch { } }
            }
            void Invoke(CatalogEntry en)
            {
                if (en.Present?.Invoke(ent) == true) return;
                fly.Hide();
                try { en.Run(ent); } catch (Exception ex) { EditorCommands.Fail("Add " + en.Name, ex); }
            }
            void Fill()
            {
                list.Children.Clear(); rows.Clear();
                string q = search.Text?.Trim() ?? "";
                string lastCat = null;
                foreach (var en in entries)
                {
                    if (q.Length > 0 && !Matches(en, q)) continue;
                    if (en.Category != lastCat)
                    {
                        list.Children.Add(new TextBlock { Text = en.Category.ToUpperInvariant(), Classes = { "small", "tertiary" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, lastCat == null ? 2 : 8, 0, 2) });
                        lastCat = en.Category;
                    }
                    bool present = en.Present?.Invoke(ent) == true;
                    var row = new Border { Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Opacity = present ? 0.45 : 1 };
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                    g.Children.Add(new VxIcon { Icon = en.Icon ?? "Gear", Width = 14, Height = 14, Foreground = PrefabWorkflow.Res(en.BrushKey ?? "VxTextSecondaryBrush"), Margin = new Thickness(0, 0, 8, 0) });
                    var nm = new TextBlock { Text = en.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(nm, 1); g.Children.Add(nm);
                    string hint = present ? "added" : en.Hint;
                    if (!string.IsNullOrEmpty(hint)) { var h = new TextBlock { Text = hint, Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(h, 2); g.Children.Add(h); }
                    row.Child = g;
                    if (!present)
                    {
                        int index = rows.Count;
                        var entry = en;
                        row.Cursor = new Cursor(StandardCursorType.Hand);
                        row.PointerEntered += (s, a) => { sel = index; Highlight(); };
                        row.PointerReleased += (s, a) => { if (a.InitialPressMouseButton == MouseButton.Left) Invoke(entry); };
                        rows.Add((en, row));
                    }
                    else ToolTip.SetTip(row, "Already on this entity");
                    list.Children.Add(row);
                }
                if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = "Nothing matches “" + q + "”", Classes = { "tertiary" }, Margin = new Thickness(8, 6) });
                sel = q.Length > 0 && rows.Count > 0 ? 0 : -1;
                Highlight();
            }
            search.TextChanged += (s, a) => Fill();
            search.KeyDown += (s, a) =>
            {
                if (a.Key == Key.Down) { if (rows.Count > 0) { sel = Math.Min(rows.Count - 1, sel + 1); Highlight(); } a.Handled = true; }
                else if (a.Key == Key.Up) { if (rows.Count > 0) { sel = Math.Max(0, sel - 1); Highlight(); } a.Handled = true; }
                else if (a.Key == Key.Return) { if (sel >= 0 && sel < rows.Count) Invoke(rows[sel].entry); else if (rows.Count == 1) Invoke(rows[0].entry); a.Handled = true; }
                else if (a.Key == Key.Escape) { fly.Hide(); a.Handled = true; }
            };
            fly.Opened += (s, a) => search.Focus();
            Fill();
            fly.ShowAt(anchor ?? AddButton);
            return fly;
        }

        private static bool HasScript(GameEntity e, string rel)
            => e.Components.OfType<Script>().Any(s => string.Equals((s.ScriptPath ?? "").Replace('\\', '/'), rel, StringComparison.OrdinalIgnoreCase));

        private void AssignScript(GameEntity e, string rel)
        {
            if (e == null || string.IsNullOrEmpty(rel)) return;
            rel = rel.Replace('\\', '/');
            if (rel.EndsWith("VortexScripting.cs", StringComparison.OrdinalIgnoreCase)) { EditorCommands.Toast("VortexScripting.cs is the API stub, not a behaviour"); return; }
            if (HasScript(e, rel)) { EditorCommands.Toast(Path.GetFileNameWithoutExtension(rel) + " is already attached"); return; }
            AddNew(new Script(e, rel), e);
        }

        private async Task BrowseScript(GameEntity e)
        {
            var p = await AssetPickerDialog.Pick("Scripts", new[] { "*.cs" });
            if (!string.IsNullOrEmpty(p)) AssignScript(e, PrefabWorkflow.Relative(p));
        }

        private async Task NewScript(GameEntity e)
        {
            if (ProjectData.Current == null) return;
            var sb = new System.Text.StringBuilder();
            foreach (char ch in (e?.Name ?? "New") + "Behaviour") if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
            string def = sb.Length == 0 || char.IsDigit(sb[0]) ? "NewBehaviour" : sb.ToString();
            var name = await PrefabWorkflow.Prompt(OwnerWindow, "New Script", "Class name of the new behaviour (created in Assets/Scripts and attached)", def, "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var abs = ScriptingService.CreateScript(name.Trim());
                AssignScript(e, ScriptingService.MakeRelative(ProjectData.Current.Path, abs));
                try { EditorCommands.Window?.AssetBrowser?.Refresh(); } catch { }
                EditorCommands.OpenInIde(abs);
            }
            catch (Exception ex) { EditorCommands.Fail("New script", ex); }
        }

        // ================================================================ drops (script / audio clip)
        private static readonly string[] AudioExt = { ".wav", ".mp3", ".ogg", ".flac", ".vsndc" };

        private static bool IsAudio(string p) => Array.IndexOf(AudioExt, Path.GetExtension(p ?? "").ToLowerInvariant()) >= 0;
        private static bool IsScript(string p) => (p ?? "").EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("VortexScripting.cs", StringComparison.OrdinalIgnoreCase);

        private void OnDragOver(object s, DragEventArgs e)
        {
            var p = PropertyRows.DroppedPath(e, "vortex/asset");
            bool ok = _entity != null && p != null && (IsScript(p) || IsAudio(p));
            e.DragEffects = ok ? DragDropEffects.Link : DragDropEffects.None;
        }

        private void OnDrop(object s, DragEventArgs e)
        {
            var ent = _entity;
            if (ent == null) return;
            var p = PropertyRows.DroppedPath(e, "vortex/asset");
            if (p == null) return;
            string rel = PrefabWorkflow.Relative(p);
            if (IsScript(p)) { AssignScript(ent, rel); e.Handled = true; return; }
            if (IsAudio(p))
            {
                // clip paths are project-relative; an out-of-project file must be imported first (non-portable path)
                if (Path.IsPathRooted(rel)) { EditorCommands.Toast("Import the audio file into the project first"); return; }
                var src = ent.GetComponent<AudioSource>();
                if (src == null) { src = new AudioSource(ent); AddNew(src, ent); }
                var old = src.AudioClipPath;
                var target = src;
                Exec("Set Audio Clip", () => target.AudioClipPath = rel, () => target.AudioClipPath = old);
                QueueRebuild();
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Component copy / paste / reset values (shared by every inspector, so a component copied in the scene can be
    /// pasted into a prefab template and back). Values travel as the component's serialized JSON — exactly what a
    /// scene / prefab stores — and are applied through the property setters (engine sync included).
    /// </summary>
    public static class ComponentClipboard
    {
        public static Type Type { get; private set; }
        public static string Json { get; private set; }
        public static string Name { get; private set; }

        public static void Copy(Component c)
        {
            if (c == null) return;
            Json = Snapshot(c);
            Type = c.GetType();
            Name = c.DisplayName;
        }

        public static void Clear() { Json = null; Type = null; Name = null; }

        /// <summary>A pasted copy may go on the entity when it has no component of that type yet (scripts: another script).</summary>
        public static bool CanPasteAsNew(GameEntity e)
        {
            if (e == null || Type == null || Json == null || Type == typeof(Transform)) return false;
            if (Type == typeof(Script) || typeof(PhysicsJoint).IsAssignableFrom(Type)) return true;
            return !e.Components.Any(c => c != null && c.GetType() == Type);
        }

        public static Component CreateCopy()
        {
            if (Json == null) return null;
            try
            {
                var c = DataSerializer.FromJson<Component>(Json);
                c.RegenerateId();
                return c;
            }
            catch { return null; }
        }

        public static string Snapshot(Component c) => DataSerializer.ToJson<Component>(c);

        private static IEnumerable<PropertyInfo> DataProps(Type t)
            => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<DataMemberAttribute>(true) != null
                            && p.Name != nameof(Component.Id) && p.Name != nameof(Component.IsEnabled));

        /// <summary>Apply a snapshot's values to the component (id and enabled state stay).</summary>
        public static void Apply(Component target, string json)
        {
            if (target == null || string.IsNullOrEmpty(json)) return;
            Component src;
            try { src = DataSerializer.FromJson<Component>(json); } catch { return; }
            if (src == null || src.GetType() != target.GetType()) return;
            foreach (var p in DataProps(target.GetType()))
            {
                try
                {
                    var v = p.GetValue(src);
                    if (!Equals(v, p.GetValue(target))) p.SetValue(target, v);
                }
                catch { }
            }
        }

        /// <summary>Snapshot of the component's defaults — keeping what it points at (asset paths, the script class,
        /// the light type, animator clips), so Reset restores settings without unhooking the content.</summary>
        public static string DefaultsSnapshot(Component c)
        {
            Component fresh;
            try { fresh = (Component)Activator.CreateInstance(c.GetType()); } catch { return null; }
            foreach (var p in DataProps(c.GetType()))
            {
                bool keep = (p.PropertyType == typeof(string) && (p.Name.EndsWith("Path", StringComparison.Ordinal) || p.Name == nameof(Script.ScriptClassName)))
                            || (c is Light && p.Name == nameof(Light.LightType))
                            || (c is Animator && (p.Name == nameof(Animator.Clips) || p.Name == nameof(Animator.DefaultClip)));
                if (!keep) continue;
                try { p.SetValue(fresh, p.GetValue(c)); } catch { }
            }
            try { return Snapshot(fresh); } catch { return null; }
        }
    }
}
