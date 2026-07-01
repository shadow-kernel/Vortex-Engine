using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;

namespace Editor.Editors.PhysicsEditor
{
    /// <summary>
    /// A dedicated, floating Collision Editor — pick an entity in the scene and give it a collider (Box, Sphere,
    /// Capsule or edge-accurate Mesh), tune it, and mark it a trigger. Built programmatically so it matches the
    /// engine's dark UI without adding a XAML page. Live: it follows the current selection and writes straight onto
    /// the entity's Collider component (the CollisionService reads these to make the world solid).
    /// </summary>
    public sealed class CollisionEditorWindow : Window
    {
        private static CollisionEditorWindow _open;
        private StackPanel _body;
        private TextBlock _entityName;

        public static CollisionEditorWindow Open(Window owner)
        {
            if (_open != null) { try { _open.Activate(); return _open; } catch { _open = null; } }
            _open = new CollisionEditorWindow { Owner = owner };
            _open.Show();
            return _open;
        }

        private CollisionEditorWindow()
        {
            Title = "Collision Editor";
            Width = 380; Height = 620; MinWidth = 340; MinHeight = 420;
            Background = Br("#FF161618");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { LastChildFill = true };

            var header = new Border { Background = Br("#FF1B1B1E"), BorderBrush = Br("#FF2C2C32"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 12, 16, 12) };
            var hs = new StackPanel();
            hs.Children.Add(new TextBlock { Text = "◈  Collision Editor", Foreground = Br("#FFF5F5F7"), FontSize = 15, FontWeight = FontWeights.Bold });
            _entityName = new TextBlock { Text = "No entity selected", Foreground = Br("#FF8A8A92"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
            hs.Children.Add(_entityName);
            header.Child = hs;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 14, 16, 16) };
            _body = new StackPanel();
            scroll.Content = _body;
            root.Children.Add(scroll);

            Content = root;

            SelectionService.Instance.SelectionChanged += OnSelectionChanged;
            Closed += (s, e) => { try { SelectionService.Instance.SelectionChanged -= OnSelectionChanged; } catch { } _open = null; };
            Rebuild();
        }

