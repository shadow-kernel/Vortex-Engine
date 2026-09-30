using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Rendering;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Socket editor: seat an entity (weapon, prop) on a bone of an animated character. Pick the target and the
    /// bone from the character's skeleton, tune the offsets and watch it follow live in the viewport.
    /// </summary>
    public sealed class SocketEditorWindow : Window
    {
        /// <summary>Open this window (owned by the main window).</summary>
        public static void Open(Editor.ECS.GameEntity entity) => EditorWindows.Show(new SocketEditorWindow(entity));

        private readonly GameEntity _entity;
        private readonly BoneAttachment _att;
        private readonly ComboBox _targets = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly ComboBox _bones = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock _status = new TextBlock { Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap };
        private readonly List<GameEntity> _animators = new List<GameEntity>();
        private readonly DispatcherTimer _preview;
        private bool _syncing;

        public SocketEditorWindow(GameEntity entity)
        {
            _entity = entity;
            _att = entity.GetComponent<BoneAttachment>();
            if (_att == null) { _att = new BoneAttachment(entity); entity.AddComponent(_att); }
            Title = "Socket — " + entity.Name; Width = 520; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

            var scene = ProjectData.Current?.ActiveScene;
            if (scene?.Entities != null) foreach (var e in scene.Entities) Collect(e);
            _targets.Items.Add("(nearest ancestor with an Animator)");
            foreach (var a in _animators) _targets.Items.Add(a.Name);
            int ti = 0;
            if (!string.IsNullOrEmpty(_att.TargetEntityId)) { int idx = _animators.FindIndex(a => a.Id.ToString() == _att.TargetEntityId); if (idx >= 0) ti = idx + 1; }
            _targets.SelectedIndex = ti;
            _targets.SelectionChanged += (s, e) => { if (_syncing) return; _att.TargetEntityId = _targets.SelectedIndex <= 0 ? "" : _animators[_targets.SelectedIndex - 1].Id.ToString(); FillBones(); Apply(); };
            _bones.SelectionChanged += (s, e) => { if (_syncing || _bones.SelectedItem == null) return; _att.BoneName = _bones.SelectedItem.ToString(); Apply(); };

            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(16, 12) };
            stack.Children.Add(new TextBlock { Text = "Attachment", Classes = { "section" } });
            stack.Children.Add(Row("Character", _targets, "The animated entity whose bone this entity follows"));
            stack.Children.Add(Row("Bone", _bones));
            stack.Children.Add(Row("Layer", Choice(() => _att.SocketRenderLayer, v => { _att.SocketRenderLayer = v; Apply(); }, "World", "First-person", "Third-person only")));
            stack.Children.Add(new TextBlock { Text = "Offset (bone space)", Classes = { "section" } });
            stack.Children.Add(Row("Position", Vector3(() => _att.OffsetPosition, v => { _att.OffsetPosition = v; Apply(); }, 0.01)));
            stack.Children.Add(Row("Rotation", Vector3(() => _att.OffsetRotation, v => { _att.OffsetRotation = v; Apply(); }, 1)));
            stack.Children.Add(Row("Scale", Vector3(() => _att.OffsetScale, v => { _att.OffsetScale = v; Apply(); }, 0.05, 0.001f)));
            var reset = new Button { Content = "Reset offsets", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
            reset.Click += (s, e) => { _att.OffsetPosition = new Vector3(0, 0, 0); _att.OffsetRotation = new Vector3(0, 0, 0); _att.OffsetScale = new Vector3(1, 1, 1); RefreshAll(); Apply(); };
            stack.Children.Add(Row("", reset));
            stack.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var detach = new Button { Content = "Remove attachment" }; detach.Click += (s, e) => { _entity.RemoveComponent(_att); SceneRenderService.RuntimeDirty = true; Close(); };
            var done = new Button { Content = "Done", Classes = { "accent" }, MinWidth = 90, IsDefault = true }; done.Click += (s, e) => Close();
            buttons.Children.Add(detach); buttons.Children.Add(done);
            stack.Children.Add(buttons);
            Content = stack;
            FillBones();
            _preview = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _preview.Tick += (s, e) => Apply();
            _preview.Start();
            Closed += (s, e) => { _preview.Stop(); EditorCommands.Window?.Inspector?.Refresh(); };
        }

        private void Collect(GameEntity e)
        {
            if (e == null) return;
            if (e.GetComponent<Animator>() != null && !ReferenceEquals(e, _entity)) _animators.Add(e);
            if (e.Children != null) foreach (var c in e.Children) Collect(c);
        }

        private GameEntity ResolveTarget()
        {
            if (_targets.SelectedIndex > 0 && _targets.SelectedIndex - 1 < _animators.Count) return _animators[_targets.SelectedIndex - 1];
            var p = _entity.Parent;
            while (p != null) { if (p.GetComponent<Animator>() != null) return p; p = p.Parent; }
            return null;
        }

        private void FillBones()
        {
            _syncing = true;
            _bones.Items.Clear();
            var target = ResolveTarget();
            SkeletonDef skel = null;
            var mr = target?.GetComponent<MeshRenderer>();
            if (mr != null && !string.IsNullOrEmpty(mr.MeshPath))
            {
                try { skel = AnimationService.Instance.GetSkeleton(Panels.Inspector.ComponentEditors.ProjectRelativeToAbsolute(mr.MeshPath.Split('#')[0])); } catch { }
            }
            if (skel != null && skel.IsValid)
            {
                foreach (var b in skel.Bones) { var n = skel.Nodes[b.NodeIndex].Name; if (!string.IsNullOrEmpty(n)) _bones.Items.Add(n); }
                _status.Text = skel.Bones.Length + " bones from " + target.Name + ". The entity follows the bone live; play mode uses the same offsets.";
            }
            else _status.Text = target == null ? "No animated character found. Pick one above or parent this entity under one." : "The character's model has no skeleton — pick a rigged model.";
            int idx = -1;
            for (int i = 0; i < _bones.Items.Count; i++) if (string.Equals(_bones.Items[i]?.ToString(), _att.BoneName, StringComparison.OrdinalIgnoreCase)) idx = i;
            if (idx < 0 && _bones.Items.Count > 0) { idx = 0; _att.BoneName = _bones.Items[0].ToString(); }
            _bones.SelectedIndex = idx;
            _syncing = false;
        }

        private void Apply()
        {
            var scene = ProjectData.Current?.ActiveScene; if (scene == null) return;
            try { BoneSocketService.Instance.ApplyOne(scene, _entity); } catch { }
            SceneRenderService.RuntimeDirty = true;
        }
    }
}
