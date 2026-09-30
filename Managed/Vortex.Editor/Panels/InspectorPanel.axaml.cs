using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
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
using Transform = Editor.ECS.Components.Transform;

namespace VortexEditor.Panels
{
    public partial class InspectorPanel : UserControl
    {
        private GameEntity _entity;
        private bool _loading;
        private static readonly string[] Tags = { "Untagged", "Player", "Enemy", "MainCamera", "Ground", "Prop", "Trigger" };
        private static readonly HashSet<Type> _collapsed = new HashSet<Type>();

        public InspectorPanel()
        {
            InitializeComponent();
            foreach (var t in Tags) TagBox.Items.Add(t);
            SelectionService.Instance.SelectionChanged += (s, e) => Dispatcher.UIThread.Post(() => Bind(e.SelectedEntity));
            SelectionService.Instance.TransformChanged += (s, e) => Dispatcher.UIThread.Post(PropertyRows.RefreshAll);
            Editor.Core.UndoRedo.UndoRedoManager.Instance.StateChanged += (s, e) => Dispatcher.UIThread.Post(PropertyRows.RefreshAll);
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        public GameEntity Entity => _entity;

        public void Refresh() => Bind(_entity);

        private void Bind(GameEntity entity)
        {
            _entity = entity;
            PropertyRows.ClearRefreshers();
            Components.Children.Clear();
            bool has = entity != null;
            NoSelection.IsVisible = !has;
            Header.IsVisible = has;
            AddButton.IsVisible = has;
            if (!has) return;
            _loading = true;
            ActiveBox.IsChecked = entity.IsActive;
            NameBox.Text = entity.Name;
            int ti = Array.IndexOf(Tags, entity.Tag ?? "Untagged");
            TagBox.SelectedIndex = ti < 0 ? 0 : ti;
            PrefabText.Text = string.IsNullOrEmpty(entity.PrefabPath) ? "—" : System.IO.Path.GetFileName(entity.PrefabPath);
            _loading = false;

            var comps = new List<Component>();
            if (entity.Transform != null) comps.Add(entity.Transform);
            if (entity.Components != null) foreach (var c in entity.Components) if (!(c is Transform) && !comps.Contains(c)) comps.Add(c);
            foreach (var c in comps) Components.Children.Add(BuildCard(c));
        }

        private Control BuildCard(Component c)
        {
            var (icon, brushKey) = ComponentEditors.Style(c);
            var card = new Border { Classes = { "card" }, Padding = new Thickness(0) };
            var stack = new StackPanel();
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), Height = 32, Margin = new Thickness(8, 0, 6, 0) };
            var chevron = new ToggleButton { Classes = { "icon", "small" }, IsChecked = !_collapsed.Contains(c.GetType()), Content = new VxIcon { Icon = "ChevronDown", Width = 12, Height = 12 } };
            var ic = new VxIcon { Icon = icon, Foreground = (IBrush)Application.Current.FindResource(brushKey), Margin = new Thickness(4, 0, 8, 0) };
            var title = new TextBlock { Text = c.DisplayName, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var enabled = new ToggleSwitch { IsChecked = c.IsEnabled, Margin = new Thickness(6, 0), IsVisible = !(c is Transform) };
            enabled.IsCheckedChanged += (s, e) => { if (!_loading) { c.IsEnabled = enabled.IsChecked == true; SceneRenderService.RuntimeDirty = true; } };
            var menu = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "More" }, IsVisible = !(c is Transform) };
            menu.Click += (s, e) =>
            {
                var m = new MenuFlyout();
                var rm = new MenuItem { Header = "Remove Component" };
                rm.Click += (a, b) => { _entity?.RemoveComponent(c); SceneRenderService.RuntimeDirty = true; Refresh(); };
                var reset = new MenuItem { Header = "Reset to defaults" };
                reset.Click += (a, b) => { ResetComponent(c); PropertyRows.RefreshAll(); SceneRenderService.RuntimeDirty = true; };
                m.Items.Add(reset); m.Items.Add(rm);
                m.ShowAt(menu);
            };
            Grid.SetColumn(ic, 1); Grid.SetColumn(title, 2); Grid.SetColumn(enabled, 3); Grid.SetColumn(menu, 4);
            header.Children.Add(chevron); header.Children.Add(ic); header.Children.Add(title); header.Children.Add(enabled); header.Children.Add(menu);
            stack.Children.Add(header);
            var body = new StackPanel { Margin = new Thickness(10, 0, 10, 10), IsVisible = chevron.IsChecked == true };
            try { foreach (var row in ComponentEditors.Build(c, _entity)) body.Children.Add(row); }
            catch (Exception ex) { body.Children.Add(PropertyRows.Warning("Editor error: " + ex.Message)); }
            chevron.IsCheckedChanged += (s, e) => { body.IsVisible = chevron.IsChecked == true; if (body.IsVisible) _collapsed.Remove(c.GetType()); else _collapsed.Add(c.GetType()); };
            stack.Children.Add(body);
            card.Child = stack;
            return card;
        }

        private static void ResetComponent(Component c)
        {
            try
            {
                var fresh = (Component)Activator.CreateInstance(c.GetType(), c.Entity);
                foreach (var p in c.GetType().GetProperties())
                    if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.Name != "Id" && p.Name != "Entity")
                        p.SetValue(c, p.GetValue(fresh));
            }
            catch { }
        }

        // ---------------------------------------------------------------- header edits
        private void OnActiveChanged(object s, RoutedEventArgs e) { if (!_loading && _entity != null) { _entity.IsActive = ActiveBox.IsChecked == true; SceneRenderService.RuntimeDirty = true; } }
        private void OnNameCommit(object s, RoutedEventArgs e) { if (!_loading && _entity != null && !string.IsNullOrWhiteSpace(NameBox.Text) && NameBox.Text != _entity.Name) _entity.Name = NameBox.Text.Trim(); }
        private void OnNameKey(object s, KeyEventArgs e) { if (e.Key == Key.Return) { OnNameCommit(s, e); e.Handled = true; } }
        private void OnTagChanged(object s, SelectionChangedEventArgs e) { if (!_loading && _entity != null && TagBox.SelectedIndex >= 0) _entity.Tag = Tags[TagBox.SelectedIndex]; }

        // ---------------------------------------------------------------- add component
        private void OnAddComponent(object sender, RoutedEventArgs e)
        {
            if (_entity == null) return;
            var m = new MenuFlyout();
            m.Items.Add(Add("Mesh Renderer", () => new MeshRenderer(_entity)));
            m.Items.Add(Add("Camera", () => new Camera(_entity)));
            var light = new MenuItem { Header = "Light" };
            light.Items.Add(Add("Directional Light", () => new Light(_entity) { LightType = LightType.Directional }));
            light.Items.Add(Add("Point Light", () => new Light(_entity) { LightType = LightType.Point }));
            light.Items.Add(Add("Spot Light", () => new Light(_entity) { LightType = LightType.Spot }));
            m.Items.Add(light);
            m.Items.Add(Add("Skybox", () => new Skybox(_entity)));
            m.Items.Add(new Separator());
            var audio = new MenuItem { Header = "Audio" };
            audio.Items.Add(Add("Audio Source", () => new AudioSource(_entity)));
            audio.Items.Add(Add("Audio Listener", () => new AudioListener(_entity)));
            audio.Items.Add(Add("Reverb Zone", () => new ReverbZone(_entity)));
            m.Items.Add(audio);
            var anim = new MenuItem { Header = "Animation" };
            anim.Items.Add(Add("Animator", () => new Animator(_entity)));
            anim.Items.Add(Add("Bone Attachment", () => new BoneAttachment(_entity)));
            anim.Items.Add(Add("Two-Bone IK", () => new TwoBoneIk(_entity)));
            anim.Items.Add(Add("Hand Pose", () => new HandPose(_entity)));
            m.Items.Add(anim);
            var phys = new MenuItem { Header = "Physics" };
            phys.Items.Add(Add("Rigidbody", () => new Rigidbody(_entity)));
            phys.Items.Add(Add("Box Collider", () => new BoxCollider(_entity)));
            phys.Items.Add(Add("Sphere Collider", () => new SphereCollider(_entity)));
            phys.Items.Add(Add("Capsule Collider", () => new CapsuleCollider(_entity)));
            phys.Items.Add(Add("Mesh Collider", () => new MeshCollider(_entity)));
            var ce = new MenuItem { Header = "Open Collision Editor…" };
            ce.Click += (s, a) => EditorCommands.Window?.OpenCollisionEditor(_entity);
            phys.Items.Add(ce);
            m.Items.Add(phys);
            var script = new MenuItem { Header = "Script" };
            try
            {
                foreach (var rel in ScriptingService.EnumerateScripts())
                {
                    var r = rel;
                    var mi = new MenuItem { Header = System.IO.Path.GetFileNameWithoutExtension(r) };
                    mi.Click += (s, a) => { _entity.AddComponent(new Script(_entity, r)); SceneRenderService.RuntimeDirty = true; Refresh(); };
                    script.Items.Add(mi);
                }
            }
            catch { }
            if (script.Items.Count > 0) script.Items.Add(new Separator());
            var ns = new MenuItem { Header = "New Script…" };
            ns.Click += (s, a) =>
            {
                var path = ScriptingService.CreateScript("NewBehaviour");
                string rel = ScriptingService.MakeRelative(ProjectData.Current?.Path ?? "", path);
                _entity.AddComponent(new Script(_entity, rel));
                EditorCommands.OpenInIde(path);
                Refresh();
            };
            script.Items.Add(ns);
            m.Items.Add(script);
            m.ShowAt(AddButton);
        }

        private MenuItem Add(string header, Func<Component> make)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => { try { _entity.AddComponent(make()); SceneRenderService.RuntimeDirty = true; Refresh(); } catch (Exception ex) { EditorCommands.Fail("Add component", ex); } };
            return mi;
        }

        // ---------------------------------------------------------------- drops (script / audio clip)
        private void OnDragOver(object s, DragEventArgs e)
        {
            var p = PropertyRows.DroppedPath(e, "vortex/asset");
            e.DragEffects = _entity != null && p != null && PropertyRows.Matches(p, new[] { "*.cs", "*.wav", "*.mp3", "*.ogg", "*.flac", "*.vsndc" }) ? DragDropEffects.Link : DragDropEffects.None;
        }
        private void OnDrop(object s, DragEventArgs e)
        {
            if (_entity == null) return;
            var p = PropertyRows.DroppedPath(e, "vortex/asset"); if (p == null) return;
            string ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
            if (ext == ".cs") { _entity.AddComponent(new Script(_entity, ScriptingService.MakeRelative(ProjectData.Current?.Path ?? "", p))); }
            else if (ext == ".wav" || ext == ".mp3" || ext == ".ogg" || ext == ".flac" || ext == ".vsndc")
            {
                var src = _entity.GetComponent<AudioSource>() ?? (AudioSource)AddAndReturn(new AudioSource(_entity));
                src.AudioClipPath = p;
            }
            Refresh();
            e.Handled = true;
        }
        private Component AddAndReturn(Component c) { _entity.AddComponent(c); return c; }
    }
}