        private void OnSelectionChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(Rebuild));
        }

        private GameEntity Ent => SelectionService.Instance.SelectedEntity;

        private void Rebuild()
        {
            _body.Children.Clear();
            var ent = Ent;
            _entityName.Text = ent != null ? ("Entity:  " + ent.Name) : "No entity selected";

            if (ent == null)
            {
                _body.Children.Add(Note("Select an entity in the Scene Hierarchy to give it a collider."));
                _body.Children.Add(HelpBlock());
                return;
            }

            var col = ent.GetComponent<Collider>();

            // ---- collider type row ----
            _body.Children.Add(Label("COLLIDER SHAPE"));
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            row.Children.Add(TypeButton("Box", col is BoxCollider, () => SetType<BoxCollider>(ent)));
            row.Children.Add(TypeButton("Sphere", col is SphereCollider, () => SetType<SphereCollider>(ent)));
            row.Children.Add(TypeButton("Capsule", col is CapsuleCollider, () => SetType<CapsuleCollider>(ent)));
            row.Children.Add(TypeButton("Mesh", col is MeshCollider, () => SetType<MeshCollider>(ent)));
            if (col != null) row.Children.Add(TypeButton("Remove", false, () => { ent.RemoveComponent(col); Rebuild(); }));
            _body.Children.Add(row);

            if (col == null)
            {
                _body.Children.Add(Note("This entity has no collider — pick a shape above. Box/Sphere/Capsule are fast; Mesh is edge-accurate (uses the object's real triangles, exactly as rendered)."));
                _body.Children.Add(HelpBlock());
                return;
            }

            // ---- common: center + trigger ----
            _body.Children.Add(Label("CENTER (offset from the entity)"));
            _body.Children.Add(Vector3Row(col.Center, v => col.Center = v));

            // ---- type-specific ----
            if (col is BoxCollider box)
            {
                _body.Children.Add(Label("SIZE"));
                _body.Children.Add(Vector3Row(box.Size, v => box.Size = v));
            }
            else if (col is SphereCollider sph)
            {
                _body.Children.Add(Label("RADIUS"));
                _body.Children.Add(FloatRow(sph.Radius, v => sph.Radius = v));
            }
            else if (col is CapsuleCollider cap)
            {
                _body.Children.Add(Label("RADIUS"));
                _body.Children.Add(FloatRow(cap.Radius, v => cap.Radius = v));
                _body.Children.Add(Label("HEIGHT"));
                _body.Children.Add(FloatRow(cap.Height, v => cap.Height = v));
            }
            else if (col is MeshCollider mesh)
            {
                var mr = ent.GetComponent<MeshRenderer>();
                _body.Children.Add(Note(mr != null && !string.IsNullOrEmpty(mr.MeshPath)
                    ? "Edge-accurate: collides against this object's real triangles" + (mr.MeshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) ? " (exact analytic shape for a primitive)." : " — exactly as rendered.")
                    : "Mesh collider needs a Mesh Renderer on this entity."));
            }

            var trig = new CheckBox { Content = "Is Trigger (overlap only — no solid blocking)", Foreground = Br("#FFC8C8CE"), IsChecked = col.IsTrigger, Margin = new Thickness(0, 12, 0, 4) };
            trig.Checked += (s, e) => col.IsTrigger = true;
            trig.Unchecked += (s, e) => col.IsTrigger = false;
            _body.Children.Add(trig);

            _body.Children.Add(ActionButton("Auto-fit to mesh", () => { AutoFit(ent, col); Rebuild(); }));
            _body.Children.Add(HelpBlock());
        }

        private void SetType<T>(GameEntity ent) where T : Collider, new()
        {
            var existing = ent.GetComponent<Collider>();
            if (existing != null) ent.RemoveComponent(existing);
            var c = new T { Entity = ent };
            if (existing != null) { c.Center = existing.Center; c.IsTrigger = existing.IsTrigger; }
            ent.AddComponent(c);
            AutoFit(ent, c);
            SelectionService.Instance.Select(ent); // refresh the main inspector too
            Rebuild();
        }

        private static void AutoFit(GameEntity ent, Collider col)
        {
            // Unit defaults work because the CollisionService scales the collider by the entity transform; for a
            // non-primitive you can tweak the numbers by hand. Keeps it simple + predictable.
            if (col is BoxCollider b) b.Size = new Vector3(1, 1, 1);
            else if (col is SphereCollider s) s.Radius = 0.5f;
            else if (col is CapsuleCollider c) { c.Radius = 0.5f; c.Height = 2f; }
        }

        // ---- tiny styled UI helpers ----
        private static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

        private static TextBlock Label(string t) => new TextBlock { Text = t, Foreground = Br("#FF6E6E77"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 6) };

        private static UIElement Note(string t) => new Border { Background = Br("#FF1E1E22"), BorderBrush = Br("#FF2C2C32"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 9, 11, 10), Margin = new Thickness(0, 0, 0, 12), Child = new TextBlock { Text = t, Foreground = Br("#FFA9A9B2"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, LineHeight = 16 } };

        private UIElement HelpBlock() => new Border { Background = Br("#FF17171A"), BorderBrush = Br("#FF262630"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 9, 11, 10), Margin = new Thickness(0, 16, 0, 0), Child = new TextBlock { Foreground = Br("#FF8A8A92"), FontSize = 11, TextWrapping = TextWrapping.Wrap, LineHeight = 16, Text = "How it works: give your level objects (ground, walls, props) a collider here. In the game, the character capsule collides against them (Vortex.Physics.MoveCharacter) — solid ground, no walking through walls, no clipping. Box/Sphere/Capsule are cheapest; Mesh is edge-accurate (the object's real triangles)." } };

        private Button TypeButton(string text, bool active, Action onClick)
        {
            var b = new Button
            {
                Content = text, Margin = new Thickness(0, 0, 7, 7), Padding = new Thickness(13, 6, 13, 6),
                Cursor = System.Windows.Input.Cursors.Hand, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = active ? Brushes.White : Br("#FFC8C8CE"),
                Background = active ? Br("#FF6C5CE7") : Br("#FF212127"),
                BorderBrush = active ? Br("#FF6C5CE7") : Br("#FF2C2C32"), BorderThickness = new Thickness(1)
            };
            b.Click += (s, e) => { try { onClick(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CollisionEditor] " + ex.Message); } };
            return b;
        }

        private Button ActionButton(string text, Action onClick)
        {
            var b = new Button
            {
                Content = text, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 8, 14, 8),
                HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 12.5, Foreground = Br("#FFE9E9ED"), Background = Br("#FF26262B"),
                BorderBrush = Br("#FF3A3A42"), BorderThickness = new Thickness(1)
            };
            b.Click += (s, e) => { try { onClick(); } catch { } };
            return b;
        }

        private UIElement Vector3Row(Vector3 v, Action<Vector3> set)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
            float x = v.X, y = v.Y, z = v.Z;
            var fx = NumBox(x, nv => { x = nv; set(new Vector3(x, y, z)); }, "X", "#FFE06C6C");
            var fy = NumBox(y, nv => { y = nv; set(new Vector3(x, y, z)); }, "Y", "#FF7CE0A3");
            var fz = NumBox(z, nv => { z = nv; set(new Vector3(x, y, z)); }, "Z", "#FF6C9CE0");
            Grid.SetColumn(fx, 0); Grid.SetColumn(fy, 1); Grid.SetColumn(fz, 2);
            g.Children.Add(fx); g.Children.Add(fy); g.Children.Add(fz);
            return g;
        }

        private UIElement FloatRow(float v, Action<float> set)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            p.Children.Add(NumBox(v, set, null, "#FFC8C8CE"));
            return p;
        }

        private UIElement NumBox(float value, Action<float> set, string tag, string tagColor)
        {
            var wrap = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
            if (tag != null) wrap.Children.Add(new TextBlock { Text = tag, Foreground = Br(tagColor), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Width = 12 });
            var tb = new TextBox
            {
                Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Background = Br("#FF202023"), Foreground = Br("#FFF0F0F3"), BorderBrush = Br("#FF34343C"),
                BorderThickness = new Thickness(1), Padding = new Thickness(7, 5, 7, 5), MinWidth = 62,
                CaretBrush = Br("#FF6C5CE7")
            };
            tb.TextChanged += (s, e) =>
            {
                if (float.TryParse(tb.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nv)) set(nv);
            };
            wrap.Children.Add(tb);
            return wrap;
        }
    }
}
