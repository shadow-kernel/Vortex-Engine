using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using VortexEditor.Controls;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Collision editor for one entity: add / remove collider shapes, fit them to the mesh bounds and tune them
    /// while the viewport draws the outlines live (colliders are forced visible while the window is open).
    /// </summary>
    public sealed class CollisionEditorWindow : Window
    {
        /// <summary>Open this window (owned by the main window).</summary>
        public static void Open(Editor.ECS.GameEntity entity) => EditorWindows.Show(new CollisionEditorWindow(entity));

        private readonly GameEntity _entity;
        private readonly StackPanel _shapes = new StackPanel { Spacing = 8 };
        private readonly bool _prevVisible;

        public CollisionEditorWindow(GameEntity entity)
        {
            _entity = entity;
            Title = "Collision — " + entity.Name; Width = 520; Height = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            _prevVisible = EditorViewportService.Instance.AreCollidersVisible;
            EditorViewportService.Instance.AreCollidersVisible = true;
            SceneRenderService.RuntimeDirty = true;
            var root = new DockPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(14, 12, 14, 6) };
            top.Children.Add(Btn("Box", () => Add(new BoxCollider(_entity))));
            top.Children.Add(Btn("Sphere", () => Add(new SphereCollider(_entity))));
            top.Children.Add(Btn("Capsule", () => Add(new CapsuleCollider(_entity))));
            top.Children.Add(Btn("Mesh", () => Add(new MeshCollider(_entity) { MeshPath = _entity.GetComponent<MeshRenderer>()?.MeshPath })));
            top.Children.Add(Btn("Rigidbody", () => { if (_entity.GetComponent<Rigidbody>() == null) Add(new Rigidbody(_entity)); }));
            DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 8, 14, 14) };
            var done = new Button { Content = "Done", Classes = { "accent" }, MinWidth = 90, IsDefault = true }; done.Click += (s, e) => Close();
            bottom.Children.Add(done);
            DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
            root.Children.Add(new ScrollViewer { Content = new Border { Child = _shapes, Margin = new Thickness(14, 4) } });
            Content = root;
            Rebuild();
            Closed += (s, e) => { EditorViewportService.Instance.AreCollidersVisible = _prevVisible; SceneRenderService.RuntimeDirty = true; EditorCommands.Window?.Inspector?.Refresh(); };
        }

        private static Button Btn(string t, Action a) { var b = new Button { Content = "+ " + t }; b.Click += (s, e) => a(); return b; }

        private void Add(Editor.ECS.Component c)
        {
            _entity.AddComponent(c);
            if (c is Collider col) FitToMesh(col);
            SceneRenderService.RuntimeDirty = true;
            Rebuild();
        }

        private void FitToMesh(Collider c)
        {
            var mr = _entity.GetComponent<MeshRenderer>();
            var s = _entity.Transform?.LocalScale ?? new Vector3(1, 1, 1);
            float hx = 0.5f, hy = 0.5f, hz = 0.5f, cx = 0, cy = 0, cz = 0;
            if (mr != null && !string.IsNullOrEmpty(mr.MeshPath) && !mr.MeshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string abs = Panels.Inspector.ComponentEditors.ProjectRelativeToAbsolute(mr.MeshPath.Split('#')[0]);
                    long handle = MeshService.Instance.GetMesh(abs);
                    if (handle >= 0 && VortexAPI.GetMeshBounds(handle, out float sx2, out float sy2, out float sz2))
                    {
                        hx = sx2 * 0.5f; hy = sy2 * 0.5f; hz = sz2 * 0.5f;
                        if (VortexAPI.GetMeshBoundsCenter(handle, out float ccx, out float ccy, out float ccz)) { cx = ccx; cy = ccy; cz = ccz; }
                    }
                }
                catch { }
            }
            c.Center = new Vector3(cx, cy, cz);
            switch (c)
            {
                case BoxCollider b: b.Size = new Vector3(Math.Max(0.01f, hx * 2), Math.Max(0.01f, hy * 2), Math.Max(0.01f, hz * 2)); break;
                case SphereCollider sp: sp.Radius = Math.Max(0.01f, Math.Max(hx, Math.Max(hy, hz))); break;
                case CapsuleCollider cp: cp.Radius = Math.Max(0.01f, Math.Max(hx, hz)); cp.Height = Math.Max(cp.Radius * 2, hy * 2); cp.Direction = 1; break;
            }
        }

        private void Rebuild()
        {
            _shapes.Children.Clear();
            var comps = new List<Editor.ECS.Component>();
            if (_entity.Components != null) foreach (var c in _entity.Components) if (c is Collider || c is Rigidbody) comps.Add(c);
            if (comps.Count == 0) _shapes.Children.Add(new TextBlock { Text = "No colliders yet. Add a shape above — it is fitted to the mesh bounds automatically.", Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20) });
            foreach (var c in comps)
            {
                var card = new Border { Classes = { "card" } };
                var st = new StackPanel { Spacing = 2 };
                var head = new DockPanel();
                var rm = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Trash" } }; rm.Click += (s, e) => { _entity.RemoveComponent(c); SceneRenderService.RuntimeDirty = true; Rebuild(); };
                DockPanel.SetDock(rm, Dock.Right);
                head.Children.Add(rm);
                if (c is Collider col)
                {
                    var fit = new Button { Content = "Fit to mesh", Classes = { "ghost" } }; fit.Click += (s, e) => { FitToMesh(col); SceneRenderService.RuntimeDirty = true; RefreshAll(); };
                    DockPanel.SetDock(fit, Dock.Right); head.Children.Add(fit);
                }
                head.Children.Add(new TextBlock { Text = c.DisplayName, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                st.Children.Add(head);
                foreach (var row in Panels.Inspector.ComponentEditors.Build(c, _entity)) if (!(row is Grid g && g.Children.Count > 1 && g.Children[1] is Button)) st.Children.Add(row);
                card.Child = st;
                _shapes.Children.Add(card);
            }
        }
    }
}
